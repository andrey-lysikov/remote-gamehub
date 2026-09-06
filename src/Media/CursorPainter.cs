//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Media;

// Draws the mouse pointer into the captured frame, which duplication hands back without it: from
// the duplication where it reports one, and from Windows on the thread below where it does not.
internal sealed unsafe class CursorPainter
{
    // The three kinds of shape, from DXGI_OUTDUPL_POINTER_SHAPE_TYPE in dxgi1_2.h.
    private const uint Monochrome = 1;
    private const uint Colour = 2;
    private const uint MaskedColour = 4;

    // The frame's size, which the pointer's position is clipped to. Not readonly: the screen
    // changes mode under this class, its own doing and a game's.
    private int _width;
    private int _height;

    // The shape last handed over by the duplication, as a cursor GDI can draw. Owned here.
    private nint _cursor;
    private int _hotspotX;
    private int _hotspotY;

    // The hotspot of whichever shape Wanted settled on, for the Draw that follows it.
    private int _drawHotspotX;
    private int _drawHotspotY;

    // Where the duplication last said the pointer is, in the frame's own coordinates.
    private int _x;
    private int _y;

    // Why the last frame carried no pointer, as a short key rather than the sentence: the sentence
    // holds coordinates, and comparing those made every pixel of movement a new line in the log.
    private string? _silence;

    // Which source the last pointer came from. See Source.
    private string? _source;

    // Whether this machine has a mouse of its own. Without one Windows keeps the pointer hidden and
    // the duplication reports none, although it is on screen and moving with what the client sends.
    private readonly bool _noMouseHere = User32.GetSystemMetrics(User32.SM_MOUSEPRESENT) == 0;

    // What Windows says about the pointer, written by the thread below and read a frame at a time.
    // Under a lock because the three belong together: a shape at another one's position jumps.
    private readonly object _gate = new();
    private nint _windowsShape;
    private int _windowsHotspotX;
    private int _windowsHotspotY;
    private int _windowsX;
    private int _windowsY;
    private bool _windowsVisible;

    private readonly Thread _watch;
    private volatile bool _watching = true;

    // Where the captured screen sits on the desktop. Windows answers in the whole desktop's
    // coordinates while the duplication answers in the frame's own.
    private int _left;
    private int _top;

    // How often Windows is asked. The pointer is drawn once per frame sent, so anything faster
    // than the frame rate is wasted, and anything much slower is a pointer that lags behind.
    private const int WatchIntervalMs = 8;

    internal CursorPainter()
    {
        _watch = new Thread(WatchWindowsPointer)
        {
            IsBackground = true,
            Name = "pointer watch",
        };

        _watch.Start();
    }

    // The frame drawn into and where it sits on the desktop, told again whenever the duplication is
    // opened. The silence is cleared with it: the old size may have been the reason for it.
    internal void FrameIs(int width, int height, Rect bounds)
    {
        _width = width;
        _height = height;
        _left = bounds.Left;
        _top = bounds.Top;
        _silence = null;

        if (_noMouseHere)
        {
            Log.Info("this machine has no mouse of its own, so Windows draws no pointer and the " +
                     "duplication reports none; the pointer in the picture is this server's own");
        }
    }

    // Everything this class knows that the duplication will not say on a machine with no mouse:
    // where the pointer is, whether Windows considers it shown, and what it would look like.
    private void WatchWindowsPointer()
    {
        try
        {
            while (_watching)
            {
                try
                {
                    Look();
                }
                catch (Exception error)
                {
                    Log.Info($"the pointer could not be read from Windows: {error.Message}");
                }

                Thread.Sleep(WatchIntervalMs);
            }
        }
        finally
        {
            Session.InputDesktop.Detach();
        }
    }

    private void Look()
    {
        // GetCursorInfo answers for the desktop this thread is on, and a stale one answers with no
        // pointer state at all — flags and position zero — which reads as a desktop with no pointer.
        Session.InputDesktop.Attach();

        var info = new CursorInfo { Size = sizeof(CursorInfo) };
        if (!User32.GetCursorInfo(ref info)) return;

        var visible = (info.Flags & User32.CURSOR_SHOWING) != 0 &&
                      (info.Flags & User32.CURSOR_SUPPRESSED) == 0;

        // A hidden pointer has no shape: what it would have been is whatever the window under it
        // would show there.
        var shape = info.Cursor != 0 ? info.Cursor : ShapeAt(info.ScreenPosition);

        var hotspotX = 0;
        var hotspotY = 0;

        if (shape != 0 && shape != _windowsShape)
        {
            // GetIconInfo creates two bitmaps every call and leaks them if they are not deleted,
            // so it is asked only when the shape is a different one.
            if (User32.GetIconInfo(shape, out var icon))
            {
                hotspotX = icon.HotspotX;
                hotspotY = icon.HotspotY;

                if (icon.MaskBitmap != 0) User32.DeleteObject(icon.MaskBitmap);
                if (icon.ColorBitmap != 0) User32.DeleteObject(icon.ColorBitmap);
            }
        }
        else
        {
            hotspotX = _windowsHotspotX;
            hotspotY = _windowsHotspotY;
        }

        lock (_gate)
        {
            _windowsShape = shape;
            _windowsHotspotX = hotspotX;
            _windowsHotspotY = hotspotY;
            _windowsX = info.ScreenPosition.X;
            _windowsY = info.ScreenPosition.Y;
            _windowsVisible = visible;
        }
    }

    // What the window under the pointer would show there. The border shapes come from the hit test
    // and nowhere else: a class cursor is the resting one, so a resizing edge was drawn as an arrow.
    private static nint ShapeAt(Native.Point position)
    {
        var window = User32.WindowFromPoint(position);
        if (window == 0) return User32.LoadCursor(0, User32.IDC_ARROW);

        var packed = (nint)((position.Y << 16) | (position.X & 0xFFFF));

        if (User32.SendMessageTimeout(window, User32.WM_NCHITTEST, 0, packed,
                                      User32.SMTO_ABORTIFHUNG, 20, out var hit) != 0)
        {
            var border = (int)hit switch
            {
                User32.HTLEFT or User32.HTRIGHT => User32.IDC_SIZEWE,
                User32.HTTOP or User32.HTBOTTOM => User32.IDC_SIZENS,
                User32.HTTOPLEFT or User32.HTBOTTOMRIGHT => User32.IDC_SIZENWSE,
                User32.HTTOPRIGHT or User32.HTBOTTOMLEFT => User32.IDC_SIZENESW,
                _ => 0,
            };

            if (border != 0) return User32.LoadCursor(0, border);
        }

        var ofTheClass = (nint)User32.GetClassLongPtr(window, User32.GCLP_HCURSOR);
        return ofTheClass != 0 ? ofTheClass : User32.LoadCursor(0, User32.IDC_ARROW);
    }

    // Where the pointer is, as the duplication reports it. Kept because it is reported only when
    // it changes, while the pointer has to be drawn into every frame that goes out.
    internal void PositionedAt(int x, int y)
    {
        _x = x;
        _y = y;
    }

    // A new shape, in the form the duplication hands it over. Called only when it changed, which
    // is what makes building a cursor for it affordable.
    internal void ShapeIs(in DxgiOutduplPointerShapeInfo info, byte[] bits, int length)
    {
        var built = Build(info, bits, length);
        if (built == 0)
        {
            Log.Info($"the pointer shape (kind {info.Type}, {info.Width}x{info.Height}) " +
                     "could not be made into a cursor; the one before it stays");
            return;
        }

        if (_cursor != 0) User32.DestroyIcon(_cursor);

        _cursor = built;
        _hotspotX = info.HotSpot.X;
        _hotspotY = info.HotSpot.Y;
    }

    // Whether there is a pointer to draw and where, without touching the picture. Asked before the
    // frame is copied: inside a game there is no pointer, and copying to change nothing is 4 GB/s.
    internal bool Wanted(bool visible, out nint cursor, out int x, out int y)
    {
        // The duplication only while it is really reporting a pointer: a shape it handed over once
        // would otherwise be drawn for ever at the place the reports stopped, which is where they come from.
        if (_cursor != 0 && visible)
        {
            Source("the duplication");

            cursor = _cursor;
            x = _x;
            y = _y;
            _drawHotspotX = _hotspotX;
            _drawHotspotY = _hotspotY;
        }
        else
        {
            Source("Windows");

            // Nothing from the duplication: a machine with no mouse, where Windows draws no
            // pointer for it to report. What Windows knows, and the shape guessed from the window.
            bool windowsVisible;

            lock (_gate)
            {
                cursor = _windowsShape;
                x = _windowsX - _left;
                y = _windowsY - _top;
                _drawHotspotX = _windowsHotspotX;
                _drawHotspotY = _windowsHotspotY;
                windowsVisible = _windowsVisible;
            }

            if (!windowsVisible && !_noMouseHere)
            {
                Silent("hidden", "neither the duplication nor Windows reports a pointer on this " +
                                 "desktop");
                return false;
            }
        }

        if (cursor == 0)
        {
            Silent("shapeless", "there is no pointer shape to draw: the duplication has handed " +
                                "none over and Windows has none either");
            return false;
        }

        if (x < 0 || y < 0 || x >= _width || y >= _height)
        {
            // The key is the case, not the position: keying on the sentence made a pointer moving
            // on another screen write a new line for every pixel, up to the frame rate.
            Silent("outside", $"the pointer is at {x},{y}, outside the captured " +
                              $"{_width}x{_height} screen");
            return false;
        }

        Drawn();
        return true;
    }

    // Draws the shape onto a surface of the frame texture, which must have been created
    // GDI-compatible. The surface is the caller's, held for as long as the texture is.
    internal void Draw(void* surface, nint cursor, int x, int y)
    {
        if (Dxgi.GetDC(surface, out var dc) < 0)
        {
            Silent("no dc", "the frame texture would not give up a device context");
            return;
        }

        try
        {
            // DrawIconEx places the drawing's top-left corner, not its hotspot, so the hotspot
            // is subtracted: without it every click on the client lands beside its target.
            User32.DrawIconEx(dc, x - _drawHotspotX, y - _drawHotspotY, cursor,
                              0, 0, 0, 0, User32.DI_NORMAL);
        }
        finally
        {
            Dxgi.ReleaseDC(surface);
        }
    }

    internal void Dispose()
    {
        _watching = false;
        _watch.Join(200);

        // Only the shape built here is destroyed. What Windows handed over is the system's own,
        // shared by every window that asks for it.
        if (_cursor == 0) return;

        User32.DestroyIcon(_cursor);
        _cursor = 0;
    }

    // One cursor out of one shape: the monochrome kind is two masks stacked in one bitmap, and the
    // colour kinds a picture whose alpha is transparency in the one and the AND mask in the other.
    private static nint Build(in DxgiOutduplPointerShapeInfo info, byte[] bits, int length)
    {
        if (info.Width <= 0 || info.Height <= 0 || info.Pitch <= 0) return 0;

        var icon = new User32.IconInfo
        {
            IsIcon = 0,
            HotspotX = info.HotSpot.X,
            HotspotY = info.HotSpot.Y,
        };

        try
        {
            switch (info.Type)
            {
                case Monochrome:
                    if (length < info.Pitch * info.Height) return 0;
                    icon.MaskBitmap = MonochromeBitmap(bits, info.Width, info.Height, info.Pitch);
                    break;

                case Colour:
                    if (length < info.Pitch * info.Height) return 0;
                    // Nothing is masked out: the alpha channel of the picture is the transparency,
                    // and GDI blends it.
                    icon.MaskBitmap = MonochromeBitmap(null, info.Width, info.Height, 0);
                    icon.ColorBitmap = ColourBitmap(bits, info.Width, info.Height, info.Pitch,
                                                    masked: false);
                    break;

                case MaskedColour:
                    if (length < info.Pitch * info.Height) return 0;
                    // Here the alpha channel is the AND mask instead: zero means the colour is
                    // copied, 0xFF that it is exclusive-ored with what is underneath.
                    icon.MaskBitmap = MaskFromAlpha(bits, info.Width, info.Height, info.Pitch);
                    icon.ColorBitmap = ColourBitmap(bits, info.Width, info.Height, info.Pitch,
                                                    masked: true);
                    break;

                default:
                    return 0;
            }

            if (icon.MaskBitmap == 0) return 0;
            if (info.Type != Monochrome && icon.ColorBitmap == 0) return 0;

            return User32.CreateIconIndirect(ref icon);
        }
        finally
        {
            // CreateIconIndirect copies both, so what was built here is finished with either way.
            if (icon.MaskBitmap != 0) User32.DeleteObject(icon.MaskBitmap);
            if (icon.ColorBitmap != 0) User32.DeleteObject(icon.ColorBitmap);
        }
    }

    // The AND and XOR masks as one one-bit bitmap. Repacked rather than handed over as it arrives:
    // GDI wants rows aligned to two bytes and the duplication's pitch is its own.
    private static nint MonochromeBitmap(byte[]? bits, int width, int height, int pitch)
    {
        var stride = (width + 15) / 16 * 2;
        var packed = new byte[stride * height];

        if (bits is not null)
        {
            var row = (width + 7) / 8;
            for (var y = 0; y < height; y++)
                Array.Copy(bits, y * pitch, packed, y * stride, Math.Min(row, stride));
        }

        return User32.CreateBitmap(width, height, 1, 1, packed);
    }

    // The AND mask of a masked-colour shape, which its alpha channel carries: 0xFF is a pixel
    // exclusive-ored with the screen, zero one copied over it.
    private static nint MaskFromAlpha(byte[] bits, int width, int height, int pitch)
    {
        var stride = (width + 15) / 16 * 2;
        var packed = new byte[stride * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (bits[y * pitch + x * 4 + 3] == 0) continue;
                packed[y * stride + x / 8] |= (byte)(0x80 >> (x % 8));
            }
        }

        return User32.CreateBitmap(width, height, 1, 1, packed);
    }

    // The colour picture, top row first. A masked shape's alpha is the mask above rather than
    // transparency, and is zeroed here so that GDI takes the AND/XOR path instead of blending.
    private static nint ColourBitmap(byte[] bits, int width, int height, int pitch, bool masked)
    {
        var header = new User32.BitmapInfoHeader
        {
            Size = (uint)sizeof(User32.BitmapInfoHeader),
            Width = width,
            // Negative for rows from the top down, which is the order the shape arrives in.
            Height = -height,
            Planes = 1,
            BitCount = 32,
            Compression = User32.BI_RGB,
        };

        var bitmap = User32.CreateDIBSection(0, ref header, User32.DIB_RGB_COLORS,
                                             out var pixels, 0, 0);
        if (bitmap == 0 || pixels == 0) return 0;

        var target = (byte*)pixels;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var from = y * pitch + x * 4;
                var to = (y * width + x) * 4;

                target[to] = bits[from];
                target[to + 1] = bits[from + 1];
                target[to + 2] = bits[from + 2];
                target[to + 3] = masked ? (byte)0 : bits[from + 3];
            }
        }

        return bitmap;
    }

    // Which of the two the pointer is taken from, written only when it changes: the answer decides
    // both what is drawn and where it goes.
    private void Source(string source)
    {
        if (source == _source) return;

        _source = source;
        Log.Info($"the pointer is being taken from {source}");
    }

    // Said once per case, and again only when the case changes. The sentence is built by the
    // caller either way; the key is what decides whether it is written.
    private void Silent(string key, string reason)
    {
        if (key == _silence) return;

        _silence = key;
        Log.Info($"the pointer is not being drawn into the picture: {reason}");
    }

    private void Drawn()
    {
        if (_silence is null) return;

        _silence = null;
        Log.Info("the pointer is being drawn into the picture again");
    }
}
