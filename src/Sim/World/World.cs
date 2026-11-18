// The game simulation.
//
// World holds all entities (units, buildings, resource nodes, projectiles) and runs one simulation step at a time.
// The actual rules live in src/Sim/Systems - each file adds methods to World (`public partial class World`):
//
//   Economy.cs       gathering, delivery, fields, storage caps, market
//   Construction.cs  placing and building, repair, dismantling, destruction
//   Production.cs    training, upgrades (player-wide and local), heroes, army pyramid
//   Combat.cs        targets, ranges, the damage formula, projectiles, poison, towers, death
//   Healing.cs       healers and healing buildings
//   Effects.cs       auras and timed states (warcry, trumpet, trapped, invulnerable ...)
//   Moves.cs         special moves and building commands
//   Buildings.cs     behaviour by building script class (gates, traps, nests, warp gate ...)
//   Animals.cs       wild animals
//   Transport.cs     bunkers and transports
//   Scripting (src/Sim/Scripting): what missions do to the world (docs/CAMPAIGN_RUNTIME.md)
// Where this file calls into a system, the call is a partial method (World.Hooks.cs) or a stub (World.Stubs.cs) until
// that system is ported.
//
// Every step: commands -> timers -> effects -> units -> buildings -> projectiles -> collision -> cleanup -> computer
// players -> campaign. Things the player should hear or see are reported as events (World.Events, drained by the
// view and the interface once per frame).
//
// Ported from remake/src/game/world.js (pw-exporter 98c7703). Everything that drew (the scene, particles, the fog of
// war's shading, damage smoke, spirit glows) is the view's now; it reads the state and the events.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

/// <summary>The game options the world is made with (world.js opts.rules and the rest of main.js's options).</summary>
public sealed class WorldOptions
{
    /// <summary>the seed of World.Rng</summary>
    public ulong Seed = 1;
    /// <summary>the warp gate rule (skirmish; campaign missions have none: GameOverMgr.usl:95-103)</summary>
    public bool Warpgate;
    public double WarpgateMinutes = 10;
    /// <summary>a multiplayer level (CLevelInfo.IsMultiplayer): the skirmish maps</summary>
    public bool Multiplayer;
    /// <summary>the skirmish game type: deathmatch | domination | defender ...</summary>
    public string Mode = "deathmatch";
    public double DefenderMin = 45;
    public bool DiplomacyLocked;
    /// <summary>overrides the map's start time of day (s)</summary>
    public double? StartTime;
}

/// <summary>A game mode's own rules (world.js hooks: the sequel's damage, lethal, power)</summary>
public sealed class ModeHooks
{
    /// <summary>damage(dmg, attacker, victim) -> true = handled</summary>
    public Func<double, Entity?, Entity, bool>? Damage;
    /// <summary>lethal(victim, attacker) -> true = it lives on at 1 hp</summary>
    public Func<Entity, Entity?, bool>? Lethal;
    /// <summary>power(worker, plant, seconds)</summary>
    public Action<Unit, Building, double>? Power;
}

/// <summary>What the simulation may ask of the fog of war (the interface computes it from the players' sight).</summary>
public interface IFogOfWar
{
    bool Visible(double x, double z);
    bool Explored(double x, double z);
}

/// <summary>options of FindTarget (combat.js findTarget opts): leash, filter</summary>
public sealed class FindOpts
{
    public bool Leash;
    public Func<Entity, bool>? Filter;
    /// <summary>non-aggressive wild animals too (animals.js); wall pieces too</summary>
    public bool Animals, Walls;
    /// <summary>every enemy around, without SortEnemyList's minimum-range rule (World.SortKeeps): the alarm list, "is an
    /// enemy near", a search made for another weapon than the unit's own (turrets, passengers)</summary>
    public bool AllZones;
    /// <summary>also a peaceful wild animal fighting an object of the searcher's owner or an ally (turrets, towers:
    /// MIRAGE's CBuildUpWeapon.FindAutoTarget - the cronosaurus attacking the neighbouring carrier)</summary>
    public bool Defend;
    public static readonly FindOpts None = new();
    /// <summary>AllZones and nothing else</summary>
    public static readonly FindOpts Around = new() { AllZones = true };
}

/// <summary>options of Engage (combat.js engage opts): hold, defend</summary>
public sealed class EngageOpts
{
    public bool Hold, Defend;
    public static readonly EngageOpts None = new();
}

public sealed partial class World
{
    static readonly Logger L = Log.For("World");
    // (numbers in strings the same on every machine: SimCulture, docs/MULTIPLAYER.md "Determinism")
    static World() { SimCulture.Use(); }
    /// <summary>the fixed step of the simulation (s): 20 steps per second (docs/ARCHITECTURE.md)</summary>
    public const double STEP = 0.05;
    const double VIRTUAL_FACTOR = 144, DAY = 86400;      // CTimeMgr (PWServer.exe 0x7bcab0: factor 144.0, day length 86400.0)

    public readonly Rules.Rules Data;
    public readonly ModelManifest Models;
    public readonly IWorldMap Map;
    public readonly WorldOptions Opts;
    /// <summary>the world's random numbers (replaces Math.random; saved with the game)</summary>
    public readonly SimRandom Rng;
    /// <summary>game time (s)</summary>
    public double Time;
    /// <summary>steps done</summary>
    public long StepNo;
    /// <summary>the next entity id (JS: NEXT_ID of entities.js, per world here)</summary>
    public int NextId = 1;
    /// <summary>the level's start time of day (s): day from 6:00 to 18:00 (12:00 when it has none)</summary>
    public double DayStart;
    /// <summary>a mode with a night of its own (the sequel's missions)</summary>
    public Func<bool>? NightFn;
    public List<Player> Players = new();
    public List<Unit> Units = new();
    public List<Building> Buildings = new();
    public List<ResNode> Resources = new();
    public List<Projectile> Projectiles = new();
    public List<Prop> Props = new();
    public readonly SpatialHash<Unit> UHash = new(12);
    public readonly SpatialHash<Building> BHash = new(16);
    public readonly SpatialHash<ResNode> RHash = new(12);
    public readonly NavGrid Nav;
    /// <summary>building footprints (also before they block the pathfinder)</summary>
    public readonly byte[] Reserved;
    /// <summary>the dead (systems)</summary>
    public List<Entity> Dead = new();
    /// <summary>Later(): delayed calls</summary>
    public List<Timer> Timers = new();
    readonly List<Timer> _due = new();
    /// <summary>PeriodicEffect()</summary>
    public List<PeriodicEffect> Effects = new();
    /// <summary>souls of fallen units (Aje shaman: Resurrect)</summary>
    public List<Spirit> Spirits = new();
    public ModeHooks Hooks = new();
    /// <summary>name -> the map's region (campaign Region; set by the game's setup)</summary>
    public Func<string, object?>? RegionOf;
    /// <summary>class (lower case) -> model used when the class's own model does not exist</summary>
    public readonly Dictionary<string, string> StandIns = new(StringComparer.Ordinal);
    /// <summary>hook(entity): called for every unit / building created (campaign registry)</summary>
    public Action<Entity>? OnEntity;
    /// <summary>the 8 m wall grid (wallmap.js; set by the map setup)</summary>
    public IWallMap? WallMap;
    public double PlayHalf;
    /// <summary>playable rectangle</summary>
    public PlayRect Play;
    /// <summary>ships / swimmers (MarkWater); null on maps without water</summary>
    public NavGrid? WaterNav;
    /// <summary>the map's landscape (Jungle, Northland, Savanna, Icewaste, Ashvalley)</summary>
    public string Setting;
    /// <summary>the setting's wood log carried by workers (product_wood_jun / _nor / _sav / _ice / _ash)</summary>
    public string ProductWood;
    public double? WaterLevel;
    public byte[]? SteepCell, ShoreCell, WetCell;
    /// <summary>an original map's walk grid (scenery without pf boxes: Prop)</summary>
    public bool MapWalkGrid;
    public string MapKind;
    /// <summary>a way a group shares (Unit.SetPath)</summary>
    public PathShare? PathShare;
    /// <summary>the fog of war of the local player (combat and the campaign ask it, as in the JS); null: all visible</summary>
    public IFogOfWar? Fow;
    /// <summary>AdaptTerrain: under a building put down in the game the ground is levelled (game/ground.js; set by the map setup)</summary>
    public Func<string, double, double, double, bool>? AdaptTerrain;
    /// <summary>a building put down: the decoration, plants, trees and stumps within its radius go (set by the map setup)</summary>
    public Action<double, double, double>? ClearVegetation;
    /// <summary>fire "animSound" events for the clips' sound events (the view turns it on)</summary>
    public bool AnimSounds;
    readonly Dictionary<int, Entity> _byId = new();
    int[]? _island;
    NavGrid? _amp;
    double _refreshT, _spiritFx;
    Dictionary<Player, (double T, bool V)>? _resCan;
    Dictionary<string, bool>? _resBy;
    HashSet<string>? _noModel;
    int _walkGroup;

    public World(Rules.Rules data, ModelManifest models, IWorldMap map, WorldOptions? opts = null)
    {
        Delayed.EnsureRegistered();
        Data = data;
        Models = models;
        Map = map;
        Opts = opts ?? new WorldOptions();
        Rng = new SimRandom(Opts.Seed);
        // the time of day (CTimeMgr: virtual seconds run 144 times as fast as the game's, a day is 86400 of them = 10
        // minutes; day from 6:00 to 18:00 - SetVirtualDayOffset(0.25), SetVirtualNightOffset(0.75); the level's start
        // time, 12:00 when it has none)
        DayStart = Opts.StartTime ?? map.StartTime ?? 12 * 3600;
        Nav = new NavGrid(map.Size, 2);
        Reserved = new byte[Nav.N * Nav.N];
        Play = map.Play;
        PlayHalf = Math.Min(Play.X1 - Play.X0, Play.Z1 - Play.Z0) / 2;
        Setting = map.Setting ?? "Jungle";
        MapKind = map.Kind;
        // the setting's wood log carried by workers (product_wood_jun / _nor / _sav / _ice / _ash)
        var key = Setting switch { "Northland" => "nor", "Savanna" => "sav", "Icewaste" => "ice", "Ashvalley" => "ash", _ => "jun" };
        ProductWood = Models.Has("product_wood_" + key) ? "product_wood_" + key : "product_wood_jun";
        Log.GameClock = () => Time;
        InitTerrain();
        InitWallMap();                                   // the 8 m wall grid (game/wallmap.js; src/Sim/Systems/WallMap.cs)
    }

    // main.js after `new World`: units stay inside the playable area and out of the water (ships use their own grid);
    // the map's walk grid or the slope rule; the regions of the ground before anything is built
    void InitTerrain()
    {
        var nav = Nav; var P = Play;
        for (int j = 0; j < nav.N; j++) for (int i = 0; i < nav.N; i++)
        {
            var (x, z) = nav.Center(j * nav.N + i);
            if (x < P.X0 + 2 || x > P.X1 - 2 || z < P.Z0 + 2 || z > P.Z1 - 2) nav.Block[j * nav.N + i]++;
        }
        MapWalkGrid = Map.Walkable != null;
        if (Map.Water != null) MarkWater(Map.Water.Value);
        // where land units can walk: the map's pathfinder grid, else a slope rule (maps/source.js slopeWalkable: the
        // sequel's cliffs and the random maps' mountains)
        MarkTerrain(Map.Walkable ?? MapRules.SlopeWalkable(Height, Map.MaxSlope), Map.Walkable != null);
        nav.LabelRegions();                              // before anything is built (nav.js: goals across mountains)
        WaterNav?.LabelRegions();
    }

    public double Height(double x, double z) => Map.Height(x, z);

    // ------------------------------------------------------------------ entities by id
    internal void Register(Entity e) => _byId[e.Id] = e;
    /// <summary>the entity with this id (null: none or removed) - commands and saved games refer to entities so</summary>
    public Entity? ById(int id) => _byId.TryGetValue(id, out var e) ? e : null;
    public T? ById<T>(int id) where T : Entity => ById(id) as T;
    /// <summary>a new group walk id (Order.Wp: the units of one group order)</summary>
    public int NextWalkGroup() => ++_walkGroup;

    /// <summary>height a unit stands at: the ground, or the water surface for ships (swimming animals a little below it)</summary>
    public double GroundY(Unit u)
    {
        if (u.WallH != null) return u.WallH.Value;           // jumping on / off a wall, falling (systems/wallwalk.js)
        if (u.OnWall != null) return WallY(u);                // on a wall's walkway
        double g = Height(u.Pos.X, u.Pos.Z);
        // a flyer's height is above the ground or the water (MIRAGE CFlyerCtrl.GetFloor; a pteranodon on foot: the ground)
        if (u.Flyer is { Ground: false }) return WaterLevel is double wl ? Math.Max(g, wl) : g;
        if (WaterLevel == null) return g;
        if (u.Naval) return Math.Max(g, WaterLevel.Value - (u.Cls == "SHIP" ? 0.1 : 0.5));
        if (u.Amphib) return Math.Max(g, WaterLevel.Value - 0.2);
        return g;
    }

    /// <summary>
    /// the land a point belongs to (the computer player's "island", GetDynamicIslandIdForChar [?engine]): the connected
    /// parts of the land grid where only the terrain counts - cliffs and water part them, buildings and trees don't.
    /// -> a number (-1: in the sea / on a cliff with no land within 8 m)
    /// </summary>
    public int LandIsland(double x, double z)
    {
        var nav = Nav; int n = nav.N;
        bool Bad(int k) => (SteepCell != null && SteepCell[k] != 0) || (WetCell != null && WetCell[k] != 0);
        if (_island == null)
        {
            var lab = new int[n * n]; Array.Fill(lab, -1);
            int id = 0;
            var st = new Stack<int>();
            for (int k0 = 0; k0 < n * n; k0++)
            {
                if (lab[k0] >= 0 || Bad(k0)) continue;
                lab[k0] = id; st.Push(k0);
                while (st.Count > 0)
                {
                    int k = st.Pop(), i = k % n, j = (k - i) / n;
                    if (i > 0 && lab[k - 1] < 0 && !Bad(k - 1)) { lab[k - 1] = id; st.Push(k - 1); }
                    if (i < n - 1 && lab[k + 1] < 0 && !Bad(k + 1)) { lab[k + 1] = id; st.Push(k + 1); }
                    if (j > 0 && lab[k - n] < 0 && !Bad(k - n)) { lab[k - n] = id; st.Push(k - n); }
                    if (j < n - 1 && lab[k + n] < 0 && !Bad(k + n)) { lab[k + n] = id; st.Push(k + n); }
                }
                id++;
            }
            _island = lab;
        }
        int kk = nav.Idx(x, z);
        if (_island[kk] >= 0) return _island[kk];
        int i0 = kk % n, j0 = (kk - i0) / n;
        for (int r = 1; r <= 4; r++) for (int j = j0 - r; j <= j0 + r; j++) for (int i = i0 - r; i <= i0 + r; i++)
        {
            if (i < 0 || j < 0 || i >= n || j >= n) continue;
            int l = _island[j * n + i];
            if (l >= 0) return l;
        }
        return -1;
    }

    /// <summary>LandIsland, searching further for land when (x, z) is in the water or on a cliff: the land nearest to it
    /// within r metres (rings of 2 m cells, nearest first) - an AI's aim on a harbour, a building on a bad cell, a start
    /// location on levelled ground. -1: no land within r.</summary>
    public int LandIslandNear(double x, double z, double r)
    {
        int l = LandIsland(x, z);
        if (l >= 0) return l;
        var nav = Nav; int n = nav.N;
        int kk = nav.Idx(x, z), i0 = kk % n, j0 = kk / n;
        int rc = (int)Math.Ceiling(r / nav.Cell);
        for (int rr = 5; rr <= rc; rr++)
        {
            int best = -1, bd = int.MaxValue;
            for (int j = j0 - rr; j <= j0 + rr; j++)
                for (int i = i0 - rr; i <= i0 + rr; i++)
                {
                    if (Math.Max(Math.Abs(i - i0), Math.Abs(j - j0)) != rr || i < 0 || j < 0 || i >= n || j >= n) continue;
                    int lab = _island![j * n + i];
                    int d = (i - i0) * (i - i0) + (j - j0) * (j - j0);
                    if (lab >= 0 && d < bd) { best = lab; bd = d; }
                }
            if (best >= 0) return best;
        }
        return -1;
    }

    /// <summary>the free cell (nav grid) of land island `island` nearest to (x, z) - its centre - or null (a co-op wave
    /// that can reach its seat neither by land nor by boat comes ashore there: CoopRuntimeEnemies)</summary>
    public (double X, double Z)? NearestOnIsland(int island, double x, double z)
    {
        if (island < 0) return null;
        LandIsland(x, z);
        var nav = Nav; int n = nav.N, best = -1; double bd = double.MaxValue;
        for (int k = 0; k < n * n; k++)
        {
            if (_island![k] != island || nav.Block[k] != 0) continue;
            // (room round it: free 4 m every way - not a cell on the waterline)
            int i = k % n, j = k / n;
            if (i < 2 || j < 2 || i >= n - 2 || j >= n - 2 || nav.Block[k - 2] != 0 || nav.Block[k + 2] != 0 || nav.Block[k - 2 * n] != 0 || nav.Block[k + 2 * n] != 0) continue;
            var (cx, cz) = nav.Center(k);
            double d = (cx - x) * (cx - x) + (cz - z) * (cz - z);
            if (d < bd) { bd = d; best = k; }
        }
        return best < 0 ? null : nav.Center(best);
    }

    /// <summary>the navigation grid a unit moves on: ships and swimming animals sail the water grid, everything else walks</summary>
    public NavGrid NavFor(Unit? u) => u == null ? Nav : NavFor(u.Naval, u.Amphib);
    public NavGrid NavFor(bool naval, bool amphib)
    {
        if (WaterNav == null) return Nav;
        if (amphib) return AmphibNav();
        return naval ? WaterNav : Nav;
    }

    /// <summary>amphibious units: a cell is passable if it is passable on land or on water (rebuilt at most every 2 s)</summary>
    public NavGrid AmphibNav()
    {
        var Ln = Nav; var Wn = WaterNav!;
        var A = _amp;
        if (A == null) { A = _amp = new NavGrid(Map.Size, 2); A.AmpT = -99; A.GateAt = Ln.GateAt; }
        long key = (long)Ln.Version * 100003 + Wn.Version;
        if (A.AmpKey != key && Time - A.AmpT > 2)
        {
            var S = ShoreCell;
            for (int k = 0; k < A.Block.Length; k++) A.Block[k] = (byte)Math.Min(S != null ? Ln.Block[k] - S[k] : Ln.Block[k], Wn.Block[k]);
            A.AmpKey = key; A.AmpT = Time; A.Version++;
        }
        return A;
    }

    /// <summary>
    /// original maps: land units walk where the map's pathfinder grid lets them (maps/ula.js pathing) - not over
    /// mountains and cliffs, through shallow water up to about 0.6 m (fords) but not up the steep shore. SteepCell marks
    /// the dry cells it blocks (coastal buildings may stand on them, construction); the ford cells are no longer wet.
    /// fords: the walk rule may open wet cells (an original map's own walk grid knows its fords); a slope rule may not -
    /// the bed of the sequel's rivers is flat but under water
    /// </summary>
    public void MarkTerrain(Func<double, double, bool> walkable, bool fords = true)
    {
        var nav = Nav; int n = nav.N;
        SteepCell = new byte[n * n];
        ShoreCell = WaterNav != null ? new byte[n * n] : null;     // steep cells at the waterline: amphibians go there
        for (int k = 0; k < n * n; k++)
        {
            var (x, z) = nav.Center(k);
            bool ok = walkable(x, z);
            if (WetCell != null && WetCell[k] != 0)
            {
                if (ok && fords) { nav.Block[k]--; WetCell[k] = 0; }
            }
            else if (!ok)
            {
                nav.Block[k]++; SteepCell[k] = 1;
                if (ShoreCell != null && Height(x, z) < WaterLevel + 2) ShoreCell[k] = 1;
            }
        }
        nav.Version++;
        _island = null;
    }

    /// <summary>water of the original maps (level in m): land units can wade 0.4 m deep, ships need 1.5 m; -> wet cells</summary>
    public int MarkWater(double level)
    {
        WaterLevel = level;
        var nav = Nav; int n = nav.N;
        var wn = WaterNav = new NavGrid(Map.Size, 2);
        WetCell = new byte[n * n];                    // 1 = blocked for land units because of the water
        int wet = 0;
        var P = Play;
        for (int k = 0; k < n * n; k++)
        {
            var (x, z) = nav.Center(k);
            double depth = level - Height(x, z);
            if (depth > 0.4) { nav.Block[k]++; WetCell[k] = 1; wet++; }
            if (depth < 1.5) wn.Block[k]++;
            if (x < P.X0 + 2 || x > P.X1 - 2 || z < P.Z0 + 2 || z > P.Z1 - 2) wn.Block[k]++;
        }
        nav.Version++; wn.Version++;
        if (wet == 0) WaterNav = null;
        return wet;
    }

    public bool IsWater(double x, double z, double depth = 0.4) => WaterLevel != null && WaterLevel.Value - Height(x, z) > depth;

    /// <summary>remove trees / bushes / decoration obstacles around a point (start locations of original maps)</summary>
    public void ClearArea(double x, double z, double r)
    {
        foreach (var n in RHash.Near(x, z, r + 4))
        {
            if (!n.Alive || n.Type == "stone" || Math.Sqrt((n.Pos.X - x) * (n.Pos.X - x) + (n.Pos.Z - z) * (n.Pos.Z - z)) > r) continue;
            DepleteNode(n);
        }
    }

    public void Reserve(IEnumerable<int> cells, int d) { foreach (var k in cells) Reserved[k] = (byte)Math.Max(0, Reserved[k] + d); }

    /// <summary>run DELAYED[kind](this, args) after `sec` game seconds (hit delays, knockbacks, delayed effects: Delayed.cs)</summary>
    public void Later(double sec, string kind, Args? args = null)
    {
        if (!Delayed.DELAYED.TryGetValue(kind, out var fn)) throw new ArgumentException("unknown delayed call " + kind);
        args ??= new Args();
        if (sec <= 0) fn(this, args); else Timers.Add(new Timer { T = Time + sec, Kind = kind, Args = args });
    }

    /// <summary>a repeating effect: PERIODIC[kind] every `every` s for `dur` s (stops early when it returns false)
    /// (systems/moves.js periodicEffect; kind: an entry of PERIODIC; s: its state)</summary>
    public void PeriodicEffect(Entity? host, double dur, double every, string kind, Args? s = null, bool now = false)
    {
        if (!Delayed.PERIODIC.ContainsKey(kind)) throw new ArgumentException("unknown periodic effect " + kind);
        Effects.Add(new PeriodicEffect { Host = host, End = Time + dur, Every = every, Next = Time + (now ? 0 : every), Kind = kind, S = s ?? new Args() });
    }

    /// <summary>runs the periodic effects that are due (systems/moves.js effectsUpdate)</summary>
    void EffectsUpdate()
    {
        if (Effects.Count == 0) return;
        var keep = new List<PeriodicEffect>();
        // (live, as the JS for-of: an effect a tick adds is run in this pass too)
        for (int i = 0; i < Effects.Count; i++)
        {
            var f = Effects[i];
            bool alive = true;
            var K = Delayed.PERIODIC[f.Kind];
            while (alive && f.Next <= Time && f.Next <= f.End) { f.Next += f.Every; alive = K(this, f); }
            if (alive && Time < f.End) keep.Add(f);
            else if (Delayed.PERIODIC_END.TryGetValue(f.Kind, out var end)) end(this, f);
        }
        Effects = keep;
    }

    /// <summary>an animation's onDone (AnimCtl): ANIM_DONE[kind](this, entity, args)</summary>
    public void RunAnimDone(Entity e, AnimDone d)
    {
        if (Delayed.ANIM_DONE.TryGetValue(d.Kind, out var fn)) fn(this, e, d.Args);
        else L.WarnOnce("animdone|" + d.Kind, "unknown animation onDone " + d.Kind);
    }

    /// <summary>sound of an animation event (the view plays one of the alternatives, for entities the player can see)</summary>
    /// <summary>a clip's sound event (clip: the clip that plays it - the view tells idle chatter from the rest)</summary>
    public void EmitAnimSound(Entity e, List<AnimSoundEvent> alts, string? clip = null) => Emit("animSound", new Args { ["entity"] = e, ["alts"] = alts, ["pos"] = e.Pos, ["clip"] = clip });

    // ------------------------------------------------------------------ spawning
    /// <summary>the model a unit class would get (tech tree gfx, class file gfx, or the mission's stand-in), null if none
    /// exists (JS: scripting.js unitModel - SpawnUnit needs it)</summary>
    public string? UnitModel(string name, int? level, IRulesOwner? owner)
    {
        var s = Data.StatsAt(name, level ?? Data.MinLevel(name, owner), owner);
        if (s?.Gfx != null && Models.Has(s.Gfx)) return s.Gfx;
        var cg = Data.ClassGfx(name);
        if (cg != null && Models.Has(cg)) return cg;
        return StandIns.TryGetValue(name.ToLowerInvariant(), out var si) && Models.Has(si) ? si : null;
    }

    public Unit? SpawnUnit(string name, Player? owner, double x, double z, int? level = null, double heading = 0, UnitOpts? opts = null)
    {
        opts ??= UnitOpts.None;
        if (!Data.Exists(name)) { L.Warn("unknown unit " + name); return null; }
        var def = Data.Def(name, owner)!;
        // unique heroes: a second one of the same class is not created (NPCMgr.AddNPC)
        if (owner != null && def.Unique && owner.Heroes.Has(name)) return null;
        // a class whose model does not exist is not created
        if (UnitModel(name, level, owner) == null) { _noModel ??= new HashSet<string>(); if (_noModel.Add(name)) L.Warn("no model for unit " + name); return null; }
        bool zep = Data.Script(name) == "CFlyingTraderObj";
        var nv = NavFor(opts.Swim || (Data.Info(name)?.Type == "SHIP" && !Unit.AMPHIBIOUS.IsMatch(name)), Unit.AMPHIBIOUS.IsMatch(name));
        if (!zep && !nv.IsFree(x, z)) { int c = nv.NearestFree(nv.Idx(x, z)); (x, z) = nv.Center(c); }
        var u = new Unit(this, name, owner, x, z, level is int lv && lv > 0 ? lv : DefaultLevel(name, owner), heading, zep ? opts.With(temp: true) : opts);
        if (zep) MakeZeppelin(u, opts.Alt);
        u.Pos.Y = GroundY(u);          // ships and swimmers start on the water, not on the sea bed below
        Units.Add(u);
        UHash.Insert(u);
        if (owner != null)
        {
            if (u.CountsInPop) { owner.Units++; owner.AtLevel[u.Level - 1]++; }
            if (def.Unique) owner.Heroes.Add(name);
            EntityFilters(u, true);
            RecomputeCaps(owner);
            if (name == "ninigi_ninja" && Data.Invented(owner, "disguise", owner.Tribe)) u.St.Camo.Add("disg");
            // Aje tracker dino (Animal.usl CTrackerDino): acts on its own, scouts forward, attacks enemies within 50 m, dies after 180 s
            if (Data.Script(name) == "CTrackerDino")
            {
                u.Autonomous = true; u.Tracker = true; u.Task = new UnitTask("roam");
                Later(180, "expire", new Args { ["u"] = u });
            }
        }
        OnEntity?.Invoke(u);
        return u;
    }

    public int DefaultLevel(string name, IRulesOwner? owner) => Data.MinLevel(name, owner);

    /// <summary>
    /// the campaign trader's zeppelin (Vehicle.usl CFlyingTraderObj): flies, cannot be hit nor selected, takes no damage,
    /// not in the unit limit; walks are straight flights to the point and its height at the order's speed (m/s, at most
    /// its max speed: task/misc.usl:137-174), always playing "standanim". alt: its height above the sea (map z).
    /// </summary>
    public void MakeZeppelin(Unit u, double? alt)
    {
        u.Zeppelin = true; u.Flying = true; u.Untargetable = true; u.Unselectable = true; u.CountsInPop = false;
        u.LiftH = alt != null ? Math.Max(0, alt.Value - Height(u.Pos.X, u.Pos.Z)) : 0;
        u.Pos.Y = GroundY(u) + u.LiftH;
    }

    // ------------------------------------------------------------------ tech tree filters driven by entities
    // (FightingObj.SetStartFilters / SetLevelFilter, BuildObjects of finished buildings, chief bonus at level 5)
    public void EntityFilters(Entity e, bool on)
    {
        var p = e.Owner;
        if (p?.Tt == null || (e is Unit uu && uu.Illusion)) return;
        var d = e.Def;
        void T(string f) { if (on) p.Tt.Enable(f); else p.Tt.Disable(f); }
        p.Tt.Suspend();                                 // (one rebuild of the tree for all of them, if any changed)
        try
        {
            if (d?.GlobalStartTT != null) T(StripFilters(d.GlobalStartTT));
            if (e is Building b && b.Built) T($"{d!.Tribe}/BuildObjects/{e.Name}");
            if (e is Unit u) LevelFilters(u, u.Level, on);
        }
        finally { p.Tt.Resume(); }
    }
    static string StripFilters(string p) => p.StartsWith("/Filters/") ? p.Substring(9) : p.StartsWith("Filters/") ? p.Substring(8) : p;

    public void LevelFilters(Unit u, int level, bool on)
    {
        var p = u.Owner;
        if (p?.Tt == null || u.Def == null) return;
        foreach (var f in Data.LevelFilters(u.Name, level)) if (!f.Contains("_Bonus")) { if (on) p.Tt.Enable(f); else p.Tt.Disable(f); }
        // level 5: the "chief" bonus of the class, heroes' included (FightingObj.usl SetLevelFilter; only the AllNonHeroes
        // filters skip heroes): Ada's faster production, Cole's tougher infantry, Taslow's building loot ...
        if (level == 5)
        {
            var f = $"{u.Def.Tribe}/Upgrades/{u.Name}/Chief_Bonus";
            if (on) p.Tt.Enable(f); else p.Tt.Disable(f);
        }
    }

    public Building? PlaceBuilding(string name, Player? owner, double x, double z, double rot = 0, bool built = true)
    {
        if (!Data.Exists(name) || !Models.Has(Data.Stats(name, 1, owner)?.Gfx)) { L.Warn("no model for building " + name); return null; }
        var b = new Building(this, name, owner, x, z, rot, built);
        Buildings.Add(b);
        BHash.Insert(b);
        Emit("ground", new Args { ["x"] = b.Pos.X, ["z"] = b.Pos.Z, ["r"] = b.Radius + 2 });   // ground cover (grass) under it goes away
        if (b.Def!.WallKind != null) WallMap?.Add(b);                    // walls, gates, towers, traps: the wall grid
        // the Aje's floating harbour counts in the unit limit and has a card in the pyramid from its site on
        // (CSwimmingHarbour.DoesCountInUnitLimit; Building.OnInit AddUnit)
        if (owner != null && b.CountsInPop) { owner.Units++; owner.AtLevel[b.Level - 1]++; }
        if (built)
        {
            EntityFilters(b, true);
            RecomputeCaps(owner);
            b.Behaviour?.Built(b, this);
        }
        // a gate site: its cells reach the pathfinder at once, in AUTO (the owner and allies pass, enemies are shut out:
        // MIRAGE's mirage_gate_blocking; the original's unfinished gate let every player through)
        else if (b.Def!.Gate) SetGateCells(b);
        OnEntity?.Invoke(b);
        return b;
    }

    /// <summary>a building a script puts down ready-made, like one built in the game: the ground levels to it and takes
    /// its brush's materials, the plants under it go (construction.js startConstruction; the sequel's starts)</summary>
    public Building? PlaceAdapted(string name, Player? owner, double x, double z, double rot = 0, bool built = true)
    {
        var def = Data.Def(name, owner);
        if (AdaptTerrain != null && BuildingGround.Changes(def))
        {
            var st = Data.Stats(name, 1, owner);
            if (st?.Gfx != null) AdaptTerrain(st.Gfx, x, z, rot);
        }
        var b = PlaceBuilding(name, owner, x, z, rot, built);
        if (ClearVegetation != null && b != null) ClearVegetation(x, z, b.Hx != 0 ? Math.Sqrt(b.Hx * b.Hx + b.Hz * b.Hz) : b.Radius);
        return b;
    }

    public ResNode AddResource(string type, string res, double x, double z, double amount, ResOpts? opts = null)
    {
        var r = new ResNode(this, type, res, x, z, amount, opts);
        Resources.Add(r);
        RHash.Insert(r);
        return r;
    }

    public Prop AddProp(PropSpec spec)
    {
        var p = new Prop(this, spec);
        Props.Add(p);
        return p;
    }

    public List<Entity> EntitiesOf(Player p)
    {
        var res = new List<Entity>();
        foreach (var u in Units) if (u.Alive && u.Owner == p) res.Add(u);
        foreach (var b in Buildings) if (b.Alive && b.Owner == p) res.Add(b);
        return res;
    }

    /// <summary>the warp gate's countdown (OnFireWarpGateTimer): its owner and the owner's friends win, everybody else loses</summary>
    public void Victory(Player p)
    {
        foreach (var o in Players) { if (o == p || p.IsFriend(o)) o.Won = true; else o.Defeated = true; }
        Emit("victory", new Args { ["player"] = p });
    }

    // ------------------------------------------------------------------ orders (from the player or the AI)
    static readonly System.Text.RegularExpressions.Regex StationaryOrders = new("move|gather|build|board|trade|repair|deliver");

    /// <summary>o.Type: move | attackmove | patrol | attack | stop | hold | gather | build | repair | deliver | heal | board |
    /// dock | fountain | portal | pickup | use | climb | unload | trade | rallyJob</summary>
    public void Order(IEnumerable<Unit> units, Order o)
    {
        foreach (var u in units.ToList())
        {
            if (!u.Alive || u.Autonomous || u.Parked) continue;
            if (u.Stationary && StationaryOrders.IsMatch(o.Type)) continue;   // mines / water turrets
            if (u.Name == "aje_torpedo_turtle" && o.Type != "kill") continue;  // obeys nothing but Kill
            if (u.Inside) { if (o.Type == "unload") LeaveTransport(u); continue; }
            if (u.Active("trapped") && u.St.TrapWhy == "dig") continue;
            if (u.Active("deaf") && o.Type != "kill") continue;              // deafened: no orders (MIRAGE FO:14376)
            if (PirateLevelPause(u, o)) continue;                             // the pirate ship levelling up (PirateShip.cs)
            if (u.FlyChange > Time) continue;                                 // rising / landing jetpack (MIRAGE Hu.usl:165)
            if (u.St.Camo.Has("entr")) DigOut(u);
            if (u.St.Camo.Has("aje") && o.Type != "move") SetCamo(u, "aje", false);
            // a finishing move plays to its end (FinishingMove.Break() = false): the order follows it
            if (u.Finishing && u.Task.Type == "finish") { u.Task.Next = o; continue; }
            // so does a special move playing out (CSpecialActionTask.SetUnStoppable: Break() = false; Moves.cs)
            if (u.Task.Type == "special" && u.Task.Act is { Breakable: false }) { u.Task.Next = o; continue; }
            ReleaseTask(u);
            u.BusyAnim = false;
            // a unit set up to shoot gets up: a new order breaks its fight off (Fight.usl Break: SwitchToWalkMode)
            if (u.Deploy is { State: "down" or "attack" } && u.Comp?.Dep != null) u.Comp.HoldMove(Time);
            // a new order drops the queued wall tiles (Q_Build: PlaceWall), unless it is the queue's own first step
            if (!o.KeepBuildQueue) u.BuildQueue = null;
            // jumping on or off a wall: the order comes after the landing; on a wall: systems/wallwalk.js
            if (u.WJump != null) { u.WJump.Then = o; continue; }
            if (u.Climbing) { u.Task.Then = o; continue; }
            if (u.OnWall != null && WallOrder(u, o)) continue;
            // a docked siege vehicle undocks first (siege.js); its passengers still get out at once
            if ((u.Docked != null || (u.Task.Type == "dock" && u.Task.Phase == "docking")) && o.Type != "unload") { Undock(u, o); continue; }
            bool user = !o.Auto;
            var t = o.Target;
            switch (o.Type)
            {
                case "move": case "attackmove":
                    // then: next order on arrival; slow: at walking pace (mission scripts); wp: the group walk (PullGroup)
                    u.Task = new UnitTask(o.Type) { X = o.X, Z = o.Z, User = user, Then = o.Then, Slow = o.Slow, Gait = o.Gait, Wp = o.Wp, Alt = o.Alt, FlySpeed = o.FlySpeed };
                    u.SetPath(o.X, o.Z);
                    u.AnchorX = o.X; u.AnchorZ = o.Z;
                    break;
                // to and fro between points, fighting what comes along (FightingObj AddPatrolPoint: Ctrl + right-click)
                case "patrol":
                {
                    var (x, z) = o.Pts![o.I % o.Pts.Count];
                    var then = o.Clone(); then.I = o.I + 1;
                    u.Task = new UnitTask("attackmove") { X = x, Z = z, User = user, Then = then };
                    u.SetPath(x, z);
                    u.AnchorX = x; u.AnchorZ = z;
                    break;
                }
                case "attack":
                    if (t is ResNode rn) { StartGather(u, rn); break; }
                    // a siege tower / brachiosaurus told to attack a wall docks to it (VH:511-514)
                    if (CanDock(u) && t?.Def != null && t.Def.WallKind == "wall") { DockOrder(u, t, user); break; }
                    // an order to attack a neutral player's object makes both players hostile; a friend's object is never
                    // attacked (FightingObj.usl:6594-6617). Automatic fights never get here with a non-hostile target.
                    if (t?.Owner != null && u.Owner != null && t.Owner != u.Owner && !u.Owner.IsEnemy(t.Owner))
                    {
                        if (!AttackAllowed(u, t)) break;
                        DeclareWar(u.Owner, t.Owner);
                    }
                    // a hand-to-hand healer goes along and heals instead (healing.js supportUpdate)
                    if (MeleeHealer(u) && t != null && t.Kind != "res") { u.Task = new UnitTask("support") { Target = t, User = user, Force = o.Force }; u.RepathT = 0; break; }
                    u.Task = new UnitTask("attack") { Target = t, User = user, ForceAttack = o.Force }; u.RepathT = 0;
                    break;
                case "stop": u.Task = UnitTask.Idle(); u.Path.Clear(); u.AnchorX = u.Pos.X; u.AnchorZ = u.Pos.Z; break;
                case "hold": u.Task = new UnitTask("hold"); u.Path.Clear(); u.AnchorX = u.Pos.X; u.AnchorZ = u.Pos.Z; break;
                case "gather": u.Task = new UnitTask("idle") { User = user }; StartGather(u, t); break;
                case "build": u.Task = new UnitTask("idle") { User = user }; StartBuild(u, t); break;
                case "repair": u.Task = new UnitTask("idle") { User = user }; StartRepair(u, t); break;
                case "deliver": u.Task = new UnitTask("gather") { Res = u.Carry != null ? u.Carry.Res : "food", Phase = "deliver", Drop = t, User = user }; break;
                case "heal": if (u.Stats!.Heal != null) { u.Task = new UnitTask("heal") { Target = t, User = user }; u.RepathT = 0; } break;
                case "board":
                    if (t is Unit tu && tu.Docked != null && IsSiegeBrachio(tu)) { ClimbSiege(u, tu, user); break; }   // up the docked brachiosaurus (CH:351)
                    if (CanBoard(u, t)) u.Task = new UnitTask("board") { Target = t, User = user };
                    break;
                case "dock": DockOrder(u, t, user); break;
                // then: an order queued behind the heal walk (the AI's walk back: MIRAGE AiGoalUseFountains Q_Action /Walk)
                case "fountain": if (CanUseWell(u, t)) u.Task = new UnitTask("fountain") { Target = t, User = user, Then = o.Then }; break;
                case "portal": PortalOrder(u, t, user); break;                           // into a MIRAGE portal terminus (PortalTerminus.cs)
                case "pickup": if (CanCarry(u) && t != null && HasItem(t)) u.Task = new UnitTask("pickup") { Target = t, User = user }; break;
                case "use": UseWall(u, t, user); break;                                  // up onto an own wall (wallwalk.js)
                case "climb": ClimbSiege(u, t, user); break;                              // a ladder / docked tower (siege.js)
                case "unload": if (u.Passengers != null) UnloadAll(u, false); break;
                case "trade": StartTrade(u, t); break;
                case "rallyJob": u.Task = UnitTask.Idle(); RallyJob(u, o.X, o.Z); break;      // a new worker at its rally point
            }
        }
    }

    public void Order(Unit u, Order o) => Order(new[] { u }, o);

    /// <summary>one step of the zeppelin's flight: straight to the point at the order's speed, its height going over to the
    /// point's along the way; arrived within 1 m</summary>
    void ZeppelinStep(Unit u, UnitTask t, double dt)
    {
        double max = u.Stats != null ? (u.Stats.MaxSpeed != 0 ? u.Stats.MaxSpeed : u.Stats.Speed) : 0;
        if (max == 0) max = 3;
        double sp = Math.Min(max, t.FlySpeed != 0 ? t.FlySpeed : max);
        if (t.Alt != null && t.Alt0 == null) { t.Alt0 = Height(u.Pos.X, u.Pos.Z) + u.LiftH; t.D0 = Math.Max(1, Hypot(t.X - u.Pos.X, t.Z - u.Pos.Z)); }
        u.Steer(dt, sp, 1);
        if (t.Alt != null)
        {
            double d = Hypot(t.X - u.Pos.X, t.Z - u.Pos.Z), f = Math.Min(1, Math.Max(0, 1 - d / t.D0));
            u.LiftH = Math.Max(0, t.Alt0!.Value + (t.Alt.Value - t.Alt0.Value) * f - Height(u.Pos.X, u.Pos.Z));
        }
        if (u.IdleAnim != null && u.Anim!.CurName != u.IdleAnim) u.Anim.Play(u.IdleAnim);
        if (u.Path.Count == 0) { var next = t.Then; u.Task = UnitTask.Idle(); if (next != null) Order(u, next); }
    }
    static double Hypot(double a, double b) => Math.Sqrt(a * a + b * b);

    /// <summary>
    /// a queued group walk: the first member to reach a waypoint takes every member of the same walk whose next walk is
    /// the same one along at once - the stragglers cut the corner, nobody waits (FightingObj.usl:2033-2044 OnActionPreEnd,
    /// misc.usl:187-199 Execute_Q_Walk, FO:8731-8746)
    /// </summary>
    void PullGroup(UnitTask done, Order next)
    {
        if (done.Wp == 0 || next.Wp == 0 || done.Wp == next.Wp) return;
        foreach (var v in Units.ToList())
        {
            var t = v.Task;
            if (!v.Alive || v.Inside || t.Wp != done.Wp || (t.Type != "move" && t.Type != "attackmove") || t.Then == null || t.Then.Wp != next.Wp) continue;
            v.Task = UnitTask.Idle();
            Order(v, t.Then);
        }
    }

    /// <summary>Shift + right-click: the order comes after the walks the unit has (HandleWalkCommand /Queued); false: it has none</summary>
    public bool QueueOrder(Unit u, Order o)
    {
        var t = u.Task;
        if (!u.Alive || (t.Type != "move" && t.Type != "attackmove" && !(t.Type == "attack" && t.Amove != null))) return false;
        if (t.Then == null) { t.Then = o; return true; }
        if (t.Then.Type == "patrol") return false;
        var x = t.Then;
        while (x.Then != null && x.Then.Type != "patrol") x = x.Then;
        if (x.Then != null) return false;
        x.Then = o;
        return true;
    }

    /// <summary>a task that gives up: what it held (a gathering slot, a building site, a field - also of a task waiting under
    /// a wall walk) is let go first, or the slot stayed taken for the rest of the game</summary>
    public void ToIdle(Unit u)
    {
        ReleaseTask(u);
        var r = u.Task.Resume;
        if (r != null) { u.Task = r; ReleaseTask(u); }
        u.Task = UnitTask.Idle();
        u.BusyAnim = false;
    }

    /// <summary>the states a unit keeps only while its task runs - on a wall, on a ladder, docked, dug in: ended when a
    /// script or a transport takes the unit out of its task (teleport, parking, a new owner, boarding)</summary>
    public void DropStates(Unit u)
    {
        if (u.OnWall != null || u.WJump != null || u.Climbing) { u.OnWall = null; u.WJump = null; u.WPath = new List<Waypoint>(); u.Climbing = false; u.WallH = null; SetOnWall(u, false); }
        if (u.Docked != null || u.DockTile != null) { SiegeGone(u); u.Docked = null; u.DockTile = null; }
        if (u.St.Camo.Has("entr")) SetCamo(u, "entr", false);
    }

    public void ReleaseTask(Unit u)
    {
        var t = u.Task;
        if (t.Type == "cast") CastCancel(u);
        if (t.Type == "portal" && t.Target is Building pb) pb.Portal?.Releases.Delete(u);     // (CEnterPortal.Break: UnregisterPassenger)
        t.Node?.Workers.Delete(u);
        if (t.Building != null) { t.Building.Builders.Delete(u); t.Building.FreeSlot(u); }
        t.Farm?.Workers.Delete(u);
        u.AttachTool(null);
        StopHealing(u);
    }

    // ------------------------------------------------------------------ per-unit update
    void UnitUpdate(Unit u, double dt)
    {
        if (!u.Alive) { DeadUnitUpdate(u, dt); return; }
        if (u.Parked) return;                          // taken out of the world by a mission (scripting setParked)
        if (u.Inside) { PassengerUpdate(u, dt); return; }
        if (u.Fourcc == "Ship") NightCheck(u, dt);     // a ship's lamps (the view switches them by u.Night)
        if (u.InvulnT > 0) u.InvulnT -= dt;
        if (u.St.Poison != null) PoisonUpdate(u);
        if (u.Fatigue > 0) FatigueUpdate(u, dt);       // a wild animal that fled (systems/animals.js)
        if (u.Pending != null) { u.Pending.T -= dt; if (u.Pending.T <= 0) { var p = u.Pending; u.Pending = null; if (p.Melee) LandBlow(u, p); else Fire(u, p.W, p.Target); } }
        // turrets that shoot by themselves (the titans' ballistas, gatlings and machine guns: Combat.GunnerUpdate)
        if (u.Comp != null && u.Comp.Gunners.Count > 0) GunnerUpdate(u, dt);
        if (u.Queue.Count > 0) ProductionUpdate(u, dt);
        // knockback slide / jetpack jump
        // (a throw ends at a cliff, a building or a closed gate: it does not carry the unit up or into it)
        if (u.Knock != null)
        {
            var k = u.Knock; double s = Math.Min(dt, k.T), nx = u.Pos.X + k.Vx * s, nz = u.Pos.Z + k.Vz * s;
            if (u.Flying || u.CanWalkInto(NavFor(u), nx, nz)) { u.Pos.X = nx; u.Pos.Z = nz; k.T -= dt; } else k.T = 0;
            if (k.T <= 0) u.Knock = null;
        }
        if (u.JumpFx != null)
        {
            var j = u.JumpFx; j.T += dt; double f = Math.Min(1, j.T / j.TT);
            u.Pos.X = j.From.X + (j.To.X - j.From.X) * f; u.Pos.Z = j.From.Z + (j.To.Z - j.From.Z) * f;
            u.JumpH = SMath.Sin(f * Math.PI) * Math.Min(20, j.TT * 8);
            if (f >= 1) { u.JumpFx = null; u.JumpH = 0; u.AnchorX = u.Pos.X; u.AnchorZ = u.Pos.Z; }
        }
        // rising / sinking (jetpack flight, Babbage going up, the exo suit coming down)
        if (u.Lift != null) { var l = u.Lift; l.T += dt; double f = Math.Min(1, l.T / l.TT); u.LiftH = l.From + (l.To - l.From) * f; if (f >= 1) u.Lift = null; }
        // MIRAGE's flying units: the flight's height, landing and taking off, the idle flyer (Systems/Flyers.cs)
        if (u.Flyer != null) FlyerUpdate(u, dt);
        // trapped, frozen, stunned: no actions
        if (u.Active("trapped")) { u.Path.Clear(); u.Vel = V3.Zero; AnimTick(u, dt); return; }
        if ((u.OnWall != null || u.WJump != null || u.Climbing) && WallPreUpdate(u, dt)) { AnimTick(u, dt); return; }
        if ((WaterNav != null || u.Naval) && NavalUnitUpdate(u, dt)) { AnimTick(u, dt); return; }
        // however a finishing move ended (a teleport, parking ...), its protection ends with it (FinishingMove.USLOnLeave)
        if (u.Finishing && u.Task.Type != "finish") { u.Finishing = false; u.BusyAnim = false; }
        // on a ladder without the climb (a hit made it fight - engage): down to the ground
        if (u.Climbing && u.Task.Type != "climb") WallFall(u, false);
        var t = u.Task;
        switch (t.Type)
        {
            case "heal": HealUpdate(u, dt); break;
            case "support": SupportUpdate(u, dt); break;
            case "idle": case "hold": IdleUpdate(u, dt); break;
            case "move": case "attackmove":
            {
                // a gate of the enemy shuts the way (Unit.SetPath): break it, then go on
                var bg = u.Breach;
                if (bg != null && (!bg.Alive || Nav.GateAt.Count == 0)) u.Breach = null;
                else if (bg != null && bg.SurfDist(u.Pos.X, u.Pos.Z) < 10)
                {
                    u.Breach = null;
                    u.Task = new UnitTask("attack") { Target = bg, Amove = new[] { t.X, t.Z }, Then = t.Then, User = false, Auto = true };
                    break;
                }
                if (t.Type == "attackmove")
                {
                    u.ScanT -= dt;
                    // (a hand-to-hand healer on an attack move heals the friends on the way and stays out of the fights)
                    if (u.ScanT <= 0 && MeleeHealer(u)) { u.ScanT = 0.4; var o = HealScan(u); if (o != null) { u.Task = new UnitTask("heal") { Target = o, User = false, Back = t }; break; } }
                    if (u.ScanT <= 0) { u.ScanT = 0.4; var e = FindTarget(u, Math.Max(30, AlarmRange(u)), FindOpts.None); if (e != null) { u.Task = new UnitTask("attack") { Target = e, Amove = new[] { t.X, t.Z }, Then = t.Then, User = false, Auto = true }; break; } }
                }
                if (u.Zeppelin) { ZeppelinStep(u, t, dt); break; }
                double sp = u.Steer(dt, t.Gait > 0 ? u.ScriptSpeed(t.Gait) : u.Task.User && t.Back == null && !t.Slow ? u.RunSpeed : u.Speed, 0.8);
                u.MoveAnim(sp, t.Gait == 0 && u.Task.User && !t.Slow && u.RunSpeed > u.Speed * 1.2, t.Gait);
                if (u.Path.Count == 0) { var next = t.Then; u.Task = UnitTask.Idle(); if (next != null) { Order(u, next); PullGroup(t, next); } }
                break;
            }
            case "attack": AttackUpdate(u, dt); break;
            case "gather": GatherUpdate(u, dt); break;
            case "build": BuildUpdate(u, dt); break;
            case "roam": RoamUpdate(u, dt); break;
            case "flee": FleeUpdate(u, dt); break;
            case "special": SpecialUpdate(u, dt); break;
            case "board": BoardUpdate(u, dt); break;
            case "fountain": FountainUpdate(u, dt); break;
            case "portal": PortalUpdate(u, dt); break;
            case "pickup": PickupUpdate(u, dt); break;
            case "wall": WallUpdate(u, dt); break;
            case "climb": ClimbUpdate(u, dt); break;
            case "dock": DockUpdate(u, dt); break;
            case "finish": FinishUpdate(u, dt); break;
            case "trade": TradeUpdate(u, dt); break;
            case "entrench": u.Vel = V3.Zero; break;
            case "cast": CastUpdate(u, dt); break;
        }
        AnimTick(u, dt);
    }

    /// <summary>advances the unit's animation and its parts (the JS did it a few times a second only off the screen)</summary>
    void AnimTick(Unit u, double dt)
    {
        u.Anim!.Update(dt);
        u.Comp?.Update(dt, u.Task.Type == "attack", u.Passengers);
    }

    /// <summary>idle / hold ground: look for work according to the stance (character.usl idle loop, FightingObj aggro timers)</summary>
    void IdleUpdate(Unit u, double dt)
    {
        var t = u.Task;
        u.Vel = V3.Zero;
        if (u.FightStandT > 0) u.FightStandT -= dt;
        u.IdleTick(dt);
        if (u.Wild || u.Autonomous) { u.Task = new UnitTask("roam"); return; }
        if (u.Name == "ninigi_ninja" && !u.St.Camo.Has("disg") && Time > u.DisgT && Data.Invented(u.RulesOwner(), "disguise", u.Owner!.Tribe, u)) u.St.Camo.Add("disg");
        if (t.VictoryAt != null && Time >= t.VictoryAt) { t.VictoryAt = null; VictoryAnim(u); }
        u.ScanT = (u.ScanT != 0 ? u.ScanT : Rng.Next()) - dt;
        if (u.ScanT > 0) return;
        u.ScanT = 0.9 + Rng.Next() * 0.2;
        IdleScan(u, t);
    }
    // the idle unit's look around (about once a second). A method of its own: the lambdas' captures of `u` made every
    // call of IdleUpdate allocate, also the many that return before the scan.
    void IdleScan(Unit u, UnitTask t)
    {
        // healers look for someone to heal
        if (u.Stats!.Heal != null && u.Owner != null)
        {
            var o = HealScan(u);
            if (o != null && (t.Type == "idle" || u.DistTo(o) - o.Radius <= u.Stats.Heal.Radius)) { u.Task = new UnitTask("heal") { Target = o, Hold = t.Type == "hold" }; return; }
        }
        if (u.Weapons.Long == null || u.CannotFight || u.Stance == 3 || u.Stance == -1) return;
        var cs = u.Cs(u.Weapons.Long);
        if (u.Stance == 1)
        {
            // defensive: fights back only - except units set to it by setAggro (AIAM state 1): they take on enemies
            // that come within 20 m of their post and return to it afterwards
            if (!u.Guard20) return;
            var e1 = FindTarget(u, 20 + u.Radius, new FindOpts { Filter = x => Hypot(x.Pos.X - u.AnchorX, x.Pos.Z - u.AnchorZ) <= 20 + x.Radius });
            if (e1 != null) Engage(u, e1, EngageOpts.None);
            return;
        }
        bool hold = t.Type == "hold" || u.Stance == 0 || u.OnWall != null;      // on a wall: no walk back to the post (FO:2393)
        double r = hold ? AttackRangeOf(u, cs) + u.Radius + 2 : Math.Max(AttackRangeOf(u, cs), 30);
        var e = FindTarget(u, r, new FindOpts { Leash = !hold, Filter = hold ? x => RangeZone(u, x) > 0 : null });
        if (e != null) { Engage(u, e, new EngageOpts { Hold = hold }); return; }
        // an enemy within the alarm range but not taken on: alarmed (FightingObj.SetAlarmed, CAlarmed)
        var a = FindTarget(u, AlarmRange(u), FindOpts.Around);   // (the alarm list: FillEnemyList, before the sort)
        if (a != null) AlarmUnit(u, a);
    }

    /// <summary>SetAlarmed / CAlarmed: the unit turns to the enemy and threatens it once - characters jans_anim_0 .. 2,
    /// animals "menace" (not in the water) - again at most every 10 s</summary>
    public void AlarmUnit(Unit u, Entity e)
    {
        if (Time < u.AlarmT || u.BusyAnim || u.Inside) return;
        u.AlarmT = Time + 10;
        string name = u.Cls == "CHTR" ? "jans_anim_" + (int)Math.Floor(Rng.Next() * 3) : u.Swimming ? "" : "menace";
        if (name == "" || !u.Anim!.Has(name)) return;
        u.Face(e.Pos.X, e.Pos.Z, 1, 100);
        u.BusyAnim = true;
        u.Anim.Play(name, new AnimOpts { Loop = false, Restart = true, OnDone = new AnimDone("busy") });
        Later(Math.Max(0.5, u.Anim.Duration(name)) + 0.2, "animDone", new Args { ["u"] = u, ["name"] = name });
    }

    /// <summary>PlayRandomVictoryAnim: no enemy around - one time in four victory_0 .. 2</summary>
    public void VictoryAnim(Unit u)
    {
        if (u.BusyAnim || FindTarget(u, AlarmRange(u), FindOpts.Around) != null) return;
        if (Rng.Next() >= 0.25) return;
        string name = "victory_" + (int)Math.Floor(Rng.Next() * 3);
        if (!u.Anim!.Has(name)) return;
        // AnimAction(victory_n, Random % 2 + 1, intro, outro) (character.usl:1064): its start part, 1 or 2 loops, its end part
        int loops = 1 + (int)Math.Floor(Rng.Next() * 2);
        u.BusyAnim = true;
        if (u.Anim.PlayLoops(name, loops, true, true, 1, new AnimDone("busy")) <= 0) u.BusyAnim = false;
    }

    void DeadUnitUpdate(Unit u, double dt)
    {
        u.DeadT += dt;
        // a ship's wreck (combat.js shipWreck): its sink animation, then gone (CUniversalCorpse: 10 s). The pirate ship's
        // hull wreck plays from when it shows (Wreck.From: PirateShip.cs PirateWreck); its parts' wrecks are timed only
        if (u.Wreck != null)
        {
            if (u.DeadT >= u.Wreck.From) u.Wreck.Anim?.Update(dt);
            if (u.DeadT > u.Wreck.Until && !u.Removed) { u.Removed = true; u.LookChanged(); }
            return;
        }
        u.Anim!.Update(dt);
        if (u.Finish != null) FinishedUpdate(u);              // the victim of a finishing move (systems/finishing.js)
        double stay = u.CorpseNode != null ? (u.CorpseGone ? 0 : 1e9) : 12;
        if (u.CorpseNode != null && u.CorpseNode.Alive) { u.CorpseNode.RotT -= dt; if (u.CorpseNode.RotT <= 0) DepleteNode(u.CorpseNode); }
        if (u.SilentDeath && !u.Removed) { u.Removed = true; u.LookChanged(); return; }
        if (u.DeadT > stay) { u.SinkY += dt * 0.6; if (u.DeadT > stay + 4) { u.Removed = true; u.LookChanged(); } }
    }

    // ------------------------------------------------------------------ per-building update
    /// <summary>the time of day in seconds (0 .. 86400)</summary>
    public double DayTime() => (((DayStart + Time * VIRTUAL_FACTOR) % DAY) + DAY) % DAY;
    /// <summary>is it night (CTimeMgr.IsVirtualDay)</summary>
    public bool IsNight()
    {
        if (NightFn != null) return NightFn();
        double f = DayTime() / DAY;
        return f < 0.25 || f > 0.75;
    }

    /// <summary>a finished building (Building.usl NIGHT_TIMER) or a ship (Ship.usl TIMER_NIGHT) looks at the time of day every
    /// 20 s and switches its lights; it starts with them off</summary>
    void NightCheck(Unit e, double dt)
    {
        if ((e.NightT += dt) < 20) return;
        e.NightT = 0;
        bool n = IsNight();
        if (n != e.Night) { e.Night = n; e.LookChanged(); }
    }
    void NightCheck(Building e, double dt)
    {
        if ((e.NightT += dt) < 20) return;
        e.NightT = 0;
        e.Night = IsNight();
    }

    void BuildingUpdate(Building b, double dt)
    {
        if (!b.Alive)
        {
            if (b.Removed) return;
            b.DeadT += dt;
            // a ruin that stays (CBuildingCorpse.ShouldStay): it never sinks; the ruins of its parts that do not stay
            // (the machine-gun nest's top) go when the others' would (Construction.PermanentRuin)
            if (b.Ruin != null && b.RuinStays) { if (b.DeadT > 20 && b.RuinParts != null && !b.RuinPartsSettled) SettleRuinParts(b); return; }
            if (b.Ruin != null) { if (b.DeadT > b.RuinSinkAt) { b.SinkY += dt * 1.0; if (b.DeadT > b.RuinSinkAt + 12) { b.Removed = true; b.LookChanged(); } } }
            else { b.SinkY += dt * b.Ht * 0.25; if (b.DeadT > 5) { b.Removed = true; b.LookChanged(); } }
            return;
        }
        if (b.Parked) return;
        var P = Profile;
        P?.Sub();
        b.UpdateAnim(dt, Time);
        if (b.Built) NightCheck(b, dt);
        b.UpdateVisual();
        P?.SubMark("bld:anim");
        // (the smoke and fire at the damage links of damaged buildings - world.js damageFx - are the view's: DmgStage)
        if (!b.Built) { ConstructionUpdate(b, dt); SiteProgressed(b); P?.SubMark("bld:construction"); return; }
        ProductionUpdate(b, dt);
        P?.SubMark("bld:production");
        TowerUpdate(b, dt);
        P?.SubMark("bld:tower");
        if (WaterNav != null) HarbourUpdate(b, dt);
        P?.SubMark("bld:harbour");
        b.Behaviour?.Update(b, this, dt);
        P?.SubMark("bld:behaviour");
    }

    /// <summary>does player p have a unit that can resurrect (the Aje shaman's Resurrect move)? checked once a second</summary>
    public bool CanResurrect(Player? p)
    {
        if (p == null) return false;
        _resCan ??= new Dictionary<Player, (double, bool)>();
        if (_resCan.TryGetValue(p, out var c) && Time - c.T < 1) return c.V;
        _resBy ??= new Dictionary<string, bool>();      // unit name -> has Resurrect
        bool Has(Unit u)
        {
            var k = u.Name + "|" + u.Level;
            if (!_resBy.TryGetValue(k, out var v)) _resBy[k] = v = MovesOf(u).Any(a => a.Id == "Resurrect" || a.Id == "reanimation");
            return v;
        }
        bool val = Units.Any(u => u.Alive && u.Owner == p && u.Cls == "CHTR" && Has(u));
        _resCan[p] = (Time, val);
        return val;
    }

    /// <summary>spirits fade after 60 s (they glimmer while they last for players with a shaman: the view, CanResurrect)</summary>
    void SpiritTick(double dt)
    {
        _spiritFx -= dt;
        if (_spiritFx <= 0) _spiritFx = 0.6;
        foreach (var s in Spirits) if (Time > s.Until) s.Alive = false;
        Spirits = Spirits.Where(s => s.Alive).ToList();
    }

    /// <summary>(p null: any player's spirit)</summary>
    public Spirit? NearestSpirit(Player? p, double x, double z, double r = 8)
    {
        Spirit? best = null; double bd = r;
        foreach (var s in Spirits)
        {
            if ((p != null && s.Owner != p) || !s.Alive || s.Busy) continue;
            double d = Hypot(s.Pos.X - x, s.Pos.Z - z);
            if (d < bd) { bd = d; best = s; }
        }
        return best;
    }

    /// <summary>an entity whose tree changed re-reads its values (checked every half second)</summary>
    void RefreshTick()
    {
        bool Changed(Entity e)
        {
            var T = e.RulesOwner()?.Tt;
            if (T == null) return false;
            if (T is LocalTree lt) lt.Sync();                  // local trees follow their owner's tree
            return T.Version != e.TtVersion;
        }
        foreach (var e in Units.ToList()) if (e.Alive && e.Owner != null && Changed(e)) e.RefreshRules();
        foreach (var e in Buildings.ToList()) if (e.Alive && e.Owner != null && Changed(e)) { e.RefreshRules(); RecomputeCaps(e.Owner); }
    }

    // ------------------------------------------------------------------ main update
    /// <summary>
    /// One fixed step of the game (docs/ARCHITECTURE.md "The simulation step"): the commands due, the world's update
    /// (delayed actions, effects, units, buildings, projectiles, separation, removal of the dead), the computer players,
    /// the campaign. The events stay in Events until the view / interface drains them.
    /// </summary>
    public void Step()
    {
        StepNo++;
        if (TrimEvents) TrimOldEvents();
        var P = Profile;
        if (P == null)
        {
            ApplyCommands();
            Update(STEP);
            AiUpdate(STEP);
            CampaignUpdate(STEP);
            FogStep();
            Stepped?.Invoke();
            return;
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        StepProfile.Current = P;
        P.Begin();
        ApplyCommands(); P.Mark("commands");
        Update(STEP);
        AiUpdate(STEP); P.Mark("ai");
        CampaignUpdate(STEP); P.Mark("campaign");
        FogStep(); P.Mark("fog");
        StepProfile.Current = null;
        P.EndStep(System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds, StepNo);
        Stepped?.Invoke();
    }
    /// <summary>after every step (a saved game's recorder takes its verification hashes: docs/SAVES.md)</summary>
    public event Action? Stepped;
    /// <summary>where the step's time goes (debug; null: not measured) - StepProfile</summary>
    public StepProfile? Profile;

    /// <summary>world.js update(dt): one step of dt seconds (World.Step uses STEP)</summary>
    public void Update(double dt)
    {
        Time += dt;
        var P = Profile;
        // delayed actions
        if (Timers.Count > 0)
        {
            bool any = false;
            foreach (var x in Timers) if (x.T <= Time) { any = true; break; }      // (most steps: none due - nothing allocated)
            if (any)
            {
                // (the due ones out, the others kept in order in the same list: two new lists a step cost ~10 KB with a
                // thousand timers waiting; the actions may add timers - after the kept ones, as before)
                var due = _due.Count == 0 ? _due : new List<Timer>();      // (a nested call: a list of its own)
                int w = 0;
                for (int i = 0; i < Timers.Count; i++) { var x = Timers[i]; if (x.T <= Time) due.Add(x); else Timers[w++] = x; }
                Timers.RemoveRange(w, Timers.Count - w);
                try { foreach (var tm in due) Delayed.DELAYED[tm.Kind](this, tm.Args); }
                finally { due.Clear(); }
            }
        }
        P?.Mark("timers");
        EffectsTick(dt);
        EffectsUpdate();
        P?.Mark("effects");
        if (Spirits.Count > 0) SpiritTick(dt);
        if (WildNests.Count > 0) WildNestsUpdate(dt);
        if (Wells.Count > 0) WellsUpdate(dt);
        if (Lakes.Count > 0) LakesUpdate(dt);
        if (Weather != null) WeatherUpdate();
        if (Go != null) GameOverUpdate(dt);              // the end of a skirmish (systems/gameover.js)
        StatsUpdate();
        _refreshT -= dt;
        P?.Sub();
        if (_refreshT <= 0) { _refreshT = 0.5; RefreshTick(); }
        P?.SubMark("world:refresh");
        P?.Mark("world");
        if (P == null) for (int i = 0; i < Units.Count; i++) UnitUpdate(Units[i], dt);
        else for (int i = 0; i < Units.Count; i++)
        {
            var u = Units[i]; string k = StepProfile.Key("unit:", u.Alive ? u.Task.Type : "dead");
            long t = System.Diagnostics.Stopwatch.GetTimestamp(), b0 = GC.GetAllocatedBytesForCurrentThread();
            UnitUpdate(u, dt);
            long dtk = System.Diagnostics.Stopwatch.GetTimestamp() - t;
            P.Add(k, dtk, GC.GetAllocatedBytesForCurrentThread() - b0);
            P.Single(dtk, k, u);
        }
        P?.Mark("units");
        for (int i = 0; i < Buildings.Count; i++) BuildingUpdate(Buildings[i], dt);
        P?.Mark("buildings");
        foreach (var p in Props) p.Update(dt);
        for (int i = 0; i < Projectiles.Count; i++) ProjectileUpdate(Projectiles[i], dt);     // (live: a hit may shoot again)
        if (Projectiles.Count > 0) Projectiles.RemoveAll(static p => !p.Alive);
        P?.Mark("props+projectiles");
        Separation(dt);
        P?.Mark("separation");
        foreach (var u in Units) if (u.Alive && !u.Inside && !u.Parked) { u.Pos.Y = GroundY(u) + u.JumpH + u.LiftH; UHash.Move(u); }
        if (Units.Any(u => u.Removed)) { foreach (var u in Units) if (u.Removed) Forget(u); Units = Units.Where(u => !u.Removed).ToList(); }
        if (Buildings.Any(b => b.Removed)) { foreach (var b in Buildings) if (b.Removed) Forget(b); Buildings = Buildings.Where(b => !b.Removed).ToList(); }
        if (Resources.Count > 50 && Resources.Any(r => !r.Alive)) { foreach (var r in Resources) if (!r.Alive) Forget(r); Resources = Resources.Where(r => r.Alive).ToList(); }
        if (Props.Any(p => !p.Alive)) Props = Props.Where(p => p.Alive).ToList();
        P?.Mark("heights+cleanup");
    }

    void Forget(Entity e)
    {
        _byId.Remove(e.Id);
        if (e is Unit u && u.HashKey != SpatialHash<Unit>.NoKey) UHash.Remove(u);
        else if (e is Building b && b.HashKey != SpatialHash<Building>.NoKey) BHash.Remove(b);
        else if (e is ResNode r && r.HashKey != SpatialHash<ResNode>.NoKey) RHash.Remove(r);
    }

    /// <summary>units push each other apart; nobody stands inside blocked cells or an enemy's closed gate</summary>
    void Separation(double dt)
    {
        void Shift(Unit u, double mx, double mz)
        {
            var nav = NavFor(u);
            double x = u.Pos.X + mx, z = u.Pos.Z + mz;
            if (u.CanWalkInto(nav, x, z)) { u.Pos.X = x; u.Pos.Z = z; }
            else if (u.CanWalkInto(nav, x, u.Pos.Z)) u.Pos.X = x;
            else if (u.CanWalkInto(nav, u.Pos.X, z)) u.Pos.Z = z;
        }
        foreach (var a in Units)
        {
            if (!a.Alive || a.Inside || a.Parked || a.Flying || a.OnWall != null || a.WJump != null || a.Climbing) continue;   // (in the air, on a wall: over everything)
            // (the neighbours into a list kept by the world: a lambda here cost an allocation per unit and step)
            var near = _sepNear; near.Clear();
            UHash.Collect(a.Pos.X, a.Pos.Z, a.Radius + 6, near);
            foreach (var b in near)
            {
                if (b == a || !b.Alive || b.Inside || b.Parked || b.Id < a.Id || b.Naval != a.Naval || b.Flying || b.OnWall != null || b.WJump != null || b.Climbing) continue;
                double dx = b.Pos.X - a.Pos.X, dz = b.Pos.Z - a.Pos.Z;
                double min = (a.Radius + b.Radius) * 0.9;
                double d2 = dx * dx + dz * dz;
                if (d2 >= min * min) continue;
                double d = Math.Sqrt(d2), push = min - d;
                // two units exactly on top of each other (trained at the same spawn point in one step, put by a script on
                // one spot) have no direction to part in: one fixed by their ids. (The JS skipped such a pair, so they
                // stayed one inside the other for good - and walked as one: the same spot, the same path.)
                if (d2 < 1e-6) { double ang = ((a.Id * 7919L + b.Id * 104729L) % 3600) * (Math.PI / 1800); dx = SMath.Cos(ang); dz = SMath.Sin(ang); d = 1; }
                // moving units yield to working/fighting ones
                double wa = LenSq(a.Vel) > 0.01 ? 1 : 0.35, wb = LenSq(b.Vel) > 0.01 ? 1 : 0.35;
                double ma = a.Radius * a.Radius, mb = b.Radius * b.Radius;
                double fa = (mb / (ma + mb)) * wa, fb = (ma / (ma + mb)) * wb;
                double s = fa + fb; if (s == 0) s = 1;
                // pushes never move a unit into a building / closed gate (it would hover inside and look stuck)
                Shift(a, -dx / d * push * fa / s, -dz / d * push * fa / s);
                Shift(b, dx / d * push * fb / s, dz / d * push * fb / s);
            }
            var nav = NavFor(a);
            int k0 = nav.Idx(a.Pos.X, a.Pos.Z);
            bool blocked = nav.Block[k0] != 0 || (nav.GateAt.Count > 0 && nav.GateBlocks(k0, a.Owner));
            if (blocked)
            {
                int c = nav.NearestFree(k0, 4);
                if (nav.Block[k0] == 0)
                {
                    var (gx, gz) = nav.Center(k0); nav.GateAt.TryGetValue(k0, out var g);
                    double ax = a.Pos.X - (g != null ? g.Pos.X : gx), az = a.Pos.Z - (g != null ? g.Pos.Z : gz);
                    double Lg = Hypot(ax, az); if (Lg == 0) Lg = 1;
                    a.Pos.X += ax / Lg * 4 * dt; a.Pos.Z += az / Lg * 4 * dt;
                    continue;
                }
                var (cx, cz) = nav.Center(c);
                double ddx = cx - a.Pos.X, ddz = cz - a.Pos.Z, dd = Hypot(ddx, ddz);
                if (dd > 0.01) { double k = Math.Min(1, 6 * 0.016 / dd + 0.05); a.Pos.X += ddx * k; a.Pos.Z += ddz * k; }
            }
        }
    }
    static double LenSq(V3 v) => v.X * v.X + v.Y * v.Y + v.Z * v.Z;
    readonly List<Unit> _sepNear = new();
    /// <summary>(Unit.Avoid's neighbours: a list kept by the world, no allocation per unit and step)</summary>
    internal readonly List<Unit> SteerNear = new();
}
