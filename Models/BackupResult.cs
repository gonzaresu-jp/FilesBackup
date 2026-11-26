namespace FilesBackup.Models
{
    public class BackupResult
    {
        public DateTime Timestamp { get; set; }
        public TimeSpan Duration { get; set; }

        /// <summary>"auto"（スケジュール実行）または "manual"（画面から実行）。旧バージョンの履歴では空</summary>
        public string Trigger { get; set; } = "";

        /// <summary>ユーザーにより中止された</summary>
        public bool Cancelled { get; set; }

        public int FilesScanned { get; set; }
        public int FilesCopied { get; set; }
        public int FilesSkipped { get; set; }
        public int FilesDeleted { get; set; }
        public long BytesCopied { get; set; }
        public int Errors { get; set; }

        /// <summary>エラー内容（多すぎると履歴が肥大化するため先頭の一部のみ保存）</summary>
        public List<string> ErrorMessages { get; set; } = new();
        public List<TaskResult> TaskResults { get; set; } = new();
    }

    public class TaskResult
    {
        public string Source { get; set; } = "";
        public string Destination { get; set; } = "";

        /// <summary>完了 / スキップ / 中止 / エラー</summary>
        public string Status { get; set; } = "";

        /// <summary>スキップやエラーの理由</summary>
        public string Message { get; set; } = "";

        public int FilesScanned { get; set; }
        public int FilesCopied { get; set; }
        public int FilesSkipped { get; set; }
        public int FilesDeleted { get; set; }
        public long BytesCopied { get; set; }
        public int Errors { get; set; }
    }
}
