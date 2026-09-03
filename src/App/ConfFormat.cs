//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

namespace RemoteGameHub.App;

// The one place that knows the name, the meaning and the wording of every setting. Adding one:
// a default on AppConfig, a line in Read, a note and a line in Write, the same text in sample.conf.
internal static class ConfFormat
{
    internal static AppConfig Read(ConfFile file, Action<string> warn)
    {
        var config = new AppConfig();

        // Which screen, which encoder, whether the pointer is drawn, the keyboard and the mouse are
        // deliberately not here: they were "auto" on every machine tried. Defaults in AppConfig.
        config.Debug = file.Bool("General", "Debug", config.Debug);
        config.HostName = file.Text("General", "HostName", config.HostName);
        config.ScaleDesktop = file.Bool("General", "ScaleDesktop", config.ScaleDesktop);

        config.PortBase = file.Number("Network", "PortBase", config.PortBase,
            AppParameters.Limits.MinPortBase, AppParameters.Limits.MaxPortBase, warn);
        config.BindAddress = file.Text("Network", "BindAddress", config.BindAddress);
        config.WebPort = file.Number("Network", "WebPort", config.WebPort,
            AppParameters.Limits.MinWebPort, AppParameters.Limits.MaxWebPort, warn);
        config.Upnp = file.Bool("Network", "Upnp", config.Upnp);

        config.Steam = file.Bool("Games", "Steam", config.Steam);
        config.Xbox = file.Bool("Games", "Xbox", config.Xbox);
        config.Epic = file.Bool("Games", "Epic", config.Epic);
        config.Gog = file.Bool("Games", "Gog", config.Gog);
        config.Ea = file.Bool("Games", "Ea", config.Ea);
        config.BattleNet = file.Bool("Games", "BattleNet", config.BattleNet);

        config.GamesFolders = file.List("Games", "Folders", config.GamesFolders);
        config.GamesDepth = file.Number("Games", "Depth", config.GamesDepth,
            AppParameters.Limits.MinGamesFolderDepth, AppParameters.Limits.MaxGamesFolderDepth, warn);
        config.GamesArtwork = file.Bool("Artwork", "Enabled", config.GamesArtwork);

        return config;
    }

    internal static string Write(AppConfig config)
    {
        var writer = new ConfFile.Writer();

        writer.Section("General");
        writer.Note("Write every step to the log, not only warnings and errors.");
        writer.Key("Debug", config.Debug);
        writer.Blank();
        writer.Note("The name Moonlight shows for this machine. \"auto\" uses the computer name.");
        writer.Key("HostName", config.HostName);
        writer.Blank();
        writer.Note("Scale the desktop up while it is streamed to a client");
        writer.Key("ScaleDesktop", config.ScaleDesktop);

        writer.Section("Network");
        writer.Note("First port of the block Moonlight expects. Every other port is derived from it.");
        writer.Key("PortBase", config.PortBase);
        writer.Blank();
        writer.Note("Address to listen on. \"any\" accepts connections on every interface.");
        writer.Key("BindAddress", config.BindAddress);
        writer.Blank();
        writer.Note("The page that shows what this server is doing and pairs a client. Local network only.");
        writer.Key("WebPort", config.WebPort);
        writer.Blank();
        writer.Note("Ask the router, over UPnP, to forward the streaming ports from the internet.");
        writer.Key("Upnp", config.Upnp);

        writer.Section("Games");
        writer.Note("Which stores to look in for installed games.");
        writer.Key("Steam", config.Steam);
        writer.Key("Xbox", config.Xbox);
        writer.Key("Epic", config.Epic);
        writer.Key("Gog", config.Gog);
        writer.Key("Ea", config.Ea);
        writer.Key("BattleNet", config.BattleNet);
        writer.Blank();
        writer.Note("Folders of your own to look in, separated by commas. A folder whose name contains\n" +
                    "a comma is wrapped in quotes or in brackets, whichever comes to hand:\n" +
                    "    Folders = D:\\Games, \"E:\\Discs, old\", [F:\\Emulators (2004)]");
        writer.Key("Folders", config.GamesFolders);
        writer.Blank();
        writer.Note("How many folder levels below each of those to look into. 1 is the folder itself.");
        writer.Key("Depth", config.GamesDepth);

        writer.Section("Artwork");
        writer.Note("Fetch the posters of installed games");
        writer.Key("Enabled", config.GamesArtwork);

        return writer.ToString();
    }
}
