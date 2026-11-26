using System.Text.Encodings.Web;
using System.Text.Json;
using FilesBackup.Models;

namespace FilesBackup.Services
{
    /// <summary>設定ファイルの読み書きと、設定のインポート/エクスポート</summary>
    public static class ConfigStore
    {
        private static readonly JsonSerializerOptions WriteOptions = new()
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,   // 日本語をエスケープせず読みやすく保存
        };

        private static readonly JsonSerializerOptions ReadOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        // ---- 個別の設定 ----

        public static List<BackupTask> LoadTasks() =>
            Read<List<BackupTask>>(AppPaths.BackupConfigPath) ?? new();

        public static void SaveTasks(List<BackupTask> tasks) =>
            Write(AppPaths.BackupConfigPath, tasks);

        public static IgnoreConfig LoadIgnore() =>
            (Read<IgnoreConfig>(AppPaths.IgnoreConfigPath) ?? new()).Normalize();

        public static void SaveIgnore(IgnoreConfig config) =>
            Write(AppPaths.IgnoreConfigPath, config.Normalize());

        public static ScheduleConfig? LoadSchedule() =>
            Read<ScheduleConfig>(AppPaths.ScheduleConfigPath);

        public static void SaveSchedule(ScheduleConfig config) =>
            Write(AppPaths.ScheduleConfigPath, config);

        public static BackupOptions LoadOptions() =>
            Read<BackupOptions>(AppPaths.OptionsConfigPath) ?? new();

        public static void SaveOptions(BackupOptions options) =>
            Write(AppPaths.OptionsConfigPath, options);

        public static AppSettings LoadAppSettings()
        {
            try { return Read<AppSettings>(AppPaths.AppSettingsPath) ?? new(); }
            catch { return new(); }
        }

        public static void SaveAppSettings(AppSettings settings) =>
            Write(AppPaths.AppSettingsPath, settings);

        // ---- インポート/エクスポート ----

        public static SettingsBundle CreateBundle() => new()
        {
            SchemaVersion = SettingsBundle.CurrentSchemaVersion,
            ExportedAt = DateTime.Now,
            AppVersion = typeof(ConfigStore).Assembly.GetName().Version?.ToString() ?? "",
            Tasks = LoadTasks(),
            Ignore = LoadIgnore(),
            Schedule = LoadSchedule(),
            Options = LoadOptions(),
        };

        public static void Export(string path) => Write(path, CreateBundle());

        /// <summary>エクスポートファイルを読み込んで検証する（まだ保存はしない）</summary>
        public static SettingsBundle ReadBundle(string path)
        {
            var bundle = Read<SettingsBundle>(path)
                ?? throw new InvalidDataException("設定ファイルを読み込めませんでした。");

            if (bundle.SchemaVersion < 1 || bundle.SchemaVersion > SettingsBundle.CurrentSchemaVersion)
                throw new InvalidDataException($"対応していない形式のバージョンです（SchemaVersion: {bundle.SchemaVersion}）。");

            bundle.Tasks ??= new();
            bundle.Ignore = (bundle.Ignore ?? new()).Normalize();
            bundle.Options ??= new();

            if (bundle.Tasks.Any(t => t == null))
                throw new InvalidDataException("バックアップ設定に空の項目があります。");

            var errors = BackupManager.FindConfigErrors(bundle.Tasks);
            if (errors.Count > 0)
                throw new InvalidDataException("危険な設定が含まれているため読み込めません。\n" + string.Join("\n", errors));

            return bundle;
        }

        /// <summary>検証済みの設定を保存する（スケジュールはタスク スケジューラへの登録を別途行う）</summary>
        public static void ApplyBundle(SettingsBundle bundle)
        {
            SaveTasks(bundle.Tasks);
            SaveIgnore(bundle.Ignore);
            SaveOptions(bundle.Options);
            if (bundle.Schedule != null)
                SaveSchedule(bundle.Schedule);
        }

        // ---- 共通 ----

        private static T? Read<T>(string path) where T : class
        {
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), ReadOptions);
        }

        // 書き込み途中で落ちても壊れないよう、一時ファイルに書いてから置き換える
        private static void Write<T>(string path, T value)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, WriteOptions));
            File.Move(temp, path, overwrite: true);
        }
    }
}
