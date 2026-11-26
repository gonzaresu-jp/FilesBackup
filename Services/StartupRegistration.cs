using Microsoft.Win32;

namespace FilesBackup.Services
{
    /// <summary>Windows のサインイン時に、タスクトレイに常駐した状態で起動する（HKCU\...\Run）</summary>
    public static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "FilesBackup";
        public const string TrayArg = "--tray";

        public static string CommandFor(string exePath) => $"\"{exePath}\" {TrayArg}";

        public static string? Current()
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) as string;
        }

        /// <summary>この exe を起動する登録だけを消す（別の場所に入れた FilesBackup の登録は残す）</summary>
        public static void RemoveIfOwnedBy(string exePath)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(ValueName) is string command &&
                command.Contains(exePath, StringComparison.OrdinalIgnoreCase))
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }

        /// <summary>設定に合わせて登録・解除する（同じ内容なら何もしない）</summary>
        public static void Apply(bool enabled, string exePath)
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);

            if (enabled)
            {
                string command = CommandFor(exePath);
                if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(ValueName, command);
            }
            else if (key.GetValue(ValueName) != null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
    }
}
