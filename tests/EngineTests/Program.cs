using System.Diagnostics;
using System.Reflection;
using FilesBackup.Models;
using FilesBackup.Services;

namespace EngineTests;

internal static class Program
{
    private static int _failures;
    private static string _root = "";

    private static int Main()
    {
        _root = Path.Combine(Path.GetTempPath(), "fbtest_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);

        // 実際の %LocalAppData%\FilesBackup を汚さないよう、テスト用フォルダを使う（AppPaths を触る前に設定する）
        Environment.SetEnvironmentVariable("FILESBACKUP_DATA_DIR", Path.Combine(_root, "appdata"));

        var tests = new (string Name, Action Body)[]
        {
            ("基本コピーと差分", BasicAndIncremental),
            ("存在しないコピー元はスキップして続行", MissingSourceContinues),
            ("コピー先がコピー元の配下なら実行しない", DestinationInsideSource),
            ("ジャンクションは既定で辿らない", JunctionsNotFollowed),
            ("除外（パス・拡張子・パターン・フォルダ名・旧形式）", IgnoreRules),
            ("隠し/システムのスキップ（ルートは対象外）", HiddenSystemSkip),
            ("ミラー：不要ファイル削除と保護", MirrorDeletesAndProtects),
            ("ミラー：コピー先が重なる設定には適用しない", MirrorBlockedOnOverlap),
            ("ミラー：コピー元が空なら削除しない", MirrorEmptySourceSafety),
            ("プレビューはコピーも履歴記録もしない", DryRun),
            ("一時停止と再開", PauseResume),
            ("中止", Cancel),
            ("ハッシュ比較", HashCompare),
            ("時刻の許容誤差", TimestampTolerance),
            ("強制除外（pagefile.sys など）", ForcedExcludes),
            ("空き容量の判定", FreeSpaceCheck),
            ("事前チェック", Validation),
            ("設定：旧形式の除外を分類・インポート/エクスポート", ConfigStoreRoundTrip),
            ("設定：旧保存先からの移行", Migration),
            ("スケジュール：取りこぼし判定", MissedRunDetection),
            ("履歴：記録とエラー数の上限", History),
            ("同時実行ロック", RunLockWorks),
            ("並列数の自動決定・使えない名前の分類・進捗の予定量", AutoParallelAndClassification),
        };

        foreach (var (name, body) in tests)
        {
            Console.WriteLine($"--- {name}");
            try
            {
                body();
            }
            catch (Exception ex)
            {
                Check(false, $"例外: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Cleanup();
        Console.WriteLine(_failures == 0 ? "\nALL PASSED" : $"\n{_failures} FAILED");
        return _failures == 0 ? 0 : 1;
    }

    // ================= ヘルパー =================

    private static void Check(bool condition, string name)
    {
        Console.WriteLine($"{(condition ? "PASS" : "FAIL")}  {name}");
        if (!condition) _failures++;
    }

    private static string D(string name)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string F(string path, string content = "x", DateTime? mtimeUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (mtimeUtc != null) File.SetLastWriteTimeUtc(path, mtimeUtc.Value);
        return path;
    }

    private static BackupManager M(IEnumerable<(string Src, string Dst)> tasks, IgnoreConfig? ignore = null, BackupOptions? options = null)
    {
        var m = new BackupManager
        {
            Tasks = tasks.Select(t => new BackupTask { Source = t.Src, Destination = t.Dst }).ToList(),
            Options = options ?? new BackupOptions(),
        };
        m.ApplyIgnore(ignore ?? new IgnoreConfig());
        return m;
    }

    private static void Junction(string link, string target)
    {
        var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        p.WaitForExit();
    }

    private static void Cleanup()
    {
        try
        {
            // ジャンクションは中身を辿らずリンクだけ消す
            foreach (var dir in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories)
                         .Where(d => (File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0).ToList())
                Directory.Delete(dir);

            foreach (var f in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            File.SetAttributes(_root, FileAttributes.Directory);

            Directory.Delete(_root, true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"(後片付けに失敗: {ex.Message} / {_root})");
        }
    }

    // ================= テスト =================

    private static void BasicAndIncremental()
    {
        string src = D("t1src"), dst = Path.Combine(_root, "t1dst");
        F(Path.Combine(src, "a.txt"));
        F(Path.Combine(src, "sub", "b.txt"));

        var r1 = M(new[] { (src, dst) }).ExecuteAllWithResult();
        Check(r1.FilesCopied == 2 && File.Exists(Path.Combine(dst, "sub", "b.txt")), $"初回 2件コピー（{r1.FilesCopied}）");

        var r2 = M(new[] { (src, dst) }).ExecuteAllWithResult();
        Check(r2.FilesCopied == 0 && r2.FilesSkipped == 2, $"2回目は差分なし（コピー {r2.FilesCopied}, スキップ {r2.FilesSkipped}）");
        Check(r2.TaskResults[0].FilesScanned == 2, "走査件数を記録");
    }

    private static void MissingSourceContinues()
    {
        string src = D("t2src"), dst = Path.Combine(_root, "t2dst");
        F(Path.Combine(src, "a.txt"));

        var r = M(new[] { (Path.Combine(_root, "no_such_dir"), Path.Combine(_root, "t2x")), (src, dst) }).ExecuteAllWithResult();
        Check(r.TaskResults[0].Status == "スキップ", $"存在しないコピー元はスキップ（{r.TaskResults[0].Status}）");
        Check(!Directory.Exists(Path.Combine(_root, "t2x")), "コピー先を作らない");
        Check(r.TaskResults[1].FilesCopied == 1, "後続のタスクは実行される");
    }

    private static void DestinationInsideSource()
    {
        string src = D("t3src");
        F(Path.Combine(src, "a.txt"));
        string inside = Path.Combine(src, "inside");

        var r = M(new[] { (src, inside) }).ExecuteAllWithResult();
        Check(r.TaskResults[0].Status == "スキップ" && r.Errors == 1, "スキップしてエラーとして記録");
        Check(!Directory.Exists(inside), "コピーしない");
        Check(BackupManager.FindConfigErrors(new[] { new BackupTask { Source = src, Destination = inside } }).Count == 1, "保存時チェックでも検出");

        string winDst = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "bk");
        string sysRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        Check(BackupManager.FindConfigErrors(new[]
        {
            new BackupTask { Source = src, Destination = winDst },
            new BackupTask { Source = src, Destination = sysRoot },
        }).Count == 2, "Windows フォルダ・システムドライブ直下も検出");
        Check(BackupManager.FindConfigErrors(new[] { new BackupTask { Source = @"E:\", Destination = @"Z:\" } }).Count == 0,
            "ドライブ全体のコピー（E:\\ → Z:\\）は許可");
    }

    private static void JunctionsNotFollowed()
    {
        string src = D("t4src"), outside = D("t4outside"), dst = Path.Combine(_root, "t4dst");
        F(Path.Combine(src, "a.txt"));
        F(Path.Combine(outside, "o.txt"));
        Junction(Path.Combine(src, "link"), outside);

        M(new[] { (src, dst) }).ExecuteAllWithResult();
        Check(!File.Exists(Path.Combine(dst, "link", "o.txt")), "既定では辿らない");

        string dst2 = Path.Combine(_root, "t4dst2");
        M(new[] { (src, dst2) }, options: new BackupOptions { FollowReparsePoints = true }).ExecuteAllWithResult();
        Check(File.Exists(Path.Combine(dst2, "link", "o.txt")), "オプションで辿れる");
    }

    private static void IgnoreRules()
    {
        string src = D("t5src"), dst = Path.Combine(_root, "t5dst");
        F(Path.Combine(src, "keep.txt"));
        F(Path.Combine(src, "x.tmp"));
        F(Path.Combine(src, "y.TMP"));
        F(Path.Combine(src, "f.bak"));
        F(Path.Combine(src, "Cache", "c.txt"));
        F(Path.Combine(src, "deep", "Cache", "d.txt"));
        F(Path.Combine(src, ".git", "config"));
        F(Path.Combine(src, "node", ".git", "HEAD"));
        F(Path.Combine(src, "skipme", "s.txt"));
        F(Path.Combine(src, "skipfile.txt"));

        var ignore = new IgnoreConfig
        {
            Paths = { ".tmp", Path.Combine(src, "skipme"), Path.Combine(src, "skipfile.txt"), "**/Cache/**" },   // 旧形式の混在
            Extensions = { "bak" },
            FolderNames = { ".git" },
        };
        var normalized = ignore.Normalize();
        Check(normalized.Extensions.ToHashSet().SetEquals(new[] { ".tmp", ".bak" }) && normalized.Patterns.Count == 1 && normalized.Paths.Count == 2,
            "旧形式の Paths を種類ごとに振り分け");

        var r = M(new[] { (src, dst) }, ignore).ExecuteAllWithResult();
        var copied = Directory.Exists(dst)
            ? Directory.GetFiles(dst, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(dst, f)).ToList()
            : new List<string>();
        Check(copied.Count == 1 && copied[0] == "keep.txt", $"keep.txt だけコピー（{string.Join(", ", copied)}）");
    }

    private static void HiddenSystemSkip()
    {
        string src = D("t6src"), dst = Path.Combine(_root, "t6dst");
        F(Path.Combine(src, "v.txt"));
        string hidden = F(Path.Combine(src, "h.txt"));
        File.SetAttributes(hidden, FileAttributes.Hidden);
        string hiddenDir = D(Path.Combine("t6src", "hd"));
        F(Path.Combine(hiddenDir, "in.txt"));
        File.SetAttributes(hiddenDir, FileAttributes.Directory | FileAttributes.Hidden);
        File.SetAttributes(src, FileAttributes.Directory | FileAttributes.Hidden | FileAttributes.System);   // ドライブ直下と同じ状態

        var r = M(new[] { (src, dst) }, options: new BackupOptions { SkipHiddenAndSystem = true }).ExecuteAllWithResult();
        Check(File.Exists(Path.Combine(dst, "v.txt")), "ルートが隠し属性でもコピーする");
        Check(!File.Exists(Path.Combine(dst, "h.txt")) && !Directory.Exists(Path.Combine(dst, "hd")), "隠しファイル・フォルダはスキップ");
        File.SetAttributes(src, FileAttributes.Directory);
    }

    private static void MirrorDeletesAndProtects()
    {
        string src = D("t7src"), dst = D("t7dst"), outside = D("t7outside");
        F(Path.Combine(src, "a.txt"), "new", DateTime.UtcNow);
        F(Path.Combine(src, "sub", "b.txt"));
        F(Path.Combine(dst, "a.txt"), "old", DateTime.UtcNow.AddHours(-1));
        F(Path.Combine(dst, "extra.txt"));
        F(Path.Combine(dst, "extraDir", "e1.txt"));
        F(Path.Combine(dst, "extraDir", "inner", "e2.txt"));
        F(Path.Combine(dst, "keep.tmp"));
        F(Path.Combine(dst, ".git", "config"));
        F(Path.Combine(outside, "precious.txt"));
        Junction(Path.Combine(dst, "linkInDst"), outside);

        var ignore = new IgnoreConfig { Extensions = { ".tmp" }, FolderNames = { ".git" } };
        var options = new BackupOptions { MirrorMode = true };

        var preview = M(new[] { (src, dst) }, ignore, options).ExecuteAllWithResult(dryRun: true);
        Check(preview.FilesDeleted == 3 && File.Exists(Path.Combine(dst, "extra.txt")), $"プレビューは削除予定だけ数える（{preview.FilesDeleted}件）");

        var r = M(new[] { (src, dst) }, ignore, options).ExecuteAllWithResult();
        Check(r.FilesDeleted == 3, $"不要ファイル3件を削除（{r.FilesDeleted}）");
        Check(!File.Exists(Path.Combine(dst, "extra.txt")) && !Directory.Exists(Path.Combine(dst, "extraDir")), "不要ファイルと空になったフォルダを削除");
        Check(File.ReadAllText(Path.Combine(dst, "a.txt")) == "new" && File.Exists(Path.Combine(dst, "sub", "b.txt")), "コピーは行われる");
        Check(File.Exists(Path.Combine(dst, "keep.tmp")) && File.Exists(Path.Combine(dst, ".git", "config")), "除外対象は削除しない");
        Check(File.Exists(Path.Combine(outside, "precious.txt")), "ジャンクションの先は削除しない");
    }

    private static void MirrorBlockedOnOverlap()
    {
        string srcA = D("t8a"), srcB = D("t8b"), shared = Path.Combine(_root, "t8shared");
        F(Path.Combine(srcA, "a1.txt"));
        F(Path.Combine(srcB, "b1.txt"));

        var tasks = new[] { (srcA, shared), (srcB, shared) };
        var conflicts = BackupManager.FindMirrorConflicts(
            tasks.Select(t => new BackupTask { Source = t.Item1, Destination = t.Item2 }).ToList(), out var blocked);
        Check(blocked.Count == 2 && conflicts.Count > 0, "重なりを検出");

        var r = M(tasks, options: new BackupOptions { MirrorMode = true }).ExecuteAllWithResult();
        Check(r.FilesDeleted == 0 && File.Exists(Path.Combine(shared, "a1.txt")) && File.Exists(Path.Combine(shared, "b1.txt")),
            "互いのファイルを削除しない");

        // D:\ → E:\ と E:\ → Z:\ のように、コピー先が他のコピー元と重なる場合も適用しない
        var chain = new List<BackupTask>
        {
            new() { Source = @"D:\", Destination = @"E:\" },
            new() { Source = @"E:\", Destination = @"Z:\" },
        };
        BackupManager.FindMirrorConflicts(chain, out var chainBlocked);
        Check(chainBlocked.SetEquals(new[] { 0 }), "コピー先が他の設定のコピー元（E:\\）になっている設定は除外");
    }

    private static void MirrorEmptySourceSafety()
    {
        string src = D("t9src"), dst = D("t9dst");
        F(Path.Combine(dst, "z.txt"));

        var r = M(new[] { (src, dst) }, options: new BackupOptions { MirrorMode = true }).ExecuteAllWithResult();
        Check(File.Exists(Path.Combine(dst, "z.txt")) && r.Errors >= 1, "コピー元が空なら削除せずエラーを記録");
    }

    private static void DryRun()
    {
        string src = D("t10src"), dst = Path.Combine(_root, "t10dst");
        for (int i = 0; i < 5; i++) F(Path.Combine(src, $"f{i}.txt"), new string('a', 100));

        int before = Logger.LoadHistory().Count;
        var r = M(new[] { (src, dst) }).ExecuteAllWithResult(dryRun: true);
        Check(r.FilesCopied == 5 && r.BytesCopied == 500, $"件数と容量を数える（{r.FilesCopied}件, {r.BytesCopied}B）");
        Check(!Directory.Exists(dst), "コピーしない");
        Check(Logger.LoadHistory().Count == before, "履歴に残さない");
    }

    private static void PauseResume()
    {
        string src = D("t11src"), dst = Path.Combine(_root, "t11dst");
        for (int i = 0; i < 50; i++) F(Path.Combine(src, $"f{i}.txt"));

        var m = M(new[] { (src, dst) });
        m.Pause();
        var task = Task.Run(() => m.ExecuteAllWithResult());
        Thread.Sleep(800);
        Check(!task.IsCompleted, "一時停止中は進まない");
        m.Resume();
        var r = task.Result;
        Check(r.FilesCopied == 50, $"再開後に完了（{r.FilesCopied}）");
    }

    private static void Cancel()
    {
        string src = D("t12src"), dst = Path.Combine(_root, "t12dst");
        for (int i = 0; i < 50; i++) F(Path.Combine(src, $"f{i}.txt"));

        var m = M(new[] { (src, dst), (src, Path.Combine(_root, "t12dst2")) });
        using var cts = new CancellationTokenSource();
        m.Pause();
        var task = Task.Run(() => m.ExecuteAllWithResult(cts.Token));
        Thread.Sleep(300);
        cts.Cancel();   // 一時停止中でも中止できる
        var r = task.Result;
        Check(r.Cancelled && r.TaskResults.All(t => t.Status == "中止"), "中止として記録");
        Check(!Directory.Exists(Path.Combine(_root, "t12dst2")), "以降のタスクは実行しない");
    }

    private static void HashCompare()
    {
        string src = D("t13src"), dst = Path.Combine(_root, "t13dst");
        for (int i = 0; i < 10; i++) F(Path.Combine(src, $"f{i}.txt"), $"content {i}");
        M(new[] { (src, dst) }).ExecuteAllWithResult();

        foreach (var f in Directory.GetFiles(src)) File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddMinutes(5));
        var r1 = M(new[] { (src, dst) }, options: new BackupOptions { CompareByHash = true }).ExecuteAllWithResult();
        Check(r1.FilesCopied == 0, $"時刻だけ新しく内容が同じならコピーしない（{r1.FilesCopied}）");

        F(Path.Combine(src, "f0.txt"), "changed!", DateTime.UtcNow.AddMinutes(10));
        var r2 = M(new[] { (src, dst) }, options: new BackupOptions { CompareByHash = true }).ExecuteAllWithResult();
        Check(r2.FilesCopied == 1, $"内容が変わったものだけコピー（{r2.FilesCopied}）");

        var r3 = M(new[] { (src, dst) }).ExecuteAllWithResult();
        Check(r3.FilesCopied == 9, $"ハッシュ比較なしなら時刻で判定（{r3.FilesCopied}）");
    }

    private static void TimestampTolerance()
    {
        string src = D("t14src"), dst = D("t14dst");
        var now = DateTime.UtcNow;
        F(Path.Combine(src, "near.txt"), "s", now);
        F(Path.Combine(dst, "near.txt"), "d", now.AddSeconds(-1));
        F(Path.Combine(src, "far.txt"), "s", now);
        F(Path.Combine(dst, "far.txt"), "d", now.AddSeconds(-5));

        var r = M(new[] { (src, dst) }).ExecuteAllWithResult();
        Check(File.ReadAllText(Path.Combine(dst, "near.txt")) == "d", "2秒以内の差はコピーしない");
        Check(File.ReadAllText(Path.Combine(dst, "far.txt")) == "s", "2秒を超える差はコピーする");
    }

    private static void ForcedExcludes()
    {
        var m = M(Array.Empty<(string, string)>());
        var set = (HashSet<string>)typeof(BackupManager).GetField("_ignorePathSet", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(m)!;
        string sysRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        Check(set.Contains(Path.Combine(sysRoot, "pagefile.sys")) && set.Contains(Path.Combine(sysRoot, "System Volume Information")),
            "各ドライブ直下の pagefile.sys・System Volume Information を除外");
    }

    private static void FreeSpaceCheck()
    {
        var method = typeof(BackupManager).GetMethod("HasEnoughSpace", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] enough = { _root, 0L, null };
        object?[] tooMuch = { _root, long.MaxValue / 4, null };
        Check((bool)method.Invoke(null, enough)! && (long)enough[2]! > 0, "必要量が空きより少なければ OK");
        Check(!(bool)method.Invoke(null, tooMuch)!, "必要量が空きを超えれば NG（差分量で判定）");
    }

    private static void Validation()
    {
        string src = D("t15src");
        var used = DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])).ToHashSet();
        char free = "BGIJKLMNOSTUVW".First(c => !used.Contains(c));

        var m = M(new[] { (Path.Combine(_root, "missing"), Path.Combine(_root, "t15ok")), (src, $"{free}:\\backup") });
        var report = m.Validate();
        Check(report.Warnings.Any(w => w.Contains("コピー元が見つかりません")), "コピー元が無ければ注意");
        Check(report.Errors.Any(e => e.Contains("ドライブが見つかりません")), "コピー先のドライブが無ければエラー");
        Check(!report.Errors.Any(e => e.Contains("t15ok")), "書き込めるコピー先はエラーにしない");
    }

    private static void ConfigStoreRoundTrip()
    {
        Directory.CreateDirectory(AppPaths.ConfigDir);
        File.WriteAllText(AppPaths.IgnoreConfigPath,
            "{\"Paths\":[\".tmp\",\"F:\\\\steam\",\"**/Cache/**\"],\"FolderNames\":[\".git\"]}");
        var loaded = ConfigStore.LoadIgnore();
        Check(loaded.Extensions.SequenceEqual(new[] { ".tmp" }) && loaded.Paths.SequenceEqual(new[] { @"F:\steam" })
              && loaded.Patterns.Count == 1 && loaded.FolderNames.SequenceEqual(new[] { ".git" }),
            "旧形式の ignore_config.json を読み込める（FolderNames も保持）");

        ConfigStore.SaveIgnore(loaded);
        string raw = File.ReadAllText(AppPaths.IgnoreConfigPath);
        Check(raw.Contains("\"Extensions\"") && raw.Contains("\"FolderNames\"") && raw.Contains(".git"), "保存時に種類ごとに分けて書く");

        ConfigStore.SaveTasks(new List<BackupTask> { new() { Source = @"E:\", Destination = @"Z:\" } });
        ConfigStore.SaveOptions(new BackupOptions { MirrorMode = true, MaxDegreeOfParallelism = 3 });
        string export = Path.Combine(_root, "export.json");
        ConfigStore.Export(export);
        var bundle = ConfigStore.ReadBundle(export);
        Check(bundle.Tasks.Count == 1 && bundle.Options.MirrorMode && bundle.Options.MaxDegreeOfParallelism == 3
              && bundle.Ignore.FolderNames.Contains(".git"), "エクスポートしたものを読み込める");

        string bad = Path.Combine(_root, "bad.json");
        File.WriteAllText(bad, "{\"SchemaVersion\":1,\"Tasks\":[{\"Source\":\"E:\\\\data\",\"Destination\":\"E:\\\\data\\\\bk\"}]}");
        bool rejected = false;
        try { ConfigStore.ReadBundle(bad); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "危険な設定を含むファイルは読み込まない");

        string future = Path.Combine(_root, "future.json");
        File.WriteAllText(future, "{\"SchemaVersion\":99}");
        bool rejectedVersion = false;
        try { ConfigStore.ReadBundle(future); } catch (InvalidDataException) { rejectedVersion = true; }
        Check(rejectedVersion, "未対応のバージョンは読み込まない");
    }

    private static void Migration()
    {
        string legacyDir = AppPaths.LegacyConfigDir;
        bool createdLegacy = !Directory.Exists(legacyDir);
        try
        {
            Directory.CreateDirectory(legacyDir);
            File.WriteAllText(Path.Combine(legacyDir, "schedule_config.json"), "{\"Frequency\":\"毎週\",\"Time\":\"17:17:00\",\"DaysOfWeek\":[0,3]}");
            if (File.Exists(AppPaths.ScheduleConfigPath)) File.Delete(AppPaths.ScheduleConfigPath);

            AppPaths.MigrateLegacyConfig();
            var schedule = ConfigStore.LoadSchedule();
            Check(schedule != null && schedule.Frequency == "毎週", "旧保存先の設定をコピー");
            Check(File.Exists(Path.Combine(legacyDir, "schedule_config.json")), "旧ファイルは残す");
            Check(schedule!.RunWithHighestPrivileges == null && schedule.EffectiveHighest && schedule.AcPowerOnly,
                "旧設定は最高特権・AC電源のみ（以前の登録内容）として扱う");

            File.WriteAllText(Path.Combine(legacyDir, "schedule_config.json"), "{\"Frequency\":\"毎日\"}");
            AppPaths.MigrateLegacyConfig();
            Check(ConfigStore.LoadSchedule()!.Frequency == "毎週", "移行済みなら上書きしない");
        }
        finally
        {
            if (createdLegacy) Directory.Delete(legacyDir, true);
        }
    }

    private static void MissedRunDetection()
    {
        var weekly = new ScheduleConfig { Frequency = "毎週", Time = new TimeSpan(17, 17, 0), DaysOfWeek = new() { DayOfWeek.Sunday, DayOfWeek.Wednesday } };
        var thursday = new DateTime(2026, 11, 26, 10, 0, 0);
        var wednesdayRun = new DateTime(2026, 11, 25, 17, 17, 0);
        var oldSave = new DateTime(2025, 8, 19);

        Check(SchedulerService.LastScheduledOccurrence(weekly, thursday) == wednesdayRun, "直近の予定（水曜 17:17）");

        var ran = new List<BackupResult> { new() { Timestamp = wednesdayRun.AddSeconds(18) } };
        Check(SchedulerService.FindMissedRun(weekly, ran, thursday, oldSave) == null, "実行済みなら知らせない");

        var notRan = new List<BackupResult> { new() { Timestamp = new DateTime(2026, 11, 22, 17, 17, 0) } };
        Check(SchedulerService.FindMissedRun(weekly, notRan, thursday, oldSave) == wednesdayRun, "実行されていなければ知らせる");
        Check(SchedulerService.FindMissedRun(weekly, notRan, thursday, thursday) == null, "予定の後に設定を保存したなら知らせない");
        Check(SchedulerService.FindMissedRun(weekly, notRan, wednesdayRun.AddMinutes(10), oldSave) == null, "予定時刻の直後は知らせない");

        var cancelled = new List<BackupResult> { new() { Timestamp = wednesdayRun.AddMinutes(1), Cancelled = true } };
        Check(SchedulerService.FindMissedRun(weekly, cancelled, thursday, oldSave) == wednesdayRun, "中止された実行は数えない");

        var daily = new ScheduleConfig { Frequency = "毎日", Time = new TimeSpan(3, 0, 0) };
        Check(SchedulerService.LastScheduledOccurrence(daily, new DateTime(2026, 11, 26, 2, 0, 0)) == new DateTime(2026, 11, 25, 3, 0, 0), "毎日：前日の予定");
        Check(SchedulerService.LastScheduledOccurrence(new ScheduleConfig { Frequency = "起動時" }, thursday) == null, "起動時は対象外");
    }

    private static void History()
    {
        var history = Logger.LoadHistory();
        Check(history.Count > 0 && history.Last().Trigger == "manual" && history.Last().TaskResults.Count > 0, "実行結果を履歴に記録");

        var big = new BackupResult { Timestamp = DateTime.Now, ErrorMessages = Enumerable.Range(0, 5000).Select(i => $"err {i}").ToList(), Errors = 5000 };
        Logger.AppendHistory(big);
        var reloaded = Logger.LoadHistory().Last();
        Check(reloaded.Errors == 5000 && reloaded.ErrorMessages.Count == 100, $"エラー内容は先頭100件だけ保存（{reloaded.ErrorMessages.Count}）");
    }

    private static void AutoParallelAndClassification()
    {
        string sysRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
        var kind = DriveProbe.Detect(sysRoot);
        int dop = DriveProbe.SuggestParallelism(sysRoot, sysRoot, out string why);
        Check(dop is >= 1 and <= 4 && why.Contains("→"), $"ドライブの種類から並列数を決める（{DriveProbe.Label(kind)} → {dop}）");
        Check(DriveProbe.Detect(@"\\server\share\x") == DriveProbe.Kind.Network, "UNC パスはネットワーク");

        Check(BackupManager.IsUnsupportedNameError(new IOException("x", unchecked((int)0x8007007B))), "ERROR_INVALID_NAME は使えない名前");
        Check(BackupManager.IsUnsupportedNameError(new IOException("x", unchecked((int)0x800700CE))), "ERROR_FILENAME_EXCED_RANGE は使えない名前");
        Check(BackupManager.IsUnsupportedNameError(new PathTooLongException()), "パスが長すぎるものは使えない名前");
        Check(!BackupManager.IsUnsupportedNameError(new IOException("x", unchecked((int)0x80070020))), "共有違反は別扱い");

        string src = D("t20src"), dst = Path.Combine(_root, "t20dst");
        for (int i = 0; i < 30; i++) F(Path.Combine(src, $"f{i}.bin"), new string('z', 1000));
        long lastPlanned = 0;
        var progress = new SyncProgress<BackupProgress>(p => lastPlanned = Math.Max(lastPlanned, p.BytesPlanned));
        var r = M(new[] { (src, dst) }).ExecuteAllWithResult(progress: progress);
        Check(r.FilesCopied == 30 && lastPlanned == 30_000, $"進捗にコピー予定量を含める（{lastPlanned}B）");
    }

    // 呼び出したスレッドでそのまま通知する（Progress<T> はスレッドプールに送るため、テストでは順序が読めない）
    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }

    private static void RunLockWorks()
    {
        using (var held = RunLock.TryAcquire())
        {
            Check(held.Acquired, "ロックを取得");
            bool runningSeenFromOtherThread = Task.Run(RunLock.IsBackupRunning).Result;
            Check(runningSeenFromOtherThread, "他から見て実行中");
        }
        Check(!Task.Run(RunLock.IsBackupRunning).Result, "解放後は実行中でない");
    }
}
