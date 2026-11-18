// Recording a running game for a saved game, and replaying a saved game (docs/SAVES.md).
//
// GameRecorder: from the first step on, every command the world applies (World.Recorded: the players' commands of
// the command sink - the computer players' own commands are made again by the replay) and every answer of the
// interface (PresenterTap), plus a state hash every HashEvery steps. Capture() makes a SaveGame of it at any moment
// between two steps (the commands still waiting for their step are kept too).
//
// SaveReplay: a fresh session of the same start (the caller makes it: MissionSession.Create with the save's GameStart
// and SimFlags, the tap as its presenter) is run to the saved step with the saved inputs - the commands queued at their
// steps, the tap answering from the record. Every hash of the save is compared on the way; a difference (or an answer
// asked at another step) stops the replay there: the world is a valid game, but not the saved one (a save of another
// version of the game), and the rest of the record is dropped. The recorder of the new session records the replayed
// inputs again, so a save of the loaded game holds the whole history.
//
// Not a JS module (game/save.js wrote the state itself).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Game;

public sealed class GameRecorder
{
    static readonly Logger L = Log.For("Game.Saves");
    /// <summary>steps between two verification hashes (30 s of game time)</summary>
    public const int HashEvery = 600;
    public readonly World.World W;
    public readonly PresenterTap? Tap;
    readonly Func<ulong> _hash;
    /// <summary>the commands applied so far, in order (= World.Recorded)</summary>
    public readonly List<Command> Commands = new();
    public readonly List<(long Step, ulong Hash)> Hashes = new();

    /// <summary>starts recording the world (before its first step); hash: the session's state hash (MissionSession.StateHash
    /// = the world's and the campaign's)</summary>
    public GameRecorder(World.World w, Func<ulong> hash, PresenterTap? tap)
    {
        W = w; _hash = hash; Tap = tap;
        if (W.StepNo != 0) L.Warn($"recording starts at step {W.StepNo}: a save of it cannot be replayed");
        W.Recorded = Commands;
        W.Stepped += OnStep;
        if (tap != null) tap.World = w;
    }

    void OnStep()
    {
        if (W.StepNo % HashEvery == 0) Hashes.Add((W.StepNo, _hash()));
    }

    /// <summary>stops recording (the session ends)</summary>
    public void Detach() { W.Stepped -= OnStep; if (W.Recorded == Commands) W.Recorded = null; }

    /// <summary>the save of the game as it is now (between two steps)</summary>
    public SaveGame Capture(GameStart start, SimFlags flags, JsonNode? setup = null, JsonNode? view = null)
    {
        var s = new SaveGame
        {
            Start = start, Setup = setup?.DeepClone(), Flags = flags, Steps = W.StepNo, View = view?.DeepClone(),
            Answers = Tap?.Record.ToList() ?? new(),
            Hashes = Hashes.ToList(),
        };
        foreach (var c in Commands) s.Commands.Add(CommandCodec.Encode(c));
        foreach (var c in W.PendingCommandList) s.Commands.Add(CommandCodec.Encode(c));
        s.Header.GameTime = W.Time;
        s.Header.Mode = start.Mode.ToString();
        s.Header.Configuration = start.Configuration;
        s.Header.Mission = start.Mission;
        s.Header.MissionFolder = start.MissionFolder;
        s.Header.Tribe = start.Tribe;
        s.Header.Difficulty = start.Difficulty;
        s.Header.Debug = start.Debug != null;
        s.Header.Date = DateTime.Now.ToString("s", System.Globalization.CultureInfo.InvariantCulture);
        return s;
    }
}

/// <summary>Runs a fresh session of a save's start to the saved step with the saved inputs (docs/SAVES.md).</summary>
public sealed class SaveReplay
{
    static readonly Logger L = Log.For("Game.Saves");
    public readonly SaveGame Save;
    public readonly World.World W;
    readonly Func<ulong> _hash;
    readonly PresenterTap? _tap;
    readonly Dictionary<long, ulong> _hashes;
    /// <summary>the step at which the replay went another way than the saved game (-1: none)</summary>
    public long DivergedAt = -1;
    /// <summary>what differed</summary>
    public string? Problem;
    /// <summary>hashes compared and equal</summary>
    public int Verified;
    bool _begun, _ended;

    public SaveReplay(SaveGame save, World.World w, Func<ulong> hash, PresenterTap? tap)
    {
        Save = save; W = w; _hash = hash; _tap = tap;
        _hashes = save.Hashes.GroupBy(h => h.Step).ToDictionary(g => g.Key, g => g.First().Hash);
    }

    public long Target => Save.Steps;
    public bool Diverged => DivergedAt >= 0;
    public bool Done => _ended || Diverged || W.StepNo >= Target;
    /// <summary>0..1 of the way</summary>
    public double Progress => Target <= 0 ? 1 : Math.Clamp((double)W.StepNo / Target, 0, 1);

    /// <summary>before the session of the replay is made: the tap answers from the save's record from the start (the
    /// trigger engine reads the player's profile while it is set up - the profile of the saved game's start, not of
    /// today)</summary>
    public static void Prepare(SaveGame save, PresenterTap tap) => tap.StartReplay(save.Answers);

    /// <summary>queues the saved commands at their steps and lets the tap answer from the record (the session must be
    /// fresh: no step run yet)</summary>
    public void Begin()
    {
        if (_begun) return;
        _begun = true;
        if (W.StepNo != 0) throw new InvalidOperationException($"a replay needs a fresh session (this one is at step {W.StepNo})");
        foreach (var n in Save.Commands)
        {
            var c = CommandCodec.Decode(n);
            if (c.Step <= 0) c.Step = 1;
            W.Enqueue(c);
        }
        if (_tap != null && !_tap.Replaying) _tap.StartReplay(Save.Answers);
        L.Info($"replaying {Save.Commands.Count} commands, {Save.Answers.Count} interface answers to step {Target} ({SaveHeader.Clock(Target * World.World.STEP)}); {_hashes.Count} hashes to compare");
    }

    /// <summary>one step of the replay; false when it is over (done or diverged)</summary>
    public bool StepOnce()
    {
        if (!_begun) Begin();
        if (Done) return false;
        W.Step();
        if (_hashes.TryGetValue(W.StepNo, out var want))
        {
            var got = _hash();
            if (got == want) Verified++;
            else Diverge($"the world differs from the saved game's at {SaveHeader.Clock(W.Time)} (step {W.StepNo}: hash {got:x16}, saved {want:x16})");
        }
        if (!Diverged && _tap?.Mismatch is { } m) Diverge(m);
        return !Done;
    }

    /// <summary>steps until done, diverged, or the time budget (ms of real time; 0 = no limit) is used up; false when over</summary>
    public bool Run(double budgetMs = 0)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (StepOnce()) if (budgetMs > 0 && sw.Elapsed.TotalMilliseconds >= budgetMs) return true;
        return false;
    }

    void Diverge(string why)
    {
        DivergedAt = W.StepNo;
        Problem = why;
        L.Warn("saved game: " + why + " - the game goes on from there; the rest of the saved game is dropped");
    }

    /// <summary>the replay is over: the interface answers again; after a divergence the saved commands still waiting are
    /// dropped (they were for another world)</summary>
    public void End()
    {
        if (_ended) return;
        _ended = true;
        _tap?.EndReplay();
        if (Diverged) W.ClearPendingCommands();
        L.Info(Diverged ? $"replay stopped at step {W.StepNo} of {Target}: {Problem}" : $"replay done: step {W.StepNo}, {Verified} of {_hashes.Count(h => h.Key <= Target)} hashes equal");
    }
}
