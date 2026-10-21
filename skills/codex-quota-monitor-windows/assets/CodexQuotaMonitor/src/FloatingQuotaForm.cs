using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace CodexQuotaMonitor
{
    internal sealed class FloatingQuotaState
    {
        internal double? Remaining;
        internal bool Stale;
        internal int LowThreshold = 20;
        internal string ResetText = "正在读取额度…";
        internal string UpdateText = "等待首次同步";
        internal string LastTaskText = "完成任务后显示本轮用量";
        internal string LastCacheText = String.Empty;
    }

    internal sealed class FloatingQuotaForm : Form
    {
        private readonly System.Windows.Forms.Timer hoverTimer = new System.Windows.Forms.Timer();
        private readonly ToolTip tip = new ToolTip { AutoPopDelay = 12000 };
        private readonly ContextMenuStrip popup = new ContextMenuStrip();
        private FloatingQuotaState state = new FloatingQuotaState();
        private bool expanded, mouseDown, dragging, reallyClosing;
        private bool presenting;
        private int hoverAction;
        private int pressedAction;
        private Point downPoint, downLocation;
        private DateTime nextScreenCheckUtc = DateTime.MinValue;
        private Rectangle lastWorkArea;
        private float displayScale = 1;
        private readonly bool diagnostic;
        internal string DockedScreenName { get; private set; }
        internal EdgeSide DockedSide { get; private set; }
        internal double DockFraction { get; private set; }
        internal bool IsExpanded { get { return expanded; } }
        internal bool IsDragging { get { return dragging; } }
        internal event EventHandler PlacementChanged;
        internal event EventHandler DashboardRequested;
        internal event EventHandler TasksRequested;
        internal event EventHandler RefreshRequested;
        internal event EventHandler HideRequested;
        internal event EventHandler ExitRequested;

        internal FloatingQuotaForm(string screenName, EdgeSide side, double fraction, bool diagnostic = false)
        {
            this.diagnostic = diagnostic;
            DockedScreenName = screenName;
            DockedSide = side;
            DockFraction = Double.IsNaN(fraction) || Double.IsInfinity(fraction) ? .35 : Math.Max(0, Math.Min(1, fraction));
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Theme.Background;
            DoubleBuffered = false; // UpdateLayeredWindow presents one complete alpha frame.
            ResizeRedraw = true;
            Text = "Codex 额度悬浮窗";
            AccessibleName = "Codex 剩余额度；单击展开／收起，拖动移动，右键更多操作";
            Cursor = Cursors.Hand;
            popup.BackColor = Theme.Background;
            popup.ForeColor = Theme.Ink;
            popup.Renderer = new ToolStripProfessionalRenderer(new QuotaMenuColors());
            popup.Items.Add("展开 / 收起", null, delegate { SetExpanded(!expanded); });
            popup.Items.Add("打开完整额度面板", null, delegate { Raise(DashboardRequested); Collapse(); });
            popup.Items.Add("查看任务用量", null, delegate { Raise(TasksRequested); Collapse(); });
            popup.Items.Add("立即刷新", null, delegate { Raise(RefreshRequested); });
            popup.Items.Add(new ToolStripSeparator());
            popup.Items.Add("隐藏悬浮窗", null, delegate { Raise(HideRequested); });
            popup.Items.Add("退出监控", null, delegate { Raise(ExitRequested); });
            hoverTimer.Interval = 100;
            hoverTimer.Tick += delegate { CheckPointer(); };
            Shown += delegate { RefreshSurface(); if (!diagnostic) hoverTimer.Start(); };
            VisibleChanged += delegate { if (!Visible) hoverTimer.Stop(); else if (!diagnostic) hoverTimer.Start(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e) {
                if (!reallyClosing && e.CloseReason == CloseReason.UserClosing)
                { e.Cancel = true; Raise(HideRequested); Hide(); }
            };
            using (var g = CreateGraphics()) displayScale = g.DpiX / 96F;
            Reposition();
            UpdateTooltip();
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000 | 0x00000080 | 0x00080000; // NOACTIVATE, TOOLWINDOW, LAYERED.
                return parameters;
            }
        }
        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0021) { message.Result = new IntPtr(3); return; } // MA_NOACTIVATE, keep the click.
            base.WndProc(ref message);
        }
        internal void ApplyState(FloatingQuotaState value)
        {
            if (value == null) return;
            bool changed = !Object.Equals(state.Remaining, value.Remaining) || state.Stale != value.Stale || state.LowThreshold != value.LowThreshold ||
                state.ResetText != value.ResetText || state.LastTaskText != value.LastTaskText || state.LastCacheText != value.LastCacheText;
            state = value;
            UpdateTooltip();
            if (changed) RefreshSurface();
        }
        internal void ShowCollapsed()
        {
            CancelDrag();
            SetExpanded(false);
            Show();
        }
        internal void Collapse() { SetExpanded(false); }
        internal void SetExpanded(bool value)
        {
            if (dragging) return;
            expanded = value;
            hoverAction = 0;
            Reposition();
        }
        internal void CloseForExit() { reallyClosing = true; Close(); }
        private void Raise(EventHandler handler) { if (handler != null) handler(this, EventArgs.Empty); }
        private void CancelDrag()
        {
            mouseDown = dragging = false; pressedAction = 0;
            Capture = false; Cursor = Cursors.Hand;
        }

        private Screen DockScreen()
        {
            return Screen.AllScreens.FirstOrDefault(s => s.DeviceName == DockedScreenName) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
        }
        private void Reposition()
        {
            var screen = DockScreen();
            DockedScreenName = screen.DeviceName;
            lastWorkArea = screen.WorkingArea;
            Bounds = EdgeDock.Bounds(lastWorkArea, DockedSide, DockFraction, expanded, displayScale);
            RefreshSurface();
        }
        internal Bitmap RenderFrame()
        {
            return FloatingQuotaVisual.Render(ClientSize, displayScale, expanded, DockedSide, state, hoverAction, mouseDown ? pressedAction : 0);
        }
        private void RefreshSurface()
        {
            if (presenting || !IsHandleCreated || IsDisposed || Disposing || Width < 1 || Height < 1) return;
            presenting = true;
            try
            {
                using (var frame = RenderFrame()) LayeredWindowSurface.Present(Handle, frame, Location);
            }
            finally { presenting = false; }
        }
        private void CheckPointer()
        {
            if (!Visible || IsDisposed || mouseDown || dragging || popup.Visible) return;
            DateTime now = DateTime.UtcNow;
            if (now >= nextScreenCheckUtc)
            {
                nextScreenCheckUtc = now.AddSeconds(2);
                var screen = DockScreen();
                if (screen.DeviceName != DockedScreenName || screen.WorkingArea != lastWorkArea)
                { Reposition(); Raise(PlacementChanged); }
            }
            // Expansion is click-driven, matching the Mac floating ball.
        }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            mouseDown = true; dragging = false; Capture = true;
            downPoint = Control.MousePosition; downLocation = Location;
            pressedAction = expanded ? ActionAt(e.Location) : 0;
            if (pressedAction != 0) RefreshSurface();
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (mouseDown && pressedAction == 0)
            {
                Point point = Control.MousePosition;
                int dx = point.X - downPoint.X, dy = point.Y - downPoint.Y;
                if (Math.Abs(dx) > SystemInformation.DragSize.Width / 2 || Math.Abs(dy) > SystemInformation.DragSize.Height / 2) dragging = true;
                if (dragging) { Location = new Point(downLocation.X + dx, downLocation.Y + dy); Cursor = Cursors.SizeAll; }
            }
            else Cursor = Cursors.Hand;
            int hover = expanded && !dragging ? ActionAt(e.Location) : 0;
            if (hover != hoverAction) { hoverAction = hover; RefreshSurface(); }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { popup.Show(this, e.Location); return; }
            if (e.Button != MouseButtons.Left || !mouseDown) return;
            mouseDown = false; Capture = false;
            if (dragging)
            {
                dragging = false;
                SnapToScreen(Screen.FromPoint(Control.MousePosition), Control.MousePosition, Top + Height / 2D);
                Cursor = Cursors.Hand;
                SetExpanded(false);
                return;
            }
            int action = pressedAction;
            pressedAction = 0;
            if (action != 0 && action == ActionAt(e.Location))
            {
                if (action == 1) { Raise(DashboardRequested); Collapse(); }
                if (action == 2) { Raise(TasksRequested); Collapse(); }
                if (action == 3) Raise(RefreshRequested);
                if (action == 4) Collapse();
            }
            else if (action == 0) SetExpanded(!expanded);
            RefreshSurface();
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverAction != 0) { hoverAction = 0; RefreshSurface(); }
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && mouseDown)
            {
                mouseDown = false;
                if (dragging) { dragging = false; SnapToScreen(Screen.FromPoint(Control.MousePosition), Control.MousePosition, Top + Height / 2D); SetExpanded(false); }
                pressedAction = 0;
                Cursor = Cursors.Hand;
                RefreshSurface();
            }
        }
        private void SnapToScreen(Screen screen, Point point, double centerY)
        {
            DockedScreenName = screen.DeviceName;
            DockedSide = EdgeDock.NearestSide(screen.WorkingArea, point);
            double half = Math.Min(EdgeDock.CollapsedHeight * displayScale, screen.WorkingArea.Height) / 2D;
            double usable = Math.Max(0, screen.WorkingArea.Height - 2 * half);
            DockFraction = usable <= 0 ? .5 : Math.Max(0, Math.Min(1, (centerY - screen.WorkingArea.Top - half) / usable));
            Reposition();
            Raise(PlacementChanged);
        }
        private int ActionAt(Point point)
        {
            if (!expanded) return 0;
            float x = point.X / displayScale, y = point.Y / displayScale;
            if (new RectangleF(16, 139, 94, 30).Contains(x, y)) return 1;
            if (new RectangleF(118, 139, 114, 30).Contains(x, y)) return 2;
            if (new RectangleF(189, 9, 25, 28).Contains(x, y)) return 3;
            if (new RectangleF(214, 9, 25, 28).Contains(x, y)) return 4;
            return 0;
        }
        private void UpdateTooltip()
        {
            string value = state.Remaining.HasValue && !Double.IsNaN(state.Remaining.Value) && !Double.IsInfinity(state.Remaining.Value) ? Math.Max(0, Math.Min(100, state.Remaining.Value)).ToString("0.#") + "%" : "未知";
            string text = "Codex 剩余 " + value + " · " + QuotaColors.LabelFor(state.Remaining, state.Stale, state.LowThreshold) + "\n" + state.UpdateText + "\n单击展开／收起 · 拖动到屏幕左右边缘 · 右键更多操作";
            if (!String.IsNullOrEmpty(state.LastCacheText)) text += "\n最近一轮：" + state.LastCacheText + "；命中输入 ÷ 全部输入";
            if (tip.GetToolTip(this) != text) tip.SetToolTip(this, text);
        }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { RefreshSurface(); }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { hoverTimer.Stop(); hoverTimer.Dispose(); tip.Dispose(); popup.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
