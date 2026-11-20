using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace AIUsage;

/// Verifies the alpha in the converted HBITMAP before handing that same bitmap to USER32.
static class LayeredSurface
{
    [StructLayout(LayoutKind.Sequential)]
    struct BitmapInfoHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int XPelsPerMeter, YPelsPerMeter;
        public uint ColorsUsed, ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeBitmap
    {
        public int Type, Width, Height, Stride;
        public ushort Planes, BitsPerPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DibSection
    {
        public NativeBitmap Bitmap;
        public BitmapInfoHeader Info;
        public uint RedMask, GreenMask, BlueMask;
        public IntPtr Section;
        public uint Offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct Blend
    {
        public byte Operation, Flags, ConstantAlpha, AlphaFormat;
    }

    [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] static extern int GetObjectInfo(IntPtr obj, int count, out DibSection dib);
    [DllImport("gdi32.dll")] static extern int GetDIBits(IntPtr dc, IntPtr bitmap, uint start, uint lines,
        [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);
    [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr window, IntPtr destinationDC,
        IntPtr destination, ref Size size, IntPtr sourceDC, ref Point source, uint colorKey, ref Blend blend, uint flags);

    static bool HasAlpha(IntPtr dc, IntPtr bitmap, Size size, Point sample, byte expected)
    {
        int count = GetObjectInfo(bitmap, Marshal.SizeOf<DibSection>(), out var dib);
        var b = dib.Bitmap;
        if (count < Marshal.SizeOf<NativeBitmap>() || b.Width != size.Width || b.Height != size.Height
            || b.Planes != 1 || b.BitsPerPixel != 32 || b.Stride < 4 * b.Width) return false;

        if (count == Marshal.SizeOf<DibSection>() && b.Bits != IntPtr.Zero &&
            Math.Abs(dib.Info.Height) == size.Height)
        {
            int row = dib.Info.Height < 0 ? sample.Y : size.Height - 1 - sample.Y;
            return Marshal.ReadByte(b.Bits, row * b.Stride + sample.X * 4 + 3) == expected;
        }

        // For a device-dependent bitmap, request one scanline in bottom-up BGRA order.
        var info = new BitmapInfoHeader { Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = b.Width,
            Height = b.Height, Planes = 1, BitCount = 32 };
        var pixels = new byte[4 * b.Width];
        return GetDIBits(dc, bitmap, (uint)(size.Height - 1 - sample.Y), 1, pixels, ref info, 0) == 1
            && pixels[4 * sample.X + 3] == expected;
    }

    public static bool Present(IntPtr window, Size size, Point sample, byte alpha, Action<Graphics> draw)
    {
        IntPtr bitmap = IntPtr.Zero, dc = IntPtr.Zero, previous = IntPtr.Zero;
        try
        {
            using var frame = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppPArgb);
            using (var graphics = Graphics.FromImage(frame))
            {
                graphics.Clear(Color.Transparent);
                draw(graphics);
            }
            bitmap = frame.GetHbitmap(Color.FromArgb(0));
            dc = CreateCompatibleDC(IntPtr.Zero);
            if (dc == IntPtr.Zero || !HasAlpha(dc, bitmap, size, sample, alpha)) return false;
            previous = SelectObject(dc, bitmap);
            if (previous == IntPtr.Zero || previous == new IntPtr(-1)) return false;
            var origin = Point.Empty;
            var blend = new Blend { ConstantAlpha = 255, AlphaFormat = 1 }; // AC_SRC_OVER, AC_SRC_ALPHA
            return UpdateLayeredWindow(window, IntPtr.Zero, IntPtr.Zero, ref size, dc, ref origin, 0, ref blend, 2);
        }
        catch (Exception e) when (e is ExternalException or ArgumentException or OutOfMemoryException)
        {
            return false;
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != new IntPtr(-1)) SelectObject(dc, previous);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
        }
    }
}
