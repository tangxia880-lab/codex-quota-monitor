using System;
using CodexQuotaMonitor;

internal static class ModelTests
{
    private static int count;
    private static void Assert(bool condition, string name)
    {
        count++;
        if (!condition) throw new Exception("FAIL: " + name);
    }

    private static UsageSnapshot ParseWindow(string primary, string account)
    {
        return UsageSnapshot.FromJson("{\"accountId\":\"" + account + "\",\"rateLimits\":{\"primary\":" + primary + ",\"secondary\":null}}");
    }

    private static UsageSnapshot Sample(double used, string reset, string account)
    {
        return ParseWindow("{\"usedPercent\":" + used.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"windowDurationMins\":300,\"resetsAt\":" + reset + "}", account);
    }

    public static int Main()
    {
        try
        {
            foreach (string malformed in new string[] { null, "", "{", "null", "[]", "123", "{}", "{\"result\":null}", "{\"rateLimits\":true}" })
                Assert(UsageSnapshot.FromJson(malformed).Buckets.Count == 0, "malformed root stays unknown");

            UsageSnapshot absent = ParseWindow("null", "a");
            Assert(absent.MainBucket.Windows.Count == 0, "absent windows do not create fictional periods");
            Assert(ParseWindow("true", "a").MainBucket.Windows.Count == 0, "malformed window values do not create periods");
            UsageSnapshot unknown = ParseWindow("{}", "a");
            Assert(unknown.MainBucket.Windows.Count == 1, "explicit empty window object retained as unknown");
            Assert(!unknown.MainBucket.Windows[0].RemainingPercent.HasValue, "null is not zero");
            Assert(unknown.MainBucket.Windows[0].Label == "未知周期", "unknown duration label");
            Assert(unknown.MainBucket.Windows[0].ResetLabel == "未提供重置时间", "unknown reset label");
            foreach (string value in new string[] { "null", "true", "\"bad\"", "{}", "[]", "\"NaN\"", "\"Infinity\"" })
                Assert(!ParseWindow("{\"usedPercent\":" + value + "}", "a").MainBucket.Windows[0].RemainingPercent.HasValue, "malformed percent stays unknown");

            Assert(Sample(-5, "null", "a").MainBucket.Windows[0].RemainingPercent == 100, "clamp negative used");
            Assert(Sample(120, "null", "a").MainBucket.Windows[0].RemainingPercent == 0, "clamp excess used");
            Assert(Sample(79.25, "null", "a").MainBucket.Windows[0].RemainingPercent == 20.75, "fraction preserved");
            Assert(Sample(80, "null", "a").MainBucket.Windows[0].Label == "5 小时", "5 hour label");
            Assert(ParseWindow("{\"usedPercent\":1,\"windowDurationMins\":10080}", "a").MainBucket.Windows[0].Label == "7 天", "7 day label");
            Assert(ParseWindow("{\"usedPercent\":1,\"windowDurationMins\":1501}", "a").MainBucket.Windows[0].Label == "1 天 1 小时 1 分钟", "mixed duration label");
            foreach (string duration in new string[] { "null", "0", "-1", "1.5", "99999999999999", "true" })
                Assert(ParseWindow("{\"usedPercent\":1,\"windowDurationMins\":" + duration + "}", "a").MainBucket.Windows[0].Label == "未知周期", "invalid duration");
            foreach (string epoch in new string[] { "null", "true", "1.5", "99999999999999", "\"bad\"" })
                Assert(!Sample(80, epoch, "a").MainBucket.Windows[0].ResetEpochSeconds.HasValue, "invalid epoch");

            UsageSnapshot multiple = UsageSnapshot.FromJson("{\"result\":{\"accountId\":\"test-account\",\"rateLimitResetCredits\":{\"availableCount\":3},\"rateLimits\":{\"primary\":{\"usedPercent\":99}},\"rateLimitsByLimitId\":{\"spark\":{\"limitName\":\"Spark\",\"primary\":{\"usedPercent\":42}},\"codex\":{\"limitId\":\"contradictory\",\"primary\":{\"usedPercent\":60},\"planType\":\"pro\",\"spendControlReached\":true},\"future-pool\":null}}}");
            Assert(multiple.Buckets.Count == 3, "all buckets included");
            Assert(multiple.MainBucket.Id == "codex", "codex is main despite order and contradictory field");
            Assert(multiple.MainBucket.Windows[0].RemainingPercent == 40, "map preferred over legacy");
            Assert(multiple.Buckets[2].Name == "future-pool" && multiple.Buckets[2].Windows.Count == 0, "unknown bucket preserved without fictional periods");
            Assert(multiple.ResetCredits == 3 && multiple.AccountKey == "test-account", "credit and account metadata");
            Assert(multiple.MainBucket.SpendControlReached && multiple.MainBucket.PlanType == "pro", "bucket metadata");
            Assert(UsageSnapshot.FromJson("{\"rateLimitsByLimitId\":{},\"rateLimits\":{}}").MainBucket != null, "empty map legacy fallback");
            Assert(UsageSnapshot.FromJson("{\"rateLimitsByLimitId\":null,\"rateLimits\":{}}").MainBucket != null, "null map legacy fallback");
            Assert(UsageSnapshot.FromJson("{\"rateLimitsByLimitId\":{\"only\":{}}}").MainBucket.Id == "only", "first bucket fallback");
            Assert(!UsageSnapshot.FromJson("{\"rateLimitResetCredits\":{\"availableCount\":null}}").ResetCredits.HasValue, "null credits unknown");
            Assert(!UsageSnapshot.FromJson("{\"rateLimitResetCredits\":{\"availableCount\":-1}}").ResetCredits.HasValue, "negative credits unknown");

            QuotaWindow countdown = Sample(90, "1800000000", "a").MainBucket.Windows[0];
            DateTime reset = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(1800000000);
            Assert(countdown.Countdown(reset.AddSeconds(-30)) == "不到 1 分钟后重置", "seconds countdown");
            Assert(countdown.Countdown(reset.AddMinutes(-15)) == "15 分钟后重置", "minutes countdown");
            Assert(countdown.Countdown(reset.AddMinutes(-65)) == "1 小时 5 分钟后重置", "hours countdown");
            Assert(countdown.Countdown(reset.AddHours(-25)) == "1 天 1 小时后重置", "days countdown");
            Assert(countdown.Countdown(reset) == "已到重置时间，等待刷新", "elapsed time never invents restored quota");

            AlertTracker alerts = new AlertTracker();
            Assert(alerts.Evaluate(null, 20).Count == 0, "null snapshot no alert");
            Assert(alerts.Evaluate(absent, 20).Count == 0, "absent windows no alert");
            Assert(alerts.Evaluate(unknown, 20).Count == 0, "unknown no alert");
            Assert(alerts.Evaluate(Sample(79, "1800000000", "a"), 20).Count == 0, "above threshold no alert");
            Assert(alerts.Evaluate(Sample(80, "1800000000", "a"), 20).Count == 1, "boundary triggers");
            Assert(alerts.Evaluate(Sample(90, "1800000000", "a"), 20).Count == 0, "poll does not duplicate");
            Assert(alerts.Evaluate(Sample(90, "null", "a"), 20).Count == 0, "missing reset does not rearm");
            Assert(alerts.Evaluate(unknown, 20).Count == 0, "unknown after low no alert");
            Assert(alerts.Evaluate(Sample(90, "1800000000", "a"), 20).Count == 0, "unknown does not imply recovery");
            Assert(alerts.Evaluate(Sample(90, "1800000300", "a"), 20).Count == 1, "new reset boundary rearms");
            Assert(alerts.Evaluate(Sample(70, "1800000300", "a"), 20).Count == 0, "recovery no alert");
            Assert(alerts.Evaluate(Sample(90, "1800000300", "a"), 20).Count == 1, "low after recovery rearms");
            Assert(alerts.Evaluate(Sample(90, "1800000300", "b"), 20).Count == 1, "other account segregated");
            Assert(alerts.Evaluate(Sample(90, "1800000300", "a"), 20).Count == 0, "returning account keeps own state");
            AlertTracker noReset = new AlertTracker();
            Assert(noReset.Evaluate(Sample(90, "null", "a"), 20).Count == 1, "low without reset alerts");
            Assert(noReset.Evaluate(Sample(90, "1800000000", "a"), 20).Count == 0, "new metadata alone no duplicate");

            UsageSnapshot manyLow = UsageSnapshot.FromJson("{\"rateLimitsByLimitId\":{\"codex\":{\"primary\":{\"usedPercent\":99},\"secondary\":{\"usedPercent\":90}},\"future\":{\"primary\":{\"usedPercent\":80},\"secondary\":{\"usedPercent\":null}}}}");
            AlertTracker manyAlerts = new AlertTracker();
            Assert(manyAlerts.Evaluate(manyLow, 20).Count == 3, "each newly low window alerts");
            Assert(manyAlerts.Evaluate(manyLow, 20).Count == 0, "all windows deduplicated");
            Console.WriteLine("PASS: " + count + " model assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
