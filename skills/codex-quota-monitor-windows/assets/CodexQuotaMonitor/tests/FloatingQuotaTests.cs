using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using CodexQuotaMonitor;

// Synthetic, diagnostic-mode UI only. No MonitorForm, Codex client, task
// sessions, tray icon, notification or settings file is opened by this test.
internal static class FloatingQuotaTests
{
    [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
    private static int assertions;
    private static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    private static object Get(object target, string name) { return target.GetType().GetField(name,Flags).GetValue(target); }
    private static void Set(object target, string name, object value) { target.GetType().GetField(name,Flags).SetValue(target,value); }
    private static object Invoke(object target, string name, params object[] arguments) { return target.GetType().GetMethod(name,Flags).Invoke(target,arguments); }
    private static void Check(bool pass,string name) { assertions++; if(!pass)throw new Exception("FAIL: "+name); }
    private static void Offscreen(FloatingQuotaForm form, bool expanded)
    {
        form.Hide();form.SetExpanded(expanded);form.Location=new Point(-30000,-30000);form.Show();Application.DoEvents();
    }
    private static Color Pixel(FloatingQuotaForm form,Point point)
    {
        using(var bitmap=form.RenderFrame())
        {
            return bitmap.GetPixel(point.X,point.Y);
        }
    }
    private static bool IsGray(Color color) { return Math.Max(color.R,Math.Max(color.G,color.B))-Math.Min(color.R,Math.Min(color.G,color.B))<=28; }
    private static bool IsHue(Color color,float minimum,float maximum) { return color.GetSaturation()>.25F && color.GetHue()>=minimum && color.GetHue()<=maximum; }
    private static int Distance(Color a,Color b) { return Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B))); }
    private static Color Composite(Color foreground,Color background)
    {
        double alpha=foreground.A/255D;
        return Color.FromArgb((int)Math.Round(foreground.R*alpha+background.R*(1-alpha)),(int)Math.Round(foreground.G*alpha+background.G*(1-alpha)),(int)Math.Round(foreground.B*alpha+background.B*(1-alpha)));
    }
    private static void CheckTransparency(Bitmap frame,string label)
    {
        Check(frame.PixelFormat==PixelFormat.Format32bppPArgb,label+" frame preserves premultiplied alpha");
        var corners=new[]{new Point(0,0),new Point(frame.Width-1,0),new Point(0,frame.Height-1),new Point(frame.Width-1,frame.Height-1)};
        foreach(var point in corners)Check(frame.GetPixel(point.X,point.Y).A==0,label+" corner is fully transparent "+point);
        int[] partialByQuarter=new int[4];
        int[] translucentEdgeByQuarter=new int[4];
        Point edge=Point.Empty;
        bool foundEdge=false;
        var alphaLevels=new System.Collections.Generic.HashSet<byte>();
        for(int y=0;y<frame.Height;y++)for(int x=0;x<frame.Width;x++)
        {
            Color pixel=frame.GetPixel(x,y);
            if(pixel.A>0 && pixel.A<255)
            {
                int quarter=(x<frame.Width/2?0:1)+(y<frame.Height/2?0:2);
                partialByQuarter[quarter]++;alphaLevels.Add(pixel.A);
                if(pixel.A>=40 && pixel.A<=215)
                {
                    translucentEdgeByQuarter[quarter]++;
                    if(!foundEdge){foundEdge=true;edge=new Point(x,y);}
                }
            }
        }
        for(int quarter=0;quarter<4;quarter++)
            Check(partialByQuarter[quarter]>2 && translucentEdgeByQuarter[quarter]>0,label+" rounded silhouette has intermediate coverage in quarter "+quarter);
        Check(alphaLevels.Count>=8,label+" silhouette includes smooth alpha levels instead of binary clipping");
        Check(frame.GetPixel(frame.Width/2,frame.Height/2).A==255,label+" panel body remains opaque");
        foreach(Color background in new[]{Color.FromArgb(247,249,246),Color.FromArgb(24,31,28)})
        {
            using(var flattened=new Bitmap(frame.Width,frame.Height,PixelFormat.Format32bppArgb))
            {
                using(var graphics=Graphics.FromImage(flattened))
                { graphics.Clear(background);graphics.CompositingMode=CompositingMode.SourceOver;graphics.DrawImageUnscaled(frame,Point.Empty); }
                foreach(var corner in corners)Check(flattened.GetPixel(corner.X,corner.Y).ToArgb()==background.ToArgb(),label+" transparent corner preserves backdrop "+background.R+" "+corner);
                Check(foundEdge && Distance(flattened.GetPixel(edge.X,edge.Y),Composite(frame.GetPixel(edge.X,edge.Y),background))<=2,label+" antialiased edge blends correctly on backdrop "+background.R);
            }
        }
    }
    private static void CheckUnicodeCoverage(FloatingQuotaForm form,float scale)
    {
        form.ApplyState(new FloatingQuotaState{Remaining=73.4,ResetText="",UpdateText="synthetic update",LastTaskText="本轮 1,234 Token"});
        using(var blank=form.RenderFrame())
        {
            form.ApplyState(new FloatingQuotaState{Remaining=73.4,ResetText="重置时间 · 1 小时后刷新",UpdateText="synthetic update",LastTaskText="本轮 1,234 Token"});
            using(var text=form.RenderFrame())
            {
                int changed=0,antialiased=0,fringed=0;
                for(int y=(int)(94*scale);y<(int)(114*scale);y++)for(int x=(int)(18*scale);x<(int)(232*scale);x++)
                {
                    Color before=blank.GetPixel(x,y),after=text.GetPixel(x,y);
                    if(Distance(before,after)<4)continue;
                    changed++;
                    double red=(before.R-after.R)/(double)(before.R-Theme.Muted.R);
                    double green=(before.G-after.G)/(double)(before.G-Theme.Muted.G);
                    double blue=(before.B-after.B)/(double)(before.B-Theme.Muted.B);
                    double coverage=(red+green+blue)/3;
                    if(coverage>.05 && coverage<.95)antialiased++;
                    // Grayscale antialiasing applies one coverage to all RGB
                    // channels. Allow integer/downsampling rounding, not ClearType fringes.
                    if(Math.Max(red,Math.Max(green,blue))-Math.Min(red,Math.Min(green,blue))>.045)fringed++;
                }
                Check(changed>80,"Unicode label renders at "+scale+"x");
                Check(antialiased>30,"Unicode glyphs have intermediate grayscale coverage at "+scale+"x");
                Check(fringed==0,"Unicode glyph coverage has no RGB color fringes at "+scale+"x (unexpected pixels "+fringed+")");
            }
        }
    }
    private static void CheckRenderScales(FloatingQuotaForm form)
    {
        float original=(float)Get(form,"displayScale");
        foreach(float scale in new[]{1F,1.5F})
        {
            Set(form,"displayScale",scale);
            foreach(bool expanded in new[]{false,true})
            {
                Offscreen(form,expanded);
                form.ApplyState(new FloatingQuotaState{Remaining=73.4,ResetText="4 天 12 小时后重置",LastTaskText="本轮 1,234 Token"});
                using(var frame=form.RenderFrame())
                {
                    Check(frame.Size==new Size((int)Math.Round((expanded?248:80)*scale),(int)Math.Round((expanded?184:80)*scale)),"render dimensions match "+scale+"x expanded "+expanded);
                    Check(form.Region==null,"no binary Form.Region clipping at "+scale+"x expanded "+expanded);
                    CheckTransparency(frame,scale+"x "+(expanded?"expanded":"collapsed"));
                }
            }
            CheckUnicodeCoverage(form,scale);
        }
        Set(form,"displayScale",original);
    }
    private static void SaveComparison(FloatingQuotaForm form,string path)
    {
        using(var atlas=new Bitmap(920,620,PixelFormat.Format32bppArgb))
        using(var g=Graphics.FromImage(atlas))
        using(var font=new Font("Segoe UI",12,FontStyle.Regular))
        {
            g.Clear(Color.FromArgb(247,249,246));
            using(var dark=new SolidBrush(Color.FromArgb(24,31,28)))g.FillRectangle(dark,460,0,460,620);
            form.ApplyState(new FloatingQuotaState{Remaining=73.4,ResetText="4 天 12 小时后重置",UpdateText="离线视觉测试",LastTaskText="本轮 1,234 Token"});
            foreach(float scale in new[]{1F,1.5F})
            {
                int top=scale==1?54:304;
                Set(form,"displayScale",scale);Offscreen(form,true);
                using(var expanded=form.RenderFrame())
                {
                    g.DrawImageUnscaled(expanded,24,top);g.DrawImageUnscaled(expanded,484,top);
                    g.DrawString(scale+"x",font,Brushes.DimGray,24,top-30);g.DrawString(scale+"x",font,Brushes.WhiteSmoke,484,top-30);
                }
                Offscreen(form,false);
                using(var collapsed=form.RenderFrame())
                {g.DrawImageUnscaled(collapsed,414,top+20);g.DrawImageUnscaled(collapsed,874,top+20);}
            }
            atlas.Save(path,ImageFormat.Png);
        }
    }
    private static void Click(FloatingQuotaForm form,int x,int y)
    {
        Offscreen(form,true);
        float scale=(float)Get(form,"displayScale");
        var point=new Point((int)(x*scale),(int)(y*scale));
        Invoke(form,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0));
        Invoke(form,"OnMouseUp",new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0));
        form.Location=new Point(-30000,-30000);
    }
    [STAThread] public static int Main(string[] args)
    {
        try
        {
            SetProcessDPIAware();
            System.Threading.Thread.CurrentThread.CurrentCulture=CultureInfo.InvariantCulture;
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using(var form=new FloatingQuotaForm(null,EdgeSide.Right,.35,true))
            {
                var timer=(System.Windows.Forms.Timer)Get(form,"hoverTimer");
                var popup=(ContextMenuStrip)Get(form,"popup");
                var tip=(ToolTip)Get(form,"tip");
                float scale=(float)Get(form,"displayScale");
                Check(!form.Visible,"constructor stays hidden");
                Check(!form.ShowInTaskbar && form.TopMost,"tool window is topmost and absent from taskbar");
                Check((bool)typeof(FloatingQuotaForm).GetProperty("ShowWithoutActivation",Flags).GetValue(form,null),"show does not activate");
                var create=(CreateParams)typeof(FloatingQuotaForm).GetProperty("CreateParams",Flags).GetValue(form,null);
                Check((create.ExStyle & 0x08000080)==0x08000080,"NOACTIVATE and TOOLWINDOW styles");
                Check((create.ExStyle & 0x00080000)!=0,"LAYERED style supports per-pixel alpha");
                Message message=Message.Create(form.Handle,0x0021,IntPtr.Zero,IntPtr.Zero);
                object[] arguments={message};Invoke(form,"WndProc",arguments);
                Check(((Message)arguments[0]).Result==new IntPtr(3),"WM_MOUSEACTIVATE preserves first click without activation");
                Check(!form.IsExpanded && form.Width==(int)Math.Round(80*scale) && form.Height==(int)Math.Round(80*scale),"collapsed logical size 80 by 80");
                Offscreen(form,true);
                Check(form.Width==(int)Math.Round(248*scale) && form.Height==(int)Math.Round(184*scale),"expanded logical size 248 by 184");
                Check(!timer.Enabled,"diagnostic mode starts no hover timer");
                var bounds=form.Bounds;
                var state=new FloatingQuotaState { Remaining=73.4,Stale=false,LowThreshold=20,UpdateText="synthetic update" };
                form.ApplyState(state);
                Check(form.Visible && form.IsExpanded && form.Bounds==bounds,"snapshot update preserves visible expanded bounds");
                Check(tip.GetToolTip(form).Contains("73.4%"),"exact percentage is represented in tooltip");
                Check(Get(form,"state")==state,"state keeps exact numeric percentage");
                form.Hide();bounds=form.Bounds;
                form.ApplyState(new FloatingQuotaState { Remaining=11 });
                Check(!form.Visible && form.IsExpanded && form.Bounds==bounds,"snapshot update preserves hidden state and bounds");

                Offscreen(form,false);
                form.ApplyState(new FloatingQuotaState { Remaining=null });
                Point sample=new Point(Math.Max(1,(int)(12*scale)),Math.Max(1,(int)(40*scale)));
                Check(IsHue(Pixel(form,sample),220,270),"unknown quota keeps blue-purple ball and displays unknown");
                Check(tip.GetToolTip(form).Contains("未知"),"unknown quota is not formatted as zero");
                form.ApplyState(new FloatingQuotaState { Remaining=73.4,Stale=true });
                Check(IsHue(Pixel(form,sample),220,270),"stale quota keeps blue-purple ball and displays pending label");
                Check(tip.GetToolTip(form).Contains("数据待更新"),"stale state is explicit");
                form.ApplyState(new FloatingQuotaState { Remaining=10,Stale=false });
                Check(IsHue(Pixel(form,sample),220,270),"low remaining quota retains shared blue-purple theme");
                form.ApplyState(new FloatingQuotaState { Remaining=40,Stale=false });
                Check(IsHue(Pixel(form,sample),220,270),"moderate remaining quota retains shared blue-purple theme");
                form.ApplyState(new FloatingQuotaState { Remaining=73.4,Stale=false });
                Check(IsHue(Pixel(form,sample),220,270),"ample remaining quota retains shared blue-purple theme");

                int dashboard=0,tasks=0,refresh=0,hide=0,exit=0;
                form.DashboardRequested+=delegate{dashboard++;};form.TasksRequested+=delegate{tasks++;};
                form.RefreshRequested+=delegate{refresh++;};form.HideRequested+=delegate{hide++;};form.ExitRequested+=delegate{exit++;};
                Click(form,45,153);Check(dashboard==1 && !form.IsExpanded,"details action routes once and collapses");
                Click(form,160,153);Check(tasks==1 && !form.IsExpanded,"task action routes once and collapses");
                Click(form,200,20);Check(refresh==1 && form.IsExpanded,"refresh action preserves expansion");
                Click(form,225,20);Check(!form.IsExpanded,"collapse action collapses");
                popup.Items[5].PerformClick();popup.Items[6].PerformClick();
                Check(hide==1 && exit==1,"popup hide and exit route through owner events");

                Offscreen(form,true);Set(form,"nextScreenCheckUtc",DateTime.MaxValue);
                Set(form,"dragging",true);
                Invoke(form,"CheckPointer");Check(form.IsExpanded,"hover collapse suppressed during drag");
                bounds=form.Bounds;form.ApplyState(new FloatingQuotaState { Remaining=44.5 });
                Check(form.IsDragging && form.IsExpanded && form.Bounds==bounds,"quota update preserves active drag geometry");
                Set(form,"dragging",false);Set(form,"mouseDown",true);
                Invoke(form,"CheckPointer");Check(form.IsExpanded,"hover collapse suppressed while mouse pressed");
                Set(form,"mouseDown",false);Invoke(form,"CheckPointer");form.Location=new Point(-30000,-30000);
                Check(form.IsExpanded,"click-opened card stays expanded when pointer leaves");

                Offscreen(form,true);Set(form,"dragging",true);Set(form,"mouseDown",true);form.Cursor=Cursors.SizeAll;
                Invoke(form,"OnMouseCaptureChanged",EventArgs.Empty);form.Location=new Point(-30000,-30000);
                Check(!form.IsDragging && !form.IsExpanded && !(bool)Get(form,"mouseDown"),"lost capture cancels drag and collapses");
                Check(form.Cursor==Cursors.Hand,"lost capture restores cursor");

                Offscreen(form,true);Set(form,"hoverAction",0);
                Point buttonSample=new Point((int)(30*scale),(int)(150*scale));
                Color idleButton=Pixel(form,buttonSample);
                Invoke(form,"OnMouseDown",new MouseEventArgs(MouseButtons.Left,1,(int)(45*scale),(int)(153*scale),0));
                Check(Distance(Pixel(form,buttonSample),idleButton)>5,"pressed action has visible button feedback");
                form.Capture=false;Application.DoEvents();
                Check((int)Get(form,"pressedAction")==0 && !(bool)Get(form,"mouseDown"),"lost action capture clears pressed state");
                Check(Pixel(form,buttonSample).ToArgb()==idleButton.ToArgb(),"lost action capture restores idle button appearance");

                Offscreen(form,true);Set(form,"dragging",true);Set(form,"mouseDown",true);
                form.ShowCollapsed();form.Location=new Point(-30000,-30000);
                Check(!form.IsExpanded && !form.IsDragging,"show collapsed cancels an in-flight drag");
                Check(form.Visible,"show collapsed reopens floating form");
                CheckRenderScales(form);
                if(args.Length>0)SaveComparison(form,args[0]);
                bool timerDisposed=false,tipDisposed=false;
                timer.Disposed+=delegate{timerDisposed=true;};tip.Disposed+=delegate{tipDisposed=true;};
                form.CloseForExit();Application.DoEvents();
                Check(form.IsDisposed && timerDisposed && tipDisposed && popup.IsDisposed,"closing disposes timer tooltip popup and form");
            }
            Console.WriteLine("PASS: "+assertions+" offline floating-window assertions; no live sources or notifications.");return 0;
        }
        catch(Exception ex){Console.WriteLine(ex.ToString());return 1;}
    }
}
