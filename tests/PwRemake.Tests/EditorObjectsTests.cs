// Tests of the map editor's objects, players and map settings (src/Sim/Editor/Objects, Players; docs/MAP_EDITOR.md
// "Objects", "Players and map settings"): the object palette built from both configurations' data (base and MIRAGE),
// placing, moving, turning, deleting with undo / redo, owner / level / hit points / amounts, copy / paste / duplicate,
// the players and settings written to and read from the map, the skirmish setup Test map makes from the player slots,
// and a Test map world with the placed objects alive with the right owners and levels.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;

namespace PwRemake.Tests;

[TestClass]
public static class EditorObjectsTests
{
    static MapDataDoc Flat(int size = 384) => MapDataDoc.CreateFlat(new NewMapSpec { Size = size, BaseHeight = 20, Water = 16, Setting = "Jungle", Name = "Objects test" });

    static CampaignTests.Cfg Cfg(TestContext t, string id = "base")
    {
        var c = CampaignTests.LoadCfgPublic(id);
        if (c == null) t.Skip("no configuration " + id);
        return c!;
    }

    static readonly Dictionary<string, List<PaletteEntry>> s_pal = new();
    static List<PaletteEntry> Palette(CampaignTests.Cfg c, ISet<string>? baseNames = null)
    {
        string k = c.Config.Id + (baseNames != null ? "+" : "");
        if (!s_pal.TryGetValue(k, out var p)) s_pal[k] = p = ObjectPalette.Build(c.Rules, c.Models.Has, baseNames);
        return p;
    }

    static PaletteEntry E(List<PaletteEntry> pal, string cls) => pal.First(e => string.Equals(e.Cls, cls, StringComparison.OrdinalIgnoreCase));

    public static void EditorObjectsPaletteBase(TestContext t)
    {
        var c = Cfg(t);
        var pal = Palette(c);
        foreach (var cat in ObjectPalette.CATEGORIES) t.True(pal.Any(e => e.Category == cat), $"category {cat} has classes");
        var b = E(pal, "hu_warehouse");
        t.True(b.Category == "Buildings" && b.Group == "Hu" && b.Type == "BLDG" && b.Owned && b.Model != null, $"hu_warehouse: a Norse building with its model ({b})");
        var u = E(pal, "hu_warrior");
        t.True(u.Category == "Units" && u.Type == "CHTR" && u.MinLevel >= 1 && u.MaxLevel >= 3, $"hu_warrior: a unit with levels {u.MinLevel}..{u.MaxLevel}");
        t.True(u.Name.Length > 0 && u.Name != "hu_warrior", $"its name from the game's texts ({u.Name})");
        var a = E(pal, "Allosaurus");
        t.True(a.Category == "Wild animals" && a.Type == "ANML" && !a.Owned, "Allosaurus: a wild animal, nobody's");
        t.True(E(pal, "aje_stegosaurus").Category == "Tribe animals", "aje_stegosaurus: a tribe animal");
        t.True(pal.Count(e => e.Category == "Nests" && e.Type == "NEST") >= 9, "the nests");
        foreach (var g in new[] { "Trees", "Stones", "Fruit bushes", "Fish" }) t.True(pal.Any(e => e.Category == "Resources" && e.Group == g), $"resources: {g}");
        t.Equal("STON", E(pal, "resource_stone_jun").Type, "a stone's tag");
        t.Equal(2000.0, E(pal, "resource_stone_jun").Amount, "a stone's amount");
        t.Equal("FRUI", E(pal, "fishshoal_tristychius").Type, "fish shoals are FRUI (MapSource: CFishShoal)");
        t.True(pal.Any(e => e.Category == "Healing wells" && e.Type == "FNTN"), "healing wells");
        foreach (var g in new[] { "Rocks and cliffs", "Ruins and buildings", "Plants", "Effects" }) t.True(pal.Any(e => e.Category == "Decoration" && e.Group == g), $"decoration: {g}");
        t.True(pal.Where(e => e.Category == "Decoration").All(e => e.Model != null), "every decoration has its model");
        t.Equal("SLOC", E(pal, "startlocation").Type, "the start location");
        t.True(pal.Any(e => e.Type == "DOFL") && pal.Any(e => e.Type == "ITSP") && pal.Any(e => e.Type == "ITEM"), "flags, item spawns, items");
        t.True(!pal.Any(e => e.Mirage), "no MIRAGE marks without the base list");
        var f = ObjectPalette.Filter(pal, "Units", "spear hu");
        t.True(f.Any(e => e.Cls == "hu_spearman") && f.All(e => e.Category == "Units"), "search: words of name / class / tribe in a category");
        t.True(ObjectPalette.Filter(pal, ObjectPalette.ALL, "").Count == pal.Count, "All lists everything");
        t.True(ObjectPalette.DefaultNestSpecies(c.Rules, "Nest_Big_Carnivores") is string sp && c.Rules.Exists(sp), "a nest's default species");
        t.Log($"{pal.Count} classes");
    }

    public static void EditorObjectsPaletteMirage(TestContext t)
    {
        var c = Cfg(t, "mirage");
        var bc = Cfg(t, "base");
        var names = ObjectPalette.ClassNamesOf(bc.Data);
        t.True(names.Contains("hu_warehouse") && names.Contains("resource_stone_jun"), "the base game's class names");
        var pal = Palette(c, names);
        var fort = E(pal, "seas_fortress");
        t.True(fort.Mirage && fort.Category == "Buildings" && fort.Group == "SEAS", "seas_fortress: a MIRAGE building of the SEAS");
        t.True(!E(pal, "hu_warehouse").Mirage, "a base building is not marked");
        var mir = ObjectPalette.Filter(pal, ObjectPalette.MIRAGE, "");
        t.True(mir.Count > 100 && mir.All(e => e.Mirage), $"the MIRAGE category: {mir.Count} classes");
        t.True(mir.Any(e => e.Category == "Units") && mir.Any(e => e.Category == "Wild animals"), "MIRAGE units and wild animals");
        t.True(pal.Count > Palette(bc).Count, "MIRAGE has more classes than the base game");
    }

    public static void EditorObjectsPlaceMoveTurnDelete(TestContext t)
    {
        var c = Cfg(t);
        var pal = Palette(c);
        var d = Flat();
        var h = new EditHistory(d);
        int changes = 0; d.Changed += ch => { if (ch.Kind == DocChangeKind.Objects) changes++; };
        int n0 = d.Objects.Count;
        var o = ObjectOps.Create(E(pal, "hu_warehouse"), d, 100, 120, 0.5, 0, 1, c.Rules);
        h.Execute(ObjectsAddRemove.Add(d, new[] { o }));
        t.Equal(n0 + 1, d.Objects.Count, "placed");
        t.True(o.Type == "BLDG" && o.Owner == 0 && o.Level == 0 && o.Z == 20, "a building of slot 0 on the ground");
        t.Near(0.5, -2 * Math.Atan2(o.Q[2], o.Q[3]), 1e-12, "the quaternion says the heading (Ula: Rot = -2 atan2(qz, qw))");
        t.True(changes == 1 && h.Dirty, "one change, dirty");
        // a drag: many steps, one undo step
        for (int k = 1; k <= 5; k++) h.Execute(ObjectsEdit.Make("Move objects", new[] { o }, x => ObjectOps.MoveTo(x, d, 100 + k * 2, 120), mergeable: true)!, merge: true);
        h.EndMerge();
        t.Near(110, o.X, 1e-9, "moved");
        t.Equal(2, h.UndoCount, "place + one move");
        h.Undo();
        t.Near(100, o.X, 1e-9, "the move undone at once");
        h.Redo();
        // turning: the heading and the quaternion together; a tilted piece keeps its tilt
        h.Execute(ObjectsEdit.Make("Turn objects", new[] { o }, x => ObjectOps.SetRot(x, 2.0))!);
        t.Near(2.0, o.Rot, 1e-12, "turned");
        var mq = Frame.MapQuat(o.Q);
        double heading = 2 * Math.Atan2(mq[1], mq[3]);
        t.Near(Frame.MapHeading(2.0), ObjectOps.Norm(heading) < 0 ? heading + 2 * Math.PI : heading, 1e-9, "the world heading as the map source's (pi - rot)");
        var tilt = new MapObject { Type = "DCCO", Cls = "rock", Q = new[] { 0.2, 0.1, 0.3, 0.927 } };
        tilt.Rot = -2 * Math.Atan2(tilt.Q[2], tilt.Q[3]);
        var q0 = (double[])tilt.Q.Clone();
        var up0 = Up(Frame.MapQuat(q0));
        ObjectOps.Turn(tilt, 0.7);
        var up1 = Up(Frame.MapQuat(tilt.Q));
        t.Near(up0.Y, up1.Y, 1e-9, "a tilted piece keeps its tilt (its up vector's height)");
        t.Near(0.7, ObjectOps.Norm(tilt.Rot - (-2 * Math.Atan2(q0[2], q0[3]))), 0.25, "and turns about the up axis by about the angle");
        ObjectOps.Turn(tilt, -0.7);
        for (int k = 0; k < 4; k++) t.Near(q0[k] / Norm(q0), tilt.Q[k], 1e-9, "turned back: the same quaternion");
        // delete and undo: the same place in the list
        var other = ObjectOps.Create(E(pal, "Allosaurus"), d, 50, 50, 0, null, 1, c.Rules);
        h.Execute(ObjectsAddRemove.Add(d, new[] { other }));
        int idx = d.Objects.ToList().IndexOf(o);
        h.Execute(ObjectsAddRemove.Remove(d, new[] { o })!);
        t.True(!d.Objects.Contains(o), "deleted");
        h.Undo();
        t.Equal(idx, d.Objects.ToList().IndexOf(o), "undo: back at its place in the list");
        h.Redo();
        t.True(!d.Objects.Contains(o) && d.Objects.Contains(other), "redo: deleted again, the other stays");
        while (h.Undo()) { }
        t.Equal(n0, d.Objects.Count, "everything undone");
        t.True(!h.Dirty, "clean");
    }

    static (double X, double Y, double Z) Up(double[] q)
    {
        // the world up vector (0, 1, 0) turned by q = (x, y, z, w)
        double x = q[0], y = q[1], z = q[2], w = q[3];
        return (2 * (x * y - w * z), 1 - 2 * (x * x + z * z), 2 * (y * z + w * x));
    }
    static double Norm(double[] q) => Math.Sqrt(q.Sum(v => v * v));

    public static void EditorObjectsOwnerLevelAttributes(TestContext t)
    {
        var c = Cfg(t);
        var pal = Palette(c);
        var d = Flat();
        var h = new EditHistory(d);
        var u = ObjectOps.Create(E(pal, "hu_warrior"), d, 100, 100, 0, 0, 9, c.Rules);
        t.Equal(E(pal, "hu_warrior").MaxLevel - 1, u.Level, "the level clamped to the class's (0-based)");
        var w = ObjectOps.Create(E(pal, "Allosaurus"), d, 120, 100, 0, 3, 1, c.Rules);
        t.True(w.Owner == null, "a wild animal is nobody's, whatever the owner asked");
        var s = ObjectOps.Create(E(pal, "resource_stone_jun"), d, 140, 100, 0, null, 1, c.Rules);
        t.Equal(2000.0, ObjectOps.Amount(s, 0), "a stone's amount (hitpoints)");
        var nest = ObjectOps.Create(E(pal, "Nest_Hadrosaurs"), d, 160, 100, 0, null, 1, c.Rules);
        t.True(nest.Attr.TryGetValue("spawn_type", out var sp) && c.Rules.Exists(sp), $"a nest's species ({sp})");
        var fish = ObjectOps.Create(E(pal, "fishshoal_tristychius"), d, 180, 100, 0, null, 1, c.Rules);
        t.Near(20, fish.Z, 1e-9, "a fish shoal on dry ground stays on the ground");
        h.Execute(ObjectsAddRemove.Add(d, new[] { u, w, s, nest }));
        h.Execute(ObjectsEdit.Make("Owner", new[] { u }, x => x.Owner = 5)!);
        h.Execute(ObjectsEdit.Make("Level", new[] { u }, x => x.Level = 1)!);
        h.Execute(ObjectsEdit.Make("Hit points", new[] { u }, x => ObjectOps.SetHpPercent(x, 40))!);
        h.Execute(ObjectsEdit.Make("Amount", new[] { s }, x => x.Attr["hitpoints"] = "777")!);
        h.Execute(ObjectsEdit.Make("Name", new[] { u }, x => x.Name = ObjectOps.UniqueName(d, "guard", x.Cls, x))!);
        t.True(u.Owner == 5 && u.Level == 1 && ObjectOps.HpPercent(u) == 40 && u.Name == "guard", "owner 5, level 2, 40 %, named");
        t.Equal(777.0, ObjectOps.Amount(s, 0), "amount");
        t.True(ObjectsEdit.Make("Nothing", new[] { u }, x => x.Owner = 5) == null, "an edit that changes nothing is none");
        ObjectOps.SetHpPercent(w, 100);
        t.True(!w.Attr.ContainsKey("hp_percent"), "100 % is no attribute");
        h.Undo(); h.Undo(); h.Undo();
        t.True(ObjectOps.HpPercent(u) == 100 && u.Level == 1 && ObjectOps.Amount(s, 0) == 2000, "undone: hit points and amount back, the level stays");
        h.Undo(); h.Undo();
        t.True(u.Owner == 0 && u.Level == E(pal, "hu_warrior").MaxLevel - 1, "owner and level back");
    }

    public static void EditorObjectsCopyPaste(TestContext t)
    {
        var c = Cfg(t);
        var pal = Palette(c);
        var d = Flat();
        var h = new EditHistory(d);
        var a = ObjectOps.Create(E(pal, "hu_warrior"), d, 100, 100, 0.3, 1, 2, c.Rules);
        var b = ObjectOps.Create(E(pal, "hu_warehouse"), d, 110, 104, 1.1, 1, 1, c.Rules);
        a.Name = "gate_guard";
        h.Execute(ObjectsAddRemove.Add(d, new[] { a, b }));
        var clip = new ObjectClipboard();
        clip.Copy(new[] { a, b });
        var pasted = clip.PasteAt(d, 200, 200);
        h.Execute(ObjectsAddRemove.Add(d, pasted, "Paste objects"));
        t.Equal(2, pasted.Count, "two copies");
        t.Near(10, pasted[1].X - pasted[0].X, 1e-9, "their places relative to each other kept (x)");
        t.Near(4, pasted[1].Y - pasted[0].Y, 1e-9, "(y)");
        t.Near(200, (pasted[0].X + pasted[1].X) / 2, 1e-9, "centred at the paste point");
        t.True(pasted[0].Rot == a.Rot && pasted[0].Owner == 1 && pasted[0].Level == a.Level && pasted[0].Cls == a.Cls, "class, heading, owner, level copied");
        t.True(pasted[0].Name == "gate_guard_2" && pasted[1].Name == b.Cls, "a trigger name made unique; a class name stays");
        t.True(!ReferenceEquals(pasted[0].Attr, a.Attr) && !ReferenceEquals(pasted[0].Q, a.Q), "deep copies");
        // duplicate: copy + paste a little aside; start locations take free slots
        var st = d.Objects.First(o => o.Type == "SLOC");
        clip.Copy(new[] { st, st });
        var dup = clip.PasteAt(d, st.X + 8, st.Y + 8);
        t.True(dup.All(o => o.Type == "SLOC") && dup[0].Owner == 2 && dup[1].Owner == 3, $"duplicated start locations take the free slots 2, 3 ({dup[0].Owner}, {dup[1].Owner})");
        h.Execute(ObjectsAddRemove.Add(d, dup, "Duplicate objects"));
        t.Equal(4, d.MaxPlayers, "four start locations: four players");
        h.Undo();
        t.Equal(2, d.MaxPlayers, "undone: two");
    }

    public static void EditorPlayersAndSettings(TestContext t)
    {
        var d = Flat();
        var h = new EditHistory(d);
        var p0 = d.GetPlayers();
        t.Equal(MapPlayerSettings.SLOTS, p0.Slots.Count, "eight slots");
        t.True(p0.Slots[0].Team == 1 && p0.Slots[1].Team == 2 && p0.Slots[0].Color != p0.Slots[1].Color, "teams and colours by default");
        h.Execute(PlayersEdit.Make(d, p =>
        {
            p.Slots[1].Tribe = "Aje"; p.Slots[1].Tribes = new() { "Aje", "Ninigi" }; p.Slots[1].Team = 3; p.Slots[1].Control = "computer";
            p.Slots[0].Food = 500; p.Slots[0].Wood = 400; p.Slots[0].PopMax = 60; p.Points = 3000; p.FlyingEnabled = true;
            p.SetRelation(0, 1, 2);
        }));
        var p1 = d.GetPlayers();
        t.True(p1.Slots[1].Tribe == "Aje" && p1.Slots[1].Tribes.SequenceEqual(new[] { "Aje", "Ninigi" }) && p1.Slots[1].Team == 3 && p1.Slots[1].Control == "computer", "slot 1 read back");
        t.True(p1.Slots[0].Food == 500 && p1.Slots[0].PopMax == 60 && p1.Points == 3000 && p1.FlyingEnabled, "resources, population, budget, MIRAGE option");
        t.True(p1.Slots[0].Diplomacy[1] == 2 && p1.Slots[1].Diplomacy[0] == 2 && p1.Slots[0].Diplomacy[2] == 0, "diplomacy both ways");
        t.Equal("12000000", d.Md.Description["Root/PlayerSettings/Player_0/Diplomacy"], "the original's diplomacy digits (1 towards itself)");
        // the map source reads them as an original map's (MpSettingsOf)
        var mp = MapSource.MpSettingsOf(d.Md);
        t.True(mp.Slots["1"].Tribes!.SequenceEqual(new[] { "Aje", "Ninigi" }) && mp.Slots["1"].Team == 3 && mp.Slots["0"].Res["food"] == 500 && mp.Slots["0"].PopMax == 60, "MapSource reads the slots");
        h.Undo();
        t.True(d.GetPlayers().Slots[1].Team == 2 && d.GetPlayers().Points == -1, "undone");
        // settings
        h.Execute(SettingsEdit.Make(d, s =>
        {
            s.Name = "Two Rivers"; s.Author = "Kacper"; s.Descriptions["uk"] = "Two tribes, one valley."; s.Descriptions["de"] = "Zwei Stämme.";
            s.StartTime = 18.5 * 3600; s.Weather = 2; s.WeatherIntensity = 8; s.WindDir = 2; s.DefaultCamera = 2.355; s.Atmos = "savanna.txt";
            s.GameTypes = new() { "Deathmatch", "Domination" };
            s.Preview = MapPreviewMaker.Make(d);
        }));
        var st = d.GetSettings();
        t.True(st.Name == "Two Rivers" && d.Name == "Two Rivers" && st.Author == "Kacper" && st.Descriptions["de"] == "Zwei Stämme.", "name, author, descriptions per language");
        t.True(st.StartTime == 18.5 * 3600 && st.Weather == 2 && st.WeatherIntensity == 8 && st.WindDir == 2 && st.DefaultCamera == 2.355 && st.Atmos == "savanna.txt", "time, weather, camera, atmosphere");
        t.True(st.Preview != null && st.Preview.W == 200 && st.Preview.Rgba.Length == 200 * 200 * 4, "a 200 x 200 preview");
        var c = CampaignTests.LoadCfgPublic("base");
        if (c != null)
        {
            var S = d.ToSource(new RulesMapAdapter(c.Rules), c.Models.Has, c.Forest);
            t.True(S.StartTime == 18.5 * 3600 && S.DefaultCamera == 2.355 && S.Atmos == "savanna.txt" && S.LevelName == "Two Rivers", "the map source plays them");
            t.True(S.Weather != null && S.Weather.Tracks.Count == 1 && S.Weather.Tracks[0].Type == 2 && S.Weather.Tracks[0].Intensity == 8 && S.Weather.Tracks[0].Wind == 270, "the weather program (rain, wind from the west)");
            t.True(S.Mp.GameTypes.SequenceEqual(new[] { "Deathmatch", "Domination" }), "game types");
        }
        h.Undo();
        t.True(d.Name == "Objects test" && d.GetSettings().Weather == 0, "undone");
        t.Equal("18:30", MapSettings.TimeText(18.5 * 3600), "time text");
    }

    public static void EditorPlayersSkirmishSetup(TestContext t)
    {
        var d = Flat();
        // three start locations: slots 0, 1 and a third one (slot 2); slot 0 a computer's, slot 1 either: you play slot 1
        var st = d.Objects.First(o => o.Type == "SLOC");
        var third = ObjectOps.Copy(st); third.X = 192; third.Y = 60; third.Owner = 2;
        d.InsertObject(d.Objects.Count, third); d.ObjectsChanged();
        t.Equal(3, d.MaxPlayers, "three players from three start locations");
        var P = d.GetPlayers();
        P.Slots[0].Control = "computer"; P.Slots[0].Tribe = "Ninigi";
        P.Slots[1].Tribe = "Aje"; P.Slots[2].Tribe = "SEAS"; P.Slots[2].Team = 2; P.Points = 1000; P.RemoveTitans = true;
        d.SetPlayers(P);
        var s = PlayersSetup.Make(d, new[] { "Hu", "Aje", "Ninigi" }, "editor:9", "Test");
        var act = Skirmish.ActiveSlots(s);
        t.Equal(3, act.Count, "three setup slots");
        t.True(s.Slots[0].Type == "human" && s.Slots[0].Hq == 1 && s.Slots[0].Tribe == "Aje", "you: slot 1 (the first a human may play), Dustriders");
        t.True(s.Slots[1].Type == "ai" && s.Slots[1].Hq == 0 && s.Slots[1].Tribe == "Ninigi", "a computer on slot 0");
        t.True(s.Slots[2].Hq == 2 && s.Slots[2].Tribe == "Random", "SEAS is not playable in base: Random");
        t.True(s.ChooseHQ && s.Points == 1000 && s.RemoveTitans && s.Map == "editor:9", "head quarters chosen, the budget, MIRAGE's option, the key");
        t.True(Skirmish.ColorClash(s) == null, "every player a colour of their own");
    }

    public static void EditorObjectsTestMapWorld(TestContext t)
    {
        var c = Cfg(t);
        var pal = Palette(c);
        var d = Flat();
        var h = new EditHistory(d);
        // slot 0's warehouse, slot 1's warrior (level 3, half hit points), slot 1's stegosaurus, a wild allosaurus, a stone
        // of 1234, a nest, a rock
        var wh = ObjectOps.Create(E(pal, "hu_warehouse"), d, 120, 130, 0, 0, 1, c.Rules);
        var war = ObjectOps.Create(E(pal, "hu_warrior"), d, 260, 250, 0, 1, 3, c.Rules);
        ObjectOps.SetHpPercent(war, 50);
        var steg = ObjectOps.Create(E(pal, "aje_stegosaurus"), d, 250, 262, 0, 1, 1, c.Rules);
        var allo = ObjectOps.Create(E(pal, "Allosaurus"), d, 192, 300, 0, null, 1, c.Rules);
        var stone = ObjectOps.Create(E(pal, "resource_stone_jun"), d, 200, 100, 0, null, 1, c.Rules);
        stone.Attr["hitpoints"] = "1234";
        var nest = ObjectOps.Create(E(pal, "Nest_Hadrosaurs"), d, 300, 192, 0, null, 1, c.Rules);
        h.Execute(ObjectsAddRemove.Add(d, new[] { wh, war, steg, allo, stone, nest }));
        var heights = (float[])d.Md.Heights.Clone();
        string key = EditorPlaytest.Register(d);
        var setup = PlayersSetup.Make(d, c.Config.PlayableTribes, key, d.Name);
        setup.Slots[0].Tribe = "Hu"; setup.Slots[1].Tribe = "Aje";
        var S = EditorPlaytest.Source(key, new RulesMapAdapter(c.Rules), c.Models.Has, c.Forest);
        var sk = SkirmishSession.Create(setup, null, c.Rules, c.Models, c.Forest, c.Brushes, c.Ai, null, null, SimFlags.Game, S);
        var W = sk.World;
        var p0 = W.Players.First(p => p.Hq == 0);
        var p1 = W.Players.First(p => p.Hq == 1);
        (double X, double Z) G(MapObject o) => S.ToGame(o.X, o.Y);
        var gw = G(wh);
        var b = W.Buildings.FirstOrDefault(x => x.Name == "hu_warehouse" && Math.Abs(x.Pos.X - gw.X) < 1 && Math.Abs(x.Pos.Z - gw.Z) < 1);
        t.True(b != null && b.Owner == p0, "the warehouse stands, slot 0's player's");
        var gu = G(war);
        var u = W.Units.FirstOrDefault(x => x.Name == "hu_warrior" && Math.Abs(x.Pos.X - gu.X) < 3 && Math.Abs(x.Pos.Z - gu.Z) < 3);
        t.True(u != null && u.Owner == p1 && u.Level == 3, $"the warrior: slot 1's player's, level 3 ({u?.Level})");
        t.Near(u!.MaxHp * 0.5, u.Hp, 1, "half its hit points");
        var gs = G(steg);
        t.True(W.Units.Any(x => x.Name == "aje_stegosaurus" && x.Owner == p1 && Math.Abs(x.Pos.X - gs.X) < 3), "the tribe animal comes alive for slot 1's player");
        var ga = G(allo);
        t.True(W.Units.Any(x => x.Name == "Allosaurus" && x.Owner == null && Math.Abs(x.Pos.X - ga.X) < 3), "the wild allosaurus, nobody's");
        var gst = G(stone);
        t.True(W.Resources.Any(r => r.Type == "stone" && r.Amount == 1234 && Math.Abs(r.Pos.X - gst.X) < 0.5), "the stone with its amount");
        t.True(W.WildNests.Count == 1, "the nest");
        t.True(W.Players.Count == 2 && p0.Tribe == "Hu" && p1.Tribe == "Aje", "the players of the slots");
        for (int i = 0; i < 200; i++) sk.Step();
        t.True(d.Md.Heights.SequenceEqual(heights), "the buildings levelled the snapshot's ground, not the edited map's");
        EditorPlaytest.Clear();
    }

    public static void EditorObjectsViewSource(TestContext t)
    {
        var c = Cfg(t);
        var pal = Palette(c);
        var d = Flat();
        d.InsertObject(d.Objects.Count, ObjectOps.Create(E(pal, "hu_warehouse"), d, 120, 130, 0, 0, 1, c.Rules));
        d.InsertObject(d.Objects.Count, ObjectOps.Create(E(pal, E(pal, "resource_stone_jun").Cls), d, 140, 130, 0, null, 1, c.Rules));
        d.ObjectsChanged();
        var R = new RulesMapAdapter(c.Rules);
        var full = d.ToSource(R, c.Models.Has, c.Forest);
        var view = d.ToViewSource(R, c.Models.Has, c.Forest);
        t.True(full.Owned.Count == 1 && full.Stones.Count == 1, "the game's source has the objects");
        t.True(view.Owned.Count == 0 && view.Stones.Count == 0 && view.Starts.Count == 2, "the view's has the start locations only");
        // the view's source shares the grids: an edit shows at once
        d.SetHeight(10, 10, 33); d.HeightsChanged(new GridRect(10, 10, 10, 10));
        var (x, z) = view.ToGame(d.OriginX + 20, d.OriginY + 20);
        t.Near(33, view.Height(x, z), 1e-4, "the view's source reads the edited heights");
    }
    /// <summary>the editor's objects, players and settings saved in the new map format (PwMap.FromMapData + PwMapWriter:
    /// objects.lua, map.lua's settings tree, minimap.png) and read back the same</summary>
    public static void EditorObjectsPlayersRoundTripPwMap(TestContext t)
    {
        var c = Cfg(t);
        var pal = Palette(c);
        var d = Flat(256);
        var u = ObjectOps.Create(E(pal, "hu_warrior"), d, 100, 100, 0.7, 1, 3, c.Rules);
        ObjectOps.SetHpPercent(u, 60);
        u.Name = "bridge_guard";
        d.InsertObject(d.Objects.Count, u);
        d.InsertObject(d.Objects.Count, ObjectOps.Create(E(pal, "Nest_Hadrosaurs"), d, 150, 150, 0, null, 1, c.Rules));
        d.ObjectsChanged();
        var snap = d.Clone();                      // (a Test map snapshot taken before: it must not see the edits below)
        d.SetPlayers(Edit(d.GetPlayers(), p => { p.Slots[1].Tribe = "Ninigi"; p.Slots[1].Team = 4; p.Slots[0].Wood = 900; p.Points = 10000; p.SetRelation(0, 1, 1); }));
        d.SetSettings(Edit(d.GetSettings(), s => { s.Name = "Round trip"; s.Author = "Editor"; s.Descriptions["de"] = "Hin und zurück"; s.Weather = 1; s.StartTime = 7 * 3600; s.Preview = MapPreviewMaker.Make(d, 64); }));
        t.True(((MapDataDoc)snap).GetPlayers().Slots[1].Team == 2 && snap.Name == "Objects test", "the snapshot keeps its own settings tree");
        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "edobjects-roundtrip-" + Environment.ProcessId);
        try
        {
            PwMapWriter.WriteFolder(PwMap.FromMapData(d.Md), dir);
            var back = new MapDataDoc(PwMapReader.Read(dir).ToMapData(), dir);
            var P = back.GetPlayers();
            t.True(P.Slots[1].Tribe == "Ninigi" && P.Slots[1].Team == 4 && P.Slots[0].Wood == 900 && P.Points == 10000 && P.Slots[0].Diplomacy[1] == 1, "the players read back");
            var S = back.GetSettings();
            t.True(S.Name == "Round trip" && back.Name == "Round trip" && S.Author == "Editor" && S.Descriptions.TryGetValue("de", out var de) && de == "Hin und zurück", "name, author, description");
            t.True(S.Weather == 1 && S.StartTime == 7 * 3600 && S.Preview != null && S.Preview.W == 64, "weather, time, preview");
            var bu = back.Objects.FirstOrDefault(o => o.Name == "bridge_guard");
            t.True(bu != null && bu.Cls == "hu_warrior" && bu.Owner == 1 && bu.Level == u.Level && ObjectOps.HpPercent(bu) == 60, "the named unit: class, owner, level, hit points");
            t.Near(u.Rot, bu!.Rot, 1e-4, "its heading");
            t.True(back.Objects.Any(o => o.Type == "NEST" && o.Attr.ContainsKey("spawn_type")), "the nest with its species");
            t.Equal(2, back.MaxPlayers, "two players");
        }
        finally { try { System.IO.Directory.Delete(dir, true); } catch (Exception) { } }
    }

    static T Edit<T>(T x, Action<T> f) { f(x); return x; }
}
