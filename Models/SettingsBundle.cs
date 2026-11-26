namespace FilesBackup.Models
{
    /// <summary>
    /// 設定のインポート/エクスポート用ファイルの形式（JSON）。
    /// スキーマは README の「設定のインポート/エクスポート」に記載。
    /// </summary>
    public class SettingsBundle
    {
        public const int CurrentSchemaVersion = 1;

        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public DateTime ExportedAt { get; set; } = DateTime.Now;
        public string AppVersion { get; set; } = "";

        public List<BackupTask> Tasks { get; set; } = new();
        public IgnoreConfig Ignore { get; set; } = new();
        public ScheduleConfig? Schedule { get; set; }
        public BackupOptions Options { get; set; } = new();
    }
}
