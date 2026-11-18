// Campaign world setup: turns a mission's data (the mission folder: mission.json, world.json, triggers.lua - see
// MissionData.cs) into a running world - the players with their diplomacy, every placed object with owner, level, hit
// points and flags, the regions, groups, question marks and start locations - and is the run-time context the trigger
// engine works on (through MissionHost.cs). The interface is described in remake/docs/CAMPAIGN_RUNTIME.md and
// docs/CAMPAIGN.md; what a mission start consists of in the original is in remake/docs/spec/triggers.md §6.7.
//
// Order of a campaign start (MissionSession.Start, main.js startGame):
//   new Campaign(data, cfg)     the mission data and the player's choices
//   Plan(rules, models)         classify the objects, collect the models the mission needs
//   map source                  the map's landscape (MapSource leaves out what the campaign places itself: Skip)
//   WorldBuilder.Build          the world, its ground and landscape; Campaign.Build: players, objects, groups, start
//                               locations; then the map's wild animals and nests, WildlifeReady
//   Update(dt)                  every simulation step: registry upkeep, the world's events to the trigger engine
//
// Ported from remake/src/game/campaign/setup.js (pw-exporter 98c7703). What drew (three.js) is the view's; the
// models a mission "loads" are only collected (Need, FullTribes) for the view - the simulation knows every model of
// the manifest.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Rules;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Campaign;

/// <summary>The player's choices for a mission (JS cfg: campaign, difficulty, tribe, cheats).</summary>
public sealed class CampaignConfig
{
    /// <summary>0 easy, 1 medium, 2 hard, 3 nightmare (hard for the triggers, the computer players' bigger raids)</summary>
    public int Difficulty = 1;
    /// <summary>"" = the mission's own tribe, else Hu, Aje, Ninigi, SEAS (tribes.js)</summary>
    public string Tribe = "";
    /// <summary>the debug options of the game (null = a normal game)</summary>
    public PwRemake.Sim.Game.DebugOptions? Cheats;
    /// <summary>the computer player's data (ai.json: the AIFT army tables of the prescan), or null</summary>
    public JsonNode? AiData;
    /// <summary>the human's start army and resources bought on the army screen (a mission with point_buy: game/pointbuy.js;
    /// null: the level's preset army)</summary>
    public PwRemake.Sim.Game.Army? Army;
    /// <summary>a co-op game (docs/MULTIPLAYER.md): the players commanded by humans (GameStart.SeatPlayers) - besides the
    /// mission's human player, allied computer players a second human took over: they get no brain</summary>
    public List<int>? Humans;
    /// <summary>a co-op campaign game (mode 2, docs/COOP.md): the seats, the scaling, the trigger mapping</summary>
    public PwRemake.Sim.Coop.CoopRuntime? Coop;
    /// <summary>MIRAGE's game options (sixth epoch, no titans) for every player of the mission (GameStart.Gameplay;
    /// null = the defaults)</summary>
    public PwRemake.Sim.Rules.GameplayOptions? Gameplay;
}

/// <summary>What the setup decided for one object of the data (setup.js plans[]).</summary>
public sealed class ObjectPlan
{
    public JsonNode O = null!;
    /// <summary>none | helper | part | unit | building | nest | landscape | resource | prop | missing</summary>
    public string Kind = "none";
    public string? Cls, Model;
    public bool Swapped;
    /// <summary>the class's own type differs from the type the map stored (Campaign.Plan): the record takes the class's</summary>
    public bool Retyped;
    public Rec? Rec;
}

/// <summary>A question mark of the map (QMRK).</summary>
public sealed class QuestionMark
{
    public string Guid = "", Name = "", State = "STATE_INVISIBLE", Tooltip = "";
    /// <summary>game coordinates</summary>
    public double X, Z;
    public Rec? Rec;
}

/// <summary>A start location of a player (game coordinates).</summary>
public sealed class StartLocation
{
    public double X, Z, Heading;
    public JsonNode Data = null!;
}

public sealed class CampaignCounts { public int Units, Buildings, Props, Landscape, Parts, Helpers, Missing; }

public sealed partial class Campaign : ICampaignStep
{
    static readonly Logger L = Log.For("Campaign");
    static readonly HashSet<string> UNIT_TYPES = new(StringComparer.Ordinal) { "CHTR", "ANML", "VHCL", "SHIP" };
    static readonly HashSet<string> FIGHTING_TYPES = new(StringComparer.Ordinal) { "CHTR", "ANML", "VHCL", "SHIP", "BLDG" };
    // the landscape of the map: left to the map source (instanced, resources, nests) unless a mission addresses the object
    static readonly HashSet<string> LANDSCAPE = new(StringComparer.Ordinal) { "TREE", "VGTN", "WOOD", "STON", "FRUI", "DCCO", "DECO", "NEST" };
    static readonly HashSet<string> RESOURCES = new(StringComparer.Ordinal) { "TREE", "WOOD", "STON", "FRUI", "NEST" };
    // parts of other objects that the remake assembles itself (World/Compose.cs): riders, build-ups, turrets, cranes
    static readonly HashSet<string> PARTS = new(StringComparer.Ordinal) { "PROD", "TRRT", "MNIO" };
    // helper objects of the editor / the scripts: a record, nothing in the world
    static readonly HashSet<string> HELPERS = new(StringComparer.Ordinal) { "SLOC", "WYPT", "GROU", "QMRK", "OTHR", "DMGL", "COLL", "FOOD", "ITSP" };
    // big scenery that units cannot walk through (the rule of the map source for landscape objects)
    static readonly Regex SOLID = new("rock|plateau|cliff|mountain|stone(?!_jun)|ruin|pillar|wall|temple|monument|archway|statue|head|coffin|stoneglobe|berg|basalt|arc0|wreck|crater|dead_tree|log|house|barricade|cage|gate|blocker|tower|hut|jail", RegexOptions.CultureInvariant);
    // (bridges are not among them: every bridge class is a CDecoColObj, which hands its model's pathfinder boxes to the
    // pathfinder - MiscObj.usl AddPFBlocker - as the map source's bridges do; a bridge without boxes keeps units only from
    // under its deck, World/Bridges.cs. Bridges never get the circle of the remake's own maps: they are walked through.)
    static readonly Regex NO_BLOCK = new("river|stream|lava|waterfall|flag|fx_|smoke|flame|decal|pile_of_goods|_dest$", RegexOptions.CultureInvariant);
    public static readonly Dictionary<string, string> MAIN_BUILDING = new(StringComparer.Ordinal) { ["Hu"] = "hu_fireplace", ["Aje"] = "aje_resource_collector", ["Ninigi"] = "ninigi_fireplace", ["SEAS"] = "seas_headquarters" };
    // classes that exist in the rules but have no model in the game data: what stands in for them
    static readonly Dictionary<string, string> STAND_IN = new(StringComparer.Ordinal) { ["stina_s0"] = "special_eusmilus" };   // Stina rides her sabre-tooth (StartLocation.usl:517-519)
    static readonly Dictionary<string, string> STAND_IN_CHTR = new(StringComparer.Ordinal) { ["Hu"] = "hu_warrior", ["Aje"] = "aje_warrior", ["Ninigi"] = "ninigi_warrior", ["SEAS"] = "seas_warrior", ["Special"] = "hu_warrior" };
    static readonly string[] QM_STATES = { "STATE_INVISIBLE", "QM_STATE_RED", "QM_STATE_GREEN", "QM_STATE_YELLOW", "EC_STATE_YELLOW" };

    public readonly MissionData Data;
    public readonly int Id;
    /// <summary>0 easy, 1 medium, 2 hard (Nightmare: 2, and Nightmare true)</summary>
    public readonly int Difficulty;
    /// <summary>3 Nightmare (the remake's own): the map, its triggers and AI levels as hard, the computer players' raids
    /// with the remake's bigger squads (ai/attack.js extraSquads)</summary>
    public readonly bool Nightmare;
    public readonly string? ChosenTribe;
    public readonly CampaignConfig Config;
    /// <summary>TribeSwap when the player plays another tribe than the mission's</summary>
    public TribeSwap? Swap;
    public JsonNode Map => Data.Map;
    /// <summary>Player per slot (null: not present)</summary>
    public readonly Player?[] Players = new Player?[PwRemake.Sim.Coop.CoopPlan.MaxSlots];
    public Player? Human;
    public int HumanSlot;
    /// <summary>a lockstep game: the camera and selection of the host's seat as the mission's tutorial conditions see them
    /// (UiStateCommand; LockstepPresenter answers from it - docs/MULTIPLAYER.md)</summary>
    public (double X, double Y, double Dist, double Yaw)? SharedCamera;
    public HashSet<int> SharedSelection = new();
    public string MissionTribe = "", HumanTribe = "";
    public Registry Objects = null!;
    public Regions Regions = null!;
    public readonly List<Group> Groups = new();
    public readonly List<QuestionMark> QuestionMarks = new();
    public readonly StartLocation?[] StartLocations = new StartLocation?[PwRemake.Sim.Coop.CoopPlan.MaxSlots];
    /// <summary>the stand-alone scenery (props)</summary>
    public readonly List<Prop> Props = new();
    /// <summary>what could not be placed, as sentences</summary>
    public readonly List<string> Warnings = new();
    public readonly CampaignCounts Counts = new();
    /// <summary>tribes of the present players; tribes whose models the view loads completely (the human's)</summary>
    public List<string> Tribes = new(), FullTribes = new();
    /// <summary>models to load (Plan, NeedClass) - for the view</summary>
    public readonly ModelSet Need = new();
    /// <summary>names of map objects the landscape source must leave out (MapSourceConfig.SkipObjects)</summary>
    public readonly HashSet<string> Skip = new(StringComparer.Ordinal);
    public readonly List<ObjectPlan> Plans = new();
    readonly OrderedMap<string, (int N, string Why)> _missing = new();
    readonly List<Rec> _resourceRecs = new();
    /// <summary>props of items and their records (the JS put rec on the prop)</summary>
    readonly Dictionary<Prop, Rec> _propRecs = new();
    HashSet<string> _namedClasses = new(StringComparer.Ordinal);
    /// <summary>the absolute number (World.EventBase + index) of the next world event to read</summary>
    long _evAbs;
    public bool Ready;
    public GameWorld World = null!;
    public Rules.Rules D = null!;
    public ModelManifest Manifest = null!;
    double _ox, _oy;
    Rec? _pending;
    Dictionary<string, string>? _lcIndex;
    Dictionary<string, string>? _mounts;
    /// <summary>bonus points of the mission (QUES accomplished + BONI): the engine's Boni</summary>
    public double Boni => Engine?.Boni ?? 0;
    /// <summary>the trigger engine (null: none - only the world, as the JS ?notriggers)</summary>
    public TriggerEngine? Engine;
    /// <summary>the host the engine works through (MissionHost.cs)</summary>
    public MissionHost? Host;

    // ------------------------------------------------------------------ events (setup.js on / emit)
    /// <summary>a unit, building or prop came into the world (placed, trained, built, spawned)</summary>
    public event Action<Rec>? Spawned;
    /// <summary>an object died, was destroyed or deleted; rec.Alive is already false, it has left its group</summary>
    public event Action<Rec>? Removed;
    /// <summary>group membership changed (GROU_CHG)</summary>
    public event Action<Group, Rec, bool>? GroupChanged;
    /// <summary>the owner changed (from, to: Player or null)</summary>
    public event Action<Rec, Player?, Player?>? OwnerChanged;
    /// <summary>a relation changed (the world's diplomacy event)</summary>
    public event Action<WorldEvent>? DiplomacyChanged;
    public event Action<QuestionMark>? QuestionMarkChanged;
    /// <summary>every event of World.Events, once, in order</summary>
    public event Action<WorldEvent>? WorldEventSeen;

    public Campaign(MissionData data, CampaignConfig cfg)
    {
        Data = data;
        Config = cfg;
        Id = data.Id;
        Difficulty = Math.Max(0, Math.Min(2, cfg.Difficulty));          // 0 easy, 1 medium, 2 hard
        Nightmare = cfg.Difficulty == 3;
        ChosenTribe = TribeSwap.TRIBES.Contains(cfg.Tribe) ? cfg.Tribe : null;
    }

    // ------------------------------------------------------------------ coordinates
    // map (x east, y south; metres from the map's north-west corner) <-> game (x east, z south, origin = centre)
    public (double X, double Z) ToGame(double mx, double my) => (mx - _ox, my - _oy);
    public (double MX, double MY) ToMap(double x, double z) => (x + _ox, z + _oy);
    /// <summary>heading of the map data (objects[].rot, the z angle of obj_rot) -> the world's heading (its own inverse)</summary>
    public static double Heading(double rot) => Frame.MapHeading(rot);
    public Player? PlayerAt(int slot) => slot >= 0 && slot < Players.Length ? Players[slot] : null;
    /// <summary>a co-op campaign game (CampaignConfig.Coop), else null</summary>
    public PwRemake.Sim.Coop.CoopRuntime? Coop => Config.Coop;
    public string Text(string key) => Data.Script.Texts.TryGetValue(key, out var t) ? t : key;
    public void Warn(string msg) { if (!Warnings.Contains(msg)) Warnings.Add(msg); }
    void Miss(string cls, string reason) { var e = _missing.TryGetValue(cls, out var v) ? v : (0, reason); _missing.Set(cls, (e.Item1 + 1, e.Item2)); }

    // ------------------------------------------------------------------ plan (before the world is built)
    string? ModelOf(JsonNode o)
    {
        var g = o["gfx"].Str().ToLowerInvariant();
        var lc = o["class"].Str().ToLowerInvariant();
        var cg = D.ClassGfx(lc);
        return g.Length > 0 && Manifest.Has(g) ? g : Manifest.Has(lc) ? lc : cg != null && Manifest.Has(cg) ? cg.ToLowerInvariant() : null;
    }
    /// <summary>the mount whose captain (rider) class is `cls`, for a rider class that cannot stand on its own (its tech
    /// tree object has a placeholder 1 hit point), else null</summary>
    public string? MountOf(string cls)
    {
        var s = D.Stats(cls, 1, null);
        if (s == null || !(s.Hp <= 1)) return null;
        if (_mounts == null)
        {
            _mounts = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var k in D.IndexOrder) { var st = D.Stats(k, 1, null); if (st?.Captain != null) _mounts.TryAdd(st.Captain.ToLowerInvariant(), k); }
        }
        return _mounts.TryGetValue(cls.ToLowerInvariant(), out var m) ? m : null;
    }
    /// <summary>exact class name of the rules for a class written in any case</summary>
    public string? RuleClass(string? cls)
    {
        if (cls == null) return null;
        if (D.Exists(cls)) return cls;
        if (_lcIndex == null) { _lcIndex = new Dictionary<string, string>(StringComparer.Ordinal); foreach (var k in D.IndexOrder) _lcIndex.TryAdd(k.ToLowerInvariant(), k); }
        return _lcIndex.TryGetValue(cls.ToLowerInvariant(), out var r) ? r : null;
    }
    static readonly Regex ToolRe = new("_(hammer|axe|pickaxe|pick|sickle|saw|basket|pannier|jug|backpack|tool)", RegexOptions.CultureInvariant);
    /// <summary>every model a rule class can show: its looks per level, weapons, projectiles, riders and build-ups</summary>
    public bool ClassModels(string cls, ModelSet? into = null)
    {
        var o = into ?? Need;
        void Add(string? n) { n = n?.ToLowerInvariant(); if (!string.IsNullOrEmpty(n) && Manifest.Has(n)) o.Use(n); }
        var name = RuleClass(cls);
        if (name == null) return false;
        var ix = D.Info(name)!;
        for (int l = 1; l <= 5; l++)
        {
            var s = D.Stats(name, l, null);
            if (s == null || (l > 1 && !s.Exists)) continue;
            Add(s.Gfx); if (ix.Type == "BLDG") Add(s.Gfx + "_dest");
            if (ix.Type == "BLDG") break;
            var ws = D.WeaponSetOf(name, l, null);
            foreach (var w in ws.All) { Add(w.Projectile); foreach (var p in w.Parts) foreach (var g in p.Gfx) Add(g); }
        }
        if (ix.Type == "BLDG") { var ws = D.WeaponSetOf(name, 1, null); foreach (var w in ws.All) Add(w.Projectile); }
        Add(D.ClassGfx(name));
        foreach (var s in D.Composites(name))
        {
            Add(s.Gfx); foreach (var v in s.Variants) Add(v);
            if (s.Kind == "rider" && s.Gfx.Length > 0 && char.IsDigit(s.Gfx[^1])) for (int l = 1; l <= 5; l++) Add(s.Gfx[..^1] + l);
        }
        var d = D.Def(name, null);
        if (d?.CaptainGfx is { Length: > 0 } cg) { Add(cg); if (char.IsDigit(cg[^1])) for (int l = 1; l <= 5; l++) Add(cg[..^1] + l); }
        if (!string.IsNullOrEmpty(ix.Tribe) && ix.Tribe != "World" && ix.Tribe != "Special")
        {
            var t = ix.Tribe.ToLowerInvariant();
            for (int l = 1; l <= 5; l++) Add($"{t}_animal_flag_0{l}");
            if (d != null && (d.CanBuild != 0 || d.CanHarvest != 0)) foreach (var n in Manifest.Names) if (n.StartsWith(t + "_", StringComparison.Ordinal) && ToolRe.IsMatch(n)) o.Use(n);
        }
        var si = StandInModel(name);
        if (si != null) Add(si);
        return true;
    }
    /// <summary>model standing in for a rule class without a model of its own (null if it has one or nothing fits)</summary>
    public string? StandInModel(string name)
    {
        var ix = D.Info(name);
        var s = D.Stats(name, 1, null);
        bool Has(string? g) => !string.IsNullOrEmpty(g) && Manifest.Has(g);
        if (ix == null || Has(s?.Gfx) || Has(D.ClassGfx(name))) return null;
        var alt = STAND_IN.TryGetValue(name.ToLowerInvariant(), out var a) ? a : ix.Type == "CHTR" && STAND_IN_CHTR.TryGetValue(ix.Tribe, out var c) ? c : null;
        var g = alt != null ? D.Stats(alt, 1, null) : null;
        return g != null && Has(g.Gfx) ? g.Gfx!.ToLowerInvariant() : null;
    }
    /// <summary>a class the mission will create later (trigger spawns, AI production): its models are listed for the view.
    /// After the build it only reports whether the class can be created now.</summary>
    public bool NeedClass(string cls)
    {
        var name = RuleClass(cls);
        // (an item: its chest - ItemModelOf)
        if (name == null && ItemModelOf(cls) is string im) { if (!Ready) Need.Use(im); return true; }
        if (Ready)
        {
            if (name != null) return D.Info(name)!.Type == "BLDG" ? Manifest.Has(D.Stats(name, 1, null)?.Gfx) : World.UnitModel(name, null, null) != null;
            return Manifest.Has(cls.ToLowerInvariant());
        }
        if (name != null) return ClassModels(name);
        var lc = cls.ToLowerInvariant();
        if (Manifest.Has(lc)) { Need.Use(lc); if (Manifest.Has(lc + "_dest")) Need.Use(lc + "_dest"); return true; }
        return false;
    }
    static readonly Regex SkipKey = new(@"^(guid|rgn_guid|.*_guid|pos|obj_pos|waypoints|msg_text|text)$", RegexOptions.CultureInvariant);
    static readonly Regex WordSplit = new(@"[|\s,;:/§&()[\]]+", RegexOptions.CultureInvariant);
    static readonly Regex Numeric = new(@"^[-\d.]+$", RegexOptions.CultureInvariant);
    /// <summary>every word of every trigger parameter that names a class or a model: what the mission may create or address</summary>
    public PwRemake.Sim.World.OrderedSet<string> NamedByTriggers()
    {
        var res = new PwRemake.Sim.World.OrderedSet<string>();
        foreach (var t in Data.Triggers)
            foreach (var n in t.Conditions.Concat(t.Actions))
                foreach (var (k, v) in n.P)
                {
                    if (v.Length < 3 || SkipKey.IsMatch(k)) continue;
                    foreach (var w in WordSplit.Split(v)) if (w.Length > 2 && !Numeric.IsMatch(w)) res.Add(w);
                }
        return res;
    }
    /// <summary>the classes of the objects an OCPY action gives the player by name (mission 12's telescope tower ruin): the
    /// player is given that very object, whatever tribe it was chosen to play</summary>
    public HashSet<string> GivenClasses()
    {
        var byName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var o in Data.Objects) if (o != null) byName[o["name"].Str()] = o["class"].Str().ToLowerInvariant();
        var res = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in Data.Triggers) foreach (var a in t.Actions) if (a.Code == "OCPY" && a.P.TryGetValue("obj_name", out var n) && byName.TryGetValue(n, out var c)) res.Add(c);
        return res;
    }
    /// <summary>the classes (lower case) of the units the player starts with: its start army and the units placed for it
    /// on the map. An objective about them is not waived when the chosen tribe makes their equivalent only later in the
    /// game (TribeSwap.Missing): MIRAGE's 4-1 for the SEAS (mission 20) gives three flamethrowers and waits for them in a
    /// region; as the Norsemen (flamethrowers from epoch 3) the region was "reached" at once</summary>
    public HashSet<string> StartUnitClasses(JsonNode? human)
    {
        var res = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in human?["start_army"]?.AsArray() ?? new JsonArray()) if (a?["class"].Str() is { Length: > 0 } c) res.Add(c.ToLowerInvariant());
        foreach (var o in Data.Objects)
            if (o != null && o["owner"] is JsonValue ov && ov.TryGetValue(out double od) && (int)od == HumanSlot && o["type"].Str() is "CHTR" or "ANML" or "VHCL" or "SHIP")
                res.Add(o["class"].Str().ToLowerInvariant());
        return res;
    }
    /// <summary>does a condition of the mission ask for the player's objects of this tribe's classes? Mission 14 lets the
    /// player bring the Norsemen, the Dustriders or the Dragon Clan and asks which tribe's workers it has</summary>
    public bool AsksFor(string tribe)
    {
        var slot = HumanSlot.ToString();
        foreach (var t in Data.Triggers)
            foreach (var c in t.Conditions)
                if (c.P.TryGetValue("obj_owner", out var ow) && ow == slot && TribeSwap.TribeOfClass(c.P.GetValueOrDefault("obj_class") ?? "") == tribe) return true;
        return false;
    }

    /// <summary>D = the rules, manifest = the asset manifest's models. Classifies every placed object and collects models
    /// and skip names.</summary>
    public Campaign Plan(Rules.Rules d, ModelManifest manifest)
    {
        D = d; Manifest = manifest;
        var players = Data.Players.Where(p => p != null).Select(p => p!).ToList();
        var human = players.FirstOrDefault(p => p["present"].Bool() && p["type"].Str() == "human") ?? players.FirstOrDefault(p => p["present"].Bool());
        HumanSlot = human != null ? human["id"].Int() : 0;
        MissionTribe = human != null ? human["tribe"].Str() : "";
        if (ChosenTribe != null && MissionTribe.Length > 0 && ChosenTribe != MissionTribe)
            Swap = new TribeSwap(D, MissionTribe, ChosenTribe, GivenClasses(), AsksFor(ChosenTribe), manifest, StartUnitClasses(human));
        HumanTribe = Swap != null ? ChosenTribe! : MissionTribe;
        Tribes = players.Where(p => p["present"].Bool() && p["tribe"].Str().Length > 0).Select(p => p == human ? HumanTribe : p["tribe"].Str()).Distinct().ToList();
        FullTribes = HumanTribe.Length > 0 ? new List<string> { HumanTribe } : new List<string>();
        // what the triggers name: classes (models listed, scenery made addressable) and objects
        var named = NamedByTriggers();
        _namedClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var w in named) if (RuleClass(w) != null || manifest.Has(w)) { _namedClasses.Add(w.ToLowerInvariant()); NeedClass(w); }
        // the effects the triggers play (EFCT): the models of the particle database
        var pdb = D.Extra.Particles as JsonObject;
        foreach (var t in Data.Triggers) foreach (var a in t.Actions)
        {
            if (a.Code != "EFCT" || pdb == null) continue;
            var fx = pdb[(a.P.GetValueOrDefault("effect_path") ?? "").Trim('/').ToLowerInvariant()] as JsonArray;
            if (fx != null && fx.Count > 0) NeedClass(fx[0].Str());
        }
        // what the triggers will create that no parameter names (actions.js prescan): the whole tribe of an AI player that
        // gets a building behaviour (AIBV Dodo / Giraffe / Schnecke / Turtle), the classes of the AIFT army tables
        var (preTribes, preClasses) = Prescan(Config.AiData);
        foreach (var t in preTribes) if (!FullTribes.Contains(t)) FullTribes.Add(t);
        foreach (var c in preClasses) NeedClass(c);
        var refs = Data.Refs;
        var refNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, r) in refs) if (r is JsonArray ra && ra.Count > 1 && ra[1].Str().Length > 0) refNames.Add(ra[1].Str());
        foreach (var on in Data.Objects)
        {
            if (on == null) continue;
            var o = on;
            var cls0 = o["class"].Str();
            var lc = cls0.ToLowerInvariant();
            var pl = new ObjectPlan { O = o };
            Plans.Add(pl);
            var rule = RuleClass(cls0); var type = o["type"].Str();
            int? owner = o["owner"] is JsonValue ? o["owner"].Int() : null;
            var attr = o["attr"];
            // the player's objects become the chosen tribe's (tribes.js); the type of an equivalent can differ (the
            // Dustriders' resource collector is an animal, the Norsemen's fireplace a building)
            if (rule != null && owner == HumanSlot && (UNIT_TYPES.Contains(type) || type == "BLDG"))
            {
                var to = OwnClass(rule);
                if (to != rule) { rule = to; type = D.Info(to)!.Type; pl.Swapped = true; }
            }
            var ix = rule != null ? D.Info(rule) : null;
            // the object is made by its class, whatever type the map stored for it: MIRAGE moved seas_carrier_fake from
            // the buildings to the ships (Objects/SEAS/SHIP, its ObjPath attribute in the map says so too) and its maps
            // kept the old type BLDG - mission 12's fleet carrier seas_carrier_fake_0 (shown by Gc00 after the film
            // sc_4030, one of the carriers Gcb04 waits for) and mission 13's four SEAS carriers stood as scenery that
            // could not be sunk. The ones the triggers spawn were ships already (the only such objects of both
            // configurations, CampaignTests.MapTypeFollowsClass)
            if (ix != null && type != ix.Type && FIGHTING_TYPES.Contains(type) && FIGHTING_TYPES.Contains(ix.Type)) { type = ix.Type; pl.Retyped = true; }
            var name = o["name"].Str();
            // scenery a mission addresses (by GUID, name or class), hides or gives to a player stands on its own
            bool addressed = refs.ContainsKey(o["guid"].Str()) || refNames.Contains(name) || named.Has(name) || _namedClasses.Contains(lc) || IsFalse(o["visible"]) || (owner != null && !RESOURCES.Contains(type));
            // (placed effects - CFX - show like scenery; a heal glow saved with a unit is not one: FightingObj.HealMe)
            if (HELPERS.Contains(type) || lc == "virtual_produce_unit" || (type == "CFXE" && lc == "fx_heal_passive")) { pl.Kind = "helper"; Skip.Add(name); continue; }
            if (PARTS.Contains(type)) { pl.Kind = "part"; Skip.Add(name); continue; }
            // the rider of a ridden animal saved as an object of its own (is_riding, transporter_guid: MIRAGE mission 7's
            // aje_velociraptor_handler_char_4 on aje_velociraptor_handler_4) is part of its mount, which shows it already;
            // alone it would stand with no hit points (its class is a 1-HP placeholder)
            if (rule != null && attr?["is_riding"].Str() == "1" && attr?["transporter_guid"].Str().Length > 0 && MountOf(rule) != null) { pl.Kind = "part"; Skip.Add(name); continue; }
            if (ix != null && UNIT_TYPES.Contains(ix.Type) && UNIT_TYPES.Contains(type))
            {
                pl.Kind = "unit"; pl.Cls = rule; Skip.Add(name);
                ClassModels(rule!);
                continue;
            }
            // (and the gates of nobody - mission 16's seas_hq_gate: a CGate opens and closes whoever owns it)
            if (ix != null && ix.Type == "BLDG" && type == "BLDG" && ((owner != null && owner >= 0 && owner < 8 && Data.Players[owner.Value]?["present"].Bool() == true) || (owner == null && D.IsA(rule!, "CGate"))))
            {
                pl.Kind = "building"; pl.Cls = rule; Skip.Add(name);
                ClassModels(rule!);
                continue;
            }
            if (type == "NEST" && ix != null)
            {
                // wild nests: the spawner is the map source's; the nest itself stands as an object that can be attacked
                // and destroyed (WildlifeReady) - missions wait for that with DEAD / DYIN
                pl.Kind = "nest"; pl.Cls = rule;
                var g = (D.Stats(rule!, 1, null)?.Gfx ?? "").ToLowerInvariant();
                if (g.Length > 0 && manifest.Has(g)) { Need.Use(g); if (manifest.Has(g + "_dest")) Need.Use(g + "_dest"); }
                continue;
            }
            if (LANDSCAPE.Contains(type) && !addressed) { pl.Kind = "landscape"; continue; }
            // trees, stones and fruit bushes a mission addresses (the tutorial highlights the bushes with OCPY) stay
            // resources of the map source; their record follows the resource node (Build: rec.Node). Hidden ones stay
            // scenery that can be shown later (OBAP).
            if (RESOURCES.Contains(type) && type != "NEST" && !IsFalse(o["visible"])) { pl.Kind = "resource"; continue; }
            // everything else is scenery of its own: needs a model
            Skip.Add(name);
            var model = ModelOf(o);
            if (model == null) { pl.Kind = "missing"; continue; }
            pl.Kind = "prop"; pl.Model = model;
            Need.Use(model);
        }
        // the chests of the items the item spawns make (systems/items.js)
        if (Data.Objects.Any(o => o?["type"].Str() == "ITSP")) Need.Use("item_treasure_chest");
        // start locations: main buildings and start armies
        foreach (var p in players)
        {
            if (!p["present"].Bool()) continue;
            var tribe = p == human ? HumanTribe : p["tribe"].Str();
            if (p["include_buildings"].Bool() && MAIN_BUILDING.TryGetValue(tribe, out var mb)) ClassModels(mb);
            if (p == human && Config.Army is { } army)
            {
                var pb = new PwRemake.Sim.Game.PointBuy(d, null);
                foreach (var c in army.Cards.Values) ClassModels(pb.Resolve(c, tribe).Cls);
                continue;
            }
            foreach (var a in p["start_army"]?.AsArray() ?? new JsonArray())
            {
                var c = p == human ? OwnClass(a!["class"].Str()) : a!["class"].Str();
                ClassModels(c);
                var r = RuleClass(c); var m = r != null ? MountOf(r) : null;
                if (m != null) ClassModels(m);
            }
        }
        Coop?.Plan(this);
        return this;
    }
    static bool IsFalse(JsonNode? n) => n is JsonValue v && v.TryGetValue(out bool b) && !b;

    static readonly Regex BUILDING_BEHAVIOUR = new("^(Dodo|Giraffe|Schnecke|Turtle|Singleplayer)", RegexOptions.CultureInvariant);
    /// <summary>Before the world is built: what the triggers will create that the class scan of the parameters does not
    /// see - whole tribes for AI players that get a building behaviour (AIBV), the classes of AIFT army tables
    /// (actions.js prescanActions)</summary>
    public (List<string> FullTribes, List<string> Classes) Prescan(JsonNode? aiData)
    {
        var full = new PwRemake.Sim.World.OrderedSet<string>(); var classes = new PwRemake.Sim.World.OrderedSet<string>();
        string? TribeOf(int slot) { var p = slot >= 0 && slot < Data.Players.Count ? Data.Players[slot] : null; return p != null && p["present"].Bool() ? p["tribe"].Str() : null; }
        int SlotOf(Dictionary<string, string> p, string k, int d) => p.TryGetValue(k, out var v) && v.Length > 0 ? (int)TrigUtil.ToInt(v) : d;
        foreach (var t in Data.Triggers)
            foreach (var a in t.Actions)
            {
                var p = Data.ActionParams(a);
                if (a.Code == "AIBV" || a.Code == "AIST")
                {
                    var tr = TribeOf(SlotOf(p, "player_id", 0));
                    var mod = p.GetValueOrDefault("module") ?? "";
                    if (tr != null && BUILDING_BEHAVIOUR.IsMatch(p.GetValueOrDefault("behavior") ?? "") && (mod.Length == 0 || mod == "CTRL")) full.Add(tr);
                }
                else if (a.Code == "AIFT")
                {
                    var tr = TribeOf(SlotOf(p, "player_id", 0));
                    if (tr == null) continue;
                    var T = aiData?["armies"]?[tr];
                    var at = p.GetValueOrDefault("attack_type") ?? "";
                    var army = (T?["Singleplayer"]?[at] ?? T?["Dodo"]?[at]) as JsonArray;
                    if (army != null)
                    {
                        foreach (var e in army) foreach (var alt in e?["alternatives"]?.AsArray() ?? new JsonArray()) if (alt?["cls"].Str() is { Length: > 0 } c) classes.Add(c);
                        if (TrigUtil.On(p.GetValueOrDefault("ship_land"))) full.Add(tr);
                    }
                    else full.Add(tr);
                }
            }
        return (full.ToList(), classes.ToList());
    }

    /// <summary>the class an object of the human player gets: with another tribe chosen, the equivalent of that tribe</summary>
    public string OwnClass(string cls) { var r = RuleClass(cls); return Swap != null && r != null ? Swap.Cls(r) : r ?? cls; }
    /// <summary>a tech tree filter path for player p (the player's filters translated to the chosen tribe; null = none)</summary>
    public string? FilterFor(Player? p, string path) => Swap != null && p == Human ? Swap.Filter(path) : path;

    // ------------------------------------------------------------------ build (from main.js buildWorld)
    /// <summary>W = the world, origin = the map source's origin (map -> game). Returns the human player.</summary>
    public Player? Build(GameWorld W, double[]? origin)
    {
        World = W;
        _ox = origin?[0] ?? 0; _oy = origin?[1] ?? 0;
        Regions = new Regions(Data.Regions, (mx, my) => ToGame(mx, my), (x, z) => ToMap(x, z));
        W.RegionOf = n => Regions.Find(n);                 // (named regions the buildings' behaviours use: the Scorpion's attacks)
        Objects = new Registry(this, Regions);
        // ---- players (triggers.md §6.7 no. 1, 2; ServerApp.usl:398-599)
        var list = new List<Player>();
        foreach (var pdn in Data.Players)
        {
            var pd = pdn!;
            if (!pd["present"].Bool()) continue;
            int id = pd["id"].Int();
            var c = pd["color"] is JsonValue && pd["color"].Int(-1) is int ci && ci >= 0 && ci < Colors.PLAYER_COLORS.Count ? Colors.PLAYER_COLORS[ci] : null;
            bool me = id == HumanSlot;
            bool coop = !me && Config.Humans != null && Config.Humans.Contains(id);     // (a co-op human took this ally over)
            var dip = pd["diplomacy"] is JsonArray da ? da.Select(x => x.Int(1)).ToArray() : null;
            var p = new Player(id, (me ? HumanTribe : pd["tribe"].Str()) ?? "", new PlayerOpts
            {
                Ai = !me && !coop, Name = pd["name"].Str() is { Length: > 0 } nm ? nm : "Player " + id, Team = pd["team"] is JsonValue ? pd["team"].Int() : null,
                Color = c != null ? Colors.Hex(c.Dark) : me ? Colors.NEUTRAL_UI_ME : 0x9a9a9a, PartyColor = c != null ? Colors.Hex(c.Light) : null, ColorSlot = c != null ? pd["color"].Int() : -1,
                Rules = D, Diplomacy = dip, Gameplay = Config.Gameplay,
            });
            p.Data = pd;
            // played with another tribe: buildings of the mission's tribe the player is given keep their own actions where
            // the chosen tribe has nothing of that role (the tavern for the SEAS, Rules.ActionsOf)
            if (me && Swap != null) p.ForeignActions = true;
            // army pyramid and population limit of the level (Restrictions/Chars; Player.usl:281, RequirementsMgr.usl:186)
            var ul = pd["unit_limits"] as JsonArray ?? new JsonArray();
            var lim = ul.Select((u, i) => u?["max"] is JsonValue mv ? mv.Num() : (i < D.Pyramid.Length ? D.Pyramid[i] : 0)).ToArray();
            if (lim.Length == 5 && lim.Where((v, i) => v != D.Pyramid[i]).Any()) p.Pyramid = lim;
            if (pd["population_limit"] is JsonValue pv && pv.Num() > 0) p.PopMax = pv.Num();
            Players[id] = p;
            list.Add(p);
        }
        Human = PlayerAt(HumanSlot);
        Coop?.AddPlayers(this, list);           // (co-op seats: new players, allies taken over - before the filters)
        W.Players = list;
        // debug option "no mission bans" (game/cheats.js): the level's AntiActions do not reach the human (SetFilter)
        var ch = Config.Cheats;
        if (Human != null && ch != null && ch.On && ch.Unlock) Human.UnlockAll = true;
        foreach (var p in list) p.Tt?.Suspend();
        // tech tree filters of the level: mostly AntiActions = what the mission forbids (ServerApp.usl:527-535)
        foreach (var p in list)
        {
            var fs = (p.Data?["tech_filters"] as JsonArray ?? new JsonArray()).Select(x => x.Str()).ToList();
            foreach (var g in Swap != null && p == Human ? Swap.Filters(fs) : fs) if (g.Length > 0) W.SetFilter(p, g, true);
        }
        // MIRAGE adds filters by level name (MirageSrvMgr.HardcodeTTFilters, for every player). As MIRAGE ships it, only its
        // first branch takes effect: the tutorial and missions 1 and 2 ban Larry and Tarna for every tribe and the tutorial
        // gets the Norse ladder; the checks for later missions (old_school_stats, tesla tower, roguemask ...) are nested
        // inside that branch and never run - followed here as the game plays.
        var key = Data.Entry.Key;
        if (D.Extra.Mod == "MIRAGE" && (key == "tutorial" || key == "single_01" || key == "single_02"))
        {
            var extra = new List<string>();
            foreach (var h in new[] { "Larry_s0", "Tarna_s0" }) foreach (var t in TribeSwap.TRIBES) extra.Add($"/Filters/AntiActions/{t}/Build/CHTR/{h}");
            if (key == "tutorial") extra.Add("/Filters/Hu/Build/BLDG/hu_ladder");
            foreach (var p in list) foreach (var g in extra) W.SetFilter(p, g, true);
        }
        // stand-ins for classes without a model
        foreach (var pl in Plans) if (pl.Cls != null) { var si = StandInModel(pl.Cls); if (si != null) W.StandIns[pl.Cls.ToLowerInvariant()] = si; }
        foreach (var w in _namedClasses) { var r = RuleClass(w); var si = r != null ? StandInModel(r) : null; if (si != null) W.StandIns[r!.ToLowerInvariant()] = si; }

        // ---- records for every object, in the order of the data
        W.OnEntity = EntityCreated;
        foreach (var pl in Plans)
        {
            var o = pl.O;
            int? owner = o["owner"] is JsonValue ? o["owner"].Int() : null;
            var rec = pl.Rec = new Rec(o["guid"].Str(), o["name"].Str(), pl.Swapped ? pl.Cls! : o["class"].Str(), pl.Swapped || (pl.Retyped && pl.Cls != null) ? D.Info(pl.Cls!)!.Type : o["type"].Str(),
                o["index"] is JsonValue ? o["index"].Int() : -1, o["handle"] is JsonArray ha && ha.Count >= 2 ? new[] { ha[0].Int(), ha[1].Int() } : null,
                owner ?? -1, o["x"].Num(), o["y"].Num(), o);
            rec.Placed = false;
            if (pl.Kind == "landscape" || pl.Kind == "part") { if (pl.Kind == "part") Counts.Parts++; else Counts.Landscape++; continue; }     // not addressable: no entry in the tables
            Objects.Add(rec);
            if (pl.Kind == "helper") Counts.Helpers++;
        }
        foreach (var gn in Data.Groups)
        {
            var g = gn!;
            var rec = Objects.ByGuid(g["guid"].Str()) ?? Objects.Add(new Rec(g["guid"].Str(), g["name"].Str(), "GroupObject", "GROU", g["index"] is JsonValue ? g["index"].Int() : -1));
            rec.Placed = true;
            rec.GroupObj = new Group(rec);
            Groups.Add(rec.GroupObj);
        }
        // ---- buildings first (units then find free ground), then units, then scenery
        foreach (var pl in Plans) if (pl.Kind == "building") PlaceBuilding(pl);
        foreach (var pl in Plans) if (pl.Kind == "unit") PlaceUnit(pl);
        foreach (var pl in Plans) if (pl.Kind == "prop") PlaceProp(pl);
        foreach (var pl in Plans)
        {
            var o = pl.O; var rec = pl.Rec!;
            var type = o["type"].Str();
            if (pl.Kind == "missing") { Counts.Missing++; Miss(o["class"].Str(), "no model"); }
            else if (pl.Kind == "nest") rec.Placed = true;       // rec.Nest: linked in WildlifeReady()
            else if (pl.Kind == "resource")
            {
                var (x, z) = ToGame(o["x"].Num(), o["y"].Num());
                rec.Node = W.Resources.FirstOrDefault(n => n.Alive && Math.Abs(n.Pos.X - x) < 1.5 && Math.Abs(n.Pos.Z - z) < 1.5);
                rec.Placed = rec.Node != null;
                if (rec.Node != null) _resourceRecs.Add(rec); else Miss(o["class"].Str(), "no resource of the map");
            }
            else if (pl.Kind == "prop" && type == "FNTN" && rec.Prop != null) W.AddWell(rec.Prop, AttrMap(o), o["class"].Str());     // healing wells
            else if (pl.Kind == "prop" && type == "ITEM" && rec.Prop != null) W.PlaceItem(o["class"].Str(), rec.Prop.Pos.X, rec.Prop.Pos.Z, rec.Prop.Rot, rec.Prop);   // items
            else if (pl.Kind == "helper" && type == "DMGL")
            {
                // lakes of healing / damage (systems/landmarks.js): a region of the map
                var a = o["attr"];
                W.AddLake(new MapLake { Name = o["name"].Str(), Region = a?["region_name"].Str() ?? "", Heal = D.IsA(o["class"].Str(), "CLakeOfHealing"), Amount = a?["damage_per_tick"].Num() ?? 0, Types = a?["damage_types"].Str() ?? "" });
            }
            else if (pl.Kind == "helper" && type == "OTHR" && o["class"].Str().StartsWith("showfow", StringComparison.OrdinalIgnoreCase) && o["owner"] is JsonValue && PlayerAt(o["owner"].Int()) is Player op)
            {
                // a placed ShowFOW_Obj: its owner sees the place (the radius is not in the data: 20 m, a guess)
                var (x, z) = ToGame(o["x"].Num(), o["y"].Num());
                rec.Reveal = W.RevealArea(op, x, z, 20, 0);
            }
        }
        // the items the level's item spawns make (CItemMgr.GenerateItems; the level's Root/Items: the map source's ItemPool)
        // (as setup.js: the generated items get no record - no script names them)
        var spawns = Plans.Where(pl => pl.O["type"].Str() == "ITSP").Select(pl =>
        {
            var o = pl.O; var a = o["attr"]; var (x, z) = ToGame(o["x"].Num(), o["y"].Num());
            bool Yes(string k) { var v = a?[k].Str(); return v == "1" || v == "true"; }
            return new ItemSpawn { X = x, Z = z, Rot = Heading(o["rot"].Num()), Items = (a?["spawn_items"].Str() ?? "").Split('|').Select(c => c.Trim()).Where(c => c.Length > 0).ToList(), Always = Yes("generate_always"), Random = Yes("random_item") };
        }).ToList();
        if (spawns.Count > 0 && ItemPool != null) W.GenerateItems(spawns, ItemPool.Pool, (int)ItemPool.Max);
        // passengers that start inside a transport (attributes transporter_guid / passenger_guids)
        foreach (var pl in Plans)
        {
            var a = pl.O["attr"];
            if (pl.Kind != "unit" || pl.Rec?.Entity == null) continue;
            if (a?["transporter_guid"].Str() is { Length: > 0 } tg) Embark(pl.Rec, Objects.ByGuid(tg));
            if (a?["passenger_guids"].Str() is { Length: > 0 } pg) foreach (var g in pg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)) Embark(Objects.ByGuid(g), pl.Rec);
        }
        // ---- groups, question marks
        foreach (var gn in Data.Groups)
        {
            var grp = Objects.GroupOf(gn!["guid"].Str());
            foreach (var m in gn["members"]?.AsArray() ?? new JsonArray()) { var r = Objects.ByGuid(m.Str()); if (r != null && r.Placed) Objects.AddToGroup(grp, r); }
        }
        foreach (var qn in Data.QuestionMarks)
        {
            var q = qn!;
            var (x, z) = ToGame(q["x"].Num(), q["y"].Num());
            QuestionMarks.Add(new QuestionMark { Guid = q["guid"].Str(), Name = q["name"].Str(), X = x, Z = z, State = q["state"].Str("STATE_INVISIBLE") is { Length: > 0 } st ? st : "STATE_INVISIBLE", Rec = Objects.ByGuid(q["guid"].Str()) });
        }
        // ---- start locations: main building, start army, start resources (StartLocation.usl:316-737)
        foreach (var p in list) SetupStartLocation(p);
        Coop?.SetupStarts(this);
        foreach (var p in list) { p.Tt?.Resume(); W.RecomputeCapsOf(p); }
        // the Dustriders' mobile base the player starts with (the map's, the start location's) is not driven in from
        // wherever it was put: it waits, and a worker puts it down where the player wants - at once and for free (the
        // user's choice; PlaceFreeMain)
        ParkFreeMains();
        foreach (var (cls, (n, why)) in _missing) Warn($"{cls}{(n > 1 ? " ×" + n : "")}: {why}");
        foreach (var w in Data.Warnings) if (Regex.IsMatch(w.Str(), @"^player \d+: start location")) Warn("data: " + w.Str());
        Ready = true;
        W.Campaign = this;
        return Human;
    }
    /// <summary>the map source's item pool (the level's Root/Items), set by the world builder</summary>
    public ItemPool? ItemPool;
    static Dictionary<string, string> AttrMap(JsonNode o)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        if (o["attr"] is JsonObject a) foreach (var (k, v) in a) d[k] = v.Str();
        return d;
    }
    /// <summary>an item the world made (systems/items.js): an object of the mission - the triggers find it by class
    /// (GiveItem, ITEM)</summary>
    public Rec AdoptItem(string cls, Prop prop)
    {
        if (_propRecs.TryGetValue(prop, out var have)) return have;
        var rec = new Rec(null, Objects.NewName(cls), cls, "ITEM") { Prop = prop };
        _propRecs[prop] = rec;
        return Objects.Add(rec);
    }
    /// <summary>the record of a prop (scenery, items), or null</summary>
    public Rec? RecOfProp(Prop p) => _propRecs.TryGetValue(p, out var r) ? r : null;

    /// <summary>the human's mobile bases (at the start: the map's, the start location's) wait for a worker. A mission made
    /// for the Dustriders but played by another tribe: the main building that stands in for the collector (the start
    /// location's, a map's collector converted) waits the same way.</summary>
    public void ParkFreeMains()
    {
        var W = World; var p = Human;
        if (p == null) return;
        void Park(Entity? e) { if (e != null && e.Alive && e.Owner == p && !e.Parked && !p.FreeMains.Contains(e)) { W.SetParked(e, true); p.FreeMains.Add(e); } }
        foreach (var u in W.Units.ToList()) if (u.Name == "aje_resource_collector") Park(u);
        if (MissionTribe != "Aje") return;
        foreach (var e in W.Units.Cast<Entity>().Concat(W.Buildings).ToList()) if (e.StartMain) Park(e);
        foreach (var pl in Plans) if (pl.Swapped && pl.Rec != null && RuleClass(pl.O["class"].Str()) == "aje_resource_collector") Park(pl.Rec.Entity);
    }
    /// <summary>put down a waiting main (FreeMains) at (x, z), turned rot: true when the ground there is free. A building
    /// is made anew there, ready (its record - a map object's - moves to it); a unit is moved.</summary>
    public bool PlaceFreeMain(Entity u, double x, double z, double rot = 0)
    {
        var W = World; var p = Human;
        if (u == null || p == null || !p.FreeMains.Contains(u) || !u.Alive || !FreeMainOk(u, x, z, rot)) return false;
        p.FreeMains.Remove(u);
        if (u is Building ub)
        {
            var rec = ub.Rec;
            ub.Rec = null;
            var at = W.PlacementOf(ub.Name, x, z, rot, p);
            W.RemoveEntity(ub);
            _pending = rec;
            var b = W.PlaceAdapted(ub.Name, p, at.X, at.Z, at.Rot, true);
            _pending = null;
            if (b != null && rec != null) { b.Rec = rec; rec.Entity = b; }
            Engine?.Touch();
            return b != null;
        }
        W.Teleport(u, x, z, rot);
        W.SetParked(u, false);
        Engine?.Touch();
        return true;
    }
    /// <summary>may it stand there: walkable land the player has seen, free of anything else</summary>
    public bool FreeMainOk(Entity u, double x, double z, double rot = 0)
    {
        var W = World; var nav = W.Nav; double r = (u.Radius != 0 ? u.Radius : 4) + 1;
        if ((W.FowOf(u.Owner) ?? W.Fow) is { } fw && !fw.Explored(x, z)) return false;
        if (u is Building) return W.PlacementOf(u.Name, x, z, rot, Human).Ok;   // (the building rules)
        for (double dz = -r; dz <= r; dz += 1) for (double dx = -r; dx <= r; dx += 1)
        {
            if (dx * dx + dz * dz > r * r) continue;
            if (!nav.IsFree(x + dx, z + dz)) return false;
        }
        bool busy = false;
        W.UHash.Query(x, z, r + 6, o => { if (!busy && o.Alive && o != u && !o.Parked && Math.Sqrt((o.Pos.X - x) * (o.Pos.X - x) + (o.Pos.Z - z) * (o.Pos.Z - z)) < r + (o.Radius != 0 ? o.Radius : 1)) busy = true; });
        return !busy;
    }

    /// <summary>the nests of the map the world builder made (WorldBuilder): (the map's nest, the world's nest)</summary>
    public readonly List<(MapNest Map, WildNest Nest)> MapNests = new();
    /// <summary>the map's wild animals and nests exist now (the world builder creates them after the players): nests get
    /// their record, and the nest itself becomes an ownerless object with hit points (Nest.usl: 500): destroying it ends
    /// the spawning, and DEAD / DYIN conditions on it fire. Nobody attacks a nest without an order (combat findTarget).</summary>
    public void WildlifeReady()
    {
        var W = World;
        foreach (var pl in Plans)
        {
            if (pl.Kind != "nest") continue;
            var o = pl.O; var rec = pl.Rec!;
            var (x, z) = ToGame(o["x"].Num(), o["y"].Num());
            rec.Nest = MapNests.FirstOrDefault(n => Math.Sqrt((n.Map.X - x) * (n.Map.X - x) + (n.Map.Z - z) * (n.Map.Z - z)) < 1).Nest;
            var gfx = D.Stats(pl.Cls!, 1, null)?.Gfx;
            if (gfx == null || !Manifest.Has(gfx)) continue;
            Building? b = null;
            _pending = rec;
            try { b = W.PlaceBuilding(pl.Cls!, null, x, z, Heading(o["rot"].Num()), true); } catch (Exception e) { Warn($"{o["class"].Str()}: nest not created ({e.Message})"); }
            _pending = null;
            if (b == null) continue;
            b.IsNest = true;
            ApplyState(pl, b);
        }
    }
    /// <summary>the world created a unit or building: give it a record (World.OnEntity)</summary>
    void EntityCreated(Entity e)
    {
        var rec = _pending;
        _pending = null;
        if (rec != null) { rec.Entity = e; e.Rec = rec; rec.Placed = true; if (!rec.Listed) Objects.Add(rec); }
        else rec = Objects.Adopt(e);
        Spawned?.Invoke(rec);
        Engine?.OnObjectSpawned(rec.Id);
    }
    /// <summary>hit points and flags of a placed object</summary>
    void ApplyState(ObjectPlan pl, Entity e)
    {
        var o = pl.O; var rec = pl.Rec!;
        double mhp = o["max_hp"].Num(), hp = o["hp"].Num();
        if (mhp > 0 && hp > 0 && hp < mhp) { e.Hp = Math.Max(1, Math.Floor(e.MaxHp * hp / mhp + 0.5)); if (e is Building b) b.UpdateVisual(); }
        // the flag bits are decoded by correlation only (CAMPAIGN_FORMAT.md §6): `visible` is certain, the others hints.
        // `invulnerable` (bit 31) is applied only to the objects of players allied with the human at the start (the
        // browser remake applies it to none): it is also set on trees, question marks, groups, the human's hero and mission
        // 1's hostile prison guard hu_warrior_68, who must be killed - but without it mission 1's captured Norse spearmen
        // (allied, unarmed) are killed by their guards while the barbarians and the Norsemen are at war (the arrival in
        // the first village until the dialogue at its gate), and "all spearmen dead" loses the mission; the mission's own
        // triggers switch the allies' invulnerability on and off where they are meant to fight (the dilo area, Stina's
        // fight). It ends when such an object comes to the human (Occupy) or a trigger sets it (MissionHost.SetInvulnerable).
        // Allied at the start: mission 1's Norsemen, one character and animal of mission 10, the B42 SEAS missions' allies.
        if (o["invulnerable"] is JsonValue iv && iv.TryGetValue(out bool inv) && inv && e.Owner != null && Human != null
            && e.Owner != Human && Human.IsFriend(e.Owner))
        {
            World.SetInvulnerable(e, true);
            rec.MapInvuln = true;
        }
        int flags = (IsFalse(o["visible"]) ? 0 : 1) | (IsFalse(o["hitable"]) ? 0 : 2) | (IsFalse(o["selectable"]) ? 0 : 4);
        if ((flags & 5) == 1 && ShownSelectable(e)) flags |= 4;
        if ((flags & 1) != 0 && IsMortal(rec)) { flags |= 6; World.SetInvulnerable(e, false); rec.MapInvuln = false; }
        if (flags != 7) SetAppearance(rec, flags);
    }
    /// <summary>remake (Kacper, three times: "make that front tower targetable and destructible"): objects a mission keeps
    /// unclickable and invulnerable that can be clicked, attacked and destroyed from the start - in single player, in co-op
    /// (with the co-op definition's `mortal` names) and with any tribe (a faction swap places the swapped class under the
    /// same record). A trigger's ObjAppear / Invulnerability cannot lock them again (MissionHost.SetAppearance /
    /// SetInvulnerable). MIRAGE 13: the Dragon Clan's tower before the gate (ninigi_small_tower_121, the map's flags not
    /// hitable / not selectable, the init trigger's "set inver", freed only after dialogue 4071).</summary>
    static readonly (string Mod, int Mission, string Name)[] MORTAL = { ("MIRAGE", 13, "ninigi_small_tower_121") };
    public bool IsMortal(Rec? r) => r != null &&
        (MORTAL.Any(m => m.Mission == Id && string.Equals(m.Mod, D.Extra.Mod, StringComparison.OrdinalIgnoreCase) && Objects.Get(m.Name) == r)
         || Coop?.Def.Mortal.Any(n => Objects.Get(n) == r) == true);
    /// <summary>objects whose map clears the selectable bit that can still be clicked and hovered (the info window, the
    /// tooltip), as in the original: the temple guards' tents of mission 3 (MIRAGE's map: aje_tent_44..46 of the player
    /// _PN_SP03_GUARDS; base mission 3's guards have none). They stay as the map has them otherwise - not hitable (the
    /// player cannot attack them), not the player's (no commands); only the selectable bit (OBAP 4) is set, so a
    /// trigger's ObjAppear still decides from then on (CAMPAIGN_FORMAT.md §6: the bits are decoded by correlation).</summary>
    static readonly (string Player, string Class)[] SELECTABLE_ANYWAY = { ("_PN_SP03_GUARDS", "tent") };
    static bool ShownSelectable(Entity e) =>
        e is Building && e.Owner?.Data?["name_key"] is JsonValue nk && nk.TryGetValue(out string? key) &&
        SELECTABLE_ANYWAY.Any(s => s.Player == key && e.Name.Contains(s.Class, StringComparison.Ordinal));
    void PlaceBuilding(ObjectPlan pl)
    {
        var o = pl.O; var W = World; var a = o["attr"];
        var (x, z) = ToGame(o["x"].Num(), o["y"].Num());
        _pending = pl.Rec;
        // building_ready "0" = a construction site (the zeppelin wreck of mission 4). Without the attribute and with 0 hit
        // points the building was never put up (the launch ramp of mission 9, "Order your workers to build the ramp!")
        var br = a?["building_ready"]; var hpa = a?["hitpoints"];
        bool ready = br is JsonValue ? br.Str() != "0" : hpa is not JsonValue || hpa.Num() > 0;
        var owner = o["owner"] is JsonValue ? PlayerAt(o["owner"].Int()) : null;
        bool same = string.Equals(pl.Cls, o["class"].Str(), StringComparison.OrdinalIgnoreCase);
        // (walls, gates, towers and traps of every tribe share the 8 m wall grid: the equivalent stands in the same place)
        var at = same || OnWallGrid(pl.Cls!, owner) ? (x, z, Heading(o["rot"].Num())) : Fit(pl.Cls!, owner, x, z, Heading(o["rot"].Num()));
        var b = W.PlaceBuilding(pl.Cls!, owner, at.Item1, at.Item2, at.Item3, ready);
        _pending = null;
        if (b == null) { Counts.Missing++; Miss(o["class"].Str(), "no model"); return; }
        if (b.Def!.Gate && a?["GateState"] is JsonValue gs) W.SetGate(b, gs.Int());     // 0 open, 1 closed, 2 automatic
        // CLaunchPad.OnPostLoad: the Dragon Clan's telescope tower ruins (mission 12) are sites already half built
        if (!ready && o["class"].Str().ToLowerInvariant() == "ninigi_telescope_tower_ruins") W.AddProgress(b, 0.49);
        Counts.Buildings++;
        ApplyState(pl, b);
    }
    bool OnWallGrid(string cls, Player? owner) => D.Def(cls, owner)?.WallKind != null;
    /// <summary>a building of another tribe's class (tribes.js) can be bigger than the one the map was made for, and
    /// harbours snap to the coast: the nearest place where it fits (else where it was)</summary>
    (double, double, double) Fit(string cls, Player? owner, double x, double z, double rot)
    {
        var W = World;
        for (int k = 0; k < 160; k++)
        {
            double r = k != 0 ? 1 + k * 0.25 : 0, a = k * 2.4;
            var pl = W.PlacementOf(cls, x + SMath.Cos(a) * r, z + SMath.Sin(a) * r, rot, owner);
            if (pl.Ok && pl.Existing == null) return (pl.X, pl.Z, pl.Rot);
        }
        return (x, z, rot);
    }
    void PlaceUnit(ObjectPlan pl)
    {
        var o = pl.O; var W = World;
        var (x, z) = ToGame(o["x"].Num(), o["y"].Num());
        var owner = o["owner"] is JsonValue ? PlayerAt(o["owner"].Int()) : null;
        _pending = pl.Rec;
        var u = W.Spawn(pl.Cls!, owner, x, z, new SpawnOpts { Level = Math.Max(o["level"].Int() + 1, D.MinLevel(pl.Cls!, owner)), Heading = Heading(o["rot"].Num()), Unit = new UnitOpts { Alt = o["z"].Num() } }) as Unit;   // (alt: the zeppelin's height)
        _pending = null;
        if (u == null) { Counts.Missing++; Miss(o["class"].Str(), ManifestHas(pl.Cls!) ? "not created" : "no model"); return; }
        if (W.StandIns.TryGetValue(pl.Cls!.ToLowerInvariant(), out var si) && u.Gfx == si) Miss(o["class"].Str(), "no model, shown as " + u.Gfx);
        u.HomeX = u.Pos.X; u.HomeZ = u.Pos.Z;
        Counts.Units++;
        ApplyState(pl, u);
    }
    bool ManifestHas(string cls) { var s = D.Stats(cls, 1, null); return s?.Gfx != null && Manifest.Has(s.Gfx); }
    void PlaceProp(ObjectPlan pl)
    {
        var o = pl.O; var W = World;
        if (!Manifest.Has(pl.Model)) { Counts.Missing++; Miss(o["class"].Str(), "model not loaded"); return; }
        var (x, z) = ToGame(o["x"].Num(), o["y"].Num());
        var lc = pl.Model!;
        var type = o["type"].Str();
        bool landscape = LANDSCAPE.Contains(type);
        bool block = !NO_BLOCK.IsMatch(lc) && (type == "DCCO" || type == "BLDG" || type == "FNTN" || SOLID.IsMatch(lc));
        var owner = o["owner"] is JsonValue ? PlayerAt(o["owner"].Int()) : null;
        // scenery keeps its stored height; ship models (wrecks, moored boats, mines: model type "Ship") are stored at the
        // sea bed and float at the water level, as the game puts them when it loads the map
        double? y = landscape || type == "BLDG" || type.Length == 0 ? o["z"].Num() : null;
        if (Manifest.Fourcc(pl.Model) == "Ship" && W.WaterLevel != null && (y ?? W.Height(x, z)) < W.WaterLevel) y = W.WaterLevel;
        var q = o["q"] is JsonArray qa && qa.Count == 4 ? qa.Select(v => v.Num()).ToArray() : null;
        var prop = W.AddProp(new PropSpec { Model = pl.Model!, X = x, Z = z, Y = y, Rot = Heading(o["rot"].Num()), Q = q, Block = block, BlockRadius = block && !Bridges.IsBridge(lc) ? Math.Min(24, ModelRadius(pl.Model!) * 0.7) : 0, Party = owner?.PartyColor });
        _propRecs[prop] = pl.Rec!;
        pl.Rec!.Prop = prop; pl.Rec.Placed = true;
        Props.Add(prop);
        Counts.Props++;
        if (IsFalse(o["visible"])) { pl.Rec.Visible = false; prop.SetVisible(false); }
    }
    double ModelRadius(string model)
    {
        var b = Manifest.Box(model);
        if (b == null) return 0;
        var (mn, mx) = b.Value;
        return Math.Max(mx.X - mn.X, mx.Z - mn.Z) / 2;
    }
    void Embark(Rec? passenger, Rec? transport)
    {
        var u = passenger?.Entity as Unit; var t = transport?.Entity as Unit;
        if (u == null || t == null || u.Inside || u == t || !u.Alive || !t.Alive) return;
        if (u.Parked) return;
        World.EnterTransport(u, t);
    }
    /// <summary>the transporter a passenger sits in (JS u.inside), or null</summary>
    public Unit? TransportOf(Unit u)
    {
        return u.Inside ? u.InsideOf as Unit : null;
    }

    /// <summary>StartLocation.usl CreateLocation: the tribe's main building (IncludeBuildings), the start army of the level's
    /// point-buy preset (unless the start location says ignore_pointbuy) and the start resources</summary>
    void SetupStartLocation(Player p)
    {
        var W = World; var pd = p.Data!; var sl = pd["start_location"];
        if (sl is not JsonObject) return;
        var (x, z) = ToGame(sl["x"].Num(), sl["y"].Num());
        double heading = Heading(sl["rot"].Num());
        StartLocations[p.Id] = new StartLocation { X = x, Z = z, Heading = heading, Data = sl };
        if (sl["is_sequence"].Bool() && sl["seq_filename"].Str().Length > 0) return;    // SL:337-347: the sequence creates the start instead
        double stand = 3;
        if (pd["include_buildings"].Bool() && MAIN_BUILDING.TryGetValue(p.Tribe, out var cls))
        {
            // Aje: the resource collector only for a computer player (or without an army budget), SL:386-390 - a human
            // buys it with the army. A mission made for another tribe has no collector in its army: then it stands here.
            bool bought = p.Tribe == "Aje" && !p.Ai && !(Swap != null && p == Human);
            var e = bought ? null : W.Spawn(cls, p, x, z, new SpawnOpts { Heading = heading });
            if (e != null && p == Human) e.StartMain = true;            // (ParkFreeMains)
            if (e != null) stand = e.Radius;
            else if (!bought) Miss(cls, "start building not created");
        }
        if (sl["ignore_pointbuy"].Bool()) return;
        // the army screen's army (SL:482-623 with the result the menu wrote, GIW:430-431)
        if (p == Human && Config.Army is { } army)
        {
            new PwRemake.Sim.Game.PointBuy(D, null).Place(W, p, army, x, z, 0.99 * stand + 1.5, heading);
            p.Res["skulls"] = Math.Max(0, pd["resources"]?["iron"].Num() ?? 0);
            return;
        }
        int i = 0;
        foreach (var an in pd["start_army"]?.AsArray() ?? new JsonArray())
        {
            var a = an!;
            // SL:545-556: workers stand in three groups by caste around the start, heroes and soldiers anywhere around it
            var caste = a["caste"].Str();
            double ang = caste == "tec" ? 2.094 : caste == "nat" || a["preset"].Str() == "Stina_s0" ? 4.189 : caste == "res" ? 0 : (i * 2.399) % 6.283;
            double r = 0.99 * stand + 1.5 + (i % 3) * 1.2;
            double gx = x + SMath.Cos(ang + 0.785) * r, gz = z - SMath.Sin(ang + 0.785) * r;
            var rule = RuleClass(p == Human ? OwnClass(a["class"].Str()) : a["class"].Str());
            // an army slot that names a rider (MIRAGE mission 7: aje_velociraptor_handler_char - the pyramid shows the
            // rider) is the animal it rides; on its own the rider has no hit points (a 1-HP placeholder: NaN / 0)
            if (rule != null) rule = MountOf(rule) ?? rule;
            var u = rule != null ? W.Spawn(rule, p, gx, gz, new SpawnOpts { Level = Math.Max(a["level"].Int() + 1, D.MinLevel(rule, p)), Heading = heading }) : null;
            if (u == null) Miss(a["class"].Str(), "start army unit not created");
            i++;
        }
        // SL:604-623. -1 = "bought on the army screen" (missions 4, 5, 9, 11, 16): the tribe's default stock instead (a guess)
        var res = pd["resources"]; var def = D.StartOf(p.Tribe)?.Res;
        foreach (var (k, rr) in new[] { ("food", "food"), ("wood", "wood"), ("stone", "stone"), ("iron", "skulls") })
        {
            var v = res?[k];
            p.Res[rr] = v is not JsonValue ? 0 : v.Num() < 0 ? (def != null && def.TryGetValue(rr, out var dv) ? dv : 0) : v.Num();
        }
    }

    // ------------------------------------------------------------------ per simulation step
    /// <summary>the world's events of the step to the registry and the trigger engine, then the engine (main.js
    /// simulate -> G.campaign.update; docs/CAMPAIGN_RUNTIME.md §11)</summary>
    public void Update(double dt)
    {
        var W = World; var ev = W.Events;
        var P = W.Profile; P?.Sub();
        int _evAt = (int)Math.Clamp(_evAbs - W.EventBase, -1, int.MaxValue);
        if (_evAt < 0 || ev.Count < _evAt) _evAt = 0;
        for (; _evAt < ev.Count; _evAt++, _evAbs = W.EventBase + _evAt)
        {
            var e = ev[_evAt];
            WorldEventSeen?.Invoke(e);
            ForwardToEngine(e);
            switch (e.Type)
            {
                case "died": case "destroyed": case "removed":
                {
                    var ent = e.E("entity");
                    var rec = ent?.Rec;
                    if (rec == null || rec.Entity != ent) break;          // converted meanwhile (Convert): the record lives on
                    // a destroyed nest spawns no more animals
                    if (rec.Nest != null) { rec.Nest.Amount = 0; W.WildNests.Remove(rec.Nest); }
                    Objects.Drop(rec);
                    break;
                }
                case "owner": { var ent = e.E("entity");
                    // the map's invulnerability of an ally ends when it comes to the human (ApplyState)
                    if (ent?.Rec is { MapInvuln: true } mr && ent.Owner == Human) { mr.MapInvuln = false; W.SetInvulnerable(ent, false); }
                    if (ent?.Rec != null) { OwnerChanged?.Invoke(ent.Rec, e.P("from"), e.P("to")); Engine?.OnOwnerChanged(ent.Rec.Id); } break; }
                case "diplomacy": DiplomacyChanged?.Invoke(e); Engine?.OnDiplomacyChanged(); break;
            }
        }
        _evAbs = W.EventBase + _evAt;
        // a used up resource the triggers address is gone (DEAD)
        if (_resourceRecs.Count > 0 && _resourceRecs.Any(r => !r.Node!.Alive))
        {
            foreach (var r in _resourceRecs.ToList()) if (!r.Node!.Alive) Objects.Drop(r);
            _resourceRecs.RemoveAll(r => !r.Node!.Alive);
        }
        Objects.Compact();
        if (Props.Any(p => !p.Alive)) Props.RemoveAll(p => !p.Alive);
        // the host's own work of the step (waypoint patrols, effects, highlights), then the triggers: after the events of
        // this step have reached the conditions
        P?.SubMark("campaign:events");
        Coop?.Tick(dt);
        Host?.Tick(dt);
        P?.SubMark("campaign:host");
        Engine?.Update(dt);
        P?.SubMark("campaign:triggers");
    }
    // engine.js worldEvent: the world's events the conditions hear
    void ForwardToEngine(WorldEvent e)
    {
        var E = Engine;
        if (E == null) return;
        switch (e.Type)
        {
            case "attacked": { var v = e.E("entity")?.Rec; var a = (e.E("by") ?? e.E("attacker"))?.Rec; if (v != null && a != null) E.OnAttacked(v.Id, a.Id); break; }
            case "built": { var b = (e.E("building") ?? e.E("entity"))?.Rec; E.OnBuilt(b?.Id ?? -1); break; }
            case "boarded": { var u = e.E("unit")?.Rec; var t = e.E("transport")?.Rec; if (u != null && t != null) E.OnBoarded(u.Id, t.Id); break; }
            case "died": case "destroyed": { var ent = e.E("entity"); if (ent?.Rec != null) E.OnObjectLost(ent.Rec.Id, ent.Owner?.Id ?? -1); break; }
            // an item taken (systems/items.js): the ITEM conditions of the taker's record
            case "itemTaken": { var u = e.E("unit")?.Rec; var cls = e.Str("cls") ?? e.Str("item"); if (u != null && cls != null) E.OnItemTaken(cls, u.Id); break; }
        }
    }
    /// <summary>a record left the registry (Registry.Drop)</summary>
    internal void OnRemoved(Rec rec) { Removed?.Invoke(rec); Engine?.OnObjectRemoved(rec.Id); }
    internal void OnGroup(Group g, Rec rec, bool added) { GroupChanged?.Invoke(g, rec, added); Engine?.OnGroupChanged(g.Rec.Id, rec.Id, added); }

    // ------------------------------------------------------------------ what missions do (map coordinates, slots, 0-based levels)
    /// <summary>create an object of class `cls` for player slot `slot` (-1 = nobody) at map position (mx, my): a unit or
    /// building of the rules, else a piece of scenery with that model. level: 1-based (null = the class's lowest), rot:
    /// map heading, group: the group to join, exact: do not look for a free spot, y: the height of scenery. Returns its
    /// record or null.</summary>
    public Rec? Spawn(string cls, int slot, double mx, double my, int? level = null, double rot = 0, string? name = null, Group? group = null, bool exact = false, bool built = true, double? y = null)
    {
        var W = World;
        var (x, z) = ToGame(mx, my);
        var owner = PlayerAt(slot);
        var rule = RuleClass(owner != null && owner == Human ? OwnClass(cls) : cls);
        Rec? rec;
        if (rule != null)
        {
            var ix = D.Info(rule)!;
            // (an amphibious transport - the hovercraft - may stand on the water: mission 7's hard Wehrspinne convoy was put
            // on the shore 58 m from its place, outside the region its waypoint order looks in, and never set off)
            if (ix.Type != "BLDG" && !exact) (x, z) = Unit.AMPHIBIOUS.IsMatch(rule) ? W.FreeSpot(x, z, amphib: true) : W.FreeSpot(x, z, water: ix.Type == "SHIP");
            if (ix.Type == "BLDG" && owner == null) rec = SpawnProp(rule, slot, x, z, rot, name, y);
            else
            {
                var pre = new Rec(null, name ?? Objects.NewName(cls), rule, ix.Type == "BLDG" ? "BLDG" : ix.Type);
                _pending = pre;
                var e = W.Spawn(rule, owner, x, z, new SpawnOpts { Level = level != null ? Math.Max(level.Value, D.MinLevel(rule, owner)) : null, Heading = Heading(rot), Built = built });
                _pending = null;
                if (e == null) { Warn($"{cls}: could not be created (model not loaded?)"); return null; }
                rec = pre;
                if (e is Unit u) { u.HomeX = u.Pos.X; u.HomeZ = u.Pos.Z; }
            }
        }
        // an item (CItem, the tech tree's Items/Objects): a chest on the ground that a unit picks up - not a decoration.
        // A quest's reward made by CreateObj (MIRAGE / base mission 13: the zeppelin trader's item_pannier, "the treasure
        // I get for the side quest with the zeppelin is unclickable"; missions 2, 4, 7 - 10, 12, 15, 22, 43 too) was
        // a scenery prop: no item, nothing to click or pick up
        else if (ItemModelOf(cls) != null) rec = SpawnItem(cls, slot, x, z, rot, name);
        else rec = SpawnProp(cls, slot, x, z, rot, name, y);
        if (rec != null && group != null) Objects.AddToGroup(group, rec);
        return rec;
    }
    /// <summary>the model of an item class (CItem: the tech tree's Items/Objects; its class gfx or the treasure chest),
    /// null when the class is no item</summary>
    public string? ItemModelOf(string cls)
    {
        if (D.ItemNode(cls) == null && D.Script(cls) != "CItem") return null;
        var lc = cls.ToLowerInvariant();
        foreach (var m in new[] { D.ClassGfx(lc)?.ToLowerInvariant(), "item_treasure_chest" }) if (m != null && Manifest.Has(m)) return m;
        return null;
    }
    /// <summary>an item made by a trigger (CreateObj): an item of the world (World.PlaceItem) lying on the ground, an
    /// object of the mission of type ITEM like the map's (the triggers' ITEM, GiveItem find it); the owner is ignored -
    /// whoever picks it up carries it (CItem)</summary>
    Rec? SpawnItem(string cls, int slot, double x, double z, double rot, string? name)
    {
        var W = World;
        var model = ItemModelOf(cls);
        if (model == null) { Warn($"{cls}: could not be created (no model)"); return null; }
        var prop = W.AddProp(new PropSpec { Model = model, X = x, Z = z, Rot = Heading(rot), Block = false });
        W.PlaceItem(cls, prop.Pos.X, prop.Pos.Z, prop.Rot, prop);
        var rec = new Rec(null, name ?? Objects.NewName(cls), cls, "ITEM", slot: slot) { Prop = prop };
        _propRecs[prop] = rec;
        Props.Add(prop);
        Objects.Add(rec);
        Spawned?.Invoke(rec);
        Engine?.OnObjectSpawned(rec.Id);
        return rec;
    }
    Rec? SpawnProp(string cls, int slot, double x, double z, double rot, string? name, double? y, string type = "DCCO")
    {
        var W = World; var lc = cls.ToLowerInvariant();
        var cg = D.ClassGfx(lc);
        var model = Manifest.Has(lc) ? lc : cg != null && Manifest.Has(cg) ? cg.ToLowerInvariant() : null;
        if (model == null) { Warn($"{cls}: could not be created (no model)"); return null; }
        var owner = PlayerAt(slot);
        bool block = !NO_BLOCK.IsMatch(model) && SOLID.IsMatch(model);
        var prop = W.AddProp(new PropSpec { Model = model, X = x, Z = z, Y = y, Rot = Heading(rot), Block = block, BlockRadius = block ? Math.Min(24, ModelRadius(model) * 0.7) : 0, Party = owner?.PartyColor });
        var rec = new Rec(null, name ?? Objects.NewName(cls), cls, type, slot: slot) { Prop = prop };
        _propRecs[prop] = rec;
        Props.Add(prop);
        Objects.Add(rec);
        Spawned?.Invoke(rec);
        Engine?.OnObjectSpawned(rec.Id);
        return rec;
    }
    /// <summary>delete an object without death (DELO, REPL): gone at once; Removed is raised</summary>
    public bool Remove(Rec? rec)
    {
        rec = Registry.Get(rec);
        if (rec == null) return false;
        if (rec.Entity != null) World.RemoveEntity(rec.Entity);
        rec.Prop?.Remove();
        if (rec.Reveal != null) World.HideArea(rec.Reveal);
        Objects.Drop(rec);
        return true;
    }
    /// <summary>OCPY: new owner slot (-1 = nobody)</summary>
    public bool SetOwner(Rec? rec, int slot)
    {
        rec = Registry.Get(rec);
        if (rec == null) return false;
        var p = PlayerAt(slot); var from = rec.Owner;
        if (rec.Entity != null && p != null && p == Human && OwnClass(rec.Entity.Name) != rec.Entity.Name) return Convert(rec, p);
        // a co-op seat in another tribe gets the equivalent too (its own swap)
        if (rec.Entity != null && p != null && Coop?.SeatOf(p) is { Seat: > 1 } cs && Coop.SeatClass(cs, rec.Entity.Name) is var cc && cc != RuleClass(rec.Entity.Name) && D.Exists(cc)) return Convert(rec, p, cc);
        if (rec.Entity != null && Swap != null && from == Human && p != Human) rec.FromHuman = true;      // (Registry.ClassIs)
        if (rec.Entity != null) return World.SetOwner(rec.Entity, p);          // 'owner' is reported from the world's event
        rec._slot = p?.Id ?? -1;
        OwnerChanged?.Invoke(rec, from, p);
        Engine?.OnOwnerChanged(rec.Id);
        return true;
    }
    /// <summary>an object given to the player in a mission played with another tribe becomes the chosen tribe's
    /// (tribes.js): the same record (name, groups) gets a new world object of the equivalent class in the same place (a
    /// building where it fits: Fit; walls, gates, towers, traps exactly, a gate with its state), with the same share of
    /// hit points and level; OwnerChanged is raised as for OCPY</summary>
    bool Convert(Rec rec, Player p, string? toClass = null)
    {
        var W = World; var e = rec.Entity!; var from = e.Owner; var to = toClass ?? OwnClass(e.Name);
        bool bld = e is Building;
        double x = e is Building bb ? bb.Origin.X : e.Pos.X, z = e is Building bz ? bz.Origin.Z : e.Pos.Z, rot = e is Building br ? br.Rot : e.Heading;
        var ix = D.Info(to)!; double ratio = e.Hp / e.MaxHp; int? gateState = e.Def?.Gate == true && e is Building gb ? gb.GateState : null;
        int? level = e is Unit eu ? eu.Level : null; bool built = e is not Building b0 || b0.Built;
        W.RemoveEntity(e);
        rec.Entity = null;
        rec.SetClass(to, ix.Type);
        _pending = rec;
        var at = ix.Type == "BLDG" && !OnWallGrid(to, p) ? Fit(to, p, x, z, rot) : (x, z, rot);
        var n = W.Spawn(to, p, at.Item1, at.Item2, new SpawnOpts { Level = level != null ? Math.Max(level.Value, D.MinLevel(to, p)) : null, Heading = at.Item3, Built = built });
        _pending = null;
        if (n == null) { Warn($"{to}: could not be created for the player (model not loaded?)"); Objects.Drop(rec); return false; }
        n.Hp = Math.Max(1, Math.Floor(n.MaxHp * ratio + 0.5));
        if (gateState != null && n.Def!.Gate && n is Building ng) W.SetGate(ng, gateState.Value);
        if (n is Building nb) nb.UpdateVisual();
        else if (n is Unit nu) { nu.HomeX = nu.Pos.X; nu.HomeZ = nu.Pos.Z; }
        OwnerChanged?.Invoke(rec, from, p);
        Engine?.OnOwnerChanged(rec.Id);
        return true;
    }
    /// <summary>co-op: the object becomes class `to` of player p (an ally seat in another faction: CoopRuntime)</summary>
    public bool ConvertTo(Rec rec, Player p, string to) => Convert(rec, p, to);
    /// <summary>ACDO SetPos: map position, optional map heading</summary>
    public bool Teleport(Rec? rec, double mx, double my, double? rot = null)
    {
        rec = Registry.Get(rec);
        if (rec == null) return false;
        var (x, z) = ToGame(mx, my);
        if (rec.Entity != null) return World.Teleport(rec.Entity, x, z, rot != null ? Heading(rot.Value) : null);
        if (rec.Prop != null) { rec.Prop.Pos = new V3(x, World.Height(x, z), z); if (rot != null) rec.Prop.Rot = Heading(rot.Value); rec.Prop.LookVersion++; return true; }
        rec._x = mx; rec._y = my;
        return true;
    }
    /// <summary>OBAP: flags = 1 visible | 2 hitable | 4 selectable (| 8 constructible | 16 destructible | 32
    /// deconstructible: kept on the record only). An object without the visible flag is out of the world until it gets
    /// it back.</summary>
    public bool SetAppearance(Rec? rec, int flags)
    {
        rec = Registry.Get(rec);
        if (rec == null) return false;
        rec.Flags = flags;
        rec.Visible = (flags & 1) != 0; rec.Hitable = (flags & 2) != 0; rec.Selectable = (flags & 4) != 0;
        var e = rec.Entity;
        if (e != null)
        {
            World.SetParked(e, !rec.Visible);
            e.Untargetable = !rec.Hitable;
            e.Unselectable = !rec.Selectable;
            if (!rec.Selectable) Host?.Presenter.Deselect(e);
        }
        rec.Prop?.SetVisible(rec.Visible);
        return true;
    }
    /// <summary>only the visible flag</summary>
    public bool SetVisible(Rec? rec, bool on) { if (Registry.Get(rec) == null) return false; int f = rec!.Flags ?? 7; return SetAppearance(rec, on ? f | 1 : f & ~1); }
    /// <summary>ACDO SetAnim: play an animation of the object's model `loops` times (&lt;= 1: once). false if it has none</summary>
    public bool PlayAnim(Rec? rec, string name, int loops = 1)
    {
        rec = Registry.Get(rec);
        if (rec == null) return false;
        if (rec.Prop != null) return rec.Prop.PlayAnim(name, loops);
        var e = rec.Entity;
        if (e?.Anim == null || !e.Anim.Has(name)) return false;
        if (e is Unit u) { World.ReleaseTask(u); World.DropStates(u); u.Task = UnitTask.Idle(); u.Path = new NavPath(); u.BusyAnim = true; }
        int left = Math.Max(1, loops);
        if (e is Building b) b.LastAnim = name;          // its last pose (a gate that opened) in a saved game
        e.Anim.Play(name, new AnimOpts { Loop = false, Restart = true, OnDone = new AnimDone("scriptAnim", new Args { ["name"] = name, ["left"] = (double)left }) });
        return true;
    }
    // the next loop of a script's animation, or the end (setup.js playAnim again())
    [AnimDone("scriptAnim")]
    static void ScriptAnimDone(GameWorld W, Entity e, Args? a)
    {
        var name = a?.Str("name") ?? "";
        int left = (int)(a?.Num("left") ?? 1) - 1;
        if (left > 0 && e.Alive) { e.Anim?.Play(name, new AnimOpts { Loop = false, Restart = true, OnDone = new AnimDone("scriptAnim", new Args { ["name"] = name, ["left"] = (double)left }) }); return; }
        if (e is Unit u) u.BusyAnim = false;
        else if (e is Building b && !Regex.IsMatch(name, "open|close")) b.IdleAnim();
    }
    /// <summary>QMRK: state = STATE_INVISIBLE | QM_STATE_RED | QM_STATE_GREEN | QM_STATE_YELLOW | EC_STATE_YELLOW (or 0-4)</summary>
    public bool SetQuestionMark(string reference, string state, string? tooltip)
    {
        var m = QuestionMarks.FirstOrDefault(q => q.Guid == reference || q.Name == reference);
        if (m == null) return false;
        m.State = int.TryParse(state, out int si) ? (si >= 0 && si < QM_STATES.Length ? QM_STATES[si] : "STATE_INVISIBLE") : state;
        if (tooltip != null) m.Tooltip = tooltip;
        QuestionMarkChanged?.Invoke(m);
        return true;
    }
    /// <summary>the index entry of the mission after this one (null after the last)</summary>
    public MissionEntry? Next()
    {
        // single_04 won: single_05, not BoosterPack 1's 4-1 that follows it in the list (CampaignMgr.usl:159-167 [MarkusO
        // 5.2.07]) - its bonus points go to mission 5; 4-3 is followed by 5 anyway
        if (Id == 4 && Data.Index.Find(e => e.Id == 5) is { } m5) return m5;
        int i = Data.Index.FindIndex(e => e.Id == Id);
        return i >= 0 && i + 1 < Data.Index.Count ? Data.Index[i + 1] : null;
    }
}
