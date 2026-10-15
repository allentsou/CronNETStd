using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CronNET
{
    /// <summary>
    /// Contract for the cron daemon scheduler.
    /// <para>Cron 排程守護程序合約介面。</para>
    /// </summary>
    public interface ICronDaemon : IDisposable
    {
        /// <summary>
        /// Adds a simple task (cannot be cancelled mid-execution, Cancel only prevents next schedule). Returns job for tracking/removal.
        /// <para>新增簡易任務（不可中途取消，Cancel 僅阻止下次排程）。回傳 job 以便追蹤或移除。</para>
        /// </summary>
        ICronJob AddJob(string schedule, ThreadStart action);

        /// <summary>
        /// Adds a cancellable synchronous task.
        /// <para>新增可取消的同步任務。</para>
        /// </summary>
        ICronJob AddJob(string schedule, Action<CancellationToken> handler);

        /// <summary>
        /// Adds a cancellable asynchronous task (recommended).
        /// <para>新增可取消的非同步任務（建議）。</para>
        /// </summary>
        ICronJob AddJob(string schedule, Func<CancellationToken, Task> handler);

        /// <summary>
        /// Removes the specified job from the scheduler.
        /// <para>從排程器中移除指定的任務。</para>
        /// </summary>
        bool RemoveJob(ICronJob job);

        /// <summary>
        /// Clears and cancels all scheduled jobs.
        /// <para>清除並取消所有排程任務。</para>
        /// </summary>
        void Clear();

        /// <summary>
        /// Starts the cron scheduler daemon.
        /// <para>啟動 Cron 排程守護程序。</para>
        /// </summary>
        void Start();

        /// <summary>
        /// Signals cancellation and returns immediately (does not wait for running tasks).
        /// Semantics: stops timer -> cancels daemon token -> signals Cancel() to each individual job.
        /// <para>發出取消訊號後立即返回（不等待執行中任務）。語意：停 timer → 取消 daemon token → 對每個別 job 發 Cancel()。</para>
        /// </summary>
        void Stop();

        /// <summary>
        /// After signaling cancellation, waits up to timeout for running tasks to finish (does not forcibly abort).
        /// <para>發出取消訊號後，最多等待 timeout 讓執行中任務收尾（不會強制終止）。</para>
        /// </summary>
        Task StopAsync(TimeSpan timeout);

        /// <summary>
        /// Gets a value indicating whether the scheduler daemon is running.
        /// <para>取得一個值，指出排程守護程序目前是否正在運作中。</para>
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Gets the read-only snapshot list of scheduled jobs.
        /// <para>取得目前已排程任務的唯讀快照清單。</para>
        /// </summary>
        IReadOnlyList<ICronJob> Jobs { get; }

        /// <summary>
        /// Event raised when any job execution encounters an unhandled exception.
        /// <para>當任何任務執行發生未處理的例外時觸發的事件。</para>
        /// </summary>
        event EventHandler<JobFailedEventArgs> JobFailed;
    }

    /// <summary>
    /// Minute-level scheduler: single-shot System.Threading.Timer recalibrating to
    /// "next minute + buffer" on each tick. Thread-safe, supports repeated Start/Stop, and implements IDisposable.
    /// <para>分鐘級排程器：單發 System.Threading.Timer，每次對齊「下一分鐘 + 緩衝」再觸發。執行緒安全、可重複 Start/Stop、可 Dispose。</para>
    /// </summary>
    public class CronDaemon : ICronDaemon
    {
        private static readonly TimeSpan FireBuffer = TimeSpan.FromMilliseconds(250);

        private readonly object _sync = new object();
        private readonly List<ICronJob> _jobs = new List<ICronJob>();
        private readonly EventHandler<JobFailedEventArgs> _jobFailedForwarder;

        private System.Threading.Timer _timer;
        private CancellationTokenSource _daemonCts;
        private DateTime _lastFireMinute = DateTime.MinValue;
        private bool _running;
        private bool _disposed;

        /// <summary>
        /// Event raised when any job execution encounters an unhandled exception.
        /// <para>當任何任務執行發生未處理的例外時觸發的事件。</para>
        /// </summary>
        public event EventHandler<JobFailedEventArgs> JobFailed;

        /// <summary>
        /// Gets a value indicating whether the scheduler daemon is running.
        /// <para>取得一個值，指出排程守護程序目前是否正在運作中。</para>
        /// </summary>
        public bool IsRunning
        {
            get { lock (_sync) { return _running && !_disposed; } }
        }

        /// <summary>
        /// Gets the read-only snapshot list of scheduled jobs.
        /// <para>取得目前已排程任務的唯讀快照清單。</para>
        /// </summary>
        public IReadOnlyList<ICronJob> Jobs
        {
            get { lock (_sync) { return _jobs.ToArray(); } }
        }

        public CronDaemon()
        {
            _jobFailedForwarder = (s, e) =>
            {
                try { JobFailed?.Invoke(this, e); } catch { /* Observer exceptions must not crash scheduler */ }
            };
        }

        #region Add / Remove

        /// <summary>
        /// Adds a simple task (cannot be cancelled mid-execution, Cancel only prevents next schedule).
        /// <para>新增簡易任務（不可中途取消，Cancel 僅阻止下次排程）。</para>
        /// <para>Fields: minute hour day month day-of-week</para>
        /// <para>* * * * *         Every minute (每分鐘)</para>
        /// <para>0 * * * *         Top of every hour (每小時整點)</para>
        /// <para>0,1,2 * * * *     Minutes 0, 1, 2 of every hour (每小時第 0, 1, 2 分)</para>
        /// <para>*/2 * * * *       Every 2 minutes (每 2 分鐘)</para>
        /// <para>0/15 * * * *      Every 15 minutes starting at :00 (每 15 分鐘，從 0 分起)</para>
        /// <para>1-55 * * * *      Minutes 1 to 55 of every hour (每小時第 1 到 55 分鐘)</para>
        /// <para>* 1,10,20 * * *   Every minute of hours 1, 10, and 20 daily (每天 1、10、20 點的每分鐘)</para>
        /// </summary>
        public ICronJob AddJob(string schedule, ThreadStart action)
        {
            if (action == null) throw new ArgumentNullException(nameof(action));
            return AddJobInternal(schedule, ct =>
            {
                action();
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Adds a cancellable synchronous task.
        /// <para>新增可取消的同步任務。</para>
        /// </summary>
        public ICronJob AddJob(string schedule, Action<CancellationToken> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return AddJobInternal(schedule, ct =>
            {
                handler(ct);
                return Task.CompletedTask;
            });
        }

        /// <summary>
        /// Adds a cancellable asynchronous task (recommended).
        /// <para>新增可取消的非同步任務（建議）。</para>
        /// </summary>
        public ICronJob AddJob(string schedule, Func<CancellationToken, Task> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return AddJobInternal(schedule, handler);
        }

        /// <summary>
        /// Adds a custom ICronJob instance.
        /// <para>新增自訂的 ICronJob 實例。</para>
        /// </summary>
        public ICronJob AddJob(ICronJob job)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            job.Failed += _jobFailedForwarder;
            lock (_sync)
            {
                ThrowIfDisposed();
                _jobs.Add(job);
            }
            return job;
        }

        private ICronJob AddJobInternal(string schedule, Func<CancellationToken, Task> handler)
        {
            var job = new CronJob(schedule, handler); // Format errors throw here; job won't be added
            job.Failed += _jobFailedForwarder;
            lock (_sync)
            {
                ThrowIfDisposed();
                _jobs.Add(job);
            }
            return job;
        }

        /// <summary>
        /// Removes the specified job from the scheduler.
        /// <para>從排程器中移除指定的任務。</para>
        /// </summary>
        public bool RemoveJob(ICronJob job)
        {
            if (job == null) return false;
            lock (_sync)
            {
                if (_jobs.Remove(job))
                {
                    job.Failed -= _jobFailedForwarder;
                    try { job.Cancel(); } catch { /* ignore */ }
                    return true;
                }
                return false;
            }
        }

        /// <summary>
        /// Clears and cancels all scheduled jobs.
        /// <para>清除並取消所有排程任務。</para>
        /// </summary>
        public void Clear()
        {
            ICronJob[] snapshot;
            lock (_sync)
            {
                snapshot = _jobs.ToArray();
                _jobs.Clear();
            }
            foreach (var j in snapshot)
            {
                j.Failed -= _jobFailedForwarder;
                try { j.Cancel(); } catch { /* ignore */ }
            }
        }

        #endregion

        #region Start / Stop

        /// <summary>
        /// Starts the cron scheduler daemon.
        /// <para>啟動 Cron 排程守護程序。</para>
        /// </summary>
        public void Start()
        {
            lock (_sync)
            {
                ThrowIfDisposed();
                if (_running) return;
                _daemonCts = new CancellationTokenSource();
                _lastFireMinute = TruncateToMinute(DateTime.Now);
                _timer = new System.Threading.Timer(OnTick, null, GetNextDueTime(), Timeout.InfiniteTimeSpan);
                _running = true;
            }
        }

        /// <summary>
        /// Signals cancellation and returns immediately (does not wait for running tasks).
        /// Semantics: stops timer -> cancels daemon token -> signals Cancel() to each individual job.
        /// <para>發出取消訊號後立即返回（不等待執行中任務）。語意：停 timer → 取消 daemon token → 對每個別 job 發 Cancel()。</para>
        /// </summary>
        public void Stop()
        {
            ICronJob[] snapshot = null;
            CancellationTokenSource cts = null;
            System.Threading.Timer timer = null;

            lock (_sync)
            {
                if (!_running) return;
                _running = false;
                snapshot = _jobs.ToArray();
                cts = _daemonCts;
                _daemonCts = null;
                timer = _timer;
                _timer = null;
            }

            // Signal and return immediately without awaiting tasks
            try { timer?.Change(Timeout.Infinite, Timeout.Infinite); } catch { /* ignore */ }
            try { timer?.Dispose(); } catch { /* ignore */ }
            try { if (cts != null && !cts.IsCancellationRequested) cts.Cancel(); } catch { /* ignore */ }
            if (snapshot != null)
            {
                foreach (var job in snapshot)
                {
                    try { job.Cancel(); } catch { /* ignore */ }
                }
            }
            // Note: Do not Dispose cts immediately here to prevent ObjectDisposedException if running jobs access WaitHandle or callbacks
        }

        Task ICronDaemon.StopAsync(TimeSpan timeout) => StopAsync(timeout);

        /// <summary>
        /// After signaling cancellation, waits up to timeout for running tasks to finish (returns true if all finished, false if timed out).
        /// Supports Timeout.InfiniteTimeSpan for indefinite wait. Uses Task.WhenAll to avoid busy polling.
        /// <para>發出取消訊號後，最多等待 timeout 讓執行中任務收尾（回傳 true 表示全部正常結束，false 表示逾時）。</para>
        /// <para>支援 Timeout.InfiniteTimeSpan 無限等待。改用 Task.WhenAll 避免忙碌輪詢。</para>
        /// </summary>
        public async Task<bool> StopAsync(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be greater than or equal to zero, or Timeout.InfiniteTimeSpan.");

            ICronJob[] snapshot;
            lock (_sync) { snapshot = _jobs.ToArray(); }
            Stop(); // Signal cancellation first (non-blocking)

            if (snapshot == null || snapshot.Length == 0) return true;

            var runningTasks = new List<Task>();
            foreach (var job in snapshot)
            {
                if (job is CronJob cj && cj.CurrentTask != null && !cj.CurrentTask.IsCompleted)
                {
                    runningTasks.Add(cj.CurrentTask);
                }
            }

            if (runningTasks.Count == 0) return true;

            Task allTasks = Task.WhenAll(runningTasks);
            if (timeout == Timeout.InfiniteTimeSpan)
            {
                await allTasks.ConfigureAwait(false);
                return true;
            }

            var delayTask = Task.Delay(timeout);
            var completed = await Task.WhenAny(allTasks, delayTask).ConfigureAwait(false);
            return completed == allTasks;
        }

        #endregion

        #region Timer Core

        private void OnTick(object state)
        {
            ICronJob[] snapshot = null;
            CancellationToken token = CancellationToken.None;
            DateTime now;

            lock (_sync)
            {
                if (!_running || _disposed || _daemonCts == null)
                    return;
                now = DateTime.Now; // Single snapshot to prevent inconsistent Now readings
                token = _daemonCts.Token;

                DateTime currentMinute = TruncateToMinute(now);
                // Trigger at most once per minute; == avoids freezing the scheduler on NTP backwards adjustment or DST shifts
                if (currentMinute == _lastFireMinute)
                {
                    RescheduleLocked();
                    return;
                }
                _lastFireMinute = currentMinute;
                snapshot = _jobs.ToArray();
            }

            if (snapshot != null && !token.IsCancellationRequested)
            {
                foreach (var job in snapshot)
                {
                    try
                    {
                        // Fire and forget: caller does not await; job tracks IsRunning internally
                        var _ = job.ExecuteAsync(now, token);
                    }
                    catch
                    {
                        // ExecuteAsync should not throw (exceptions trigger Failed event); this is the last line of defense
                    }
                }
            }

            lock (_sync)
            {
                if (_running && !_disposed)
                    RescheduleLocked();
            }
        }

        private void RescheduleLocked()
        {
            try
            {
                if (_timer != null && _running && !_disposed)
                    _timer.Change(GetNextDueTime(), Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException) { /* Stop race condition, ignore */ }
        }

        private static TimeSpan GetNextDueTime()
        {
            DateTime now = DateTime.Now;
            DateTime nextMinute = new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind).AddMinutes(1);
            TimeSpan delay = (nextMinute - now) + FireBuffer;
            if (delay < FireBuffer) delay = FireBuffer;
            if (delay > TimeSpan.FromSeconds(65)) delay = TimeSpan.FromSeconds(65);
            return delay;
        }

        private static DateTime TruncateToMinute(DateTime dt)
        {
            return new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Kind);
        }

        #endregion

        #region Dispose

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!disposing) return;
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            try { Stop(); } catch { /* ignore */ }
            Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CronDaemon));
        }

        #endregion
    }
}
