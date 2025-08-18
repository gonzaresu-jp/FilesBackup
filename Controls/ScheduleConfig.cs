namespace FilesBackup.Controls
{
    public class ScheduleConfig
    {
        /// <summary>実行頻度：毎日・毎週・起動時</summary>
        public string Frequency { get; set; } = "毎日";

        /// <summary>実行時刻。起動時なら null</summary>
        public TimeSpan? Time { get; set; } = null;

        /// <summary>毎週のときだけ有効な曜日リスト</summary>
        public List<DayOfWeek> WeekDays { get; set; } = new();
    }
}
