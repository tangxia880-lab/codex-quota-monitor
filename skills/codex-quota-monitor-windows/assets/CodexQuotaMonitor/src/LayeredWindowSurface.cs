using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace CodexQuotaMonitor
{
    // The caller owns the HWND and Bitmap and calls Present on the window's UI
    // thread. The HWND must have WS_EX_LAYERED. Do not mix this path with Form
    // Opacity/TransparencyKey or SetLayeredWindowAttributes on the same handle.
    internal static class LayeredWindowSurface
    {
        private const uint DibRgbColors = 0;
        private const uint BiRgb = 0;
        private const uint UlwAlpha = 2;
        private const byte AcSrcOver = 0;
        private const byte AcSrcAlpha = 1;
        private static readonly IntPtr GdiError = new IntPtr(-1);

        internal static void Present(IntPtr windowHandle, Bitmap frame, Point location)
        {
            if (windowHandle == IntPtr.Zero) throw new ArgumentException("窗口句柄不可为空。", "windowHandle");
            if (frame == null) throw new ArgumentNullException("frame");
            int width = frame.Width, height = frame.Height;
            if (width <= 0 || height <= 0) throw new ArgumentException("图像尺寸必须大于零。", "frame");
            int rowBytes, imageBytes;
            try { rowBytes = checked(width * 4); imageBytes = checked(rowBytes * height); }
            catch (OverflowException) { throw new ArgumentException("悬浮窗图像过大。", "frame"); }

            Bitmap converted = null;
            IntPtr screenDc = IntPtr.Zero;
            IntPtr sourceDc = IntPtr.Zero;
            IntPtr dib = IntPtr.Zero;
            IntPtr previousBitmap = IntPtr.Zero;
            try
            {
                Bitmap pixels = frame;
                if (frame.PixelFormat != PixelFormat.Format32bppPArgb)
                {
                    // A SourceCopy into an explicitly PArgb bitmap performs the
                    // alpha premultiplication once; do not multiply these bytes again.
                    converted = new Bitmap(width, height, PixelFormat.Format32bppPArgb);
                    using (Graphics graphics = Graphics.FromImage(converted))
                    {
                        graphics.CompositingMode = CompositingMode.SourceCopy;
                        graphics.DrawImage(frame, new Rectangle(0, 0, width, height), 0, 0, width, height, GraphicsUnit.Pixel);
                    }
                    pixels = converted;
                }

                screenDc = GetDC(IntPtr.Zero);
                if (screenDc == IntPtr.Zero) throw Failure("GetDC");
                sourceDc = CreateCompatibleDC(screenDc);
                if (sourceDc == IntPtr.Zero) throw Failure("CreateCompatibleDC");

                var bitmapInfo = new BitmapInfo();
                bitmapInfo.Header.Size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader));
                bitmapInfo.Header.Width = width;
                bitmapInfo.Header.Height = -height; // Negative height gives top-down rows.
                bitmapInfo.Header.Planes = 1;
                bitmapInfo.Header.BitCount = 32;
                bitmapInfo.Header.Compression = BiRgb;
                bitmapInfo.Header.SizeImage = (uint)imageBytes;
                IntPtr bits;
                dib = CreateDIBSection(screenDc, ref bitmapInfo, DibRgbColors, out bits, IntPtr.Zero, 0);
                if (dib == IntPtr.Zero || bits == IntPtr.Zero) throw Failure("CreateDIBSection");

                // Copy before selecting the DIB into a DC: there are no earlier
                // GDI writes to synchronize with GdiFlush. Four-byte BGRA pixels
                // naturally satisfy DIB DWORD row alignment at every width.
                BitmapData locked = pixels.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
                try
                {
                    if (Math.Abs((long)locked.Stride) < rowBytes)
                        throw new ArgumentException("图像行跨度无效。", "frame");
                    var row = new byte[rowBytes];
                    for (int y = 0; y < height; y++)
                    {
                        // Scan0 denotes logical row zero; honoring the signed
                        // source stride also handles bottom-up Bitmap buffers.
                        IntPtr source = AddOffset(locked.Scan0, (long)y * locked.Stride);
                        IntPtr destination = AddOffset(bits, (long)y * rowBytes);
                        Marshal.Copy(source, row, 0, rowBytes);
                        Marshal.Copy(row, 0, destination, rowBytes);
                    }
                }
                finally { pixels.UnlockBits(locked); }

                previousBitmap = SelectObject(sourceDc, dib);
                if (previousBitmap == IntPtr.Zero || previousBitmap == GdiError) throw Failure("SelectObject");
                var destinationPoint = new NativePoint { X = location.X, Y = location.Y };
                var sourcePoint = new NativePoint { X = 0, Y = 0 };
                var size = new NativeSize { Width = width, Height = height };
                var blend = new BlendFunction { BlendOp = AcSrcOver, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = AcSrcAlpha };
                if (!UpdateLayeredWindow(windowHandle, screenDc, ref destinationPoint, ref size, sourceDc, ref sourcePoint, 0, ref blend, UlwAlpha))
                    throw Failure("UpdateLayeredWindow");
            }
            finally
            {
                // Restore the stock bitmap, then destroy the DC before deleting
                // the DIB so it cannot remain selected even if restoration fails.
                // Cleanup must not replace the original Win32/GDI+ exception.
                if (sourceDc != IntPtr.Zero)
                {
                    if (previousBitmap != IntPtr.Zero && previousBitmap != GdiError) SelectObject(sourceDc, previousBitmap);
                    DeleteDC(sourceDc);
                }
                if (dib != IntPtr.Zero) DeleteObject(dib);
                if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
                if (converted != null) converted.Dispose();
            }
        }

        private static IntPtr AddOffset(IntPtr address, long offset)
        {
            return new IntPtr(checked(address.ToInt64() + offset));
        }

        private static Win32Exception Failure(string operation)
        {
            int error = Marshal.GetLastWin32Error();
            return new Win32Exception(error, "悬浮窗透明图像更新失败（" + operation + "）。");
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint { internal int X; internal int Y; }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize { internal int Width; internal int Height; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct BlendFunction
        {
            internal byte BlendOp;
            internal byte BlendFlags;
            internal byte SourceConstantAlpha;
            internal byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            internal uint Size;
            internal int Width;
            internal int Height;
            internal ushort Planes;
            internal ushort BitCount;
            internal uint Compression;
            internal uint SizeImage;
            internal int XPelsPerMeter;
            internal int YPelsPerMeter;
            internal uint ColorsUsed;
            internal uint ColorsImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            internal BitmapInfoHeader Header;
            internal uint Colors; // One unused RGBQUAD; 32-bpp BI_RGB has no palette.
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetDC(IntPtr windowHandle);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern int ReleaseDC(IntPtr windowHandle, IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateCompatibleDC(IntPtr dc);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo bitmapInfo, uint usage, out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll", SetLastError = true)]
        private static extern IntPtr SelectObject(IntPtr dc, IntPtr value);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteObject(IntPtr value);

        [DllImport("gdi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DeleteDC(IntPtr dc);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateLayeredWindow(IntPtr windowHandle, IntPtr destinationDc, ref NativePoint destination, ref NativeSize size, IntPtr sourceDc, ref NativePoint source, uint colorKey, ref BlendFunction blend, uint flags);
    }
}
