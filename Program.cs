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

            // �����o�b�N�A�b�v�݂̂̏����i�t�H�[���\���Ȃ��j
            if (args.Length > 1 && args[1].Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "backup_config.json");
                    string ignorePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Config", "ignore_config.json");

                    var manager = new BackupManager();
                    manager.LoadFromJson(configPath);
                    manager.LoadIgnoreList(ignorePath); // �� �Y�ꂸ�ɏ��O���X�g���ǂݍ���

                    int changedCount = manager.ExecuteAll();

                    // �ʒm�i�o���[���\���j
                    using var notifyIcon = new NotifyIcon
                    {
                        Icon = SystemIcons.Application,
                        Visible = true,
                        BalloonTipTitle = "�o�b�N�A�b�v����",
                        BalloonTipText = $"�ύX���ꂽ�t�@�C�����F{changedCount} ��",
                        BalloonTipIcon = ToolTipIcon.Info
                    };

                    notifyIcon.ShowBalloonTip(5000);

                    // �����҂��Ă���I���i�ʒm���\�������悤�Ɂj
                    Thread.Sleep(3000);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"�����o�b�N�A�b�v�Ɏ��s���܂���: {ex.Message}", "�G���[", MessageBoxButtons.OK, MessageBoxIcon.Error);
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
                    MessageBox.Show("���łɎ��s���ł����A�E�B���h�E�̌Ăяo���Ɏ��s���܂����B", "�ʒm���s");
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
