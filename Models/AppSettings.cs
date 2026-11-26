namespace FilesBackup.Models
{
    /// <summary>アプリ自体の設定（app_settings.json）。ユーザーごとに保存する（管理者運用モードでも共通化しない）</summary>
    public class AppSettings
    {
        /// <summary>Windows のサインイン時に起動して、タスクトレイに常駐する</summary>
        public bool StartWithWindows { get; set; } = true;

        /// <summary>新しいバージョンを自動で確認する（1日1回）</summary>
        public bool AutoCheckUpdates { get; set; } = true;

        /// <summary>最後に更新を確認した日時</summary>
        public DateTime? LastUpdateCheck { get; set; }

        /// <summary>「今回はスキップ」したバージョン（このバージョンは自動では知らせない）</summary>
        public string? SkippedVersion { get; set; }
    }
}
