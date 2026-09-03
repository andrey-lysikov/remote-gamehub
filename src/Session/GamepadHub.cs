//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.ComponentModel;
using RemoteGameHub.App;

namespace RemoteGameHub.Session;

// The controllers the client brings with it, presented to this machine as real ones: plugged in
// when the client first reports one, unplugged when it goes, so the machine is left as it was.
internal sealed class GamepadHub : IDisposable
{
    // The identifiers of a Microsoft Xbox 360 wired pad. Games look at these to decide which
    // glyphs to draw, so imitating a real one is "A" rather than "Button 1".
    private const ushort VendorMicrosoft = 0x045E;
    private const ushort ProductXbox360Wired = 0x028E;

    // How long a feedback request waits before being asked again. It exists so that a pad being
    // unplugged is noticed promptly; rumble arriving meanwhile completes the request at once.
    private const int FeedbackPollMs = 1000;

    private sealed class Pad
    {
        internal uint Serial;
        internal Thread? Feedback;
        internal volatile bool Stopping;
        internal GamepadState Last = GamepadState.Released;

        // What was last sent back to the client, so that a game repeating itself does not.
        // Touched only by this pad's own feedback thread.
        internal GamepadFeedback LastFeedback;
    }

    private readonly object _gate = new();
    private readonly Pad?[] _pads = new Pad?[ViGEmBus.MaxTargets];
    private readonly ViGEmBus? _bus;

    private bool _disposed;

    // Raised on a thread of its own when a game rumbles a pad. The stream forwards it to the
    // client, which is the only place that can actually shake anything.
    internal event Action<GamepadFeedback>? Feedback;

    // false when the bus is absent: the caller then ignores controller packets.
    internal bool IsAvailable => _bus is not null;

    // Which driver is presenting the pads, for the page and the log. Named rather than assumed:
    // ViGEmBus is the only one this server speaks to today and will not be the only one for ever.
    internal string Driver => _bus is not null ? "ViGEmBus" : "none";

    private GamepadHub(ViGEmBus? bus) => _bus = bus;

    // Opens whatever controller bus this machine has, at startup, so its absence is known then and
    // not mid-game. No setting: an installed driver was installed on purpose. Always returns a hub.
    internal static GamepadHub Open() => new(ViGEmBus.Open());

    // Reports the state of one controller, plugging it in if this is the first time it is seen.
    // Indices are the client's own numbering, zero to three.
    internal void Update(int index, in GamepadState state)
    {
        if (_bus is null || _disposed) return;
        if (index < 0 || index >= _pads.Length) return;

        Pad pad;
        lock (_gate)
        {
            var existing = _pads[index];
            if (existing is null)
            {
                var plugged = PlugIn(index);
                if (plugged is null) return;
                existing = plugged;
            }

            // The same state twice is not worth a trip through the driver, and a client that
            // reports at its own frame rate sends a great many of those.
            if (existing.Last == state) return;

            existing.Last = state;
            pad = existing;
        }

        try
        {
            _bus.Submit(pad.Serial, state.ToReport());
        }
        catch (Win32Exception error)
        {
            Log.Warn($"controller {index} stopped accepting reports ({error.NativeErrorCode}); " +
                     "unplugging it. It will be plugged in again if the client reports it.");
            Remove(index);
        }
    }

    // The controllers the client says it currently has, as a bit per index. Those missing from it
    // are unplugged. Moonlight sends this whenever a pad is connected or removed at its end.
    internal void SetActive(ushort mask)
    {
        if (_bus is null || _disposed) return;

        for (var index = 0; index < _pads.Length; index++)
        {
            if ((mask & (1 << index)) == 0 && _pads[index] is not null)
                Remove(index);
        }
    }

    private Pad? PlugIn(int index)
    {
        uint? serial;
        try
        {
            serial = _bus!.PlugIn(VendorMicrosoft, ProductXbox360Wired);
        }
        catch (Exception error)
        {
            Log.Warn($"could not plug in controller {index}: {error.Message}");
            return null;
        }

        if (serial is null)
        {
            Log.Warn($"the virtual controller bus has no free slot for controller {index}. " +
                     "Another program using ViGEm is probably holding them.");
            return null;
        }

        var pad = new Pad { Serial = serial.Value };
        _pads[index] = pad;

        pad.Feedback = new Thread(() => WatchFeedback(index, pad))
        {
            IsBackground = true,
            Name = $"gamepad {index} feedback",
        };
        pad.Feedback.Start();

        Log.Info($"controller {index} plugged in as an Xbox 360 pad (bus slot {serial.Value})");
        return pad;
    }

    private void Remove(int index)
    {
        Pad? pad;
        lock (_gate)
        {
            pad = _pads[index];
            _pads[index] = null;
        }

        if (pad is null) return;

        pad.Stopping = true;

        // Unplugged first, on purpose: that completes the outstanding feedback request with
        // ERROR_OPERATION_ABORTED and lets its thread end.
        try
        {
            _bus!.Unplug(pad.Serial);
        }
        catch (Exception error)
        {
            Log.Info($"unplugging controller {index} failed: {error.Message}");
        }

        Log.Info($"controller {index} unplugged");
    }

    // One thread per pad, parked on the bus waiting for a game to rumble it. A thread rather than
    // a timer because the wait is a blocking device request that completes when the guest writes.
    private void WatchFeedback(int index, Pad pad)
    {
        while (!pad.Stopping && !_disposed)
        {
            try
            {
                if (!_bus!.WaitForFeedback(pad.Serial, FeedbackPollMs,
                                           out var large, out var small, out var led))
                {
                    continue;
                }

                var feedback = new GamepadFeedback(index, large, small, led);

                // Only when something actually changed: a game that is rumbling writes the same
                // two numbers many times a second, and the control channel is shared with input.
                if (feedback == pad.LastFeedback) continue;

                pad.LastFeedback = feedback;
                Feedback?.Invoke(feedback);
            }
            catch (Exception error)
            {
                if (pad.Stopping || _disposed) return;

                Log.Info($"the feedback request for controller {index} ended: {error.Message}");
                return;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Every pad is unplugged before the handle closes: closing it alone leaves the pads on the
        // bus until the driver notices, and a game goes on seeing controllers nobody reports for.
        for (var index = 0; index < _pads.Length; index++)
        {
            if (_pads[index] is not null) Remove(index);
        }

        _bus?.Dispose();
    }
}
