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

        config.Debug = file.Bool("General", "Debug", config.Debug);
        config.HostName = file.Text("General", "HostName", config.HostName);
        config.VirtualMouse = file.Bool("General", "VirtualMouse", config.VirtualMouse);
        config.VirtualDisplay = file.Bool("General", "VirtualDisplay", config.VirtualDisplay);

        config.Output = file.Text("Display", "Output", config.Output);
        config.Encoder = file.Enum("Display", "Encoder", config.Encoder);
        config.Adapt = file.Bool("Display", "Adapt", config.Adapt);
        config.ScaleDesktop = file.Bool("Display", "ScaleDesktop", config.ScaleDesktop);

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
        writer.Note("If you don't have a real HID device, we can emulate one.");
        writer.Key("VirtualMouse", config.VirtualMouse);
        writer.Blank();
        writer.Note("Prefer a virtual display driver over a real screen, when one is found. This server\n" +
                    "never installs one itself; the installer's own checkbox does, if you asked it to.");
        writer.Key("VirtualDisplay", config.VirtualDisplay);

        writer.Section("Display");
        writer.Note("Which screen to stream: \"auto\" for the one attached to the desktop, a screen's own\n" +
                    "number (shown in the log at startup) or a piece of its name for another.");
        writer.Key("Output", config.Output);
        writer.Blank();
        writer.Note("Which card encodes use \"auto\" follows whichever card the screen above is on.");
        writer.Key("Encoder", config.Encoder.ToString());
        writer.Blank();
        writer.Note("Adapt host screen to client resolution and settings compliance");
        writer.Key("Adapt", config.Adapt);
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
        writer.Note("Folders of your own, separated by commas; a name with a comma goes in quotes or brackets:\n" +
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
