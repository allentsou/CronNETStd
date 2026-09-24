using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CronNET
{
    public interface ICronDaemon : IDisposable
    {
        /// <summary>簡易任務（不可中途取消，Cancel 僅阻止下次排程）。回傳 job 以便追蹤/移除。</summary>
        ICronJob AddJob(string schedule, ThreadStart action);
        /// <summary>可取消的同步任務。</summary>
        ICronJob AddJob(string schedule, Action<CancellationToken> handler);
        /// <summary>可取消的非同步任務（建議）。</summary>
        ICronJob AddJob(string schedule, Func<CancellationToken, Task> handler);

        bool RemoveJob(ICronJob job);
        void Clear();

        void Start();
        /// <summary>
        /// 發出取消訊號後立即返回（不等待執行中任務）。
        /// 語意：停 timer → 取消 daemon token → 對每個別 job 發 Cancel()。
        /// </summary>
        void Stop();

        /// <summary>發出取消訊號後，最多等待 timeout 讓執行中任務收尾（不會強制終止）。</summary>
        Task StopAsync(TimeSpan timeout);

        bool IsRunning { get; }
        IReadOnlyList<ICronJob> Jobs { get; }

        event EventHandler<JobFailedEventArgs> JobFailed;
    }

    /// <summary>
    /// 分鐘級排程器：單發 System.Threading.Timer，
    /// 每次對齊「下一分鐘 + 緩衝」再觸發，執行緒安全、可重複 Start/Stop、可 Dispose。
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

        public event EventHandler<JobFailedEventArgs> JobFailed;

        public bool IsRunning
        {
            get { lock (_sync) { return _running && !_disposed; } }
        }

        public IReadOnlyList<ICronJob> Jobs
        {
            get { lock (_sync) { return _jobs.ToArray(); } }
        }

        public CronDaemon()
        {
            _jobFailedForwarder = (s, e) =>
            {
                try { JobFailed?.Invoke(this, e); } catch { /* 觀察者例外不可回灌 */ }
            };
        }

        #region Add / Remove

        /// <summary>
        /// 欄位：分 時 日 月 週
        /// * * * * *         每分鐘
        /// 0 * * * *         每小時整點
        /// 0,1,2 * * * *     每小時的 0、1、2 分
        /// */2 * * * *       每 2 分鐘
        /// 0/15 * * * *      每 15 分鐘（從 0 分起）
        /// 1-55 * * * *      每小時第 1 到 55 分鐘
        /// * 1,10,20 * * *   每天 1、10、20 點的每分鐘
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

        public ICronJob AddJob(string schedule, Action<CancellationToken> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return AddJobInternal(schedule, ct =>
            {
                handler(ct);
                return Task.CompletedTask;
            });
        }

        public ICronJob AddJob(string schedule, Func<CancellationToken, Task> handler)
        {
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            return AddJobInternal(schedule, handler);
        }

        /// <summary>新增自訂的 ICronJob 實例。</summary>
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
            var job = new CronJob(schedule, handler); // 格式錯誤在此拋，job 不會被加入
            job.Failed += _jobFailedForwarder;
            lock (_sync)
            {
                ThrowIfDisposed();
                _jobs.Add(job);
            }
            return job;
        }

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

            // 以下皆為「發訊號即返」，不等待任何 Task
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
            // 注意：不在此立即 Dispose cts，避免正在收尾中的 Job 因存取 WaitHandle 或回呼而拋 ObjectDisposedException
        }

        Task ICronDaemon.StopAsync(TimeSpan timeout) => StopAsync(timeout);

        /// <summary>
        /// 發出取消訊號後，最多等待 timeout 讓執行中任務收尾（回傳 true 表示全部正常結束，false 表示逾時）。
        /// 支援 Timeout.InfiniteTimeSpan 無限等待。改用 Task.WhenAll 避免忙碌輪詢。
        /// </summary>
        public async Task<bool> StopAsync(TimeSpan timeout)
        {
            if (timeout < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout 必須大於等於零或為 Timeout.InfiniteTimeSpan。");

            ICronJob[] snapshot;
            lock (_sync) { snapshot = _jobs.ToArray(); }
            Stop(); // 先發訊號（非阻塞）

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

        #region Timer 核心

        private void OnTick(object state)
        {
            ICronJob[] snapshot = null;
            CancellationToken token = CancellationToken.None;
            DateTime now;

            lock (_sync)
            {
                if (!_running || _disposed || _daemonCts == null)
                    return;
                now = DateTime.Now; // 單次快照，避免多次 Now 不一致
                token = _daemonCts.Token;

                DateTime currentMinute = TruncateToMinute(now);
                // 同一分鐘只觸發一次；使用 == 避免 NTP 倒退校時或冬令時回調時造成排程全面凍結
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
                        // 發射後不理：呼叫端不等待；job 內部自行追蹤 IsRunning
                        var _ = job.ExecuteAsync(now, token);
                    }
                    catch
                    {
                        // ExecuteAsync 本身不該拋（job 內例外走 Failed 事件），此為最後防線
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
            catch (ObjectDisposedException) { /* Stop 競速，忽略 */ }
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
