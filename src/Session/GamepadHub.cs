//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.App;

namespace RemoteGameHub.Session;

// The controllers the client brings with it, presented to this machine as real ones: plugged in
// when first reported, unplugged when gone. Driven by whichever bus this machine has — see Open.
internal sealed class GamepadHub : IDisposable
{
    // How many controllers Moonlight itself ever asks about: activeGamepadMask is one bit per
    // pad and the client numbers them 0 to 3.
    internal const int MaxPads = 4;

    // How long a feedback request waits before being asked again. It exists so that a pad being
    // unplugged is noticed promptly; rumble arriving meanwhile completes the request at once.
    private const int FeedbackPollMs = 1000;

    private sealed class Pad
    {
        internal IGamepadTarget? Target;
        internal Thread? Feedback;
        internal volatile bool Stopping;
        internal GamepadState Last = GamepadState.Released;

        // What was last sent back to the client, so that a game repeating itself does not.
        // Touched only by this pad's own feedback thread.
        internal GamepadFeedback LastFeedback;
    }

    private readonly object _gate = new();
    private readonly Pad?[] _pads = new Pad?[MaxPads];
    private readonly IGamepadBus? _bus;

    // What the client last said about the pad in each slot — ControllerArrived precedes any
    // state for it, so this is always set by the time a slot's first Update plugs it in.
    private readonly GamepadKind[] _kinds = new GamepadKind[MaxPads];

    private bool _disposed;

    // Raised on a thread of its own when a game rumbles a pad. The stream forwards it to the
    // client, which is the only place that can actually shake anything.
    internal event Action<GamepadFeedback>? Feedback;

    // false when no bus is available: the caller then ignores controller packets.
    internal bool IsAvailable => _bus is not null;

    // Which driver is presenting the pads, for the page and the log.
    internal string Driver => _bus?.Name ?? "none";

    private GamepadHub(IGamepadBus? bus) => _bus = bus;

    // Opens whatever controller bus this machine has, at startup.
    internal static GamepadHub Open()
    {
        IGamepadBus? bus = ViGEmGamepadBus.Open();
        return new GamepadHub(bus);
    }

    // Harmless to call after the slot is already plugged in: only the next reconnection uses it.
    internal void SetKind(int index, GamepadKind kind)
    {
        if (index < 0 || index >= _kinds.Length) return;
        _kinds[index] = kind;
    }

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
            pad.Target!.Submit(state);
        }
        catch (Exception error)
        {
            Log.Warn($"controller {index} stopped accepting reports ({error.Message}); " +
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
        var kind = _kinds[index];

        IGamepadTarget? target;
        try
        {
            target = _bus!.PlugIn(index, kind);
        }
        catch (Exception error)
        {
            Log.Warn($"could not plug in controller {index}: {error.Message}");
            return null;
        }

        if (target is null) return null;

        var pad = new Pad { Target = target };
        _pads[index] = pad;

        pad.Feedback = new Thread(() => WatchFeedback(index, pad))
        {
            IsBackground = true,
            Name = $"gamepad {index} feedback",
        };
        pad.Feedback.Start();

        Log.Info($"controller {index} plugged in through {_bus!.Name} as " +
                 (kind == GamepadKind.PlayStation ? "a PlayStation pad" : "an Xbox pad"));
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
            pad.Target?.Dispose();
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
                if (!pad.Target!.WaitForFeedback(FeedbackPollMs, out var large, out var small,
                                                 out var led))
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
