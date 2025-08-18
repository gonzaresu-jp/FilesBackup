using System.Drawing;
using System.Windows.Forms;

namespace FilesBackup
{
    partial class Form1
    {
        private System.ComponentModel.IContainer components = null;
        private TabControl tabControl;
        private TabPage tabTasks;
        private TabPage tabSchedule;

        private FlowLayoutPanel flowPanel;
        private Button btnAddTask;
        private Button btnSave;
        private Button btnStartBackup;

        private Label lblFrequency;
        private ComboBox cmbFrequency;
        private Label lblScheduleTime;
        private DateTimePicker dtpScheduleTime;
        private CheckedListBox clbWeekDays;
        private Button btnSaveSchedule;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
                components.Dispose();
            base.Dispose(disposing);
        }

        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();

            // タブコントロールとタブ
            tabControl = new TabControl();
            tabTasks = new TabPage("バックアップ設定");
            tabSchedule = new TabPage("スケジュール設定");

            // タスク一覧
            flowPanel = new FlowLayoutPanel
            {
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Location = new Point(170, 10),
                Size = new Size(900, 350)
            };

            // タスク追加・保存・実行ボタン
            btnAddTask = new Button
            {
                Text = "＋追加",
                Location = new Point(10, 10),
                Size = new Size(100, 40)
            };
            btnAddTask.Click += btnAddTask_Click;

            btnSave = new Button
            {
                Text = "保存",
                Location = new Point(620, 370),
                Size = new Size(150, 40)
            };
            btnSave.Click += btnSave_Click;

            btnStartBackup = new Button
            {
                Text = "バックアップ開始",
                Location = new Point(780, 370),
                Size = new Size(150, 40)
            };
            btnStartBackup.Click += btnStartBackup_Click;

            // スケジュール：頻度ラベル
            lblFrequency = new Label
            {
                Text = "実行頻度：",
                Location = new Point(20, 30),
                AutoSize = true
            };

            // スケジュール：頻度選択
            cmbFrequency = new ComboBox
            {
                Location = new Point(100, 25),
                Width = 150,
                DropDownStyle = ComboBoxStyle.DropDownList
            };
            cmbFrequency.Items.AddRange(new string[] { "毎日", "毎週", "起動時" });
            cmbFrequency.SelectedIndex = 0;

            // スケジュール：時刻ラベル
            lblScheduleTime = new Label
            {
                Text = "実行時刻：",
                Location = new Point(20, 70),
                AutoSize = true
            };

            // スケジュール：時刻選択
            dtpScheduleTime = new DateTimePicker
            {
                Format = DateTimePickerFormat.Time,
                ShowUpDown = true,
                Location = new Point(100, 65),
                Width = 100
            };

            // スケジュール：曜日選択（毎週のみ）
            clbWeekDays = new CheckedListBox
            {
                Location = new Point(20, 110),
                Size = new Size(300, 100),
                Visible = false
            };
            clbWeekDays.Items.AddRange(new string[] { "日", "月", "火", "水", "木", "金", "土" });

            // スケジュール：保存ボタン
            btnSaveSchedule = new Button
            {
                Text = "スケジュール保存",
                Location = new Point(20, 220),
                Size = new Size(180, 40)
            };
            btnSaveSchedule.Click += btnSaveSchedule_Click;

            // 頻度選択時の表示切替ロジック
            cmbFrequency.SelectedIndexChanged += (s, e) =>
            {
                string selected = cmbFrequency.SelectedItem?.ToString();
                clbWeekDays.Visible = selected == "毎週";
                dtpScheduleTime.Enabled = selected != "起動時";

                // ★ 追加（初期化中はガードで無視される）
                MarkScheduleModified();
            };

            // 除外設定タブ
            tabIgnore = new TabPage("除外設定");

            lstIgnoreItems = new ListBox
            {
                Location = new Point(20, 20),
                Size = new Size(600, 300)
            };

            btnAddIgnoreFile = new Button
            {
                Text = "ファイルを除外",
                Location = new Point(640, 20),
                Size = new Size(120, 40)
            };
            btnAddIgnoreFile.Click += btnAddIgnoreFile_Click;

            btnAddIgnoreFolder = new Button
            {
                Text = "フォルダを除外",
                Location = new Point(640, 70),
                Size = new Size(120, 40)
            };
            btnAddIgnoreFolder.Click += btnAddIgnoreFolder_Click;

            // 拡張子入力欄
            txtIgnoreExtension = new TextBox
            {
                Location = new Point(20, 340),
                Size = new Size(200, 30),
                PlaceholderText = ".tmp など"
            };

            // 拡張子追加ボタン
            btnAddIgnoreExtension = new Button
            {
                Text = "拡張子を除外",
                Location = new Point(230, 340),
                Size = new Size(120, 30)
            };
            btnAddIgnoreExtension.Click += btnAddIgnoreExtension_Click;

            btnSaveIgnore = new Button
            {
                Text = "保存",
                Location = new Point(640, 130),
                Size = new Size(120, 40)
            };
            btnSaveIgnore.Click += btnSaveIgnore_Click;
            // 除外項目削除ボタンの定義

            btnRemoveIgnoreItem = new Button
            {
                Text = "選択項目を削除",
                Location = new Point(640, 180),
                Size = new Size(120, 40)
            };
            btnRemoveIgnoreItem.Click += btnRemoveIgnoreItem_Click;

            tabIgnore.Controls.Add(lstIgnoreItems);
            tabIgnore.Controls.Add(btnAddIgnoreFile);
            tabIgnore.Controls.Add(btnAddIgnoreFolder);
            tabIgnore.Controls.Add(txtIgnoreExtension);
            tabIgnore.Controls.Add(btnAddIgnoreExtension);
            tabIgnore.Controls.Add(btnSaveIgnore);
            tabIgnore.Controls.Add(btnRemoveIgnoreItem);


            // タブへの配置
            tabTasks.Controls.Add(flowPanel);
            tabTasks.Controls.Add(btnAddTask);
            tabTasks.Controls.Add(btnSave);
            tabTasks.Controls.Add(btnStartBackup);

            tabSchedule.Controls.Add(lblFrequency);
            tabSchedule.Controls.Add(cmbFrequency);
            tabSchedule.Controls.Add(lblScheduleTime);
            tabSchedule.Controls.Add(dtpScheduleTime);
            tabSchedule.Controls.Add(clbWeekDays);
            tabSchedule.Controls.Add(btnSaveSchedule);

            // タブコントロール全体
            tabControl.Controls.Add(tabTasks);
            tabControl.Controls.Add(tabSchedule);
            tabControl.Controls.Add(tabIgnore);
            tabControl.Location = new Point(0, 0);
            tabControl.Size = new Size(960, 480);

            // フォーム設定
            this.ClientSize = new Size(960, 480);
            this.Controls.Add(tabControl);
            this.Name = "Form1";
            this.Text = "Backup Tool";

        }
    }
}
