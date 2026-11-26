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

        /// <summary>
        /// 世代管理：上書き・削除する前のファイルを、コピー先の .FilesBackup-versions\日時\ に移して残す。
        /// 元ファイルが壊れたり暗号化されたりしても、前の版から戻せる
        /// </summary>
        public bool KeepVersions { get; set; } = true;

        /// <summary>残した版を保持する日数（これより古い版は自動で削除する）</summary>
        public int VersionRetentionDays { get; set; } = 30;

        /// <summary>
        /// OneDrive・Google ドライブなどの「オンラインのみ」のファイルもダウンロードしてコピーする。
        /// 既定はスキップ（読むたびにダウンロードが始まり、ディスクと回線を大量に使うため）
        /// </summary>
        public bool DownloadOnlineOnlyFiles { get; set; } = false;

        /// <summary>NAS などで使えない名前（長すぎる名前・末尾のピリオドなど）を、使える名前に変えて保存する</summary>
        public bool FixUnsupportedNames { get; set; } = true;

        /// <summary>コピーした後に、コピー元とコピー先の内容（SHA-256）が一致するか確かめる</summary>
        public bool VerifyAfterCopy { get; set; } = false;
    }
}
