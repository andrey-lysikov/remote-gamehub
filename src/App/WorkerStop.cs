//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

namespace RemoteGameHub.App;

// The service asking its worker to close. A named event, because the two are in different sessions
// and share nothing else; both run as SYSTEM, so the event's default security admits them both.
internal static class WorkerStop
{
    // The worker's side: stop is called once, on a pool thread, when the service sets the event.
    internal static IDisposable Listen(Action stop)
    {
        var signal = new EventWaitHandle(false, EventResetMode.ManualReset,
                                         AppParameters.Identity.WorkerStopEvent);
        var registration = ThreadPool.RegisterWaitForSingleObject(
            signal, (_, _) => stop(), null, Timeout.Infinite, executeOnlyOnce: true);

        return new Listening(signal, registration);
    }

    // The service's side. False when nobody is listening: a worker still starting, or one from a
    // release before this, which is then left its few seconds and ended as it always was.
    internal static bool Ask()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(AppParameters.Identity.WorkerStopEvent, out var signal))
                return false;

            using (signal) return signal.Set();
        }
        catch (Exception error)
        {
            Log.Info($"the worker could not be asked to close: {error.Message}");
            return false;
        }
    }

    private sealed class Listening(EventWaitHandle signal, RegisteredWaitHandle registration) : IDisposable
    {
        public void Dispose()
        {
            registration.Unregister(null);
            signal.Dispose();
        }
    }
}
