using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilesBackup.Models;

namespace FilesBackup.Services
{
    public enum BackupPhase { Scanning, Copying, Mirroring }

    /// <summary>進捗通知（タスク単位）</summary>
    public record BackupProgress(
        BackupPhase Phase,
        string TaskLabel,
        int Scanned,
        int EstimatedTotal,
        int Copied,
        long BytesCopied,
        long BytesPlanned,
        int Deleted,
        string Current);

    public record ValidationReport(List<string> Errors, List<string> Warnings);

    public class BackupManager
    {
        private const int MaxCopyAttempts = 3;                              // 一時的なIOエラーの試行回数
        private const int ChunkSize = 100_000;                              // まとめてコピーする件数（メモリ使用量の上限）
        private const long FreeSpaceMargin = 512L * 1024 * 1024;            // 空き容量の余裕
        private const int MaxStoredErrors = 1000;                           // 保持するエラーメッセージ数
        private const int MaxLoggedErrorsPerTask = 200;                     // 1つの設定でログに詳細を書くエラー数
        private static readonly TimeSpan TimestampTolerance = TimeSpan.FromSeconds(2);   // NAS/FAT の時刻精度差を吸収
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(200);

        // 各ドライブ直下で常に除外するもの（システムが使用中でコピーできない・コピーしても意味がない）
        private static readonly string[] ForcedRootFolders = { "System Volume Information", "$RECYCLE.BIN" };
        private static readonly string[] ForcedRootFiles = { "pagefile.sys", "hiberfil.sys", "swapfile.sys", "DumpStack.log.tmp", "DumpStack.log" };
        private static readonly HashSet<string> ForcedRootNames = new(ForcedRootFolders.Concat(ForcedRootFiles), StringComparer.OrdinalIgnoreCase);

        /// <summary>世代管理で前の版を残すフォルダ（コピー先の直下）。コピー元にあってもコピーせず、ミラーでも消さない</summary>
        public const string VersionsFolderName = ".FilesBackup-versions";
        private const string VersionStampFormat = "yyyy-MM-dd_HHmmss";

        // OneDrive・Google ドライブなどの「オンラインのみ」のファイル（読むとダウンロードが始まる）
        private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
        private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

        // ネットワークが切れたときに再接続を待つ時間と、1つの設定で再接続する回数の上限
        private static readonly TimeSpan ReconnectWait = TimeSpan.FromMinutes(5);
        private const int MaxReconnects = 5;

        // 再試行する価値のあるIOエラー（共有違反・ロック・ネットワーク一時障害）
        private static readonly HashSet<int> RetryableWin32Errors = new() { 32, 33, 59, 64, 121 };

        private static readonly EnumerationOptions ListOptions = new()
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = false,
            AttributesToSkip = 0,           // 既定では隠し/システムが飛ばされるため、明示的に全件取得する
            ReturnSpecialDirectories = false,
        };

        private readonly ManualResetEventSlim _running = new(true);
        private readonly ConcurrentQueue<string> _errorMessages = new();
        private int _errorCount;
        private long _lastProgressTicks;

        // ネットワークの切断（設定ごとにリセット）
        private readonly object _reconnectGate = new();
        private volatile bool _connectionLost;
        private int _reconnects;

        // 除外ルール（ApplyIgnore で組み立てる）
        private IgnoreConfig _ignore = new();
        private HashSet<string> _ignorePathSet = new(StringComparer.OrdinalIgnoreCase);
        private List<string> _ignorePathList = new();
        private HashSet<string> _ignoreExtensions = new(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _ignoreFolderNames = new(StringComparer.OrdinalIgnoreCase);
        private List<Regex> _ignoreGlobs = new();

        public List<BackupTask> Tasks { get; set; } = new();
        public BackupOptions Options { get; set; } = new();
        public IgnoreConfig Ignore => _ignore;

        /// <summary>
        /// 開始時にネットワークドライブへ接続できない場合に待つ時間。
        /// 自動実行では NAS の起動待ちなどのため数分待つ。画面からの実行では待たずに「延期」にする
        /// </summary>
        public TimeSpan NetworkWait { get; set; } = TimeSpan.Zero;

        /// <summary>名前の変換を、コピー先の種類によらず強制する（テスト用。null ならネットワーク上のコピー先だけ変換）</summary>
        public bool? ConvertNamesOverride { get; set; }

        public bool IsPaused => !_running.IsSet;
        public void Pause() => _running.Reset();
        public void Resume() => _running.Set();

        public BackupManager()
        {
            ApplyIgnore(new IgnoreConfig());
        }

        // ================= 設定の読み込み =================

        public void LoadFromJson(string jsonPath)
        {
            if (!File.Exists(jsonPath))
                throw new FileNotFoundException("設定ファイルが見つかりません", jsonPath);
            string json = File.ReadAllText(jsonPath);
            Tasks = JsonSerializer.Deserialize<List<BackupTask>>(json) ?? new();
        }

        public void LoadIgnoreList(string ignorePath)
        {
            IgnoreConfig config = new();
            if (File.Exists(ignorePath))
            {
                config = JsonSerializer.Deserialize<IgnoreConfig>(File.ReadAllText(ignorePath),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
            }
            ApplyIgnore(config);
        }

        public void ApplyIgnore(IgnoreConfig config)
        {
            _ignore = config.Normalize();

            var paths = new List<string>();
            foreach (var p in _ignore.Paths)
            {
                // ネットワークドライブは UNC パスでも一致するよう、両方の形で持つ
                try { paths.Add(NormalizePath(p)); } catch { /* 不正なパスは無視 */ }
                try { paths.Add(Path.GetFullPath(p).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)); } catch { }
            }

            foreach (var drive in SafeReadyDrives())
            {
                foreach (var name in ForcedRootFolders.Concat(ForcedRootFiles))
                    paths.Add(NormalizePath(Path.Combine(drive, name)));
            }

            _ignorePathList = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _ignorePathSet = new HashSet<string>(_ignorePathList, StringComparer.OrdinalIgnoreCase);
            _ignoreExtensions = new HashSet<string>(_ignore.Extensions, StringComparer.OrdinalIgnoreCase);
            _ignoreFolderNames = new HashSet<string>(_ignore.FolderNames, StringComparer.OrdinalIgnoreCase);
            _ignoreGlobs = _ignore.Patterns.Select(GlobToRegex).ToList();
        }

        private static IEnumerable<string> SafeReadyDrives()
        {
            var roots = new List<string>();
            try
            {
                foreach (var d in DriveInfo.GetDrives())
                {
                    try { if (d.IsReady) roots.Add(d.RootDirectory.FullName); } catch { }
                }
            }
            catch { }
            return roots;
        }

        // ================= 事前チェック =================

        /// <summary>
        /// 危険な設定を列挙する（保存時・実行時に使用）。
        ///  - コピー先がコピー元（または同一）の配下 → 無限コピーになる
        ///  - コピー先がシステムドライブ直下や Windows・Program Files → 誤操作でシステムを壊す恐れ
        /// </summary>
        public static List<string> FindConfigErrors(IEnumerable<BackupTask> tasks)
        {
            var errors = new List<string>();
            var protectedDirs = GetProtectedDirectories().ToList();
            string systemRoot = NormalizePath(Path.GetPathRoot(Environment.SystemDirectory)!);

            foreach (var task in tasks)
            {
                if (task == null || string.IsNullOrWhiteSpace(task.Source) || string.IsNullOrWhiteSpace(task.Destination))
                    continue;

                try
                {
                    if (IsSameOrInside(task.Destination, task.Source))
                    {
                        errors.Add($"コピー先がコピー元の配下になっています: {task.Source} → {task.Destination}");
                        continue;
                    }

                    if (NormalizePath(task.Destination).Equals(systemRoot, StringComparison.OrdinalIgnoreCase))
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

        /// <summary>
        /// ミラーモードを適用すると危険なタスクを列挙する。
        /// コピー先が他のタスクのコピー元・コピー先や、自分のコピー元と重なっていると、
        /// 「コピー元に無いファイル」として他のデータを消してしまうため。
        /// </summary>
        public static List<string> FindMirrorConflicts(IReadOnlyList<BackupTask> tasks, out HashSet<int> blocked)
        {
            var messages = new List<string>();
            blocked = new HashSet<int>();

            for (int i = 0; i < tasks.Count; i++)
            {
                var a = tasks[i];
                if (a == null || string.IsNullOrWhiteSpace(a.Source) || string.IsNullOrWhiteSpace(a.Destination))
                    continue;

                try
                {
                    if (Overlaps(a.Destination, a.Source))
                    {
                        blocked.Add(i);
                        messages.Add($"コピー元とコピー先が重なっています: {a.Source} → {a.Destination}");
                        continue;
                    }

                    for (int j = 0; j < tasks.Count; j++)
                    {
                        if (i == j) continue;
                        var b = tasks[j];
                        if (b == null || string.IsNullOrWhiteSpace(b.Source) || string.IsNullOrWhiteSpace(b.Destination))
                            continue;

                        if (Overlaps(a.Destination, b.Destination))
                        {
                            blocked.Add(i);
                            messages.Add($"コピー先が他の設定のコピー先と重なっています: {a.Destination} と {b.Destination}");
                            break;
                        }
                        if (Overlaps(a.Destination, b.Source))
                        {
                            blocked.Add(i);
                            messages.Add($"コピー先が他の設定のコピー元と重なっています: {a.Destination} と {b.Source}");
                            break;
                        }
                    }
                }
                catch
                {
                    blocked.Add(i);
                }
            }
            return messages.Distinct().ToList();
        }

        /// <summary>実行前の検証（コピー元の有無、書き込み可否、ドライブの有無、ミラーの安全性など）</summary>
        public ValidationReport Validate()
        {
            var errors = FindConfigErrors(Tasks);
            var warnings = new List<string>();

            if (Tasks.Count == 0)
                warnings.Add("バックアップ設定がありません。");

            foreach (var task in Tasks)
            {
                if (string.IsNullOrWhiteSpace(task.Source) || string.IsNullOrWhiteSpace(task.Destination))
                {
                    warnings.Add("コピー元またはコピー先が空の設定があります（実行時はスキップします）。");
                    continue;
                }

                try
                {
                    string src = NetworkPaths.Resolve(task.Source);
                    string dst = NetworkPaths.Resolve(task.Destination);

                    if (NetworkPaths.IsUnc(src) && !NetworkPaths.IsReachable(src, out string srcReason))
                        warnings.Add($"コピー元に接続できません（実行時は延期します）: {task.Source}（{srcReason}）");
                    else if (!Directory.Exists(src))
                        warnings.Add($"コピー元が見つかりません（実行時はスキップします）: {task.Source}");

                    if (!NetworkPaths.IsReachable(dst, out string dstReason))
                        errors.Add($"コピー先に接続できません（実行時は延期します）: {task.Destination}（{dstReason}）");
                    else if (!CanWrite(dst, out string reason))
                        errors.Add($"コピー先に書き込めません: {task.Destination}（{reason}）");
                }
                catch (Exception ex)
                {
                    errors.Add($"パスが不正です: {task.Source} → {task.Destination}（{ex.Message}）");
                }
            }

            if (Options.MirrorMode)
            {
                foreach (var m in FindMirrorConflicts(Tasks, out _))
                    warnings.Add($"ミラーモードを適用しません（{m}）");
            }

            return new ValidationReport(errors.Distinct().ToList(), warnings.Distinct().ToList());
        }

        // 実際に一時ファイルを作ってみて書き込みできるか確かめる（コピー先がまだ無ければ、存在する親フォルダで確認）
        private static bool CanWrite(string path, out string reason)
        {
            reason = "";
            try
            {
                string dir = Path.GetFullPath(path);
                while (!Directory.Exists(dir))
                {
                    string? parent = Path.GetDirectoryName(dir);
                    if (string.IsNullOrEmpty(parent)) { reason = "フォルダがありません"; return false; }
                    dir = parent;
                }

                string probe = Path.Combine(dir, $".filesbackup_write_test_{Guid.NewGuid():N}.tmp");
                using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose)) { }
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }

        // ================= 実行 =================

        public int ExecuteAll() => ExecuteAllWithResult().FilesCopied;

        /// <summary>
        /// すべてのタスクを順に実行する。
        /// dryRun = true のときはコピー・削除を行わず、件数だけ数える（履歴には残さない）。
        /// 中止された場合は例外ではなく Cancelled = true の結果を返す。
        /// </summary>
        public BackupResult ExecuteAllWithResult(
            CancellationToken ct = default,
            IProgress<BackupProgress>? progress = null,
            bool dryRun = false,
            string trigger = "manual")
        {
            var result = new BackupResult { Timestamp = DateTime.Now, Trigger = trigger };
            var sw = Stopwatch.StartNew();
            _errorMessages.Clear();
            _errorCount = 0;

            string label = dryRun ? "プレビュー" : "バックアップ";
            Logger.CleanOldLogs();
            Logger.Info($"=== {label}開始 ===");

            // 割り当てが見えているネットワークドライブを覚えておく（管理者権限のタスクで UNC パスに読み替えるため）
            NetworkPaths.Remember();

            HashSet<int> mirrorBlocked = new();
            if (Options.MirrorMode)
            {
                foreach (var m in FindMirrorConflicts(Tasks, out mirrorBlocked))
                    Logger.Warn($"ミラーモードを適用しません: {m}");
            }

            var estimates = EstimateScanCounts();

            for (int i = 0; i < Tasks.Count; i++)
            {
                var task = Tasks[i];
                var tr = new TaskResult { Source = task.Source, Destination = task.Destination };
                result.TaskResults.Add(tr);

                if (ct.IsCancellationRequested)
                {
                    tr.Status = "中止";
                    result.Cancelled = true;
                    continue;
                }

                try
                {
                    estimates.TryGetValue(Key(task), out int estimate);
                    bool mirror = Options.MirrorMode && !mirrorBlocked.Contains(i);
                    RunTask(task, tr, mirror, dryRun, estimate, progress, ct);
                }
                catch (OperationCanceledException)
                {
                    tr.Status = "中止";
                    result.Cancelled = true;
                    Logger.Warn($"中止しました: {task.Source} -> {task.Destination}");
                }
                catch (Exception ex)
                {
                    tr.Status = "エラー";
                    tr.Message = ex.Message;
                    tr.Errors++;
                    RecordError($"{task.Source} -> {task.Destination}: {ex.Message}");
                    Logger.Error($"タスクの実行に失敗しました: {task.Source} -> {task.Destination}", ex);
                }

                result.FilesScanned += tr.FilesScanned;
                result.FilesCopied += tr.FilesCopied;
                result.FilesSkipped += tr.FilesSkipped;
                result.FilesDeleted += tr.FilesDeleted;
                result.BytesCopied += tr.BytesCopied;
            }

            sw.Stop();
            result.Duration = sw.Elapsed;
            result.Errors = _errorCount;
            result.ErrorMessages = _errorMessages.Take(MaxStoredErrors).ToList();

            Logger.Info($"=== {label}{(result.Cancelled ? "中止" : "完了")}: " +
                        $"コピー{result.FilesCopied}件, スキップ{result.FilesSkipped}件, 削除{result.FilesDeleted}件, エラー{result.Errors}件" +
                        $" ({result.Duration.TotalSeconds:F1}秒, {FormatBytes(result.BytesCopied)}) ===");

            if (!dryRun)
                Logger.AppendHistory(result);

            return result;
        }

        private sealed class Counters
        {
            public int Scanned, Copied, Skipped, Deleted, Errors, UnreadableDirs, LinksSkipped, UnsupportedNames;
            public int OnlineOnly, Renamed, Versioned;
            public long Bytes, PlannedBytes;
        }

        /// <summary>コピー予定（コピー先の名前は、名前を変換した場合にコピー元と異なる）</summary>
        private readonly record struct PlanItem(string SrcRelative, string DstRelative, long Size, long ExistingSize);

        private enum ConnectionState { Fine, Recovered, Lost }

        private void RunTask(BackupTask task, TaskResult tr, bool mirror, bool dryRun, int estimate,
                             IProgress<BackupProgress>? progress, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(task.Source) || string.IsNullOrWhiteSpace(task.Destination))
            {
                tr.Status = "スキップ";
                tr.Message = "コピー元またはコピー先が空です";
                Logger.Warn("スキップ（コピー元またはコピー先が空）");
                return;
            }

            var configErrors = FindConfigErrors(new[] { task });
            if (configErrors.Count > 0)
            {
                tr.Status = "スキップ";
                tr.Message = configErrors[0];
                tr.Errors++;
                RecordError(configErrors[0]);
                Logger.Warn($"スキップ（危険な設定）: {configErrors[0]}");
                return;
            }

            // ネットワークドライブは UNC パスに読み替える（管理者権限のタスクなどでドライブ文字が見えなくても動くように）
            string src = Path.GetFullPath(NetworkPaths.Resolve(task.Source));
            string dst = Path.GetFullPath(NetworkPaths.Resolve(task.Destination));
            string taskLabel = $"{task.Source} → {task.Destination}";
            _connectionLost = false;
            _reconnects = 0;

            if (!src.Equals(Path.GetFullPath(task.Source), StringComparison.OrdinalIgnoreCase) ||
                !dst.Equals(Path.GetFullPath(task.Destination), StringComparison.OrdinalIgnoreCase))
                Logger.Info($"ネットワークドライブを UNC パスで扱います: {src} -> {dst}");

            void ReportStatus(string text) =>
                progress?.Report(new BackupProgress(BackupPhase.Scanning, taskLabel, 0, estimate, 0, 0, 0, 0, text));

            // つながらないドライブ・共有は、ファイルごとにエラーを出さず「延期」にする
            bool EnsureReachable(string path, string what)
            {
                TimeSpan wait = NetworkPaths.IsUnc(path) ? NetworkWait : TimeSpan.Zero;
                if (NetworkPaths.WaitUntilReachable(path, wait, ct, out string reason, r =>
                    {
                        Logger.Warn($"{what}に接続できないため、最大 {wait.TotalMinutes:0} 分待ちます: {r}");
                        ReportStatus($"{what}への接続を待っています…（{r}）");
                    }))
                    return true;

                tr.Status = "延期";
                tr.Message = $"{what}に接続できません: {reason}";
                tr.Errors++;
                RecordError($"{taskLabel}: {tr.Message}");
                Logger.Warn($"延期（{what}に接続できません）: {path} - {reason}");
                return false;
            }

            if (NetworkPaths.IsUnc(src) && !EnsureReachable(src, "コピー元"))
                return;
            if (!EnsureReachable(dst, "コピー先"))
                return;

            if (!Directory.Exists(src))
            {
                tr.Status = "スキップ";
                tr.Message = "コピー元が見つかりません";
                Logger.Warn($"スキップ（コピー元が見つかりません）: {src}");
                return;
            }

            if (_ignorePathList.Any(p => IsSameOrInsideNormalized(NormalizePath(src), p)))
            {
                tr.Status = "スキップ";
                tr.Message = "コピー元が除外設定に含まれています";
                Logger.Warn($"スキップ（コピー元が除外設定に含まれています）: {src}");
                return;
            }

            // 並列数：0 は自動（ドライブの種類から決める）
            int dop;
            string dopNote;
            if (Options.MaxDegreeOfParallelism > 0)
            {
                dop = Math.Clamp(Options.MaxDegreeOfParallelism, 1, 8);
                dopNote = $"並列数 {dop}";
            }
            else
            {
                dop = DriveProbe.SuggestParallelism(src, dst, out string why);
                dopNote = $"並列数 自動→{dop}（{why}）";
            }

            Logger.Info($"処理中: {src} -> {dst}（{dopNote}）{(mirror ? "（ミラーモード）" : "")}{(dryRun ? "（プレビュー）" : "")}");

            var c = new Counters();
            var chunk = new List<PlanItem>();
            var mirrorFiles = new List<string>();
            var mirrorDirs = new List<string>();
            bool stoppedForSpace = false;
            bool aborted = false;   // 途中で接続が切れた
            int rootEntries = 0;

            // 世代管理：上書き・削除する前のファイルを移す先（この実行の日時のフォルダ）
            string? versionDir = Options.KeepVersions && !dryRun
                ? Path.Combine(dst, VersionsFolderName, DateTime.Now.ToString(VersionStampFormat))
                : null;

            // NAS などで使えない名前の変換は、ネットワーク上のコピー先だけに行う（ローカルの NTFS では不要）
            bool convertNames = ConvertNamesOverride ?? (Options.FixUnsupportedNames && NetworkPaths.IsUnc(dst));

            void Report(BackupPhase phase, string current, bool force = false)
            {
                if (progress == null) return;
                long now = Environment.TickCount64;
                if (!force && now - Interlocked.Read(ref _lastProgressTicks) < ProgressInterval.TotalMilliseconds) return;
                Interlocked.Exchange(ref _lastProgressTicks, now);
                progress.Report(new BackupProgress(phase, taskLabel, c.Scanned, estimate,
                    Volatile.Read(ref c.Copied), Interlocked.Read(ref c.Bytes), Interlocked.Read(ref c.PlannedBytes), c.Deleted, current));
            }

            // コピー予定をまとめてコピーする。容量不足なら false
            bool Flush()
            {
                if (chunk.Count == 0) return true;

                // 世代管理では上書きする前のファイルも残すので、上書き分も空きが必要
                long needed = chunk.Sum(item => versionDir != null ? item.Size : Math.Max(0, item.Size - item.ExistingSize));
                if (!dryRun && !HasEnoughSpace(dst, needed, out long free))
                {
                    string root = Path.GetPathRoot(dst) ?? dst;
                    Logger.Warn($"ディスク容量不足: {root} 空き{FormatBytes(free)}, 必要{FormatBytes(needed)}");
                    Logger.Warn($"スキップ（ディスク容量不足）: {src} -> {dst}");
                    RecordError($"ディスク容量不足のため中断: {dst}（空き {FormatBytes(free)} / 必要 {FormatBytes(needed)}）");
                    c.Errors++;
                    tr.Status = "容量不足";
                    tr.Message = $"空き {FormatBytes(free)} / 必要 {FormatBytes(needed)}";
                    return false;
                }

                if (dryRun)
                {
                    c.Copied += chunk.Count;
                    c.Bytes += chunk.Sum(item => item.Size);
                    chunk.Clear();
                    return true;
                }

                CopyChunk(src, dst, versionDir, chunk, dop, c, ct, Report);
                chunk.Clear();
                return true;
            }

            // ---- 走査（コピー元とコピー先をフォルダ単位で1回ずつ読む）----
            var stack = new Stack<(string Src, string Dst)>();
            stack.Push(("", ""));

            while (stack.Count > 0 && !stoppedForSpace && !aborted)
            {
                ct.ThrowIfCancellationRequested();
                _running.Wait(ct);

                var entry = stack.Pop();
                string rel = entry.Src;
                string dstRel = entry.Dst;
                bool isRoot = rel.Length == 0;
                string srcDir = isRoot ? src : Path.Combine(src, rel);
                string dstDir = isRoot ? dst : Path.Combine(dst, dstRel);

                FileInfo[] files;
                DirectoryInfo[] dirs;
                try
                {
                    var di = new DirectoryInfo(srcDir);
                    files = di.GetFiles("*", ListOptions);
                    dirs = di.GetDirectories("*", ListOptions);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    if (isRoot)
                        throw new IOException($"コピー元を読み取れません: {ex.Message}", ex);

                    // 接続が切れていたら再接続を待って読み直す
                    var state = CheckConnection(src, dst, ct, ReportStatus);
                    if (state == ConnectionState.Recovered) { stack.Push(entry); continue; }
                    if (state == ConnectionState.Lost) { aborted = true; break; }

                    c.UnreadableDirs++;
                    Logger.Warn($"フォルダを読み取れません（スキップ）: {srcDir} - {ex.Message}");
                    continue;   // 読めなかったフォルダは、ミラーでも削除対象にしない
                }

                if (isRoot)
                    rootEntries = files.Length + dirs.Length;

                // コピー先の同じフォルダを一度だけ読む（ファイルごとに問い合わせない）
                Dictionary<string, FileInfo>? dstFiles = null;
                DirectoryInfo[]? dstDirs = null;
                bool mirrorHere = mirror;
                var dstDirInfo = new DirectoryInfo(dstDir);
                if (dstDirInfo.Exists)
                {
                    try
                    {
                        dstFiles = new Dictionary<string, FileInfo>(StringComparer.OrdinalIgnoreCase);
                        foreach (var f in dstDirInfo.GetFiles("*", ListOptions))
                            dstFiles.TryAdd(f.Name, f);
                        if (mirror)
                            dstDirs = dstDirInfo.GetDirectories("*", ListOptions);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        var state = CheckConnection(src, dst, ct, ReportStatus);
                        if (state == ConnectionState.Recovered) { stack.Push(entry); continue; }
                        if (state == ConnectionState.Lost) { aborted = true; break; }

                        Logger.Warn($"コピー先フォルダを読み取れません: {dstDir} - {ex.Message}");
                        dstFiles = null;
                        mirrorHere = false;
                    }
                }

                // ミラーで「コピー元にある」とみなす名前（変換後の名前も含む）
                HashSet<string>? srcFileNames = mirrorHere && dstFiles != null
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : null;

                foreach (var file in files)
                {
                    c.Scanned++;

                    if (srcFileNames != null)
                    {
                        srcFileNames.Add(file.Name);
                        if (convertNames) srcFileNames.Add(SafeDestName(file.Name));
                    }

                    if (IsFileExcluded(file, c))
                    {
                        c.Skipped++;
                        continue;
                    }

                    // オンラインのみのファイルは、読むとダウンロードが始まるので既定ではスキップする
                    if (!Options.DownloadOnlineOnlyFiles && IsOnlineOnly(file.Attributes))
                    {
                        c.OnlineOnly++;
                        c.Skipped++;
                        continue;
                    }

                    // コピー先の名前：既にその名前であればそのまま、無ければ（変換する場合）使える名前に変える
                    string dstName = file.Name;
                    FileInfo? existing = null;
                    dstFiles?.TryGetValue(file.Name, out existing);
                    if (existing == null && convertNames)
                    {
                        string safe = SafeDestName(file.Name);
                        if (!safe.Equals(file.Name, StringComparison.Ordinal))
                        {
                            dstName = safe;
                            dstFiles?.TryGetValue(safe, out existing);
                            c.Renamed++;
                        }
                    }

                    bool needsCopy;
                    try
                    {
                        needsCopy = NeedsCopy(file, existing);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        c.Errors++;
                        RecordError($"{file.FullName} -> {ex.Message}");
                        continue;
                    }

                    if (needsCopy)
                    {
                        if (dstName != file.Name && c.Renamed <= MaxLoggedErrorsPerTask)
                            Logger.Info($"名前を変換して保存: {file.FullName} → {dstName}");
                        chunk.Add(new PlanItem(
                            isRoot ? file.Name : Path.Combine(rel, file.Name),
                            isRoot ? dstName : Path.Combine(dstRel, dstName),
                            file.Length, existing?.Length ?? 0));
                        Interlocked.Add(ref c.PlannedBytes, file.Length);
                        if (chunk.Count >= ChunkSize && !Flush())
                        {
                            stoppedForSpace = true;
                            break;
                        }
                    }
                    else
                    {
                        c.Skipped++;
                    }

                    Report(BackupPhase.Scanning, file.FullName);
                }

                if (stoppedForSpace)
                    break;

                if (_connectionLost)
                {
                    aborted = true;
                    break;
                }

                // ミラー：コピー先にだけあるファイル・フォルダを集める（削除は全コピー完了後）
                if (srcFileNames != null && dstFiles != null)
                {
                    foreach (var kv in dstFiles)
                    {
                        if (!srcFileNames.Contains(kv.Key) && !IsProtectedFromMirror(kv.Value, Path.Combine(srcDir, kv.Key), isDirectory: false))
                            mirrorFiles.Add(kv.Value.FullName);
                    }

                    if (dstDirs != null)
                    {
                        var srcDirNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var d in dirs)
                        {
                            srcDirNames.Add(d.Name);
                            if (convertNames) srcDirNames.Add(SafeDestName(d.Name));
                        }
                        foreach (var dd in dstDirs)
                        {
                            if (!srcDirNames.Contains(dd.Name) && !IsProtectedFromMirror(dd, Path.Combine(srcDir, dd.Name), isDirectory: true))
                                CollectMirrorSubtree(dd, Path.Combine(srcDir, dd.Name), mirrorFiles, mirrorDirs, ct);
                        }
                    }
                }

                foreach (var dir in dirs)
                {
                    if (IsDirExcluded(dir, c))
                        continue;

                    // フォルダ名も、コピー先に元の名前のフォルダが無ければ使える名前に変える
                    string dstName = dir.Name;
                    if (convertNames)
                    {
                        string safe = SafeDestName(dir.Name);
                        if (!safe.Equals(dir.Name, StringComparison.Ordinal) && !Directory.Exists(Path.Combine(dstDir, dir.Name)))
                        {
                            dstName = safe;
                            c.Renamed++;
                        }
                    }

                    stack.Push((isRoot ? dir.Name : Path.Combine(rel, dir.Name),
                                isRoot ? dstName : Path.Combine(dstRel, dstName)));
                }
            }

            // ---- 残りをコピー ----
            if (!stoppedForSpace && !aborted && !Flush())
                stoppedForSpace = true;
            if (_connectionLost)
                aborted = true;

            if (aborted)
            {
                tr.Status = "延期";
                tr.Message = "途中で接続が切れたため中断しました（次回の実行で続きからコピーします）";
                c.Errors++;
                RecordError($"{taskLabel}: {tr.Message}");
                Logger.Warn($"中断（接続が切れました）: {src} -> {dst}");
            }

            // ---- ミラー削除（コピーが最後まで成功した場合のみ）----
            if (mirror && !stoppedForSpace && !aborted)
            {
                if (rootEntries == 0 && (mirrorFiles.Count > 0 || mirrorDirs.Count > 0))
                {
                    Logger.Warn($"コピー元が空のため、ミラーによる削除を行いません（ドライブの接続間違いの可能性）: {src}");
                    RecordError($"コピー元が空のためミラー削除を中止: {src}");
                    c.Errors++;
                }
                else
                {
                    DeleteMirrorExtras(dst, versionDir, mirrorFiles, mirrorDirs, dryRun, c, ct, Report);
                }
            }

            // ---- 古い版の整理 ----
            if (Options.KeepVersions && !dryRun && !aborted)
                PruneVersions(dst, Options.VersionRetentionDays);

            tr.FilesScanned = c.Scanned;
            tr.FilesCopied = c.Copied;
            tr.FilesSkipped = c.Skipped;
            tr.FilesDeleted = c.Deleted;
            tr.BytesCopied = c.Bytes;
            tr.Errors += c.Errors;
            if (string.IsNullOrEmpty(tr.Status))
                tr.Status = c.Errors > 0 ? "完了（エラーあり）" : "完了";
            var notes = new List<string>();
            if (!string.IsNullOrEmpty(tr.Message)) notes.Add(tr.Message);
            if (c.UnsupportedNames > 0)
                notes.Add($"コピー先で使えない名前のファイル {c.UnsupportedNames:N0} 件（NAS などは名前の長さや使える文字に制限があります）");
            if (c.Renamed > 0)
                notes.Add($"名前を変換して保存 {c.Renamed:N0} 件（NAS で使えない名前のため。変換内容はログに記録）");
            if (c.OnlineOnly > 0)
                notes.Add($"オンラインのみのファイル {c.OnlineOnly:N0} 件をスキップ（クラウドから未ダウンロード）");
            if (c.Versioned > 0)
                notes.Add($"前の版を {c.Versioned:N0} 件保存（{VersionsFolderName}）");
            if (c.UnreadableDirs > 0)
                notes.Add($"読み取れないフォルダ {c.UnreadableDirs:N0} 件をスキップ");
            if (c.Errors > MaxLoggedErrorsPerTask)
                notes.Add($"エラーが多いため、ログには先頭 {MaxLoggedErrorsPerTask} 件だけ詳細を記録");
            tr.Message = string.Join(" / ", notes);

            Report(BackupPhase.Copying, "", force: true);
            Logger.Info($"完了: コピー{c.Copied}件, スキップ{c.Skipped}件" +
                        (mirror ? $", 削除{c.Deleted}件" : "") +
                        (c.LinksSkipped > 0 ? $", リンク除外{c.LinksSkipped}件" : "") +
                        (c.UnreadableDirs > 0 ? $", 読取不可フォルダ{c.UnreadableDirs}件" : "") +
                        (c.Versioned > 0 ? $", 前の版を保存{c.Versioned}件" : "") +
                        (c.OnlineOnly > 0 ? $", オンラインのみ{c.OnlineOnly}件" : "") +
                        (c.Renamed > 0 ? $", 名前変換{c.Renamed}件" : "") +
                        (dryRun ? "（プレビュー）" : ""));
        }

        /// <summary>
        /// コピー先のファイルシステムが受け付けない名前か（NAS の名前の長さ・文字の制限、長すぎるパスなど）。
        /// 何度やり直しても成功しないため、他のエラーと分けて集計する。
        /// </summary>
        public static bool IsUnsupportedNameError(Exception ex)
        {
            if (ex is PathTooLongException) return true;
            if (ex is not IOException) return false;
            int code = ex.HResult & 0xFFFF;
            return code == 123 /* ERROR_INVALID_NAME */ || code == 206 /* ERROR_FILENAME_EXCED_RANGE */;
        }

        private void CopyChunk(string srcRoot, string dstRoot, string? versionDir, List<PlanItem> items, int dop, Counters c,
                               CancellationToken ct, Action<BackupPhase, string, bool> report)
        {
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(dop, 1, 8),
                CancellationToken = ct,
            };

            void Status(string text) => report(BackupPhase.Copying, text, true);

            try
            {
                Parallel.ForEach(items, options, (item, loop) =>
                {
                    _running.Wait(ct);   // 一時停止中はここで待つ
                    if (_connectionLost) { loop.Stop(); return; }

                    string s = Path.Combine(srcRoot, item.SrcRelative);
                    string d = Path.Combine(dstRoot, item.DstRelative);
                    string? v = versionDir == null ? null : Path.Combine(versionDir, item.DstRelative);

                    for (int attempt = 0; ; attempt++)
                    {
                        try
                        {
                            if (CopyOne(s, d, v))
                                Interlocked.Increment(ref c.Versioned);
                            Interlocked.Increment(ref c.Copied);
                            Interlocked.Add(ref c.Bytes, item.Size);
                            break;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            bool badName = IsUnsupportedNameError(ex);

                            // ネットワークが切れた場合は、再接続を待ってやり直す（切れたままなら設定ごと延期）
                            if (!badName && ex is (IOException or UnauthorizedAccessException) && attempt < 3)
                            {
                                var state = CheckConnection(srcRoot, dstRoot, ct, Status);
                                if (state == ConnectionState.Recovered) continue;
                                if (state == ConnectionState.Lost) { loop.Stop(); return; }
                            }

                            int n = Interlocked.Increment(ref c.Errors);
                            if (badName) Interlocked.Increment(ref c.UnsupportedNames);
                            RecordError($"{s} -> {(badName ? "コピー先で使えない名前: " : "")}{ex.Message}");

                            // 同じ原因のエラーが大量に出てもログが肥大化しないよう、詳細は先頭だけ書く（件数は集計する）
                            if (n <= MaxLoggedErrorsPerTask)
                                Logger.Error($"コピーエラー: {s}", ex);
                            else if (n == MaxLoggedErrorsPerTask + 1)
                                Logger.Warn("エラーが多いため、以降のエラーはログへの記録を省略します（件数は集計します）");
                            break;
                        }
                    }

                    report(BackupPhase.Copying, s, false);
                });
            }
            catch (AggregateException ae) when (ae.Flatten().InnerExceptions.All(e => e is OperationCanceledException))
            {
                throw new OperationCanceledException(ct);
            }
        }

        /// <summary>
        /// 1ファイルをコピーする。versionPath があれば、上書きする前のファイルをそこへ移して残す（残した場合 true）。
        /// </summary>
        private bool CopyOne(string src, string dst, string? versionPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            bool versioned = false;

            if (File.Exists(dst))
            {
                // 宛先が読み取り専用だと上書きできず失敗し続けるため、事前に解除する
                var attrs = File.GetAttributes(dst);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(dst, attrs & ~FileAttributes.ReadOnly);

                if (versionPath != null)
                    versioned = MoveToVersions(dst, versionPath);
            }

            CopyWithRetry(src, dst);

            if (Options.VerifyAfterCopy && !HashEquals(src, dst))
            {
                // 一時的な読み書きの不具合に備えて1回だけやり直す
                CopyWithRetry(src, dst);
                if (!HashEquals(src, dst))
                    throw new IOException("コピー後の確認で、コピー元とコピー先の内容が一致しません（コピー中に変更された可能性があります）");
            }

            return versioned;
        }

        private static void CopyWithRetry(string src, string dst)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Copy(src, dst, true);
                    return;
                }
                catch (IOException ex) when (attempt < MaxCopyAttempts && RetryableWin32Errors.Contains(ex.HResult & 0xFFFF))
                {
                    Thread.Sleep(TimeSpan.FromSeconds(attempt));   // 1秒、2秒と間隔を空けて再試行
                }
            }
        }

        /// <summary>前の版として移す。移せなければ（名前が長すぎる等）ログに残して false</summary>
        private static bool MoveToVersions(string path, string versionPath)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(versionPath)!);
                File.Move(path, versionPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"前の版を残せませんでした（そのまま上書きします）: {path} - {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 接続が切れていないか確かめ、切れていれば再接続を待つ。
        /// 複数のスレッドから呼ばれても、待つのは1つだけ（他は結果を待つ）。
        /// </summary>
        private ConnectionState CheckConnection(string srcRoot, string dstRoot, CancellationToken ct, Action<string> status)
        {
            if (_connectionLost) return ConnectionState.Lost;
            if (QuickReachable(srcRoot) && QuickReachable(dstRoot)) return ConnectionState.Fine;

            lock (_reconnectGate)
            {
                if (_connectionLost) return ConnectionState.Lost;
                if (QuickReachable(srcRoot) && QuickReachable(dstRoot)) return ConnectionState.Recovered;

                if (++_reconnects > MaxReconnects)
                {
                    Logger.Warn("接続が何度も切れるため、この設定を中断します");
                    _connectionLost = true;
                    return ConnectionState.Lost;
                }

                Logger.Warn($"接続が切れました。再接続を最大 {ReconnectWait.TotalMinutes:0} 分待ちます: {srcRoot} -> {dstRoot}");
                status("接続が切れました。再接続を待っています…");

                bool back = NetworkPaths.WaitUntilReachable(srcRoot, ReconnectWait, ct, out _, interval: TimeSpan.FromSeconds(10)) &&
                            NetworkPaths.WaitUntilReachable(dstRoot, ReconnectWait, ct, out _, interval: TimeSpan.FromSeconds(10));
                if (back)
                {
                    Logger.Info("再接続しました。続きからコピーします");
                    return ConnectionState.Recovered;
                }

                _connectionLost = true;
                return ConnectionState.Lost;
            }
        }

        private static bool QuickReachable(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(path);
                return !string.IsNullOrEmpty(root) && Directory.Exists(root);
            }
            catch
            {
                return false;
            }
        }

        private bool NeedsCopy(FileInfo src, FileInfo? existing)
        {
            if (existing == null)
                return true;

            if (src.LastWriteTimeUtc - existing.LastWriteTimeUtc <= TimestampTolerance)
                return false;

            if (!Options.CompareByHash)
                return true;

            // 時刻だけ新しく内容が同じなら、コピーを省く
            if (src.Length != existing.Length)
                return true;

            // コピー先がオンラインのみ（クラウド上）なら、読むとダウンロードが始まるので大きさだけで判断する
            if (IsOnlineOnly(existing.Attributes))
                return false;

            return !HashEquals(src.FullName, existing.FullName);
        }

        private static bool HashEquals(string a, string b)
        {
            using var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
            using var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20);
            return SHA256.HashData(fa).AsSpan().SequenceEqual(SHA256.HashData(fb));
        }

        private static bool HasEnoughSpace(string dst, long needed, out long free)
        {
            // UNC パス（NAS）でも空き容量を確かめる。取得できなければコピー時のエラーに任せる
            free = NetworkPaths.GetFreeSpace(dst);
            return free < 0 || free >= needed + FreeSpaceMargin;
        }

        // ================= 世代管理 =================

        /// <summary>保持期間を過ぎた版（日時のフォルダ）を削除する</summary>
        private static void PruneVersions(string dstRoot, int retentionDays)
        {
            string root = Path.Combine(dstRoot, VersionsFolderName);
            try
            {
                if (!Directory.Exists(root)) return;

                // 普段は目に入らないよう隠しフォルダにする
                var attrs = File.GetAttributes(root);
                if ((attrs & FileAttributes.Hidden) == 0)
                    File.SetAttributes(root, attrs | FileAttributes.Hidden);

                DateTime limit = DateTime.Now.AddDays(-Math.Max(1, retentionDays));
                foreach (var dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    if (!DateTime.TryParseExact(name, VersionStampFormat, System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var stamp) || stamp >= limit)
                        continue;

                    try
                    {
                        // リンクがあっても中身は辿らない（Directory.Delete はリンク自体を消す）
                        Directory.Delete(dir, recursive: true);
                        Logger.Info($"保持期間を過ぎた版を削除: {dir}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"古い版を削除できませんでした: {dir} - {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"古い版の整理に失敗しました: {root} - {ex.Message}");
            }
        }

        // ================= 名前の変換・クラウド =================

        /// <summary>OneDrive・Google ドライブなどの「オンラインのみ」のファイルか（読むとダウンロードが始まる）</summary>
        public static bool IsOnlineOnly(FileAttributes attrs) =>
            (attrs & (RecallOnDataAccess | RecallOnOpen | FileAttributes.Offline)) != 0;

        private const int MaxNameBytes = 255;   // NAS（ext4・Btrfs など）の名前の上限（UTF-8 のバイト数）

        /// <summary>
        /// NAS などで使えない名前を、使える名前に変える（使える名前ならそのまま返す）。
        ///  - 使えない文字（" * : &lt; &gt; ? | と制御文字）→ _
        ///  - 末尾のピリオド・空白 → _
        ///  - UTF-8 で 255 バイトを超える名前 → 拡張子を残して縮め、元の名前から作った短い識別子を付ける（同じ名前は毎回同じ名前になる）
        /// </summary>
        public static string SafeDestName(string name)
        {
            var sb = new StringBuilder(name.Length);
            foreach (char ch in name)
                sb.Append(ch < 0x20 || "\"*:<>?|".IndexOf(ch) >= 0 ? '_' : ch);

            int end = sb.Length;
            while (end > 0 && (sb[end - 1] == '.' || sb[end - 1] == ' ')) end--;
            for (int i = end; i < sb.Length; i++) sb[i] = '_';

            string s = sb.ToString();
            if (Encoding.UTF8.GetByteCount(s) <= MaxNameBytes)
                return s;

            string id = "~" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name)))[..8].ToLowerInvariant();
            string ext = Path.GetExtension(s);
            if (Encoding.UTF8.GetByteCount(ext) > 32) ext = "";
            string stem = s[..^ext.Length];

            int budget = MaxNameBytes - Encoding.UTF8.GetByteCount(id) - Encoding.UTF8.GetByteCount(ext);
            int len = stem.Length;
            while (len > 0 && Encoding.UTF8.GetByteCount(stem.AsSpan(0, len)) > budget) len--;
            if (len > 0 && char.IsHighSurrogate(stem[len - 1])) len--;   // サロゲートペアを分断しない
            return stem[..len].TrimEnd(' ', '.') + id + ext;
        }

        /// <summary>ドライブ（共有）の直下にあるか</summary>
        private static bool IsAtVolumeRoot(string fullPath)
        {
            string? parent = Path.GetDirectoryName(fullPath);
            string? root = Path.GetPathRoot(fullPath);
            return parent != null && root != null &&
                   parent.TrimEnd('\\').Equals(root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>常に除外する項目か（各ドライブ直下の pagefile.sys など、このアプリの版フォルダ）</summary>
        private static bool IsAlwaysExcluded(FileSystemInfo item) =>
            item.Name.Equals(VersionsFolderName, StringComparison.OrdinalIgnoreCase) ||
            (ForcedRootNames.Contains(item.Name) && IsAtVolumeRoot(item.FullName));

        // ================= ミラー =================

        // コピー先にだけあるフォルダの中身を削除対象として集める（リンク先や除外対象には入らない）
        private void CollectMirrorSubtree(DirectoryInfo root, string srcEquivalent, List<string> files, List<string> dirs, CancellationToken ct)
        {
            var stack = new Stack<(DirectoryInfo Dir, string SrcEq)>();
            stack.Push((root, srcEquivalent));

            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                var (dir, srcEq) = stack.Pop();
                dirs.Add(dir.FullName);

                try
                {
                    foreach (var f in dir.GetFiles("*", ListOptions))
                    {
                        if (!IsProtectedFromMirror(f, Path.Combine(srcEq, f.Name), isDirectory: false))
                            files.Add(f.FullName);
                    }
                    foreach (var sub in dir.GetDirectories("*", ListOptions))
                    {
                        if (!IsProtectedFromMirror(sub, Path.Combine(srcEq, sub.Name), isDirectory: true))
                            stack.Push((sub, Path.Combine(srcEq, sub.Name)));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.Warn($"コピー先フォルダを読み取れません（ミラー削除の対象外）: {dir.FullName} - {ex.Message}");
                }
            }
        }

        private void DeleteMirrorExtras(string dstRoot, string? versionDir, List<string> files, List<string> dirs, bool dryRun,
                                        Counters c, CancellationToken ct, Action<BackupPhase, string, bool> report)
        {
            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                _running.Wait(ct);

                if (dryRun)
                {
                    c.Deleted++;
                    continue;
                }

                try
                {
                    var attrs = File.GetAttributes(file);
                    if ((attrs & FileAttributes.ReadOnly) != 0)
                        File.SetAttributes(file, attrs & ~FileAttributes.ReadOnly);

                    if (versionDir != null)
                    {
                        // 世代管理：消さずに前の版として移す（保持期間を過ぎたら消える）
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(versionDir, Path.GetRelativePath(dstRoot, file)))!);
                        File.Move(file, Path.Combine(versionDir, Path.GetRelativePath(dstRoot, file)), overwrite: true);
                        c.Versioned++;
                        Logger.Info($"削除（ミラー・前の版として保存）: {file}");
                    }
                    else
                    {
                        File.Delete(file);
                        Logger.Info($"削除（ミラー）: {file}");
                    }
                    c.Deleted++;
                }
                catch (Exception ex)
                {
                    c.Errors++;
                    RecordError($"削除失敗 {file}: {ex.Message}");
                    Logger.Error($"削除失敗: {file}", ex);
                }

                report(BackupPhase.Mirroring, file, false);
            }

            if (dryRun)
                return;

            // 空になったフォルダを深い順に削除（中身が残っていれば消えない）
            foreach (var dir in dirs.OrderByDescending(d => d.Length))
            {
                try { Directory.Delete(dir, recursive: false); } catch { }
            }
        }

        private bool IsProtectedFromMirror(FileSystemInfo dstItem, string srcEquivalent, bool isDirectory)
        {
            var attrs = dstItem.Attributes;

            if ((attrs & FileAttributes.ReparsePoint) != 0)
                return true;    // リンクの先は絶対に消さない
            if (IsAlwaysExcluded(dstItem))
                return true;    // 前の版のフォルダ、ドライブ直下のシステムファイル
            if (Options.SkipHiddenAndSystem && (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                return true;
            if (_ignorePathSet.Contains(dstItem.FullName) || _ignorePathSet.Contains(srcEquivalent))
                return true;
            if (isDirectory && _ignoreFolderNames.Contains(dstItem.Name))
                return true;
            if (!isDirectory && _ignoreExtensions.Contains(Path.GetExtension(dstItem.Name)))
                return true;
            if (_ignoreGlobs.Count > 0)
            {
                string a = srcEquivalent.Replace('\\', '/');
                string b = dstItem.FullName.Replace('\\', '/');
                if (_ignoreGlobs.Any(rx => rx.IsMatch(a) || rx.IsMatch(b)))
                    return true;
            }
            return false;
        }

        // ================= 除外判定 =================

        private bool IsFileExcluded(FileInfo file, Counters c)
        {
            if (_ignoreExtensions.Count > 0 && _ignoreExtensions.Contains(file.Extension))
                return true;
            if (_ignorePathSet.Contains(file.FullName))
                return true;
            if (IsAlwaysExcluded(file))
                return true;

            var attrs = file.Attributes;
            if (Options.SkipHiddenAndSystem && (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                return true;
            if (!Options.FollowReparsePoints && (attrs & FileAttributes.ReparsePoint) != 0)
            {
                c.LinksSkipped++;
                return true;
            }

            if (_ignoreGlobs.Count > 0)
            {
                string normalized = file.FullName.Replace('\\', '/');
                if (_ignoreGlobs.Any(rx => rx.IsMatch(normalized)))
                    return true;
            }
            return false;
        }

        private bool IsDirExcluded(DirectoryInfo dir, Counters c)
        {
            if (_ignorePathSet.Contains(dir.FullName) || _ignoreFolderNames.Contains(dir.Name))
                return true;
            if (IsAlwaysExcluded(dir))
                return true;

            var attrs = dir.Attributes;
            if (Options.SkipHiddenAndSystem && (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                return true;
            if (!Options.FollowReparsePoints && (attrs & FileAttributes.ReparsePoint) != 0)
            {
                c.LinksSkipped++;
                Logger.Info($"ジャンクション/シンボリックリンクをスキップ: {dir.FullName}");
                return true;
            }

            if (_ignoreGlobs.Count > 0)
            {
                string normalized = dir.FullName.Replace('\\', '/');
                if (_ignoreGlobs.Any(rx => rx.IsMatch(normalized)))
                    return true;
            }
            return false;
        }

        // ワイルドカードを正規表現に変換（* は区切り内、** は区切りをまたぐ、? は1文字）
        private static Regex GlobToRegex(string glob)
        {
            string g = glob.Replace('\\', '/');
            var sb = new StringBuilder("^");

            for (int i = 0; i < g.Length; i++)
            {
                char ch = g[i];
                if (ch == '*' && i + 1 < g.Length && g[i + 1] == '*')
                {
                    if (i + 2 < g.Length && g[i + 2] == '/')
                    {
                        sb.Append("(?:.*/)?");          // "**/" は 0 個以上のフォルダ
                        i += 2;
                    }
                    else if (i + 2 == g.Length && sb.Length > 0 && sb[sb.Length - 1] == '/')
                    {
                        sb.Length--;
                        sb.Append("(?:/.*)?");          // 末尾の "/**" はそのフォルダ自身と配下すべて
                        i++;
                    }
                    else
                    {
                        sb.Append(".*");
                        i++;
                    }
                }
                else if (ch == '*') sb.Append("[^/]*");
                else if (ch == '?') sb.Append("[^/]");
                else sb.Append(Regex.Escape(ch.ToString()));
            }

            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        }

        // ================= 補助 =================

        private void RecordError(string message)
        {
            if (Interlocked.Increment(ref _errorCount) <= MaxStoredErrors)
                _errorMessages.Enqueue(message);
        }

        private static string Key(BackupTask t) => $"{t.Source}|{t.Destination}".ToLowerInvariant();

        // 進捗表示の目安として、前回の実行で走査した件数を使う
        private Dictionary<string, int> EstimateScanCounts()
        {
            var map = new Dictionary<string, int>();
            try
            {
                foreach (var h in Logger.LoadHistory().AsEnumerable().Reverse())
                {
                    foreach (var t in h.TaskResults)
                    {
                        string key = $"{t.Source}|{t.Destination}".ToLowerInvariant();
                        int count = t.FilesScanned > 0 ? t.FilesScanned : t.FilesCopied + t.FilesSkipped;
                        if (count > 0 && !map.ContainsKey(key))
                            map[key] = count;
                    }
                }
            }
            catch { }
            return map;
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

        /// <summary>比較用に正規化したフルパス（末尾の区切り文字なし。"E:\" は "E:"）</summary>
        /// <remarks>ネットワークドライブは UNC パスに読み替える（Z:\ と \\server\share を同じ場所として扱うため）</remarks>
        private static string NormalizePath(string path) =>
            Path.GetFullPath(NetworkPaths.Resolve(path)).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        private static bool IsSameOrInsideNormalized(string p, string r) =>
            p.Equals(r, StringComparison.OrdinalIgnoreCase) ||
            p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

        // path が parent と同じ、またはその配下にあるか（"C:\foo" と "C:\foobar" を取り違えないよう区切り文字で判定）
        public static bool IsSameOrInside(string path, string parent) =>
            IsSameOrInsideNormalized(NormalizePath(path), NormalizePath(parent));

        private static bool Overlaps(string a, string b) => IsSameOrInside(a, b) || IsSameOrInside(b, a);

        public static string FormatBytes(long bytes)
        {
            if (bytes < 0) return "不明";
            string[] sizes = ["B", "KB", "MB", "GB", "TB"];
            double len = bytes;
            int order = 0;
            while (len >= 1024 && order < sizes.Length - 1) { order++; len /= 1024; }
            return $"{len:0.##} {sizes[order]}";
        }
    }
}
