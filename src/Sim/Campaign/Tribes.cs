// Playing a campaign mission with another tribe than the one it was made for (campaign menu "Your tribe").
//
// Every object the player owns - placed on the map, in the start army, created for the player by a trigger or given
// to the player later (OCPY) - becomes the chosen tribe's object of the same role; heroes and the Special tribe stay
// as they are. Conditions and queries that name a class of the mission's tribe accept the equivalent class for the
// player's objects (Registry.ClassIs), and the tech tree filters of the level and of TECH actions are translated
// path by path (Filter()).
//
// Which class stands in for which is chosen among everything the chosen tribe can make in the game data of the mod
// configuration (its Build actions: a mod's new buildings and units take part - MIRAGE's SEAS have tents, a pub, a
// wall, spearmen ...), by closeness (Profile(), Distance()): what the object is (building, character, animal,
// vehicle, ship), what it does (wall piece, gate, harbour, worker, healer, transport, population, which units it
// makes, which goods it takes in, whether it is armed, how far it shoots), how big, sturdy, dear and late it is, and
// its name (aje_tent and seas_small_tent are both tents). ROLES below is the hand-made table of the base game's
// roles (from its build menus and the script classes CFireplace, CRallyBuilding, CWall ...): its entry is preferred
// a little, and a role it leaves empty (null) stays empty unless something fits well. An objective that asks for a
// class the chosen tribe has nothing for, or only in a later epoch, is met at once (Missing()).
//
// Ported from remake/src/game/campaign/tribes.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Content;
using PwRemake.Sim.Rules;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Campaign;

public sealed class TribeSwap
{
    // classes that stay what they are whatever the tribe: Taslow's towers (the Amazon water temple, single_07: the
    // player's towers at the start, which its triggers count by class - the user: "they shouldn't change")
    static readonly HashSet<string> KEEP = new(StringComparer.Ordinal) { "aje_tesla_tower", "aje_tesla_tower_canon" };

    //                     role                     Hu                          Aje                           Ninigi                           SEAS
    public static readonly string?[][] ROLES =
    {
        // buildings (the workers' build menu)
        new[] { "main", "hu_fireplace", "aje_resource_collector", "ninigi_fireplace", "seas_headquarters" },
        new[] { "house", "hu_stone_cottage", "aje_tent", null, "seas_barracks" },            // population: SEAS barracks (+25), Ninigi fireplace
        new[] { "barracks", "hu_arena", "aje_rodeo", "ninigi_dojo", "seas_barracks" },
        new[] { "animal farm", "hu_small_animal_farm", "aje_small_farm", "ninigi_animal_farm", "seas_garage" },
        new[] { "smith", "hu_weapons_smith", "aje_weapons_builder", "ninigi_weapon_maker", "seas_barracks" },
        new[] { "machines", "hu_machine_maker", null, "ninigi_engineer", "seas_garage" },
        new[] { "harbour", "hu_harbour", "aje_floating_harbour", "ninigi_harbour", "seas_carrier" },
        new[] { "lumber", "hu_lumberjack_cottage", null, "ninigi_lumbermill", "seas_steelwork" },
        new[] { "quarry", "hu_stone_quarry", null, "ninigi_stone_quarry", "seas_steelwork" },
        new[] { "hunting", null, null, "ninigi_hunting_lodge", "seas_steelwork" },
        new[] { "warehouse", "hu_warehouse", "aje_bazaar", "ninigi_warehouse", "seas_steelwork" },
        new[] { "field", "hu_corn_field", "aje_slaughterhouse", "ninigi_paddy", "seas_greenhouse" },
        new[] { "tavern", "hu_tavern", "aje_cook_house", "ninigi_teahouse", null },
        new[] { "temple", "hu_temple", "aje_temple", "ninigi_temple", null },
        new[] { "warpgate", "hu_warpgate", "aje_warpgate", "ninigi_warpgate", null },
        new[] { "small tower", "hu_small_tower", "aje_small_tower", "ninigi_small_tower", "seas_turret_tower" },
        new[] { "large tower", "hu_large_tower", "aje_medium_tower", "ninigi_telescope_tower", "seas_turret_tower" },
        new[] { "palisade", "hu_palisade", "aje_bone_palisade", "ninigi_palisade", "seas_fence" },
        new[] { "palisade gate", "hu_palisade_gate", "aje_bone_palisade_gate", "ninigi_palisade_gate", "seas_gate" },
        new[] { "wall", "hu_small_wall", "aje_clay_wall", "ninigi_defense_skewer", "seas_fence" },
        new[] { "wall gate", "hu_small_wall_gate", "aje_clay_wall_gate", "ninigi_defense_skewer_gate", "seas_gate" },
        new[] { "strong wall", "hu_re_enforced_wall", "aje_clay_wall", "ninigi_defense_skewer", "seas_fence" },
        new[] { "strong wall gate", "hu_re_enforced_wall_gate", "aje_clay_wall_gate", "ninigi_defense_skewer_gate", "seas_gate" },
        new[] { "rally point", "hu_rally_point", "aje_rally_point", "ninigi_rally_point", "seas_rally_point" },
        new[] { "harbour rally point", "hu_rally_point_harbour", "aje_rally_point_harbour", "ninigi_rally_point_harbour", "seas_rally_point_harbour" },
        // the defence building that breeds guards (the build menus' DEFE entry 7-9: the Dragon Clan's dilophosaurus nest,
        // the Norse kennel, the Dustriders' skull protector, MIRAGE's SEAS stygimoloch nest; the base game's SEAS have
        // none: the closest of their buildings). Mission 13 counts 8 dilophosaurus nests at the gate (L13SQ06): "nest"
        // says too little by name (GENERIC) for the closeness to pick these, so the swapped tribe's nests never counted.
        // ALSO: the conditions count further classes of the chosen tribe for the role
        new[] { "breeding defence", "hu_kennel", "aje_skull_protector", "ninigi_dilophosaurus_nest", "seas_stygimoloch_nest" },
        // characters: main building, barracks (menu positions 0-4), smith and temple
        new[] { "worker", "hu_worker", "aje_worker", "ninigi_worker", "seas_worker" },
        new[] { "warrior", "hu_warrior", "aje_warrior", "ninigi_warrior", "seas_warrior" },
        new[] { "spearman", "hu_spearman", "aje_spearman", "ninigi_spearman", "seas_gunner" },
        new[] { "archer", "hu_archer", "aje_archer", "ninigi_archer", "seas_marksman" },
        new[] { "marksman", "hu_marksman", "aje_thrower", "ninigi_marksman", "seas_marksman" },
        new[] { "pikeman", "hu_pikeman", "aje_rammer", "ninigi_icespearman", "seas_rocketman" },
        new[] { "berserker", "hu_berserker", "aje_assassin", "ninigi_sumo", "seas_gunner" },
        new[] { "killer", "hu_killer", "aje_poisoner", "ninigi_ninja", "seas_rocketman" },
        new[] { "flamethrower", "hu_flamethrower", "aje_flamethrower", "ninigi_flamethrower", "seas_flamethrower" },
        new[] { "priest", "hu_druid", "aje_shaman", "ninigi_monk", "seas_medic" },
        // animals (the animal farm's menu) and vehicles
        new[] { "scout", "hu_scout", "aje_tracker_dino", "ninigi_scout", "seas_mechanical_walker" },
        new[] { "archer carrier", "hu_chariot", "aje_triceratops_archer", "ninigi_saltasaurus_archer", "seas_triceratops_transporter" },   // captain class: the archer
        new[] { "ranged animal", "hu_kentrosaurus", "aje_dilophosaurus", "ninigi_saltasaurus_archer", "seas_mobile_suit" },
        new[] { "charger", "hu_wild_boar", "aje_ankylosaurus", "ninigi_baryonyx", "seas_triceratops" },
        new[] { "medium animal", "hu_rhino", "aje_stegosaurus", "ninigi_parasaurolophus_drums", "seas_triceratops" },
        new[] { "animal transport", "hu_rhino_transporter", "aje_triceratops_archer", "ninigi_saltasaurus_archer", "seas_triceratops_transporter" },
        new[] { "siege animal", "hu_rhino_ballista", "aje_ankylosaurus", "ninigi_parasaurolophus_gatling", "seas_mobile_suit_flamethrower" },
        new[] { "heavy animal", "hu_mammoth", "aje_brachiosaurus", "ninigi_seismosaurus", "seas_wehrspinne" },
        new[] { "big hunter", "hu_triceratops", "aje_allosaurus", "ninigi_seismosaurus", "seas_wehrspinne" },
        new[] { "sabre-tooth", "hu_eusmilus", "aje_eusmilus", "ninigi_eusmilus", "seas_triceratops" },
        new[] { "cart", "hu_cart", "aje_trade_dino", "ninigi_cart", "seas_triceratops_resource_collector" },
        new[] { "ram", "hu_steam_ram", "aje_ankylosaurus", "ninigi_siegetower", "seas_mobile_suit" },
        new[] { "tank", "hu_steam_tank", "aje_atroxosaurus", "ninigi_firecannon", "seas_mobile_suit" },
        // ships (the harbour's menu; SEAS: the carrier's)
        new[] { "fishing boat", "hu_fishing_boat", "aje_catamaran", "ninigi_fishing_boat", null },
        new[] { "transport ship", "hu_transport_ship", "aje_transport_turtle", "ninigi_transport_boat", "seas_hovercraft" },
        new[] { "warship", "hu_dragon_boat", "aje_cronosaurus", "ninigi_fire_boat", "seas_submarine" },
        new[] { "heavy warship", "hu_steam_boat", "aje_torpedo_turtle", "ninigi_rocket_boat", "seas_submarine" },
        new[] { "ram ship", "hu_ram_ship", "aje_torpedo_turtle", "ninigi_corsair", "seas_submarine" },
    };
    public static readonly string[] TRIBES = { "Hu", "Aje", "Ninigi", "SEAS" };
    /// <summary>more classes of the chosen tribe that a condition naming a role's class counts besides the equivalent
    /// (the player's objects still become the equivalent): equivalent (lower case) -> the others. A role a tribe fills
    /// with two buildings: MIRAGE's SEAS defend with the stygimoloch nest and the machine-gun nest, MIRAGE's Dustriders
    /// can breed gallimimi in a nest (a hero's invention) besides the skull protector</summary>
    public static readonly Dictionary<string, string[]> ALSO = new(StringComparer.Ordinal)
    {
        ["seas_stygimoloch_nest"] = new[] { "seas_mg_nest" },
        ["aje_skull_protector"] = new[] { "aje_gallimimus_nest" },
    };
    /// <summary>a mod's classes that play a role of the table whose class the mod has not (they are found for the
    /// role by closeness, but the way back needs the row): MIRAGE's SEAS cart. MIRAGE's SEAS missions (22: the trade
    /// dino reaching a region) asked the Norsemen and the Dragon Clan for a class they had nothing for</summary>
    static readonly Dictionary<string, string> ALIASES = new(StringComparer.Ordinal) { ["seas_trade_dino"] = "cart" };
    static readonly Dictionary<string, string> PREFIX = new(StringComparer.Ordinal) { ["hu"] = "Hu", ["aje"] = "Aje", ["ninigi"] = "Ninigi", ["seas"] = "SEAS" };

    // class name (lower case) -> its row of ROLES (a class can have several roles: aje_clay_wall is wall and strong
    // wall; the first row counts)
    static readonly Dictionary<string, string?[]> BY_CLASS = BuildByClass();
    static Dictionary<string, string?[]> BuildByClass()
    {
        var d = new Dictionary<string, string?[]>(StringComparer.Ordinal);
        foreach (var row in ROLES) for (int t = 1; t <= 4; t++) if (row[t] is string k) d.TryAdd(k, row);
        foreach (var (k, role) in ALIASES) if (ROLES.FirstOrDefault(r => r[0] == role) is { } row) d.TryAdd(k, row);
        return d;
    }

    // words of class names that say little about what an object is (sizes, grades, upgrade stages)
    static readonly HashSet<string> MODIFIERS = new(StringComparer.Ordinal) { "small", "medium", "large", "big", "huge", "heavy", "light", "mini", "re", "enforced", "new", "upgrade", "lumber" };
    // head nouns that say little about what an object is: the trade dino is no tracker dino, the pirate boss ship no ram ship
    static readonly HashSet<string> GENERIC = new(StringComparer.Ordinal) { "dino", "ship", "boat", "machine", "maker", "nest", "field", "farm", "house", "part", "box", "builder" };
    static readonly Regex TribePrefix = new("^[a-z]+_", RegexOptions.CultureInvariant);
    static readonly Regex NoWord = new(@"^\d+$|^s\d$|^lvl\d$", RegexOptions.CultureInvariant);
    static List<string> Words(string cls) => TribePrefix.Replace(cls.ToLowerInvariant(), "", 1).Split('_').Where(w => w.Length > 0 && !NoWord.IsMatch(w)).ToList();
    static double Ln1(double a, double b) => Math.Abs(SMath.Log((a + 1) / (b + 1)));
    const double NAME_NOUN = 3, NAME_WORD = 1, NAME_MOD = 0.5;
    // thresholds of Distance(): a class outside the table of the same kind by name fits up to FITS, else up to FILLS;
    // it takes a table role over when it is at most BETTER farther than the table's class
    const double FITS = 8, FILLS = 2, BETTER = 1;

    static readonly Regex TribeOfRe = new("^([a-z]+)_", RegexOptions.CultureInvariant);
    /// <summary>the tribe a class belongs to by its name (hu_, aje_, ninigi_, seas_), null for heroes, Special and World objects</summary>
    public static string? TribeOfClass(string? name)
    {
        var m = TribeOfRe.Match((name ?? "").ToLowerInvariant());
        return m.Success && PREFIX.TryGetValue(m.Groups[1].Value, out var t) ? t : null;
    }

    /// <summary>A class profile (what it is and does) for Distance().</summary>
    public sealed class Profile
    {
        public string Cls = "", Type = "", Wall = "";
        public bool Coastal, Unlimited, Heal, Worker, Gatherer, Transport, Armed, Shown;
        public double Pop, Dps, Range, Hp, Cost, Epoch, Area;
        public HashSet<string> Prod = new(StringComparer.Ordinal), Drop = new(StringComparer.Ordinal);
        public List<string> Words = new();
    }
    public sealed record Closest(string Cls, double D, (string Cls, double D)? Named);

    sealed class TribeOwner : IRulesOwner
    {
        public TribeOwner(string tribe, TechTree tt) { Tribe = tribe; Tt = tt; }
        public TechTree? Tt { get; }
        public string Tribe { get; }
        public bool Debug => false;
        public int MaxEpoch => 0;
        public bool ForeignActions => false;
    }
    sealed class Made
    {
        public readonly Dictionary<string, (double Epoch, double Cost)> Objs = new(StringComparer.Ordinal);
        public readonly List<string> ObjOrder = new();
        public readonly Dictionary<string, HashSet<string>> Prod = new(StringComparer.Ordinal);
    }

    readonly Rules.Rules D;
    public readonly string From, To;
    readonly HashSet<string> _given, _starts;
    readonly bool _native;
    readonly ModelManifest? _models;
    readonly int _col;
    readonly Dictionary<string, string?> _memo = new(StringComparer.Ordinal);
    readonly Dictionary<string, bool> _memoSet = new(StringComparer.Ordinal);
    readonly HashSet<string> _epochNeeds;
    Dictionary<string, TribeOwner>? _owners;
    Dictionary<string, Made>? _madeBy;
    Dictionary<string, Profile?>? _profiles;
    Dictionary<string, string>? _lcIndex;
    List<string>? _sources;

    /// <summary>D = the rules (exact class names), from = the tribe the mission gives the player, to = the chosen one; given
    /// = the classes (lower case) of the objects the mission gives the player by name (Campaign.GivenClasses), native =
    /// the mission's conditions ask for the chosen tribe's own classes (Campaign.AsksFor), models = the asset manifest
    /// (footprints) or null, starts = the classes (lower case) of the units the player starts with
    /// (Campaign.StartUnitClasses)</summary>
    public TribeSwap(Rules.Rules d, string from, string to, ISet<string>? given = null, bool native = false, ModelManifest? models = null, ISet<string>? starts = null)
    {
        D = d; From = from; To = to;
        _given = given != null ? new HashSet<string>(given, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        _starts = starts != null ? new HashSet<string>(starts, StringComparer.Ordinal) : new HashSet<string>(StringComparer.Ordinal);
        _native = native; _models = models;
        _col = Array.IndexOf(TRIBES, to) + 1;
        // the buildings the chosen tribe's epochs need (the Dragon Clan: lumber mill and dojo): a level that forbids their
        // equivalents until a later epoch (the tutorial: barracks after epoch 2) must not lock the chosen tribe out
        _epochNeeds = new HashSet<string>(ParseActions(to).Where(a => a.Kind == "Upgrades" && AgeRe.IsMatch(a.Id)).SelectMany(a => a.BReq.Select(b => b.ToLowerInvariant())), StringComparer.Ordinal);
    }
    static readonly Regex AgeRe = new(@"^age_\d$", RegexOptions.CultureInvariant);
    List<ActionDef> ParseActions(string tribe) => D.ParseActions(tribe, t => TT.NodeAt(D.Base.Start, $"/Actions/{t}"), false);

    /// <summary>the chosen tribe's class for a class of another tribe: (false, _) = the class is no tribe class (hero,
    /// World, Special, already the chosen tribe's: JS undefined), (true, null) = the role has nothing in the chosen
    /// tribe, (true, cls) = the rules' class name</summary>
    public (bool Tribal, string? Cls) EquivalentOf(string? cls)
    {
        var lc = (cls ?? "").ToLowerInvariant();
        if (_memoSet.TryGetValue(lc, out var set)) return (set, _memo[lc]);
        bool tribal = false; string? res = null;
        var t = TribeOfClass(lc);
        if (t != null && t != To && !KEEP.Contains(lc))
        {
            tribal = true;
            BY_CLASS.TryGetValue(lc, out var row);
            bool tableSet = row != null;                 // the class has a row: its entry may be null (an empty role)
            string? table = row?[_col];
            bool lost = table != null && Exact(table) == null;                    // a mod without the role's class (MIRAGE: the SEAS cart)
            if (lost) { table = null; tableSet = false; }
            var a = GetProfile(Exact(lc) ?? lc, t);
            // the table's classes are the base game's roles: other classes (a mod's, those of the base game outside the
            // table) compete with them only where a role is outside the table, empty or filled better
            var best = a != null ? ClosestOf(a, !tableSet ? null : c => !BY_CLASS.ContainsKey(c.ToLowerInvariant())) : null;
            if (table != null)
            {
                res = Exact(table);
                var b = res != null && a != null ? GetProfile(res, To) : null;
                double dt = b != null ? Distance(a!, b) : double.PositiveInfinity;
                // a class of the same kind by name that the table's is not (the Dustriders' tent -> MIRAGE's SEAS small tent)
                // and about as close. An incompatible table class stands: the table knows roles the data does not show (the
                // Dustriders' main building is an animal, the resource collector)
                var n = best?.Named;
                if (n != null && !SharesNoun(a, b) && double.IsFinite(dt) && n.Value.D < dt + BETTER) res = n.Value.Cls;
            }
            else if (tableSet)                           // the role is empty (null) in the chosen tribe
            {
                var m = best == null ? null : best.Named is { } nb && nb.D <= FITS ? nb.Cls : best.D <= FILLS ? best.Cls : null;
                res = m;
            }
            else
            {
                var same = Exact(TribePrefix.Replace(lc, To.ToLowerInvariant() + "_", 1));       // hu_harbour -> ninigi_harbour
                var m = best == null ? null : best.Named is { } nb && nb.D <= FITS ? nb.Cls : lost && best.D <= FITS ? best.Cls : null;
                res = same ?? m;
            }
        }
        _memoSet[lc] = tribal; _memo[lc] = res;
        return (tribal, res);
    }

    // ---------------------------------------------------------------- matching by closeness
    TribeOwner Owner(string tribe)
    {
        _owners ??= new Dictionary<string, TribeOwner>(StringComparer.Ordinal);
        if (!_owners.TryGetValue(tribe, out var o)) _owners[tribe] = o = new TribeOwner(tribe, D.NewTree(tribe));
        return o;
    }
    static readonly Regex AgeReq = new(@"^age_(\d)$", RegexOptions.CultureInvariant);
    static readonly Regex HeroReq = new(@"_s\d$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    // what a tribe's Build actions make: {cls (exact) -> {epoch, cost}} and {producer class -> set of object types}
    Made MadeBy(string tribe)
    {
        _madeBy ??= new Dictionary<string, Made>(StringComparer.Ordinal);
        if (_madeBy.TryGetValue(tribe, out var have)) return have;
        var pre = tribe.ToLowerInvariant() + "_";
        var res = new Made();
        foreach (var a in ParseActions(tribe))
        {
            if (a.Kind != "Build") continue;
            double ep = 0;
            foreach (var r in a.Req) { var g = AgeReq.Match(r); ep = Math.Max(ep, g.Success ? double.Parse(g.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) : HeroReq.IsMatch(r) ? 99 : 0); }
            double cost = a.Cost.Food + a.Cost.Wood + a.Cost.Stone;
            foreach (var r in a.Results)
            {
                var cls = Exact(r.Obj);
                if (cls == null || !cls.ToLowerInvariant().StartsWith(pre, StringComparison.Ordinal)) continue;
                if (!res.Objs.TryGetValue(cls, out var e)) { res.Objs[cls] = (ep, cost); res.ObjOrder.Add(cls); }
                else if (ep < e.Epoch) res.Objs[cls] = (ep, Math.Min(e.Cost, cost));
                var ix = D.Info(cls);
                // animals and vehicles are the same kind of product (the Norse animal farm, the SEAS garage)
                foreach (var l in a.Locs)
                {
                    var at = l.At.ToLowerInvariant();
                    if (!res.Prod.TryGetValue(at, out var s)) res.Prod[at] = s = new HashSet<string>(StringComparer.Ordinal);
                    if (ix != null) s.Add(ix.Type == "VHCL" ? "ANML" : ix.Type);
                }
            }
        }
        _madeBy[tribe] = res;
        return res;
    }

    /// <summary>what a class is and does, for Distance()</summary>
    public Profile? GetProfile(string cls, string tribe)
    {
        _profiles ??= new Dictionary<string, Profile?>(StringComparer.Ordinal);
        var key = tribe + "|" + cls;
        if (_profiles.TryGetValue(key, out var have)) return have;
        var o = Owner(tribe);
        var ix = D.Info(cls);
        var d = ix != null ? D.Def(cls, o) : null;
        int lv = 0;
        if (d != null) for (int l = 1; l <= 5; l++) if (d.Levels[l] is { Exists: true }) { lv = l; break; }
        var s = lv > 0 ? d!.Levels[lv] : null;                                    // its first level
        Profile? p = null;
        if (s != null && s.Exists)
        {
            var M = MadeBy(tribe);
            var b = M.Objs.TryGetValue(cls, out var bb) ? bb : (0, 0);
            var ws = D.WeaponSetOf(cls, lv, o);
            var w = ws.Long != null ? D.WeaponStatsOf(ws.Long, cls, o) : null;
            var mdl = _models == null ? null : new[] { s.Gfx, D.ClassGfx(cls) }.Select(g => g != null ? _models.Get(g) : null).FirstOrDefault(m => m != null);
            var pf = mdl?.Pf;
            p = new Profile
            {
                Cls = cls, Type = ix!.Type, Wall = d!.WallKind ?? "", Coastal = d.Coastal, Pop = s.Limits.MaxUnits,
                Prod = M.Prod.TryGetValue(cls.ToLowerInvariant(), out var pr) ? pr : new HashSet<string>(StringComparer.Ordinal),
                Drop = new HashSet<string>(d.Delivery, StringComparer.Ordinal), Unlimited = d.Unlimited.Count > 0, Heal = s.Heal != null,
                Worker = d.CanBuild > 0 && d.CanHarvest > 0, Gatherer = !(d.CanBuild > 0) && d.CanHarvest > 0,     // all Dustriders build
                Transport = d.MaxPassengers > 0, Armed = w != null && w.Dmg > 0, Dps = w != null ? w.Dmg / Math.Max(0.3, w.Dur) : 0, Range = w?.Range ?? 0,
                Hp = s.Hp, Cost = b.Item2, Epoch = Math.Min(b.Item1, 6),
                Area = pf != null ? pf.Where(r => r.Length >= 6 && r[0] == 1).Sum(r => Math.Abs(r[4] * r[5])) : 0,
                Words = Words(cls),
                Shown = _models == null || mdl != null,          // MIRAGE's SEAS spearmen and snipers name models the game does not have
            };
        }
        _profiles[key] = p;
        return p;
    }

    /// <summary>how far apart two classes are (+Infinity: one can never stand for the other)</summary>
    public double Distance(Profile a, Profile b)
    {
        if ((a.Type == "BLDG") != (b.Type == "BLDG") || (a.Type == "SHIP") != (b.Type == "SHIP")) return double.PositiveInfinity;
        if (a.Wall != b.Wall || a.Coastal != b.Coastal || a.Worker != b.Worker) return double.PositiveInfinity;
        static int Diff(HashSet<string> x, HashSet<string> y) { int n = 0; foreach (var k in x) if (!y.Contains(k)) n++; foreach (var k in y) if (!x.Contains(k)) n++; return n; }
        double d = 0;
        if (a.Type != b.Type) d += a.Type == "CHTR" || b.Type == "CHTR" ? 6 : 1.5;        // animal <-> vehicle: close
        if (a.Type == "BLDG")
        {
            d += 3 * Diff(a.Prod, b.Prod) + 0.5 * Diff(a.Drop, b.Drop) + ((a.Pop > 0) != (b.Pop > 0) ? 1.5 : 0) + 0.5 * Ln1(a.Pop, b.Pop);
            if (a.Unlimited != b.Unlimited) d += 3;
            if (a.Armed != b.Armed) d += 3;
        }
        else
        {
            if (a.Heal != b.Heal) d += 6;
            if (a.Gatherer != b.Gatherer) d += 5;
            if (a.Transport != b.Transport) d += 4;
            if (a.Armed != b.Armed) d += 4;
            if ((a.Range > 8) != (b.Range > 8)) d += 4;                                     // fights from afar or up close
            d += 0.7 * Ln1(a.Dps, b.Dps) + 0.03 * Math.Abs(a.Range - b.Range);
        }
        d += 0.7 * Ln1(a.Hp, b.Hp) + 0.7 * Ln1(a.Cost, b.Cost);
        if (a.Area != 0 && b.Area != 0) d += 0.7 * Ln1(a.Area, b.Area);
        d += b.Epoch > a.Epoch ? b.Epoch - a.Epoch : 0.25 * (a.Epoch - b.Epoch);           // not later in the game than the original
        double name = SharesNoun(a, b) ? NAME_NOUN : 0;
        for (int i = 0; i < a.Words.Count - 1; i++) { var w = a.Words[i]; if (b.Words.Contains(w)) name += MODIFIERS.Contains(w) || GENERIC.Contains(w) ? NAME_MOD : NAME_WORD; }
        return d - Math.Min(name, 1.5 * NAME_NOUN);
    }

    /// <summary>the names end in the same telling noun (aje_tent, seas_small_tent)</summary>
    public static bool SharesNoun(Profile? a, Profile? b)
    {
        var x = a != null && a.Words.Count > 0 ? a.Words[^1] : null;
        var y = b != null && b.Words.Count > 0 ? b.Words[^1] : null;
        return x != null && x == y && !MODIFIERS.Contains(x) && !GENERIC.Contains(x);
    }

    /// <summary>the chosen tribe's closest class to the profile a among those that pass `only`, and the closest of those
    /// whose name shares a noun with a's; null when nothing is comparable</summary>
    public Closest? ClosestOf(Profile a, Func<string, bool>? only)
    {
        (string Cls, double D)? best = null, named = null;
        foreach (var c in MadeBy(To).ObjOrder)
        {
            if (only != null && !only(c)) continue;
            var b = GetProfile(c, To);
            double d = b != null && b.Shown ? Distance(a, b) : double.PositiveInfinity;
            if (!double.IsFinite(d)) continue;
            if (best == null || d < best.Value.D) best = (c, d);
            if (SharesNoun(a, b) && (named == null || d < named.Value.D)) named = (c, d);
        }
        return best == null ? null : new Closest(best.Value.Cls, best.Value.D, named);
    }

    /// <summary>the epoch a class can first be made in (its Build action's inventions: age_N; a hero's invention: never),
    /// 0 if nothing is needed or it is not made at all</summary>
    public double Epoch(string cls, string tribe) { var e = Exact(cls); return e != null && MadeBy(tribe).Objs.TryGetValue(e, out var v) ? v.Epoch : 0; }

    public string? Exact(string? lc)
    {
        if (lc == null) return null;
        if (_lcIndex == null) { _lcIndex = new Dictionary<string, string>(StringComparer.Ordinal); foreach (var k in D.IndexOrder) _lcIndex.TryAdd(k.ToLowerInvariant(), k); }
        return _lcIndex.TryGetValue(lc.ToLowerInvariant(), out var r) ? r : null;
    }

    /// <summary>the class an object of the player becomes (the class itself when it has no equivalent)</summary>
    public string Cls(string name) => EquivalentOf(name).Cls ?? name;

    /// <summary>the chosen tribe's class that an object query of the mission's tribe means (a condition counting the
    /// Norsemen's workers counts the chosen tribe's). Classes of the other tribes are meant as they are, and nothing is
    /// translated when the mission asks for the chosen tribe's classes itself (mission 14: the original's own tribe
    /// choice). null = none (JS undefined / null)</summary>
    public string? StandsFor(string name) => !_native && TribeOfClass(name) == From ? EquivalentOf(name).Cls : null;

    /// <summary>does an object of the chosen tribe's class `lc` (lower case) count for a query of the mission's class
    /// `name`: its equivalent (StandsFor) or one of the equivalent's ALSO classes (Registry.ClassIs)</summary>
    public bool CountsAs(string name, string lc)
    {
        var e = StandsFor(name)?.ToLowerInvariant();
        if (e == null) return false;
        if (e == lc) return true;
        return ALSO.TryGetValue(e, out var more) && Array.IndexOf(more, lc) >= 0;
    }

    /// <summary>a class the mission names that the chosen tribe has nothing for, or only later in the game (the Dragon
    /// Clan's archer carrier needs epoch 3, the Dustriders' a hero's invention; the Norse chariot comes with epoch 2): an
    /// objective about it is met at once (the conditions). A class outside the table without a namesake whose object the
    /// mission gives the player by name is not missing: the player has that very object (mission 12's telescope tower
    /// ruin)</summary>
    public bool Missing(string? name)
    {
        var lc = (name ?? "").ToLowerInvariant();
        if (TribeOfClass(lc) != From) return false;
        var (tribal, e) = EquivalentOf(lc);
        if (tribal && e == null) return BY_CLASS.ContainsKey(lc) || !_given.Contains(lc);
        // (units the player starts with: it has their equivalents already - Campaign.StartUnitClasses)
        return e != null && Epoch(e, To) > Epoch(lc, From) && !_starts.Contains(lc);
    }

    static readonly Regex BanRe = new(@"AntiActions/[A-Za-z]+/Build/[A-Za-z]+/([^/\s]+)\s*$", RegexOptions.CultureInvariant);
    /// <summary>the level's filters for the chosen tribe: each translated (Filter); a ban of an equivalent that another
    /// class of the level's tribe - one the level does not ban - turns into as well is dropped. The Dustriders' first
    /// mission bans the clay wall and its gate but not the bone palisade and its gate: both pairs are the SEAS fence and
    /// gate, which the ban took away altogether.</summary>
    public List<string> Filters(IEnumerable<string> list)
    {
        var all = list.ToList();
        var banned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in all) { var m = BanRe.Match(f); if (m.Success) banned.Add(m.Groups[1].Value.ToLowerInvariant()); }
        _sources ??= ParseActions(From).Where(a => a.Kind == "Build" && a.Results.Count > 0).Select(a => a.Results[0].Obj).ToList();
        var res = new List<string>();
        foreach (var f in all)
        {
            var g = Filter(f);
            if (g == null) continue;
            var m = BanRe.Match(g);
            if (m.Success)
            {
                var target = m.Groups[1].Value.ToLowerInvariant();
                bool shared = _sources.Any(c => !banned.Contains(c.ToLowerInvariant()) && (EquivalentOf(c).Cls ?? "").ToLowerInvariant() == target);
                if (shared) continue;
            }
            res.Add(g);
        }
        return res;
    }

    static readonly Regex FilterRe = new(@"^(/?Filters/(?:AntiActions/)?)([A-Za-z]+)/(.*)$", RegexOptions.CultureInvariant);
    static readonly Regex LvRe = new(@"^(.*?)(_\d)?$", RegexOptions.CultureInvariant);
    /// <summary>a tech tree filter path of the mission's tribe for the chosen tribe (null: drop it)
    ///   /Filters/AntiActions/Hu/Build/CHTR/hu_warrior_2       -> .../SEAS/Build/CHTR/seas_warrior_2
    ///   /Filters/AntiActions/Hu/Upgrades/hu_fireplace/age_3   -> .../SEAS/Upgrades/seas_headquarters/age_3 (ages only)
    ///   /Filters/AntiActions/Hu/Build/CHTR/lovelace_s0        -> .../SEAS/Build/CHTR/lovelace_s0 (heroes: the tribe only)</summary>
    public string? Filter(string path)
    {
        var p = path.Trim();
        var m = FilterRe.Match(p);
        if (!m.Success || m.Groups[2].Value != From) return p;               // another tribe's filter, Items, ...: as it is
        var head = m.Groups[1].Value + To + "/";
        var rest = m.Groups[3].Value.Split('/');
        if (rest[0] == "Build" && rest.Length >= 3)
        {
            var lv = LvRe.Match(rest[2]);
            if (TribeOfClass(lv.Groups[1].Value) == null) return head + string.Join("/", rest);      // heroes
            var e = EquivalentOf(lv.Groups[1].Value).Cls;
            if (e == null || (m.Groups[1].Value.Contains("AntiActions") && _epochNeeds.Contains(e.ToLowerInvariant()))) return null;
            var ix = D.Info(e);
            return head + string.Join("/", new[] { "Build", ix != null ? ix.Type : rest[1], e.ToLowerInvariant() + lv.Groups[2].Value });
        }
        if (rest[0] == "Upgrades" && rest.Length >= 3 && AgeRe.IsMatch(rest[2]))
        {
            var e = EquivalentOf(rest[1]).Cls;
            return e != null ? head + string.Join("/", new[] { "Upgrades", e.ToLowerInvariant(), rest[2] }) : null;
        }
        if (rest[0] == "BuildObjects" && rest.Length >= 2)
        {
            var e = EquivalentOf(rest[1]).Cls;
            return e != null ? head + string.Join("/", new[] { "BuildObjects", e.ToLowerInvariant() }) : null;
        }
        return null;                                                 // tribe-specific upgrades: nothing to translate
    }
}
