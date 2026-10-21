using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web.Script.Serialization;

namespace CodexQuotaMonitor
{
    public sealed class TokenCounts
    {
        public long? Total { get; set; }
        public long? Input { get; set; }
        public long? CachedInput { get; set; }
        public long? CacheWriteInput { get; set; }
        public long? Output { get; set; }
        public long? Reasoning { get; set; }

        public TokenCounts Clone()
        {
            return (TokenCounts)MemberwiseClone();
        }
    }

    public sealed class TaskUsageRecord
    {
        public string Key { get; set; }
        public string ThreadId { get; set; }
        public string TurnId { get; set; }
        public string RootTurnId { get; set; }
        public string ParentThreadId { get; set; }
        public string Title { get; set; }
        public string Model { get; set; }
        public string Status { get; set; }
        public string ScopeNote { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public DateTime CompletedAtUtc { get; set; }
        public TokenCounts Usage { get; set; }
        public TokenCounts Cumulative { get; set; }
        public bool IsPartial { get; set; }

        public TaskUsageRecord()
        {
            Usage = new TokenCounts();
            Cumulative = new TokenCounts();
        }

        public TaskUsageRecord Clone()
        {
            var copy = (TaskUsageRecord)MemberwiseClone();
            copy.Usage = Usage == null ? new TokenCounts() : Usage.Clone();
            copy.Cumulative = Cumulative == null ? new TokenCounts() : Cumulative.Clone();
            return copy;
        }
    }

    // Reads only session, turn and numeric usage metadata. No messages, tool
    // arguments, responses or credentials are retained in parser state.
    public sealed class TaskUsageParser
    {
        private sealed class TurnState
        {
            public TaskUsageRecord Record;
            public bool OwnStarted;
            public bool OwnUsage;
            public bool Completed;
            public long LastUsageOrdinal = -1;
            public long CompletionOrdinal = -1;
        }

        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private readonly Dictionary<string, TurnState> turns = new Dictionary<string, TurnState>(StringComparer.Ordinal);
        private bool hasMeta;
        private bool uncertainInheritance;
        private long? ownHistoryStartsAt;
        private long sequence;
        public string ThreadId { get; private set; }
        public string ParentThreadId { get; private set; }
        public string Model { get; private set; }

        public TaskUsageParser(string fallbackThreadId)
        {
            ThreadId = fallbackThreadId ?? String.Empty;
            ParentThreadId = String.Empty;
            Model = String.Empty;
        }

        public int ActiveCount
        {
            get { return turns.Values.Count(s => !s.Completed && IsOwned(s)); }
        }

        // Detached snapshots allow the UI to annotate titles and aggregate child
        // usage without changing the parser. Late usage updates repair old rows.
        public List<TaskUsageRecord> Completed
        {
            get
            {
                var result = new List<TaskUsageRecord>();
                foreach (TurnState state in turns.Values)
                {
                    if (!state.Completed || !IsOwned(state)) continue;
                    TaskUsageRecord copy = state.Record.Clone();
                    copy.IsPartial = !copy.Usage.Total.HasValue;
                    copy.ScopeNote = copy.IsPartial ? "本机记录未提供完整用量；无法计算的字段显示未知。" : "当前会话用量；子任务另列，缓存和推理明细不重复计入总数。";
                    result.Add(copy);
                }
                return result.OrderByDescending(r => r.CompletedAtUtc).ThenBy(r => r.Key, StringComparer.Ordinal).ToList();
            }
        }

        private bool IsOwned(TurnState state)
        {
            return state.OwnUsage || (!uncertainInheritance && state.OwnStarted);
        }

        public void AddLine(string line)
        {
            if (String.IsNullOrWhiteSpace(line) || line.Length > serializer.MaxJsonLength) return;
            // Skip the ordinary (possibly large and private) content lines before
            // JSON parsing. The root type is still verified below.
            if (line.IndexOf("\"session_meta\"", StringComparison.Ordinal) < 0 &&
                line.IndexOf("\"turn_context\"", StringComparison.Ordinal) < 0 &&
                line.IndexOf("\"token_usage_record\"", StringComparison.Ordinal) < 0 &&
                line.IndexOf("\"event_msg\"", StringComparison.Ordinal) < 0) return;
            IDictionary<string, object> root;
            try { root = serializer.DeserializeObject(line) as IDictionary<string, object>; }
            catch (ArgumentException) { return; }
            catch (InvalidOperationException) { return; }
            if (root == null) return;
            var payload = Read(root, "payload") as IDictionary<string, object>;
            if (payload == null) return;
            string type = Text(root, "type");
            long? ordinal = Count(Read(root, "ordinal"));
            long order = ordinal ?? ++sequence;
            sequence = Math.Max(sequence, order);

            if (type == "session_meta")
            {
                string id = Text(payload, "id");
                if (!hasMeta)
                {
                    hasMeta = true;
                    if (!String.IsNullOrEmpty(id)) ThreadId = id;
                    ParentThreadId = Text(payload, "parent_thread_id") ?? String.Empty;
                    ownHistoryStartsAt = Count(Read(payload, "subagent_history_start_ordinal"));
                    // A fork may copy prior turns without a subagent cutoff. Own
                    // token records are then required to establish turn ownership.
                    uncertainInheritance = !ownHistoryStartsAt.HasValue && !String.IsNullOrEmpty(Text(payload, "forked_from_id"));
                }
                else if (!String.IsNullOrEmpty(id) && id != ThreadId && !ownHistoryStartsAt.HasValue)
                    uncertainInheritance = true;
                return;
            }

            if (ownHistoryStartsAt.HasValue && (!ordinal.HasValue || ordinal.Value < ownHistoryStartsAt.Value)) return;
            string explicitThread = Text(payload, "thread_id");
            if (!String.IsNullOrEmpty(explicitThread) && explicitThread != ThreadId) return;
            string turnId = Text(payload, "turn_id");
            if (String.IsNullOrEmpty(turnId) || String.IsNullOrEmpty(ThreadId)) return;
            DateTime timestamp = Time(Read(root, "timestamp"));

            if (type == "token_usage_record")
            {
                // An explicit thread ID is essential: copied parent usage must
                // never become a child's bill, even when turn IDs are inherited.
                if (explicitThread != ThreadId) return;
                TurnState state = Turn(turnId);
                if (order < state.LastUsageOrdinal) return;
                state.LastUsageOrdinal = order;
                state.OwnUsage = true;
                state.Record.RootTurnId = Text(payload, "root_turn_id") ?? state.Record.RootTurnId;
                state.Record.Usage = Counts(Read(payload, "turn_token_usage") as IDictionary<string, object>);
                state.Record.Cumulative = Counts(Read(payload, "thread_token_usage") as IDictionary<string, object>);
                // payload.usage is one response, not the entire turn. It is
                // deliberately never used as a substitute for turn_token_usage.
                return;
            }
            if (type == "turn_context")
            {
                TurnState state = Turn(turnId);
                string model = Text(payload, "model");
                if (model != null) { Model = model; state.Record.Model = model; }
                state.Record.RootTurnId = Text(payload, "root_turn_id") ?? state.Record.RootTurnId;
                return;
            }
            if (type != "event_msg") return;
            string eventType = Text(payload, "type");
            if (eventType == "task_started")
            {
                TurnState state = Turn(turnId);
                state.OwnStarted = true;
                DateTime started = Time(Read(payload, "started_at"));
                if (started == DateTime.MinValue) started = timestamp;
                if (state.Record.StartedAtUtc == DateTime.MinValue || (started != DateTime.MinValue && started < state.Record.StartedAtUtc))
                    state.Record.StartedAtUtc = started;
                return;
            }
            if (eventType == "task_complete")
            {
                TurnState state = Turn(turnId);
                if (order < state.CompletionOrdinal) return;
                state.CompletionOrdinal = order;
                state.Completed = true;
                state.Record.Status = "completed";
                DateTime started = Time(Read(payload, "started_at"));
                if (started != DateTime.MinValue) state.Record.StartedAtUtc = started;
                DateTime ended = Time(Read(payload, "completed_at"));
                state.Record.CompletedAtUtc = ended == DateTime.MinValue ? timestamp : ended;
            }
        }

        private TurnState Turn(string turnId)
        {
            TurnState state;
            if (turns.TryGetValue(turnId, out state)) return state;
            state = new TurnState { Record = new TaskUsageRecord
            {
                Key = ThreadId + ":" + turnId,
                ThreadId = ThreadId,
                TurnId = turnId,
                RootTurnId = String.IsNullOrEmpty(ParentThreadId) ? turnId : String.Empty,
                ParentThreadId = ParentThreadId,
                Title = "任务 " + (ThreadId.Length > 8 ? ThreadId.Substring(0, 8) : ThreadId),
                Model = Model,
                Status = "inProgress",
                IsPartial = true
            }};
            turns.Add(turnId, state);
            return state;
        }

        private static object Read(IDictionary<string, object> data, string key)
        {
            object value;
            return data != null && data.TryGetValue(key, out value) ? value : null;
        }

        private static string Text(IDictionary<string, object> data, string key)
        {
            string value = Read(data, key) as string;
            if (String.IsNullOrWhiteSpace(value)) return null;
            return value.Length <= 512 ? value : value.Substring(0, 512);
        }

        private static long? Count(object value)
        {
            if (!(value is byte || value is short || value is int || value is long || value is decimal || value is double || value is float)) return null;
            decimal number;
            if (!Decimal.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out number)) return null;
            return number >= 0 && number <= Int64.MaxValue && number == Decimal.Truncate(number) ? (long?)number : null;
        }

        private static TokenCounts Counts(IDictionary<string, object> data)
        {
            return new TokenCounts
            {
                Total = Count(Read(data, "total_tokens")),
                Input = Count(Read(data, "input_tokens")),
                CachedInput = Count(Read(data, "cached_input_tokens")),
                CacheWriteInput = Count(Read(data, "cache_write_input_tokens")),
                Output = Count(Read(data, "output_tokens")),
                Reasoning = Count(Read(data, "reasoning_output_tokens"))
            };
        }

        private static DateTime Time(object value)
        {
            string text = value as string;
            DateTime timestamp;
            if (text != null && DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out timestamp)) return timestamp;
            long? epoch = Count(value);
            if (!epoch.HasValue || epoch.Value > 253402300799L) return DateTime.MinValue;
            try { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(epoch.Value); }
            catch (ArgumentOutOfRangeException) { return DateTime.MinValue; }
        }
    }
}
