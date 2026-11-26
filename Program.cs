using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using FilesBackup.Services;

namespace FilesBackup
{
    internal static class Program
    {
        private const string MutexName = "FilesBackupSingletonMutex";
        private const string PipeName = "FilesBackupPipe";
        private const string ShowCommand = "SHOW";

        /// <summary>アプリ自身による再起動（管理者として再起動・保存先の切り替え）で起動されたことを示す引数</summary>
        public const string RestartArg = "--restart";

        /// <summary>README 用の画面写真を保存して終了する（--screenshots フォルダ）</summary>
        private const string ScreenshotsArg = "--screenshots";

        [STAThread]
        static int Main()
        {
            string[] args = Environment.GetCommandLineArgs();

            // 旧バージョンの保存先（exe と同じフォルダの Config）から設定を移行する
            AppPaths.MigrateLegacyConfig();

            if (args.Length > 1 && args[1].Equals("auto", StringComparison.OrdinalIgnoreCase))
                return RunAuto();

            if (args.Length > 2 && args[1].Equals(ScreenshotsArg, StringComparison.OrdinalIgnoreCase))
            {
                ApplicationConfiguration.Initialize();
                Application.Run(new Form1(screenshotDir: args[2]));
                return 0;
            }

            bool createdNew;
            using Mutex mutex = new(true, MutexName, out createdNew);

            // アプリ自身が再起動した場合は、元のプロセスが終了するのを待ってから起動する
            if (!createdNew && args.Contains(RestartArg, StringComparer.OrdinalIgnoreCase))
            {
                try { createdNew = mutex.WaitOne(TimeSpan.FromSeconds(15)); }
                catch (AbandonedMutexException) { createdNew = true; }
            }

            if (!createdNew)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(1000);
                    using var writer = new StreamWriter(client) { AutoFlush = true };
                    writer.WriteLine(ShowCommand);
                }
                catch
                {
                    MessageBox.Show("既に実行されていますが、ウィンドウの呼び出しに失敗しました。", "通知失敗");
                }
                return 0;
            }

            var pipeThread = new Thread(PipeServerLoop) { IsBackground = true };
            pipeThread.Start();

            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
            return 0;
        }

        /// <summary>
        /// タスク スケジューラからの自動実行。
        /// 利用者が不在でも止まらないよう、メッセージボックスは出さず通知とログだけにする。
        /// 失敗時は終了コード 1 を返し、タスク スケジューラの再試行に任せる。
        /// </summary>
        private static int RunAuto()
        {
            using var runLock = RunLock.TryAcquire();
            if (!runLock.Acquired)
            {
                Logger.Warn("自動バックアップをスキップしました（別のバックアップが実行中です）");
                return 0;
            }

            int exitCode = 0;
            string title;
            string text;
            ToolTipIcon icon;

            try
            {
                Logger.Info("=== 自動バックアップ開始 ===");

                var manager = new BackupManager
                {
                    Tasks = ConfigStore.LoadTasks(),
                    Options = ConfigStore.LoadOptions(),
                };
                manager.ApplyIgnore(ConfigStore.LoadIgnore());

                var result = manager.ExecuteAllWithResult(trigger: "auto");

                title = "バックアップ完了";
                text = $"コピー: {result.FilesCopied}件, スキップ: {result.FilesSkipped}件";
                if (result.FilesDeleted > 0) text += $", 削除: {result.FilesDeleted}件";
                if (result.Errors > 0) text += $", エラー: {result.Errors}件";
                icon = result.Errors > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info;

                Logger.Info("=== 自動バックアップ終了 ===");
            }
            catch (Exception ex)
            {
                Logger.Error("自動バックアップに失敗しました", ex);
                title = "自動バックアップに失敗しました";
                text = ex.Message;
                icon = ToolTipIcon.Error;
                exitCode = 1;
            }

            try
            {
                using var notifyIcon = new NotifyIcon
                {
                    Icon = UI.Theme.CreateAppIcon(),
                    Visible = true,
                    BalloonTipTitle = title,
                    BalloonTipText = text.Length > 250 ? text[..250] : text,
                    BalloonTipIcon = icon,
                };
                notifyIcon.ShowBalloonTip(5000);
                Thread.Sleep(3000);
            }
            catch { }

            return exitCode;
        }

        /// <summary>
        /// 2つ目の起動から「ウィンドウを表示して」を受け取る。
        /// 接続できるのは同じユーザーのみ。受け付けるのは短い "SHOW" だけで、それ以外は読み捨てる。
        /// </summary>
        private static void PipeServerLoop()
        {
            while (true)
            {
                try
                {
                    using var server = CreatePipeServer();
                    server.WaitForConnection();

                    // 長い入力でメモリを使われないよう、先頭の数文字だけ読む
                    var buffer = new char[16];
                    using var reader = new StreamReader(server);
                    int read = reader.Read(buffer, 0, buffer.Length);
                    string command = new string(buffer, 0, Math.Max(0, read)).Trim();

                    if (command == ShowCommand && Application.OpenForms.Count > 0 && Application.OpenForms[0] is Form1 form)
                    {
                        form.Invoke(() =>
                        {
                            form.Show();
                            form.WindowState = FormWindowState.Normal;
                            form.BringToFront();
                        });
                    }
                }
                catch
                {
                    // 1回の失敗でサーバーを止めない（次の接続を待つ）
                    Thread.Sleep(500);
                }
            }
        }

        private static NamedPipeServerStream CreatePipeServer()
        {
            try
            {
                var security = new PipeSecurity();
                var user = WindowsIdentity.GetCurrent().User;
                if (user != null)
                {
                    security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
                    return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, security);
                }
            }
            catch { }

            return new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
        }
    }
}
