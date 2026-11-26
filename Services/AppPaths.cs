using System.Security.AccessControl;
using System.Security.Principal;

namespace FilesBackup.Services
{
    /// <summary>
    /// 設定・ログ・履歴の保存先。
    ///  - 通常：ユーザー単位の %LocalAppData%\FilesBackup
    ///  - 管理者運用モード：全ユーザー共通の %ProgramData%\FilesBackup（設定の変更には管理者権限が必要）
    /// 以前は exe と同じフォルダ（例: C:\Program Files\FilesBackup\Config）に保存していたため、起動時に移行する。
    /// </summary>
    public static class AppPaths
    {
        public static readonly string UserDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilesBackup");

        public static readonly string MachineDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "FilesBackup");

        /// <summary>このファイルがあれば管理者運用モード</summary>
        public static readonly string MachineModeFlagPath = Path.Combine(MachineDataDir, "machine_mode.flag");

        private static readonly string? OverrideDir =
            Environment.GetEnvironmentVariable("FILESBACKUP_DATA_DIR") is { Length: > 0 } d ? d : null;

        /// <summary>保存先を差し替えて動かしている（テスト・画面写真の作成）。この場合はレジストリなど PC 全体の設定に触れない</summary>
        public static bool IsOverridden => OverrideDir != null;

        /// <summary>管理者運用モードか（起動時に決まる。切り替えたらアプリを再起動する）</summary>
        public static readonly bool IsMachineMode = OverrideDir == null && File.Exists(MachineModeFlagPath);

        // 環境変数 FILESBACKUP_DATA_DIR で差し替え可能（テストで実際の設定・履歴を汚さないため）
        public static readonly string DataDir = OverrideDir ?? (IsMachineMode ? MachineDataDir : UserDataDir);

        public static readonly string ConfigDir = Path.Combine(DataDir, "Config");
        public static readonly string LogDir = Path.Combine(DataDir, "Logs");
        public static readonly string HistoryPath = Path.Combine(DataDir, "history.json");

        /// <summary>旧バージョンの保存先（exe と同じフォルダの Config）</summary>
        public static readonly string LegacyConfigDir = Path.Combine(AppContext.BaseDirectory, "Config");

        public static string BackupConfigPath => Path.Combine(ConfigDir, "backup_config.json");
        public static string IgnoreConfigPath => Path.Combine(ConfigDir, "ignore_config.json");
        public static string ScheduleConfigPath => Path.Combine(ConfigDir, "schedule_config.json");
        public static string OptionsConfigPath => Path.Combine(ConfigDir, "options_config.json");

        /// <summary>アプリ自体の設定（常駐・更新）は、管理者運用モードでもユーザーごとに保存する</summary>
        public static string AppSettingsPath => Path.Combine(OverrideDir ?? UserDataDir, "app_settings.json");

        /// <summary>アップデートの結果（更新後の起動時に読んで知らせる）</summary>
        public static string UpdateResultPath => Path.Combine(OverrideDir ?? UserDataDir, "update_result.txt");

        /// <summary>
        /// ネットワークドライブの割り当て（Z: → \\server\share）の控え。
        /// 管理者権限のタスクやサインイン前の実行では割り当てが見えないため、普段の起動時に覚えておく
        /// </summary>
        public static string NetworkDrivesPath => Path.Combine(OverrideDir ?? UserDataDir, "network_drives.json");

        /// <summary>更新前の版の控え</summary>
        public static string InstallBackupDir => Path.Combine(OverrideDir ?? UserDataDir, "InstallBackup");

        private static readonly string[] LegacyFiles =
        {
            "backup_config.json",
            "ignore_config.json",
            "schedule_config.json",
        };

        private static readonly string[] ConfigFiles = LegacyFiles.Append("options_config.json").ToArray();

        /// <summary>
        /// 旧保存先の設定を新しい保存先へコピーする（新しい側に既にあるファイルは上書きしない）。
        /// 旧ファイルは念のため残す。
        /// </summary>
        public static void MigrateLegacyConfig()
        {
            try
            {
                if (!Directory.Exists(LegacyConfigDir))
                    return;

                if (string.Equals(Path.GetFullPath(LegacyConfigDir).TrimEnd('\\'),
                                  Path.GetFullPath(ConfigDir).TrimEnd('\\'),
                                  StringComparison.OrdinalIgnoreCase))
                    return;

                Directory.CreateDirectory(ConfigDir);
                foreach (var name in LegacyFiles)
                {
                    string legacy = Path.Combine(LegacyConfigDir, name);
                    string current = Path.Combine(ConfigDir, name);
                    if (File.Exists(legacy) && !File.Exists(current))
                    {
                        File.Copy(legacy, current);
                        Logger.Info($"設定を移行しました: {legacy} → {current}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("設定の移行に失敗しました", ex);
            }
        }

        /// <summary>
        /// 管理者運用モードにする（管理者権限が必要）。
        /// 今のユーザーの設定と履歴を %ProgramData% にコピーし、ログと履歴は標準権限の自動実行からも書けるようにする。
        /// </summary>
        public static void EnableMachineMode()
        {
            string machineConfig = Path.Combine(MachineDataDir, "Config");
            string machineLogs = Path.Combine(MachineDataDir, "Logs");
            string machineHistory = Path.Combine(MachineDataDir, "history.json");

            Directory.CreateDirectory(machineConfig);
            Directory.CreateDirectory(machineLogs);

            string userConfig = Path.Combine(UserDataDir, "Config");
            foreach (var name in ConfigFiles)
            {
                string from = Path.Combine(userConfig, name);
                if (File.Exists(from))
                    File.Copy(from, Path.Combine(machineConfig, name), overwrite: true);
            }

            string userHistory = Path.Combine(UserDataDir, "history.json");
            if (!File.Exists(machineHistory))
            {
                if (File.Exists(userHistory)) File.Copy(userHistory, machineHistory);
                else File.WriteAllText(machineHistory, "[]");
            }

            // 設定は管理者だけが変更できる（%ProgramData% の既定の権限のまま）。
            // ログと履歴は、標準権限で動く自動実行からも書き込めるようにする。
            var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
            var dirSecurity = new DirectoryInfo(machineLogs).GetAccessControl();
            dirSecurity.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(machineLogs).SetAccessControl(dirSecurity);

            var fileSecurity = new FileInfo(machineHistory).GetAccessControl();
            fileSecurity.AddAccessRule(new FileSystemAccessRule(users, FileSystemRights.Modify, AccessControlType.Allow));
            new FileInfo(machineHistory).SetAccessControl(fileSecurity);

            File.WriteAllText(MachineModeFlagPath, "FilesBackup の設定・ログ・履歴をこのフォルダに保存します。このファイルを削除すると、ユーザー単位の保存先に戻ります。");
        }

        /// <summary>管理者運用モードをやめる（管理者権限が必要）。共通の設定を今のユーザーの保存先にコピーして戻す。</summary>
        public static void DisableMachineMode()
        {
            string machineConfig = Path.Combine(MachineDataDir, "Config");
            string userConfig = Path.Combine(UserDataDir, "Config");
            Directory.CreateDirectory(userConfig);

            foreach (var name in ConfigFiles)
            {
                string from = Path.Combine(machineConfig, name);
                if (File.Exists(from))
                    File.Copy(from, Path.Combine(userConfig, name), overwrite: true);
            }

            File.Delete(MachineModeFlagPath);
        }
    }
}
