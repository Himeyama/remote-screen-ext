using System.Runtime.InteropServices;

namespace RemoteScreen;

sealed unsafe class DesktopDuplication : IDisposable
{
    const int NotFound = unchecked((int)0x887A0002);
    const int WaitTimeout = unchecked((int)0x887A0027);
    static readonly Guid FactoryId = new("770aae78-f26f-4dba-a829-253c83d1b387");
    static readonly Guid Output1Id = new("00cddea8-939b-4b83-a340-a685226666cc");
    static readonly Guid Texture2DId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    // COM method slots from the Windows SDK's dxgi.h, dxgi1_2.h, and d3d11.h.
    const int EnumAdapters1 = 12, EnumOutputs = 7, GetOutputDescription = 7, DuplicateOutput = 22;
    const int AcquireNextFrame = 8, ReleaseFrame = 14;
    const int CreateTexture2D = 5, GetTextureDescription = 10, Map = 14, Unmap = 15, CopyResource = 47;

    readonly int _sourceWidth;
    readonly int _sourceHeight;
    readonly BgraResampler _resampler;
    readonly uint[] _desktop;
    nint _device;
    nint _context;
    nint _duplication;
    nint _staging;
    bool _hasPixels;
    bool _separatePointer;

    DesktopDuplication(Display display, int width, int height)
    {
        _sourceWidth = display.Bounds.Width;
        _sourceHeight = display.Bounds.Height;
        _resampler = new BgraResampler(_sourceWidth, _sourceHeight, width, height);
        _desktop = new uint[width * height];
    }

    public static DesktopDuplication? TryCreate(Display display, int width, int height)
    {
        nint factory = 0;
        try
        {
            Guid iid = FactoryId;
            Marshal.ThrowExceptionForHR(CreateDXGIFactory1(&iid, &factory));
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                nint adapter = 0;
                int status = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(factory, EnumAdapters1))
                    (factory, adapterIndex, &adapter);
                if (status == NotFound)
                {
                    return null;
                }
                Marshal.ThrowExceptionForHR(status);
                try
                {
                    using OutputReference output = FindOutput(adapter, display.Handle);
                    if (output.Value == 0)
                    {
                        continue;
                    }

                    DesktopDuplication result = new(display, width, height);
                    try
                    {
                        result.Initialize(adapter, output.Value);
                        return result;
                    }
                    catch
                    {
                        result.Dispose();
                        throw;
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        catch (Exception error) when (error is COMException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            Release(factory);
        }
    }

    static OutputReference FindOutput(nint adapter, nint monitor)
    {
        for (uint index = 0; ; index++)
        {
            nint output = 0;
            int status = ((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Method(adapter, EnumOutputs))
                (adapter, index, &output);
            if (status == NotFound)
            {
                return default;
            }
            Marshal.ThrowExceptionForHR(status);

            OutputDescription description = default;
            status = ((delegate* unmanaged[Stdcall]<nint, OutputDescription*, int>)Method(output, GetOutputDescription))
                (output, &description);
            const uint identityRotation = 1;
            if (status >= 0 && description.Monitor == monitor && description.Attached != 0
                && description.Rotation == identityRotation)
            {
                return new OutputReference(output);
            }
            Release(output);
        }
    }

    void Initialize(nint adapter, nint output)
    {
        Marshal.ThrowExceptionForHR(D3D11CreateDevice(adapter, 0, 0, 0, 0, 0, 7,
            out _device, 0, out _context));

        Guid iid = Output1Id;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(output, ref iid, out nint output1));
        try
        {
            nint duplication = 0;
            Marshal.ThrowExceptionForHR(
                ((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Method(output1, DuplicateOutput))
                (output1, _device, &duplication));
            _duplication = duplication;
        }
        finally
        {
            Release(output1);
        }
    }

    public bool CopyNextFrame(Span<uint> target, out bool paintPointer)
    {
        paintPointer = false;
        FrameInfo info = default;
        nint resource = 0;
        uint timeout = _hasPixels ? 0u : 30u;
        int status = ((delegate* unmanaged[Stdcall]<nint, uint, FrameInfo*, nint*, int>)
            Method(_duplication, AcquireNextFrame))(_duplication, timeout, &info, &resource);
        if (status == WaitTimeout)
        {
            return false;
        }
        Marshal.ThrowExceptionForHR(status);

        try
        {
            bool firstFrame = !_hasPixels;
            if (firstFrame || info.LastPresentTime != 0)
            {
                CopyDesktop(resource);
                _hasPixels = true;
            }
            if (firstFrame || info.LastMouseUpdateTime != 0)
            {
                _separatePointer = info.PointerVisible != 0;
            }

            _desktop.AsSpan().CopyTo(target);
            paintPointer = _separatePointer;
            return true;
        }
        finally
        {
            Release(resource);
            ((delegate* unmanaged[Stdcall]<nint, int>)Method(_duplication, ReleaseFrame))(_duplication);
        }
    }

    void CopyDesktop(nint resource)
    {
        Guid iid = Texture2DId;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(resource, ref iid, out nint texture));
        try
        {
            if (_staging == 0)
            {
                TextureDescription description = default;
                ((delegate* unmanaged[Stdcall]<nint, TextureDescription*, void>)Method(texture, GetTextureDescription))
                    (texture, &description);
                const uint bgra8 = 87;
                if (description.Width != _sourceWidth || description.Height != _sourceHeight || description.Format != bgra8)
                {
                    throw new InvalidOperationException("DirectX の画面サイズまたは色形式が変更されました。");
                }
                const uint stagingUsage = 3, cpuRead = 0x20000;
                description.Usage = stagingUsage;
                description.CpuAccess = cpuRead;
                description.Bind = 0;
                description.Misc = 0;
                nint staging = 0;
                Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, TextureDescription*, nint, nint*, int>)
                    Method(_device, CreateTexture2D))(_device, &description, 0, &staging));
                _staging = staging;
            }

            ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Method(_context, CopyResource))
                (_context, _staging, texture);
            SurfaceMap pixels = default;
            const uint mapRead = 1;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<nint, nint, uint, uint, uint, SurfaceMap*, int>)
                Method(_context, Map))(_context, _staging, 0, mapRead, 0, &pixels));
            try
            {
                _resampler.Copy(pixels.Data, (int)pixels.Pitch, _desktop);
            }
            finally
            {
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Method(_context, Unmap))(_context, _staging, 0);
            }
        }
        finally
        {
            Release(texture);
        }
    }

    public void Dispose()
    {
        Release(_staging);
        _staging = 0;
        Release(_duplication);
        _duplication = 0;
        Release(_context);
        _context = 0;
        Release(_device);
        _device = 0;
    }

    static nint Method(nint instance, int slot) => (*(nint**)instance)[slot];

    static void Release(nint instance)
    {
        if (instance != 0)
        {
            Marshal.Release(instance);
        }
    }

    readonly record struct OutputReference(nint Value) : IDisposable
    {
        public void Dispose() => Release(Value);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct OutputDescription
    {
        public fixed char Name[32];
        public int Left, Top, Right, Bottom, Attached;
        public uint Rotation;
        public nint Monitor;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct FrameInfo
    {
        public long LastPresentTime, LastMouseUpdateTime;
        public uint AccumulatedFrames;
        public int RectanglesCoalesced, ProtectedContentMaskedOut, PointerX, PointerY, PointerVisible;
        public uint MetadataSize, PointerShapeSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct TextureDescription
    {
        public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, Bind, CpuAccess, Misc;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SurfaceMap
    {
        public nint Data;
        public uint Pitch, DepthPitch;
    }

    [DllImport("dxgi.dll", ExactSpelling = true)]
    static extern int CreateDXGIFactory1(Guid* iid, nint* factory);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int D3D11CreateDevice(nint adapter, uint driverType, nint software, uint flags,
        nint levels, uint levelCount, uint sdkVersion, out nint device, nint level, out nint context);
}
