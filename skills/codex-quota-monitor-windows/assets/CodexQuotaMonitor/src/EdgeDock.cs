using System;
using System.Drawing;

namespace CodexQuotaMonitor
{
    public enum EdgeSide { Left, Right }

    // Geometry uses a monitor's working area, so the taskbar is excluded.
    public static class EdgeDock
    {
        public const int CollapsedWidth = 80;
        public const int CollapsedHeight = 80;
        public const int ExpandedWidth = 248;
        public const int ExpandedHeight = 184;

        public static Rectangle Bounds(Rectangle workArea, EdgeSide side, double fraction, bool expanded, float scale)
        {
            if (Single.IsNaN(scale)) scale = 1F;
            scale = Math.Max(.75F, Math.Min(4F, scale));
            if (Double.IsNaN(fraction)) fraction = .35;
            fraction = Math.Max(0, Math.Min(1, fraction));

            int areaWidth = Math.Max(0, workArea.Width);
            int areaHeight = Math.Max(0, workArea.Height);
            int width = Math.Min(areaWidth, Scaled(expanded ? ExpandedWidth : CollapsedWidth, scale));
            int height = Math.Min(areaHeight, Scaled(expanded ? ExpandedHeight : CollapsedHeight, scale));
            int tabHeight = Math.Min(areaHeight, Scaled(CollapsedHeight, scale));

            // Anchor the expanded panel to the collapsed tab's center rather than
            // giving the two sizes independent fractions of their travel range.
            double center = (double)workArea.Top + tabHeight / 2.0 + (areaHeight - tabHeight) * fraction;
            double top = Math.Round(center - height / 2.0, MidpointRounding.AwayFromZero);
            top = Math.Max(workArea.Top, Math.Min((double)workArea.Top + areaHeight - height, top));
            double left = side == EdgeSide.Left ? workArea.Left : (double)workArea.Left + areaWidth - width;
            return new Rectangle(ToCoordinate(left), ToCoordinate(top), width, height);
        }

        public static EdgeSide NearestSide(Rectangle workArea, Point point)
        {
            double leftDistance = Math.Abs((double)point.X - workArea.Left);
            double rightDistance = Math.Abs((double)point.X - ((double)workArea.Left + Math.Max(0, workArea.Width)));
            return leftDistance < rightDistance ? EdgeSide.Left : EdgeSide.Right;
        }

        private static int Scaled(int logicalSize, float scale)
        {
            return (int)Math.Round(logicalSize * (double)scale, MidpointRounding.AwayFromZero);
        }

        private static int ToCoordinate(double value)
        {
            return (int)Math.Max(Int32.MinValue, Math.Min(Int32.MaxValue, value));
        }
    }

    public static class QuotaColors
    {
        private enum State { Unknown, Stale, Low, Watch, Plenty }

        public static Color ColorFor(double? remaining, bool stale, int lowThreshold = 20)
        {
            switch (Classify(remaining, stale, lowThreshold))
            {
                case State.Low: return Color.FromArgb(0xD8, 0x58, 0x4C);
                case State.Watch: return Color.FromArgb(0xE3, 0xA2, 0x3C);
                case State.Plenty: return Color.FromArgb(0x24, 0x91, 0x6C);
                default: return Color.FromArgb(0x84, 0x90, 0x8B);
            }
        }

        public static string LabelFor(double? remaining, bool stale, int lowThreshold = 20)
        {
            switch (Classify(remaining, stale, lowThreshold))
            {
                case State.Stale: return "数据待更新";
                case State.Low: return "额度偏低";
                case State.Watch: return "留意用量";
                case State.Plenty: return "额度充足";
                default: return "额度未知";
            }
        }

        private static State Classify(double? remaining, bool stale, int lowThreshold)
        {
            if (stale) return State.Stale;
            if (!remaining.HasValue || Double.IsNaN(remaining.Value) || Double.IsInfinity(remaining.Value)) return State.Unknown;
            double value = Math.Max(0, Math.Min(100, remaining.Value));
            int threshold = Math.Max(0, Math.Min(100, lowThreshold));
            if (value <= threshold) return State.Low;
            if (value <= Math.Max(50, threshold)) return State.Watch;
            return State.Plenty;
        }
    }
}
