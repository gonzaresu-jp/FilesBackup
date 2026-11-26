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

            // アンインストーラーから呼ばれる後片付け（自動起動の登録とスケジュールのタスクを消す。設定・履歴は残す）
            if (args.Length > 1 && args[1].Equals(UninstallArg, StringComparison.OrdinalIgnoreCase))
            {
                string self = Environment.ProcessPath ?? Application.ExecutablePath;
                try { StartupRegistration.RemoveIfOwnedBy(self); } catch { }
                try { SchedulerService.UnregisterIfOwnedBy(self); } catch { }
                return 0;
            }

            // 旧バージョンの保存先（exe と同じフォルダの Config）から設定を移行する
            AppPaths.MigrateLegacyConfig();

            // 旧バージョンで肥大化した履歴ファイルを整理する（起動のたびに数百MBを読み込まないように）
            if (Logger.CompactHistoryIfLarge())
                MemoryTrimmer.Trim();   // 大きなファイルを読み込んだ分のメモリを返す

            if (args.Length > 1 && args[1].Equals("auto", StringComparison.OrdinalIgnoreCase))
                return RunAuto();

            if (args.Length > 2 && args[1].Equals(MakeIconArg, StringComparison.OrdinalIgnoreCase))
            {
                UI.Theme.SaveIconFile(args[2]);
                return 0;
            }

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

            bool startInTray = args.Contains(StartupRegistration.TrayArg, StringComparer.OrdinalIgnoreCase);

            if (!createdNew)
            {
                // サインイン時の自動起動で、すでに起動していれば何もしない
                if (startInTray) return 0;

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
            _mainForm = new Form1(startInTray: startInTray);
            Application.Run(_mainForm);
            return 0;
        }

        /// <summary>exe 用のアイコンファイルを書き出して終了する（--make-icon 保存先.ico）</summary>
        private const string MakeIconArg = "--make-icon";

        /// <summary>アンインストーラーから呼ばれる後片付け</summary>
        private const string UninstallArg = "--uninstall";

        private static volatile Form1? _mainForm;

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

                    // スリープ復帰直後や NAS の起動待ちで、ネットワークの共有にすぐつながらないことがあるので少し待つ
                    NetworkWait = TimeSpan.FromMinutes(10),
                };
                manager.ApplyIgnore(ConfigStore.LoadIgnore());

                var result = manager.ExecuteAllWithResult(trigger: "auto");
                int postponed = result.TaskResults.Count(t => t.Status == "延期");

                title = "バックアップ完了";
                text = $"コピー: {result.FilesCopied}件, スキップ: {result.FilesSkipped}件";
                if (result.FilesDeleted > 0) text += $", 削除: {result.FilesDeleted}件";
                if (result.Errors > 0) text += $", エラー: {result.Errors}件";
                icon = result.Errors > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info;

                if (postponed > 0)
                {
                    // 接続できなかった設定がある。終了コード 1 でタスク スケジューラに失敗として残す
                    title = "一部のバックアップを延期しました";
                    text = $"接続できない保存先があります（{postponed}件）。" + text;
                    icon = ToolTipIcon.Warning;
                    exitCode = 1;
                }

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

                    if (command == ShowCommand && _mainForm is { IsHandleCreated: true } form)
                        form.Invoke(form.ShowWindow);
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
