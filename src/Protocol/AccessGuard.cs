//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Net;
using RemoteGameHub.App;

namespace RemoteGameHub.Protocol;

// One refused address as the page shows it: how many attempts it has made, how many blocks in a
// row it has earned, and how much of the current one is left.
internal readonly record struct BlockedPeer(string Address, int Attempts, int Blocks, TimeSpan Left);

// Counts failed pairing attempts per address and refuses the ones that keep getting the PIN
// wrong. Only ever for addresses off this network, and only while [Network] Upnp is on: without
// forwarding nothing outside can reach these ports, and a house is not a thing to lock out of.
internal sealed class AccessGuard
{
    // One address's history. Failures older than the block itself are of no interest: a wrong
    // PIN this morning and a wrong PIN tonight are two people mistyping, not somebody guessing.
    private sealed class Record
    {
        // The run towards the next block. Starts again after a block, and after a long enough
        // quiet; Attempts below is the number the page shows and does not.
        internal int Failures;

        // Every failed attempt this address has made while this record has lived, blocks and all.
        // The one number that says whether it is a person mistyping or something working through
        // the PIN space.
        internal int Attempts;

        // Blocks in a row, with nothing legitimate in between. Each one lasts this many times
        // [Network] BlockMinutes, so something that keeps coming back is refused for longer and
        // longer while a person who got it wrong once waits the plain fifteen minutes.
        internal int Blocks;

        internal DateTime LastFailure;
        internal DateTime BlockedUntil;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Record> _records = new(StringComparer.OrdinalIgnoreCase);

    private readonly AppConfig _config;

    // Where the same thing is kept between runs, or null for a guard with no memory beyond this
    // process, which is what the tests use.
    private readonly BlockStore? _store;

    internal AccessGuard(AppConfig config, BlockStore? store = null)
    {
        _config = config;
        _store = store;

        Restore();
    }

    // What the last run was refusing. The service starts a fresh worker whenever the person
    // signing in changes, so without this an address could spend a block a restart and never
    // reach the second one, which is the whole of what makes coming back cost more.
    private void Restore()
    {
        if (_store is null) return;

        var now = Clock();
        var blocked = 0;
        var stale = new List<string>();

        lock (_gate)
        {
            foreach (var block in _store.All())
            {
                var record = new Record
                {
                    Attempts = block.Attempts,
                    Blocks = block.Blocks,
                    LastFailure = block.LastFailure,
                    BlockedUntil = block.BlockedUntil,
                };

                _records[block.Address] = record;
                if (record.BlockedUntil > now) blocked++;
            }

            // Rows the machine slept through: the same rule the guard applies while it runs, put
            // to a table that may have sat still for a week.
            stale.AddRange(Forget(now));
        }

        _store.Remove(stale);

        if (_records.Count == 0 && stale.Count == 0) return;

        Log.Info($"{_records.Count} address(es) carried over from the last run, {blocked} of them " +
                 $"still refused; {stale.Count} had been quiet long enough to be forgotten");
    }

    // What the guard calls now. Every span here is measured in minutes or hours, which is not a
    // thing a test can sit through, so the tests move this instead. Nothing else replaces it.
    internal Func<DateTime> Clock { get; set; } = () => DateTime.UtcNow;

    private TimeSpan Block => TimeSpan.FromMinutes(_config.BlockMinutes);

    // What the next block lasts for an address that has already earned this many: the configured
    // span multiplied by the number of blocks in a row, and never more than a day, which is the
    // most [Network] BlockMinutes may be set to in the first place.
    private TimeSpan BlockFor(int blocks)
    {
        var minutes = (long)_config.BlockMinutes * Math.Max(1, blocks);
        return TimeSpan.FromMinutes(Math.Min(minutes, AppParameters.Limits.MaxBlockMinutes));
    }

    // Whether this address is one this guard has anything to say about at all.
    internal bool Watches(IPAddress? address) =>
        _config.Upnp &&
        _config.BlockAfterFailures > 0 &&
        address is not null &&
        !WebConsole.IsPrivate(Peer.Plain(address));

    // Whether anything is counted at all, which is what decides whether the page shows this at
    // all: with the ports unforwarded there is nothing outside that could reach them.
    internal bool IsOn => _config.Upnp && _config.BlockAfterFailures > 0;

    // Whether the address is refused at this moment, and for how much longer. Asked before a
    // request is read, so a blocked address costs one accept and nothing else.
    internal bool IsBlocked(IPAddress? address, out TimeSpan left)
    {
        left = TimeSpan.Zero;
        if (!Watches(address)) return false;

        lock (_gate)
        {
            if (!_records.TryGetValue(Key(address!), out var record)) return false;

            var now = Clock();
            if (record.BlockedUntil <= now) return false;

            left = record.BlockedUntil - now;
            return true;
        }
    }

    // One attempt that did not end in a pairing: a wrong PIN, a signature that did not verify, or
    // an attempt nobody at this machine answered. why goes in the log with the count.
    internal void Failed(IPAddress? address, string why)
    {
        if (!Watches(address)) return;

        var peer = Peer.Describe(address);
        var now = Clock();
        var key = Key(address!);
        int failures;
        int attempts;
        int blocks;
        var span = TimeSpan.Zero;
        bool blocked;

        List<string> swept;
        StoredBlock write;

        lock (_gate)
        {
            swept = Forget(now);

            if (!_records.TryGetValue(key, out var record))
            {
                record = new Record();
                _records[key] = record;
            }

            // A run of failures, not a lifetime tally: the count starts again when the last one
            // is older than a block, so an address that gets it wrong once a day is never refused.
            if (record.Failures > 0 && now - record.LastFailure > Block) record.Failures = 0;

            record.Failures++;
            record.Attempts++;
            record.LastFailure = now;
            failures = record.Failures;
            attempts = record.Attempts;

            blocked = failures >= _config.BlockAfterFailures;
            if (blocked)
            {
                // The second block in a row lasts twice as long as the first, the third three
                // times, and so on. Nothing resets this but a pairing that finished or the button
                // on the page: coming back quiet for an hour is exactly what a scanner does.
                record.Blocks++;
                span = BlockFor(record.Blocks);
                record.BlockedUntil = now + span;
                record.Failures = 0;
            }

            blocks = record.Blocks;
            write = new StoredBlock(key, record.Attempts, record.Blocks, record.LastFailure,
                                    record.BlockedUntil);
        }

        // Outside the lock above, and in this order: a row swept away and written again in the
        // same breath would leave the table saying more than the guard does.
        _store?.Remove(swept);
        _store?.Save(write);

        if (!blocked)
        {
            Log.Warn($"{peer} failed to pair ({why}). That is {failures} of " +
                     $"{_config.BlockAfterFailures} attempts allowed from outside this network " +
                     $"before the address is refused for {_config.BlockMinutes} minute(s).");
            return;
        }

        Log.Warn(
            $"{peer} is refused for the next {span.TotalMinutes:0} minute(s): it failed to pair " +
            $"{_config.BlockAfterFailures} times\nand is not on this network. Connections from it " +
            $"are dropped without being read. It has made {attempts} attempt(s) in all, and this " +
            $"is\nblock {blocks} in a row, each one lasting that many times [Network] BlockMinutes.\n" +
            "What this counts and how long it starts at are [Network] BlockAfterFailures and " +
            "BlockMinutes.");
    }

    // A pairing that finished. The address starts again from nothing: the person got it right,
    // and the attempts they fumbled first should not count towards a later refusal — nor should
    // the blocks they earned lengthen the next one.
    internal void Succeeded(IPAddress? address)
    {
        if (!Watches(address)) return;

        var key = Key(address!);
        bool had;

        lock (_gate)
        {
            had = _records.Remove(key);
        }

        if (!had) return;

        _store?.Remove(new[] { key });
        Log.Info($"{Peer.Describe(address)} paired; its failed attempts are forgotten");
    }

    // The addresses refused right now, the ones that have tried hardest first. What the page
    // lists; the count is the whole of it, and the page shows only the first few.
    internal IReadOnlyList<BlockedPeer> Blocked()
    {
        if (!IsOn) return Array.Empty<BlockedPeer>();

        var now = Clock();
        List<string> swept;
        IReadOnlyList<BlockedPeer> blocked;

        lock (_gate)
        {
            // The page asks for this every few seconds, which is the only sweep this table gets
            // while nobody is knocking.
            swept = Forget(now);

            blocked = _records
                .Where(entry => entry.Value.BlockedUntil > now)
                .Select(entry => new BlockedPeer(entry.Key, entry.Value.Attempts,
                                                 entry.Value.Blocks,
                                                 entry.Value.BlockedUntil - now))
                .OrderByDescending(peer => peer.Attempts)
                .ThenByDescending(peer => peer.Left)
                .ToArray();
        }

        _store?.Remove(swept);
        return blocked;
    }

    // Lets one address back in, from the button on the page. The whole record goes, the run of
    // blocks with it: whoever pressed it said this address is not what the count took it for, so
    // the next block it earns is a first one again.
    internal bool Release(string? address)
    {
        if (!IPAddress.TryParse(address, out var parsed)) return false;

        var key = Key(parsed);

        lock (_gate)
        {
            if (!_records.Remove(key)) return false;
        }

        _store?.Remove(new[] { key });

        Log.Event($"{Peer.Describe(parsed)} was let back in from the page; its attempts and the " +
                  "blocks it had run up are forgotten, and it is counted from nothing again.");
        return true;
    }

    // Addresses whose block has expired and that have been quiet since for as long as that block
    // lasted. The quiet is measured from the end of the block, not from the last attempt: without
    // it the record would go the moment the block did, and every block would be a first one.
    // Answers which addresses it dropped, so that the caller can take them out of the database
    // once it is out of the lock.
    private List<string> Forget(DateTime now)
    {
        var stale = _records
            .Where(entry => entry.Value.BlockedUntil <= now &&
                            now - Later(entry.Value.LastFailure, entry.Value.BlockedUntil) >
                                BlockFor(entry.Value.Blocks))
            .Select(entry => entry.Key)
            .ToList();

        foreach (var key in stale) _records.Remove(key);

        return stale;
    }

    private static DateTime Later(DateTime one, DateTime other) => one > other ? one : other;

    // The address as one string, with the IPv4-mapped IPv6 form folded into the plain one: the
    // listeners are dual-mode, and the same client arrives under either spelling.
    private static string Key(IPAddress address) => Peer.Plain(address).ToString();
}
