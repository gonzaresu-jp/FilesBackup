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

            // ��������C�x���g�w��
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

            // �� �����������B�����ȍ~��UI�C�x���g�����_���Ώ�
            _initializing = false;

            // �O�̂��ߎ��o������ɖ߂�
            ResetButtonStyle(btnSaveSchedule);
            ResetButtonStyle(btnSaveIgnore);
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // �A�v���S�̂��I���������A�E�B���h�E������\���ɂ���
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
        // ResetButtonStyle ��u������
        private void ResetButtonStyle(Button button)
        {
            button.FlatStyle = FlatStyle.Standard;
            button.UseVisualStyleBackColor = true;
            button.FlatAppearance.BorderSize = 0;                         // �d�v
            button.FlatAppearance.BorderColor = SystemColors.Control;     // �d�v
            button.ForeColor = SystemColors.ControlText;
            button.Font = new Font(button.Font, FontStyle.Regular);
            button.Invalidate();
        }


        private void InitializeTrayIcon()
        {
            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add("�J��", null, (s, e) => ShowWindow());
            trayMenu.Items.Add("�I��", null, (s, e) =>
            {
                trayIcon.Visible = false;
                trayIcon.Dispose(); // �g���C�A�C�R���𖾎��I�ɔj��
                Environment.Exit(0); // �m���Ƀv���Z�X�I��
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
                MessageBox.Show("�ݒ�t�@�C���̓ǂݍ��݂Ɏ��s���܂���: " + ex.Message);
            }
        }
        private void SaveBackupTasks()
        {
            try
            {
                var tasks = new List<BackupTask>();

                // �� flowPanel.Controls.Cast<Control>().Reverse() ���g���ď������t��
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
                MessageBox.Show("�ݒ��ۑ����܂����B", "����", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("�ݒ�̕ۑ��Ɏ��s���܂���: " + ex.Message);
            }
        }

        private void btnStartBackup_Click(object sender, EventArgs e)
        {
            try
            {
                var manager = new BackupManager();      // �� �錾�͂���1�񂾂�
                manager.LoadFromJson(configPath);

                // �� �g���q���O�𐳂�����������i�ʃf�V���A���C�Y�ł͂Ȃ���p���[�_�[���g���j
                manager.LoadIgnoreList(ignorePath);

                int changedCount = manager.ExecuteAll();
                trayIcon.ShowBalloonTip(
                    5000, "�o�b�N�A�b�v����",
                    $"�ύX���ꂽ�t�@�C�����F{changedCount} ��",
                    ToolTipIcon.Info
                );
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"�o�b�N�A�b�v���ɃG���[���������܂���: {ex.Message}",
                    "�G���[", MessageBoxButtons.OK, MessageBoxIcon.Error
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

            // �� �����ŃC�x���g�w��
            taskControl.OnChanged += (s, ev) => MarkTaskModified();

            // ��ړ��C�x���g
            taskControl.OnMoveUpRequested += (s, ev) =>
            {
                int index = flowPanel.Controls.GetChildIndex(taskControl);
                int minIndex = (taskHeader != null && !taskHeader.IsDisposed) ? 1 : 0;
                if (index > minIndex)
                {
                    flowPanel.Controls.SetChildIndex(taskControl, index - 1);
                    flowPanel.Invalidate();
                    MarkTaskModified(); // �� �ǉ�
                }
            };

            // ���ړ��C�x���g
            taskControl.OnMoveDownRequested += (s, ev) =>
            {
                int index = flowPanel.Controls.GetChildIndex(taskControl);
                if (index < flowPanel.Controls.Count - 1)
                {
                    flowPanel.Controls.SetChildIndex(taskControl, index + 1);
                    flowPanel.Invalidate();
                    MarkTaskModified(); // �� �ǉ�
                }
            };

            // �폜�C�x���g
            taskControl.OnRemoveRequested += (s, ev) =>
            {
                flowPanel.Controls.Remove(taskControl);
                MarkTaskModified(); // �� �ǉ�
            };


            flowPanel.Controls.Add(taskControl);
            if (taskHeader != null && !taskHeader.IsDisposed)
                {
                int headerIndex = flowPanel.Controls.IndexOf(taskHeader);
                flowPanel.Controls.SetChildIndex(taskControl, headerIndex + 1); // �w�b�_�[����
                }
             else
                {
                flowPanel.Controls.SetChildIndex(taskControl, 0);
                }
        }

        private void EnsureTaskHeader()
        {
            if (taskHeader != null && !taskHeader.IsDisposed) return;

            // BackupTaskControl �̃��C�A�E�g�Ƒ�����i��:30 +2 / ��:30 +10 = 72px�j
            // txtSource.Left = 72, txtDestination.Left = 332 �����̔z�u
            const int srcLeft = 72;
            const int dstLeft = 332;

            taskHeader = new Panel
            {
                Width = 820,   // BackupTaskControl �Ɠ�����
                Height = 20,
                Margin = new Padding(0, 0, 0, 4),
                BackColor = Color.Transparent
            };

            var lblSrc = new Label
            {
                AutoSize = true,
                Text = "�R�s�[��",
                Font = new Font(Font, FontStyle.Bold),
                Location = new Point(srcLeft, 0)
            };
            var lblDst = new Label
            {
                AutoSize = true,
                Text = "�R�s�[��",
                Font = new Font(Font, FontStyle.Bold),
                Location = new Point(dstLeft, 0)
            };

            taskHeader.Controls.Add(lblSrc);
            taskHeader.Controls.Add(lblDst);

            // �ǉ����Ĉ�ԏ�iindex=0�j�ɌŒ�
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
                _suppressUiMark = true;                // �� �ǉ��i���[�h���͓_���֎~�j
                if (File.Exists(schedulePath))
                {
                    var json = File.ReadAllText(schedulePath);
                    var saved = JsonSerializer.Deserialize<ScheduleConfig>(json);
                    if (saved != null)
                    {
                        dtpScheduleTime.Value = DateTime.Today.Add(saved.Time); // ����
                        cmbFrequency.SelectedItem = saved.Frequency;            // �p�x

                        if (saved.Frequency == "���T" && saved.DaysOfWeek != null)
                        {
                            for (int i = 0; i < clbWeekDays.Items.Count; i++)
                            {
                                string dayStr = clbWeekDays.Items[i].ToString()!;
                                DayOfWeek day = (DayOfWeek)"�����ΐ��؋��y".IndexOf(dayStr);
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
                MessageBox.Show("�X�P�W���[���ǂݍ��݃G���[: " + ex.Message);
            }
            finally
            {
                _suppressUiMark = false;               // �� ����
            }
        }

        private void btnSaveSchedule_Click(object sender, EventArgs e)
        {
            try
            {
                _suppressUiMark = true; // �� �ۑ����͓_���֎~

                string frequency = cmbFrequency.SelectedItem?.ToString() ?? "����";
                TimeSpan time = dtpScheduleTime.Value.TimeOfDay;

                List<DayOfWeek>? daysOfWeek = null;
                if (frequency == "���T")
                {
                    daysOfWeek = clbWeekDays.CheckedItems
                        .Cast<string>()
                        .Select(d => "�����ΐ��؋��y".Select((c, i) => new { Char = c.ToString(), Day = (DayOfWeek)i })
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

                MessageBox.Show("�X�P�W���[����ۑ����A�^�X�N�ɓo�^���܂����B", "����", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("�X�P�W���[���ۑ����s: " + ex.Message, "�G���[", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                scheduleModified = false;
                _suppressUiMark = false;                 // �� ����
                ResetButtonStyle(btnSaveSchedule);       // �� ����������
            }
        }


        private void RegisterTaskScheduler(string frequency, TimeSpan time, List<DayOfWeek>? daysOfWeek = null)
        {
            string exePath = Application.ExecutablePath;
            string taskName = "FilesBackupAutoTask";

            using (TaskService ts = new TaskService())
            {
                // �Â��^�X�N���폜�i����΁j
                var oldTask = ts.FindTask(taskName);
                if (oldTask != null)
                    ts.RootFolder.DeleteTask(taskName);

                TaskDefinition td = ts.NewTask();
                td.RegistrationInfo.Description = "�����o�b�N�A�b�v�^�X�N";

                // ���������|�C���g�F�u�X�P�W���[���𓦂����炷���Ɏ��s�v�I�v�V����
                td.Settings.StartWhenAvailable = true;
                td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;
                td.Principal.RunLevel = TaskRunLevel.Highest; // �Ǘ��Ҏ��s

                // �g���K�[�ݒ�
                if (frequency == "����")
                {
                    var trigger = new DailyTrigger { DaysInterval = 1, StartBoundary = DateTime.Today + time };
                    td.Triggers.Add(trigger);
                }
                else if (frequency == "���T" && daysOfWeek != null)
                {
                    var trigger = new WeeklyTrigger { StartBoundary = DateTime.Today + time };
                    foreach (var day in daysOfWeek)
                        trigger.DaysOfWeek |= (DaysOfTheWeek)(1 << (int)day);
                    td.Triggers.Add(trigger);
                }
                else if (frequency == "�N����")
                {
                    td.Triggers.Add(new BootTrigger());
                }
                else
                {
                    MessageBox.Show("�X�P�W���[���ݒ肪�s���ł��B", "�G���[", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                // ���s�A�N�V�����ݒ�
                td.Actions.Add(new ExecAction(exePath, "auto", null));

                // �o�^
                ts.RootFolder.RegisterTaskDefinition(taskName, td);
            }
        }

        private void LoadIgnoreList()
        {
            try
            {
                _suppressUiMark = true;                // �� �ǉ��i���[�h���͓_���֎~�j
                if (File.Exists(ignorePath))
                {
                    var config = JsonSerializer.Deserialize<IgnoreConfig>(File.ReadAllText(ignorePath));
                    lstIgnoreItems.Items.Clear();
                    if (config?.Paths != null)
                        lstIgnoreItems.Items.AddRange(config.Paths.ToArray());
                }
                lstIgnoreItems.SelectedIndex = -1;     // �� ���I����
            }
            catch (Exception ex)
            {
                MessageBox.Show("���O�ݒ�̓ǂݍ��݂Ɏ��s���܂���: " + ex.Message);
            }
            finally
            {
                _suppressUiMark = false;               // �� ����
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
                _suppressUiMark = true; // �� �ۑ����͓_���֎~

                var list = new List<string>();
                foreach (var item in lstIgnoreItems.Items)
                    list.Add(item.ToString()!);

                var config = new IgnoreConfig { Paths = list };
                Directory.CreateDirectory(Path.GetDirectoryName(ignorePath)!);
                File.WriteAllText(ignorePath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));

                MessageBox.Show("���O�ݒ��ۑ����܂����B", "����", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("���O�ݒ�̕ۑ��Ɏ��s���܂���: " + ex.Message, "�G���[", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                ignoreModified = false;
                _suppressUiMark = false;            // �� ����
                ResetButtonStyle(btnSaveIgnore);    // �� ����������
            }
        }
        private class ScheduleConfig
        {
            public string Frequency { get; set; } = "����";
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
                MessageBox.Show("�g���q����͂��Ă��������i��F.tmp�j", "���̓G���[", MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
                MessageBox.Show("���łɃ��X�g�ɒǉ�����Ă��܂��B", "�d��", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        private void btnRemoveIgnoreItem_Click(object sender, EventArgs e)
        {
            var selected = lstIgnoreItems.SelectedItem;
            if (selected != null)
            {
                lstIgnoreItems.Items.Remove(selected);
                MarkIgnoreModified(); // �O�̂��߂����ł��ύX���m
            }
            else
            {
                MessageBox.Show("�폜���鍀�ڂ�I�����Ă��������B", "���", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
    }
}
