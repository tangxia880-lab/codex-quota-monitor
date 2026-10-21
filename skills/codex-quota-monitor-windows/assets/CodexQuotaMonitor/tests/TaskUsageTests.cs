using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using CodexQuotaMonitor;

internal static class TaskUsageTests
{
    private static int assertions;
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

    private static void Assert(bool condition, string name)
    {
        assertions++;
        if (!condition) throw new Exception("FAIL: " + name);
    }

    private static string Line(string type, int ordinal, object payload)
    {
        return Json.Serialize(new { type = type, ordinal = ordinal, timestamp = "2026-09-11T01:02:03.000Z", payload = payload });
    }

    private static string Meta(string id, string parent, int? cutoff, int ordinal)
    {
        return Line("session_meta", ordinal, new { id = id, parent_thread_id = parent, subagent_history_start_ordinal = cutoff });
    }

    private static string Start(string turn, int ordinal)
    {
        return Line("event_msg", ordinal, new { type = "task_started", turn_id = turn, started_at = 1789088400L });
    }

    private static string Done(string turn, int ordinal)
    {
        return Line("event_msg", ordinal, new { type = "task_complete", turn_id = turn, started_at = 1789088400L, completed_at = 1789088500L, duration_ms = 100000 });
    }

    private static object Counts(long total)
    {
        return new { total_tokens = total, input_tokens = total - 100, cached_input_tokens = 70, cache_write_input_tokens = 0, output_tokens = 100, reasoning_output_tokens = 40 };
    }

    private static string Usage(string thread, string turn, long turnTotal, long threadTotal, int ordinal, string rootTurn)
    {
        return Line("token_usage_record", ordinal, new
        {
            thread_id = thread, turn_id = turn, root_turn_id = rootTurn,
            session_id = "synthetic-session", response_id = "synthetic-response",
            usage = Counts(120), turn_token_usage = Counts(turnTotal), thread_token_usage = Counts(threadTotal)
        });
    }

    public static int Main()
    {
        try
        {
            var parser = new TaskUsageParser("fallback");
            parser.AddLine(Meta("root", null, null, 0));
            parser.AddLine(Start("one", 1));
            parser.AddLine(Line("turn_context", 2, new { turn_id = "one", model = "test-model", root_turn_id = "one" }));
            Assert(parser.ThreadId == "root", "first meta overrides filename fallback");
            Assert(parser.ActiveCount == 1 && parser.Completed.Count == 0, "start is active and not completed");
            parser.AddLine(Usage("root", "one", 200, 200, 3, "one"));
            parser.AddLine(Usage("root", "one", 650, 650, 4, "one"));
            parser.AddLine(Done("one", 5));
            TaskUsageRecord first = parser.Completed[0];
            Assert(parser.Completed.Count == 1 && parser.ActiveCount == 0, "completed count and inactive turn");
            Assert(first.Usage.Total == 650, "cumulative turn snapshots replace, never sum");
            Assert(first.Cumulative.Total == 650, "thread cumulative retained separately");
            Assert(first.Usage.Input == 550 && first.Usage.Output == 100, "input and output retained");
            Assert(first.Usage.CachedInput == 70 && first.Usage.Reasoning == 40, "subset breakdown retained without increasing total");
            Assert(first.Usage.CacheWriteInput == 0 && !first.IsPartial, "explicit zero cache write is known");
            Assert(first.Model == "test-model" && first.RootTurnId == "one", "turn model and root mapping");
            Assert(first.ThreadId == "root" && first.TurnId == "one" && first.Key == "root:one", "identity retained");
            Assert(first.Status == "completed" && first.CompletedAtUtc.Kind == DateTimeKind.Utc, "status and UTC timestamp");
            Assert((first.CompletedAtUtc - first.StartedAtUtc).TotalSeconds == 100, "numeric completion timestamps");

            parser.AddLine(Done("one", 5));
            parser.AddLine(Usage("root", "one", 650, 650, 4, "one"));
            Assert(parser.Completed.Count == 1 && parser.Completed[0].Usage.Total == 650, "duplicate events do not duplicate rows or tokens");
            parser.AddLine(Usage("root", "one", 200, 200, 3, "one"));
            Assert(parser.Completed[0].Usage.Total == 650, "old ordinal cannot roll back numeric snapshot");
            parser.AddLine(Usage("root", "one", 700, 700, 6, "one"));
            Assert(parser.Completed[0].Usage.Total == 700, "late final usage repairs completed record");
            first.Usage.Total = 999999;
            first.Title = "mutated";
            Assert(parser.Completed[0].Usage.Total == 700 && parser.Completed[0].Title != "mutated", "returned records are detached deep copies");

            parser.AddLine(Start("two", 7));
            parser.AddLine(Usage("root", "two", 300, 1000, 8, "two"));
            parser.AddLine(Done("two", 9));
            Assert(parser.Completed.Count == 2, "same task has separate completed turns");
            TaskUsageRecord second = parser.Completed.Find(r => r.TurnId == "two");
            Assert(second.Usage.Total == 300 && second.Cumulative.Total == 1000, "new turn does not charge prior turn cumulative");

            var late = new TaskUsageParser("root");
            late.AddLine(Meta("root", null, null, 0));
            late.AddLine(Done("late", 3));
            Assert(late.Completed.Count == 0, "unowned completion waits for start or usage proof");
            late.AddLine(Usage("root", "late", 500, 500, 2, "late"));
            Assert(late.Completed.Count == 1 && late.Completed[0].Usage.Total == 500, "out of order own usage proves completion ownership");
            Assert(late.Completed[0].StartedAtUtc != DateTime.MinValue, "completion carries start time without start event");

            var legacy = new TaskUsageParser("old");
            legacy.AddLine(Meta("old", null, null, 0));
            legacy.AddLine(Start("old-turn", 1));
            legacy.AddLine(Line("event_msg", 2, new { type = "token_count", info = new { total_token_usage = Counts(99000), last_token_usage = Counts(800) } }));
            legacy.AddLine(Done("old-turn", 3));
            Assert(legacy.Completed.Count == 1 && legacy.Completed[0].IsPartial, "old format completion retained as incomplete");
            Assert(!legacy.Completed[0].Usage.Total.HasValue, "legacy cumulative and last are never guessed to be a turn");
            Assert(!legacy.Completed[0].Cumulative.Total.HasValue, "unknown cumulative stays unknown");

            var child = new TaskUsageParser("child-file");
            child.AddLine(Meta("child", "root", 10, 0));
            child.AddLine(Meta("root", null, null, 1));
            child.AddLine(Start("inherited", 2));
            child.AddLine(Usage("root", "inherited", 4000, 8000, 3, "inherited"));
            child.AddLine(Done("inherited", 4));
            child.AddLine(Line("turn_context", 5, new { turn_id = "inherited", model = "parent-model" }));
            Assert(child.ThreadId == "child" && child.ParentThreadId == "root", "copied metadata never changes identity");
            Assert(child.Completed.Count == 0 && child.ActiveCount == 0, "copied pre-cutoff events ignored");
            Assert(child.Model == "", "copied pre-cutoff context ignored");
            child.AddLine(Start("child-turn", 10));
            child.AddLine(Line("turn_context", 11, new { turn_id = "child-turn", model = "child-model", root_turn_id = "parent-turn" }));
            child.AddLine(Usage("root", "child-turn", 5000, 10000, 12, "parent-turn"));
            child.AddLine(Usage("child", "child-turn", 800, 800, 13, "parent-turn"));
            child.AddLine(Done("child-turn", 14));
            Assert(child.Completed.Count == 1 && child.Completed[0].Usage.Total == 800, "only child own token totals used");
            Assert(child.Completed[0].RootTurnId == "parent-turn" && child.Completed[0].ParentThreadId == "root", "child attribution metadata retained");
            Assert(child.Completed[0].Model == "child-model", "own context model retained");

            var missingOrdinal = new TaskUsageParser("child");
            missingOrdinal.AddLine(Meta("child", "root", 10, 0));
            missingOrdinal.AddLine("{\"type\":\"event_msg\",\"payload\":{\"type\":\"task_started\",\"turn_id\":\"ambiguous\"}}");
            missingOrdinal.AddLine(Done("ambiguous", 11));
            Assert(missingOrdinal.Completed.Count == 0, "missing ordinal cannot bypass inherited cutoff");

            var fork = new TaskUsageParser("fork");
            fork.AddLine(Meta("fork", null, null, 0));
            fork.AddLine(Meta("root", null, null, 1));
            fork.AddLine(Start("copied", 2));
            fork.AddLine(Usage("root", "copied", 900, 1900, 3, "copied"));
            fork.AddLine(Done("copied", 4));
            Assert(fork.Completed.Count == 0 && fork.ActiveCount == 0, "foreign inherited history without cutoff requires own usage proof");
            fork.AddLine(Start("new", 5));
            fork.AddLine(Usage("fork", "new", 400, 400, 6, "new"));
            fork.AddLine(Done("new", 7));
            Assert(fork.Completed.Count == 1 && fork.Completed[0].Usage.Total == 400, "fork own turn accounted without inherited totals");

            var empty = new TaskUsageParser("zero");
            empty.AddLine(Meta("zero", null, null, 0));
            empty.AddLine(Start("empty", 1));
            empty.AddLine(Line("token_usage_record", 2, new { thread_id = "zero", turn_id = "empty", usage = Counts(500) }));
            empty.AddLine(Done("empty", 3));
            Assert(empty.Completed[0].IsPartial && !empty.Completed[0].Usage.Total.HasValue, "response usage cannot substitute missing turn totals");
            empty.AddLine(Line("token_usage_record", 4, new { thread_id = "zero", turn_id = "empty", turn_token_usage = new { total_tokens = 0 } }));
            Assert(empty.Completed[0].Usage.Total == 0 && !empty.Completed[0].IsPartial, "explicit zero total is available usage");

            var invalid = new TaskUsageParser("bad");
            invalid.AddLine(Meta("bad", null, null, 0));
            invalid.AddLine(Start("bad", 1));
            invalid.AddLine(Line("token_usage_record", 2, new { thread_id = "bad", turn_id = "bad", turn_token_usage = new { total_tokens = -5, input_tokens = "9", output_tokens = true, cached_input_tokens = 1.5, reasoning_output_tokens = new int[] { 1 } } }));
            invalid.AddLine(Done("bad", 3));
            TokenCounts invalidCounts = invalid.Completed[0].Usage;
            Assert(!invalidCounts.Total.HasValue && !invalidCounts.Input.HasValue && !invalidCounts.Output.HasValue, "negative string and boolean counters rejected");
            Assert(!invalidCounts.CachedInput.HasValue && !invalidCounts.Reasoning.HasValue, "fractional and collection counters rejected");
            foreach (string broken in new string[] { null, "", "{", "[]", "{\"type\":\"event_msg\",\"payload\":null}", "{\"type\":\"token_usage_record\",", "{\"type\":\"response_item\",\"payload\":{\"text\":\"private synthetic content\"}}" })
                invalid.AddLine(broken);
            Assert(invalid.Completed.Count == 1, "malformed and content records cannot alter history");

            var large = new TaskUsageParser("big");
            large.AddLine(Meta("big", null, null, 0));
            large.AddLine(Usage("big", "big-turn", 8000000000L, 18000000000L, 1, "big-turn"));
            large.AddLine(Done("big-turn", 2));
            Assert(large.Completed[0].Usage.Total == 8000000000L && large.Completed[0].Cumulative.Total == 18000000000L, "64-bit counters do not overflow at Int32 limits");

            var timestamps = new TaskUsageParser("time");
            timestamps.AddLine(Meta("time", null, null, 0));
            timestamps.AddLine(Start("time-turn", 1));
            timestamps.AddLine(Line("event_msg", 2, new { type = "task_complete", turn_id = "time-turn", started_at = "2026-09-11T08:00:00+08:00", completed_at = "2026-09-11T08:01:00+08:00" }));
            Assert(timestamps.Completed[0].StartedAtUtc.Hour == 0 && timestamps.Completed[0].CompletedAtUtc.Minute == 1, "ISO offsets normalized to UTC");

            var isolated = new TaskUsageParser("other");
            isolated.AddLine(Meta("other", null, null, 0));
            isolated.AddLine(Start("one", 1));
            isolated.AddLine(Usage("other", "one", 800, 800, 2, "one"));
            isolated.AddLine(Done("one", 3));
            Assert(isolated.Completed[0].Key != parser.Completed.Find(r => r.TurnId == "one").Key, "same turn ID in different threads does not collide");
            Console.WriteLine("PASS: " + assertions + " task usage assertions");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}
