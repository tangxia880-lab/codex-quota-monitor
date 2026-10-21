using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web.Script.Serialization;

namespace CodexQuotaMonitor
{
    public sealed class UsageSnapshot
    {
        public List<QuotaBucket> Buckets { get; private set; }
        public int? ResetCredits { get; set; }
        public DateTime FetchedAtUtc { get; set; }
        // Account identity is used only in memory to separate notification state.
        public string AccountKey { get; set; }

        public QuotaBucket MainBucket
        {
            get
            {
                foreach (QuotaBucket bucket in Buckets)
                    if (String.Equals(bucket.Id, "codex", StringComparison.OrdinalIgnoreCase))
                        return bucket;
                return Buckets.Count == 0 ? null : Buckets[0];
            }
        }

        public UsageSnapshot()
        {
            Buckets = new List<QuotaBucket>();
            FetchedAtUtc = DateTime.UtcNow;
            AccountKey = String.Empty;
        }

        public static UsageSnapshot FromJson(string rawResultJson)
        {
            UsageSnapshot snapshot = new UsageSnapshot();
            if (String.IsNullOrWhiteSpace(rawResultJson)) return snapshot;
            IDictionary<string, object> root;
            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                serializer.MaxJsonLength = 2 * 1024 * 1024;
                root = serializer.DeserializeObject(rawResultJson) as IDictionary<string, object>;
            }
            catch (ArgumentException) { return snapshot; }
            catch (InvalidOperationException) { return snapshot; }
            if (root == null) return snapshot;

            // Accept either the app-server result itself or its JSON-RPC envelope.
            if (root.ContainsKey("result"))
            {
                root = Read(root, "result") as IDictionary<string, object>;
                if (root == null) return snapshot;
            }

            snapshot.AccountKey = ReadString(root, "accountId", "account_id") ?? String.Empty;
            IDictionary<string, object> resetCredits = Read(root, "rateLimitResetCredits", "rate_limit_reset_credits") as IDictionary<string, object>;
            snapshot.ResetCredits = ReadNonnegativeInt(Read(resetCredits, "availableCount", "available_count"));

            IDictionary<string, object> byId = Read(root, "rateLimitsByLimitId", "rate_limits_by_limit_id") as IDictionary<string, object>;
            if (byId != null && byId.Count > 0)
            {
                foreach (KeyValuePair<string, object> pair in byId)
                    snapshot.Buckets.Add(ReadBucket(pair.Value as IDictionary<string, object>, pair.Key));
            }
            else
            {
                IDictionary<string, object> legacy = Read(root, "rateLimits", "rate_limits") as IDictionary<string, object>;
                if (legacy != null) snapshot.Buckets.Add(ReadBucket(legacy, null));
            }
            return snapshot;
        }

        private static QuotaBucket ReadBucket(IDictionary<string, object> data, string mapId)
        {
            // Map keys are authoritative for the multi-bucket response.
            string id = mapId ?? ReadString(data, "limitId", "limit_id") ?? "codex";
            string displayName = ReadString(data, "limitName", "limit_name");
            QuotaBucket bucket = new QuotaBucket();
            bucket.Id = id;
            bucket.Name = String.IsNullOrWhiteSpace(displayName) ? (String.Equals(id, "codex", StringComparison.OrdinalIgnoreCase) ? "Codex" : id) : displayName;
            bucket.PlanType = ReadString(data, "planType", "plan_type");
            bucket.SpendControlReached = Object.Equals(Read(data, "spendControlReached", "spend_control_reached"), true);
            IDictionary<string, object> primary = Read(data, "primary") as IDictionary<string, object>;
            IDictionary<string, object> secondary = Read(data, "secondary") as IDictionary<string, object>;
            if (primary != null) bucket.Windows.Add(ReadWindow(primary, "primary"));
            if (secondary != null) bucket.Windows.Add(ReadWindow(secondary, "secondary"));
            return bucket;
        }

        private static QuotaWindow ReadWindow(IDictionary<string, object> data, string key)
        {
            QuotaWindow window = new QuotaWindow();
            window.Key = key;
            double? used = ReadNumber(Read(data, "usedPercent", "used_percent"));
            window.RemainingPercent = used.HasValue ? (double?)Math.Max(0, Math.Min(100, 100 - used.Value)) : null;
            int? duration = ReadNonnegativeInt(Read(data, "windowDurationMins", "window_duration_mins"));
            window.DurationMinutes = duration.HasValue && duration.Value > 0 ? duration : null;
            double? epoch = ReadNumber(Read(data, "resetsAt", "resets_at"));
            if (epoch.HasValue && epoch.Value == Math.Truncate(epoch.Value) && epoch.Value >= -62135596800d && epoch.Value <= 253402300799d)
                window.ResetEpochSeconds = (long)epoch.Value;
            return window;
        }

        private static object Read(IDictionary<string, object> data, params string[] keys)
        {
            if (data == null) return null;
            foreach (string key in keys)
            {
                object value;
                if (data.TryGetValue(key, out value)) return value;
            }
            return null;
        }

        private static string ReadString(IDictionary<string, object> data, params string[] keys)
        {
            string value = Read(data, keys) as string;
            return String.IsNullOrWhiteSpace(value) ? null : value;
        }

        private static double? ReadNumber(object value)
        {
            if (value == null || value is bool || value is IDictionary<string, object> || value is Array) return null;
            double number;
            if (!Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return null;
            return Double.IsNaN(number) || Double.IsInfinity(number) ? null : (double?)number;
        }

        private static int? ReadNonnegativeInt(object value)
        {
            double? number = ReadNumber(value);
            return number.HasValue && number.Value >= 0 && number.Value <= Int32.MaxValue && number.Value == Math.Truncate(number.Value) ? (int?)number.Value : null;
        }
    }

    public sealed class QuotaBucket
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string PlanType { get; set; }
        public List<QuotaWindow> Windows { get; private set; }
        public bool SpendControlReached { get; set; }

        public QuotaBucket() { Windows = new List<QuotaWindow>(); }
    }

    public sealed class QuotaWindow
    {
        private static readonly DateTime EpochUtc = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public string Key { get; set; }
        public double? RemainingPercent { get; set; }
        public long? ResetEpochSeconds { get; set; }
        public int? DurationMinutes { get; set; }

        public string Label
        {
            get
            {
                if (!DurationMinutes.HasValue || DurationMinutes.Value <= 0) return "未知周期";
                int minutes = DurationMinutes.Value;
                List<string> parts = new List<string>();
                if (minutes >= 1440) { parts.Add((minutes / 1440).ToString(CultureInfo.InvariantCulture) + " 天"); minutes %= 1440; }
                if (minutes >= 60) { parts.Add((minutes / 60).ToString(CultureInfo.InvariantCulture) + " 小时"); minutes %= 60; }
                if (minutes > 0) parts.Add(minutes.ToString(CultureInfo.InvariantCulture) + " 分钟");
                return String.Join(" ", parts.ToArray());
            }
        }

        public string ResetLabel
        {
            get
            {
                DateTime? reset = ResetTimeUtc;
                return reset.HasValue ? reset.Value.ToLocalTime().ToString("MM/dd HH:mm", CultureInfo.InvariantCulture) : "未提供重置时间";
            }
        }

        private DateTime? ResetTimeUtc
        {
            get
            {
                if (!ResetEpochSeconds.HasValue) return null;
                try { return EpochUtc.AddSeconds(ResetEpochSeconds.Value); }
                catch (ArgumentOutOfRangeException) { return null; }
            }
        }

        public string Countdown(DateTime utcNow)
        {
            DateTime? reset = ResetTimeUtc;
            if (!reset.HasValue) return "未提供重置时间";
            DateTime now = utcNow.Kind == DateTimeKind.Local ? utcNow.ToUniversalTime() : DateTime.SpecifyKind(utcNow, DateTimeKind.Utc);
            TimeSpan left = reset.Value - now;
            if (left.TotalSeconds <= 0) return "已到重置时间，等待刷新";
            if (left.TotalMinutes < 1) return "不到 1 分钟后重置";
            if (left.TotalDays >= 1) return String.Format(CultureInfo.InvariantCulture, "{0} 天 {1} 小时后重置", (int)left.TotalDays, left.Hours);
            if (left.TotalHours >= 1) return String.Format(CultureInfo.InvariantCulture, "{0} 小时 {1} 分钟后重置", (int)left.TotalHours, left.Minutes);
            return ((int)Math.Ceiling(left.TotalMinutes)).ToString(CultureInfo.InvariantCulture) + " 分钟后重置";
        }
    }

    public sealed class AlertTracker
    {
        private sealed class WindowState
        {
            public bool Alerted;
            public long? ResetEpochSeconds;
        }

        private readonly Dictionary<string, WindowState> states = new Dictionary<string, WindowState>(StringComparer.Ordinal);

        public List<string> Evaluate(UsageSnapshot snapshot, int threshold)
        {
            List<string> messages = new List<string>();
            if (snapshot == null) return messages;
            threshold = Math.Max(0, Math.Min(100, threshold));
            foreach (QuotaBucket bucket in snapshot.Buckets)
            {
                foreach (QuotaWindow window in bucket.Windows)
                {
                    if (!window.RemainingPercent.HasValue || Double.IsNaN(window.RemainingPercent.Value) || Double.IsInfinity(window.RemainingPercent.Value)) continue;
                    string account = snapshot.AccountKey ?? String.Empty;
                    string bucketId = bucket.Id ?? String.Empty;
                    string key = account.Length.ToString(CultureInfo.InvariantCulture) + ":" + account + bucketId.Length.ToString(CultureInfo.InvariantCulture) + ":" + bucketId + (window.Key ?? String.Empty);
                    WindowState state;
                    if (!states.TryGetValue(key, out state))
                    {
                        state = new WindowState();
                        states.Add(key, state);
                    }
                    // Missing reset metadata is not evidence of a new quota period.
                    if (state.ResetEpochSeconds.HasValue && window.ResetEpochSeconds.HasValue && state.ResetEpochSeconds.Value != window.ResetEpochSeconds.Value)
                        state.Alerted = false;
                    if (window.ResetEpochSeconds.HasValue) state.ResetEpochSeconds = window.ResetEpochSeconds;
                    if (window.RemainingPercent.Value > threshold)
                    {
                        state.Alerted = false;
                        continue;
                    }
                    if (state.Alerted) continue;
                    state.Alerted = true;
                    messages.Add(String.Format(CultureInfo.InvariantCulture, "{0} · {1}额度剩余 {2:0.#}%，{3}。", bucket.Name ?? bucket.Id ?? "Codex", window.Label, window.RemainingPercent.Value, window.Countdown(snapshot.FetchedAtUtc)));
                }
            }
            return messages;
        }
    }
}
