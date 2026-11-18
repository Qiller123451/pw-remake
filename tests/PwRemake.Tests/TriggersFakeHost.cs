// A mission host for the trigger engine's tests: a small world of records (objects with class, type, owner,
// position, hit points, level, group), regions (rect / oval shapes), players (relations, resources, filters) and the
// mission UI - enough to run the engine's semantics without the real world (the role of the JS remake's
// tests/campaign_mock.mjs). Every call the engine makes is recorded in Calls ("Spawn hu_warrior 1 ..."); tests push
// world events with Kill / Move / Attack ... which report to the engine as the real world would.
// The query block (Select / Matches) follows registry.js select / matches (triggers.md §2.1 / §2.2).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;

namespace PwRemake.Tests;

public sealed class FakeObj
{
    public int Id; public string Guid = "", Name = "", Class = "", Type = ""; public int Owner = -1;
    public double X, Y, Z, Rot, Hp = 100, MaxHp = 100, Sight = 25, Radius = 0.5; public int Level = 1;
    public bool Alive = true, Parked, Built = true, IsItem; public int Group = -1, Inside = -1, Capacity, Passengers;
    public string? CarriedItem, GateState, Task; public int Flags = 7;
    public List<int> Members = new();             // a GROU object
    public Dictionary<string, string> Attr = new();
    public bool IsUnit => Type is "CHTR" or "ANML" or "VHCL" or "SHIP";
    public bool IsBuilding => Type == "BLDG";
    public bool HasEntity => IsUnit || IsBuilding;
}

public sealed class FakeRegion
{
    public string Guid = "", Name = "";
    public List<(bool Oval, double X, double Y, double W, double H, bool Enabled)> Shapes = new();
    public bool Contains(double x, double y) => Shapes.Any(s => s.Enabled && (s.Oval
        ? Math.Pow((x - (s.X + s.W / 2)) / (s.W / 2), 2) + Math.Pow((y - (s.Y + s.H / 2)) / (s.H / 2), 2) <= 1
        : x >= s.X && x <= s.X + s.W && y >= s.Y && y <= s.Y + s.H));
}

public sealed class FakePlayer
{
    public int[] Rel = { 1, 1, 1, 1, 1, 1, 1, 1 };
    public Dictionary<string, double> Res = new() { ["food"] = 0, ["wood"] = 0, ["stone"] = 0, ["iron"] = 0 };
    public HashSet<string> Filters = new();
    public int[]? Pyramid; public int PopMax = -1, AnimalsNeutral; public string Behaviour = "Mikrobe";
    public Dictionary<string, string> Caps = new();
}

public sealed class FakeMissionHost : IMissionHost
{
    public TriggerEngine? E;
    public readonly List<FakeObj> Objs = new();             // creation order = query order
    public readonly Dictionary<string, FakeRegion> Regions = new();
    public readonly FakePlayer?[] Players = new FakePlayer?[8];
    public readonly List<string> Calls = new();
    public int Diff = 1;
    public SimRandom Rng = new(7);
    public bool HoldSequences, HoldDialogs, Busy;
    public readonly List<SequenceRequest> Sequences = new();
    public readonly List<(string Path, int Token)> Dialogs = new();
    public readonly List<MapMarker> Markers = new();
    public readonly Dictionary<string, (string State, string? Tip)> QuestionMarks = new();
    public readonly List<(string Quest, string Change)> QuestLog = new();
    public string InfoBar = "";
    public (bool Won, string Text)? Ended;
    public Dictionary<string, string>? Profile;
    public readonly Dictionary<int, (List<MapPos> Pts, int Mode)> Patrols = new();
    int nextId = 1;

    public FakeMissionHost() { for (int i = 0; i < 3; i++) Players[i] = new FakePlayer(); Players[0]!.Rel[0] = 2; Players[1]!.Rel[1] = 2; Players[2]!.Rel[2] = 2; }

    // ------------------------------------------------------------------ building the test world
    public FakeObj Add(string name, string cls, string type, int owner, double x, double y, string? guid = null)
    {
        var o = new FakeObj { Id = nextId++, Name = name, Class = cls, Type = type, Owner = owner, X = x, Y = y, Guid = guid ?? "" };
        Objs.Add(o);
        return o;
    }
    public FakeObj AddGroup(string name, string guid) => Add(name, "GroupObject", "GROU", -1, 0, 0, guid);
    public FakeRegion AddRegion(string guid, string name, double x, double y, double w, double h, bool oval = false)
    {
        var r = new FakeRegion { Guid = guid, Name = name };
        r.Shapes.Add((oval, x, y, w, h, true));
        Regions[guid] = r;
        return r;
    }
    /// <summary>Objects, groups, regions and players of a pw-campaign/1 document (as the world setup would place them).</summary>
    public void LoadWorld(JsonNode doc)
    {
        foreach (var p in doc["players"]?.AsArray() ?? new JsonArray())
        {
            int id = p!["id"].Int(); if (id < 0 || id > 7 || !p["present"].Bool()) continue;
            var fp = Players[id] = new FakePlayer();
            if (p["diplomacy"] is JsonArray d) for (int j = 0; j < Math.Min(8, d.Count); j++) fp.Rel[j] = d[j].Int(1);
            fp.Rel[id] = 2;
        }
        // the start army at the start location (what the world setup creates, triggers.md §6.7 no. 4)
        foreach (var p in doc["players"]?.AsArray() ?? new JsonArray())
        {
            var sl = p?["start_location"];
            if (p == null || !p["present"].Bool() || sl is not JsonObject) continue;
            if (p["include_buildings"].Bool())
            {
                var main = p["tribe"].Str() switch { "Hu" => "hu_fireplace", "Aje" => "aje_resource_collector", "Ninigi" => "ninigi_fireplace", "SEAS" => "seas_headquarters", _ => "" };
                if (main.Length > 0) Add($"{main}_{nextId}", main, "BLDG", p["id"].Int(), sl["x"].Num(), sl["y"].Num());
            }
            if (sl["ignore_pointbuy"].Bool()) continue;
            int k = 0;
            foreach (var a in p["start_army"]?.AsArray() ?? new JsonArray())
            {
                var cls = a!["class"].Str();
                var u = Add($"{cls}_{nextId}", cls, "CHTR", p["id"].Int(), sl["x"].Num() + (k % 4) * 2, sl["y"].Num() + (k / 4) * 2);
                u.Level = a["level"].Int() + 1; k++;
            }
        }
        foreach (var o in doc["objects"]?.AsArray() ?? new JsonArray())
        {
            var f = Add(o!["name"].Str(), o["class"].Str(), o["type"].Str(), o["owner"] is JsonValue ? o["owner"].Int(-1) : -1, o["x"].Num(), o["y"].Num(), o["guid"].Str());
            f.Level = o["level"].Int() + 1; f.Parked = o["visible"] is JsonValue && !o["visible"].Bool(); f.Rot = o["rot"].Num();
        }
        foreach (var g in doc["groups"]?.AsArray() ?? new JsonArray())
        {
            var go = AddGroup(g!["name"].Str(), g["guid"].Str());
            foreach (var m in g["members"]?.AsArray() ?? new JsonArray()) { var mo = Objs.FirstOrDefault(x => x.Guid == m.Str()); if (mo != null && mo.Group < 0) { go.Members.Add(mo.Id); mo.Group = go.Id; } }
        }
        foreach (var r in doc["regions"]?.AsArray() ?? new JsonArray())
        {
            var fr = new FakeRegion { Guid = r!["guid"].Str(), Name = r["name"].Str() };
            foreach (var s in r["shapes"]?.AsArray() ?? new JsonArray())
                fr.Shapes.Add((s!["type"].Str() == "oval", s["x"].Num(), s["y"].Num(), s["w"].Num(), s["h"].Num(), !(s["enabled"] is JsonValue) || s["enabled"].Bool()));
            Regions[fr.Guid] = fr;
        }
    }

    FakeObj? O(int id) => id > 0 && id <= Objs.Count ? Objs[id - 1] : null;
    FakeObj? Live(int id) { var o = O(id); return o != null && o.Alive ? o : null; }
    public FakeObj Obj(int id) => O(id)!;
    public FakeObj? ByName(string n) => Objs.FirstOrDefault(o => o.Alive && o.Name == n);
    void Rec(string s) => Calls.Add(s);
    static string F(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ world events pushed by tests
    /// <summary>The object dies (a unit / building) or is deleted: out of its group, lost, removed.</summary>
    public void KillNow(int id, bool lost = true)
    {
        var o = Live(id);
        if (o == null) return;
        o.Alive = false; o.Hp = 0;
        if (o.Group >= 0) { var g = O(o.Group)!; g.Members.Remove(id); o.Group = -1; E?.OnGroupChanged(g.Id, id, false); }
        if (lost && o.HasEntity) E?.OnObjectLost(id, o.Owner);
        E?.OnObjectRemoved(id);
    }
    public void MoveNow(int id, double x, double y) { var o = Live(id); if (o != null) { o.X = x; o.Y = y; } }

    // ------------------------------------------------------------------ IMissionHost
    public int Difficulty => Diff;
    public int HumanSlot => 0;
    public double Random() => Rng.Next();

    FakeRegion? Region(string? g) => g != null && Regions.TryGetValue(g, out var r) ? r : Regions.Values.FirstOrDefault(x => x.Name == g && g != null);
    bool InRegion(FakeObj o, FakeRegion? r) { if (r == null) return true; var (x, y) = PosOf(o); return r.Contains(x, y); }
    (double, double) PosOf(FakeObj o) { if (o.Inside >= 0 && O(o.Inside) is { } t) return (t.X, t.Y); return (o.X, o.Y); }
    static readonly HashSet<string> NotReal = new() { "OTHR", "FOOD", "FRUI", "SLOC", "WYPT", "QMRK", "GROU", "ITSP", "DMGL", "CFXE", "COLL", "" };

    public IReadOnlyList<int> Select(TriggerParams p, string prefix)
    {
        string G(string k, string d) { var v = p[prefix + k]; return string.IsNullOrEmpty(v) ? d : v; }
        var region = Region(G("rgn_guid", ""));
        var name = G("obj_name", "NA");
        if (name != "NA")
        {
            var rec = Objs.FirstOrDefault(o => o.Alive && o.Guid == G("obj_guid", "NA") && o.Guid.Length > 0) ?? ByName(name);
            if (rec == null) return Array.Empty<int>();
            var list = rec.Type == "GROU" ? rec.Members.Where(m => O(m)!.Alive).ToList() : new List<int> { rec.Id };
            return list.Where(m => InRegion(O(m)!, region)).ToList();
        }
        var ts = G("obj_type", "All");
        bool all = ts.Contains("All");
        var types = all ? null : ts.Split('|', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        long owner = TrigUtil.ToInt(G("obj_owner", "-2"));
        var cls = G("obj_class", "NA");
        var ex = G("exclude_class", "").Split('|').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0 && s != "na").ToHashSet();
        bool anyOwner = owner < -1, anyClass = cls == "NA";
        var res = new List<int>();
        foreach (var o in Objs)
        {
            if (!o.Alive) continue;
            if (!anyOwner && o.Owner != owner) continue;
            if (!anyClass && !string.Equals(o.Class, cls, StringComparison.OrdinalIgnoreCase)) continue;
            if (types != null && !types.Contains(o.Type)) continue;
            if (ex.Contains(o.Class.ToLowerInvariant())) continue;
            if (all && (anyOwner && anyClass ? !o.HasEntity : NotReal.Contains(o.Type))) continue;
            if (!InRegion(o, region)) continue;
            if (o.Type == "GROU") res.AddRange(o.Members.Where(m => O(m)!.Alive)); else res.Add(o.Id);
        }
        return res;
    }
    public bool Matches(int obj, TriggerParams p, string prefix)
    {
        var o = Live(obj);
        if (o == null) return false;
        string G(string k, string d) { var v = p[prefix + k]; return string.IsNullOrEmpty(v) ? d : v; }
        if (!InRegion(o, Region(G("rgn_guid", "")))) return false;
        var name = G("obj_name", "NA");
        if (name != "NA") return o.Guid == G("obj_guid", "NA") || o.Name == name;
        long owner = TrigUtil.ToInt(G("obj_owner", "-2"));
        if (owner >= -1 && o.Owner != owner) return false;
        var cls = G("obj_class", "NA");
        if (cls != "NA" && !string.Equals(o.Class, cls, StringComparison.OrdinalIgnoreCase)) return false;
        var types = G("obj_type", "All").Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (!types.Contains("All") && !types.Contains(o.Type)) return false;
        return !G("exclude_class", "").Split('|').Any(c => c.Trim().Length > 0 && string.Equals(c.Trim(), o.Class, StringComparison.OrdinalIgnoreCase));
    }
    public ObjectInfo? Info(int id)
    {
        var o = Live(id);
        if (o == null) return null;
        var (x, y) = PosOf(o);
        return new ObjectInfo
        {
            Id = o.Id, Guid = o.Guid, Name = o.Name, Class = o.Class, Type = o.Type, Owner = o.Owner, X = x, Y = y, Z = o.Z, Rot = o.Rot, HasEntity = o.HasEntity,
            IsUnit = o.IsUnit, IsBuilding = o.IsBuilding, Built = o.Built, Hp = o.Hp, MaxHp = o.MaxHp, Radius = o.Radius, Sight = o.Sight, Level = o.IsUnit ? o.Level : 0,
            Parked = o.Parked, Inside = o.Inside, Passengers = o.Passengers, Capacity = o.Capacity, GateState = o.GateState, Task = o.Task, CountsInPop = o.IsUnit,
            Produces = o.IsBuilding, WallLike = o.Class.Contains("wall"), IsItem = o.IsItem, CarriedItem = o.CarriedItem,
        };
    }
    public string? DataAttribute(int obj, string name) => Live(obj)?.Attr.GetValueOrDefault(name);
    public int FindObject(string g) => (Objs.FirstOrDefault(o => o.Alive && o.Guid == g && g.Length > 0) ?? ByName(g))?.Id ?? -1;
    public bool IsWorldRegion(string? g) => string.IsNullOrEmpty(g) || g == "UniqueWorldRegion" || Region(g) == null;
    public HashSet<string> MissingClasses = new();
    public bool IsMissingClass(string cls, int owner) => MissingClasses.Contains(cls.ToLowerInvariant());
    public int CountFinished(string cls, int owner, string? rgn) => Objs.Count(o => o.Alive && o.Owner == owner && string.Equals(o.Class, cls, StringComparison.OrdinalIgnoreCase) && (!o.IsBuilding || o.Built) && InRegion(o, Region(rgn)));
    public IReadOnlyList<int> PlayerObjects(int slot) => Objs.Where(o => o.Alive && o.Owner == slot && o.HasEntity).Select(o => o.Id).ToList();
    public int CountCharacters(int slot, int level) => Objs.Count(o => o.Alive && o.Owner == slot && o.Type == "CHTR" && o.Level == level);
    public bool UnitSlotFree(int slot, int level) { var p = Players[slot]; return p?.Pyramid == null || CountCharacters(slot, level) < p.Pyramid[level - 1]; }

    public int FindGroup(string? g) => g == null ? -1 : Objs.FirstOrDefault(o => o.Type == "GROU" && (o.Guid == g || o.Name == g))?.Id ?? -1;
    public IReadOnlyList<int> GroupMembers(int g) => O(g)?.Members.Where(m => O(m)!.Alive).ToList() ?? new List<int>();
    public int GroupOf(int obj) => Live(obj)?.Group ?? -1;
    public bool AddToGroup(int g, int obj)
    {
        var G = O(g); var o = Live(obj);
        if (G == null || o == null || o.Type == "GROU" || o.Group == g || G.Members.Count >= 140) return false;
        if (o.Group >= 0) RemoveFromGroup(obj);
        G.Members.Add(obj); o.Group = g;
        E?.OnGroupChanged(g, obj, true);
        return true;
    }
    public bool RemoveFromGroup(int obj)
    {
        var o = Live(obj); if (o == null || o.Group < 0) return false;
        var g = O(o.Group)!; g.Members.Remove(obj); o.Group = -1; E?.OnGroupChanged(g.Id, obj, false);
        return true;
    }

    public Dictionary<string, string> ClassTypes = new() { ["hu_fireplace"] = "BLDG", ["aje_tesla_tower"] = "BLDG" };
    public int Spawn(string cls, int owner, double x, double y, SpawnOptions o)
    {
        Rec($"Spawn {cls} {owner} {F(x)} {F(y)} L{o.Level}");
        var type = ClassTypes.TryGetValue(cls, out var t) ? t : cls.Contains("ship") ? "SHIP" : cls.StartsWith("hc_") ? "DCCO" : "CHTR";
        var n = Add(cls + "_" + nextId, cls, type, owner, x, y);
        n.Level = o.Level ?? 1; n.Rot = o.Rot;
        if (n.IsUnit && cls.Contains("transport")) n.Capacity = 4;
        if (o.Group >= 0) AddToGroup(o.Group, n.Id);
        E?.OnObjectSpawned(n.Id);
        return n.Id;
    }
    public int SpawnOutOf(int source, string cls, int level, int owner, int group, MapPos? target)
    {
        var s = O(source)!;
        Rec($"SpawnOut {cls} from {s.Name}");
        return Spawn(cls, owner, target?.X ?? s.X, target?.Y ?? s.Y, new SpawnOptions { Level = level, Group = group });
    }
    public void ApplyBuildup(int obj, string b) => Rec($"Buildup {obj} {b}");
    public bool Remove(int obj) { if (Live(obj) == null) return false; Rec($"Remove {Obj(obj).Name}"); KillNow(obj, false); return true; }
    public void Kill(int obj) { if (Live(obj) == null) return; Rec($"Kill {Obj(obj).Name}"); KillNow(obj, true); }
    public void SetOwner(int obj, int slot, double hl) { var o = Live(obj); if (o == null) return; o.Owner = slot; Rec($"Owner {o.Name} {slot}"); E?.OnOwnerChanged(obj); }
    public void SetAppearance(int obj, int flags) { var o = Live(obj); if (o == null) return; o.Flags = flags; o.Parked = (flags & 1) == 0; Rec($"Appear {o.Name} {flags}"); }
    public void SetHitpoints(int obj, double hp) { var o = Live(obj); if (o != null) o.Hp = hp; }
    public void SetLevel(int obj, int level) { var o = Live(obj); if (o != null) o.Level = level; }
    public void OrderMove(int obj, MapPos to, double speed, bool attack) => Rec($"{(attack ? "AttackMove" : "Move")} {Obj(obj).Name} {F(to.X)} {F(to.Y)}");
    public void OrderAttack(int obj, int target) => Rec($"Attack {Obj(obj).Name} {Obj(target).Name}");
    public void OrderStop(int obj) => Rec($"Stop {Obj(obj).Name}");
    public void OrderBuild(int w, int b) => Rec($"Build {Obj(w).Name} {Obj(b).Name}");
    public void OrderBoard(int u, int t) => Rec($"Board {Obj(u).Name} {Obj(t).Name}");
    public bool CanBoard(int u, int t) => true;
    public void UnloadAll(int t) { Rec($"Unload {Obj(t).Name}"); Obj(t).Passengers = 0; }
    public void EnterTransport(int u, int t) { Obj(u).Inside = t; Obj(t).Passengers++; Rec($"Enter {Obj(u).Class} {Obj(t).Name}"); }
    public void Teleport(int obj, double x, double y) { var o = Live(obj); if (o == null) return; o.X = x; o.Y = y; Rec($"Teleport {o.Name} {F(x)} {F(y)}"); }
    public void FaceTo(int obj, double x, double y) => Rec($"Face {Obj(obj).Name}");
    public HashSet<string> AnimsKnown = new() { "open", "close" };
    public bool PlayAnim(int obj, string name, int loops) { Rec($"Anim {Obj(obj).Name} {name} {loops}"); return AnimsKnown.Contains(name); }
    public double AnimLength(int obj, string name) => AnimsKnown.Contains(name) ? 1.5 : -1;
    public void SetInvulnerable(int obj, bool on, double s) => Rec($"Invulnerable {Obj(obj).Name} {on} {F(s)}");
    public void SetDisarmed(int obj, bool on, double s) => Rec($"Disarmed {Obj(obj).Name} {on}");
    public void SetGate(int obj, int state) => Rec($"Gate {Obj(obj).Name} {state}");
    public void SetRallyPoint(int obj, double x, double y) => Rec($"Rally {Obj(obj).Name}");
    public void GiveItem(int unit, int item) { Rec($"GiveItem {Obj(unit).Name} {Obj(item).Name}"); Obj(unit).CarriedItem = Obj(item).Class; }
    public bool Command(int obj, string c) { Rec($"Command {Obj(obj).Name} {c}"); return true; }
    public void PrepareUnboard(int obj, MapPos at) => Rec($"PrepareUnboard {Obj(obj).Name}");
    public void StartPatrol(int obj, IReadOnlyList<MapPos> pts, int mode, double speed, bool straight) { Patrols[obj] = (pts.ToList(), mode); Rec($"Patrol {Obj(obj).Name} {pts.Count} m{mode}"); }

    public bool PlayerExists(int s) => s >= 0 && s < 8 && Players[s] != null;
    public int? Relation(int a, int b) => PlayerExists(a) && PlayerExists(b) ? (a == b ? 2 : Players[a]!.Rel[b]) : null;
    public void SetRelation(int a, int b, int r) { Players[a]!.Rel[b] = r; Rec($"Relation {a} {b} {r}"); }
    public double? PlayerAttribute(int s, string n)
    {
        if (!PlayerExists(s)) return null;
        if (n == "units") return Objs.Count(o => o.Alive && o.Owner == s && o.IsUnit);
        return Players[s]!.Res.TryGetValue(n == "skulls" ? "iron" : n, out var v) ? v : null;
    }
    public void ChangeResource(int s, string n, string mod, bool cap)
    {
        var R = Players[s]!.Res; double cur = R.GetValueOrDefault(n), v = TrigUtil.Num(mod.TrimStart('+', '=', '-'));
        R[n] = mod.StartsWith('+') ? cur + v : mod.StartsWith('-') ? Math.Max(0, cur - v) : v;
    }
    public bool? HasTechFilter(int s, string f) => PlayerExists(s) ? Players[s]!.Filters.Contains(f.TrimStart('/').Replace("Filters/", "")) : null;
    public void SetTechFilter(int s, string path, bool on) { var f = path.TrimStart('/').Replace("Filters/", ""); if (on) Players[s]!.Filters.Add(f); else Players[s]!.Filters.Remove(f); }
    public void SetPopulationLimit(int s, int l) => Players[s]!.PopMax = l;
    public void SetUnitLimits(int s, int[] p) => Players[s]!.Pyramid = p;
    public void SetResourceCaps(int s, string f, string w, string st) { Players[s]!.Caps["food"] = f; Players[s]!.Caps["wood"] = w; Players[s]!.Caps["stone"] = st; }
    public void SetNeutralToAnimals(int s, int v) => Players[s]!.AnimalsNeutral = v;
    public bool SetRegionEnabled(string g, bool on, int sub)
    {
        var r = Region(g); if (r == null) return false;
        for (int i = 0; i < r.Shapes.Count; i++) if (sub < 0 || sub == i) r.Shapes[i] = r.Shapes[i] with { Enabled = on };
        Rec($"Region {r.Name} {on} {sub}");
        return true;
    }
    public bool MoveRegion(string g, double x, double y) { Rec($"MoveRegion {g}"); return Region(g) != null; }
    public bool EnableDisabledShapes(string g)
    {
        var r = Region(g); if (r == null || !r.Shapes.Any(s => !s.Enabled)) return false;
        for (int i = 0; i < r.Shapes.Count; i++) r.Shapes[i] = r.Shapes[i] with { Enabled = true };
        return true;
    }
    int reveals;
    public int RevealArea(int s, double x, double y, double radius, double sec, int follow) { Rec($"Reveal {s} {F(x)} {F(y)} r{F(radius)} {F(sec)}s{(follow >= 0 ? " follow " + Obj(follow).Name : "")}"); return ++reveals; }

    public bool AiBehavior(int s, string b, string m) { if (!PlayerExists(s) || s == 0) return false; Players[s]!.Behaviour = b; Rec($"AiBehavior {s} {b} {m}"); return true; }
    public bool AiAttack(int s, AiAttackOrder o) { Rec($"AiAttack {s} {o.Type} {o.Targets.Count}"); return PlayerExists(s); }
    public void AiAutoAttack(int s, IReadOnlyList<int> t) => Rec($"AiAutoAttack {s}");
    public void AiDefendArea(int s, string id, MapPos p, double r, int max) => Rec($"AiDefendArea {s} {id} {max}");
    public void AiLockUnits(int s, IReadOnlyList<int> u, bool l) => Rec($"AiLock {s} {u.Count} {l}");
    public void SetAggression(IReadOnlyList<int> u, int st) => Rec($"Aggression {u.Count} {st}");
    public void AiRegionMap(int s, string m, string r, double v, bool add) => Rec($"AiRegion {s} {m} {r} {F(v)}");
    public void AiModuleCommand(int s, string m, string c) => Rec($"AiCommand {s} {m} {c}");
    public void AiGameOver() => Rec("AiGameOver");

    public void QuestChanged(QuestState q, string change) => QuestLog.Add((q.Name, change));
    public void SetQuestionMark(string g, string st, string? tip) => QuestionMarks[g] = (st, tip);
    public void ShowMarker(MapMarker m) => Markers.Add(m);
    public void RemoveMarker(string id) => Markers.RemoveAll(m => m.Id == id);
    public void SetInfoBar(string text) => InfoBar = text;
    public void PlaySound(string name, MapPos? at) => Rec($"Sound {name}");
    public void PlayMusic(string file, bool loop) => Rec($"Music {file}");
    public void NextWeather() => Rec("Weather");
    public void PlayEffect(string path, double x, double y, int loops) => Rec($"Effect {path}");
    public bool IsBusy => Busy;
    public bool PlaySequence(SequenceRequest r) { Sequences.Add(r); Rec($"Sequence {TrigUtil.FilePart(r.Path)}"); return HoldSequences && !r.Path.Contains("missing"); }
    public void SequenceFinished(SequenceRequest r) => Rec($"SequenceEnd {TrigUtil.FilePart(r.Path)}");
    public bool PlayDialog(string path, int token) { Dialogs.Add((path, token)); Rec($"Dialog {TrigUtil.FilePart(path)}"); return HoldDialogs; }
    public void EndMission(bool won, string text) { Ended = (won, text); Rec($"End {won} {text}"); }
    public (double X, double Y, double Dist, double Yaw)? Camera { get; set; }
    public HashSet<int> Selected = new();
    public bool IsSelected(int obj) => Selected.Contains(obj);
    public IReadOnlyDictionary<string, string>? LoadProfile() => Profile;
    public void SaveProfile(IReadOnlyDictionary<string, string> v) => Profile = v.ToDictionary(x => x.Key, x => x.Value);

    /// <summary>Ends the shown sequence / dialogues (the UI's "skip").</summary>
    public void SkipAll()
    {
        foreach (var (_, tok) in Dialogs.ToArray()) E!.EndDialog(tok);
        Dialogs.Clear();
        E!.EndSequence();
    }

    /// <summary>Runs the engine for `seconds` of game time in 50 ms steps.</summary>
    public void Run(double seconds)
    {
        int n = (int)Math.Round(seconds / 0.05);
        for (int i = 0; i < n; i++) E!.Update(0.05);
    }
}
