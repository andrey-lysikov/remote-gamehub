//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using Microsoft.Win32;
using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Session;

// What the client sees while a game is still loading: its picture and its name, drawn once into a
// texture and handed to the encoder in place of the desktop, so only the client ever sees it.
internal sealed unsafe class StartingCard : IDisposable
{
    private void* _texture;
    private void* _staging;

    // The texture to encode instead of the desktop. Owned here; do not release it.
    internal nint Texture => (nint)_texture;

    private StartingCard(void* texture, void* staging)
    {
        _texture = texture;
        _staging = staging;
    }

    // Draws the card and puts it on the graphics card. Returns null rather than throwing: a
    // missing picture is a reason to show the desktop, never a reason to lose the stream.
    internal static StartingCard? Create(nint device, nint context, int width, int height,
                                         uint format, string title, string? posterPath)
    {
        if (format != Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM &&
            format != Dxgi.DXGI_FORMAT_R10G10B10A2_UNORM)
        {
            // A format this drawing cannot fill is a desktop shown a little early, not an error.
            Log.Info("the stream is in a format the starting card cannot be drawn in; " +
                     "the desktop is shown while the game loads");
            return null;
        }

        void* texture = null;
        void* staging = null;

        try
        {
            using var picture = Draw(width, height, title, posterPath);

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

            var card = new StartingCard(texture, staging);
            texture = null;
            staging = null;
            return card;
        }
        catch (Exception error)
        {
            Log.Info($"the starting card could not be made: {error.Message}");
            Com.ReleaseAndClear(ref staging);
            Com.ReleaseAndClear(ref texture);
            return null;
        }
    }

    // ------------------------------------------------------------------ the drawing

    private static Bitmap Draw(int width, int height, string title, string? posterPath)
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

            DrawPoster(canvas, area, posterPath, accent, foreground, title);

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
        }

        return picture;
    }

    // The game's own picture when there is one, and a lettered tile when there is not — which
    // is better than a hole, and is what the client would draw in its own list anyway.
    private static void DrawPoster(Graphics canvas, Rectangle area, string? posterPath,
                                   Color accent, Color foreground, string title)
    {
        if (posterPath is not null && File.Exists(posterPath))
        {
            try
            {
                using var poster = Image.FromFile(posterPath);

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
                // A picture that will not open is a picture that is not shown. The tile below
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
            var value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\DWM",
                "ColorizationColor", null);

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
                    ConvertToHdr10(locked, mapped, width, height);
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

    // ------------------------------------------------------------------ the ten-bit form

    // The card as a high-dynamic-range stream expects it: BT.2020 primaries, the ST 2084 curve,
    // ten bits each. The published arithmetic, on the processor because the card is drawn once.
    private static void ConvertToHdr10(BitmapData source, D3D11MappedSubresource destination,
                                       int width, int height)
    {
        // sRGB byte to linear light, exact, all 256 cases.
        var toLinear = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var c = i / 255f;
            toLinear[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        // Linear light to the ST 2084 curve, through a table rather than three powers per pixel.
        // The input is scaled so the card's white lands at 80 cd/m² of the curve's 10000.
        const float m1 = 0.1593017578125f;
        const float m2 = 78.84375f;
        const float c1 = 0.8359375f;
        const float c2 = 18.8515625f;
        const float c3 = 18.6875f;

        var toPq = new ushort[4096];
        for (var i = 0; i < toPq.Length; i++)
        {
            var y = i / (float)(toPq.Length - 1) * (80f / 10000f);
            var p = MathF.Pow(y, m1);
            var pq = MathF.Pow((c1 + c2 * p) / (1f + c3 * p), m2);
            toPq[i] = (ushort)MathF.Round(pq * 1023f);
        }

        for (var y = 0; y < height; y++)
        {
            var row = (byte*)source.Scan0 + (long)y * source.Stride;
            var target = (uint*)((byte*)destination.Data + (long)y * destination.RowPitch);

            for (var x = 0; x < width; x++)
            {
                var b = toLinear[row[x * 4 + 0]];
                var g = toLinear[row[x * 4 + 1]];
                var r = toLinear[row[x * 4 + 2]];

                // BT.709 primaries into BT.2020, the published matrix. Inputs in [0,1] stay in
                // [0,1]: the wider gamut contains the narrower one whole.
                var r2 = 0.6274040f * r + 0.3292820f * g + 0.0433136f * b;
                var g2 = 0.0690970f * r + 0.9195400f * g + 0.0113612f * b;
                var b2 = 0.0163916f * r + 0.0880132f * g + 0.8955950f * b;

                var scale = toPq.Length - 1;
                var rq = (uint)toPq[(int)(Math.Clamp(r2, 0f, 1f) * scale)];
                var gq = (uint)toPq[(int)(Math.Clamp(g2, 0f, 1f) * scale)];
                var bq = (uint)toPq[(int)(Math.Clamp(b2, 0f, 1f) * scale)];

                // R10G10B10A2: red in the lowest bits, alpha in the top two.
                target[x] = rq | (gq << 10) | (bq << 20) | (3u << 30);
            }
        }
    }

    public void Dispose()
    {
        Com.ReleaseAndClear(ref _staging);
        Com.ReleaseAndClear(ref _texture);
    }
}
