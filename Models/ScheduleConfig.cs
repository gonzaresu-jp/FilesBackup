using System.Text.Json.Serialization;

namespace FilesBackup.Models
{
    /// <summary>スケジュール設定（schedule_config.json）</summary>
    public class ScheduleConfig
    {
        /// <summary>実行頻度：毎日・毎週・起動時</summary>
        public string Frequency { get; set; } = "毎日";

        public TimeSpan Time { get; set; }

        /// <summary>毎週のときだけ使う曜日</summary>
        public List<DayOfWeek>? DaysOfWeek { get; set; }

        /// <summary>
        /// 最高特権で実行するか。null は旧バージョンの設定（常に最高特権で登録されていた）なので true として扱う。
        /// </summary>
        public bool? RunWithHighestPrivileges { get; set; }

        /// <summary>AC電源に接続している時だけ実行する（タスク スケジューラの既定と同じく true）</summary>
        public bool AcPowerOnly { get; set; } = true;

        /// <summary>スリープ中ならスリープを解除して実行する</summary>
        public bool WakeToRun { get; set; } = false;

        /// <summary>アプリを開いたとき、予定の実行が行われていなければ知らせる</summary>
        public bool NotifyMissedRuns { get; set; } = true;

        [JsonIgnore]
        public bool EffectiveHighest => RunWithHighestPrivileges ?? true;
    }
}
