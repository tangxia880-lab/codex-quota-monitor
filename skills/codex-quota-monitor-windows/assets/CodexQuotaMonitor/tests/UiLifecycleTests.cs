using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using CodexQuotaMonitor;

// Offline WinForms regression harness. Only synthetic snapshots and task records
// are supplied; diagnostic mode disables live readers, settings writes and alerts.
internal static class UiLifecycleTests
{
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    private static int assertions;

    private static object Field(object target, string name)
    {
        FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new Exception("Missing test seam: " + name);
        return field.GetValue(target);
    }

    private static void Check(bool pass, string name)
    {
        assertions++;
        if (!pass) throw new Exception("FAIL: " + name);
    }

    private static double? Remaining(MonitorForm form)
    {
        return (double?)typeof(MonitorForm).GetMethod("MainRemaining", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
    }

    private static void PlaceOffscreen(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-30000, -30000);
    }

    private static void LoadQuota(MonitorForm form, string json)
    {
        form.SetSnapshot(UsageSnapshot.FromJson(json));
        Application.DoEvents();
        using (var bitmap = new Bitmap(form.ClientSize.Width, form.ClientSize.Height))
            form.Surface.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
    }

    private static void CheckFooter(MonitorForm form, string size)
    {
        Button refresh = (Button)Field(form, "refresh");
        Button hide = (Button)Field(form, "hide");
        Button tasks = (Button)Field(form, "taskButton");
        Check(refresh.Width >= 100 && refresh.Parent.ClientRectangle.Contains(refresh.Bounds), size + " refresh button fits");
        Check(hide.Width >= 100 && hide.Parent.ClientRectangle.Contains(hide.Bounds), size + " hide button fits");
        Check(!refresh.Bounds.IntersectsWith(hide.Bounds), size + " footer buttons do not overlap");
        Check(tasks.Visible && tasks.Enabled && tasks.Parent.ClientRectangle.Contains(tasks.Bounds), size + " task button visible and fits footer");
        foreach (Control sibling in tasks.Parent.Controls)
            if (sibling != tasks && sibling.Visible)
                Check(!tasks.Bounds.IntersectsWith(sibling.Bounds), size + " task button does not overlap " + sibling.GetType().Name + " " + sibling.Text);
    }

    private static TaskUsageUpdate Fixture()
    {
        DateTime end = new DateTime(2026, 9, 11, 1, 0, 0, DateTimeKind.Utc);
        return new TaskUsageUpdate {
            Status = "离线测试数据", ActiveCount = 0, CheckedAtUtc = end,
            NewCompletions = new List<TaskUsageRecord>(),
            Records = new List<TaskUsageRecord> {
                new TaskUsageRecord {
                    Key = "root:turn-2", ThreadId = "root", TurnId = "turn-2", Title = "修复示例任务", Model = "fixture-model", Status = "completed",
                    StartedAtUtc = end.AddMinutes(-2), CompletedAtUtc = end,
                    Usage = new TokenCounts { Total = 150, Input = 100, CachedInput = 60, Output = 50, Reasoning = 20 },
                    Cumulative = new TokenCounts { Total = 1200, Input = 900, Output = 300 }
                },
                new TaskUsageRecord {
                    Key = "child:turn-1", ThreadId = "child", TurnId = "turn-1", ParentThreadId = "root", Title = "检查子任务", Status = "completed",
                    StartedAtUtc = end.AddMinutes(-1), CompletedAtUtc = end,
                    Usage = new TokenCounts { Total = 30, Input = 20, CachedInput = 10, Output = 10, Reasoning = 5 },
                    Cumulative = new TokenCounts { Total = 30 }
                },
                new TaskUsageRecord {
                    Key = "unknown:turn-1", ThreadId = "unknown", TurnId = "turn-1", Title = "未提供用量", Status = "completed", IsPartial = true,
                    Usage = null, Cumulative = null
                }
            }
        };
    }

    private static void SelectRow(DataGridView grid, int index)
    {
        grid.ClearSelection();
        grid.CurrentCell = grid.Rows[index].Cells[0];
        grid.Rows[index].Selected = true;
        Application.DoEvents();
    }

    private static string Cell(DataGridView grid, int row, string column)
    {
        return Convert.ToString(grid.Rows[row].Cells[column].Value);
    }

    private static void TestMainForm()
    {
        using (var form = new MonitorForm(false, true))
        {
            PlaceOffscreen(form);
            Check(Field(form, "tray") == null, "diagnostic mode creates no tray icon");
            Check(Field(form, "taskMonitor") == null, "diagnostic mode creates no live task reader");
            Check(Remaining(form) == null, "empty initial quota is unknown");
            form.Show(); Application.DoEvents();
            CheckFooter(form, "default size");
            var intervalMenu = (ContextMenuStrip)Field(form, "intervalMenu");
            Check(form.SelectedRefreshText == "5 分钟", "default refresh selection is 5 minutes");
            int[] values = { 1, 5, 10, 15 };
            for (int i = 0; i < values.Length; i++)
            {
                ((ToolStripMenuItem)intervalMenu.Items[i]).PerformClick();
                Check(form.SelectedRefreshText == values[i] + " 分钟", "refresh label " + values[i]);
                Check(((Settings)Field(form, "settings")).IntervalMinutes == values[i], "refresh setting " + values[i]);
            }
            LoadQuota(form, "{\"rateLimitResetCredits\":{\"availableCount\":3},\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":27,\"windowDurationMins\":10080},\"secondary\":null}}");
            Check(Remaining(form) == 73, "known main percentage");
            var cards = (Panel)Field(form, "cards");
            Check(cards.Controls.OfType<QuotaCard>().Count() == 1, "known main renders one quota card");
            form.SetTaskUpdateForDiagnostics(Fixture());
            Check(((Button)Field(form, "taskButton")).Text == "任务用量 · 3", "task button shows fixture record count");
            var floating = form.CreateFloatingForDiagnostics();
            var floatingState = (FloatingQuotaState)Field(floating, "state");
            Check(floatingState.LastCacheText == "缓存 60.0%", "floating cache percentage uses latest main round");
            Check(floatingState.LastTaskText.Contains("150 Token"), "floating keeps latest round tokens alongside cache");
            var cacheRepair = Fixture();
            cacheRepair.Records[0].Usage.CachedInput = 90;
            form.SetTaskUpdateForDiagnostics(cacheRepair);
            typeof(MonitorForm).GetMethod("UpdateFloating", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(form, null);
            Check(((FloatingQuotaState)Field(floating, "state")).LastCacheText == "缓存 90.0%", "late cache-only repair updates floating percentage");
            Check(((ToolTip)Field(floating, "tip")).GetToolTip(floating).Contains("命中输入 ÷ 全部输入"), "floating tooltip explains cache denominator");
            Size original = form.Size;
            form.Size = form.MinimumSize; Application.DoEvents();
            CheckFooter(form, "minimum size after task update");
            form.Size = original; Application.DoEvents();
            LoadQuota(form, "{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":null,\"secondary\":null}}");
            Check(Remaining(form) == null, "all null windows remain unknown");
            LoadQuota(form, "{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":null},\"secondary\":{\"usedPercent\":50}}}");
            Check(Remaining(form) == 50, "unknown window excluded from minimum");
            LoadQuota(form, "{\"rateLimitsByLimitId\":{\"spark\":{\"primary\":{\"usedPercent\":99}},\"codex\":{\"primary\":{\"usedPercent\":20},\"secondary\":{\"usedPercent\":85}}}}");
            Check(Remaining(form) == 15, "minimum of dual main windows and main bucket preferred");
            LoadQuota(form, "{}");
            Check(Remaining(form) == null, "absent quota remains unknown");
            Check(cards.Controls.Count == 1 && cards.Controls[0] is EmptyPanel, "absent quota renders empty state");
            var client = (CodexClient)Field(form, "client");
            var timer = (System.Windows.Forms.Timer)Field(form, "timer");
            CancellationToken token = ((CancellationTokenSource)Field(form, "cancellation")).Token;
            form.CloseForExit(); Application.DoEvents();
            Check(form.IsDisposed, "close disposes main form");
            Check(token.IsCancellationRequested, "close cancels refresh lifetime");
            Check(!timer.Enabled, "close stops timer");
            bool clientDisposed = false;
            try { client.ReadRateLimitsAsync(CancellationToken.None).GetAwaiter().GetResult(); }
            catch (ObjectDisposedException) { clientDisposed = true; }
            Check(clientDisposed, "close disposes client before any request");
        }
    }

    private static void TestTaskForm()
    {
        var notificationChanges = new List<bool>();
        using (var form = new TaskUsageForm(true, delegate(bool value) { notificationChanges.Add(value); }))
        {
            PlaceOffscreen(form);
            var notify = (CheckBox)Field(form, "notify");
            var summary = (Label)Field(form, "summary");
            var detail = (Label)Field(form, "detail");
            var source = (Label)Field(form, "source");
            var cacheRate = (Label)Field(form, "cacheRate");
            var cacheTotals = (Label)Field(form, "cacheTotals");
            var cacheCoverage = (Label)Field(form, "cacheCoverage");
            var grid = (DataGridView)Field(form, "grid");
            Check(notify.Checked && notificationChanges.Count == 0, "constructor does not save notification preference");
            form.Show(); Application.DoEvents();
            Check(notify.Visible && notify.Parent.ClientRectangle.Contains(notify.Bounds), "notification checkbox fits header");
            Check(!notify.Bounds.IntersectsWith(summary.Bounds), "notification checkbox does not overlap header summary");
            Size original = form.Size;
            form.Size = form.MinimumSize; Application.DoEvents();
            Check(notify.Parent.ClientRectangle.Contains(notify.Bounds), "notification checkbox fits minimum-size header");
            Check(grid.ClientSize.Width >= grid.Columns.Cast<DataGridViewColumn>().Sum(c => c.MinimumWidth), "minimum-size task table fits minimum column widths");
            form.Size = original; Application.DoEvents();

            TaskUsageUpdate update = Fixture();
            form.SetUpdate(update, false); Application.DoEvents();
            Check(!notify.Checked && notificationChanges.Count == 0, "SetUpdate changes notification display without saving");
            typeof(CheckBox).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(notify, new object[] { EventArgs.Empty });
            Check(notify.Checked && notificationChanges.SequenceEqual(new[] { true }), "user checkbox click saves exactly once");
            form.SetUpdate(update, false); Application.DoEvents();
            Check(!notify.Checked && notificationChanges.Count == 1, "unchanged records still sync preference without callback");
            Check(grid.Rows.Count == 3, "one row per completed turn including child and unknown");
            Check(summary.Text.Contains("最近 3 轮记录") && summary.Text.Contains("主任务 2 轮"), "summary separates main-task rounds from child rounds");
            Check(Cell(grid, 0, "turn") == "150" && Cell(grid, 0, "total") == "1,200", "turn total stays separate from cumulative total");
            Check(Cell(grid, 1, "turn") == "30" && Cell(grid, 1, "task").StartsWith("↳ "), "child usage is its own row");
            Check(Cell(grid, 2, "turn") == "未知" && Cell(grid, 2, "total") == "未知", "missing totals remain unknown");
            Check(Cell(grid, 2, "time") == "时间未知" && Cell(grid, 2, "status") == "用量不完整", "unknown metadata stays visibly incomplete");
            Check(Cell(grid, 0, "cache") == "60.0%" && Cell(grid, 1, "cache") == "50.0%" && Cell(grid, 2, "cache") == "未知", "per-round cache column separates known and missing inputs");
            Check(cacheRate.Text == "58.3%", "cache summary weights by input tokens across independent parent and child rounds");
            Check(cacheTotals.Text.Contains("70") && cacheTotals.Text.Contains("120"), "cache summary shows numerator and denominator");
            Check(cacheCoverage.Text.Contains("2") && cacheCoverage.Text.Contains("1"), "cache summary exposes valid and missing coverage");
            Check(grid.Rows[2].DefaultCellStyle.ForeColor == Theme.Danger, "partial usage is highlighted");
            SelectRow(grid, 0);
            Check(detail.Text.Contains("输入  100") && detail.Text.Contains("其中缓存命中  60"), "selected detail distinguishes cached subset of input");
            Check(detail.Text.Contains("输出  50") && detail.Text.Contains("其中推理  20"), "selected detail distinguishes reasoning subset of output");
            Check(detail.Text.Contains("60.0%"), "selected detail includes per-round cache hit rate");
            Check(detail.Text.Contains("不重复加入总数") && Cell(grid, 0, "turn") == "150", "cached and reasoning subsets do not inflate total");
            Check(detail.Text.Contains("fixture-model") && detail.Text.Contains("2 分钟"), "selected detail shows model and duration");
            Check(source.Text.Contains("子任务另列") && source.Text.Contains("不能换算"), "scope text explains separate child use and quota limitation");
            SelectRow(grid, 2);
            Check(detail.Text.StartsWith("未提供用量") && detail.Text.Contains("输入  未知") && detail.Text.Contains("输出  未知"), "selection updates to unknown detail without stale counts");

            TaskUsageUpdate repaired = Fixture();
            repaired.Records[2].Usage = new TokenCounts { Total = 7, Input = 5, Output = 2 };
            repaired.Records[2].Cumulative = new TokenCounts { Total = 700 };
            repaired.Records[2].IsPartial = false;
            form.SetUpdate(repaired, false); Application.DoEvents();
            Check(Cell(grid, 2, "turn") == "7" && Cell(grid, 2, "total") == "700", "late known totals repair an unknown row");
            Check(grid.SelectedRows.Count == 1 && ((TaskUsageRecord)grid.SelectedRows[0].Tag).Key == "unknown:turn-1", "selection preserved across record repair");
            Check(detail.Text.StartsWith("未提供用量") && detail.Text.Contains("输入  5"), "selected repaired row details refresh");

            SelectRow(grid, 0);
            TaskUsageUpdate detailedRepair = Fixture();
            detailedRepair.Records[2] = repaired.Records[2];
            detailedRepair.Records[0].Usage.CachedInput = 61;
            detailedRepair.Records[0].Cumulative.Total = 1300;
            form.SetUpdate(detailedRepair, false); Application.DoEvents();
            Check(Cell(grid, 0, "total") == "1,300", "cumulative-only repair updates row with unchanged turn total");
            Check(detail.Text.Contains("其中缓存命中  61"), "detail-only repair refreshes selected row");
            Check(cacheRate.Text == "59.2%" && Cell(grid, 0, "cache") == "61.0%", "cache-only repair refreshes weighted summary and selected row");
            Check(notificationChanges.Count == 1, "all data updates leave preference callback untouched");
            form.SetUpdate(new TaskUsageUpdate { Records = null, Status = "测试空记录" }, false); Application.DoEvents();
            Check(grid.Rows.Count == 0 && detail.Text.Contains("暂无已结束的本机任务"), "empty update clears rows and shows useful empty state");
            Check(cacheRate.Text == "未知", "empty cache summary never reports zero or perfect hit rate");
            form.Close(); Application.DoEvents();
            Check(form.IsDisposed && grid.IsDisposed && notify.IsDisposed, "closing task window disposes controls");
        }
    }

    [STAThread]
    private static int Main()
    {
        try
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            TestMainForm();
            TestTaskForm();
            Console.WriteLine("PASS: " + assertions + " offline WinForms lifecycle, layout and task usage assertions; no live sessions, requests, settings writes or notifications.");
            return 0;
        }
        catch (Exception error) { Console.WriteLine(error.ToString()); return 1; }
    }
}
