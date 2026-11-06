using System;
using System.Threading;
using System.Threading.Tasks;

namespace CronNET
{
    /// <summary>
    /// Event data carried when a job execution fails.
    /// <para>排程任務執行失敗時攜帶的事件資訊。</para>
    /// </summary>
    public sealed class JobFailedEventArgs : EventArgs
    {
        /// <summary>
        /// Gets the cron schedule expression.
        /// <para>取得 Cron 排程表達式。</para>
        /// </summary>
        public string ScheduleExpression { get; }

        /// <summary>
        /// Gets the unhandled exception that occurred.
        /// <para>取得發生的未處理例外狀況。</para>
        /// </summary>
        public Exception Exception { get; }

        /// <summary>
        /// Gets the timestamp when the failure occurred.
        /// <para>取得失敗發生的時間點。</para>
        /// </summary>
        public DateTime FailedAt { get; }

        /// <summary>
        /// Gets the job instance that failed, if available.
        /// <para>取得失敗的任務實例（若有提供）。</para>
        /// </summary>
        public ICronJob Job { get; }

        public JobFailedEventArgs(string scheduleExpression, Exception exception, DateTime failedAt, ICronJob job = null)
        {
            ScheduleExpression = scheduleExpression;
            Exception = exception;
            FailedAt = failedAt;
            Job = job;
        }
    }

    /// <summary>
    /// Contract representing a scheduled cron job.
    /// <para>定義 Cron 排程任務合約介面。</para>
    /// </summary>
    public interface ICronJob
    {
        /// <summary>
        /// Gets the cron schedule expression for this job.
        /// <para>取得此任務的 Cron 排程表達式。</para>
        /// </summary>
        string ScheduleExpression { get; }

        /// <summary>
        /// Gets a value indicating whether this job is currently running.
        /// <para>取得一個值，指出此任務目前是否正在執行中。</para>
        /// </summary>
        bool IsRunning { get; }

        /// <summary>
        /// Triggered once when the schedule matches and no execution is overlapping. Uses fire-and-forget semantics:
        /// the returned Task represents this execution; the caller can choose to await or ignore it.
        /// <para>時間命中且無重疊執行時啟動一次。採「發射後不理」語意：回傳的 Task 代表該次執行，呼叫端可選擇 await 或忽略。</para>
        /// </summary>
        /// <param name="now">The current timestamp.<para>當前時間戳記。</para></param>
        /// <param name="daemonToken">The daemon's cancellation token.<para>守護程序的取消語彙基元。</para></param>
        Task ExecuteAsync(DateTime now, CancellationToken daemonToken);

        /// <summary>
        /// Signals cancellation to the running execution and prevents all subsequent scheduled runs; returns immediately (non-blocking).
        /// Handlers must observe CancellationToken to finish the current run early.
        /// <para>對執行中的任務發出取消訊號，並阻止後續所有排程執行；立即返回（不等待）。委派需觀察 CancellationToken 才會提早結束本次執行。</para>
        /// </summary>
        void Cancel();

        /// <summary>
        /// Event raised when an unhandled exception occurs during execution.
        /// <para>當任務執行發生未處理的例外時觸發的事件。</para>
        /// </summary>
        event EventHandler<JobFailedEventArgs> Failed;
    }

    /// <summary>
    /// A Job implementing cooperative cancellation using Task + CancellationToken.
    /// Overlap policy: skips the current execution if the previous run is still in progress.
    /// Note: Delegates that do not accept a token (Action / ThreadStart) cannot be cancelled mid-execution;
    /// Cancel() only prevents subsequent scheduled runs. To support cancellation, use an overload accepting CancellationToken.
    /// <para>以 Task + CancellationToken 實作協作式取消的 Job。</para>
    /// <para>重疊執行政策：若上次任務仍在執行，直接跳過本次觸發。</para>
    /// <para>注意：不接受 token 的委派（Action / ThreadStart）無法被中途取消，Cancel() 僅能阻止後續排程；要可取消請使用接受 CancellationToken 的多載。</para>
    /// </summary>
    public class CronJob : ICronJob
    {
        private readonly ICronSchedule _schedule;
        private readonly string _scheduleExpression;
        private readonly Func<CancellationToken, Task> _handler;

        private int _running; // 0 = idle, 1 = running
        private readonly object _ctsLock = new object();
        private CancellationTokenSource _currentCts;
        private Task _currentTask = Task.CompletedTask;
        private bool _cancelled; // Guarded by _ctsLock; once set, no further runs start

        public event EventHandler<JobFailedEventArgs> Failed;

        public string ScheduleExpression => _scheduleExpression;
        public bool IsRunning => Volatile.Read(ref _running) == 1;

        /// <summary>
        /// Gets a value indicating whether <see cref="Cancel"/> has been called; a cancelled job never runs again.
        /// <para>取得一個值，指出是否已呼叫 <see cref="Cancel"/>；已取消的任務不會再執行。</para>
        /// </summary>
        public bool IsCancelled
        {
            get { lock (_ctsLock) { return _cancelled; } }
        }

        /// <summary>
        /// Exposes the currently executing Task for efficient awaiting by Daemon (avoids busy polling).
        /// <para>暴露目前執行的 Task，供 Daemon 高效等待結束（避免忙碌輪詢）。</para>
        /// </summary>
        public Task CurrentTask
        {
            get { lock (_ctsLock) { return _currentTask; } }
        }

        public CronJob(string schedule, Func<CancellationToken, Task> handler)
        {
            if (string.IsNullOrWhiteSpace(schedule))
                throw new ArgumentException("Schedule expression cannot be empty.", nameof(schedule));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _scheduleExpression = schedule.Trim();
            _schedule = new CronSchedule(_scheduleExpression); // Throws FormatException directly on invalid format
        }

        public CronJob(ICronSchedule schedule, Func<CancellationToken, Task> handler)
        {
            _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _scheduleExpression = (_schedule as CronSchedule)?.Expression ?? "* * * * *";
        }

        public CronJob(string schedule, Action<CancellationToken> handler)
            : this(schedule, handler == null
                ? throw new ArgumentNullException(nameof(handler))
                : (Func<CancellationToken, Task>)(ct => { handler(ct); return Task.CompletedTask; }))
        {
        }

        public CronJob(string schedule, Action handler)
            : this(schedule, handler == null
                ? throw new ArgumentNullException(nameof(handler))
                : (Func<CancellationToken, Task>)(_ => { handler(); return Task.CompletedTask; }))
        {
        }

        public CronJob(string schedule, ThreadStart handler)
            : this(schedule, handler == null
                ? throw new ArgumentNullException(nameof(handler))
                : (Action)(() => handler()))
        {
        }

        public Task ExecuteAsync(DateTime now, CancellationToken daemonToken)
        {
            if (daemonToken.IsCancellationRequested)
                return Task.CompletedTask;

            if (!_schedule.IsTime(now))
                return Task.CompletedTask;

            // Already running -> skip
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                return Task.CompletedTask;

            CancellationTokenSource linked;
            Task task;
            Task<Task> outer;
            lock (_ctsLock)
            {
                // Checked under the lock so a concurrent Cancel() cannot slip between the check and CTS creation
                if (_cancelled)
                {
                    Interlocked.Exchange(ref _running, 0);
                    return Task.CompletedTask;
                }

                try
                {
                    linked = CancellationTokenSource.CreateLinkedTokenSource(daemonToken);
                }
                catch
                {
                    Interlocked.Exchange(ref _running, 0);
                    throw;
                }
                // Store previous _currentCts (only one can run at a time)
                var old = _currentCts;
                _currentCts = linked;
                if (old != null)
                {
                    try { old.Dispose(); } catch { /* ignore */ }
                }

                // Create the task cold and publish it as CurrentTask before it starts, so observers
                // (e.g. CronDaemon.StopAsync) never see a stale completed task while this run is in flight.
                // CancellationToken.None: cancellation only affects the inner handler, and the finally cleanup always runs.
                outer = new Task<Task>(() => RunAsync(linked), CancellationToken.None, TaskCreationOptions.DenyChildAttach);
                task = outer.Unwrap();
                _currentTask = task;
            }

            outer.Start(TaskScheduler.Default);
            return task;
        }

        private async Task RunAsync(CancellationTokenSource linked)
        {
            CancellationToken token = linked.Token;
            try
            {
                var inner = _handler(token);
                if (inner != null)
                    await inner.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Cooperative cancellation caused by our own token: normal completion, not treated as failure.
                // Other OperationCanceledExceptions (e.g. HttpClient timeouts) fall through and are reported as failures.
            }
            catch (Exception ex)
            {
                try { OnFailed(ex); } catch { /* Event handlers must not crash the job */ }
            }
            finally
            {
                lock (_ctsLock)
                {
                    if (ReferenceEquals(_currentCts, linked))
                        _currentCts = null;
                }
                try { linked.Dispose(); } catch { /* ignore */ }
                Interlocked.Exchange(ref _running, 0);
            }
        }

        /// <summary>
        /// Signals cancellation to the running execution and prevents all subsequent scheduled runs.
        /// Returns immediately without waiting for the current run to finish.
        /// <para>對執行中的任務發出取消訊號，並阻止後續所有排程執行。立即返回，不等待本次執行結束。</para>
        /// </summary>
        public void Cancel()
        {
            lock (_ctsLock)
            {
                _cancelled = true;
                CancelCurrentLocked();
            }
        }

        /// <summary>
        /// Signals cancellation to the running execution only; future scheduled runs are still allowed.
        /// Used by <see cref="CronDaemon.Stop"/> so the daemon can be restarted.
        /// <para>僅對執行中的任務發出取消訊號，不影響後續排程。供 <see cref="CronDaemon.Stop"/> 使用，讓 daemon 可重新啟動。</para>
        /// </summary>
        internal void CancelCurrentRun()
        {
            lock (_ctsLock)
            {
                CancelCurrentLocked();
            }
        }

        private void CancelCurrentLocked()
        {
            try
            {
                if (_currentCts != null && !_currentCts.IsCancellationRequested)
                    _currentCts.Cancel();
            }
            catch (ObjectDisposedException) { /* Already completed/disposed, ignore */ }
            catch (Exception) { /* Incidental exceptions during Cancel(), ignore */ }
        }

        private void OnFailed(Exception ex)
        {
            var handler = Failed;
            if (handler == null) return;

            var args = new JobFailedEventArgs(_scheduleExpression, ex, DateTime.Now, this);
            foreach (EventHandler<JobFailedEventArgs> sub in handler.GetInvocationList())
            {
                try
                {
                    sub(this, args);
                }
                catch
                {
                    // Exceptions from individual subscribers should not interrupt notifications to other subscribers
                }
            }
        }
    }
}
