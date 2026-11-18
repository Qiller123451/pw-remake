// A co-op campaign game in the simulation (docs/COOP.md): the seats as players of the world, their starts, the hero
// split, the enemy scaling, the AI commanders, the transfers, and the seat mapping the trigger engine goes through
// (MissionHost and Registry ask it what "the player" means).
//
// Made by MissionSession.Create from GameStart.Coop and handed to the campaign (CampaignConfig.Coop -> Campaign.Coop);
// the campaign calls it at its stages:
//   Plan            per-seat tribe swaps, the models the seats need (their tribes are loaded fully)
//   AddPlayers      new seat players (free slots), ally seats taken over (faction, no brain), diplomacy
//   SetupStarts     main buildings, start armies, resources of the new seats; an ally seat's objects in its faction
//   AfterBuild      the hero split, the garrisons scaled, the scaling factors on the enemies
//   Tick            every step: the AI commanders, the scaled players after diplomacy changes
// Everything here is a pure function of GameStart and the commands: the same on every machine.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using CampaignT = PwRemake.Sim.Campaign.Campaign;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Coop;

/// <summary>A seat in the world.</summary>
public sealed class CoopSeatRun
{
    public CoopSeat Start = null!;
    public CoopSeatDef? Def;
    public int Seat => Start.Seat;
    public int Slot => Start.Slot;
    public bool Ai => Start.Ai;
    public Player Player = null!;
    /// <summary>the tribe the mission gave this slot (seat 1 and new seats: the mission's human tribe; an ally seat: the
    /// ally's) and the tribe it plays</summary>
    public string FromTribe = "", Tribe = "";
    /// <summary>the class equivalents from FromTribe to Tribe (null: the same tribe). Seat 1's is the campaign's Swap.</summary>
    public TribeSwap? Swap;
    /// <summary>a slot the mission does not know (a new player); false: seat 1 or an ally's slot</summary>
    public bool New;
    public CoopCommander? Commander;
    public override string ToString() => $"seat {Seat} slot {Slot} {Tribe}{(Ai ? " ai" : "")}{(New ? " new" : "")}";
}

public sealed class CoopRuntime
{
    static readonly Logger L = Log.For("Coop");
    public readonly CoopStart Start;
    public readonly CoopDefinition Def;
    public readonly CoopScaling Scaling;
    public readonly List<CoopSeatRun> Seats = new();
    public CampaignT C = null!;
    public GameWorld W => C.World;
    /// <summary>the players the scaling applies to now (hostile to seat 1, not excluded, not a seat)</summary>
    public readonly OrderedSet<int> Scaled = new();
    /// <summary>units the garrison scaling added (tests, the log)</summary>
    public int GarrisonExtra, SpawnExtra;
    bool _dipDirty;
    readonly HashSet<int> _seatSlots = new();
    /// <summary>the enemies the definition adds (CoopEnemies.cs)</summary>
    public readonly CoopRuntimeEnemies Enemies;
    Ai.AiData? _aiData;
    double _enemyThink;

    // ---- the hero phase (docs/COOP.md "Hero phase"): before seat 1 has a base, seats 2 / 3 have no base of their own;
    // the gifts of the triggers are dealt over the seats; the enemies get no more units
    /// <summary>the mission has a hero phase (decided at the start: the definition's `phase.hero`, else seat 1 starts
    /// with neither a worker nor a building in a building mission)</summary>
    public bool HasHeroPhase { get; private set; }
    /// <summary>the hero phase is on now (until the bases are handed out)</summary>
    public bool HeroPhase { get; private set; }
    /// <summary>when the bases were handed out (game seconds; null: not yet / no hero phase)</summary>
    public double? HandedOverAt { get; private set; }
    /// <summary>the gifts of the triggers are dealt over the seats now (a hero phase or a no-build mission)</summary>
    public bool Dealing => (Def.NoBuild || HeroPhase) && Def.Phase.Gifts == "deal";
    /// <summary>the enemies get more units for the extra seats (not in a hero phase, not in a no-build mission)</summary>
    public bool CountScaling => !Def.NoBuild && !HeroPhase;
    /// <summary>the seats' starts kept for the hand-over: where, facing</summary>
    readonly List<(CoopSeatRun Seat, double X, double Z, double Heading)> _pending = new();
    /// <summary>units dealt to each seat (tests, the log)</summary>
    public readonly Dictionary<int, int> Dealt = new();
    // a gift batch (one OCPY / SPGR / CPLX action): the seats in their round, the next one
    int _batch; List<CoopSeatRun>? _round; int _turn;

    public CoopRuntime(CoopStart start)
    {
        Start = start;
        Def = start.Def();
        Scaling = new CoopScaling(Def.Scaling, start.Seats.Count, start.Difficulty);
        Enemies = new CoopRuntimeEnemies(this);
        foreach (var s in start.Seats.OrderBy(x => x.Seat))
        {
            Seats.Add(new CoopSeatRun { Start = s, Def = Def.Seats.GetValueOrDefault(s.Seat) });
            _seatSlots.Add(s.Slot);
        }
    }

    // ================================================================== the seat mapping (MissionHost, Registry)
    /// <summary>the mission's human player slot (seat 1)</summary>
    public int HumanSlot => Seats[0].Slot;
    public bool IsSeatSlot(int slot) => _seatSlots.Contains(slot);
    public CoopSeatRun? SeatOfSlot(int slot) => Seats.FirstOrDefault(s => s.Slot == slot);
    public CoopSeatRun? SeatOf(Player? p) => p == null ? null : SeatOfSlot(p.Id);
    public CoopSeatRun? SeatNo(int seat) => Seats.FirstOrDefault(s => s.Seat == seat);
    public IEnumerable<Player> SeatPlayers => Seats.Select(s => s.Player).Where(p => p != null);
    public IEnumerable<CoopSeatRun> HumanSeats => Seats.Where(s => !s.Ai);

    /// <summary>does an object of `actual` count for a query of player `query`? "The player" (seat 1's slot) means every
    /// seat; any other slot (an ally seat's own id too) only itself</summary>
    public bool OwnerMatches(int actual, int query) => actual == query || (query == HumanSlot && IsSeatSlot(actual));
    /// <summary>the slots a player parameter stands for (seat 1's slot: every seat)</summary>
    public IReadOnlyList<int> Expand(int slot) => slot == HumanSlot ? Seats.Select(s => s.Slot).ToList() : new[] { slot };
    /// <summary>where what the triggers give "the player" goes: seat `give_to` (default 1); other slots stay</summary>
    public int GiveTo(int slot)
    {
        if (slot != HumanSlot) return slot;
        var s = SeatNo(Def.GiveTo) ?? Seats[0];
        return s.Slot;
    }
    /// <summary>a story hero given to / made for "the player" goes to its seat (an AI seat's to the first human)</summary>
    public int HeroSlot(string cls, int slot)
    {
        if (slot != HumanSlot && !(SeatOfSlot(slot)?.Ai ?? false)) return slot;
        var key = Start.Heroes.Keys.FirstOrDefault(k => HeroIs(cls, k));
        if (key != null && SeatNo(Start.Heroes[key]) is { Ai: false } hs) return hs.Slot;
        var seat = SeatOfSlot(slot);
        if (seat != null && seat.Ai && IsHeroClass(cls)) return HumanSeats.First().Slot;
        return slot;
    }
    // ------------------------------------------------------------------ dealing the gifts (hero phase, no-build missions)
    /// <summary>the units a seat controls (heroes count; workers not)</summary>
    public int UnitsOf(CoopSeatRun s) => W.Units.Count(u => u.Alive && u.Owner == s.Player && !u.IsWorker);
    /// <summary>the seats in dealing order: the fewest units first, ties seat 2, 3, then 1</summary>
    List<CoopSeatRun> DealOrder(bool humansOnly = false) =>
        Seats.Where(s => !humansOnly || !s.Ai).OrderBy(UnitsOf).ThenBy(s => s.Seat == 1 ? 9 : s.Seat).ToList();
    /// <summary>a gift action begins (true) / ends: within it the units go round the seats one by one</summary>
    public void GiftBatch(bool begin)
    {
        if (begin) { if (_batch++ == 0) { _round = null; _turn = 0; } }
        else if (_batch > 0 && --_batch == 0) _round = null;
    }
    /// <summary>where a gift of the triggers for player `slot` goes: a story hero to its seat; in a hero phase / no-build
    /// mission a unit is dealt (round robin within one action, the seat with the fewest units first), a new hero to the
    /// human with the fewest units, what a base is made of (workers, buildings, a main building) to the `give_to` seat -
    /// in a no-build mission buildings are dealt too; otherwise the `give_to` seat</summary>
    public int GiftSlot(string cls, int slot)
    {
        int give = GiveTo(slot);
        if (slot != HumanSlot) return HeroSlot(cls, give);
        var key = Start.Heroes.Keys.FirstOrDefault(k => HeroIs(cls, k));
        if (key != null || !Dealing) return HeroSlot(cls, give);
        var r = C.RuleClass(cls);
        bool hero = r != null && C.D.Def(r, null)?.Unique == true;
        if (hero)
        {
            var h = DealOrder(humansOnly: true).FirstOrDefault();
            return Count(h ?? Seats[0], cls).Slot;
        }
        bool building = r != null && C.D.Info(r) is { Type: "BLDG" };
        if (!Def.NoBuild && (building || IsBaseItem(cls))) return give;
        CoopSeatRun to;
        if (_batch > 0)
        {
            _round ??= DealOrder();
            to = _round[_turn++ % _round.Count];
        }
        else to = DealOrder()[0];
        return Count(to, cls).Slot;
    }
    CoopSeatRun Count(CoopSeatRun s, string cls)
    {
        Dealt[s.Seat] = Dealt.GetValueOrDefault(s.Seat) + 1;
        GiftLog.Add((W.Time, s.Seat, cls, string.Join(" ", Seats.Select(x => $"{x.Seat}:{UnitsOf(x)}"))));
        return s;
    }
    /// <summary>every dealt gift: when, to which seat, what (tests, the log)</summary>
    public readonly List<(double T, int Seat, string Cls, string Units)> GiftLog = new();

    /// <summary>classes that stand for a hero class of the definition (Stina rides her sabre-tooth: the remake's unit is
    /// special_eusmilus, StartLocation.usl:517-519)</summary>
    static readonly Dictionary<string, string> HERO_ALIAS = new(StringComparer.OrdinalIgnoreCase) { ["Stina_s0"] = "special_eusmilus" };
    /// <summary>is a unit / object class the definition's hero class?</summary>
    public static bool HeroIs(string unitCls, string heroCls) =>
        string.Equals(unitCls, heroCls, StringComparison.OrdinalIgnoreCase) || (HERO_ALIAS.TryGetValue(heroCls, out var a) && string.Equals(a, unitCls, StringComparison.OrdinalIgnoreCase));
    bool IsHeroClass(string cls) => C?.D != null && C.RuleClass(cls) is { } r && C.D.Def(r, null)?.Unique == true;
    /// <summary>the class swap of a slot's objects (class queries of the mission's tribe find the seat's equivalents)</summary>
    public TribeSwap? SwapOf(int slot) => slot == HumanSlot ? C.Swap : SeatOfSlot(slot)?.Swap;
    /// <summary>the class a seat's object gets for a class the mission names</summary>
    public string OwnClass(CoopSeatRun s, string cls) { var r = C.RuleClass(cls); return s.Seat == 1 ? C.OwnClass(cls) : s.Swap != null && r != null ? s.Swap.Cls(r) : r ?? cls; }
    /// <summary>the class an existing object becomes when it is given to a seat: by the tribe of the object's own class
    /// (an object of seat 1's swapped tribe - MIRAGE 13 played as SEAS - given to a Dragon Clan seat turns back into the
    /// Dragon Clan's, though that seat has no swap of its own), else as OwnClass</summary>
    public string SeatClass(CoopSeatRun s, string cls)
    {
        var r = C.RuleClass(cls);
        if (r == null) return cls;
        var t = TribeSwap.TribeOfClass(r);
        // (only seat 1's own tribe: captured or third-party objects stay as the mission gives them)
        if (t != null && s.Tribe.Length > 0 && t != s.Tribe && t == C.HumanTribe && C.Swap != null)
        {
            if (!_swaps.TryGetValue((t, s.Tribe), out var sw)) _swaps[(t, s.Tribe)] = sw = new TribeSwap(C.D, t, s.Tribe);
            var to = sw.Cls(r);
            return C.D.Exists(to) ? to : r;
        }
        return OwnClass(s, cls);
    }
    readonly Dictionary<(string, string), TribeSwap> _swaps = new();
    /// <summary>a tech filter path of the mission for a seat's tree</summary>
    public string? FilterFor(Player p, string path) { var s = SeatOf(p); if (s == null || s.Seat == 1) return C.FilterFor(p, path); return s.Swap != null ? s.Swap.Filter(path) : path; }

    // ================================================================== Plan (before the world)
    public void Plan(CampaignT c)
    {
        C = c;
        foreach (var s in Seats)
        {
            if (s.Seat == 1) { s.FromTribe = c.MissionTribe; s.Tribe = c.HumanTribe; s.Swap = c.Swap; continue; }
            var ally = s.Def?.Ally is int a ? c.Data.Players.FirstOrDefault(p => p != null && p["id"].Int() == a) : null;
            s.New = ally == null;
            s.FromTribe = ally != null ? ally["tribe"].Str() : c.MissionTribe;
            if (s.FromTribe.Length == 0) s.FromTribe = c.MissionTribe;
            s.Tribe = TribeSwap.TRIBES.Contains(s.Start.Tribe) ? s.Start.Tribe : c.HumanTribe;
            if (s.Tribe != s.FromTribe) s.Swap = new TribeSwap(c.D, s.FromTribe, s.Tribe, null, false, c.Manifest);
            if (!c.Tribes.Contains(s.Tribe)) c.Tribes.Add(s.Tribe);
            if (!c.FullTribes.Contains(s.Tribe)) c.FullTribes.Add(s.Tribe);     // (the view loads every model of a played tribe)
            if (CampaignT.MAIN_BUILDING.TryGetValue(s.Tribe, out var mb)) c.ClassModels(mb);
            foreach (var cls in ArmyOf(s)) c.ClassModels(cls);
        }
        Enemies.Plan(c);
        L.Info($"co-op: {string.Join(", ", Seats)}; scaling {Scaling}");
    }

    /// <summary>the classes of a seat's start army (rule classes of its tribe)</summary>
    public List<string> ArmyOf(CoopSeatRun s)
    {
        var res = new List<string>();
        var sd = s.Def;
        if (sd == null || sd.Army == "none") return res;
        if (sd.Army == "list" && sd.ArmyList != null)
        {
            foreach (var cls in sd.ArmyList) { var r = C.RuleClass(cls); if (r == null) continue; var t = TribeSwap.TribeOfClass(r); res.Add(t != null && t != s.Tribe ? new TribeSwap(C.D, t, s.Tribe).Cls(r) : r); }
            return res;
        }
        // "default": seat 1's start army of the mission in the seat's tribe, without the story heroes (unique classes)
        var human = C.Data.Players.FirstOrDefault(p => p != null && p["id"].Int() == C.HumanSlot);
        foreach (var an in human?["start_army"]?.AsArray() ?? new JsonArray())
        {
            var r = C.RuleClass(an!["class"].Str());
            if (r == null || C.D.Def(r, null)?.Unique == true) continue;
            var t = TribeSwap.TribeOfClass(r);
            if (t != null && t != s.Tribe) r = (s.Swap != null && t == s.FromTribe ? s.Swap : new TribeSwap(C.D, t, s.Tribe)).Cls(r);
            r = C.MountOf(r) ?? r;
            res.Add(r);
        }
        // a building mission: workers to start a base with (the tribe's skirmish start units) when the army has none
        if (!Def.NoBuild && !res.Any(cls => C.D.Def(cls, null) is { } d && (d.CanBuild != 0 || d.CanHarvest != 0)))
            foreach (var u in C.D.StartOf(s.Tribe)?.Units ?? new List<string>()) if (C.RuleClass(u) is { } ru && C.D.Def(ru, null)?.Unique != true) res.Add(ru);
        return res;
    }

    // ================================================================== AddPlayers (Campaign.Build, the players made)
    public void AddPlayers(CampaignT c, List<Player> list)
    {
        var human = c.PlayerAt(c.HumanSlot) ?? throw new InvalidOperationException("co-op: the mission has no human player");
        var hd = human.Data ?? new JsonObject();
        var usedColors = new HashSet<int>(list.Select(p => p.ColorSlot));
        foreach (var s in Seats)
        {
            if (s.Seat == 1) { s.Player = human; if (s.Start.Name.Length > 0) human.Name = s.Start.Name; continue; }
            if (!s.New)
            {
                var p = c.PlayerAt(s.Slot)!;
                p.Ai = s.Ai; p.Human = !s.Ai; p.ControlledBy = s.Ai ? ControlledBy.Computer : ControlledBy.Human;
                if (p.Tribe != s.Tribe) { p.Tribe = s.Tribe; p.Tt = c.D.PlayerTree(s.Tribe, p.Gameplay); }
                p.Name = s.Start.Name.Length > 0 ? s.Start.Name : p.Name;
                // the mission's filters of the ally (its own tribe's) in the seat's tribe
                if (s.Swap != null && p.Data is JsonObject pd0)
                {
                    var d = (JsonObject)pd0.DeepClone();
                    d["tech_filters"] = new JsonArray(s.Swap.Filters((pd0["tech_filters"] as JsonArray ?? new JsonArray()).Select(x => x.Str())).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
                    p.Data = d;
                }
                s.Player = p;
                continue;
            }
            int ci = Enumerable.Range(0, Colors.PLAYER_COLORS.Count).FirstOrDefault(i => !usedColors.Contains(i), 0);
            usedColors.Add(ci);
            var col = Colors.PLAYER_COLORS[ci];
            var np = new Player(s.Slot, s.Tribe, new PlayerOpts
            {
                Ai = s.Ai, Name = s.Start.Name.Length > 0 ? s.Start.Name : $"Seat {s.Seat}", Team = human.Team,
                Color = Colors.Hex(col.Dark), PartyColor = Colors.Hex(col.Light), ColorSlot = ci, Rules = c.D, Gameplay = c.Config.Gameplay,
            });
            // the mission's rules for the human player (filters in the seat's tribe, unit limits, population) - no start
            // location or army of the level: SetupStarts places them
            var data = (JsonObject)hd.DeepClone();
            data["id"] = s.Slot; data["start_location"] = null; data["start_army"] = new JsonArray(); data["control"] = s.Ai ? "coop_ai" : "human";
            var fs = (hd["tech_filters"] as JsonArray ?? new JsonArray()).Select(x => x.Str()).ToList();
            data["tech_filters"] = new JsonArray((s.Swap != null ? s.Swap.Filters(fs) : fs).Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            np.Data = data;
            if (human.Pyramid != null) np.Pyramid = (double[])human.Pyramid.Clone();
            np.PopMax = human.PopMax;
            c.Players[s.Slot] = np;
            list.Add(np);
            s.Player = np;
        }
        // diplomacy: a new seat stands to everybody as seat 1 does (and everybody to it); the seats are allies
        foreach (var s in Seats.Where(x => x.New))
            foreach (var q in list)
            {
                if (q == s.Player || IsSeatSlot(q.Id)) continue;
                s.Player.SetRelation(q, human.Relation(q));
                q.SetRelation(s.Player, q.Relation(human));
            }
        foreach (var a in Seats) foreach (var b in Seats) if (a != b) a.Player.SetRelation(b.Player, 2);
        // the new enemies of the definition (their own slots, relations as their `like`)
        Enemies.AddPlayers(c, list);
        // every seat's slot counts as the humans' for the computer players' "the human" (passive option, strategy)
        L.Info("co-op players: " + string.Join(", ", Seats.Select(s => $"{s.Seat}={s.Player.Id} {s.Player.Tribe} {s.Player.Name}")));
    }

    // ================================================================== SetupStarts (Campaign.Build, after the start locations)
    public void SetupStarts(CampaignT c)
    {
        var W = c.World;
        var human = Seats[0].Player;
        var hd = human.Data;
        // the hero phase: the definition's, else a building mission whose seat 1 starts with neither a worker nor a building
        var me = human;
        bool seat1Base = W.Buildings.Any(b => b.Alive && b.Owner == me) || W.Units.Any(u => u.Alive && u.Owner == me && IsBaseItem(u.Name));
        HasHeroPhase = !Def.NoBuild && (Def.Phase.Hero ?? !seat1Base);
        HeroPhase = HasHeroPhase;
        if (HasHeroPhase) L.Info("co-op: hero phase until seat 1 gets its base" + (Def.Phase.BaseWhen != null ? " (" + Def.Phase.BaseWhen + ")" : ""));
        var sl1 = c.StartLocations[c.HumanSlot];
        foreach (var s in Seats.Where(x => x.Seat != 1))
        {
            var p = s.Player;
            var sd = s.Def!;
            double x, z;
            if (HasHeroPhase && s.New)
            {
                // no base, no army, no stock yet: kept for the hand-over; meanwhile the seat looks at seat 1's start and
                // gets its `hero_army` there (mission 1: a Norse warrior each)
                _pending.Add((s, sd.X ?? 0, sd.Z ?? 0, CampaignT.Heading(sd.Dir)));
                foreach (var k in p.Res.Keys.ToList()) p.Res[k] = 0;
                double hx = sl1?.X ?? sd.X ?? 0, hz = sl1?.Z ?? sd.Z ?? 0;
                c.StartLocations[s.Slot] = new StartLocation { X = hx, Z = hz, Heading = sl1?.Heading ?? 0, Data = new JsonObject() };
                int j = 0;
                foreach (var cls0 in sd.HeroArmy)
                {
                    var r = C.RuleClass(cls0);
                    if (r == null) { c.Warn($"co-op seat {s.Seat}: hero_army class {cls0} unknown"); continue; }
                    var t = TribeSwap.TribeOfClass(r);
                    var cls = t != null && t != s.Tribe ? new TribeSwap(C.D, t, s.Tribe).Cls(r) : r;
                    double ang = (s.Seat * 1.7 + j * 2.399) % 6.283;
                    var (gx, gz) = W.FreeSpot(hx + SMath.Cos(ang) * 5, hz - SMath.Sin(ang) * 5);
                    var u = W.Spawn(cls, p, gx, gz, new SpawnOpts { Level = c.D.MinLevel(cls, p), Heading = sl1?.Heading ?? 0 });
                    if (u is Unit uu) { uu.HomeX = uu.Pos.X; uu.HomeZ = uu.Pos.Z; c.Objects.Adopt(uu); }
                    else c.Warn($"co-op seat {s.Seat}: {cls} not created");
                    j++;
                }
                continue;
            }
            if (!s.New)
            {
                // an ally seat in another faction: its objects become the seat tribe's (as the human's in a swapped mission)
                if (s.Swap != null)
                    foreach (var r in c.Objects.All.ToList())
                        if (r.Alive && r.Entity is { Alive: true } e && e.Owner == p && C.RuleClass(e.Name) is { } rc && s.Swap.Cls(rc) is var to && to != rc && C.D.Exists(to))
                            c.ConvertTo(r, p, to);
                // an ally that owns something plays on with it; one that owns nothing gets a start like a new seat: at the
                // definition's start, else its own start location of the map
                if (W.Units.Any(u => u.Alive && u.Owner == p) || W.Buildings.Any(b => b.Alive && b.Owner == p)) continue;
                var asl = c.StartLocations[s.Slot];
                if (sd.X == null && asl == null) continue;
                x = sd.X ?? asl!.X; z = sd.Z ?? asl!.Z;
            }
            else { x = sd.X ?? 0; z = sd.Z ?? 0; }
            PlaceStart(c, s, x, z, CampaignT.Heading(sd.Dir));
        }
    }

    /// <summary>a seat's base start: main building, start army, start location, start resources</summary>
    void PlaceStart(CampaignT c, CoopSeatRun s, double x, double z, double heading)
    {
        var W = c.World; var p = s.Player; var sd = s.Def!; var hd = Seats[0].Player.Data;
        {
            double stand = 3;
            bool wantBase = !Def.NoBuild && (sd.Base ?? true);
            if (wantBase && CampaignT.MAIN_BUILDING.TryGetValue(s.Tribe, out var mb))
            {
                var e = W.Spawn(mb, p, x, z, new SpawnOpts { Heading = heading });
                if (e != null) stand = e.Radius; else c.Warn($"co-op seat {s.Seat}: {mb} could not be placed at {x:0},{z:0}");
            }
            int i = 0;
            foreach (var cls in ArmyOf(s))
            {
                double ang = (i * 2.399) % 6.283, r = 0.99 * stand + 1.5 + (i % 3) * 1.2;
                var (gx, gz) = W.FreeSpot(x + SMath.Cos(ang + 0.785) * r, z - SMath.Sin(ang + 0.785) * r);
                var u = W.Spawn(cls, p, gx, gz, new SpawnOpts { Level = c.D.MinLevel(cls, p), Heading = heading });
                if (u is Unit uu) { uu.HomeX = uu.Pos.X; uu.HomeZ = uu.Pos.Z; }
                if (u == null) c.Warn($"co-op seat {s.Seat}: {cls} not created");
                i++;
            }
            c.StartLocations[s.Slot] = new StartLocation { X = x, Z = z, Heading = heading, Data = new JsonObject() };
            // resources: the definition's, else seat 1's of the mission (-1: the tribe's default stock)
            var res = hd?["resources"]; var def = c.D.StartOf(p.Tribe)?.Res;
            foreach (var (k, rr) in new[] { ("food", "food"), ("wood", "wood"), ("stone", "stone"), ("iron", "skulls") })
            {
                double v;
                if (sd.Resources != null && sd.Resources.TryGetValue(k, out var dv0)) v = dv0;
                else { var n = res?[k]; v = n is not JsonValue ? 0 : n.Num() < 0 ? (def != null && def.TryGetValue(rr, out var dv) ? dv : 0) : n.Num(); }
                p.Res[rr] = v;
            }
        }
    }

    // ================================================================== AfterBuild (MissionSession, world and brains ready)
    public void AfterBuild(CampaignT c)
    {
        var W = c.World;
        // ---- heroes of seat 1 that the split gives to other seats
        foreach (var (cls, seatNo) in Start.Heroes)
        {
            var to = SeatNo(seatNo);
            if (to == null || to.Seat == 1 || to.Ai) continue;
            foreach (var u in W.Units.Where(u => u.Alive && u.Owner == Seats[0].Player && HeroIs(u.Name, cls)).ToList())
                W.SetOwner(u, to.Player);
        }
        // ---- buildable heroes: whoever builds one first has it; AI commanders build none
        W.CoopHeroBlocked = (p, cls) =>
        {
            var s = SeatOf(p);
            if (s == null) return false;
            if (s.Ai) return true;
            foreach (var o in Seats) if (o != s && (o.Player.Heroes.Has(cls) || W.AnyQueuedObj(o.Player, cls))) return true;
            return false;
        };
        // ---- the enemies
        RecomputeScaled();
        // ---- the definition's changes to the mission's own objects: a second gate, seat 1's defence split
        Gates(c);
        SplitStart(c);
        // `mortal`: these objects can be clicked, targeted and destroyed from the start (the map may place them
        // unselectable and unhitable until a later phase - MIRAGE 13's gate tower: flags 278528, selectable false)
        foreach (var n in Def.Mortal)
            if (c.Objects.Get(n) is { } mr && mr.Entity != null) c.SetAppearance(mr, (mr.Flags ?? 7) | 7);
        // (a hero phase: no more enemy units and no enemy bases of the definition until the bases are handed out)
        if (CountScaling && Def.Scaling.Garrisons) ScaleGarrisons(c);
        // ---- the enemy bases of the definition (after the garrisons: their units are scaled as they are placed)
        if (!HeroPhase) Enemies.Place(c, _aiData);
        c.DiplomacyChanged += _ => _dipDirty = true;
    }

    // ================================================================== the hero phase: its end, the hand-over
    /// <summary>is a class something a base is made of (a building, a worker, a builder, a tribe's main building - the
    /// Dustriders' is a unit)?</summary>
    public bool IsBaseItem(string cls)
    {
        var r = C.RuleClass(cls);
        if (r == null) return false;
        if (CampaignT.MAIN_BUILDING.ContainsValue(r)) return true;
        if (C.D.Info(r) is { Type: "BLDG" }) return true;
        var d = C.D.Def(r, null);
        return d != null && (d.CanHarvest != 0 || d.CanBuild != 0);
    }

    /// <summary>does the base phase begin now? (the definition's `phase.base_when`, else seat 1 owns a building, a
    /// worker or a main building)</summary>
    bool BaseBegins()
    {
        var bw = Def.Phase.BaseWhen;
        if (bw != null)
        {
            if (bw.StartsWith("trigger:", StringComparison.Ordinal)) return C.Engine?.Find(bw[8..]) is { Fired: > 0 };
            if (bw.StartsWith("quest:", StringComparison.Ordinal)) return C.Engine?.Quests.FirstOrDefault(q => q.Name == bw[6..]) is { } q && (q.Visible || q.Accomplished);
            return false;
        }
        var me = Seats[0].Player;
        return W.Buildings.Any(b => b.Alive && b.Owner == me) || W.Units.Any(u => u.Alive && u.Owner == me && IsBaseItem(u.Name));
    }

    /// <summary>the end of the hero phase: seats 2 / 3 get their base starts (at the moment seat 1 gets its base), the
    /// enemies their extra units and the definition's enemy bases</summary>
    public void HandOver()
    {
        if (!HeroPhase) return;
        HeroPhase = false;
        HandedOverAt = W.Time;
        foreach (var (s, x, z, h) in _pending) { PlaceStart(C, s, x, z, h); s.Commander?.Rebase(x, z); }
        _pending.Clear();
        // the enemies: the scaling of their numbers (made again: the wave and population factors), their garrisons, the
        // definition's enemy bases
        foreach (var id in Scaled.ToList()) Scaled.Delete(id);
        foreach (var p in W.Players) { p.CoopDamage = 1; p.CoopHealth = 1; if (p.CoopPopBase is double b) { p.PopMax = b; p.CoopPopBase = null; } }
        RecomputeScaled();
        if (Def.Scaling.Garrisons) ScaleGarrisons(C);
        Enemies.Place(C, _aiData);
        L.Info($"co-op: the hero phase ends at {W.Time:0.0} s - the seats' bases are handed out; dealt: " + string.Join(", ", Dealt.OrderBy(x => x.Key).Select(x => $"seat {x.Key} {x.Value}")));
        W.Emit("coopBases", new Args());
    }

    /// <summary>the players the scaling applies to: hostile to seat 1, not excluded, not a seat; their strength factors
    /// and population limits follow (a player that is no longer hostile gets its own back)</summary>
    public void RecomputeScaled()
    {
        var W = C.World; var me = Seats[0].Player;
        foreach (var p in W.Players)
        {
            bool on = !IsSeatSlot(p.Id) && !Def.Scaling.Exclude.Contains(p.Id) && p.IsEnemy(me);
            if (on == Scaled.Has(p.Id)) continue;
            if (on)
            {
                Scaled.Add(p.Id);
                p.CoopDamage = Scaling.Damage; p.CoopHealth = Scaling.Health;
                if (Def.Scaling.Production && CountScaling) { p.CoopPopBase = p.PopMax; p.PopMax = Math.Ceiling(p.PopMax * Scaling.Count); }
            }
            else
            {
                Scaled.Delete(p.Id);
                p.CoopDamage = 1; p.CoopHealth = 1;
                if (p.CoopPopBase is double b) { p.PopMax = b; p.CoopPopBase = null; }
            }
            if (W.Brains.Get(p.Id) is Ai.TribeAI t) t.WaveScale = on && Def.Scaling.Waves && CountScaling ? Scaling.Count : 1;
        }
    }
    public bool IsScaled(int slot) => Scaled.Has(slot);

    /// <summary>more units in the enemies' placed garrisons: for every placed unit of a scaled player (not a hero, shown
    /// or in a group) the extra copies stand beside it, in its group, with its stance and appearance</summary>
    void ScaleGarrisons(CampaignT c)
    {
        var W = c.World;
        var placed = c.Plans.Where(pl => pl.Kind == "unit" && pl.Rec?.Entity is Unit { Alive: true } u && u.Owner != null && IsScaled(u.Owner.Id)).Select(pl => pl.Rec!).ToList();
        foreach (var rec in placed)
        {
            var u = (Unit)rec.Entity!;
            if (u.Def?.Unique == true || u.Inside || u.IsWorker) continue;
            if (!rec.Visible && rec.Group == null) continue;
            int n = Scaling.Units(u.Owner!.Id, 1) - 1;
            for (int k = 0; k < n; k++)
            {
                var (x, z) = W.FreeSpot(u.Pos.X + 2.5 * (k + 1), u.Pos.Z + 1.5);
                var e = W.Spawn(u.Name, u.Owner, x, z, new SpawnOpts { Level = u.Level, Heading = u.Heading });
                if (e is not Unit d) continue;
                d.HomeX = d.Pos.X; d.HomeZ = d.Pos.Z; d.Stance = u.Stance;
                var dr = c.Objects.Adopt(d);
                if (rec.Group != null) c.Objects.AddToGroup(rec.Group, dr);
                if (rec.Flags is int f) c.SetAppearance(dr, f);
                GarrisonExtra++;
            }
        }
        if (GarrisonExtra > 0) L.Info($"co-op: {GarrisonExtra} units added to the enemies' garrisons ({Scaling})");
    }

    /// <summary>a unit the triggers spawn for player `slot`: how many in all (1 = no extra)</summary>
    public int SpawnCount(int slot, string cls)
    {
        if (!Def.Scaling.Spawns || !IsScaled(slot) || !CountScaling) return 1;
        var r = C.RuleClass(cls);
        if (r == null || C.D.Info(r) is not { } ix || ix.Type == "BLDG" || C.D.Def(r, null)?.Unique == true) return 1;
        int n = Scaling.Units(slot, 1);
        SpawnExtra += n - 1;
        return n;
    }

    // ================================================================== the definition's changes to the mission's objects
    /// <summary>`gates`: a gate of the mission's class in place of wall pieces; the triggers' conditions on those pieces
    /// watch the gate (the registry's names and guids of the pieces point at it)</summary>
    void Gates(CampaignT c)
    {
        foreach (var g in Def.Gates.Where(g => g.Seats <= Seats.Count))
        {
            var recs = g.Replaces.Select(n => c.Objects.Get(n)).Where(r => r?.Entity is Building { Alive: true }).ToList();
            if (recs.Count == 0) { c.Warn($"co-op gate {g.Class}: none of {string.Join(", ", g.Replaces)} found"); continue; }
            var first = (Building)recs[0]!.Entity!;
            var owner = first.Owner;
            var cls = C.RuleClass(g.Class) ?? g.Class;
            // in the wall line: along the replaced pieces, its middle at theirs (GatePlace)
            var at = GatePlace(W.Models, C.D.Stats(cls, 1, null)?.Gfx, recs.Select(r => (((Building)r!.Entity!).Boxes, ((Building)r.Entity!).Rot)).ToList());
            double x = at?.X ?? recs.Average(r => r!.Entity!.Pos.X), z = at?.Z ?? recs.Average(r => r!.Entity!.Pos.Z), heading = at?.Rot ?? first.Rot;
            foreach (var r in recs) c.Remove(r);
            var e = W.Spawn(cls, owner, x, z, new SpawnOpts { Heading = heading });
            if (e is not Building gb) { c.Warn($"co-op gate {g.Class}: not placed at {x:0},{z:0}"); continue; }
            var rec = c.Objects.Adopt(gb);
            foreach (var r in recs) c.Objects.Alias(r!, rec);
            L.Info($"co-op: {cls} in place of {string.Join(", ", g.Replaces)} at ({c.ToMap(x, z).MX:0}, {c.ToMap(x, z).MY:0})");
        }
    }

    /// <summary>
    /// where a gate goes in place of wall pieces (game coordinates: the model's origin and its turn). The pieces' outer
    /// ends (the ends of their footprints' long sides) give the line: the gate's long side lies along it, its footprint's
    /// middle at the line's middle, so the walls left on both sides meet its ends in line. It stands the way the mission's
    /// own gate stands to its walls (mission 13's hcl13_gate: turned 180° from the wall pieces - map rot -3.07 between
    /// pieces of 0.21 and 0). The same for the pieces as map objects (before the world: the pavement, CoopMapHook.Maps)
    /// and as buildings (Gates). null: no pieces.
    /// </summary>
    public static (double X, double Z, double Rot)? GatePlace(ModelManifest M, string? gateGfx, IReadOnlyList<(double[][] Boxes, double Rot)> pieces)
    {
        var ends = new List<(double X, double Z)>();
        foreach (var (boxes, prot) in pieces)
        {
            double c = SMath.Cos(prot), s = SMath.Sin(prot);
            foreach (var b in boxes)
            {
                // the box's long side: local x -> (cos, -sin), local z -> (sin, cos) (Footprint.Place)
                bool alongX = b[2] >= b[3];
                double ux = alongX ? c : s, uz = alongX ? -s : c, h = alongX ? b[2] : b[3];
                ends.Add((b[0] + ux * h, b[1] + uz * h)); ends.Add((b[0] - ux * h, b[1] - uz * h));
            }
        }
        if (ends.Count < 2) return null;
        (double X, double Z) A = ends[0], B = ends[1]; double best = -1;
        for (int i = 0; i < ends.Count; i++)
            for (int j = i + 1; j < ends.Count; j++)
            {
                double d = Sq(ends[i].X - ends[j].X) + Sq(ends[i].Z - ends[j].Z);
                if (d > best) { best = d; A = ends[i]; B = ends[j]; }
            }
        double mx = (A.X + B.X) / 2, mz = (A.Z + B.Z) / 2, dx = B.X - A.X, dz = B.Z - A.Z;
        // the gate's own long side (its footprint's): local x or local z along the line
        var f0 = Footprint.Place(M, gateGfx, 0, 0, 0);
        double r0 = f0.Hx >= f0.Hz ? SMath.Atan2(-dz, dx) : SMath.Atan2(dx, dz);
        // of the two turns along the line the one nearer the pieces' turn + 180°
        double want = pieces[0].Rot + Math.PI;
        double Diff(double a) { double d = (a - want) % (2 * Math.PI); if (d < -Math.PI) d += 2 * Math.PI; if (d > Math.PI) d -= 2 * Math.PI; return Math.Abs(d); }
        double rot = Diff(r0) <= Diff(r0 + Math.PI) ? r0 : r0 + Math.PI;
        if (rot < 0) rot += 2 * Math.PI;
        // the origin: the footprint's middle (Place's Fx, Fz relative to the origin) on the line's middle
        var f = Footprint.Place(M, gateGfx, 0, 0, rot);
        return (mx - f.Fx, mz - f.Fz, rot);
    }

    /// <summary>`start_split` and `deal_start`: seat 1's placed objects (its defence) shared with the other seats at the
    /// start - objects in an area to a seat (moved, if asked: made again there), seat 1's start fighters dealt round
    /// robin</summary>
    void SplitStart(CampaignT c)
    {
        var me = Seats[0].Player;
        foreach (var sp in Def.StartSplit.Where(q => q.For == Seats.Count))
        {
            var to = SeatNo(sp.To);
            if (to == null) continue;
            var (cx, cz) = c.ToGame(sp.X, sp.Z);
            var ents = W.Buildings.Cast<Entity>().Concat(W.Units).Where(e => e.Alive && e.Owner == me && Sq(e.Pos.X - cx) + Sq(e.Pos.Z - cz) <= sp.R * sp.R
                && (sp.Classes.Count == 0 || sp.Classes.Any(k => string.Equals(C.RuleClass(k) ?? k, e.Name, StringComparison.Ordinal) || string.Equals(C.OwnClass(k), e.Name, StringComparison.Ordinal)))).OrderBy(e => Sq(e.Pos.X - cx) + Sq(e.Pos.Z - cz)).ThenBy(e => e.Id).Take(sp.Max).ToList();
            int i = 0;
            foreach (var e in ents)
            {
                if (sp.MoveX is double mx && sp.MoveZ is double mz && e is Building)
                {
                    var (gx, gz) = c.ToGame(mx + 14 * (i % 3), mz + 12 * (i / 3));
                    var rec = e.Rec;
                    var group = rec?.Group;
                    if (rec != null) c.Remove(rec); else W.RemoveEntity(e);
                    // (in the receiving seat's tribe: seat 1's swapped defences become the seat's own)
                    var nb = W.Spawn(SeatClass(to, e.Name), to.Player, gx, gz, new SpawnOpts { Heading = e.Heading });
                    if (nb is Building bb)
                    {
                        // the moved one keeps the placed one's name, GUID and group: the triggers that name it find it
                        // (MIRAGE 13's gate tower ninigi_small_tower_121: made invulnerable, later selected)
                        var nr = c.Objects.Adopt(bb);
                        if (rec != null) { c.Objects.Alias(rec, nr); if (group != null) c.Objects.AddToGroup(group, nr); }
                    }
                    else c.Warn($"co-op split: {e.Name} not placed at {mx:0},{mz:0}");
                }
                else if (e.Rec is { Alive: true } rec2) c.SetOwner(rec2, to.Player.Id);
                else W.SetOwner(e, to.Player);
                i++;
            }
        }
        if (Def.DealStart && Seats.Count > 1)
        {
            // (not the story's characters: mission 8's Kleemann, schliemann_s0, is no hero class but must stay seat 1's)
            var fighters = W.Units.Where(u => u.Alive && u.Owner == me && !u.IsWorker && u.Def?.Unique != true && !u.Inside && !u.Name.EndsWith("_s0", StringComparison.OrdinalIgnoreCase)).OrderBy(u => u.Id).ToList();
            var round = DealOrder();
            int k = 0;
            foreach (var u in fighters)
            {
                var s = round[k++ % round.Count];
                if (s.Seat == 1) continue;
                if (u.Rec is { Alive: true } rec) c.SetOwner(rec, s.Player.Id); else W.SetOwner(u, s.Player);
            }
        }
    }
    static double Sq(double v) => v * v;

    /// <summary>`reroute`: a scripted wave path (map coordinates) that ends near a point ends at the definition's
    /// points instead (every n-th wave, and those from its `from` area); the same path for every member of a wave</summary>
    public IReadOnlyList<(double X, double Y)>? Reroute(IReadOnlyList<(double X, double Y)> pts, int key)
    {
        if (pts.Count == 0) return null;
        foreach (var r in Def.Reroutes.Where(q => q.Seats <= Seats.Count))
        {
            var last = pts[^1];
            if (Sq(last.X - r.NearX) + Sq(last.Y - r.NearZ) > r.NearR * r.NearR) continue;
            if (!_rerouted.TryGetValue(key, out var go))
            {
                bool from = r.FromX is double fx && r.FromZ is double fz && Sq(pts[0].X - fx) + Sq(pts[0].Y - fz) <= Sq(r.FromR ?? 60);
                go = from || (r.Every > 0 && ++_rerouteN % r.Every == 0);
                _rerouted[key] = go;
            }
            if (!go) return null;
            // the path until it comes near the old end, then the definition's points
            var res = new List<(double X, double Y)>();
            foreach (var p in pts) { if (Sq(p.X - r.NearX) + Sq(p.Y - r.NearZ) <= Sq(r.NearR * 2.5)) break; res.Add(p); }
            res.AddRange(r.To);
            return res;
        }
        return null;
    }
    readonly Dictionary<int, bool> _rerouted = new();
    int _rerouteN;

    // ================================================================== commanders
    public void CreateCommanders(Ai.AiData data)
    {
        _aiData = data;
        foreach (var s in Seats.Where(x => x.Ai))
            s.Commander = new CoopCommander(this, s, data);
    }

    // ================================================================== every step (Campaign.Update)
    public void Tick(double dt)
    {
        if (_dipDirty) { _dipDirty = false; Enemies.SyncRelations(W.Players); RecomputeScaled(); }
        if (HeroPhase && BaseBegins()) HandOver();
        foreach (var s in Seats) s.Commander?.Tick(dt);
        if (Enemies.List.Count > 0 && W.Time >= _enemyThink) { _enemyThink = W.Time + 1; Enemies.Tick(C); }
    }

    // ================================================================== transfers
    /// <summary>gives units of `from` to the seat player `to`: only between seats, never buildings, heroes only between
    /// humans; returns the number given or a reason</summary>
    public (int N, string? Why) Give(Player from, IEnumerable<Entity> ents, Player to)
    {
        var a = SeatOf(from); var b = SeatOf(to);
        if (a == null || b == null || a == b) return (0, "seat");
        int n = 0; string? why = null;
        foreach (var e in ents.ToList())
        {
            if (e is not Unit u || !u.Alive || u.Owner != from) { why ??= "building"; continue; }
            if (u.Def?.Unique == true && (a.Ai || b.Ai)) { why = "hero"; continue; }
            if (u.Inside) continue;
            if (W.SetOwner(u, to)) n++;          // ('owner' event: the campaign tells the triggers)
        }
        return (n, n > 0 ? null : why ?? "none");
    }
}
