using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Runtime.InteropServices;

namespace RemoteScreen;

sealed record Display(string Name, Rectangle Bounds, bool IsPrimary, nint Handle)
{
    public string Label => $"{Name.Replace(@"\\.\", "")} · {Bounds.Width} × {Bounds.Height} · ({Bounds.X}, {Bounds.Y})";
}

static class DesktopCapture
{
    public static List<Display> ListDisplays()
    {
        List<Display> displays = [];
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info))
            {
                displays.Add(new Display(info.Device, Rectangle.FromLTRB(
                    info.Left, info.Top, info.Right, info.Bottom), (info.Flags & 1) != 0, monitor));
            }

            return true;
        }, 0);
        return displays;
    }

    public sealed class Encoder : IDisposable
    {
        const uint SourceCopyWithLayeredWindows = 0x40CC0020;
        readonly int _width;
        readonly int _height;
        readonly int _columns;
        readonly int _rows;
        readonly byte[] _previous;
        readonly uint[] _rgba = new uint[ScreenFrame.TileSize * ScreenFrame.TileSize];
        readonly MemoryStream _compressed = new();
        nint _device;
        nint _bitmapHandle;
        nint _previousObject;
        nint _pixels;
        DesktopDuplication? _duplication;
        long _lastDuplicationFailure;
        byte[][]? _tiles;

        public Encoder(Display display)
        {
            Display = display;
            Rectangle bounds = display.Bounds;
            double scale = Math.Min(1, 1920d / Math.Max(bounds.Width, bounds.Height));
            _width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
            _height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
            _columns = (_width + ScreenFrame.TileSize - 1) / ScreenFrame.TileSize;
            _rows = (_height + ScreenFrame.TileSize - 1) / ScreenFrame.TileSize;
            _previous = new byte[_width * _height * 4];
            nint source = GetScreenDevice();
            try
            {
                _device = CreateCompatibleDC(source);
                if (_device == 0)
                {
                    throw new InvalidOperationException("画面取り込み用のデバイスを作成できません。");
                }
                SetStretchBltMode(_device, 4); // HALFTONE
                SetBrushOrgEx(_device, 0, 0, 0);

                BitmapHeader header = new()
                {
                    Size = (uint)Marshal.SizeOf<BitmapHeader>(),
                    Width = _width,
                    Height = -_height,
                    Planes = 1,
                    BitCount = 32
                };
                _bitmapHandle = CreateDIBSection(source, ref header, 0, out _pixels, 0, 0);
                if (_bitmapHandle == 0 || _pixels == 0)
                {
                    throw new InvalidOperationException("画面取り込み用のビットマップを作成できません。");
                }

                _previousObject = SelectObject(_device, _bitmapHandle);
                if (_previousObject == 0 || _previousObject == -1)
                {
                    throw new InvalidOperationException("画面取り込み用のビットマップを選択できません。");
                }
                _duplication = DesktopDuplication.TryCreate(display, _width, _height);
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                ReleaseDC(0, source);
            }
        }

        public Display Display { get; }
        public bool UsesDesktopDuplication => _duplication is not null;

        public unsafe ScreenFrame? CaptureChangedFrame()
        {
            ObjectDisposedException.ThrowIf(_device == 0, this);
            Span<byte> pixels = new((void*)_pixels, _previous.Length);
            if (!CapturePixels(MemoryMarshal.Cast<byte, uint>(pixels)))
            {
                return null;
            }

            if (_tiles is not null && pixels.SequenceEqual(_previous))
            {
                return null;
            }

            byte[][] tiles = _tiles is null ? new byte[_columns * _rows][] : (byte[][])_tiles.Clone();
            for (int row = 0; row < _rows; row++)
            {
                int y = row * ScreenFrame.TileSize;
                int height = Math.Min(ScreenFrame.TileSize, _height - y);
                for (int column = 0; column < _columns; column++)
                {
                    int x = column * ScreenFrame.TileSize;
                    int width = Math.Min(ScreenFrame.TileSize, _width - x);
                    if (_tiles is null || TileChanged(pixels, x, y, width, height))
                    {
                        tiles[row * _columns + column] = CompressTile(pixels, x, y, width, height);
                    }
                }
            }

            _tiles = tiles;
            return new ScreenFrame(_width, _height, tiles);
        }

        bool CapturePixels(Span<uint> pixels)
        {
            Rectangle bounds = Display.Bounds;
            if (_duplication is null && _lastDuplicationFailure != 0 &&
                Stopwatch.GetElapsedTime(_lastDuplicationFailure) >= TimeSpan.FromSeconds(2))
            {
                _duplication = DesktopDuplication.TryCreate(Display, _width, _height);
                _lastDuplicationFailure = _duplication is null ? Stopwatch.GetTimestamp() : 0;
            }
            if (_duplication is not null)
            {
                try
                {
                    if (_duplication.CopyNextFrame(pixels, out bool paintPointer))
                    {
                        if (paintPointer)
                        {
                            PaintCursor(_device, bounds, _width, _height);
                            GdiFlush();
                        }
                        return true;
                    }
                    if (_tiles is not null)
                    {
                        return false;
                    }
                }
                catch (Exception error) when (error is COMException or InvalidOperationException or ArgumentException)
                {
                    _duplication.Dispose();
                    _duplication = null;
                    _lastDuplicationFailure = Stopwatch.GetTimestamp();
                }
            }

            nint source = GetScreenDevice();
            try
            {
                bool copied = _width == bounds.Width && _height == bounds.Height
                    ? BitBlt(_device, 0, 0, _width, _height, source, bounds.X, bounds.Y, SourceCopyWithLayeredWindows)
                    : StretchBlt(_device, 0, 0, _width, _height, source, bounds.X, bounds.Y,
                        bounds.Width, bounds.Height, SourceCopyWithLayeredWindows);
                if (!copied)
                {
                    throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                }
                PaintCursor(_device, bounds, _width, _height);
                GdiFlush();
                return true;
            }
            finally
            {
                ReleaseDC(0, source);
            }
        }

        bool TileChanged(ReadOnlySpan<byte> pixels, int x, int y, int width, int height)
        {
            for (int row = y; row < y + height; row++)
            {
                int start = (row * _width + x) * 4;
                if (!pixels.Slice(start, width * 4).SequenceEqual(_previous.AsSpan(start, width * 4)))
                {
                    return true;
                }
            }
            return false;
        }

        byte[] CompressTile(ReadOnlySpan<byte> pixels, int x, int y, int width, int height)
        {
            for (int row = 0; row < height; row++)
            {
                int start = ((y + row) * _width + x) * 4;
                ReadOnlySpan<byte> bytes = pixels.Slice(start, width * 4);
                bytes.CopyTo(_previous.AsSpan(start, width * 4));
                ReadOnlySpan<uint> bgra = MemoryMarshal.Cast<byte, uint>(bytes);
                Span<uint> rgba = _rgba.AsSpan(row * width, width);
                for (int column = 0; column < width; column++)
                {
                    uint pixel = bgra[column];
                    rgba[column] = 0xff000000 | ((pixel & 0xff) << 16) | (pixel & 0x00ff00) | ((pixel >> 16) & 0xff);
                }
            }

            _compressed.SetLength(0);
            using (ZLibStream zlib = new(_compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                zlib.Write(MemoryMarshal.AsBytes(_rgba.AsSpan(0, width * height)));
            }
            return _compressed.ToArray();
        }

        public void Dispose()
        {
            _duplication?.Dispose();
            _duplication = null;
            if (_previousObject != 0 && _previousObject != -1)
            {
                SelectObject(_device, _previousObject);
                _previousObject = 0;
            }
            if (_bitmapHandle != 0)
            {
                DeleteObject(_bitmapHandle);
                _bitmapHandle = 0;
            }
            if (_device != 0)
            {
                DeleteDC(_device);
                _device = 0;
            }
            _compressed.Dispose();
        }
    }

    static nint GetScreenDevice()
    {
        nint source = GetDC(0);
        return source != 0 ? source : throw new InvalidOperationException("Windows のデスクトップにアクセスできません。");
    }

    static void PaintCursor(nint device, Rectangle bounds, int width, int height)
    {
        CursorInfo cursor = new() { Size = Marshal.SizeOf<CursorInfo>() };
        if (!GetCursorInfo(ref cursor) || cursor.Flags != 1 || !bounds.Contains(cursor.X, cursor.Y)
            || !GetIconInfo(cursor.Handle, out IconInfo icon))
        {
            return;
        }

        try
        {
            int x = (cursor.X - bounds.X) * width / bounds.Width - icon.HotspotX;
            int y = (cursor.Y - bounds.Y) * height / bounds.Height - icon.HotspotY;
            DrawIconEx(device, x, y, cursor.Handle, 0, 0, 0, 0, 3);
        }
        finally
        {
            DeleteObject(icon.Mask);
            if (icon.Color != 0)
            {
                DeleteObject(icon.Color);
            }
        }
    }

    delegate bool MonitorCallback(nint monitor, nint device, nint rect, nint state);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct MonitorInfo
    {
        public int Size;
        public int Left, Top, Right, Bottom;
        public int WorkLeft, WorkTop, WorkRight, WorkBottom;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct CursorInfo
    {
        public int Size;
        public uint Flags;
        public nint Handle;
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IconInfo
    {
        public int IsIcon, HotspotX, HotspotY;
        public nint Mask, Color;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct BitmapHeader
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, BitCount;
        public uint Compression, ImageSize;
        public int PixelsPerMeterX, PixelsPerMeterY;
        public uint ColorsUsed, ImportantColors;
    }

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(nint device, nint clip, MonitorCallback callback, nint state);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    static extern bool GetCursorInfo(ref CursorInfo info);

    [DllImport("user32.dll")]
    static extern bool GetIconInfo(nint icon, out IconInfo info);

    [DllImport("user32.dll")]
    static extern bool DrawIconEx(nint device, int x, int y, nint icon, int width, int height,
        uint step, nint brush, uint flags);

    [DllImport("gdi32.dll")]
    static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll")]
    static extern nint GetDC(nint window);

    [DllImport("user32.dll")]
    static extern int ReleaseDC(nint window, nint device);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern bool BitBlt(nint destination, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, uint operation);

    [DllImport("gdi32.dll", SetLastError = true)]
    static extern bool StretchBlt(nint destination, int x, int y, int width, int height,
        nint source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);

    [DllImport("gdi32.dll")]
    static extern nint CreateCompatibleDC(nint device);

    [DllImport("gdi32.dll")]
    static extern bool DeleteDC(nint device);

    [DllImport("gdi32.dll")]
    static extern nint CreateDIBSection(nint device, ref BitmapHeader info, uint usage, out nint pixels,
        nint section, uint offset);

    [DllImport("gdi32.dll")]
    static extern nint SelectObject(nint device, nint handle);

    [DllImport("gdi32.dll")]
    static extern bool GdiFlush();

    [DllImport("gdi32.dll")]
    static extern int SetStretchBltMode(nint device, int mode);

    [DllImport("gdi32.dll")]
    static extern bool SetBrushOrgEx(nint device, int x, int y, nint previousOrigin);
}
