using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using FilesBackup.Models;
using FilesBackup.Services;
using FilesBackup.UI;

namespace FilesBackup
{
    public class Form1 : Form
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);

        [DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const string GitHubUrl = "https://github.com/gonzaresu-jp/FilesBackup";

        private enum IgnoreKind { Path, Extension, Pattern, FolderName }
        private sealed record IgnoreItem(IgnoreKind Kind, string Value);

        private static readonly string[] IgnoreKindLabels = { "パス", "拡張子", "ワイルドカード", "フォルダ名" };

        // ---- 起動モード ----
        private readonly string? _screenshotDir;
        private bool _allowVisible;                 // false の間はウィンドウを出さない（トレイ常駐で起動したとき）
        private bool _uiBuilt;                      // 画面の部品を作ってあるか（トレイ常駐中は作らずメモリを節約する）
        private bool _trayHintShown;
        private Action? _balloonAction;             // 通知をクリックしたときの動作
        private readonly System.Windows.Forms.Timer _startupTimer = new() { Interval = 1500 };
        private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = 60 * 60 * 1000 };

        // ---- アプリ設定・アップデート ----
        private AppSettings _appSettings = new();
        private UpdateInfo? _update;
        private bool _updating;
        private Card bannerUpdate = null!;
        private ThemedLabel lblBannerUpdate = null!;
        private ThemedLabel lblUpdateStatus = null!, lblUpdateNotes = null!;
        private ModernButton btnInstallUpdate = null!, btnCheckUpdate = null!;
        private ThinProgress updateProgress = null!;
        private ToggleSwitch tglAutoUpdate = null!, tglStartup = null!;

        // ---- 骨組み ----
        private NavRail rail = null!;
        private SideList side = null!;
        private RoundedPanel surface = null!;
        private readonly Dictionary<string, Control> _views = new();
        private NotifyIcon trayIcon = null!;
        private ToolStripMenuItem trayCancelItem = null!;
        private readonly Icon _appIcon = Theme.CreateAppIcon();

        private string _page = "backup";
        private string _sideKey = "overview";

        // ---- データ（画面とは別に保持し、テーマ切替で画面を作り直しても消えない）----
        private List<BackupTask> _tasks = new();
        private List<IgnoreItem> _ignore = new();
        private BackupOptions _options = new();
        private ScheduleConfig _schedule = new();
        private List<BackupResult> _history = new();
        private SchedulerService.TaskStatusInfo? _taskStatus;
        private bool _tasksDirty, _ignoreDirty, _scheduleDirty;
        private bool _loading;
        private int _taskIndex = -1;

        // ---- 実行中 ----
        private bool _backupRunning;
        private bool _runningDryRun;
        private CancellationTokenSource? _cts;
        private BackupManager? _currentManager;
        private RunLock? _runLock;
        private readonly Stopwatch _etaWatch = new();
        private string _etaTask = "";

        // ---- 概要 ----
        private Card bannerUnsaved = null!;
        private ModernButton btnCheck = null!, btnPreview = null!, btnPause = null!, btnCancel = null!, btnStart = null!;
        private StackPanel runPanel = null!;
        private ThinProgress progress = null!;
        private ThemedLabel lblRunMain = null!, lblRunFile = null!, lblRunBytes = null!;
        private StatTile tileLast = null!, tileCopied = null!, tileErrors = null!, tileNext = null!;
        private ToggleSwitch tglMirror = null!, tglHash = null!;
        private ComboBox cmbParallel = null!;

        // ---- 設定の編集 ----
        private PageHeader taskHeader = null!;
        private ModernInput inSrc = null!, inDst = null!;
        private ThemedLabel lblTaskWarn = null!, lblTaskInfo = null!, lblTaskLast = null!;
        private ModernButton btnTaskUp = null!, btnTaskDown = null!, btnTaskSave = null!;

        // ---- 履歴 ----
        private ListView lvHistory = null!;
        private PageHeader historyHeader = null!;

        // ---- スケジュール ----
        private PageHeader scheduleHeader = null!;
        private ThemedLabel lblFrequency = null!, lblScheduleStatus = null!;
        private ComboBox cmbHour = null!, cmbMinute = null!;
        private RowPanel rowTime = null!, rowDays = null!;
        private DayChips dayChips = null!;
        private ToggleSwitch tglHighest = null!, tglAcOnly = null!, tglWake = null!, tglNotify = null!;
        private ModernButton btnSaveSchedule = null!;

        // ---- 除外設定 ----
        private ComboBox cmbIgnoreKind = null!;
        private ModernInput inIgnore = null!;
        private ThemedLabel lblIgnoreHint = null!;
        private Card cardIgnoreList = null!;
        private ListBox lstIgnore = null!;
        private ToggleSwitch tglHidden = null!, tglLinks = null!;
        private ModernButton btnSaveIgnore = null!;

        // ---- 設定 ----
        private ToggleSwitch tglDark = null!, tglMachine = null!;

        public Form1(string? screenshotDir = null, bool startInTray = false)
        {
            _screenshotDir = screenshotDir;
            _allowVisible = !startInTray;
            _trayHintShown = startInTray;
            _appSettings = ConfigStore.LoadAppSettings();

            Theme.DetectSystem();
            Text = "FilesBackup";
            Icon = _appIcon;
            Font = Theme.Body;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(S(1180), S(740));
            MinimumSize = new Size(S(1040), S(680));

            LoadData();
            InitializeTrayIcon();

            // トレイ常駐で起動したときは、ウィンドウを開くまで画面を作らない
            if (_allowVisible || _screenshotDir != null)
                BuildUi();

            Shown += Form1_Shown;

            if (_screenshotDir == null)
            {
                // ウィンドウを出さずに起動しても動くよう、起動後の処理はタイマーで行う
                if (!_allowVisible) CreateHandle();
                _startupTimer.Tick += StartupTimer_Tick;
                _startupTimer.Start();
                _updateTimer.Tick += async (s, e) =>
                {
                    await CheckForUpdatesAsync(manual: false);
                    if (!Visible && !_backupRunning) MemoryTrimmer.Trim();
                };
                _updateTimer.Start();
            }
        }

        // トレイ常駐で起動したときは、ShowWindow が呼ばれるまでウィンドウを出さない
        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(_allowVisible && value);
        }

        private int S(int v) => (int)Math.Round(v * DeviceDpi / 96f);

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

        // ================================================================
        // データ
        // ================================================================

        private void LoadData()
        {
            try { _tasks = ConfigStore.LoadTasks(); }
            catch (Exception ex) { ShowError("バックアップ設定の読み込みに失敗しました", ex); }

            try { _options = ConfigStore.LoadOptions(); }
            catch (Exception ex) { ShowError("オプションの読み込みに失敗しました", ex); }

            try
            {
                var ignore = ConfigStore.LoadIgnore();
                _ignore = ignore.Paths.Select(p => new IgnoreItem(IgnoreKind.Path, p))
                    .Concat(ignore.Extensions.Select(x => new IgnoreItem(IgnoreKind.Extension, x)))
                    .Concat(ignore.Patterns.Select(x => new IgnoreItem(IgnoreKind.Pattern, x)))
                    .Concat(ignore.FolderNames.Select(x => new IgnoreItem(IgnoreKind.FolderName, x)))
                    .ToList();
            }
            catch (Exception ex) { ShowError("除外設定の読み込みに失敗しました", ex); }

            try
            {
                _schedule = ConfigStore.LoadSchedule() ?? new ScheduleConfig
                {
                    Frequency = "毎日",
                    Time = new TimeSpan(12, 0, 0),
                    RunWithHighestPrivileges = false,   // 新規は標準権限
                    AcPowerOnly = true,
                    NotifyMissedRuns = true,
                };
            }
            catch (Exception ex) { ShowError("スケジュールの読み込みに失敗しました", ex); }

            _history = Logger.LoadHistory();
            _taskStatus = SchedulerService.GetStatus();
        }

        private static void ShowError(string message, Exception ex) =>
            MessageBox.Show($"{message}:\n{ex.Message}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);

        // ================================================================
        // 画面の組み立て（テーマ切替時も呼ぶ）
        // ================================================================

        private void BuildUi()
        {
            SuspendLayout();
            foreach (Control c in Controls.Cast<Control>().ToList())
            {
                Controls.Remove(c);
                c.Dispose();
            }
            _views.Clear();

            BackColor = Theme.Side;
            ForeColor = Theme.Text;

            rail = new NavRail { Dock = DockStyle.Left, Width = S(76) };
            rail.Add("backup", Glyph.Home, "バックアップ");
            rail.Add("history", Glyph.History, "履歴");
            rail.Add("schedule", Glyph.Calendar, "スケジュール");
            rail.Add("ignore", Glyph.Filter, "除外設定");
            rail.Add("theme", Theme.IsDark ? Glyph.Sun : Glyph.Moon, Theme.IsDark ? "ライトモードにする" : "ダークモードにする", bottom: true);
            rail.Add("settings", Glyph.Settings, "設定", bottom: true);
            rail.ItemClicked += Rail_ItemClicked;

            side = new SideList { Dock = DockStyle.Left, Width = S(292) };
            side.EntryClicked += Side_EntryClicked;

            var surfaceHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Side, Padding = new Padding(0, S(12), S(12), S(12)) };
            surface = new RoundedPanel { Dock = DockStyle.Fill, FillColor = Theme.Surface, BorderColor = Theme.SurfaceBorder, Radius = 18, Padding = new Padding(S(2)) };
            surfaceHost.Controls.Add(surface);

            // ドッキングは後から追加したものが先に配置される
            Controls.Add(surfaceHost);
            Controls.Add(side);
            Controls.Add(rail);

            AddView("overview", BuildOverview());
            AddView("task", BuildTaskEditor());
            AddView("history", BuildHistory());
            AddView("schedule", BuildSchedule());
            AddView("ignore", BuildIgnore());
            AddView("settings:general", BuildSettingsGeneral());
            AddView("settings:protection", BuildSettingsProtection());
            AddView("settings:storage", BuildSettingsStorage());
            AddView("settings:transfer", BuildSettingsTransfer());
            AddView("settings:privilege", BuildSettingsPrivilege());
            AddView("settings:update", BuildSettingsUpdate());
            AddView("settings:about", BuildSettingsAbout());

            ResumeLayout(true);
            ApplyDwmDarkMode();
            _uiBuilt = true;

            UpdateTiles();
            UpdateUpdateUi();
            SetRunningUi(_backupRunning, _runningDryRun);
            Navigate(_page, _sideKey);
        }

        /// <summary>
        /// トレイに隠したときに画面の部品を捨て、メモリを OS に返す（設定や未保存の変更はデータとして残る）。
        /// バックアップ・アップデート中は進捗を表示するため捨てない。
        /// </summary>
        private void ReleaseUiIfIdle()
        {
            if (!_uiBuilt || _backupRunning || _updating || Visible || _screenshotDir != null) return;

            CaptureScheduleUi();
            SuspendLayout();
            foreach (Control c in Controls.Cast<Control>().ToList())
            {
                Controls.Remove(c);
                c.Dispose();
            }
            _views.Clear();
            _uiBuilt = false;
            ResumeLayout(false);

            MemoryTrimmer.Trim();
        }

        private void AddView(string key, Control view)
        {
            view.Dock = DockStyle.Fill;
            view.Visible = false;
            surface.Controls.Add(view);
            _views[key] = view;
        }

        private void ShowView(string key)
        {
            foreach (var (k, v) in _views)
                v.Visible = k == key;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyDwmDarkMode();
        }

        private void ApplyDwmDarkMode()
        {
            if (!IsHandleCreated) return;
            ApplyTitleBarColors(Handle);
        }

        // タイトルバーをアプリの配色に合わせる（Windows のアクセントカラーで青くならないように。Windows 11 のみ有効）
        private static void ApplyTitleBarColors(IntPtr handle)
        {
            try
            {
                int dark = Theme.IsDark ? 1 : 0;
                DwmSetWindowAttribute(handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

                int caption = ColorRef(Theme.Side);
                int text = ColorRef(Theme.Text);
                DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(handle, DWMWA_CAPTION_COLOR, ref caption, sizeof(int));
                DwmSetWindowAttribute(handle, DWMWA_TEXT_COLOR, ref text, sizeof(int));
            }
            catch { }
        }

        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWA_CAPTION_COLOR = 35;
        private const int DWMWA_TEXT_COLOR = 36;

        private static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

        private static void DarkScrollbars(Control c)
        {
            try { SetWindowTheme(c.Handle, Theme.IsDark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
        }

        // ---- 部品を作る補助 ----

        private StackPanel NewPage() => new()
        {
            AutoScroll = true,
            BackColor = Theme.Surface,
            Padding = new Padding(S(40), S(34), S(40), S(34)),
            Gap = 18,
        };

        private ModernButton Btn(string text, string glyph = "", ButtonKind kind = ButtonKind.Secondary, EventHandler? click = null)
        {
            var b = new ModernButton { Text = text, Glyph = glyph, Kind = kind, Height = S(38) };
            b.Width = b.PreferredWidth();
            if (click != null) b.Click += click;
            return b;
        }

        private ToggleSwitch Toggle(string text, string description = "")
        {
            var t = new ToggleSwitch { Text = text };
            if (!string.IsNullOrEmpty(description)) t.Description = description;
            else t.Height = S(30);
            return t;
        }

        private ThemedLabel Label(string text, ThemedLabel.Style style = ThemedLabel.Style.Body, int height = 22, int width = 0)
        {
            var l = new ThemedLabel(style) { Text = text, Height = S(height) };
            if (width > 0) l.Width = S(width);
            return l;
        }

        private ComboBox Combo(IEnumerable<string> items, int width)
        {
            var cb = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                FlatStyle = FlatStyle.Flat,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = S(26),
                Width = S(width),
                Font = Theme.Body,
                BackColor = Theme.Input,
                ForeColor = Theme.Text,
            };
            cb.Items.AddRange(items.Cast<object>().ToArray());
            cb.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (var brush = new SolidBrush(selected ? Theme.AccentSoft : Theme.Input))
                    e.Graphics.FillRectangle(brush, e.Bounds);
                var r = new Rectangle(e.Bounds.X + S(8), e.Bounds.Y, e.Bounds.Width - S(8), e.Bounds.Height);
                TextRenderer.DrawText(e.Graphics, cb.Items[e.Index]?.ToString(), Theme.Body, r, Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.SingleLine);
            };
            cb.HandleCreated += (s, e) => DarkScrollbars(cb);
            return cb;
        }

        /// <summary>ページ上部の見出し（タイトルと説明）</summary>
        private sealed class PageHeader : Panel
        {
            public ThemedLabel TitleLabel { get; }
            public ThemedLabel SubLabel { get; }

            public PageHeader(string title, string sub)
            {
                BackColor = Theme.Surface;
                TitleLabel = new ThemedLabel(ThemedLabel.Style.PageTitle) { Text = title };
                SubLabel = new ThemedLabel(ThemedLabel.Style.Muted) { Text = sub };
                Controls.Add(TitleLabel);
                Controls.Add(SubLabel);
                Height = 72;
            }

            protected override void OnLayout(LayoutEventArgs levent)
            {
                base.OnLayout(levent);
                float k = DeviceDpi / 96f;
                TitleLabel.SetBounds(0, 0, Width, (int)(36 * k));
                SubLabel.SetBounds(0, (int)(40 * k), Width, (int)(24 * k));
            }
        }

        // ================================================================
        // ナビゲーション
        // ================================================================

        private void Rail_ItemClicked(object? sender, string key)
        {
            if (key == "theme")
            {
                ToggleTheme();
                return;
            }

            string sideKey = key switch
            {
                "backup" => "overview",
                "history" => "all",
                "schedule" => _schedule.Frequency,
                "ignore" => "all",
                "settings" => "general",
                _ => "",
            };
            Navigate(key, sideKey);
        }

        private void Navigate(string page, string? sideKey)
        {
            _page = page;
            rail.Selected = page;

            switch (page)
            {
                case "backup":
                    if (sideKey != null && sideKey.StartsWith("task:") && int.TryParse(sideKey[5..], out int index) && index < _tasks.Count)
                    {
                        _sideKey = sideKey;
                        LoadTaskEditor(index);
                        ShowView("task");
                    }
                    else
                    {
                        _sideKey = "overview";
                        _taskIndex = -1;
                        ShowView("overview");
                    }
                    break;

                case "history":
                    _sideKey = sideKey ?? "all";
                    FillHistory();
                    ShowView("history");
                    break;

                case "schedule":
                    _sideKey = _schedule.Frequency;
                    LoadScheduleUi();
                    ShowView("schedule");
                    break;

                case "ignore":
                    _sideKey = sideKey ?? "all";
                    FillIgnoreList();
                    ShowView("ignore");
                    break;

                case "settings":
                    _sideKey = sideKey ?? "general";
                    if (!_views.ContainsKey("settings:" + _sideKey)) _sideKey = "general";
                    ShowView("settings:" + _sideKey);
                    break;
            }

            RefreshSide();
        }

        private void RefreshSide()
        {
            if (!_uiBuilt) return;
            switch (_page)
            {
                case "backup":
                {
                    var entries = new List<SideEntry>
                    {
                        SideEntry.Item("overview", Glyph.Home, "概要", _tasksDirty ? "未保存" : "", countColor: _tasksDirty ? Theme.Accent : null),
                        SideEntry.Separator(),
                        SideEntry.Header("設定（上から順に実行）"),
                    };
                    for (int i = 0; i < _tasks.Count; i++)
                    {
                        var (glyph, color) = TaskStatusGlyph(_tasks[i]);
                        entries.Add(new SideEntry
                        {
                            Key = $"task:{i}",
                            Glyph = glyph,
                            GlyphColor = color,
                            Text = ShortTaskLabel(_tasks[i]),
                            Count = $"{i + 1}",
                            Tooltip = TaskLabel(_tasks[i]),
                        });
                    }
                    entries.Add(SideEntry.Add("add-task", "設定を追加"));
                    side.SetEntries("バックアップ", entries, _sideKey);
                    break;
                }

                case "history":
                {
                    int auto = _history.Count(h => h.Trigger == "auto");
                    int manual = _history.Count(h => h.Trigger == "manual");
                    int ok = _history.Count(h => HistoryState(h) == "ok");
                    int warn = _history.Count(h => HistoryState(h) == "warn");
                    int cancel = _history.Count(h => h.Cancelled);
                    side.SetEntries("履歴", new[]
                    {
                        SideEntry.Item("all", Glyph.History, "すべて", $"{_history.Count}"),
                        SideEntry.Item("auto", Glyph.Lightning, "自動実行", $"{auto}"),
                        SideEntry.Item("manual", Glyph.Person, "手動実行", $"{manual}"),
                        SideEntry.Separator(),
                        SideEntry.Header("結果"),
                        SideEntry.Item("ok", Glyph.Completed, "完了", $"{ok}", Theme.Success),
                        SideEntry.Item("warn", Glyph.Warning, "要確認", $"{warn}", Theme.Warning, warn > 0 ? Theme.Warning : null),
                        SideEntry.Item("cancel", Glyph.Stop, "中止", $"{cancel}"),
                    }, _sideKey);
                    break;
                }

                case "schedule":
                {
                    var st = _taskStatus;
                    side.SetEntries("スケジュール", new[]
                    {
                        SideEntry.Header("実行頻度"),
                        SideEntry.Item("毎日", Glyph.Repeat, "毎日"),
                        SideEntry.Item("毎週", Glyph.Calendar, "毎週"),
                        SideEntry.Item("起動時", Glyph.Power, "PC の起動時"),
                        SideEntry.Separator(),
                        SideEntry.Header("登録状況"),
                        SideEntry.Info(Glyph.Clock, "次回", st?.NextRun?.ToString("MM/dd HH:mm") ?? "—"),
                        SideEntry.Info(Glyph.History, "前回", st?.LastRun?.ToString("MM/dd HH:mm") ?? "—"),
                        SideEntry.Info(Glyph.Shield, "権限", st is { Exists: true } ? st.RunLevel : "未登録"),
                    }, _schedule.Frequency);
                    break;
                }

                case "ignore":
                {
                    int Count(IgnoreKind k) => _ignore.Count(x => x.Kind == k);
                    side.SetEntries("除外設定", new[]
                    {
                        SideEntry.Item("all", Glyph.Filter, "すべて", $"{_ignore.Count}"),
                        SideEntry.Separator(),
                        SideEntry.Header("種類"),
                        SideEntry.Item("Path", Glyph.Folder, "パス", $"{Count(IgnoreKind.Path)}"),
                        SideEntry.Item("Extension", Glyph.Tag, "拡張子", $"{Count(IgnoreKind.Extension)}"),
                        SideEntry.Item("Pattern", Glyph.CheckList, "ワイルドカード", $"{Count(IgnoreKind.Pattern)}"),
                        SideEntry.Item("FolderName", Glyph.Document, "フォルダ名", $"{Count(IgnoreKind.FolderName)}"),
                        SideEntry.Add("add-ignore", "除外を追加"),
                    }, _sideKey);
                    break;
                }

                case "settings":
                    side.SetEntries("設定", new[]
                    {
                        SideEntry.Item("general", Glyph.Brush, "一般"),
                        SideEntry.Item("update", Glyph.Download, "アップデート", _update != null ? "新しい版" : "",
                            countColor: _update != null ? Theme.Accent : null),
                        SideEntry.Item("protection", Glyph.History, "保護とネットワーク", _options.KeepVersions ? "" : "オフ"),
                        SideEntry.Item("storage", Glyph.Storage, "保存先", AppPaths.IsMachineMode ? "共通" : ""),
                        SideEntry.Item("transfer", Glyph.Export, "インポート/エクスポート"),
                        SideEntry.Item("privilege", Glyph.Shield, "実行権限", IsElevated ? "管理者" : ""),
                        SideEntry.Item("about", Glyph.Info, "バージョン情報"),
                    }, _sideKey);
                    break;
            }
        }

        private void Side_EntryClicked(object? sender, string key)
        {
            switch (_page)
            {
                case "backup":
                    if (key == "add-task")
                    {
                        _tasks.Add(new BackupTask());
                        MarkTasksDirty();
                        Navigate("backup", $"task:{_tasks.Count - 1}");
                        inSrc.Box.Focus();
                    }
                    else
                    {
                        Navigate("backup", key);
                    }
                    break;

                case "history":
                    Navigate("history", key);
                    break;

                case "schedule":
                    if (key is "毎日" or "毎週" or "起動時" && _schedule.Frequency != key)
                    {
                        _schedule.Frequency = key;
                        MarkScheduleDirty();
                        LoadScheduleUi();
                        RefreshSide();
                    }
                    break;

                case "ignore":
                    if (key == "add-ignore")
                    {
                        side.SelectedKey = _sideKey;
                        inIgnore.Box.Focus();
                    }
                    else
                    {
                        Navigate("ignore", key);
                        if (Enum.TryParse<IgnoreKind>(key, out var kind))
                            cmbIgnoreKind.SelectedIndex = (int)kind;
                    }
                    break;

                case "settings":
                    Navigate("settings", key);
                    break;
            }
        }

        private void ToggleTheme()
        {
            if (_backupRunning)
            {
                MessageBox.Show("バックアップ実行中は表示を切り替えられません。", "確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            CaptureScheduleUi();
            Theme.Set(!Theme.IsDark);
            BuildUi();
        }

        // ================================================================
        // 概要
        // ================================================================

        private Control BuildOverview()
        {
            var page = NewPage();
            page.Controls.Add(new PageHeader("バックアップ", "設定を上から順に実行します。左の一覧から設定を選ぶと編集できます。"));

            // 未保存の変更
            bannerUnsaved = new Card { FillColor = Theme.AccentSoft, BorderColor = Theme.AccentSoft };
            var bannerRow = new RowPanel { Height = S(38), BackColor = Theme.AccentSoft };
            bannerRow.AddFill(Label("バックアップ設定に保存されていない変更があります。", height: 24));
            bannerRow.AddRight(Btn("変更を保存", Glyph.Save, ButtonKind.Primary, (s, e) => SaveTasks(showMessage: true)));
            bannerUnsaved.Body.BackColor = Theme.AccentSoft;
            bannerUnsaved.Add(bannerRow);
            bannerUnsaved.Visible = _tasksDirty;
            page.Controls.Add(bannerUnsaved);

            // アップデートのお知らせ
            bannerUpdate = new Card { FillColor = Theme.AccentSoft, BorderColor = Theme.AccentSoft };
            bannerUpdate.Body.BackColor = Theme.AccentSoft;
            var updateRow = new RowPanel { Height = S(38), BackColor = Theme.AccentSoft };
            lblBannerUpdate = Label("", height: 24);
            updateRow.AddFill(lblBannerUpdate);
            updateRow.AddRight(Btn("あとで", click: (s, e) =>
            {
                if (_update != null) _appSettings.SkippedVersion = _update.Version.ToString();
                TrySaveAppSettings();
                bannerUpdate.Visible = false;
            }));
            updateRow.AddRight(Btn("更新する", Glyph.Download, ButtonKind.Primary, async (s, e) => await InstallUpdateAsync()));
            bannerUpdate.Add(updateRow);
            bannerUpdate.Visible = false;
            page.Controls.Add(bannerUpdate);

            // 実行
            var run = new Card("実行", "「事前チェック」と「プレビュー」で、実行前に内容を確認できます");
            btnCheck = Btn("事前チェック", Glyph.CheckList, click: btnCheck_Click);
            btnPreview = Btn("プレビュー", Glyph.Preview, click: async (s, e) => await RunBackupAsync(dryRun: true));
            btnPause = Btn("一時停止", Glyph.Pause, click: btnPause_Click);
            btnCancel = Btn("中止", Glyph.Stop, ButtonKind.Danger, btnCancel_Click);
            btnStart = Btn("バックアップ開始", Glyph.Play, ButtonKind.Primary, async (s, e) => await RunBackupAsync(dryRun: false));
            btnStart.Width = Math.Max(btnStart.Width, S(170));
            var runRow = new RowPanel { Height = S(40), BackColor = Theme.Card };
            runRow.AddLeft(btnCheck).AddLeft(btnPreview).AddRight(btnPause).AddRight(btnCancel).AddRight(btnStart);

            runPanel = new StackPanel { AutoHeight = true, Gap = 6, BackColor = Theme.Card, Visible = false, Padding = new Padding(0, S(6), 0, 0) };
            progress = new ThinProgress { Height = S(6) };
            lblRunMain = Label("", height: 24);
            lblRunFile = Label("", ThemedLabel.Style.Small, 20);
            lblRunBytes = Label("", ThemedLabel.Style.Small, 20);
            runPanel.Controls.AddRange(new Control[] { progress, lblRunMain, lblRunFile, lblRunBytes });
            run.Add(runRow, runPanel);
            page.Controls.Add(run);

            // 数値のタイル
            var tiles = new ColumnsPanel { Height = S(100), BackColor = Theme.Surface };
            tileLast = new StatTile { Caption = "最後の実行", Glyph = Glyph.History };
            tileCopied = new StatTile { Caption = "コピーしたファイル", Glyph = Glyph.Sync };
            tileErrors = new StatTile { Caption = "エラー", Glyph = Glyph.Warning };
            tileNext = new StatTile { Caption = "次回の予定", Glyph = Glyph.Calendar };
            tiles.Controls.AddRange(new Control[] { tileLast, tileCopied, tileErrors, tileNext });
            page.Controls.Add(tiles);

            // オプション
            var opt = new Card("オプション", "手動実行とスケジュールによる自動実行の両方に適用されます（変更はすぐに保存されます）");
            tglMirror = Toggle("ミラーモード", "コピー元に無いファイルをコピー先から削除します。他の設定とフォルダが重なる場合は適用しません");
            tglHash = Toggle("ハッシュで内容を比較", "更新日時だけ新しく、内容が同じファイルはコピーしません（比較に時間がかかります）");
            cmbParallel = Combo(new[] { "自動" }.Concat(Enumerable.Range(1, 8).Select(i => i.ToString())), 110);
            var parallelRow = new RowPanel { Height = S(36), BackColor = Theme.Card };
            parallelRow.AddLeft(Label("並列コピー数", width: 120));
            parallelRow.AddLeft(cmbParallel);
            parallelRow.AddFill(Label("自動：コピー元・コピー先のドライブの種類から決めます（HDD は 1、SSD・NAS は 4）", ThemedLabel.Style.Small, 20));
            opt.Add(tglMirror, tglHash, parallelRow);
            page.Controls.Add(opt);

            _loading = true;
            tglMirror.Checked = _options.MirrorMode;
            tglHash.Checked = _options.CompareByHash;
            cmbParallel.SelectedIndex = Math.Clamp(_options.MaxDegreeOfParallelism, 0, 8);
            _loading = false;

            tglMirror.CheckedChanged += tglMirror_CheckedChanged;
            tglHash.CheckedChanged += (s, e) => { if (!_loading) { _options.CompareByHash = tglHash.Checked; SaveOptions(); } };
            cmbParallel.SelectedIndexChanged += (s, e) => { if (!_loading) { _options.MaxDegreeOfParallelism = cmbParallel.SelectedIndex; SaveOptions(); } };

            return page;
        }

        private void UpdateTiles()
        {
            if (!_uiBuilt) return;
            var last = _history.LastOrDefault();
            if (last == null)
            {
                tileLast.Value = "—";
                tileLast.Sub = "まだ実行していません";
                tileCopied.Value = "—";
                tileCopied.Sub = "";
                tileErrors.Value = "—";
                tileErrors.Sub = "";
                tileErrors.ValueColor = null;
            }
            else
            {
                tileLast.Value = last.Timestamp.ToString("MM/dd HH:mm");
                tileLast.Sub = $"{TriggerLabel(last.Trigger)} ・ {FormatDuration(last.Duration)}" + (last.Cancelled ? " ・ 中止" : "");

                // 件数とエラーは、最後まで終わった実行の結果を出す
                var done = _history.LastOrDefault(h => !h.Cancelled) ?? last;
                tileCopied.Value = $"{done.FilesCopied:N0} 件";
                tileCopied.Sub = $"{FormatBytes(done.BytesCopied)} ・ {done.Timestamp:MM/dd}";
                int attention = done.TaskResults.Count(t => t.Status is "容量不足" or "エラー" or "延期" || t.Status.StartsWith("スキップ"));
                tileErrors.Value = $"{done.Errors:N0} 件";
                tileErrors.ValueColor = done.Errors > 0 ? Theme.Danger : Theme.Success;
                tileErrors.Sub = attention > 0 ? $"要確認の設定 {attention} 件" : "問題はありません";
            }

            tileNext.Value = _taskStatus?.NextRun?.ToString("MM/dd HH:mm") ?? "—";
            tileNext.Sub = _taskStatus is { Exists: true } ? ScheduleSummary(_schedule) : "スケジュール未登録";
        }

        private void tglMirror_CheckedChanged(object? sender, EventArgs e)
        {
            if (_loading) return;

            if (tglMirror.Checked)
            {
                var conflicts = BackupManager.FindMirrorConflicts(_tasks, out var blocked);
                var sb = new StringBuilder();
                sb.AppendLine("ミラーモードでは、コピー元に無いファイルをコピー先から削除します。");
                sb.AppendLine("スケジュールによる自動実行にも適用されます。");
                sb.AppendLine();
                sb.AppendLine("・他の設定とフォルダが重なる設定には適用しません");
                sb.AppendLine("・除外設定に一致するファイルやリンクは削除しません");
                if (conflicts.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"現在の設定のうち {blocked.Count} 件には適用されません:");
                    foreach (var c in conflicts) sb.AppendLine("　" + c);
                }
                sb.AppendLine();
                sb.Append("有効にしますか？");

                if (MessageBox.Show(sb.ToString(), "ミラーモード", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
                    _loading = true;
                    tglMirror.Checked = false;
                    _loading = false;
                    return;
                }
            }

            _options.MirrorMode = tglMirror.Checked;
            SaveOptions();
        }

        private void SaveOptions()
        {
            try
            {
                ConfigStore.SaveOptions(_options);
            }
            catch (Exception ex)
            {
                ShowSaveError("オプションの保存に失敗しました", ex);
            }
        }

        private static void ShowSaveError(string message, Exception ex)
        {
            string hint = ex is UnauthorizedAccessException && AppPaths.IsMachineMode
                ? "\n\n管理者運用モードでは、設定の変更に管理者権限が必要です。「設定」→「実行権限」から管理者として再起動してください。"
                : "";
            MessageBox.Show($"{message}:\n{ex.Message}{hint}", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        // ================================================================
        // 設定の編集
        // ================================================================

        private Control BuildTaskEditor()
        {
            var page = NewPage();
            taskHeader = new PageHeader("", "");
            page.Controls.Add(taskHeader);

            var folders = new Card("フォルダ", "コピー元の中身を、コピー先に同じ構成でコピーします");
            inSrc = new ModernInput { Height = S(38), Placeholder = @"例: D:\Photos" };
            inDst = new ModernInput { Height = S(38), Placeholder = @"例: E:\Backup\Photos" };
            var rowSrc = new RowPanel { Height = S(40), BackColor = Theme.Card };
            rowSrc.AddFill(inSrc).AddRight(Btn("フォルダを選択", Glyph.Folder, click: (s, e) => PickFolder(inSrc)));
            var rowDst = new RowPanel { Height = S(40), BackColor = Theme.Card };
            rowDst.AddFill(inDst).AddRight(Btn("フォルダを選択", Glyph.Folder, click: (s, e) => PickFolder(inDst)));
            lblTaskWarn = Label("", height: 22);
            lblTaskInfo = Label("", ThemedLabel.Style.Small, 20);
            folders.Add(Label("コピー元", ThemedLabel.Style.Small, 20), rowSrc, Label("コピー先", ThemedLabel.Style.Small, 20), rowDst, lblTaskWarn, lblTaskInfo);
            page.Controls.Add(folders);

            var last = new Card("前回の結果");
            lblTaskLast = Label("", height: 72);
            last.Add(lblTaskLast);
            page.Controls.Add(last);

            btnTaskUp = Btn("上へ", Glyph.Up, click: (s, e) => MoveTask(-1));
            btnTaskDown = Btn("下へ", Glyph.Down, click: (s, e) => MoveTask(+1));
            btnTaskSave = Btn("変更を保存", Glyph.Save, ButtonKind.Primary, (s, e) => SaveTasks(showMessage: true));
            var actions = new RowPanel { Height = S(40), BackColor = Theme.Surface };
            actions.AddLeft(btnTaskUp).AddLeft(btnTaskDown).AddLeft(Btn("この設定を削除", Glyph.Delete, ButtonKind.Danger, (s, e) => DeleteTask()))
                   .AddRight(btnTaskSave);
            page.Controls.Add(actions);

            inSrc.ValueChanged += (s, e) => OnTaskEdited(src: true);
            inDst.ValueChanged += (s, e) => OnTaskEdited(src: false);
            inSrc.Box.Leave += (s, e) => UpdateTaskInfo();
            inDst.Box.Leave += (s, e) => UpdateTaskInfo();

            return page;
        }

        private void LoadTaskEditor(int index)
        {
            _taskIndex = index;
            var task = _tasks[index];
            _loading = true;
            inSrc.Value = task.Source;
            inDst.Value = task.Destination;
            _loading = false;

            UpdateTaskHeader();
            UpdateTaskWarning();
            UpdateTaskInfo();

            btnTaskUp.Enabled = index > 0;
            btnTaskDown.Enabled = index < _tasks.Count - 1;
            btnTaskSave.Highlighted = _tasksDirty;

            var result = LastResultFor(task);
            if (result == null)
            {
                lblTaskLast.Text = "まだ実行されていません。";
            }
            else
            {
                var (h, t) = result.Value;
                var sb = new StringBuilder();
                sb.AppendLine($"{h.Timestamp:yyyy/MM/dd HH:mm}（{TriggerLabel(h.Trigger)}）　{(string.IsNullOrEmpty(t.Status) ? "完了" : t.Status)}");
                sb.AppendLine($"コピー {t.FilesCopied:N0} 件（{FormatBytes(t.BytesCopied)}） ・ スキップ {t.FilesSkipped:N0} 件 ・ 削除 {t.FilesDeleted:N0} 件 ・ エラー {t.Errors:N0} 件");
                if (!string.IsNullOrEmpty(t.Message)) sb.AppendLine(t.Message);
                lblTaskLast.Text = sb.ToString().TrimEnd();
            }
        }

        private void UpdateTaskHeader()
        {
            if (_taskIndex < 0 || _taskIndex >= _tasks.Count) return;
            var task = _tasks[_taskIndex];
            taskHeader.TitleLabel.Text = string.IsNullOrWhiteSpace(task.Source) && string.IsNullOrWhiteSpace(task.Destination)
                ? "新しい設定"
                : TaskLabel(task);
            taskHeader.SubLabel.Text = $"設定 {_taskIndex + 1} / {_tasks.Count}（上から順に実行します）";
        }

        private void OnTaskEdited(bool src)
        {
            if (_loading || _taskIndex < 0 || _taskIndex >= _tasks.Count) return;
            var task = _tasks[_taskIndex];
            if (src) task.Source = inSrc.Value.Trim();
            else task.Destination = inDst.Value.Trim();
            MarkTasksDirty();
            UpdateTaskHeader();
            UpdateTaskWarning();
            RefreshSide();
        }

        private void UpdateTaskWarning()
        {
            if (_taskIndex < 0 || _taskIndex >= _tasks.Count) return;
            var task = _tasks[_taskIndex];
            string message = "";
            Color? color = null;

            var errors = BackupManager.FindConfigErrors(new[] { task });
            if (errors.Count > 0)
            {
                message = "⚠ " + errors[0];
                color = Theme.Danger;
            }
            else if (!string.IsNullOrWhiteSpace(task.Source) && !SafeDirectoryExists(task.Source))
            {
                message = "⚠ コピー元が見つかりません（実行時はスキップします）";
                color = Theme.Warning;
            }

            lblTaskWarn.OverrideColor = color;
            lblTaskWarn.ApplyTheme();
            lblTaskWarn.Text = message;
            lblTaskWarn.Visible = message.Length > 0;
        }

        private void UpdateTaskInfo()
        {
            if (_taskIndex < 0 || _taskIndex >= _tasks.Count) return;
            var task = _tasks[_taskIndex];
            if (string.IsNullOrWhiteSpace(task.Source) || string.IsNullOrWhiteSpace(task.Destination))
            {
                lblTaskInfo.Text = "";
                lblTaskInfo.Visible = false;
                return;
            }

            int dop = DriveProbe.SuggestParallelism(task.Source, task.Destination, out string why);
            lblTaskInfo.Text = $"ドライブの種類: {why}（並列コピー数が「自動」のときは {dop}）";
            lblTaskInfo.Visible = true;
        }

        private static bool SafeDirectoryExists(string path)
        {
            try { return Directory.Exists(path); } catch { return false; }
        }

        private void PickFolder(ModernInput input)
        {
            using var dialog = new FolderBrowserDialog();
            if (!string.IsNullOrWhiteSpace(input.Value) && SafeDirectoryExists(input.Value))
                dialog.InitialDirectory = input.Value;
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                input.Value = dialog.SelectedPath;
                UpdateTaskInfo();
            }
        }

        private void MoveTask(int delta)
        {
            int to = _taskIndex + delta;
            if (_taskIndex < 0 || to < 0 || to >= _tasks.Count) return;
            (_tasks[_taskIndex], _tasks[to]) = (_tasks[to], _tasks[_taskIndex]);
            MarkTasksDirty();
            Navigate("backup", $"task:{to}");
        }

        private void DeleteTask()
        {
            if (_taskIndex < 0 || _taskIndex >= _tasks.Count) return;
            var task = _tasks[_taskIndex];
            bool blank = string.IsNullOrWhiteSpace(task.Source) && string.IsNullOrWhiteSpace(task.Destination);
            if (!blank && MessageBox.Show($"この設定を削除しますか？\n{TaskLabel(task)}\n\n（コピー済みのファイルは削除しません）", "確認",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            _tasks.RemoveAt(_taskIndex);
            MarkTasksDirty();
            Navigate("backup", _tasks.Count == 0 ? "overview" : $"task:{Math.Min(_taskIndex, _tasks.Count - 1)}");
        }

        private void MarkTasksDirty()
        {
            _tasksDirty = true;
            bannerUnsaved.Visible = true;
            btnTaskSave.Highlighted = true;
        }

        /// <summary>バックアップ設定を保存する。成功したら true</summary>
        private bool SaveTasks(bool showMessage)
        {
            // 空の設定・重複・コピー元とコピー先が同じものは保存しない
            var cleaned = new List<BackupTask>();
            foreach (var t in _tasks)
            {
                if (string.IsNullOrWhiteSpace(t.Source) || string.IsNullOrWhiteSpace(t.Destination)) continue;
                if (string.Equals(t.Source, t.Destination, StringComparison.OrdinalIgnoreCase)) continue;
                if (cleaned.Any(c => c.Source.Equals(t.Source, StringComparison.OrdinalIgnoreCase) &&
                                     c.Destination.Equals(t.Destination, StringComparison.OrdinalIgnoreCase))) continue;
                cleaned.Add(new BackupTask { Source = t.Source, Destination = t.Destination });
            }

            var errors = BackupManager.FindConfigErrors(cleaned);
            if (errors.Count > 0)
            {
                MessageBox.Show("次の設定は危険なため保存できません。修正してください。\n\n" + string.Join("\n", errors),
                    "保存できません", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            try
            {
                ConfigStore.SaveTasks(cleaned);
            }
            catch (Exception ex)
            {
                ShowSaveError("設定の保存に失敗しました", ex);
                return false;
            }

            bool removed = cleaned.Count != _tasks.Count;
            _tasks = cleaned;
            _tasksDirty = false;
            bannerUnsaved.Visible = false;
            btnTaskSave.Highlighted = false;

            if (_page == "backup")
            {
                if (_taskIndex >= _tasks.Count || removed && _sideKey.StartsWith("task:"))
                    Navigate("backup", "overview");
                else
                    Navigate("backup", _sideKey);
            }

            if (showMessage)
            {
                var conflicts = _options.MirrorMode ? BackupManager.FindMirrorConflicts(_tasks, out _) : new List<string>();
                string text = "設定を保存しました。";
                if (removed) text += "\n（空の設定・重複した設定は保存しませんでした）";
                if (conflicts.Count > 0)
                    text += "\n\n次の設定にはミラーモード（削除）を適用しません:\n" + string.Join("\n", conflicts);
                MessageBox.Show(text, "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            return true;
        }

        // ================================================================
        // 実行
        // ================================================================

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
                ShowError("事前チェックに失敗しました", ex);
            }
            finally
            {
                btnCheck.Enabled = !_backupRunning;
            }
        }

        private void btnPause_Click(object? sender, EventArgs e)
        {
            if (_currentManager == null) return;
            if (_currentManager.IsPaused)
            {
                _currentManager.Resume();
                btnPause.Text = "一時停止";
                btnPause.Glyph = Glyph.Pause;
                _etaWatch.Start();
            }
            else
            {
                _currentManager.Pause();
                btnPause.Text = "再開";
                btnPause.Glyph = Glyph.Play;
                _etaWatch.Stop();
            }
            btnPause.Width = btnPause.PreferredWidth();
            btnPause.Parent?.PerformLayout();
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
                lblRunMain.Text = "中止しています...";
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
            if (!_tasksDirty) return true;

            var answer = MessageBox.Show(
                "バックアップ設定の変更が保存されていません。保存してから続けますか？\n（「いいえ」は保存済みの設定で続けます）",
                "確認", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);

            if (answer == DialogResult.Cancel) return false;
            if (answer == DialogResult.Yes) return SaveTasks(showMessage: false);
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
                ShowError("設定の読み込みに失敗しました", ex);
                return;
            }

            _runLock = runLock;
            _backupRunning = true;
            _runningDryRun = dryRun;
            _cts = new CancellationTokenSource();
            _currentManager = manager;
            _etaTask = "";
            SetRunningUi(true, dryRun);

            var progressReporter = new Progress<BackupProgress>(UpdateProgressUi);
            var token = _cts.Token;
            BackupResult? result = null;

            try
            {
                result = await Task.Run(() => manager.ExecuteAllWithResult(token, progressReporter, dryRun, "manual"));
            }
            catch (Exception ex)
            {
                ShowError("バックアップ中にエラーが発生しました", ex);
            }
            finally
            {
                _backupRunning = false;
                _currentManager = null;
                _cts.Dispose();
                _cts = null;
                _runLock?.Dispose();
                _runLock = null;
                _etaWatch.Reset();
                SetRunningUi(false, dryRun);
            }

            if (result != null)
            {
                if (!dryRun)
                {
                    _history = Logger.LoadHistory();
                    UpdateTiles();
                    RefreshSide();
                }
                ShowResult(result, dryRun);
            }

            // バックアップで使ったメモリを返す（ウィンドウを閉じていれば画面も捨てる）
            if (Visible) MemoryTrimmer.Trim(workingSet: false);
            else ReleaseUiIfIdle();
        }

        private void SetRunningUi(bool running, bool dryRun)
        {
            if (btnStart == null || btnStart.IsDisposed) return;

            btnStart.Enabled = !running;
            btnPreview.Enabled = !running;
            btnCheck.Enabled = !running;
            btnPause.Enabled = running;
            btnCancel.Enabled = running;
            btnPause.Text = "一時停止";
            btnPause.Glyph = Glyph.Pause;
            btnPause.Width = btnPause.PreferredWidth();
            trayCancelItem.Enabled = running;

            btnStart.Text = running && !dryRun ? "実行中..." : "バックアップ開始";
            btnPreview.Text = running && dryRun ? "プレビュー中..." : "プレビュー";
            btnPreview.Width = btnPreview.PreferredWidth();
            btnStart.Parent?.PerformLayout();

            runPanel.Visible = running;
            if (running)
            {
                progress.Indeterminate = true;
                lblRunMain.Text = "準備中...";
                lblRunFile.Text = "";
                lblRunBytes.Text = "";
            }
            else
            {
                progress.Indeterminate = false;
            }
        }

        private void UpdateProgressUi(BackupProgress p)
        {
            if (!_backupRunning || lblRunMain.IsDisposed) return;

            if (p.TaskLabel != _etaTask)
            {
                _etaTask = p.TaskLabel;
                _etaWatch.Restart();
            }

            string phase = p.Phase switch
            {
                BackupPhase.Scanning => "確認中",
                BackupPhase.Copying => "コピー中",
                BackupPhase.Mirroring => "ミラー削除中",
                _ => ""
            };

            string eta = "";
            if (p.EstimatedTotal > 0)
            {
                float fraction = Math.Min(0.99f, (float)p.Scanned / p.EstimatedTotal);
                progress.Indeterminate = false;
                progress.Value = fraction;

                var elapsed = _etaWatch.Elapsed;
                if (fraction > 0.02f && elapsed.TotalSeconds > 5)
                {
                    var remaining = TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - fraction) / fraction);
                    eta = $"　この設定の残り {FormatEta(remaining)}";
                }
            }
            else
            {
                progress.Indeterminate = true;
            }

            string total = p.EstimatedTotal > 0 ? $" / 約{p.EstimatedTotal:N0}" : "";
            lblRunMain.Text = $"{phase}: {p.TaskLabel}　確認済み {p.Scanned:N0}{total} 件{eta}";
            lblRunFile.Text = string.IsNullOrEmpty(p.Current) ? "" : p.Current;
            string copied = _runningDryRun ? "コピー予定" : "コピー済み";
            string planned = !_runningDryRun && p.BytesPlanned > p.BytesCopied ? $" / 予定 {FormatBytes(p.BytesPlanned)}" : "";
            lblRunBytes.Text = $"{copied}: {p.Copied:N0} 件（{FormatBytes(p.BytesCopied)}{planned}）" +
                               (p.Deleted > 0 ? $"　{(_runningDryRun ? "削除予定" : "削除")}: {p.Deleted:N0} 件" : "");
        }

        private static string FormatEta(TimeSpan t)
        {
            if (t.TotalMinutes < 1) return "1分未満";
            if (t.TotalHours < 1) return $"約{(int)Math.Ceiling(t.TotalMinutes)}分";
            return $"約{(int)t.TotalHours}時間{t.Minutes}分";
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
                foreach (var root in result.TaskResults.Select(t => SafeRoot(t.Destination)).Where(r => r != null).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var drive = new DriveInfo(root!);
                        if (drive.IsReady) sb.AppendLine($"空き容量 {root}: {FormatBytes(drive.AvailableFreeSpace)}");
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
                trayIcon.ShowBalloonTip(5000, "バックアップ完了", $"コピー: {result.FilesCopied:N0}件 / スキップ: {result.FilesSkipped:N0}件", ToolTipIcon.Info);

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

        // ================================================================
        // 履歴
        // ================================================================

        private Control BuildHistory()
        {
            var view = new Panel { BackColor = Theme.Surface, Padding = new Padding(S(40), S(34), S(40), S(34)) };

            historyHeader = new PageHeader("履歴", "行をダブルクリックすると、設定ごとの結果とエラー内容を表示します") { Dock = DockStyle.Top, Height = S(72) };

            var buttons = new RowPanel { Dock = DockStyle.Top, Height = S(44), BackColor = Theme.Surface };
            buttons.AddLeft(Btn("ログを開く", Glyph.Document, click: (s, e) => OpenLatestLog()));
            buttons.AddLeft(Btn("ログフォルダを開く", Glyph.Folder, click: (s, e) => OpenFolder(AppPaths.LogDir)));
            buttons.AddRight(Btn("履歴をクリア", Glyph.Delete, ButtonKind.Danger, (s, e) => ClearHistory()));

            var spacer = new Panel { Dock = DockStyle.Top, Height = S(14), BackColor = Theme.Surface };

            var listHost = new RoundedPanel { Dock = DockStyle.Fill, FillColor = Theme.Card, BorderColor = Theme.CardBorder, Radius = 14, Padding = new Padding(S(10)) };
            lvHistory = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                BorderStyle = BorderStyle.None,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BackColor = Theme.Card,
                ForeColor = Theme.Text,
                Font = Theme.Body,
                OwnerDraw = true,
                SmallImageList = new ImageList { ImageSize = new Size(1, S(30)) },
            };
            lvHistory.Columns.Add("日時", S(128));
            lvHistory.Columns.Add("種別", S(46));
            lvHistory.Columns.Add("所要時間", S(80));
            lvHistory.Columns.Add("コピー", S(64), HorizontalAlignment.Right);
            lvHistory.Columns.Add("スキップ", S(84), HorizontalAlignment.Right);
            lvHistory.Columns.Add("削除", S(46), HorizontalAlignment.Right);
            lvHistory.Columns.Add("エラー", S(52), HorizontalAlignment.Right);
            lvHistory.Columns.Add("コピー量", S(84), HorizontalAlignment.Right);
            lvHistory.Columns.Add("状態", S(110));
            lvHistory.DrawColumnHeader += ListView_DrawColumnHeader;
            lvHistory.DrawItem += (s, e) => e.DrawDefault = true;
            lvHistory.DrawSubItem += (s, e) => e.DrawDefault = true;
            lvHistory.Resize += (s, e) => { if (lvHistory.Columns.Count > 0) lvHistory.Columns[^1].Width = -2; };
            lvHistory.DoubleClick += lvHistory_DoubleClick;
            lvHistory.HandleCreated += (s, e) => DarkScrollbars(lvHistory);
            listHost.Controls.Add(lvHistory);

            view.Controls.Add(listHost);
            view.Controls.Add(spacer);
            view.Controls.Add(buttons);
            view.Controls.Add(historyHeader);
            return view;
        }

        private void ListView_DrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
        {
            using (var brush = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(brush, e.Bounds);
            using (var pen = new Pen(Theme.Separator))
                e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);

            var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine |
                        (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
            TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", Theme.Small, Rectangle.Inflate(e.Bounds, -S(6), 0), Theme.TextMuted, flags);
        }

        private static string HistoryState(BackupResult h)
        {
            if (h.Cancelled) return "cancel";
            bool attention = h.Errors > 0 || h.TaskResults.Any(t => t.Status is "容量不足" or "エラー" or "延期" || t.Status.StartsWith("スキップ"));
            return attention ? "warn" : "ok";
        }

        private void FillHistory()
        {
            IEnumerable<BackupResult> items = _history.AsEnumerable().Reverse();
            items = _sideKey switch
            {
                "auto" => items.Where(h => h.Trigger == "auto"),
                "manual" => items.Where(h => h.Trigger == "manual"),
                "ok" => items.Where(h => HistoryState(h) == "ok"),
                "warn" => items.Where(h => HistoryState(h) == "warn"),
                "cancel" => items.Where(h => h.Cancelled),
                _ => items,
            };

            historyHeader.TitleLabel.Text = _sideKey switch
            {
                "auto" => "履歴：自動実行",
                "manual" => "履歴：手動実行",
                "ok" => "履歴：完了",
                "warn" => "履歴：要確認",
                "cancel" => "履歴：中止",
                _ => "履歴",
            };

            lvHistory.BeginUpdate();
            try
            {
                lvHistory.Items.Clear();
                foreach (var h in items)
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
                    if (HistoryState(h) != "ok") item.ForeColor = h.Cancelled ? Theme.TextMuted : Theme.Danger;
                    lvHistory.Items.Add(item);
                }
            }
            finally
            {
                lvHistory.EndUpdate();
            }
        }

        private static string SummaryStatus(BackupResult h)
        {
            if (h.Cancelled) return "中止";
            int capacity = h.TaskResults.Count(t => t.Status == "容量不足");
            int skipped = h.TaskResults.Count(t => t.Status.StartsWith("スキップ"));
            if (capacity > 0) return $"容量不足（{capacity}件）";
            if (skipped > 0) return $"スキップあり（{skipped}件）";
            if (h.Errors > 0) return "エラーあり";
            return "完了";
        }

        private void lvHistory_DoubleClick(object? sender, EventArgs e)
        {
            if (lvHistory.SelectedItems.Count == 0 || lvHistory.SelectedItems[0].Tag is not BackupResult h) return;

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
                Size = new Size(S(900), S(580)),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = Theme.Surface,
                Font = Theme.Body,
                MinimizeBox = false,
                ShowInTaskbar = false,
                Icon = _appIcon,
                Padding = new Padding(S(16)),
            };
            var box = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
                BackColor = Theme.Surface,
                ForeColor = Theme.Text,
                BorderStyle = BorderStyle.None,
            };
            dialog.Controls.Add(box);
            dialog.HandleCreated += (s, e) => ApplyTitleBarColors(dialog.Handle);
            box.HandleCreated += (s, e) => DarkScrollbars(box);
            dialog.Shown += (s, e) => { box.SelectionStart = 0; box.SelectionLength = 0; };
            dialog.ShowDialog(this);
        }

        private void ClearHistory()
        {
            if (MessageBox.Show("履歴を全て削除しますか？", "確認", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            Logger.SaveHistory(new List<BackupResult>());
            _history = Logger.LoadHistory();
            FillHistory();
            UpdateTiles();
            RefreshSide();
        }

        private static void OpenLatestLog()
        {
            string logPath = Logger.GetLogPath();
            if (!File.Exists(logPath) && Directory.Exists(AppPaths.LogDir))
                logPath = new DirectoryInfo(AppPaths.LogDir).GetFiles("*.log").OrderByDescending(f => f.LastWriteTime).FirstOrDefault()?.FullName ?? "";

            if (File.Exists(logPath))
                Process.Start("notepad.exe", logPath);
            else
                MessageBox.Show("ログファイルがありません。", "通知", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        // ================================================================
        // スケジュール
        // ================================================================

        private Control BuildSchedule()
        {
            var page = NewPage();
            scheduleHeader = new PageHeader("スケジュール", "Windows のタスク スケジューラに登録して、自動でバックアップします");
            page.Controls.Add(scheduleHeader);

            var timing = new Card("実行のタイミング", "実行頻度は左の一覧で選びます");
            lblFrequency = Label("", ThemedLabel.Style.Body, 24);
            cmbHour = Combo(Enumerable.Range(0, 24).Select(h => h.ToString("00")), 80);
            cmbMinute = Combo(Enumerable.Range(0, 60).Select(m => m.ToString("00")), 80);
            rowTime = new RowPanel { Height = S(38), BackColor = Theme.Card };
            var colon = Label(":", width: 14);
            colon.AutoEllipsis = false;
            colon.TextAlign = ContentAlignment.MiddleCenter;
            rowTime.AddLeft(Label("時刻", width: 60)).AddLeft(cmbHour).AddLeft(colon).AddLeft(cmbMinute);
            dayChips = new DayChips { Height = S(40), Width = S(7 * 48) };
            rowDays = new RowPanel { Height = S(44), BackColor = Theme.Card };
            rowDays.AddLeft(Label("曜日", width: 60)).AddLeft(dayChips);
            timing.Add(lblFrequency, rowTime, rowDays);
            page.Controls.Add(timing);

            var conditions = new Card("実行の条件");
            tglHighest = Toggle("最高特権で実行する", "管理者権限が必要なフォルダがある場合だけオンにします（登録に管理者権限が必要）");
            tglAcOnly = Toggle("AC電源に接続している時だけ実行する");
            tglWake = Toggle("スリープ中なら、スリープを解除して実行する");
            tglNotify = Toggle("予定の実行が行われていなければ知らせる", "PC の電源が切れていた場合などに、アプリを開いたときに今すぐ実行するか確認します");
            conditions.Add(tglHighest, tglAcOnly, tglWake, tglNotify);
            page.Controls.Add(conditions);

            btnSaveSchedule = Btn("スケジュールを保存", Glyph.Save, ButtonKind.Primary, (s, e) => SaveSchedule());
            var actions = new RowPanel { Height = S(40), BackColor = Theme.Surface };
            actions.AddRight(btnSaveSchedule);
            page.Controls.Add(actions);

            var status = new Card("登録状況");
            lblScheduleStatus = Label("", ThemedLabel.Style.Body, 74);
            status.Add(lblScheduleStatus);
            page.Controls.Add(status);

            EventHandler dirty = (s, e) => { if (!_loading) { CaptureScheduleUi(); MarkScheduleDirty(); } };
            cmbHour.SelectedIndexChanged += dirty;
            cmbMinute.SelectedIndexChanged += dirty;
            dayChips.SelectionChanged += dirty;
            tglHighest.CheckedChanged += dirty;
            tglAcOnly.CheckedChanged += dirty;
            tglWake.CheckedChanged += dirty;
            tglNotify.CheckedChanged += dirty;

            return page;
        }

        private void LoadScheduleUi()
        {
            _loading = true;
            try
            {
                lblFrequency.Text = _schedule.Frequency switch
                {
                    "毎週" => "毎週、選んだ曜日に実行します",
                    "起動時" => "PC の起動時に実行します",
                    _ => "毎日、決まった時刻に実行します",
                };
                cmbHour.SelectedIndex = Math.Clamp(_schedule.Time.Hours, 0, 23);
                cmbMinute.SelectedIndex = Math.Clamp(_schedule.Time.Minutes, 0, 59);
                rowTime.Enabled = _schedule.Frequency != "起動時";
                rowDays.Visible = _schedule.Frequency == "毎週";
                dayChips.SetSelected(_schedule.DaysOfWeek ?? new List<DayOfWeek>());
                tglHighest.Checked = _schedule.EffectiveHighest;
                tglAcOnly.Checked = _schedule.AcPowerOnly;
                tglWake.Checked = _schedule.WakeToRun;
                tglNotify.Checked = _schedule.NotifyMissedRuns;
                btnSaveSchedule.Highlighted = _scheduleDirty;
                UpdateScheduleStatus();
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>画面の内容を _schedule に書き戻す（テーマ切替で画面を作り直す前にも呼ぶ）</summary>
        private void CaptureScheduleUi()
        {
            if (cmbHour == null || cmbHour.IsDisposed || cmbHour.SelectedIndex < 0) return;
            _schedule.Time = new TimeSpan(cmbHour.SelectedIndex, Math.Max(0, cmbMinute.SelectedIndex), 0);
            _schedule.DaysOfWeek = _schedule.Frequency == "毎週" ? dayChips.Selected.OrderBy(d => d).ToList() : null;
            _schedule.RunWithHighestPrivileges = tglHighest.Checked;
            _schedule.AcPowerOnly = tglAcOnly.Checked;
            _schedule.WakeToRun = tglWake.Checked;
            _schedule.NotifyMissedRuns = tglNotify.Checked;
        }

        private void MarkScheduleDirty()
        {
            _scheduleDirty = true;
            if (btnSaveSchedule != null && !btnSaveSchedule.IsDisposed) btnSaveSchedule.Highlighted = true;
        }

        private void SaveSchedule()
        {
            CaptureScheduleUi();
            if (_schedule.Frequency == "毎週" && (_schedule.DaysOfWeek == null || _schedule.DaysOfWeek.Count == 0))
            {
                MessageBox.Show("曜日を1つ以上選んでください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            RegisterSchedule(_schedule, showMessage: true);
        }

        private bool RegisterSchedule(ScheduleConfig config, bool showMessage)
        {
            try
            {
                SchedulerService.Register(config, Application.ExecutablePath);
                ConfigStore.SaveSchedule(config);
                _scheduleDirty = false;
                if (showMessage)
                    MessageBox.Show("スケジュールを保存し、タスクに登録しました。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return true;
            }
            catch (UnauthorizedAccessException) when (!IsElevated)
            {
                // 最高特権での登録や、管理者が登録した既存タスクの置き換えには管理者権限が必要
                try { ConfigStore.SaveSchedule(config); _scheduleDirty = false; } catch { }

                var answer = MessageBox.Show(
                    "タスク スケジューラへの登録に管理者権限が必要です。\n" +
                    "（「最高特権で実行する」がオンの場合や、以前のバージョンが管理者として登録したタスクを置き換える場合）\n\n" +
                    "設定は保存しました。管理者としてアプリを再起動しますか？\n再起動後に「スケジュールを保存」をもう一度押してください。",
                    "管理者権限が必要です", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (answer == DialogResult.Yes)
                    RestartApp(elevated: true);
                return false;
            }
            catch (Exception ex)
            {
                ShowSaveError("スケジュールの保存に失敗しました", ex);
                return false;
            }
            finally
            {
                _taskStatus = SchedulerService.GetStatus();
                if (btnSaveSchedule != null && !btnSaveSchedule.IsDisposed) btnSaveSchedule.Highlighted = _scheduleDirty;
                UpdateScheduleStatus();
                UpdateTiles();
                if (_page == "schedule") RefreshSide();
            }
        }

        private void UpdateScheduleStatus()
        {
            if (lblScheduleStatus == null || lblScheduleStatus.IsDisposed) return;
            var sb = new StringBuilder();
            var status = _taskStatus ?? SchedulerService.GetStatus();

            if (!string.IsNullOrEmpty(status.Error))
                sb.AppendLine($"タスクの状態を取得できませんでした（{status.Error}）");
            else if (!status.Exists)
                sb.AppendLine("タスク スケジューラに未登録です。「スケジュールを保存」で登録されます。");
            else
            {
                sb.AppendLine($"登録済み（{status.RunLevel}）　次回: {FormatDate(status.NextRun)}　前回: {FormatDate(status.LastRun)}");
                if (!string.IsNullOrEmpty(status.ExecutablePath) &&
                    !string.Equals(status.ExecutablePath, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                    sb.AppendLine($"※ 登録されている実行ファイルが、いま開いているものと異なります: {status.ExecutablePath}");
            }

            var last = _history.LastOrDefault();
            if (last != null)
                sb.AppendLine($"最後のバックアップ: {last.Timestamp:yyyy/MM/dd HH:mm}（{TriggerLabel(last.Trigger)}、コピー {last.FilesCopied:N0} 件、エラー {last.Errors:N0} 件）");

            lblScheduleStatus.Text = sb.ToString().TrimEnd();
        }

        private static string ScheduleSummary(ScheduleConfig s)
        {
            string time = $"{s.Time.Hours:00}:{s.Time.Minutes:00}";
            return s.Frequency switch
            {
                "毎週" => $"毎週 {string.Join("・", (s.DaysOfWeek ?? new()).OrderBy(d => d).Select(d => "日月火水木金土"[(int)d]))} {time}",
                "起動時" => "PC の起動時",
                _ => $"毎日 {time}",
            };
        }

        private static string FormatDate(DateTime? value) => value?.ToString("yyyy/MM/dd HH:mm") ?? "—";

        // ================================================================
        // 除外設定
        // ================================================================

        private Control BuildIgnore()
        {
            var page = NewPage();
            page.Controls.Add(new PageHeader("除外設定", "コピーしないファイルやフォルダを指定します"));

            var add = new Card("除外を追加");
            cmbIgnoreKind = Combo(IgnoreKindLabels, 150);
            inIgnore = new ModernInput { Height = S(38) };
            var row = new RowPanel { Height = S(40), BackColor = Theme.Card };
            row.AddLeft(cmbIgnoreKind).AddFill(inIgnore).AddRight(Btn("追加", Glyph.Add, ButtonKind.Primary, (s, e) => AddIgnoreFromInput()));
            var row2 = new RowPanel { Height = S(40), BackColor = Theme.Card };
            row2.AddLeft(Btn("ファイルを選択", Glyph.OpenFile, click: (s, e) => PickIgnoreFile()));
            row2.AddLeft(Btn("フォルダを選択", Glyph.Folder, click: (s, e) => PickIgnoreFolder()));
            lblIgnoreHint = Label("", ThemedLabel.Style.Small, 20);
            add.Add(row, row2, lblIgnoreHint);
            page.Controls.Add(add);

            cardIgnoreList = new Card("すべて");
            lstIgnore = new ListBox
            {
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = S(38),
                Height = S(38 * 7 + 4),
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Card,
                ForeColor = Theme.Text,
                IntegralHeight = false,
                SelectionMode = SelectionMode.MultiExtended,
            };
            lstIgnore.DrawItem += lstIgnore_DrawItem;
            lstIgnore.KeyDown += (s, e) => { if (e.KeyCode == Keys.Delete) RemoveSelectedIgnore(); };
            lstIgnore.HandleCreated += (s, e) => DarkScrollbars(lstIgnore);
            var listActions = new RowPanel { Height = S(40), BackColor = Theme.Card };
            listActions.AddLeft(Label("Delete キーでも削除できます", ThemedLabel.Style.Small, 20, 260));
            listActions.AddRight(Btn("選択した項目を削除", Glyph.Delete, ButtonKind.Danger, (s, e) => RemoveSelectedIgnore()));
            cardIgnoreList.Add(lstIgnore, listActions);
            page.Controls.Add(cardIgnoreList);

            btnSaveIgnore = Btn("除外設定を保存", Glyph.Save, ButtonKind.Primary, (s, e) => SaveIgnore());
            btnSaveIgnore.Highlighted = _ignoreDirty;
            var actions = new RowPanel { Height = S(40), BackColor = Theme.Surface };
            actions.AddRight(btnSaveIgnore);
            page.Controls.Add(actions);

            var opt = new Card("オプション", "手動実行と自動実行の両方に適用されます（変更はすぐに保存されます）");
            tglHidden = Toggle("隠し/システム属性のファイル・フォルダを除外する");
            tglLinks = Toggle("ジャンクション/シンボリックリンクを辿る", "通常はオフにしてください。リンク先の意図しない範囲までコピーする恐れがあります");
            opt.Add(tglHidden, tglLinks,
                Label("各ドライブ直下の System Volume Information・$RECYCLE.BIN・pagefile.sys などは常に除外します", ThemedLabel.Style.Small, 20));
            page.Controls.Add(opt);

            _loading = true;
            tglHidden.Checked = _options.SkipHiddenAndSystem;
            tglLinks.Checked = _options.FollowReparsePoints;
            cmbIgnoreKind.SelectedIndex = 0;
            _loading = false;

            tglHidden.CheckedChanged += (s, e) => { if (!_loading) { _options.SkipHiddenAndSystem = tglHidden.Checked; SaveOptions(); } };
            tglLinks.CheckedChanged += (s, e) => { if (!_loading) { _options.FollowReparsePoints = tglLinks.Checked; SaveOptions(); } };
            cmbIgnoreKind.SelectedIndexChanged += (s, e) => UpdateIgnoreHint();
            inIgnore.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { AddIgnoreFromInput(); e.SuppressKeyPress = true; } };
            UpdateIgnoreHint();

            return page;
        }

        private void UpdateIgnoreHint()
        {
            var kind = (IgnoreKind)Math.Max(0, cmbIgnoreKind.SelectedIndex);
            (lblIgnoreHint.Text, inIgnore.Placeholder) = kind switch
            {
                IgnoreKind.Path => ("そのファイル、またはそのフォルダと配下すべてを除外します", @"例: F:\steam"),
                IgnoreKind.Extension => ("その拡張子のファイルを除外します", "例: .tmp"),
                IgnoreKind.Pattern => ("* は区切り内の任意の文字、** はフォルダをまたぐ任意の文字、? は1文字", "例: **/Cache/**"),
                _ => ("どこにあっても、この名前のフォルダを除外します", "例: .git、node_modules"),
            };
        }

        private void FillIgnoreList()
        {
            IEnumerable<IgnoreItem> items = _ignore;
            string title = "すべて";
            if (Enum.TryParse<IgnoreKind>(_sideKey, out var kind))
            {
                items = items.Where(x => x.Kind == kind);
                title = IgnoreKindLabels[(int)kind];
            }

            var list = items.ToList();
            cardIgnoreList.SetTitle($"{title}（{list.Count} 件）");
            lstIgnore.BeginUpdate();
            lstIgnore.Items.Clear();
            foreach (var x in list) lstIgnore.Items.Add(x);
            lstIgnore.EndUpdate();
        }

        private void lstIgnore_DrawItem(object? sender, DrawItemEventArgs e)
        {
            var g = e.Graphics;
            using (var back = new SolidBrush(Theme.Card)) g.FillRectangle(back, e.Bounds);
            if (e.Index < 0 || lstIgnore.Items[e.Index] is not IgnoreItem item) return;

            var rowRect = new RectangleF(e.Bounds.X + 0.5f, e.Bounds.Y + S(2) + 0.5f, e.Bounds.Width - S(4) - 1, e.Bounds.Height - S(4) - 1);
            if ((e.State & DrawItemState.Selected) != 0)
                Theme.FillRound(g, Theme.SideSelected, rowRect, S(8), Theme.SideSelectedBorder);

            string glyph = item.Kind switch
            {
                IgnoreKind.Path => Glyph.Folder,
                IgnoreKind.Extension => Glyph.Tag,
                IgnoreKind.Pattern => Glyph.CheckList,
                _ => Glyph.Document,
            };
            Theme.DrawGlyph(g, glyph, 11.5f, Theme.Text, new Rectangle(e.Bounds.X + S(10), e.Bounds.Y, S(24), e.Bounds.Height));

            string kindLabel = IgnoreKindLabels[(int)item.Kind];
            int kindWidth = TextRenderer.MeasureText(kindLabel, Theme.Small).Width;
            TextRenderer.DrawText(g, item.Value, Theme.Body,
                new Rectangle(e.Bounds.X + S(44), e.Bounds.Y, e.Bounds.Width - S(44) - kindWidth - S(24), e.Bounds.Height), Theme.Text,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
            TextRenderer.DrawText(g, kindLabel, Theme.Small,
                new Rectangle(e.Bounds.Right - kindWidth - S(16), e.Bounds.Y, kindWidth + S(4), e.Bounds.Height), Theme.TextMuted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.Right | TextFormatFlags.SingleLine);
        }

        private void AddIgnoreFromInput()
        {
            string value = inIgnore.Value.Trim();
            var kind = (IgnoreKind)Math.Max(0, cmbIgnoreKind.SelectedIndex);
            if (string.IsNullOrEmpty(value)) return;

            switch (kind)
            {
                case IgnoreKind.Extension:
                    if (value.IndexOfAny(new[] { '\\', '/', '*', '?', ':' }) >= 0 && !IgnoreConfig.IsExtension(value))
                    {
                        MessageBox.Show("拡張子を入力してください（例 .tmp）", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    value = IgnoreConfig.NormalizeExtension(value);
                    break;

                case IgnoreKind.Pattern:
                    if (!IgnoreConfig.IsPattern(value))
                    {
                        MessageBox.Show("* または ? を含むパターンを入力してください（例 **/Cache/**、F:\\work\\*.bak）", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    if (IgnoreConfig.IsExtension(value))
                    {
                        kind = IgnoreKind.Extension;   // "*.tmp" は拡張子として扱う
                        value = IgnoreConfig.NormalizeExtension(value);
                    }
                    break;

                case IgnoreKind.FolderName:
                    value = value.Trim('\\', '/');
                    if (value.IndexOfAny(new[] { '\\', '/', '*', '?', ':' }) >= 0)
                    {
                        MessageBox.Show("フォルダ名だけを入力してください（例 .git、node_modules）", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    break;

                default:
                    if (IgnoreConfig.IsPattern(value))
                    {
                        MessageBox.Show("パスに * や ? は使えません。ワイルドカードを使う場合は種類を「ワイルドカード」にしてください。", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    break;
            }

            if (AddIgnoreItem(new IgnoreItem(kind, value)))
                inIgnore.Value = "";
        }

        private bool AddIgnoreItem(IgnoreItem item)
        {
            if (_ignore.Any(x => x.Kind == item.Kind && string.Equals(x.Value, item.Value, StringComparison.OrdinalIgnoreCase)))
            {
                MessageBox.Show("既に追加されています。", "重複", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }

            _ignore.Add(item);
            MarkIgnoreDirty();
            FillIgnoreList();
            RefreshSide();
            return true;
        }

        private void PickIgnoreFile()
        {
            using var dlg = new OpenFileDialog();
            if (dlg.ShowDialog(this) == DialogResult.OK)
                AddIgnoreItem(new IgnoreItem(IgnoreKind.Path, dlg.FileName));
        }

        private void PickIgnoreFolder()
        {
            using var dlg = new FolderBrowserDialog();
            if (dlg.ShowDialog(this) == DialogResult.OK)
                AddIgnoreItem(new IgnoreItem(IgnoreKind.Path, dlg.SelectedPath));
        }

        private void RemoveSelectedIgnore()
        {
            var selected = lstIgnore.SelectedItems.OfType<IgnoreItem>().ToList();
            if (selected.Count == 0)
            {
                MessageBox.Show("削除する項目を選択してください。", "通知", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            foreach (var item in selected) _ignore.Remove(item);
            MarkIgnoreDirty();
            FillIgnoreList();
            RefreshSide();
        }

        private void MarkIgnoreDirty()
        {
            _ignoreDirty = true;
            btnSaveIgnore.Highlighted = true;
        }

        private void SaveIgnore()
        {
            var config = new IgnoreConfig
            {
                Paths = _ignore.Where(x => x.Kind == IgnoreKind.Path).Select(x => x.Value).ToList(),
                Extensions = _ignore.Where(x => x.Kind == IgnoreKind.Extension).Select(x => x.Value).ToList(),
                Patterns = _ignore.Where(x => x.Kind == IgnoreKind.Pattern).Select(x => x.Value).ToList(),
                FolderNames = _ignore.Where(x => x.Kind == IgnoreKind.FolderName).Select(x => x.Value).ToList(),
            };

            try
            {
                ConfigStore.SaveIgnore(config);
                _ignoreDirty = false;
                btnSaveIgnore.Highlighted = false;
                MessageBox.Show("除外設定を保存しました。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowSaveError("除外設定の保存に失敗しました", ex);
            }
        }

        // ================================================================
        // 設定
        // ================================================================

        private Control BuildSettingsGeneral()
        {
            var page = NewPage();
            page.Controls.Add(new PageHeader("一般", "起動と見た目の設定です"));

            var startup = new Card("起動と常駐");
            tglStartup = Toggle("Windows のサインイン時に起動して、タスクトレイに常駐する",
                "ウィンドウは出さずにトレイで待機します。アイコンをクリックすると開きます");
            tglStartup.Checked = _appSettings.StartWithWindows;
            tglStartup.CheckedChanged += (s, e) =>
            {
                _appSettings.StartWithWindows = tglStartup.Checked;
                TrySaveAppSettings();
                if (AppPaths.IsOverridden) return;
                try { StartupRegistration.Apply(_appSettings.StartWithWindows, Application.ExecutablePath); }
                catch (Exception ex) { ShowError("自動起動の設定に失敗しました", ex); }
            };
            startup.Add(tglStartup,
                Label("ウィンドウを閉じたり最小化したりしても、終了せずにタスクトレイに常駐します（終了はトレイのメニューから）",
                    ThemedLabel.Style.Small, 20));
            page.Controls.Add(startup);

            var card = new Card("テーマ");
            tglDark = Toggle("ダークモード", "左下のボタンでも切り替えられます（起動時は Windows の設定に合わせます）");
            tglDark.Checked = Theme.IsDark;
            tglDark.CheckedChanged += (s, e) => { if (tglDark.Checked != Theme.IsDark) BeginInvoke(ToggleTheme); };
            card.Add(tglDark);
            page.Controls.Add(card);
            return page;
        }

        private Control BuildSettingsUpdate()
        {
            var page = NewPage();
            page.Controls.Add(new PageHeader("アップデート", $"今のバージョン: {UpdateService.CurrentVersion}"));

            var card = new Card("新しいバージョン", "GitHub で公開されている最新版を確認します");
            lblUpdateStatus = Label("", height: 24);
            lblUpdateNotes = Label("", ThemedLabel.Style.Small, 110);
            lblUpdateNotes.Visible = false;
            updateProgress = new ThinProgress { Height = S(6), Visible = false };
            btnCheckUpdate = Btn("更新を確認", Glyph.Refresh, click: async (s, e) => await CheckForUpdatesAsync(manual: true));
            btnInstallUpdate = Btn("更新する", Glyph.Download, ButtonKind.Primary, async (s, e) => await InstallUpdateAsync());
            btnInstallUpdate.Visible = false;
            var row = new RowPanel { Height = S(40), BackColor = Theme.Card };
            row.AddLeft(btnCheckUpdate).AddRight(btnInstallUpdate);
            card.Add(lblUpdateStatus, updateProgress, lblUpdateNotes, row);
            page.Controls.Add(card);

            var auto = new Card("自動で確認");
            tglAutoUpdate = Toggle("新しいバージョンを自動で確認する（1日1回）",
                "見つかったらトレイの通知でお知らせします。「更新する」を押すと、ダウンロードからインストールまで自動で行います");
            tglAutoUpdate.Checked = _appSettings.AutoCheckUpdates;
            tglAutoUpdate.CheckedChanged += (s, e) => { _appSettings.AutoCheckUpdates = tglAutoUpdate.Checked; TrySaveAppSettings(); };
            auto.Add(tglAutoUpdate,
                Label("更新前のバージョンは %LocalAppData%\\FilesBackup\\InstallBackup に保存します", ThemedLabel.Style.Small, 20));
            page.Controls.Add(auto);
            return page;
        }

        private static readonly int[] RetentionChoices = { 7, 14, 30, 60, 90, 180, 365 };

        private Control BuildSettingsProtection()
        {
            var page = NewPage();
            page.Controls.Add(new PageHeader("保護とネットワーク", "手動実行とスケジュールによる自動実行の両方に適用されます（変更はすぐに保存されます）"));

            // 世代管理
            var versions = new Card("前の版を残す（世代管理）",
                "コピー元のファイルが壊れたり、ランサムウェアで暗号化されたりしても、上書きされる前の版から戻せます");
            var tglVersions = Toggle("上書き・削除する前のファイルを残す",
                $"コピー先の {BackupManager.VersionsFolderName}\\日時\\ に移します（隠しフォルダ）。ミラーモードで削除するファイルも、ここに移します");
            var cmbRetention = Combo(RetentionChoices.Select(d => $"{d} 日"), 110);
            var retentionRow = new RowPanel { Height = S(36), BackColor = Theme.Card };
            retentionRow.AddLeft(Label("保持する期間", width: 120));
            retentionRow.AddLeft(cmbRetention);
            retentionRow.AddFill(Label("これより古い版は、次のバックアップのときに削除します", ThemedLabel.Style.Small, 20));
            versions.Add(tglVersions, retentionRow);
            page.Controls.Add(versions);

            // ネットワークドライブ
            var network = new Card("ネットワークドライブ（NAS）",
                "Z: などの割り当てを覚えておき、管理者権限の自動実行でも \\\\サーバー\\共有 として使います");
            var lblDrives = Label("", ThemedLabel.Style.Small, 20);
            void ShowDrives()
            {
                var map = NetworkPaths.Remembered;
                lblDrives.Text = map.Count == 0
                    ? "覚えているネットワークドライブはありません"
                    : "覚えている割り当て: " + string.Join("、", map.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: = {kv.Value}"));
                lblDrives.Height = S(20 * Math.Max(1, (lblDrives.Text.Length + 79) / 80));
            }
            var driveRow = new RowPanel { Height = S(40), BackColor = Theme.Card };
            driveRow.AddLeft(Btn("割り当てを読み込み直す", Glyph.Refresh, click: async (s, e) =>
            {
                await System.Threading.Tasks.Task.Run(NetworkPaths.Remember);
                ShowDrives();
            }));
            var tglNames = Toggle("NAS で使えない名前を、使える名前に変えて保存する",
                "長すぎる名前や末尾のピリオドなど。名前の先頭を残して ~識別子 を付けます（ログに記録）");
            network.Add(lblDrives, driveRow, tglNames,
                Label("接続できない場合は、ファイルごとのエラーにせず「延期」にします。スケジュール実行では最大 10 分、接続を待ちます", ThemedLabel.Style.Small, 20),
                Label("認証が必要な NAS は、Windows の「資格情報マネージャー」にユーザー名とパスワードを登録してください", ThemedLabel.Style.Small, 20));
            page.Controls.Add(network);

            // クラウドの同期フォルダ
            var cloud = new Card("OneDrive・Google ドライブ",
                "「オンラインのみ」のファイル（クラウドにだけあり、PC にはまだ無いファイル）の扱いです");
            var tglOnline = Toggle("オンラインのみのファイルもダウンロードしてコピーする",
                "オフ（おすすめ）：スキップします。オン：読むたびにダウンロードが始まるので、ディスクと回線を大量に使うことがあります");
            cloud.Add(tglOnline);
            page.Controls.Add(cloud);

            // 検証
            var verify = new Card("コピーの確認");
            var tglVerify = Toggle("コピーした後に、内容が一致するか確かめる",
                "コピー元とコピー先を読み直して SHA-256 で比べます。確実になりますが、コピーにかかる時間が増えます");
            verify.Add(tglVerify);
            page.Controls.Add(verify);

            _loading = true;
            tglVersions.Checked = _options.KeepVersions;
            int idx = Array.IndexOf(RetentionChoices, _options.VersionRetentionDays);
            cmbRetention.SelectedIndex = idx >= 0 ? idx : Array.IndexOf(RetentionChoices, 30);
            cmbRetention.Enabled = _options.KeepVersions;
            tglNames.Checked = _options.FixUnsupportedNames;
            tglOnline.Checked = _options.DownloadOnlineOnlyFiles;
            tglVerify.Checked = _options.VerifyAfterCopy;
            ShowDrives();
            _loading = false;

            tglVersions.CheckedChanged += (s, e) =>
            {
                if (_loading) return;
                _options.KeepVersions = tglVersions.Checked;
                cmbRetention.Enabled = tglVersions.Checked;
                SaveOptions();
                RefreshSide();
            };
            cmbRetention.SelectedIndexChanged += (s, e) =>
            {
                if (_loading || cmbRetention.SelectedIndex < 0) return;
                _options.VersionRetentionDays = RetentionChoices[cmbRetention.SelectedIndex];
                SaveOptions();
            };
            tglNames.CheckedChanged += (s, e) => { if (!_loading) { _options.FixUnsupportedNames = tglNames.Checked; SaveOptions(); } };
            tglOnline.CheckedChanged += (s, e) => { if (!_loading) { _options.DownloadOnlineOnlyFiles = tglOnline.Checked; SaveOptions(); } };
            tglVerify.CheckedChanged += (s, e) => { if (!_loading) { _options.VerifyAfterCopy = tglVerify.Checked; SaveOptions(); } };

            return page;
        }

        private Control BuildSettingsStorage()
        {
            var page = NewPage();
            page.Controls.Add(new PageHeader("保存先", "設定・ログ・履歴の保存場所です"));

            var card = new Card("保存先のフォルダ");
            var path = new ModernInput { Height = S(38), ReadOnly = true, Value = AppPaths.DataDir };
            var row = new RowPanel { Height = S(40), BackColor = Theme.Card };
            row.AddFill(path).AddRight(Btn("フォルダを開く", Glyph.Folder, click: (s, e) => OpenFolder(AppPaths.DataDir)));
            card.Add(row, Label(AppPaths.IsMachineMode
                ? "管理者運用モード：全ユーザー共通の保存先を使っています。設定の変更には管理者権限が必要です。"
                : "ユーザーごとの保存先を使っています。", ThemedLabel.Style.Small, 20));
            page.Controls.Add(card);

            var machine = new Card("管理者運用モード", "PC の管理者が設定を管理し、全ユーザーで共通の設定を使う場合に使います");
            tglMachine = Toggle("全ユーザー共通の保存先（%ProgramData%\\FilesBackup）を使う", "切り替えには管理者権限が必要です。切り替え後にアプリを再起動します");
            tglMachine.Checked = AppPaths.IsMachineMode;
            tglMachine.CheckedChanged += tglMachine_CheckedChanged;
            machine.Add(tglMachine);
            page.Controls.Add(machine);
            return page;
        }

        private void tglMachine_CheckedChanged(object? sender, EventArgs e)
        {
            if (_loading || tglMachine.Checked == AppPaths.IsMachineMode) return;

            void Revert()
            {
                _loading = true;
                tglMachine.Checked = AppPaths.IsMachineMode;
                _loading = false;
            }

            if (_backupRunning || _tasksDirty || _ignoreDirty || _scheduleDirty)
            {
                MessageBox.Show("バックアップの実行中や、保存していない変更があるときは切り替えられません。", "確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
                Revert();
                return;
            }

            if (!IsElevated)
            {
                Revert();
                if (MessageBox.Show("切り替えには管理者権限が必要です。管理者としてアプリを再起動しますか？", "管理者権限が必要です",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                    RestartApp(elevated: true);
                return;
            }

            string message = tglMachine.Checked
                ? "現在の設定と履歴を %ProgramData%\\FilesBackup にコピーし、全ユーザー共通の設定に切り替えます。\n以後、設定の変更には管理者権限が必要です。\n\n切り替えてアプリを再起動しますか？"
                : "共通の設定を、このユーザーの保存先（%LocalAppData%\\FilesBackup）にコピーして戻します。\n\n切り替えてアプリを再起動しますか？";
            if (MessageBox.Show(message, "管理者運用モード", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                Revert();
                return;
            }

            try
            {
                if (tglMachine.Checked) AppPaths.EnableMachineMode();
                else AppPaths.DisableMachineMode();
            }
            catch (Exception ex)
            {
                Revert();
                ShowError("切り替えに失敗しました", ex);
                return;
            }

            RestartApp(elevated: false);
        }

        private Control BuildSettingsTransfer()
        {
            var page = NewPage();
            page.Controls.Add(new PageHeader("インポート/エクスポート", "すべての設定を1つの JSON ファイルにまとめて保存・読み込みします"));

            var card = new Card("設定ファイル", "バックアップ・除外・スケジュール・オプションの設定が対象です（履歴は含みません）");
            var row = new RowPanel { Height = S(40), BackColor = Theme.Card };
            row.AddLeft(Btn("設定をエクスポート", Glyph.Export, click: (s, e) => ExportSettings()));
            row.AddLeft(Btn("設定をインポート", Glyph.Import, click: (s, e) => ImportSettings()));
            card.Add(row, Label("ファイルの形式は README の「インポート/エクスポートの形式」に記載しています", ThemedLabel.Style.Small, 20));
            page.Controls.Add(card);
            return page;
        }

        private Control BuildSettingsPrivilege()
        {
            var page = NewPage();
            page.Controls.Add(new PageHeader("実行権限", "通常は管理者権限なしで動きます"));

            bool elevated = IsElevated;
            var card = new Card("現在の権限");
            var row = new RowPanel { Height = S(40), BackColor = Theme.Card };
            row.AddFill(Label(elevated ? "管理者として実行しています" : "標準ユーザーとして実行しています", height: 24));
            var restart = Btn("管理者として再起動", Glyph.Admin, click: (s, e) => ConfirmAndRestartElevated());
            restart.Enabled = !elevated;
            row.AddRight(restart);
            card.Add(row, Label("管理者権限が必要なフォルダのバックアップ、最高特権でのスケジュール登録、管理者運用モードの切り替えに使います",
                ThemedLabel.Style.Small, 20));
            page.Controls.Add(card);
            return page;
        }

        private Control BuildSettingsAbout()
        {
            var page = NewPage();
            page.Controls.Add(new PageHeader("バージョン情報", "FilesBackup"));

            var version = typeof(Form1).Assembly.GetName().Version;
            var card = new Card("FilesBackup");
            var row = new RowPanel { Height = S(40), BackColor = Theme.Card };
            row.AddFill(Label($"バージョン {version?.ToString(3)}", height: 24));
            row.AddRight(Btn("GitHub を開く", Glyph.Link, click: (s, e) => OpenUrl(GitHubUrl)));
            card.Add(row,
                Label($"設定・ログ・履歴: {AppPaths.DataDir}", ThemedLabel.Style.Small, 20),
                Label($"実行ファイル: {Application.ExecutablePath}", ThemedLabel.Style.Small, 20));
            page.Controls.Add(card);
            return page;
        }

        private static void OpenFolder(string path)
        {
            try
            {
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowError("フォルダを開けませんでした", ex);
            }
        }

        private static void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { ShowError("開けませんでした", ex); }
        }

        private void ExportSettings()
        {
            if (_tasksDirty || _scheduleDirty || _ignoreDirty)
            {
                MessageBox.Show("保存されていない変更があります。各ページで保存してからエクスポートしてください。", "確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new SaveFileDialog
            {
                Filter = "JSON ファイル (*.json)|*.json",
                FileName = $"FilesBackup_settings_{DateTime.Now:yyyyMMdd}.json",
            };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                ConfigStore.Export(dlg.FileName);
                MessageBox.Show($"設定をエクスポートしました。\n{dlg.FileName}", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowError("エクスポートに失敗しました", ex);
            }
        }

        private void ImportSettings()
        {
            if (_backupRunning)
            {
                MessageBox.Show("バックアップ実行中はインポートできません。", "確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dlg = new OpenFileDialog { Filter = "JSON ファイル (*.json)|*.json|すべてのファイル (*.*)|*.*" };
            if (dlg.ShowDialog(this) != DialogResult.OK) return;

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
            string summary =
                "次の設定で、現在の設定を上書きします。よろしいですか？\n\n" +
                $"・バックアップ設定: {bundle.Tasks.Count} 件\n" +
                $"・除外設定: {ignoreCount} 件\n" +
                $"・スケジュール: {(bundle.Schedule == null ? "含まれていません（現在の設定のまま）" : ScheduleSummary(bundle.Schedule))}\n" +
                $"・ミラーモード: {(bundle.Options.MirrorMode ? "有効" : "無効")}\n" +
                $"（エクスポート日時: {bundle.ExportedAt:yyyy/MM/dd HH:mm}）";

            if (MessageBox.Show(summary, "インポート", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            try
            {
                ConfigStore.ApplyBundle(bundle);
            }
            catch (Exception ex)
            {
                ShowSaveError("インポートに失敗しました", ex);
                return;
            }

            _tasksDirty = _ignoreDirty = _scheduleDirty = false;
            LoadData();
            BuildUi();

            if (bundle.Schedule != null &&
                MessageBox.Show("読み込んだスケジュールをタスク スケジューラに登録しますか？", "インポート",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
            {
                RegisterSchedule(_schedule, showMessage: true);
            }
            else
            {
                MessageBox.Show("インポートしました。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private void ConfirmAndRestartElevated()
        {
            if ((_tasksDirty || _ignoreDirty || _scheduleDirty) &&
                MessageBox.Show("保存されていない変更があります。保存せずに再起動しますか？", "確認",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            RestartApp(elevated: true);
        }

        private void RestartApp(bool elevated)
        {
            if (_backupRunning)
            {
                MessageBox.Show("バックアップ実行中は再起動できません。", "確認", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                var psi = new ProcessStartInfo(Application.ExecutablePath, Program.RestartArg) { UseShellExecute = true };
                if (elevated) psi.Verb = "runas";
                Process.Start(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                return;   // UAC の確認で「いいえ」が選ばれた
            }
            catch (Exception ex)
            {
                ShowError("再起動できませんでした", ex);
                return;
            }

            trayIcon.Visible = false;
            trayIcon.Dispose();
            Environment.Exit(0);
        }

        // ================================================================
        // トレイ・起動時・終了時
        // ================================================================

        private void InitializeTrayIcon()
        {
            var menu = new ContextMenuStrip { Font = Theme.Body };
            menu.Items.Add("開く", null, (s, e) => ShowWindow());
            menu.Items.Add("今すぐバックアップ", null, async (s, e) =>
            {
                ShowWindow();
                Navigate("backup", "overview");
                await RunBackupAsync(dryRun: false);
            });
            trayCancelItem = new ToolStripMenuItem("バックアップを中止", null, (s, e) => { _currentManager?.Resume(); _cts?.Cancel(); }) { Enabled = false };
            menu.Items.Add(trayCancelItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("更新を確認", null, async (s, e) => await CheckForUpdatesAsync(manual: true));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("終了", null, (s, e) => ExitApp());

            trayIcon = new NotifyIcon
            {
                Icon = _appIcon,
                ContextMenuStrip = menu,
                Text = "FilesBackup",
                Visible = _screenshotDir == null,
            };
            trayIcon.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
            trayIcon.BalloonTipClicked += (s, e) =>
            {
                var action = _balloonAction;
                _balloonAction = null;
                (action ?? ShowWindow)();
            };
            trayIcon.BalloonTipClosed += (s, e) => _balloonAction = null;
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon, Action? onClick = null)
        {
            if (_screenshotDir != null) return;
            _balloonAction = onClick;
            trayIcon.ShowBalloonTip(8000, title, text, icon);
        }

        public void ShowWindow()
        {
            _allowVisible = true;
            if (!_uiBuilt) BuildUi();
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            BringToFront();
            Activate();
        }

        // トレイに隠す（初回だけ、トレイで動いていることを知らせる）
        private void HideToTray()
        {
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                ShowBalloon("FilesBackup", "タスクトレイで動いています。アイコンをクリックすると開きます。", ToolTipIcon.Info);
            }

            // 閉じるボタンの処理が終わってから画面を捨てる
            BeginInvoke(ReleaseUiIfIdle);
        }

        private void ExitApp()
        {
            if (_backupRunning)
            {
                ShowWindow();
                MessageBox.Show("バックアップ実行中です。中止してから終了してください。", "確認", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if ((_tasksDirty || _ignoreDirty || _scheduleDirty) &&
                MessageBox.Show("保存されていない変更があります。保存せずに終了しますか？", "確認",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                ShowWindow();
                return;
            }
            trayIcon.Visible = false;
            trayIcon.Dispose();
            Environment.Exit(0);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (WindowState == FormWindowState.Minimized && _screenshotDir == null)
                HideToTray();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_screenshotDir != null) return;

            if (_backupRunning)
            {
                MessageBox.Show("バックアップ実行中です。完了または中止してから閉じてください。", "確認", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                e.Cancel = true;
                return;
            }

            if (_tasksDirty || _scheduleDirty || _ignoreDirty)
            {
                if (MessageBox.Show("保存されていない変更があります。保存せずに閉じますか？", "確認",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.No)
                {
                    e.Cancel = true;
                    return;
                }
            }

            // ウィンドウを閉じてもトレイに常駐する
            e.Cancel = true;
            HideToTray();
        }

        private async void Form1_Shown(object? sender, EventArgs e)
        {
            if (_screenshotDir != null)
                await CaptureScreenshotsAsync(_screenshotDir);
        }

        // 起動直後に1回だけ行う処理（ウィンドウを出していなくても動く）
        private async void StartupTimer_Tick(object? sender, EventArgs e)
        {
            _startupTimer.Stop();

            // サインイン時の自動起動を設定に合わせる（保存先を差し替えたテスト実行では、PC の設定に触れない）
            if (!AppPaths.IsOverridden)
            {
                try { StartupRegistration.Apply(_appSettings.StartWithWindows, Application.ExecutablePath); }
                catch (Exception ex) { Logger.Error("自動起動の登録に失敗しました", ex); }
            }

            // ネットワークドライブの割り当てを覚えておく（管理者権限のスケジュール実行では割り当てが見えないため）
            await System.Threading.Tasks.Task.Run(NetworkPaths.Remember);

            // アップデートの結果を知らせる
            string? updateResult = UpdateService.TakeResult();
            if (updateResult != null)
            {
                if (updateResult.StartsWith("OK"))
                    ShowBalloon("アップデートしました", $"FilesBackup {UpdateService.CurrentVersion} に更新しました。", ToolTipIcon.Info);
                else
                    ShowBalloon("アップデートに失敗しました", updateResult.Replace("ERROR ", ""), ToolTipIcon.Warning);
            }

            await CheckMissedRunAsync();

            if (_appSettings.AutoCheckUpdates)
                await CheckForUpdatesAsync(manual: false);

            // 起動時の処理で使ったメモリを返す（トレイ常駐で待機するだけなので）
            if (!Visible) MemoryTrimmer.Trim();
        }

        // 予定の実行が行われていなければ知らせる（PC の電源が切れていた等）
        private async Task CheckMissedRunAsync()
        {
            try
            {
                var cfg = ConfigStore.LoadSchedule();
                if (cfg == null || !cfg.NotifyMissedRuns) return;

                DateTime savedAt = File.GetLastWriteTime(AppPaths.ScheduleConfigPath);
                var missed = SchedulerService.FindMissedRun(cfg, _history, DateTime.Now, savedAt);
                if (missed == null || RunLock.IsBackupRunning()) return;

                async void RunNow()
                {
                    ShowWindow();
                    Navigate("backup", "overview");
                    await RunBackupAsync(dryRun: false);
                }

                if (!Visible)
                {
                    // トレイ常駐中は通知で知らせ、クリックされたら実行する
                    ShowBalloon("予定のバックアップが実行されていません",
                        $"{missed:MM/dd HH:mm} の予定が実行されていません。クリックすると今すぐ実行します。",
                        ToolTipIcon.Warning, RunNow);
                    return;
                }

                if (MessageBox.Show($"{missed:yyyy/MM/dd HH:mm} に予定されていたバックアップが実行されていません。\n今すぐ実行しますか？",
                        "予定のバックアップ", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                {
                    Navigate("backup", "overview");
                    await RunBackupAsync(dryRun: false);
                }
            }
            catch (Exception ex)
            {
                Logger.Error("予定実行の確認に失敗しました", ex);
            }
        }

        // ================================================================
        // アップデート
        // ================================================================

        private async Task CheckForUpdatesAsync(bool manual)
        {
            if (_updating) return;
            if (!manual)
            {
                // 自動確認は1日1回まで
                if (!_appSettings.AutoCheckUpdates) return;
                if (_appSettings.LastUpdateCheck is DateTime last && DateTime.Now - last < TimeSpan.FromHours(23)) return;
            }

            SetUpdateStatus("更新を確認しています...");
            try
            {
                _update = await UpdateService.CheckAsync();
                _appSettings.LastUpdateCheck = DateTime.Now;
                TrySaveAppSettings();
            }
            catch (Exception ex)
            {
                Logger.Warn($"更新の確認に失敗しました: {ex.Message}");
                SetUpdateStatus($"更新を確認できませんでした（{ex.Message}）");
                if (manual)
                    MessageBox.Show($"更新を確認できませんでした。\n{ex.Message}", "アップデート", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            UpdateUpdateUi();

            if (_update == null)
            {
                if (manual)
                    MessageBox.Show($"最新のバージョン（{UpdateService.CurrentVersion}）を使っています。", "アップデート", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (manual)
            {
                ShowWindow();
                Navigate("settings", "update");
            }
            else if (_update.Version.ToString() != _appSettings.SkippedVersion)
            {
                ShowBalloon("新しいバージョンがあります",
                    $"FilesBackup {_update.Version} が公開されています。クリックすると更新できます。",
                    ToolTipIcon.Info,
                    () => { ShowWindow(); Navigate("settings", "update"); });
            }
        }

        private void SetUpdateStatus(string text)
        {
            if (lblUpdateStatus != null && !lblUpdateStatus.IsDisposed) lblUpdateStatus.Text = text;
        }

        /// <summary>アップデートの有無に合わせて、概要のお知らせと設定ページを更新する</summary>
        private void UpdateUpdateUi()
        {
            if (bannerUpdate == null || bannerUpdate.IsDisposed) return;

            bool available = _update != null;
            bannerUpdate.Visible = available && _update!.Version.ToString() != _appSettings.SkippedVersion;
            if (available) lblBannerUpdate.Text = $"新しいバージョン {_update!.Version} が公開されています（今のバージョン: {UpdateService.CurrentVersion}）";

            btnInstallUpdate.Visible = available;
            btnInstallUpdate.Enabled = !_updating;
            btnCheckUpdate.Enabled = !_updating;
            btnInstallUpdate.Parent?.PerformLayout();

            string last = _appSettings.LastUpdateCheck is DateTime d ? $"（最終確認: {d:MM/dd HH:mm}）" : "";
            lblUpdateStatus.Text = available
                ? $"新しいバージョン {_update!.Version} があります。「更新する」で、ダウンロードからインストールまで自動で行います。"
                : $"最新のバージョンを使っています{last}";

            lblUpdateNotes.Text = available ? ReleaseNotesSummary(_update!.Notes) : "";
            lblUpdateNotes.Visible = available && lblUpdateNotes.Text.Length > 0;

            if (_page == "settings") RefreshSide();
        }

        private static string ReleaseNotesSummary(string notes)
        {
            var lines = notes.Replace("\r\n", "\n").Split('\n')
                .Select(l => l.Trim())
                .Where(l => l.Length > 0)
                .Take(6)
                .ToList();
            return string.Join("\n", lines);
        }

        private void TrySaveAppSettings()
        {
            try { ConfigStore.SaveAppSettings(_appSettings); }
            catch (Exception ex) { Logger.Warn($"アプリ設定の保存に失敗しました: {ex.Message}"); }
        }

        private async Task InstallUpdateAsync()
        {
            if (_update == null || _updating) return;

            if (_backupRunning || RunLock.IsBackupRunning())
            {
                MessageBox.Show("バックアップの実行中は更新できません。終わってからもう一度お試しください。", "アップデート", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if ((_tasksDirty || _ignoreDirty || _scheduleDirty) &&
                MessageBox.Show("保存されていない変更があります。保存せずに更新しますか？", "確認",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;

            string installDir = AppContext.BaseDirectory;
            string elevation = UpdateService.NeedsElevation(installDir)
                ? "\n\nインストール先への書き込みに管理者権限が必要なため、確認の画面（UAC）が表示されます。"
                : "";
            if (MessageBox.Show(
                    $"FilesBackup を {_update.Version} に更新します。\n\n" +
                    "ダウンロードが終わるとアプリをいったん終了し、ファイルを入れ替えてから自動で起動し直します。" +
                    "設定・履歴はそのまま引き継がれ、今のバージョンは控えとして保存します。" + elevation,
                    "アップデート", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                return;

            _updating = true;
            UpdateUpdateUi();
            updateProgress.Visible = true;
            updateProgress.Value = 0;
            SetUpdateStatus("ダウンロードしています...");

            try
            {
                var progressReporter = new Progress<double>(p =>
                {
                    updateProgress.Value = (float)p;
                    SetUpdateStatus($"ダウンロードしています... {p:P0}");
                });
                string package = await UpdateService.DownloadAsync(_update, installDir, progressReporter);

                SetUpdateStatus("インストールを開始します...");
                UpdateService.LaunchInstaller(package, installDir);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                _updating = false;
                updateProgress.Visible = false;
                SetUpdateStatus("管理者権限の確認がキャンセルされたため、更新しませんでした。");
                UpdateUpdateUi();
                return;
            }
            catch (Exception ex)
            {
                _updating = false;
                updateProgress.Visible = false;
                Logger.Error("アップデートに失敗しました", ex);
                UpdateUpdateUi();
                ShowError("アップデートに失敗しました", ex);
                return;
            }

            // 入れ替えはスクリプトが行う。アプリは終了し、入れ替え後に自動で起動し直す
            trayIcon.Visible = false;
            trayIcon.Dispose();
            Environment.Exit(0);
        }

        // ---- README 用の画面写真（--screenshots <フォルダ>）----

        private async Task CaptureScreenshotsAsync(string dir)
        {
            Directory.CreateDirectory(dir);
            var shots = new (string Page, string Key, string File)[]
            {
                ("backup", "overview", "backup.png"),
                ("backup", "task:0", "task.png"),
                ("schedule", "", "schedule.png"),
                ("ignore", "all", "ignore.png"),
                ("history", "all", "history.png"),
                ("settings", "storage", "settings.png"),
                ("settings", "protection", "protection.png"),
            };

            Theme.Set(false);
            BuildUi();
            foreach (var shot in shots)
            {
                Navigate(shot.Page, shot.Key);
                await SaveWindowImageAsync(Path.Combine(dir, shot.File));
            }

            Theme.Set(true);
            BuildUi();
            Navigate("backup", "overview");
            await SaveWindowImageAsync(Path.Combine(dir, "backup_dark.png"));

            Environment.Exit(0);
        }

        private async Task SaveWindowImageAsync(string path)
        {
            Refresh();
            await Task.Delay(400);
            Refresh();

            using var bmp = new Bitmap(Width, Height);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                PrintWindow(Handle, hdc, 2 /* PW_RENDERFULLCONTENT */);
                g.ReleaseHdc(hdc);
            }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }

        // ================================================================
        // 書式・補助
        // ================================================================

        /// <summary>一覧用の短い表示（ドライブ直下はそのまま、それ以外は最後のフォルダ名）</summary>
        private static string ShortTaskLabel(BackupTask t)
        {
            static string Short(string p)
            {
                if (string.IsNullOrWhiteSpace(p)) return "（未設定）";
                string trimmed = p.TrimEnd('\\', '/');
                if (trimmed.Length <= 2) return p;   // "E:\" など
                string name = Path.GetFileName(trimmed);
                return string.IsNullOrEmpty(name) ? p : name;
            }
            return $"{Short(t.Source)} → {Short(t.Destination)}";
        }

        private static string TaskLabel(BackupTask t)
        {
            string src = string.IsNullOrWhiteSpace(t.Source) ? "（未設定）" : t.Source;
            string dst = string.IsNullOrWhiteSpace(t.Destination) ? "（未設定）" : t.Destination;
            return $"{src} → {dst}";
        }

        private (BackupResult, TaskResult)? LastResultFor(BackupTask task)
        {
            for (int i = _history.Count - 1; i >= 0; i--)
            {
                var t = _history[i].TaskResults.FirstOrDefault(r =>
                    string.Equals(r.Source, task.Source, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(r.Destination, task.Destination, StringComparison.OrdinalIgnoreCase));
                if (t != null) return (_history[i], t);
            }
            return null;
        }

        private (string Glyph, Color? Color) TaskStatusGlyph(BackupTask task)
        {
            if (string.IsNullOrWhiteSpace(task.Source) || string.IsNullOrWhiteSpace(task.Destination))
                return (Glyph.Circle, Theme.TextMuted);

            var last = LastResultFor(task);
            if (last == null) return (Glyph.Circle, Theme.TextMuted);

            var t = last.Value.Item2;
            if (t.Status is "容量不足" or "エラー" or "延期" || t.Status.StartsWith("スキップ") || t.Errors > 0)
                return (Glyph.Warning, Theme.Warning);
            if (t.Status == "中止")
                return (Glyph.Stop, Theme.TextMuted);
            return (Glyph.Completed, Theme.Success);
        }

        private static string TriggerLabel(string? trigger) => trigger switch
        {
            "auto" => "自動",
            "manual" => "手動",
            _ => "—",   // 旧バージョンの履歴には種別が無い
        };

        private static string FormatBytes(long bytes) => BackupManager.FormatBytes(bytes);

        private static string FormatDuration(TimeSpan d)
        {
            if (d.TotalHours >= 1) return $"{(int)d.TotalHours}時間{d.Minutes}分";
            if (d.TotalMinutes >= 1) return $"{d.Minutes}分{d.Seconds}秒";
            return $"{d.TotalSeconds:F1}秒";
        }
    }
}
