using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Windows.Forms;

namespace CodexQuotaMonitor
{
    internal static class TaskUsageText
    {
        internal static string Count(long? value) { return value.HasValue ? value.Value.ToString("N0", CultureInfo.InvariantCulture) : "未知"; }
        internal static string Title(TaskUsageRecord record)
        {
            string title = String.IsNullOrWhiteSpace(record.Title) ? "任务 " + ShortId(record.ThreadId) : record.Title;
            return String.IsNullOrWhiteSpace(record.ParentThreadId) ? title : "↳ " + title;
        }
        private static string ShortId(string value) { return String.IsNullOrEmpty(value) ? "未知" : value.Substring(0, Math.Min(8, value.Length)); }
        internal static string Status(TaskUsageRecord record)
        {
            if (record.Status == "interrupted" || record.Status == "aborted") return "已中断";
            if (record.Status == "failed") return "失败";
            return record.IsPartial ? "用量不完整" : "已完成";
        }
    }

    internal sealed class TaskUsageForm : Form
    {
        private readonly DataGridView grid;
        private readonly Label summary, detail, source;
        private readonly Label cacheRate, cacheTotals, cacheCoverage;
        private readonly CheckBox notify;
        private readonly Action<bool> saveNotifications;
        private bool applying;
        private string fingerprint;

        internal TaskUsageForm(bool notificationsEnabled, Action<bool> saveNotifications)
        {
            SuspendLayout();
            this.saveNotifications = saveNotifications;
            Text = "Codex · 任务用量";
            Font = new Font("Microsoft YaHei UI", 9F);
            BackColor = Theme.Background;
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1000, 800);
            MinimumSize = new Size(800, 680);
            StartPosition = FormStartPosition.CenterParent;
            var header = new Panel { Dock = DockStyle.Top, Height = 108, BackColor = Theme.Background };
            var title = new Label { Text = "任务用量", Font = new Font("Microsoft YaHei UI", 22F, FontStyle.Bold), AutoSize = true, Location = new Point(25, 20), ForeColor = Theme.Ink };
            summary = new Label { Text = "正在读取最近的本机任务…", AutoSize = false, Height = 27, Location = new Point(29, 74), Width = 720, ForeColor = Theme.Muted, AutoEllipsis = true };
            notify = new CheckBox { Text = "每轮完成后提醒", AutoSize = true, Checked = notificationsEnabled, Location = new Point(779, 34), ForeColor = Theme.Ink };
            header.Controls.AddRange(new Control[] { title, summary, notify });
            Action layoutHeader = delegate {
                float dpi; using (var g = CreateGraphics()) dpi = g.DpiX / 96F;
                notify.Left = header.ClientSize.Width - notify.Width - (int)(28 * dpi);
                summary.Width = Math.Max(300, header.ClientSize.Width - (int)(56 * dpi));
            };
            header.Resize += delegate { layoutHeader(); };
            Shown += delegate { layoutHeader(); };
            notify.CheckedChanged += delegate { if (!applying && this.saveNotifications != null) this.saveNotifications(notify.Checked); };
            var cacheFrame = new Panel { Dock = DockStyle.Top, Height = 190, Padding = new Padding(28, 0, 28, 16), BackColor = Theme.Background };
            var cacheCard = new Panel { Dock = DockStyle.Fill, BackColor = Theme.PaleGreen, AccessibleName = "近期缓存命中率" };
            var cacheCaption = new Label { Text = "近期缓存命中率", AutoSize = false, ForeColor = Theme.Ink, Font = new Font(Font, FontStyle.Bold) };
            cacheRate = new Label { Text = "未知", AutoSize = false, ForeColor = Theme.Green, Font = new Font("Microsoft YaHei UI", 31F, FontStyle.Bold) };
            var cacheWeight = new Label { Text = "按输入 Token 加权", AutoSize = false, ForeColor = Theme.Muted, Font = new Font("Microsoft YaHei UI", 8.5F) };
            var cacheTotalsCaption = new Label { Text = "缓存命中输入 / 有效输入 Token", AutoSize = false, ForeColor = Theme.Muted };
            cacheTotals = new Label { Text = "0 / 0", AutoSize = false, ForeColor = Theme.Ink, Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold), AutoEllipsis = true };
            cacheCoverage = new Label { Text = "有效 0 轮 · 未知 0 轮 · 零输入 0 轮", AutoSize = false, ForeColor = Theme.Ink, AutoEllipsis = true };
            var cacheScope = new Label { Text = "统计范围：本机保留记录（最多 200 轮，含主任务和子任务）。未知、零输入轮次已排除。", AutoSize = false, ForeColor = Theme.Muted, Font = new Font("Microsoft YaHei UI", 8.5F) };
            var cacheDivider = new Panel { BackColor = Theme.Border };
            cacheCard.Controls.AddRange(new Control[] { cacheCaption, cacheRate, cacheWeight, cacheTotalsCaption, cacheTotals, cacheCoverage, cacheScope, cacheDivider });
            cacheFrame.Controls.Add(cacheCard);
            Action layoutCache = delegate {
                float dpi; using (var g = CreateGraphics()) dpi = g.DpiX / 96F;
                Func<int, int> px = v => (int)Math.Round(v * dpi);
                int rightWidth = Math.Max(px(200), cacheCard.ClientSize.Width - px(287));
                cacheCaption.SetBounds(px(22), px(15), px(208), px(23));
                cacheRate.SetBounds(px(18), px(38), px(214), px(58));
                cacheWeight.SetBounds(px(23), px(100), px(206), px(22));
                cacheDivider.SetBounds(px(244), px(22), px(1), px(96));
                cacheTotalsCaption.SetBounds(px(266), px(19), rightWidth, px(24));
                cacheTotals.SetBounds(px(264), px(48), rightWidth, px(32));
                cacheCoverage.SetBounds(px(266), px(94), rightWidth, px(27));
                cacheScope.SetBounds(px(22), px(137), Math.Max(px(200), cacheCard.ClientSize.Width - px(44)), px(34));
            };
            cacheCard.Resize += delegate { layoutCache(); };
            Shown += delegate { layoutCache(); };
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 202, BackColor = Theme.Card };
            detail = new Label { Dock = DockStyle.Fill, Padding = new Padding(28, 17, 28, 0), ForeColor = Theme.Ink, Text = "选中一条记录，查看输入、缓存、输出和推理明细。" };
            source = new Label { Dock = DockStyle.Bottom, Height = 56, Padding = new Padding(28, 3, 20, 0), ForeColor = Theme.Muted, Text = "命中率 = 缓存命中输入 ÷ 输入（缓存已包含在输入）；输出 Token 不参与计算。\n每条记录对应一轮，子任务另列。本轮 Token 不能换算成独占的账户额度百分比。" };
            footer.Controls.Add(detail); footer.Controls.Add(source);
            var frame = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 0, 28, 18), BackColor = Theme.Background };
            grid = new DataGridView {
                Dock = DockStyle.Fill, BackgroundColor = Theme.Card, BorderStyle = BorderStyle.None,
                AllowUserToAddRows = false, AllowUserToDeleteRows = false, ReadOnly = true,
                RowHeadersVisible = false, MultiSelect = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AllowUserToResizeRows = false, AutoGenerateColumns = false, EnableHeadersVisualStyles = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal,
                ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None, GridColor = Theme.Border,
                ColumnHeadersHeight = 40, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
            };
            grid.DefaultCellStyle = new DataGridViewCellStyle { ForeColor = Theme.Ink, BackColor = Theme.Card, SelectionBackColor = Theme.PaleGreen, SelectionForeColor = Theme.Ink, Padding = new Padding(6, 0, 6, 0) };
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Theme.Background, ForeColor = Theme.Muted, Font = new Font(Font, FontStyle.Regular), Padding = new Padding(6, 0, 6, 0) };
            grid.RowTemplate.Height = 44;
            AddColumn("time", "完成时间", 122, 110);
            AddColumn("task", "任务", 220, 150);
            AddColumn("turn", "本轮 Token", 114, 100);
            AddColumn("cache", "缓存命中率", 115, 100);
            AddColumn("total", "会话累计", 115, 100);
            AddColumn("status", "状态", 102, 85);
            grid.Columns["cache"].DefaultCellStyle.ForeColor = Theme.Green;
            grid.Columns["cache"].DefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.Columns["cache"].HeaderCell.Style.Alignment = DataGridViewContentAlignment.MiddleRight;
            grid.SelectionChanged += delegate { ShowDetail(); };
            frame.Controls.Add(grid);
            Controls.Add(frame); Controls.Add(footer); Controls.Add(cacheFrame); Controls.Add(header);
            ResumeLayout(false);
        }
        private void AddColumn(string name, string title, float weight, int minimum)
        {
            grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, FillWeight = weight, MinimumWidth = minimum, SortMode = DataGridViewColumnSortMode.NotSortable });
        }
        internal void SetUpdate(TaskUsageUpdate update, bool notificationsEnabled)
        {
            if (update == null) return;
            applying = true; notify.Checked = notificationsEnabled; applying = false;
            var records = update.Records ?? new List<TaskUsageRecord>();
            int roots = records.Count(r => String.IsNullOrEmpty(r.ParentThreadId));
            summary.Text = "最近 " + records.Count + " 轮记录 · 主任务 " + roots + " 轮 · " + update.Status;
            CacheUsageSummary cache = CacheUsageStatistics.Summarize(records);
            cacheRate.Text = cache.HitPercent.HasValue ? cache.HitPercent.Value.ToString("0.0", CultureInfo.InvariantCulture) + "%" : "未知";
            cacheTotals.Text = cache.CachedTokens.ToString("N0", CultureInfo.InvariantCulture) + " / " + cache.InputTokens.ToString("N0", CultureInfo.InvariantCulture);
            cacheCoverage.Text = "有效 " + cache.IncludedRecords + " 轮 · 未知 " + cache.MissingRecords + " 轮 · 零输入 " + cache.ZeroInputRecords + " 轮";
            cacheRate.AccessibleDescription = "近期缓存命中率，按输入 Token 加权；" + cacheCoverage.Text;
            string current = String.Join("|", records.Select(r => r.Key + ":" +
                (r.Usage == null ? "?" : String.Join(",", new long?[] { r.Usage.Total, r.Usage.Input, r.Usage.CachedInput, r.Usage.CacheWriteInput, r.Usage.Output, r.Usage.Reasoning }.Select(v => v.ToString()).ToArray())) + ":" +
                (r.Cumulative == null ? "?" : r.Cumulative.Total.ToString()) + ":" + r.Status + ":" + r.Title + ":" + r.Model + ":" + r.ScopeNote + ":" + r.IsPartial).ToArray());
            if (current == fingerprint) return;
            fingerprint = current;
            string selected = grid.SelectedRows.Count == 0 ? null : ((TaskUsageRecord)grid.SelectedRows[0].Tag).Key;
            grid.Rows.Clear();
            foreach (var record in records)
            {
                int row = grid.Rows.Add(record.CompletedAtUtc == DateTime.MinValue ? "时间未知" : record.CompletedAtUtc.ToLocalTime().ToString("MM/dd HH:mm:ss"), TaskUsageText.Title(record), TaskUsageText.Count(record.Usage == null ? null : record.Usage.Total), CacheUsageStatistics.FormatPercent(record.Usage), TaskUsageText.Count(record.Cumulative == null ? null : record.Cumulative.Total), TaskUsageText.Status(record));
                grid.Rows[row].Tag = record;
                grid.Rows[row].Cells[1].ToolTipText = TaskUsageText.Title(record);
                grid.Rows[row].Cells["cache"].ToolTipText = "缓存命中输入 ÷ 输入；缓存已包含在输入，不包含输出 Token。";
                if (record.IsPartial) grid.Rows[row].DefaultCellStyle.ForeColor = Theme.Danger;
                if (record.Key == selected) { grid.Rows[row].Selected = true; grid.CurrentCell = grid.Rows[row].Cells[0]; }
            }
            ShowDetail();
        }
        private void ShowDetail()
        {
            if (grid.SelectedRows.Count == 0 || !(grid.SelectedRows[0].Tag is TaskUsageRecord))
            {
                detail.Text = grid.Rows.Count == 0 ? "暂无已结束的本机任务。监控运行后，新完成的轮次会自动出现在这里。" : "选中一条记录，查看明细。";
                return;
            }
            var record = (TaskUsageRecord)grid.SelectedRows[0].Tag;
            var usage = record.Usage ?? new TokenCounts();
            string duration = record.StartedAtUtc > DateTime.MinValue && record.CompletedAtUtc >= record.StartedAtUtc ? " · 用时 " + (record.CompletedAtUtc - record.StartedAtUtc).TotalMinutes.ToString("0.#") + " 分钟" : "";
            detail.Text = TaskUsageText.Title(record) + "\n" +
                "缓存命中率  " + CacheUsageStatistics.FormatPercent(usage) + "    输入  " + TaskUsageText.Count(usage.Input) + "    其中缓存命中  " + TaskUsageText.Count(usage.CachedInput) + "\n" +
                "输出  " + TaskUsageText.Count(usage.Output) + "    其中推理  " + TaskUsageText.Count(usage.Reasoning) + "\n" +
                "模型  " + (String.IsNullOrEmpty(record.Model) ? "未提供" : record.Model) + duration + "\n" +
                (String.IsNullOrEmpty(record.ScopeNote) ? "统计本会话；子任务另列。缓存、推理明细不重复加入总数。" : record.ScopeNote);
        }
    }
}
