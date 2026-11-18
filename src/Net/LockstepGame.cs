// A machine's running lockstep game (docs/MULTIPLAYER.md "Lockstep"): the world (IGameSim) stepped on the host's
// turns. Before step k the commands of turn k go into the world's queue (World.Enqueue: applied at the start of step k,
// in the host's order, on every machine alike); after it the machine acknowledges its step, reports its hash every
// HashEvery steps and writes a dump when the host asks for one (a desync). The pacing: one step per 50 ms of real time
// while turns are there; a machine that has fallen behind (turns waiting) runs faster until it has caught up, one that
// has run out of turns waits - the host holds the turns back while the game is paused, a cutscene is on show or a player
// is waited for, so every machine stands still at the same moment.
//
// Engine-free: the game's session (game/src/App/GameSession.Net.cs), the tests and a bot use it alike.
using System;
using System.Collections.Generic;
using System.IO;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Net;

public sealed class LockstepGame
{
    static readonly Logger L = Log.For("Net.Lockstep");
    public readonly NetClient Net;
    public readonly IGameSim Sim;
    public readonly GameStart Start;
    public readonly int Seat;
    public GameWorld W => Sim.World;
    /// <summary>the mission's interface answers (co-op; null in a skirmish): its scenes go to the host</summary>
    public readonly LockstepPresenter? Presenter;
    public int HashEvery = NetProtocol.HashEvery;
    /// <summary>where this machine writes its own desync dumps (null: not written)</summary>
    public string? DumpDir;
    /// <summary>a command of this machine came back in a turn: (the original the interface submitted, the copy applied)</summary>
    public event Action<Command, Command>? Delivered;
    /// <summary>tests: a hook that may change the world after a step (a deliberate desync)</summary>
    public Action<long>? AfterStepHook;
    double _acc;
    long _lastAck;
    public long Steps => W.StepNo;
    /// <summary>the part of a step since the last one (the view interpolates between the last two steps)</summary>
    public double Fraction => Math.Clamp(_acc / GameWorld.STEP, 0, 1);

    public LockstepGame(NetClient net, IGameSim sim, GameStart start, int seat, LockstepPresenter? presenter = null)
    {
        Net = net; Sim = sim; Start = start; Seat = seat; Presenter = presenter;
        if (presenter != null)
            presenter.SceneStarted += (kind, token, shown) =>
            {
                Net.SceneStart(kind, token, W.StepNo);
                if (!shown) Net.SceneVote(kind, token, false);         // (this machine shows nothing: it is done at once)
            };
    }

    /// <summary>this machine's player (the seat's)</summary>
    public int Player => Seat >= 0 && Start.Seats != null && Seat < Start.Seats.Count ? Start.Seats[Seat].Player : -1;
    /// <summary>turns that arrived and are not played yet</summary>
    public long Buffer => Net.LastTurn - W.StepNo;
    public bool CanStep => Net.LastTurn > W.StepNo;

    /// <summary>what the players see on top of the game: paused, waiting, a desync (null: nothing)</summary>
    public string? Status =>
        Net.Ended ? "The game is over: " + Net.EndReason :
        !Net.Connected && Net.Error != null ? "Connection lost: " + Net.Error :
        Net.HostPaused ? $"Paused by {Net.PausedBy}" :
        Net.Waiting != "" ? "Waiting for " + Net.Waiting :
        null;

    /// <summary>the interface's command: to the host (it comes back in a turn)</summary>
    public void Submit(Command c) => Net.SubmitCommand(c);

    /// <summary>the interface ended a scene (time up, skipped): a vote - the host ends it for everybody by the scene rule</summary>
    public void SceneDone(string kind, int token, bool timeline) => Net.SceneVote(kind, token, timeline);

    /// <summary>runs the steps due after dt real seconds (paced; catching up when behind); before / after: the session's
    /// own work around each step. unpaced: every step that has its turn, up to maxSteps (tests, a rejoin's catch-up).
    /// Returns the steps run.</summary>
    public int Advance(double dt, Action? before = null, Action? after = null, int maxSteps = 8, bool unpaced = false)
    {
        Net.Poll();
        long buffer = Buffer;
        if (buffer <= 0) { _acc = 0; return 0; }
        int want;
        if (unpaced || Net.Rejoining && buffer > 40) want = (int)Math.Min(buffer, unpaced ? maxSteps : maxSteps * 8);
        else
        {
            // one step per 50 ms; faster when turns pile up (the network delivered a burst, or this machine was slow)
            double rate = buffer > 20 ? 4 : buffer > 6 ? 1.5 : buffer > 2 ? 1.1 : 1;
            _acc += dt * rate;
            want = (int)Math.Min(buffer, Math.Min(maxSteps, Math.Floor(_acc / GameWorld.STEP)));
            _acc -= want * GameWorld.STEP;
            if (_acc > GameWorld.STEP) _acc = GameWorld.STEP;
        }
        int n = 0;
        for (; n < want; n++)
        {
            before?.Invoke();
            if (!StepOnce()) break;
            after?.Invoke();
        }
        if (Net.Rejoining && Buffer == 0 && n > 0) L.Info($"caught up at step {W.StepNo}");
        return n;
    }

    /// <summary>one step on its turn (false: the turn is not there)</summary>
    public bool StepOnce()
    {
        long k = W.StepNo + 1;
        var turn = Net.TakeTurn(k);
        if (turn == null) return false;
        foreach (var (cmd, mine) in turn)
        {
            W.Enqueue(cmd);
            if (mine != null) Delivered?.Invoke(mine, cmd);
        }
        try { Sim.Step(); }
        catch (Exception e) { L.Error($"simulation step {k} failed", e); }
        AfterStepHook?.Invoke(k);
        if (k - _lastAck >= 5 || Buffer == 0) { Net.Ack(k); _lastAck = k; }
        if (k % HashEvery == 0)
        {
            ulong h = Sim.StateHash();
            Net.SendHash(k, h, Parts());
            L.Info($"step {k} ({SaveHeader.Clock(W.Time)}): hash {h:x16}, {Buffer} turns waiting");
        }
        if (k == Net.DumpAt)
        {
            var text = Dump();
            Net.SendDump(k, text);
            if (DumpDir != null)
                try
                {
                    Directory.CreateDirectory(DumpDir);
                    var f = Path.Combine(DumpDir, $"desync_{Net.DesyncStep}_step{k}_seat{Seat}.txt");
                    File.WriteAllText(f, text);
                    L.Error($"desync: this machine's world at step {k} written to {f}");
                }
                catch (Exception e) { L.Warn("desync dump: " + e.Message); }
        }
        return true;
    }

    List<(string, ulong)> Parts()
    {
        var p = W.StateHashParts();
        p.Add(("game", Sim.StateHash()));
        return p;
    }

    /// <summary>the world as text (World.StateDump, plus the mission's records and triggers)</summary>
    public string Dump()
    {
        var sw = new StringWriter { NewLine = "\n" };
        sw.WriteLine($"# {Start}");
        sw.WriteLine($"# seat {Seat}, hash {Sim.StateHash():x16}");
        if (Sim is MissionSession ms) ms.StateDump(sw); else W.StateDump(sw);
        return sw.ToString();
    }
}

/// <summary>A fingerprint of a configuration's game data (the files the simulation reads): two machines with other data
/// would run apart, so the lobby shows it as a problem before the start.</summary>
public static class ContentFingerprint
{
    public static string Of(string dataDir, string? missionsDir = null)
    {
        ulong h = 14695981039346656037UL;
        void Mix(string s) { foreach (var b in System.Text.Encoding.UTF8.GetBytes(s)) { h ^= b; h *= 1099511628211UL; } }
        foreach (var f in new[] { "techtree.json", "gamedata.json", "ai.json", "forest.json", "assets/manifest.json" })
        {
            var p = Path.Combine(dataDir, f);
            if (!File.Exists(p)) { Mix(f + ":none"); continue; }
            var fi = new FileInfo(p);
            Mix($"{f}:{fi.Length}");
            using var s = File.OpenRead(p);
            var buf = new byte[1 << 16];
            int r;
            while ((r = s.Read(buf, 0, buf.Length)) > 0) for (int i = 0; i < r; i++) { h ^= buf[i]; h *= 1099511628211UL; }
        }
        if (missionsDir != null && File.Exists(Path.Combine(missionsDir, "index.json"))) Mix(File.ReadAllText(Path.Combine(missionsDir, "index.json")));
        return h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
}
