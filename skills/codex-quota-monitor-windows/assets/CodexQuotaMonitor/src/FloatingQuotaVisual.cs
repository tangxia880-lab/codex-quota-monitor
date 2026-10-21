using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;

namespace CodexQuotaMonitor
{
    internal static class FloatingQuotaVisual
    {
        // Rasterize the complete transparent surface at 4x, then filter down.
        // The resulting premultiplied alpha also antialiases the HWND outline.
        internal static Bitmap Render(Size pixels, float scale, bool expanded, EdgeSide side, FloatingQuotaState state, int hover, int pressed)
        {
            const int supersample = 4;
            int width = Math.Max(1, pixels.Width), height = Math.Max(1, pixels.Height);
            using (var large = new Bitmap(width * supersample, height * supersample, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(large))
                {
                    g.Clear(Color.Transparent);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.ScaleTransform(scale * supersample, scale * supersample);
                    Draw(g, width / scale, height / scale, expanded, side, state, hover, pressed);
                }
                var output = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                using (var g = Graphics.FromImage(output))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(large, new Rectangle(0, 0, width, height), 0, 0, large.Width, large.Height, GraphicsUnit.Pixel);
                }
                return output;
            }
        }
        private static void Border(Graphics g, RectangleF bounds, float radius, Color color, float width)
        {
            using (var path = Theme.Round(bounds, radius)) using (var pen = new Pen(color, width)) g.DrawPath(pen, path);
        }
        private static void Shadow(Graphics g, RectangleF rect, float radius, float spread)
        {
            // Small translucent falloff rings leave the corners fully transparent.
            for (int i = 5; i >= 1; i--)
            {
                float grow = i * spread / 5F;
                var ring = new RectangleF(rect.X - grow, rect.Y - grow + .65F, rect.Width + 2 * grow, rect.Height + 2 * grow);
                Border(g, ring, radius + grow, Color.FromArgb((int)(14 - i * 1.8), 16, 35, 28), .65F);
            }
        }
        private static void Label(Graphics g, string value, string face, float size, FontStyle weight, Color color, RectangleF bounds)
        {
            using (var font = new Font(face, size, weight, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
                g.DrawString(value ?? String.Empty, font, brush, bounds, format);
        }
        private static void Draw(Graphics g, float width, float height, bool expanded, EdgeSide side, FloatingQuotaState state, int hover, int pressed)
        {
            bool known = state.Remaining.HasValue && !Double.IsNaN(state.Remaining.Value) && !Double.IsInfinity(state.Remaining.Value);
            float percentage = known ? (float)Math.Max(0, Math.Min(100, state.Remaining.Value)) : 0;
            if (!expanded)
            {
                // Same 72px blue-purple floating ball as the Mac desktop widget.
                var body = new RectangleF((width - 72) / 2, (height - 72) / 2, 72, 72);
                Shadow(g, body, 36, 2.5F);
                using (var fill = new LinearGradientBrush(body, Theme.BallBlue, Theme.BallPurple, LinearGradientMode.ForwardDiagonal))
                    g.FillEllipse(fill, body);
                string value = known ? percentage.ToString("0", CultureInfo.InvariantCulture) + "%" : "—";
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                using (var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel))
                    g.DrawString(value, font, Brushes.White, new RectangleF(body.X, body.Y + 19, 72, 22), format);
                using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
                using (var font = new Font("Microsoft YaHei UI", 10, FontStyle.Regular, GraphicsUnit.Pixel))
                using (var brush = new SolidBrush(Color.FromArgb(217, Color.White)))
                    g.DrawString(state.Stale ? "待更新" : "剩余额度", font, brush, new RectangleF(body.X, body.Y + 40, 72, 16), format);
                return;
            }
            var card = new RectangleF(4, 4, width - 8, height - 8);
            Shadow(g, card, 17, 2.7F);
            using (var path = Theme.Round(card, 17))
            using (var fill = new LinearGradientBrush(card, Theme.Background, Theme.Background, LinearGradientMode.Vertical))
                g.FillPath(fill, path);
            Border(g, new RectangleF(4.5F, 4.5F, width - 9, height - 9), 16.5F, Theme.Border, .8F);
            Theme.Box(g, new RectangleF(side == EdgeSide.Right ? width - 7 : 5, 25, 2, height - 50), 1, Color.FromArgb(160, Theme.Green));
            using (var brush = new SolidBrush(Theme.Green)) g.FillEllipse(brush, 18, 20, 6, 6);
            Label(g, "Codex", "Segoe UI", 13, FontStyle.Bold, Theme.Ink, new RectangleF(30, 13, 75, 24));
            Label(g, "额度", "Microsoft YaHei UI", 10.5F, FontStyle.Regular, Theme.Muted, new RectangleF(77, 15, 62, 23));
            if (hover == 3 || pressed == 3) Theme.Box(g, new RectangleF(189, 10, 24, 25), 7, Theme.PaleGreen);
            if (hover == 4 || pressed == 4) Theme.Box(g, new RectangleF(215, 10, 22, 25), 7, Theme.PaleGreen);
            using (var pen = new Pen(Theme.Muted, 1.2F) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                g.DrawArc(pen, new RectangleF(196, 17, 10, 10), 25, 285);
                g.DrawLines(pen, new[] { new PointF(202, 16.5F), new PointF(205.5F, 16.5F), new PointF(205.5F, 20) });
                g.DrawLine(pen, 222, 22, 230, 22);
            }
            string number = known ? percentage.ToString("0.#", CultureInfo.InvariantCulture) + "%" : "—";
            Label(g, number, "Segoe UI", 38, FontStyle.Bold, state.Stale ? Theme.Muted : Theme.Green, new RectangleF(14, 38, 140, 55));
            Label(g, QuotaColors.LabelFor(state.Remaining, state.Stale, state.LowThreshold), "Microsoft YaHei UI", 10.5F, FontStyle.Regular, Theme.Muted, new RectangleF(148, 63, 90, 24));
            Label(g, state.ResetText, "Microsoft YaHei UI", 10.5F, FontStyle.Regular, Theme.Muted, new RectangleF(18, 94, 214, 21));
            bool showCache = !String.IsNullOrEmpty(state.LastCacheText);
            Label(g, state.LastTaskText, "Microsoft YaHei UI", 10.5F, FontStyle.Regular, Theme.Ink, new RectangleF(18, 114, showCache ? 120 : 214, 21));
            if (showCache) Label(g, state.LastCacheText, "Microsoft YaHei UI", 10.5F, FontStyle.Regular, Theme.Muted, new RectangleF(144, 114, 90, 21));
            var primary = new RectangleF(16, 139, 94, 30);
            Color primaryColor = pressed == 1 ? Theme.BallPurple : hover == 1 ? Theme.BallBlue : Theme.Green;
            Theme.Box(g, primary, 9, primaryColor);
            Border(g, new RectangleF(16.5F, 139.5F, 93, 29), 8.5F, Color.FromArgb(55, Color.White), .6F);
            Label(g, "额度详情", "Microsoft YaHei UI", 10.5F, FontStyle.Regular, Color.White, new RectangleF(39, 147, 69, 20));
            var secondary = new RectangleF(118, 139, 114, 30);
            Theme.Box(g, secondary, 9, pressed == 2 ? Theme.Border : hover == 2 ? Theme.PaleGreen : Theme.Card);
            Border(g, new RectangleF(118.5F, 139.5F, 113, 29), 8.5F, Color.FromArgb(110, Color.White), .7F);
            Label(g, "任务用量", "Microsoft YaHei UI", 10.5F, FontStyle.Regular, Theme.Ink, new RectangleF(151, 147, 80, 20));
        }
    }
}
