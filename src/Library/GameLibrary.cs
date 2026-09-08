//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Globalization;
using RemoteGameHub.App;

namespace RemoteGameHub.Library;

// One game as a scanner reports it, before it has a database row.
internal sealed record ScannedGame(
    string Source,
    string? ExternalId,
    string Title,
    string LaunchCommand,
    string? InstallPath,
    string? BoxArtPath);

// One game as /applist needs it.
internal sealed record ListedGame(long Id, string Title);

// A game with no picture yet, as the artwork worker needs to search for one.
internal sealed record ArtworkCandidate(long Id, string Source, string? ExternalId, string Title);

// Everything needed to start one game and to recognise it afterwards.
internal sealed record LaunchTarget(string Command, string? InstallPath, string Title,
                                    bool Pointer, StreamQuality Quality, bool ShowCard);

// The games this machine has, as of the last scan: once at every start, after preflight, and again
// only when somebody asks for it. Between scans the database is truth.
internal sealed class GameLibrary
{
    private readonly Database _database;

    // How long a game that has gone is kept out of sight before its row is really deleted, so that
    // a reinstall finds the corrected title and the chosen cover waiting.
    internal static readonly TimeSpan KeepRemoved = TimeSpan.FromDays(120);

    internal GameLibrary(Database database) => _database = database;

    // Scans every source and makes the table match what was found. first_seen_at survives the
    // rescan; rows the scan did not touch are marked gone, not deleted, and wait KeepRemoved.
    internal void Rescan(AppConfig config)
    {
        // The order is the priority: the first source to claim a title keeps it, sorted by how well
        // each source knows what it offers. Reordering these lines is the whole mechanism.
        var sources = new (string Name, bool Enabled, Func<IReadOnlyList<ScannedGame>> Scan)[]
        {
            ("Steam", config.Steam, SteamScanner.Scan),
            ("Xbox", config.Xbox, XboxScanner.Scan),
            ("Epic", config.Epic, LauncherScanners.Epic),
            ("GOG", config.Gog, LauncherScanners.Gog),
            ("EA", config.Ea, LauncherScanners.Ea),
            ("Battle.net", config.BattleNet, LauncherScanners.BattleNet),
            ("folders", config.GamesFolders.Count > 0,
                () => FolderScanner.Scan(config.GamesFolders, config.GamesDepth)),
        };

        var found = new List<ScannedGame>();
        var counts = new List<string>();

        // One entry per title, the first source to claim it keeping it. The sources overlap by
        // nature: a Steam game is also an installed program and may sit in a scanned folder too.
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The games a person entered on the page are claimed before any scanner is asked, so a
        // scanner that finds the same game is the one that gives way.
        var manual = ManualRows();
        var manualByKey = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in manual)
        {
            claimed[TitleKey(row.Title)] = "you";
            manualByKey[TitleKey(row.Title)] = row.Id;
        }

        // Manual rows a scanner did meet, by way of the claim above. They are not upserted, so
        // without this they carry no current stamp and the sweep would decide they had gone.
        var manualSeen = new HashSet<long>();

        foreach (var (name, enabled, scan) in sources)
        {
            if (!enabled) continue;

            var games = new List<ScannedGame>();
            var scanned = 0;

            foreach (var game in scan())
            {
                scanned++;
                var key = TitleKey(game.Title);

                if (claimed.TryGetValue(key, out var owner))
                {
                    // Said out loud: a game that starts wrongly is usually one that was found
                    // twice and kept from the wrong side.
                    Log.Info($"    \"{game.Title}\" was also found by {name}, and is kept as " +
                             $"{owner} found it");

                    if (manualByKey.TryGetValue(key, out var manualId)) manualSeen.Add(manualId);
                    continue;
                }

                claimed[key] = name;
                games.Add(game);
            }

            found.AddRange(games);

            // New games, and how many were found in all: "0 from Steam" beside three manifests
            // reads as a broken scan, when it only means all three were already known.
            counts.Add(scanned == games.Count
                ? $"{games.Count} from {name}"
                : $"{games.Count} new of {scanned} from {name}");
        }

        // One stamp for the whole scan, so that "touched by this scan" is an equality test. The
        // deletion below removes every row carrying any other stamp.
        var stamp = Stamp(DateTimeOffset.UtcNow);

        // Manual rows no scanner met. Whether they are gone is asked of the file system, and only
        // where there is something to ask about: a steam:// or shell: command is kept instead.
        var manualGone = manual
            .Where(row => !manualSeen.Contains(row.Id) && IsGoneFromDisk(row))
            .Select(row => row.Id)
            .ToList();

        lock (_database.Gate)
        {
            using (var begin = _database.Command("BEGIN IMMEDIATE;")) begin.ExecuteNonQuery();

            try
            {
                foreach (var game in found) Upsert(game, stamp);

                // The manual rows a scanner did meet: seen now, and back if they had gone.
                foreach (var id in manualSeen)
                {
                    using var touch = _database.Command(
                        "UPDATE games SET last_seen_at = $stamp, removed_at = NULL WHERE id = $id;");
                    touch.Parameters.AddWithValue("$stamp", stamp);
                    touch.Parameters.AddWithValue("$id", id);
                    touch.ExecuteNonQuery();
                }

                // The manual rows whose files are gone: given the same stamp as everything else
                // that was not found, so the one sweep below covers them too.
                foreach (var id in manualGone)
                {
                    using var age = _database.Command(
                        "UPDATE games SET last_seen_at = '' WHERE id = $id;");
                    age.Parameters.AddWithValue("$id", id);
                    age.ExecuteNonQuery();
                }

                // Everything else somebody entered stays as it is, carrying whatever stamp it had.
                using (var keep = _database.Command(
                           "UPDATE games SET last_seen_at = $stamp " +
                           "WHERE manual = 1 AND last_seen_at <> '' AND last_seen_at <> $stamp;"))
                {
                    keep.Parameters.AddWithValue("$stamp", stamp);
                    keep.ExecuteNonQuery();
                }

                // Not deleted: marked gone, for every row this scan did not touch, whoever put it
                // there. A corrected title and a chosen cover wait if the game comes back.
                int removed;
                using (var mark = _database.Command(
                           "UPDATE games SET removed_at = $stamp " +
                           "WHERE last_seen_at <> $stamp AND removed_at IS NULL;"))
                {
                    mark.Parameters.AddWithValue("$stamp", stamp);
                    removed = mark.ExecuteNonQuery();
                }

                using (var commit = _database.Command("COMMIT;")) commit.ExecuteNonQuery();

                if (removed > 0)
                {
                    Log.Info($"{removed} game(s) from the last scan are gone and were hidden. " +
                             $"What was edited about them is kept for " +
                             $"{KeepRemoved.TotalDays:0} days in case they come back.");
                }
            }
            catch
            {
                using (var rollback = _database.Command("ROLLBACK;")) rollback.ExecuteNonQuery();
                throw;
            }
        }

        PruneRemoved();

        Log.Info(counts.Count == 0
            ? "game scan: every source is turned off in [Games]"
            : "game scan: " + string.Join(", ", counts));

        // The launch command is printed with the title: the title comes from the store, the command
        // is what is handed to Windows, and only the second can be wrong invisibly from the client.
        foreach (var game in found.OrderBy(g => g.Title, StringComparer.OrdinalIgnoreCase))
            Log.Info($"    [{game.Source}] {game.Title}\n        {game.LaunchCommand}");
    }

    // What counts as the same game when two sources both offer one: a trailing version clause in
    // either language is dropped, then all that is not a letter or a digit. Blunt on purpose.
    private static string TitleKey(string title)
    {
        var text = title;

        foreach (var marker in new[] { ", версия", ", version", " версия ", " version " })
        {
            var at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (at > 0) text = text[..at];
        }

        return new string(text.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    private void Upsert(ScannedGame game, string stamp)
    {
        using var command = _database.Command(
            """
            INSERT INTO games (source, external_id, title, launch_command,
                               install_path, box_art_path, first_seen_at, last_seen_at)
            VALUES ($source, $external, $title, $launch, $install, $art, $stamp, $stamp)
            ON CONFLICT (source, launch_command) DO UPDATE SET
                external_id  = excluded.external_id,
                -- What somebody typed outlives what a scanner reads. An edit is a statement that
                -- the scanner got the name or the folder wrong, and a scan that overwrote it would
                -- undo that at every start without saying so.
                title        = CASE WHEN games.manual = 1 THEN games.title ELSE excluded.title END,
                install_path = CASE WHEN games.manual = 1
                                    THEN games.install_path ELSE excluded.install_path END,
                -- The game is here again. Whatever was edited about it before it went is exactly
                -- what this row still holds, which is the reason it was kept rather than deleted.
                removed_at   = NULL,
                -- The scan only ever finds art the store keeps locally, and finds none for most
                -- games. A picture fetched from the online catalogue must survive the next scan,
                -- so a scanner with nothing to say leaves what is already there alone.
                box_art_path = COALESCE(excluded.box_art_path, games.box_art_path),
                last_seen_at = excluded.last_seen_at;
            """);

        command.Parameters.AddWithValue("$source", game.Source);
        command.Parameters.AddWithValue("$external", (object?)game.ExternalId ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", game.Title);
        command.Parameters.AddWithValue("$launch", game.LaunchCommand);
        command.Parameters.AddWithValue("$install", (object?)game.InstallPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$art", (object?)game.BoxArtPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$stamp", stamp);

        command.ExecuteNonQuery();
    }

    // Everything /applist serves, ordered by title. The identifiers here are database row ids; the
    // protocol adds AppParameters.Protocol.GameAppIdOffset before a client sees them.
    internal IReadOnlyList<ListedGame> List()
    {
        var games = new List<ListedGame>();

        lock (_database.Gate)
        {
            using var command = _database.Command(
                "SELECT id, title FROM games WHERE removed_at IS NULL " +
                "ORDER BY title COLLATE NOCASE;");

            using var reader = command.ExecuteReader();
            while (reader.Read())
                games.Add(new ListedGame(reader.GetInt64(0), reader.GetString(1)));
        }

        return games;
    }

    // How many games the list would hold. Asked once a second by the page, which used to run the
    // whole of List and its sort to read one number off the end of it.
    internal int Count()
    {
        lock (_database.Gate)
        {
            using var command = _database.Command(
                "SELECT COUNT(*) FROM games WHERE removed_at IS NULL;");

            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    // Really deletes the games that went more than KeepRemoved ago, and the covers kept for them.
    // Run at the end of every scan, the only moment this list is rewritten anyway.
    private void PruneRemoved()
    {
        var cutoff = Stamp(DateTimeOffset.UtcNow - KeepRemoved);
        var gone = new List<(string Title, string? Art)>();

        lock (_database.Gate)
        {
            using (var select = _database.Command(
                       "SELECT title, box_art_path FROM games " +
                       "WHERE removed_at IS NOT NULL AND removed_at < $cutoff;"))
            {
                select.Parameters.AddWithValue("$cutoff", cutoff);

                using var reader = select.ExecuteReader();
                while (reader.Read())
                    gone.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }

            if (gone.Count == 0) return;

            using var delete = _database.Command(
                "DELETE FROM games WHERE removed_at IS NOT NULL AND removed_at < $cutoff;");
            delete.Parameters.AddWithValue("$cutoff", cutoff);
            delete.ExecuteNonQuery();
        }

        foreach (var (title, art) in gone) DeleteOurCover(title, art);

        Log.Info($"{gone.Count} game(s) had been gone for more than {KeepRemoved.TotalDays:0} days " +
                 "and were forgotten: " +
                 string.Join(", ", gone.Select(g => $"\"{g.Title}\"")));
    }

    // Deletes a cover, but only one this server fetched: a store's own artwork lives in the
    // store's folder and is not ours to delete.
    private static void DeleteOurCover(string? title, string? art)
    {
        if (art is null || !art.Contains(CoverArt.Folder, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            File.Delete(art);
        }
        catch (Exception error)
        {
            Log.Info($"the cover of \"{title}\" could not be deleted: {error.Message}");
        }
    }

    // One game a person entered or edited, as the scan needs to see it.
    private sealed record ManualRow(long Id, string Title, string LaunchCommand, string? InstallPath);

    // The games a person put there, which the scan must not overwrite. Rows already marked gone are
    // included: a scanner meeting that title again is how they come back.
    private IReadOnlyList<ManualRow> ManualRows()
    {
        var rows = new List<ManualRow>();

        lock (_database.Gate)
        {
            using var command = _database.Command(
                "SELECT id, title, launch_command, install_path FROM games WHERE manual = 1;");

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                rows.Add(new ManualRow(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        return rows;
    }

    // Whether a game somebody entered has plainly been uninstalled: only ever true for a folder
    // that was named and has gone, or a command that is a path. An address answers no.
    private static bool IsGoneFromDisk(ManualRow row)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(row.InstallPath))
                return !Directory.Exists(row.InstallPath);

            var command = row.LaunchCommand.Trim().Trim('"');

            var looksLikeAPath = command.Length > 3 &&
                                 (command[1] == ':' || command.StartsWith(@"\\", StringComparison.Ordinal));

            return looksLikeAPath && !File.Exists(command) && !Directory.Exists(command);
        }
        catch (Exception)
        {
            // A path that cannot even be asked about — a drive that is not there, a name Windows
            // refuses — is not a game to remove on that evidence.
            return false;
        }
    }

    // Everything the page shows for one row. ArtStamp is when the picture was last written, zero
    // when there is none: the page hangs it on the cover's address so a new one is fetched.
    internal sealed record GameDetail(long Id, string Source, string Title, string LaunchCommand,
                                      string? InstallPath, long ArtStamp, bool Manual, bool ArtManual,
                                      bool Pointer, StreamQuality Quality, bool ShowCard);

    internal IReadOnlyList<GameDetail> Details()
    {
        var games = new List<GameDetail>();

        lock (_database.Gate)
        {
            using var command = _database.Command(
                "SELECT id, source, title, launch_command, install_path, box_art_path, manual, " +
                "art_manual, pointer, quality, starting_card FROM games WHERE removed_at IS NULL " +
                "ORDER BY title COLLATE NOCASE;");

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                games.Add(new GameDetail(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4),
                    ArtStamp(reader.IsDBNull(5) ? null : reader.GetString(5)),
                    reader.GetInt64(6) != 0,
                    reader.GetInt64(7) != 0,
                    reader.GetInt64(8) != 0,
                    Quality(reader.GetInt64(9)),
                    reader.GetInt64(10) != 0));
            }
        }

        return games;
    }

    // The picture's last write time in ticks, or zero when there is none. A cover chosen by hand
    // replaces the file at the same path, so the time is the only thing that says it is another.
    private static long ArtStamp(string? path)
    {
        if (string.IsNullOrEmpty(path)) return 0;

        var file = new FileInfo(path);
        return file.Exists ? file.LastWriteTimeUtc.Ticks : 0;
    }

    // Adds a game a person described, or changes one. Either way the row becomes theirs and the
    // scan leaves it alone: an edit is a statement that the scanner got something wrong.
    internal long Save(long id, string title, string launchCommand, string? folder)
    {
        var now = Stamp(DateTimeOffset.UtcNow);
        var install = string.IsNullOrWhiteSpace(folder) ? null : folder.Trim();

        lock (_database.Gate)
        {
            if (id > 0)
            {
                using var update = _database.Command(
                    "UPDATE games SET title = $title, launch_command = $launch, " +
                    // Editing a game that had gone is how somebody says it is back — the row is
                    // still there for four months precisely so that this works.
                    "install_path = $install, manual = 1, removed_at = NULL, " +
                    "last_seen_at = $now WHERE id = $id;");

                update.Parameters.AddWithValue("$title", title);
                update.Parameters.AddWithValue("$launch", launchCommand);
                update.Parameters.AddWithValue("$install", (object?)install ?? DBNull.Value);
                update.Parameters.AddWithValue("$now", now);
                update.Parameters.AddWithValue("$id", id);
                update.ExecuteNonQuery();

                Log.Info($"\"{title}\" was edited by hand; the scan will leave it alone from now on");
                return id;
            }

            using var insert = _database.Command(
                "INSERT INTO games (source, external_id, title, launch_command, install_path, " +
                "box_art_path, first_seen_at, last_seen_at, manual) " +
                "VALUES ('by hand', NULL, $title, $launch, $install, NULL, $now, $now, 1) " +
                "RETURNING id;");

            insert.Parameters.AddWithValue("$title", title);
            insert.Parameters.AddWithValue("$launch", launchCommand);
            insert.Parameters.AddWithValue("$install", (object?)install ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", now);

            var added = Convert.ToInt64(insert.ExecuteScalar());
            Log.Info($"\"{title}\" was added by hand and starts {launchCommand}");
            return added;
        }
    }

    // Takes a game off the list; a scanned game comes back at the next scan, since it is still
    // installed. The row is hidden and the cover left where it is, both for KeepRemoved.
    internal void Remove(long id)
    {
        string? title = null;

        lock (_database.Gate)
        {
            using (var read = _database.Command("SELECT title FROM games WHERE id = $id;"))
            {
                read.Parameters.AddWithValue("$id", id);
                title = read.ExecuteScalar() as string;
            }

            using var hide = _database.Command(
                "UPDATE games SET removed_at = $now WHERE id = $id;");
            hide.Parameters.AddWithValue("$now", Stamp(DateTimeOffset.UtcNow));
            hide.Parameters.AddWithValue("$id", id);
            hide.ExecuteNonQuery();
        }

        Log.Info($"\"{title ?? id.ToString(CultureInfo.InvariantCulture)}\" was taken off the list. " +
                 $"What was edited about it is kept for {KeepRemoved.TotalDays:0} days.");
    }

    internal string? BoxArtPath(long gameId)
    {
        string? path;

        lock (_database.Gate)
        {
            using var command = _database.Command(
                "SELECT box_art_path FROM games WHERE id = $id AND removed_at IS NULL;");
            command.Parameters.AddWithValue("$id", gameId);

            path = command.ExecuteScalar() as string;
        }

        return path is not null && File.Exists(path) ? path : null;
    }

    // A cover the database still points to but that is gone from disk (the folder cleared by
    // hand) is forgotten here, so NeedingArtwork offers the game a picture again.
    internal int ForgetMissingArtwork()
    {
        var missing = new List<long>();

        lock (_database.Gate)
        {
            using (var select = _database.Command(
                       "SELECT id, box_art_path FROM games " +
                       "WHERE box_art_path IS NOT NULL AND removed_at IS NULL;"))
            {
                using var reader = select.ExecuteReader();
                while (reader.Read())
                {
                    if (!File.Exists(reader.GetString(1))) missing.Add(reader.GetInt64(0));
                }
            }

            if (missing.Count == 0) return 0;

            using var update = _database.Command(
                "UPDATE games SET box_art_path = NULL, art_checked_at = NULL WHERE id = $id;");
            var id = update.Parameters.Add("$id", Microsoft.Data.Sqlite.SqliteType.Integer);

            foreach (var gameId in missing)
            {
                id.Value = gameId;
                update.ExecuteNonQuery();
            }
        }

        return missing.Count;
    }

    // The games with no picture to show and not looked up recently. The artwork worker takes this
    // list once, after the listeners are open, and works through it in the background.
    internal IReadOnlyList<ArtworkCandidate> NeedingArtwork(TimeSpan retryAfter)
    {
        var candidates = new List<ArtworkCandidate>();
        var cutoff = Stamp(DateTimeOffset.UtcNow - retryAfter);

        lock (_database.Gate)
        {
            using var command = _database.Command(
                "SELECT id, source, external_id, title FROM games " +
                "WHERE box_art_path IS NULL AND removed_at IS NULL " +
                "  AND (art_checked_at IS NULL OR art_checked_at < $cutoff) " +
                "ORDER BY title COLLATE NOCASE;");
            command.Parameters.AddWithValue("$cutoff", cutoff);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                candidates.Add(new ArtworkCandidate(
                    reader.GetInt64(0),
                    reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2),
                    reader.GetString(3)));
            }
        }

        return candidates;
    }

    // Records the outcome of one lookup; a null path is recorded too, as "searched for and not
    // found". byHand marks a cover that came from the page: only ever set, never cleared.
    internal void RecordArtwork(long gameId, string? path, bool byHand = false)
    {
        lock (_database.Gate)
        {
            using var command = _database.Command(
                "UPDATE games SET art_checked_at = $now" +
                (path is null ? string.Empty : ", box_art_path = $path") +
                (byHand ? ", art_manual = 1" : string.Empty) +
                " WHERE id = $id;");

            command.Parameters.AddWithValue("$now", Stamp(DateTimeOffset.UtcNow));
            command.Parameters.AddWithValue("$id", gameId);
            if (path is not null) command.Parameters.AddWithValue("$path", path);

            command.ExecuteNonQuery();
        }
    }

    // Every stored picture, so that files belonging to no game can be swept up.
    internal IReadOnlyList<string> ArtworkPaths()
    {
        var paths = new List<string>();

        lock (_database.Gate)
        {
            using var command = _database.Command(
                "SELECT box_art_path FROM games WHERE box_art_path IS NOT NULL;");

            using var reader = command.ExecuteReader();
            while (reader.Read()) paths.Add(reader.GetString(0));
        }

        return paths;
    }

    // Whether this server draws the pointer into this game's picture. Most games draw their own,
    // and the two together are two pointers a step apart; the ones that draw none need this.
    internal void RecordPointer(long gameId, bool wanted)
    {
        lock (_database.Gate)
        {
            using var command = _database.Command(
                "UPDATE games SET pointer = $pointer WHERE id = $id;");
            command.Parameters.AddWithValue("$pointer", wanted ? 1 : 0);
            command.Parameters.AddWithValue("$id", gameId);
            command.ExecuteNonQuery();
        }
    }

    // Whether the starting card is shown for this game while it loads. On by default; the switch
    // exists for the odd game a static card in front of it confuses.
    internal void RecordShowCard(long gameId, bool wanted)
    {
        lock (_database.Gate)
        {
            using var command = _database.Command(
                "UPDATE games SET starting_card = $card WHERE id = $id;");
            command.Parameters.AddWithValue("$card", wanted ? 1 : 0);
            command.Parameters.AddWithValue("$id", gameId);
            command.ExecuteNonQuery();
        }
    }

    // How much work the encoder puts into this game. Anything the database does not recognise is
    // High, which is where every game starts.
    internal void RecordQuality(long gameId, StreamQuality quality)
    {
        lock (_database.Gate)
        {
            using var command = _database.Command(
                "UPDATE games SET quality = $quality WHERE id = $id;");
            command.Parameters.AddWithValue("$quality", (int)quality);
            command.Parameters.AddWithValue("$id", gameId);
            command.ExecuteNonQuery();
        }
    }

    private static StreamQuality Quality(long stored) =>
        Enum.IsDefined(typeof(StreamQuality), (int)stored)
            ? (StreamQuality)stored
            : StreamQuality.High;

    // What is needed to start one game and then recognise it running: the command, the folder its
    // files live in — how a process is matched when the name is no help — and the title.
    internal LaunchTarget? Target(long gameId)
    {
        lock (_database.Gate)
        {
            using var command = _database.Command(
                "SELECT launch_command, install_path, title, pointer, quality, starting_card " +
                "FROM games WHERE id = $id AND removed_at IS NULL;");
            command.Parameters.AddWithValue("$id", gameId);

            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            return new LaunchTarget(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetString(2),
                reader.GetInt64(3) != 0,
                Quality(reader.GetInt64(4)),
                reader.GetInt64(5) != 0);
        }
    }

    // The same shape ClientStore stores. To the millisecond and not the second: two scans inside
    // one second carried the same stamp, and a game gone between them counted as touched.
    private static string Stamp(DateTimeOffset moment) =>
        moment.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
}
