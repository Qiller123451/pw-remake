// Unit tests of the rules' building blocks (the golden tests compare whole answers with the JS: RulesGoldenTests.cs).
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class RulesTests
{
    public static void TtNodeKeepsJsKeyOrder(TestContext t)
    {
        var n = TtNode.FromJson(JsonNode.Parse("{\"b\":\"1\",\"2\":\"x\",\"a\":\"2\",\"0\":\"y\",\"_value\":\"v\",\"10\":\"z\"}"));
        t.Equal("0,2,10,b,a,_value", string.Join(",", n.Keys), "integer keys first (ascending), then insertion order");
        t.Equal("v", n.Scalar, "an object's own value is _value");
        n["1"] = new TtNode("w"); n["c"] = new TtNode("c");
        t.Equal("0,1,2,10,b,a,_value,c", string.Join(",", n.Keys), "inserting keeps the order");
    }

    public static void ModificatorOps(TestContext t)
    {
        var root = TtNode.FromJson(JsonNode.Parse("{\"Objects\":{\"Hu\":{\"x\":{\"hp\":\"100\",\"vis\":{\"_value\":\"1\"}}}}}"));
        TT.ApplyMod(root, new TtMod("add", "/Objects/Hu/x/hp", new TtNode("25.5")));
        t.Equal("125.5", root["Objects"]!["Hu"]!["x"]!["hp"]!.Leaf, "add");
        TT.ApplyMod(root, new TtMod("multiply", "/Objects/Hu/x/hp", new TtNode("2")));
        t.Equal("251", root["Objects"]!["Hu"]!["x"]!["hp"]!.Leaf, "multiply (JS number to string)");
        TT.ApplyMod(root, new TtMod("replace", "/Objects/Hu/x/vis", new TtNode("0")));
        t.Equal("0", root["Objects"]!["Hu"]!["x"]!["vis"]!.Scalar, "replace keeps an object's children, sets its _value");
        TT.ApplyMod(root, new TtMod("replace", "/Objects/Hu/y/new/deep", new TtNode("7")));
        t.Equal("7", TT.NodeAt(root, "/Objects/Hu/y/new/deep")?.Leaf, "replace creates the path");
        TT.ApplyMod(root, new TtMod("remove", "/Objects/Hu/x", null));
        t.True(TT.NodeAt(root, "/Objects/Hu/x") == null, "remove");
        t.Equal("0.30000000000000004", TT.JsString(0.1 + 0.2), "String(0.1 + 0.2)");
        t.Equal("1e+21", TT.JsString(1e21), "String(1e21)");
        t.Equal("1e-7", TT.JsString(1e-7), "String(1e-7)");
        t.Equal("123456789012345680000", TT.JsString(123456789012345678901.0), "String(1.2345678901234568e20)");
        t.Equal(3.0, TT.Num("3abc"), "parseFloat");
        t.Equal(-1.0, TT.Num("Infinity", -1), "not finite: the default");
    }

    public static void TreeFiltersSwitchOnAndOff(TestContext t)
    {
        var R = Rules.Load(RulesTestData.Base(t));
        var T = R.NewTree("Hu");
        int v0 = T.Version;
        var o = new Player(0, "Hu") { Tt = T };
        t.True(!T.Invented("age_2"), "no epoch 2 at the start");
        T.Enable("Hu/Upgrades/hu_fireplace/age_2");
        t.True(T.Invented("age_2") && T.Version == v0 + 1, "the filter switched on rebuilds the tree");
        t.Equal(2, o.Epoch(), "the player's epoch");
        T.Enable("Hu/Upgrades/hu_fireplace/age_2");
        T.Disable("Hu/Upgrades/hu_fireplace/age_2");
        t.True(T.Invented("age_2"), "counted: still on after one of two is switched off");
        T.Disable("Hu/Upgrades/hu_fireplace/age_2");
        t.True(!T.Invented("age_2"), "off");
        T.Suspend(); T.Enable("Hu/Upgrades/hu_fireplace/age_2"); T.Enable("Hu/Upgrades/hu_fireplace/age_3");
        int v1 = T.Version; T.Resume();
        t.Equal(v1 + 1, T.Version, "suspended: one rebuild for both");
        var L = new LocalTree(T);
        t.True(L.Invented("age_3"), "a local tree follows its parent");
    }

    public static void BuffsOfAUnit(TestContext t)
    {
        var data = RulesTestData.Base(t);
        var R = Rules.Load(data);
        var man = PwRemake.Sim.Core.Json.ParseFile(System.IO.Path.Combine(data, "assets", "manifest.json"));
        if (man == null) t.Skip("no manifest");
        var M = new PwRemake.Sim.Content.ModelManifest(man!, null, null);
        var W = new PwRemake.Sim.World.World(R, M, new FlatMap(128));
        var p = new Player(0, "Hu", new PlayerOpts { Rules = R }); W.Players.Add(p);
        var q = new Player(1, "Aje", new PlayerOpts { Rules = R }); W.Players.Add(q);
        var u = W.SpawnUnit("hu_warrior", p, 0, 0)!;
        t.Equal(0, Buffs.BuffsOf(W, u).Count, "no buffs on a fresh warrior");
        u.St.Aura.Warcry = 4; u.St.T["trapped"] = 10; u.St.Camo.Add("disg"); u.St.Bonus.DEFENSE = 5;
        var b = Buffs.BuffsOf(W, u);
        t.True(b.Contains("more_damage") && b.Contains("is_held") && b.Contains("is_camouflaged") && b.Contains("more_defense"), string.Join(",", b));
        var own = Buffs.VisibleBuffs(W, u, p).Select(x => x.Attrib).ToList();
        var enemy = Buffs.VisibleBuffs(W, u, q).Select(x => x.Attrib).ToList();
        t.True(own.Count >= enemy.Count && own.Count > 0, $"the owner sees all ({own.Count}), an enemy by visibility ({enemy.Count})");
    }
}
