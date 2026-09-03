//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Drawing;
using System.Drawing.Imaging;
using RemoteGameHub.App;
using RemoteGameHub.Native;

namespace RemoteGameHub.Media;

// --capture-test: takes one frame and writes it out as a picture, to settle whether capture is
// what a black screen is, without a client involved. A picture cannot go into the log.
internal static unsafe class CaptureSelfTest
{
    // Long enough for a screen that is simply not changing to change once.
    private const int TotalWaitMs = 3000;

    private const int SliceMs = 100;

    internal static int Run(DisplayOutput output, bool captureCursor, string directory)
    {
        using var duplicator = DesktopDuplicator.Create(output, captureCursor);

        var deadline = Environment.TickCount64 + TotalWaitMs;
        while (Environment.TickCount64 < deadline)
        {
            var status = duplicator.TryCapture(SliceMs);

            switch (status)
            {
                case CaptureStatus.Frame:
                    // The pointer is drawn into the picture that is sent, not into the desktop
                    // copy, so the test has to ask for the same composition a stream does.
                    duplicator.DrawPointer();

                    var path = Path.Combine(directory, AppParameters.Identity.FileBase + "-capture.png");
                    Save(duplicator, path);
                    Log.Info($"capture test: one frame of {duplicator.Width}x{duplicator.Height} " +
                             $"written to {path}");
                    return 0;

                case CaptureStatus.Lost:
                    if (!duplicator.Reopen()) Thread.Sleep(SliceMs);
                    break;

                case CaptureStatus.Unavailable:
                    Thread.Sleep(SliceMs);
                    break;
            }
        }

        // Not a failure of the capture path: an idle desktop produces nothing, by design.
        Log.Warn(
            "capture test: nothing was drawn on the screen within three seconds, so there was no\n" +
            "frame to take. Desktop Duplication reports changes, not a picture on demand.\n" +
            "Run it again with something moving on the screen — a window being dragged is enough.");
        return 2;
    }

    private static void Save(DesktopDuplicator duplicator, string path)
    {
        var device = (void*)duplicator.Device;
        var context = (void*)duplicator.Context;

        // The frame lives on the card, where nothing on the processor can read it. A staging
        // texture is the only surface Direct3D will map, so the frame is copied into one first.
        var desc = new D3D11Texture2DDesc
        {
            Width = (uint)duplicator.Width,
            Height = (uint)duplicator.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Dxgi.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleCount = 1,
            SampleQuality = 0,
            Usage = D3D11.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CpuAccessFlags = D3D11.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        };

        var staging = D3D11.CreateTexture2D(device, desc);
        try
        {
            D3D11.CopyResource(context, staging, (void*)duplicator.FrameTexture);

            Com.Check(D3D11.Map(context, staging, 0, D3D11.D3D11_MAP_READ, out var mapped),
                "ID3D11DeviceContext::Map");

            try
            {
                using var bitmap = new Bitmap(duplicator.Width, duplicator.Height,
                                              PixelFormat.Format32bppArgb);
                var bits = bitmap.LockBits(
                    new Rectangle(0, 0, duplicator.Width, duplicator.Height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                try
                {
                    // Copied a row at a time: the card's pitch is its own business and is almost
                    // never the width in bytes.
                    for (var y = 0; y < duplicator.Height; y++)
                    {
                        Buffer.MemoryCopy(
                            (byte*)mapped.Data + (long)y * mapped.RowPitch,
                            (byte*)bits.Scan0 + (long)y * bits.Stride,
                            bits.Stride,
                            duplicator.Width * 4);
                    }
                }
                finally
                {
                    bitmap.UnlockBits(bits);
                }

                bitmap.Save(path, ImageFormat.Png);
            }
            finally
            {
                D3D11.Unmap(context, staging, 0);
            }
        }
        finally
        {
            Com.Release(staging);
        }
    }
}
