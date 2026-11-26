using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace FilesBackup.Services
{
    /// <summary>
    /// ドライブの種類（SSD / HDD / ネットワーク）を調べ、並列コピー数の目安を決める。
    /// HDD はヘッドの移動が増えると遅くなるため 1、SSD・ネットワークは同時転送が効くため 4。
    /// </summary>
    public static class DriveProbe
    {
        public enum Kind { Unknown, Ssd, Hdd, Network }

        private static readonly ConcurrentDictionary<char, Kind> Cache = new();

        public static Kind Detect(string path)
        {
            try
            {
                string? root = Path.GetPathRoot(Path.GetFullPath(path));
                if (string.IsNullOrEmpty(root)) return Kind.Unknown;
                if (root.StartsWith(@"\\")) return Kind.Network;

                char letter = char.ToUpperInvariant(root[0]);
                return Cache.GetOrAdd(letter, l =>
                {
                    var drive = new DriveInfo(root);
                    if (drive.DriveType == DriveType.Network) return Kind.Network;
                    return QuerySeekPenalty(l) switch
                    {
                        true => Kind.Hdd,
                        false => Kind.Ssd,
                        _ => Kind.Unknown,
                    };
                });
            }
            catch
            {
                return Kind.Unknown;
            }
        }

        public static string Label(Kind kind) => kind switch
        {
            Kind.Ssd => "SSD",
            Kind.Hdd => "HDD",
            Kind.Network => "ネットワーク",
            _ => "不明",
        };

        /// <summary>コピー元とコピー先の種類から並列数を決める</summary>
        public static int SuggestParallelism(string src, string dst, out string reason)
        {
            var a = Detect(src);
            var b = Detect(dst);
            reason = $"{Label(a)} → {Label(b)}";

            if (a == Kind.Hdd || b == Kind.Hdd) return 1;
            if (a == Kind.Network || b == Kind.Network) return 4;
            if (a == Kind.Ssd && b == Kind.Ssd) return 4;
            return 2;
        }

        // ---- IOCTL_STORAGE_QUERY_PROPERTY（StorageDeviceSeekPenaltyProperty）----

        private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x2D1400;
        private const int StorageDeviceSeekPenaltyProperty = 7;
        private const int PropertyStandardQuery = 0;

        [StructLayout(LayoutKind.Sequential)]
        private struct STORAGE_PROPERTY_QUERY
        {
            public int PropertyId;
            public int QueryType;
            public int AdditionalParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DEVICE_SEEK_PENALTY_DESCRIPTOR
        {
            public uint Version;
            public uint Size;
            public byte IncursSeekPenalty;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
            uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(SafeFileHandle device, uint code,
            ref STORAGE_PROPERTY_QUERY input, int inputSize,
            out DEVICE_SEEK_PENALTY_DESCRIPTOR output, int outputSize,
            out uint returned, IntPtr overlapped);

        // 管理者権限なしで問い合わせられるよう、アクセス権 0 で開く
        private static bool? QuerySeekPenalty(char letter)
        {
            using var handle = CreateFile($@"\\.\{letter}:", 0, 3 /* 読み書き共有 */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            if (handle.IsInvalid) return null;

            var query = new STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceSeekPenaltyProperty, QueryType = PropertyStandardQuery };
            bool ok = DeviceIoControl(handle, IOCTL_STORAGE_QUERY_PROPERTY,
                ref query, Marshal.SizeOf<STORAGE_PROPERTY_QUERY>(),
                out var desc, Marshal.SizeOf<DEVICE_SEEK_PENALTY_DESCRIPTOR>(),
                out _, IntPtr.Zero);
            return ok ? desc.IncursSeekPenalty != 0 : null;
        }
    }
}
