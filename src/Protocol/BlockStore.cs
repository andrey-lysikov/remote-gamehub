//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using RemoteGameHub.App;
using RemoteGameHub.Library;

namespace RemoteGameHub.Protocol;

// One refused address as the database keeps it. The guard works from its own table in memory and
// writes through to this: a restart is common — the service moves its worker whenever the person
// signing in changes — and a run of failures that starts again at every restart counts nothing.
internal sealed record StoredBlock(string Address, int Attempts, int Blocks,
                                   DateTime LastFailure, DateTime BlockedUntil);

// Where AccessGuard keeps what it knows between runs. Small and written rarely: a row appears the
// first time an address off this network fails, and goes when it pairs, is let in from the page,
// or has been quiet long enough for the guard to forget it.
internal sealed class BlockStore
{
    private readonly Database _database;

    internal BlockStore(Database database) => _database = database;

    // Every address on record, blocked or merely counted. Read once, at the start.
    internal IReadOnlyList<StoredBlock> All()
    {
        var blocks = new List<StoredBlock>();

        lock (_database.Gate)
        {
            using var command = _database.Command(
                "SELECT address, attempts, blocks, last_failure, blocked_until FROM blocks;");

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                blocks.Add(new StoredBlock(reader.GetString(0), reader.GetInt32(1),
                                           reader.GetInt32(2), Parse(reader.GetString(3)),
                                           Parse(reader.GetString(4))));
            }
        }

        return blocks;
    }

    // Writes one address's state over whatever was there. Called with the guard's lock already
    // let go: this takes the database's own, and the two are never wanted at once.
    internal void Save(StoredBlock block)
    {
        try
        {
            lock (_database.Gate)
            {
                using var command = _database.Command(
                    "INSERT INTO blocks (address, attempts, blocks, last_failure, blocked_until) " +
                    "VALUES ($address, $attempts, $blocks, $lastFailure, $blockedUntil) " +
                    "ON CONFLICT (address) DO UPDATE SET attempts = $attempts, blocks = $blocks, " +
                    "last_failure = $lastFailure, blocked_until = $blockedUntil;");

                command.Parameters.AddWithValue("$address", block.Address);
                command.Parameters.AddWithValue("$attempts", block.Attempts);
                command.Parameters.AddWithValue("$blocks", block.Blocks);
                command.Parameters.AddWithValue("$lastFailure", Stamp(block.LastFailure));
                command.Parameters.AddWithValue("$blockedUntil", Stamp(block.BlockedUntil));

                command.ExecuteNonQuery();
            }
        }
        catch (Exception error)
        {
            // Never worth failing a request over: the guard has the same thing in memory and goes
            // on refusing with it. All that is lost is the count surviving a restart.
            Log.Warn($"the refusal of {block.Address} could not be written to the database " +
                     $"({error.Message}); it still holds until this server is restarted");
        }
    }

    // Drops the addresses named, because they paired, were let in from the page, or went quiet
    // long enough. Any that are not there were never written, which is not a fault.
    internal void Remove(IEnumerable<string> addresses)
    {
        var list = addresses as IReadOnlyCollection<string> ?? addresses.ToArray();
        if (list.Count == 0) return;

        try
        {
            lock (_database.Gate)
            {
                foreach (var address in list)
                {
                    using var command = _database.Command("DELETE FROM blocks WHERE address = $address;");
                    command.Parameters.AddWithValue("$address", address);
                    command.ExecuteNonQuery();
                }
            }
        }
        catch (Exception error)
        {
            Log.Warn($"a refusal could not be removed from the database ({error.Message}); it is " +
                     "gone from this run either way, and the row is overwritten the next time " +
                     "that address fails");
        }
    }

    // The same spelling ClientStore uses: sortable, unambiguous, and a date to anybody who opens
    // the file with a tool of their own.
    private static string Stamp(DateTime moment) =>
        moment.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static DateTime Parse(string stamp) =>
        DateTime.SpecifyKind(
            DateTime.ParseExact(stamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeKind.Utc);
}
