// The enemies a co-op definition adds (docs/COOP.md "Enemy bases", `enemies` of schema v1): an existing enemy base grown
// (buildings and units for an enemy player of the mission - its brain and its triggers drive them) or a new enemy base
// of a new computer player (a free slot, 8 and up first: the campaign world has 12) with the remake's AI brain
// (src/Sim/Ai), set to build up and to send waves at the co-op seats, tuned by the co-op difficulty. Each addition
// appears from a number of seats (`seats = 3`: only in a game of three), so a 2-seat game gets less.
//
//   enemies = {
//     { name = "Raider camp", seats = 2, owner = "new" | <player id>, tribe = "Aje", like = 1,
//       at = { x = 1200, z = 640, r = 48 },               -- the original map's coordinates (as everything in a definition)
//       preset = "outpost" | "camp" | "town",              -- the tribe's own buildings (the classes the missions use)
//       buildings = { { class = "aje_small_tower", x = 1190, z = 610, rot = 0 }, ... },   -- more, at points (or round `at`)
//       army = { "aje_warrior", { class = "aje_archer", count = 3 } },                      -- placed units (scaled)
//       brain = { behaviour = "Dodo", attack = true, first_attack = 540, interval = 300, wave = "SuicideAttack", target = 3 },
//                                                          -- (no attack of its own before first_attack either)
//       resources = { food = 600, wood = 600, stone = 400 },
//       notes = "why it is there and how the objectives treat it" },
//   }
//
// owner "new": a player of its own that no trigger knows - no objective counts it; it stands to everybody as `like`
// does (an enemy player of the mission: the same team and colour - not its tech bans -, and its relations follow like's whenever the
// diplomacy changes - neutral barbarians that turn hostile later take it with them). A brain unless `brain = false`.
// owner = <id>: the buildings and units join that player (its objectives count them: the definition's notes say why
// that is right); its own brain and triggers drive it; `brain` (waves) only if it asks for them.
//
// Placement: a building at its point if it fits there, else at the nearest free spot (a spiral of 4 m steps, CanPlace,
// on dry ground - not at the waterline);
// a preset's main building at `at`, the rest round it. Everything is a pure function of GameStart: the same on every
// machine.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Ai;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;
using CampaignT = PwRemake.Sim.Campaign.Campaign;

namespace PwRemake.Sim.Coop;

/// <summary>one entry of `enemies`</summary>
public sealed class CoopEnemyDef
{
    public int Index;
    public string Name = "";
    /// <summary>from how many seats it appears (2: always, 3: only with three seats)</summary>
    public int Seats = 2;
    /// <summary>an existing player of the mission whose base it grows; null: a new computer player</summary>
    public int? Owner;
    public string Tribe = "";
    /// <summary>the mission's player a new enemy stands to everybody as (relations, team, colour, filters)</summary>
    public int? Like;
    /// <summary>the base's centre and build radius (map coordinates of the original map)</summary>
    public double X, Z, R = 40;
    public string? Preset;
    public readonly List<(string Cls, double? X, double? Z, double Rot)> Buildings = new();
    public readonly List<(string Cls, int Count)> Army = new();
    /// <summary>a brain of its own (new players) / waves (both)</summary>
    public bool Brain = true;
    public string Behaviour = "Dodo";
    /// <summary>waves at the co-op seats: from FirstAttack s, every Interval s (shorter on harder co-op difficulties)</summary>
    public bool Attack = true;
    public double FirstAttack = 600, Interval = 300;
    public string Wave = "SuicideAttack";
    /// <summary>the seat its waves go for (null or not in use: the nearest seat in use)</summary>
    public int? Target;
    public Dictionary<string, double>? Resources;
    public string Notes = "";
    public bool New => Owner == null;
    public override string ToString() => $"enemy {Index} '{Name}' ({(New ? "new " + Tribe : "player " + Owner)}, from {Seats} seats)";

    public static readonly string[] Presets = { "outpost", "camp", "town" };

    /// <summary>`enemies` of a definition (problems: what is wrong with it)</summary>
    public static List<CoopEnemyDef> Parse(JsonNode? n, Action<string> problem)
    {
        var res = new List<CoopEnemyDef>();
        var items = n is JsonArray a ? a.ToList() : n is JsonObject o && o.Count == 0 ? new List<JsonNode?>() : null;
        if (n != null && items == null) { problem("enemies is not a list"); return res; }
        int k = 0;
        foreach (var it in items ?? new List<JsonNode?>())
        {
            k++;
            if (it is not JsonObject e) { problem($"enemies[{k}] is not a table"); continue; }
            var d = new CoopEnemyDef { Index = k, Name = e["name"].Str($"enemy {k}"), Seats = e["seats"].Int(2), Notes = e["notes"].Str() };
            if (d.Seats is < 2 or > 3) problem($"enemies[{k}].seats is 2 or 3");
            if (e["owner"] is JsonValue ov && ov.TryGetValue<string>(out var os)) { if (os != "new") problem($"enemies[{k}].owner is \"new\" or a player id"); }
            else if (e["owner"] is JsonValue ov2) d.Owner = ov2.Int();
            d.Tribe = e["tribe"].Str();
            if (e["like"] is JsonValue lv) d.Like = lv.Int();
            if (e["at"] is JsonObject at) { d.X = at["x"].Num(); d.Z = at["z"].Num(); d.R = at["r"] is JsonValue ? at["r"].Num() : 40; }
            else problem($"enemies[{k}] has no `at`");
            d.Preset = e["preset"] is JsonValue pv ? pv.Str() : null;
            if (d.Preset != null && !Presets.Contains(d.Preset)) problem($"enemies[{k}].preset '{d.Preset}' (outpost, camp or town)");
            foreach (var b in e["buildings"] as JsonArray ?? new JsonArray())
            {
                if (b is not JsonObject bo || bo["class"].Str().Length == 0) { problem($"enemies[{k}].buildings: an entry without a class"); continue; }
                d.Buildings.Add((bo["class"].Str(), bo["x"] is JsonValue ? bo["x"].Num() : null, bo["z"] is JsonValue ? bo["z"].Num() : null, bo["rot"].Num()));
            }
            foreach (var u in e["army"] as JsonArray ?? new JsonArray())
            {
                if (u is JsonValue uv && uv.Str().Length > 0) d.Army.Add((uv.Str(), 1));
                else if (u is JsonObject uo && uo["class"].Str().Length > 0) d.Army.Add((uo["class"].Str(), Math.Max(1, uo["count"].Int(1))));
                else problem($"enemies[{k}].army: a class or {{ class =, count = }}");
            }
            if (e["brain"] is JsonValue bv && bv.TryGetValue<bool>(out var bb)) { d.Brain = bb; d.Attack = bb && d.New; }
            else if (e["brain"] is JsonObject br)
            {
                d.Brain = true;
                d.Behaviour = br["behaviour"].Str("Dodo");
                d.Attack = br["attack"].Bool(true);
                d.FirstAttack = br["first_attack"] is JsonValue ? br["first_attack"].Num() : d.FirstAttack;
                d.Interval = br["interval"] is JsonValue ? Math.Max(60, br["interval"].Num()) : d.Interval;
                d.Wave = br["wave"].Str("SuicideAttack");
                if (br["target"] is JsonValue tv) d.Target = tv.Int();
            }
            else if (!d.New) { d.Brain = false; d.Attack = false; }
            if (e["resources"] is JsonObject ro) d.Resources = ro.ToDictionary(x => x.Key, x => x.Value.Num());
            if (d.New && d.Tribe.Length == 0 && d.Like == null) problem($"enemies[{k}]: a new enemy needs a tribe (or like)");
            if (d.Preset == null && d.Buildings.Count == 0 && d.Army.Count == 0) problem($"enemies[{k}] adds nothing (preset, buildings or army)");
            res.Add(d);
        }
        return res;
    }

    /// <summary>the buildings of a preset for a tribe (the classes the original missions use for that tribe's bases):
    /// the main building first (a camp or town), then production, towers and houses</summary>
    public static List<string> PresetClasses(string preset, string tribe)
    {
        (string Main, string[] Outpost, string[] Camp, string[] Town) t = tribe switch
        {
            "Hu" => ("hu_fireplace", new[] { "hu_small_tower", "hu_large_tower", "hu_arena", "hu_small_tower" },
                new[] { "hu_arena", "hu_small_tower", "hu_stone_cottage", "hu_small_tower", "hu_stone_cottage", "hu_large_tower" },
                new[] { "hu_arena", "hu_weapons_smith", "hu_large_tower", "hu_stone_cottage", "hu_small_tower", "hu_stone_cottage", "hu_warehouse", "hu_large_tower", "hu_stone_cottage", "hu_small_tower", "hu_temple" }),
            "Aje" => ("aje_resource_collector", new[] { "aje_small_tower", "aje_medium_tower", "aje_rodeo", "aje_small_tower" },
                new[] { "aje_rodeo", "aje_small_tower", "aje_tent", "aje_small_tower", "aje_tent", "aje_medium_tower" },
                new[] { "aje_rodeo", "aje_weapons_builder", "aje_medium_tower", "aje_tent", "aje_small_tower", "aje_tent", "aje_slaughterhouse", "aje_medium_tower", "aje_tent", "aje_small_tower", "aje_temple" }),
            "Ninigi" => ("ninigi_fireplace", new[] { "ninigi_small_tower", "ninigi_dojo", "ninigi_small_tower", "ninigi_small_tower" },
                new[] { "ninigi_dojo", "ninigi_small_tower", "ninigi_warehouse", "ninigi_small_tower", "ninigi_animal_farm", "ninigi_small_tower" },
                new[] { "ninigi_dojo", "ninigi_weapon_maker", "ninigi_small_tower", "ninigi_warehouse", "ninigi_small_tower", "ninigi_animal_farm", "ninigi_engineer", "ninigi_small_tower", "ninigi_temple", "ninigi_small_tower" }),
            "SEAS" => ("seas_headquarters", new[] { "seas_turret_tower", "seas_barracks", "seas_turret_tower", "seas_hq_machinegun_nest" },
                new[] { "seas_barracks", "seas_turret_tower", "seas_small_tent", "seas_turret_tower", "seas_small_tent", "seas_greenhouse" },
                new[] { "seas_barracks", "seas_garage", "seas_turret_tower", "seas_small_tent", "seas_turret_tower", "seas_steelwork", "seas_greenhouse", "seas_turret_tower", "seas_small_tent", "seas_barracks" }),
            _ => ("", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
        };
        var l = new List<string>();
        if (t.Main.Length == 0) return l;
        if (preset != "outpost") l.Add(t.Main);
        l.AddRange(preset == "outpost" ? t.Outpost : preset == "camp" ? t.Camp : t.Town);
        return l;
    }
    /// <summary>a preset's start army: the tribe's workers (camp, town) and fighters</summary>
    public static List<string> PresetArmy(string preset, string tribe)
    {
        string w = tribe switch { "Hu" => "hu_worker", "Aje" => "aje_worker", "Ninigi" => "ninigi_worker", "SEAS" => "seas_worker", _ => "" };
        string f = tribe switch { "Hu" => "hu_warrior", "Aje" => "aje_warrior", "Ninigi" => "ninigi_warrior", "SEAS" => "seas_warrior", _ => "" };
        string g = tribe switch { "Hu" => "hu_archer", "Aje" => "aje_archer", "Ninigi" => "ninigi_archer", "SEAS" => "seas_marksman", _ => "" };
        var l = new List<string>();
        if (w.Length == 0) return l;
        int workers = preset == "town" ? 6 : preset == "camp" ? 4 : 0, fighters = preset == "town" ? 6 : preset == "camp" ? 4 : 3;
        for (int i = 0; i < workers; i++) l.Add(w);
        for (int i = 0; i < fighters; i++) l.Add(i % 2 == 0 ? f : g);
        return l;
    }
}

/// <summary>an enemy addition in the running game</summary>
public sealed class CoopEnemyRun
{
    public CoopEnemyDef Def = null!;
    public Player Player = null!;
    public Player? Like;
    public int Slot;
    /// <summary>game coordinates of `at`</summary>
    public double X, Z;
    public TribeAI? Brain;
    public double NextWave = double.MaxValue;
    /// <summary>waves called / launched (the brain sent units)</summary>
    public int Waves, Launched;
    /// <summary>how the last wave went (CoopRuntimeEnemies.WaveWay): "land", "building", "boat" or "beach"</summary>
    public string Way = "";
    public readonly List<Entity> Placed = new();
    public override string ToString() => $"{Def} slot {Slot}";
}

public sealed class CoopRuntimeEnemies
{
    static readonly Logger L = Log.For("Coop.Enemies");
    readonly CoopRuntime R;
    public readonly List<CoopEnemyRun> List = new();
    AiData? _ai;
    public CoopRuntimeEnemies(CoopRuntime r) { R = r; }

    /// <summary>the additions of this game (by seat count)</summary>
    public IEnumerable<CoopEnemyDef> InUse => R.Def.Enemies.Where(e => e.Seats <= R.Seats.Count);

    /// <summary>Plan: the models of the classes they need (a new enemy's tribe in full: its brain builds anything)</summary>
    public void Plan(CampaignT c)
    {
        foreach (var e in InUse)
        {
            string tribe = TribeOf(c, e);
            if (e.New && tribe.Length > 0)
            {
                if (!c.Tribes.Contains(tribe)) c.Tribes.Add(tribe);
                if (e.Brain && !c.FullTribes.Contains(tribe)) c.FullTribes.Add(tribe);
            }
            foreach (var cls in Classes(e, tribe)) c.ClassModels(cls);
        }
    }
    static string TribeOf(CampaignT c, CoopEnemyDef e)
    {
        if (e.Tribe.Length > 0) return e.Tribe;
        int? id = e.Owner ?? e.Like;
        var pd = id != null ? c.Data.Players.FirstOrDefault(p => p != null && p["id"].Int() == id) : null;
        return pd?["tribe"].Str() ?? "";
    }
    static IEnumerable<string> Classes(CoopEnemyDef e, string tribe)
    {
        if (e.Preset != null) { foreach (var b in CoopEnemyDef.PresetClasses(e.Preset, tribe)) yield return b; foreach (var u in CoopEnemyDef.PresetArmy(e.Preset, tribe)) yield return u; }
        foreach (var b in e.Buildings) yield return b.Cls;
        foreach (var u in e.Army) yield return u.Cls;
    }

    /// <summary>AddPlayers: the new enemies' players (free slots: 8 .. 11 first, then the mission's unused ones)</summary>
    public void AddPlayers(CampaignT c, List<Player> list)
    {
        var used = new HashSet<int>(list.Select(p => p.Id));
        foreach (var pd in c.Data.Players) if (pd != null && pd["present"].Bool()) used.Add(pd["id"].Int());
        foreach (var e in InUse)
        {
            Player? like = e.Like is int li ? list.FirstOrDefault(p => p.Id == li) : e.Owner is int oi ? list.FirstOrDefault(p => p.Id == oi) : null;
            if (!e.New)
            {
                var owner = list.FirstOrDefault(p => p.Id == e.Owner);
                if (owner == null || R.IsSeatSlot(owner.Id)) { c.Warn($"co-op {e}: player {e.Owner} is not a computer player of the mission"); continue; }
                List.Add(new CoopEnemyRun { Def = e, Player = owner, Slot = owner.Id, Like = owner });
                continue;
            }
            int slot = Enumerable.Range(8, CoopPlan.MaxSlots - 8).Concat(Enumerable.Range(1, 7)).FirstOrDefault(s => !used.Contains(s), -1);
            if (slot < 0) { c.Warn($"co-op {e}: no free player slot"); continue; }
            used.Add(slot);
            string tribe = TribeOf(c, e);
            like ??= list.FirstOrDefault(p => p.Ai && !R.IsSeatSlot(p.Id) && p.Tribe.Length > 0 && p.IsEnemy(R.Seats[0].Player));
            var np = new Player(slot, tribe, new PlayerOpts
            {
                Ai = true, Name = e.Name, Team = like?.Team, Rules = c.D, Gameplay = c.Config.Gameplay,
                Color = like?.Color ?? 0x9a3030, PartyColor = like?.PartyColor, ColorSlot = like?.ColorSlot ?? -1,
            });
            var data = (JsonObject?)like?.Data?.DeepClone() ?? new JsonObject();
            data["id"] = slot; data["start_location"] = null; data["start_army"] = new JsonArray(); data["control"] = "coop_enemy"; data["name"] = e.Name;
            // (not the mission's bans of `like`: a pirate band the story keeps from building would not build a camp -
            // the new player has its tribe's tree and climbs it as its brain does)
            data["tech_filters"] = new JsonArray();
            np.Data = data;
            np.PopMax = Math.Max(like?.PopMax ?? 0, 80);
            if (like?.Pyramid != null) np.Pyramid = (double[])like.Pyramid.Clone();
            c.Players[slot] = np;
            list.Add(np);
            List.Add(new CoopEnemyRun { Def = e, Player = np, Slot = slot, Like = like });
        }
        SyncRelations(list);
        if (List.Count > 0) L.Info("co-op enemies: " + string.Join(", ", List));
    }

    /// <summary>a new enemy stands to everybody as its `like` does (and everybody to it); allied with like and with the
    /// other new enemies of the same like. Again after every diplomacy change of the mission.</summary>
    public void SyncRelations(IEnumerable<Player> players)
    {
        var all = players.ToList();
        foreach (var e in List.Where(x => x.Def.New))
        {
            var p = e.Player; var like = e.Like;
            foreach (var q in all)
            {
                if (q == p) continue;
                int rel, back;
                if (like == null) { rel = R.IsSeatSlot(q.Id) ? 0 : 1; back = rel; }
                else if (q == like || List.Any(o => o.Def.New && o.Player == q && o.Like == like)) { rel = 2; back = 2; }
                else { rel = like.Relation(q); back = q.Relation(like); }
                if (p.Relation(q) != rel) p.SetRelation(q, rel);
                if (q.Relation(p) != back) q.SetRelation(p, back);
            }
        }
    }

    /// <summary>AfterBuild (the world, the seats and the scaling ready): the buildings and units placed, the brains made</summary>
    public void Place(CampaignT c, AiData? ai)
    {
        _ai = ai;
        var W = c.World;
        foreach (var e in List)
        {
            var d = e.Def; var p = e.Player;
            (e.X, e.Z) = c.ToGame(d.X, d.Z);
            string tribe = p.Tribe;
            // the buildings: the preset's round `at` (its main building at the centre), then the listed ones
            var todo = new List<(string Cls, double X, double Z, double Rot, bool AtPoint)>();
            if (d.Preset != null)
            {
                var cls = CoopEnemyDef.PresetClasses(d.Preset, tribe);
                int i = 0;
                foreach (var b in cls)
                {
                    bool main = i == 0 && d.Preset != "outpost";
                    // (round the centre: a ring at 0.55 r, the towers further out)
                    double ang = i * 2.399 + d.Index, rr = main ? 0 : d.R * (b.Contains("tower") || b.Contains("turret") ? 0.85 : 0.5);
                    var (s, co) = Maps.MapExtension.SinCos(ang);
                    todo.Add((b, e.X + co * rr, e.Z + s * rr, ang, false));
                    i++;
                }
            }
            foreach (var b in d.Buildings)
            {
                var (bx, bz) = b.X is double mx && b.Z is double mz ? c.ToGame(mx, mz) : (e.X, e.Z);
                todo.Add((b.Cls, bx, bz, b.Rot, b.X != null));
            }
            int placed = 0;
            foreach (var t in todo)
            {
                var cls = c.RuleClass(t.Cls) ?? t.Cls;
                if (!c.D.Exists(cls)) { c.Warn($"co-op {d}: unknown class {t.Cls}"); continue; }
                var spot = FindSpot(W, cls, p, t.X, t.Z, t.Rot, d.R + 40);
                if (spot == null) { c.Warn($"co-op {d}: no room for {cls} near {t.X:0},{t.Z:0}"); continue; }
                var ent = W.Spawn(cls, p, spot.Value.X, spot.Value.Z, new SpawnOpts { Heading = t.Rot, Built = true });
                if (ent == null) { c.Warn($"co-op {d}: {cls} not placed"); continue; }
                c.Objects.Adopt(ent);
                e.Placed.Add(ent); placed++;
            }
            // the units (scaled like the mission's garrisons of a scaled player)
            var army = new List<string>();
            if (d.Preset != null) army.AddRange(CoopEnemyDef.PresetArmy(d.Preset, tribe));
            foreach (var (cl, n) in d.Army) for (int k = 0; k < n; k++) army.Add(cl);
            int units = 0, idx = 0;
            foreach (var cl in army)
            {
                var cls = c.RuleClass(cl) ?? cl;
                if (!c.D.Exists(cls)) { c.Warn($"co-op {d}: unknown class {cl}"); continue; }
                bool worker = c.D.Def(cls, null) is { } ud && (ud.CanHarvest != 0 || ud.CanBuild != 0);
                int copies = worker || !R.IsScaled(p.Id) || !R.Def.Scaling.Garrisons || !R.EnemyScaling ? 1 : R.Scaling.Units(p.Id, 1);
                for (int k = 0; k < copies; k++)
                {
                    double ang = idx * 2.399, rr = 8 + (idx % 4) * 3;
                    var (s, co) = Maps.MapExtension.SinCos(ang);
                    var (gx, gz) = W.FreeSpot(e.X + co * rr, e.Z + s * rr);
                    var u = W.Spawn(cls, p, gx, gz, new SpawnOpts { Level = c.D.MinLevel(cls, p), Heading = ang });
                    idx++;
                    if (u is not Unit uu) continue;
                    uu.HomeX = uu.Pos.X; uu.HomeZ = uu.Pos.Z;
                    c.Objects.Adopt(uu);
                    e.Placed.Add(uu); units++;
                }
            }
            if (e.Def.New)
            {
                c.StartLocations[e.Slot] = new StartLocation { X = e.X, Z = e.Z, Heading = 0, Data = new JsonObject() };
                var def = c.D.StartOf(tribe)?.Res;
                foreach (var (k, rr) in new[] { ("food", "food"), ("wood", "wood"), ("stone", "stone"), ("iron", "skulls") })
                    p.Res[rr] = d.Resources != null && d.Resources.TryGetValue(k, out var v) ? v : k == "iron" ? 0 : 500;
            }
            L.Info($"co-op {d}: {placed} buildings, {units} units at {e.X:0},{e.Z:0} (slot {e.Slot})");
        }
        // the brains of the new enemies (and the waves of every addition that asks for them)
        foreach (var e in List)
        {
            var d = e.Def;
            if (d.New && d.Brain && ai != null)
            {
                var A = W.Ai;
                if (A == null)
                {
                    A = new AiPlayers(W, ai) { Human = c.Human?.Id ?? 0 };
                    A.MapToGame = m => { var (x, z) = c.ToGame(m.X, m.Y); return new Pt(x, z); };
                    A.ResolveRegion = name => c.Regions.Find(name) is { } rg ? new AiRegionFn(rg.Contains) : null;
                    A.Attach();
                }
                // the co-op difficulty's level: Normal 4, Hard 6, Brutal 8
                var b = new TribeAI(W, e.Player, ai, new TribeAIOptions
                {
                    Difficulty = 4 + 2 * Math.Clamp(R.Start.Difficulty, 0, 2), Behaviour = d.Behaviour, MapOptions = AiMapOptions.From(c.Map["ai_options"]),
                    Multimap = false, LevelName = null, ExtraSquads = true,
                });
                b.Start = new Pt(e.X, e.Z);
                // (no attacks of its own before its first wave: the seats get their time to build - then as `attack` says)
                b.Params.NoFight = true;
                A.Add(b);
                e.Brain = b;
            }
            else if (!d.New && d.Attack) e.Brain = W.Brains.Get(e.Slot) as TribeAI;
            if (e.Brain != null && d.Attack)
            {
                // harder co-op difficulties: sooner and more often
                double k = 1 - 0.15 * Math.Clamp(R.Start.Difficulty, 0, 2);
                // (placed at the start, or when a hero phase ends: the first wave that long after)
                e.NextWave = W.Time + d.FirstAttack * k;
            }
        }
    }

    /// <summary>how far above the water a building's ground must be (its centre and its rim)</summary>
    const double DRY = 0.5;
    /// <summary>the nearest spot (4 m spiral) where cls fits, on dry ground; null: none within maxR</summary>
    static (double X, double Z)? FindSpot(World.World W, string cls, Player p, double x, double z, double rot, double maxR)
    {
        // (on dry ground: CanPlace takes the shallow water at a coast - the smoothed co-op coasts (MapExtension.SmoothJoins)
        // put mission 10's west outpost machine-gun nest on the waterline below its plateau; a harbour goes by the shore)
        double? wl = W.Data.Def(cls, p)?.Coastal == true ? null : W.Map.Water;
        double rad = W.FootprintOf(cls, x, z, rot, false, p).Radius;
        bool Dry(double px, double pz)
        {
            if (wl is not double w) return true;
            if (W.Height(px, pz) < w + DRY) return false;
            for (int k = 0; k < 8; k++) { var (s, c) = Maps.MapExtension.SinCos(k * Math.PI / 4); if (W.Height(px + c * rad, pz + s * rad) < w + DRY) return false; }
            return true;
        }
        bool Fits(double px, double pz) => W.CanPlace(cls, px, pz, rot, p) && Dry(px, pz);
        if (Fits(x, z)) return (x, z);
        for (double r = 4; r <= maxR; r += 4)
        {
            int n = Math.Max(8, (int)(r * 2 * Math.PI / 4));
            for (int i = 0; i < n; i++)
            {
                var (s, c) = Maps.MapExtension.SinCos(2 * Math.PI * i / n);
                double px = x + c * r, pz = z + s * r;
                if (Fits(px, pz)) return (px, pz);
            }
        }
        return null;
    }

    /// <summary>every step: the waves (once a second is enough)</summary>
    public void Tick(CampaignT c)
    {
        var W = c.World;
        foreach (var e in List)
        {
            if (e.Brain == null || W.Time < e.NextWave) continue;
            if (e.Def.New && e.Def.Attack) e.Brain.Params.NoFight = false;
            var d = e.Def;
            double k = 1 - 0.15 * Math.Clamp(R.Start.Difficulty, 0, 2);
            // nothing left of it: no more waves
            if (!W.Buildings.Any(b => b.Alive && b.Owner == e.Player) && !W.Units.Any(u => u.Alive && u.Owner == e.Player)) { e.Brain = null; continue; }
            // the seat it goes for: the definition's, else the nearest one in use (by its start)
            // (only seats it is at war with: an enemy that is still neutral - its `like` too - sends nothing yet)
            var foes = R.Seats.Where(s => e.Player.IsEnemy(s.Player)).ToList();
            if (foes.Count == 0) { L.Debug($"co-op {d}: at peace with every seat"); continue; }
            var seat = (d.Target is int ts ? foes.FirstOrDefault(s => s.Seat == ts) : null) ?? foes.OrderBy(s => Dist(c, s, e)).First();
            var targets = W.Buildings.Where(b => b.Alive && b.Owner == seat.Player).Cast<Entity>().ToList();
            if (targets.Count == 0) targets = W.Units.Where(u => u.Alive && u.Owner == seat.Player && !u.Inside).Cast<Entity>().ToList();
            if (targets.Count == 0) { L.Debug($"co-op {d}: seat {seat.Seat} has nothing to attack"); continue; }
            var sl = c.StartLocations[seat.Slot];
            var pos = sl != null ? new Pt(sl.X, sl.Z) : Pt.Of(targets[0]);
            // (the wave's time is used up only when one goes: at peace or with nothing to attack it asks again next second)
            e.NextWave = W.Time + d.Interval * k;
            int tier = Math.Min(5, 1 + e.Waves / 2);
            var q = new TribeAI.AttackRequest
            {
                Type = d.Wave + "_" + tier, Targets = targets.Take(8).ToList(), Position = pos, SpawnPosition = new Pt(e.X, e.Z), Spawn = true, IgnoreLocations = true, AttackOnTheWay = true,
            };
            e.Way = WaveWay(W, e, q, targets);
            var id = e.Brain.StartAttack(q);
            e.Waves++;
            if (id != null) e.Launched++;
            L.Info($"co-op {d}: wave {e.Waves} ({d.Wave}_{tier}) at seat {seat.Seat}, {e.Way}{(id == null ? " - not launched" : "")}");
        }
    }
    /// <summary>how a wave gets to its seat (docs/COOP.md "Waves that cannot walk"): the waves are land armies spawned at
    /// the camp, and a camp is meant to stand on its seat's land (CoopMapsTests.LandIslands; MapExtension.Joins). Where it
    /// does not (World.LandIsland: the camp's land is not the seat's), the brain would take its transports across - a
    /// new camp has none, and the wave never left (base 06's raiders' cove until 2026-11-12). Then, in this order:
    /// "building" - the seat's buildings on the camp's own land (an outpost it built there), on foot; "boat" - a scripted
    /// landing: transports spawned with the wave sail to the seat's coast (the original's ship_land; the tribe has a
    /// transport and one water joins the two coasts); "beach" - the wave comes ashore on the seat's land, at its point
    /// nearest the camp, as if it had landed. -> "land" (by land, as meant), or which of these; q changed to match.</summary>
    static string WaveWay(World.World W, CoopEnemyRun e, TribeAI.AttackRequest q, List<Entity> targets)
    {
        var aim = q.Position!.Value;
        int camp = W.LandIslandNear(e.X, e.Z, 24);
        int seat = W.LandIslandNear(aim.X, aim.Z, 24);
        if (seat < 0) foreach (var t in targets) if ((seat = W.LandIslandNear(t.Pos.X, t.Pos.Z, t.Radius + 16)) >= 0) break;
        if (camp < 0 || seat < 0 || camp == seat) return "land";
        var here = targets.Where(t => W.LandIslandNear(t.Pos.X, t.Pos.Z, t.Radius + 16) == camp).ToList();
        if (here.Count > 0) { q.Targets = here.Take(8).ToList(); q.Position = Pt.Of(here[0]); return "building"; }
        if (e.Brain!.WaveLanding(new Pt(e.X, e.Z), aim) is Pt land) { q.ShipLand = true; q.Landing = land; return "boat"; }
        if (W.NearestOnIsland(seat, e.X, e.Z) is var (bx, bz)) { q.SpawnPosition = new Pt(bx, bz); return "beach"; }
        return "land";
    }
    static double Dist(CampaignT c, CoopSeatRun s, CoopEnemyRun e)
    {
        var sl = c.StartLocations[s.Slot];
        return sl == null ? 1e9 : Math.Sqrt((sl.X - e.X) * (sl.X - e.X) + (sl.Z - e.Z) * (sl.Z - e.Z));
    }
}
