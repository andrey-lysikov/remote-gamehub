//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Media;

internal enum CaptureStatus
{
    // A new picture is in the frame texture.
    Frame,
    // Nothing changed within the timeout. Not an error: an idle desktop draws nothing.
    Idle,
    // The duplication has to be created again. Ordinary, and handled by the caller.
    Lost,
    // Nothing can be captured for the moment — the sign-in screen, a UAC prompt, a locked
    // session. Waiting is the only correct response; the desktop comes back on its own.
    Unavailable,
}

internal enum ReopenOutcome
{
    // A new duplication is open. The frame texture is new too, so the encoder is rebuilt with it.
    Reopened,
    // Windows will not duplicate this screen at the moment, and the reason passes on its own: a
    // prompt on the secure desktop, the lock screen, a session being handed over. Waiting is the
    // answer, and it must not count towards giving up — nothing is wrong with this server.
    Unavailable,
    // Something else, which may or may not pass. Counted, and the stream ends if it goes on.
    Failed,
}

// The desktop is there but not this process's to duplicate right now. Separated from every other
// refusal because the answer is different: wait, rather than rebuild or give up. Windows says this
// while the secure desktop is in front — a UAC prompt, the sign-in or lock screen — to everything
// that is not LocalSystem, and to LocalSystem too when its thread is on the wrong desktop.
internal sealed class DesktopUnavailableException : Exception
{
    internal DesktopUnavailableException(string message) : base(message) { }
}

// The desktop as the compositor flattened it, through Desktop Duplication — hence no per-API path.
// The frame is copied out because the acquired one must be released before the next is asked for.
internal sealed unsafe class DesktopDuplicator : IDisposable
{
    // Whether what was captured is an HDR desktop, which Windows composes as linear half floats.
    // ColourConverter turns those into the ten-bit BT.2020 PQ the encoder takes.
    internal bool IsHdrDesktop => FrameFormat == Dxgi.DXGI_FORMAT_R16G16B16A16_FLOAT;

    // GDI compatibility, so the pointer can be drawn by the code that defines how.
    private const uint D3D11_RESOURCE_MISC_GDI_COMPATIBLE = 0x200;

    private readonly int _adapterIndex;
    private readonly int _outputIndex;
    private readonly bool _captureCursor;
    private readonly bool _preferHdr;
    private readonly CursorPainter? _cursor;

    // What the duplication last said about the pointer. See TryCapture.
    private bool _pointerVisible;

    // Where the captured screen sits on the desktop. See OpenDuplication.
    private Rect _bounds;

    // How many frames the desktop composed since the last capture, as the duplication counts them.
    // More than one means this end asked too slowly and DXGI coalesced what it missed.
    internal uint LastAccumulatedFrames { get; private set; }

    // Where the pointer's shape is read into. See ReadPointerShape.
    private byte[] _shapeBuffer = Array.Empty<byte>();

    private void* _adapter;
    private void* _device;
    private void* _context;
    private void* _output1;
    private void* _duplication;
    private void* _frame;
    private void* _composed;
    private void* _composedSurface;

    // The pointer, drawn by GDI onto its own eight-bit BGRA texture rather than into the frame
    // (half floats are not a format GDI can touch); used for an HDR desktop only.
    private void* _cursorOverlay;
    private void* _cursorOverlaySurface;
    private void* _cursorOverlayRtv;

    // Whether the pointer is drawn into this duplication. Not the painter itself: nulling that on
    // a ten-bit desktop lost the pointer for the rest of the session, reopen in eight bits or not.
    private bool _drawPointer;

    // Whether the composite holds this frame's pointer. When it does not the encoder is handed the
    // desktop copy itself, and no frame is copied to change nothing.
    private bool _pointerDrawn;

    // Where and as what the pointer was drawn into the frame before this one. See DrawPointer.
    private int _pointerDrawnX;
    private int _pointerDrawnY;
    private nint _pointerDrawnShape;

    private bool _holdingFrame;
    private bool _disposed;

    internal int Width { get; private set; }
    internal int Height { get; private set; }

    // What the frame texture is in: eight-bit BGRA for an ordinary desktop, half-float when the
    // screen is in high dynamic range and that was asked for.
    internal uint FrameFormat { get; private set; } = Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM;

    // The refresh rate of the captured screen, or zero when the driver does not say.
    internal double RefreshRate { get; private set; }

    // What this screen says about its colour, for the client to set its own by. Rec. 2020's own
    // numbers when the screen answers nothing, which is every screen that does no HDR.
    internal HdrDisplay Hdr { get; private set; } = HdrDisplay.Rec2020;

    // The texture the encoder reads: the raw capture for an HDR desktop (the shader composites
    // the pointer from CursorOverlay instead), or the copy with the pointer drawn in otherwise.
    internal nint FrameTexture => !IsHdrDesktop && _pointerDrawn ? (nint)_composed : (nint)_frame;

    // The pointer, on its own, for the colour shader to blend onto the picture it converts. Zero
    // when there is nothing to draw with: an ordinary desktop bakes the pointer in above instead.
    internal nint CursorOverlay => IsHdrDesktop && _drawPointer ? (nint)_cursorOverlay : 0;

    // The device the frame belongs to. The encoder has to be created on this one.
    internal nint Device => (nint)_device;

    internal nint Context => (nint)_context;

    private DesktopDuplicator(DisplayOutput output, bool captureCursor, bool preferHdr)
    {
        _adapterIndex = output.AdapterIndex;
        _outputIndex = output.OutputIndex;
        _captureCursor = captureCursor;
        _preferHdr = preferHdr;

        // The size it draws inside is told to it when the duplication opens, which is where the
        // frame's own size becomes known; a mode change opens it again with the new one.
        _cursor = captureCursor ? new CursorPainter() : null;
    }

    // preferHdr asks for the desktop as it really is when the screen is in high dynamic range,
    // rather than the eight-bit conversion DXGI would otherwise hand back.
    internal static DesktopDuplicator Create(DisplayOutput output, bool captureCursor,
                                             bool preferHdr = false)
    {
        var duplicator = new DesktopDuplicator(output, captureCursor, preferHdr);
        try
        {
            duplicator.OpenDevice();
            duplicator.OpenDuplication();
            return duplicator;
        }
        catch
        {
            duplicator.Dispose();
            throw;
        }
    }

    private void OpenDevice()
    {
        var factory = Dxgi.CreateFactory();
        try
        {
            Com.Check(Dxgi.EnumAdapters1(factory, (uint)_adapterIndex, out _adapter),
                $"IDXGIFactory1::EnumAdapters1({_adapterIndex})");
        }
        finally
        {
            Com.Release(factory);
        }

        D3D11.CreateDevice(_adapter, out _device, out _context, out var featureLevel);

        // Capture and the encoder run on different threads against this one device.
        D3D11.EnableMultithreadProtection(_device);

        Log.Info($"Direct3D 11 device created on adapter {_adapterIndex}, " +
                 $"feature level {featureLevel >> 12}.{(featureLevel >> 8) & 0xF}");
    }

    private void OpenDuplication()
    {
        // The one call that makes a UAC prompt streamable. A duplication belongs to the desktop the
        // asking thread is on, and Windows composes the prompt on a desktop of its own (Winlogon).
        // Moved there first, the duplication opens on it and the prompt is what gets captured;
        // left on Default, DuplicateOutput answers DXGI_ERROR_ACCESS_DENIED until the prompt is
        // gone. Only LocalSystem may open the secure desktop, which is why the service exists —
        // for anyone else this call quietly does nothing and the refusal below is handled instead.
        Session.InputDesktop.Attach(force: true);

        Com.Check(Dxgi.EnumOutputs(_adapter, (uint)_outputIndex, out var output),
            $"IDXGIAdapter::EnumOutputs({_outputIndex})");

        void* output5 = null;
        try
        {
            Com.Check(Com.QueryInterface(output, Dxgi.IID_IDXGIOutput1, out _output1),
                "IDXGIOutput::QueryInterface(IDXGIOutput1)");

            // Only asked for when high dynamic range is wanted, and only used if the newer
            // interface is there; the older path below is the one that has been proven.
            if (_preferHdr) Com.QueryInterface(output, Dxgi.IID_IDXGIOutput5, out output5);

            // Where this screen sits now, not when it was enumerated: a mode change moves them
            // all, and Windows answers about the pointer in the whole desktop's coordinates.
            _bounds = Dxgi.GetOutputDesc(output).DesktopCoordinates;

            if (Dxgi.GetOutputDesc1(output, out var colour)) Hdr = HdrDisplay.From(colour);
        }
        finally
        {
            Com.Release(output);
        }

        var hr = -1;

        if (output5 is not null)
        {
            // Half floats first, eight-bit BGRA after them. Ten-bit unorm is deliberately not
            // asked for: on an HDR desktop it does not convert to PQ, it clips the half floats.
            var formats = stackalloc uint[2]
            {
                Dxgi.DXGI_FORMAT_R16G16B16A16_FLOAT,
                Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM,
            };

            hr = Dxgi.DuplicateOutput1(output5, _device, formats, 2, out _duplication);
            if (hr < 0)
            {
                Log.Info($"the screen would not be duplicated in high dynamic range " +
                         $"({Com.Describe(hr)}); asking for the ordinary form instead");
            }

            Com.Release(output5);
        }

        if (hr < 0) hr = Dxgi.DuplicateOutput(_output1, _device, out _duplication);
        if (hr < 0) throw DescribeDuplicationFailure(hr);

        var desc = Dxgi.GetDuplicationDesc(_duplication);

        // What the desktop is really composed in, whatever was asked for. The one fact the ten-bit
        // path turns on, and it costs a line to have it in the log of every stream.
        Log.Info($"the desktop is composed in {FormatName(desc.ModeDesc.Format)}" +
                 (_preferHdr ? ", with ten bits asked for" : string.Empty));

        if (desc.ModeDesc.Format != 0 &&
            desc.ModeDesc.Format != Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM &&
            desc.ModeDesc.Format != Dxgi.DXGI_FORMAT_R16G16B16A16_FLOAT)
        {
            // Neither of the two asked for. Nothing here knows what it is, and a texture handed
            // to the encoder in the wrong format sends black frames and reports no error at all.
            Log.Warn(
                $"The desktop was duplicated in a format this server cannot encode (DXGI format " +
                $"{desc.ModeDesc.Format}). It is opened again in eight bits, so the stream is\n" +
                "standard range.");

            Com.ReleaseAndClear(ref _duplication);

            hr = Dxgi.DuplicateOutput(_output1, _device, out _duplication);
            if (hr < 0) throw DescribeDuplicationFailure(hr);

            desc = Dxgi.GetDuplicationDesc(_duplication);
        }

        Width = (int)desc.ModeDesc.Width;
        Height = (int)desc.ModeDesc.Height;
        FrameFormat = desc.ModeDesc.Format == 0
            ? Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM
            : desc.ModeDesc.Format;

        // The pointer's position arrives in the frame's own coordinates, so the painter needs the
        // size the frame is now: a mode change is exactly what moves the edge it is clipped to.
        _cursor?.FrameIs(Width, Height, _bounds);

        // Off only when there is nothing to draw the pointer with; see CreateFrameTexture for
        // where GDI writes it either way.
        _drawPointer = _cursor is not null;
        RefreshRate = desc.ModeDesc.RefreshDenominator == 0
            ? 0
            : (double)desc.ModeDesc.RefreshNumerator / desc.ModeDesc.RefreshDenominator;

        if (desc.DesktopImageInSystemMemory != 0)
        {
            // The desktop is being drawn on the processor: a basic display driver, or a remote
            // session. There is nothing on the card to encode, and saying so here is clearer.
            Log.Warn(
                "The desktop is being rendered in system memory, not on the graphics card.\n" +
                "That happens with the Microsoft Basic Display Adapter and inside a remote desktop\n" +
                "session. The picture cannot be handed to the card's encoder from there.\n" +
                "What to do: install the graphics driver, and stream from the physical screen\n" +
                "rather than from a remote desktop session on this machine.");
        }

        CreateFrameTexture();

        var rate = RefreshRate > 0
            ? RefreshRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " Hz"
            : "unknown refresh rate";
        Log.Info($"desktop duplication opened: {Width}x{Height}, {rate}" +
                 (IsHdrDesktop ? ", high dynamic range" : string.Empty) +
                 (_drawPointer ? ", pointer drawn into the frame" : ", pointer not drawn") +
                 (_captureCursor && !_drawPointer ? " (it could not be)" : string.Empty));
    }

    private void CreateFrameTexture()
    {
        // Here rather than in Reopen: between a lost duplication and a new one the frame texture
        // holds the last picture that was captured, and the stream goes on sending it. A prompt
        // for administrator rights is exactly that gap, and a client sent nothing for the seven
        // seconds it waits gives up and disconnects — which is not what the waiting is for.
        ReleaseFrameTextures();

        var desc = new D3D11Texture2DDesc
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            // Whatever the duplication settled on, to the encoder as it is: converting to NV12
            // here would mean a shader and a second texture for a job the encoder does itself.
            Format = FrameFormat,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = D3D11.D3D11_USAGE_DEFAULT,
            // RENDER_TARGET is not optional: the second texture below is made from this same
            // description, and a GDI-compatible texture — which draws the pointer — must have it.
            BindFlags = D3D11.D3D11_BIND_SHADER_RESOURCE | D3D11.D3D11_BIND_RENDER_TARGET,
            CpuAccessFlags = 0,
            MiscFlags = 0,
        };

        _frame = D3D11.CreateTexture2D(_device, desc);

        // The pointer is drawn into a second texture, not into the desktop copy: an unchanged
        // desktop is not re-copied, so the copy would collect a trail of arrows.
        if (!_drawPointer) return;

        if (IsHdrDesktop)
        {
            // GDI writes nothing but eight-bit BGRA, so an HDR desktop's pointer goes into its
            // own texture (the colour shader blends it in) instead of the half-float frame.
            var overlay = desc;
            overlay.Format = Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM;
            overlay.MiscFlags = D3D11_RESOURCE_MISC_GDI_COMPATIBLE;
            _cursorOverlay = D3D11.CreateTexture2D(_device, overlay);

            if (Com.QueryInterface(_cursorOverlay, Dxgi.IID_IDXGISurface1, out _cursorOverlaySurface) < 0)
            {
                Log.Info("the pointer overlay is not a surface GDI can draw into; the pointer is " +
                         "not drawn");
                _drawPointer = false;
                return;
            }

            _cursorOverlayRtv = D3D11.CreateRenderTargetView(_device, _cursorOverlay,
                new D3D11RenderTargetViewDesc
                {
                    Format = Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM,
                    ViewDimension = D3D11.D3D11_RTV_DIMENSION_TEXTURE2D,
                });

            ClearCursorOverlay();
            return;
        }

        desc.MiscFlags = D3D11_RESOURCE_MISC_GDI_COMPATIBLE;
        _composed = D3D11.CreateTexture2D(_device, desc);

        // Asked for once and held with the texture. Asking per frame was one COM call in and one
        // out, at up to 240 a second, on an interface for a texture that never changes.
        if (Com.QueryInterface(_composed, Dxgi.IID_IDXGISurface1, out _composedSurface) < 0)
        {
            Log.Info("the frame texture is not a surface GDI can draw into; the pointer is not " +
                     "drawn");
            _drawPointer = false;
        }
    }

    // Composes the frame the encoder reads, once per frame sent, so a still desktop carries a
    // pointer that moves. Answers whether it moved or changed shape since the frame before.
    internal bool DrawPointer()
    {
        var wasDrawn = _pointerDrawn;
        _pointerDrawn = false;

        if (!_drawPointer || _frame is null) return false;

        // Asked before the copy, not after: inside a game there is no pointer at all, and copying
        // a whole frame to change nothing is half a gigabyte a second at 1080p60.
        if (!_cursor!.Wanted(_pointerVisible, out var shape, out var x, out var y))
        {
            // The HDR overlay is sampled every frame regardless, so a pointer that is gone must
            // be cleared out of it or it goes on showing up in every frame after.
            if (IsHdrDesktop && wasDrawn) ClearCursorOverlay();
            return wasDrawn;
        }

        if (IsHdrDesktop)
        {
            ClearCursorOverlay();
            _cursor.Draw(_cursorOverlaySurface, shape, x, y);
        }
        else
        {
            D3D11.CopyResource(_context, _composed, _frame);
            _cursor.Draw(_composedSurface, shape, x, y);
        }

        _pointerDrawn = true;

        var moved = !wasDrawn || x != _pointerDrawnX || y != _pointerDrawnY ||
                    shape != _pointerDrawnShape;

        _pointerDrawnX = x;
        _pointerDrawnY = y;
        _pointerDrawnShape = shape;

        return moved;
    }

    private void ClearCursorOverlay()
    {
        var clear = stackalloc float[4];
        D3D11.ClearRenderTargetView(_context, _cursorOverlayRtv, clear);
    }

    // Waits up to timeoutMs for the desktop to change, and copies it into the
    // frame texture when it does.
    internal CaptureStatus TryCapture(int timeoutMs)
    {
        if (_disposed || _frame is null) return CaptureStatus.Lost;

        // The duplication is gone but the texture it filled is not, which is how the stream goes
        // on sending the last picture while Windows is holding the screen back. Answered as Lost
        // so the caller asks for a new duplication, which is the only thing that can end this.
        if (_duplication is null) return CaptureStatus.Lost;

        ReleaseHeldFrame();

        var hr = Dxgi.AcquireNextFrame(_duplication, (uint)timeoutMs, out var info, out var resource);

        if (hr == Dxgi.DXGI_ERROR_WAIT_TIMEOUT)
        {
            // Nothing was composed within the timeout. Said plainly: left at the last count, the
            // previous frame's number was added again on every wait, hundreds of times a second.
            LastAccumulatedFrames = 0;
            return CaptureStatus.Idle;
        }


        if (hr == Dxgi.DXGI_ERROR_ACCESS_LOST || hr == Dxgi.DXGI_ERROR_DEVICE_REMOVED)
        {
            Log.Info($"desktop duplication lost ({Com.Describe(hr)}); opening it again");
            return CaptureStatus.Lost;
        }

        if (hr == Dxgi.DXGI_ERROR_ACCESS_DENIED || hr == Dxgi.E_ACCESSDENIED ||
            hr == Dxgi.DXGI_ERROR_SESSION_DISCONNECTED)
        {
            return CaptureStatus.Unavailable;
        }

        if (hr < 0)
        {
            // Anything else, DXGI_ERROR_INVALID_CALL included, which is what a stale duplication
            // has been seen to give instead of DXGI_ERROR_ACCESS_LOST. Rebuilt, not thrown from.
            Log.Info($"the desktop could not be captured ({Com.Describe(hr)}); " +
                     "the duplication is being opened again");
            return CaptureStatus.Lost;
        }

        _holdingFrame = true;

        // Where the pointer is and whether it is shown, according to the duplication itself; the
        // fields are filled in only when they changed, which a non-zero LastMouseUpdateTime says.
        if (info.LastMouseUpdateTime != 0 && _cursor is not null)
        {
            _pointerVisible = info.PointerPosition.Visible != 0;
            _cursor.PositionedAt(info.PointerPosition.Position.X, info.PointerPosition.Position.Y);
        }

        // The shape, handed over only when it has changed. It comes with the frame, so it is taken
        // whether or not the desktop moved: a pointer that changed over a still desktop is common.
        if (info.PointerShapeBufferSize > 0 && _cursor is not null) ReadPointerShape(info);

        LastAccumulatedFrames = info.AccumulatedFrames;

        try
        {
            // Zero accumulated frames means the desktop did not change and only the pointer moved;
            // the copy is still current, and the pointer is drawn when the frame goes out.
            if (info.AccumulatedFrames == 0) return CaptureStatus.Idle;

            if (Com.QueryInterface(resource, D3D11.IID_ID3D11Texture2D, out var texture) < 0)
                return CaptureStatus.Idle;

            try
            {
                D3D11.CopyResource(_context, _frame, texture);
            }
            finally
            {
                Com.Release(texture);
            }

            return CaptureStatus.Frame;
        }
        finally
        {
            Com.Release(resource);

            // Given back the moment the copy is made: the compositor hands over nothing while a
            // frame is held, so everything drawn during the encoding was coalesced into one.
            ReleaseHeldFrame();
        }
    }

    // The DXGI formats a duplicated desktop is ever handed over in, by the numbers in dxgiformat.h.
    private static string FormatName(uint format) => format switch
    {
        0 => "an unstated format",
        10 => "sixteen-bit half floats (R16G16B16A16_FLOAT, an HDR desktop)",
        24 => "ten-bit unorm (R10G10B10A2_UNORM)",
        87 => "eight-bit BGRA (B8G8R8A8_UNORM)",
        _ => $"DXGI format {format}",
    };

    // The pointer's new shape, into the painter. The buffer grows to whatever is asked for and is
    // then kept: a shape is a few kilobytes and this runs whenever the pointer changes.
    private void ReadPointerShape(in DxgiOutduplFrameInfo info)
    {
        if (_shapeBuffer.Length < info.PointerShapeBufferSize)
            _shapeBuffer = new byte[info.PointerShapeBufferSize];

        int hr;
        uint written;
        DxgiOutduplPointerShapeInfo shape;

        fixed (byte* buffer = _shapeBuffer)
        {
            hr = Dxgi.GetFramePointerShape(_duplication, (uint)_shapeBuffer.Length, buffer,
                                           out written, out shape);
        }

        if (hr < 0)
        {
            Log.Info($"the pointer shape could not be read ({Com.Describe(hr)}); " +
                     "the one before it stays");
            return;
        }

        _cursor!.ShapeIs(shape, _shapeBuffer, (int)written);
    }

    // Given back as soon as it is copied and in any case before the next is asked for: holding two
    // is not allowed. Called twice on the ordinary path, which the flag makes free.
    private void ReleaseHeldFrame()
    {
        if (!_holdingFrame) return;
        _holdingFrame = false;
        Dxgi.ReleaseFrame(_duplication);
    }

    // The textures the frame is composed in. Released when the next set is made, not when the
    // duplication is lost: see CreateFrameTexture for why the last picture is kept.
    private void ReleaseFrameTextures()
    {
        Com.ReleaseAndClear(ref _frame);
        Com.ReleaseAndClear(ref _composedSurface);
        Com.ReleaseAndClear(ref _composed);
        Com.ReleaseAndClear(ref _cursorOverlayRtv);
        Com.ReleaseAndClear(ref _cursorOverlaySurface);
        Com.ReleaseAndClear(ref _cursorOverlay);
        _pointerDrawn = false;
    }

    // Opens the duplication again after it was lost; the device is kept, since rebuilding it would
    // invalidate the encoder. The resolution can differ afterwards, so both are rebuilt.
    internal ReopenOutcome Reopen()
    {
        ReleaseHeldFrame();

        Com.ReleaseAndClear(ref _duplication);
        Com.ReleaseAndClear(ref _output1);

        try
        {
            OpenDuplication();
            return ReopenOutcome.Reopened;
        }
        catch (DesktopUnavailableException error)
        {
            // Not a failure, and deliberately not counted as one: the caller waits and asks again.
            Log.Info($"the desktop cannot be duplicated at the moment: {error.Message}");
            return ReopenOutcome.Unavailable;
        }
        catch (Exception error)
        {
            Log.Info($"the desktop is not available yet: {error.Message}");
            return ReopenOutcome.Failed;
        }
    }

    // Turns the one failure everybody meets into an explanation. DuplicateOutput refuses
    // for several unrelated reasons and returns the same kind of code for all of them.
    private static Exception DescribeDuplicationFailure(int hr) => hr switch
    {
        Dxgi.DXGI_ERROR_NOT_CURRENTLY_AVAILABLE => new InvalidOperationException(
            "The desktop cannot be duplicated: Windows already has as many duplications of this " +
            "screen as it allows. Another streaming or recording program is holding one."),

        Dxgi.DXGI_ERROR_ACCESS_DENIED or Dxgi.E_ACCESSDENIED => new DesktopUnavailableException(
            "Windows refuses to duplicate this screen, which is what it answers while the " +
            "sign-in screen, a UAC prompt or the lock screen is in front" +
            (App.PlatformGuard.IsSystem
                ? ". It becomes available again on its own once the desktop is back."
                : ". Only a server running as LocalSystem can capture those; install the service " +
                  "to stream them. It becomes available again on its own once the prompt is gone.")),

        Dxgi.DXGI_ERROR_UNSUPPORTED => new InvalidOperationException(
            "This screen cannot be duplicated. That is what a graphics driver answers when the " +
            "desktop is drawn by something that has no duplication support — the Microsoft Basic " +
            "Display Adapter, or a remote desktop session. Install the graphics driver, and stream " +
            "from the physical screen."),

        Dxgi.DXGI_ERROR_SESSION_DISCONNECTED => new DesktopUnavailableException(
            "There is no session on this screen to duplicate. This happens when the server runs " +
            "in a session that is not the one attached to the console, and while the console is " +
            "being handed from one session to another."),

        _ => new InvalidOperationException(
            $"IDXGIOutput1::DuplicateOutput failed: 0x{hr:X8} ({Com.Describe(hr)})."),
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ReleaseHeldFrame();
        _cursor?.Dispose();

        Com.ReleaseAndClear(ref _duplication);
        Com.ReleaseAndClear(ref _output1);
        ReleaseFrameTextures();
        Com.ReleaseAndClear(ref _context);
        Com.ReleaseAndClear(ref _device);
        Com.ReleaseAndClear(ref _adapter);
    }
}
