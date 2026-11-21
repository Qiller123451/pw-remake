// The Game API: what Lua code of a map sees (`api` in Script / Check functions and in custom.lua). Flat, simple
// calls; objects are numbers (ids), positions map coordinates, players slots 0-7. Everything goes through the
// trigger engine and IMissionHost - the same code the trigger types use, nothing implemented a second time:
// api.action("SpawnGroup", {...}) even runs any action type of the original by its C# class.
// The full list with examples: docs/LUA_MAPS.md "The Game API".
using System;
using System.Collections.Generic;
using System.Linq;
using MoonSharp.Interpreter;

namespace PwRemake.Sim.Scripting;

public static class GameApi
{
    public static Table Create(TriggerEngine E, LuaHost lua)
    {
        var S = lua.Lua;
        var H = E.Host;
        var api = new Table(S);
        Table Sub(string name) { var t = new Table(S); api[name] = t; return t; }
        static DynValue Fn(Func<CallbackArguments, DynValue> f) => DynValue.NewCallback((c, a) => f(a));
        static string Str(CallbackArguments a, int i, string what) =>
            (a.Count > i ? LuaHost.ParamString(a[i]) : null) ?? throw new ScriptRuntimeException($"{what}: argument {i + 1} must be a string or number");
        static double Num(CallbackArguments a, int i, string what, double? def = null)
        {
            if (a.Count > i && a[i].Type == DataType.Number) return a[i].Number;
            if (a.Count > i && a[i].Type == DataType.String && TrigUtil.Num(a[i].String, double.NaN) is var d && !double.IsNaN(d)) return d;
            if (def != null && (a.Count <= i || a[i].IsNil())) return def.Value;
            throw new ScriptRuntimeException($"{what}: argument {i + 1} must be a number");
        }
        DynValue Ids(IEnumerable<int> ids) { var t = new Table(S); int i = 1; foreach (var x in ids) t[i++] = x; return DynValue.NewTable(t); }
        TriggerParams Query(CallbackArguments a, string what)
        {
            if (a.Count < 1 || a[0].Type != DataType.Table) throw new ScriptRuntimeException($"{what}: expects a query table, e.g. {{ obj_class = \"hu_worker\", obj_owner = 0 }}");
            var p = TriggerParams.WithDefaults(E.Script.ConditionDefaults.GetValueOrDefault("OBJC"), new Dictionary<string, string>());
            foreach (var kv in a[0].Table.Pairs) if (kv.Key.Type == DataType.String && LuaHost.ParamString(kv.Value) is { } v) p[kv.Key.String] = v;
            return p;
        }
        Trigger Find(CallbackArguments a, string what) => E.Find(Str(a, 0, what)) ?? throw new ScriptRuntimeException($"{what}: no trigger \"{Str(a, 0, what)}\"");

        // ---- general
        api["time"] = Fn(a => DynValue.NewNumber(E.Time));
        api["difficulty"] = Fn(a => DynValue.NewNumber(H.Difficulty));
        api["human"] = Fn(a => DynValue.NewNumber(H.HumanSlot));
        api["random"] = Fn(a => DynValue.NewNumber(H.Random()));
        api["log"] = Fn(a => { PwRemake.Sim.Core.Log.For("Lua").Info(string.Join(" ", a.GetArray().Select(x => x.ToPrintString()))); return DynValue.Nil; });
        api["exists"] = Fn(a => DynValue.NewBoolean(H.ClassExists(Str(a, 0, "api.exists"))));
        api["text"] = Fn(a => DynValue.NewString(E.Text(Str(a, 0, "api.text"))));
        api["win"] = Fn(a => { E.Win(a.Count > 0 ? a[0].CastToString() ?? "" : ""); return DynValue.Nil; });
        api["lose"] = Fn(a => { E.Lose(a.Count > 0 ? a[0].CastToString() : null); return DynValue.Nil; });
        // any action type of the original, now: api.action("SpawnGroup", { classes_0 = "...", pos = "..." })
        var scriptTrigger = new Trigger(E, new TriggerDef { Name = "(script)" }, -1);
        api["action"] = Fn(a =>
        {
            var type = Str(a, 0, "api.action");
            var e = TriggerTypes.All.FirstOrDefault(x => !x.IsCondition && (x.Name == type || x.Code == type));
            if (e == null || e.Code == "SCRP") throw new ScriptRuntimeException($"api.action: \"{type}\" is no action type");
            var nd = new NodeDef { Code = e.Code };
            if (a.Count > 1 && a[1].Type == DataType.Table)
                foreach (var kv in a[1].Table.Pairs) if (kv.Key.Type == DataType.String && LuaHost.ParamString(kv.Value) is { } v) nd.P[kv.Key.String] = v;
            var act = TriggerTypes.CreateAction(e.Code)!;
            act.Setup(nd, TriggerParams.WithDefaults(E.Script.ActionDefaults.GetValueOrDefault(e.Code), nd.P));
            act.Run(E, scriptTrigger);
            return DynValue.Nil;
        });

        // ---- variables
        var vars = Sub("vars");
        vars["get"] = Fn(a => E.GetVarOrNull(Str(a, 0, "api.vars.get")) is { } v ? DynValue.NewString(v) : DynValue.Nil);
        vars["number"] = Fn(a => DynValue.NewNumber(TrigUtil.ToInt(E.GetVar(Str(a, 0, "api.vars.number")))));
        vars["set"] = Fn(a => { E.SetVar(Str(a, 0, "api.vars.set"), "set", Str(a, 1, "api.vars.set")); return DynValue.Nil; });
        vars["add"] = Fn(a => { E.SetVar(Str(a, 0, "api.vars.add"), "+", TrigUtil.NumStr(Num(a, 1, "api.vars.add", 1))); return DynValue.Nil; });

        // ---- triggers
        var trig = Sub("triggers");
        trig["enable"] = Fn(a => { E.Enable(Find(a, "api.triggers.enable")); return DynValue.Nil; });
        trig["disable"] = Fn(a => { E.Disable(Find(a, "api.triggers.disable")); return DynValue.Nil; });
        trig["enabled"] = Fn(a => DynValue.NewBoolean(Find(a, "api.triggers.enabled").Enabled));
        trig["fired"] = Fn(a => DynValue.NewNumber(Find(a, "api.triggers.fired").Fired));
        trig["folder"] = Fn(a => { E.SetNode(Str(a, 0, "api.triggers.folder"), a.Count < 2 || a[1].CastToBool()); return DynValue.Nil; });

        // ---- quests
        var quests = Sub("quests");
        quests["set"] = Fn(a =>
        {
            var q = E.Quest(Str(a, 0, "api.quests.set")) ?? throw new ScriptRuntimeException($"api.quests.set: no quest \"{Str(a, 0, "")}\"");
            var st = a.Count > 1 && a[1].Type == DataType.String ? a[1].String switch { "show" => 0, "done" => 1, "failed" => 2, "hide" => 3, _ => -1 } : (long)Num(a, 1, "api.quests.set");
            E.SetQuest(q, st);
            return DynValue.Nil;
        });
        quests["get"] = Fn(a =>
        {
            var q = E.Quest(Str(a, 0, "api.quests.get"));
            if (q == null) return DynValue.Nil;
            var t = new Table(S); t["name"] = q.Name; t["visible"] = q.Visible; t["done"] = q.Accomplished; t["failed"] = q.Unaccomplishable; t["main"] = q.Main;
            return DynValue.NewTable(t);
        });

        // ---- level timers
        var timers = Sub("timers");
        timers["start"] = Fn(a => { E.Timer(Str(a, 0, "api.timers.start"), "create", Num(a, 1, "api.timers.start"), a.Count > 2 && a[2].CastToBool(), a.Count > 3 ? E.Text(a[3].CastToString() ?? "") : ""); return DynValue.Nil; });
        timers["pause"] = Fn(a => { E.Timer(Str(a, 0, "api.timers.pause"), "pause"); return DynValue.Nil; });
        timers["resume"] = Fn(a => { E.Timer(Str(a, 0, "api.timers.resume"), "unpause"); return DynValue.Nil; });
        timers["kill"] = Fn(a => { E.Timer(Str(a, 0, "api.timers.kill"), "kill"); return DynValue.Nil; });
        timers["left"] = Fn(a => E.Timers.TryGetValue(Str(a, 0, "api.timers.left"), out var t) ? DynValue.NewNumber(t.Left) : DynValue.Nil);

        // ---- objects
        var objs = Sub("objects");
        objs["find"] = Fn(a => Ids(H.Select(Query(a, "api.objects.find"), "")));
        objs["info"] = Fn(a =>
        {
            var i = H.Info((int)Num(a, 0, "api.objects.info"));
            if (i == null) return DynValue.Nil;
            var t = new Table(S);
            t["id"] = i.Id; t["guid"] = i.Guid; t["name"] = i.Name; t["class"] = i.Class; t["type"] = i.Type; t["owner"] = i.Owner;
            t["x"] = i.X; t["y"] = i.Y; t["z"] = i.Z; t["hp"] = i.Hp; t["maxhp"] = i.MaxHp; t["level"] = i.Level; t["alive"] = i.Alive; t["hidden"] = i.Parked;
            return DynValue.NewTable(t);
        });
        objs["find_one"] = Fn(a => { int o = H.FindObject(Str(a, 0, "api.objects.find_one")); return o >= 0 ? DynValue.NewNumber(o) : DynValue.Nil; });
        objs["group"] = Fn(a => { int g = H.FindGroup(Str(a, 0, "api.objects.group")); return g >= 0 ? Ids(H.GroupMembers(g)) : DynValue.Nil; });
        objs["add_to_group"] = Fn(a => { int g = H.FindGroup(Str(a, 0, "api.objects.add_to_group")); return DynValue.NewBoolean(g >= 0 && H.AddToGroup(g, (int)Num(a, 1, "api.objects.add_to_group"))); });
        objs["remove"] = Fn(a => DynValue.NewBoolean(H.Remove((int)Num(a, 0, "api.objects.remove"))));
        // the infected stage 0 .. 3 the view draws (docs/INFECTED_LOOK.md)
        objs["infect"] = Fn(a => DynValue.NewBoolean(H.SetInfection((int)Num(a, 0, "api.objects.infect"), (int)Num(a, 1, "api.objects.infect", 3))));
        objs["infection"] = Fn(a => DynValue.NewNumber(H.Infection((int)Num(a, 0, "api.objects.infection"))));
        objs["appear"] = Fn(a => { H.SetAppearance((int)Num(a, 0, "api.objects.appear"), (int)Num(a, 1, "api.objects.appear", 7)); E.Touch(); return DynValue.Nil; });

        // ---- units (and buildings)
        var units = Sub("units");
        units["spawn"] = Fn(a =>
        {
            // (7th argument true: one unit, not more for a co-op's scaled enemy - a unique boss)
            var o = new SpawnOptions { Level = a.Count > 4 && a[4].Type == DataType.Number ? (int)a[4].Number : null, NoScale = a.Count > 6 && a[6].CastToBool() };
            if (a.Count > 5 && LuaHost.ParamString(a[5]) is { } g) o.Group = H.FindGroup(g);
            int id = H.Spawn(Str(a, 0, "api.units.spawn"), (int)Num(a, 1, "api.units.spawn"), Num(a, 2, "api.units.spawn"), Num(a, 3, "api.units.spawn"), o);
            return id >= 0 ? DynValue.NewNumber(id) : DynValue.Nil;
        });
        units["kill"] = Fn(a => { H.Kill((int)Num(a, 0, "api.units.kill")); return DynValue.Nil; });
        units["move"] = Fn(a => { H.OrderMove((int)Num(a, 0, "api.units.move"), new MapPos(Num(a, 1, "api.units.move"), Num(a, 2, "api.units.move"), 0), 0, a.Count > 3 && a[3].CastToBool()); return DynValue.Nil; });
        units["attack"] = Fn(a => { H.OrderAttack((int)Num(a, 0, "api.units.attack"), (int)Num(a, 1, "api.units.attack")); return DynValue.Nil; });
        units["stop"] = Fn(a => { H.OrderStop((int)Num(a, 0, "api.units.stop")); return DynValue.Nil; });
        units["teleport"] = Fn(a => { H.Teleport((int)Num(a, 0, "api.units.teleport"), Num(a, 1, "api.units.teleport"), Num(a, 2, "api.units.teleport")); E.Touch(); return DynValue.Nil; });
        units["owner"] = Fn(a => { H.SetOwner((int)Num(a, 0, "api.units.owner"), (int)Num(a, 1, "api.units.owner"), 0); E.Touch(); return DynValue.Nil; });
        units["hp"] = Fn(a => { H.SetHitpoints((int)Num(a, 0, "api.units.hp"), Num(a, 1, "api.units.hp")); return DynValue.Nil; });
        units["invulnerable"] = Fn(a => { H.SetInvulnerable((int)Num(a, 0, "api.units.invulnerable"), a.Count < 2 || a[1].CastToBool(), Num(a, 2, "api.units.invulnerable", 0)); return DynValue.Nil; });
        units["anim"] = Fn(a => DynValue.NewBoolean(H.PlayAnim((int)Num(a, 0, "api.units.anim"), Str(a, 1, "api.units.anim"), (int)Num(a, 2, "api.units.anim", 1))));
        units["scale"] = Fn(a => { H.SetDrawScale((int)Num(a, 0, "api.units.scale"), Num(a, 1, "api.units.scale", 1)); return DynValue.Nil; });
        units["maxhp"] = Fn(a => { H.SetMaxHp((int)Num(a, 0, "api.units.maxhp"), Num(a, 1, "api.units.maxhp")); return DynValue.Nil; });
        // a unit set on fire: it burns to death in `seconds` (Outbreak: the infected in the sunlight)
        units["burn"] = Fn(a => DynValue.NewBoolean(H.Burn((int)Num(a, 0, "api.units.burn"), Num(a, 1, "api.units.burn", 8))));
        // a worker to the nearest source of a resource ("food": its owner's field first)
        units["gather"] = Fn(a => DynValue.NewBoolean(H.OrderGather((int)Num(a, 0, "api.units.gather"), Str(a, 1, "api.units.gather"))));
        // a class in the slot's own tribe (the tribe swap; co-op seats too): a house for every seat's tribe
        units["spawn_own"] = Fn(a =>
        {
            int id = H.SpawnOwn(Str(a, 0, "api.units.spawn_own"), (int)Num(a, 1, "api.units.spawn_own"), Num(a, 2, "api.units.spawn_own"), Num(a, 3, "api.units.spawn_own"), a.Count > 4 && a[4].Type == DataType.Number ? (int)a[4].Number : null);
            return id >= 0 ? DynValue.NewNumber(id) : DynValue.Nil;
        });

        // ---- players
        var players = Sub("players");
        players["resource"] = Fn(a =>
        {
            int s = (int)Num(a, 0, "api.players.resource"); var n = Str(a, 1, "api.players.resource");
            if (a.Count > 2 && !a[2].IsNil()) H.ChangeResource(s, n, Str(a, 2, "api.players.resource"), a.Count > 3 && a[3].CastToBool());
            return H.PlayerAttribute(s, n) is { } v ? DynValue.NewNumber(v) : DynValue.Nil;
        });
        players["attribute"] = Fn(a => H.PlayerAttribute((int)Num(a, 0, "api.players.attribute"), Str(a, 1, "api.players.attribute")) is { } v ? DynValue.NewNumber(v) : DynValue.Nil);
        players["relation"] = Fn(a =>
        {
            int x = (int)Num(a, 0, "api.players.relation"), y = (int)Num(a, 1, "api.players.relation");
            if (a.Count > 2 && !a[2].IsNil()) { H.SetRelation(x, y, (int)Num(a, 2, "api.players.relation")); E.OnDiplomacyChanged(); }
            return H.Relation(x, y) is { } r ? DynValue.NewNumber(r) : DynValue.Nil;
        });
        players["seats"] = Fn(a => Ids(H.SeatSlots));
        // the slot's epoch; with a second argument raised to it first
        players["epoch"] = Fn(a =>
        {
            int s = (int)Num(a, 0, "api.players.epoch");
            if (a.Count > 1 && a[1].Type == DataType.Number) return DynValue.NewNumber(H.RaiseEpoch(s, (int)a[1].Number));
            return H.PlayerAttribute(s, "epoch") is { } v ? DynValue.NewNumber(v) : DynValue.NewNumber(1);
        });
        players["tech"] = Fn(a => { H.SetTechFilter((int)Num(a, 0, "api.players.tech"), Str(a, 1, "api.players.tech"), a.Count < 3 || a[2].CastToBool()); return DynValue.Nil; });

        // ---- presentation
        var ui = Sub("ui");
        ui["infobar"] = Fn(a => { E.SetInfoBar(a.Count > 0 ? a[0].CastToString() : null); return DynValue.Nil; });
        ui["ping"] = Fn(a =>
        {
            var m = new MapMarker { X = Num(a, 0, "api.ui.ping"), Y = Num(a, 1, "api.ui.ping"), Id = a.Count > 2 ? a[2].CastToString() ?? "" : "", Kind = "SPHint", Color = 0x6ee06e, Ttl = 5, Repeats = 5, Interval = 0.5 };
            H.ShowMarker(m);
            return DynValue.Nil;
        });
        ui["unping"] = Fn(a => { H.RemoveMarker(Str(a, 0, "api.ui.unping")); return DynValue.Nil; });
        ui["dialog"] = Fn(a => { E.PlayDialog(Str(a, 0, "api.ui.dialog"), null); return DynValue.Nil; });
        ui["sequence"] = Fn(a => { E.PlaySequence(new SequenceRequest { Path = Str(a, 0, "api.ui.sequence"), Quit = a.Count > 1 && a[1].CastToBool(), Trigger = "(script)" }); return DynValue.Nil; });
        ui["sound"] = Fn(a => { H.PlaySound(Str(a, 0, "api.ui.sound"), null); return DynValue.Nil; });
        ui["reveal"] = Fn(a => DynValue.NewNumber(H.RevealArea(H.HumanSlot, Num(a, 0, "api.ui.reveal"), Num(a, 1, "api.ui.reveal"), Num(a, 2, "api.ui.reveal", 20), Num(a, 3, "api.ui.reveal", 0), -1)));

        // ---- the map's day-night cycle (map.lua day_night: docs/LUA_MAPS.md "The Game API")
        var dn = Sub("daynight");
        dn["enabled"] = Fn(a => DynValue.NewBoolean(H.DayNight != null));
        dn["phase"] = Fn(a => H.DayNight is { } s ? DynValue.NewString(s.PhaseName) : DynValue.Nil);
        dn["is_night"] = Fn(a => DynValue.NewBoolean(H.DayNight is { IsNight: true }));
        dn["night"] = Fn(a => DynValue.NewNumber(H.DayNight?.NightNo ?? 0));
        dn["left"] = Fn(a => DynValue.NewNumber(H.DayNight?.Left ?? 0));
        dn["to_night"] = Fn(a => DynValue.NewNumber(H.DayNight?.ToNight ?? 0));
        dn["to_day"] = Fn(a => DynValue.NewNumber(H.DayNight?.ToDay ?? 0));
        dn["darkness"] = Fn(a => DynValue.NewNumber(H.DayNight?.Darkness ?? 0));
        dn["clock"] = Fn(a => DynValue.NewNumber(H.DayNight?.Clock ?? 0));
        // new lengths: { day =, night =, first_day = } (seconds) - for the mission's start (its difficulty)
        dn["set"] = Fn(a =>
        {
            var t = a.Count > 0 && a[0].Type == DataType.Table ? a[0].Table : throw new ScriptRuntimeException("api.daynight.set: a table { day, night, first_day }");
            double? N(string k) => t.Get(k).Type == DataType.Number ? t.Get(k).Number : null;
            H.SetDayNightTimes(N("day"), N("night"), N("first_day"));
            return DynValue.Nil;
        });

        // ---- computer players
        var ai = Sub("ai");
        ai["behavior"] = Fn(a => DynValue.NewBoolean(H.AiBehavior((int)Num(a, 0, "api.ai.behavior"), Str(a, 1, "api.ai.behavior"), a.Count > 2 ? a[2].CastToString() ?? "CTRL" : "CTRL")));
        return api;
    }
}
