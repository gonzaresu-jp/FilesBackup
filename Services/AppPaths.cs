namespace FilesBackup.Services
{
    /// <summary>
    /// 設定・ログ・履歴の保存先。
    /// 以前は exe と同じフォルダ（例: C:\Program Files\FilesBackup\Config）に保存していたが、
    /// 書き込みに管理者権限が必要になるため、ユーザー単位の %LocalAppData%\FilesBackup に移した。
    /// </summary>
    public static class AppPaths
    {
        // 環境変数 FILESBACKUP_DATA_DIR で差し替え可能（テストで実際の設定・履歴を汚さないため）
        public static readonly string DataDir =
            Environment.GetEnvironmentVariable("FILESBACKUP_DATA_DIR") is { Length: > 0 } overridden
                ? overridden
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilesBackup");

        public static readonly string ConfigDir = Path.Combine(DataDir, "Config");
        public static readonly string LogDir = Path.Combine(DataDir, "Logs");
        public static readonly string HistoryPath = Path.Combine(DataDir, "history.json");

        /// <summary>旧バージョンの保存先（exe と同じフォルダの Config）</summary>
        public static readonly string LegacyConfigDir = Path.Combine(AppContext.BaseDirectory, "Config");

        public static string BackupConfigPath => Path.Combine(ConfigDir, "backup_config.json");
        public static string IgnoreConfigPath => Path.Combine(ConfigDir, "ignore_config.json");
        public static string ScheduleConfigPath => Path.Combine(ConfigDir, "schedule_config.json");
        public static string OptionsConfigPath => Path.Combine(ConfigDir, "options_config.json");

        private static readonly string[] MigratedFiles =
        {
            "backup_config.json",
            "ignore_config.json",
            "schedule_config.json",
        };

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
                foreach (var name in MigratedFiles)
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
    }
}
