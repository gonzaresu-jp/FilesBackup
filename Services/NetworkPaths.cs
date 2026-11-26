using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace FilesBackup.Services
{
    /// <summary>
    /// ネットワークドライブ（Z: など）の扱い。
    ///  - ドライブ文字の割り当てはサインイン中のユーザーにしか見えず、管理者権限のタスクや
    ///    サインイン前の実行では「Z:\ が見つからない」になる。
    ///    そこで、割り当てが見えるときに \\server\share を覚えておき、実行時は UNC パスに読み替える。
    ///  - NAS の電源が切れている・認証が通らないなどで接続できない場合の判定と待機。
    /// </summary>
    public static class NetworkPaths
    {
        [DllImport("mpr.dll", CharSet = CharSet.Unicode)]
        private static extern int WNetGetConnection(string localName, StringBuilder remoteName, ref int length);

        private const int NoError = 0;
        private const int ErrorConnectionUnavail = 1201;   // 割り当てはあるが、今はつながっていない

        private static readonly object Gate = new();
        private static Dictionary<string, string>? _remembered;
        private static readonly Dictionary<char, (string? Unc, long Ticks)> LiveCache = new();
        private const long LiveCacheMs = 30_000;

        // 接続が切れたときに読み替える Win32 エラー（ネットワーク名が見つからない・接続が切れた など）
        private static readonly HashSet<int> NetworkErrors = new() { 51, 53, 59, 64, 67, 121, 1222, 1231, 1232, 1236, 2250 };

        // 認証が通らないときのエラー
        private static readonly HashSet<int> AuthErrors = new() { 5, 86, 1244, 1326, 1327, 1330, 1331, 1385, 1907, 2242 };

        public static bool IsUnc(string path) => path.StartsWith(@"\\", StringComparison.Ordinal);

        /// <summary>今のユーザーで見えている割り当て（無ければ null）</summary>
        public static string? GetLiveMapping(char letter)
        {
            letter = char.ToUpperInvariant(letter);
            lock (Gate)
            {
                if (LiveCache.TryGetValue(letter, out var hit) && Environment.TickCount64 - hit.Ticks < LiveCacheMs)
                    return hit.Unc;
            }

            string? unc = null;
            try
            {
                var sb = new StringBuilder(512);
                int len = sb.Capacity;
                int rc = WNetGetConnection($"{letter}:", sb, ref len);
                if ((rc == NoError || rc == ErrorConnectionUnavail) && sb.Length > 0 && IsUnc(sb.ToString()))
                    unc = sb.ToString().TrimEnd('\\');
            }
            catch { }

            lock (Gate) LiveCache[letter] = (unc, Environment.TickCount64);
            return unc;
        }

        /// <summary>覚えている割り当て（ドライブ文字 → \\server\share）</summary>
        public static IReadOnlyDictionary<string, string> Remembered
        {
            get
            {
                lock (Gate)
                {
                    if (_remembered == null)
                    {
                        _remembered = new(StringComparer.OrdinalIgnoreCase);
                        try
                        {
                            if (File.Exists(AppPaths.NetworkDrivesPath))
                            {
                                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(AppPaths.NetworkDrivesPath));
                                if (map != null)
                                    foreach (var kv in map)
                                        if (kv.Key.Length == 1 && char.IsLetter(kv.Key[0]) && IsUnc(kv.Value))
                                            _remembered[kv.Key.ToUpperInvariant()] = kv.Value.TrimEnd('\\');
                            }
                        }
                        catch { }
                    }
                    return new Dictionary<string, string>(_remembered, StringComparer.OrdinalIgnoreCase);
                }
            }
        }

        /// <summary>今見えているネットワークドライブの割り当てを覚える（変わっていれば保存）</summary>
        public static void Remember()
        {
            var current = Remembered;
            var updated = new Dictionary<string, string>(current, StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var drive in DriveInfo.GetDrives())
                {
                    if (drive.DriveType != DriveType.Network) continue;
                    char letter = char.ToUpperInvariant(drive.Name[0]);
                    string? unc = GetLiveMapping(letter);
                    if (unc != null) updated[letter.ToString()] = unc;
                }
            }
            catch { }

            bool changed = updated.Count != current.Count ||
                           updated.Any(kv => !current.TryGetValue(kv.Key, out var v) || !string.Equals(v, kv.Value, StringComparison.OrdinalIgnoreCase));
            if (!changed) return;

            SetRemembered(updated);
        }

        /// <summary>覚えている割り当てを置き換える（テスト用にも使う）</summary>
        public static void SetRemembered(IDictionary<string, string> map)
        {
            lock (Gate)
            {
                _remembered = new Dictionary<string, string>(map, StringComparer.OrdinalIgnoreCase);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.NetworkDrivesPath)!);
                    File.WriteAllText(AppPaths.NetworkDrivesPath,
                        JsonSerializer.Serialize(_remembered.OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value),
                            new JsonSerializerOptions { WriteIndented = true }));
                }
                catch { }
            }
        }

        /// <summary>
        /// ネットワークドライブのパスを UNC パスに読み替える（Z:\foo → \\server\share\foo）。
        /// ローカルドライブや、割り当てが分からないパスはそのまま返す。
        /// </summary>
        public static string Resolve(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length < 2 || path[1] != ':' || !char.IsAsciiLetter(path[0]))
                return path;

            char letter = char.ToUpperInvariant(path[0]);
            string? unc = GetLiveMapping(letter);

            if (unc == null)
            {
                // 割り当てが見えない。そのドライブ文字が実在するなら（ローカルドライブ）そのまま使う
                bool exists;
                try { exists = Directory.Exists($"{letter}:\\"); } catch { exists = false; }
                if (exists || !Remembered.TryGetValue(letter.ToString(), out unc))
                    return path;
            }

            string rest = path.Substring(2).TrimStart('\\', '/');
            return rest.Length == 0 ? unc + "\\" : unc + "\\" + rest;
        }

        /// <summary>そのパスのドライブ（共有）に今つながるか。つながらない場合は理由を返す</summary>
        public static bool IsReachable(string path, out string reason)
        {
            reason = "";
            string? root;
            try { root = Path.GetPathRoot(Path.GetFullPath(path)); }
            catch (Exception ex) { reason = ex.Message; return false; }
            if (string.IsNullOrEmpty(root)) { reason = "パスが不正です"; return false; }

            try
            {
                if (Directory.Exists(root)) return true;
            }
            catch { }

            if (!IsUnc(root))
            {
                reason = $"{root} が見つかりません（ドライブが接続されているか確認してください）";
                return false;
            }

            // 共有に接続できない理由を調べる（認証・NAS の電源など）
            try
            {
                using var e = Directory.EnumerateFileSystemEntries(root).GetEnumerator();
                e.MoveNext();
                return true;
            }
            catch (Exception ex)
            {
                reason = Describe(ex, root);
                return false;
            }
        }

        /// <summary>接続できない理由を、利用者が対処しやすい文に直す</summary>
        public static string Describe(Exception ex, string root)
        {
            int code = ex.HResult & 0xFFFF;
            bool unc = IsUnc(root);

            if (unc && (ex is UnauthorizedAccessException || AuthErrors.Contains(code)))
                return $"{root} の認証が通りません。Windows の「資格情報マネージャー」で、この NAS/共有のユーザー名とパスワードを登録してください";
            if (unc && (NetworkErrors.Contains(code) || ex is DirectoryNotFoundException))
                return $"{root} に接続できません（NAS の電源やネットワークを確認してください）";
            if (ex is DirectoryNotFoundException || code == 3 || code == 21)
                return $"{root} が見つかりません（ドライブが接続されているか確認してください）";
            return $"{root}: {ex.Message}";
        }

        /// <summary>ネットワーク障害が原因と思われる IO エラーか</summary>
        public static bool IsNetworkError(Exception ex) =>
            ex is IOException && NetworkErrors.Contains(ex.HResult & 0xFFFF);

        /// <summary>
        /// つながるまで待つ（maxWait まで、interval おきに確認）。
        /// onWaiting は待ち始めたときに1回呼ぶ。つながれば true。
        /// </summary>
        public static bool WaitUntilReachable(string path, TimeSpan maxWait, CancellationToken ct, out string reason,
                                              Action<string>? onWaiting = null, TimeSpan? interval = null)
        {
            if (IsReachable(path, out reason))
                return true;
            if (maxWait <= TimeSpan.Zero)
                return false;

            onWaiting?.Invoke(reason);
            var step = interval ?? TimeSpan.FromSeconds(15);
            var deadline = DateTime.UtcNow + maxWait;
            while (DateTime.UtcNow < deadline)
            {
                if (ct.WaitHandle.WaitOne(step))
                    ct.ThrowIfCancellationRequested();
                lock (Gate) LiveCache.Clear();
                if (IsReachable(path, out reason))
                    return true;
            }
            return false;
        }

        // ================= 空き容量 =================

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable,
                                                     out ulong totalBytes, out ulong totalFreeBytes);

        /// <summary>空き容量（UNC パスでも取得できる）。取得できなければ -1</summary>
        public static long GetFreeSpace(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return -1;
                if (!root.EndsWith('\\')) root += "\\";
                return GetDiskFreeSpaceEx(root, out ulong free, out _, out _) ? (long)Math.Min(free, long.MaxValue) : -1;
            }
            catch
            {
                return -1;
            }
        }
    }
}
