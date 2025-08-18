using System.Text.Json;
using FilesBackup.Models;

namespace FilesBackup.Services
{
    public class BackupManager
    {
        public List<BackupTask> Tasks { get; private set; } = new();

        public List<string> IgnorePaths { get; set; } = new();         // 除外パス（ファイル・フォルダ）
        public List<string> IgnoreExtensions { get; set; } = new();    // 除外拡張子（例: .log, .tmp）

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
            foreach (var task in Tasks)
            {
                int copied = Copy(task.Source, task.Destination);
                totalCopied += copied;
                Console.WriteLine($"[{task.Source} → {task.Destination}] {copied} 個コピーしました");
            }
            return totalCopied;
        }

        private int Copy(string src, string dst)
        {
            int count = 0;

            foreach (var file in EnumerateFilesSafe(src))
            {
                string fullPath = Path.GetFullPath(file);
                string relative = Path.GetRelativePath(src, file);
                string extension = Path.GetExtension(file).ToLowerInvariant();

                if (IgnoreExtensions.Contains(extension))
                    continue;

                if (IgnorePaths.Any(ignore =>
                    fullPath.StartsWith(Path.GetFullPath(ignore), StringComparison.OrdinalIgnoreCase)))
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
                    Console.WriteLine($"アクセス拒否: {file} - {ex.Message}");
                }
                catch (IOException ex)
                {
                    Console.WriteLine($"IOエラー: {file} - {ex.Message}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"予期せぬエラー: {file} - {ex.Message}");
                }
            }

            return count;
        }

        private IEnumerable<string> EnumerateFilesSafe(string root)
        {
            var dirs = new Stack<string>();
            dirs.Push(root);

            while (dirs.Count > 0)
            {
                string currentDir = dirs.Pop();

                // 除外対象ディレクトリならスキップ
                if (IgnorePaths.Any(ignore =>
                    Path.GetFullPath(currentDir).Equals(Path.GetFullPath(ignore), StringComparison.OrdinalIgnoreCase)))
                    continue;

                string[] files = Array.Empty<string>();
                try
                {
                    files = Directory.GetFiles(currentDir);
                }
                catch (UnauthorizedAccessException) { continue; }

                foreach (var file in files)
                    yield return file;

                string[] subDirs = Array.Empty<string>();
                try
                {
                    subDirs = Directory.GetDirectories(currentDir);
                }
                catch (UnauthorizedAccessException) { continue; }

                foreach (var dir in subDirs)
                    dirs.Push(dir);
            }
        }
    }
}
