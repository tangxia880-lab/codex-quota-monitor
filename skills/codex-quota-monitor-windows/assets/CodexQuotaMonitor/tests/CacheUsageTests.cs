using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using CodexQuotaMonitor;

internal static class CacheUsageTests
{
    private static int assertions;

    private static void Check(bool condition, string message)
    {
        assertions++;
        if (!condition) throw new Exception("FAIL: " + message);
    }

    private static TokenCounts Counts(long? input, long? cached)
    {
        return new TokenCounts { Input = input, CachedInput = cached };
    }

    private static TaskUsageRecord Record(string key, long? input, long? cached)
    {
        return new TaskUsageRecord { Key = key, Usage = Counts(input, cached) };
    }

    private static void CheckPercent(double? actual, double expected, string message)
    {
        Check(actual.HasValue && Math.Abs(actual.Value - expected) < .000000001, message);
    }

    private static void TestIndividualRates()
    {
        CheckPercent(CacheUsageStatistics.Percent(Counts(100, 0)), 0, "known zero cached input is a zero percent hit rate");
        CheckPercent(CacheUsageStatistics.Percent(Counts(100, 100)), 100, "fully cached input is one hundred percent");
        CheckPercent(CacheUsageStatistics.Percent(Counts(400, 125)), 31.25, "cache percentage uses input denominator");
        Check(CacheUsageStatistics.FormatPercent(Counts(3, 1)) == "33.3%", "percentage formats to one decimal");
        Check(CacheUsageStatistics.FormatPercent(Counts(10, 0)) == "0.0%", "known zero percentage stays distinct from unknown");
        Check(CacheUsageStatistics.FormatPercent(Counts(10, 10)) == "100.0%", "full percentage keeps one decimal");
        foreach (TokenCounts invalid in new[] { null, Counts(null, null), Counts(null, 0), Counts(5, null), Counts(0, 0), Counts(0, null), Counts(0, 1), Counts(-1, 0), Counts(4, -1), Counts(4, 5) })
        {
            Check(!CacheUsageStatistics.Percent(invalid).HasValue, "invalid or zero-input rate is unknown");
            Check(CacheUsageStatistics.FormatPercent(invalid) == "未知", "invalid or zero-input formatted rate is unknown");
        }
        TokenCounts unrelated = Counts(100, 40);
        unrelated.Total = null; unrelated.Output = 9999; unrelated.Reasoning = 999; unrelated.CacheWriteInput = 500;
        CheckPercent(CacheUsageStatistics.Percent(unrelated), 40, "total output reasoning and cache-write values do not change cache-read rate");
        CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            Check(CacheUsageStatistics.FormatPercent(Counts(8, 1)) == "12.5%", "format remains consistent across machine locales");
        }
        finally { Thread.CurrentThread.CurrentCulture = previous; }
    }

    private static void TestWeightedSummary()
    {
        TaskUsageRecord large = Record("large", 1000, 900);
        large.Cumulative = Counts(900000, 900000);
        TaskUsageRecord small = Record("small", 20, 0);
        small.Cumulative = Counts(920000, 920000);
        CacheUsageSummary result = CacheUsageStatistics.Summarize(new[] { large, small });
        Check(result.TotalRecords == 2 && result.IncludedRecords == 2 && result.MissingRecords == 0 && result.ZeroInputRecords == 0, "weighted summary includes both valid rounds");
        Check(result.InputTokens == 1020M && result.CachedTokens == 900M, "summary sums independent Usage fields and ignores Cumulative");
        CheckPercent(result.HitPercent, 900D / 1020D * 100, "summary is input-weighted rather than mean of round percentages");
        Check(result.HitPercent != 45, "unequal rounds cannot produce unweighted mean");
        Check(large.Usage.Input == 1000 && large.Usage.CachedInput == 900 && large.Cumulative.Input == 900000, "statistics do not mutate source records");

        var mixed = new List<TaskUsageRecord> {
            Record("valid", 10, 3), Record("no-hits", 5, 0), Record("zero", 0, 0), Record("zero-unknown", 0, null),
            Record("missing-input", null, 0), Record("missing-cache", 8, null), Record("negative-input", -2, 0),
            Record("negative-cache", 8, -1), Record("too-much-cache", 8, 9), Record("zero-invalid", 0, 1),
            new TaskUsageRecord { Key = "null-usage", Usage = null }, null
        };
        result = CacheUsageStatistics.Summarize(mixed);
        Check(result.TotalRecords == 12 && result.IncludedRecords == 2, "all independent records are counted and only valid positive inputs included");
        Check(result.ZeroInputRecords == 1 && result.MissingRecords == 9, "zero input with known zero cache is separated from incomplete or invalid data");
        Check(result.InputTokens == 15M && result.CachedTokens == 3M, "invalid records contribute no numerator or denominator");
        CheckPercent(result.HitPercent, 20, "known zero-cache sample remains in weighted denominator");
        Check(result.TotalRecords == result.IncludedRecords + result.ZeroInputRecords + result.MissingRecords, "sample accounting partitions deduplicated records");
        result = CacheUsageStatistics.Summarize(new[] { Record("only-zero", 0, 0), Record("only-missing", null, null) });
        Check(!result.HitPercent.HasValue && result.InputTokens == 0 && result.CachedTokens == 0, "all-excluded summary is unknown rather than zero percent");
        result = CacheUsageStatistics.Summarize(null);
        Check(result.TotalRecords == 0 && !result.HitPercent.HasValue, "null collection is empty unknown summary");
        result = CacheUsageStatistics.Summarize(new TaskUsageRecord[0]);
        Check(result.TotalRecords == 0 && result.IncludedRecords == 0 && result.MissingRecords == 0 && result.ZeroInputRecords == 0 && !result.HitPercent.HasValue, "empty collection has no sample or assumed rate");
    }

    private static void TestDeduplicationAndChildren()
    {
        CacheUsageSummary result = CacheUsageStatistics.Summarize(new[] {
            Record("same", 100, 100), Record("other", 10, 0), Record("same", 20, 5)
        });
        Check(result.TotalRecords == 2 && result.IncludedRecords == 2, "nonempty record keys are deduplicated");
        Check(result.InputTokens == 30M && result.CachedTokens == 5M, "last record wins without accumulating earlier snapshot");
        result = CacheUsageStatistics.Summarize(new[] { Record("repair", 100, 20), Record("repair", 100, null) });
        Check(result.TotalRecords == 1 && result.MissingRecords == 1 && !result.HitPercent.HasValue, "later unknown correction replaces prior valid sample");
        result = CacheUsageStatistics.Summarize(new[] { Record("repair", null, null), Record("repair", 100, 25) });
        Check(result.TotalRecords == 1 && result.IncludedRecords == 1, "later completed fields repair missing record");
        CheckPercent(result.HitPercent, 25, "repaired record contributes once");
        result = CacheUsageStatistics.Summarize(new[] { Record(null, 10, 1), Record(null, 10, 2), Record("", 10, 3), Record("", 10, 4) });
        Check(result.TotalRecords == 4 && result.InputTokens == 40M && result.CachedTokens == 10M, "empty keys remain independent samples");
        result = CacheUsageStatistics.Summarize(new[] { Record("A", 10, 1), Record("a", 10, 2) });
        Check(result.TotalRecords == 2, "record identity uses ordinal case-sensitive keys");

        TaskUsageRecord parent = Record("parent:round", 120, 60);
        parent.ThreadId = "parent"; parent.ParentThreadId = null; parent.Cumulative = Counts(12000, 10000);
        TaskUsageRecord child = Record("child:round", 80, 40);
        child.ThreadId = "child"; child.ParentThreadId = "parent"; child.Cumulative = Counts(12080, 10040);
        result = CacheUsageStatistics.Summarize(new[] { parent, child, child.Clone(), parent.Clone() });
        Check(result.TotalRecords == 2 && result.IncludedRecords == 2, "parent and child each contribute exactly one independent turn");
        Check(result.InputTokens == 200M && result.CachedTokens == 100M, "child usage is included without inherited cumulative history");
        CheckPercent(result.HitPercent, 50, "parent and child produce weighted own-usage hit rate");
    }

    private static void TestLargeCounts()
    {
        CheckPercent(CacheUsageStatistics.Percent(Counts(Int64.MaxValue, Int64.MaxValue)), 100, "individual percentage multiplication does not overflow Int64");
        var records = new List<TaskUsageRecord>();
        for (int index = 0; index < 8; index++) records.Add(Record(index.ToString(CultureInfo.InvariantCulture), Int64.MaxValue, Int64.MaxValue / 2));
        CacheUsageSummary result = CacheUsageStatistics.Summarize(records);
        Check(result.InputTokens == (decimal)Int64.MaxValue * 8M, "input sum can exceed Int64 without wrapping");
        Check(result.CachedTokens == (decimal)(Int64.MaxValue / 2) * 8M, "cached sum can exceed Int64 without wrapping");
        CheckPercent(result.HitPercent, 50, "large weighted hit percentage stays correct");
    }

    private static int Main()
    {
        try
        {
            TestIndividualRates(); TestWeightedSummary(); TestDeduplicationAndChildren(); TestLargeCounts();
            Console.WriteLine("PASS: " + assertions + " synthetic cache-usage assertions; no live sessions or cost estimates.");
            return 0;
        }
        catch (Exception error) { Console.WriteLine(error.ToString()); return 1; }
    }
}
