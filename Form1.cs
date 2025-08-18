using System.Text.Json;
using FilesBackup.Models;
using FilesBackup.Services;
using Microsoft.Win32.TaskScheduler;

namespace FilesBackup
{
    public partial class Form1 : Form
    {
        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private readonly string configPath;
        private readonly string schedulePath;
        private readonly string ignorePath;


        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;

        private TabPage tabIgnore;
        private ListBox lstIgnoreItems;
        private Button btnAddIgnoreFile;
        private Button btnAddIgnoreFolder;
        private Button btnAddIgnoreExtension;
        private Button btnSaveIgnore;
        private TextBox txtIgnoreExtension;
        private Button btnRemoveIgnoreItem;

        private bool taskModified = false;
        private bool scheduleModified = false;
        private bool ignoreModified = false;

        private Color defaultButtonColor;

        private bool _initializing = true;
        private bool _suppressUiMark = false;

        private Panel? taskHeader;



        public Form1()
        {
            configPath = Path.Combine(BaseDir, "Config", "backup_config.json");
            schedulePath = Path.Combine(BaseDir, "Config", "schedule_config.json");
            ignorePath = Path.Combine(BaseDir, "Config", "ignore_config.json");

            _initializing = true;
            InitializeComponent();
            EnsureTaskHeader();
            InitializeTrayIcon();
            LoadBackupTasks();
            LoadSchedule();
            LoadIgnoreList();
            _initializing = false;

            defaultButtonColor = btnSave.BackColor;

            // ここからイベント購読
            flowPanel.ControlAdded += (s, e) => MarkTaskModified();
            flowPanel.ControlRemoved += (s, e) => MarkTaskModified();

            cmbFrequency.SelectedIndexChanged += (s, e) => MarkScheduleModified();
            dtpScheduleTime.ValueChanged += (s, e) => MarkScheduleModified();
            clbWeekDays.ItemCheck += (s, e) => MarkScheduleModified();

            lstIgnoreItems.SelectedIndexChanged += (s, e) => MarkIgnoreModified();
            btnAddIgnoreFile.Click += (s, e) => MarkIgnoreModified();
            btnAddIgnoreFolder.Click += (s, e) => MarkIgnoreModified();
            btnAddIgnoreExtension.Click += (s, e) => MarkIgnoreModified();
            btnRemoveIgnoreItem.Click += (s, e) => MarkIgnoreModified();

            // ★ 初期化完了。ここ以降のUIイベントだけ点灯対象
            _initializing = false;

            // 念のため視覚を既定に戻す
            ResetButtonStyle(btnSaveSchedule);
            ResetButtonStyle(btnSaveIgnore);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // アプリ全体を終了させず、ウィンドウだけ非表示にする
            e.Cancel = true;
            this.Hide();
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
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = ColorTranslator.FromHtml("#774433"); ;
            button.FlatAppearance.BorderSize = 2;
            button.ForeColor = ColorTranslator.FromHtml("#AA8833"); ;
            button.Font = new Font(button.Font, FontStyle.Bold);
        }
        // ResetButtonStyle を置き換え
        private void ResetButtonStyle(Button button)
        {
            button.FlatStyle = FlatStyle.Standard;
            button.UseVisualStyleBackColor = true;
            button.FlatAppearance.BorderSize = 0;                         // 重要
            button.FlatAppearance.BorderColor = SystemColors.Control;     // 重要
            button.ForeColor = SystemColors.ControlText;
            button.Font = new Font(button.Font, FontStyle.Regular);
            button.Invalidate();
        }


        private void InitializeTrayIcon()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("開く", null, (s, e) => ShowWindow());
            trayMenu.Items.Add("終了", null, (s, e) =>
            {
                trayIcon.Visible = false;
                trayIcon.Dispose(); // トレイアイコンを明示的に破棄
                Environment.Exit(0); // 確実にプロセス終了
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

        private void LoadBackupTasks()
        {
            try
            {
                if (File.Exists(configPath))
                {
                    string json = File.ReadAllText(configPath);
                    var tasks = JsonSerializer.Deserialize<List<BackupTask>>(json);
                    if (tasks != null)
                    {
                        foreach (var task in tasks)
                        {
                            AddTaskControl(task.Source, task.Destination, bypassBlankGuard: true);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("設定ファイルの読み込みに失敗しました: " + ex.Message);
            }
        }
        private void SaveBackupTasks()
        {
            try
            {
                var tasks = new List<BackupTask>();

                // ▼ flowPanel.Controls.Cast<Control>().Reverse() を使って順序を逆に
                foreach (Control ctrl in flowPanel.Controls.Cast<Control>().Reverse())
                {
                    if (ctrl is BackupTaskControl taskControl)
                    {
                        var source = taskControl.Source;
                        var destination = taskControl.Destination;

                        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(destination))
                            continue;

                        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (!tasks.Exists(t =>
                            t.Source.Equals(source, StringComparison.OrdinalIgnoreCase) &&
                            t.Destination.Equals(destination, StringComparison.OrdinalIgnoreCase)))
                        {
                            tasks.Add(new BackupTask { Source = source, Destination = destination });
                        }
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(configPath, JsonSerializer.Serialize(tasks, new JsonSerializerOptions { WriteIndented = true }));
                MessageBox.Show("設定を保存しました。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("設定の保存に失敗しました: " + ex.Message);
            }
        }

        private void btnStartBackup_Click(object sender, EventArgs e)
        {
            try
            {
                var manager = new BackupManager();      // ★ 宣言はこれ1回だけ
                manager.LoadFromJson(configPath);

                // ★ 拡張子除外を正しく効かせる（個別デシリアライズではなく専用ローダーを使う）
                manager.LoadIgnoreList(ignorePath);

                int changedCount = manager.ExecuteAll();
                trayIcon.ShowBalloonTip(
                    5000, "バックアップ完了",
                    $"変更されたファイル数：{changedCount} 件",
                    ToolTipIcon.Info
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"バックアップ中にエラーが発生しました: {ex.Message}",
                    "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error
                );
            }
        }

        private void btnAddTask_Click(object sender, EventArgs e)
        {
            AddTaskControl();
        }

        private void AddTaskControl(string source = "", string destination = "", bool bypassBlankGuard = false)
        {
            if (!bypassBlankGuard)
            {
                foreach (Control ctrl in flowPanel.Controls)
                {
                    if (ctrl is BackupTaskControl existingControl)
                    {
                        if (string.IsNullOrWhiteSpace(existingControl.Source) &&
                            string.IsNullOrWhiteSpace(existingControl.Destination))
                        {
                            return;
                        }
                    }
                }
            }

            var taskControl = new BackupTaskControl();

            if (!string.IsNullOrEmpty(source) || !string.IsNullOrEmpty(destination))
            {
                taskControl.SetTask(new BackupTask { Source = source, Destination = destination }, silent: true);
            }

            // ★ ここでイベント購読
            taskControl.OnChanged += (s, ev) => MarkTaskModified();

            // 上移動イベント
            taskControl.OnMoveUpRequested += (s, ev) =>
            {
                int index = flowPanel.Controls.GetChildIndex(taskControl);
                int minIndex = (taskHeader != null && !taskHeader.IsDisposed) ? 1 : 0;
                if (index > minIndex)
                {
                    flowPanel.Controls.SetChildIndex(taskControl, index - 1);
                    flowPanel.Invalidate();
                    MarkTaskModified(); // ★ 追加
                }
            };

            // 下移動イベント
            taskControl.OnMoveDownRequested += (s, ev) =>
            {
                int index = flowPanel.Controls.GetChildIndex(taskControl);
                if (index < flowPanel.Controls.Count - 1)
                {
                    flowPanel.Controls.SetChildIndex(taskControl, index + 1);
                    flowPanel.Invalidate();
                    MarkTaskModified(); // ★ 追加
                }
            };

            // 削除イベント
            taskControl.OnRemoveRequested += (s, ev) =>
            {
                flowPanel.Controls.Remove(taskControl);
                MarkTaskModified(); // ★ 追加
            };


            flowPanel.Controls.Add(taskControl);
            if (taskHeader != null && !taskHeader.IsDisposed)
                {
                int headerIndex = flowPanel.Controls.IndexOf(taskHeader);
                flowPanel.Controls.SetChildIndex(taskControl, headerIndex + 1); // ヘッダー直下
                }
             else
                {
                flowPanel.Controls.SetChildIndex(taskControl, 0);
                }
        }

        private void EnsureTaskHeader()
        {
            if (taskHeader != null && !taskHeader.IsDisposed) return;

            // BackupTaskControl のレイアウトと揃える（↑:30 +2 / ↓:30 +10 = 72px）
            // txtSource.Left = 72, txtDestination.Left = 332 が今の配置
            const int srcLeft = 72;
            const int dstLeft = 332;

            taskHeader = new Panel
            {
                Width = 820,   // BackupTaskControl と同じ幅
                Height = 20,
                Margin = new Padding(0, 0, 0, 4),
                BackColor = Color.Transparent
            };

            var lblSrc = new Label
            {
                AutoSize = true,
                Text = "コピー元",
                Font = new Font(Font, FontStyle.Bold),
                Location = new Point(srcLeft, 0)
            };
            var lblDst = new Label
            {
                AutoSize = true,
                Text = "コピー先",
                Font = new Font(Font, FontStyle.Bold),
                Location = new Point(dstLeft, 0)
            };

            taskHeader.Controls.Add(lblSrc);
            taskHeader.Controls.Add(lblDst);

            // 追加して一番上（index=0）に固定
            flowPanel.Controls.Add(taskHeader);
            flowPanel.Controls.SetChildIndex(taskHeader, 0);
        }


        private void btnSave_Click(object sender, EventArgs e)
        {
            SaveBackupTasks();
            taskModified = false;
            ResetButtonStyle(btnSave);
        }

        private void LoadSchedule()
        {
            try
            {
                _suppressUiMark = true;                // ← 追加（ロード中は点灯禁止）
                if (File.Exists(schedulePath))
                {
                    var json = File.ReadAllText(schedulePath);
                    var saved = JsonSerializer.Deserialize<ScheduleConfig>(json);
                    if (saved != null)
                    {
                        dtpScheduleTime.Value = DateTime.Today.Add(saved.Time); // 時刻
                        cmbFrequency.SelectedItem = saved.Frequency;            // 頻度

                        if (saved.Frequency == "毎週" && saved.DaysOfWeek != null)
                        {
                            for (int i = 0; i < clbWeekDays.Items.Count; i++)
                            {
                                string dayStr = clbWeekDays.Items[i].ToString()!;
                                DayOfWeek day = (DayOfWeek)"日月火水木金土".IndexOf(dayStr);
                                clbWeekDays.SetItemChecked(i, saved.DaysOfWeek.Contains(day));
                            }
                        }
                        else
                        {
                            for (int i = 0; i < clbWeekDays.Items.Count; i++)
                                clbWeekDays.SetItemChecked(i, false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("スケジュール読み込みエラー: " + ex.Message);
            }
            finally
            {
                _suppressUiMark = false;               // ← 解除
            }
        }

        private void btnSaveSchedule_Click(object sender, EventArgs e)
        {
            try
            {
                _suppressUiMark = true; // ← 保存中は点灯禁止

                string frequency = cmbFrequency.SelectedItem?.ToString() ?? "毎日";
                TimeSpan time = dtpScheduleTime.Value.TimeOfDay;

                List<DayOfWeek>? daysOfWeek = null;
                if (frequency == "毎週")
                {
                    daysOfWeek = clbWeekDays.CheckedItems
                        .Cast<string>()
                        .Select(d => "日月火水木金土".Select((c, i) => new { Char = c.ToString(), Day = (DayOfWeek)i })
                        .First(pair => d.StartsWith(pair.Char)).Day)
                        .ToList();
                }

                var schedule = new ScheduleConfig
                {
                    Frequency = frequency,
                    Time = time,
                    DaysOfWeek = daysOfWeek
                };

                Directory.CreateDirectory(Path.GetDirectoryName(schedulePath)!);
                File.WriteAllText(schedulePath, JsonSerializer.Serialize(schedule, new JsonSerializerOptions { WriteIndented = true }));

                RegisterTaskScheduler(frequency, time, daysOfWeek);

                MessageBox.Show("スケジュールを保存し、タスクに登録しました。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("スケジュール保存失敗: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                scheduleModified = false;
                _suppressUiMark = false;                 // ← 解除
                ResetButtonStyle(btnSaveSchedule);       // ← 正しく消灯
            }
        }


        private void RegisterTaskScheduler(string frequency, TimeSpan time, List<DayOfWeek>? daysOfWeek = null)
        {
            string exePath = Application.ExecutablePath;
            string taskName = "FilesBackupAutoTask";

            using (TaskService ts = new TaskService())
            {
                // 古いタスクを削除（あれば）
                var oldTask = ts.FindTask(taskName);
                if (oldTask != null)
                    ts.RootFolder.DeleteTask(taskName);

                TaskDefinition td = ts.NewTask();
                td.RegistrationInfo.Description = "自動バックアップタスク";

                // ★ここがポイント：「スケジュールを逃したらすぐに実行」オプション
                td.Settings.StartWhenAvailable = true;
                td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
                td.Principal.RunLevel = TaskRunLevel.Highest; // 管理者実行

                // トリガー設定
                if (frequency == "毎日")
                {
                    var trigger = new DailyTrigger { DaysInterval = 1, StartBoundary = DateTime.Today + time };
                    td.Triggers.Add(trigger);
                }
                else if (frequency == "毎週" && daysOfWeek != null)
                {
                    var trigger = new WeeklyTrigger { StartBoundary = DateTime.Today + time };
                    foreach (var day in daysOfWeek)
                        trigger.DaysOfWeek |= (DaysOfTheWeek)(1 << (int)day);
                    td.Triggers.Add(trigger);
                }
                else if (frequency == "起動時")
                {
                    td.Triggers.Add(new BootTrigger());
                }
                else
                {
                    MessageBox.Show("スケジュール設定が不正です。", "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // 実行アクション設定
                td.Actions.Add(new ExecAction(exePath, "auto", null));

                // 登録
                ts.RootFolder.RegisterTaskDefinition(taskName, td);
            }
        }

        private void LoadIgnoreList()
        {
            try
            {
                _suppressUiMark = true;                // ← 追加（ロード中は点灯禁止）
                if (File.Exists(ignorePath))
                {
                    var config = JsonSerializer.Deserialize<IgnoreConfig>(File.ReadAllText(ignorePath));
                    lstIgnoreItems.Items.Clear();
                    if (config?.Paths != null)
                        lstIgnoreItems.Items.AddRange(config.Paths.ToArray());
                }
                lstIgnoreItems.SelectedIndex = -1;     // ← 無選択に
            }
            catch (Exception ex)
            {
                MessageBox.Show("除外設定の読み込みに失敗しました: " + ex.Message);
            }
            finally
            {
                _suppressUiMark = false;               // ← 解除
            }
        }

        private void btnAddIgnoreFile_Click(object sender, EventArgs e)
        {
            using var dlg = new OpenFileDialog();
            if (dlg.ShowDialog() == DialogResult.OK)
                lstIgnoreItems.Items.Add(dlg.FileName);
        }

        private void btnAddIgnoreFolder_Click(object sender, EventArgs e)
        {
            using var dlg = new FolderBrowserDialog();
            if (dlg.ShowDialog() == DialogResult.OK)
                lstIgnoreItems.Items.Add(dlg.SelectedPath);
        }

        private void btnSaveIgnore_Click(object sender, EventArgs e)
        {
            try
            {
                _suppressUiMark = true; // ← 保存中は点灯禁止

                var list = new List<string>();
                foreach (var item in lstIgnoreItems.Items)
                    list.Add(item.ToString()!);

                var config = new IgnoreConfig { Paths = list };
                Directory.CreateDirectory(Path.GetDirectoryName(ignorePath)!);
                File.WriteAllText(ignorePath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

                MessageBox.Show("除外設定を保存しました。", "完了", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("除外設定の保存に失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                ignoreModified = false;
                _suppressUiMark = false;            // ← 解除
                ResetButtonStyle(btnSaveIgnore);    // ← 正しく消灯
            }
        }
        private class ScheduleConfig
        {
            public string Frequency { get; set; } = "毎日";
            public TimeSpan Time { get; set; }
            public List<DayOfWeek>? DaysOfWeek { get; set; }
        }

        private class IgnoreConfig
        {
            public List<string> Paths { get; set; } = new();
        }
        private void btnAddIgnoreExtension_Click(object sender, EventArgs e)
        {
            string ext = txtIgnoreExtension.Text.Trim();

            if (string.IsNullOrEmpty(ext))
            {
                MessageBox.Show("拡張子を入力してください（例：.tmp）", "入力エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!ext.StartsWith("."))
            {
                ext = "." + ext;
            }

            if (!lstIgnoreItems.Items.Contains(ext))
            {
                lstIgnoreItems.Items.Add(ext);
                txtIgnoreExtension.Clear();
            }
            else
            {
                MessageBox.Show("すでにリストに追加されています。", "重複", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        private void btnRemoveIgnoreItem_Click(object sender, EventArgs e)
        {
            var selected = lstIgnoreItems.SelectedItem;
            if (selected != null)
            {
                lstIgnoreItems.Items.Remove(selected);
                MarkIgnoreModified(); // 念のためここでも変更検知
            }
            else
            {
                MessageBox.Show("削除する項目を選択してください。", "情報", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
