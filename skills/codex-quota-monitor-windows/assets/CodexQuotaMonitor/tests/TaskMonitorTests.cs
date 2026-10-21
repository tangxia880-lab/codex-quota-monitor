using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using CodexQuotaMonitor;

internal static class TaskMonitorTests
{
    private static readonly JavaScriptSerializer Json = new JavaScriptSerializer();
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private static int assertions;
    private static DateTime clock = DateTime.UtcNow;
    private static int ordinal;
    private const string ThreadId = "11111111-1111-1111-1111-111111111111";
    private static void Assert(bool pass, string name) { assertions++; if (!pass) throw new Exception("FAIL: " + name); }
    private static long Epoch(DateTime time) { return (long)(time - new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds; }
    private static string Line(string type, object payload)
    {
        return Json.Serialize(new { timestamp = clock.ToString("o"), ordinal = ordinal++, type = type, payload = payload });
    }
    private static string Meta() { return Line("session_meta", new { id=ThreadId, session_id=ThreadId }); }
    private static string Start(string turn) { return Line("event_msg", new { type="task_started", turn_id=turn, started_at=Epoch(clock) }); }
    private static string Done(string turn) { return Line("event_msg", new { type="task_complete", turn_id=turn, started_at=Epoch(clock.AddSeconds(-1)), completed_at=Epoch(clock) }); }
    private static object Counts(long count) { return new { total_tokens=count, input_tokens=count-10, output_tokens=10, cached_input_tokens=20, reasoning_output_tokens=5 }; }
    private static string Usage(string turn, long count, long cumulative)
    {
        return Line("token_usage_record", new { thread_id=ThreadId, turn_id=turn, root_turn_id=turn, session_id=ThreadId, response_id="synthetic-"+ordinal, usage=Counts(50), turn_token_usage=Counts(count), thread_token_usage=Counts(cumulative) });
    }
    private static void Append(string file, params string[] lines) { File.AppendAllText(file,String.Join("\n",lines)+"\n",Utf8); }
    private static TaskUsageUpdate Poll(TaskUsageMonitor monitor) { return monitor.PollAsync(CancellationToken.None).GetAwaiter().GetResult(); }

    public static int Main(string[] args)
    {
        string testRoot = args.Length>0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(),"CodexQuotaMonitor-task-fixtures-"+Guid.NewGuid().ToString("N"));
        string source = Path.Combine(testRoot,"source");
        string storage = Path.Combine(testRoot,"storage");
        string day=Path.Combine(source,"sessions",DateTime.UtcNow.ToString("yyyy"),DateTime.UtcNow.ToString("MM"),DateTime.UtcNow.ToString("dd"));
        Directory.CreateDirectory(day);
        string file=Path.Combine(day,"rollout-synthetic-"+ThreadId+".jsonl");
        try
        {
            DateTime startClock=clock;
            clock=clock.AddMinutes(-1);
            Append(file,Meta(),Start("old"),Usage("old",100,100),Done("old"),Line("response_item",new { type="message",content="DO_NOT_PERSIST_THIS_MESSAGE",arguments="DO_NOT_PERSIST_TOOL_ARGUMENTS" }));
            clock=startClock;
            File.WriteAllText(Path.Combine(source,"session_index.jsonl"),Json.Serialize(new { id=ThreadId,thread_name="合成测试任务" })+"\n",Utf8);
            using(var monitor=new TaskUsageMonitor(storage,source,delegate{return clock;}))
            {
                var first=Poll(monitor);
                Assert(first.Records.Count==1 && first.Records[0].Usage.Total==100,"cold start reads historic numeric row");
                Assert(first.NewCompletions.Count==0,"cold start never alerts history");
                Assert(first.Records[0].Title=="合成测试任务","session index supplies title");
                clock=clock.AddSeconds(2);
                Append(file,Start("new"),Usage("new",200,300),Done("new"));
                var pending=Poll(monitor);
                Assert(pending.Records.Count==2 && pending.NewCompletions.Count==0,"new completion row visible during grace");
                clock=clock.AddSeconds(6);
                Assert(Poll(monitor).NewCompletions.Single().TurnId=="new","known completion surfaces after grace");
                Assert(Poll(monitor).NewCompletions.Count==0,"same completion surfaces once");
                Append(file,Usage("new",250,350));
                var enriched=Poll(monitor);
                Assert(enriched.Records.Single(r=>r.TurnId=="new").Usage.Total==250,"late final usage enriches row");
                Assert(enriched.NewCompletions.Count==0,"late enrichment never resurfaces completion");

                clock=clock.AddSeconds(2);
                Append(file,Start("partial"),Usage("partial",300,650));
                string done=Done("partial");
                byte[] bytes=Utf8.GetBytes(done+"\n");
                int split=bytes.Length/2;
                using(var stream=new FileStream(file,FileMode.Append,FileAccess.Write,FileShare.ReadWrite)) stream.Write(bytes,0,split);
                Assert(Poll(monitor).Records.All(r=>r.TurnId!="partial"),"partial last line is not parsed early");
                using(var stream=new FileStream(file,FileMode.Append,FileAccess.Write,FileShare.ReadWrite)) stream.Write(bytes,split,bytes.Length-split);
                Assert(Poll(monitor).Records.Single(r=>r.TurnId=="partial").Usage.Total==300,"completed partial line parses once");
                clock=clock.AddSeconds(6);
                Assert(Poll(monitor).NewCompletions.Single().TurnId=="partial","partial append completion surfaces once");

                clock=clock.AddSeconds(2);
                Append(file,Start("unknown"),Done("unknown"));
                var unknown=Poll(monitor);
                Assert(!unknown.Records.Single(r=>r.TurnId=="unknown").Usage.Total.HasValue,"unknown usage remains null");
                clock=clock.AddSeconds(6);
                Assert(Poll(monitor).NewCompletions.Count==0,"unknown completion waits at least ten seconds");
                clock=clock.AddSeconds(5);
                Assert(Poll(monitor).NewCompletions.Single().TurnId=="unknown","unknown completion eventually surfaces");
                Append(file,Usage("unknown",150,800));
                Assert(Poll(monitor).NewCompletions.Count==0,"unknown late usage no repeat");

                clock=clock.AddSeconds(2); ordinal=0;
                File.WriteAllText(file,String.Join("\n",new[]{Meta(),Start("after-truncate"),Usage("after-truncate",90,90),Done("after-truncate")})+"\n",Utf8);
                var truncated=Poll(monitor);
                Assert(truncated.Records.Any(r=>r.TurnId=="after-truncate" && r.Usage.Total==90),"truncation resets byte offset and parser");
                Assert(truncated.Records.Any(r=>r.TurnId=="old"),"truncation preserves saved numeric history");
                clock=clock.AddSeconds(6);
                Assert(Poll(monitor).NewCompletions.Single().TurnId=="after-truncate","post-truncation completion surfaces once");
                string saved=File.ReadAllText(Path.Combine(storage,"task-usage.json"),Utf8);
                Assert(!saved.Contains("DO_NOT_PERSIST") && !saved.Contains("content") && !saved.Contains("arguments") && !saved.Contains("payload"),"persistence excludes transcript and tool fields");
            }
            clock=clock.AddSeconds(2);
            using(var restarted=new TaskUsageMonitor(storage,source,delegate{return clock;}))
            {
                var restored=Poll(restarted);
                Assert(restored.Records.Count>=5,"restart restores numeric history");
                Assert(restored.NewCompletions.Count==0,"restart never replays saved completions");
                Append(file,Usage("after-truncate",95,95));
                clock=clock.AddSeconds(12);
                var updated=Poll(restarted);
                Assert(updated.NewCompletions.Count==0 && updated.Records.Single(r=>r.TurnId=="after-truncate").Usage.Total==95,"restart duplicate remains deduped while usage updates");
                var canceled=new CancellationTokenSource();canceled.Cancel();bool canceledObserved=false;
                try{restarted.PollAsync(canceled.Token).GetAwaiter().GetResult();}catch(OperationCanceledException){canceledObserved=true;}
                Assert(canceledObserved,"canceled poll stops without I/O");
            }
            string bootstrapSource=Path.Combine(testRoot,"bootstrap-source");
            string bootstrapDay=Path.Combine(bootstrapSource,"sessions",DateTime.UtcNow.ToString("yyyy"),DateTime.UtcNow.ToString("MM"),DateTime.UtcNow.ToString("dd"));
            Directory.CreateDirectory(bootstrapDay);
            string bootstrapFile=Path.Combine(bootstrapDay,"rollout-synthetic-"+ThreadId+".jsonl");
            ordinal=0;
            Append(bootstrapFile,Meta(),Start("during-bootstrap"));
            using(var bootstrap=new TaskUsageMonitor(Path.Combine(testRoot,"bootstrap-storage"),bootstrapSource,delegate{return clock;}))
            {
                clock=clock.AddSeconds(2);
                Append(bootstrapFile,Usage("during-bootstrap",120,120),Done("during-bootstrap"));
                Assert(Poll(bootstrap).NewCompletions.Count==0,"completion during bootstrap first enters grace");
                clock=clock.AddSeconds(6);
                Assert(Poll(bootstrap).NewCompletions.Single().TurnId=="during-bootstrap","completion after launch is not lost during bootstrap");
            }
            Console.WriteLine("PASS: "+assertions+" isolated task-monitor assertions.");
            return 0;
        }
        catch(Exception ex) { Console.WriteLine(ex.ToString());return 1; }
    }
}
