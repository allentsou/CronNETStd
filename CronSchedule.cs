using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CronNET
{
    /// <summary>
    /// Defines a cron schedule contract.
    /// <para>定義 Cron 排程合約介面。</para>
    /// </summary>
    public interface ICronSchedule
    {
        /// <summary>
        /// Validates whether the given cron expression is valid.
        /// <para>驗證指定的 Cron 表達式格式是否有效。</para>
        /// </summary>
        bool IsValid(string expression);

        /// <summary>
        /// Determines whether the specified date and time matches this schedule.
        /// <para>判斷指定的日期與時間是否符合此排程。</para>
        /// </summary>
        bool IsTime(DateTime dateTime);
    }

    /// <summary>
    /// Standard 5-field cron expression parser: minute, hour, day of month, month, day of week.
    /// <para>標準 5 欄式 Cron 表達式解析器：分、時、日、月、週。</para>
    /// <para>語法支援：*、?、*/n、start/n、a-b、a-b/n、a,b,c（含 union，如 1,2-5/2）。</para>
    /// <para>欄位範圍：分 0-59、時 0-23、日 1-31、月 1-12、週 0-6（7 視為週日別名）。</para>
    /// <para>日與週之間為 AND 語意（皆須命中才執行）。</para>
    /// </summary>
    public class CronSchedule : ICronSchedule
    {
        // Field boundaries (closed intervals)
        private const int MinMinute = 0, MaxMinute = 59;
        private const int MinHour = 0, MaxHour = 23;
        private const int MinDayOfMonth = 1, MaxDayOfMonth = 31;
        private const int MinMonth = 1, MaxMonth = 12;
        private const int MinDayOfWeek = 0, MaxDayOfWeek = 6;
        // Upper input bound for day of week: 7 (7 = Sunday alias, normalized to 0)
        private const int MaxDayOfWeekInput = 7;

        private readonly string _expression;
        private readonly HashSet<int> _minutes;
        private readonly HashSet<int> _hours;
        private readonly HashSet<int> _daysOfMonth;
        private readonly HashSet<int> _months;
        private readonly HashSet<int> _daysOfWeek;

        // Cache ReadOnlyCollection to avoid repeated OrderBy + ToList heap allocations on each property access
        private readonly ReadOnlyCollection<int> _readOnlyMinutes;
        private readonly ReadOnlyCollection<int> _readOnlyHours;
        private readonly ReadOnlyCollection<int> _readOnlyDaysOfMonth;
        private readonly ReadOnlyCollection<int> _readOnlyMonths;
        private readonly ReadOnlyCollection<int> _readOnlyDaysOfWeek;

        /// <summary>
        /// Gets the matched minutes (0-59).
        /// <para>取得符合的分鐘清單（0-59）。</para>
        /// </summary>
        public IReadOnlyCollection<int> Minutes => _readOnlyMinutes;

        /// <summary>
        /// Gets the matched hours (0-23).
        /// <para>取得符合的小時清單（0-23）。</para>
        /// </summary>
        public IReadOnlyCollection<int> Hours => _readOnlyHours;

        /// <summary>
        /// Gets the matched days of month (1-31).
        /// <para>取得符合的日期清單（1-31）。</para>
        /// </summary>
        public IReadOnlyCollection<int> DaysOfMonth => _readOnlyDaysOfMonth;

        /// <summary>
        /// Gets the matched months (1-12).
        /// <para>取得符合的月份清單（1-12）。</para>
        /// </summary>
        public IReadOnlyCollection<int> Months => _readOnlyMonths;

        /// <summary>
        /// Gets the matched days of week (0-6, Sunday is 0).
        /// <para>取得符合的星期清單（0-6，0 為週日）。</para>
        /// </summary>
        public IReadOnlyCollection<int> DaysOfWeek => _readOnlyDaysOfWeek;

        /// <summary>
        /// Gets the original cron expression string.
        /// <para>取得原始的 Cron 表達式字串。</para>
        /// </summary>
        public string Expression => _expression;

        /// <summary>
        /// Initializes a new instance of <see cref="CronSchedule"/> defaulting to every minute (* * * * *).
        /// <para>初始化 <see cref="CronSchedule"/> 類別的新執行個體，預設為每分鐘執行一次 (* * * * *)。</para>
        /// </summary>
        public CronSchedule() : this("* * * * *")
        {
        }

        /// <summary>
        /// Initializes a new instance of <see cref="CronSchedule"/> with the specified cron expression.
        /// <para>使用指定的 Cron 表達式初始化 <see cref="CronSchedule"/> 類別的新執行個體。</para>
        /// </summary>
        /// <param name="expression">Standard 5-field cron expression.<para>標準 5 欄式 Cron 表達式。</para></param>
        public CronSchedule(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                throw new ArgumentException("Cron expression cannot be empty.", nameof(expression));

            _expression = expression.Trim();
            string[] fields = _expression.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5)
                throw new FormatException(
                    $"Cron expression must have exactly 5 fields (minute, hour, day, month, day-of-week), but found {fields.Length}: '{_expression}'.");

            try
            {
                _minutes = ParseField(fields[0], MinMinute, MaxMinute, "minute", false);
                _hours = ParseField(fields[1], MinHour, MaxHour, "hour", false);
                _daysOfMonth = ParseField(fields[2], MinDayOfMonth, MaxDayOfMonth, "day of month", false);
                _months = ParseField(fields[3], MinMonth, MaxMonth, "month", false);
                _daysOfWeek = ParseField(fields[4], MinDayOfWeek, MaxDayOfWeekInput, "day of week", true);

                _readOnlyMinutes = new ReadOnlyCollection<int>(_minutes.OrderBy(x => x).ToList());
                _readOnlyHours = new ReadOnlyCollection<int>(_hours.OrderBy(x => x).ToList());
                _readOnlyDaysOfMonth = new ReadOnlyCollection<int>(_daysOfMonth.OrderBy(x => x).ToList());
                _readOnlyMonths = new ReadOnlyCollection<int>(_months.OrderBy(x => x).ToList());
                _readOnlyDaysOfWeek = new ReadOnlyCollection<int>(_daysOfWeek.OrderBy(x => x).ToList());
            }
            catch (FormatException)
            {
                throw;
            }
            catch (Exception ex)
            {
                throw new FormatException($"Failed to parse cron expression: '{_expression}'. {ex.Message}", ex);
            }
        }

        /// <summary>
        /// Statically pre-checks whether a cron expression format is valid.
        /// <para>靜態預檢 Cron 表達式格式是否有效。</para>
        /// </summary>
        /// <param name="expression">The cron expression to validate.<para>欲驗證的 Cron 表達式。</para></param>
        /// <returns>True if valid; otherwise, false.<para>若有效則為 true；否則為 false。</para></returns>
        public static bool IsValidExpression(string expression) => TryParse(expression, out _);

        /// <summary>
        /// Attempts to parse a cron expression. Returns false instead of throwing if parsing fails.
        /// <para>嘗試解析 Cron 表達式。解析失敗回傳 false 而不拋出例外。</para>
        /// </summary>
        /// <param name="expression">The cron expression to parse.<para>欲解析的 Cron 表達式。</para></param>
        /// <param name="schedule">The parsed <see cref="CronSchedule"/> instance if successful; otherwise, null.<para>若成功則包含解析後的 <see cref="CronSchedule"/> 實例；否則為 null。</para></param>
        /// <returns>True if successfully parsed; otherwise, false.<para>若解析成功則為 true；否則為 false。</para></returns>
        public static bool TryParse(string expression, out CronSchedule schedule)
        {
            schedule = null;
            if (string.IsNullOrWhiteSpace(expression))
                return false;
            try
            {
                schedule = new CronSchedule(expression);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Validates whether the given cron expression is valid.
        /// <para>驗證指定的 Cron 表達式格式是否有效。</para>
        /// </summary>
        public bool IsValid(string expression) => IsValidExpression(expression);

        /// <summary>
        /// Determines whether the specified date and time matches this schedule.
        /// <para>判斷指定的日期與時間是否符合此排程。</para>
        /// </summary>
        public bool IsTime(DateTime dateTime)
        {
            return _minutes.Contains(dateTime.Minute) &&
                   _hours.Contains(dateTime.Hour) &&
                   _daysOfMonth.Contains(dateTime.Day) &&
                   _months.Contains(dateTime.Month) &&
                   _daysOfWeek.Contains((int)dateTime.DayOfWeek);
        }

        /// <summary>
        /// Predicts the next occurrence time after the specified reference time. Returns null if no match within 1 year.
        /// <para>預測指定時間點之後的下一次觸發時間。若 1 年內無符合時間則回傳 null。</para>
        /// </summary>
        /// <param name="fromTime">The reference starting time.<para>起算基準時間。</para></param>
        /// <returns>The next matching trigger time, or null if not found within a year.<para>下一次觸發時間，若一年內無匹配則為 null。</para></returns>
        public DateTime? GetNextOccurrence(DateTime fromTime)
        {
            DateTime cur = new DateTime(fromTime.Year, fromTime.Month, fromTime.Day, fromTime.Hour, fromTime.Minute, 0, fromTime.Kind).AddMinutes(1);
            DateTime max = cur.AddYears(1);

            while (cur < max)
            {
                if (!_months.Contains(cur.Month))
                {
                    cur = new DateTime(cur.Year, cur.Month, 1, 0, 0, 0, cur.Kind).AddMonths(1);
                    continue;
                }

                if (!_daysOfMonth.Contains(cur.Day) || !_daysOfWeek.Contains((int)cur.DayOfWeek))
                {
                    cur = new DateTime(cur.Year, cur.Month, cur.Day, 0, 0, 0, cur.Kind).AddDays(1);
                    continue;
                }

                if (!_hours.Contains(cur.Hour))
                {
                    cur = new DateTime(cur.Year, cur.Month, cur.Day, cur.Hour, 0, 0, cur.Kind).AddHours(1);
                    continue;
                }

                if (_minutes.Contains(cur.Minute))
                    return cur;

                cur = cur.AddMinutes(1);
            }

            return null;
        }

        #region Internal Parsing Implementation

        private static HashSet<int> ParseField(string field, int min, int max, string fieldName, bool isDayOfWeek)
        {
            if (string.IsNullOrWhiteSpace(field))
                throw new FormatException($"The '{fieldName}' field cannot be empty.");

            var result = new HashSet<int>();
            string[] tokens = field.Split(',');
            foreach (string raw in tokens)
            {
                string token = raw.Trim();
                if (token.Length == 0)
                    throw new FormatException($"The '{fieldName}' field contains an empty token: '{field}'.");
                foreach (int v in ExpandToken(token, min, max, fieldName, isDayOfWeek))
                    result.Add(v);
            }

            if (result.Count == 0)
                throw new FormatException($"The '{fieldName}' field parsed to an empty result: '{field}'.");
            return result;
        }

        private static IEnumerable<int> ExpandToken(string token, int min, int max, string fieldName, bool isDayOfWeek)
        {
            // Wildcard * (or Quartz-compatible ? for day of month / day of week)
            if (token == "*" || (token == "?" && (fieldName == "day of month" || fieldName == "day of week")))
            {
                for (int i = min; i <= max; i++)
                    yield return Normalize(i, isDayOfWeek);
                yield break;
            }

            // Supports: */step, range/step, and start/step (e.g. 0/15, 5/10)
            int slash = token.IndexOf('/');
            if (slash >= 0)
            {
                string left = token.Substring(0, slash).Trim();
                string right = token.Substring(slash + 1).Trim();
                int step = ParseStep(right, fieldName);

                int start, end;
                if (left == "*")
                {
                    start = min;
                    end = max;
                }
                else if (left.IndexOf('-') >= 0)
                {
                    ParseRange(left, min, max, fieldName, isDayOfWeek, out start, out end);
                }
                else
                {
                    start = ParseValue(left, min, max, fieldName, isDayOfWeek);
                    end = isDayOfWeek ? MaxDayOfWeekInput : max;
                }

                for (int i = start; i <= end; i++)
                {
                    if ((i - start) % step == 0)
                        yield return Normalize(i, isDayOfWeek);
                }
                yield break;
            }

            // Range (a-b)
            if (token.IndexOf('-') >= 0)
            {
                ParseRange(token, min, max, fieldName, isDayOfWeek, out int start, out int end);
                for (int i = start; i <= end; i++)
                    yield return Normalize(i, isDayOfWeek);
                yield break;
            }

            // Single value
            yield return Normalize(ParseValue(token, min, max, fieldName, isDayOfWeek), isDayOfWeek);
        }

        private static void ParseRange(string text, int min, int max, string fieldName, bool isDayOfWeek, out int start, out int end)
        {
            string[] parts = text.Split('-');
            if (parts.Length != 2)
                throw new FormatException($"Invalid range format for '{fieldName}': '{text}' (expected a-b).");
            start = ParseValue(parts[0].Trim(), min, max, fieldName, isDayOfWeek);
            end = ParseValue(parts[1].Trim(), min, max, fieldName, isDayOfWeek);
            if (start > end)
                throw new FormatException($"Range start cannot be greater than end for '{fieldName}': '{text}'.");
        }

        private static int ParseValue(string text, int min, int max, string fieldName, bool isDayOfWeek)
        {
            if (!int.TryParse(text, out int v))
                throw new FormatException($"Non-numeric value found for '{fieldName}': '{text}'.");
            int effectiveMax = isDayOfWeek ? MaxDayOfWeekInput : max;
            if (v < min || v > effectiveMax)
                throw new FormatException($"Value {v} is out of range [{min}-{effectiveMax}] for '{fieldName}'.");
            return v;
        }

        private static int ParseStep(string text, string fieldName)
        {
            if (!int.TryParse(text.Trim(), out int step) || step <= 0)
                throw new FormatException($"Step value must be a positive integer for '{fieldName}': '{text}'.");
            return step;
        }

        private static int Normalize(int v, bool isDayOfWeek)
        {
            // Normalize Sunday alias: 7 -> 0
            return (isDayOfWeek && v == MaxDayOfWeekInput) ? 0 : v;
        }

        #endregion
    }
}
