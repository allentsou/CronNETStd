using System;
using System.Threading;
using System.Threading.Tasks;

namespace CronNET
{
    /// <summary>Job 執行失敗時攜帶的資訊。</summary>
    public sealed class JobFailedEventArgs : EventArgs
    {
        public string ScheduleExpression { get; }
        public Exception Exception { get; }
        public DateTime FailedAt { get; }
        public ICronJob Job { get; }

        public JobFailedEventArgs(string scheduleExpression, Exception exception, DateTime failedAt, ICronJob job = null)
        {
            ScheduleExpression = scheduleExpression;
            Exception = exception;
            FailedAt = failedAt;
            Job = job;
        }
    }

    public interface ICronJob
    {
        string ScheduleExpression { get; }
        bool IsRunning { get; }

        /// <summary>
        /// 時間命中且無重疊執行時啟動一次。採「發射後不理」語意：
        /// 回傳的 Task 代表該次執行，呼叫端可選擇 await 或忽略。
        /// </summary>
        Task ExecuteAsync(DateTime now, CancellationToken daemonToken);

        /// <summary>
        /// 發出取消訊號後立即返回（不等待）。Job 需觀察 CancellationToken 才會提早收尾。
        /// </summary>
        void Cancel();

        event EventHandler<JobFailedEventArgs> Failed;
    }

    /// <summary>
    /// 以 Task + CancellationToken 實作協作式取消的 Job。
    /// 重疊執行政策：若上次仍在跑，直接跳過本次。
    /// 注意：不接受 token 的委派（Action / ThreadStart）無法被中途取消，
    /// Cancel() 僅能阻止下次排程；要可取消請使用接受 CancellationToken 的多載。
    /// </summary>
    public class CronJob : ICronJob
    {
        private readonly ICronSchedule _schedule;
        private readonly string _scheduleExpression;
        private readonly Func<CancellationToken, Task> _handler;

        private int _running; // 0 = 閒置, 1 = 執行中
        private readonly object _ctsLock = new object();
        private CancellationTokenSource _currentCts;
        private Task _currentTask = Task.CompletedTask;

        public event EventHandler<JobFailedEventArgs> Failed;

        public string ScheduleExpression => _scheduleExpression;
        public bool IsRunning => Volatile.Read(ref _running) == 1;

        /// <summary>暴露目前執行的 Task，供 Daemon 高效等待結束（避免忙碌輪詢）。</summary>
        public Task CurrentTask
        {
            get { lock (_ctsLock) { return _currentTask; } }
        }

        public CronJob(string schedule, Func<CancellationToken, Task> handler)
        {
            if (string.IsNullOrWhiteSpace(schedule))
                throw new ArgumentException("排程不可為空。", nameof(schedule));
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
            _scheduleExpression = schedule.Trim();
            _schedule = new CronSchedule(_scheduleExpression); // 無效格式直接拋 FormatException
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

            // 已在執行 → 跳過
            if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                return Task.CompletedTask;

            CancellationTokenSource linked;
            lock (_ctsLock)
            {
                try
                {
                    linked = CancellationTokenSource.CreateLinkedTokenSource(daemonToken);
                }
                catch
                {
                    Interlocked.Exchange(ref _running, 0);
                    throw;
                }
                // 收納前一次的 _currentCts（同時只會有一個 running）
                var old = _currentCts;
                _currentCts = linked;
                if (old != null)
                {
                    try { old.Dispose(); } catch { /* ignore */ }
                }
            }

            CancellationToken token = linked.Token;

            // 用 CancellationToken.None 啟動外層，讓取消只影響內層 handler，不會跳過 finally 清理
            var task = Task.Run(async () =>
            {
                try
                {
                    var inner = _handler(token);
                    if (inner != null)
                        await inner.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 協作式取消：正常收尾，不視為失敗
                }
                catch (Exception ex)
                {
                    try { OnFailed(ex); } catch { /* 事件處理器不可拖垮 Job */ }
                }
                finally
                {
                    Interlocked.Exchange(ref _running, 0);
                    lock (_ctsLock)
                    {
                        if (ReferenceEquals(_currentCts, linked))
                            _currentCts = null;
                    }
                    try { linked.Dispose(); } catch { /* ignore */ }
                }
            }, CancellationToken.None);

            lock (_ctsLock)
            {
                _currentTask = task;
            }

            return task;
        }

        /// <summary>發出取消訊號後立即返回，不等待執行緒結束。</summary>
        public void Cancel()
        {
            lock (_ctsLock)
            {
                try
                {
                    if (_currentCts != null && !_currentCts.IsCancellationRequested)
                        _currentCts.Cancel();
                }
                catch (ObjectDisposedException) { /* 已收尾，忽略 */ }
                catch (Exception) { /* Cancel 期間的附帶例外，忽略 */ }
            }
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
                    // 個別監聽者的例外不應中斷其他監聽者通知
                }
            }
        }
    }
}
