// Game rules derived from the (runtime) tech tree - see TechTree.cs - plus a few tables the original keeps in
// script files (script classes, resource values, the hero list; tools/build_data.py -> gamedata.json).
//
// Every query takes the owning player (or null for wild animals) because each player has its own tree:
// upgrades, level filters and buildings change the values while the game runs. Results are memoised per tree
// version, so reading stats every frame is cheap.
//
//   rules.Def(name, owner)             class data (type, tribe, script class, flags, captain, delivery, ...)
//   rules.Stats(name, level, owner)    per-level stats (hit points, sight, speed, gfx, limits, carry, abilities ...)
//   rules.WeaponSet(name, lv, owner)   weapons a unit uses at a level {long, medium, short} (WeaponMgr.GetBestWeapon)
//   rules.WeaponStats(w, name, owner)  a weapon's values after the tribe's /Modifications (FightingObj.UpdateWeapons)
//   rules.ActionsAt(owner, at)         build / upgrade / move actions offered by a unit or building class
//   rules.Check(owner, action, unit)   null if the action can be started, else a reason code
//
// Naming: "level" is always the pyramid row as shown to the player, 1..5 (the scripts use 0..4).
//
// Ported from remake/src/game/rules.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Rules;

/// <summary>
/// Whose tree a rule is read from: a player (World/Player), a unit's own local tree (Entity.RulesOwner), or null for
/// the neutral world. JS: `owner.tt`, `owner.tribe`, `owner.debug`, `owner.maxEpoch`, `owner.foreignActions`.
/// </summary>
public interface IRulesOwner
{
    TechTree? Tt { get; }
    string Tribe { get; }
    /// <summary>debug mode: everything free and instant, no building requirements (player.js)</summary>
    bool Debug { get; }
    /// <summary>debug option "highest epoch" (0 = none)</summary>
    int MaxEpoch { get; }
    /// <summary>a player given buildings of another tribe offers their own tribe's actions (campaign/tribes.js)</summary>
    bool ForeignActions { get; }
}

/// <summary>Costs of an action: food, wood, stone, skulls ("iron" in the tech tree), power (the sequel's).</summary>
public sealed class Cost
{
    public double Food, Wood, Stone, Skulls, Power;
    public double this[string r]
    {
        get => r switch { "food" => Food, "wood" => Wood, "stone" => Stone, "skulls" => Skulls, "power" => Power, _ => 0 };
        set { switch (r) { case "food": Food = value; break; case "wood": Wood = value; break; case "stone": Stone = value; break; case "skulls": Skulls = value; break; case "power": Power = value; break; } }
    }
    public Cost Clone() => (Cost)MemberwiseClone();
    public override string ToString() => $"food {Food} wood {Wood} stone {Stone} skulls {Skulls}" + (Power != 0 ? $" power {Power}" : "");
}

public sealed class ResAmounts
{
    public double Food, Wood, Stone;
    public double this[string r] => r switch { "food" => Food, "wood" => Wood, "stone" => Stone, _ => 0 };
}

public sealed class Limits
{
    public double MaxUnits, MaxFood, MaxWood, MaxStone;
}

public sealed class HealAbility
{
    public double Radius, Amount, Mod;
}

/// <summary>A value of a special ability's parameter: a number, else the original string (num(v, v) in the JS).</summary>
public readonly struct AbilityValue
{
    public readonly double? Number;
    public readonly string? Raw;
    public AbilityValue(double? n, string? raw) { Number = n; Raw = raw; }
    public bool IsNumber => Number.HasValue;
    /// <summary>JS truthiness</summary>
    public bool Truthy => Number.HasValue ? Number.Value != 0 && !double.IsNaN(Number.Value) : !string.IsNullOrEmpty(Raw);
    public override string ToString() => Number.HasValue ? TT.JsString(Number.Value) : Raw ?? "undefined";
}

/// <summary>The parameters of one enabled special ability (special_abilities/&lt;name&gt;/*).</summary>
public sealed class Ability : Dictionary<string, AbilityValue>
{
    public Ability() : base(StringComparer.Ordinal) { }
    public double Num(string k, double d = 0) => TryGetValue(k, out var v) && v.Number.HasValue ? v.Number.Value : d;
    public string? Str(string k) => TryGetValue(k, out var v) ? v.Number.HasValue ? TT.JsString(v.Number.Value) : v.Raw : null;
    /// <summary>buffs.js on(): true, 1, "1", "true"</summary>
    public bool On(string k) => TryGetValue(k, out var v) && (v.Number.HasValue ? v.Number.Value == 1 : v.Raw == "1" || v.Raw == "true");
}

/// <summary>Per-level stats of a class (rules.stats).</summary>
public sealed class LevelStats
{
    public bool Exists;
    public int Level;
    public double Hp, Fow, Speed, MaxSpeed, Tf, Scalps, Size, CaptainLevel, Aggressive, MaxWorkers, Agility, MaxPassengers;
    public string? Gfx;
    public ResAmounts Carry = new();
    public Limits Limits = new();
    public HealAbility? Heal;
    public double HealModRel = 1, HealModAbs;
    /// <summary>the enabled special abilities by name (in tech tree order)</summary>
    public Dictionary<string, Ability> Abilities = new(StringComparer.Ordinal);
    public string? Captain;
}

/// <summary>Class data (rules.def).</summary>
public sealed class ObjectDef
{
    public string Name = "", Tribe = "", Type = "", Path = "";
    public string Class => Type;
    public string? Desc, Script, Caste, StandAnim, Fx;
    public double Aggressive, CanBuild, CanHarvest, MaxHelpers, Repletion, Agility, Density, Growup, MaxWorkers, FormationPos = -1, MaxPassengers;
    public bool NightActive, Wall, Gate, Coastal, BuildupAutoattack, Unique, Ladder;
    /// <summary>which kind of wall-map object: "wall" | "gate" | "tower" | "trap" | null (docs/spec/walls.md §1)</summary>
    public string? WallKind;
    /// <summary>resources an "unlimited" building produces (CUnlimitedBuilding)</summary>
    public List<string> Unlimited = new();
    public List<string> Delivery = new();
    public string? Captain, CaptainGfx, StartTT, GlobalStartTT;
    public int AttackType;
    /// <summary>0 none, 1 characters, 2 characters + animals + vehicles</summary>
    public int TransportClass;
    /// <summary>stats of levels 1..5 (index 0 unused)</summary>
    public LevelStats?[] Levels = new LevelStats?[6];
}

public sealed record WeaponAnim(string? Name, double Delay, double ShootDelay, double Combo, string? Follow);
public sealed record WeaponPart(List<string> Gfx, string Link, string? Kind);

/// <summary>Raw weapon data (tech tree values, no modifiers).</summary>
public sealed class Weapon
{
    public string Id = "", Path = "";
    public double Level, Secondary, Slot, Dmg, MinDmg, EndDmg, Range, MinRange, Freq, Splash, Def, RDef, Ap, Poison, PoisonTicks,
        BulletSpeed, Falloff, Jitter, Size, PenAngle = 30;
    public string? Caste, Fx, Projectile;
    public bool Penetration;
    public Dictionary<string, double> Bonus = new(StringComparer.Ordinal), DefBonus = new(StringComparer.Ordinal);
    public List<WeaponAnim> Anims = new();
    public List<WeaponPart> Parts = new();
    public override string ToString() => Id;
}

public sealed class WeaponSet
{
    public Weapon? Long, Medium, Short;
    public List<Weapon> All = new();
}

/// <summary>A weapon's values for a unit class after the tribe's modifiers.</summary>
public sealed class WeaponStats
{
    public Weapon W = null!;
    public double Dmg, EndDmg, Ap, Splash, Prot, RProt, Range, MinRange, Dur, Poison, PoisonTicks;
}

public sealed class ActionLoc
{
    public string At = "";
    /// <summary>uiposition: {cat, menupos, subcat ...} (or a string), null if none</summary>
    public TtNode? Ui;
    public string? Icon, Desc;
    public bool Hidden;
    public List<string>? LocalFlags;
    public string? UiStr(string key) => Ui != null && Ui.IsObject ? Ui[key]?.Scalar : null;
}

public sealed class ActionResult
{
    public string Obj = "";
    public string? Path, Type, Rider;
    public string Cls = "player";
    public double Level;
    public List<string> ResultActions = new();
}

/// <summary>A build / upgrade / move action of the tech tree (/Actions/&lt;tribe&gt;/&lt;kind&gt;/&lt;cat&gt;/&lt;id&gt;).</summary>
public sealed class ActionDef
{
    public string Id = "", Tribe = "", Kind = "", Cat = "", Type = "", Path = "";
    public double Time, LevelReq;
    public bool Visible, Disabled;
    /// <summary>an action the remake adds to the original's tree ("remake": "1": Rules.RemakeWallUpgrades)</summary>
    public bool Remake;
    /// <summary>MIRAGE's building limits: an upgrade with "check = '1'" counts toward the limit of its product from the
    /// moment it is queued (RequirementsMgr.usl CanBuildingBeAdded, Action.usl Init; World.BuildingLimits)</summary>
    public bool LimitCheck;
    /// <summary>"product_name" / "product_tribe" of an upgrade: the class it turns the building into (the tower upgrades:
    /// aje_small_tower/medium_tower -> aje_medium_tower); null: the action's id</summary>
    public string? Product, ProductTribe;
    public Cost Cost = new();
    public List<string> Req = new(), BReq = new(), CasteReq = new(), Tribes = new();
    public List<ActionLoc> Locs = new();
    public List<ActionResult> Results = new();
    public List<string>? Target, TargetOwner;
    /// <summary>the location this action was found at (ActionsAt / ActionsOf), else null</summary>
    public ActionLoc? Loc;

    /// <summary>{ ...a, loc: l }</summary>
    public ActionDef WithLoc(ActionLoc l) { var c = (ActionDef)MemberwiseClone(); c.Loc = l; return c; }
    public override string ToString() => Path;
}

public sealed class IndexEntry
{
    public string Tribe = "", Type = "", Path = "";
}

public sealed class Rules
{
    static readonly Logger L = Log.For("Rules");

    public static readonly string[] RES = { "food", "wood", "stone" };
    /// <summary>"iron" in the tech tree = skulls; power: the sequel's electricity (0 everywhere else)</summary>
    public static readonly string[] COSTS = { "food", "wood", "stone", "skulls", "power" };
    public static readonly string[] TYPES = { "CHTR", "ANML", "VHCL", "SHIP", "BLDG", "NEST" };
    public static readonly double[] SPEED_FALLBACK = { 0, 1.5, 5, 7, 10 };
    // units that prefer buildings as targets (SetAttackType(1): character.usl:614, Vehicle.usl, Ship.usl, Animal.usl)
    static readonly HashSet<string> SIEGE = new(StringComparer.Ordinal) { "aje_rammer", "ninigi_mortar", "hu_steam_ram", "ninigi_firecannon", "hu_steam_boat", "aje_catamaran",
        "ninigi_rocket_boat", "hu_mammoth_log_cannon", "aje_ankylosaurus" };
    // transport class 2 (carries characters, animals and vehicles): ships, hovercraft, transport turtle (TransportObj.usl:1076)
    static readonly HashSet<string> TRANSPORT2 = new(StringComparer.Ordinal) { "seas_hovercraft", "aje_transport_turtle" };
    // build actions whose start level the remake sets itself (Rules.StartLevel)
    static readonly Dictionary<string, int> LEVEL_OVERRIDE_MIRAGE = new() { ["seas_lumberjack"] = 4, ["seas_lumberjack_minigun"] = 4 };
    static readonly Regex WallScript = new("^(CWall|CNinigi_Defense_Skewer)$", RegexOptions.Compiled);
    static readonly Regex TrapScript = new("Pitfall|SnareTrap|ResinField|Minefield|PoisonDung|Trap", RegexOptions.Compiled);
    static readonly Regex LadderScript = new("^(CLadder|CSeasStaircase)$", RegexOptions.Compiled);
    static readonly Regex TargetSplit = new("[ ,|]+", RegexOptions.Compiled);

    public readonly TechTreeBase Base;
    public readonly GameData Extra;
    public readonly Dictionary<string, TextEntry> Texts;
    public readonly Dictionary<string, string> Scripts;
    public readonly Dictionary<string, Dictionary<string, double>> ResourceValues;
    readonly HashSet<string> _npc;
    public readonly Dictionary<string, string> SameAs;
    public readonly double[] Pyramid;
    public readonly double[] LevelupSkulls;
    /// <summary>the neutral tree of the wild animals</summary>
    public readonly TechTree World;
    /// <summary>name -> { tribe, type, path } for every object class of StartTT (insertion order kept in IndexOrder)</summary>
    public readonly Dictionary<string, IndexEntry> Index = new(StringComparer.Ordinal);
    public readonly List<string> IndexOrder = new();
    /// <summary>unit class -> [weapon path] (a weapon lists its users); key order in UsersOrder</summary>
    readonly Dictionary<string, List<string>> _users = new(StringComparer.Ordinal);
    readonly List<string> _usersOrder = new();

    public Rules(TtNode tt, GameData extra)
    {
        Extra = extra;
        ScriptedGates(tt, extra);
        RemakeWallUpgrades(tt);
        MirageFarmLimitChecks(tt);
        RemakeFlyAbilities(tt, extra);
        Base = new TechTreeBase(tt);
        Texts = extra.Texts;
        Scripts = extra.Script;
        ResourceValues = extra.Resources;
        _npc = new HashSet<string>(extra.Npc.Select(n => n.ToLowerInvariant()), StringComparer.Ordinal);
        SameAs = extra.SameAs;
        Pyramid = extra.Pyramid;
        LevelupSkulls = extra.LevelupSkulls;
        World = new TechTree(Base, "World");
        var O = Base.Start["Objects"];
        if (O != null)
            foreach (var (tribe, tn) in O.Entries())
            {
                if (!tn.IsObject) continue;
                foreach (var type in TYPES)
                {
                    var tt0 = tn[type];
                    if (tt0 == null || !tt0.IsObject) continue;
                    foreach (var (name, node) in tt0.Entries())
                        if (node.IsObject && !Index.ContainsKey(name)) { Index[name] = new IndexEntry { Tribe = tribe, Type = type, Path = $"/Objects/{tribe}/{type}/{name}" }; IndexOrder.Add(name); }
                }
                var W = tn["Weapons"];
                if (W == null || !W.IsObject) continue;
                foreach (var (w, node) in W.Entries())
                {
                    if (!node.IsObject) continue;
                    foreach (var u in Strs(node["Users"]))
                    {
                        if (!_users.TryGetValue(u, out var l)) { _users[u] = l = new List<string>(); _usersOrder.Add(u); }
                        l.Add($"/Objects/{tribe}/Weapons/{w}");
                    }
                }
            }
        L.Info($"rules: {Index.Count} classes, {_users.Count} weapon users, mod '{extra.Mod}'");
    }

    /// <summary>Loads techtree.json and gamedata.json of a data folder (a configuration's "data").</summary>
    public static Rules Load(string dataDir)
    {
        var tt = TtNode.FromJson(Json.ParseFile(Path.Combine(dataDir, "techtree.json")));
        var gd = GameData.Load(Path.Combine(dataDir, "gamedata.json"));
        return new Rules(tt, gd);
    }

    // ---------------------------------------------------------------- helpers of rules.js
    /// <summary>list(n): the children of an object except _value</summary>
    public static List<TtNode> List(TtNode? n)
    {
        var res = new List<TtNode>();
        if (n == null || !n.IsObject) return res;
        foreach (var (k, v) in n.Entries()) if (k != "_value") res.Add(v);
        return res;
    }
    /// <summary>strs(n): the string values of the children</summary>
    public static List<string> Strs(TtNode? n)
    {
        var res = new List<string>();
        foreach (var v in List(n)) { var s = v.Scalar; if (s != null) res.Add(s); }
        return res;
    }
    static double Clamp(double v, double a, double b) => Math.Max(a, Math.Min(b, v));
    static string? Sc(TtNode? n) => n?.Scalar;
    static double N(TtNode? n, double d = 0) => TT.Num(n?.Scalar, d);
    static TtNode? Ch(TtNode? n, string k) => n != null && n.IsObject ? n[k] : null;
    static bool Truthy(TtNode? n) => n != null && (n.IsObject || !string.IsNullOrEmpty(n.Leaf));
    static List<string> FlagList(TtNode? n)
    {
        var res = new List<string>();
        if (n == null || !n.IsObject) return res;
        foreach (var (k, v) in n.Entries()) if (k != "_value" && v.Scalar == "1") res.Add(k);
        return res;
    }

    // Gates the maps place that the tech tree does not list (the campaign's seas_endboss_gate, seas_hq_gate: script class
    // CGate in classes/buildings/*.txt, a model, nothing else; a listed CGate class without the gate flag is a gate too,
    // Rules.Def). The engine makes them gates with the class's defaults; the remake gives them the node of the tribe's
    // own gate (SEAS: seas_gate) with their model, so they open and close (their GateState, the triggers' Open / Close
    // Gate) instead of standing as closed scenery.
    static void ScriptedGates(TtNode tt, GameData extra)
    {
        var O = tt["StartTT"]?["Objects"];
        var S = extra.Script; var gfx = extra.ClassGfx; var P = extra.ClassParents;
        bool IsGate(string? c) { for (int k = 0; c != null && k < 20; k++, c = P.TryGetValue(c, out var p) ? p : null) if (c == "CGate") return true; return false; }
        if (O == null || !O.IsObject) return;
        bool Has(string n) { foreach (var (_, t) in O.Entries()) { var b = Ch(t, "BLDG"); if (b != null && b.IsObject && Truthy(b[n])) return true; } return false; }
        foreach (var (n, cls) in S)
        {
            if (!IsGate(cls) || !gfx.TryGetValue(n, out var g) || string.IsNullOrEmpty(g) || Has(n)) continue;
            string tribe = n.StartsWith("seas_") ? "SEAS" : n.StartsWith("hu_") ? "Hu" : n.StartsWith("aje_") ? "Aje" : n.StartsWith("ninigi_") ? "Ninigi" : "SEAS";
            string tpl = tribe switch { "SEAS" => "seas_gate", "Hu" => "hu_palisade_gate", "Aje" => "aje_bone_palisade_gate", _ => "ninigi_palisade_gate" };
            var bl = Ch(O[tribe], "BLDG");
            var src = Ch(bl, tpl);
            if (src == null || !src.IsObject) continue;
            var c = src.Clone();
            c["gfx"] = new TtNode(g);
            bl![n] = c;
        }
    }

    // The remake's own wall upgrades (docs/SIMULATION.md "Wall upgrades"; README "What the remake adds"). MIRAGE gives the
    // SEAS a wall (seas_wall, class 3) but no way to turn a fence into one, where the Dustriders have their per-piece
    // clay wall upgrade (/Actions/Aje/Upgrades/aje_bone_palisade/clay_wall). The remake adds one in that upgrade's form,
    // written into the tech tree before it is read, only where the data has both classes and no such upgrade of its own:
    //   /Actions/<T>/Upgrades/<from>/<to>   personal, at the <from> piece (PROD / UPGR), the result a local filter;
    //       price: <to>'s build cost less <from>'s (each resource at least 0) and build time less <from>'s (at least
    //       1 s) - the clay wall's pattern (30/40 - 20/20 = 10/20 wood/stone, 22 - 10 = 12 s); conditions (inventions)
    //       and visibility of <vis>'s build action, and every filter modificator of <vis>'s visibility follows the
    //       upgrade too (MIRAGE: Miyagi's level 4 / 5, the 5th epoch), as the clay wall's does; the cost and duration
    //       modificators of <to>'s build action as well (Miyagi's chief bonus x0.7); its icon and help text <to>'s
    //   /Filters/<T>/Upgrades/<from>/<to>   on that piece only: <from>'s gfx := "<to>" (the class name, as the
    //       original's wall filters write it: the object becomes that class - World.WallClassChanged), the action gone
    static readonly (string Tribe, string From, string To, string Vis)[] REMAKE_WALL_UPGRADES =
    {
        ("SEAS", "seas_fence", "seas_wall", "seas_wall"),
        ("SEAS", "seas_gate", "seas_wall_gate", "seas_wall"),
    };
    static void RemakeWallUpgrades(TtNode tt)
    {
        var S = Ch(tt, "StartTT"); var A = Ch(S, "Actions"); var O = Ch(S, "Objects"); var F = Ch(tt, "Filters");
        if (A == null || O == null || F == null) return;
        static TtNode Obj(TtNode parent, string key) { var n = parent[key]; if (n == null || !n.IsObject) { n = new TtNode(); parent[key] = n; } return n; }
        static string I(double v) => v.ToString(CultureInfo.InvariantCulture);
        var twins = new List<(TtNode Mods, string Key, TtNode Mod)>();
        foreach (var (tribe, from, to, vis) in REMAKE_WALL_UPGRADES)
        {
            var build = Ch(Ch(Ch(A, tribe), "Build"), "BLDG");
            TtNode? bFrom = Ch(build, from), bTo = Ch(build, to), bVis = Ch(build, vis);
            var objs = Ch(Ch(O, tribe), "BLDG");
            if (bFrom == null || bTo == null || bVis == null || Ch(objs, from) == null || Ch(objs, to) == null) continue;
            var ups = Obj(Obj(A[tribe]!, "Upgrades"), from);
            if (ups[to] != null) continue;                 // the data has its own
            string upPath = $"/Actions/{tribe}/Upgrades/{from}/{to}", fPath = $"/Filters/{tribe}/Upgrades/{from}/{to}";
            var cost = new System.Text.Json.Nodes.JsonObject();
            foreach (var r in new[] { "units", "food", "wood", "stone", "iron" })
                cost[r] = I(Math.Max(0, N(Ch(Ch(Ch(bTo, "conditions"), "rescosts"), r)) - N(Ch(Ch(Ch(bFrom, "conditions"), "rescosts"), r))));
            var inv = new System.Text.Json.Nodes.JsonObject();
            int k = 0;
            foreach (var x in Strs(Ch(Ch(bVis, "conditions"), "inventobjects"))) inv[I(k++)] = x;
            var up = new System.Text.Json.Nodes.JsonObject
            {
                ["code"] = $"{to}_Upgrades_{tribe}_remake",
                ["remake"] = "1",
                ["duration"] = I(Math.Max(1, N(bTo["duration"]) - N(bFrom["duration"]))),
                ["visibility"] = Sc(bVis["visibility"]) ?? "1",
                ["secondarycontroller"] = "0",
                ["personal"] = "1",
                ["locations"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["0"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["description"] = $"_{to}", ["iconpath"] = Sc(Ch(Ch(Ch(bTo, "locations"), "0"), "iconpath")) ?? to,
                        ["uiposition"] = new System.Text.Json.Nodes.JsonObject { ["cat"] = "PROD", ["menupos"] = "0", ["subcat"] = "UPGR" },
                        ["_value"] = $"/Objects/{tribe}/BLDG/{from}",
                    },
                },
                ["results"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["0"] = new System.Text.Json.Nodes.JsonObject { ["class"] = "local", ["type"] = "activate", ["upgrade"] = "1", ["_value"] = fPath },
                },
                ["conditions"] = new System.Text.Json.Nodes.JsonObject { ["tribe"] = new System.Text.Json.Nodes.JsonObject { ["0"] = tribe }, ["rescosts"] = cost, ["inventobjects"] = inv },
            };
            ups[to] = TtNode.FromJson(up);
            var filter = new System.Text.Json.Nodes.JsonObject
            {
                ["priority"] = "10",
                ["Modificators"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["mod_gfx"] = new System.Text.Json.Nodes.JsonObject { ["op"] = "replace", ["path"] = $"/Objects/{tribe}/BLDG/{from}/gfx", ["value"] = to },
                    ["RemoveMe"] = new System.Text.Json.Nodes.JsonObject { ["op"] = "remove", ["path"] = upPath },
                },
            };
            Obj(Obj(Obj(F, tribe), "Upgrades"), from)[to] = TtNode.FromJson(filter);
            // the modificators of the build actions that the upgrade follows (collected first, added after the walk)
            string visA = $"/Actions/{tribe}/Build/BLDG/{vis}", costA = $"/Actions/{tribe}/Build/BLDG/{to}";
            void Walk(TtNode n)
            {
                foreach (var (key, c) in n.Entries())
                {
                    if (!c.IsObject) continue;
                    if (key != "Modificators") { Walk(c); continue; }
                    foreach (var (mk, m) in c.Entries())
                    {
                        var p = Sc(Ch(m, "path"));
                        if (p == null || !m.IsObject) continue;
                        string? np = p == visA + "/visibility" ? upPath + "/visibility"
                            : p.StartsWith(costA + "/conditions/rescosts/", StringComparison.Ordinal) || p == costA + "/duration" ? upPath + p.Substring(costA.Length) : null;
                        if (np == null) continue;
                        var tw = m.Clone(); tw["path"] = new TtNode(np);
                        twins.Add((c, $"{mk}_remake_{from}_{to}", tw));
                    }
                }
            }
            Walk(F);
        }
        foreach (var (mods, key, mod) in twins) if (mods[key] == null) mods[key] = mod;
    }

    // MIRAGE's building limits (World.BuildingLimits, docs/SIMULATION.md "Building limits"): an Aje farm's conversion to
    // another farm type is an upgrade of the farm (/Actions/Aje/Upgrades/aje_small_farm/<type>); with "check = '1'" the
    // server counts it toward the new type's limit from the moment it is queued (CAction.Init), so a worker cannot place
    // one farm more while it runs. MIRAGE master (a349cb41) has the check on the tower upgrades only; the fix delivered
    // to the modder (mirage_aje_farm_limits_v3, _TechTree.ttree) adds it to the three farm conversions. The remake carries
    // that data fix itself until master has it: only where the tree has building limits at all (MIRAGE: the
    // Filters/<tribe>/BuildingCount filters; the base game has none) and the action has no check of its own.
    static readonly string[] FARM_TYPES = { "aje_small_farm", "aje_medium_farm", "aje_huge_farm" };
    static void MirageFarmLimitChecks(TtNode tt)
    {
        var ups = Ch(Ch(Ch(Ch(Ch(tt, "StartTT"), "Actions"), "Aje"), "Upgrades"), "aje_small_farm");
        var counts = Ch(Ch(Ch(tt, "Filters"), "Aje"), "BuildingCount");
        if (ups == null || counts == null || !counts.IsObject) return;
        foreach (var f in FARM_TYPES)
        {
            var a = Ch(ups, f);
            if (a == null || !a.IsObject || a["check"] != null) continue;
            a["check"] = new TtNode("1");
        }
    }

    // The flyers' abilities "Land" and "Take off" of MIRAGE's flying units v3/v4 design (delivered to the modder, not in
    // MIRAGE master a349cb41: mirage_flying_units_v4 _TechTree.ttree, Moves/<TYPE>/fly_land and fly_takeoff; World.Flyers):
    //   /Actions/<T>/Moves/<TYPE>/fly_land     a target point (secondarycontroller VEC3), icon jetfly_off, SPEC/UFOC place 1
    //   /Actions/<T>/Moves/<TYPE>/fly_takeoff  on the spot, icon jetfly_on, SPEC/UFOC place 2
    // at every flying class of that tribe and type the data has (by script class: CAjePteranodon, CHuPteranodon,
    // CNinigiCargoLifter, CNinigiDirigible, CZeppelin, CSeasHelicopter), duration 1, condition the tribe. Written into the
    // tree before it is read, only where the data has no such action of its own ("remake": 1 - not in the JS's tree).
    // Whether a unit offers them is decided by World.MovesOf (only a flyer: a zeppelin without the option FlyingEnabled
    // or a helicopter in its assault mode is none).
    static readonly string[] FLY_ABILITY_SCRIPTS = { "CAjePteranodon", "CHuPteranodon", "CNinigiCargoLifter", "CNinigiDirigible", "CZeppelin", "CSeasHelicopter" };
    static void RemakeFlyAbilities(TtNode tt, GameData extra)
    {
        var A = Ch(Ch(tt, "StartTT"), "Actions"); var O = Ch(Ch(tt, "StartTT"), "Objects");
        if (A == null || O == null) return;
        static TtNode Obj(TtNode parent, string key) { var n = parent[key]; if (n == null || !n.IsObject) { n = new TtNode(); parent[key] = n; } return n; }
        foreach (var (tribe, tn) in O.Entries())
        {
            if (!tn.IsObject || A[tribe] == null) continue;
            foreach (var type in new[] { "ANML", "VHCL" })
            {
                var objs = Ch(tn, type);
                if (objs == null || !objs.IsObject) continue;
                var flyers = new List<string>();
                foreach (var (name, node) in objs.Entries())
                    if (node.IsObject && extra.Script.TryGetValue(name.ToLowerInvariant(), out var sc) && sc != null && Array.IndexOf(FLY_ABILITY_SCRIPTS, sc) >= 0) flyers.Add(name);
                if (flyers.Count == 0) continue;
                var moves = Obj(Obj(A[tribe]!, "Moves"), type);
                foreach (var (id, icon, pos, target) in new[] { ("fly_land", "jetfly_off", "1", true), ("fly_takeoff", "jetfly_on", "2", false) })
                {
                    if (moves[id] != null) continue;            // (the data has its own)
                    var locs = new System.Text.Json.Nodes.JsonObject();
                    for (int i = 0; i < flyers.Count; i++)
                        locs[i.ToString(CultureInfo.InvariantCulture)] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["description"] = "_" + icon, ["iconpath"] = icon,
                            ["uiposition"] = new System.Text.Json.Nodes.JsonObject { ["cat"] = "SPEC", ["subcat"] = "UFOC", ["menupos"] = pos },
                            ["_value"] = $"/Objects/{tribe}/{type}/{flyers[i]}",
                        };
                    var act = new System.Text.Json.Nodes.JsonObject
                    {
                        ["code"] = $"{id}_Moves_{tribe}", ["remake"] = "1", ["duration"] = "1", ["visibility"] = "1", ["locations"] = locs,
                        ["conditions"] = new System.Text.Json.Nodes.JsonObject { ["tribe"] = new System.Text.Json.Nodes.JsonObject { ["0"] = tribe } },
                    };
                    if (target) act["secondarycontroller"] = new System.Text.Json.Nodes.JsonObject { ["_value"] = "1", ["0"] = "VEC3" };
                    moves[id] = TtNode.FromJson(act);
                }
            }
        }
    }

    // ---------------------------------------------------------------- basics
    public TechTree Tree(IRulesOwner? owner) => owner?.Tt ?? World;
    public TechTree NewTree(string tribe) => new(Base, tribe);
    /// <summary>a player's tree: a new tree with the game options' filters on (MIRAGE: epoch six, no titans -
    /// GameplayOptions; null = the defaults)</summary>
    public TechTree PlayerTree(string tribe, GameplayOptions? opts = null)
    {
        var t = NewTree(tribe);
        foreach (var f in (opts ?? GameplayOptions.Default).Filters(Base)) t.Enable(f);
        return t;
    }
    public IndexEntry? Info(string? name) => name != null && Index.TryGetValue(name, out var e) ? e : null;
    public bool Exists(string? name) => name != null && Index.ContainsKey(name);

    /// <summary>of the actions that make an object (their first result), the one that is that object's own: its id is the
    /// object's name, else a visible one, else the first. MIRAGE's tech tree has hidden Build actions aje_medium_farm /
    /// aje_huge_farm that put up an aje_small_farm in another mode, and its keys are sorted - "the first action of the
    /// object" was the huge farm's (a hidden action: the Dustriders' computer player never got its farm, docs/AI.md)</summary>
    public static ActionDef? OwnActionOf(IEnumerable<ActionDef> list, string obj, Func<ActionDef, bool>? where = null)
    {
        ActionDef? first = null, visible = null;
        foreach (var a in list)
        {
            if (a.Results.Count == 0 || a.Results[0].Obj != obj || (where != null && !where(a))) continue;
            if (a.Id == obj) return a;
            first ??= a;
            if (a.Visible) visible ??= a;
        }
        return visible ?? first;
    }
    /// <summary>the class whose scripted rules a class follows: itself, or for a copy its original</summary>
    public string Kind(string name) => SameAs.TryGetValue(name, out var o) ? o : name;
    /// <summary>a wild animal class: one of the world's (tech tree Objects/World/ANML; MIRAGE FightingObj.CheckWildAnimal)</summary>
    public bool IsWildClass(string name) { var ix = Info(Kind(name)); return ix != null && ix.Tribe == "World" && ix.Type == "ANML"; }
    /// <summary>script class of an object ("CTower", "CGrowingField", ...; classes/**/*.txt)</summary>
    public string? Script(string? name) => name != null && Scripts.TryGetValue(name.ToLowerInvariant(), out var s) ? s : null;
    /// <summary>is the object's script class `cls` or one derived from it (gamedata.json "classParents")</summary>
    public bool IsA(string name, string cls)
    {
        var P = Extra.ClassParents;
        int n = 0;
        for (var c = Script(Kind(name)); c != null && n < 32; c = P.TryGetValue(c, out var p) ? p : null, n++) if (c == cls) return true;
        return false;
    }
    /// <summary>attached parts of an object (riders, turrets, build-ups ...; Compose.cs), empty if none</summary>
    public List<CompositeSpec> Composites(string name) => Extra.Composites.TryGetValue(name.ToLowerInvariant(), out var l) ? l : s_noParts;
    static readonly List<CompositeSpec> s_noParts = new();
    /// <summary>the model named in the class file (used when the tech tree's gfx doesn't exist, e.g. hu_flamethrower)</summary>
    public string? ClassGfx(string name) => Extra.ClassGfx.TryGetValue(name.ToLowerInvariant(), out var g) && !string.IsNullOrEmpty(g) ? g : null;

    /// <summary>
    /// Is the clip a melee strike of the class's model (Compose.CarrierStrikes)? Variants share a model: the clips the
    /// melee weapons of the classes drawn with it list are its strikes (the saltasaurus' "attack_front" is the archer
    /// variant's stomp, the pteranodon's the wild one's peck). Start tree; class file models.
    /// </summary>
    public bool IsStrikeOf(string cls, string? clip) => clip != null && ClassGfx(cls) is string g && Strikes.TryGetValue(g, out var s) && s.Contains(clip);
    Dictionary<string, HashSet<string>> Strikes => _strikes ??= MakeStrikes();
    Dictionary<string, HashSet<string>>? _strikes;
    Dictionary<string, HashSet<string>> MakeStrikes()
    {
        var strikes = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in IndexOrder)
        {
            if (ClassGfx(c) is not string g) continue;
            foreach (var p in WeaponPaths(c, null))
                if (WeaponAt(p, null) is { Projectile: null } w)
                    foreach (var a in w.Anims)
                        if (!string.IsNullOrEmpty(a.Name)) { if (!strikes.TryGetValue(g, out var s)) strikes[g] = s = new HashSet<string>(StringComparer.OrdinalIgnoreCase); s.Add(a.Name); }
        }
        return strikes;
    }

    /// <summary>an item class of the tech tree (StartTT/Items/Objects/&lt;class&gt;: effect, GlobalTT, LocalTT, EffectName, bonus ...)</summary>
    public Dictionary<string, string?>? ItemNode(string cls)
    {
        var O = Ch(Ch(Base.Start, "Items"), "Objects");
        var n = Ch(O, cls.ToLowerInvariant()) ?? Ch(O, cls);
        if (n == null || !n.IsObject) return null;
        var res = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (k, v) in n.Entries()) res[k] = v.Scalar;
        return res;
    }
    /// <summary>food of a carcass "&lt;class&gt;_food" (settings/Resources.txt FOOD; default 100 - Resource.usl:61)</summary>
    public double CorpseFood(string name) { var v = ResourceValue("FOOD", name.ToLowerInvariant() + "_food", 0); return v > 0 ? v : 100; }
    public double ResourceValue(string section, string name, double d) =>
        ResourceValues.TryGetValue(section, out var s) && s.TryGetValue(name.ToLowerInvariant(), out var v) && v > 0 ? v : d;

    // ---------------------------------------------------------------- class data
    /// <summary>a class's node of the tech tree (/Objects/&lt;tribe&gt;/&lt;type&gt;/&lt;class&gt;): the owner's tree, else the start tree</summary>
    public TtNode? ObjectNode(string name, IRulesOwner? owner)
    {
        var ix = Info(name);
        return ix == null ? null : Tree(owner).Node(ix.Path) ?? TT.NodeAt(Base.Start, ix.Path);
    }

    public ObjectDef? Def(string name, IRulesOwner? owner)
    {
        var T = Tree(owner);
        // (a key without a string and no closure: these are asked many times a step per unit - the string keys
        // and the lambdas were a good part of the simulation's garbage)
        var mk = new TechTree.MemoKey(1, null, name, 0);
        if (T.TryMemo(mk, out var mv)) return (ObjectDef?)mv!;
        return T.SetMemo(mk, DefOf0(name, owner, T));
    }
    ObjectDef? DefOf0(string name, IRulesOwner? owner, TechTree T)
    {
        var ix = Info(name);
        if (ix == null) return null;
        var o = T.Node(ix.Path) ?? TT.NodeAt(Base.Start, ix.Path);
        if (o == null) return null;
        var cap = Sc(Ch(o, "captainclass"));
        var d = new ObjectDef
        {
            Name = name, Tribe = ix.Tribe, Type = ix.Type, Path = ix.Path, Desc = Sc(Ch(o, "description")),
            Script = Script(name),
            Aggressive = N(Ch(o, "aggressive")), CanBuild = N(Ch(o, "can_build")), CanHarvest = N(Ch(o, "can_harvest")),
            // wild animals (Animal.usl, Nest.usl): helpers it relies on, seconds a meal lasts, seconds between walks,
            // metres per animal of a nest's flock, seconds to grow up, awake at night
            MaxHelpers = N(Ch(o, "maxhelpers")), Repletion = N(Ch(o, "repletion")), Agility = N(Ch(o, "agility")),
            Density = N(Ch(o, "density")), Growup = N(Ch(o, "growup_duration")), NightActive = N(Ch(o, "nightactive")) == 1,
            MaxWorkers = N(Ch(o, "maxworkers")), Caste = NullIfEmpty(Sc(Ch(o, "caste"))), StandAnim = Sc(Ch(o, "standanim")), Fx = Sc(Ch(o, "fx_class")),
            FormationPos = N(Ch(o, "formation_pos"), -1),
            // a gate: the tech tree's gate flag, or a script class derived from CGate - the engine makes every such object
            // a gate, and a mod may list one without the flag (MIRAGE's seas_endboss_gate, ship_chain) or give it its own
            // class (MIRAGE's seas_hq_gate: CGateFOWVIsible)
            Wall = TT.Bool(Sc(Ch(o, "wall"))), Gate = TT.Bool(Sc(Ch(o, "gate"))) || IsA(name, "CGate"), Coastal = TT.Bool(Sc(Ch(o, "coastal"))),
            WallKind = null,
            // resources an "unlimited" building produces (CUnlimitedBuilding: Objects/../unlimited/<res> = 1)
            Unlimited = FlagList(Ch(o, "unlimited")),
            MaxPassengers = N(Ch(o, "max_passengers")), BuildupAutoattack = TT.Bool(Sc(Ch(o, "buildup_autoattack"))),
            Delivery = FlagList(Ch(o, "delivery")),
            Captain = !string.IsNullOrEmpty(cap) && cap != "0" ? cap : null,
            StartTT = NotZero(Sc(Ch(o, "StartTT"))),
            GlobalStartTT = NotZero(Sc(Ch(o, "GlobalStartTT"))),
            Unique = _npc.Contains(name.ToLowerInvariant()),
            AttackType = SIEGE.Contains(name) ? 1 : 0,
        };
        // objects of the wall placer (TT wall=1) and gates: which kind of wall-map object (docs/spec/walls.md §1)
        // MIRAGE splits the flag: its towers carry `tower = 1` and its traps `trap = 1` instead of `wall = 1` (MIRAGE
        // PlaceController.usl: m_bWall / m_bGate / m_bTower / m_bTrap from the tech tree)
        bool towerFlag = TT.Bool(Sc(Ch(o, "tower"))), trapFlag = TT.Bool(Sc(Ch(o, "trap")));
        // siege ladders go against a wall piece (systems/siege.js); MIRAGE flags them wall = 1, the base game gate = 1 -
        // neither makes them wall-map objects
        d.Ladder = LadderScript.IsMatch(d.Script ?? "");
        if (d.Ladder) d.WallKind = null;
        else if (d.Gate) d.WallKind = "gate";
        else if (trapFlag) d.WallKind = "trap";
        else if (towerFlag) d.WallKind = "tower";
        else if (d.Wall) d.WallKind = WallScript.IsMatch(d.Script ?? "undefined") ? "wall" : TrapScript.IsMatch(d.Script ?? "undefined") ? "trap" : "tower";
        d.TransportClass = d.MaxPassengers == 0 ? 0 : (ix.Type == "SHIP" || TRANSPORT2.Contains(name)) ? 2 : 1;
        if (d.Captain != null)
        {
            var c = Info(d.Captain);
            var cn = c != null ? T.Node(c.Path) ?? TT.NodeAt(Base.Start, c.Path) : null;
            d.CaptainGfx = cn != null ? Sc(Ch(cn, "gfx")) : null;
        }
        for (int l = 1; l <= 5; l++) d.Levels[l] = Stats(name, l, owner);
        return d;
    }
    static string? NullIfEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;
    static string? NotZero(string? s) => !string.IsNullOrEmpty(s) && s != "0" ? s : null;

    /// <summary>modifier column of a class: characters use their caste, everything else "tec" (FightingObj.usl:8395, character.usl:2640)</summary>
    public string Column(string name, IRulesOwner? owner)
    {
        var ix = Info(name);
        if (ix == null || ix.Type != "CHTR") return "tec";
        var o = Tree(owner).Node(ix.Path) ?? TT.NodeAt(Base.Start, ix.Path);
        var c = Sc(Ch(o, "caste"));
        return c == "res" || c == "nat" || c == "tec" ? c : "tec";
    }

    /// <summary>filters of a unit class at a level (FightingObj.SetLevelFilter: "Filters/&lt;tribe&gt;/Upgrades/&lt;class&gt;/Lvl&lt;N&gt;...")</summary>
    public List<string> LevelFilters(string name, int level)
    {
        if (level <= 1) return new List<string>();
        var ix = Info(name);
        if (ix == null) return new List<string>();
        return Base.FiltersUnder($"{ix.Tribe}/Upgrades/{name}/Lvl{level}");
    }

    /// <summary>can a unit class exist at this level? (level 1 always; higher levels need their bonus filter)</summary>
    public bool HasLevel(string name, int level)
    {
        if (level == 1) return true;
        return LevelFilters(name, level).Any(p => p.Contains("_Bonus"));
    }

    /// <summary>the object node of a class at a level: player tree value + the unit's own "_Bonus" level filter</summary>
    public TtNode? LevelNode(string name, int level, IRulesOwner? owner)
    {
        var T = Tree(owner);
        // (a key without a string and no closure: these are asked many times a step per unit - the string keys
        // and the lambdas were a good part of the simulation's garbage)
        var mk = new TechTree.MemoKey(2, null, name, level);
        if (T.TryMemo(mk, out var mv)) return (TtNode?)mv!;
        return T.SetMemo(mk, LevelNode0(name, level, owner, T));
    }
    TtNode? LevelNode0(string name, int level, IRulesOwner? owner, TechTree T)
    {
        var ix = Info(name);
        if (ix == null) return null;
        var o = T.Node(ix.Path) ?? TT.NodeAt(Base.Start, ix.Path);
        if (o == null) return null;
        var bonus = LevelFilters(name, level).Where(p => p.Contains("_Bonus")).ToList();
        if (bonus.Count == 0) return o;
        // apply the bonus modificators that address this object to a private copy
        var parts = ix.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var root = new TtNode(); var n = root;
        for (int i = 0; i < parts.Length - 1; i++) { var c = new TtNode(); n[parts[i]] = c; n = c; }
        string last = parts[^1];
        n[last] = o.Clone();
        foreach (var p in bonus)
        {
            var f = Base.Filter(p);
            var mods = TT.ModsOf(f);
            foreach (var m in mods) if (m.Path.StartsWith(ix.Path + "/", StringComparison.Ordinal) || m.Path == ix.Path) TT.ApplyMod(root, m);
            // LevelBonusData (a full snapshot) fills values the modificators don't touch
            var lbd = Ch(f, "LevelBonusData");
            if (f != null && lbd != null && mods.Count == 0)
            {
                var tgt = n[last];
                if (tgt != null && tgt.IsObject && lbd.IsObject) foreach (var (k, v) in lbd.Entries()) tgt[k] = v.Clone();
            }
        }
        return n[last];
    }

    /// <summary>stats at a level (GetTechTreeHitpoints / UpdateFOW / GetScalpValue / GetResInvCap apply the tribe's /Modifications)</summary>
    public LevelStats? Stats(string name, int level, IRulesOwner? owner)
    {
        var T = Tree(owner);
        // (a key without a string and no closure: these are asked many times a step per unit - the string keys
        // and the lambdas were a good part of the simulation's garbage)
        var mk = new TechTree.MemoKey(3, null, name, level);
        if (T.TryMemo(mk, out var mv)) return (LevelStats?)mv!;
        return T.SetMemo(mk, Stats0(name, level, owner, T));
    }
    LevelStats? Stats0(string name, int level, IRulesOwner? owner, TechTree T)
    {
        var ix = Info(name);
        if (ix == null) return null;
        var o = LevelNode(name, level, owner);
        if (o == null) return null;
        bool exists = level == 1 ? N(Ch(o, "hitpoints")) > 1 : HasLevel(name, level);
        string typ = ix.Type, col = Column(name, owner);
        string mtribe = ix.Tribe == "Special" || ix.Tribe == "World" ? T.Tribe : ix.Tribe;
        double Mod(string stat, double v) => T.Modify(typ, stat, v, col, mtribe);
        var sa0 = Ch(o, "special_abilities");
        var sa = sa0 != null && sa0.IsObject ? sa0 : new TtNode();
        bool On(string k) { var x = sa[k]; if (x == null || !x.IsObject) return false; var en = Sc(x["enabled"]); return en != "false" && en != "0"; }
        HealAbility? heal = On("heal") ? new HealAbility { Radius = N(sa["heal"]!["radius"]), Amount = N(sa["heal"]!["amount"]), Mod = N(sa["heal"]!["mod"]) } : null;
        var lim = Ch(o, "UpdateLimits"); var inv = Ch(o, "ResInvCaps");
        // hit points: tribe/type modifier, then the per-class modifier /Modifications/<tribe>/<class>/Hitpoints (FO:3718)
        double hp = exists ? Mod("Hitpoints", N(Ch(o, "hitpoints"))) : 0;
        hp = hp * T.Num($"/Modifications/{mtribe}/{name}/Hitpoints/rel", 1) + T.Num($"/Modifications/{mtribe}/{name}/Hitpoints/abs", 0);
        double Cap(string r) => Math.Max(0, Mod("ResInv", N(Ch(inv, r), 5)));
        var abilities = new Dictionary<string, Ability>(StringComparer.Ordinal);
        foreach (var k in sa.Keys)
        {
            if (!On(k)) continue;
            var a = new Ability();
            foreach (var (kk, vv) in sa[k]!.Entries())
            {
                if (kk == "_value") continue;
                var s = vv.Scalar;
                a[kk] = s != null && TT.ParseFloat(s, out double x) && double.IsFinite(x) ? new AbilityValue(x, s) : new AbilityValue(null, s);
            }
            abilities[k] = a;
        }
        var capc = Sc(Ch(o, "captainclass"));
        return new LevelStats
        {
            Exists = exists, Level = level, Hp = hp,
            Fow = Mod("FOW", N(Ch(o, "FOW"), 25)),
            Speed = N(Ch(o, "defaultspeed")), MaxSpeed = N(Ch(o, "maxspeed")),
            Tf = N(Ch(o, "timefactor"), 1), Scalps = TT.JsRound(Mod("Skulls", N(Ch(o, "scalps"), 5))),
            Gfx = Sc(Ch(o, "gfx")), Size = N(Ch(o, "unit_size")), CaptainLevel = N(Ch(o, "captainlevel")),
            Aggressive = N(Ch(o, "aggressive")),
            Carry = new ResAmounts { Food = Cap("food"), Wood = Cap("wood"), Stone = Cap("stone") },
            Limits = new Limits { MaxUnits = N(Ch(lim, "max_units")), MaxFood = N(Ch(lim, "max_food")), MaxWood = N(Ch(lim, "max_wood")), MaxStone = N(Ch(lim, "max_stone")) },
            Heal = heal, HealModRel = T.Modifier(typ, "Healing", true, col, mtribe), HealModAbs = T.Modifier(typ, "Healing", false, col, mtribe),
            Abilities = abilities, MaxWorkers = N(Ch(o, "maxworkers")), Agility = N(Ch(o, "agility")),
            MaxPassengers = N(Ch(o, "max_passengers")),
            Captain = !string.IsNullOrEmpty(capc) && capc != "0" ? capc : null,
        };
    }

    // ---------------------------------------------------------------- weapons
    /// <summary>
    /// The clips the weapons play to attack (their Animations; standing / idle clips left out): many are not named
    /// "attack..." - the SEAS flamethrower fires with "seas_flamethrower_0", the gunner with "seas_gunner_0", the
    /// marksman with "marksman_gatling". The view sprays a weapon's fire and muzzle flash while one of them plays.
    /// </summary>
    public HashSet<string> WeaponClips()
    {
        var r = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var u in _usersOrder)
            foreach (var p in _users[u])
                if (WeaponAt(p, null) is Weapon w)
                    foreach (var a in w.Anims)
                        if (!string.IsNullOrEmpty(a.Name) && !System.Text.RegularExpressions.Regex.IsMatch(a.Name, "stand|idle|walk|unused", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                            r.Add(a.Name);
        return r;
    }

    /// <summary>raw weapon data (tech tree values, no modifiers)</summary>
    public Weapon? WeaponAt(string path, IRulesOwner? owner)
    {
        var T = Tree(owner);
        // (a key without a string and no closure: these are asked many times a step per unit - the string keys
        // and the lambdas were a good part of the simulation's garbage)
        var mk = new TechTree.MemoKey(4, null, path, 0);
        if (T.TryMemo(mk, out var mv)) return (Weapon?)mv!;
        return T.SetMemo(mk, WeaponAt0(path, owner, T));
    }
    Weapon? WeaponAt0(string path, IRulesOwner? owner, TechTree T)
    {
        var w = T.Node(path) ?? TT.NodeAt(Base.Start, path);
        if (w == null || !w.IsObject) return null;
        Dictionary<string, double> Bonus(TtNode? n)
        {
            var res = new Dictionary<string, double>(StringComparer.Ordinal);
            var t = Ch(n, "Type");
            if (t != null && t.IsObject) foreach (var (k, v) in t.Entries()) if (k != "_value") res[k] = N(v);
            var c = Ch(n, "Class");
            if (c != null && c.IsObject) foreach (var (k, v) in c.Entries()) if (k != "_value") res[k] = (res.TryGetValue(k, out var x) ? x : 0) + N(v);
            return res;
        }
        var proj = Strs(w["Projectile"]);
        var wp = new Weapon
        {
            Id = path.Substring(path.LastIndexOf('/') + 1), Path = path, Level = N(w["level"]), Secondary = N(w["secondary"]), Slot = N(w["slot"]),
            Dmg = N(w["damage"]), MinDmg = N(w["mindamage"]), EndDmg = N(w["enddamage"]), Range = N(w["range"]),
            MinRange = N(w["minattackrange"]), Freq = N(w["frequency"]), Splash = N(w["hitrange"]),
            Def = N(w["defense"]), RDef = N(w["rangeddefense"]), Ap = N(w["armorpiercing"]),
            Poison = N(w["poison_damage"]), PoisonTicks = N(w["poison_tick_count"]), BulletSpeed = N(w["bulletspeed"]),
            Falloff = N(w["bulletfalloff"]), Jitter = N(w["jitter"]), Size = N(w["unit_size"]), Caste = Sc(w["caste"]),
            Penetration = TT.Bool(Sc(w["penetration"])), PenAngle = N(w["penetration_angle"], 30), Fx = Sc(w["fx_class"]),
            Projectile = proj.Count > 0 && proj[0] != "" ? proj[0] : null, Bonus = Bonus(w["AttackBonus"]), DefBonus = Bonus(w["DefenseBonus"]),
        };
        foreach (var a in List(w["Animations"]))
            if (a.IsObject) wp.Anims.Add(new WeaponAnim(a.Scalar, N(a["delay"]), N(a["shootdelay"]), N(a["combo"]), NullIfEmpty(Sc(a["followanim"]))));
        // a part without Links goes to the right hand (character.usl: "Default Value in TTEditor is HndR" - the SEAS
        // flamethrower's flamer has none and was never drawn)
        foreach (var p in List(w["Parts"]))
            if (p.IsObject) { var lk = Sc(p["Links"]); wp.Parts.Add(new WeaponPart(Strs(p["Gfx"]), string.IsNullOrEmpty(lk) ? "HndR" : lk, p.Scalar)); }
        return wp;
    }

    /// <summary>
    /// WeaponMgr.GetStandardWeapons + GetBestWeapon (WeaponMgr.usl:226-359):
    ///   candidates: weapons whose Users list the class and whose level &lt;= the unit's level (a weapon with all values 0 is ignored)
    ///   primary ("long")  = best non-secondary weapon by NoBoni value (ties keep the first)
    ///   short             = best secondary weapon with range &lt; 1   (e.g. a spearman's dagger when enemies get too close)
    ///   medium            = best secondary weapon with range &gt;= 1
    /// </summary>
    public WeaponSet WeaponSetOf(string name, int level, IRulesOwner? owner)
    {
        var T = Tree(owner);
        // (a key without a string and no closure: these are asked many times a step per unit - the string keys
        // and the lambdas were a good part of the simulation's garbage)
        var mk = new TechTree.MemoKey(5, null, name, level);
        if (T.TryMemo(mk, out var mv)) return (WeaponSet)mv!;
        return T.SetMemo(mk, WeaponSetOf0(name, level, owner, T));
    }
    WeaponSet WeaponSetOf0(string name, int level, IRulesOwner? owner, TechTree T)
    {
        var set = new WeaponSet();
        var cand = WeaponPaths(name, owner).Select(p => WeaponAt(p, owner)).Where(w => w != null && !(w.Dmg == 0 && w.Def == 0 && w.RDef == 0 && w.Level == 0 && w.Range == 0)).Cast<Weapon>().ToList();
        set.All = cand;
        var avail = cand.Where(w => (w.Level != 0 ? w.Level : 1) <= level).ToList();
        static double NoBoni(Weapon w) => (w.Dmg + w.Def + w.RDef) * (1 + (w.Range - w.MinRange) * 0.1) * (60 / Math.Max(w.Freq, 60)) * (1 + (w.Splash + w.EndDmg) * 0.1);
        Weapon? Best(Func<Weapon, bool> f)
        {
            Weapon? b = null; double bv = -1;
            foreach (var w in avail) { if (!f(w)) continue; double v = NoBoni(w); if (v > bv) { bv = v; b = w; } }
            return b;
        }
        set.Long = Best(w => w.Secondary == 0);
        set.Short = Best(w => w.Secondary != 0 && w.Range < 1);
        set.Medium = Best(w => w.Secondary != 0 && w.Range >= 1);
        set.Long ??= set.Medium ?? set.Short;
        return set;
    }

    /// <summary>
    /// weapons whose "Users" list names this class. Filters can move a class to other weapons (e.g.
    /// aje_ankylosaurus_catapult removes the unit from aje_ankylosaurus_weapon_b..e and adds it to the catapult weapons),
    /// so the lists are read from the owner's current tree; the start tree's index is the fallback.
    /// </summary>
    public List<string> WeaponPaths(string name, IRulesOwner? owner)
    {
        var T = Tree(owner);
        var map = T.Memo("weaponUsers", () =>
        {
            var m = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            void Add(string u, string p) { if (!m.TryGetValue(u, out var l)) m[u] = l = new List<string>(); l.Add(p); }
            var O = T.Node("/Objects") ?? new TtNode();
            // a tree only holds its own tribe, "Special" and "World": other tribes' weapons come from the start tree
            foreach (var u in _usersOrder) foreach (var p in _users[u]) if (Ch(O, p.Split('/')[2]) == null) Add(u, p);
            foreach (var (tribe, tn) in O.Entries())
            {
                var W = Ch(tn, "Weapons");
                if (W == null || !W.IsObject) continue;
                foreach (var (w, node) in W.Entries())
                {
                    if (!node.IsObject) continue;
                    foreach (var u in Strs(node["Users"])) Add(u, $"/Objects/{tribe}/Weapons/{w}");
                }
            }
            return m;
        });
        return map.TryGetValue(name, out var l) ? l : new List<string>();
    }

    /// <summary>
    /// a weapon's values for a unit class after the tribe's modifiers (FightingObj.UpdateWeapons FO:7420):
    ///   dmg   = (damage * ranged_damage for projectile weapons) * Damage       (rel/abs)
    ///   prot  = clamp(defense * Defence, 0, 99)    rprot = clamp(rangeddefense * RangedDefence, 0, 99)
    ///   range = range * Range                      dur   = 60 / frequency * WeaponDuration   (seconds between attacks)
    /// </summary>
    public WeaponStats? WeaponStatsOf(Weapon? w, string name, IRulesOwner? owner)
    {
        if (w == null) return null;
        var T = Tree(owner);
        // (a key without a string and no closure: these are asked many times a step per unit - the string keys
        // and the lambdas were a good part of the simulation's garbage)
        var mk = new TechTree.MemoKey(6, w.Path, name, 0);          // (by the path, as before: not the object)
        if (T.TryMemo(mk, out var mv)) return (WeaponStats?)mv!;
        return T.SetMemo(mk, WeaponStatsOf0(w, name, owner, T));
    }
    WeaponStats? WeaponStatsOf0(Weapon w, string name, IRulesOwner? owner, TechTree T)
    {
        var ix = Info(name) ?? new IndexEntry { Type = "CHTR", Tribe = T.Tribe };
        var col = Column(name, owner);
        var tribe = ix.Tribe == "Special" || ix.Tribe == "World" ? T.Tribe : ix.Tribe;
        double M(string stat, double v) => T.Modify(ix.Type, stat, v, col, tribe);
        double dmg = w.Dmg;
        if (w.Projectile != null) dmg = M("ranged_damage", dmg);
        dmg = M("Damage", dmg);
        return new WeaponStats
        {
            W = w, Dmg = dmg, EndDmg = w.EndDmg, Ap = w.Ap, Splash = w.Splash,
            Prot = Clamp(M("Defence", w.Def), 0, 99), RProt = Clamp(M("RangedDefence", w.RDef), 0, 99),
            Range = M("Range", w.Range), MinRange = w.MinRange,
            Dur = M("WeaponDuration", 60 / (w.Freq != 0 ? w.Freq : 1)),
            Poison = w.Poison, PoisonTicks = w.PoisonTicks,
        };
    }

    /// <summary>victim's "Defence_&lt;attackerType&gt;" modifier (used with DefenseBonus in TakeDmg): (rel, abs)</summary>
    public (double Rel, double Abs) DefenceVs(string name, IRulesOwner? owner, string attackerType)
    {
        var T = Tree(owner);
        var ix = Info(name) ?? new IndexEntry { Type = "CHTR" };
        var col = Column(name, owner);
        return (T.Modifier(ix.Type, "Defence_" + attackerType, true, col), T.Modifier(ix.Type, "Defence_" + attackerType, false, col));
    }

    // ---------------------------------------------------------------- actions
    /// <summary>every action of the tribe: { id, kind: Build|Upgrades|Moves, cat, path, time, cost, req, breq, locs, results, visible, disabled }</summary>
    public List<ActionDef> Actions(IRulesOwner? owner)
    {
        var T = Tree(owner);
        return T.Memo("actions", () => ParseActions(T.Tribe, tribe => T.Node($"/Actions/{tribe}")));
    }

    /// <summary>the actions of another tribe, from the unmodified tech tree (a player's tree only holds its own tribe)</summary>
    public List<ActionDef> ForeignActions(IRulesOwner? owner, string tribe) =>
        Tree(owner).Memo("foreign|" + tribe, () => ParseActions(tribe, t => TT.NodeAt(Base.Start, $"/Actions/{t}"), false));

    public List<ActionDef> ParseActions(string tribe0, Func<string, TtNode?> node, bool special = true)
    {
        var res = new List<ActionDef>();
        foreach (var tribe in special ? new[] { tribe0, "Special" } : new[] { tribe0 })
        {
            var A = node(tribe);
            if (A == null) continue;
            foreach (var kind in new[] { "Build", "Upgrades", "Moves" })
            {
                var K = Ch(A, kind);
                if (K == null || !K.IsObject) continue;
                foreach (var (cat, C) in K.Entries())
                {
                    if (!C.IsObject) continue;
                    foreach (var (id, a) in C.Entries())
                    {
                        if (!a.IsObject || !Truthy(a["locations"])) continue;
                        // (a node that only a filter made: MIRAGE's xenage/flying_disabled adds to the menu position of
                        // "ninigi_ninigi_helicopter" - a misspelt name - and so creates an action of nothing but a location)
                        if (a.Keys.All(k => k == "locations" || k == "_value")) continue;
                        res.Add(ParseAction(tribe, kind, cat, id, a));
                    }
                }
            }
        }
        return res;
    }

    public ActionDef ParseAction(string tribe, string kind, string cat, string id, TtNode a)
    {
        var c = Ch(a, "conditions");
        var rc = Ch(c, "rescosts");
        // visibility: filters "add" to it (MIRAGE counts: -1 + age_5 + epoch_six_enabled = 1); the server's
        // RequirementsMgr allows an action whose visibility is > 0, the command bar shows one whose visibility is 1
        // (MIRAGE CommandBar.usl GetSubValueI("visibility", 1) != 1: skip). A negative value is "hidden until enough
        // filters added to it" - the SEAS' VHCL/seas_scorpion (-1, final_stage adds -2) is never shown: the Scorpion
        // comes from the launch pad BLDG/seas_final_boss (CSeasScorpionLaunchpad).
        var d = new ActionDef
        {
            Id = id, Tribe = tribe, Kind = kind, Cat = kind + "/" + cat, Type = cat, Path = $"/Actions/{tribe}/{kind}/{cat}/{id}",
            Time = N(a["duration"]), Visible = N(a["visibility"], 1) > 0, Disabled = TT.Bool(Sc(a["disabled"])), Remake = TT.Bool(Sc(a["remake"])),
            LimitCheck = N(a["check"]) == 1, Product = NullIfEmpty(Sc(a["product_name"])), ProductTribe = NullIfEmpty(Sc(a["product_tribe"])),
            Cost = new Cost { Food = N(Ch(rc, "food")), Wood = N(Ch(rc, "wood")), Stone = N(Ch(rc, "stone")), Skulls = N(Ch(rc, "iron")), Power = N(Ch(rc, "power")) },
            Req = Strs(Ch(c, "inventobjects")), BReq = Strs(Ch(c, "buildobjects")), LevelReq = N(Ch(c, "level"), 0), CasteReq = Strs(Ch(c, "caste")),
            Tribes = Strs(Ch(c, "tribe")),
        };
        foreach (var l in List(a["locations"]))
        {
            // (a location given as a plain string is {_value: l}; its other fields are undefined)
            var up = Ch(l, "uiposition");
            var lf = Ch(l, "localflags");
            d.Locs.Add(new ActionLoc
            {
                At = LastPart(l.Scalar ?? ""), Ui = Truthy(up) ? up : null, Icon = Sc(Ch(l, "iconpath")), Desc = Sc(Ch(l, "description")),
                Hidden = Truthy(up) && Sc(Ch(up, "menupos")) == "-1",      // menupos -1: not in any menu
                LocalFlags = lf != null && lf.IsObject ? lf.Keys.Where(k => k != "_value").ToList() : null,
            });
        }
        foreach (var r in List(a["results"]))
        {
            if (!r.IsObject) continue;
            var fl = r["flags"];
            var cls = Sc(r["class"]);
            d.Results.Add(new ActionResult
            {
                Obj = LastPart(r.Scalar ?? ""), Path = r.Scalar, Type = Sc(r["type"]), Cls = string.IsNullOrEmpty(cls) ? "player" : cls,
                Level = Truthy(fl) ? N(Ch(fl, "level"), 0) : 0, Rider = Sc(r["rider"]), ResultActions = Strs(r["resultactions"]),
            });
        }
        var sc = a["secondarycontroller"];
        List<string>? target = sc != null && sc.IsObject ? Strs(sc)
            : !string.IsNullOrEmpty(Sc(sc)) && Sc(sc) != "0" ? TargetSplit.Split(Sc(sc)!).Where(s => s.Length > 0).ToList() : null;
        d.Target = target != null && target.Count > 0 ? target : null;
        var so = a["secondarycontrollerowner"];
        d.TargetOwner = so != null && so.IsObject ? Strs(so) : !string.IsNullOrEmpty(Sc(so)) ? new List<string> { Sc(so)! } : null;
        return d;
    }
    static string LastPart(string s) { int i = s.LastIndexOf('/'); return i < 0 ? s : s.Substring(i + 1); }

    /// <summary>actions offered at a location class, each with its location entry as .Loc</summary>
    public List<ActionDef> ActionsAt(IRulesOwner? owner, string at)
    {
        var T = Tree(owner);
        // (a key without a string and no closure: these are asked many times a step per unit - the string keys
        // and the lambdas were a good part of the simulation's garbage)
        var mk = new TechTree.MemoKey(7, null, at, 0);
        if (T.TryMemo(mk, out var mv)) return (List<ActionDef>)mv!;
        return T.SetMemo(mk, ActionsAt0(owner, at, T));
    }
    List<ActionDef> ActionsAt0(IRulesOwner? owner, string at, TechTree T)
    {
        var res = new List<ActionDef>();
        foreach (var a in Actions(owner)) foreach (var l in a.Locs) if (l.At == at && !l.Hidden) res.Add(a.WithLoc(l));
        return res;
    }

    /// <summary>
    /// actions an entity offers: its own class, plus the type-wide ones (location "/Objects/&lt;tribe&gt;/CHTR" etc.).
    /// name: the entity's class, kind: "unit" | "building", type: its def type (CHTR ...) or its cls.
    /// </summary>
    public List<ActionDef> ActionsOf(IRulesOwner? owner, string name, string kind, string? type)
    {
        var T = Tree(owner);
        // (a key without a string and no closure: these are asked many times a step per unit - the string keys
        // and the lambdas were a good part of the simulation's garbage)
        var mk = new TechTree.MemoKey(8, null, name, 0);
        if (T.TryMemo(mk, out var mv)) return (List<ActionDef>)mv!;
        return T.SetMemo(mk, ActionsOf0(owner, name, kind, type, T));
    }
    List<ActionDef> ActionsOf0(IRulesOwner? owner, string name, string kind, string? type, TechTree T)
    {
        var byClass = ActionsAt(owner, name);
        // a building of another tribe that the player was given in a mission played with another tribe
        // (campaign/tribes.js: owner.foreignActions) offers its own tribe's actions - the SEAS hire heroes in the
        // Norsemen's tavern
        var ix = Info(name);
        if (owner != null && owner.ForeignActions && ix != null && TT.TRIBES.Contains(ix.Tribe) && ix.Tribe != T.Tribe && kind == "building")
        {
            byClass = new List<ActionDef>(byClass);
            foreach (var a in ForeignActions(owner, ix.Tribe)) foreach (var l in a.Locs) if (l.At == name && !l.Hidden) byClass.Add(a.WithLoc(l));
        }
        var byType = type != null ? ActionsAt(owner, type) : new List<ActionDef>();
        var seen = new HashSet<string>(byClass.Select(a => a.Path), StringComparer.Ordinal);
        var res = new List<ActionDef>(byClass);
        foreach (var a in byType) if (!seen.Contains(a.Path)) res.Add(a);
        return res;
    }

    static readonly Regex AgeId = new(@"^age_\d$", RegexOptions.Compiled);

    /// <summary>RequirementsMgr: null = ok, else "hidden" | "disabled" | "req" | "level". unit: the acting unit or
    /// building (its level and local inventions), null for a player-wide action.</summary>
    public string? Check(IRulesOwner? owner, ActionDef a, IActionUser? unit = null)
    {
        if (!a.Visible) return "hidden";
        if (a.Disabled) return "disabled";
        // debug option "highest epoch" (game/cheats.js): the epoch upgrades above it do not exist
        // (MIRAGE's sixth stage "final_stage" is epoch 6: StartLocation.usl hides it with the AntiActions of a MaxEpoch below 6)
        if (owner != null && owner.MaxEpoch > 0 && a.Kind == "Upgrades" && (AgeId.IsMatch(a.Id) ? a.Id[4] - '0' : a.Id == "final_stage" ? 6 : 0) > owner.MaxEpoch) return "hidden";
        var T = Tree(owner);
        var t = a.Tribe == "Special" ? T.Tribe : a.Tribe;
        foreach (var r in a.Req) if (!Invented(owner, r, t, unit)) return "req";
        // debug mode (player.js): no buildings needed either, e.g. the Dragon Clan epoch upgrade (lumber mill + dojo)
        if (owner == null || !owner.Debug) foreach (var r in a.BReq) if (!T.Built(r, t)) return "req";
        if (unit != null && a.LevelReq != 0 && unit.Level < a.LevelReq) return "level";
        return null;
    }

    /// <summary>invention check; "local" upgrades (e.g. Ninigi Explode, Aje farm modes) live on the building itself</summary>
    public bool Invented(IRulesOwner? owner, string name, string? tribe = null, IActionUser? unit = null)
    {
        if (unit?.LocalInvents != null && unit.LocalInvents.Contains(name)) return true;
        return Tree(owner).Invented(name, tribe);
    }

    public List<string> Missing(IRulesOwner? owner, ActionDef a, IActionUser? unit = null)
    {
        var T = Tree(owner);
        var t = a.Tribe == "Special" ? T.Tribe : a.Tribe;
        var res = a.Req.Where(r => !Invented(owner, r, t, unit)).ToList();
        if (owner == null || !owner.Debug) res.AddRange(a.BReq.Where(r => !T.Built(r, t)));
        return res;
    }

    public int StartLevel(ActionDef action)
    {
        int lv = 1;
        foreach (var r in action.Results) if (r.Level != 0) lv = Math.Max(lv, (int)r.Level);
        // the user's choice (2026-10-23): with MIRAGE both Executioners start at level 4 (MIRAGE's tech tree builds them at
        // 3; its own xenage/old_school_stats filter, which would make it 4, is never switched on). The boss variant
        // (seas_lumberjack_minigun_boss, level 5) keeps its level.
        if (action.Kind == "Build" && Extra.Mod == "MIRAGE" && LEVEL_OVERRIDE_MIRAGE.TryGetValue(action.Id, out int ov)) lv = ov;
        return lv;
    }

    /// <summary>the lowest level of a class; a level the tech tree lists without hit points (1: the vehicles below their
    /// crew's level, e.g. seas_wehrspinne 2, hu_steam_tank 2-3 - their build actions make them at 3 / 4) is no real level</summary>
    public int MinLevel(string name, IRulesOwner? owner)
    {
        int first = 0;
        for (int l = 1; l <= 5; l++) { var s = Stats(name, l, owner); if (s != null && s.Exists) { if (first == 0) first = l; if (!(s.Hp <= 1)) return l; } }
        return first != 0 ? first : 1;
    }

    /// <summary>nearest existing level's stats (the game never asks for a level the class doesn't have, but be forgiving)</summary>
    public LevelStats? StatsAt(string name, int level, IRulesOwner? owner)
    {
        for (int l = level; l >= 1; l--) { var s = Stats(name, l, owner); if (s != null && s.Exists) return s; }
        for (int l = level; l <= 5; l++) { var s = Stats(name, l, owner); if (s != null && s.Exists) return s; }
        return Stats(name, 1, owner);
    }

    public double MiscValue(IRulesOwner? owner, string key, double d) { var T = Tree(owner); return T.Num($"/MiscValues/{T.Tribe}/{key}", d); }

    // ---------------------------------------------------------------- misc
    /// <summary>the texts of a class as the original finds them (HelpMenu.GetObjHelpitemKey): the help item of its tech-tree
    /// description key (the heroes: lovelace_s0 -> "_lovelace"), else of its class name.</summary>
    public TextEntry Text(string name, IRulesOwner? owner = null)
    {
        if (SameAs.ContainsKey(name) && Texts.TryGetValue(name, out var own)) return own;
        var d = Exists(name) ? Def(name, owner) : null;
        return (d != null && !string.IsNullOrEmpty(d.Desc) ? TextKey(d.Desc) : null) ?? TextKey(name) ?? new TextEntry { Name = Prettify(name) };
    }

    /// <summary>the help item of a key as the tech tree and the scripts spell it ("_hu_drums", "cb_walk"; any case), null when none</summary>
    public TextEntry? TextKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return null;
        var k = key.StartsWith('_') ? key.Substring(1) : key;
        return Texts.TryGetValue(k, out var t) ? t : Texts.TryGetValue(k.ToLowerInvariant(), out t) ? t : null;
    }

    /// <summary>the help item of an action: its location's description key (the tooltip's), else its result's or its own name</summary>
    public TextEntry ActionText(ActionDef a)
    {
        var r = a.Kind == "Build" && a.Results.Count > 0 ? a.Results[0].Obj : null;
        if (!string.IsNullOrEmpty(r) && SameAs.ContainsKey(r) && Texts.TryGetValue(r, out var own)) return own;
        var l = a.Loc ?? (a.Locs.Count > 0 ? a.Locs[0] : null);
        return (l != null ? TextKey(l.Desc) : null) ?? Text(!string.IsNullOrEmpty(r) ? r : a.Id);
    }

    /// <summary>a string of the game's string tables (main_uk.ltf: "_ObjCommandBar_InventionsNeeded" ...)</summary>
    public string Ui(string key, string? d = null) => Extra.Ui.TryGetValue(key, out var v) ? v : d ?? key;
    public List<IdleEntry>? IdleSet(string name) => Extra.Idle.TryGetValue(name.ToLowerInvariant(), out var l) ? l : null;
    public StartDef? StartOf(string tribe) => Extra.Start.TryGetValue(tribe, out var s) ? s : null;
    public IReadOnlyList<string> Tribes() => TT.TRIBES;

    static readonly Regex PrefixRe = new("^(seas|aje|hu|ninigi)_", RegexOptions.Compiled);
    static readonly Regex SuffixRe = new(@"_s\d$", RegexOptions.Compiled);
    static readonly Regex WordRe = new(@"\b\w", RegexOptions.Compiled | RegexOptions.ECMAScript);
    public static string Prettify(string n)
    {
        var s = SuffixRe.Replace(PrefixRe.Replace(n, ""), "").Replace('_', ' ');
        return WordRe.Replace(s, m => m.Value.ToUpperInvariant());
    }
}

/// <summary>What Rules.Check needs of the acting unit or building: its level and its own ("local") inventions.</summary>
public interface IActionUser
{
    int Level { get; }
    ISet<string>? LocalInvents { get; }
}
