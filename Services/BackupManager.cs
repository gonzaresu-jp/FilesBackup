using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilesBackup.Models;

namespace FilesBackup.Services
{
    // 進捗通知（現在のタスク内の処理済み件数・総件数・直近のファイル）
    public record BackupProgress(int Done, int Total, string Current);

    public class BackupManager
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilesBackup", "logs");
        private static readonly object LogLock = new();

        private const int MaxCopyAttempts = 3;                                          // IOエラー時の試行回数
        private const int LogRetentionDays = 30;                                        // ログの保持日数
        private const int ProgressReportInterval = 20;                                  // 進捗通知の間引き（件数）
        private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);  // NAS/FAT の時刻精度差を吸収

        // 一時停止の制御。Set のときは実行、Reset のときは待機
        private readonly ManualResetEventSlim _running = new(true);

        public List<BackupTask> Tasks { get; private set; } = new();

        public List<string> IgnorePaths { get; set; } = new();         // 除外パス（ファイル・フォルダ）
        public List<string> IgnoreExtensions { get; set; } = new();    // 除外拡張子（例: .log, .tmp）
        public List<Regex> IgnoreGlobs { get; private set; } = new();  // ワイルドカード除外（例: **/Cache/**）

        // 隠し/システム属性のファイル・フォルダをスキップするか
        public bool SkipHiddenAndSystem { get; set; } = false;

        // ジャンクション/シンボリックリンク（再解析ポイント）を辿るか。既定は辿らない。
        public bool FollowReparsePoints { get; set; } = false;

        // 時刻が新しくても内容が同じならコピーしない（ハッシュで確認）
        public bool CompareByHash { get; set; } = false;

        // 並列コピー数（HDD は 1、SSD や NAS は 2〜4 程度が目安）
        private int _maxDegreeOfParallelism = 2;
        public int MaxDegreeOfParallelism
        {
            get => _maxDegreeOfParallelism;
            set => _maxDegreeOfParallelism = Math.Clamp(value, 1, 8);
        }

        public bool IsPaused => !_running.IsSet;

        public void Pause() => _running.Reset();
        public void Resume() => _running.Set();

        public void LoadFromJson(string jsonPath)
        {
            if (!File.Exists(jsonPath))
                throw new FileNotFoundException("設定ファイルが見つかりません", jsonPath);

            string json = File.ReadAllText(jsonPath);
            Tasks = JsonSerializer.Deserialize<List<BackupTask>>(json) ?? new();
        }

        private class IgnoreConfig
        {
            public List<string> Paths { get; set; } = new();
            public bool SkipHiddenAndSystem { get; set; } = false;
            public bool CompareByHash { get; set; } = false;
            public int MaxDegreeOfParallelism { get; set; } = 2;
        }

        public void LoadIgnoreList(string ignorePath)
        {
            var entries = new List<string>();

            if (File.Exists(ignorePath))
            {
                string json = File.ReadAllText(ignorePath);
                var config = JsonSerializer.Deserialize<IgnoreConfig>(json);

                if (config != null)
                {
                    SkipHiddenAndSystem = config.SkipHiddenAndSystem;
                    CompareByHash = config.CompareByHash;
                    MaxDegreeOfParallelism = config.MaxDegreeOfParallelism;
                    entries = config.Paths.Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                }
            }

            IgnoreExtensions = entries
                .Where(IsExtensionEntry)
                .Select(x => x.TrimStart('*').ToLowerInvariant())
                .ToList();

            IgnoreGlobs = entries
                .Where(x => !IsExtensionEntry(x) && IsGlob(x))
                .Select(GlobToRegex)
                .ToList();

            IgnorePaths = entries
                .Where(x => !IsExtensionEntry(x) && !IsGlob(x))
                .ToList();

            // 強制除外対象（全ドライブ共通）
            var systemFolders = new[]
            {
                "System Volume Information",
                "$RECYCLE.BIN"
            };

            foreach (var drive in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                foreach (var sys in systemFolders)
                {
                    string sysPath = Path.Combine(drive.RootDirectory.FullName, sys);
                    if (!IgnorePaths.Contains(sysPath, StringComparer.OrdinalIgnoreCase))
                        IgnorePaths.Add(sysPath);
                }
            }
        }

        // 設定の問題を列挙する（保存時・実行時の警告用）
        //  - コピー先がコピー元（または同一）の配下 → 無限コピーになるため不可
        //  - コピー先がシステムの保護領域（Windows・Program Files など）や、システムドライブ直下 → 誤操作防止のため不可
        public static List<string> FindConfigErrors(IEnumerable<BackupTask> tasks)
        {
            var errors = new List<string>();
            var protectedDirs = GetProtectedDirectories();
            string systemRoot = Path.GetFullPath(Path.GetPathRoot(Environment.SystemDirectory)!).TrimEnd('\\');

            foreach (var task in tasks)
            {
                if (string.IsNullOrWhiteSpace(task.Source) || string.IsNullOrWhiteSpace(task.Destination))
                    continue;

                try
                {
                    if (IsSameOrInside(task.Destination, task.Source))
                    {
                        errors.Add($"コピー先がコピー元の配下になっています: {task.Source} → {task.Destination}");
                        continue;
                    }

                    if (Path.GetFullPath(task.Destination).TrimEnd('\\').Equals(systemRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        errors.Add($"コピー先がシステムドライブ直下です: {task.Destination}");
                        continue;
                    }

                    string? hit = protectedDirs.FirstOrDefault(p => IsSameOrInside(task.Destination, p));
                    if (hit != null)
                        errors.Add($"コピー先がシステムの保護領域です: {task.Destination}（{hit}）");
                }
                catch (Exception ex)
                {
                    errors.Add($"パスが不正です: {task.Source} → {task.Destination}（{ex.Message}）");
                }
            }
            return errors;
        }

        // dryRun: コピーせず、コピーされる予定の件数だけ数える
        public int ExecuteAll(CancellationToken cancellationToken = default, bool dryRun = false, IProgress<BackupProgress>? progress = null)
        {
            CleanOldLogs();

            int totalCopied = 0;
            var ignores = IgnorePaths.ToList();
            string tag = dryRun ? "[DRY-RUN] " : "";

            foreach (var task in Tasks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (string.IsNullOrWhiteSpace(task.Source) || string.IsNullOrWhiteSpace(task.Destination))
                    {
                        Log("[SKIP] コピー元またはコピー先が空のため、この設定をスキップします");
                        continue;
                    }

                    // コピー元が無い場合は他のタスクを止めずにスキップ
                    if (!Directory.Exists(task.Source))
                    {
                        Log($"[SKIP] コピー元が存在しません: {task.Source}");
                        continue;
                    }

                    var errors = FindConfigErrors(new[] { task });
                    if (errors.Count > 0)
                    {
                        Log($"[SKIP] {errors[0]}");
                        continue;
                    }

                    int copied = CopyTask(task.Source, task.Destination, ignores, cancellationToken, dryRun, progress);
                    totalCopied += copied;
                    Log($"{tag}[{task.Source} → {task.Destination}] {copied} 個{(dryRun ? "コピー予定" : "コピーしました")}");
                }
                catch (OperationCanceledException)
                {
                    Log("[CANCEL] ユーザーによりバックアップを中止しました");
                    throw;
                }
                catch (Exception ex)
                {
                    Log($"[ERROR] {task.Source} → {task.Destination}: {ex.Message}");
                }
            }
            return totalCopied;
        }

        private int CopyTask(string src, string dst, IReadOnlyList<string> ignores, CancellationToken ct, bool dryRun, IProgress<BackupProgress>? progress)
        {
            // 先に対象ファイルを列挙してから並列コピーする（進捗の総件数を出すため）
            var files = EnumerateFilesSafe(src, ignores)
                .Where(file => !IgnoreExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
                .Where(file => !IsExcluded(file, ignores))
                .ToList();

            int copied = 0;
            int done = 0;
            int total = files.Count;

            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                CancellationToken = ct
            };

            try
            {
                Parallel.ForEach(files, options, file =>
                {
                    _running.Wait(ct);              // 一時停止中はここで待つ
                    ct.ThrowIfCancellationRequested();

                    string destPath = Path.Combine(dst, Path.GetRelativePath(src, file));

                    try
                    {
                        if (NeedsCopy(file, destPath))
                        {
                            if (!dryRun)
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                                CopyWithRetry(file, destPath);
                            }
                            Interlocked.Increment(ref copied);
                        }
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        Log($"[SKIP] アクセス拒否: {file} - {ex.Message}");
                    }
                    catch (IOException ex)
                    {
                        Log($"[SKIP] IOエラー: {file} - {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        Log($"[SKIP] 予期せぬエラー: {file} - {ex.Message}");
                    }

                    int d = Interlocked.Increment(ref done);
                    if (progress != null && (d % ProgressReportInterval == 0 || d == total))
                        progress.Report(new BackupProgress(d, total, file));
                });
            }
            catch (AggregateException ae) when (ae.InnerExceptions.All(e => e is OperationCanceledException))
            {
                throw new OperationCanceledException(ct);
            }

            return copied;
        }

        // コピーが必要か：コピー先が無い、またはコピー元が許容誤差より新しい（かつ内容が違う）場合
        private bool NeedsCopy(string src, string dst)
        {
            if (!File.Exists(dst))
                return true;

            var srcInfo = new FileInfo(src);
            var dstInfo = new FileInfo(dst);

            if (srcInfo.LastWriteTimeUtc - dstInfo.LastWriteTimeUtc <= TimestampTolerance)
                return false;

            if (!CompareByHash)
                return true;

            // 時刻だけ新しく内容が同じなら、コピーを省く
            if (srcInfo.Length != dstInfo.Length)
                return true;

            return !HashEquals(src, dst);
        }

        private static bool HashEquals(string a, string b)
        {
            using var fa = File.OpenRead(a);
            using var fb = File.OpenRead(b);
            return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
        }

        // 一時的なIOエラー（ファイルのロード中など）は少し待って再試行する
        private static void CopyWithRetry(string src, string dst)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(src, dst, true);
                    return;
                }
                catch (IOException) when (attempt < MaxCopyAttempts)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(attempt));
                }
            }
        }

        private IEnumerable<string> EnumerateFilesSafe(string root, IReadOnlyList<string> ignores)
        {
            var dirs = new Stack<string>();
            dirs.Push(root);

            while (dirs.Count > 0)
            {
                string currentDir = dirs.Pop();

                // 除外対象ディレクトリならスキップ
                if (IsExcluded(currentDir, ignores))
                    continue;

                string[] files;
                string[] subDirs;
                try
                {
                    files = Directory.GetFiles(currentDir);
                    subDirs = Directory.GetDirectories(currentDir);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
                {
                    // 存在しない・権限がない・パスが長すぎる等は、そのフォルダだけ飛ばして続行
                    Log($"[SKIP] フォルダを読み取れません: {currentDir} - {ex.Message}");
                    continue;
                }

                foreach (var file in files)
                {
                    if (SkipHiddenAndSystem && IsHiddenOrSystem(file))
                        continue;

                    if (!FollowReparsePoints && IsReparsePoint(file))
                    {
                        Log($"[SKIP] ジャンクション/シンボリックリンクを除外: {file}");
                        continue;
                    }
                    yield return file;
                }

                foreach (var dir in subDirs)
                {
                    if (SkipHiddenAndSystem && IsHiddenOrSystem(dir))
                        continue;

                    if (!FollowReparsePoints && IsReparsePoint(dir))
                    {
                        Log($"[SKIP] ジャンクション/シンボリックリンクを除外: {dir}");
                        continue;
                    }
                    dirs.Push(dir);
                }
            }
        }

        // 除外パス（配下を含む）またはワイルドカード除外に一致するか
        private bool IsExcluded(string path, IReadOnlyList<string> ignores)
        {
            if (ignores.Any(ignore => IsSameOrInside(path, ignore)))
                return true;

            if (IgnoreGlobs.Count > 0)
            {
                string normalized = Path.GetFullPath(path).Replace('\\', '/');
                if (IgnoreGlobs.Any(rx => rx.IsMatch(normalized)))
                    return true;
            }
            return false;
        }

        // "*.tmp" や ".tmp" の形式（拡張子指定）か
        private static bool IsExtensionEntry(string entry) =>
            Regex.IsMatch(entry, @"^\*?\.[^\\/*?:]+$");

        private static bool IsGlob(string entry) =>
            entry.IndexOfAny(new[] { '*', '?' }) >= 0;

        // ワイルドカードを正規表現に変換（* は区切り内、** は区切りをまたぐ、? は1文字）
        private static Regex GlobToRegex(string glob)
        {
            string g = glob.Replace('\\', '/');
            var sb = new StringBuilder("^");

            for (int i = 0; i < g.Length; i++)
            {
                char c = g[i];

                if (c == '*' && i + 1 < g.Length && g[i + 1] == '*')
                {
                    if (i + 2 < g.Length && g[i + 2] == '/')
                    {
                        // "**/" は 0 個以上のフォルダ
                        sb.Append("(?:.*/)?");
                        i += 2;
                    }
                    else if (i + 2 == g.Length && sb.Length > 0 && sb[sb.Length - 1] == '/')
                    {
                        // 末尾の "/**" は、そのフォルダ自身とその配下すべて
                        sb.Length--;
                        sb.Append("(?:/.*)?");
                        i++;
                    }
                    else
                    {
                        sb.Append(".*");
                        i++;
                    }
                }
                else if (c == '*')
                {
                    sb.Append("[^/]*");
                }
                else if (c == '?')
                {
                    sb.Append("[^/]");
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }

            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static IEnumerable<string> GetProtectedDirectories()
        {
            var candidates = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
            };
            return candidates.Where(p => !string.IsNullOrWhiteSpace(p));
        }

        // path が parent と同じ、またはその配下にあるか（"C:\foo" と "C:\foobar" を取り違えないよう区切り文字で判定）
        private static bool IsSameOrInside(string path, string parent)
        {
            char[] seps = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };
            string p = Path.GetFullPath(path).TrimEnd(seps);
            string r = Path.GetFullPath(parent).TrimEnd(seps);

            return p.Equals(r, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReparsePoint(string path)
        {
            try
            {
                return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsHiddenOrSystem(string path)
        {
            try
            {
                return (File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) != 0;
            }
            catch
            {
                return false;
            }
        }

        // 保持日数より古いログファイルを削除する（失敗しても続行）
        private static void CleanOldLogs()
        {
            try
            {
                if (!Directory.Exists(LogDir))
                    return;

                DateTime limit = DateTime.Now.AddDays(-LogRetentionDays);
                foreach (var file in Directory.GetFiles(LogDir, "*.log"))
                {
                    if (File.GetLastWriteTime(file) < limit)
                        File.Delete(file);
                }
            }
            catch
            {
                // ログ整理の失敗でバックアップを止めない
            }
        }

        // コンソール（自動実行時は見えない）と日付別ログファイルの両方へ出力（並列実行中も混ざらないよう排他）
        private static void Log(string message)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
            lock (LogLock)
            {
                Console.WriteLine(line);
                try
                {
                    Directory.CreateDirectory(LogDir);
                    File.AppendAllText(Path.Combine(LogDir, $"{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
                }
                catch
                {
                    // ログ書き込み失敗でバックアップ自体を止めない
                }
            }
        }
    }
}
