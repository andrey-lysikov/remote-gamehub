//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Session;

// What the client sees while a game is still loading, handed to the encoder in place of the
// desktop. Redrawn now and then rather than once, so the spinner under the caption turns.
internal sealed unsafe class StartingCard : IDisposable
{
    // How often the card is redrawn for the spinner's sake. Fast enough to look like it is
    // turning, slow enough that redrawing the whole picture is not real work.
    private static readonly TimeSpan RedrawEvery = TimeSpan.FromMilliseconds(66);

    private void* _texture;
    private void* _staging;
    private readonly void* _context;
    private readonly int _width;
    private readonly int _height;
    private readonly uint _format;
    private readonly string _title;
    private readonly Image? _poster;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastRedraw = TimeSpan.MinValue;

    // The texture to encode instead of the desktop. Owned here; do not release it.
    internal nint Texture => (nint)_texture;

    private StartingCard(void* texture, void* staging, void* context, int width, int height,
                         uint format, string title, Image? poster)
    {
        _texture = texture;
        _staging = staging;
        _context = context;
        _width = width;
        _height = height;
        _format = format;
        _title = title;
        _poster = poster;
    }

    // Draws the card and puts it on the graphics card. Returns null rather than throwing: a
    // missing picture is a reason to show the desktop, never a reason to lose the stream.
    internal static StartingCard? Create(nint device, nint context, int width, int height,
                                         uint format, string title, string? posterPath)
    {
        if (format != Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM &&
            format != Dxgi.DXGI_FORMAT_R16G16B16A16_FLOAT)
        {
            // A format this drawing cannot fill is a desktop shown a little early, not an error.
            Log.Info("the stream is in a format the starting card cannot be drawn in; " +
                     "the desktop is shown while the game loads");
            return null;
        }

        void* texture = null;
        void* staging = null;
        var poster = LoadPoster(posterPath);

        try
        {
            using var picture = Draw(width, height, title, poster, 0);

            var description = new D3D11Texture2DDesc
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = format,
                SampleCount = 1,
                SampleQuality = 0,
                Usage = D3D11.D3D11_USAGE_DEFAULT,
                BindFlags = D3D11.D3D11_BIND_SHADER_RESOURCE | D3D11.D3D11_BIND_RENDER_TARGET,
                CpuAccessFlags = 0,
                MiscFlags = 0,
            };

            texture = D3D11.CreateTexture2D((void*)device, description);

            // The drawing is made on the processor, so it needs a texture the processor may
            // write to, and one copy from that into the one the encoder reads.
            description.Usage = D3D11.D3D11_USAGE_STAGING;
            description.BindFlags = 0;
            description.CpuAccessFlags = D3D11.D3D11_CPU_ACCESS_WRITE;
            staging = D3D11.CreateTexture2D((void*)device, description);

            Upload((void*)context, staging, texture, picture, width, height, format);

            Log.Info($"the client is shown a starting card for \"{title}\"" +
                     (posterPath is null ? " (no picture for it yet)" : string.Empty));

            var card = new StartingCard(texture, staging, (void*)context, width, height, format,
                title, poster);
            texture = null;
            staging = null;
            poster = null;
            return card;
        }
        catch (Exception error)
        {
            Log.Info($"the starting card could not be made: {error.Message}");
            Com.ReleaseAndClear(ref staging);
            Com.ReleaseAndClear(ref texture);
            return null;
        }
        finally
        {
            // Still set only when the card was never built.
            poster?.Dispose();
        }
    }

    // The game's picture, decoded once for the life of the card: it is drawn again fifteen times
    // a second for the spinner, and opening the file each time was a decode per redraw.
    private static Image? LoadPoster(string? posterPath)
    {
        if (posterPath is null || !File.Exists(posterPath)) return null;

        try
        {
            return Image.FromFile(posterPath);
        }
        catch (Exception)
        {
            // A picture that will not open is a picture that is not shown; the tile takes its place.
            return null;
        }
    }

    // Redraws at RedrawEvery's own pace regardless of how often this is called. Never throws: a
    // spinner that stops turning is not a reason to lose the picture behind it.
    internal void Update()
    {
        var now = _clock.Elapsed;
        if (_lastRedraw != TimeSpan.MinValue && now - _lastRedraw < RedrawEvery) return;
        _lastRedraw = now;

        try
        {
            using var picture = Draw(_width, _height, _title, _poster,
                (float)(now.TotalMilliseconds % 1500 / 1500 * 360));
            Upload(_context, _staging, _texture, picture, _width, _height, _format);
        }
        catch (Exception error)
        {
            Log.Info($"the starting card could not be redrawn: {error.Message}");
        }
    }

    // ------------------------------------------------------------------ the drawing

    private static Bitmap Draw(int width, int height, string title, Image? poster,
                               float spinnerAngle)
    {
        var dark = ThemeIcons.AppsAreDark();
        var background = BackgroundOf(dark);
        var foreground = ForegroundOf(dark);
        var accent = AccentOf(foreground);

        var picture = new Bitmap(width, height, PixelFormat.Format32bppArgb);

        using (var canvas = Graphics.FromImage(picture))
        {
            canvas.SmoothingMode = SmoothingMode.AntiAlias;
            canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
            canvas.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            canvas.Clear(background);

            // The poster takes the middle of the screen at the shape a store portrait comes in,
            // the caption under it. Measured from the height, so a phone matches a television.
            var posterHeight = (int)(height * 0.46);
            var posterWidth = posterHeight * 2 / 3;
            var posterTop = (height - posterHeight) / 2 - (int)(height * 0.06);
            var posterLeft = (width - posterWidth) / 2;
            var area = new Rectangle(posterLeft, posterTop, posterWidth, posterHeight);

            DrawPoster(canvas, area, poster, accent, foreground, title);

            using var font = CaptionFont(height);
            using var brush = new SolidBrush(foreground);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Near,
                Trimming = StringTrimming.EllipsisCharacter,
            };

            var caption = new Rectangle(width / 8, area.Bottom + (int)(height * 0.06),
                width * 3 / 4, (int)(height * 0.2));

            canvas.DrawString($"Starting {title}", font, brush, caption, format);

            // Under the text itself, not under the whole (much taller) box it is aligned to the
            // top of: LineAlignment.Near leaves most of caption's own height empty below it.
            var textBottom = caption.Top + (int)font.GetHeight(canvas);
            DrawSpinner(canvas, width, textBottom + (int)(height * 0.025), height, foreground,
                spinnerAngle);
        }

        return picture;
    }

    // Windows' own indeterminate ring, under the caption, so a game taking its time reads as
    // loading rather than as this server having stopped.
    private static void DrawSpinner(Graphics canvas, int width, int top, int height,
                                    Color foreground, float angle)
    {
        var diameter = (int)(height * 0.045);
        var area = new Rectangle(width / 2 - diameter / 2, top, diameter, diameter);

        using var pen = new Pen(foreground, Math.Max(2f, diameter * 0.12f))
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
        };

        canvas.DrawArc(pen, area, angle, 100f);
    }

    // The game's own picture when there is one, and a lettered tile when there is not — which
    // is better than a hole, and is what the client would draw in its own list anyway.
    private static void DrawPoster(Graphics canvas, Rectangle area, Image? poster,
                                   Color accent, Color foreground, string title)
    {
        if (poster is not null)
        {
            try
            {
                // Fitted inside the space rather than stretched to it: a store's wide header
                // picture and its tall portrait are both used, and neither should be distorted.
                var scale = Math.Min((float)area.Width / poster.Width,
                                     (float)area.Height / poster.Height);
                var drawn = new Rectangle(
                    area.Left + (int)((area.Width - poster.Width * scale) / 2),
                    area.Top + (int)((area.Height - poster.Height * scale) / 2),
                    (int)(poster.Width * scale), (int)(poster.Height * scale));

                canvas.DrawImage(poster, drawn);
                return;
            }
            catch (Exception)
            {
                // A picture that will not draw is a picture that is not shown. The tile below
                // takes its place.
            }
        }

        using var tile = new SolidBrush(accent);
        canvas.FillRectangle(tile, area);

        using var letterFont = new Font(FontFamilyName, area.Height * 0.4f, FontStyle.Regular,
            GraphicsUnit.Pixel);
        using var letterBrush = new SolidBrush(foreground);
        using var centred = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };

        var letter = title.Length > 0 ? title[..1].ToUpperInvariant() : "?";
        canvas.DrawString(letter, letterFont, letterBrush, area, centred);
    }

    private const string FontFamilyName = "Segoe UI";

    private static Font CaptionFont(int height) =>
        new(FontFamilyName, height * 0.045f, FontStyle.Regular, GraphicsUnit.Pixel);

    // The greys Windows 11 itself uses behind a full-screen page, so the card reads as part of
    // the machine rather than as something painted over it.
    private static Color BackgroundOf(bool dark) =>
        dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(243, 243, 243);

    private static Color ForegroundOf(bool dark) =>
        dark ? Color.FromArgb(240, 240, 240) : Color.FromArgb(26, 26, 26);

    // The colour this machine has been told to use for itself. It is only ever a background for
    // the lettered tile, so a machine that has never been personalised losing it costs nothing.
    private static Color AccentOf(Color fallback)
    {
        try
        {
            // The signed-in person's, not this process's: as the service's worker this process
            // is SYSTEM, whose own registry has never been personalised.
            var value = UserContext.ReadUserSetting(@"Software\Microsoft\Windows\DWM",
                                                    "ColorizationColor");

            if (value is int packed)
            {
                var colour = Color.FromArgb(packed);
                return Color.FromArgb(255, colour.R, colour.G, colour.B);
            }
        }
        catch (Exception)
        {
            // The personalisation key is not something to fail a stream over.
        }

        return Color.FromArgb(64, fallback);
    }

    // ------------------------------------------------------------------ onto the card

    private static void Upload(void* context, void* staging, void* texture, Bitmap picture,
                               int width, int height, uint format)
    {
        Com.Check(D3D11.Map(context, staging, 0, D3D11.D3D11_MAP_WRITE, out var mapped),
            "ID3D11DeviceContext::Map(the starting card)");

        try
        {
            var locked = picture.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            try
            {
                if (format == Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM)
                {
                    // Format32bppArgb is blue, green, red, alpha in memory on a little-endian
                    // machine, which is exactly what DXGI calls B8G8R8A8: the rows are copied.
                    var source = (byte*)locked.Scan0;
                    var destination = (byte*)mapped.Data;
                    var rowBytes = width * 4;

                    for (var y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(source + (long)y * locked.Stride,
                            destination + (long)y * mapped.RowPitch, mapped.RowPitch, rowBytes);
                    }
                }
                else
                {
                    ConvertToScRgb(locked, mapped, width, height);
                }
            }
            finally
            {
                picture.UnlockBits(locked);
            }
        }
        finally
        {
            D3D11.Unmap(context, staging, 0);
        }

        D3D11.CopyResource(context, texture, staging);
    }

    // ------------------------------------------------------------------ the HDR form

    // The card in the same linear scRGB half floats DXGI hands back for an HDR desktop, so
    // ColourConverter turns it into ten-bit BT.2020 PQ exactly as it would a real captured frame.
    private static void ConvertToScRgb(BitmapData source, D3D11MappedSubresource destination,
                                       int width, int height)
    {
        // sRGB byte to linear light, exact, all 256 cases. scRGB's 1.0 is the same eighty-nit
        // white the drawing was made against, so nothing here is scaled beyond [0,1].
        var toLinear = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255f;
            toLinear[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        for (var y = 0; y < height; y++)
        {
            var row = (byte*)source.Scan0 + (long)y * source.Stride;
            var target = (Half*)((byte*)destination.Data + (long)y * destination.RowPitch);

            for (var x = 0; x < width; x++)
            {
                // Format32bppArgb in memory is blue, green, red, alpha; R16G16B16A16_FLOAT wants
                // red first, so the channels are reordered here rather than by the caller.
                target[x * 4 + 0] = (Half)toLinear[row[x * 4 + 2]];
                target[x * 4 + 1] = (Half)toLinear[row[x * 4 + 1]];
                target[x * 4 + 2] = (Half)toLinear[row[x * 4 + 0]];
                target[x * 4 + 3] = (Half)(row[x * 4 + 3] / 255f);
            }
        }
    }

    public void Dispose()
    {
        _poster?.Dispose();
        Com.ReleaseAndClear(ref _staging);
        Com.ReleaseAndClear(ref _texture);
    }
}
