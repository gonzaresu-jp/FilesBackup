namespace FilesBackup.Services
{
    /// <summary>
    /// バックアップの同時実行を防ぐロック（名前付きミューテックス）。
    /// 自動実行（タスク スケジューラ）と画面からの手動実行が重ならないようにする。
    /// 取得したスレッドで Dispose すること（ミューテックスはスレッドに紐づくため）。
    /// </summary>
    public sealed class RunLock : IDisposable
    {
        private const string MutexName = "FilesBackupRunLock";

        private readonly Mutex? _mutex;
        public bool Acquired { get; }

        private RunLock(Mutex? mutex, bool acquired)
        {
            _mutex = mutex;
            Acquired = acquired;
        }

        public static RunLock TryAcquire()
        {
            Mutex? mutex = null;
            try
            {
                mutex = new Mutex(false, MutexName);
                bool acquired;
                try
                {
                    acquired = mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    acquired = true;   // 前の実行が異常終了していた
                }
                return new RunLock(mutex, acquired);
            }
            catch (UnauthorizedAccessException)
            {
                // 管理者権限で動いている実行が作ったミューテックスは、標準権限からは開けない＝実行中
                mutex?.Dispose();
                return new RunLock(null, false);
            }
        }

        /// <summary>他でバックアップが実行中か</summary>
        public static bool IsBackupRunning()
        {
            using var probe = TryAcquire();
            return !probe.Acquired;
        }

        public void Dispose()
        {
            if (_mutex == null) return;
            if (Acquired)
            {
                try { _mutex.ReleaseMutex(); } catch (ApplicationException) { }
            }
            _mutex.Dispose();
        }
    }
}
