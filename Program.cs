using System.IO.Pipes;
using FilesBackup.Services;

namespace FilesBackup
{
    internal static class Program
    {
        private const string MutexName = "FilesBackupSingletonMutex";
        private const string PipeName = "FilesBackupPipe";

        [STAThread]
        static void Main()
        {
            string[] args = Environment.GetCommandLineArgs();

            // 自動バックアップのみの処理（フォーム表示なし）
            if (args.Length > 1 && args[1].Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "backup_config.json");
                    string ignorePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "ignore_config.json");

                    var manager = new BackupManager();
                    manager.LoadFromJson(configPath);
                    manager.LoadIgnoreList(ignorePath); // ← 忘れずに除外リストも読み込む

                    int changedCount = manager.ExecuteAll();

                    // 通知（バルーン表示）
                    using var notifyIcon = new NotifyIcon
                    {
                        Icon = SystemIcons.Application,
                        Visible = true,
                        BalloonTipTitle = "バックアップ完了",
                        BalloonTipText = $"変更されたファイル数：{changedCount} 件",
                        BalloonTipIcon = ToolTipIcon.Info
                    };

                    notifyIcon.ShowBalloonTip(5000);

                    // 少し待ってから終了（通知が表示されるように）
                    Thread.Sleep(3000);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"自動バックアップに失敗しました: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                return;
            }

            bool createdNew;
            using Mutex mutex = new(true, MutexName, out createdNew);

            if (!createdNew)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                    client.Connect(1000);
                    using var writer = new StreamWriter(client) { AutoFlush = true };
                    writer.WriteLine("SHOW");
                }
                catch
                {
                    MessageBox.Show("すでに実行中ですが、ウィンドウの呼び出しに失敗しました。", "通知失敗");
                }
                return;
            }

            Thread pipeThread = new(() =>
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In);
                    using var reader = new StreamReader(server);
                    while (true)
                    {
                        server.WaitForConnection();
                        string? command = reader.ReadLine();
                        if (command == "SHOW" && Application.OpenForms.Count > 0)
                        {
                            Form1? form = Application.OpenForms[0] as Form1;
                            if (form != null)
                            {
                                form.Invoke(() => {
                                    form.Show();
                                    form.WindowState = FormWindowState.Normal;
                                    form.BringToFront();
                                });
                            }
                        }
                        server.Disconnect();
                    }
                }
                catch { }
            });
            pipeThread.IsBackground = true;
            pipeThread.Start();

            ApplicationConfiguration.Initialize();
            Application.Run(new Form1());
        }
    }
}
