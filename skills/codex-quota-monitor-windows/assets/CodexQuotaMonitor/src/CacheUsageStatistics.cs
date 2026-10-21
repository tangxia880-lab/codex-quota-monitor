using System;
using System.Collections.Generic;
using System.Globalization;

namespace CodexQuotaMonitor
{
    internal sealed class CacheUsageSummary
    {
        public int TotalRecords { get; private set; }
        public int IncludedRecords { get; private set; }
        public int MissingRecords { get; private set; }
        public int ZeroInputRecords { get; private set; }
        public decimal InputTokens { get; private set; }
        public decimal CachedTokens { get; private set; }
        public double? HitPercent
        {
            get { return InputTokens > 0 ? (double?)(CachedTokens / InputTokens * 100M) : null; }
        }

        internal CacheUsageSummary(int totalRecords, int includedRecords, int missingRecords, int zeroInputRecords, decimal inputTokens, decimal cachedTokens)
        {
            TotalRecords = totalRecords;
            IncludedRecords = includedRecords;
            MissingRecords = missingRecords;
            ZeroInputRecords = zeroInputRecords;
            InputTokens = inputTokens;
            CachedTokens = cachedTokens;
        }
    }

    internal static class CacheUsageStatistics
    {
        internal static double? Percent(TokenCounts counts)
        {
            long input, cached;
            if (!TryRead(counts, out input, out cached) || input == 0) return null;
            return (double)((decimal)cached / input * 100M);
        }

        internal static string FormatPercent(TokenCounts counts)
        {
            double? percent = Percent(counts);
            return percent.HasValue ? percent.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "未知";
        }

        internal static CacheUsageSummary Summarize(IEnumerable<TaskUsageRecord> records)
        {
            var unique = new List<TaskUsageRecord>();
            var positions = new Dictionary<string, int>(StringComparer.Ordinal);
            if (records != null)
            {
                foreach (TaskUsageRecord record in records)
                {
                    int position;
                    if (record != null && !String.IsNullOrEmpty(record.Key))
                    {
                        if (positions.TryGetValue(record.Key, out position))
                        {
                            // Later snapshots repair earlier records, including
                            // corrections from a known value back to unknown.
                            unique[position] = record;
                            continue;
                        }
                        positions.Add(record.Key, unique.Count);
                    }
                    unique.Add(record);
                }
            }

            int included = 0, missing = 0, zeroInput = 0;
            decimal inputTotal = 0, cachedTotal = 0;
            foreach (TaskUsageRecord record in unique)
            {
                long input, cached;
                // Usage belongs to this independent turn. Cumulative would count
                // earlier turns again; parent and child turns already have their
                // own usage and are therefore both retained here.
                if (!TryRead(record == null ? null : record.Usage, out input, out cached))
                {
                    missing++;
                    continue;
                }
                if (input == 0)
                {
                    zeroInput++;
                    continue;
                }
                included++;
                // Decimal sums avoid overflowing Int64 across many valid turns.
                inputTotal += input;
                cachedTotal += cached;
            }
            return new CacheUsageSummary(unique.Count, included, missing, zeroInput, inputTotal, cachedTotal);
        }

        private static bool TryRead(TokenCounts counts, out long input, out long cached)
        {
            input = cached = 0;
            if (counts == null || !counts.Input.HasValue || !counts.CachedInput.HasValue) return false;
            input = counts.Input.Value;
            cached = counts.CachedInput.Value;
            return input >= 0 && cached >= 0 && cached <= input;
        }
    }
}
