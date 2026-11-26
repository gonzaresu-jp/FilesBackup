using System.Text.Json;
using FilesBackup.Models;

namespace FilesBackup.Services
{
    public class BackupManager
    {
        private static readonly string LogDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FilesBackup", "logs");

        public List<BackupTask> Tasks { get; private set; } = new();

        public List<string> IgnorePaths { get; set; } = new();         // 除外パス（ファイル・フォルダ）
        public List<string> IgnoreExtensions { get; set; } = new();    // 除外拡張子（例: .log, .tmp）

        // ジャンクション/シンボリックリンク（再解析ポイント）を辿るか。既定は辿らない。
        public bool FollowReparsePoints { get; set; } = false;

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
        }

        public void LoadIgnoreList(string ignorePath)
        {
            if (File.Exists(ignorePath))
            {
                string json = File.ReadAllText(ignorePath);
                var config = JsonSerializer.Deserialize<IgnoreConfig>(json);

                if (config != null)
                {
                    IgnorePaths = config.Paths
                        .Where(x => !x.StartsWith("*.") && !x.StartsWith("."))
                        .ToList();

                    IgnoreExtensions = config.Paths
                        .Where(x => x.StartsWith("*.") || x.StartsWith("."))
                        .Select(x => x.TrimStart('*').ToLowerInvariant())
                        .ToList();
                }
            }

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

        public int ExecuteAll()
        {
            int totalCopied = 0;
            var ignores = IgnorePaths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();

            foreach (var task in Tasks)
            {
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

                    // コピー先がコピー元（または同一）の配下だと、コピーが自分自身を再帰的に読み続けるため実行しない
                    if (IsSameOrInside(task.Destination, task.Source))
                    {
                        Log($"[SKIP] コピー先がコピー元の配下にあるため実行しません: {task.Source} → {task.Destination}");
                        continue;
                    }

                    int copied = Copy(task.Source, task.Destination, ignores);
                    totalCopied += copied;
                    Log($"[{task.Source} → {task.Destination}] {copied} 個コピーしました");
                }
                catch (Exception ex)
                {
                    Log($"[ERROR] {task.Source} → {task.Destination}: {ex.Message}");
                }
            }
            return totalCopied;
        }

        private int Copy(string src, string dst, IReadOnlyList<string> ignores)
        {
            int count = 0;

            foreach (var file in EnumerateFilesSafe(src, ignores))
            {
                string relative = Path.GetRelativePath(src, file);
                string extension = Path.GetExtension(file).ToLowerInvariant();

                if (IgnoreExtensions.Contains(extension))
                    continue;

                if (ignores.Any(ignore => IsSameOrInside(file, ignore)))
                    continue;

                string destPath = Path.Combine(dst, relative);

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

                    if (!File.Exists(destPath) ||
                        File.GetLastWriteTimeUtc(file) > File.GetLastWriteTimeUtc(destPath))
                    {
                        File.Copy(file, destPath, true);
                        count++;
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
            }

            return count;
        }

        private IEnumerable<string> EnumerateFilesSafe(string root, IReadOnlyList<string> ignores)
        {
            var dirs = new Stack<string>();
            dirs.Push(root);

            while (dirs.Count > 0)
            {
                string currentDir = dirs.Pop();

                // 除外対象ディレクトリならスキップ
                if (ignores.Any(ignore => IsSameOrInside(currentDir, ignore)))
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
                    if (!FollowReparsePoints && IsReparsePoint(file))
                    {
                        Log($"[SKIP] ジャンクション/シンボリックリンクを除外: {file}");
                        continue;
                    }
                    yield return file;
                }

                foreach (var dir in subDirs)
                {
                    if (!FollowReparsePoints && IsReparsePoint(dir))
                    {
                        Log($"[SKIP] ジャンクション/シンボリックリンクを除外: {dir}");
                        continue;
                    }
                    dirs.Push(dir);
                }
            }
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

        // コンソール（自動実行時は見えない）と日付別ログファイルの両方へ出力
        private static void Log(string message)
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
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
