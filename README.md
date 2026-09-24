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

### 🚀 Quick Start

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using CronNET;

using (var daemon = new CronDaemon())
{
    // 1. Simple synchronous job (uninterruptible during execution, Cancel blocks next run)
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

    // Handle job failures safely (won't crash the scheduler)
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
*(Note: Day of Month and Day of Week use AND semantics—both must match).*

| Schedule | Expression | Description |
|---|---|---|
| Every minute | `* * * * *` | Every minute |
| Every 2 minutes | `*/2 * * * *` | Minutes: 0, 2, 4, 6... |
| Every 15 minutes | `0/15 * * * *` or `*/15 * * * *` | Minutes: 0, 15, 30, 45 |
| At 5, 15, 25, 35, 45, 55 min | `5/10 * * * *` | Minutes starting at 5 every 10 min |
| Top of every hour | `0 * * * *` | Hourly at :00 |
| Every day at 08:00 | `0 8 * * *` | 08:00 daily |
| Every weekday at 09:00 | `0 9 * * 1-5` | Mon - Fri at 09:00 |
| Every Sunday at midnight | `0 0 * * 0` or `0 0 * * 7` | Sunday 00:00 |
| 1st day of month at 00:00 | `0 0 1 * *` | Monthly on day 1 |
| Weekday with `?` wildcard | `0 9 * * ?` | Compatible with Quartz `?` |

### 🔍 Validation & Prediction

```csharp
// Safe validation without throwing exceptions
if (CronSchedule.TryParse("0/15 * * * *", out var schedule))
{
    // Predict next occurrence
    DateTime? nextRun = schedule.GetNextOccurrence(DateTime.Now);
    Console.WriteLine($"Next trigger: {nextRun}");
}

// Quick validation check
bool isValid = CronSchedule.IsValidExpression("0 8 * * 1-5"); // true
```

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

### 🚀 快速上手

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using CronNET;

using (var daemon = new CronDaemon())
{
    // 1. 簡易任務（執行中無法中途取消，Cancel 僅阻止下次排程）
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

    // 統一攔截任務例外（任務錯誤不會拖垮排程器）
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
*(注意：日與週為 AND 語意，兩者皆命中才觸發)。*

| 需求 | 寫法 | 說明 |
|---|---|---|
| 每分鐘 | `* * * * *` | 每分鐘整點觸發 |
| 每 2 分鐘 | `*/2 * * * *` | 0, 2, 4, 6... 分 |
| 每 15 分鐘 | `0/15 * * * *` 或 `*/15 * * * *` | 0, 15, 30, 45 分 |
| 從第 5 分起每 10 分 | `5/10 * * * *` | 5, 15, 25, 35, 45, 55 分 |
| 每小時整點 | `0 * * * *` | 每小時 00 分 |
| 每天 08:00 | `0 8 * * *` | 每天早上八點 |
| 每個工作日 09:00 | `0 9 * * 1-5` | 週一至週五 09:00 |
| 每週日 00:00 | `0 0 * * 0` 或 `0 0 * * 7` | 週日 00:00 |
| 每月 1 號 00:00 | `0 0 1 * *` | 每月 1 號 00:00 |
| 支援 `?` 萬用字元 | `0 9 * * ?` | 相容 Quartz 的 `?` 語法 |

### 🔍 驗證與預測下次執行時間

```csharp
// 安全解析（不拋出例外）
if (CronSchedule.TryParse("0/15 * * * *", out var schedule))
{
    // 預算下一次觸發時間點
    DateTime? nextRun = schedule.GetNextOccurrence(DateTime.Now);
    Console.WriteLine($"下次執行時間：{nextRun}");
}

// 靜態快速驗證格式
bool isValid = CronSchedule.IsValidExpression("0 8 * * 1-5"); // true
```

---

## License

This project is licensed under the [MIT License](LICENSE).
