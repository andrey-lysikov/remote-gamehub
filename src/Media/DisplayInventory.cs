//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Text;
using RemoteGameHub.Native;

namespace RemoteGameHub.Media;

// One output of one adapter, as DXGI sees it.
internal sealed record DisplayOutput(
    int AdapterIndex,
    int OutputIndex,
    string DeviceName,
    Rect Bounds,
    bool AttachedToDesktop)
{
    // The number shown in the log and accepted by [Display] Output.
    internal string Label => $"{AdapterIndex}.{OutputIndex}";

    internal bool IsPrimary => AttachedToDesktop && Bounds.Left == 0 && Bounds.Top == 0;
}

internal sealed record GraphicsAdapter(
    int Index,
    string Name,
    uint VendorId,
    uint DeviceId,
    long DedicatedVideoMemory,
    bool IsSoftware,
    Luid AdapterLuid,
    IReadOnlyList<DisplayOutput> Outputs)
{
    internal bool IsNvidia => VendorId == Dxgi.VendorNvidia;
    internal bool IsAmd => VendorId == Dxgi.VendorAmd;

    // DXGI does not say whether a card is discrete, so this is judged from the memory it owns: an
    // integrated part reports next to none. Used to explain a refusal, never to make one.
    internal bool LooksDiscrete => !IsSoftware && DedicatedVideoMemory >= 512L * 1024 * 1024;

    internal string Vendor => Dxgi.DescribeVendor(VendorId);

    // The adapter description is the one thing an IddCx driver publishes without this server
    // installing anything of its own. Held as fragments: builds append their own words to it.
    private static readonly string[] VirtualDisplayNames =
    [
        "Virtual Display Driver",     // VirtualDrivers/Virtual-Display-Driver, HDR builds included
        "IddSampleDriver",            // Microsoft's IddCx sample and the forks that keep its name
        "Parsec Virtual Display",     // installed with the Parsec host
        "usbmmidd",                   // Amyuni's, which most virtual-display scripts install
        "USB Mobile Monitor",         // the same driver under the name Windows shows for it
        "Virtual Display Adapter",    // Sunshine's fork and others that follow its naming
    ];

    // Matched loosely on purpose: an exact list goes stale with the next release of any of them,
    // and a card that is not a virtual display has none of these words in its description.
    internal bool IsVirtualDisplay =>
        VirtualDisplayNames.Any(known => Name.Contains(known, StringComparison.OrdinalIgnoreCase));
}

// What a screen says about its own colour, in the units the protocol carries: chromaticities
// normalised to fifty thousand, luminance in nits and the minimum in ten-thousandths of one.
internal readonly record struct HdrDisplay(int RedX, int RedY, int GreenX, int GreenY,
                                           int BlueX, int BlueY, int WhiteX, int WhiteY,
                                           int MinLuminance, int MaxLuminance,
                                           int MaxFullFrameLuminance)
{
    // What Rec. 2020 says, for a screen that answers nothing: better than zeros, which some
    // clients read as "black is the brightest this can do".
    internal static HdrDisplay Rec2020 => new(35400, 14600, 8500, 79800, 6550, 2300,
                                              15635, 16450, 50, 1000, 1000);

    internal static HdrDisplay From(in DxgiOutputDesc1 desc)
    {
        static int Chromaticity(float value) => (int)Math.Round(value * 50000.0);

        return new HdrDisplay(
            Chromaticity(desc.RedPrimaryX), Chromaticity(desc.RedPrimaryY),
            Chromaticity(desc.GreenPrimaryX), Chromaticity(desc.GreenPrimaryY),
            Chromaticity(desc.BluePrimaryX), Chromaticity(desc.BluePrimaryY),
            Chromaticity(desc.WhitePointX), Chromaticity(desc.WhitePointY),
            (int)Math.Round(desc.MinLuminance * 10000.0),
            (int)Math.Round(desc.MaxLuminance),
            (int)Math.Round(desc.MaxFullFrameLuminance));
    }
}

// Enumerates what the machine has to stream from, once at startup. Without the list in the log,
// "no output" and "the wrong output" read exactly alike.
internal static unsafe class DisplayInventory
{
    internal static IReadOnlyList<GraphicsAdapter> Enumerate()
    {
        var adapters = new List<GraphicsAdapter>();
        var factory = Dxgi.CreateFactory();

        try
        {
            for (uint index = 0; ; index++)
            {
                var hr = Dxgi.EnumAdapters1(factory, index, out var adapter);
                if (hr == Dxgi.DXGI_ERROR_NOT_FOUND) break;
                Com.Check(hr, "IDXGIFactory1::EnumAdapters1");

                try
                {
                    var desc = Dxgi.GetAdapterDesc(adapter);
                    adapters.Add(new GraphicsAdapter(
                        (int)index,
                        desc.Name,
                        desc.VendorId,
                        desc.DeviceId,
                        (long)desc.DedicatedVideoMemory,
                        (desc.Flags & Dxgi.DXGI_ADAPTER_FLAG_SOFTWARE) != 0,
                        desc.AdapterLuid,
                        EnumerateOutputs(adapter, (int)index)));
                }
                finally
                {
                    Com.Release(adapter);
                }
            }
        }
        finally
        {
            Com.Release(factory);
        }

        return adapters;
    }

    private static IReadOnlyList<DisplayOutput> EnumerateOutputs(void* adapter, int adapterIndex)
    {
        var outputs = new List<DisplayOutput>();

        for (uint index = 0; ; index++)
        {
            var hr = Dxgi.EnumOutputs(adapter, index, out var output);
            if (hr == Dxgi.DXGI_ERROR_NOT_FOUND) break;
            Com.Check(hr, "IDXGIAdapter::EnumOutputs");

            try
            {
                var desc = Dxgi.GetOutputDesc(output);
                outputs.Add(new DisplayOutput(
                    adapterIndex,
                    (int)index,
                    desc.Name,
                    desc.DesktopCoordinates,
                    desc.AttachedToDesktop != 0));
            }
            finally
            {
                Com.Release(output);
            }
        }

        return outputs;
    }

    // Resolves [Display] Output: auto takes the virtual display driver when asked for and found,
    // else the primary screen; a label such as 0.1 an exact output; anything else the device name.
    internal static DisplayOutput? Select(IReadOnlyList<GraphicsAdapter> adapters, string wanted,
                                          bool preferVirtualDisplay, out string reason)
    {
        var attached = adapters
            .SelectMany(a => a.Outputs.Select(o => (Adapter: a, Output: o)))
            .Where(x => x.Output.AttachedToDesktop)
            .ToList();

        if (attached.Count == 0)
        {
            reason = "no output is attached to the desktop";
            return null;
        }

        if (string.Equals(wanted, "auto", StringComparison.OrdinalIgnoreCase))
        {
            if (preferVirtualDisplay)
            {
                var virtualOutput = attached.FirstOrDefault(x => x.Adapter.IsVirtualDisplay).Output;
                if (virtualOutput is not null)
                {
                    reason = "the virtual display driver";
                    return virtualOutput;
                }
            }

            var chosen = attached.FirstOrDefault(x => x.Output.IsPrimary).Output ?? attached[0].Output;
            reason = chosen.IsPrimary ? "primary screen" : "first screen attached to the desktop";
            return chosen;
        }

        var outputs = attached.Select(x => x.Output).ToList();

        var byLabel = outputs.FirstOrDefault(o =>
            string.Equals(o.Label, wanted, StringComparison.OrdinalIgnoreCase));
        if (byLabel is not null)
        {
            reason = $"selected by number {wanted}";
            return byLabel;
        }

        var byName = outputs.FirstOrDefault(o =>
            o.DeviceName.Contains(wanted, StringComparison.OrdinalIgnoreCase));
        if (byName is not null)
        {
            reason = $"selected by name matching \"{wanted}\"";
            return byName;
        }

        reason = $"no screen matches \"{wanted}\"";
        return null;
    }

    // Among every output, attached or not: the log line this drives is about the driver's
    // absence, not about which screen the stream ends up on.
    internal static bool HasVirtualDisplay(IReadOnlyList<GraphicsAdapter> adapters) =>
        adapters.Any(a => a.IsVirtualDisplay);

    // The list as it goes into the log. Every configurable choice is printed with the value that
    // would select it, so that a report can be answered from the log alone.
    internal static string Describe(IReadOnlyList<GraphicsAdapter> adapters)
    {
        var text = new StringBuilder();
        text.Append("graphics adapters and screens:");

        foreach (var adapter in adapters)
        {
            // Invariant, like every number in the log: a decimal comma in one report and a point
            // in the next makes them harder to compare than the numbers are worth.
            var memory = (adapter.DedicatedVideoMemory / (1024.0 * 1024.0 * 1024.0))
                .ToString("0.0", CultureInfo.InvariantCulture);
            text.AppendLine();
            text.Append($"  adapter {adapter.Index}: \"{adapter.Name}\" ({adapter.Vendor}, " +
                        $"device 0x{adapter.DeviceId:X4}, {memory} GB dedicated" +
                        (adapter.IsSoftware ? ", software" : string.Empty) + ")");

            if (adapter.Outputs.Count == 0)
            {
                text.AppendLine();
                text.Append("    no outputs");
                continue;
            }

            foreach (var output in adapter.Outputs)
            {
                var bounds = output.Bounds;
                text.AppendLine();
                text.Append($"    Output = {output.Label}  \"{output.DeviceName}\"  " +
                            $"{bounds.Width}x{bounds.Height} at {bounds.Left},{bounds.Top}" +
                            (output.AttachedToDesktop ? string.Empty : "  (not attached)") +
                            (output.IsPrimary ? "  (primary)" : string.Empty));
            }
        }

        return text.ToString();
    }
}
