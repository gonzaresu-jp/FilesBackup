// BackupTaskControl.cs
using FilesBackup.Models;

namespace FilesBackup
{
    public partial class BackupTaskControl : UserControl
    {
        public event EventHandler? OnRemoveRequested;
        public event EventHandler? OnMoveUpRequested;
        public event EventHandler? OnMoveDownRequested;
        public event EventHandler? OnChanged;

        private TextBox txtSource = null!;
        private TextBox txtDestination = null!;
        private Button btnBrowseSource = null!;
        private Button btnBrowseDestination = null!;
        private Button btnRemove = null!;
        private Button btnUp = null!;
        private Button btnDown = null!;

        private bool _suppressNotify = false;

        // ボタンの説明（ツールチップ）。コンポーネントは保持しておかないと表示が消えるためフィールドにする
        private readonly ToolTip _tip = new();

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
            ApplyTheme();
        }

        private void InitializeComponent()
        {
            btnUp = new Button() { Text = "↑", Width = 30, Height = 30, FlatStyle = FlatStyle.Flat };
            btnDown = new Button() { Text = "↓", Width = 30, Height = 30, FlatStyle = FlatStyle.Flat };
            txtSource = new TextBox() { Width = 200, BorderStyle = BorderStyle.FixedSingle };
            btnBrowseSource = new Button() { Text = "...", Width = 30, Height = 30, FlatStyle = FlatStyle.Flat };
            txtDestination = new TextBox() { Width = 200, BorderStyle = BorderStyle.FixedSingle };
            btnBrowseDestination = new Button() { Text = "...", Width = 30, Height = 30, FlatStyle = FlatStyle.Flat };
            btnRemove = new Button() { Text = "×", Width = 30, Height = 30, FlatStyle = FlatStyle.Flat };

            this.Height = 50;
            this.Width = 850;
            this.Margin = new Padding(0, 0, 0, 10);

            // イベント
            btnUp.Click += (s, e) => { OnMoveUpRequested?.Invoke(this, EventArgs.Empty); FireChanged(); };
            btnDown.Click += (s, e) => { OnMoveDownRequested?.Invoke(this, EventArgs.Empty); FireChanged(); };
            btnRemove.Click += (s, e) => { OnRemoveRequested?.Invoke(this, EventArgs.Empty); FireChanged(); };

            txtSource.TextChanged += (s, e) => FireChanged();
            txtDestination.TextChanged += (s, e) => FireChanged();

            btnBrowseSource.Click += (s, e) =>
            {
                using var dialog = new FolderBrowserDialog();
                if (dialog.ShowDialog() == DialogResult.OK)
                    txtSource.Text = dialog.SelectedPath;
            };
            btnBrowseDestination.Click += (s, e) =>
            {
                using var dialog = new FolderBrowserDialog();
                if (dialog.ShowDialog() == DialogResult.OK)
                    txtDestination.Text = dialog.SelectedPath;
            };

            _tip.SetToolTip(btnUp, "上へ移動（上から順に実行されます）");
            _tip.SetToolTip(btnDown, "下へ移動");
            _tip.SetToolTip(btnBrowseSource, "コピー元フォルダを選択");
            _tip.SetToolTip(btnBrowseDestination, "コピー先フォルダを選択");
            _tip.SetToolTip(btnRemove, "この設定を削除");

            Controls.Add(btnUp);
            Controls.Add(btnDown);
            Controls.Add(txtSource);
            Controls.Add(btnBrowseSource);
            Controls.Add(txtDestination);
            Controls.Add(btnBrowseDestination);
            Controls.Add(btnRemove);

            int x = 5;
            btnUp.Location = new Point(x, 10);
            x += btnUp.Width + 2;
            btnDown.Location = new Point(x, 10);

            x += btnDown.Width + 15;
            txtSource.Location = new Point(x, 12);
            x += txtSource.Width + 5;
            btnBrowseSource.Location = new Point(x, 10);

            x += btnBrowseSource.Width + 20;
            txtDestination.Location = new Point(x, 12);
            x += txtDestination.Width + 5;
            btnBrowseDestination.Location = new Point(x, 10);

            x += btnBrowseDestination.Width + 20;
            btnRemove.Location = new Point(x, 10);
            
            // 全体へのホバー効果
            this.MouseEnter += (s, e) => this.BackColor = Form1.ModernTheme.HoverBg;
            this.MouseLeave += (s, e) => this.BackColor = Form1.ModernTheme.Background;
        }

        public void ApplyTheme()
        {
            this.BackColor = Form1.ModernTheme.Background;
            this.Font = new Font("Segoe UI", 10);

            foreach (Control c in Controls)
            {
                c.Font = this.Font;
                if (c is Button b)
                {
                    b.BackColor = Form1.ModernTheme.HoverBg;
                    b.ForeColor = Form1.ModernTheme.TextPrimary;
                    b.FlatAppearance.BorderSize = 0;
                    b.MouseEnter += (s, e) => b.BackColor = Form1.ModernTheme.Border;
                    b.MouseLeave += (s, e) => b.BackColor = Form1.ModernTheme.HoverBg;
                }
                else if (c is TextBox t)
                {
                    t.BackColor = Form1.ModernTheme.Surface;
                    t.ForeColor = Form1.ModernTheme.TextPrimary;
                }
            }
            
            btnRemove.ForeColor = Color.FromArgb(220, 38, 38);
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

        public void SetTask(BackupTask task, bool silent = false)
        {
            _suppressNotify = silent;
            this.Source = task.Source;
            this.Destination = task.Destination;
            _suppressNotify = false;
        }
    }
}
