using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace FilesBackup.Services
{
    /// <summary>
    /// 待機中のメモリを減らす。トレイ常駐中はほとんど何もしないので、
    /// 画面を閉じたときやバックアップの後に、使わなくなったメモリを OS に返す。
    /// </summary>
    public static class MemoryTrimmer
    {
        [DllImport("kernel32.dll")]
        private static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr minimum, IntPtr maximum);

        /// <param name="workingSet">true なら、物理メモリ上の作業領域も手放す（再び使うときはディスクから読み戻す）</param>
        public static void Trim(bool workingSet = true)
        {
            try
            {
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();

                if (workingSet)
                {
                    using var self = Process.GetCurrentProcess();
                    SetProcessWorkingSetSize(self.Handle, (IntPtr)(-1), (IntPtr)(-1));
                }
            }
            catch { }
        }
    }
}
