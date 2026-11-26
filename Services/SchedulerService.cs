using FilesBackup.Models;
using Microsoft.Win32.TaskScheduler;

namespace FilesBackup.Services
{
    /// <summary>Windows タスク スケジューラへの登録と、予定実行の取りこぼし判定</summary>
    public static class SchedulerService
    {
        public const string TaskName = "FilesBackupAutoTask";

        public static void Register(ScheduleConfig config, string exePath)
        {
            using var ts = new TaskService();

            if (ts.FindTask(TaskName) != null)
                ts.RootFolder.DeleteTask(TaskName);

            TaskDefinition td = ts.NewTask();
            td.RegistrationInfo.Description = "FilesBackup 定期バックアップタスク";

            // 予定時刻に PC が起動していなかった場合、起動後すぐに実行する
            td.Settings.StartWhenAvailable = true;
            td.Settings.MultipleInstances = TaskInstancesPolicy.IgnoreNew;

            // 既定（72時間）で打ち切られないようにする（初回の全体コピーは長時間かかる）
            td.Settings.ExecutionTimeLimit = TimeSpan.Zero;

            // 失敗時の再試行（自動実行が異常終了したとき 15 分おきに最大 3 回）
            td.Settings.RestartCount = 3;
            td.Settings.RestartInterval = TimeSpan.FromMinutes(15);

            // 電源条件
            td.Settings.DisallowStartIfOnBatteries = config.AcPowerOnly;
            td.Settings.StopIfGoingOnBatteries = config.AcPowerOnly;
            td.Settings.WakeToRun = config.WakeToRun;

            // 既定は標準権限。管理者権限が必要なコピー元/先がある場合だけ最高特権にする
            td.Principal.RunLevel = config.EffectiveHighest ? TaskRunLevel.Highest : TaskRunLevel.LUA;

            switch (config.Frequency)
            {
                case "毎日":
                    td.Triggers.Add(new DailyTrigger { DaysInterval = 1, StartBoundary = DateTime.Today + config.Time });
                    break;

                case "毎週" when config.DaysOfWeek is { Count: > 0 }:
                    // WeeklyTrigger の既定値は日曜日なので、そこに OR すると常に日曜が含まれてしまう。先に組み立てて代入する
                    DaysOfTheWeek mask = 0;
                    foreach (var day in config.DaysOfWeek)
                        mask |= (DaysOfTheWeek)(1 << (int)day);
                    td.Triggers.Add(new WeeklyTrigger { StartBoundary = DateTime.Today + config.Time, DaysOfWeek = mask });
                    break;

                case "起動時":
                    td.Triggers.Add(new BootTrigger());
                    break;

                default:
                    throw new InvalidOperationException("スケジュール設定内容が不完全です（毎週の場合は曜日を選んでください）。");
            }

            td.Actions.Add(new ExecAction(exePath, "auto", null));
            ts.RootFolder.RegisterTaskDefinition(TaskName, td);
        }

        public record TaskStatusInfo(bool Exists, DateTime? NextRun, DateTime? LastRun, string RunLevel, string ExecutablePath, string? Error = null);

        public static TaskStatusInfo GetStatus()
        {
            try
            {
                using var ts = new TaskService();
                var task = ts.FindTask(TaskName);
                if (task == null)
                    return new TaskStatusInfo(false, null, null, "", "");

                DateTime? next = task.NextRunTime > new DateTime(2000, 1, 1) ? task.NextRunTime : null;
                DateTime? last = task.LastRunTime > new DateTime(2000, 1, 1) ? task.LastRunTime : null;
                string level = task.Definition.Principal.RunLevel == TaskRunLevel.Highest ? "最高特権" : "標準権限";
                string exe = task.Definition.Actions.OfType<ExecAction>().FirstOrDefault()?.Path ?? "";
                return new TaskStatusInfo(true, next, last, level, exe);
            }
            catch (Exception ex)
            {
                return new TaskStatusInfo(false, null, null, "", "", ex.Message);
            }
        }

        /// <summary>now 以前で直近の予定実行時刻（起動時トリガーや不正な設定なら null）</summary>
        public static DateTime? LastScheduledOccurrence(ScheduleConfig config, DateTime now)
        {
            if (config.Frequency == "毎日")
            {
                DateTime today = now.Date + config.Time;
                return today <= now ? today : today.AddDays(-1);
            }

            if (config.Frequency == "毎週" && config.DaysOfWeek is { Count: > 0 })
            {
                for (int back = 0; back <= 7; back++)
                {
                    DateTime candidate = now.Date.AddDays(-back) + config.Time;
                    if (candidate <= now && config.DaysOfWeek.Contains(candidate.DayOfWeek))
                        return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// 予定の実行が行われていない場合、その予定時刻を返す。
        /// 判定材料：直近の予定時刻より後に開始した実行が履歴に無い／設定がその後に変更されていない。
        /// </summary>
        public static DateTime? FindMissedRun(ScheduleConfig config, IEnumerable<BackupResult> history, DateTime now, DateTime configSavedAt)
        {
            DateTime? scheduled = LastScheduledOccurrence(config, now);
            if (scheduled == null)
                return null;

            // 予定時刻の直後はまだ実行中・開始待ちの可能性があるので判定しない
            if (now - scheduled.Value < TimeSpan.FromMinutes(30))
                return null;

            // 予定時刻より後に設定を保存した（＝その回はまだ登録されていなかった）
            if (configSavedAt > scheduled.Value)
                return null;

            bool ran = history.Any(h => !h.Cancelled && h.Timestamp >= scheduled.Value.AddMinutes(-5));
            return ran ? null : scheduled;
        }
    }
}
