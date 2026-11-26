using System.Text.Encodings.Web;
using System.Text.Json;
using FilesBackup.Models;

namespace FilesBackup.Services
{
    public static class Logger
    {
        private const int LogRetentionDays = 30;        // ログの保持日数
        private const int MaxHistoryEntries = 100;      // 履歴の最大件数
        private const int MaxErrorMessagesPerEntry = 100; // 1回分の履歴に残すエラー数（多すぎると履歴が肥大化する）

        private static readonly object _lock = new();

        private static readonly JsonSerializerOptions HistoryJsonOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public static void Info(string message) => Write("INFO", message);
        public static void Warn(string message) => Write("WARN", message);
        public static void Error(string message, Exception? ex = null)
        {
            Write("ERROR", ex == null ? message : $"{message}: {ex.Message}");
        }

        /// <summary>日付が変わっても当日のファイルへ書く</summary>
        public static string GetLogPath() =>
            Path.Combine(AppPaths.LogDir, $"backup_{DateTime.Now:yyyy-MM-dd}.log");

        private static void Write(string level, string message)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.LogDir);
                    var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [{level}] {message}";
                    File.AppendAllText(GetLogPath(), line + Environment.NewLine);
                }
                catch { }
            }
        }

        /// <summary>保持日数より古いログを削除する</summary>
        public static void CleanOldLogs()
        {
            try
            {
                if (!Directory.Exists(AppPaths.LogDir)) return;
                DateTime limit = DateTime.Now.AddDays(-LogRetentionDays);
                foreach (var file in Directory.GetFiles(AppPaths.LogDir, "*.log"))
                {
                    if (File.GetLastWriteTime(file) < limit)
                        File.Delete(file);
                }
            }
            catch { }
        }

        public static List<BackupResult> LoadHistory()
        {
            lock (_lock)
            {
                try
                {
                    if (!File.Exists(AppPaths.HistoryPath)) return new();
                    return JsonSerializer.Deserialize<List<BackupResult>>(File.ReadAllText(AppPaths.HistoryPath)) ?? new();
                }
                catch { return new(); }
            }
        }

        public static void SaveHistory(List<BackupResult> history)
        {
            lock (_lock)
            {
                try
                {
                    Directory.CreateDirectory(AppPaths.DataDir);
                    if (history.Count > MaxHistoryEntries)
                        history = history.TakeLast(MaxHistoryEntries).ToList();

                    foreach (var h in history)
                    {
                        if (h.ErrorMessages.Count > MaxErrorMessagesPerEntry)
                            h.ErrorMessages = h.ErrorMessages.Take(MaxErrorMessagesPerEntry).ToList();
                    }

                    string temp = AppPaths.HistoryPath + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(history, HistoryJsonOptions));
                    File.Move(temp, AppPaths.HistoryPath, overwrite: true);
                }
                catch { }
            }
        }

        public static void AppendHistory(BackupResult result)
        {
            lock (_lock)
            {
                var history = LoadHistory();
                history.Add(result);
                SaveHistory(history);
            }
        }
    }
}
