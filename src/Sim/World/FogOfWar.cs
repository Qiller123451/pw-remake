// Fog of war: what the local player sees now, has seen once, and has never seen - simulation state.
//
// The systems ask it (World.Fow: the human player's units do not pick targets in the fog, effects that need sight,
// the campaign's question marks), so it must change at the same moments on every machine and in a replay of the
// game: the world updates it itself, every 0.2 s of game time at the end of a step (and at the end of a step in which
// a mission revealed or hid an area or a debug tool switched it), from the same state on every run. Before, the game's
// session updated it from the frame loop (every 0.2 s of steps, plus at frames: after the debug tools, when a cutscene
// began, at the oracle's reveal events), so what the human's units did depended on the frames (docs/SAVES.md).
//
// FogGrid is the per-player grid of the JS FogOfWar class (engine/terrain.js): cells of `cell` metres over the square
// world, Vis (seen now) and Explored (seen once); Update(viewers) stamps the viewers' sight discs. The view draws it
// (game/src/View/Terrain/FogOfWar.cs FogOverlay, the minimap) and reads Visible / Explored for what it shows.
// What sees (main.js updateFow): the units and finished buildings of the fog's player and of the players who are its
// friends both ways, the oracle's / fireworks' reveal circles ("revealarea" events), MIRAGE's parasites (Watchers),
// the missions' reveals (RevealArea).
//
// Ported from remake/src/engine/terrain.js (FogOfWar) and remake/src/main.js (updateFow) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;

namespace PwRemake.Sim.World;

/// <summary>the fog of war grid of one player (terrain.js FogOfWar without the texture)</summary>
public sealed class FogGrid : IFogOfWar
{
    public readonly double Size, Cell;
    public readonly int N;
    /// <summary>currently visible (count of viewers > 0)</summary>
    public readonly byte[] Vis;
    public readonly byte[] ExploredCells;
    public bool RevealAll;
    /// <summary>changes with every Update (the minimap and the view repaint when it changes)</summary>
    public int Version;
    readonly Dictionary<int, int[][]> _discs = new();

    public FogGrid(double size, double cell = 4)
    {
        Size = size; Cell = cell;
        N = (int)Math.Ceiling(size / cell);
        Vis = new byte[N * N];
        ExploredCells = new byte[N * N];
    }

    int[][] Disc(double r)
    {
        int k = (int)Math.Round(r / Cell, MidpointRounding.AwayFromZero);
        if (_discs.TryGetValue(k, out var d)) return d;
        var outList = new int[2 * k + 1][];
        for (int j = -k; j <= k; j++)
        {
            int w = (int)Math.Floor(Math.Sqrt(k * k - j * j));
            outList[j + k] = new[] { j, -w, w };
        }
        _discs[k] = outList;
        return outList;
    }

    /// <summary>viewers: (x, z, radius)</summary>
    public void Update(IEnumerable<(double x, double z, double r)> viewers)
    {
        int n = N;
        double half = Size / 2;
        Array.Clear(Vis);
        foreach (var (x, z, r) in viewers)
        {
            int ci = (int)Math.Floor((x + half) / Cell), cj = (int)Math.Floor((z + half) / Cell);
            foreach (var row in Disc(r))
            {
                int j = cj + row[0];
                if (j < 0 || j >= n) continue;
                int o = j * n;
                for (int i = Math.Max(0, ci + row[1]); i <= Math.Min(n - 1, ci + row[2]); i++) { Vis[o + i] = 1; ExploredCells[o + i] = 1; }
            }
        }
        Version++;
    }

    /// <summary>the texture bytes: 255 visible, 115 explored, 0 never seen (all 255 with RevealAll)</summary>
    public byte[] TextureBytes(byte[]? into = null)
    {
        var d = into ?? new byte[N * N];
        if (RevealAll) { Array.Fill(d, (byte)255); return d; }
        for (int i = 0; i < d.Length; i++) d[i] = Vis[i] != 0 ? (byte)255 : ExploredCells[i] != 0 ? (byte)115 : (byte)0;
        return d;
    }

    int Index(double x, double z)
    {
        double half = Size / 2;
        int i = (int)Math.Floor((x + half) / Cell), j = (int)Math.Floor((z + half) / Cell);
        return i < 0 || j < 0 || i >= N || j >= N ? -1 : j * N + i;
    }
    public bool Visible(double x, double z) { if (RevealAll) return true; int k = Index(x, z); return k >= 0 && Vis[k] == 1; }
    public bool Explored(double x, double z) { if (RevealAll) return true; int k = Index(x, z); return k >= 0 && ExploredCells[k] == 1; }
}

public sealed partial class World
{
    /// <summary>one player's fog: its grid and the oracle's reveal circles that are its own</summary>
    sealed class PlayerFog
    {
        public Player P = null!;
        public FogGrid G = null!;
        public readonly List<(double X, double Z, double R, double Until)> Reveals = new();
    }
    /// <summary>the fogs kept, in the order they were enabled: single player the human's; a lockstep game (co-op, PvP:
    /// docs/MULTIPLAYER.md) one per human player - every machine keeps all of them, so what the systems ask of a player's
    /// fog is the same everywhere, whoever looks at the screen</summary>
    readonly List<PlayerFog> _fogs = new();
    /// <summary>the fog of war of FogPlayer, kept by the world (EnableFog); null: none (headless runs: all visible)</summary>
    public FogGrid? Fog { get; private set; }
    /// <summary>whose fog Fog is (the first enabled: the human player; in a lockstep game the first human)</summary>
    public Player? FogPlayer { get; private set; }
    /// <summary>the fog is updated at the end of this step (a mission revealed an area, a debug tool switched it)</summary>
    public bool FogDirty;
    /// <summary>steps between two updates (0.2 s: main.js updateFow)</summary>
    public const int FogEvery = 4;
    long _fogEvAbs;

    /// <summary>the world keeps the fog of war of player p from now on (cell metres): World.Fow answers the systems from
    /// it, updated at the end of every FogEvery-th step; the debug option "whole map visible" reveals it all. Another
    /// player's call adds its own fog (the humans of a lockstep game); the first stays Fog / Fow.</summary>
    public FogGrid EnableFog(Player p, double cell = 4)
    {
        var have = _fogs.Find(f => f.P == p);
        if (have != null) return have.G;
        var pf = new PlayerFog { P = p, G = new FogGrid(Map.Size, cell) { RevealAll = Cheats?.Reveal == true } };
        if (_fogs.Count == 0) _fogEvAbs = EventBase + Events.Count;
        _fogs.Add(pf);
        if (Fog == null) { Fog = pf.G; FogPlayer = p; Fow = pf.G; }
        FogUpdate(pf);
        return pf.G;
    }

    /// <summary>player p's own fog (null: it has none - a computer player, or a game without fog)</summary>
    public FogGrid? FogOf(Player? p)
    {
        if (p == null) return null;
        foreach (var f in _fogs) if (f.P == p) return f.G;
        return null;
    }
    /// <summary>the fog the systems consult for player p's units (the human's units do not pick targets in the fog):
    /// its own grid, or Fow when Fow is p's (an interface's own IFogOfWar), else null - computer players see through it</summary>
    public IFogOfWar? FowOf(Player? p) => p == null ? null : (IFogOfWar?)FogOf(p) ?? (p == FogPlayer ? Fow : null);
    /// <summary>the players whose fog the world keeps</summary>
    public IEnumerable<Player> FogPlayers { get { foreach (var f in _fogs) yield return f.P; } }

    /// <summary>does the fog's player see what player p sees? its own things; also those of players who are friends in
    /// both directions (main.js G.sees)</summary>
    public bool FogSees(Player? p) => FogSees(FogPlayer, p);
    /// <summary>does player `owner` of a fog see what player p sees (G.sees for that player)</summary>
    public bool FogSees(Player? owner, Player? p) => p != null && owner != null && (p == owner || (FogAllies && owner.IsFriend(p) && p.IsFriend(owner)));
    /// <summary>the fog's player also sees through its allies' eyes (main.js G.sees: in a campaign; a skirmish: only its own)</summary>
    public bool FogAllies = true;
    /// <summary>the units' and buildings' sight is this share of their range (the sequel's nights: 0.6; main.js G.sightScale)</summary>
    public double SightScale = 1;
    /// <summary>a mode's own viewers of the fog's player (the sequel's searchlights at night: SequelRuntime.Viewers)</summary>
    public Func<IEnumerable<(double X, double Z, double R)>>? ExtraViewers;

    /// <summary>the end of a step: the oracle's reveals of this step, then the update when it is due</summary>
    void FogStep()
    {
        if (_fogs.Count == 0) return;
        // the oracle's / fireworks' circles of the step ("revealarea" events: moves.js; the JS kept them in G.reveals)
        for (int i = EventIndex(_fogEvAbs); i < Events.Count; i++)
        {
            var e = Events[i];
            if (e.Type != "revealarea") continue;
            var who = e.P("player");
            foreach (var f in _fogs) if (who == f.P) f.Reveals.Add((e.Num("x"), e.Num("z"), e.Num("r"), e.Num("until")));
        }
        _fogEvAbs = EventBase + Events.Count;
        if (StepNo % FogEvery == 1 || FogDirty) FogUpdate();
    }

    /// <summary>main.js updateFow: the viewers of each fog's player stamp their sight</summary>
    public void FogUpdate()
    {
        FogDirty = false;
        foreach (var f in _fogs) FogUpdate(f);
    }

    void FogUpdate(PlayerFog f)
    {
        var P = f.P;
        bool Sees(Player? p) => FogSees(P, p);
        var viewers = new List<(double, double, double)>();
        f.Reveals.RemoveAll(r => r.Until <= Time);
        double k = SightScale;
        foreach (var u in Units) if (u.Alive && !u.Parked && Sees(u.Owner)) viewers.Add((u.Pos.X, u.Pos.Z, u.Fow * k));
        // a building sees once it is finished (Building.usl UpdateFOW: a site's range is 0 - its workers see it)
        foreach (var b in Buildings) if (b.Alive && !b.Parked && b.Built && Sees(b.Owner)) viewers.Add((b.Pos.X, b.Pos.Z, b.Fow * k));
        if (ExtraViewers != null && P == FogPlayer) foreach (var v in ExtraViewers()) viewers.Add(v);     // (the sequel: the human's searchlights)
        foreach (var r in f.Reveals) viewers.Add((r.X, r.Z, r.R));                // oracle / fireworks
        foreach (var w in Watchers) if (w.Host != null && w.Host.Alive && Sees(w.Player)) viewers.Add((w.Host.Pos.X, w.Host.Pos.Z, w.Host.Fow * k));   // MIRAGE parasites
        if (Reveals.Count > 0) foreach (var r in RevealsFor(Sees)) viewers.Add(r);     // missions (RevealArea)
        f.G.Update(viewers);
    }

    /// <summary>the debug tools' "fog of war on / off": everything visible or not (updated at the end of the step)</summary>
    public void SetFogRevealAll(bool on)
    {
        if (_fogs.Count == 0) return;
        foreach (var f in _fogs) f.G.RevealAll = on;
        FogDirty = true;
    }
}
