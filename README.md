# CronNETStd

[![NuGet Version](https://img.shields.io/nuget/v/CronNETStd.svg?style=flat-square)](https://www.nuget.org/packages/CronNETStd/)
[![Target Frameworks](https://img.shields.io/badge/.NET-%3E%3D%20Standard%202.0%20%7C%208.0%20%7C%209.0-blue.svg?style=flat-square)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=flat-square)](LICENSE)

[English](#english) | [繁體中文](#繁體中文)

---

<a name="english"></a>
## English

**CronNETStd** is an ultra-lightweight, high-performance minute-level cron scheduler for .NET. Built with modern `Task` + `CancellationToken` cooperative cancellation, atomic overlap prevention, and single-shot `System.Threading.Timer` precision.

Targeting `.NET Standard 2.0`, `.NET Standard 2.1`, `.NET 8.0`, and `.NET 9.0`, compatible with .NET Framework 4.7.2+, .NET Core 2.0+, and all modern .NET platforms.

### 🌟 Key Features

- **Cooperative Cancellation**: Non-blocking `Cancel()` and `Stop()`. Handlers observe `CancellationToken` to exit gracefully without thread abortion.
- **Overlap Prevention**: Atomic `Interlocked.CompareExchange` check—skips execution automatically if the previous run is still active.
- **Precise Minute Alignment**: Single-shot timer recalibrates to `Next Minute + 250ms` on every tick. Robust against system clock changes (NTP sync backwards or DST shifts).
- **Zero-Polling Graceful Shutdown**: `StopAsync(TimeSpan timeout)` uses `Task.WhenAll` to await running jobs without 25ms busy-polling. Supports `Timeout.InfiniteTimeSpan`.
- **Rich Cron Syntax**: Supports `*`, `?`, `*/step`, `start/step` (e.g. `0/15`, `5/10`), `a-b`, `a-b/step`, and comma unions (`a,b,c`).
- **Static Validation & Prediction**: `CronSchedule.TryParse()`, `CronSchedule.IsValidExpression()`, and `GetNextOccurrence()` without throwing format exceptions.
- **Zero Allocation Reads**: Parsed schedules are sorted and cached as `ReadOnlyCollection<int>`.

### 📦 Installation

```bash
dotnet add package CronNETStd
```

Package Manager:
```powershell
Install-Package CronNETStd
```

Or reference the source project directly:
```bash
dotnet add reference ../CronNETStd/CronNET.csproj
```

### 🚀 Quick Start

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using CronNET;

using (var daemon = new CronDaemon())
{
    // 1. Simple synchronous job (uninterruptible during execution, Cancel blocks subsequent runs)
    daemon.AddJob("* * * * *", (ThreadStart)(() => 
    {
        Console.WriteLine("Runs every minute");
    }));

    // 2. Cancellable synchronous job
    daemon.AddJob("*/5 * * * *", (CancellationToken ct) =>
    {
        for (int i = 0; i < 10; i++)
        {
            ct.ThrowIfCancellationRequested(); // Observe cancellation
            Console.WriteLine($"Step {i}");
            Thread.Sleep(500);
        }
    });

    // 3. Cancellable asynchronous job (Recommended)
    daemon.AddJob("0 8 * * *", async (CancellationToken ct) =>
    {
        await Task.Delay(1000, ct); // Pass token to async operations
        Console.WriteLine("Runs every day at 08:00");
    });

    // Handle job failures safely (won't crash the scheduler); e.Job is the failed job instance
    daemon.JobFailed += (sender, e) =>
    {
        Console.WriteLine($"[{e.FailedAt}] Job ({e.ScheduleExpression}) failed: {e.Exception.Message}");
    };

    // Start scheduler
    daemon.Start();

    Console.WriteLine("Press Enter to stop...");
    Console.ReadLine();

    // Fast non-blocking signal: stops timer, cancels daemon token and all jobs
    daemon.Stop();
}
```

#### Graceful Shutdown with Timeout

```csharp
// Returns true if all running jobs completed within timeout, false if timed out
bool success = await daemon.StopAsync(TimeSpan.FromSeconds(10));

// Or wait indefinitely until all running jobs finish:
await daemon.StopAsync(Timeout.InfiniteTimeSpan);
```

### ⏰ Cron Syntax & Examples

Cron expression format (5 fields):
```
┌─── Minute (0 - 59)
│ ┌─── Hour (0 - 23)
│ │ ┌─── Day of Month (1 - 31)
│ │ │ ┌─── Month (1 - 12)
│ │ │ │ ┌─── Day of Week (0 - 6, 7 is an alias for 0 / Sunday)
│ │ │ │ │
* * * * *
```
Each field supports `*`, `?`, `*/n`, `start/n` (e.g. `0/15`), `a-b`, `a-b/n`, `a,b,c`, and unions such as `1,2-10/2`.

*(Note: Day of Month and Day of Week use AND semantics—both must match. Standard cron usually uses OR when both are restricted.)*

| Schedule | Expression | Description |
|---|---|---|
| Every minute | `* * * * *` | Every minute |
| Every 2 minutes | `*/2 * * * *` | Minutes: 0, 2, 4, 6... |
| Every 15 minutes | `0/15 * * * *` or `*/15 * * * *` | Minutes: 0, 15, 30, 45 |
| At 5, 15, 25, 35, 45, 55 min | `5/10 * * * *` | Minutes starting at 5 every 10 min |
| Top of every hour | `0 * * * *` | Hourly at :00 |
| Minutes 0, 1, 2 of every hour | `0,1,2 * * * *` | :00, :01, :02 |
| Minutes 1 to 55 of every hour | `1-55 * * * *` | Every minute from :01 to :55 |
| Every minute of hours 1, 10, 20 | `* 1,10,20 * * *` | 01:xx, 10:xx, 20:xx |
| Every day at 08:00 | `0 8 * * *` | 08:00 daily |
| Every day at 12:30 | `30 12 * * *` | 12:30 daily |
| Every day at 08:00 and 20:00 | `0 8,20 * * *` | Twice daily |
| Every 15 min between 22:00 and 23:59 | `*/15 22-23 * * *` | 22:00, 22:15 ... 23:45 |
| Every Monday at 09:00 | `0 9 * * 1` | Monday = 1 |
| Every weekday at 09:00 | `0 9 * * 1-5` | Mon - Fri at 09:00 |
| Every Sunday at midnight | `0 0 * * 0` or `0 0 * * 7` | Sunday 00:00 |
| 1st day of month at 00:00 | `0 0 1 * *` | Monthly on day 1 |
| 1st and 15th of month at 00:00 | `0 0 1,15 * *` | Twice monthly |
| January 1st at 00:00 | `0 0 1 1 *` | Yearly |
| Union: minutes 1, 2, 4, 6, 8, 10 | `1,2-10/2 * * * *` | `1` plus `2-10/2` |
| Every day at 09:00 (`?` wildcard) | `0 9 * * ?` | Same as `0 9 * * *`; compatible with Quartz `?` |

### 🔍 Validation & Prediction

```csharp
// Safe validation without throwing exceptions
if (CronSchedule.TryParse("0/15 * * * *", out var schedule))
{
    // Predict next occurrence
    DateTime? nextRun = schedule.GetNextOccurrence(DateTime.Now);
    Console.WriteLine($"Next trigger: {nextRun}"); // null if no match within 100 years (e.g. "0 0 31 2 *")
}

// Quick validation check
bool isValid = CronSchedule.IsValidExpression("0 8 * * 1-5"); // true
bool invalid = CronSchedule.IsValidExpression("60 * * * *");  // false (out of range)
```

### ⚙️ Behavior Details

1. **Minute precision**: Fires about 250ms after second 00 of each minute, at most once per minute.
2. **Overlap skipping**: If a job's previous run is still active, the current trigger is skipped (check `IsRunning`).
3. **Cooperative cancellation**: `Cancel()` / `Stop()` only signal. Handlers must observe the `CancellationToken` (pass it to `Task.Delay`, `HttpClient`, etc., or check `ct.IsCancellationRequested`); otherwise the current run completes. Delegates without a token (`ThreadStart` / `Action`) cannot be interrupted mid-run.
   - `job.Cancel()` permanently disables the job (`IsCancelled` becomes true; it never fires again).
   - `daemon.Stop()` cancels only the running executions; calling `Start()` again resumes scheduling.
   - An `OperationCanceledException` not caused by the job's own token (e.g. an `HttpClient` timeout) is treated as a failure and raises `Failed`.
4. **Exception isolation**: Exceptions thrown by a job never kill the scheduler; they are reported via `job.Failed` / `daemon.JobFailed`. A throwing subscriber does not prevent other subscribers from being notified.
5. **`Stop()` never waits**: Always returns in milliseconds; use `await StopAsync(timeout)` to wait. `StopAsync` waits for tasks the daemon started for any `ICronJob`, plus `CronJob.CurrentTask`.
6. **Idempotent `Start()`**: Calling it repeatedly is harmless; `using` / `Dispose()` calls `Stop()` automatically. Adding the same job instance twice has no effect.

### 📚 API Reference

- **`CronSchedule`**: `CronSchedule(expression)`, `IsTime(DateTime)`, `GetNextOccurrence(DateTime)`, cached read-only `Minutes` / `Hours` / `DaysOfMonth` / `Months` / `DaysOfWeek`, `Expression`, static `TryParse(expr, out schedule)` / `IsValidExpression(expr)`.
- **`CronJob`**: `CronJob(schedule, handler)` where handler is `ThreadStart` / `Action` / `Action<CancellationToken>` / `Func<CancellationToken, Task>`; `ExecuteAsync(now, daemonToken)`, `Cancel()`, `IsRunning`, `IsCancelled`, `CurrentTask`, `Failed`.
- **`CronDaemon`**: `AddJob` ×4 (including custom `ICronJob`), `RemoveJob`, `Clear`, `Start`, `Stop`, `StopAsync(timeout)` (returns `Task<bool>`), `IsRunning`, `Jobs`, `JobFailed`, `Dispose`.

---

<a name="繁體中文"></a>
## 繁體中文

**CronNETStd** 是一個極輕量、高效能的 .NET 分鐘級 Cron 排程庫。採用現代化 `Task` + `CancellationToken` 協作式取消、原子旗標防重疊執行，並以單發 `System.Threading.Timer` 實現精準分鐘對齊。

支援 `.NET Standard 2.0`、`.NET Standard 2.1`、`.NET 8.0`、`.NET 9.0`，可直接於 .NET Framework 4.7.2+、.NET Core 2.0+ 及現代 .NET 專案中引用。

### 🌟 核心特色

- **協作式取消架構**：`Cancel()` 與 `Stop()` 為非阻塞式立即返回，任務內部只需觀察 `CancellationToken` 即可安全優雅收尾。
- **原子旗標防重疊**：採用 `Interlocked.CompareExchange` 判斷，若上次任務尚未跑完，本次自動跳過，杜絕並發衝突。
- **精準對齊分鐘**：單發 Timer 每次重新對齊「下一分鐘 + 250ms」；時鐘倒退（NTP 校時或冬令時回調）自動校正，不卡死、不凍結。
- **零輪詢優雅關機**：`StopAsync(TimeSpan timeout)` 採用 `Task.WhenAll` 等待進行中任務，告別 25ms 忙碌輪詢（Busy-polling），支援 `Timeout.InfiniteTimeSpan` 無限等待。
- **廣泛語法支援**：支援 `*`、`?`、`*/step`、`start/step`（如 `0/15`、`5/10`）、`a-b`、`a-b/step`、以及逗號聯集（`a,b,c`）。
- **靜態預檢與時間預測**：提供 `CronSchedule.TryParse()`、`CronSchedule.IsValidExpression()` 與 `GetNextOccurrence()`，驗證失敗不需拋出異常。
- **零配置唯讀屬性**：內部排序完成後快取為 `ReadOnlyCollection<int>`，屬性讀取 0 堆積配置。

### 📦 安裝套件

```bash
dotnet add package CronNETStd
```

Package Manager:
```powershell
Install-Package CronNETStd
```

或直接引用原始碼專案：
```bash
dotnet add reference ../CronNETStd/CronNET.csproj
```

### 🚀 快速上手

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using CronNET;

using (var daemon = new CronDaemon())
{
    // 1. 簡易任務（執行中無法中途取消，Cancel 僅阻止後續排程）
    daemon.AddJob("* * * * *", (ThreadStart)(() => 
    {
        Console.WriteLine("每分鐘執行一次");
    }));

    // 2. 可取消的同步任務
    daemon.AddJob("*/5 * * * *", (CancellationToken ct) =>
    {
        for (int i = 0; i < 10; i++)
        {
            ct.ThrowIfCancellationRequested(); // 觀察取消訊號
            Console.WriteLine($"步驟 {i}");
            Thread.Sleep(500);
        }
    });

    // 3. 可取消的非同步任務（最推薦）
    daemon.AddJob("0 8 * * *", async (CancellationToken ct) =>
    {
        await Task.Delay(1000, ct); // 將 token 傳入非同步作業
        Console.WriteLine("每天 08:00 執行");
    });

    // 統一攔截任務例外（任務錯誤不會拖垮排程器；e.Job 可取得出錯的任務實例）
    daemon.JobFailed += (sender, e) =>
    {
        Console.WriteLine($"[{e.FailedAt}] 任務 ({e.ScheduleExpression}) 失敗：{e.Exception.Message}");
    };

    // 啟動排程
    daemon.Start();

    Console.WriteLine("按 Enter 結束...");
    Console.ReadLine();

    // 發送取消訊號並立即返回（不阻塞）
    daemon.Stop();
}
```

#### 優雅關機與等待收尾

```csharp
// 發出取消訊號，最多等待 10 秒；回傳 true 表示全部正常結束，false 表示逾時
bool allCompleted = await daemon.StopAsync(TimeSpan.FromSeconds(10));

// 或是無限等待直到所有工作執行完畢：
await daemon.StopAsync(Timeout.InfiniteTimeSpan);
```

### ⏰ Cron 語法與範例

標準 5 欄式 Cron 格式：
```
┌─── 分（0 - 59）
│ ┌─── 時（0 - 23）
│ │ ┌─── 日（1 - 31）
│ │ │ ┌─── 月（1 - 12）
│ │ │ │ ┌─── 週（0 - 6，7 為週日別名）
│ │ │ │ │
* * * * *
```
每欄支援：`*`、`?`、`*/n`、`start/n`（如 `0/15`）、`a-b`、`a-b/n`、`a,b,c`，以及逗號聯集（如 `1,2-10/2`）。

*(注意：日與週為 AND 語意，兩者皆命中才觸發；標準 cron 在兩者皆有限制時多為 OR)。*

| 需求 | 寫法 | 說明 |
|---|---|---|
| 每分鐘 | `* * * * *` | 每分鐘整點觸發 |
| 每 2 分鐘 | `*/2 * * * *` | 0, 2, 4, 6... 分 |
| 每 15 分鐘 | `0/15 * * * *` 或 `*/15 * * * *` | 0, 15, 30, 45 分 |
| 從第 5 分起每 10 分 | `5/10 * * * *` | 5, 15, 25, 35, 45, 55 分 |
| 每小時整點 | `0 * * * *` | 每小時 00 分 |
| 每小時的 0、1、2 分 | `0,1,2 * * * *` | 00、01、02 分 |
| 每小時第 1～55 分鐘每分鐘 | `1-55 * * * *` | 01 到 55 分每分鐘 |
| 每天 1、10、20 點的每分鐘 | `* 1,10,20 * * *` | 01:xx、10:xx、20:xx |
| 每天 08:00 | `0 8 * * *` | 每天早上八點 |
| 每天 12:30 | `30 12 * * *` | 每天 12:30 |
| 每天 08:00 與 20:00 | `0 8,20 * * *` | 每天兩次 |
| 每晚 22～23 點每 15 分 | `*/15 22-23 * * *` | 22:00、22:15 … 23:45 |
| 每週一 09:00 | `0 9 * * 1` | 週一 = 1 |
| 每個工作日 09:00 | `0 9 * * 1-5` | 週一至週五 09:00 |
| 每週日 00:00 | `0 0 * * 0` 或 `0 0 * * 7` | 週日 00:00 |
| 每月 1 號 00:00 | `0 0 1 * *` | 每月 1 號 00:00 |
| 每月 1 號與 15 號 00:00 | `0 0 1,15 * *` | 每月兩次 |
| 每年 1/1 00:00 | `0 0 1 1 *` | 每年一次 |
| 混合：分鐘 1、2、4、6、8、10 | `1,2-10/2 * * * *` | `1` 加上 `2-10/2` |
| 每天 09:00（`?` 萬用字元） | `0 9 * * ?` | 等同 `0 9 * * *`；相容 Quartz 的 `?` 語法 |

### 🔍 驗證與預測下次執行時間

```csharp
// 安全解析（不拋出例外）
if (CronSchedule.TryParse("0/15 * * * *", out var schedule))
{
    // 預算下一次觸發時間點
    DateTime? nextRun = schedule.GetNextOccurrence(DateTime.Now);
    Console.WriteLine($"下次執行時間：{nextRun}"); // 100 年內無符合時間（如 "0 0 31 2 *"）則為 null
}

// 靜態快速驗證格式
bool isValid = CronSchedule.IsValidExpression("0 8 * * 1-5"); // true
bool invalid = CronSchedule.IsValidExpression("60 * * * *");  // false（超出範圍）
```

### ⚙️ 行為細節

1. **分鐘精度**：排程在「每分鐘 00 秒過後約 250ms」觸發；同一分鐘最多觸發一次。
2. **重疊跳過**：某 job 上次還沒跑完，本次直接跳過（可用 `IsRunning` 查詢）。
3. **取消是協作式**：`Cancel()` / `Stop()` 只發訊號。handler 必須觀察 `CancellationToken`（傳給 `Task.Delay` / HttpClient 等，或檢查 `ct.IsCancellationRequested`），否則該次會跑完；不接受 token 的委派（`ThreadStart` / `Action`）無法中途停止。
   - `job.Cancel()` 會永久停用該 job（`IsCancelled` 為 true，之後不再觸發）。
   - `daemon.Stop()` 只取消執行中的那次，之後再 `Start()` 仍會照常排程。
   - handler 拋出非本身 token 造成的 `OperationCanceledException`（例如 HttpClient 逾時）會視為失敗並觸發 `Failed`。
4. **例外隔離**：job 內拋例外不會殺掉排程，會經 `job.Failed` / `daemon.JobFailed` 事件送出；某個訂閱者拋例外也不影響其他訂閱者收到通知。
5. **`Stop()` 不等待**：一律毫秒級返回；要等請用 `await StopAsync(timeout)`。`StopAsync` 會等待 daemon 為任何 `ICronJob` 啟動的任務，以及 `CronJob.CurrentTask`。
6. **`Start()` 冪等**：重複呼叫無害；`using` / `Dispose()` 會自動 `Stop()`。同一個 job 實例重複加入不會有效果。

### 📚 API 一覽

- **`CronSchedule`**：`CronSchedule(expression)`、`IsTime(DateTime)`、`GetNextOccurrence(DateTime)`、快取唯讀的 `Minutes` / `Hours` / `DaysOfMonth` / `Months` / `DaysOfWeek`、`Expression`、靜態 `TryParse(expr, out schedule)` / `IsValidExpression(expr)`。
- **`CronJob`**：`CronJob(schedule, handler)`，handler 可為 `ThreadStart` / `Action` / `Action<CancellationToken>` / `Func<CancellationToken, Task>`；`ExecuteAsync(now, daemonToken)`、`Cancel()`、`IsRunning`、`IsCancelled`、`CurrentTask`、`Failed`。
- **`CronDaemon`**：`AddJob` ×4（支援自訂 `ICronJob`）、`RemoveJob`、`Clear`、`Start`、`Stop`、`StopAsync(timeout)`（回傳 `Task<bool>`）、`IsRunning`、`Jobs`、`JobFailed`、`Dispose`。

---

## License

This project is licensed under the [MIT License](LICENSE).
