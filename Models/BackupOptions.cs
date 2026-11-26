namespace FilesBackup.Models
{
    /// <summary>バックアップ動作のオプション（options_config.json）。手動実行・自動実行の両方に適用される。</summary>
    public class BackupOptions
    {
        /// <summary>コピー先にあってコピー元に無いファイルを削除する</summary>
        public bool MirrorMode { get; set; } = false;

        /// <summary>隠し/システム属性のファイル・フォルダをスキップする（タスクのルート自体は対象外）</summary>
        public bool SkipHiddenAndSystem { get; set; } = false;

        /// <summary>ジャンクション/シンボリックリンクを辿る（既定は辿らない）</summary>
        public bool FollowReparsePoints { get; set; } = false;

        /// <summary>時刻だけ新しく内容が同じファイルはコピーしない（SHA-256 で比較）</summary>
        public bool CompareByHash { get; set; } = false;

        /// <summary>並列コピー数。0 は自動（コピー元・コピー先のドライブの種類から決める：HDD は 1、SSD・NAS は 4）</summary>
        public int MaxDegreeOfParallelism { get; set; } = 0;
    }
}
