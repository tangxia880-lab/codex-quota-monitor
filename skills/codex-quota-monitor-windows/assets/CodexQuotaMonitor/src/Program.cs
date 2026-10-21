using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace CodexQuotaMonitor
{
    internal static class Program
    {
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        internal static EventWaitHandle OpenEvent;
        internal static EventWaitHandle ExitEvent;
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                SetProcessDPIAware();
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                int diagnostic = Array.IndexOf(args, "--diagnose");
                if (diagnostic >= 0 && diagnostic + 1 < args.Length)
                    return Diagnose(args, args[diagnostic + 1]);
                if (args.Contains("--quit"))
                {
                    try { using (var e = EventWaitHandle.OpenExisting("Local\\CodexQuotaMonitor.Exit.v1")) e.Set(); } catch (WaitHandleCannotBeOpenedException) { }
                    return 0;
                }
                bool created;
                using (var mutex = new Mutex(true, "Local\\CodexQuotaMonitor.Instance.v1", out created))
                {
                    if (!created)
                    {
                        try { using (var e = EventWaitHandle.OpenExisting("Local\\CodexQuotaMonitor.Open.v1")) e.Set(); } catch { }
                        return 0;
                    }
                    using (OpenEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexQuotaMonitor.Open.v1"))
                    using (ExitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\CodexQuotaMonitor.Exit.v1"))
                    using (var form = new MonitorForm(args.Contains("--tray"), false))
                        Application.Run(form);
                }
                return 0;
            }
            catch (Exception ex)
            {
                MessageBox.Show("无法启动额度监控。\n" + ex.Message, "Codex 额度", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }

        private static int Diagnose(string[] args, string reportPath)
        {
            var report = new Dictionary<string, object>();
            try
            {
                UsageSnapshot snapshot;
                using (var client = new CodexClient())
                {
                    snapshot = UsageSnapshot.FromJson(client.ReadRateLimitsAsync(CancellationToken.None).GetAwaiter().GetResult());
                    report["codexPath"] = client.CodexPath;
                }
                report["success"] = true;
                report["fetchedAtUtc"] = snapshot.FetchedAtUtc.ToString("o");
                report["resetCredits"] = snapshot.ResetCredits;
                report["buckets"] = snapshot.Buckets.Select(b => new {
                    id = b.Id, name = b.Name, windows = b.Windows.Select(w => new {
                        label = w.Label, remainingPercent = w.RemainingPercent,
                        resetEpochSeconds = w.ResetEpochSeconds
                    }).ToArray()
                }).ToArray();
                using (var form = new MonitorForm(false, true))
                {
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(-30000, -30000);
                    form.SetSnapshot(snapshot);
                    if (args.Contains("--with-tasks"))
                    {
                        using (var monitor = new TaskUsageMonitor(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath)), "task-diagnostic-data")))
                        {
                            var taskData = monitor.PollAsync(CancellationToken.None).GetAwaiter().GetResult();
                            form.SetTaskUpdateForDiagnostics(taskData);
                            report["taskRecords"] = taskData.Records.Count;
                            report["taskRecordsWithUsage"] = taskData.Records.Count(r => r.Usage != null && r.Usage.Total.HasValue);
                            report["taskHistoryStatus"] = taskData.Status;
                            report["cacheUsage"] = CacheUsageStatistics.Summarize(taskData.Records);
                            int taskRender = Array.IndexOf(args, "--render-tasks");
                            if (taskRender >= 0 && taskRender + 1 < args.Length)
                            {
                                using (var history = new TaskUsageForm(true, null))
                                {
                                    history.StartPosition = FormStartPosition.Manual; history.Location = new Point(-30000, -30000);
                                    history.SetUpdate(taskData, true); history.Show(); Application.DoEvents();
                                    using (var bitmap = new Bitmap(history.Width, history.Height))
                                    { history.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size)); bitmap.Save(args[taskRender + 1], System.Drawing.Imaging.ImageFormat.Png); }
                                    history.Close();
                                }
                            }
                        }
                    }
                    form.Show();
                    Application.DoEvents();
                    report["refreshSelection"] = form.SelectedRefreshText;
                    int floatRender = Array.IndexOf(args, "--render-floating");
                    if (floatRender >= 0 && floatRender + 1 < args.Length)
                    {
                        string prefix = args[floatRender + 1];
                        var widget = form.CreateFloatingForDiagnostics();
                        foreach (bool expanded in new[] { false, true })
                        {
                            widget.SetExpanded(expanded);
                            using (var bitmap = widget.RenderFrame())
                            {
                                bitmap.Save(prefix + (expanded ? "-expanded.png" : "-collapsed.png"), System.Drawing.Imaging.ImageFormat.Png);
                            }
                        }
                        report["floatingRendered"] = true;
                    }
                    int render = Array.IndexOf(args, "--render");
                    if (render >= 0 && render + 1 < args.Length)
                    {
                        using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
                        {
                            form.Surface.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                            bitmap.Save(args[render + 1], System.Drawing.Imaging.ImageFormat.Png);
                        }
                        report["rendered"] = true;
                    }
                    form.CloseForExit();
                }
            }
            catch (Exception ex) { report["success"] = false; report["error"] = ex.Message; }
            File.WriteAllText(reportPath, new JavaScriptSerializer().Serialize(report), new UTF8Encoding(false));
            return Object.Equals(report["success"], true) ? 0 : 1;
        }
    }

    internal sealed class Settings
    {
        public int IntervalMinutes { get; set; }
        public int Threshold { get; set; }
        public bool Notifications { get; set; }
        public bool OnTop { get; set; }
        public bool? TaskNotifications { get; set; }
        public bool? FloatingEnabled { get; set; }
        public string FloatingScreen { get; set; }
        public string FloatingSide { get; set; }
        public double? FloatingFraction { get; set; }
        internal static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexQuotaMonitor"); } }
        internal static Settings Load()
        {
            try
            {
                var s = new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(Path.Combine(Folder, "settings.json")));
                if (s != null && new[] { 1, 5, 10, 15 }.Contains(s.IntervalMinutes) && s.Threshold >= 1 && s.Threshold <= 90) return s;
            }
            catch { }
            return new Settings { IntervalMinutes = 5, Threshold = 20, Notifications = true };
        }
        internal bool Save()
        {
            try
            {
                Directory.CreateDirectory(Folder);
                string path = Path.Combine(Folder, "settings.json");
                string temp = path + ".tmp";
                File.WriteAllText(temp, new JavaScriptSerializer().Serialize(this), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(temp, path, null); else File.Move(temp, path);
                return true;
            }
            catch { return false; }
        }
    }

    internal sealed class MonitorForm : Form
    {
        private readonly Settings settings;
        private readonly CodexClient client = new CodexClient();
        private readonly AlertTracker alerts = new AlertTracker();
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        private readonly bool diagnostic, startInTray;
        private FloatingQuotaForm floating;
        private ToolStripMenuItem floatingMenu;
        private bool allowDashboardShow, monitoringQueued;
        private readonly TaskUsageMonitor taskMonitor;
        private TaskUsageUpdate taskUpdate;
        private TaskUsageForm taskForm;
        private bool pollingTasks;
        private DateTime nextTaskPollUtc = DateTime.MinValue;
        private Button taskButton;
        private readonly Queue<string[]> notificationQueue = new Queue<string[]>();
        private DateTime nextNotificationUtc = DateTime.MinValue;
        private bool lastNotificationWasTask;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private NotifyIcon tray;
        private Icon currentIcon;
        private HeaderPanel header;
        private Panel cards;
        private Label status, credits, intervalCaption, thresholdCaption;
        private readonly ToolTip statusTip = new ToolTip { AutoPopDelay = 15000 };
        private Action layoutFooter;
        private Button refresh, hide;
        private Button interval;
        private readonly ContextMenuStrip intervalMenu = new ContextMenuStrip();
        private NumericUpDown threshold;
        private CheckBox notifications, onTop;
        private UsageSnapshot snapshot;
        private DateTime nextRefreshUtc = DateTime.MinValue;
        private bool refreshing, exiting, loadingSettings;
        private bool lastStale;
        private int failures;
        private string error;
        internal Panel Surface { get; private set; }
        internal string SelectedRefreshText { get { return settings.IntervalMinutes + " 分钟"; } }
        internal MonitorForm(bool startInTray, bool diagnostic)
        {
            this.startInTray = startInTray; this.diagnostic = diagnostic;
            settings = diagnostic ? new Settings { IntervalMinutes = 5, Threshold = 20, Notifications = true } : Settings.Load();
            if (!diagnostic) taskMonitor = new TaskUsageMonitor(Settings.Folder);
            Text = "Codex 额度监控";
            Font = new Font("Microsoft YaHei UI", 9F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(716, 824);
            MinimumSize = new Size(640, 620);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Background;
            TopMost = settings.OnTop;
            Surface = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background };
            Controls.Add(Surface);
            header = new HeaderPanel { Dock = DockStyle.Top, Height = 104 };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 152, Padding = new Padding(28, 10, 28, 16), BackColor = Theme.Background };
            cards = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(28, 0, 28, 0), BackColor = Theme.Background };
            Surface.Controls.Add(cards); Surface.Controls.Add(footer); Surface.Controls.Add(header);

            credits = new Label { AutoSize = true, ForeColor = Theme.Muted, Location = new Point(28, 8), Text = "重置券  —" };
            taskButton = MakeButton("任务用量", false);
            taskButton.SetBounds(568, 0, 120, 30);
            taskButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            taskButton.Click += delegate { ShowTaskUsage(); };
            intervalCaption = new Label { AutoSize = true, ForeColor = Theme.Ink, Text = "自动刷新", Location = new Point(28, 45) };
            interval = MakeButton(settings.IntervalMinutes + " 分钟 ▾", false);
            interval.SetBounds(96, 39, 86, 28);
            foreach (int minutes in new[] { 1, 5, 10, 15 })
            {
                int selectedMinutes = minutes;
                intervalMenu.Items.Add(minutes + " 分钟", null, delegate {
                    settings.IntervalMinutes = selectedMinutes;
                    interval.Text = selectedMinutes + " 分钟 ▾";
                    SaveSettings();
                });
            }
            intervalMenu.BackColor = Theme.Background; intervalMenu.ForeColor = Theme.Ink;
            intervalMenu.Renderer = new ToolStripProfessionalRenderer(new QuotaMenuColors());
            interval.Click += delegate { intervalMenu.Show(interval, new Point(0, interval.Height)); };
            thresholdCaption = new Label { AutoSize = true, ForeColor = Theme.Ink, Text = "提醒阈值", Location = new Point(202, 45) };
            threshold = new NumericUpDown { Location = new Point(270, 40), Width = 61, Minimum = 1, Maximum = 90, BorderStyle = BorderStyle.FixedSingle };
            var percent = new Label { AutoSize = true, Text = "%", ForeColor = Theme.Muted, Location = new Point(336, 45) };
            notifications = new CheckBox { AutoSize = true, Text = "低额度提醒", Location = new Point(379, 43), ForeColor = Theme.Ink };
            interval.BackColor = Theme.Card; interval.ForeColor = Theme.Ink;
            threshold.BackColor = Theme.Card; threshold.ForeColor = Theme.Ink;
            onTop = new CheckBox { AutoSize = true, Text = "置顶", Location = new Point(510, 43), ForeColor = Theme.Ink };
            status = new Label { AutoSize = false, Location = new Point(28, 81), Height = 50, ForeColor = Theme.Muted, Text = "正在读取 Codex 登录状态与账户额度…" };
            refresh = MakeButton("立即刷新", true);
            hide = MakeButton("收至托盘", false);
            footer.Controls.AddRange(new Control[] { credits, taskButton, intervalCaption, interval, thresholdCaption, threshold, percent, notifications, onTop, status, refresh, hide });
            layoutFooter = delegate {
                float dpi; using (Graphics g = CreateGraphics()) dpi = g.DpiX / 96F;
                refresh.SetBounds(footer.Width - (int)(136 * dpi), (int)(87 * dpi), (int)(108 * dpi), (int)(36 * dpi));
                hide.SetBounds(footer.Width - (int)(252 * dpi), (int)(87 * dpi), (int)(108 * dpi), (int)(36 * dpi));
                status.Width = Math.Max((int)(170 * dpi), footer.Width - (int)(306 * dpi));
            };
            footer.Resize += delegate { layoutFooter(); };
            refresh.Click += async delegate { await RefreshAsync(); };
            hide.Click += delegate { Hide(); };
            loadingSettings = true;
            threshold.Value = settings.Threshold;
            notifications.Checked = settings.Notifications;
            onTop.Checked = settings.OnTop;
            loadingSettings = false;
            threshold.ValueChanged += delegate { SaveSettings(); };
            notifications.CheckedChanged += delegate { SaveSettings(); };
            onTop.CheckedChanged += delegate { SaveSettings(); };
            cards.Resize += delegate { ResizeCards(); };
            currentIcon = Theme.MakeIcon(null, false);
            Icon = currentIcon;
            if (!diagnostic)
            {
                var menu = new ContextMenuStrip();
                floatingMenu = new ToolStripMenuItem("显示贴边悬浮窗", null, delegate { ToggleFloating(); });
                floatingMenu.Checked = settings.FloatingEnabled != false;
                menu.Items.Add(floatingMenu);
                menu.Items.Add("打开额度面板", null, delegate { ShowDashboard(); });
                menu.Items.Add("立即刷新", null, async delegate { await RefreshAsync(); });
                menu.Items.Add("查看任务用量", null, delegate { ShowTaskUsage(); });
                menu.Items.Add(new ToolStripSeparator());
                menu.Items.Add("退出监控", null, delegate { CloseForExit(); });
                tray = new NotifyIcon { Icon = currentIcon, Text = "Codex 额度 · 正在连接", Visible = true, ContextMenuStrip = menu };
                tray.DoubleClick += delegate { ShowDashboard(); };
                tray.BalloonTipClicked += delegate { if (lastNotificationWasTask) ShowTaskUsage(); else ShowDashboard(); };
            }
            timer.Interval = 1000;
            timer.Tick += async delegate {
                if (Program.OpenEvent != null && Program.OpenEvent.WaitOne(0)) RevealFloating();
                if (Program.ExitEvent != null && Program.ExitEvent.WaitOne(0)) { CloseForExit(); return; }
                UpdateStatus();
                if (lastStale != IsStale) { lastStale = IsStale; UpdateTray(); }
                UpdateFloating();
                foreach (Control control in cards.Controls) control.Invalidate();
                ShowNextNotification();
                if (DateTime.UtcNow >= nextTaskPollUtc && !pollingTasks) await PollTasksAsync();
                if (exiting) return;
                if (DateTime.UtcNow >= nextRefreshUtc && !refreshing) await RefreshAsync();
            };
            Shown += delegate {
                layoutFooter();
            };
            Resize += delegate { if (!diagnostic && WindowState == FormWindowState.Minimized) Hide(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (!exiting && !diagnostic && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
                if (!e.Cancel) exiting = true;
            };
            FormClosed += delegate {
                timer.Stop(); cancellation.Cancel(); client.Dispose();
                if (taskMonitor != null) taskMonitor.Dispose();
                if (taskForm != null && !taskForm.IsDisposed) taskForm.Close();
                if (floating != null && !floating.IsDisposed) floating.CloseForExit();
                if (tray != null) { tray.Visible = false; tray.Dispose(); }
                if (currentIcon != null) currentIcon.Dispose();
                timer.Dispose(); cancellation.Dispose();
                statusTip.Dispose();
                intervalMenu.Dispose();
            };
            SetSnapshot(null);
        }

        // The controller has a message-loop handle, but its large dashboard is
        // initially invisible. Background monitoring does not depend on Shown.
        protected override void SetVisibleCore(bool value)
        {
            if (value && !diagnostic)
            {
                if (!IsHandleCreated) CreateHandle();
                if (!monitoringQueued)
                {
                    monitoringQueued = true;
                    BeginInvoke((Action)StartMonitoring);
                }
                if (!allowDashboardShow) { base.SetVisibleCore(false); return; }
            }
            base.SetVisibleCore(value);
            if (!value) allowDashboardShow = false;
        }
        private async void StartMonitoring()
        {
            if (diagnostic || exiting) return;
            if (settings.FloatingEnabled != false) CreateFloating().ShowCollapsed();
            timer.Start();
            await PollTasksAsync();
            if (!exiting) await RefreshAsync();
        }

        private FloatingQuotaForm CreateFloating()
        {
            if (floating != null && !floating.IsDisposed) return floating;
            EdgeSide side = settings.FloatingSide == "Left" ? EdgeSide.Left : EdgeSide.Right;
            floating = new FloatingQuotaForm(settings.FloatingScreen, side, settings.FloatingFraction ?? .35, diagnostic);
            floating.DashboardRequested += delegate { ShowDashboard(); };
            floating.TasksRequested += delegate { ShowTaskUsage(); };
            floating.RefreshRequested += async delegate { await RefreshAsync(); };
            floating.HideRequested += delegate { SetFloatingVisible(false); };
            floating.ExitRequested += delegate { CloseForExit(); };
            floating.PlacementChanged += delegate {
                settings.FloatingScreen = floating.DockedScreenName;
                settings.FloatingSide = floating.DockedSide.ToString();
                settings.FloatingFraction = floating.DockFraction;
                if (!diagnostic) settings.Save();
            };
            UpdateFloating();
            return floating;
        }
        internal FloatingQuotaForm CreateFloatingForDiagnostics() { return CreateFloating(); }
        private void SetFloatingVisible(bool visible)
        {
            settings.FloatingEnabled = visible;
            if (visible) CreateFloating().ShowCollapsed();
            else if (floating != null && !floating.IsDisposed) floating.Hide();
            if (floatingMenu != null) floatingMenu.Checked = visible;
            if (!diagnostic) settings.Save();
        }
        private void ToggleFloating() { SetFloatingVisible(floating == null || floating.IsDisposed || !floating.Visible); }
        private void RevealFloating() { SetFloatingVisible(true); }
        private void UpdateFloating()
        {
            if (floating == null || floating.IsDisposed) return;
            QuotaWindow window = snapshot == null || snapshot.MainBucket == null ? null : snapshot.MainBucket.Windows
                .OrderBy(w => w.RemainingPercent ?? Double.MaxValue).FirstOrDefault();
            TaskUsageRecord lastTask = taskUpdate == null ? null : taskUpdate.Records.FirstOrDefault(r => String.IsNullOrEmpty(r.ParentThreadId));
            floating.ApplyState(new FloatingQuotaState {
                Remaining = MainRemaining(), Stale = IsStale, LowThreshold = settings.Threshold,
                ResetText = window == null ? "正在读取额度…" : IsStale ? "同步失败 · 显示上次结果" : window.Countdown(DateTime.UtcNow),
                UpdateText = snapshot == null ? "等待首次同步" : "更新于 " + snapshot.FetchedAtUtc.ToLocalTime().ToString("HH:mm:ss"),
                LastTaskText = lastTask == null ? "完成任务后显示本轮用量" : "本轮 " + CompactTokens(lastTask.Usage == null ? null : lastTask.Usage.Total) + " Token",
                LastCacheText = lastTask == null ? String.Empty : "缓存 " + CacheUsageStatistics.FormatPercent(lastTask.Usage)
            });
        }

        private static string CompactTokens(long? value)
        {
            if (!value.HasValue) return "未知";
            decimal count = value.Value;
            if (count >= 1000000000M) return (count / 1000000000M).ToString("0.##", CultureInfo.InvariantCulture) + "B";
            if (count >= 1000000M) return (count / 1000000M).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (count >= 1000M) return (count / 1000M).ToString("0.##", CultureInfo.InvariantCulture) + "K";
            return count.ToString("0", CultureInfo.InvariantCulture);
        }

        private Button MakeButton(string text, bool primary)
        {
            var button = new Button { Text = text, FlatStyle = FlatStyle.Flat, BackColor = primary ? Theme.BallBlue : Theme.Background, ForeColor = primary ? Color.White : Theme.Ink, Cursor = Cursors.Hand, TabStop = true };
            button.FlatAppearance.BorderSize = primary ? 0 : 1;
            button.FlatAppearance.BorderColor = Theme.Border;
            return button;
        }
        private void SaveSettings()
        {
            if (loadingSettings) return;
            settings.Threshold = (int)threshold.Value;
            settings.Notifications = notifications.Checked; settings.OnTop = onTop.Checked;
            TopMost = settings.OnTop;
            if (!diagnostic && !settings.Save())
                MessageBox.Show(this, "本次设置已生效，但无法保存到本机。下次启动将恢复原设置。", "Codex 额度");
            nextRefreshUtc = DateTime.UtcNow.AddMinutes(settings.IntervalMinutes);
            UpdateStatus();
            if (snapshot != null && error == null) CheckAlerts();
            foreach (QuotaCard card in cards.Controls.OfType<QuotaCard>()) { card.Threshold = settings.Threshold; card.Invalidate(); }
            UpdateTray();
        }
        internal void SetSnapshot(UsageSnapshot value)
        {
            snapshot = value;
            if (diagnostic && value != null) nextRefreshUtc = DateTime.UtcNow.AddMinutes(settings.IntervalMinutes);
            cards.SuspendLayout();
            while (cards.Controls.Count > 0) { Control old = cards.Controls[0]; cards.Controls.Remove(old); old.Dispose(); }
            if (value == null || value.Buckets.Count == 0)
            {
                cards.Controls.Add(new EmptyPanel { Height = 260, Width = 640 });
            }
            else
            {
                float dpi; using (Graphics g = CreateGraphics()) dpi = g.DpiX / 96F;
                foreach (QuotaBucket bucket in value.Buckets.OrderBy(b => b == value.MainBucket ? 0 : b.Id == "codex_bengalfox" ? 1 : 2))
                {
                    var card = new QuotaCard(bucket, bucket == value.MainBucket) { Threshold = settings.Threshold };
                    card.Height = (int)(card.Height * dpi);
                    cards.Controls.Add(card);
                }
            }
            ResizeCards();
            cards.ResumeLayout();
            credits.Text = "可用重置券  " + (value != null && value.ResetCredits.HasValue ? value.ResetCredits.Value.ToString() + " 次" : "未提供") + "    ·    关闭后继续在托盘监控";
            UpdateStatus(); UpdateTray();
        }
        private void ResizeCards()
        {
            float dpi; using (Graphics g = CreateGraphics()) dpi = g.DpiX / 96F;
            int padding = (int)(28 * dpi), gap = (int)(12 * dpi);
            int y = 0;
            foreach (Control card in cards.Controls)
            {
                card.Location = new Point(padding, y + cards.AutoScrollPosition.Y);
                card.Width = Math.Max(250, cards.ClientSize.Width - padding * 2);
                y += card.Height + gap;
            }
            cards.AutoScrollMinSize = new Size(0, y);
        }
        private async Task RefreshAsync()
        {
            if (refreshing || exiting) return;
            refreshing = true; refresh.Enabled = false; refresh.Text = "同步中…"; UpdateStatus();
            try
            {
                string json = await client.ReadRateLimitsAsync(cancellation.Token);
                if (exiting) return;
                var value = UsageSnapshot.FromJson(json);
                if (value.Buckets.Count == 0) throw new InvalidOperationException("接口尚未提供额度。请确认 Codex 使用 ChatGPT 账户登录。");
                error = null; failures = 0;
                nextRefreshUtc = DateTime.UtcNow.AddMinutes(settings.IntervalMinutes);
                SetSnapshot(value); CheckAlerts();
            }
            catch (OperationCanceledException) { if (!exiting) error = "连接超时，请稍后重试。"; }
            catch (Exception ex) { if (!exiting) error = ex.Message; }
            finally
            {
                if (!exiting)
                {
                    if (error != null) { failures++; nextRefreshUtc = DateTime.UtcNow.AddSeconds(Math.Min(900, 60 * Math.Pow(2, Math.Min(4, failures - 1)))); }
                    refreshing = false; refresh.Enabled = true; refresh.Text = "立即刷新";
                    UpdateStatus(); UpdateTray();
                }
            }
        }
        private bool IsStale { get { return error != null || (snapshot != null && (DateTime.UtcNow - snapshot.FetchedAtUtc).TotalMinutes > Math.Max(2, settings.IntervalMinutes * 2)); } }
        private void UpdateStatus()
        {
            header.StatusText = refreshing ? "正在同步" : IsStale ? "待重新同步" : snapshot != null ? "已连接" : "连接中";
            header.Error = IsStale;
            header.Invalidate();
            foreach (QuotaCard card in cards.Controls.OfType<QuotaCard>()) card.Stale = IsStale;
            if (error != null)
            {
                status.ForeColor = Theme.Danger;
                status.Text = "同步失败，显示上次结果。" + Environment.NewLine + error;
                if (snapshot == null) status.Text = "尚未获取额度。" + Environment.NewLine + error;
            }
            else if (snapshot != null)
            {
                status.ForeColor = IsStale ? Theme.Danger : Theme.Muted;
                int seconds = Math.Max(0, (int)(nextRefreshUtc - DateTime.UtcNow).TotalSeconds);
                status.Text = "更新于 " + snapshot.FetchedAtUtc.ToLocalTime().ToString("HH:mm:ss") + (IsStale ? " · 数据已过期" : "") + Environment.NewLine +
                    (refreshing ? "正在获取最新额度…" : "下次刷新 " + (seconds / 60).ToString("00") + ":" + (seconds % 60).ToString("00"));
            }
            else { status.ForeColor = Theme.Muted; status.Text = "正在读取 Codex 账户额度…"; }
            if (statusTip.GetToolTip(status) != status.Text) statusTip.SetToolTip(status, status.Text);
        }
        private double? MainRemaining()
        {
            if (snapshot == null || snapshot.MainBucket == null) return null;
            var known = snapshot.MainBucket.Windows.Where(w => w.RemainingPercent.HasValue).Select(w => w.RemainingPercent.Value).ToArray();
            return known.Length == 0 ? (double?)null : known.Min();
        }
        private void UpdateTray()
        {
            UpdateFloating();
            if (tray == null) return;
            double? remaining = MainRemaining();
            var nextIcon = Theme.MakeIcon(IsStale ? null : remaining, remaining.HasValue && remaining.Value <= settings.Threshold);
            var old = currentIcon; currentIcon = nextIcon; tray.Icon = nextIcon; Icon = nextIcon;
            if (old != null) old.Dispose();
            string text = "Codex · " + (IsStale ? "同步失败 / 数据待更新" : remaining.HasValue ? "剩余 " + remaining.Value.ToString("0.#") + "%" : "额度未知");
            if (snapshot != null) text += " · " + snapshot.FetchedAtUtc.ToLocalTime().ToString("HH:mm") + " 更新";
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
        }
        private void CheckAlerts()
        {
            if (diagnostic || !settings.Notifications || snapshot == null || IsStale) return;
            List<string> messages = alerts.Evaluate(snapshot, settings.Threshold);
            if (messages.Count > 0 && tray != null)
                QueueNotification("Codex 额度提醒", String.Join(Environment.NewLine, messages));
        }
        private async Task PollTasksAsync()
        {
            if (diagnostic || exiting || pollingTasks || taskMonitor == null) return;
            pollingTasks = true;
            try
            {
                var update = await taskMonitor.PollAsync(cancellation.Token);
                if (exiting) return;
                taskUpdate = update;
                taskButton.Text = "任务用量 · " + update.Records.Count;
                statusTip.SetToolTip(taskButton, update.Status);
                if (taskForm != null && !taskForm.IsDisposed) taskForm.SetUpdate(update, settings.TaskNotifications != false);
                if (settings.TaskNotifications != false && update.NewCompletions.Count > 0)
                {
                    var records = update.NewCompletions.Where(r => String.IsNullOrEmpty(r.ParentThreadId)).ToList();
                    if (records.Count > 0)
                    {
                        string body = String.Join(Environment.NewLine, records.Take(3).Select(r => TaskUsageText.Title(r) + "\n本轮 " + TaskUsageText.Count(r.Usage == null ? null : r.Usage.Total) + " Token · 缓存命中率 " + CacheUsageStatistics.FormatPercent(r.Usage) + " · 子任务另列").ToArray());
                        if (records.Count > 3) body += "\n另有 " + (records.Count - 3) + " 轮，请查看任务用量。";
                        QueueNotification("Codex 任务已结束", body);
                    }
                    // Account quota may lag; refresh it without attributing the change to one task.
                    nextRefreshUtc = DateTime.MinValue;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!exiting) { taskButton.Text = "任务用量 · 重试中"; statusTip.SetToolTip(taskButton, "本机任务记录暂不可读，稍后自动重试。"); }
            }
            finally { pollingTasks = false; nextTaskPollUtc = DateTime.UtcNow.AddSeconds(5); }
        }
        internal void SetTaskUpdateForDiagnostics(TaskUsageUpdate update)
        {
            taskUpdate = update;
            taskButton.Text = "任务用量 · " + update.Records.Count;
        }
        private void ShowTaskUsage()
        {
            if (taskForm == null || taskForm.IsDisposed)
                taskForm = new TaskUsageForm(settings.TaskNotifications != false, delegate(bool enabled) {
                    settings.TaskNotifications = enabled;
                    if (!diagnostic && !settings.Save()) MessageBox.Show(taskForm, "设置已生效，但暂时无法保存。", "Codex 任务用量");
                });
            if (taskUpdate != null) taskForm.SetUpdate(taskUpdate, settings.TaskNotifications != false);
            taskForm.Show(); taskForm.WindowState = FormWindowState.Normal; taskForm.Activate();
        }
        private void QueueNotification(string title, string body)
        {
            if (diagnostic || tray == null) return;
            if (notificationQueue.Count < 20) notificationQueue.Enqueue(new[] { title, body });
            ShowNextNotification();
        }
        private void ShowNextNotification()
        {
            if (tray == null || notificationQueue.Count == 0 || DateTime.UtcNow < nextNotificationUtc) return;
            string[] item = notificationQueue.Dequeue();
            lastNotificationWasTask = item[0].Contains("任务");
            tray.ShowBalloonTip(10000, item[0], item[1], item[0].Contains("额度") ? ToolTipIcon.Warning : ToolTipIcon.Info);
            nextNotificationUtc = DateTime.UtcNow.AddSeconds(12);
        }
        private void ShowDashboard() { allowDashboardShow = true; Show(); WindowState = FormWindowState.Normal; Activate(); }
        internal void CloseForExit() { exiting = true; Close(); }
    }

    internal static class Theme
    {
        internal static readonly Color Background = Color.FromArgb(30, 32, 40);
        internal static readonly Color Ink = Color.FromArgb(232, 232, 240);
        internal static readonly Color Muted = Color.FromArgb(154, 154, 176);
        internal static readonly Color Green = Color.FromArgb(122, 155, 255);
        internal static readonly Color PaleGreen = Color.FromArgb(41, 44, 56);
        internal static readonly Color Border = Color.FromArgb(57, 60, 76);
        internal static readonly Color Danger = Color.FromArgb(248, 128, 136);
        internal static readonly Color Card = Color.FromArgb(38, 40, 50);
        internal static readonly Color BallBlue = Color.FromArgb(77, 124, 254);
        internal static readonly Color BallPurple = Color.FromArgb(122, 92, 255);
        [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
        internal static GraphicsPath Round(RectangleF r, float radius)
        {
            var p = new GraphicsPath(); float d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90); p.CloseFigure(); return p;
        }
        internal static void Box(Graphics g, RectangleF rect, float radius, Color color)
        { using (var path = Round(rect, radius)) using (var brush = new SolidBrush(color)) g.FillPath(brush, path); }
        internal static void Text(Graphics g, string text, float size, FontStyle style, Color color, RectangleF bounds)
        {
            using (var font = new Font("Microsoft YaHei UI", size, style, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(text, font, brush, bounds, format);
        }
        internal static Icon MakeIcon(double? value, bool low)
        {
            using (var bitmap = new Bitmap(32, 32))
            {
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    Box(g, new RectangleF(0, 0, 32, 32), 9, !value.HasValue ? Muted : low ? Danger : Green);
                    using (var font = new Font("Segoe UI", value.HasValue && value.Value >= 100 ? 13 : 17, FontStyle.Bold, GraphicsUnit.Pixel))
                    using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        g.DrawString(value.HasValue ? Math.Floor(value.Value).ToString() : "–", font, Brushes.White, new RectangleF(0, -1, 32, 32), format);
                }
                IntPtr handle = bitmap.GetHicon();
                try { using (Icon temp = Icon.FromHandle(handle)) return (Icon)temp.Clone(); }
                finally { DestroyIcon(handle); }
            }
        }
    }

    internal sealed class QuotaMenuColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Background; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Background; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Background; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Background; } }
        public override Color MenuItemSelected { get { return Theme.PaleGreen; } }
        public override Color MenuItemBorder { get { return Theme.Border; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Background; } }
    }

    internal class PaintedPanel : Panel
    {
        internal PaintedPanel() { DoubleBuffered = true; ResizeRedraw = true; }
        protected Graphics LogicalGraphics(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
            e.Graphics.ScaleTransform(DeviceScale, DeviceScale);
            return e.Graphics;
        }
        protected float DeviceScale { get { using (Graphics g = CreateGraphics()) return g.DpiX / 96F; } }
    }
    internal sealed class HeaderPanel : PaintedPanel
    {
        internal string StatusText = "连接中";
        internal bool Error;
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = LogicalGraphics(e); float width = Width / DeviceScale;
            Theme.Box(g, new RectangleF(28, 27, 38, 38), 12, Theme.BallBlue);
            Theme.Text(g, "C", 26, FontStyle.Bold, Color.White, new RectangleF(36, 28, 29, 36));
            Theme.Text(g, "Codex 额度", 25, FontStyle.Bold, Theme.Ink, new RectangleF(80, 21, 320, 39));
            Theme.Text(g, "账户用量 · 托盘随时查看", 12, FontStyle.Regular, Theme.Muted, new RectangleF(82, 61, 360, 22));
            Theme.Box(g, new RectangleF(width - 144, 34, 116, 29), 14, Error ? Color.FromArgb(65, 38, 47) : Theme.PaleGreen);
            Theme.Text(g, "●  " + StatusText, 12, FontStyle.Regular, Error ? Theme.Danger : Theme.Green, new RectangleF(width - 132, 40, 102, 23));
        }
    }
    internal sealed class QuotaCard : PaintedPanel
    {
        private readonly QuotaBucket bucket;
        private readonly bool main;
        internal int Threshold = 20;
        internal bool Stale;
        internal QuotaCard(QuotaBucket bucket, bool main)
        {
            this.bucket = bucket; this.main = main;
            Height = main ? 220 : 156;
            AccessibleName = bucket.Name + "额度";
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = LogicalGraphics(e); float width = Width / DeviceScale, height = Height / DeviceScale;
            Theme.Box(g, new RectangleF(0, 0, width - 1, height - 1), 18, main ? Theme.PaleGreen : Theme.Card);
            Theme.Text(g, bucket.Name, main ? 18 : 16, FontStyle.Bold, Theme.Ink, new RectangleF(22, 17, width - 230, 29));
            string plan = String.IsNullOrWhiteSpace(bucket.PlanType) ? "" : bucket.PlanType.ToUpperInvariant();
            string tag = Stale ? "上次结果 · 待更新" : bucket.SpendControlReached ? "已达消费上限" : main ? plan + "  /  主要额度" : "独立额度";
            Theme.Text(g, tag, 11, FontStyle.Regular, Stale || bucket.SpendControlReached ? Theme.Danger : Theme.Muted, new RectangleF(width - 201, 22, 180, 24));
            if (bucket.Windows.Count == 0)
            {
                Theme.Text(g, "暂未提供可用额度窗口", 16, FontStyle.Regular, Theme.Muted, new RectangleF(22, 73, width - 44, 40)); return;
            }
            float column = (width - 44) / bucket.Windows.Count;
            for (int i = 0; i < bucket.Windows.Count; i++)
            {
                var window = bucket.Windows[i]; float x = 22 + column * i; float space = column - (i == bucket.Windows.Count - 1 ? 0 : 22);
                Color ink = window.RemainingPercent.HasValue && window.RemainingPercent.Value <= Threshold ? Theme.Danger : Theme.Green;
                string number = window.RemainingPercent.HasValue ? window.RemainingPercent.Value.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";
                float top = main ? 58 : 51;
                Theme.Text(g, number, main ? 50 : 31, FontStyle.Bold, ink, new RectangleF(x - 3, top, space, main ? 65 : 45));
                Theme.Text(g, window.Label + "额度 · 剩余", 11, FontStyle.Regular, Theme.Muted, new RectangleF(x, top + (main ? 64 : 43), space, 24));
                float barY = main ? 155 : 111;
                Theme.Box(g, new RectangleF(x, barY, space, 5), 2.4F, Theme.Border);
                if (window.RemainingPercent.HasValue && window.RemainingPercent.Value > 0)
                {
                    float fill = Math.Max(5, space * (float)window.RemainingPercent.Value / 100F);
                    Theme.Box(g, new RectangleF(x, barY, fill, 5), 2.4F, ink == Theme.Danger ? Theme.Danger : Theme.Green);
                }
                string reset = window.ResetLabel;
                if (main)
                {
                    Theme.Text(g, "重置 " + reset, 11, FontStyle.Regular, Theme.Muted, new RectangleF(x, 173, space, 21));
                    Theme.Text(g, window.Countdown(DateTime.UtcNow), 11, FontStyle.Regular, Theme.Muted, new RectangleF(x, 192, space, 21));
                }
                else Theme.Text(g, "重置 " + reset, 11, FontStyle.Regular, Theme.Muted, new RectangleF(x, 128, space, 23));
            }
        }
    }
    internal sealed class EmptyPanel : PaintedPanel
    {
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); var g = LogicalGraphics(e); float width = Width / DeviceScale;
            Theme.Box(g, new RectangleF(0, 0, width - 1, 259), 18, Theme.Card);
            Theme.Text(g, "等待额度数据", 27, FontStyle.Bold, Theme.Ink, new RectangleF(28, 38, width - 56, 50));
            Theme.Text(g, "请先在本机 Codex 中登录 ChatGPT 账户。", 14, FontStyle.Regular, Theme.Muted, new RectangleF(28, 101, width - 56, 30));
            Theme.Text(g, "连接成功后，这里会显示各模型额度与重置时间。", 14, FontStyle.Regular, Theme.Muted, new RectangleF(28, 133, width - 56, 30));
            Theme.Text(g, "网络恢复后会自动重试，也可以点击「立即刷新」。", 13, FontStyle.Regular, Theme.Muted, new RectangleF(28, 195, width - 56, 30));
        }
    }
}
