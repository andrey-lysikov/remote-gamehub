//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using RemoteGameHub.App;
using RemoteGameHub.Library;

namespace RemoteGameHub.Protocol;

// One client that has paired, as it is remembered.
internal sealed record KnownClient(
    long Id,
    string UniqueId,
    string Name,
    string Fingerprint,
    DateTimeOffset PairedAt,
    DateTimeOffset LastSeenAt);

// The clients this machine has met. A client is admitted the moment it pairs, without anyone being
// asked: this store is a memory, never a guest list.
internal sealed class ClientStore
{
    // How long a client that never comes back is kept. Each one left in the list is a certificate
    // that still opens this screen; six months keeps a device used seasonally.
    internal static readonly TimeSpan ForgetAfter = TimeSpan.FromDays(180);

    private readonly Database _database;

    internal ClientStore(Database database) => _database = database;

    // The fingerprint a certificate is recognised by. Comparing whole certificates on every request
    // is work for nothing, and subjects or serial numbers are what a client chose to call itself.
    internal static string Fingerprint(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.RawData));

    internal KnownClient? Find(X509Certificate2 certificate) => Find(Fingerprint(certificate));

    internal KnownClient? Find(string fingerprint)
    {
        lock (_database.Gate)
        {
            using var command = _database.Command(
                "SELECT id, unique_id, name, fingerprint, paired_at, last_seen_at " +
                "FROM clients WHERE fingerprint = $fingerprint;");
            command.Parameters.AddWithValue("$fingerprint", fingerprint);

            using var reader = command.ExecuteReader();
            return reader.Read() ? Read(reader) : null;
        }
    }

    // Records a client that has just paired. Any earlier record of the same device is deleted, not
    // updated: it pairs again with a new certificate, and the old one would still work.
    internal void Admit(string uniqueId, string name, X509Certificate2 certificate)
    {
        var fingerprint = Fingerprint(certificate);
        var now = DateTimeOffset.UtcNow;

        lock (_database.Gate)
        {
            using (var remove = _database.Command(
                       "DELETE FROM clients WHERE fingerprint = $fingerprint " +
                       "OR (unique_id = $uniqueId AND name = $name);"))
            {
                remove.Parameters.AddWithValue("$fingerprint", fingerprint);
                remove.Parameters.AddWithValue("$uniqueId", uniqueId);
                remove.Parameters.AddWithValue("$name", name);

                var replaced = remove.ExecuteNonQuery();
                if (replaced > 0)
                    Log.Event($"\"{name}\" had paired before; the earlier record was removed and replaced");
            }

            using var insert = _database.Command(
                "INSERT INTO clients (unique_id, name, certificate, fingerprint, paired_at, last_seen_at) " +
                "VALUES ($uniqueId, $name, $certificate, $fingerprint, $now, $now);");

            insert.Parameters.AddWithValue("$uniqueId", uniqueId);
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$certificate", certificate.ExportCertificatePem());
            insert.Parameters.AddWithValue("$fingerprint", fingerprint);
            insert.Parameters.AddWithValue("$now", Stamp(now));

            insert.ExecuteNonQuery();
        }

        Log.Event($"\"{name}\" paired and will be remembered; certificate {fingerprint[..16]}…");
    }

    // Marks a client as seen today, which is what keeps it from being forgotten. Called from the
    // ordinary request path: a client merely listed on a television every week is still in use.
    internal void Touch(string fingerprint)
    {
        lock (_database.Gate)
        {
            using var command = _database.Command(
                "UPDATE clients SET last_seen_at = $now WHERE fingerprint = $fingerprint;");

            command.Parameters.AddWithValue("$now", Stamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.ExecuteNonQuery();
        }
    }

    // Removes clients not seen within ForgetAfter. At startup and only there: one removed in the
    // middle of a session would lose the stream for a reason nothing could explain.
    internal void ForgetStale()
    {
        var cutoff = DateTimeOffset.UtcNow - ForgetAfter;
        var removed = new List<string>();

        lock (_database.Gate)
        {
            using (var select = _database.Command(
                       "SELECT name, last_seen_at FROM clients WHERE last_seen_at < $cutoff;"))
            {
                select.Parameters.AddWithValue("$cutoff", Stamp(cutoff));

                using var reader = select.ExecuteReader();
                while (reader.Read())
                    removed.Add($"{reader.GetString(0)} (last seen {reader.GetString(1)[..10]})");
            }

            if (removed.Count == 0) return;

            using var delete = _database.Command("DELETE FROM clients WHERE last_seen_at < $cutoff;");
            delete.Parameters.AddWithValue("$cutoff", Stamp(cutoff));
            delete.ExecuteNonQuery();
        }

        // Named, not counted. A user who finds a device no longer connecting deserves to see it in
        // the log rather than to guess.
        Log.Warn(
            $"{removed.Count} client(s) had not connected for {ForgetAfter.TotalDays:0} days and were\n" +
            "removed. They will ask to pair again the next time they are used:\n" +
            string.Join("\n", removed.Select(name => "    " + name)));
    }

    // Every client this machine remembers, the most recently seen first — which is the order
    // somebody looking for the device in their hand wants them in.
    internal IReadOnlyList<KnownClient> All()
    {
        var clients = new List<KnownClient>();

        lock (_database.Gate)
        {
            using var command = _database.Command(
                "SELECT id, unique_id, name, fingerprint, paired_at, last_seen_at " +
                "FROM clients ORDER BY last_seen_at DESC;");

            using var reader = command.ExecuteReader();
            while (reader.Read()) clients.Add(Read(reader));
        }

        return clients;
    }

    // Removes one client, because somebody asked. The device is not blocked — there is no list of
    // the unwelcome here — it is simply no longer recognised and will ask to pair again.
    internal bool Forget(long id)
    {
        string? name = null;

        lock (_database.Gate)
        {
            using (var select = _database.Command("SELECT name FROM clients WHERE id = $id;"))
            {
                select.Parameters.AddWithValue("$id", id);
                name = select.ExecuteScalar() as string;
            }

            if (name is null) return false;

            using var delete = _database.Command("DELETE FROM clients WHERE id = $id;");
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        Log.Event($"\"{name}\" was forgotten; it will ask to pair again when it is next used");
        return true;
    }

    internal int Count()
    {
        lock (_database.Gate)
        {
            using var command = _database.Command("SELECT COUNT(*) FROM clients;");
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    private static KnownClient Read(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        reader.GetString(2),
        reader.GetString(3),
        Parse(reader.GetString(4)),
        Parse(reader.GetString(5)));

    // Stored as text in a sortable, unambiguous form, so that a comparison in SQL is a string
    // comparison and a person reading the file with any tool sees a date rather than a number.
    private static string Stamp(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string stamp) =>
        DateTime.SpecifyKind(
            DateTime.ParseExact(stamp, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
            DateTimeKind.Utc);
}
