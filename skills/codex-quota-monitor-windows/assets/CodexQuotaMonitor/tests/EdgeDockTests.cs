using System;
using System.Drawing;
using CodexQuotaMonitor;

internal static class EdgeDockTests
{
    private static int assertions;

    private static void Check(bool pass, string message)
    {
        assertions++;
        if (!pass) throw new Exception("FAIL: " + message);
    }

    private static bool Within(Rectangle outer, Rectangle inner)
    {
        return inner.Width >= 0 && inner.Height >= 0 &&
            inner.Left >= outer.Left && inner.Top >= outer.Top &&
            (long)inner.Left + inner.Width <= (long)outer.Left + Math.Max(0, outer.Width) &&
            (long)inner.Top + inner.Height <= (long)outer.Top + Math.Max(0, outer.Height);
    }

    private static void CheckColor(double? remaining, bool stale, int threshold, int rgb, string label)
    {
        Color actual = QuotaColors.ColorFor(remaining, stale, threshold);
        Check(actual.ToArgb() == (unchecked((int)0xFF000000) | rgb), "color for " + remaining + ", stale " + stale + ", threshold " + threshold);
        Check(QuotaColors.LabelFor(remaining, stale, threshold) == label, "label matches color for " + remaining + ", stale " + stale + ", threshold " + threshold);
    }

    private static void TestColors()
    {
        CheckColor(null, false, 20, 0x84908B, "额度未知");
        CheckColor(Double.NaN, false, 20, 0x84908B, "额度未知");
        CheckColor(Double.PositiveInfinity, false, 20, 0x84908B, "额度未知");
        CheckColor(Double.NegativeInfinity, false, 20, 0x84908B, "额度未知");
        CheckColor(90, true, 20, 0x84908B, "数据待更新");
        CheckColor(5, true, 20, 0x84908B, "数据待更新");
        CheckColor(null, true, 20, 0x84908B, "数据待更新");
        CheckColor(0, false, 20, 0xD8584C, "额度偏低");
        CheckColor(20, false, 20, 0xD8584C, "额度偏低");
        CheckColor(20.01, false, 20, 0xE3A23C, "留意用量");
        CheckColor(50, false, 20, 0xE3A23C, "留意用量");
        CheckColor(50.01, false, 20, 0x24916C, "额度充足");
        CheckColor(100, false, 20, 0x24916C, "额度充足");
        CheckColor(-100, false, 20, 0xD8584C, "额度偏低");
        CheckColor(300, false, 20, 0x24916C, "额度充足");
        CheckColor(60, false, 60, 0xD8584C, "额度偏低");
        CheckColor(60.01, false, 60, 0x24916C, "额度充足");
        CheckColor(30, false, 30, 0xD8584C, "额度偏低");
        CheckColor(30.01, false, 30, 0xE3A23C, "留意用量");
        CheckColor(0, false, -10, 0xD8584C, "额度偏低");
        CheckColor(1, false, -10, 0xE3A23C, "留意用量");
        CheckColor(100, false, 200, 0xD8584C, "额度偏低");
        Check(QuotaColors.ColorFor(10, false) == QuotaColors.ColorFor(10, false, 20), "color default threshold is 20");
        Check(QuotaColors.LabelFor(10, false) == QuotaColors.LabelFor(10, false, 20), "label default threshold is 20");
    }

    private static void TestGeometry()
    {
        Rectangle primary = new Rectangle(0, 0, 1920, 1040);
        Rectangle collapsed = EdgeDock.Bounds(primary, EdgeSide.Right, .5, false, 1);
        Rectangle expanded = EdgeDock.Bounds(primary, EdgeSide.Right, .5, true, 1);
        Check(collapsed.Size == new Size(80, 80), "collapsed size uses requested logical dimensions");
        Check(expanded.Size == new Size(248, 184), "expanded size uses requested logical dimensions");
        Check(collapsed.Right == primary.Right && expanded.Right == primary.Right, "right docking touches working-area edge");
        Check(collapsed.Top + collapsed.Height / 2 == expanded.Top + expanded.Height / 2, "expanded panel retains tab center in open space");
        Check(EdgeDock.Bounds(primary, EdgeSide.Left, .5, false, 1).Left == 0, "left docking touches working-area edge");
        Check(EdgeDock.Bounds(primary, EdgeSide.Right, 0, false, 1).Top == primary.Top, "collapsed fraction zero reaches top");
        Check(EdgeDock.Bounds(primary, EdgeSide.Right, 1, false, 1).Bottom == primary.Bottom, "collapsed fraction one reaches bottom");
        Check(EdgeDock.Bounds(primary, EdgeSide.Right, 0, true, 1).Top == primary.Top, "expanded top clamps into work area");
        Check(EdgeDock.Bounds(primary, EdgeSide.Right, 1, true, 1).Bottom == primary.Bottom, "expanded bottom clamps into work area");
        Check(EdgeDock.Bounds(primary, EdgeSide.Right, Double.NaN, false, 1) == EdgeDock.Bounds(primary, EdgeSide.Right, .35, false, 1), "NaN fraction uses default .35");
        Check(EdgeDock.Bounds(primary, EdgeSide.Right, Double.NegativeInfinity, false, 1) == EdgeDock.Bounds(primary, EdgeSide.Right, 0, false, 1), "negative infinite fraction clamps to top");
        Check(EdgeDock.Bounds(primary, EdgeSide.Right, Double.PositiveInfinity, false, 1) == EdgeDock.Bounds(primary, EdgeSide.Right, 1, false, 1), "positive infinite fraction clamps to bottom");
        Check(EdgeDock.Bounds(primary, EdgeSide.Right, .35, false, Single.NaN) == EdgeDock.Bounds(primary, EdgeSide.Right, .35, false, 1), "NaN scale defaults to 1");
        Check(EdgeDock.Bounds(primary, EdgeSide.Right, .35, false, 0).Size == new Size(60, 60), "small scale clamps to .75");
        Check(EdgeDock.Bounds(primary, EdgeSide.Right, .35, false, 100).Size == new Size(320, 320), "large scale clamps to 4");
        Check(EdgeDock.Bounds(primary, EdgeSide.Right, .35, true, 1.5F).Size == new Size(372, 276), "150 percent DPI sizes correctly");

        Rectangle[] areas = {
            primary, new Rectangle(-1920, -240, 1920, 1040), new Rectangle(1920, 120, 1440, 860),
            new Rectangle(-3840, -1080, 1920, 1040), new Rectangle(-8, -5, 10, 8),
            new Rectangle(30, 40, 1, 1), new Rectangle(-20, -20, 0, 0)
        };
        double[] fractions = { -1, 0, .35, .5, 1, 2, Double.NaN };
        float[] scales = { .5F, 1, 1.25F, 2, 4, 9, Single.NaN, Single.PositiveInfinity, Single.NegativeInfinity };
        foreach (Rectangle area in areas)
            foreach (EdgeSide side in new[] { EdgeSide.Left, EdgeSide.Right })
                foreach (bool open in new[] { false, true })
                    foreach (double fraction in fractions)
                        foreach (float scale in scales)
                        {
                            Rectangle actual = EdgeDock.Bounds(area, side, fraction, open, scale);
                            Check(Within(area, actual), "bounds stay in " + area + " side " + side + " expanded " + open + " fraction " + fraction + " scale " + scale);
                            Check(actual.Width <= Math.Max(0, area.Width) && actual.Height <= Math.Max(0, area.Height), "tiny work area constrains size");
                        }

        Rectangle negative = new Rectangle(-1920, -100, 1920, 1040);
        Check(EdgeDock.NearestSide(primary, new Point(1, 500)) == EdgeSide.Left, "nearest left edge");
        Check(EdgeDock.NearestSide(primary, new Point(1919, 500)) == EdgeSide.Right, "nearest right edge");
        Check(EdgeDock.NearestSide(primary, new Point(960, -500)) == EdgeSide.Right, "equidistant tie chooses right independent of y");
        Check(EdgeDock.NearestSide(negative, new Point(-1900, 0)) == EdgeSide.Left, "negative monitor left edge");
        Check(EdgeDock.NearestSide(negative, new Point(-5, 0)) == EdgeSide.Right, "negative monitor right edge");
        Check(EdgeDock.NearestSide(primary, new Point(-500, 0)) == EdgeSide.Left, "point outside left still chooses nearest edge");
        Check(EdgeDock.NearestSide(primary, new Point(5000, 0)) == EdgeSide.Right, "point outside right still chooses nearest edge");
    }

    private static int Main()
    {
        try
        {
            TestColors(); TestGeometry();
            Console.WriteLine("PASS: " + assertions + " offline screen-edge geometry and quota-color assertions.");
            return 0;
        }
        catch (Exception error) { Console.WriteLine(error.ToString()); return 1; }
    }
}
