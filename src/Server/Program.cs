// The dedicated server (docs/MULTIPLAYER.md "Dedicated server"): a NetHost without a player of its own. The lobby comes
// from a JSON file (a LobbyState: mode, the skirmish setup or the campaign start, the co-op players, the scene rule);
// players join from the game's Multiplayer menu; the game starts when --players humans are in and ready; the server
// relays the turns and compares the hashes, and writes the game's replay (the start and every command at its step)
// when the game ends or the server is stopped.
//
//   dotnet run --project src/Server -- --setup lobby.json [--port 1905] [--players 2] [--replay game.pwsave] [--build <version>]
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using PwRemake.Net;
using PwRemake.Sim.Core;

namespace PwRemake.Server;

public static class Program
{
    public static int Main(string[] argv)
    {
        SimCulture.Use();
        Log.AddSink(new ConsoleLogSink());
        Log.DefaultLevel = LogLevel.Info;
        var a = new Dictionary<string, string>();
        for (int i = 0; i < argv.Length; i++)
            if (argv[i].StartsWith("--")) a[argv[i][2..]] = i + 1 < argv.Length && !argv[i + 1].StartsWith("--") ? argv[++i] : "";
        if (!a.TryGetValue("setup", out var setupFile) || !File.Exists(setupFile))
        {
            Console.Error.WriteLine("usage: PwRemake.Server --setup lobby.json [--port 1905] [--players 2] [--replay game.pwsave] [--build version]");
            return 2;
        }
        var lobby = NetProtocol.FromNode<LobbyState>(System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(setupFile)));
        if (lobby == null) { Console.Error.WriteLine($"{setupFile}: not a lobby (LobbyState JSON)"); return 2; }
        int port = a.TryGetValue("port", out var ps) && int.TryParse(ps, out var pp) ? pp : NetProtocol.DefaultPort;
        int players = a.TryGetValue("players", out var pl) && int.TryParse(pl, out var pn) ? pn : 2;
        var replay = a.GetValueOrDefault("replay", "replay.pwsave");
        using var host = new NetHost(port) { Dedicated = true, Autostart = players, Build = a.GetValueOrDefault("build", ""), DumpDir = Directory.GetCurrentDirectory() };
        host.Info += s => Console.WriteLine(s);
        host.SetLobby(lobby);
        Console.WriteLine($"ParaWorld Remake server on port {host.Port}: {lobby.Mode} \"{lobby.Name}\" ({lobby.Configuration}), starting with {players} players");
        bool stop = false;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop = true; };
        var sw = System.Diagnostics.Stopwatch.StartNew();
        double last = 0;
        while (!stop && host.Lobby.State != "over")
        {
            double now = sw.Elapsed.TotalSeconds;
            host.Update(now - last);
            last = now;
            Thread.Sleep(5);
        }
        if (host.Replay() is { } r)
        {
            r.Header.Name = Path.GetFileNameWithoutExtension(replay);
            r.Write(replay);
            Console.WriteLine($"replay written: {replay} ({r.Steps} steps, {r.Commands.Count} commands)");
        }
        return 0;
    }
}
