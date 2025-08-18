// BackupTaskControl.cs
using FilesBackup.Models;

namespace FilesBackup
{
    public partial class BackupTaskControl : UserControl
    {
        public event EventHandler? OnRemoveRequested;
        public event EventHandler? OnMoveUpRequested;
        public event EventHandler? OnMoveDownRequested;

        // ★追加：編集通知イベント
        public event EventHandler? OnChanged;

        private TextBox txtSource;
        private TextBox txtDestination;
        private Button btnBrowseSource;
        private Button btnBrowseDestination;
        private Button btnRemove;
        private Button btnUp;
        private Button btnDown;

        // 初期セット時の通知抑制
        private bool _suppressNotify = false;

        public string Source
        {
            get => txtSource.Text.Trim();
            set
            {
                _suppressNotify = true;
                txtSource.Text = value;
                _suppressNotify = false;
            }
        }

        public string Destination
        {
            get => txtDestination.Text.Trim();
            set
            {
                _suppressNotify = true;
                txtDestination.Text = value;
                _suppressNotify = false;
            }
        }

        public BackupTaskControl()
        {
            InitializeComponent();
        }

        private void InitializeComponent()
        {
            btnUp = new Button() { Text = "↑", Width = 30, Height = 30 };
            btnDown = new Button() { Text = "↓", Width = 30, Height = 30 };
            txtSource = new TextBox() { Width = 200 };
            btnBrowseSource = new Button() { Text = "参照", Width = 50 };
            txtDestination = new TextBox() { Width = 200 };
            btnBrowseDestination = new Button() { Text = "参照", Width = 50 };
            btnRemove = new Button() { Text = "削除", Width = 50 };

            this.Height = 40;
            this.Width = 820;

            // 既存イベント
            btnUp.Click += (s, e) => { OnMoveUpRequested?.Invoke(this, EventArgs.Empty); FireChanged(); };
            btnDown.Click += (s, e) => { OnMoveDownRequested?.Invoke(this, EventArgs.Empty); FireChanged(); };
            btnRemove.Click += (s, e) => { OnRemoveRequested?.Invoke(this, EventArgs.Empty); FireChanged(); };

            // ★テキスト編集を検知
            txtSource.TextChanged += (s, e) => FireChanged();
            txtDestination.TextChanged += (s, e) => FireChanged();

            // ★参照ボタンでパス決定時も検知
            btnBrowseSource.Click += (s, e) =>
            {
                using var dialog = new FolderBrowserDialog();
                if (dialog.ShowDialog() == DialogResult.OK)
                    txtSource.Text = dialog.SelectedPath; // TextChanged経由で通知される
            };
            btnBrowseDestination.Click += (s, e) =>
            {
                using var dialog = new FolderBrowserDialog();
                if (dialog.ShowDialog() == DialogResult.OK)
                    txtDestination.Text = dialog.SelectedPath; // TextChanged経由で通知される
            };

            Controls.Add(btnUp);
            Controls.Add(btnDown);
            Controls.Add(txtSource);
            Controls.Add(btnBrowseSource);
            Controls.Add(txtDestination);
            Controls.Add(btnBrowseDestination);
            Controls.Add(btnRemove);

            int x = 0;
            btnUp.Location = new Point(x, 5);
            x += btnUp.Width + 2;
            btnDown.Location = new Point(x, 5);

            x += btnDown.Width + 10;
            txtSource.Location = new Point(x, 5);

            x += txtSource.Width + 5;
            btnBrowseSource.Location = new Point(x, 5);

            x += btnBrowseSource.Width + 5;
            txtDestination.Location = new Point(x, 5);

            x += txtDestination.Width + 5;
            btnBrowseDestination.Location = new Point(x, 5);

            x += btnBrowseDestination.Width + 5;
            btnRemove.Location = new Point(x, 5);
        }

        private void FireChanged()
        {
            if (_suppressNotify) return;
            OnChanged?.Invoke(this, EventArgs.Empty);
        }

        public BackupTask ToBackupTask() => new BackupTask
        {
            Source = this.Source,
            Destination = this.Destination,
        };

        // ★初期化時は通知抑制してセット
        public void SetTask(BackupTask task, bool silent = false)
        {
            _suppressNotify = silent;
            this.Source = task.Source;
            this.Destination = task.Destination;
            _suppressNotify = false;
        }
    }
}
