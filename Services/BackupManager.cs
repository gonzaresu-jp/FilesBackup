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
                try { paths.Add(NormalizePath(p)); } catch { /* 不正なパスは無視 */ }
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
                    if (!Directory.Exists(task.Source))
                        warnings.Add($"コピー元が見つかりません（実行時はスキップします）: {task.Source}");

                    string? root = Path.GetPathRoot(Path.GetFullPath(task.Destination));
                    if (!string.IsNullOrEmpty(root) && !Directory.Exists(root))
                        errors.Add($"コピー先のドライブが見つかりません: {task.Destination}");
                    else if (!CanWrite(task.Destination, out string reason))
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
            public long Bytes, PlannedBytes;
        }

        private readonly record struct PlanItem(string Relative, long Size, long ExistingSize);

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

            string src = Path.GetFullPath(task.Source);
            string dst = Path.GetFullPath(task.Destination);
            string taskLabel = $"{task.Source} → {task.Destination}";

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
            int rootEntries = 0;

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

                long needed = chunk.Sum(item => Math.Max(0, item.Size - item.ExistingSize));
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

                CopyChunk(src, dst, chunk, dop, c, ct, Report);
                chunk.Clear();
                return true;
            }

            // ---- 走査（コピー元とコピー先をフォルダ単位で1回ずつ読む）----
            var stack = new Stack<string>();
            stack.Push("");

            while (stack.Count > 0 && !stoppedForSpace)
            {
                ct.ThrowIfCancellationRequested();
                _running.Wait(ct);

                string rel = stack.Pop();
                bool isRoot = rel.Length == 0;
                string srcDir = isRoot ? src : Path.Combine(src, rel);
                string dstDir = isRoot ? dst : Path.Combine(dst, rel);

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
                        Logger.Warn($"コピー先フォルダを読み取れません: {dstDir} - {ex.Message}");
                        dstFiles = null;
                        mirrorHere = false;
                    }
                }

                foreach (var file in files)
                {
                    c.Scanned++;

                    if (IsFileExcluded(file, c))
                    {
                        c.Skipped++;
                        continue;
                    }

                    FileInfo? existing = null;
                    dstFiles?.TryGetValue(file.Name, out existing);

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
                        chunk.Add(new PlanItem(isRoot ? file.Name : Path.Combine(rel, file.Name), file.Length, existing?.Length ?? 0));
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

                // ミラー：コピー先にだけあるファイル・フォルダを集める（削除は全コピー完了後）
                if (mirrorHere && dstFiles != null)
                {
                    var srcFileNames = new HashSet<string>(files.Select(f => f.Name), StringComparer.OrdinalIgnoreCase);
                    foreach (var kv in dstFiles)
                    {
                        if (!srcFileNames.Contains(kv.Key) && !IsProtectedFromMirror(kv.Value, Path.Combine(srcDir, kv.Key), isDirectory: false))
                            mirrorFiles.Add(kv.Value.FullName);
                    }

                    if (dstDirs != null)
                    {
                        var srcDirNames = new HashSet<string>(dirs.Select(d => d.Name), StringComparer.OrdinalIgnoreCase);
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
                    stack.Push(isRoot ? dir.Name : Path.Combine(rel, dir.Name));
                }
            }

            // ---- 残りをコピー ----
            if (!stoppedForSpace && !Flush())
                stoppedForSpace = true;

            // ---- ミラー削除（コピーが最後まで成功した場合のみ）----
            if (mirror && !stoppedForSpace)
            {
                if (rootEntries == 0 && (mirrorFiles.Count > 0 || mirrorDirs.Count > 0))
                {
                    Logger.Warn($"コピー元が空のため、ミラーによる削除を行いません（ドライブの接続間違いの可能性）: {src}");
                    RecordError($"コピー元が空のためミラー削除を中止: {src}");
                    c.Errors++;
                }
                else
                {
                    DeleteMirrorExtras(mirrorFiles, mirrorDirs, dryRun, c, ct, Report);
                }
            }

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

        private void CopyChunk(string srcRoot, string dstRoot, List<PlanItem> items, int dop, Counters c, CancellationToken ct,
                               Action<BackupPhase, string, bool> report)
        {
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = Math.Clamp(dop, 1, 8),
                CancellationToken = ct,
            };

            try
            {
                Parallel.ForEach(items, options, item =>
                {
                    _running.Wait(ct);   // 一時停止中はここで待つ

                    string s = Path.Combine(srcRoot, item.Relative);
                    string d = Path.Combine(dstRoot, item.Relative);
                    try
                    {
                        CopyOne(s, d);
                        Interlocked.Increment(ref c.Copied);
                        Interlocked.Add(ref c.Bytes, item.Size);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        int n = Interlocked.Increment(ref c.Errors);
                        bool badName = IsUnsupportedNameError(ex);
                        if (badName) Interlocked.Increment(ref c.UnsupportedNames);
                        RecordError($"{s} -> {(badName ? "コピー先で使えない名前: " : "")}{ex.Message}");

                        // 同じ原因のエラーが大量に出てもログが肥大化しないよう、詳細は先頭だけ書く（件数は集計する）
                        if (n <= MaxLoggedErrorsPerTask)
                            Logger.Error($"コピーエラー: {s}", ex);
                        else if (n == MaxLoggedErrorsPerTask + 1)
                            Logger.Warn("エラーが多いため、以降のエラーはログへの記録を省略します（件数は集計します）");
                    }

                    report(BackupPhase.Copying, s, false);
                });
            }
            catch (AggregateException ae) when (ae.Flatten().InnerExceptions.All(e => e is OperationCanceledException))
            {
                throw new OperationCanceledException(ct);
            }
        }

        private static void CopyOne(string src, string dst)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);

            // 宛先が読み取り専用だと上書きできず失敗し続けるため、事前に解除する
            if (File.Exists(dst))
            {
                var attrs = File.GetAttributes(dst);
                if ((attrs & FileAttributes.ReadOnly) != 0)
                    File.SetAttributes(dst, attrs & ~FileAttributes.ReadOnly);
            }

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
            free = -1;
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(dst));
                if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\"))
                    return true;   // ネットワークパスは確認できないので、コピー時のエラーに任せる

                free = new DriveInfo(root).AvailableFreeSpace;
                return free >= needed + FreeSpaceMargin;
            }
            catch
            {
                return true;
            }
        }

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

        private void DeleteMirrorExtras(List<string> files, List<string> dirs, bool dryRun, Counters c, CancellationToken ct,
                                        Action<BackupPhase, string, bool> report)
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
                    File.Delete(file);
                    c.Deleted++;
                    Logger.Info($"削除（ミラー）: {file}");
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
        private static string NormalizePath(string path) =>
            Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

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
