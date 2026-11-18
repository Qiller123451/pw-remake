// The mission API of the remake's own campaigns (docs/SEQUEL.md "Mission API"): what a mission.lua sees. A mission
// file returns a table of handlers; the runtime calls them by name with `M` (this table of functions) and an argument
// table: setup, tick (every 0.5 s), died, evacuated, rescued, captured, asleep, loaded, netted, plantTaken,
// riftClosed, arrived_<piece>, done_<objective>, and the mission's own timer names (M.after(seconds, name, args)).
//
//   return {
//     setup = function(M) M.objective("evac", "Lead the townsfolk to the buses", { of = 8, civ = true }) M.after(70, "rift") end,
//     rift = function(M) M.rift("mine", 170, -60, { every = 50, waves = { { {"Velociraptor", 3} } } }) end,
//     evacuated = function(M, a) M.progress("evac", a.n) end,
//   }
//
// Objects are numbers (entity ids), roles are names ("me", "outbreak", "civilians", "crane", "aje", "foe", "foe2",
// "ally"), positions game metres (x east, z south, from the map's centre). M.state is the mission's own table (kept in
// the Lua state: a saved game replays the mission, so it comes back the same). The random numbers (math.random,
// M.random) are the world's. The full list: docs/SEQUEL.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using MoonSharp.Interpreter;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Sequel;

public sealed class SequelLua : ISequelScript
{
    static readonly Logger L = Log.For("Sequel.Lua");
    public readonly LuaHost Lua;
    readonly SequelRuntime R;
    Table? _handlers;
    public Table M = null!;
    public readonly List<string> Errors;

    SequelLua(LuaHost lua, SequelRuntime r) { Lua = lua; R = r; Errors = lua.Errors; }

    /// <summary>loads mission.lua of the folder (with the standard library: require "sequel" / "scenarios") and binds it
    /// to the runtime; errors are logged (Errors) and the mission goes on without the broken part</summary>
    public static SequelLua Load(string folder, SequelRuntime r, string? stdDir = null)
    {
        var host = new LuaHost(stdDir ?? LuaMission.DefaultStdDir(), folder) { Random = () => r.W.Rng.Next() };
        var s = new SequelLua(host, r);
        s.M = s.MakeApi();
        host.Lua.Globals["M"] = s.M;
        var file = Path.Combine(folder, "mission.lua");
        if (File.Exists(file))
        {
            var res = host.RunString(File.ReadAllText(file), "mission.lua");
            if (res?.Type == DataType.Table) s._handlers = res.Table;
            else if (res != null) L.Error($"{folder}/mission.lua must return a table of handlers");
        }
        else L.Warn($"no mission.lua in {folder}");
        r.Script = s;
        return s;
    }

    public bool Has(string name) => _handlers?.Get(name).Type == DataType.Function;

    public void Call(string name, IReadOnlyDictionary<string, object?>? args = null)
    {
        if (_handlers == null) return;
        var f = _handlers.Get(name);
        if (f.Type != DataType.Function) return;
        var t = new Table(Lua.Lua);
        if (args != null) foreach (var (k, v) in args) t[k] = ToLua(v);
        Lua.Call(f, $"mission.lua {name}", M, t);
    }

    DynValue ToLua(object? v) => v switch
    {
        null => DynValue.Nil,
        double d => DynValue.NewNumber(d),
        int i => DynValue.NewNumber(i),
        long l => DynValue.NewNumber(l),
        bool b => DynValue.NewBoolean(b),
        string s => DynValue.NewString(s),
        Entity e => DynValue.NewNumber(e.Id),
        DynValue dv => dv,
        _ => DynValue.NewString(v.ToString() ?? ""),
    };

    /// <summary>a JSON value as Lua (mission.json "def" for the script)</summary>
    public static DynValue FromJson(Script S, JsonNode? n)
    {
        switch (n)
        {
            case null: return DynValue.Nil;
            case JsonObject o: { var t = new Table(S); foreach (var (k, v) in o) t[k] = FromJson(S, v); return DynValue.NewTable(t); }
            case JsonArray a: { var t = new Table(S); int i = 1; foreach (var v in a) t[i++] = FromJson(S, v); return DynValue.NewTable(t); }
            case JsonValue v:
                return v.GetValueKind() switch
                {
                    System.Text.Json.JsonValueKind.Number => DynValue.NewNumber(v.Num()),
                    System.Text.Json.JsonValueKind.True => DynValue.True,
                    System.Text.Json.JsonValueKind.False => DynValue.False,
                    _ => DynValue.NewString(v.Str()),
                };
        }
        return DynValue.Nil;
    }

    // ------------------------------------------------------------------------------------------------- the API
    Table MakeApi()
    {
        var S = Lua.Lua;
        var W = R.W;
        var api = new Table(S);
        static DynValue Fn(Func<CallbackArguments, DynValue> f) => DynValue.NewCallback((c, a) => f(a));
        DynValue Nil = DynValue.Nil;
        string Str(CallbackArguments a, int i, string what) => a.Count > i && LuaHost.ParamString(a[i]) is { } s ? s : throw new ScriptRuntimeException($"M.{what}: argument {i + 1} must be a string");
        string? OptStr(CallbackArguments a, int i) => a.Count > i && !a[i].IsNil() ? LuaHost.ParamString(a[i]) : null;
        double Num(CallbackArguments a, int i, string what, double? def = null)
        {
            if (a.Count > i && a[i].Type == DataType.Number) return a[i].Number;
            if (def != null && (a.Count <= i || a[i].IsNil())) return def.Value;
            throw new ScriptRuntimeException($"M.{what}: argument {i + 1} must be a number");
        }
        Table? Opt(CallbackArguments a, int i) => a.Count > i && a[i].Type == DataType.Table ? a[i].Table : null;
        double ONum(Table? t, string k, double def) => t?.Get(k) is { Type: DataType.Number } v ? v.Number : def;
        bool OBool(Table? t, string k) => t != null && t.Get(k).CastToBool();
        string? OStr(Table? t, string k) => t?.Get(k) is { Type: DataType.String } v ? v.String : null;
        DynValue Ids(IEnumerable<Entity> es) { var t = new Table(S); int i = 1; foreach (var e in es) t[i++] = e.Id; return DynValue.NewTable(t); }
        Entity? Ent(DynValue v) => v.Type == DataType.Number ? W.ById((int)v.Number) : null;
        Entity? EntArg(CallbackArguments a, int i) => a.Count > i ? Ent(a[i]) : null;
        IEnumerable<Entity> EntList(DynValue v)
        {
            if (v.Type == DataType.Number) { if (W.ById((int)v.Number) is { } e) yield return e; }
            else if (v.Type == DataType.Table) foreach (var x in v.Table.Values) if (Ent(x) is { } e) yield return e;
        }
        // a class or a list of classes (the first the data has: M.cls)
        string Cls(DynValue v) => v.Type == DataType.Table ? R.Cls(v.Table.Values.Select(x => x.CastToString() ?? "").ToList()) : v.CastToString() ?? "";
        // a point {x, z} or an entity id -> (pos, entity)
        (double[]? P, Entity? E) Target(DynValue v)
        {
            if (v.Type == DataType.Number) return (null, W.ById((int)v.Number));
            if (v.Type == DataType.Table) { var t = v.Table; var x = t.Get(1); var z = t.Get(2); if (x.Type == DataType.Number) return (new[] { x.Number, z.Number }, null); if (t.Get("x").Type == DataType.Number) return (new[] { t.Get("x").Number, t.Get("z").Number }, null); }
            return (null, null);
        }

        // ---- what the script reads: info tables
        Table Info(Entity e)
        {
            var t = new Table(S);
            var m = R.MarkOf(e);
            t["id"] = e.Id; t["name"] = e.Name; t["cls"] = e.Cls; t["role"] = R.RoleOf(e.Owner) is { } ro ? DynValue.NewString(ro) : DynValue.Nil;
            t["x"] = e.Pos.X; t["z"] = e.Pos.Z; t["y"] = e.Pos.Y; t["hp"] = e.Hp; t["maxhp"] = e.MaxHp; t["alive"] = e.Alive; t["level"] = e.Level;
            t["unit"] = e is Unit; t["building"] = e is Building; t["radius"] = e.Radius;
            t["parked"] = e.Parked; t["inside"] = e.Inside; t["untargetable"] = e.Untargetable;
            if (e is Unit u) { t["worker"] = u.IsWorker; t["task"] = u.Task.Type; t["size"] = u.Stats?.Size ?? 0; }
            if (e is Building b) { t["built"] = b.Built; t["powered"] = R.Powered(b); t["workers"] = b.Workers.Count; }
            if (m != null)
            {
                if (m.Tag != null) t["tag"] = m.Tag;
                t["asleep"] = m.Asleep > R.Time; t["rescued"] = m.Rescued; t["hiding"] = m.Hiding; t["envoy"] = m.Envoy; t["intown"] = m.InTown;
                if (m.Rift != null) t["rift"] = m.Rift;
            }
            return t;
        }
        // a filter: nil, a class name, a list of class names, a table {name=, tag=, worker=, cls=, built=}, or function(info)
        Func<Entity, bool> Filter(CallbackArguments a, int i)
        {
            if (a.Count <= i || a[i].IsNil()) return _ => true;
            var f = a[i];
            if (f.Type == DataType.String) { var n = f.String; return e => e.Name == n; }
            if (f.Type == DataType.Function) return e => Lua.Call(f, "M filter", Info(e)) is { } r && r.CastToBool();
            if (f.Type == DataType.Table)
            {
                var t = f.Table;
                if (t.Length > 0) { var names = t.Values.Select(x => x.CastToString()).ToHashSet(); return e => names.Contains(e.Name); }
                string? name = OStr(t, "name"), tag = OStr(t, "tag"), cls = OStr(t, "cls");
                var worker = t.Get("worker"); var built = t.Get("built"); var envoy = t.Get("envoy");
                return e => (name == null || e.Name == name) && (tag == null || R.MarkOf(e)?.Tag == tag || R.Classes.Tagged(e.Name, tag)) && (cls == null || e.Cls == cls)
                            && (worker.IsNil() || (e is Unit u && u.IsWorker) == worker.CastToBool())
                            && (built.IsNil() || (e is Building b && b.Built) == built.CastToBool())
                            && (envoy.IsNil() || (R.MarkOf(e)?.Envoy ?? false) == envoy.CastToBool());
            }
            return _ => true;
        }
        Player? Role(CallbackArguments a, int i) => a.Count > i && !a[i].IsNil() ? R.PlayerOf(a[i].CastToString()) : null;
        bool OfRole(Entity e, CallbackArguments a, int i) => a.Count <= i || a[i].IsNil() ? e.Owner == null : e.Owner == R.PlayerOf(a[i].CastToString());

        // ---- general
        var meta = new Table(S);
        meta["__index"] = DynValue.NewCallback((c, a) =>
        {
            var k = a[1].CastToString();
            return k switch
            {
                "time" => DynValue.NewNumber(R.Time),
                "cp" => DynValue.NewNumber(R.Cp),
                "diff" => DynValue.NewNumber(R.Diff),
                "night" => DynValue.NewBoolean(R.NightOn),
                "game_time" => DynValue.NewNumber(W.Time),
                _ => DynValue.Nil,
            };
        });
        api.MetaTable = meta;
        api["id"] = R.Id; api["kind"] = R.Kind; api["tribe"] = R.Tribe; api["difficulty"] = R.Difficulty;
        api["def"] = FromJson(S, R.Def);
        api["state"] = new Table(S);
        api["call"] = Fn(a => { R.Call(Str(a, 0, "call"), ArgsOf(Opt(a, 1))); return Nil; });
        api["after"] = Fn(a => { R.After(Num(a, 0, "after"), Str(a, 1, "after"), ArgsOf(Opt(a, 2))); return Nil; });
        api["say"] = Fn(a => { R.Say(Str(a, 0, "say"), Str(a, 1, "say")); return Nil; });
        api["notify"] = Fn(a => { R.Notify(Str(a, 0, "notify"), OptStr(a, 1) ?? ""); return Nil; });
        api["log"] = Fn(a => { L.Info(string.Join(" ", a.GetArray().Select(x => x.ToPrintString()))); return Nil; });
        api["random"] = Fn(a => DynValue.NewNumber(W.Rng.Next()));
        api["height"] = Fn(a => DynValue.NewNumber(W.Height(Num(a, 0, "height"), Num(a, 1, "height"))));
        api["win"] = Fn(a => { R.Win(OptStr(a, 0)); return Nil; });
        api["lose"] = Fn(a => { R.Lose(OptStr(a, 0)); return Nil; });
        api["grant"] = Fn(a => { R.Grant(Num(a, 0, "grant")); return Nil; });
        api["count"] = Fn(a => DynValue.NewNumber(R.Count(Str(a, 0, "count"), Num(a, 1, "count", 1))));
        api["counter"] = Fn(a => DynValue.NewNumber(R.Counter(Str(a, 0, "counter"))));
        api["introduce"] = Fn(a => { R.Introduce(Str(a, 0, "introduce"), a.Count > 1 && a[1].CastToBool()); return Nil; });
        api["mark"] = Fn(a => { R.AddMarker(Num(a, 0, "mark"), Num(a, 1, "mark"), Str(a, 2, "mark"), (int)Num(a, 3, "mark", 0xffd040), Num(a, 4, "mark", 1e9)); return Nil; });
        api["unmark"] = Fn(a => { R.Unmark(Str(a, 0, "unmark")); return Nil; });
        api["set_night"] = Fn(a => { R.SetNight(a.Count > 0 && a[0].CastToBool()); return Nil; });
        api["fx"] = Fn(a => { double x = Num(a, 1, "fx"), z = Num(a, 2, "fx"); R.Fx(Str(a, 0, "fx"), x, W.Height(x, z) + Num(a, 4, "fx", 0), z, Num(a, 3, "fx", 1)); return Nil; });
        // the mission clock the HUD shows: a countdown to `at` (game seconds of the mission; 0 or nil: none) with a label
        api["set_clock"] = Fn(a => { R.ClockAt = a.Count > 0 && a[0].Type == DataType.Number ? a[0].Number : 0; R.ClockLabel = OptStr(a, 1) ?? ""; return Nil; });
        api["shake"] = Fn(a => { W.Emit("sqshake", new Args { ["strength"] = Num(a, 0, "shake", 10) }); return Nil; });

        // ---- objectives
        api["objective"] = Fn(a =>
        {
            var o = Opt(a, 2);
            var ob = R.Objective(Str(a, 0, "objective"), Str(a, 1, "objective"), ONum(o, "of", 0), OBool(o, "secondary"), OBool(o, "civ"), o != null && !o.Get("of").IsNil());
            if (OBool(o, "hidden")) ob.Hidden = true;
            return Nil;
        });
        api["progress"] = Fn(a => { R.Progress(Str(a, 0, "progress"), Num(a, 1, "progress")); return Nil; });
        api["complete"] = Fn(a => { R.Complete(Str(a, 0, "complete")); return Nil; });
        api["fail"] = Fn(a => { R.Fail(Str(a, 0, "fail")); return Nil; });
        api["is_done"] = Fn(a => DynValue.NewBoolean(R.IsDone(Str(a, 0, "is_done"))));
        api["get_objective"] = Fn(a =>
        {
            var o = R.Objectives.FirstOrDefault(x => x.Id == Str(a, 0, "get_objective"));
            if (o == null) return Nil;
            var t = new Table(S); t["id"] = o.Id; t["text"] = o.Text; t["done"] = o.Done; t["failed"] = o.Failed; t["count"] = o.Count; t["of"] = o.Of; t["secondary"] = o.Secondary; t["note"] = o.Note;
            return DynValue.NewTable(t);
        });
        api["set_note"] = Fn(a => { var o = R.Objectives.FirstOrDefault(x => x.Id == Str(a, 0, "set_note")); if (o != null) o.Note = OptStr(a, 1) ?? ""; return Nil; });

        // ---- objects
        api["cls"] = Fn(a => DynValue.NewString(a.Count > 0 ? Cls(a[0]) : ""));
        api["exists"] = Fn(a => DynValue.NewBoolean(W.Data.Exists(Str(a, 0, "exists"))));
        api["spawn"] = Fn(a =>
        {
            var o = Opt(a, 5);
            var so = new SequelRuntime.SpawnOpts2 { Level = (int)ONum(o, "level", 1), Tag = OStr(o, "tag"), Heading = ONum(o, "heading", 0), Swim = OBool(o, "swim") };
            if (o != null && !o.Get("target").IsNil()) { var (p, e) = Target(o.Get("target")); so.TargetPos = p; so.TargetE = e; }
            var us = R.Spawn(Cls(a[0]), OptStr(a, 1), Num(a, 2, "spawn"), Num(a, 3, "spawn"), (int)Num(a, 4, "spawn", 1), so);
            return Ids(us);
        });
        api["building"] = Fn(a => R.PlaceBuilding(Cls(a[0]), OptStr(a, 1), Num(a, 2, "building"), Num(a, 3, "building"), Num(a, 4, "building", 0)) is { } b ? DynValue.NewNumber(b.Id) : Nil);
        api["units"] = Fn(a => { var f = Filter(a, 1); return Ids(W.Units.Where(u => u.Alive && OfRole(u, a, 0) && f(u)).ToList()); });
        api["buildings"] = Fn(a => { var f = Filter(a, 1); return Ids(W.Buildings.Where(b => b.Alive && OfRole(b, a, 0) && f(b)).ToList()); });
        api["near"] = Fn(a =>
        {
            double x = Num(a, 0, "near"), z = Num(a, 1, "near"), r = Num(a, 2, "near"); var f = Filter(a, 4);
            return Ids(W.Units.Where(u => u.Alive && OfRole(u, a, 3) && Math.Sqrt((u.Pos.X - x) * (u.Pos.X - x) + (u.Pos.Z - z) * (u.Pos.Z - z)) <= r && f(u)).ToList());
        });
        api["in_zone"] = Fn(a =>
        {
            var Z = R.Zone(Str(a, 0, "in_zone")); if (Z == null) return DynValue.NewTable(new Table(S));
            var f = Filter(a, 2);
            return Ids(W.Units.Where(u => u.Alive && OfRole(u, a, 1) && Math.Sqrt((u.Pos.X - Z.X) * (u.Pos.X - Z.X) + (u.Pos.Z - Z.Z) * (u.Pos.Z - Z.Z)) <= Z.R && f(u)).ToList());
        });
        api["info"] = Fn(a => EntArg(a, 0) is { } e ? DynValue.NewTable(Info(e)) : Nil);
        api["alive"] = Fn(a => DynValue.NewBoolean(EntArg(a, 0) is { Alive: true }));
        api["home"] = Fn(a => R.Home(Role(a, 0)) is { } h ? DynValue.NewNumber(h.Id) : Nil);
        api["send"] = Fn(a => { var (p, e) = Target(a[1]); R.Send(EntList(a[0]).OfType<Unit>().ToList(), p, e); return Nil; });
        api["move"] = Fn(a => { double x = Num(a, 1, "move"), z = Num(a, 2, "move"); foreach (var u in EntList(a[0]).OfType<Unit>()) W.Order(u, new Order("move") { X = x, Z = z, Auto = true }); return Nil; });
        api["attack"] = Fn(a => { var t = EntArg(a, 1); if (t != null) foreach (var u in EntList(a[0]).OfType<Unit>()) W.Order(u, new Order("attack") { Target = t, Auto = true }); return Nil; });
        api["stop"] = Fn(a => { foreach (var u in EntList(a[0]).OfType<Unit>()) W.Order(u, new Order("stop") { Auto = true }); return Nil; });
        api["kill"] = Fn(a => { foreach (var e in EntList(a[0]).ToList()) if (e.Alive) W.Kill(e, null); return Nil; });
        api["remove"] = Fn(a => { foreach (var e in EntList(a[0]).ToList()) if (e.Alive) W.RemoveEntity(e); return Nil; });
        api["teleport"] = Fn(a => { if (EntArg(a, 0) is { } e) W.Teleport(e, Num(a, 1, "teleport"), Num(a, 2, "teleport")); return Nil; });
        api["set_owner"] = Fn(a => { foreach (var e in EntList(a[0]).ToList()) W.SetOwner(e, Role(a, 1)); return Nil; });
        api["invulnerable"] = Fn(a => { foreach (var e in EntList(a[0])) W.SetInvulnerable(e, a.Count > 1 && a[1].CastToBool()); return Nil; });
        api["trap"] = Fn(a => { double s = Num(a, 1, "trap"); var why = OptStr(a, 2) ?? "trapped"; foreach (var e in EntList(a[0])) { if (s <= 0) e.St.T["trapped"] = 0; else W.Trap(e, s, why); } return Nil; });
        api["set_hp"] = Fn(a =>
        {
            foreach (var e in EntList(a[0]))
            {
                if (a.Count > 2 && a[2].Type == DataType.Number) e.MaxHp = a[2].Number;
                e.Hp = Math.Min(e.MaxHp, Num(a, 1, "set_hp"));
            }
            return Nil;
        });
        api["scale_hp"] = Fn(a => { double k = Num(a, 1, "scale_hp"); foreach (var e in EntList(a[0])) { e.MaxHp *= k; e.Hp = e.MaxHp; } return Nil; });
        api["sleep"] = Fn(a => { if (EntArg(a, 0) is Unit u && u.Alive) R.Sleep(u, Num(a, 1, "sleep", 50)); return Nil; });
        api["wake"] = Fn(a => { if (EntArg(a, 0) is Unit u && u.Alive) R.Wake(u); return Nil; });
        // the runtime's marks on an object: target ({x, z} or an id), tag, home ({x, z, r}), rescued, hiding, envoy, intown,
        // untargetable, disarmed, scale (drawn bigger)
        api["set"] = Fn(a =>
        {
            var key = Str(a, 1, "set"); var v = a.Count > 2 ? a[2] : Nil;
            foreach (var e in EntList(a[0]))
            {
                var m = R.Mark(e);
                switch (key)
                {
                    case "target": { var (p, t) = Target(v); m.TargetPos = p; m.TargetE = t; break; }
                    case "tag": m.Tag = v.IsNil() ? null : v.CastToString(); break;
                    case "home": m.Home = v.Type == DataType.Table ? new[] { v.Table.Get(1).Number, v.Table.Get(2).Number, v.Table.Get(3).Type == DataType.Number ? v.Table.Get(3).Number : 35 } : null; break;
                    case "rescued": m.Rescued = v.CastToBool(); break;
                    case "hiding": m.Hiding = v.CastToBool(); break;
                    case "envoy": m.Envoy = v.CastToBool(); break;
                    case "intown": m.InTown = v.CastToBool(); break;
                    case "rift": m.Rift = v.IsNil() ? null : v.CastToString(); break;
                    case "untargetable": e.Untargetable = v.CastToBool(); break;
                    case "disarmed": e.Disarmed = v.CastToBool(); break;
                    case "unselectable": e.Unselectable = v.CastToBool(); break;
                    case "scale": e.DrawScale = v.Type == DataType.Number ? v.Number : 1; e.LookChanged(); break;
                    default: throw new ScriptRuntimeException($"M.set: unknown mark \"{key}\"");
                }
            }
            return Nil;
        });

        // ---- rifts, zones, pens, set pieces
        api["rift"] = Fn(a =>
        {
            var o = Opt(a, 3);
            var waves = new List<List<(string, double)>>();
            if (o?.Get("waves") is { Type: DataType.Table } wt)
                foreach (var w in wt.Table.Values)
                {
                    var wave = new List<(string, double)>();
                    if (w.Type == DataType.Table) foreach (var g in w.Table.Values) if (g.Type == DataType.Table) wave.Add((Cls(g.Table.Get(1)), g.Table.Get(2).Number));
                    waves.Add(wave);
                }
            double[]? tp = null; Entity? te = null;
            if (o != null && !o.Get("target").IsNil()) (tp, te) = Target(o.Get("target"));
            var r = R.Rift(Str(a, 0, "rift"), Num(a, 1, "rift"), Num(a, 2, "rift"), ONum(o, "every", 45), ONum(o, "first", 10), waves.Count > 0 ? waves : null, ONum(o, "sealTime", 60), ONum(o, "cap", 18), tp, te, ONum(o, "guard", 0));
            return DynValue.NewTable(RiftInfo(r));
        });
        api["rifts"] = Fn(a => { var t = new Table(S); int i = 1; foreach (var r in R.Rifts) t[i++] = RiftInfo(r); return DynValue.NewTable(t); });
        api["rift_set"] = Fn(a =>
        {
            var r = R.Rifts.LastOrDefault(x => x.Id == Str(a, 0, "rift_set")); if (r == null) return Nil;
            var key = Str(a, 1, "rift_set"); var v = a.Count > 2 ? a[2] : Nil;
            switch (key)
            {
                case "every": r.Every = v.Number; break;
                case "sealTime": r.SealTime = v.Number; break;
                case "cap": r.Cap = v.Number; break;
                case "next": r.Next = v.Number; break;
                case "guard": r.Guard = v.Number; break;
                case "target": (r.TargetPos, r.TargetE) = Target(v); break;
                case "open": if (!v.CastToBool()) { r.Open = false; R.Unmark("rift:" + r.Id); } break;
                default: throw new ScriptRuntimeException($"M.rift_set: unknown field \"{key}\"");
            }
            return Nil;
        });
        // the latest rift of that id is open (a rift raised again has the same id)
        api["rift_open"] = Fn(a => DynValue.NewBoolean(R.Rifts.LastOrDefault(x => x.Id == Str(a, 0, "rift_open")) is { Open: true }));
        api["close_rift"] = Fn(a => { var r = R.Rifts.LastOrDefault(x => x.Id == Str(a, 0, "close_rift")); if (r != null) R.CloseRift(r); return Nil; });
        api["remove_rift_building"] = Fn(a => { var r = R.Rifts.LastOrDefault(x => x.Id == Str(a, 0, "remove_rift_building")); if (r?.E is { Alive: true } e) { W.SetInvulnerable(e, false); W.RemoveEntity(e); } return Nil; });
        api["zone"] = Fn(a =>
        {
            var z = R.Zone(Str(a, 0, "zone")); if (z == null) return Nil;
            var t = new Table(S); t["name"] = z.Name; t["x"] = z.X; t["z"] = z.Z; t["r"] = z.R; t["kind"] = z.Kind;
            return DynValue.NewTable(t);
        });
        api["add_zone"] = Fn(a =>
        {
            var o = Opt(a, 0) ?? throw new ScriptRuntimeException("M.add_zone: expects {name=, x=, z=, r=, kind=}");
            var z = new SqZone { Name = OStr(o, "name") ?? "", X = ONum(o, "x", 0), Z = ONum(o, "z", 0), R = ONum(o, "r", 10), Kind = OStr(o, "kind") ?? "" };
            R.Zones.Set(z.Name, z);
            return Nil;
        });
        api["pen"] = Fn(a => Ids(R.Pen(Str(a, 0, "pen"), Num(a, 1, "pen"), Num(a, 2, "pen"), OptStr(a, 3) ?? "seas_fence", OptStr(a, 4) ?? "seas_gate")));
        api["piece"] = Fn(a =>
        {
            var o = Opt(a, 6);
            double? y = a.Count > 4 && a[4].Type == DataType.Number ? a[4].Number : null;
            R.Piece(Str(a, 0, "piece"), Str(a, 1, "piece"), Num(a, 2, "piece"), Num(a, 3, "piece"), y, Num(a, 5, "piece", 0), ONum(o, "spin", 0));
            return Nil;
        });
        api["sail"] = Fn(a =>
        {
            var path = new List<double[]>();
            if (a.Count > 1 && a[1].Type == DataType.Table) foreach (var p in a[1].Table.Values) if (p.Type == DataType.Table) path.Add(new[] { p.Table.Get(1).Number, p.Table.Get(2).Number });
            R.Sail(Str(a, 0, "sail"), path, Num(a, 2, "sail"), a.Count > 3 && a[3].CastToBool());
            return Nil;
        });
        api["remove_piece"] = Fn(a => { var id = Str(a, 0, "remove_piece"); R.Pieces.RemoveAll(p => p.Id == id); return Nil; });

        // ---- players
        api["res"] = Fn(a =>
        {
            var p = Role(a, 0); var r = Str(a, 1, "res");
            if (p == null) return DynValue.NewNumber(0);
            if (a.Count > 2 && a[2].Type == DataType.Number) p.Res[r] = a[2].Number;
            return DynValue.NewNumber(p[r]);
        });
        api["add_res"] = Fn(a => { var p = Role(a, 0); if (p != null) { var r = Str(a, 1, "add_res"); p.Res[r] = p[r] + Num(a, 2, "add_res"); } return Nil; });
        api["relation"] = Fn(a =>
        {
            var p = Role(a, 0); var q = Role(a, 1);
            if (p == null || q == null) return DynValue.NewNumber(1);
            if (a.Count > 2 && a[2].Type == DataType.Number) { p.SetRelation(q, (int)a[2].Number); if (!(a.Count > 3 && a[3].Type == DataType.Boolean && !a[3].Boolean)) q.SetRelation(p, (int)a[2].Number); }
            return DynValue.NewNumber(p.Relation(q));
        });
        api["role_of"] = Fn(a => EntArg(a, 0) is { } e && R.RoleOf(e.Owner) is { } r ? DynValue.NewString(r) : Nil);
        api["has_role"] = Fn(a => DynValue.NewBoolean(Role(a, 0) != null));
        api["ai"] = Fn(a => { if (R.Brains.TryGetValue(Str(a, 0, "ai"), out var b)) b.SetBehaviour(Str(a, 1, "ai"), OptStr(a, 2) ?? "CTRL"); return Nil; });
        api["ai_lock"] = Fn(a => { if (R.Brains.TryGetValue(Str(a, 0, "ai_lock"), out var b)) b.LockUnits(EntList(a[1]).OfType<Unit>().ToList(), a.Count > 2 && a[2].CastToBool()); return Nil; });
        api["ai_attack"] = Fn(a => { if (R.Brains.TryGetValue(Str(a, 0, "ai_attack"), out var b)) b.StartAutoAttack(EntList(a[1]).ToList()); return Nil; });
        api["has_ai"] = Fn(a => DynValue.NewBoolean(R.Brains.ContainsKey(Str(a, 0, "has_ai"))));
        api["epoch"] = Fn(a => { var p = Role(a, 0); return DynValue.NewNumber(p?.Epoch() ?? 1); });
        api["area_damage"] = Fn(a =>
        {
            double x = Num(a, 0, "area_damage"), z = Num(a, 1, "area_damage");
            W.AreaDamage(null, new V3(x, W.Height(x, z), z), Num(a, 2, "area_damage"), Num(a, 3, "area_damage"), Num(a, 4, "area_damage", 0),
                new AreaOpts { Owner = Role(a, 5), Direct = true, Size = Num(a, 6, "area_damage", 8) });
            return Nil;
        });
        api["reveal"] = Fn(a => { W.RevealArea(R.Me, Num(a, 0, "reveal"), Num(a, 1, "reveal"), Num(a, 2, "reveal", 40), Num(a, 3, "reveal", 0)); return Nil; });
        return api;
    }

    Table RiftInfo(SqRift r)
    {
        var t = new Table(Lua.Lua);
        t["id"] = r.Id; t["x"] = r.X; t["z"] = r.Z; t["open"] = r.Open; t["wave"] = r.Wave; t["seal"] = r.Seal; t["sealTime"] = r.SealTime; t["every"] = r.Every;
        if (r.E != null) t["e"] = r.E.Id;
        if (r.Stab != null) t["stab"] = r.Stab.Id;
        return t;
    }

    Dictionary<string, object?>? ArgsOf(Table? t)
    {
        if (t == null) return null;
        var d = new Dictionary<string, object?>();
        foreach (var kv in t.Pairs) if (kv.Key.Type == DataType.String) d[kv.Key.String] = kv.Value.Type switch { DataType.Number => kv.Value.Number, DataType.String => kv.Value.String, DataType.Boolean => kv.Value.Boolean, _ => kv.Value };
        return d;
    }
}
