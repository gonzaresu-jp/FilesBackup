using System.Drawing;
using System.Windows.Forms;

namespace FilesBackup
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;
        private ToolTip toolTip = null!;

        // ページ
        private Panel pnlTasks = null!;
        private Panel pnlSchedule = null!;
        private Panel pnlSettings = null!;

        // バックアップ
        private FlowLayoutPanel flowPanel = null!;
        private Button btnAddTask = null!;
        private Button btnSave = null!;
        private Button btnStartBackup = null!;
        private Button btnCheck = null!;
        private Button btnPreview = null!;
        private Button btnPause = null!;
        private Button btnCancel = null!;
        private Label lblParallel = null!;
        private NumericUpDown nudParallel = null!;
        private CheckBox chkCompareHash = null!;
        private ProgressBar progressBar1 = null!;
        private Label lblProgress = null!;

        // スケジュール
        private Label lblFrequency = null!;
        private ComboBox cmbFrequency = null!;
        private Label lblScheduleTime = null!;
        private DateTimePicker dtpScheduleTime = null!;
        private CheckedListBox clbWeekDays = null!;
        private CheckBox chkHighest = null!;
        private CheckBox chkAcOnly = null!;
        private CheckBox chkWake = null!;
        private CheckBox chkNotifyMissed = null!;
        private Button btnSaveSchedule = null!;
        private Label lblScheduleStatus = null!;

        // 除外設定
        private Label lblIgnoreHint = null!;
        private Button btnAddIgnorePattern = null!;
        private Button btnAddIgnoreFolderName = null!;
        private CheckBox chkSkipHiddenSystem = null!;
        private CheckBox chkFollowLinks = null!;

        // 履歴
        private Button btnOpenLogFolder = null!;
        private Label lblHistoryHint = null!;

        // 設定
        private Button btnNavSettings = null!;
        private Label lblConfigDirTitle = null!;
        private TextBox txtConfigDir = null!;
        private Button btnOpenConfigDir = null!;
        private Button btnExport = null!;
        private Button btnImport = null!;
        private Label lblExportHint = null!;
        private Label lblPrivilege = null!;
        private Button btnRestartElevated = null!;
        private Label lblVersion = null!;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            toolTip = new ToolTip(components);

            // ===== サイドバー =====
            panelSidebar = new Panel
            {
                Dock = DockStyle.Left,
                Width = 180,
                BackColor = ModernTheme.Sidebar
            };

            navIndicator = new Panel
            {
                Width = 4,
                BackColor = Color.FromArgb(59, 130, 246),
                Location = new Point(0, 0),
                Visible = false
            };
            panelSidebar.Controls.Add(navIndicator);

            btnNavBackups = CreateNavButton("バックアップ", 60);
            btnNavBackups.Click += (s, e) => ShowPage("backups", btnNavBackups);
            panelSidebar.Controls.Add(btnNavBackups);

            btnNavSchedule = CreateNavButton("スケジュール", 110);
            btnNavSchedule.Click += (s, e) => ShowPage("schedule", btnNavSchedule);
            panelSidebar.Controls.Add(btnNavSchedule);

            btnNavIgnore = CreateNavButton("除外設定", 160);
            btnNavIgnore.Click += (s, e) => ShowPage("ignore", btnNavIgnore);
            panelSidebar.Controls.Add(btnNavIgnore);

            btnNavHistory = CreateNavButton("履歴", 210);
            btnNavHistory.Click += (s, e) => ShowPage("history", btnNavHistory);
            panelSidebar.Controls.Add(btnNavHistory);

            btnNavSettings = CreateNavButton("設定", 260);
            btnNavSettings.Click += (s, e) => ShowPage("settings", btnNavSettings);
            panelSidebar.Controls.Add(btnNavSettings);

            btnThemeToggle = new Button
            {
                Text = "☾ ダークモード",
                Location = new Point(0, 460),
                Size = new Size(180, 50),
                FlatStyle = FlatStyle.Flat,
                ForeColor = ModernTheme.TextSecondary,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(20, 0, 0, 0),
                Font = new Font("Segoe UI", 11)
            };
            btnThemeToggle.Click += btnThemeToggle_Click;
            panelSidebar.Controls.Add(btnThemeToggle);

            // ===== メインコンテンツ =====
            panelContent = new Panel
            {
                Dock = DockStyle.Fill,
                BackColor = ModernTheme.Background,
                Padding = new Padding(20)
            };
            panelContent.Paint += (s, e) =>
            {
                using var pen = new Pen(ModernTheme.Border, 1);
                e.Graphics.DrawRectangle(pen, 0, 0, panelContent.ClientSize.Width - 1, panelContent.ClientSize.Height - 1);
            };

            // ページ切り替え用パネル（TabControlはネイティブ枠線を消せないため使用しない）
            pnlTasks = new Panel { Dock = DockStyle.Fill, BackColor = ModernTheme.Background, Visible = false };
            pnlSchedule = new Panel { Dock = DockStyle.Fill, BackColor = ModernTheme.Background, Visible = false };
            pnlIgnore = new Panel { Dock = DockStyle.Fill, BackColor = ModernTheme.Background, Visible = false };
            pnlHistory = new Panel { Dock = DockStyle.Fill, BackColor = ModernTheme.Background, Visible = false };
            pnlSettings = new Panel { Dock = DockStyle.Fill, BackColor = ModernTheme.Background, Visible = false };

            // ===== バックアップ =====
            flowPanel = new FlowLayoutPanel
            {
                AutoScroll = false,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Dock = DockStyle.Top,
                Height = 270,
                BackColor = ModernTheme.Background
            };

            btnAddTask = new Button { Text = "＋ 追加", Location = new Point(0, 280), Size = new Size(100, 32), FlatStyle = FlatStyle.Flat };
            btnAddTask.Click += btnAddTask_Click;

            chkMirrorMode = new CheckBox { Text = "", Location = new Point(120, 286), Size = new Size(20, 20), FlatStyle = FlatStyle.Flat };
            lblMirrorMode = new Label
            {
                Text = "ミラーモード（コピー先の不要ファイルを削除）",
                Location = new Point(142, 288),
                AutoSize = true,
                ForeColor = Color.FromArgb(107, 114, 128)
            };

            lblParallel = new Label { Text = "並列コピー数", Location = new Point(0, 324), AutoSize = true };
            nudParallel = new NumericUpDown { Location = new Point(100, 320), Size = new Size(55, 25), Minimum = 1, Maximum = 8, Value = 2 };
            chkCompareHash = new CheckBox { Text = "ハッシュで内容を比較", Location = new Point(175, 322), AutoSize = true, FlatStyle = FlatStyle.Flat };

            btnCheck = new Button { Text = "事前チェック", Location = new Point(0, 356), Size = new Size(110, 36), FlatStyle = FlatStyle.Flat };
            btnCheck.Click += btnCheck_Click;
            btnPreview = new Button { Text = "プレビュー", Location = new Point(118, 356), Size = new Size(110, 36), FlatStyle = FlatStyle.Flat };
            btnPreview.Click += btnPreview_Click;
            btnPause = new Button { Text = "一時停止", Location = new Point(236, 356), Size = new Size(100, 36), FlatStyle = FlatStyle.Flat, Enabled = false };
            btnPause.Click += btnPause_Click;
            btnCancel = new Button { Text = "中止", Location = new Point(344, 356), Size = new Size(80, 36), FlatStyle = FlatStyle.Flat, Enabled = false };
            btnCancel.Click += btnCancel_Click;

            btnSave = new Button { Text = "保存", Location = new Point(480, 356), Size = new Size(120, 36), FlatStyle = FlatStyle.Flat };
            btnSave.Click += btnSave_Click;

            btnStartBackup = new Button
            {
                Text = "バックアップ開始",
                Location = new Point(610, 356),
                Size = new Size(150, 36),
                FlatStyle = FlatStyle.Flat,
                BackColor = Color.FromArgb(59, 130, 246),
                ForeColor = Color.White
            };
            btnStartBackup.Click += btnStartBackup_Click;

            progressBar1 = new ProgressBar { Location = new Point(0, 400), Size = new Size(760, 4), Visible = false, Style = ProgressBarStyle.Continuous };
            lblProgress = new Label { Location = new Point(0, 408), Size = new Size(760, 20), Text = "", Visible = false, ForeColor = ModernTheme.TextSecondary, AutoEllipsis = true };
            lblCurrentFile = new Label { Location = new Point(0, 428), Size = new Size(760, 20), Text = "", Visible = false, ForeColor = ModernTheme.TextSecondary, AutoEllipsis = true };
            lblBytesInfo = new Label { Location = new Point(0, 448), Size = new Size(760, 20), Text = "", Visible = false, ForeColor = ModernTheme.TextSecondary, AutoEllipsis = true };

            toolTip.SetToolTip(btnCheck, "コピー元の有無・コピー先への書き込み可否・危険な設定などを確認します");
            toolTip.SetToolTip(btnPreview, "実際にはコピーせず、コピー・削除される件数と容量を数えます");
            toolTip.SetToolTip(btnPause, "実行中のファイルが終わったところで一時停止します");
            toolTip.SetToolTip(btnCancel, "バックアップを中止します（コピー済みのファイルはそのまま残ります）");
            toolTip.SetToolTip(nudParallel, "同時にコピーするファイル数。HDD は 1、SSD や NAS は 2〜4 が目安です");
            toolTip.SetToolTip(chkCompareHash, "更新日時だけ新しく内容が同じファイルはコピーしません（比較に時間がかかります）");
            toolTip.SetToolTip(lblMirrorMode, "コピー元に無いファイルをコピー先から削除します。他の設定とコピー先が重なる場合は適用されません");

            // ===== スケジュール =====
            lblFrequency = new Label { Text = "実行頻度", Location = new Point(20, 20), AutoSize = true };
            cmbFrequency = new ComboBox
            {
                Location = new Point(20, 45),
                Width = 200,
                DropDownStyle = ComboBoxStyle.DropDownList,
                BackColor = ModernTheme.Surface,
                ForeColor = ModernTheme.TextPrimary,
                FlatStyle = FlatStyle.Flat
            };
            cmbFrequency.Items.AddRange(new string[] { "毎日", "毎週", "起動時" });

            lblScheduleTime = new Label { Text = "実行時刻", Location = new Point(20, 90), AutoSize = true };
            dtpScheduleTime = new DateTimePicker
            {
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "HH:mm",
                ShowUpDown = true,
                Location = new Point(20, 115),
                Width = 120
            };

            // 曜日は1行に並べてスクロール不要にする
            clbWeekDays = new CheckedListBox
            {
                Location = new Point(20, 158),
                Size = new Size(450, 30),
                MultiColumn = true,
                ColumnWidth = 60,
                CheckOnClick = true,
                Visible = false,
                BackColor = ModernTheme.Surface,
                ForeColor = ModernTheme.TextPrimary,
                BorderStyle = BorderStyle.None
            };
            clbWeekDays.Items.AddRange(new string[] { "日", "月", "火", "水", "木", "金", "土" });

            chkHighest = new CheckBox { Text = "最高特権で実行する（管理者権限が必要なフォルダがある場合のみ）", Location = new Point(20, 200), AutoSize = true, FlatStyle = FlatStyle.Flat };
            chkAcOnly = new CheckBox { Text = "AC電源に接続している時だけ実行する", Location = new Point(20, 228), AutoSize = true, FlatStyle = FlatStyle.Flat };
            chkWake = new CheckBox { Text = "スリープ中なら、スリープを解除して実行する", Location = new Point(20, 256), AutoSize = true, FlatStyle = FlatStyle.Flat };
            chkNotifyMissed = new CheckBox { Text = "予定の実行が行われていなければ、アプリを開いたときに知らせる", Location = new Point(20, 284), AutoSize = true, FlatStyle = FlatStyle.Flat };

            btnSaveSchedule = new Button { Text = "スケジュールを保存", Location = new Point(20, 320), Size = new Size(200, 40), FlatStyle = FlatStyle.Flat };
            btnSaveSchedule.Click += btnSaveSchedule_Click;

            lblScheduleStatus = new Label { Location = new Point(20, 375), Size = new Size(740, 80), Text = "", ForeColor = ModernTheme.TextSecondary };

            toolTip.SetToolTip(chkHighest, "オンにするとタスクの登録に管理者権限が必要です。通常はオフ（標準権限）で十分です");
            toolTip.SetToolTip(chkNotifyMissed, "PC の電源が切れていた等で予定の実行が行われなかった場合に、今すぐ実行するか確認します");

            // ===== 除外設定 =====
            lstIgnoreItems = new ListBox
            {
                Location = new Point(20, 20),
                Size = new Size(500, 270),
                BackColor = ModernTheme.Surface,
                ForeColor = ModernTheme.TextPrimary,
                BorderStyle = BorderStyle.None,
                HorizontalScrollbar = true
            };
            btnAddIgnoreFile = new Button { Text = "ファイルを追加", Location = new Point(530, 20), Size = new Size(150, 36), FlatStyle = FlatStyle.Flat };
            btnAddIgnoreFile.Click += btnAddIgnoreFile_Click;
            btnAddIgnoreFolder = new Button { Text = "フォルダを追加", Location = new Point(530, 70), Size = new Size(150, 36), FlatStyle = FlatStyle.Flat };
            btnAddIgnoreFolder.Click += btnAddIgnoreFolder_Click;
            btnSaveIgnore = new Button { Text = "保存", Location = new Point(530, 120), Size = new Size(150, 36), FlatStyle = FlatStyle.Flat };
            btnSaveIgnore.Click += btnSaveIgnore_Click;
            btnRemoveIgnoreItem = new Button { Text = "選択項目を削除", Location = new Point(530, 170), Size = new Size(150, 36), FlatStyle = FlatStyle.Flat };
            btnRemoveIgnoreItem.Click += btnRemoveIgnoreItem_Click;

            lblIgnoreHint = new Label
            {
                Text = "拡張子は .tmp、ワイルドカードは **/Cache/** や *.bak（* と ? が使えます）、フォルダ名は .git のように入力",
                Location = new Point(20, 296),
                Size = new Size(740, 20),
                ForeColor = ModernTheme.TextSecondary,
                AutoEllipsis = true
            };

            txtIgnoreExtension = new TextBox { Location = new Point(20, 322), Size = new Size(200, 30), PlaceholderText = ".tmp / **/Cache/** / .git" };
            btnAddIgnoreExtension = new Button { Text = "拡張子を追加", Location = new Point(230, 320), Size = new Size(130, 30), FlatStyle = FlatStyle.Flat };
            btnAddIgnoreExtension.Click += btnAddIgnoreExtension_Click;
            btnAddIgnorePattern = new Button { Text = "パターンを追加", Location = new Point(368, 320), Size = new Size(130, 30), FlatStyle = FlatStyle.Flat };
            btnAddIgnorePattern.Click += btnAddIgnorePattern_Click;
            btnAddIgnoreFolderName = new Button { Text = "フォルダ名を追加", Location = new Point(506, 320), Size = new Size(150, 30), FlatStyle = FlatStyle.Flat };
            btnAddIgnoreFolderName.Click += btnAddIgnoreFolderName_Click;

            chkSkipHiddenSystem = new CheckBox { Text = "隠し/システム属性のファイル・フォルダを除外する", Location = new Point(20, 364), AutoSize = true, FlatStyle = FlatStyle.Flat };
            chkFollowLinks = new CheckBox { Text = "ジャンクション/シンボリックリンクを辿る（通常はオフ。意図しない範囲をコピーする恐れがあります）", Location = new Point(20, 392), AutoSize = true, FlatStyle = FlatStyle.Flat };

            toolTip.SetToolTip(btnAddIgnoreFolderName, "どこにあっても、この名前のフォルダを除外します（例: .git, node_modules）");
            toolTip.SetToolTip(btnAddIgnorePattern, "* は区切り内の任意の文字、** はフォルダをまたぐ任意の文字、? は1文字");

            // ===== 履歴 =====
            lstHistory = new ListView
            {
                Location = new Point(20, 20),
                Size = new Size(720, 330),
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = ModernTheme.Surface,
                ForeColor = ModernTheme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                HeaderStyle = ColumnHeaderStyle.Nonclickable
            };
            lstHistory.Columns.Add("日時", 130);
            lstHistory.Columns.Add("種別", 50);
            lstHistory.Columns.Add("所要時間", 75);
            lstHistory.Columns.Add("コピー", 65);
            lstHistory.Columns.Add("スキップ", 75);
            lstHistory.Columns.Add("削除", 50);
            lstHistory.Columns.Add("エラー", 55);
            lstHistory.Columns.Add("コピー量", 80);
            lstHistory.Columns.Add("状態", 120);
            // 最終列を残り幅いっぱいに広げ、ヘッダー右側に隙間ができないようにする
            lstHistory.Resize += (s, e) =>
            {
                if (lstHistory.Columns.Count == 0) return;
                lstHistory.Columns[^1].Width = -2;
            };
            lstHistory.DoubleClick += lstHistory_DoubleClick;

            btnClearHistory = new Button { Text = "履歴をクリア", Location = new Point(20, 360), Size = new Size(150, 36), FlatStyle = FlatStyle.Flat };
            btnClearHistory.Click += btnClearHistory_Click;
            btnOpenLog = new Button { Text = "ログを開く", Location = new Point(180, 360), Size = new Size(150, 36), FlatStyle = FlatStyle.Flat };
            btnOpenLog.Click += btnOpenLog_Click;
            btnOpenLogFolder = new Button { Text = "ログフォルダを開く", Location = new Point(340, 360), Size = new Size(170, 36), FlatStyle = FlatStyle.Flat };
            btnOpenLogFolder.Click += btnOpenLogFolder_Click;
            lblHistoryHint = new Label { Text = "行をダブルクリックすると、設定ごとの結果とエラー内容を表示します", Location = new Point(20, 405), AutoSize = true, ForeColor = ModernTheme.TextSecondary };

            // ===== 設定 =====
            lblConfigDirTitle = new Label { Text = "設定の保存先", Location = new Point(20, 20), AutoSize = true };
            txtConfigDir = new TextBox { Location = new Point(20, 45), Size = new Size(560, 30), ReadOnly = true };
            btnOpenConfigDir = new Button { Text = "フォルダを開く", Location = new Point(590, 43), Size = new Size(150, 30), FlatStyle = FlatStyle.Flat };
            btnOpenConfigDir.Click += btnOpenConfigDir_Click;

            btnExport = new Button { Text = "設定をエクスポート", Location = new Point(20, 95), Size = new Size(200, 36), FlatStyle = FlatStyle.Flat };
            btnExport.Click += btnExport_Click;
            btnImport = new Button { Text = "設定をインポート", Location = new Point(230, 95), Size = new Size(200, 36), FlatStyle = FlatStyle.Flat };
            btnImport.Click += btnImport_Click;
            lblExportHint = new Label
            {
                Text = "バックアップ・除外・スケジュール・オプションの設定を1つの JSON ファイルにまとめて保存／読み込みします",
                Location = new Point(20, 140),
                Size = new Size(740, 20),
                ForeColor = ModernTheme.TextSecondary
            };

            lblPrivilege = new Label { Text = "", Location = new Point(20, 185), AutoSize = true };
            btnRestartElevated = new Button { Text = "管理者として再起動", Location = new Point(20, 210), Size = new Size(200, 36), FlatStyle = FlatStyle.Flat };
            btnRestartElevated.Click += btnRestartElevated_Click;
            lblVersion = new Label { Text = "", Location = new Point(20, 265), AutoSize = true, ForeColor = ModernTheme.TextSecondary };

            toolTip.SetToolTip(btnRestartElevated, "管理者権限が必要なフォルダのバックアップや、最高特権でのスケジュール登録に使います");

            // ===== 配置 =====
            pnlTasks.Controls.AddRange(new Control[]
            {
                flowPanel, btnAddTask, chkMirrorMode, lblMirrorMode, lblParallel, nudParallel, chkCompareHash,
                btnCheck, btnPreview, btnPause, btnCancel, btnSave, btnStartBackup,
                progressBar1, lblProgress, lblCurrentFile, lblBytesInfo
            });

            pnlSchedule.Controls.AddRange(new Control[]
            {
                lblFrequency, cmbFrequency, lblScheduleTime, dtpScheduleTime, clbWeekDays,
                chkHighest, chkAcOnly, chkWake, chkNotifyMissed, btnSaveSchedule, lblScheduleStatus
            });

            pnlIgnore.Controls.AddRange(new Control[]
            {
                lstIgnoreItems, btnAddIgnoreFile, btnAddIgnoreFolder, btnSaveIgnore, btnRemoveIgnoreItem,
                lblIgnoreHint, txtIgnoreExtension, btnAddIgnoreExtension, btnAddIgnorePattern, btnAddIgnoreFolderName,
                chkSkipHiddenSystem, chkFollowLinks
            });

            pnlHistory.Controls.AddRange(new Control[] { lstHistory, btnClearHistory, btnOpenLog, btnOpenLogFolder, lblHistoryHint });

            pnlSettings.Controls.AddRange(new Control[]
            {
                lblConfigDirTitle, txtConfigDir, btnOpenConfigDir, btnExport, btnImport, lblExportHint,
                lblPrivilege, btnRestartElevated, lblVersion
            });

            panelContent.Controls.Add(pnlTasks);
            panelContent.Controls.Add(pnlSchedule);
            panelContent.Controls.Add(pnlIgnore);
            panelContent.Controls.Add(pnlHistory);
            panelContent.Controls.Add(pnlSettings);

            this.Controls.Add(panelContent);
            this.Controls.Add(panelSidebar);

            this.ClientSize = new Size(1000, 520);
            this.MinimumSize = new Size(1016, 559);
            this.Name = "Form1";
            this.Text = "FilesBackup";
            this.BackColor = ModernTheme.Background;
        }

        private Button CreateNavButton(string text, int y)
        {
            return new Button
            {
                Text = text,
                Location = new Point(0, y),
                Size = new Size(180, 50),
                FlatStyle = FlatStyle.Flat,
                ForeColor = ModernTheme.TextSecondary,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(20, 0, 0, 0),
                Font = new Font("Segoe UI", 11)
            };
        }
    }
}
