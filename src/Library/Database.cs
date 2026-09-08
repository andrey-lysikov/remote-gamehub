//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using Microsoft.Data.Sqlite;
using RemoteGameHub.App;

namespace RemoteGameHub.Library;

// The one file this server keeps between runs beside the configuration and the log: the clients
// that have paired and the games found. The schema is migrated here, in order, by version number.
internal sealed class Database : IDisposable
{
    private readonly SqliteConnection _connection;

    internal string Path { get; }

    private Database(SqliteConnection connection, string path)
    {
        _connection = connection;
        Path = path;
    }

    internal static Database Open(string directory)
    {
        var path = System.IO.Path.Combine(directory, AppParameters.Identity.FileBase + ".db");

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // One process, several threads: the connection is shared and every statement runs under
            // the lock below, so pooling would only hide which thread holds what.
            Pooling = false,
        }.ToString());

        connection.Open();

        // Write-ahead logging, so that a reader is never blocked by the write that is happening at
        // the same moment. A pairing exchange and the startup cleanup can genuinely overlap.
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA synchronous = NORMAL;");
        Execute(connection, "PRAGMA foreign_keys = ON;");

        var database = new Database(connection, path);
        database.Migrate();

        return database;
    }

    private void Migrate()
    {
        var version = ScalarInt("PRAGMA user_version;");
        var from = version;

        if (version < 1)
        {
            Execute(_connection,
                """
                CREATE TABLE clients (
                    id            INTEGER PRIMARY KEY AUTOINCREMENT,
                    unique_id     TEXT    NOT NULL,
                    name          TEXT    NOT NULL,
                    certificate   TEXT    NOT NULL,
                    fingerprint   TEXT    NOT NULL UNIQUE,
                    paired_at     TEXT    NOT NULL,
                    last_seen_at  TEXT    NOT NULL
                );
                """);

            // Looked up by fingerprint on every request that arrives over TLS, which is all of
            // them once a client is paired.
            Execute(_connection, "CREATE INDEX clients_fingerprint ON clients (fingerprint);");
            Execute(_connection, "PRAGMA user_version = 1;");
            version = 1;
        }

        if (version < 2)
        {
            // The games the scanners found. Rewritten whole on every start rather than maintained
            // incrementally; first_seen_at survives a rescan, everything else the scan overwrites.
            Execute(_connection,
                """
                CREATE TABLE games (
                    id             INTEGER PRIMARY KEY AUTOINCREMENT,
                    source         TEXT    NOT NULL,   -- 'steam' | 'xbox' | 'folder'
                    external_id    TEXT,               -- steam app id, package family name, or NULL
                    title          TEXT    NOT NULL,
                    launch_command TEXT    NOT NULL,   -- what Start-Process is given
                    install_path   TEXT,
                    box_art_path   TEXT,               -- a local file, or NULL
                    first_seen_at  TEXT    NOT NULL,
                    last_seen_at   TEXT    NOT NULL,
                    UNIQUE (source, launch_command)
                );
                """);

            Execute(_connection, "PRAGMA user_version = 2;");
            version = 2;
        }

        if (version < 3)
        {
            // When the online lookup for this game's cover art last ran. No art and no stamp means
            // never looked up; a stamp and no art means nothing was found, and is not asked again.
            Execute(_connection, "ALTER TABLE games ADD COLUMN art_checked_at TEXT;");

            Execute(_connection, "PRAGMA user_version = 3;");
            version = 3;
        }

        if (version < 4)
        {
            // Set on a game a person added or edited on the page: never overwritten by a scanner
            // and never removed for being absent from a scan. A scanner finding it gives way.
            Execute(_connection, "ALTER TABLE games ADD COLUMN manual INTEGER NOT NULL DEFAULT 0;");

            Execute(_connection, "PRAGMA user_version = 4;");
            version = 4;
        }

        if (version < 5)
        {
            // When the game stopped being installed, or was removed from the page. Hidden rather
            // than deleted, so a game that comes back finds its corrected title and chosen cover.
            Execute(_connection, "ALTER TABLE games ADD COLUMN removed_at TEXT;");

            Execute(_connection, "PRAGMA user_version = 5;");
            version = 5;
        }

        if (version < 6)
        {
            // Set when the cover came from the page rather than the background search. Kept apart
            // from "manual" above: a corrected picture does not mean the title is wrong as well.
            Execute(_connection, "ALTER TABLE games ADD COLUMN art_manual INTEGER NOT NULL DEFAULT 0;");

            Execute(_connection, "PRAGMA user_version = 6;");
            version = 6;
        }

        if (version < 7)
        {
            // Whether this server draws the pointer into this game's picture. Off by default:
            // most games draw their own, and two pointers a step apart is worse than none.
            Execute(_connection, "ALTER TABLE games ADD COLUMN pointer INTEGER NOT NULL DEFAULT 0;");

            Execute(_connection, "PRAGMA user_version = 7;");
            version = 7;
        }

        if (version < 8)
        {
            // How much work the encoder puts into this game, as StreamQuality counts it. Two is
            // High, which every game starts at: a quiet one can afford more, a fast one less.
            Execute(_connection, "ALTER TABLE games ADD COLUMN quality INTEGER NOT NULL DEFAULT 2;");

            Execute(_connection, "PRAGMA user_version = 8;");
            version = 8;
        }

        if (version < 9)
        {
            // Whether the starting card is shown for this game while it loads. On by default,
            // unlike the pointer above: it is the odd game that is confused by it, not the rule.
            Execute(_connection, "ALTER TABLE games ADD COLUMN starting_card INTEGER NOT NULL DEFAULT 1;");

            Execute(_connection, "PRAGMA user_version = 9;");
            version = 9;
        }

        Log.Info(from == version
            ? $"database {Path}, schema version {version}"
            : $"database {Path}, schema migrated from version {from} to {version}");
    }

    // Every statement runs under this. SQLite would serialise them, but the connection is one
    // object with one state, and two threads stepping through readers on it at once is a crash.
    internal object Gate { get; } = new();

    internal SqliteCommand Command(string sql)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        return command;
    }

    private int ScalarInt(string sql)
    {
        using var command = Command(sql);
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        lock (Gate)
        {
            _connection.Dispose();
        }
    }
}
