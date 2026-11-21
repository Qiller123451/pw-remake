// Networked games, headless (docs/MULTIPLAYER.md "Tests"): a host and two or three clients in this process, over
// localhost TCP - the real NetHost / NetClient / LockstepGame, each client with its own world built from the GameStart
// the host sent. Bots give orders through the network (they come back in turns); the host compares the machines' hashes
// every 5 s of game time. Checked: a PvP skirmish of three humans and a computer player, a co-op campaign mission (the
// player shared, and split: an allied computer player taken over), cutscenes that hold the game until everybody has seen
// them (and the host-decides rule), pause, a player who loses the connection and comes back (catching up), a dropped
// player, the chat, the lobby's refusals, a deliberate desync (detected, dumps compared), and saves / replays of a
// networked game.
//
//   dotnet run --project tests/PwRemake.Tests -- Multiplayer          (PW_MP_MINUTES: game minutes of the long runs, default 2)
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using PwRemake.Net;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class MultiplayerTests
{
    static double Minutes => double.TryParse(Environment.GetEnvironmentVariable("PW_MP_MINUTES"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 2;
    const string Ver = "test-build";

    // ======================================================================================== the rig
    /// <summary>A mission UI of a client, scripted: it shows every cutscene and dialogue and is done with it `Delay` steps
    /// later (a vote to the host); a machine with another delay is slower to watch.</summary>
    sealed class BotUi : NullPresenter
    {
        public LockstepPresenter? Lp;
        public World? W;
        public int Delay = 60;
        public readonly List<(string Kind, int Token, long At)> Shown = new();
        public int Scenes;
        /// <summary>the UI's clock: real time (50 ms per host tick), not the world's - a cutscene holds the world</summary>
        public long Ticks;
        public override bool PlaySequence(SequencePlay play) { Shown.Add(("sequence", Lp!.SequenceToken, Ticks)); Scenes++; return true; }
        public override bool PlayDialog(DialogPlay d) { Shown.Add(("dialog", d.Token, Ticks)); Scenes++; return true; }
        public void Tick(LockstepGame g)
        {
            Ticks++;
            for (int i = Shown.Count - 1; i >= 0; i--)
                if (Ticks - Shown[i].At >= Delay) { g.SceneDone(Shown[i].Kind, Shown[i].Token, false); Shown.RemoveAt(i); }
        }
    }

    sealed class Machine
    {
        public NetClient Net = null!;
        public LockstepGame? Game;
        public BotUi Ui = new();
        public PresenterTap? Tap;
        public GameRecorder? Rec;
        public int Commands;
        public string Name => Net.Name;
    }

    sealed class Rig : IDisposable
    {
        public NetHost Host = null!;
        public readonly List<Machine> M = new();
        public CampaignTests.Cfg Cfg = null!;
        public string DumpDir = Path.Combine(Path.GetTempPath(), "pw_mp_test_" + Guid.NewGuid().ToString("N")[..8]);
        public void Dispose() { foreach (var m in M) m.Net.Dispose(); Host.Dispose(); }

        public void Pump(double dt = 0) { Host.Update(dt); foreach (var m in M) m.Net.Poll(); }
        /// <summary>pumps until the condition holds (the sockets are asynchronous); fails after the timeout</summary>
        public void Until(TestContext t, Func<bool> ok, string what, int ms = 10000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!ok())
            {
                if (sw.ElapsedMilliseconds > ms) t.True(false, "timed out waiting for: " + what);
                Pump();
                Thread.Sleep(1);
            }
        }
    }

    static Rig Make(TestContext t, string cfgId, int clients, Func<LobbyState> lobby, string[]? names = null, bool dedicated = false)
    {
        var c = CampaignTests.LoadCfgPublic(cfgId);
        if (c == null) t.Skip($"configuration {cfgId} or its data not found");
        var r = new Rig { Cfg = c! };
        r.Host = new NetHost(0, loopbackOnly: true) { Build = Ver, Fingerprint = "fp", DumpDir = r.DumpDir, Dedicated = dedicated, Autostart = dedicated ? clients : 0 };
        var l = lobby();
        l.Configuration = cfgId;
        r.Host.SetLobby(l);
        for (int i = 0; i < clients; i++)
        {
            var m = new Machine { Net = new NetClient(names?[i] ?? (i == 0 ? "Host" : "Player" + (i + 1))) };
            m.Net.Connect("127.0.0.1", r.Host.Port, cfgId, Ver, "fp", i == 0 && !dedicated ? r.Host.HostToken : null);
            r.M.Add(m);
            r.Until(t, () => m.Net.Me >= 0 && r.M.All(x => x.Net.Lobby?.Peers.Count == r.M.Count), $"{m.Name} joined");
        }
        return r;
    }

    /// <summary>everybody ready, the host starts, every machine builds its world (co-op: through LockstepPresenter and the
    /// recording tap, as the game does), the game runs</summary>
    static void StartGame(TestContext t, Rig r, Dictionary<string, string>? profile = null, bool record = false)
    {
        foreach (var m in r.M.Skip(1)) m.Net.SetReady(true);
        r.Until(t, () => r.Host.Lobby.Players.All(p => p.Ready), "everybody ready");
        r.M[0].Net.HostStart(profile);
        r.Until(t, () => r.M.All(m => m.Net.Start != null), "the start reached everybody");
        foreach (var m in r.M) Build(t, r, m, record);
        r.Until(t, () => r.M.All(m => m.Net.Running) && r.Host.Lobby.State == "running", "the game runs");
    }

    static void Build(TestContext t, Rig r, Machine m, bool record)
    {
        var start = m.Net.Start!;
        var c = r.Cfg;
        IGameSim sim;
        LockstepPresenter? lp = null;
        if (start.Mode == GameMode.Skirmish)
        {
            var sk = SkirmishSession.Create(start.Skirmish!.Clone(), null, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai, flags: SimFlags.Game);
            Seats.Apply(sk.World, start.Seats);
            sim = sk;
        }
        else
        {
            lp = new LockstepPresenter(start, m.Ui);
            var tap = m.Tap = new PresenterTap(lp) { DeterministicInner = true };
            lp.Quiet = () => tap.Replaying;
            var ms = MissionSession.Create(start, c.Config, new SessionOptions
            {
                Presenter = tap, Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, Flags = SimFlags.Game,
            });
            lp.C = ms.Campaign;
            sim = ms;
        }
        m.Ui.Lp = lp; m.Ui.W = sim.World;
        m.Game = new LockstepGame(m.Net, sim, start, m.Net.Seat, lp) { DumpDir = r.DumpDir };
        if (record) m.Rec = new GameRecorder(sim.World, sim.StateHash, m.Tap);
        m.Net.Loaded();
    }

    /// <summary>a bot's orders for its seat's player, decided from its own world (every 10 s the idle fighters attack-move
    /// to the nearest enemy; every 30 s a group walk) - through the network</summary>
    static void Bot(Machine m)
    {
        var g = m.Game!; var W = g.W;
        var me = W.Players.FirstOrDefault(p => p.Id == g.Player);
        if (me == null) return;
        long n = W.StepNo;
        long phase = 37 * g.Seat;
        if ((n + phase) % 200 == 0)
        {
            var idle = W.Units.Where(u => u.Alive && u.Owner == me && !u.Inside && !u.Parked && !u.IsWorker && (u.Task.Type == "idle" || u.Task.Type == "hold")).ToList();
            if (idle.Count > 0)
            {
                double cx = idle.Average(u => u.Pos.X), cz = idle.Average(u => u.Pos.Z);
                var best = W.Units.Cast<Entity>().Concat(W.Buildings).Where(e => e.Alive && !e.Parked && e.Owner != null && me.IsEnemy(e.Owner))
                    .OrderBy(e => (e.Pos.X - cx) * (e.Pos.X - cx) + (e.Pos.Z - cz) * (e.Pos.Z - cz)).ThenBy(e => e.Id).FirstOrDefault();
                if (best != null) { g.Submit(new OrderCommand { Player = me.Id, Units = idle.Select(u => u.Id).ToArray(), Type = "attackmove", X = best.Pos.X, Z = best.Pos.Z }); m.Commands++; }
            }
        }
        if ((n + phase) % 600 == 300)
        {
            var all = W.Units.Where(u => u.Alive && u.Owner == me && !u.Inside && !u.Parked).Take(6).ToList();
            if (all.Count > 1) { g.Submit(new GroupMoveCommand { Player = me.Id, Units = all.Select(u => u.Id).ToArray(), X = all[0].Pos.X + 12, Z = all[0].Pos.Z - 8 }); m.Commands++; }
        }
    }

    /// <summary>plays `steps` turns: the host issues one turn per 50 ms of (simulated) time, every machine plays what it has
    /// (its bot and its UI act after each step); stops early when the host holds the turns for longer than `holdLimit`
    /// host ticks (a cutscene everybody has to see, a pause)</summary>
    static int Play(TestContext t, Rig r, long steps, Action<Machine>? afterStep = null, bool bots = true, int holdLimit = 2000)
    {
        long target = r.Host.LastIssued + steps;
        int held = 0, ticks = 0;
        while (r.Host.LastIssued < target)
        {
            ticks++;
            long before = r.Host.LastIssued;
            r.Host.Update(World.STEP);
            held = r.Host.LastIssued == before ? held + 1 : 0;
            if (held > 0) Thread.Sleep(1);          // (held: give the sockets' threads time - the votes are on their way)
            if (held > holdLimit) t.True(false, $"the host held the turns for {held} ticks at step {r.Host.LastIssued} (waiting for: {r.M[0].Net.Waiting}; paused {r.Host.PausedByPlayer})");
            foreach (var m in r.M) { Catch(t, r, m, bots, afterStep); if (m.Game != null) m.Ui.Tick(m.Game); }
        }
        // everybody to the last turn
        foreach (var m in r.M) Catch(t, r, m, bots, afterStep);
        return ticks;
    }

    static void Catch(TestContext t, Rig r, Machine m, bool bots, Action<Machine>? afterStep)
    {
        if (m.Game == null || !m.Net.Connected) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (m.Game.W.StepNo < r.Host.LastIssued)
        {
            m.Net.Poll();
            if (!m.Game.CanStep)
            {
                r.Host.Update(0);
                if (sw.ElapsedMilliseconds > 10000) t.True(false, $"{m.Name}: the turns stopped at {m.Net.LastTurn} (host at {r.Host.LastIssued})");
                Thread.Sleep(0);
                continue;
            }
            m.Game.StepOnce();
            if (bots) Bot(m);
            afterStep?.Invoke(m);
        }
    }

    static void SameWorlds(TestContext t, Rig r, string what)
    {
        var live = r.M.Where(m => m.Game != null && m.Net.Connected).ToList();
        long step = live[0].Game!.W.StepNo;
        t.True(live.All(m => m.Game!.W.StepNo == step), $"{what}: every machine at step {step} ({string.Join(", ", live.Select(m => m.Game!.W.StepNo))})");
        var h = live.Select(m => m.Game!.Sim.StateHash()).ToList();
        t.True(h.Distinct().Count() == 1, $"{what}: the same world on every machine at step {step} ({string.Join(", ", h.Select(x => x.ToString("x16")))})");
        t.Equal(-1L, r.Host.DesyncAt, $"{what}: no desync reported by the host");
    }

    static LobbyState SkirmishLobby(int humans, int ais, ulong seed = 11)
    {
        var s = Skirmish.DefaultSetup("Hu", "Aje");
        s.Map = ""; s.Seed = seed; s.Points = -1;
        string[] tribes = { "Hu", "Aje", "Ninigi", "Hu", "Aje" };          // (then Hu, Aje, Ninigi ...)
        for (int i = 0; i < s.Slots.Count; i++)
        {
            var sl = s.Slots[i];
            if (i < humans) { sl.Type = "open"; sl.Tribe = tribes[i % tribes.Length]; sl.Team = i + 1; }
            else if (i < humans + ais) { sl.Type = "ai"; sl.Tribe = "Aje"; sl.Level = 5; sl.Team = i + 1; sl.Personality = "Offensive"; }
            else sl.Type = "closed";
        }
        s.Teams = humans + ais;
        return new LobbyState { Mode = "skirmish", Skirmish = s, InputDelay = 4 };
    }

    // ======================================================================================== PvP
    /// <summary>three humans (bots) and a computer player on a random jungle: lobby, start, minutes of play with orders over
    /// the network - every machine's world the same at every hash check and at the end</summary>
    public static void SkirmishThreeHumans(TestContext t)
    {
        using var r = Make(t, "base", 3, () => SkirmishLobby(3, 1));
        t.Equal(3, r.Host.Lobby.Skirmish!.Slots.Count(s => s.Type == "human"), "three human slots taken");
        t.True(r.Host.Lobby.Peers.Select(p => p.Slot).Distinct().Count() == 3, "each on a slot of its own");
        StartGame(t, r);
        var start = r.M[0].Net.Start!;
        t.Equal(3, start.Seats!.Count, "three seats");
        t.True(start.Seats.Select(s => s.Player).SequenceEqual(new[] { 0, 1, 2 }), "seats on players 0, 1, 2");
        t.True(r.M.All(m => m.Game!.W.Players.Count(p => !p.Ai) == 3 && m.Game.W.Players.Count == 4), "3 human players and a computer player on every machine");
        t.True(r.M.All(m => m.Game!.W.FogPlayers.Count() == 3), "every human has a fog on every machine");
        int n = (int)(Minutes * 60 / World.STEP);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Play(t, r, n);
        SameWorlds(t, r, "after the game");
        int cmds = r.M.Sum(m => m.Commands);
        var applied = r.M[0].Game!.W.CommandResults.Count;
        t.True(cmds > 5, $"the bots gave orders ({cmds})");
        t.Log($"{n} steps ({SaveHeader.Clock(n * World.STEP)}) on 3 machines in {sw.ElapsedMilliseconds} ms: {cmds} orders over the network, {r.Host.Turns.Count} turns, " +
              $"units {string.Join("/", r.M[0].Game!.W.Players.Select(p => r.M[0].Game!.W.Units.Count(u => u.Alive && u.Owner == p)))}, hash {r.M[0].Game!.Sim.StateHash():x16}");
    }

    /// <summary>the remake's 16 players over the network (protocol 3): sixteen peers fill the lobby, the 17th is refused, a
    /// player's colour of the whole spectrum ("#rrggbb") reaches everybody; then a game of three humans and thirteen
    /// computer players on the huge random jungle runs the same on every machine</summary>
    public static void SixteenPlayers(TestContext t)
    {
        t.Equal(8, NetProtocol.Version, "protocol 8: 16 players, custom colours (3), a mission's game options (4), the map's content hash (5), the map sent to a player who lacks it (6), co-op armies per seat (7), the campaign colour (8)");
        using (var r = Make(t, "base", 16, () => SkirmishLobby(16, 0)))
        {
            t.Equal(16, r.Host.Lobby.Skirmish!.Slots.Count(s => s.Type == "human"), "sixteen human slots taken");
            var extra = new NetClient("Late");
            extra.Connect("127.0.0.1", r.Host.Port, "base", Ver, "fp", null);
            r.Until(t, () => { extra.Poll(); return extra.Error != null; }, "the 17th refused");
            t.True(extra.Error!.Contains("full"), $"the game is full ({extra.Error})");
            extra.Dispose();
            r.M[15].Net.SetSlot(new JsonObject { ["color"] = "#12ab34" });
            r.Until(t, () => r.M.All(m => m.Net.Lobby?.Skirmish?.Slots[15].Color == "#12ab34"), "the custom colour everywhere");
        }
        using var g = Make(t, "base", 3, () => SkirmishLobby(3, 13));
        g.M[1].Net.SetSlot(new JsonObject { ["color"] = "#ff00ff" });
        g.Until(t, () => g.Host.Lobby.Skirmish!.Slots[1].Color == "#ff00ff", "the colour set");
        StartGame(t, g);
        t.True(g.M.All(m => m.Game!.W.Players.Count == 16), "16 players on every machine");
        var p1 = g.M[0].Game!.W.Players[1];
        t.True(p1.PartyColor == 0xff00ff && p1.ColorSlot == -1, "the custom colour on the player's models");
        Play(t, g, 600);
        SameWorlds(t, g, "16 players after 30 s");
    }

    /// <summary>a machine whose world is changed behind the game's back (resources added at step 150): the host finds it at
    /// the next hash check (step 200), names the differing part, every machine dumps its world at the same later step and
    /// the host's report shows the differing line</summary>
    public static void DesyncIsDetected(TestContext t)
    {
        using var r = Make(t, "base", 2, () => SkirmishLobby(2, 0));
        StartGame(t, r);
        string? told = null;
        r.M[0].Net.DesyncDetected += d => told = d;
        r.M[1].Game!.AfterStepHook = k => { if (k == 150) r.M[1].Game!.W.Players[0].Res["food"] += 1; };
        Play(t, r, 260);
        t.Equal(200L, r.Host.DesyncAt, "the desync found at the first hash check after the change");
        r.Until(t, () => told != null, "the players told");
        t.True(told!.Contains("players"), $"the differing part named: {told}");
        Play(t, r, 20);
        r.Until(t, () => r.M[0].Net.DesyncReport != null, "the dumps compared");
        var report = r.M[0].Net.DesyncReport!;
        t.Log(report.Split('\n').Take(5).Aggregate((a, b) => a + "\n    " + b));
        t.True(report.Contains("player 0") && report.Contains("food="), "the report shows the player's resources differing");
        t.True(Directory.GetFiles(r.DumpDir, "*.txt", SearchOption.AllDirectories).Length >= 3, "the dumps written (two machines' own, the host's copies and its report)");
    }

    /// <summary>pause: no turns while a player paused the game, the same step everywhere; then on</summary>
    public static void PauseHoldsEverybody(TestContext t)
    {
        using var r = Make(t, "base", 2, () => SkirmishLobby(2, 1));
        StartGame(t, r);
        Play(t, r, 100);
        r.M[1].Net.RequestPause(true);
        r.Until(t, () => r.Host.PausedByPlayer && r.M[0].Net.HostPaused, "paused");
        long at = r.Host.LastIssued;
        for (int i = 0; i < 50; i++) r.Host.Update(World.STEP);
        t.Equal(at, r.Host.LastIssued, "no turns while paused");
        t.Equal("Paused by Player2", r.M[0].Game!.Status, "the status line");
        foreach (var m in r.M) Catch(t, r, m, true, null);
        SameWorlds(t, r, "paused");
        r.M[0].Net.RequestPause(false);
        r.Until(t, () => !r.Host.PausedByPlayer, "resumed");
        Play(t, r, 100);
        SameWorlds(t, r, "after the pause");
    }

    /// <summary>a player loses the connection: the game waits; the player connects again with the same name and gets the
    /// start and every turn so far, builds the world and catches up - the same world as the others; then another one
    /// leaves for good (dropped by the host) and the game goes on without them</summary>
    public static void DropRejoinAndLeave(TestContext t)
    {
        using var r = Make(t, "base", 3, () => SkirmishLobby(3, 1));
        StartGame(t, r);
        Play(t, r, 300);
        // ---- Player2 loses its connection
        var lost = r.M[1];
        lost.Net.Dispose();
        r.Until(t, () => r.M[0].Net.Waiting.Contains("Player2"), "the host waits for Player2");
        long held = r.Host.LastIssued;
        for (int i = 0; i < 40; i++) r.Host.Update(World.STEP);
        t.Equal(held, r.Host.LastIssued, "no turns while waiting for a lost player");
        // ---- back with the same name: the start and every turn, replayed
        var back = new Machine { Net = new NetClient("Player2") };
        r.M[1] = back;
        back.Net.Connect("127.0.0.1", r.Host.Port, "base", Ver, "fp");
        r.Until(t, () => back.Net.Start != null && back.Net.Rejoining, "the start again for the returning player");
        Build(t, r, back, false);
        r.Until(t, () => back.Net.LastTurn >= held, "every turn so far received");
        while (back.Game!.CanStep) back.Game.StepOnce();
        t.Equal(held, back.Game.W.StepNo, "caught up to the last turn");
        Play(t, r, 200);
        SameWorlds(t, r, "after the rejoin");
        // ---- Player3 leaves; the host drops it at once
        var gone = r.M[2];
        gone.Net.Leave();
        r.Until(t, () => r.M[0].Net.Chat.Any(c => c.Text.Contains("Player3 left")), "Player3 has left");
        r.M.RemoveAt(2);
        Play(t, r, 200);
        SameWorlds(t, r, "without Player3");
    }

    /// <summary>the lobby: another protocol version is refused, a game in progress refuses strangers, other game data is a
    /// problem that stops the start, chat reaches everybody</summary>
    public static void LobbyRules(TestContext t)
    {
        using var r = Make(t, "base", 2, () => SkirmishLobby(3, 0));
        r.M[1].Net.SendChat("hello there");
        r.Until(t, () => r.M[0].Net.Chat.Any(c => c.Text == "hello there" && c.Name == "Player2"), "chat to the host");
        // other game data: joins, but cannot play
        var odd = new NetClient("Odd");
        odd.Connect("127.0.0.1", r.Host.Port, "base", Ver, "other");
        r.Until(t, () => r.Host.Lobby.Peers.Any(p => p.Name == "Odd"), "Odd joined");
        t.True(r.Host.Lobby.Peers.First(p => p.Name == "Odd").Problem != null, "other game data is a problem");
        odd.SetReady(true); r.M[1].Net.SetReady(true);
        r.Until(t, () => r.Host.Lobby.Peers.All(p => p.Ready || p.Host), "ready");
        t.True(LobbyStart.Problem(r.Host.Lobby)?.Contains("Odd") == true, "the start refused because of Odd's data");
        odd.Leave();
        r.Until(t, () => r.Host.Lobby.Peers.All(p => p.Name != "Odd"), "Odd left");
        t.Equal("open", r.Host.Lobby.Skirmish!.Slots[2].Type, "Odd's slot open again");
        // another version of the game
        var old = new NetClient("Old");
        old.Connect("127.0.0.1", r.Host.Port, "base", "other-build", "fp");
        r.Until(t, () => r.Host.Lobby.Peers.Any(p => p.Name == "Old"), "Old joined");
        t.True(r.Host.Lobby.Peers.First(p => p.Name == "Old").Problem!.Contains("version"), "another version is a problem");
        old.Leave();
        // close the open slot, start; a stranger is refused while the game runs
        var l = r.Host.Lobby.Clone();
        foreach (var s in l.Skirmish!.Slots) if (s.Type == "open") s.Type = "closed";
        l.Skirmish.Slots[2].Type = "ai";
        r.M[0].Net.HostSetLobby(l);
        r.Until(t, () => r.Host.Lobby.Skirmish!.Slots[2].Type == "ai" && r.Host.Lobby.Peers.Count == 2, "the host's change");
        StartGame(t, r);
        var late = new NetClient("Late");
        late.Connect("127.0.0.1", r.Host.Port, "base", Ver, "fp");
        r.Until(t, () => { late.Poll(); return late.Error != null; }, "the late player refused");
        t.True(late.Error!.Contains("started"), $"refused: {late.Error}");
        Play(t, r, 60);
        SameWorlds(t, r, "the game");
    }

    /// <summary>a dedicated server (no player of its own, no world): two players join, say ready, the server starts by
    /// itself, relays and compares; its replay of the game replays to the players' world</summary>
    public static void DedicatedServer(TestContext t)
    {
        using var r = Make(t, "base", 2, () => SkirmishLobby(2, 1), dedicated: true);
        t.True(r.Host.Lobby.Players.All(p => !p.Host), "nobody is the host's own player");
        foreach (var m in r.M) m.Net.SetReady(true);
        r.Until(t, () => r.M.All(m => m.Net.Start != null), "the server started by itself");
        foreach (var m in r.M) Build(t, r, m, false);
        r.Until(t, () => r.M.All(m => m.Net.Running), "the game runs");
        Play(t, r, 600);
        SameWorlds(t, r, "the server's game");
        var rep = r.Host.Replay()!;
        var c = r.Cfg;
        var sk = SkirmishSession.Create(rep.Start.Skirmish!.Clone(), null, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai, flags: rep.Flags);
        var sr = new SaveReplay(rep, sk.World, sk.StateHash, null);
        sr.Run(); sr.End();
        t.Equal(r.M[0].Game!.Sim.StateHash(), sk.StateHash(), "the server's replay is the game");
    }

    // ======================================================================================== co-op
    static LobbyState CoopLobby(int mission, List<CoopChoice> choices, string rule = "all") => new()
    {
        Mode = "coop", SceneRule = rule, InputDelay = 4, CoopPlayers = choices,
        Campaign = new GameStart { Mission = mission, Difficulty = 1, Seed = 77 },
    };

    /// <summary>two humans share the mission's player (mission 1): the triggers, quests and cutscenes run on both machines;
    /// a cutscene holds the game until both have seen it (the slower one watches longer) - "all"; the same world all the way</summary>
    public static void CoopSharedMission(TestContext t)
    {
        var choices = new List<CoopChoice> { new() { Player = 0, Name = "Cole", Main = true } };
        using var r = Make(t, "base", 2, () => CoopLobby(1, choices));
        r.M[0].Ui.Delay = 40; r.M[1].Ui.Delay = 140;           // (the second machine watches longer)
        StartGame(t, r, new Dictionary<string, string> { ["Tutorial_Started"] = "1" });
        var s = (MissionSession)r.M[0].Game!.Sim;
        t.True(r.M.All(m => m.Game!.Start.Seats!.All(x => x.Player == 0)), "both seats command player 0");
        t.Equal(2, s.Human!.Seats.Count, "the shared player knows its two humans");
        int n = (int)(Minutes * 60 / World.STEP);
        int ticks = Play(t, r, n);
        SameWorlds(t, r, "after the co-op game");
        // a cutscene held the game until the slower machine (140 ticks) had seen it: more host ticks than turns
        t.True(ticks >= n + 100, $"the cutscenes held the turns for the slower machine ({ticks} ticks for {n} turns)");
        var e = s.Engine!;
        t.True(e.StatFired > 0, $"the triggers fired ({e.StatFired})");
        t.True(r.M.All(m => m.Ui.Scenes > 0), $"both machines were shown the mission's scenes ({string.Join("/", r.M.Select(m => m.Ui.Scenes))})");
        t.True(e.SequencesEnded > 0 || e.DialogsEnded.Count > 0, "scenes ended by the host's command");
        t.Log($"mission 1 co-op: {n} steps in {ticks} host ticks, {e.StatFired} triggers fired, {e.SequencesEnded} cutscenes and {e.DialogsEnded.Count} dialogues ended by votes, quests {e.Quests.Count(q => q.Visible)}; game time {SaveHeader.Clock(s.World.Time)}, hash {s.StateHash():x16}");
    }

    /// <summary>"host" scene rule: a cutscene ends when the host has seen it, the other machine's view is ended by the
    /// command; Shared mode offers no ally takeover (mission 2: the allied player 3 stays the computer's - taking an ally
    /// over is the co-op campaign mode's, docs/COOP.md)</summary>
    public static void CoopSharedHostRule(TestContext t)
    {
        var choices = new List<CoopChoice> { new() { Player = 0, Name = "Bud", Main = true } };
        using var r = Make(t, "base", 2, () => CoopLobby(2, choices, "host"));
        r.M[0].Ui.Delay = 30; r.M[1].Ui.Delay = 100000;        // (the second machine never finishes watching)
        r.M[1].Net.SetCoopPlayer(3);
        r.Pump(); Thread.Sleep(20); r.Pump();
        t.True(r.Host.Lobby.Peers.All(p => p.Player == 0), "Shared: the ally cannot be taken over");
        var l = r.Host.Lobby.Clone(); l.CoopPlayers!.Add(new CoopChoice { Player = 3, Name = "Ally" });
        l.Peers.First(p => !p.Host).Player = 3;
        t.True(LobbyStart.Problem(l) != null, "Shared: a player on another player than the mission's is refused");
        StartGame(t, r);
        var s = (MissionSession)r.M[1].Game!.Sim;
        var p3 = s.World.Players.First(p => p.Id == 3);
        t.True(p3.Ai && s.World.Brains.Has(3), "player 3 stays the computer's");
        t.True(r.M.All(m => m.Game!.Player == 0), "both machines command player 0");
        Play(t, r, (long)(Minutes * 60 / World.STEP));
        SameWorlds(t, r, "after the shared co-op game");
        t.True(r.M[0].Ui.Scenes > 0, "the host was shown scenes");
        var e = s.Engine!;
        t.True(e.SequencesEnded + e.DialogsEnded.Count > 0, $"scenes ended by the host alone ({e.SequencesEnded} cutscenes, {e.DialogsEnded.Count} dialogues)");
    }

    // ======================================================================================== co-op campaign (mode 2)
    static LobbyState CoopCampaignLobby(TestContext t, int mission, string[] kinds, string[] tribes, int difficulty = 1, string? defLua = null)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no base configuration");
        var def = defLua != null ? PwRemake.Sim.Coop.CoopDefinition.FromLua(defLua) : PwRemake.Sim.Coop.CoopDefinition.Load("base", mission);
        if (def == null) t.Skip($"no co-op definition of mission {mission}");
        var folder = MissionSession.MissionFolderOf(new GameStart { Mission = mission }, c!.Config)!;
        var l = CoopLobby(mission, new List<CoopChoice> { new() { Player = 0, Name = "You", Main = true } });
        l.CoopMode = "campaign";
        l.Coop = new CoopLobbyState
        {
            Difficulty = difficulty, Definition = def!.Raw, MissionPlayers = PwRemake.Sim.Coop.CoopPlan.MissionSlots(folder),
            Seats = Enumerable.Range(0, 3).Select(i => new CoopLobbySeat { Seat = i + 1, Kind = kinds[i], Tribe = tribes[i] }).ToList(),
        };
        return l;
    }

    /// <summary>mode 2 over the network: two humans and an AI commander (mission 3), each seat its faction and base; the
    /// second player takes seat 2 and picks its faction in the lobby; orders, a command to the AI commander and a transfer
    /// travel as commands; the same world everywhere</summary>
    public static void CoopCampaignTwoHumansAndAi(TestContext t)
    {
        using var r = Make(t, "base", 2, () => CoopCampaignLobby(t, 3, new[] { "human", "human", "ai" }, new[] { "Hu", "", "Ninigi" }));
        t.True(r.Host.Lobby.Peers.Select(p => p.Seat).OrderBy(x => x).SequenceEqual(new[] { 1, 2 }), "the host on seat 1, the second player on seat 2");
        r.M[1].Net.SetCoopTribe("Aje");
        r.Until(t, () => r.Host.Lobby.Coop!.Seats[1].Tribe == "Aje", "the second player's faction");
        StartGame(t, r);
        var start = r.M[0].Net.Start!;
        t.True(start.Coop != null && start.Coop.Seats.Count == 3 && start.Coop.Seats[2].Ai, "the start carries the co-op seats");
        t.Equal(2, start.Seats!.Count, "two humans in the seats");
        var s1 = (MissionSession)r.M[1].Game!.Sim;
        var co = s1.Campaign.Coop!;
        t.Equal(co.Seats[1].Slot, r.M[1].Game!.Player, "the second machine plays seat 2's slot");
        t.Equal("Aje", co.Seats[1].Player.Tribe, "seat 2's faction");
        t.True(co.Seats[2].Commander != null, "the AI commander runs on every machine");
        int n = (int)(Minutes * 60 / World.STEP);
        bool sent = false; int given = 0;
        Play(t, r, n, m =>
        {
            // the host's machine: once, a command to the AI commander and a worker given to seat 2
            if (sent || m != r.M[0] || m.Game!.W.StepNo < 200) return;
            sent = true;
            var W = m.Game.W;
            var sl = ((MissionSession)m.Game.Sim).Campaign.StartLocations[co.Seats[2].Slot]!;
            m.Game.Submit(new PwRemake.Sim.Coop.CoopCommand { Player = 0, Seat = 3, Kind = "defend", HasPoint = true, X = sl.X + 20, Z = sl.Z });
            given = W.Units.First(u => u.Owner?.Id == 0 && u.IsWorker && !u.Inside).Id;
            m.Game.Submit(new PwRemake.Sim.Coop.CoopGiveCommand { Player = 0, Units = new[] { given }, ToSeat = 2 });
        });
        SameWorlds(t, r, "the co-op campaign game");
        foreach (var m in r.M) t.Equal("defend", ((MissionSession)m.Game!.Sim).Campaign.Coop!.Seats[2].Commander!.Order, $"{m.Name}: the AI commander defends");
        var gw = s1.World.ById(given);
        t.True(s1.World.CommandResults.Any(x => x.Command is PwRemake.Sim.Coop.CoopGiveCommand && x.Reason == null) && (gw == null || gw.Owner == co.Seats[1].Player), $"the given worker is seat 2's ({gw?.Name} of {gw?.Owner?.Id}, alive {gw?.Alive}; {string.Join(", ", s1.World.CommandResults.Where(x => x.Command is PwRemake.Sim.Coop.CoopGiveCommand).Select(x => x.Reason ?? "ok"))})");
    }

    /// <summary>mode 2, one human and two AI commanders over the network: a "Both AI commanders" command (CoopCommand with
    /// Seats) is sent as one command, travels as JSON through the host and comes back with its seats; both commanders
    /// carry it out</summary>
    public static void CoopCampaignBothCommanders(TestContext t)
    {
        using var r = Make(t, "base", 1, () => CoopCampaignLobby(t, 3, new[] { "human", "ai", "ai" }, new[] { "Hu", "Aje", "Ninigi" }));
        StartGame(t, r);
        var s = (MissionSession)r.M[0].Game!.Sim;
        var co = s.Campaign.Coop!;
        t.True(co.Seats.Count == 3 && co.Seats[1].Commander != null && co.Seats[2].Commander != null, "two AI commanders");
        PwRemake.Sim.Coop.CoopCommand? sent = null;
        Play(t, r, 400, m =>
        {
            if (sent != null || m.Game!.W.StepNo < 100) return;
            var sl = s.Campaign.StartLocations[co.Seats[2].Slot]!;
            sent = new PwRemake.Sim.Coop.CoopCommand { Player = 0, Seat = 2, Seats = new[] { 2, 3 }, Kind = "defend", HasPoint = true, X = sl.X + 20, Z = sl.Z };
            m.Game.Submit(sent);
        });
        var got = s.World.CommandResults.Where(x => x.Command is PwRemake.Sim.Coop.CoopCommand).ToList();
        t.Equal(1, got.Count, "one command for both");
        t.True(!ReferenceEquals(got[0].Command, sent), "the applied command is the network's copy");
        t.True(((PwRemake.Sim.Coop.CoopCommand)got[0].Command).Seats.SequenceEqual(new[] { 2, 3 }) && got[0].Reason == null, $"it kept its seats and was accepted ({got[0].Reason})");
        t.True(co.Seats[1].Commander!.Order == "defend" && co.Seats[2].Commander!.Order == "defend", "both AI commanders defend");
        SameWorlds(t, r, "the co-op game with two AI commanders");
    }

    /// <summary>mode 2 with two seats (seat 3 empty): two humans on mission 2, seat 2 the ally 3's slot</summary>
    public static void CoopCampaignTwoSeats(TestContext t)
    {
        using var r = Make(t, "base", 2, () => CoopCampaignLobby(t, 2, new[] { "human", "human", "empty" }, new[] { "", "Ninigi", "" }, defLua: CoopTests.AllyDef02));
        StartGame(t, r);
        var s = (MissionSession)r.M[0].Game!.Sim;
        var co = s.Campaign.Coop!;
        t.Equal(2, co.Seats.Count, "two seats");
        t.Equal(3, r.M[1].Game!.Player, "the second human plays the ally's slot");
        t.Equal(5, s.World.Players.Count, "nothing for the empty seat");
        Play(t, r, (long)(Minutes * 60 / World.STEP / 2));
        SameWorlds(t, r, "the two-seat co-op game");
    }

    /// <summary>mode 2, a mission with an army budget (mission 2), over the network (docs/COOP.md "Armies"): each human buys
    /// the army of its own seat on its own machine - seat 2 before the start (in the lobby), seat 1 after the host pressed
    /// Start (the lobby state "armies" until every human seat has chosen); a faction change drops a seat's army; the
    /// armies travel to the host ("coop-army") and into GameStart (CoopSeat.Army, seat 1's GameStart.Army); every machine
    /// places them the same; the AI commander keeps its definition army. Then: nobody chooses - the host starts after its
    /// timeout with the level's armies.</summary>
    public static void CoopCampaignArmies(TestContext t)
    {
        const double CREDITS = 4000;
        LobbyState Lobby()
        {
            var l = CoopCampaignLobby(t, 2, new[] { "human", "human", "ai" }, new[] { "", "Aje", "Ninigi" });
            l.Coop!.ArmyBudget = true; l.Coop.Credits = CREDITS;
            return l;
        }
        using var r = Make(t, "base", 2, Lobby);
        var c = r.Cfg;
        var folder = MissionSession.MissionFolderOf(new GameStart { Mission = 2 }, c.Config)!;
        var mj = PwRemake.Sim.Maps.PwMapReader.MissionOf(folder);
        var PB = new PointBuy(c.Rules, null);
        // seat 2 (the second machine): its own shop and budget, no story heroes; it buys two warriors
        var o2 = CampaignArmy.ForCoopSeat(mj, c.Rules, "Aje", CREDITS, 2);
        t.True(o2 != null, "mission 2 has an army screen");
        var a2 = o2!.Defaults();
        t.True(a2.Cards.Values.All(x => c.Rules.Def(PB.Resolve(x, "Aje").Cls, null)?.Unique != true), "seat 2's army has no story hero");
        t.True(o2.Allowed == null || o2.Allowed.All(x => c.Rules.Def(PB.Resolve(x, "Aje").Cls, null)?.Unique != true), "nor does its shop");
        t.Log($"seat 2: total {o2.Total}, left {PB.Left(a2, o2.Total, "Aje")}, warrior {PB.Price("aje_warrior", 1, "Aje")}, blocked {string.Join("/", o2.Blocked)}, cards {string.Join(" ", a2.Cards.Select(kv => kv.Key + ":" + kv.Value))}, allowed {(o2.Allowed == null ? "all" : string.Join(" ", o2.Allowed))}");
        t.True(PB.BuyLowest(a2, "aje_warrior", o2.Total, "Aje", o2.Blocked) != null && PB.BuyLowest(a2, "aje_warrior", o2.Total, "Aje", o2.Blocked) != null, "two warriors bought");
        int want2 = a2.Cards.Values.Count(x => x == "aje_warrior");
        r.M[1].Net.SetCoopArmy(a2);
        r.Until(t, () => r.M[0].Net.Lobby!.Coop!.Seats[1].ArmyDone, "seat 2's army reached the host and the lobby");
        t.Equal(want2, r.M[0].Net.Lobby!.Coop!.Seats[1].Army!.Cards.Values.Count(x => x == "aje_warrior"), "everybody sees seat 2's army");
        // a faction change drops it; chosen again
        r.M[1].Net.SetCoopTribe("Ninigi");
        r.Until(t, () => r.Host.Lobby.Coop!.Seats[1].Tribe == "Ninigi", "the faction changed");
        t.True(!r.Host.Lobby.Coop!.Seats[1].ArmyDone, "another faction: the army must be chosen again");
        r.M[1].Net.SetCoopTribe("Aje");
        r.Until(t, () => r.Host.Lobby.Coop!.Seats[1].Tribe == "Aje", "the faction back");
        r.M[1].Net.SetCoopArmy(a2);
        r.Until(t, () => r.Host.Lobby.Coop!.Seats[1].ArmyDone, "seat 2's army again");
        // the host starts: seat 1 has not chosen - the armies are waited for
        r.M[1].Net.SetReady(true);
        r.Until(t, () => r.Host.Lobby.Players.All(p => p.Ready), "everybody ready");
        r.M[0].Net.HostStart(null);
        r.Until(t, () => r.M.All(m => m.Net.Lobby?.State == "armies"), "the lobby state \"armies\" on every machine");
        t.True(r.M[1].Net.Lobby!.ArmyWait > 0 && r.M[1].Net.Lobby!.Coop!.ArmiesOpen.Select(x => x.Seat).SequenceEqual(new[] { 1 }), "seat 1 is waited for, with the time left");
        for (int i = 0; i < 20; i++) { r.Pump(); Thread.Sleep(5); }
        t.True(r.M.All(m => m.Net.Start == null), "no start while seat 1 chooses");
        // seat 1 (the host's machine): the mission's army with one more warrior
        var o1 = CampaignArmy.ForCoopSeat(mj, c.Rules, "", CREDITS, 1)!;
        var a1 = o1.Defaults();
        t.Log($"seat 1: total {o1.Total}, left {PB.Left(a1, o1.Total, o1.Tribe)}, cards {string.Join(" ", a1.Cards.Select(kv => kv.Key + ":" + kv.Value))}");
        t.True(PB.BuyLowest(a1, "hu_warrior", o1.Total, o1.Tribe, o1.Blocked) != null, "seat 1 buys a warrior");
        r.M[0].Net.SetCoopArmy(a1);
        r.Until(t, () => r.M.All(m => m.Net.Start != null), "the start reached everybody once every army is chosen");
        var start = r.M[1].Net.Start!;
        t.True(start.Army != null && start.Army.Cards.Count == a1.Cards.Count, "seat 1's army is the campaign's army (GameStart.Army)");
        t.True(start.Coop!.SeatOf(2)!.Army is { } sa2 && sa2.Cards.Values.Count(x => x == "aje_warrior") == want2, "seat 2's army in the start");
        t.True(start.Coop.SeatOf(3)!.Army == null, "the AI commander buys nothing");
        foreach (var m in r.M) Build(t, r, m, false);
        r.Until(t, () => r.M.All(m => m.Net.Running) && r.Host.Lobby.State == "running", "the game runs");
        foreach (var m in r.M)
        {
            var co = ((MissionSession)m.Game!.Sim).Campaign.Coop!;
            var W = m.Game.W;
            var p1 = co.Seats.First(x => x.Seat == 1).Player; var p2 = co.Seats.First(x => x.Seat == 2).Player; var p3 = co.Seats.First(x => x.Seat == 3).Player;
            t.True(W.Units.Count(u => u.Alive && u.Owner == p1 && u.Name == "hu_warrior") >= a1.Cards.Values.Count(x => x == "hu_warrior"), $"{m.Name}: seat 1's bought warriors");
            t.Equal(want2, W.Units.Count(u => u.Alive && u.Owner == p2 && u.Name == "aje_warrior"), $"{m.Name}: seat 2's bought warriors");
            t.True(W.Units.Any(u => u.Alive && u.Owner == p2 && u.Name == "aje_resource_collector") || W.Buildings.Any(b => b.Alive && b.Owner == p2), $"{m.Name}: seat 2 has its main building (once)");
            t.True(W.Units.Count(u => u.Alive && u.Owner == p2 && u.Name == "aje_resource_collector") <= 1, $"{m.Name}: not two collectors");
            t.True(W.Units.Any(u => u.Alive && u.Owner == p3), $"{m.Name}: the AI commander has its definition army");
        }
        Play(t, r, 300);
        SameWorlds(t, r, "the co-op game with bought armies");

        // nobody chooses: the host's timeout starts the game with the level's armies
        using var r2 = Make(t, "base", 2, Lobby);
        r2.Host.ArmyTimeout = 1;
        r2.M[1].Net.SetReady(true);
        r2.Until(t, () => r2.Host.Lobby.Players.All(p => p.Ready), "everybody ready (2)");
        r2.M[0].Net.HostStart(null);
        r2.Until(t, () => r2.M.All(m => m.Net.Lobby?.State == "armies"), "the armies are waited for (2)");
        r2.Until(t, () => r2.M.All(m => m.Net.Start != null), "the start after the timeout", 15000);
        var st2 = r2.M[0].Net.Start!;
        t.True(st2.Army == null && st2.Coop!.Seats.All(x => x.Army == null), "the level's armies for every seat");
        foreach (var m in r2.M) Build(t, r2, m, false);
        r2.Until(t, () => r2.M.All(m => m.Net.Running), "the game runs (2)");
        Play(t, r2, 100);
        SameWorlds(t, r2, "the co-op game after the army timeout");
    }

    // ======================================================================================== saves and replays
    /// <summary>a networked co-op game saved by the host's machine (its recorder, as the game's): the save replays to the same
    /// world in a fresh session (the lockstep presenter answers again); the host's own record of the turns (a dedicated
    /// server's replay, no interface answers, no hashes) replays to the same world too</summary>
    public static void SaveAndReplayNetworkedGame(TestContext t)
    {
        var choices = new List<CoopChoice> { new() { Player = 0, Name = "Cole", Main = true } };
        using var r = Make(t, "base", 2, () => CoopLobby(1, choices));
        r.M[0].Ui.Delay = 40; r.M[1].Ui.Delay = 60;
        StartGame(t, r, null, record: true);
        Play(t, r, 1200);
        var host = r.M[0];
        var save = host.Rec!.Capture(host.Game!.Start, SimFlags.Game);
        var file = Path.Combine(r.DumpDir, "net.pwsave");
        Directory.CreateDirectory(r.DumpDir);
        save.Name(file);
        var want = host.Game.Sim.StateHash();
        t.Log($"saved at step {save.Steps}: {save.Commands.Count} commands, {save.Answers.Count} answers, {save.Hashes.Count} hashes");
        ulong Replay(SaveGame sv, string what)
        {
            var c = r.Cfg;
            var lp = new LockstepPresenter(sv.Start, new NullPresenter());
            var tap = new PresenterTap(lp) { DeterministicInner = true };
            lp.Quiet = () => tap.Replaying;
            SaveReplay.Prepare(sv, tap);
            var ms = MissionSession.Create(sv.Start, c.Config, new SessionOptions { Presenter = tap, Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, Flags = sv.Flags });
            lp.C = ms.Campaign;
            _ = new GameRecorder(ms.World, ms.StateHash, tap);          // (as the game's loading: the tap marks its answers with the world's steps)
            var rep = new SaveReplay(sv, ms.World, ms.StateHash, tap);
            rep.Run();
            rep.End();
            t.True(!rep.Diverged, $"{what}: replayed without divergence ({rep.Problem})");
            t.Equal(sv.Steps, ms.World.StepNo, $"{what}: to the saved step");
            return ms.StateHash();
        }
        var loaded = SaveGame.Read(file);
        t.True(loaded.Start.Seats?.Count == 2, "the save knows the seats");
        t.Equal(want, Replay(loaded, "the host's save"), "the host's save replays to the same world");
        var server = r.Host.Replay()!;
        t.Equal(r.Host.LastIssued, server.Steps, "the host's turns to the last issued step");
        // (the machines stand at the last turn: the server's replay ends there too)
        t.Equal(want, Replay(server, "the server's replay"), "the host's record of the turns replays to the same world");
    }

    // ======================================================================================== the map sent to a player
    /// <summary>MapFiles.UserDir set for a while (two machines in one process: each has its own maps folder)</summary>
    sealed class UserDirScope : IDisposable
    {
        readonly string? _was;
        public UserDirScope(string dir) { _was = PwRemake.Sim.Maps.MapFiles.UserDir; PwRemake.Sim.Maps.MapFiles.UserDir = dir; }
        public void Dispose() => PwRemake.Sim.Maps.MapFiles.UserDir = _was;
    }

    /// <summary>Warcraft 3 style (protocol 6, docs/MULTIPLAYER.md "Maps"): the host's machine has a map of its own (My maps),
    /// the second player has none - it asks, the host sends the .pwmap in chunks over the lobby's connection (the progress
    /// at the player's name, the start refused meanwhile), the player checks and keeps it in My maps/downloaded/&lt;hash&gt;.pwmap;
    /// then the problem is gone, the game starts and both worlds - one built from the host's folder, one from the received
    /// file - are the same at every hash check. A damaged or foreign map is refused.</summary>
    public static void MapTransferToPlayerWithoutMap(TestContext t)
    {
        var root = Path.Combine(Path.GetTempPath(), "pw_mp_maps_" + Guid.NewGuid().ToString("N")[..8]);
        string dirA = Path.Combine(root, "A"), dirB = Path.Combine(root, "B");
        Directory.CreateDirectory(dirA); Directory.CreateDirectory(dirB);
        string hash;
        using (new UserDirScope(dirA))
        {
            var d = EditorSaveTests.Edited();
            d.Save(Path.Combine(dirA, "Arena"));
            // (a bigger map: a file of the folder - a module custom.lua could load - so that it goes in several chunks)
            var extra = new byte[400_000];
            uint x = 7;
            for (int i = 0; i < extra.Length; i++) { x = x * 1664525 + 1013904223; extra[i] = (byte)(x >> 24); }
            File.WriteAllBytes(Path.Combine(dirA, "Arena", "notes.bin"), extra);
            hash = PwRemake.Sim.Maps.MapFiles.ContentHash(Path.Combine(dirA, "Arena"));
        }
        var lobby = SkirmishLobby(2, 0);
        lobby.Skirmish!.Map = "User/Arena"; lobby.Skirmish.MapName = "Arena"; lobby.Skirmish.MapHash = hash;
        using var r = Make(t, "base", 2, () => lobby, new[] { "Kacper", "Zidell" });
        var cfg = r.Cfg.Config;
        r.Host.MapFile = (id, h) => { using (new UserDirScope(dirA)) { var b = MapTransfer.PackFor(cfg, id, h, out var why); return (b, why); } };
        var hostM = r.M[0]; var other = r.M[1];
        other.Net.MapDir = Path.Combine(dirB, PwRemake.Sim.Maps.MapFiles.DownloadedFolder);      // (its maps folder: in the game MapFiles.UserDir's)
        var syncA = new LobbyMapSync(hostM.Net, () => cfg);
        var syncB = new LobbyMapSync(other.Net, () => cfg);
        var progress = new SortedSet<double>();
        // (every lobby the host sends: the player's progress as everybody sees it)
        other.Net.LobbyChanged += l => { if (l.Peers.FirstOrDefault(p => p.Name == "Zidell")?.MapProgress is double mp) progress.Add(mp); };
        void Pump()
        {
            r.Pump();
            using (new UserDirScope(dirA)) syncA.Update();
            using (new UserDirScope(dirB)) syncB.Update();
        }
        void Until(Func<bool> ok, string what)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!ok()) { if (sw.ElapsedMilliseconds > 15000) t.True(false, "timed out waiting for: " + what); Pump(); Thread.Sleep(1); }
        }
        // the player without the map asks for it; while it arrives the host cannot start
        string? seenProblem = null;
        Until(() => (seenProblem ??= r.Host.Lobby.Peers.FirstOrDefault(p => p.Name == "Zidell")?.Problem is { } pr && pr.Contains("receiving the map") ? pr : null) != null
                    || File.Exists(Path.Combine(dirB, "downloaded", hash + ".pwmap")), "Zidell asks for the map");
        t.True(seenProblem != null, "the lobby shows the transfer at the player's name: " + seenProblem);
        Until(() => File.Exists(Path.Combine(dirB, "downloaded", hash + ".pwmap")) && r.Host.Lobby.Peers.All(p => p.Problem == null)
                    && other.Net.Lobby?.Peers.All(p => p.Problem == null) == true, "the map arrived and the problem is gone (as the player sees the lobby too)");
        t.True(progress.Count >= 3 && progress.Max == 1, $"the progress in the lobby went up in steps: {string.Join(", ", progress.Select(x => x.ToString("0.00")))}");
        var got = Path.Combine(dirB, "downloaded", hash + ".pwmap");
        t.Equal(hash, PwRemake.Sim.Maps.MapFiles.ContentHash(got), "the received .pwmap has the host's content hash");
        using (new UserDirScope(dirB))
        {
            t.True(LobbyMap.Problem(cfg, r.Host.Lobby.Skirmish) == null, "the player's copy is the host's now");
            t.Equal(got, PwRemake.Sim.Game.SkirmishMaps.Resolve(cfg, "User/Arena", hash), "the setup's map id finds the received copy by its hash");
            t.True(PwRemake.Sim.Maps.MapFiles.Find(cfg, "User/Arena") == null, "(the player has no map of that name of its own)");
        }
        t.True(other.Net.MapError == null && other.Net.MapReceiving == null, "no error on the player's side");
        // the game: each machine builds its world from its own copy of the map
        other.Net.SetReady(true);
        Until(() => r.Host.Lobby.Players.All(p => p.Ready) && LobbyStart.Problem(r.Host.Lobby) == null, "everybody ready, nothing refuses the start");
        hostM.Net.HostStart(null);
        r.Until(t, () => r.M.All(m => m.Net.Start != null), "the start reached everybody");
        foreach (var (m, dir) in new[] { (hostM, dirA), (other, dirB) })
        {
            var start = m.Net.Start!;
            string? path;
            using (new UserDirScope(dir)) path = PwRemake.Sim.Game.SkirmishMaps.Resolve(cfg, start.Skirmish!.Map, start.Skirmish.MapHash);
            t.True(path != null && PwRemake.Sim.Maps.MapFiles.ContentHash(path) == hash, $"{m.Name}: the map of the start ({path})");
            var c = r.Cfg;
            var sk = SkirmishSession.Create(start.Skirmish!.Clone(), path, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai, flags: SimFlags.Game);
            Seats.Apply(sk.World, start.Seats);
            m.Ui.W = sk.World;
            m.Game = new LockstepGame(m.Net, sk, start, m.Net.Seat, null) { DumpDir = r.DumpDir };
            m.Net.Loaded();
        }
        r.Until(t, () => r.M.All(m => m.Net.Running) && r.Host.Lobby.State == "running", "the game runs");
        Play(t, r, 600);
        SameWorlds(t, r, "30 s on the sent map");
        t.True(hostM.Game!.W.Players.Count == 2 && hostM.Game.W.Buildings.Count > 0, "two players on the map");
        // what the player refuses
        var bytes = File.ReadAllBytes(got);
        var bad = Path.Combine(root, "bad");
        bool Refused(Action a, string what) { try { a(); return false; } catch (InvalidDataException e) { t.Log($"refused ({what}): {e.Message}"); return true; } }
        t.True(Refused(() => MapTransfer.Store(bytes, "0123456789abcdef", bad), "another hash"), "a map whose content is not the lobby's hash");
        t.True(Refused(() => MapTransfer.Store(bytes, "../../evil", bad), "a bad name"), "a hash that is no hash (a path)");
        t.True(Refused(() => MapTransfer.Store(new byte[] { 1, 2, 3 }, hash, bad), "not a zip"), "not a zip");
        using (var ms = new MemoryStream())
        {
            using (var z = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, true))
            using (var w = new StreamWriter(z.CreateEntry("../outside.lua").Open())) w.Write("return {}");
            var evil = ms.ToArray();
            t.True(Refused(() => MapTransfer.Store(evil, hash, bad), "an entry outside"), "an entry outside the map");
        }
        t.True(!Directory.Exists(bad) || Directory.GetFiles(bad).Length == 0, "nothing of the refused maps stays");
        try { Directory.Delete(root, true); } catch (IOException) { }
    }
}

static class SaveExt
{
    public static void Name(this SaveGame s, string file) { s.Header.Name = Path.GetFileNameWithoutExtension(file); s.Write(file); }
}
