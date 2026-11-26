using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using FilesBackup.Models;
using FilesBackup.Services;

namespace FilesBackup
{
    public partial class Form1 : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const string FolderNamePrefix = "[フォルダ名] ";

        private static void ApplyDarkScrollbar(Control ctrl)
        {
            try
            {
                string themeName = ModernTheme.IsDarkMode ? "DarkMode_Explorer" : "Explorer";
                SetWindowTheme(ctrl.Handle, themeName, null);
            }
            catch { }
        }

        public static class ModernTheme
        {
            public static bool IsDarkMode { get; private set; }

            public static Color Background => IsDarkMode ? Color.FromArgb(30, 30, 30) : Color.FromArgb(249, 250, 251);
            public static Color Sidebar => IsDarkMode ? Color.FromArgb(37, 37, 38) : Color.FromArgb(255, 255, 255);
            public static Color Surface => IsDarkMode ? Color.FromArgb(45, 45, 48) : Color.FromArgb(255, 255, 255);
            public static Color Accent => Color.FromArgb(37, 99, 235);
            public static Color AccentHover => Color.FromArgb(59, 130, 246);
            public static Color TextPrimary => IsDarkMode ? Color.FromArgb(224, 224, 224) : Color.FromArgb(17, 24, 39);
            public static Color TextSecondary => IsDarkMode ? Color.FromArgb(150, 150, 150) : Color.FromArgb(107, 114, 128);
            public static Color Border => IsDarkMode ? Color.FromArgb(63, 63, 70) : Color.FromArgb(229, 231, 235);
            public static Color HoverBg => IsDarkMode ? Color.FromArgb(51, 51, 55) : Color.FromArgb(243, 244, 246);
            public static Color Danger => Color.FromArgb(220, 38, 38);

            public static readonly Font MainFont = new Font("Segoe UI", 10);
            public static readonly Font HeaderFont = new Font("Segoe UI", 11, FontStyle.Bold);

            public static void DetectSystemTheme()
            {
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                    var value = key?.GetValue("AppsUseLightTheme");
                    IsDarkMode = value is int v && v == 0;
                }
                catch
                {
                    IsDarkMode = false;
                }
            }

            public static void ToggleTheme()
            {
                IsDarkMode = !IsDarkMode;
            }
        }

        private Panel panelSidebar = null!;
        private Panel panelContent = null!;
        private Panel navIndicator = null!;
        private Button btnNavBackups = null!;
        private Button btnNavSchedule = null!;
        private Button btnNavIgnore = null!;
        private Button btnNavHistory = null!;

        private NotifyIcon trayIcon = null!;
        private ContextMenuStrip trayMenu = null!;
        private ToolStripMenuItem trayCancelItem = null!;

        private Panel pnlIgnore = null!;
        private ListBox lstIgnoreItems = null!;
        private Button btnAddIgnoreFile = null!;
        private Button btnAddIgnoreFolder = null!;
        private Button btnAddIgnoreExtension = null!;
        private Button btnSaveIgnore = null!;
        private TextBox txtIgnoreExtension = null!;
        private Button btnRemoveIgnoreItem = null!;

        private Panel pnlHistory = null!;
        private ListView lstHistory = null!;
        private Button btnClearHistory = null!;
        private Button btnOpenLog = null!;

        private CheckBox chkMirrorMode = null!;
        private Label lblMirrorMode = null!;
        private Label lblCurrentFile = null!;
        private Label lblBytesInfo = null!;
        private Button btnThemeToggle = null!;

        private bool taskModified = false;
        private bool scheduleModified = false;
        private bool ignoreModified = false;

        private bool _initializing = true;
        private bool _suppressUiMark = false;

        private Panel? taskHeader;
        private string _currentPage = "backups";

        // 実行中のバックアップ
        private bool _backupRunning = false;
        private bool _runningDryRun = false;
        private CancellationTokenSource? _cts;
        private BackupManager? _currentManager;
        private RunLock? _runLock;

        private static bool IsElevated
        {
            get
            {
                try
                {
                    using var id = WindowsIdentity.GetCurrent();
                    return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch { return false; }
            }
        }

        public Form1()
        {
            ModernTheme.DetectSystemTheme();
            _initializing = true;
            InitializeComponent();
            MarkSecondaryLabels();
            EnsureTaskHeader();
            InitializeTrayIcon();
            LoadBackupTasks();
            LoadOptions();
            LoadSchedule();
            LoadIgnoreList();
            LoadHistoryList();
            LoadSettingsPage();
            ApplyModernTheme();
            _initializing = false;

            ShowPage("backups", btnNavBackups);

            flowPanel.ControlAdded += (s, e) => { MarkTaskModified(); UpdateFlowPanelScroll(); };
            flowPanel.ControlRemoved += (s, e) => { MarkTaskModified(); UpdateFlowPanelScroll(); };

            chkMirrorMode.CheckedChanged += chkMirrorMode_CheckedChanged;
            nudParallel.ValueChanged += (s, e) => MarkTaskModified();
            chkCompareHash.CheckedChanged += (s, e) => MarkTaskModified();

            cmbFrequency.SelectedIndexChanged += (s, e) => { UpdateScheduleControls(); MarkScheduleModified(); };
            dtpScheduleTime.ValueChanged += (s, e) => MarkScheduleModified();
            clbWeekDays.ItemCheck += (s, e) => MarkScheduleModified();
            chkHighest.CheckedChanged += (s, e) => MarkScheduleModified();
            chkAcOnly.CheckedChanged += (s, e) => MarkScheduleModified();
            chkWake.CheckedChanged += (s, e) => MarkScheduleModified();
            chkNotifyMissed.CheckedChanged += (s, e) => MarkScheduleModified();

            chkSkipHiddenSystem.CheckedChanged += (s, e) => MarkIgnoreModified();
            chkFollowLinks.CheckedChanged += (s, e) => MarkIgnoreModified();

            this.Shown += Form1_Shown;

            ResetButtonStyle(btnSave);
            ResetButtonStyle(btnSaveSchedule);
            ResetButtonStyle(btnSaveIgnore);
        }

        // 補足説明のラベルは、テーマ適用後も薄い文字色にする
        private void MarkSecondaryLabels()
        {
            foreach (var l in new Label[] { lblMirrorMode, lblProgress, lblCurrentFile, lblBytesInfo, lblScheduleStatus,
                                            lblIgnoreHint, lblHistoryHint, lblExportHint, lblVersion })
                l.Tag = "secondary";
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_backupRunning)
            {
                MessageBox.Show("バックアップ実行中です。完了または中止してから閉じてください。", "確認", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }

            if (taskModified || scheduleModified || ignoreModified)
            {
                var result = MessageBox.Show(
                    "保存されていない変更があります。保存せずに閉じますか？",
                    "確認",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);

                if (result == DialogResult.No)
                {
                    e.Cancel = true;
                    return;
                }
            }

            // ウィンドウを閉じてもトレイに常駐する
            this.Hide();
            e.Cancel = true;
        }

        private void MarkTaskModified()
        {
            if (_initializing || _suppressUiMark) return;
            taskModified = true;
            HighlightButton(btnSave);
        }

        private void MarkScheduleModified()
        {
            if (_initializing || _suppressUiMark) return;
            scheduleModified = true;
            HighlightButton(btnSaveSchedule);
        }

        private void MarkIgnoreModified()
        {
            if (_initializing || _suppressUiMark) return;
            ignoreModified = true;
            HighlightButton(btnSaveIgnore);
        }

        private void HighlightButton(Button button)
        {
            button.FlatAppearance.BorderColor = ModernTheme.Accent;
            button.FlatAppearance.BorderSize = 1;
            button.ForeColor = ModernTheme.Accent;
        }

        private void ResetButtonStyle(Button? button)
        {
            if (button == null) return;
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = ModernTheme.Surface;
            button.ForeColor = ModernTheme.TextPrimary;
            button.FlatAppearance.BorderColor = ModernTheme.Border;
            button.FlatAppearance.BorderSize = 1;
            button.Font = ModernTheme.MainFont;
        }

        // ================= テーマ =================

        private void UpdateThemeToggleButton()
        {
            if (btnThemeToggle == null) return;
            btnThemeToggle.Text = ModernTheme.IsDarkMode ? "☀ ライトモード" : "☾ ダークモード";
            btnThemeToggle.BackColor = ModernTheme.Sidebar;
            btnThemeToggle.ForeColor = ModernTheme.TextSecondary;
        }

        private void btnThemeToggle_Click(object? sender, EventArgs e)
        {
            ModernTheme.ToggleTheme();
            ApplyDwmDarkMode();
            ApplyModernTheme();
            foreach (Control c in flowPanel.Controls)
            {
                if (c is BackupTaskControl taskCtrl)
                    taskCtrl.ApplyTheme();
            }
            ShowPage(_currentPage, NavButtonFor(_currentPage));
        }

        private void ApplyDwmDarkMode()
        {
            try
            {
                int value = ModernTheme.IsDarkMode ? 1 : 0;
                DwmSetWindowAttribute(this.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
            }
            catch { }
        }

        private void UpdateFlowPanelScroll()
        {
            int totalHeight = 0;
            foreach (Control c in flowPanel.Controls)
                totalHeight += c.Height + c.Margin.Top + c.Margin.Bottom;
            flowPanel.AutoScroll = totalHeight > flowPanel.ClientSize.Height;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDwmDarkMode();
        }

        private void ApplyModernTheme()
        {
            this.BackColor = ModernTheme.Background;
            this.ForeColor = ModernTheme.TextPrimary;
            this.Font = ModernTheme.MainFont;

            foreach (Control c in this.Controls) ApplyThemeToControl(c);

            panelSidebar.BackColor = ModernTheme.Sidebar;
            panelContent.BackColor = ModernTheme.Background;

            navIndicator.BackColor = ModernTheme.Accent;
            navIndicator.Visible = false;

            if (taskModified) HighlightButton(btnSave);
            if (scheduleModified) HighlightButton(btnSaveSchedule);
            if (ignoreModified) HighlightButton(btnSaveIgnore);

            UpdateThemeToggleButton();
        }

        private void ApplyThemeToControl(Control ctrl)
        {
            if (ctrl is Button btn && btn.Parent == panelSidebar) return;

            ctrl.Font = ModernTheme.MainFont;

            if (ctrl is Panel p && p != panelSidebar && p != panelContent)
            {
                p.BackColor = ModernTheme.Background;
            }
            else if (ctrl is FlowLayoutPanel flp)
            {
                flp.BackColor = ModernTheme.Background;
                ApplyDarkScrollbar(flp);
            }

            if (ctrl is Button b)
            {
                b.FlatStyle = FlatStyle.Flat;
                if (b == btnStartBackup)
                {
                    b.BackColor = ModernTheme.Accent;
                    b.ForeColor = Color.White;
                    b.MouseEnter += (s, e) => b.BackColor = ModernTheme.AccentHover;
                    b.MouseLeave += (s, e) => b.BackColor = ModernTheme.Accent;
                }
                else
                {
                    b.BackColor = ModernTheme.Surface;
                    b.ForeColor = b == btnCancel ? ModernTheme.Danger : ModernTheme.TextPrimary;
                    b.FlatAppearance.BorderColor = ModernTheme.Border;
                    b.MouseEnter += (s, e) => b.BackColor = ModernTheme.HoverBg;
                    b.MouseLeave += (s, e) => b.BackColor = ModernTheme.Surface;
                }
                b.FlatAppearance.BorderSize = 1;
            }
            else if (ctrl is Label l)
            {
                l.ForeColor = (l.Tag as string) == "secondary" ? ModernTheme.TextSecondary : ModernTheme.TextPrimary;
            }
            else if (ctrl is TextBox t)
            {
                t.BackColor = ModernTheme.Surface;
                t.ForeColor = ModernTheme.TextPrimary;
                t.BorderStyle = BorderStyle.FixedSingle;
            }
            else if (ctrl is NumericUpDown nud)
            {
                nud.BackColor = ModernTheme.Surface;
                nud.ForeColor = ModernTheme.TextPrimary;
            }
            else if (ctrl is ListBox lb)
            {
                lb.BackColor = ModernTheme.Surface;
                lb.ForeColor = ModernTheme.TextPrimary;
                lb.BorderStyle = lb is CheckedListBox ? BorderStyle.None : BorderStyle.FixedSingle;
                ApplyDarkScrollbar(lb);
            }
            else if (ctrl is ComboBox cb)
            {
                cb.BackColor = ModernTheme.Surface;
                cb.ForeColor = ModernTheme.TextPrimary;
                ApplyDarkScrollbar(cb);
            }
            else if (ctrl is CheckBox chk)
            {
                chk.ForeColor = ModernTheme.TextPrimary;
            }
            else if (ctrl is ListView lv)
            {
                lv.BackColor = ModernTheme.Surface;
                lv.ForeColor = ModernTheme.TextPrimary;
                lv.BorderStyle = BorderStyle.FixedSingle;
                ApplyDarkScrollbar(lv);
                lv.OwnerDraw = true;
                lv.DrawColumnHeader -= ListView_DrawColumnHeader;
                lv.DrawColumnHeader += ListView_DrawColumnHeader;
                lv.DrawItem -= ListView_DrawItem;
                lv.DrawItem += ListView_DrawItem;
                lv.DrawSubItem -= ListView_DrawSubItem;
                lv.DrawSubItem += ListView_DrawSubItem;
            }

            foreach (Control child in ctrl.Controls) ApplyThemeToControl(child);
        }

        private void ListView_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
        {
            using var backBrush = new SolidBrush(ModernTheme.Surface);
            e.Graphics.FillRectangle(backBrush, e.Bounds);
            using var borderPen = new Pen(ModernTheme.Border);
            e.Graphics.DrawLine(borderPen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
            e.Graphics.DrawLine(borderPen, e.Bounds.Right - 1, e.Bounds.Top, e.Bounds.Right - 1, e.Bounds.Bottom);
            var textBounds = Rectangle.Inflate(e.Bounds, -4, 0);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", e.Font, textBounds, ModernTheme.TextPrimary,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
        }

        private void ListView_DrawItem(object? sender, DrawListViewItemEventArgs e)
        {
            e.DrawDefault = true;
        }

        private void ListView_DrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
        {
            e.DrawDefault = true;
        }

        // ================= ページ・トレイ =================

        private Button NavButtonFor(string page) => page switch
        {
            "schedule" => btnNavSchedule,
            "ignore" => btnNavIgnore,
            "history" => btnNavHistory,
            "settings" => btnNavSettings,
            _ => btnNavBackups,
        };

        private void ShowPage(string pageName, Button navButton)
        {
            _currentPage = pageName;

            foreach (Control c in panelSidebar.Controls)
            {
                if (c is Button b && b != btnThemeToggle)
                {
                    b.BackColor = ModernTheme.Sidebar;
                    b.ForeColor = ModernTheme.TextSecondary;
                }
            }
            navButton.BackColor = ModernTheme.HoverBg;
            navButton.ForeColor = ModernTheme.Accent;

            navIndicator.Location = new Point(0, navButton.Location.Y);
            navIndicator.Height = navButton.Height;
            navIndicator.Visible = true;

            pnlTasks.Visible = pageName == "backups";
            pnlSchedule.Visible = pageName == "schedule";
            pnlIgnore.Visible = pageName == "ignore";
            pnlHistory.Visible = pageName == "history";
            pnlSettings.Visible = pageName == "settings";

            if (pageName == "schedule") UpdateScheduleStatus();
            if (pageName == "history") LoadHistoryList();
        }

        private void InitializeTrayIcon()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("開く", null, (s, e) => ShowWindow());
            trayCancelItem = new ToolStripMenuItem("バックアップを中止", null, (s, e) => _cts?.Cancel()) { Enabled = false };
            trayMenu.Items.Add(trayCancelItem);
            trayMenu.Items.Add("終了", null, (s, e) =>
            {
                if (_backupRunning)
                {
                    ShowWindow();
                    MessageBox.Show("バックアップ実行中です。中止してから終了してください。", "確認", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                trayIcon.Visible = false;
                trayIcon.Dispose();
                Environment.Exit(0);
            });

            trayIcon = new NotifyIcon
            {
                Icon = SystemIcons.Application,
                ContextMenuStrip = trayMenu,
                Text = "FilesBackup",
                Visible = true
            };

            trayIcon.DoubleClick += (s, e) => ShowWindow();
        }

        public void ShowWindow()
        {
            this.Show();
            this.WindowState = FormWindowState.Normal;
            this.BringToFront();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (this.WindowState == FormWindowState.Minimized)
            {
                this.Hide();
            }
        }

        // 予定の実行が行われていなければ知らせる（PC の電源が切れていた等）
        private async void Form1_Shown(object? sender, EventArgs e)
        {
            try
            {
                var cfg = ConfigStore.LoadSchedule();
                if (cfg == null || !cfg.NotifyMissedRuns) return;

                DateTime savedAt = File.GetLastWriteTime(AppPaths.ScheduleConfigPath);
                var missed = SchedulerService.FindMissedRun(cfg, Logger.LoadHistory(), DateTime.Now, savedAt);
                if (missed == null || RunLock.IsBackupRunning()) return;

                var answer = MessageBox.Show(
                    $"{missed:yyyy/MM/dd HH:mm} に予定されていたバックアップが実行されていません。\n今すぐ実行しますか？",
                    "予定のバックアップ", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer == DialogResult.Yes)
                {
                    ShowPage("backups", btnNavBackups);
                    await RunBackupAsync(dryRun: false);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("予定実行の確認に失敗しました", ex);
            }
        }

        // ================= バックアップ設定 =================

        private void LoadBackupTasks()
        {
            try
            {
                foreach (var task in ConfigStore.LoadTasks())
                    AddTaskControl(task.Source, task.Destination, bypassBlankGuard: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show("設定ファイルの読み込みに失敗しました: " + ex.Message);
            }
        }

        private void LoadOptions()
        {
            _suppressUiMark = true;
            try
            {
                var o = ConfigStore.LoadOptions();
                chkMirrorMode.Checked = o.MirrorMode;
                nudParallel.Value = Math.Clamp(o.MaxDegreeOfParallelism, 1, 8);
                chkCompareHash.Checked = o.CompareByHash;
                chkSkipHiddenSystem.Checked = o.SkipHiddenAndSystem;
                chkFollowLinks.Checked = o.FollowReparsePoints;
            }
            catch (Exception ex)
            {
                MessageBox.Show("オプションの読み込みに失敗しました: " + ex.Message);
            }
            finally
            {
                _suppressUiMark = false;
            }
        }

        private List<BackupTask> CollectTasksFromUi()
        {
            // 画面の上から順に実行する（表示順＝保存順）
            var tasks = new List<BackupTask>();
            foreach (Control ctrl in flowPanel.Controls)
            {
                if (ctrl is BackupTaskControl taskControl)
                {
                    var source = taskControl.Source;
                    var destination = taskControl.Destination;
                    if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination)) continue;
                    if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!tasks.Exists(t =>
                        t.Source.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                        t.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase)))
                    {
                        tasks.Add(new BackupTask { Source = source, Destination = destination });
                    }
                }
            }
            return tasks;
        }

        /// <summary>バックアップ設定とオプション（ミラー・並列数・ハッシュ）を保存する。成功したら true</summary>
        private bool SaveBackupTasks(bool showMessage)
        {
            try
            {
                var tasks = CollectTasksFromUi();

                // 危険な設定（コピー元の配下・システム領域への書き込み）は保存させない
                var configErrors = BackupManager.FindConfigErrors(tasks);
                if (configErrors.Count > 0)
                {
                    MessageBox.Show(
                        "次の設定は危険なため保存できません。修正してください。\n\n" + string.Join("\n", configErrors),
                        "保存できません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }

                var options = ConfigStore.LoadOptions();
                options.MirrorMode = chkMirrorMode.Checked;
                options.MaxDegreeOfParallelism = (int)nudParallel.Value;
                options.CompareByHash = chkCompareHash.Checked;

                ConfigStore.SaveTasks(tasks);
                ConfigStore.SaveOptions(options);

                taskModified = false;
                ResetButtonStyle(btnSave);

                if (options.MirrorMode)
                {
                    var conflicts = BackupManager.FindMirrorConflicts(tasks, out _);
                    if (conflicts.Count > 0)
                    {
                        MessageBox.Show(
                            "保存しました。\n\nただし次の設定にはミラーモード（削除）を適用しません（他のデータを消してしまう恐れがあるため）。\n\n"
                            + string.Join("\n", conflicts),
                            "ミラーモードについて", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        return true;
                    }
                }

                if (showMessage)
                    MessageBox.Show("設定を保存しました。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("設定の保存に失敗しました: " + ex.Message);
                return false;
            }
        }

        private void btnSave_Click(object? sender, EventArgs e)
        {
            SaveBackupTasks(showMessage: true);
        }

        private void chkMirrorMode_CheckedChanged(object? sender, EventArgs e)
        {
            if (_initializing || _suppressUiMark) return;

            if (chkMirrorMode.Checked)
            {
                var answer = MessageBox.Show(
                    "ミラーモードでは、コピー元に無いファイルをコピー先から削除します。\n" +
                    "保存すると、スケジュールによる自動実行にも適用されます。\n\n" +
                    "・他の設定とコピー先やコピー元が重なる設定には適用しません\n" +
                    "・除外設定に一致するファイルやリンクは削除しません\n\n" +
                    "有効にしますか？",
                    "ミラーモード", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);

                if (answer != DialogResult.Yes)
                {
                    _suppressUiMark = true;
                    chkMirrorMode.Checked = false;
                    _suppressUiMark = false;
                    return;
                }
            }
            MarkTaskModified();
        }

        private void btnAddTask_Click(object? sender, EventArgs e)
        {
            AddTaskControl();
        }

        private void AddTaskControl(string source = "", string destination = "", bool bypassBlankGuard = false)
        {
            if (!bypassBlankGuard)
            {
                foreach (Control ctrl in flowPanel.Controls)
                {
                    if (ctrl is BackupTaskControl existingControl &&
                        string.IsNullOrWhiteSpace(existingControl.Source) &&
                        string.IsNullOrWhiteSpace(existingControl.Destination))
                        return;
                }
            }

            var taskControl = new BackupTaskControl();
            if (!string.IsNullOrEmpty(source) || !string.IsNullOrEmpty(destination))
                taskControl.SetTask(new BackupTask { Source = source, Destination = destination }, silent: true);

            taskControl.OnChanged += (s, ev) => MarkTaskModified();
            taskControl.OnMoveUpRequested += (s, ev) =>
            {
                int index = flowPanel.Controls.GetChildIndex(taskControl);
                int minIndex = (taskHeader != null && !taskHeader.IsDisposed) ? 1 : 0;
                if (index > minIndex)
                {
                    flowPanel.Controls.SetChildIndex(taskControl, index - 1);
                    flowPanel.Invalidate();
                    MarkTaskModified();
                }
            };
            taskControl.OnMoveDownRequested += (s, ev) =>
            {
                int index = flowPanel.Controls.GetChildIndex(taskControl);
                if (index < flowPanel.Controls.Count - 1)
                {
                    flowPanel.Controls.SetChildIndex(taskControl, index + 1);
                    flowPanel.Invalidate();
                    MarkTaskModified();
                }
            };
            taskControl.OnRemoveRequested += (s, ev) =>
            {
                flowPanel.Controls.Remove(taskControl);
                MarkTaskModified();
            };

            // 末尾に追加する（以前は見出しの直後に挿入していたため、表示が実行順と逆になっていた）
            flowPanel.Controls.Add(taskControl);
        }

        private void EnsureTaskHeader()
        {
            if (taskHeader != null && !taskHeader.IsDisposed) return;
            const int srcLeft = 82;
            const int dstLeft = 337;

            taskHeader = new Panel
            {
                Width = 820,
                Height = 20,
                Margin = new Padding(0, 0, 0, 4),
                BackColor = ModernTheme.Background
            };

            var lblSrc = new Label { AutoSize = true, Text = "コピー元", Font = new Font(Font, FontStyle.Bold), Location = new Point(srcLeft, 0), Tag = "secondary" };
            var lblDst = new Label { AutoSize = true, Text = "コピー先", Font = new Font(Font, FontStyle.Bold), Location = new Point(dstLeft, 0), Tag = "secondary" };
            taskHeader.Controls.Add(lblSrc);
            taskHeader.Controls.Add(lblDst);

            flowPanel.Controls.Add(taskHeader);
            flowPanel.Controls.SetChildIndex(taskHeader, 0);
        }

        // ================= 実行 =================

        private async void btnStartBackup_Click(object? sender, EventArgs e) => await RunBackupAsync(dryRun: false);

        private async void btnPreview_Click(object? sender, EventArgs e) => await RunBackupAsync(dryRun: true);

        private void btnPause_Click(object? sender, EventArgs e)
        {
            if (_currentManager == null) return;
            if (_currentManager.IsPaused)
            {
                _currentManager.Resume();
                btnPause.Text = "一時停止";
            }
            else
            {
                _currentManager.Pause();
                btnPause.Text = "再開";
            }
        }

        private void btnCancel_Click(object? sender, EventArgs e)
        {
            if (_cts == null) return;
            if (MessageBox.Show("バックアップを中止しますか？（コピー済みのファイルはそのまま残ります）", "確認",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                _currentManager?.Resume();   // 一時停止中でも中止できるように
                _cts.Cancel();
                btnCancel.Enabled = false;
                lblProgress.Text = "中止しています...";
            }
        }

        private async void btnCheck_Click(object? sender, EventArgs e)
        {
            if (!ConfirmUnsavedTasks()) return;

            btnCheck.Enabled = false;
            try
            {
                var manager = CreateManager();
                var report = await Task.Run(manager.Validate);

                var sb = new StringBuilder();
                if (report.Errors.Count == 0 && report.Warnings.Count == 0)
                {
                    sb.AppendLine("問題は見つかりませんでした。");
                }
                else
                {
                    if (report.Errors.Count > 0)
                    {
                        sb.AppendLine("【エラー】");
                        foreach (var m in report.Errors) sb.AppendLine("・" + m);
                        sb.AppendLine();
                    }
                    if (report.Warnings.Count > 0)
                    {
                        sb.AppendLine("【注意】");
                        foreach (var m in report.Warnings) sb.AppendLine("・" + m);
                    }
                }
                sb.AppendLine();
                sb.AppendLine("コピーされる件数や容量は「プレビュー」で確認できます。");

                MessageBox.Show(sb.ToString(), "事前チェック", MessageBoxButtons.OK,
                    report.Errors.Count > 0 ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("事前チェックに失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnCheck.Enabled = !_backupRunning;
            }
        }

        private static BackupManager CreateManager()
        {
            var manager = new BackupManager
            {
                Tasks = ConfigStore.LoadTasks(),
                Options = ConfigStore.LoadOptions(),
            };
            manager.ApplyIgnore(ConfigStore.LoadIgnore());
            return manager;
        }

        // 画面の変更が未保存なら、保存するか確認する。続行してよければ true
        private bool ConfirmUnsavedTasks()
        {
            if (!taskModified) return true;

            var answer = MessageBox.Show(
                "バックアップ設定の変更が保存されていません。保存してから続けますか？\n（「いいえ」は保存済みの設定で続けます）",
                "確認", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.Yes) return SaveBackupTasks(showMessage: false);
            return true;
        }

        private async Task RunBackupAsync(bool dryRun)
        {
            if (_backupRunning) return;
            if (!ConfirmUnsavedTasks()) return;

            // 自動実行と重ならないようにする（取得したUIスレッドで解放する）
            var runLock = RunLock.TryAcquire();
            if (!runLock.Acquired)
            {
                runLock.Dispose();
                MessageBox.Show("別のバックアップ（スケジュールによる自動実行など）が実行中です。終わってから実行してください。",
                    "実行中", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            BackupManager manager;
            try
            {
                manager = CreateManager();

                if (!dryRun)
                {
                    var report = await Task.Run(manager.Validate);
                    if (report.Errors.Count > 0)
                    {
                        var answer = MessageBox.Show(
                            "次の問題があります。\n\n" + string.Join("\n", report.Errors.Select(m => "・" + m)) +
                            "\n\nこのまま実行しますか？（問題のある設定はスキップまたはエラーになります）",
                            "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                        if (answer != DialogResult.Yes)
                        {
                            runLock.Dispose();
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                runLock.Dispose();
                MessageBox.Show("設定の読み込みに失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            _runLock = runLock;
            _backupRunning = true;
            _runningDryRun = dryRun;
            _cts = new CancellationTokenSource();
            _currentManager = manager;
            SetRunningUi(true, dryRun);

            var progress = new Progress<BackupProgress>(UpdateProgressUi);
            var token = _cts.Token;
            BackupResult? result = null;

            try
            {
                result = await Task.Run(() => manager.ExecuteAllWithResult(token, progress, dryRun, "manual"));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"バックアップ中にエラーが発生しました: {ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _backupRunning = false;
                _currentManager = null;
                _cts.Dispose();
                _cts = null;
                _runLock?.Dispose();
                _runLock = null;
                SetRunningUi(false, dryRun);
            }

            if (result != null)
            {
                ShowResult(result, dryRun);
                if (!dryRun) LoadHistoryList();
            }
        }

        private void SetRunningUi(bool running, bool dryRun)
        {
            btnStartBackup.Enabled = !running;
            btnPreview.Enabled = !running;
            btnCheck.Enabled = !running;
            btnSave.Enabled = !running;
            btnAddTask.Enabled = !running;
            btnPause.Enabled = running;
            btnCancel.Enabled = running;
            btnPause.Text = "一時停止";
            trayCancelItem.Enabled = running;

            btnStartBackup.Text = running && !dryRun ? "バックアップ実行中..." : "バックアップ開始";
            btnPreview.Text = running && dryRun ? "プレビュー中..." : "プレビュー";

            progressBar1.Visible = running;
            lblProgress.Visible = running;
            lblCurrentFile.Visible = running;
            lblBytesInfo.Visible = running;

            if (running)
            {
                progressBar1.Style = ProgressBarStyle.Marquee;
                lblProgress.Text = "準備中...";
                lblCurrentFile.Text = "";
                lblBytesInfo.Text = "";
            }
        }

        private void UpdateProgressUi(BackupProgress p)
        {
            if (!_backupRunning) return;

            string phase = p.Phase switch
            {
                BackupPhase.Scanning => "確認中",
                BackupPhase.Copying => "コピー中",
                BackupPhase.Mirroring => "ミラー削除中",
                _ => ""
            };

            if (p.EstimatedTotal > 0)
            {
                if (progressBar1.Style != ProgressBarStyle.Continuous)
                {
                    progressBar1.Style = ProgressBarStyle.Continuous;
                    progressBar1.Maximum = 1000;
                }
                progressBar1.Value = (int)Math.Min(990, (long)p.Scanned * 1000 / p.EstimatedTotal);
            }
            else if (progressBar1.Style != ProgressBarStyle.Marquee)
            {
                progressBar1.Style = ProgressBarStyle.Marquee;
            }

            string total = p.EstimatedTotal > 0 ? $" / 約{p.EstimatedTotal:N0}" : "";
            lblProgress.Text = $"{phase}: {p.TaskLabel}　確認済み {p.Scanned:N0}{total} 件";
            lblCurrentFile.Text = string.IsNullOrEmpty(p.Current) ? "" : $"処理中: {p.Current}";
            string copied = _runningDryRun ? "コピー予定" : "コピー済み";
            lblBytesInfo.Text = $"{copied}: {p.Copied:N0} 件（{FormatBytes(p.BytesCopied)}）" +
                                (p.Deleted > 0 ? $"　{(_runningDryRun ? "削除予定" : "削除")}: {p.Deleted:N0} 件" : "");
        }

        private void ShowResult(BackupResult result, bool dryRun)
        {
            var sb = new StringBuilder();
            if (dryRun)
                sb.AppendLine(result.Cancelled ? "プレビューを中止しました。" : "プレビュー結果（実際にはコピー・削除していません）");
            else
                sb.AppendLine(result.Cancelled ? "バックアップを中止しました。" : "バックアップが完了しました。");
            sb.AppendLine();

            string copyLabel = dryRun ? "コピー予定" : "コピー";
            string deleteLabel = dryRun ? "削除予定" : "削除";

            foreach (var t in result.TaskResults)
            {
                sb.AppendLine($"■ {t.Source} → {t.Destination}　[{t.Status}]");
                if (t.FilesScanned > 0 || t.FilesCopied > 0)
                {
                    sb.Append($"　{copyLabel}: {t.FilesCopied:N0} 件（{FormatBytes(t.BytesCopied)}）　スキップ: {t.FilesSkipped:N0} 件");
                    if (t.FilesDeleted > 0) sb.Append($"　{deleteLabel}: {t.FilesDeleted:N0} 件");
                    if (t.Errors > 0) sb.Append($"　エラー: {t.Errors:N0} 件");
                    sb.AppendLine();
                }
                if (!string.IsNullOrEmpty(t.Message))
                    sb.AppendLine($"　{t.Message}");
            }

            if (dryRun)
            {
                sb.AppendLine();
                foreach (var root in result.TaskResults
                             .Select(t => SafeRoot(t.Destination))
                             .Where(r => r != null)
                             .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var drive = new DriveInfo(root!);
                        if (drive.IsReady)
                            sb.AppendLine($"空き容量 {root}: {FormatBytes(drive.AvailableFreeSpace)}");
                    }
                    catch { }
                }
            }

            sb.AppendLine();
            sb.AppendLine($"所要時間: {FormatDuration(result.Duration)}");
            if (result.Errors > 0)
                sb.AppendLine($"エラー: {result.Errors:N0} 件（「履歴」の詳細またはログで確認できます）");

            bool warn = result.Errors > 0 || result.Cancelled;
            if (!dryRun && !warn)
            {
                trayIcon.ShowBalloonTip(5000, "バックアップ完了",
                    $"コピー: {result.FilesCopied:N0}件 / スキップ: {result.FilesSkipped:N0}件", ToolTipIcon.Info);
            }

            MessageBox.Show(sb.ToString(), dryRun ? "プレビュー" : (warn ? "完了（確認が必要な項目あり）" : "完了"),
                MessageBoxButtons.OK, warn ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        }

        private static string? SafeRoot(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                return string.IsNullOrEmpty(root) || root.StartsWith(@"\\") ? null : root;
            }
            catch { return null; }
        }

        // ================= スケジュール =================

        private void LoadSchedule()
        {
            try
            {
                _suppressUiMark = true;
                var saved = ConfigStore.LoadSchedule();
                if (saved != null)
                {
                    dtpScheduleTime.Value = DateTime.Today.Add(saved.Time);
                    cmbFrequency.SelectedItem = saved.Frequency;
                    for (int i = 0; i < clbWeekDays.Items.Count; i++)
                    {
                        var day = (DayOfWeek)"日月火水木金土".IndexOf(clbWeekDays.Items[i].ToString()!);
                        clbWeekDays.SetItemChecked(i, saved.Frequency == "毎週" && saved.DaysOfWeek != null && saved.DaysOfWeek.Contains(day));
                    }
                    chkHighest.Checked = saved.EffectiveHighest;
                    chkAcOnly.Checked = saved.AcPowerOnly;
                    chkWake.Checked = saved.WakeToRun;
                    chkNotifyMissed.Checked = saved.NotifyMissedRuns;
                }
                else
                {
                    // 初期値（最高特権は既定でオフ）
                    cmbFrequency.SelectedIndex = 0;
                    dtpScheduleTime.Value = DateTime.Today.AddHours(12);
                    chkHighest.Checked = false;
                    chkAcOnly.Checked = true;
                    chkWake.Checked = false;
                    chkNotifyMissed.Checked = true;
                }

                if (cmbFrequency.SelectedIndex < 0) cmbFrequency.SelectedIndex = 0;
                UpdateScheduleControls();
            }
            catch (Exception ex)
            {
                MessageBox.Show("スケジュール読み込みエラー: " + ex.Message);
            }
            finally
            {
                _suppressUiMark = false;
            }
        }

        private void UpdateScheduleControls()
        {
            string selected = cmbFrequency.SelectedItem?.ToString() ?? "毎日";
            clbWeekDays.Visible = selected == "毎週";
            dtpScheduleTime.Enabled = selected != "起動時";
        }

        private ScheduleConfig BuildScheduleFromUi()
        {
            string frequency = cmbFrequency.SelectedItem?.ToString() ?? "毎日";
            List<DayOfWeek>? days = null;
            if (frequency == "毎週")
            {
                days = clbWeekDays.CheckedItems
                    .Cast<string>()
                    .Select(d => (DayOfWeek)"日月火水木金土".IndexOf(d))
                    .ToList();
            }

            var time = dtpScheduleTime.Value.TimeOfDay;
            return new ScheduleConfig
            {
                Frequency = frequency,
                Time = new TimeSpan(time.Hours, time.Minutes, 0),
                DaysOfWeek = days,
                RunWithHighestPrivileges = chkHighest.Checked,
                AcPowerOnly = chkAcOnly.Checked,
                WakeToRun = chkWake.Checked,
                NotifyMissedRuns = chkNotifyMissed.Checked,
            };
        }

        private void btnSaveSchedule_Click(object? sender, EventArgs e)
        {
            var config = BuildScheduleFromUi();
            if (config.Frequency == "毎週" && (config.DaysOfWeek == null || config.DaysOfWeek.Count == 0))
            {
                MessageBox.Show("曜日を1つ以上選んでください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RegisterSchedule(config, showMessage: true);
        }

        private bool RegisterSchedule(ScheduleConfig config, bool showMessage)
        {
            try
            {
                SchedulerService.Register(config, Application.ExecutablePath);
                ConfigStore.SaveSchedule(config);
                scheduleModified = false;
                ResetButtonStyle(btnSaveSchedule);
                if (showMessage)
                    MessageBox.Show("スケジュールを保存し、タスクに登録しました。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            catch (UnauthorizedAccessException) when (!IsElevated)
            {
                // 最高特権での登録や、管理者が登録した既存タスクの置き換えには管理者権限が必要
                ConfigStore.SaveSchedule(config);
                scheduleModified = false;
                ResetButtonStyle(btnSaveSchedule);

                var answer = MessageBox.Show(
                    "タスク スケジューラへの登録に管理者権限が必要です。\n" +
                    "（「最高特権で実行する」がオンの場合や、以前のバージョンが管理者として登録したタスクを置き換える場合）\n\n" +
                    "設定は保存しました。管理者としてアプリを再起動しますか？\n再起動後に「スケジュールを保存」をもう一度押してください。",
                    "管理者権限が必要です", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes)
                    RestartElevated();
                return false;
            }
            catch (Exception ex)
            {
                MessageBox.Show("スケジュール保存失敗: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
            finally
            {
                UpdateScheduleStatus();
            }
        }

        private void UpdateScheduleStatus()
        {
            var sb = new StringBuilder();
            var status = SchedulerService.GetStatus();

            if (!string.IsNullOrEmpty(status.Error))
            {
                sb.AppendLine($"タスクの状態を取得できませんでした（{status.Error}）");
            }
            else if (!status.Exists)
            {
                sb.AppendLine("タスク スケジューラに未登録です。「スケジュールを保存」で登録されます。");
            }
            else
            {
                sb.AppendLine($"登録済み（{status.RunLevel}）　次回: {FormatDate(status.NextRun)}　前回: {FormatDate(status.LastRun)}");
                if (!string.IsNullOrEmpty(status.ExecutablePath) &&
                    !string.Equals(status.ExecutablePath, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine($"※ 登録されている実行ファイルが、いま開いているものと異なります: {status.ExecutablePath}");
                }
            }

            var last = Logger.LoadHistory().LastOrDefault();
            if (last != null)
            {
                sb.AppendLine($"最後のバックアップ: {last.Timestamp:yyyy/MM/dd HH:mm}（{TriggerLabel(last.Trigger)}、" +
                              $"コピー {last.FilesCopied:N0} 件、エラー {last.Errors:N0} 件）");
            }

            lblScheduleStatus.Text = sb.ToString().TrimEnd();
        }

        private static string FormatDate(DateTime? value) => value?.ToString("yyyy/MM/dd HH:mm") ?? "—";

        // ================= 除外設定 =================

        private void LoadIgnoreList()
        {
            try
            {
                _suppressUiMark = true;
                var config = ConfigStore.LoadIgnore();
                lstIgnoreItems.Items.Clear();
                foreach (var p in config.Paths) lstIgnoreItems.Items.Add(p);
                foreach (var x in config.Extensions) lstIgnoreItems.Items.Add(x);
                foreach (var x in config.Patterns) lstIgnoreItems.Items.Add(x);
                foreach (var x in config.FolderNames) lstIgnoreItems.Items.Add(FolderNamePrefix + x);
                lstIgnoreItems.SelectedIndex = -1;
            }
            catch (Exception ex)
            {
                MessageBox.Show("除外設定の読み込みに失敗しました: " + ex.Message);
            }
            finally
            {
                _suppressUiMark = false;
            }
        }

        private bool AddIgnoreItem(string item)
        {
            foreach (var existing in lstIgnoreItems.Items)
            {
                if (string.Equals(existing.ToString(), item, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("既に追加されています。", "重複", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return false;
                }
            }
            lstIgnoreItems.Items.Add(item);
            MarkIgnoreModified();
            return true;
        }

        private void btnAddIgnoreFile_Click(object? sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog();
            if (dlg.ShowDialog() == DialogResult.OK)
                AddIgnoreItem(dlg.FileName);
        }

        private void btnAddIgnoreFolder_Click(object? sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog();
            if (dlg.ShowDialog() == DialogResult.OK)
                AddIgnoreItem(dlg.SelectedPath);
        }

        private void btnAddIgnoreExtension_Click(object? sender, EventArgs e)
        {
            string ext = txtIgnoreExtension.Text.Trim();
            if (string.IsNullOrEmpty(ext) || ext.IndexOfAny(new[] { '\\', '/', '*', '?', ':' }) >= 0)
            {
                MessageBox.Show("拡張子を入力してください（例 .tmp）", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (AddIgnoreItem(IgnoreConfig.NormalizeExtension(ext)))
                txtIgnoreExtension.Clear();
        }

        private void btnAddIgnorePattern_Click(object? sender, EventArgs e)
        {
            string pattern = txtIgnoreExtension.Text.Trim();
            if (!IgnoreConfig.IsPattern(pattern))
            {
                MessageBox.Show("* または ? を含むパターンを入力してください（例 **/Cache/**、F:\\work\\*.bak）", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (IgnoreConfig.IsExtension(pattern))
                pattern = IgnoreConfig.NormalizeExtension(pattern);   // "*.tmp" は拡張子として扱う
            if (AddIgnoreItem(pattern))
                txtIgnoreExtension.Clear();
        }

        private void btnAddIgnoreFolderName_Click(object? sender, EventArgs e)
        {
            string name = txtIgnoreExtension.Text.Trim().Trim('\\', '/');
            if (string.IsNullOrEmpty(name) || name.IndexOfAny(new[] { '\\', '/', '*', '?', ':' }) >= 0)
            {
                MessageBox.Show("フォルダ名だけを入力してください（例 .git、node_modules）", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (AddIgnoreItem(FolderNamePrefix + name))
                txtIgnoreExtension.Clear();
        }

        private void btnSaveIgnore_Click(object? sender, EventArgs e)
        {
            try
            {
                var config = new IgnoreConfig();
                foreach (var item in lstIgnoreItems.Items)
                {
                    string text = item.ToString()!;
                    if (text.StartsWith(FolderNamePrefix, StringComparison.Ordinal))
                        config.FolderNames.Add(text[FolderNamePrefix.Length..]);
                    else
                        config.Paths.Add(text);   // 拡張子・ワイルドカードは保存時に自動で振り分ける
                }
                ConfigStore.SaveIgnore(config);

                var options = ConfigStore.LoadOptions();
                options.SkipHiddenAndSystem = chkSkipHiddenSystem.Checked;
                options.FollowReparsePoints = chkFollowLinks.Checked;
                ConfigStore.SaveOptions(options);

                ignoreModified = false;
                ResetButtonStyle(btnSaveIgnore);
                LoadIgnoreList();
                MessageBox.Show("除外設定を保存しました。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("除外設定の保存に失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnRemoveIgnoreItem_Click(object? sender, EventArgs e)
        {
            var selected = lstIgnoreItems.SelectedItem;
            if (selected != null)
            {
                lstIgnoreItems.Items.Remove(selected);
                MarkIgnoreModified();
            }
            else
            {
                MessageBox.Show("削除する項目を選択してください。", "通知", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        // ================= 履歴 =================

        private void LoadHistoryList()
        {
            lstHistory.BeginUpdate();
            try
            {
                lstHistory.Items.Clear();
                foreach (var h in Logger.LoadHistory().AsEnumerable().Reverse())
                {
                    var item = new ListViewItem(h.Timestamp.ToString("yyyy/MM/dd HH:mm")) { Tag = h };
                    item.SubItems.Add(TriggerLabel(h.Trigger));
                    item.SubItems.Add(FormatDuration(h.Duration));
                    item.SubItems.Add($"{h.FilesCopied:N0}");
                    item.SubItems.Add($"{h.FilesSkipped:N0}");
                    item.SubItems.Add($"{h.FilesDeleted:N0}");
                    item.SubItems.Add($"{h.Errors:N0}");
                    item.SubItems.Add(FormatBytes(h.BytesCopied));
                    item.SubItems.Add(SummaryStatus(h));
                    if (h.Errors > 0 || h.Cancelled || h.TaskResults.Any(t => t.Status == "容量不足"))
                        item.ForeColor = ModernTheme.Danger;
                    lstHistory.Items.Add(item);
                }
            }
            finally
            {
                lstHistory.EndUpdate();
            }
        }

        // 旧バージョンの履歴には種別が無い
        private static string TriggerLabel(string? trigger) => trigger switch
        {
            "auto" => "自動",
            "manual" => "手動",
            _ => "—",
        };

        private static string SummaryStatus(BackupResult h)
        {
            if (h.Cancelled) return "中止";
            int skipped = h.TaskResults.Count(t => t.Status.StartsWith("スキップ") || t.Status == "容量不足");
            if (h.TaskResults.Any(t => t.Status == "容量不足")) return $"容量不足あり（{skipped}件）";
            if (skipped > 0) return $"スキップあり（{skipped}件）";
            if (h.Errors > 0) return "エラーあり";
            return "完了";
        }

        private void lstHistory_DoubleClick(object? sender, EventArgs e)
        {
            if (lstHistory.SelectedItems.Count == 0 || lstHistory.SelectedItems[0].Tag is not BackupResult h)
                return;

            var sb = new StringBuilder();
            sb.AppendLine($"日時: {h.Timestamp:yyyy/MM/dd HH:mm:ss}（{TriggerLabel(h.Trigger)}）");
            sb.AppendLine($"所要時間: {FormatDuration(h.Duration)}");
            sb.AppendLine($"コピー: {h.FilesCopied:N0} 件（{FormatBytes(h.BytesCopied)}）　スキップ: {h.FilesSkipped:N0} 件　削除: {h.FilesDeleted:N0} 件　エラー: {h.Errors:N0} 件");
            if (h.Cancelled) sb.AppendLine("※ 途中で中止されました");
            sb.AppendLine();

            if (h.TaskResults.Count > 0)
            {
                sb.AppendLine("【設定ごとの結果】");
                foreach (var t in h.TaskResults)
                {
                    sb.AppendLine($"■ {t.Source} → {t.Destination}　{(string.IsNullOrEmpty(t.Status) ? "" : $"[{t.Status}]")}");
                    sb.AppendLine($"　コピー {t.FilesCopied:N0} 件（{FormatBytes(t.BytesCopied)}）／スキップ {t.FilesSkipped:N0} 件／削除 {t.FilesDeleted:N0} 件／エラー {t.Errors:N0} 件");
                    if (!string.IsNullOrEmpty(t.Message)) sb.AppendLine($"　{t.Message}");
                }
                sb.AppendLine();
            }

            if (h.ErrorMessages.Count > 0)
            {
                sb.AppendLine($"【エラー内容】（先頭 {h.ErrorMessages.Count:N0} 件）");
                foreach (var m in h.ErrorMessages) sb.AppendLine(m);
            }

            ShowTextDialog("履歴の詳細", sb.ToString());
        }

        private void ShowTextDialog(string title, string text)
        {
            using var dialog = new Form
            {
                Text = title,
                Size = new Size(860, 560),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = ModernTheme.Background,
                Font = ModernTheme.MainFont,
                MinimizeBox = false,
                ShowInTaskbar = false,
            };
            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                BackColor = ModernTheme.Surface,
                ForeColor = ModernTheme.TextPrimary,
                BorderStyle = BorderStyle.None,
            };
            dialog.Controls.Add(box);
            dialog.Shown += (s, e) => { box.SelectionStart = 0; box.SelectionLength = 0; };
            dialog.ShowDialog(this);
        }

        private void btnClearHistory_Click(object? sender, EventArgs e)
        {
            if (MessageBox.Show("履歴を全て削除しますか？", "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                Logger.SaveHistory(new List<BackupResult>());
                LoadHistoryList();
            }
        }

        private void btnOpenLog_Click(object? sender, EventArgs e)
        {
            string logPath = Logger.GetLogPath();
            if (!File.Exists(logPath))
            {
                // 今日のログが無ければ、一番新しいログを開く
                logPath = Directory.Exists(AppPaths.LogDir)
                    ? new DirectoryInfo(AppPaths.LogDir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName ?? ""
                    : "";
            }

            if (File.Exists(logPath))
                Process.Start("notepad.exe", logPath);
            else
                MessageBox.Show("ログファイルがありません。", "通知", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void btnOpenLogFolder_Click(object? sender, EventArgs e) => OpenFolder(AppPaths.LogDir);

        // ================= 設定（保存先・インポート/エクスポート・権限） =================

        private void LoadSettingsPage()
        {
            txtConfigDir.Text = AppPaths.ConfigDir;
            bool elevated = IsElevated;
            lblPrivilege.Text = elevated
                ? "現在の実行権限: 管理者"
                : "現在の実行権限: 標準ユーザー（管理者権限が必要なフォルダは読み取れない場合があります）";
            btnRestartElevated.Enabled = !elevated;
            var version = typeof(Form1).Assembly.GetName().Version;
            lblVersion.Text = $"バージョン: {version?.ToString(3)}";
        }

        private void btnOpenConfigDir_Click(object? sender, EventArgs e) => OpenFolder(AppPaths.ConfigDir);

        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show("フォルダを開けませんでした: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnExport_Click(object? sender, EventArgs e)
        {
            if (taskModified || scheduleModified || ignoreModified)
            {
                MessageBox.Show("保存されていない変更があります。各ページで保存してからエクスポートしてください。", "確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Filter = "JSON ファイル (*.json)|*.json",
                FileName = $"FilesBackup_settings_{DateTime.Now:yyyyMMdd}.json",
            };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            try
            {
                ConfigStore.Export(dlg.FileName);
                MessageBox.Show($"設定をエクスポートしました。\n{dlg.FileName}", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("エクスポートに失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void btnImport_Click(object? sender, EventArgs e)
        {
            if (_backupRunning)
            {
                MessageBox.Show("バックアップ実行中はインポートできません。", "確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new OpenFileDialog { Filter = "JSON ファイル (*.json)|*.json|すべてのファイル (*.*)|*.*" };
            if (dlg.ShowDialog() != DialogResult.OK) return;

            SettingsBundle bundle;
            try
            {
                bundle = ConfigStore.ReadBundle(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show("読み込めませんでした。\n\n" + ex.Message, "インポート", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            int ignoreCount = bundle.Ignore.Paths.Count + bundle.Ignore.Extensions.Count + bundle.Ignore.Patterns.Count + bundle.Ignore.FolderNames.Count;
            var summary =
                $"次の設定で、現在の設定を上書きします。よろしいですか？\n\n" +
                $"・バックアップ設定: {bundle.Tasks.Count} 件\n" +
                $"・除外設定: {ignoreCount} 件\n" +
                $"・スケジュール: {(bundle.Schedule == null ? "含まれていません（現在の設定のまま）" : bundle.Schedule.Frequency)}\n" +
                $"・ミラーモード: {(bundle.Options.MirrorMode ? "有効" : "無効")}\n" +
                $"（エクスポート日時: {bundle.ExportedAt:yyyy/MM/dd HH:mm}）";

            if (MessageBox.Show(summary, "インポート", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            try
            {
                ConfigStore.ApplyBundle(bundle);
                ReloadAllSettings();

                if (bundle.Schedule != null &&
                    MessageBox.Show("読み込んだスケジュールをタスク スケジューラに登録しますか？", "インポート",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    RegisterSchedule(bundle.Schedule, showMessage: true);
                }
                else
                {
                    MessageBox.Show("インポートしました。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("インポートに失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void ReloadAllSettings()
        {
            _initializing = true;
            try
            {
                foreach (var ctrl in flowPanel.Controls.OfType<BackupTaskControl>().ToList())
                {
                    flowPanel.Controls.Remove(ctrl);
                    ctrl.Dispose();
                }
                LoadBackupTasks();
                LoadOptions();
                LoadSchedule();
                LoadIgnoreList();
                ApplyModernTheme();
            }
            finally
            {
                _initializing = false;
            }

            taskModified = scheduleModified = ignoreModified = false;
            ResetButtonStyle(btnSave);
            ResetButtonStyle(btnSaveSchedule);
            ResetButtonStyle(btnSaveIgnore);
            UpdateScheduleStatus();
        }

        private void btnRestartElevated_Click(object? sender, EventArgs e)
        {
            if (taskModified || scheduleModified || ignoreModified)
            {
                if (MessageBox.Show("保存されていない変更があります。保存せずに再起動しますか？", "確認",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
            }
            RestartElevated();
        }

        private void RestartElevated()
        {
            if (_backupRunning)
            {
                MessageBox.Show("バックアップ実行中は再起動できません。", "確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, Program.ElevatedRestartArg)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                });
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return;   // UAC の確認で「いいえ」が選ばれた
            }
            catch (Exception ex)
            {
                MessageBox.Show("再起動できませんでした: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            trayIcon.Visible = false;
            trayIcon.Dispose();
            Environment.Exit(0);
        }

        // ================= 書式 =================

        private static string FormatBytes(long bytes) => BackupManager.FormatBytes(bytes);

        private static string FormatDuration(TimeSpan d)
        {
            if (d.TotalHours >= 1) return $"{(int)d.TotalHours}時間{d.Minutes}分";
            if (d.TotalMinutes >= 1) return $"{d.Minutes}分{d.Seconds}秒";
            return $"{d.TotalSeconds:F1}秒";
        }
    }
}
