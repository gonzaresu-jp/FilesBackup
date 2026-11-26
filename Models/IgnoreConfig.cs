using System.Text.RegularExpressions;

namespace FilesBackup.Models
{
    /// <summary>
    /// 除外設定（ignore_config.json）。種類ごとに独立したリストで保持する。
    /// 旧形式（Paths に拡張子やワイルドカードが混在）は <see cref="Normalize"/> で自動的に振り分ける。
    /// </summary>
    public class IgnoreConfig
    {
        /// <summary>絶対パス（ファイルまたはフォルダ）。フォルダは配下も含めて除外する。例: F:\steam</summary>
        public List<string> Paths { get; set; } = new();

        /// <summary>拡張子。例: .tmp</summary>
        public List<string> Extensions { get; set; } = new();

        /// <summary>ワイルドカード。* は区切り内、** は区切りをまたぐ、? は1文字。例: **/Cache/**</summary>
        public List<string> Patterns { get; set; } = new();

        /// <summary>どこにあっても除外するフォルダ名。例: .git, node_modules</summary>
        public List<string> FolderNames { get; set; } = new();

        public static bool IsExtension(string entry) =>
            Regex.IsMatch(entry.Trim(), @"^\*?\.[^\\/*?:]+$");

        public static bool IsPattern(string entry) =>
            entry.IndexOfAny(new[] { '*', '?' }) >= 0;

        public static string NormalizeExtension(string entry)
        {
            string e = entry.Trim().TrimStart('*').ToLowerInvariant();
            return e.StartsWith('.') ? e : "." + e;
        }

        /// <summary>各リストを種類ごとに振り分け直し、空要素と重複を取り除いたコピーを返す</summary>
        public IgnoreConfig Normalize()
        {
            var result = new IgnoreConfig();
            var cmp = StringComparer.OrdinalIgnoreCase;

            // Extensions に書かれたものは "bak" のようにドットが無くても拡張子として扱う
            foreach (var raw in Extensions ?? new())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string e = raw.Trim();
                if (e.IndexOfAny(new[] { '\\', '/', ':' }) >= 0 || (IsPattern(e) && !IsExtension(e))) continue;
                string ext = NormalizeExtension(e);
                if (!result.Extensions.Contains(ext, cmp)) result.Extensions.Add(ext);
            }

            // Paths・Patterns は中身を見て振り分ける（旧形式では Paths に拡張子やワイルドカードが混在していた）
            foreach (var raw in (Paths ?? new()).Concat(Patterns ?? new()))
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string e = raw.Trim();

                if (IsExtension(e))
                {
                    string ext = NormalizeExtension(e);
                    if (!result.Extensions.Contains(ext, cmp)) result.Extensions.Add(ext);
                }
                else if (IsPattern(e))
                {
                    if (!result.Patterns.Contains(e, cmp)) result.Patterns.Add(e);
                }
                else if (!result.Paths.Contains(e, cmp))
                {
                    result.Paths.Add(e);
                }
            }

            foreach (var raw in FolderNames ?? new())
            {
                if (string.IsNullOrWhiteSpace(raw)) continue;
                string name = raw.Trim().Trim('\\', '/');
                if (!result.FolderNames.Contains(name, cmp)) result.FolderNames.Add(name);
            }

            return result;
        }
    }
}
