//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Net;
using RemoteGameHub.App;
using RemoteGameHub.Library;
using RemoteGameHub.Protocol;
using Xunit;

namespace RemoteGameHub.Tests;

// Who the guard refuses and who it never touches. The counting matters less than the two edges:
// a house on its own network is never locked out, and nothing is refused unless Upnp asked for it.
public class AccessGuardTests
{
    private static AppConfig Forwarded(int failures = 3, int minutes = 15) => new()
    {
        Upnp = true,
        BlockAfterFailures = failures,
        BlockMinutes = minutes,
    };

    private static readonly IPAddress Outside = IPAddress.Parse("203.0.113.7");
    private static readonly IPAddress Inside = IPAddress.Parse("192.168.1.40");
    private static readonly IPAddress AlsoOutside = IPAddress.Parse("198.51.100.3");

    // A guard whose clock is this test's to move: blocks are counted in minutes and the second
    // one only happens after the first has run out, which is not a thing to sit through.
    private static (AccessGuard Guard, Func<double, double> Move) Wound(int failures = 2,
                                                                       int minutes = 15)
    {
        var guard = new AccessGuard(Forwarded(failures, minutes));
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

        guard.Clock = () => now;

        return (guard, forward =>
        {
            now = now.AddMinutes(forward);
            return forward;
        });
    }

    private static void FailTwice(AccessGuard guard, IPAddress address)
    {
        guard.Failed(address, "wrong PIN");
        guard.Failed(address, "wrong PIN");
    }

    [Fact]
    public void An_address_outside_is_refused_once_it_has_spent_its_attempts()
    {
        var guard = new AccessGuard(Forwarded(failures: 3));

        guard.Failed(Outside, "wrong PIN");
        guard.Failed(Outside, "wrong PIN");
        Assert.False(guard.IsBlocked(Outside, out _));

        guard.Failed(Outside, "wrong PIN");

        Assert.True(guard.IsBlocked(Outside, out var left));
        Assert.InRange(left.TotalMinutes, 14, 15);
    }

    [Fact]
    public void An_address_on_this_network_is_never_refused()
    {
        var guard = new AccessGuard(Forwarded(failures: 1));

        guard.Failed(Inside, "wrong PIN");
        guard.Failed(Inside, "wrong PIN");

        Assert.False(guard.IsBlocked(Inside, out _));
        Assert.False(guard.Watches(Inside));
    }

    [Fact]
    public void Nothing_is_refused_while_the_ports_are_not_forwarded()
    {
        var guard = new AccessGuard(new AppConfig { Upnp = false, BlockAfterFailures = 1 });

        guard.Failed(Outside, "wrong PIN");

        Assert.False(guard.IsBlocked(Outside, out _));
        Assert.False(guard.Watches(Outside));
    }

    [Fact]
    public void Zero_attempts_turns_the_whole_thing_off()
    {
        var guard = new AccessGuard(Forwarded(failures: 0));

        guard.Failed(Outside, "wrong PIN");

        Assert.False(guard.IsBlocked(Outside, out _));
    }

    [Fact]
    public void A_pairing_that_finished_forgets_what_came_before_it()
    {
        var guard = new AccessGuard(Forwarded(failures: 3));

        guard.Failed(Outside, "wrong PIN");
        guard.Failed(Outside, "wrong PIN");
        guard.Succeeded(Outside);

        // The two above are spent, so this one is the first of three rather than the last.
        guard.Failed(Outside, "wrong PIN");
        Assert.False(guard.IsBlocked(Outside, out _));
    }

    [Fact]
    public void The_mapped_and_the_plain_spelling_of_one_address_are_the_same_address()
    {
        var guard = new AccessGuard(Forwarded(failures: 2));
        var mapped = IPAddress.Parse("::ffff:203.0.113.7");

        guard.Failed(Outside, "wrong PIN");
        guard.Failed(mapped, "wrong PIN");

        Assert.True(guard.IsBlocked(mapped, out _));
        Assert.True(guard.IsBlocked(Outside, out _));
    }
    [Fact]
    public void A_second_block_in_a_row_lasts_twice_as_long_and_a_third_three_times()
    {
        var (guard, move) = Wound(failures: 2, minutes: 15);

        FailTwice(guard, Outside);
        Assert.True(guard.IsBlocked(Outside, out var first));
        Assert.Equal(15, first.TotalMinutes, 1);

        // Back the moment it is let in again, which is what a second block in a row means.
        move(16);
        FailTwice(guard, Outside);
        Assert.True(guard.IsBlocked(Outside, out var second));
        Assert.Equal(30, second.TotalMinutes, 1);

        move(31);
        FailTwice(guard, Outside);
        Assert.True(guard.IsBlocked(Outside, out var third));
        Assert.Equal(45, third.TotalMinutes, 1);
    }

    [Fact]
    public void A_run_of_blocks_is_forgotten_after_a_quiet_as_long_as_the_last_one()
    {
        var (guard, move) = Wound(failures: 2, minutes: 15);

        FailTwice(guard, Outside);
        Assert.True(guard.IsBlocked(Outside, out _));

        // The block, and then as long again with nothing heard from it: this is somebody who
        // mistyped, went away, and came back another day.
        move(15 + 16);
        FailTwice(guard, Outside);

        Assert.True(guard.IsBlocked(Outside, out var again));
        Assert.Equal(15, again.TotalMinutes, 1);
    }

    [Fact]
    public void A_pairing_that_finished_starts_the_length_again_too()
    {
        var (guard, move) = Wound(failures: 2, minutes: 15);

        FailTwice(guard, Outside);
        move(16);
        FailTwice(guard, Outside);
        Assert.True(guard.IsBlocked(Outside, out var second));
        Assert.Equal(30, second.TotalMinutes, 1);

        guard.Succeeded(Outside);
        Assert.False(guard.IsBlocked(Outside, out _));

        FailTwice(guard, Outside);
        Assert.True(guard.IsBlocked(Outside, out var afterwards));
        Assert.Equal(15, afterwards.TotalMinutes, 1);
    }

    [Fact]
    public void No_block_outlasts_a_day_however_many_have_been_earned()
    {
        var (guard, move) = Wound(failures: 2, minutes: 24 * 60);

        FailTwice(guard, Outside);
        move(24 * 60 + 1);
        FailTwice(guard, Outside);

        Assert.True(guard.IsBlocked(Outside, out var second));
        Assert.Equal(24 * 60, second.TotalMinutes, 1);
    }

    [Fact]
    public void The_list_counts_every_refused_address_and_puts_the_busiest_first()
    {
        var (guard, _) = Wound(failures: 2, minutes: 15);

        FailTwice(guard, AlsoOutside);

        guard.Failed(Outside, "wrong PIN");
        guard.Failed(Outside, "wrong PIN");
        guard.Failed(Outside, "wrong PIN");
        guard.Failed(Outside, "wrong PIN");

        var blocked = guard.Blocked();

        Assert.Equal(2, blocked.Count);
        Assert.Equal(Outside.ToString(), blocked[0].Address);
        Assert.Equal(4, blocked[0].Attempts);
        Assert.Equal(2, blocked[0].Blocks);
        Assert.Equal(AlsoOutside.ToString(), blocked[1].Address);
        Assert.Equal(2, blocked[1].Attempts);
    }

    [Fact]
    public void An_address_let_back_in_is_refused_no_longer_and_counted_from_nothing()
    {
        var (guard, _) = Wound(failures: 2, minutes: 15);

        FailTwice(guard, Outside);
        Assert.True(guard.IsBlocked(Outside, out _));

        Assert.True(guard.Release(Outside.ToString()));
        Assert.False(guard.IsBlocked(Outside, out _));
        Assert.Empty(guard.Blocked());

        // The run of blocks went with the record, so the next one is a first block again.
        FailTwice(guard, Outside);
        Assert.True(guard.IsBlocked(Outside, out var next));
        Assert.Equal(15, next.TotalMinutes, 1);
    }

    [Fact]
    public void Letting_in_an_address_nobody_is_refusing_says_so()
    {
        var (guard, _) = Wound();

        Assert.False(guard.Release(Outside.ToString()));
        Assert.False(guard.Release("not an address"));
        Assert.False(guard.Release(null));
    }

    [Fact]
    public void Nothing_is_listed_while_the_ports_are_not_forwarded()
    {
        var guard = new AccessGuard(new AppConfig { Upnp = false, BlockAfterFailures = 1 });

        guard.Failed(Outside, "wrong PIN");

        Assert.False(guard.IsOn);
        Assert.Empty(guard.Blocked());
    }
    [Fact]
    public void What_is_being_refused_outlives_a_restart()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var store = new BlockStore(database);

        var before = new AccessGuard(Forwarded(failures: 2), store);
        FailTwice(before, Outside);
        Assert.True(before.IsBlocked(Outside, out _));

        // The service starts a fresh worker whenever the person signing in changes, which must not
        // be a way of clearing this.
        var after = new AccessGuard(Forwarded(failures: 2), store);

        Assert.True(after.IsBlocked(Outside, out var left));
        Assert.InRange(left.TotalMinutes, 14, 15);

        var listed = Assert.Single(after.Blocked());
        Assert.Equal(Outside.ToString(), listed.Address);
        Assert.Equal(2, listed.Attempts);
        Assert.Equal(1, listed.Blocks);
    }

    [Fact]
    public void The_run_of_blocks_outlives_a_restart_too_so_the_next_one_is_longer()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var store = new BlockStore(database);

        FailTwice(new AccessGuard(Forwarded(failures: 2), store), Outside);

        var after = new AccessGuard(Forwarded(failures: 2), store);

        // Past the first block, but well inside the quiet that would forget the run.
        var when = DateTime.UtcNow.AddMinutes(16);
        after.Clock = () => when;
        Assert.False(after.IsBlocked(Outside, out _));

        FailTwice(after, Outside);

        Assert.True(after.IsBlocked(Outside, out var second));
        Assert.Equal(30, second.TotalMinutes, 1);
    }

    [Fact]
    public void An_address_let_back_in_stays_let_in_after_a_restart()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var store = new BlockStore(database);

        var before = new AccessGuard(Forwarded(failures: 2), store);
        FailTwice(before, Outside);
        Assert.True(before.Release(Outside.ToString()));

        var after = new AccessGuard(Forwarded(failures: 2), store);

        Assert.False(after.IsBlocked(Outside, out _));
        Assert.Empty(after.Blocked());
    }

    [Fact]
    public void A_pairing_that_finished_is_remembered_across_a_restart_as_well()
    {
        using var folder = new TestFolder();
        using var database = Database.Open(folder.Path);
        var store = new BlockStore(database);

        var before = new AccessGuard(Forwarded(failures: 3), store);
        before.Failed(Outside, "wrong PIN");
        before.Failed(Outside, "wrong PIN");
        before.Succeeded(Outside);

        // The two it fumbled are gone, so the next one is the first of three rather than the last.
        var after = new AccessGuard(Forwarded(failures: 3), store);
        after.Failed(Outside, "wrong PIN");

        Assert.False(after.IsBlocked(Outside, out _));
    }
}
