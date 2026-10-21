using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexQuotaMonitor
{
    public sealed class TaskUsageUpdate
    {
        public List<TaskUsageRecord> Records { get; set; }
        public List<TaskUsageRecord> NewCompletions { get; set; }
        public string Status { get; set; }
        public int ActiveCount { get; set; }
        public DateTime CheckedAtUtc { get; set; }
    }

    // This reads only local rollout metadata. No Codex server, model turn,
    // credential file or chat content is persisted by the monitor.
    public sealed class TaskUsageMonitor : IDisposable
    {
        private sealed class Cursor : IDisposable
        {
            internal string Path;
            internal long Offset;
            internal DateTime CreationUtc;
            internal readonly MemoryStream Pending = new MemoryStream();
            internal bool SkipLongLine;
            internal TaskUsageParser Parser;
            public void Dispose() { Pending.Dispose(); }
        }
        private const int MaxLineBytes = 8 * 1024 * 1024;
        private const int MaxFileBytesPerPoll = 4 * 1024 * 1024;
        private readonly string codexHome, sessionsRoot, archiveRoot, historyPath;
        private readonly DateTime startedUtc;
        private readonly Func<DateTime> clock;
        private readonly SemaphoreSlim pollGate = new SemaphoreSlim(1, 1);
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private readonly ConcurrentDictionary<string, byte> changedPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Cursor> cursors = new Dictionary<string, Cursor>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, TaskUsageRecord> history = new Dictionary<string, TaskUsageRecord>(StringComparer.Ordinal);
        private readonly HashSet<string> observed = new HashSet<string>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> pendingCompletions = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly List<FileSystemWatcher> watchers = new List<FileSystemWatcher>();
        private readonly JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
        private readonly Cursor indexCursor;
        private DateTime lastDiscoveryUtc = DateTime.MinValue;
        private volatile bool disposed;
        private string lastSavedJson;

        // Isolated synthetic-log testing can supply its own source directory.
        public TaskUsageMonitor(string storageFolder = null, string sourceHome = null)
            : this(storageFolder, sourceHome, null) { }

        internal TaskUsageMonitor(string storageFolder, string sourceHome, Func<DateTime> utcNow)
        {
            clock = utcNow ?? delegate { return DateTime.UtcNow; };
            startedUtc = clock();
            if (String.IsNullOrWhiteSpace(sourceHome)) sourceHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            codexHome = String.IsNullOrWhiteSpace(sourceHome)
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
                : System.IO.Path.GetFullPath(sourceHome);
            sessionsRoot = System.IO.Path.Combine(codexHome, "sessions");
            archiveRoot = System.IO.Path.Combine(codexHome, "archived_sessions");
            string folder = String.IsNullOrWhiteSpace(storageFolder)
                ? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaMonitor")
                : System.IO.Path.GetFullPath(storageFolder);
            historyPath = System.IO.Path.Combine(folder, "task-usage.json");
            indexCursor = new Cursor { Path = System.IO.Path.Combine(codexHome, "session_index.jsonl") };
            LoadHistory();
            AddWatcher(sessionsRoot);
            AddWatcher(archiveRoot);
        }

        public async Task<TaskUsageUpdate> PollAsync(CancellationToken cancellationToken)
        {
            if (disposed) throw new ObjectDisposedException("TaskUsageMonitor");
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token))
            {
                await pollGate.WaitAsync(linked.Token).ConfigureAwait(false);
                try { return await Task.Run(delegate { return PollCore(linked.Token); }, linked.Token).ConfigureAwait(false); }
                finally { pollGate.Release(); }
            }
        }

        private TaskUsageUpdate PollCore(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            int inaccessible = 0;
            var fresh = new List<TaskUsageRecord>();
            DateTime now = clock();
            if ((now - lastDiscoveryUtc).TotalSeconds >= 30)
            {
                DiscoverRecent();
                lastDiscoveryUtc = now;
            }
            byte ignored;
            foreach (string path in changedPaths.Keys)
                if (changedPaths.TryRemove(path, out ignored)) Track(path);
            try { ReadTail(indexCursor, MaxFileBytesPerPoll, ReadIndexLine, token); }
            catch (IOException) { inaccessible++; }
            catch (UnauthorizedAccessException) { inaccessible++; }

            int byteBudget = 32 * 1024 * 1024;
            foreach (Cursor cursor in cursors.Values.ToArray())
            {
                token.ThrowIfCancellationRequested();
                if (byteBudget <= 0) break;
                try
                {
                    int bytesRead = ReadTail(cursor, Math.Min(MaxFileBytesPerPoll, byteBudget), delegate(string line) { cursor.Parser.AddLine(line); }, token);
                    byteBudget -= bytesRead;
                    if (bytesRead == 0) continue;
                    foreach (TaskUsageRecord record in cursor.Parser.Completed)
                    {
                        if (record == null || String.IsNullOrWhiteSpace(record.Key)) continue;
                        ApplyTitle(record);
                        bool isNew = observed.Add(record.Key);
                        history[record.Key] = record;
                        // First launch and history reconstruction never replay old balloons.
                        // Root UI separately decides whether completion notifications are enabled.
                        if (isNew && record.CompletedAtUtc >= startedUtc)
                            pendingCompletions[record.Key] = now;
                    }
                }
                catch (IOException) { inaccessible++; }
                catch (UnauthorizedAccessException) { inaccessible++; }
            }
            foreach (TaskUsageRecord record in history.Values) ApplyTitle(record);
            foreach (string key in pendingCompletions.Keys.ToArray())
            {
                TaskUsageRecord record;
                if (!history.TryGetValue(key, out record)) { pendingCompletions.Remove(key); continue; }
                bool known = record.Usage != null && (record.Usage.Total.HasValue || record.Usage.Input.HasValue || record.Usage.Output.HasValue);
                if ((now - pendingCompletions[key]).TotalSeconds < (known ? 5 : 10)) continue;
                fresh.Add(record);
                pendingCompletions.Remove(key);
            }
            var ordered = history.Values.OrderByDescending(r => r.CompletedAtUtc).ThenBy(r => r.Key, StringComparer.Ordinal).Take(200).ToList();
            var keep = new HashSet<string>(ordered.Select(r => r.Key), StringComparer.Ordinal);
            foreach (string key in history.Keys.Where(k => !keep.Contains(k)).ToArray()) history.Remove(key);
            bool saved = SaveHistory(ordered);
            int active = cursors.Values.Where(c => c.Parser != null).Sum(c => c.Parser.ActiveCount);
            string status = cursors.Count == 0
                ? "等待本机 Codex 任务记录；完成一轮后会自动显示用量。"
                : "每 5 秒检查本机记录；主任务与子任务分别统计。";
            if (inaccessible > 0) status += " 部分记录暂时不可读，将自动重试。";
            if (!saved) status += " 本次历史未能保存到本机。";
            return new TaskUsageUpdate { Records = ordered.Select(r => r.Clone()).ToList(), NewCompletions = fresh.Select(r => r.Clone()).ToList(), Status = status, ActiveCount = active, CheckedAtUtc = now };
        }

        private void AddWatcher(string root)
        {
            if (!Directory.Exists(root)) return;
            try
            {
                var watcher = new FileSystemWatcher(root, "*.jsonl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite,
                    InternalBufferSize = 32768
                };
                watcher.Created += delegate(object sender, FileSystemEventArgs e) { QueuePath(e.FullPath); };
                watcher.Changed += delegate(object sender, FileSystemEventArgs e) { QueuePath(e.FullPath); };
                watcher.Renamed += delegate(object sender, RenamedEventArgs e) { QueuePath(e.FullPath); };
                watcher.EnableRaisingEvents = true;
                watchers.Add(watcher);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
        }

        private void QueuePath(string path)
        {
            if (!disposed && changedPaths.Count < 4096) changedPaths.TryAdd(path, 0);
        }

        private void DiscoverRecent()
        {
            // Scan date directories, not the complete transcript archive. Watchers
            // capture later writes to older active tasks outside this startup window.
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DateTime today in new[] { DateTime.Now.Date, DateTime.UtcNow.Date })
                for (int day = 0; day < 7; day++)
                {
                    DateTime date = today.AddDays(-day);
                    directories.Add(System.IO.Path.Combine(sessionsRoot, date.ToString("yyyy"), date.ToString("MM"), date.ToString("dd")));
                }
            foreach (string directory in directories.OrderByDescending(d => d, StringComparer.Ordinal))
            {
                try
                {
                    if (!Directory.Exists(directory)) continue;
                    foreach (string path in Directory.GetFiles(directory, "*.jsonl", SearchOption.TopDirectoryOnly)) Track(path);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private void Track(string path)
        {
            if (cursors.ContainsKey(path) || cursors.Count >= 1024) return;
            string full;
            try { full = System.IO.Path.GetFullPath(path); }
            catch (ArgumentException) { return; }
            catch (NotSupportedException) { return; }
            if (!full.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return;
            if (!full.StartsWith(sessionsRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !full.StartsWith(archiveRoot + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return;
            string fallback = ThreadIdFromPath(full);
            cursors[full] = new Cursor { Path = full, Parser = new TaskUsageParser(fallback) };
        }

        private static string ThreadIdFromPath(string path)
        {
            Match match = Regex.Match(System.IO.Path.GetFileNameWithoutExtension(path), @"([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$", RegexOptions.IgnoreCase);
            return match.Success ? match.Groups[1].Value : String.Empty;
        }

        private int ReadTail(Cursor cursor, int maxBytes, Action<string> accept, CancellationToken token)
        {
            if (!File.Exists(cursor.Path)) return 0;
            var info = new FileInfo(cursor.Path);
            bool replaced = cursor.CreationUtc != DateTime.MinValue && cursor.CreationUtc != info.CreationTimeUtc;
            if (info.Length < cursor.Offset || replaced)
            {
                cursor.Offset = 0;
                cursor.Pending.SetLength(0);
                cursor.SkipLongLine = false;
                if (cursor.Parser != null) cursor.Parser = new TaskUsageParser(ThreadIdFromPath(cursor.Path));
            }
            cursor.CreationUtc = info.CreationTimeUtc;
            if (info.Length <= cursor.Offset) return 0;
            int total = 0;
            using (var stream = new FileStream(cursor.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(cursor.Offset, SeekOrigin.Begin);
                var buffer = new byte[65536];
                while (total < maxBytes)
                {
                    token.ThrowIfCancellationRequested();
                    int count = stream.Read(buffer, 0, Math.Min(buffer.Length, maxBytes - total));
                    if (count == 0) break;
                    int start = 0;
                    for (int i = 0; i < count; i++)
                        if (buffer[i] == 10)
                        {
                            Append(cursor, buffer, start, i - start);
                            if (!cursor.SkipLongLine)
                            {
                                string line = Encoding.UTF8.GetString(cursor.Pending.GetBuffer(), 0, (int)cursor.Pending.Length).TrimEnd('\r').TrimStart('\uFEFF');
                                accept(line);
                            }
                            cursor.Pending.SetLength(0); cursor.SkipLongLine = false; start = i + 1;
                        }
                    Append(cursor, buffer, start, count - start);
                    total += count;
                    cursor.Offset += count;
                }
            }
            return total;
        }

        private static void Append(Cursor cursor, byte[] buffer, int start, int count)
        {
            if (cursor.SkipLongLine) return;
            if (cursor.Pending.Length + count > MaxLineBytes)
            {
                cursor.Pending.SetLength(0); cursor.SkipLongLine = true; return;
            }
            cursor.Pending.Write(buffer, start, count);
        }

        private void ReadIndexLine(string line)
        {
            try
            {
                var row = serializer.DeserializeObject(line) as Dictionary<string, object>;
                if (row == null) return;
                object id, name;
                if (row.TryGetValue("id", out id) && row.TryGetValue("thread_name", out name) && id is string && name is string)
                    titles[(string)id] = CleanTitle((string)name);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        private void ApplyTitle(TaskUsageRecord record)
        {
            string title;
            if (!String.IsNullOrEmpty(record.ThreadId) && titles.TryGetValue(record.ThreadId, out title)) record.Title = title;
            else if (!String.IsNullOrEmpty(record.ParentThreadId) && titles.TryGetValue(record.ParentThreadId, out title)) record.Title = title + " · 子任务";
            else if (String.IsNullOrWhiteSpace(record.Title)) record.Title = String.IsNullOrEmpty(record.ParentThreadId) ? "Codex 任务" : "Codex 子任务";
            record.Title = CleanTitle(record.Title);
            // A parent's own cumulative counter excludes its child processes.
            // Keep each child separate instead of implying a summed account cost.
            if (String.IsNullOrWhiteSpace(record.ScopeNote)) record.ScopeNote = "仅此任务自身；子任务单列，不代表订阅额度百分比。";
        }

        private static string CleanTitle(string title)
        {
            if (title == null) return String.Empty;
            string clean = new string(title.Where(c => !Char.IsControl(c)).Take(140).ToArray());
            return clean.Trim();
        }

        private void LoadHistory()
        {
            try
            {
                if (!File.Exists(historyPath) || new FileInfo(historyPath).Length > 4 * 1024 * 1024) return;
                var records = serializer.Deserialize<List<TaskUsageRecord>>(File.ReadAllText(historyPath, Encoding.UTF8));
                if (records == null) return;
                foreach (TaskUsageRecord record in records.Where(r => r != null && !String.IsNullOrWhiteSpace(r.Key)).OrderByDescending(r => r.CompletedAtUtc).Take(200))
                {
                    ApplyTitle(record);
                    history[record.Key] = record;
                    observed.Add(record.Key);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        private bool SaveHistory(List<TaskUsageRecord> records)
        {
            try
            {
                string json = serializer.Serialize(records);
                if (json == lastSavedJson) return true;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(historyPath));
                string temporary = historyPath + ".tmp";
                File.WriteAllText(temporary, json, new UTF8Encoding(false));
                if (File.Exists(historyPath)) File.Replace(temporary, historyPath, null); else File.Move(temporary, historyPath);
                lastSavedJson = json;
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
            catch (InvalidOperationException) { return false; }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            lifetime.Cancel();
            foreach (FileSystemWatcher watcher in watchers) watcher.Dispose();
            // Cleanup is serialized with the worker. It never blocks the UI while
            // a file read finishes, and no cursor is disposed underneath a poll.
            Task.Run(async delegate
            {
                await pollGate.WaitAsync().ConfigureAwait(false);
                try { foreach (Cursor cursor in cursors.Values) cursor.Dispose(); indexCursor.Dispose(); }
                finally { pollGate.Release(); }
            });
        }
    }
}
