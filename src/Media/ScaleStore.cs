//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using RemoteGameHub.App;
using RemoteGameHub.Library;

namespace RemoteGameHub.Media;

// Where DisplayAdaptation keeps the desktop scale a stream replaced. Unlike the mode, a scale is
// saved by Windows itself and outlives this process: a shutdown in the middle of a stream would
// leave the stream's scale for good. A row lives from just before the scale is changed until it is
// put back, and one still there at a start is a restore that never happened.
internal sealed class ScaleStore
{
    private readonly Database _database;

    internal ScaleStore(Database database) => _database = database;

    // Every screen left scaled, as its \\.\DISPLAYn name and the percentage to go back to.
    internal IReadOnlyList<(string Device, int Percent)> All()
    {
        var scales = new List<(string, int)>();

        try
        {
            lock (_database.Gate)
            {
                using var command = _database.Command("SELECT device, percent FROM display_scales;");

                using var reader = command.ExecuteReader();
                while (reader.Read()) scales.Add((reader.GetString(0), reader.GetInt32(1)));
            }
        }
        catch (Exception error)
        {
            Log.Warn($"the desktop scales left by earlier streams could not be read ({error.Message})");
        }

        return scales;
    }

    // Records the scale to go back to, unless one is already recorded for this screen: that one
    // was never restored, so it is the person's own and what the screen shows now is not. Answers
    // with whichever is kept, or the one given when nothing could be written.
    internal int Remember(string device, int percent)
    {
        try
        {
            lock (_database.Gate)
            {
                using (var insert = _database.Command(
                    "INSERT INTO display_scales (device, percent) VALUES ($device, $percent) " +
                    "ON CONFLICT (device) DO NOTHING;"))
                {
                    insert.Parameters.AddWithValue("$device", device);
                    insert.Parameters.AddWithValue("$percent", percent);
                    insert.ExecuteNonQuery();
                }

                using var select = _database.Command(
                    "SELECT percent FROM display_scales WHERE device = $device;");
                select.Parameters.AddWithValue("$device", device);

                return Convert.ToInt32(select.ExecuteScalar() ?? percent);
            }
        }
        catch (Exception error)
        {
            // The stream still restores it when it ends; only a shutdown in the middle loses it.
            Log.Info($"the desktop scale to go back to could not be written to the database: " +
                     error.Message);
            return percent;
        }
    }

    // The scale is back, or was never changed. A screen with no row is not a fault.
    internal void Forget(string device)
    {
        try
        {
            lock (_database.Gate)
            {
                using var command = _database.Command("DELETE FROM display_scales WHERE device = $device;");
                command.Parameters.AddWithValue("$device", device);
                command.ExecuteNonQuery();
            }
        }
        catch (Exception error)
        {
            Log.Info($"the desktop scale of {device} could not be removed from the database " +
                     $"({error.Message}); the next start finds it already in place");
        }
    }
}
