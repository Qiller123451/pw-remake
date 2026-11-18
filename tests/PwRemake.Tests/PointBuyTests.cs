// Golden tests of the army budget and the skirmish setup: the JS remake's game/pointbuy.js and game/skirmish.js (run in
// node by tests/golden/pointbuy_dump.mjs) against the C# PointBuy / Skirmish (src/Sim/Game), for both configurations:
// prices, rates, totals, shops, presets, loaded armies, the computer players' armies, a scripted shopping session (the
// army after every step) and the setup's slots, game types and start checks. Then skirmish games with an army budget
// (SkirmishSession): the human's bought army, the computer players' armies, handicaps, colours, the setup's JSON.
//
// Needs node and the JS sources (PW_REMAKE_SRC, default /home/claude/pw-exporter/remake/src) and the data folders
// (PW_DATA / PW_DATA_MIRAGE, default /opt/data/base, /opt/data/mirage).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;

namespace PwRemake.Tests;

[TestClass]
public static class PointBuyTests
{
    public static void PointBuyGoldenBase(TestContext t) => Run(t, RulesTestData.Base(t));
    public static void PointBuyGoldenMirage(TestContext t) => Run(t, RulesTestData.Mirage(t));

    static void Run(TestContext t, string data)
    {
        var dump = Dump(t, data);
        var D = PwRemake.Sim.Rules.Rules.Load(data);
        var ai = Json.TryParseFile(Path.Combine(data, "ai.json"));
        var PB = new PointBuy(D, ai);
        int n = 0, bad = 0;
        var fails = new List<string>();
        SkirmishSetup? sk = null;
        foreach (var q in (JsonArray)dump["queries"]!)
        {
            string op = q!["op"].Str(); var args = (JsonArray)q["args"]!;
            JsonNode? mine;
            if (op == "session") { n++; bad += Session(PB, args, (JsonArray)q["r"]!, fails); continue; }
            if (op == "skDefault") { sk = Skirmish.DefaultSetup(); mine = SetupN(sk); }
            else if (op == "skOp") mine = SkOp(sk!, args);
            else mine = Query(PB, op, args);
            n++;
            if (!Same(q["r"], mine)) { bad++; if (fails.Count < 10) fails.Add($"{op}({Show(args)}): JS {Show(q["r"])} / C# {Show(mine)}"); }
        }
        foreach (var f in fails) t.Log(f);
        t.Log($"{n} queries, {bad} differences");
        t.True(n > 1000, "the dump has the queries");
        t.Equal(0, bad, "differences to the JS army budget");
    }

    // ------------------------------------------------------------------ skirmish games with an army budget
    const string MAP = "/mnt/user-data/uploads/Paraworld/Data/Base/Maps/Multiplayer/multi_2_jun_001.ula";

    static SkirmishSession Game(TestContext t, string cfgId, SkirmishSetup setup)
    {
        var c = CampaignTests.LoadCfgPublic(cfgId);
        if (c == null) t.Skip("no configuration " + cfgId);
        if (!File.Exists(MAP)) t.Skip("map not staged: " + MAP);
        return SkirmishSession.Create(setup, MAP, c!.Rules, c.Models, c.Forest, c.Brushes, c.Ai);
    }

    /// <summary>main.js skirmishPlayers / createBrains with points: the human's default preset (or own army), the Aje
    /// collector a card of the army, the computer players' AiPBPreset armies, colours, the handicap</summary>
    public static void SkirmishArmyBudget(TestContext t)
    {
        foreach (var cfgId in new[] { "base", "mirage" })
        {
            var setup = Skirmish.DefaultSetup("Hu", "Ninigi");
            setup.Points = 3000; setup.Seed = 7; setup.Handicap = true;
            setup.Slots[1].Hp = 150;
            var ss = Game(t, cfgId, setup);
            var W = ss.World; var me = ss.Human!; var ai = W.Players[1];
            var pre = ss.PB.Preset("Hu", 3000, "standard")!;
            var army = ss.PB.Load(pre, "Hu", ss.PB.Total(3000, "Hu"));
            int mine = W.Units.Count(u => u.Owner == me && u.Alive);
            t.Log($"{cfgId}: preset {pre.Name}: {army.Cards.Count} cards, res {army.R("food")}/{army.R("wood")}/{army.R("stone")}; you have {mine} units, {me.Res["food"]}/{me.Res["wood"]}/{me.Res["stone"]}; ai {W.Units.Count(u => u.Owner == ai)} units");
            t.Equal(army.Cards.Count, mine, "the human's units are the preset's cards");
            foreach (var r in PointBuy.RES) t.Equal(army.R(r), me.Res[r], "the human's " + r + " from the preset");
            t.True(W.Units.Count(u => u.Owner == ai) >= 2, "the computer player's army was placed");
            t.True(ss.Ai.Brains.Get(1) != null, "the computer player has a brain");
            t.Equal(Colors("blue"), me.Color, "your colour");
            t.Equal(Colors("red"), ai.Color, "the computer player's colour");
            var aiUnit = W.Units.First(u => u.Owner == ai && u.Alive);
            var same = W.Data.Stats(aiUnit.Name, aiUnit.Level, null);
            t.Log($"handicap 150 %: {aiUnit.Name} {aiUnit.MaxHp} hp (neutral {same?.Hp})");
        }
        // an Aje human with points: the resource collector is a card of the army, no tent placed for it
        {
            var setup = Skirmish.DefaultSetup("Aje", "Hu");
            setup.Points = 1000; setup.Seed = 3;
            var ss = Game(t, "base", setup);
            var me = ss.Human!;
            t.True(ss.World.Units.Any(u => u.Owner == me && u.Name == "aje_resource_collector"), "the Aje collector came with the army");
            t.True(ss.Homes.Any(h => h.Owner == me), "the collector is the home");
        }
        // an own army ("My Army") that costs too much: one worker (and no resources)
        {
            var setup = Skirmish.DefaultSetup("Hu", "Aje");
            setup.Points = 1000; setup.Seed = 5;
            var a = new Army(); a.Cards[51] = "Cole_s0"; a.Res["food"] = 500;
            setup.Slots[0].Army = a; setup.Slots[0].ArmyTribe = "Hu"; setup.Slots[0].ArmyPoints = 1000; setup.Slots[0].Preset = "custom";
            var ss = Game(t, "base", setup);
            t.True(ss.Human!.Cheated, "the dear army is refused");
            t.Equal(1, ss.World.Units.Count(u => u.Owner == ss.Human && u.Alive), "one worker instead");
        }
    }
    /// <summary>menu.js campaignArmy + campaign/setup.js startLocation: a mission with an army budget starts with the army
    /// bought on the army screen (its cards at their levels, its resources) instead of the level's preset</summary>
    public static void CampaignArmyScreen(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no configuration base");
        var folder = System.IO.Path.Combine(c!.Config.MissionsDir, "02_single_02");
        var mj = Json.TryParseFile(System.IO.Path.Combine(folder, "mission.json"));
        if (mj == null) t.Skip("mission 2 not prepared");
        var offer = PwRemake.Sim.Campaign.CampaignArmy.Of(mj, c.Rules, "", 0)!;
        t.True(offer != null, "mission 2 has an army screen");
        t.Equal("Hu", offer.Tribe, "the mission's tribe");
        t.Equal(mj!["min_credits"].Num(), offer.Credits, "the budget: the mission's least credits");
        var army = offer.Defaults();
        t.True(army.Cards.Count >= 3 && army.Fixed.Count == army.Cards.Count, "the level's preset, fixed");
        t.Log($"mission 2: {offer.Credits} points, preset {string.Join(" ", army.Cards.Select(kv => kv.Key + ":" + kv.Value))}, allowed {string.Join(" ", offer.Allowed!)}, blocked {string.Join(",", offer.Blocked)}");
        var PB = new PointBuy(c.Rules, null);
        t.True(PB.Buy(army, "hu_warrior", 1, offer.Total, "Hu", offer.Blocked) == null, "the least credits buy the preset and nothing more");
        // with 1000 bonus points more: a warrior on level 1 and 50 more wood
        offer = PwRemake.Sim.Campaign.CampaignArmy.Of(mj, c.Rules, "", offer.Credits + 1000)!;
        army = offer.Defaults();
        int? slot = PB.Buy(army, "hu_warrior", 1, offer.Total, "Hu", offer.Blocked);
        t.True(slot != null, "a warrior can be bought");
        PB.AddRes(army, "wood", 50, offer.Total, "Hu");
        var s = PwRemake.Sim.Campaign.MissionSession.Create(new GameStart { Configuration = "base", Mission = 2, Difficulty = 1, Seed = 1, Army = army }, c.Config,
            new PwRemake.Sim.Campaign.SessionOptions { Rules = c.Rules, Models = c.Models, Forest = c.Forest, Brushes = c.Brushes, AiData = c.Ai, DataDir = c.Data, NoTriggers = true });
        var me = s.Human!;
        t.True(s.World.Units.Any(u => u.Owner == me && u.Name == "hu_warrior"), "the bought warrior stands at the start");
        t.Equal(army.R("wood"), me.Res["wood"], "the bought wood");
        t.Equal(army.Cards.Count, s.World.Units.Count(u => u.Owner == me && u.Alive), "one unit per card");
        // another tribe: the preset in that tribe's classes
        var aje = PwRemake.Sim.Campaign.CampaignArmy.Of(mj, c.Rules, "Ninigi", 5000)!;
        t.Equal(5000.0, aje.Credits, "the bonus points of the mission before, when more");
        t.True(aje.Defaults().Cards.Values.All(x => !x.StartsWith("hu_")), "the Dragon Clan's classes for the Norsemen's");
    }

    /// <summary>the army screen's default army keeps every card of the level's fixed preset, with every tribe of both
    /// configurations: the budget is at least the preset's price (mission 11 as the Norsemen lost its heroes, and with
    /// them the mission at once)</summary>
    public static void CampaignArmyKeepsThePreset(TestContext t)
    {
        int n = 0;
        foreach (var cfg in new[] { "base", "mirage" })
        {
            var c = CampaignTests.LoadCfgPublic(cfg);
            if (c == null) continue;
            foreach (var dir in System.IO.Directory.GetDirectories(c.Config.MissionsDir).OrderBy(x => x))
            {
                var mj = Json.TryParseFile(System.IO.Path.Combine(dir, "mission.json"));
                if (mj?["players"] is not System.Text.Json.Nodes.JsonArray ps || ps.FirstOrDefault(p => p?["type"].Str() == "human")?["point_buy"] == null) continue;
                foreach (var tribe in c.Config.PlayableTribes)
                {
                    var o = PwRemake.Sim.Campaign.CampaignArmy.Of(mj, c.Rules, tribe, 0);
                    if (o == null || o.Credits <= 0) continue;
                    var hp = ps.First(p => p?["type"].Str() == "human")!;
                    var src = (hp["point_buy"]?["tribes"]?[tribe] ?? hp["point_buy"]?["tribes"]?[hp["tribe"].Str()]) as System.Text.Json.Nodes.JsonObject;
                    var army = o.Defaults();
                    var lost = PbPreset.UnitsOf(src).Where(kv => int.TryParse(kv.Key, out int sl) && kv.Value != "Blocked"
                        && !System.Text.RegularExpressions.Regex.IsMatch(kv.Value, "^(Nature|Resource|Technics)$") && !army.Cards.ContainsKey(sl)).Select(kv => kv.Key + ":" + kv.Value).ToList();
                    t.True(lost.Count == 0, $"{cfg} {System.IO.Path.GetFileName(dir)} as {tribe}: the preset kept (lost: {string.Join(" ", lost)})");
                    n++;
                }
            }
        }
        if (n == 0) t.Skip("no prepared missions with an army budget");
        t.Log($"{n} armies checked");
    }

    /// <summary>the skirmish map list of the installation (SkirmishMaps: the level info only) and its headers</summary>
    public static void SkirmishMapList(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no configuration base");
        var all = SkirmishMaps.List(c!.Config, all: true);
        foreach (var m in all) t.Log($"{m.Path} | {m.Folder} | {m.Name} | {m.Players} | {string.Join("/", m.Modes)} | {m.Type} | {m.Setting}");
        var mp = SkirmishMaps.List(c.Config);
        t.True(mp.Count >= 5, "the multiplayer maps are listed");
        t.True(mp.All(m => m.Type == "multiplayer") && !mp.Any(m => m.File.StartsWith("single", StringComparison.OrdinalIgnoreCase)), "no campaign maps");
        foreach (var m in mp) { var hh = SkirmishMaps.Header(m.FullPath); t.Log(m.File + ": " + string.Join("; ", hh.Info.Select(kv => kv.Key + "=" + kv.Value)) + " || " + string.Join("; ", hh.Description.Where(kv => kv.Key.StartsWith("Root/Base/")).Select(kv => kv.Key + "=" + kv.Value))); }
        var h = SkirmishMaps.Header(mp[0].FullPath);
        var full = PwRemake.Sim.Maps.Ula.ParseUlaFile(mp[0].FullPath);
        t.Equal(full.Name, h.Name, "the header's name");
        t.Equal(full.W, h.W, "the header's size");
        t.Equal(full.Setting, h.Setting, "the header's setting");
        t.Equal(full.MaxPlayers, h.MaxPlayers, "the header's players");
        t.Equal(SkirmishMaps.Resolve(c.Config, mp[0].Path), mp[0].FullPath, "a list entry resolves to its file");
    }

    /// <summary>the random jungle ("" map), Domination and Defender set up and run (a minute each)</summary>
    public static void SkirmishModesAndRandomMap(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no configuration base");
        var rnd = Skirmish.DefaultSetup("Ninigi", "Hu"); rnd.Map = ""; rnd.Seed = 99;
        Skirmish.OpenSlot(rnd, 2); rnd.Slots[2].Tribe = "Aje";
        var a = SkirmishSession.Create(rnd, null, c!.Rules, c.Models, c.Forest, c.Brushes, c.Ai);
        a.Run(60);
        t.Equal(3, a.World.Players.Count, "three players on the random jungle");
        t.True(a.Homes.Count == 3 && a.Map.Kind != "original", "each with its home");
        var mp = SkirmishMaps.List(c.Config);
        var dom = mp.FirstOrDefault(m => m.SetupModes.Contains("domination"));
        var def = mp.FirstOrDefault(m => m.SetupModes.Contains("defender"));
        if (dom != null)
        {
            var s = Skirmish.DefaultSetup(); Skirmish.ApplyMode(s, "domination"); s.Map = dom.Path;
            var g = SkirmishSession.Create(s, dom.FullPath, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai);
            g.Run(30);
            t.Equal("domination", g.World.Go?.Mode, "domination: the game-over rules' mode");
            t.True(g.World.Opts.DiplomacyLocked && !g.World.Opts.Warpgate, "domination locks diplomacy, no warp gate");
        }
        if (def != null)
        {
            var s = Skirmish.DefaultSetup(); Skirmish.ApplyMode(s, "defender"); s.Map = def.Path;
            Skirmish.OpenSlot(s, 2);
            for (int i = 0; i < 3; i++) s.Slots[i].Hq = i;
            t.Equal(null, Skirmish.Validate(s, def.Players), "a valid defender setup");
            var g = SkirmishSession.Create(s, def.FullPath, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai);
            g.Run(30);
            t.Equal(1, g.Human!.Team, "the human on position 1 defends (team 1)");
            t.True(g.World.Players.Skip(1).All(p => p.Team == 2), "the others attack (team 2)");
        }
    }

    static int Colors(string id) { var c = PwRemake.Sim.Rules.Colors.ColorById(id)!; return PwRemake.Sim.Rules.Colors.Hex(c.Dark); }

    /// <summary>the setup is a plain JSON object (a lobby fills it, the machines of a lockstep game share it): the same
    /// setup after a round trip, and the same world from it (state hash)</summary>
    public static void SkirmishSetupJson(TestContext t)
    {
        var setup = Skirmish.DefaultSetup("Ninigi", "Hu");
        Skirmish.OpenSlot(setup, 2); setup.Slots[2].Tribe = "Aje"; setup.Slots[2].Personality = "Offensive";
        setup.Points = 1000; setup.Mode = "deathmatch"; setup.Seed = 42; setup.Map = "Base/Maps/Multiplayer/multi_2_jun_001.ula";
        setup.Slots[0].Army = new Army { Cards = { [0] = "worker", [1] = "ninigi_worker" } }; setup.Slots[0].ArmyTribe = "Ninigi"; setup.Slots[0].ArmyPoints = 1000;
        var json = setup.ToJson();
        var back = SkirmishSetup.FromJson(json)!;
        t.Equal(json, back.ToJson(), "the same JSON after a round trip");
        t.Equal("ninigi_worker", back.Slots[0].Army!.Cards[1], "the army's cards");
        t.Equal("Offensive", back.Slots[2].Personality, "a slot's personality");
        t.True(json.Contains("\"chooseHQ\"") && json.Contains("\"slots\""), "the JS names of the fields");
        back.Slots.RemoveAt(7);           // (two of the three players: the map has two start positions)
        setup.Slots[2].Type = back.Slots[2].Type = "closed";
        var a = Game(t, "base", setup.Clone()); var b = Game(t, "base", back);
        for (int i = 0; i < 200; i++) { a.Step(); b.Step(); }
        t.Equal(a.World.StateHash(), b.World.StateHash(), "the same game from the round-tripped setup");
    }

    static JsonNode? Query(PointBuy PB, string op, JsonArray a)
    {
        switch (op)
        {
            case "ready": return PB.Ready;
            case "prices": return Arr(Enumerable.Range(1, 5).Select(l => (JsonNode?)PB.Price(a[0].Str(), l, a[1].Str())));
            case "isHero": return PB.IsHero(a[0].Str());
            case "rate": return PB.Rate(a[0].Str(), a[1].Str());
            case "resCost": return PB.ResCost(a[0].Str(), a[1].Num(), a[2].Str());
            case "resFor": return PB.ResFor(a[0].Str(), a[1].Num(), a[2].Str());
            case "total": return PB.Total(a[0].Num(), a[1].Str(), a[2].Bool(), a[3].Bool());
            case "resolve": { var (cls, caste) = PB.Resolve(a[0].Str(), a[1].Str()); return Obj(("cls", cls), ("caste", caste)); }
            case "shop":
            {
                var allowed = a[1] is JsonArray al ? al.Select(x => x.Str()).ToHashSet() : null;
                return Arr(PB.Shop(a[0].Str(), allowed).Select(e => (JsonNode?)Arr(new JsonNode?[] { e.Cls, e.Tab, e.Prices == null ? null : Arr(e.Prices.Select(p => (JsonNode?)p)) })));
            }
            case "presets": return Arr(PB.Presets(a[0].Str(), a[1].Num()).Select(p => PresetN(p)));
            case "preset": return PresetN(PB.Preset(a[0].Str(), a[1].Num(), a[2].Str()));
            case "load":
            {
                var p = PresetOfDump(a[0]!); var o = a[3]!;
                Dictionary<string, double>? locked = o["lockedRes"] is JsonObject lo ? lo.ToDictionary(kv => kv.Key, kv => kv.Value.Num()) : null;
                return ArmyN(PB.Load(p, a[1].Str(), a[2].Num(), o["fixed"].Bool(), o["buildings"]?.Bool() ?? true, o["mp"]?.Bool() ?? true, locked));
            }
            case "clear": return ArmyN(PB.Clear(a[0].Str(), a[1].Bool(), a[2].Bool()));
            case "aiPreset": return PresetN(PB.AiPreset(a[0].Str(), a[1].Num(), a[2].Str(), a[3].Num()));
            case "genericClass": return PB.GenericClass(a[0].Str(), a[1].Str());
        }
        throw new Exception("unknown query " + op);
    }

    /// <summary>the scripted shopping session: replays each step, compares the step's result and the army after it</summary>
    static int Session(PointBuy PB, JsonArray a, JsonArray steps, List<string> fails)
    {
        string tribe = a[0].Str(); double total = a[2].Num();
        var army = ArmyOfDump(a[3]!);
        int i = 0;
        foreach (var s in steps)
        {
            var st = (JsonArray)s!["step"]!;
            string k = st[0].Str();
            JsonNode? r = k switch
            {
                "buy" => PB.Buy(army, st[1].Str(), st[2].Int(), total, tribe, st[3] is JsonArray bl ? bl.Select(x => x.Int()).ToArray() : null) is int sl ? sl : null,
                "buyLowest" => PB.BuyLowest(army, st[1].Str(), total, tribe) is int sl2 ? sl2 : null,
                "sell" => PB.Sell(army, st[1].Int()),
                "move" => Mv(),
                _ => PB.AddRes(army, st[1].Str(), st[2].Num(), total, tribe),
            };
            JsonNode? Mv() { int from = st[1].Int(), to = st[2].Int(); bool ok = PB.CanMove(army, from, to, total, tribe); if (ok) PB.Move(army, from, to); return ok; }
            var mine = Obj(("r", r), ("army", ArmyN(army)), ("cost", PB.Cost(army, tribe)), ("left", PB.Left(army, total, tribe)), ("free", Arr(Enumerable.Range(1, 5).Select(l => (JsonNode?)PB.FreeSlot(army, l)))));
            var js = Obj(("r", st[4]?.DeepClone()), ("army", s["army"]?.DeepClone()), ("cost", s["cost"]?.DeepClone()), ("left", s["left"]?.DeepClone()), ("free", s["free"]?.DeepClone()));
            if (!Same(js, mine)) { if (fails.Count < 10) fails.Add($"session {tribe} {a[1]} step {i} {st.ToJsonString()}: JS {Show(js)} / C# {Show(mine)}"); return 1; }
            i++;
        }
        return 0;
    }

    static JsonNode? SkOp(SkirmishSetup s, JsonArray op)
    {
        string? r = null;
        switch (op[0].Str())
        {
            case "open": Skirmish.OpenSlot(s, op[1].Int()); break;
            case "close": s.Slots[op[1].Int()].Type = "closed"; break;
            case "mode": Skirmish.ApplyMode(s, op[1].Str()); break;
            case "team": s.Slots[op[1].Int()].Team = op[2].Int(); break;
            case "color": s.Slots[op[1].Int()].Color = op[2].Str(); break;
            case "hq": s.Slots[op[1].Int()].Hq = op[2].Int(); break;
            case "teams": s.Teams = op[1].Int(); break;
            case "validate": r = Skirmish.Validate(s, op[1].Int()); break;
        }
        return Obj(("r", r), ("setup", SetupN(s)));
    }
    // (the JS's setup has eight slots; the remake's 16: the first eight are compared)
    static JsonNode SetupN(SkirmishSetup s) => Obj(("mode", s.Mode), ("points", s.Points), ("warpgate", s.Warpgate), ("fog", s.Fog), ("lockDiplomacy", s.LockDiplomacy), ("handicap", s.Handicap),
        ("chooseHQ", s.ChooseHQ), ("teams", s.Teams), ("defenderMin", s.DefenderMin),
        ("slots", Arr(s.Slots.Take(PwRemake.Sim.Rules.Colors.ORIGINAL_COLORS).Select(x => (JsonNode?)Obj(("type", x.Type), ("tribe", x.Tribe), ("personality", x.Personality), ("level", x.Level), ("team", x.Team), ("color", x.Color), ("hp", x.Hp), ("hq", x.Hq))))));

    // ------------------------------------------------------------------ shapes
    static JsonNode? PresetN(PbPreset? p) => p == null ? null : Obj(("name", p.Name), ("cost", p.Cost), ("standard", p.Standard), ("kind", p.Kind),
        ("units", Arr(p.Units.Select(u => (JsonNode?)Arr(new JsonNode?[] { u.Key, u.Value })))), ("res", ResObj(p.Res)));
    static JsonObject ResObj(Dictionary<string, double> r) { var o = new JsonObject(); foreach (var (k, v) in r) o[k] = v; return o; }
    static PbPreset PresetOfDump(JsonNode n) => new()
    {
        Name = n["name"].Str(), Cost = n["cost"].Num(), Standard = n["standard"].Bool(), Kind = n["kind"] == null ? null : n["kind"].Str(),
        Units = ((JsonArray)n["units"]!).Select(u => new KeyValuePair<string, string>(u![0].Str(), u[1].Str())).ToList(),
        Res = n["res"] is JsonObject ro ? ro.ToDictionary(kv => kv.Key, kv => kv.Value.Num()) : new(),
    };
    static JsonNode ArmyN(Army a)
    {
        var cards = new JsonObject(); foreach (var (k, v) in a.Cards) cards[k.ToString(CultureInfo.InvariantCulture)] = v;
        var fixd = new JsonObject(); foreach (var (k, v) in a.Fixed) fixd[k.ToString(CultureInfo.InvariantCulture)] = v;
        return Obj(("cards", cards), ("fixed", fixd), ("res", Obj(("food", a.R("food")), ("wood", a.R("wood")), ("stone", a.R("stone")))),
            ("min", Obj(("food", a.MinOf("food")), ("wood", a.MinOf("wood")), ("stone", a.MinOf("stone")))),
            ("locked", Arr(a.Locked.Where(kv => kv.Value).Select(kv => kv.Key).OrderBy(x => x, StringComparer.Ordinal).Select(x => (JsonNode?)x))), ("collector", a.Collector));
    }
    static Army ArmyOfDump(JsonNode n)
    {
        var a = new Army();
        foreach (var (k, v) in (JsonObject)n["cards"]!) a.Cards[int.Parse(k)] = v.Str();
        foreach (var (k, v) in (JsonObject)n["fixed"]!) a.Fixed[int.Parse(k)] = v.Int();
        foreach (var r in PointBuy.RES) { a.Res[r] = n["res"]![r].Num(); a.Min[r] = n["min"]![r].Num(); }
        foreach (var l in (JsonArray)n["locked"]!) a.Locked[l.Str()] = true;
        a.Collector = n["collector"].Bool();
        return a;
    }
    static JsonArray Arr(IEnumerable<JsonNode?> l) => new(l.Select(x => x?.DeepClone()).ToArray());
    static JsonObject Obj(params (string K, JsonNode? V)[] kv) { var o = new JsonObject(); foreach (var (k, v) in kv) o[k] = v?.DeepClone(); return o; }

    static bool Same(JsonNode? a, JsonNode? b)
    {
        if (a == null || b == null) return a == null && b == null;
        if (a is JsonArray x && b is JsonArray y) return x.Count == y.Count && x.Zip(y).All(p => Same(p.First, p.Second));
        if (a is JsonObject o && b is JsonObject p) return o.Count == p.Count && o.All(kv => p.ContainsKey(kv.Key) && Same(kv.Value, p[kv.Key]));
        if (a is JsonValue va && b is JsonValue vb)
        {
            if (va.TryGetValue<double>(out var da) && vb.TryGetValue<double>(out var db)) return da == db || Math.Abs(da - db) <= 1e-12 * Math.Max(1, Math.Abs(da));
            return a.ToJsonString() == b.ToJsonString();
        }
        return false;
    }
    static string Show(JsonNode? n) { var s = n?.ToJsonString() ?? "null"; return s.Length > 400 ? s.Substring(0, 400) + "…" : s; }

    static readonly Dictionary<string, JsonNode> s_dumps = new();
    static JsonNode Dump(TestContext t, string data)
    {
        if (s_dumps.TryGetValue(data, out var d)) return d;
        string src = RulesTestData.RemakeSrc();
        if (!File.Exists(Path.Combine(src, "game", "pointbuy.js"))) t.Skip($"JS remake sources not found ({src}; PW_REMAKE_SRC)");
        string script = Path.Combine(AiTestData.RepoRoot(), "tests", "golden", "pointbuy_dump.mjs");
        string outDir = Path.Combine(Path.GetTempPath(), "pwremake-golden");
        Directory.CreateDirectory(outDir);
        string outFile = Path.Combine(outDir, "pointbuy_" + Math.Abs(data.GetHashCode()).ToString(CultureInfo.InvariantCulture) + ".json");
        var psi = new ProcessStartInfo("node", $"\"{script}\" \"{src}\" \"{data}\" \"{outFile}\"") { RedirectStandardError = true, RedirectStandardOutput = true };
        Process? p;
        try { p = Process.Start(psi); } catch (Exception e) { t.Skip("node not available: " + e.Message); return null!; }
        p!.WaitForExit(300000);
        string err = p.StandardError.ReadToEnd();
        if (p.ExitCode != 0) throw new Exception($"pointbuy_dump.mjs failed ({p.ExitCode}): {err}");
        return s_dumps[data] = Json.ParseFile(outFile)!;
    }
}
