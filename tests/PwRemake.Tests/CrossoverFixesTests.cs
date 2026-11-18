// The small fixes found by comparing the remake with the MIRAGE deliveries (docs/SIMULATION.md, docs/HUD.md):
// * statues (hu_berserk_statue_01..04, hu_avatar, hu_stone_statue) board no transport or bunker and climb up nothing
//   (the original's BoardTransport / EnterBunker, MIRAGE's mirage_statues_no_boarding: CCharacter.CannotClimbSiege);
// * a gate site blocks enemies from its placement on, its owner and allies pass (MIRAGE's mirage_gate_blocking);
// * turrets and towers shoot a peaceful wild animal that fights a neighbour (mirage_transport_turret_autotarget v2);
// * the minimap hides camouflaged enemies as the 3D view does (World.HiddenFrom);
// * the info window: the flowerpot's skull bonus, the amulet's defence against animals (mirage_item_stat_display).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Commands;
using PwRemake.Sim.World;
using PwRemake.UI.Hud.Logic;

namespace PwRemake.Tests;

[TestClass]
public static class CrossoverFixesTests
{
    public static void StatuesNeitherBoardNorClimb(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M, 256, 3);
        var p = Sys1.AddPlayer(W, R, 0, "Hu");
        var warrior = W.SpawnUnit("hu_warrior", p, 0, 0)!;
        var statues = new[] { "hu_berserk_statue_01", "hu_berserk_statue_04", "hu_avatar" }.Where(R.Exists).Select((n, i) => W.SpawnUnit(n, p, 10 + 8 * i, 0)).Where(u => u != null).ToList();
        if (statues.Count == 0) t.Skip("no statue classes in this data");
        t.True(World.IsStatue(statues[0]) && !World.IsStatue(warrior), "IsStatue");
        // a bunker
        var bunker = R.Exists("hu_bunker") ? W.PlaceBuilding("hu_bunker", p, 60, 60, 0, true) : null;
        if (bunker != null)
        {
            t.True(W.CanBoard(warrior, bunker), "a warrior enters the bunker");
            foreach (var s in statues) t.True(!W.CanBoard(s!, bunker), $"{s!.Name} does not enter the bunker");
        }
        // a transport a character boards
        Unit? tr = null;
        foreach (var n in R.Index.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!n.StartsWith("hu_") || R.Index[n].Type is not ("ANML" or "VHCL")) continue;
            var u = W.SpawnUnit(n, p, -60, -60);
            if (u == null) continue;
            if (W.Capacity(u) > 0 && W.CanBoard(warrior, u)) { tr = u; break; }
            W.Kill(u, null);
        }
        if (tr != null)
        {
            t.Log($"transport: {tr.Name}");
            foreach (var s in statues) t.True(!W.CanBoard(s!, tr), $"{s!.Name} does not board {tr.Name}");
        }
        t.True(bunker != null || tr != null, "a bunker or a transport to try");
        // climbing up: walkways, ladders, siege brachiosaurs (World.CanClimb / MayUseSiege)
        t.True(World.CanClimb(warrior), "a warrior climbs");
        foreach (var s in statues) t.True(!World.CanClimb(s), $"{s!.Name} does not climb");
    }

    public static void GateSiteBlocksEnemies(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M, 256, 3);
        var me = Sys1.AddPlayer(W, R, 0, "Hu", 1); var ally = Sys1.AddPlayer(W, R, 1, "Hu", 1); var foe = Sys1.AddPlayer(W, R, 2, "Aje", 2);
        var site = W.PlaceBuilding("hu_palisade_gate", me, 20, 20, 0, false);
        if (site == null) t.Skip("no hu_palisade_gate");
        t.True(!site!.Built && site.Cells.Count > 0, "a gate site");
        var nav = W.Nav;
        t.True(site.Cells.All(k => nav.GateAt.TryGetValue(k, out var g) && g == site), "its cells ask the gate from the placement on");
        t.True(site.Cells.All(k => nav.GateBlocks(k, foe)), "the site shuts out the enemy (was: a hole for every player)");
        t.True(site.Cells.All(k => !nav.GateBlocks(k, me) && !nav.GateBlocks(k, ally)), "its owner and allies pass");
        // an enemy walking across: not through the site
        var walker = W.SpawnUnit("aje_warrior", foe, 20, -10)!;
        W.SetAggro(new[] { walker }, -1);
        W.Order(new[] { walker }, new Order("move") { X = 20, Z = 50 });
        bool through = false;
        for (int i = 0; i < 20 * 25; i++) { W.Step(); if (site.Cells.Contains(nav.Idx(walker.Pos.X, walker.Pos.Z))) through = true; }
        t.True(!through, "an enemy's walk goes round the site");
        // finished: AUTO as before
        W.AddProgress(site, 1);
        t.True(site.Built, "finished");
        t.True(site.Cells.All(k => nav.GateBlocks(k, foe) && !nav.GateBlocks(k, me)), "finished: AUTO (owner passes, the enemy not)");
        W.Kill(site, null);
        t.True(site.Cells.All(k => !nav.GateAt.ContainsKey(k)), "destroyed: its cells are free");
    }

    public static void TowersShootAnAnimalFightingANeighbour(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M, 256, 3);
        var me = Sys1.AddPlayer(W, R, 0, "Hu", 1); var ally = Sys1.AddPlayer(W, R, 1, "Hu", 1);
        var tower = W.PlaceBuilding("hu_small_tower", me, 0, 0, 0, true)!;
        var cs = tower.Cs(tower.Weapons.Long)!;
        // a wild animal that is not aggressive and has a weapon (Triceratops, Stegosaurus ...)
        string? beast = new[] { "Triceratops", "Stegosaurus", "Mammoth", "Iguanodon" }.Concat(R.Index.Keys.OrderBy(k => k, StringComparer.Ordinal))
            .FirstOrDefault(n => R.Exists(n) && R.Index[n].Type == "ANML" && W.SpawnUnit(n, null, 200, 200) is { } a && Peaceful(W, a));
        if (beast == null) t.Skip("no peaceful wild animal");
        double d = cs.Range * 0.6;
        var victim = W.SpawnUnit("hu_warrior", ally, d + 4, 0)!;
        victim.Hp = victim.MaxHp = 1e6;
        W.SetAggro(new[] { victim }, -1);
        var animal = W.SpawnUnit(beast!, null, d, 0)!;
        animal.Hp = animal.MaxHp = 1e6;
        int Shots(int seconds)
        {
            int ev = W.Events.Count, n = 0;
            for (int i = 0; i < 20 * seconds; i++) { W.Step(); foreach (var e in W.EventsSince(ev)) if (e.Type == "shoot" && e.E("attacker") == tower) n++; ev = W.Events.Count; }
            return n;
        }
        // grazing: left alone
        t.Equal(0, Shots(4), $"a peaceful {beast} is left alone");
        // it attacks the ally's warrior next to the tower: the tower shoots it (was: only one that hit the tower)
        W.Order(new[] { animal }, new Order("attack") { Target = victim });
        t.Equal("attack", animal.Task.Type, "the animal fights the warrior");
        int s = Shots(8);
        t.True(s > 0 && animal.Hp < animal.MaxHp, $"the tower shoots the {beast} that fights its neighbour ({s} shots)");
    }
    static bool Peaceful(World W, Unit a) { bool ok = a.Wild && a.Def!.Aggressive != 1 && a.Weapons.Short != null || a.Wild && a.Def!.Aggressive != 1 && a.Weapons.Long != null; W.Kill(a, null); return ok; }

    sealed class Host : IHudContext
    {
        public Host(World w, Player me) { World = w; Me = me; }
        public World World { get; }
        public Player Me { get; }
        public bool Campaign => false;
        public bool Multiplayer => false;
        public void Submit(Command c) { }
        public List<Entity> Sel = new();
        public IReadOnlyList<Entity> Selection => Sel;
        public void SetSelection(IReadOnlyList<Entity> list, bool silent = false) { }
        public (double X, double Z, double Yaw) Camera => (0, 0, 0);
        public void MoveCamera(double x, double z) { }
        public IReadOnlyList<(double X, double Z)>? ViewCorners() => null;
        public IFogOfWar? Fog => null;
        public int FogVersion => 0;
        public string? InputMode => null;
        public void SetInputMode(string? mode, Sim.Rules.ActionDef? a = null) { }
        public IReadOnlyList<Entity>? Group(int k) => null;
        public string? SelectedGroup => null;
        public void GroupButton(int k, string how) { }
        public void OrderOn(Entity target, bool shift, bool ctrl) { }
        public (double X, double Z) MapToWorld(double mx, double my) => (mx, my);
        public void UiSound(params string[] names) { }
        public void ErrorSound() { }
        public void Ordered(string type, IReadOnlyList<Entity> units) { }
        public void OpenMenu() { }
        public void OpenChat(bool history) { }
    }

    public static void MinimapHidesCamouflagedEnemies(TestContext t)
    {
        var (R, M) = Sys1.LoadBare(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M, 256, 3);
        var me = Sys1.AddPlayer(W, R, 0, "Hu", 1); var foe = Sys1.AddPlayer(W, R, 1, "Aje", 2);
        var enemy = W.SpawnUnit("aje_warrior", foe, 30, 30)!;
        var table = Enumerable.Range(0, 6).Select(r => Enumerable.Range(0, 21).Select(c => new[] { 10 * r, 10 * c, 50, 255 }).ToArray()).ToArray();
        var m = new MinimapModel(new Host(W, me), MinimapSource.From(W.Map), table) { PlayerColors = false };
        m.BuildBackground();
        byte[] Shot() { m.Update(0, true); return (byte[])m.Tex.Clone(); }
        var shown = Shot();
        enemy.Parked = true; var none = Shot(); enemy.Parked = false;
        t.True(!shown.SequenceEqual(none), "the enemy's dot is drawn");
        W.SetCamoLayer(enemy, "test", true);
        t.True(W.HiddenFrom(enemy, me), "camouflaged: hidden from the player (the 3D view)");
        t.True(Shot().SequenceEqual(none), "camouflaged: no dot on the minimap either");
        t.True(!W.HiddenFrom(enemy, foe), "its owner still sees it");
        W.SetCamoLayer(enemy, "test", false);
        t.True(Shot().SequenceEqual(shown), "uncovered: drawn again");
    }

    public static void InfoShowsSkullAndDefenceBonuses(TestContext t)
    {
        var (R, M) = Sys1.Load(t, RulesTestData.Base(t));
        var W = Sys1.NewWorld(R, M, 256, 3);
        var me = Sys1.AddPlayer(W, R, 0, "Hu", 1); var ally = Sys1.AddPlayer(W, R, 1, "Hu", 1); var foe = Sys1.AddPlayer(W, R, 2, "Aje", 2);
        var host = new Host(W, me); var H = new HudModel(host);
        var mine = W.SpawnUnit("hu_warrior", me, 0, 0)!;
        var enemy = W.SpawnUnit("aje_warrior", foe, 30, 0)!;
        var friend = W.SpawnUnit("hu_warrior", ally, -30, 0)!;
        double sk = enemy.Stats!.Scalps;
        t.True(sk > 0, "an enemy warrior is worth skulls");
        t.Equal(Sim.Rules.TT.JsString(sk), H.ScalpsText(enemy), "no bonus: the skulls alone");
        // the flowerpot of insanity: x1.2 (base) - the info window shows the extra skulls, the kill pays them
        var pot = W.PlaceItem("item_flowerpot_of_insanity", 2, 0);
        if (pot == null) t.Skip("no item model");
        W.TakeItem(mine, pot!);
        t.True(me.ScalpMod > 1, $"the flowerpot's modifier ({me.ScalpMod})");
        double pay = W.ScalpsFor(enemy, me);
        t.True(pay > sk, "the kill pays more");
        t.Equal($"{Sim.Rules.TT.JsString(sk)} +{Sim.Rules.TT.JsString(pay - sk)}", H.ScalpsText(enemy), "the skulls with the bonus");
        if (friend.Stats!.Scalps != 0) t.Equal(Sim.Rules.TT.JsString(friend.Stats.Scalps), H.ScalpsText(friend), "an ally's unit: no bonus");
        double before = me["skulls"];
        enemy.LastDamager = me; W.Kill(enemy, mine);
        t.Equal(before + pay, me["skulls"], "the kill pays what the window showed");
        // the bony amulet: +10 against animals (Defence_ANML tec_abs) - in the rules and in the armour's tooltip
        W.DropItem(mine);
        t.Equal(0.0, W.DefenceBonusVs(mine, "ANML"), "no amulet: no bonus against animals");
        t.Equal(0, H.DefenceLines(mine).Count, "no tooltip lines");
        var am = W.PlaceItem("item_amulett1", mine.Pos.X + 1, 0);
        if (am == null) t.Skip("no amulet");
        W.TakeItem(mine, am!);
        t.Equal(10.0, W.DefenceBonusVs(mine, "ANML"), "the amulet: +10 against animals (TakeDmg adds tec_abs also without a DefenseBonus)");
        t.Equal(0.0, W.DefenceBonusVs(mine, "CHTR"), "not against characters");
        t.True(H.DefenceLines(mine).SequenceEqual(new[] { "Against animals: +10" }), $"the armour's tooltip: {string.Join(" | ", H.DefenceLines(mine))}");
        host.Sel = new List<Entity> { mine };
        var info = H.Info();
        int ai = info.Stats.FindIndex(s => s.Icon == "info_armor");
        t.True(ai >= 0 && info.StatLines.TryGetValue(ai, out var ls) && ls.Contains("Against animals: +10"), "the info window's armour row carries it");
    }
}
