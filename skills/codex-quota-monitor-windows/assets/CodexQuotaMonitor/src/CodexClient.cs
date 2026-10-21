using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace CodexQuotaMonitor
{
    // Only initialize and account/rateLimits/read are sent. Authentication stays
    // inside the installed Codex CLI; this client never opens credential files.
    public sealed class CodexClient : IDisposable
    {
        private readonly object gate = new object();
        private readonly HashSet<Process> children = new HashSet<Process>();
        private readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        private bool disposed;

        public string CodexPath { get; private set; }

        public CodexClient()
        {
            CodexPath = FindCodexExecutable();
        }

        public async Task<string> ReadRateLimitsAsync(CancellationToken cancellationToken)
        {
            CancellationTokenSource linked;
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("CodexClient");
                linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            }
            using (linked)
            {
                linked.CancelAfter(TimeSpan.FromSeconds(35));
                linked.Token.ThrowIfCancellationRequested();
                // Re-discover on refresh so desktop updates do not pin an old binary.
                CodexPath = FindCodexExecutable();
                if (String.IsNullOrEmpty(CodexPath))
                    throw new InvalidOperationException("没有找到 Codex 程序。请安装并打开 Codex 桌面版，登录后重试。");

                var serializer = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 };
                var process = new Process();
                process.StartInfo = new ProcessStartInfo
                {
                    FileName = CodexPath,
                    Arguments = "app-server --stdio",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8,
                    WorkingDirectory = Path.GetDirectoryName(CodexPath)
                };
                // Drain stderr to avoid pipe deadlocks; never persist or show its
                // contents because upstream diagnostics may include private data.
                process.ErrorDataReceived += delegate { };
                lock (gate)
                {
                    if (disposed) { process.Dispose(); throw new ObjectDisposedException("CodexClient"); }
                    children.Add(process);
                }
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (!process.Start()) throw new InvalidOperationException("无法启动 Codex 额度查询，请重新打开 Codex 后重试。");
                    process.BeginErrorReadLine();
                    using (linked.Token.Register(delegate { StopChild(process); }))
                    {
                        linked.Token.ThrowIfCancellationRequested();
                        await SendAsync(process, serializer, new
                        {
                            id = 1,
                            method = "initialize",
                            @params = new
                            {
                                clientInfo = new { name = "codex_quota_monitor", title = "Codex Quota Monitor", version = "1.0.0" },
                                capabilities = (object)null
                            }
                        }).ConfigureAwait(false);
                        await ReadResponseAsync(process, serializer, 1, linked.Token).ConfigureAwait(false);
                        await SendAsync(process, serializer, new { method = "initialized" }).ConfigureAwait(false);
                        await SendAsync(process, serializer, new { id = 2, method = "account/rateLimits/read" }).ConfigureAwait(false);
                        object result = await ReadResponseAsync(process, serializer, 2, linked.Token).ConfigureAwait(false);
                        return serializer.Serialize(result);
                    }
                }
                catch (OperationCanceledException)
                {
                    if (cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested) throw;
                    throw new TimeoutException("额度查询超过 35 秒。请检查网络和 Codex 登录状态，然后重试。");
                }
                catch (Win32Exception)
                {
                    throw new InvalidOperationException("无法启动已安装的 Codex。请重新打开或更新 Codex 桌面版后重试。");
                }
                catch (IOException)
                {
                    if (linked.IsCancellationRequested)
                    {
                        if (cancellationToken.IsCancellationRequested || lifetime.IsCancellationRequested)
                            throw new OperationCanceledException(cancellationToken);
                        throw new TimeoutException("额度查询超过 35 秒。请检查网络和 Codex 登录状态，然后重试。");
                    }
                    throw new InvalidOperationException("Codex 查询连接已断开。请重新打开 Codex 后重试。");
                }
                finally
                {
                    StopChild(process);
                    lock (gate) children.Remove(process);
                    process.Dispose();
                }
            }
        }

        private static async Task SendAsync(Process process, JavaScriptSerializer serializer, object message)
        {
            await process.StandardInput.WriteLineAsync(serializer.Serialize(message)).ConfigureAwait(false);
            await process.StandardInput.FlushAsync().ConfigureAwait(false);
        }

        private static async Task<object> ReadResponseAsync(Process process, JavaScriptSerializer serializer, int expectedId, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                string line = await process.StandardOutput.ReadLineAsync().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (line == null)
                    throw new InvalidOperationException("Codex 查询进程提前退出。请确认 Codex 已登录并可正常使用，然后重试。");
                if (String.IsNullOrWhiteSpace(line)) continue;
                Dictionary<string, object> message;
                try { message = serializer.DeserializeObject(line) as Dictionary<string, object>; }
                catch (ArgumentException) { continue; }
                catch (InvalidOperationException) { continue; }
                if (message == null) continue;
                object id;
                if (!message.TryGetValue("id", out id) || Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture) != expectedId.ToString()) continue;
                object error;
                if (message.TryGetValue("error", out error) && error != null)
                    throw CreateSafeRpcException(serializer.Serialize(error));
                object result;
                if (!message.TryGetValue("result", out result) || result == null)
                    throw new InvalidOperationException("Codex 没有返回额度数据。请稍后重试或更新 Codex。");
                return result;
            }
        }

        private static Exception CreateSafeRpcException(string error)
        {
            string lower = error.ToLowerInvariant();
            if (lower.Contains("-32601") || lower.Contains("method not found"))
                return new InvalidOperationException("当前 Codex 版本不支持额度查询，请更新 Codex 桌面版后重试。");
            if (lower.Contains("401") || lower.Contains("unauthoriz") || lower.Contains("not logged") || lower.Contains("auth") || lower.Contains("chatgpt"))
                return new InvalidOperationException("无法读取账户额度。请在 Codex 中使用 ChatGPT 账户登录；API Key 登录可能不提供订阅额度。");
            if (lower.Contains("429") || lower.Contains("too many"))
                return new InvalidOperationException("额度查询过于频繁，请稍后重试。");
            if (lower.Contains("timeout") || lower.Contains("timed out"))
                return new TimeoutException("额度服务响应超时，请检查网络后重试。");
            return new InvalidOperationException("暂时无法读取 Codex 额度。请检查网络和 Codex 登录状态后重试。");
        }

        private static string FindCodexExecutable()
        {
            var desktop = new List<string>();
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string desktopBin = Path.Combine(local, "OpenAI", "Codex", "bin");
            AddFile(desktop, Path.Combine(desktopBin, "codex.exe"));
            foreach (string folder in SafeDirectories(desktopBin))
                AddFile(desktop, Path.Combine(folder, "codex.exe"));
            string preferred = Newest(desktop);
            if (preferred != null) return preferred;

            // Windows npm installs keep a native CLI in their platform package.
            var npm = new List<string>();
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            AddNpmCandidates(npm, Path.Combine(roaming, "npm", "node_modules", "@openai"));
            string path = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
            foreach (string entry in path.Split(Path.PathSeparator))
            {
                string directory = entry.Trim().Trim('"');
                if (String.IsNullOrEmpty(directory)) continue;
                try
                {
                    if (!Path.IsPathRooted(directory)) continue;
                    AddFile(npm, Path.Combine(directory, "codex.exe"));
                    AddNpmCandidates(npm, Path.Combine(directory, "node_modules", "@openai"));
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
            return Newest(npm) ?? String.Empty;
        }

        private static void AddNpmCandidates(List<string> files, string openAiRoot)
        {
            string[] platformPackages = { "codex-win32-x64", "codex-win32-arm64" };
            string[] triples = { "x86_64-pc-windows-msvc", "aarch64-pc-windows-msvc" };
            for (int i = 0; i < platformPackages.Length; i++)
            {
                AddFile(files, Path.Combine(openAiRoot, "codex", "node_modules", "@openai", platformPackages[i], "vendor", triples[i], "bin", "codex.exe"));
                AddFile(files, Path.Combine(openAiRoot, platformPackages[i], "vendor", triples[i], "bin", "codex.exe"));
                AddFile(files, Path.Combine(openAiRoot, "codex", "vendor", triples[i], "codex", "codex.exe"));
            }
        }

        private static void AddFile(List<string> files, string path)
        {
            if (File.Exists(path) && !files.Contains(path, StringComparer.OrdinalIgnoreCase)) files.Add(path);
        }

        private static string[] SafeDirectories(string path)
        {
            try { return Directory.Exists(path) ? Directory.GetDirectories(path) : new string[0]; }
            catch (IOException) { return new string[0]; }
            catch (UnauthorizedAccessException) { return new string[0]; }
        }

        private static string Newest(List<string> paths)
        {
            return paths.OrderByDescending(delegate(string path)
            {
                try { return File.GetLastWriteTimeUtc(path); }
                catch (IOException) { return DateTime.MinValue; }
                catch (UnauthorizedAccessException) { return DateTime.MinValue; }
            }).FirstOrDefault();
        }

        private static void StopChild(Process process)
        {
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
            catch (Win32Exception) { }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                lifetime.Cancel();
                foreach (Process child in children) StopChild(child);
            }
            // Keep this small CTS alive while in-flight async cleanup may still
            // inspect IsCancellationRequested; it owns no wait handle here.
        }
    }
}
