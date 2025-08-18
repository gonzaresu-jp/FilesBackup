namespace FilesBackup.Models
{
    public class BackupTask
    {
        public string Source { get; set; } = "";
        public string Destination { get; set; } = "";

        // 新しく追加：バックアップ実行時刻（24時間形式）
        public TimeSpan? ScheduledTime { get; set; }
    }
}
