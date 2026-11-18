// The object registry of a campaign mission: every object the triggers can address has a record (Rec) here -
// the placed units, buildings and scenery of the map, the groups, and everything created at run time (start army,
// produced units, spawned groups). Triggers address objects by GUID, by name, by handle (group member lists), by
// class + owner + type, and by region (remake/docs/spec/triggers.md §2); Select() implements that parameter block.
//
// A record outlives its object: when the object dies or is deleted, rec.Alive becomes false and the record leaves
// the lookup tables ("an object that is not found" = dead, as the DEAD condition needs it).
//
// The trigger engine (IMissionHost) addresses records by Id: a number given out when the record joins the registry,
// never reused within a mission.
//
// Ported from remake/src/game/campaign/registry.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Campaign;

public sealed class Rec
{
    static readonly HashSet<string> UNIT_TYPES = new(StringComparer.Ordinal) { "CHTR", "ANML", "VHCL", "SHIP" };
    /// <summary>the registry's id (0 = not in the registry)</summary>
    public int Id;
    public readonly string? Guid;
    public readonly string Name;
    /// <summary>class as written in the data (Cole_s0, hu_worker)</summary>
    public string Cls;
    public string Lc;
    /// <summary>CHTR | ANML | VHCL | SHIP | BLDG | DCCO | GROU | ...</summary>
    public string Type;
    /// <summary>position in the map's object list (-1: created at run time)</summary>
    public readonly int Index;
    /// <summary>[index, serial] of the original handle manager</summary>
    public readonly int[]? Handle;
    /// <summary>the JSON object (placed objects)</summary>
    public readonly JsonNode? Data;
    /// <summary>world Unit / Building (rec.Entity.Rec == rec)</summary>
    public Entity? Entity;
    /// <summary>a mission played with another tribe: the object was the player's (its class the chosen tribe's) and the
    /// mission gave it to another player (OCPY) - class queries keep finding it as the mission's class (Registry.ClassIs)</summary>
    public bool FromHuman;
    /// <summary>scenery, items, non-tech-tree objects</summary>
    public Prop? Prop;
    /// <summary>type GROU: its Group</summary>
    public Group? GroupObj;
    /// <summary>the Group this object is a member of (at most one, GroupObj.usl:93)</summary>
    public Group? Group;
    /// <summary>type NEST: the world's nest (World.WildNests) or null</summary>
    public WildNest? Nest;
    /// <summary>an addressed tree / stone / bush of the map: its resource node</summary>
    public ResNode? Node;
    /// <summary>a placed ShowFOW_Obj: its reveal</summary>
    public Reveal? Reveal;
    public bool Alive = true;
    /// <summary>invulnerable from the map's object flag (Campaign.ApplyState) and not yet changed by a trigger</summary>
    public bool MapInvuln;
    /// <summary>false: in the data but not in the world (model missing, hidden helper)</summary>
    public bool Placed = true;
    /// <summary>OBAP flags</summary>
    public bool Visible = true, Hitable = true, Selectable = true;
    /// <summary>all OBAP bits as last set (null: never set)</summary>
    public int? Flags;
    /// <summary>in the registry's tables</summary>
    public bool Listed;
    /// <summary>ACDO SetAnim on an object whose model has no such animation (kept for a later model)</summary>
    public string? AnimWanted;
    internal int _slot;
    internal double _x, _y;

    public Rec(string? guid, string name, string cls, string? type, int index = -1, int[]? handle = null, int slot = -1, double x = 0, double y = 0, JsonNode? data = null)
    {
        Guid = string.IsNullOrEmpty(guid) ? null : guid;
        Name = name;
        Cls = cls ?? "";
        Lc = Cls.ToLowerInvariant();
        Type = type ?? "";
        Index = index;
        Handle = handle;
        Data = data;
        _slot = slot;
        _x = x; _y = y;
    }
    /// <summary>owner slot: 0-7, -1 = nobody</summary>
    public int Slot => Entity != null ? Entity.Owner?.Id ?? -1 : _slot;
    public Player? Owner => Entity?.Owner;
    public bool IsUnit => UNIT_TYPES.Contains(Type);
    /// <summary>0-based level as the triggers count it</summary>
    public int Level0 => Entity is Unit u ? u.Level - 1 : 0;
    /// <summary>the object became another class (the player's objects in a mission played with another tribe, tribes.js)</summary>
    public void SetClass(string cls, string? type) { Cls = cls; Lc = cls.ToLowerInvariant(); if (!string.IsNullOrEmpty(type)) Type = type; }
    /// <summary>a raw attribute of the map data (objects[].attr), or null</summary>
    public string? Attr(string name) => Data?["attr"]?[name] is JsonNode n ? n.Str() : null;
    public override string ToString() => $"{Name} ({Cls}{(Alive ? "" : ", gone")})";
}

public sealed class Group
{
    public readonly Rec Rec;
    public readonly string? Guid;
    public readonly string Name;
    public readonly PwRemake.Sim.World.OrderedSet<Rec> Members = new();
    public Group(Rec rec) { Rec = rec; Guid = rec.Guid; Name = rec.Name; }
    public int Size => Members.Count;
    public List<Rec> List() => Members.ToList();
}

/// <summary>A generic query of the registry (Registry.Query).</summary>
public sealed class RecQuery
{
    public Region? Region;
    public ISet<string>? Types;
    /// <summary>slot, -1 nobody; null any</summary>
    public int? Owner;
    public string? Cls;
    public List<string>? Exclude;
    public Func<Rec, bool>? Filter;
}

public sealed class Registry
{
    // object types an "All" query never returns (ConditionFactory.usl:619-627: no OTHR, no food objects), plus the
    // editor's helper objects that are not things of the world
    static readonly HashSet<string> NOT_REAL = new(StringComparer.Ordinal) { "OTHR", "FOOD", "FRUI", "SLOC", "WYPT", "QMRK", "GROU", "ITSP", "DMGL", "CFXE", "COLL", "" };

    readonly Campaign C;
    public readonly Regions Regions;
    /// <summary>live records in creation order (the order queries return)</summary>
    public List<Rec> All = new();
    readonly Dictionary<string, Rec> _guids = new(StringComparer.Ordinal), _names = new(StringComparer.Ordinal), _handles = new(StringComparer.Ordinal);
    readonly Dictionary<int, Rec> _indices = new(), _ids = new();
    /// <summary>run-time names: class -> next number</summary>
    readonly Dictionary<string, int> _counter = new(StringComparer.Ordinal);
    bool _dirty;
    int _nextId = 1;

    public Registry(Campaign c, Regions regions) { C = c; Regions = regions; }

    // ------------------------------------------------------------------ classes
    /// <summary>does the record's object count as class `lc` (lower case)? Its own class, or for the player's objects in
    /// a mission played with another tribe the chosen tribe's equivalent of the class the mission names (tribes.js) - also
    /// once the mission has given such an object to another player (Rec.FromHuman: BoosterPack 1 mission 4-1 hands the
    /// player's flamethrowers to the fugitives, OCPY, and counts "hu_flamethrower of player 5"; as another tribe they
    /// were never counted and the quest could not be finished)</summary>
    public bool ClassIs(Rec rec, string lc)
    {
        if (rec.Lc == lc) return true;
        var S = C.Swap;
        if (S != null && (rec.Slot == C.HumanSlot || rec.FromHuman) && S.CountsAs(lc, rec.Lc)) return true;
        // a co-op seat in another tribe: its own equivalents (docs/COOP.md)
        var cs = C.Coop?.SeatOfSlot(rec.Slot);
        return cs is { Seat: > 1, Swap: { } ss } && ss.CountsAs(lc, rec.Lc);
    }

    /// <summary>the classes of a query's obj_class, lower case: one class (the original maps: "hu_worker") or a
    /// `|`-separated list with a trailing `|` (the maps MIRAGE edited: "hu_stone_cottage|", "ninigi_corsair|
    /// ninigi_transport_boat|" - written like obj_type and exclude_class; read as one class each, the original's maps
    /// of MIRAGE work in its game only so); null for none. (Not in the JS: its campaign data had no such lists.)</summary>
    public static string[]? ClassList(string? cls)
    {
        if (string.IsNullOrEmpty(cls)) return null;
        if (cls.IndexOf('|') < 0) return new[] { cls.ToLowerInvariant() };
        var l = cls.Split('|').Select(c => c.Trim().ToLowerInvariant()).Where(c => c.Length > 0 && c != "na").ToArray();
        return l.Length > 0 ? l : null;
    }
    bool ClassIsAny(Rec rec, string[] cls) { foreach (var c in cls) if (ClassIs(rec, c)) return true; return false; }

    // ------------------------------------------------------------------ records
    public Rec Add(Rec rec)
    {
        rec.Listed = true;
        if (rec.Id == 0) rec.Id = _nextId++;
        _ids[rec.Id] = rec;
        All.Add(rec);
        if (rec.Guid != null) _guids[rec.Guid] = rec;
        if (!string.IsNullOrEmpty(rec.Name)) _names.TryAdd(rec.Name, rec);
        if (rec.Handle != null && rec.Handle.Length >= 2) _handles[rec.Handle[0] + ":" + rec.Handle[1]] = rec;
        if (rec.Index >= 0) _indices[rec.Index] = rec;
        return rec;
    }
    /// <summary>a new name of the form the editor uses: &lt;class&gt;_&lt;n&gt;, n from 0, skipping names that exist</summary>
    public string NewName(string cls)
    {
        int n = _counter.TryGetValue(cls, out var c) ? c : 0;
        while (_names.ContainsKey(cls + "_" + n)) n++;
        _counter[cls] = n + 1;
        return cls + "_" + n;
    }
    /// <summary>record of a world entity created at run time (called for every unit / building the world creates)</summary>
    public Rec Adopt(Entity e)
    {
        if (e.Rec != null) return e.Rec;
        var rec = new Rec(null, NewName(e.Name), e.Name, e is Building ? "BLDG" : e.Cls);
        rec.Entity = e; e.Rec = rec;
        return Add(rec);
    }
    /// <summary>co-op: the GUID and name of a removed object stand for another one from now on (a gate in place of wall
    /// pieces: the triggers' conditions on the pieces watch the gate)</summary>
    public void Alias(Rec old, Rec to)
    {
        if (old.Guid != null) _guids[old.Guid] = to;
        _names[old.Name] = to;
    }
    /// <summary>the object is gone (died, destroyed, deleted): out of the tables and out of its group</summary>
    public void Drop(Rec? rec)
    {
        if (rec == null || !rec.Alive) return;
        rec.Alive = false;
        if (rec.Guid != null && _guids.TryGetValue(rec.Guid, out var g) && g == rec) _guids.Remove(rec.Guid);
        if (_names.TryGetValue(rec.Name, out var n) && n == rec) _names.Remove(rec.Name);
        if (rec.Handle != null && rec.Handle.Length >= 2) _handles.Remove(rec.Handle[0] + ":" + rec.Handle[1]);
        if (rec.Index >= 0) _indices.Remove(rec.Index);
        _dirty = true;
        if (rec.Group != null) RemoveFromGroup(rec);
        C.OnRemoved(rec);
    }
    /// <summary>Drop() only marks; the list is compacted here (once per step) so that loops over All stay valid</summary>
    public void Compact() { if (_dirty) { All = All.Where(r => r.Alive).ToList(); _dirty = false; } }

    // ------------------------------------------------------------------ lookups (live objects only)
    public Rec? ByGuid(string? g) => !string.IsNullOrEmpty(g) && _guids.TryGetValue(g, out var r) ? r : null;
    public Rec? ByName(string? n) => !string.IsNullOrEmpty(n) && _names.TryGetValue(n, out var r) ? r : null;
    public Rec? ByHandle(int index, int serial) => _handles.TryGetValue(index + ":" + serial, out var r) ? r : null;
    public Rec? ByIndex(int i) => _indices.TryGetValue(i, out var r) ? r : null;
    /// <summary>a record by its id, alive or not (null: no such id)</summary>
    public Rec? ById(int id) => _ids.TryGetValue(id, out var r) ? r : null;
    public static Rec? Of(Entity? e) => e?.Rec;
    /// <summary>a live record from a GUID or a name</summary>
    public Rec? Get(string? r) => ByGuid(r) ?? ByName(r);
    public static Rec? Get(Rec? r) => r != null && r.Alive ? r : null;
    public static Rec? Get(Entity? e) => e?.Rec is { Alive: true } r ? r : null;
    /// <summary>a Group by GUID or name</summary>
    public Group? GroupOf(string? r) => Get(r)?.GroupObj;

    // ------------------------------------------------------------------ groups (GroupObj.usl)
    public bool AddToGroup(Group? g, Rec? rec)
    {
        rec = Get(rec);
        if (g == null || rec == null || rec.GroupObj != null || rec.Group == g) return false;    // a group cannot contain a group (GO:85)
        if (g.Members.Count >= 140) return false;                                               // GO:7
        if (rec.Group != null) RemoveFromGroup(rec);                                            // a unit is in at most one group (GO:93)
        g.Members.Add(rec); rec.Group = g;
        C.OnGroup(g, rec, true);
        return true;
    }
    public bool RemoveFromGroup(Rec? rec)
    {
        var g = rec?.Group;
        if (g == null) return false;
        g.Members.Delete(rec!); rec!.Group = null;
        C.OnGroup(g, rec, false);
        return true;
    }

    // ------------------------------------------------------------------ queries
    /// <summary>position of a record in game coordinates (a passenger is where its transport is)</summary>
    public (double X, double Z) PosOf(Rec rec)
    {
        var e = rec.Entity;
        if (e != null) { var t = e is Unit u && u.Inside ? C.TransportOf(u) : null; var p = t != null ? t.Pos : e.Pos; return (p.X, p.Z); }
        if (rec.Prop != null) { var c = C.World?.ItemOfProp(rec.Prop)?.Carrier; var p = c != null ? c.Pos : rec.Prop.Pos; return (p.X, p.Z); }   // (a carried item: where its carrier is)
        if (rec.Node != null) return (rec.Node.Pos.X, rec.Node.Pos.Z);
        return C.ToGame(rec._x, rec._y);
    }
    public bool InRegion(Rec rec, Region? region)
    {
        if (region == null || region.World) return true;
        var (x, z) = PosOf(rec);
        return region.Contains(x, z);
    }
    /// <summary>generic filter: region, types, owner (slot, -1 nobody, null any), cls, exclude (classes), filter(rec).
    /// Classes compare without case.</summary>
    bool ExcludedClass(Rec r, List<string> ex) { foreach (var c in ex) if (ClassIs(r, c)) return true; return false; }
    public List<Rec> Query(RecQuery q)
    {
        var cls = ClassList(q.Cls);
        var ex = q.Exclude is { Count: > 0 } ? q.Exclude.Select(c => c.ToLowerInvariant()).Distinct().ToList() : null;
        var res = new List<Rec>();
        foreach (var r in All)
        {
            if (!r.Alive || !r.Placed) continue;
            if (r.Entity != null && !r.Entity.Alive) continue;     // dying: its "died" event is handled at the end of the step
            if (q.Owner != null && r.Slot != q.Owner && !(C.Coop?.OwnerMatches(r.Slot, q.Owner.Value) ?? false)) continue;     // (co-op: "the player" = every seat)
            if (cls != null && !ClassIsAny(r, cls)) continue;
            // the type of an equivalent can differ (the Dustriders' resource collector is an animal, the fireplace a building)
            if (q.Types != null && !q.Types.Contains(r.Type) && !(cls != null && Array.IndexOf(cls, r.Lc) < 0)) continue;
            if (ex != null && ExcludedClass(r, ex)) continue;      // (no lambda here: its capture of r cost an allocation per record)
            if (q.Filter != null && !q.Filter(r)) continue;
            if (q.Region != null && !InRegion(r, q.Region)) continue;
            res.Add(r);
        }
        return res;
    }

    static string G(TriggerParams p, string prefix, string k, string d) { var v = p[prefix + k]; return string.IsNullOrEmpty(v) ? d : v; }

    /// <summary>the object-query parameter block of conditions and actions (triggers.md §2.1; CObjFinder.MakeQuery,
    /// ConditionFactory.usl:530-663). p = the node's parameters (defaults need not be applied), prefix = "" | "dst_" |
    /// "B_" | "trgt_" | "sub_". Returns live records in creation order; groups are replaced by their members.</summary>
    public List<Rec> Select(TriggerParams p, string prefix = "")
    {
        var region = Regions.Get(G(p, prefix, "rgn_guid", ""));
        var name = G(p, prefix, "obj_name", "NA");
        if (name != "NA")
        {
            // by name: GUID first, then the name (CF:535-539); a group = its members (CF:543-551); only the region filters
            var rec = ByGuid(G(p, prefix, "obj_guid", "NA")) ?? ByName(name);
            if (rec == null) return new List<Rec>();
            var list = rec.GroupObj != null ? rec.GroupObj.List().Where(m => m.Alive).ToList() : new List<Rec> { rec };
            return region.World ? list : list.Where(r => InRegion(r, region)).ToList();
        }
        var ts = G(p, prefix, "obj_type", "All");
        bool all = ts.Contains("All");             // CF:568 Find("All"): "AllNC" counts as All
        var types = all ? null : new HashSet<string>(ts.Split('|', StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);
        long owner = TrigUtil.ToInt(G(p, prefix, "obj_owner", "-2"));
        var cls = G(p, prefix, "obj_class", "NA");
        var ex = G(p, prefix, "exclude_class", "").Split('|').Select(s => s.Trim()).Where(s => s.Length > 0 && s != "NA").ToList();
        bool anyOwner = !(owner >= -1), anyClass = cls == "NA";
        var res = Query(new RecQuery
        {
            Region = region, Types = types, Owner = anyOwner ? null : (int)owner, Cls = anyClass ? null : cls, Exclude = ex,
            // "All" = all real things (CF:619-639). Without an owner and a class it would return every piece of scenery of
            // the region; no trigger means that (triggers.md §2.1), so such a query is limited to units and buildings.
            Filter = all ? (anyOwner && anyClass ? r => r.Entity != null : r => !NOT_REAL.Contains(r.Type)) : null,
        });
        var outList = new List<Rec>();
        foreach (var r in res) { if (r.GroupObj != null) { foreach (var m in r.GroupObj.Members) if (m.Alive) outList.Add(m); } else outList.Add(r); }
        return outList;
    }

    /// <summary>"does this one object match the query" (CObjFinder.Contains, CF:721-757; used by ISFG): a group named in
    /// the query does NOT match its members, and "AllNC" is no wildcard here (triggers.md §2.2)</summary>
    public bool Matches(Rec? rec, TriggerParams p, string prefix = "")
    {
        rec = Get(rec);
        if (rec == null) return false;
        if (!InRegion(rec, Regions.Get(G(p, prefix, "rgn_guid", "")))) return false;
        var name = G(p, prefix, "obj_name", "NA");
        if (name != "NA") return rec.Guid == G(p, prefix, "obj_guid", "NA") || rec.Name == name;
        long owner = TrigUtil.ToInt(G(p, prefix, "obj_owner", "-2"));
        if (owner >= -1 && rec.Slot != owner && !(C.Coop?.OwnerMatches(rec.Slot, (int)owner) ?? false)) return false;
        var cls = G(p, prefix, "obj_class", "NA") is var c0 && c0 == "NA" ? null : ClassList(c0);
        if (cls != null && !ClassIsAny(rec, cls)) return false;
        var types = G(p, prefix, "obj_type", "All").Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (!types.Contains("All") && !types.Contains(rec.Type) && !(cls != null && Array.IndexOf(cls, rec.Lc) < 0)) return false;
        var ex = G(p, prefix, "exclude_class", "").Split('|').Select(s => s.Trim().ToLowerInvariant()).Where(s => s.Length > 0);
        return !ex.Any(c => ClassIs(rec, c));
    }
}
