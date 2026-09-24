using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace CronNET
{
    public interface ICronSchedule
    {
        bool IsValid(string expression);
        bool IsTime(DateTime dateTime);
    }

    /// <summary>
    /// 標準 5 欄 cron 解析器：分 時 日 月 週
    /// 語法支援：*、?、*/n、start/n、a-b、a-b/n、a,b,c（含 union，如 1,2-5/2）
    /// 欄位範圍：分 0-59、時 0-23、日 1-31、月 1-12、週 0-6（7 視為週日別名）。
    /// 日與週之間為 AND 語意（皆須命中才執行）。
    /// </summary>
    public class CronSchedule : ICronSchedule
    {
        // 欄位邊界（閉區間）
        private const int MinMinute = 0, MaxMinute = 59;
        private const int MinHour = 0, MaxHour = 23;
        private const int MinDayOfMonth = 1, MaxDayOfMonth = 31;
        private const int MinMonth = 1, MaxMonth = 12;
        private const int MinDayOfWeek = 0, MaxDayOfWeek = 6;
        // 星期輸入上限 7（7 = 週日別名，正規化為 0）
        private const int MaxDayOfWeekInput = 7;

        private readonly string _expression;
        private readonly HashSet<int> _minutes;
        private readonly HashSet<int> _hours;
        private readonly HashSet<int> _daysOfMonth;
        private readonly HashSet<int> _months;
        private readonly HashSet<int> _daysOfWeek;

        // 快取 ReadOnlyCollection，避免每次屬性讀取都重複 OrderBy + ToList 配置記憶體
        private readonly ReadOnlyCollection<int> _readOnlyMinutes;
        private readonly ReadOnlyCollection<int> _readOnlyHours;
        private readonly ReadOnlyCollection<int> _readOnlyDaysOfMonth;
        private readonly ReadOnlyCollection<int> _readOnlyMonths;
        private readonly ReadOnlyCollection<int> _readOnlyDaysOfWeek;

        public IReadOnlyCollection<int> Minutes => _readOnlyMinutes;
        public IReadOnlyCollection<int> Hours => _readOnlyHours;
        public IReadOnlyCollection<int> DaysOfMonth => _readOnlyDaysOfMonth;
        public IReadOnlyCollection<int> Months => _readOnlyMonths;
        public IReadOnlyCollection<int> DaysOfWeek => _readOnlyDaysOfWeek;
        public string Expression => _expression;

        /// <summary>預設每分鐘。</summary>
        public CronSchedule() : this("* * * * *")
        {
        }

        public CronSchedule(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                throw new ArgumentException("Cron expression 不可為空。", nameof(expression));

            _expression = expression.Trim();
            string[] fields = _expression.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length != 5)
                throw new FormatException(
                    $"Cron expression 必須恰好 5 個欄位（分 時 日 月 週），實際為 {fields.Length} 個：'{_expression}'。");

            try
            {
                _minutes = ParseField(fields[0], MinMinute, MaxMinute, "分", false);
                _hours = ParseField(fields[1], MinHour, MaxHour, "時", false);
                _daysOfMonth = ParseField(fields[2], MinDayOfMonth, MaxDayOfMonth, "日", false);
                _months = ParseField(fields[3], MinMonth, MaxMonth, "月", false);
                _daysOfWeek = ParseField(fields[4], MinDayOfWeek, MaxDayOfWeekInput, "週", true);

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
                throw new FormatException($"Cron expression 解析失敗：'{_expression}'。{ex.Message}", ex);
            }
        }

        /// <summary>
        /// 靜態預檢 Cron 表達式格式是否有效。
        /// </summary>
        public static bool IsValidExpression(string expression) => TryParse(expression, out _);

        /// <summary>
        /// 嘗試解析 Cron 表達式，解析失敗回傳 false 而不拋出例外。
        /// </summary>
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

        public bool IsValid(string expression) => IsValidExpression(expression);

        public bool IsTime(DateTime dateTime)
        {
            return _minutes.Contains(dateTime.Minute) &&
                   _hours.Contains(dateTime.Hour) &&
                   _daysOfMonth.Contains(dateTime.Day) &&
                   _months.Contains(dateTime.Month) &&
                   _daysOfWeek.Contains((int)dateTime.DayOfWeek);
        }

        /// <summary>
        /// 預測指定時間點之後的下一次觸發時間。若 1 年內無符合時間則回傳 null。
        /// </summary>
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

        #region 解析內部實作

        private static HashSet<int> ParseField(string field, int min, int max, string fieldName, bool isDayOfWeek)
        {
            if (string.IsNullOrWhiteSpace(field))
                throw new FormatException($"「{fieldName}」欄位不可為空。");

            var result = new HashSet<int>();
            string[] tokens = field.Split(',');
            foreach (string raw in tokens)
            {
                string token = raw.Trim();
                if (token.Length == 0)
                    throw new FormatException($"「{fieldName}」欄位含空 token：'{field}'。");
                foreach (int v in ExpandToken(token, min, max, fieldName, isDayOfWeek))
                    result.Add(v);
            }

            if (result.Count == 0)
                throw new FormatException($"「{fieldName}」欄位解析結果為空：'{field}'。");
            return result;
        }

        private static IEnumerable<int> ExpandToken(string token, int min, int max, string fieldName, bool isDayOfWeek)
        {
            // * 萬用字元（或是 Quartz 常見的 ? 符號）
            if (token == "*" || (token == "?" && (fieldName == "日" || fieldName == "週")))
            {
                for (int i = min; i <= max; i++)
                    yield return Normalize(i, isDayOfWeek);
                yield break;
            }

            // 支援：*/step、range/step、以及 start/step（如 0/15、5/10）
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

            // range (a-b)
            if (token.IndexOf('-') >= 0)
            {
                ParseRange(token, min, max, fieldName, isDayOfWeek, out int start, out int end);
                for (int i = start; i <= end; i++)
                    yield return Normalize(i, isDayOfWeek);
                yield break;
            }

            // single value
            yield return Normalize(ParseValue(token, min, max, fieldName, isDayOfWeek), isDayOfWeek);
        }

        private static void ParseRange(string text, int min, int max, string fieldName, bool isDayOfWeek, out int start, out int end)
        {
            string[] parts = text.Split('-');
            if (parts.Length != 2)
                throw new FormatException($"「{fieldName}」範圍格式錯誤：'{text}'（應為 a-b）。");
            start = ParseValue(parts[0].Trim(), min, max, fieldName, isDayOfWeek);
            end = ParseValue(parts[1].Trim(), min, max, fieldName, isDayOfWeek);
            if (start > end)
                throw new FormatException($"「{fieldName}」範圍起始不可大於結束：'{text}'。");
        }

        private static int ParseValue(string text, int min, int max, string fieldName, bool isDayOfWeek)
        {
            if (!int.TryParse(text, out int v))
                throw new FormatException($"「{fieldName}」含非數字：'{text}'。");
            int effectiveMax = isDayOfWeek ? MaxDayOfWeekInput : max;
            if (v < min || v > effectiveMax)
                throw new FormatException($"「{fieldName}」數值 {v} 超出範圍 [{min}-{effectiveMax}]。");
            return v;
        }

        private static int ParseStep(string text, string fieldName)
        {
            if (!int.TryParse(text.Trim(), out int step) || step <= 0)
                throw new FormatException($"「{fieldName}」步進值必須為正整數：'{text}'。");
            return step;
        }

        private static int Normalize(int v, bool isDayOfWeek)
        {
            // 週日別名 7 -> 0
            return (isDayOfWeek && v == MaxDayOfWeekInput) ? 0 : v;
        }

        #endregion
    }
}
