// MIRAGE's SEAS building classes (src/Sim/Systems/MirageSeasBuildings.cs, PortalTerminus.cs), one test per class, with
// the original's numbers (Mirage.usl, Nest.usl, FightingObj.usl, mirage_buildings.txt): the psi disruptor, the speaker,
// the Stygimoloch nest, the Tesla machine, the mine, the pub, the warp gate, the portal terminus and the towers' turrets
// (Kacper: "please port the rest of these, we do not want to lose any features from the original").
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class MirageSeasBuildingsTests
{
    static void Rich(Player p) { p.CapsFixed = true; foreach (var r in Rules.RES) { p.Caps[r] = 20000; p[r] = 10000; } p["skulls"] = 2000; }

    static (W_ W, Player P, Player E) World(TestContext t, string enemy = "Hu")
    {
        var (w, p, e) = Sys2.World(t, Sys2.Mirage, "SEAS", enemy);
        Rich(p); Rich(e);
        return (w, p, e);
    }

    static Building Place(TestContext t, W_ w, string name, Player p, double x, double z, bool built = true, double rot = 0)
    {
        var b = w.PlaceBuilding(name, p, x, z, rot, built);
        if (b == null) t.Skip("no model for " + name);
        return b!;
    }

    static List<string> Decos(Building b) => b.Parts.Where(p => p.Spec?.Kind == "deco").Select(p => p.Gfx).ToList();
    static string Join(IEnumerable<string> l) => string.Join(",", l);

    /// <summary>CSeasPsiDisruptor: "open" held shut on the site, played when finished; enemies within 40 m attack 1.5 x
    /// slower (seas_disruptor/reduce_speed on their own tree), once however many disruptors; own units not; leaving or the
    /// disruptor's end takes it away; the buffs</summary>
    public static void PsiDisruptor(TestContext t)
    {
        var (w, p, e) = World(t);
        t.Equal("CSeasPsiDisruptor", w.Data.Script("seas_disruptor"), "class");
        var b = Place(t, w, "seas_disruptor", p, 0, 0, built: false);
        if (b.Anim != null && b.Anim.Has("open")) t.True(b.Anim.CurName == "open" && b.Anim.Cur!.TimeScale == 0 && b.Anim.Cur.Time == 0, "a site: open held at its first frame");
        t.True(Buffs.BuffsOf(w, b).Contains("owner_less_attack_speed"), "the disruptor's buff from its site on");
        var foe = w.SpawnUnit("hu_warrior", e, 20, 0)!;
        var own = w.SpawnUnit("seas_soldier", p, 15, 0) ?? w.SpawnUnit("seas_worker", p, 15, 0)!;
        double Dur(Unit u) => u.Cs(u.Weapons.Long)!.Dur;
        double dur0 = Dur(foe), own0 = Dur(own);
        Sys2.Run(w, 1);
        t.Equal(0, foe.PsiDisrupt, "a site disrupts nobody");
        w.FinishBuilding(b);
        if (b.Anim != null && b.Anim.Has("open")) t.True(b.Anim.CurName == "open" && b.Anim.Cur!.TimeScale == 1, "finished: open plays");
        Sys2.Run(w, 1);
        t.Equal(1, foe.PsiDisrupt, "an enemy within 40 m");
        t.True(foe.LocalTT != null && foe.LocalTT.Has(W_.DISRUPT_PATH), "the filter on the enemy's own tree");
        t.Near(dur0 * 1.5, Dur(foe), 1e-6, "WeaponDuration x 1.5");
        t.True(Buffs.BuffsOf(w, foe).Contains("slower_fighting_rate"), "the unit's buff");
        t.Equal(0, own.PsiDisrupt, "own units are not disrupted");
        t.Near(own0, Dur(own), 1e-9, "own unit's attacks as before");
        // a second disruptor: still once
        var b2 = Place(t, w, "seas_disruptor", p, 30, 25);
        Sys2.Run(w, 1);
        t.Equal(2, foe.PsiDisrupt, "in two regions");
        t.Near(dur0 * 1.5, Dur(foe), 1e-6, "two disruptors do not add up");
        // out of both
        w.Teleport(foe, 100, 0);
        Sys2.Run(w, 1);
        t.Equal(0, foe.PsiDisrupt, "left the regions");
        t.True(!(foe.LocalTT != null && foe.LocalTT.Has(W_.DISRUPT_PATH)), "filter off");
        t.Near(dur0, Dur(foe), 1e-9, "attacks as fast as before");
        // the disruptor's end takes the flag away at once
        w.Teleport(foe, 22, 0);
        Sys2.Run(w, 1);
        t.True(foe.PsiDisrupt > 0, "back in");
        w.KillBuilding(b, null); w.KillBuilding(b2, null);
        Sys2.Run(w, 0.1);
        t.Equal(0, foe.PsiDisrupt, "the disruptors destroyed: no flag");
        t.Near(dur0, Dur(foe), 1e-9, "and no filter");
        // ships too, beyond 40 m nothing
        var far = w.SpawnUnit("hu_warrior", e, 60, 60)!;
        var b3 = Place(t, w, "seas_disruptor", p, 0, 0);
        Sys2.Run(w, 1);
        t.Equal(0, far.PsiDisrupt, "beyond 40 m");
    }

    /// <summary>CSeasSpeaker: finished, own characters / animals / vehicles within 50 m: + 20 defence, + 20 ranged
    /// defence (once); not the enemy's, not an ally's (AuraSharing off), not from a site</summary>
    public static void Speaker(TestContext t)
    {
        var (w, p, e) = World(t);
        t.Equal("CSeasSpeaker", w.Data.Script("seas_speaker"), "class");
        var b = Place(t, w, "seas_speaker", p, 0, 0, built: false);
        var own = w.SpawnUnit("seas_worker", p, 30, 0)!;
        var foe = w.SpawnUnit("hu_worker", e, 35, 5)!;
        var ally = new Player(3, "SEAS", new PlayerOpts { Rules = w.Data, Team = 1 });
        w.Players.Add(ally);
        var au = w.SpawnUnit("seas_worker", ally, 25, -5)!;
        Sys2.Run(w, 1);
        t.Equal(0.0, w.TempDef(own, 0), "a site gives nothing");
        w.FinishBuilding(b);
        Sys2.Run(w, 1);
        t.Equal(20.0, w.TempDef(own, 0), "+ 20 defence");
        t.Equal(20.0, w.TempRangedDef(own, 0), "+ 20 ranged defence");
        var bf = Buffs.BuffsOf(w, own);
        t.True(bf.Contains("more_defense") && bf.Contains("more_armor"), "the unit's buffs");
        var sb = Buffs.BuffsOf(w, b);
        t.True(sb.Contains("owner_more_defense") && sb.Contains("owner_more_armor"), "the speaker's buffs");
        t.Equal(0.0, w.TempDef(foe, 0), "not the enemy");
        t.Equal(0.0, w.TempDef(au, 0), "not an ally (AuraSharing off)");
        var b2 = Place(t, w, "seas_speaker", p, 40, 30);
        Sys2.Run(w, 1);
        t.Equal(2, own.SpeakerIn, "in two speakers' regions");
        t.Equal(20.0, w.TempDef(own, 0), "they do not add up");
        w.Teleport(own, 120, 0);
        Sys2.Run(w, 1);
        t.Equal(0.0, w.TempDef(own, 0), "out of the regions");
        w.Teleport(own, 10, 0);
        Sys2.Run(w, 1);
        w.KillBuilding(b, null); w.KillBuilding(b2, null);
        t.Equal(0, own.SpeakerIn, "the speakers destroyed: nothing");
    }

    /// <summary>CSeasStygiNest: finished, a grown autonomous stygimoloch of the owner every 10 s up to 2; 360 s after it was
    /// finished up to 4; they keep within 70 m; the nest's end leaves them alive</summary>
    public static void StygiNest(TestContext t)
    {
        var (w, p, _) = World(t);
        t.Equal("CSeasStygiNest", w.Data.Script("seas_stygimoloch_nest"), "class");
        if (!w.Data.Exists(W_.STYGI_SPAWN)) t.Skip("no " + W_.STYGI_SPAWN);
        var b = Place(t, w, "seas_stygimoloch_nest", p, 0, 0, built: false);
        Sys2.Run(w, 15);
        t.True(b.Stygi == null, "a site breeds nothing");
        w.FinishBuilding(b);
        Sys2.Run(w, 9.9);
        t.Equal(0, b.Stygi!.Members.Count, "none before 10 s");
        Sys2.Run(w, 0.2);
        t.Equal(1, b.Stygi.Members.Count, "one after 10 s");
        var u = b.Stygi.Members[0];
        t.Equal(W_.STYGI_SPAWN, u.Name, "a stygimoloch");
        t.True(u.Owner == p && u.Autonomous, "the owner's, acting by itself");
        t.Equal(W_.NEST_RADIUS_1, u.HomeRadius, "its home: 70 m");
        Sys2.Run(w, 10);
        t.Equal(2, b.Stygi.Members.Count, "two after 20 s");
        Sys2.Run(w, 30);
        t.Equal(2, b.Stygi.Members.Count, "spawn_max 2");
        Sys2.Run(w, 360 - 50 + 0.2);
        t.Equal(W_.NEW_EGGS, b.Stygi.Grow, "360 s after: + 2");
        Sys2.Run(w, 20.5);
        t.Equal(4, b.Stygi.Members.Count, "up to 4");
        // one dies: another after 10 s
        w.Kill(b.Stygi.Members[0], null);
        Sys2.Run(w, 5);
        t.Equal(3, b.Stygi.Members.Count, "one less");
        Sys2.Run(w, 5.2);
        t.Equal(4, b.Stygi.Members.Count, "a new one 10 s later");
        // one far away walks back
        var far = b.Stygi.Members[1];
        w.Teleport(far, 100, 0);
        far.Task = UnitTask.Idle();
        Sys2.Run(w, 2.1);
        t.True(far.Task.Type == "roam" && far.Task.WalkTo is { } to && Math.Sqrt(to.X * to.X + to.Z * to.Z) <= W_.NEST_RADIUS_1 + 2, "outside 70 m: back into the area");
        var kids = b.Stygi.Members.ToList();
        w.KillBuilding(b, null);
        t.True(kids.All(k => k.Alive), "the nest's end leaves its animals");
    }

    /// <summary>CTeslaMachine: a producer's rally point on a finished own Tesla machine - its units come out at the machine;
    /// not at a site, not at an enemy's machine; the classes that can (CRallyBuilding, CTemple, CNPCSeller, CSeasFortress)</summary>
    public static void TeslaMachine(TestContext t)
    {
        var (w, p, e) = World(t);
        t.Equal("CTeslaMachine", w.Data.Script("seas_tesla_machine"), "class");
        p.PopMax = 52; w.RecomputeCapsOf(p);
        var hq = Place(t, w, "seas_headquarters", p, 0, 0);
        t.True(w.TeslaProducer(hq), "the headquarters (CRallyBuilding)");
        if (w.Data.Exists("seas_pub")) t.True(w.TeslaProducer(Place(t, w, "seas_pub", p, -60, 60)), "the pub (CNPCSeller)");
        if (w.Data.Exists("hu_tavern")) t.True(w.Data.IsA("hu_tavern", "CNPCSeller"), "the Norse tavern too");
        var m = Place(t, w, "seas_tesla_machine", p, 80, 60);
        var a = w.Data.ActionsOf(hq.RulesOwner(), hq.Name, hq.Kind, hq.Def?.Type ?? hq.Cls).First(x => x.Kind == "Build" && x.Results.Count > 0 && x.Results[0].Obj == "seas_worker");
        // the rally point on the machine (SetRallyCommand, as the right click sends it)
        new PwRemake.Sim.Commands.SetRallyCommand { Player = p.Id, Producers = new[] { hq.Id }, X = m.Pos.X, Z = m.Pos.Z, Target = m.Id }.Apply(w);
        t.True(hq.TeslaM == m, "the producer's Tesla machine");
        Unit? Train()
        {
            t.Equal(null, w.QueueAction(hq, a), "queued");
            for (int i = 0; i < (int)((w.ProductionTime(a, p) + 3) / W_.STEP); i++) { int n = w.Events.Count; w.Step(); foreach (var ev in w.EventsSince(n)) if (ev.Type == "trained") return ev.U("unit"); }
            return null;
        }
        var u = Train()!;
        double d = Math.Sqrt((u.Pos.X - m.Origin.X) * (u.Pos.X - m.Origin.X) + (u.Pos.Z - m.Origin.Z) * (u.Pos.Z - m.Origin.Z));
        t.True(d <= m.Radius + 3, $"came out at the Tesla machine ({d:0.0} m from it)");
        // a site: not set
        var site = Place(t, w, "seas_tesla_machine", p, -80, -60, built: false);
        new PwRemake.Sim.Commands.SetRallyCommand { Player = p.Id, Producers = new[] { hq.Id }, X = site.Pos.X, Z = site.Pos.Z, Target = site.Id }.Apply(w);
        t.True(hq.TeslaM == null, "not a site");
        // an enemy's machine: not set
        var em = Place(t, w, "seas_tesla_machine", e, 80, -60);
        w.CheckTeslaMachine(hq, em);
        t.True(hq.TeslaM == null, "not an enemy's machine");
        // back to the machine, then it is destroyed: the door again
        w.CheckTeslaMachine(hq, m);
        w.KillBuilding(m, null);
        var u2 = Train()!;
        t.True(Math.Sqrt(u2.Pos.X * u2.Pos.X + u2.Pos.Z * u2.Pos.Z) < 40, "the machine gone: out of the headquarters");
    }

    /// <summary>CSeasMine: a stone field the SEAS worker hacks (hacking_stone, the pick) and carries in its backpack; its six
    /// parts once finished</summary>
    public static void Mine(TestContext t)
    {
        var (w, p, _) = World(t);
        t.Equal("CSeasMine", w.Data.Script("seas_mine"), "class");
        var site = Place(t, w, "seas_mine", p, 40, 40, built: false);
        t.Equal(0, Decos(site).Count, "no parts on the site");
        var b = Place(t, w, "seas_mine", p, 0, 30);
        var dec = Decos(b);
        t.Equal(6, dec.Count, "seas_mine_misc_0..5");
        t.True(dec.Contains("seas_hq_house_addon_06") && dec.Contains("seas_hq_house_addon_39"), "their models");
        t.True(b.Parts.Where(x => x.Spec?.Kind == "deco").All(x => x.VisLevel == 4), "finished parts");
        Place(t, w, "seas_headquarters", p, -40, -40);
        var u = w.SpawnUnit("seas_worker", p, 0, 10)!;
        w.Order(u, new Order("gather") { Target = b });
        bool hacked = false, pick = false; string? carried = null; double stone0 = p["stone"];
        for (int i = 0; i < (int)(120 / W_.STEP); i++)
        {
            w.Step();
            if (u.Anim!.CurName == "hacking_stone") hacked = true;
            if (u.ToolGfx != null && u.ToolGfx.EndsWith("_pick")) pick = true;
            if (u.Carry is { Amount: > 0, Look: { } lk }) carried = lk[1];
            if (p["stone"] > stone0) break;
        }
        t.True(hacked, "hacking_stone");
        t.True(pick, "with the pick");
        t.Equal("seas_backpack", carried, "the stone in the backpack");
        t.True(p["stone"] > stone0, "stone delivered");
        t.True(W_.IsMineField(b), "a mine field");
    }

    /// <summary>CSeasPub: a CNPCSeller (the SEAS tavern) with its gate and antenna once finished</summary>
    public static void Pub(TestContext t)
    {
        var (w, p, _) = World(t);
        t.Equal("CSeasPub", w.Data.Script("seas_pub"), "class");
        t.True(w.Data.IsA("seas_pub", "CNPCSeller"), "a tavern");
        var site = Place(t, w, "seas_pub", p, 40, 40, built: false);
        t.Equal(0, Decos(site).Count, "no parts on the site");
        w.FinishBuilding(site);
        t.Equal("seas_hq_house_addon_07,seas_hq_house_addon_14", Join(Decos(site)), "seas_pub_misc_0 / 1 when finished");
        var g = site.Parts.First(x => x.Spec?.Kind == "deco").Spec!;
        t.Near(1.0, g.OffsetModel.X, 1e-9, "the gate's offset (GSF -1.0, 3.05, -0.3 -> model frame)");
        t.Near(-0.3, g.OffsetModel.Y, 1e-9, "up"); t.Near(-3.05, g.OffsetModel.Z, 1e-9, "back");
    }

    /// <summary>CSeasWarpGate: a warp gate (the countdown and the victory, one per player, the AI's target); its parts;
    /// destroyed, the first part's ruin plays "destroy"</summary>
    public static void WarpGate(TestContext t)
    {
        var (w, p, e) = World(t);
        t.Equal("CSeasWarpGate", w.Data.Script("seas_warpgate"), "class");
        w.Opts.Warpgate = true; w.Opts.WarpgateMinutes = 1;
        var b = Place(t, w, "seas_warpgate", p, 0, 0, built: false);
        t.True(b.WarpSight != null, "shown to every player (CWarpGate)");
        w.FinishBuilding(b);
        t.Near(60, b.WarpT ?? -1, 1e-9, "the countdown");
        t.Equal("scorpion_hut_04_dest,seas_hq_babbage_machine", Join(Decos(b)), "seas_wg_misc_0 / 1");
        Sys2.Run(w, 60.1);
        t.True(p.Won && e.Defeated, "held: its owner wins");
        // without the option it deletes itself; one per player
        var (w2, p2, _) = World(t);
        var g2 = Place(t, w2, "seas_warpgate", p2, 0, 0, built: false);
        w2.Opts.Multiplayer = true;
        var g3 = Place(t, w2, "seas_warpgate", p2, 60, 60, built: false);
        Sys2.Run(w2, 0.2);
        t.True(!g3.Alive, "no warp gate option: gone");
        t.True(W_.IsWarpGateScript(w.Data.Script("seas_warpgate")), "the AI and the one-per-player rule know it");
        // the ruin: the first part plays destroy
        var (w3, p3, _) = World(t);
        w3.Opts.Warpgate = true;
        var b3 = Place(t, w3, "seas_warpgate", p3, 0, 0);
        w3.KillBuilding(b3, null);
        var rp = b3.RuinParts?.FirstOrDefault(x => x.Gfx == "scorpion_hut_04_dest");
        if (w3.Models.Clips("scorpion_hut_04_dest").Has("destroy")) t.True(rp != null && rp.From == "scorpion_hut_04_dest", "the hut's ruin part (plays destroy)");
        t.True(b3.RuinParts == null || b3.RuinParts.All(x => x.Gfx != "seas_hq_babbage_machine"), "the other part goes");
    }

    /// <summary>CPortalTerminus / CSeasPortalTerminus: the option makes it buildable; a rally point on another portal is the
    /// destination; a unit sent in comes out at the destination's release point, one a second; no destination: a walk</summary>
    public static void PortalTerminus(TestContext t)
    {
        var (w, p, e) = World(t);
        t.Equal("CSeasPortalTerminus", w.Data.Script("seas_portal_terminus"), "class");
        t.True(w.Data.IsA("seas_portal_terminus", "CPortalTerminus"), "a portal");
        // the option (without the warp gate): visible
        var path = "/Actions/SEAS/Build/BLDG/seas_portal_terminus";
        var (w0, p0, _) = World(t);
        w0.RaiseEpoch(p0, 5);
        t.Equal("hidden", w0.Data.Check(p0, w0.Data.Actions(p0).First(a => a.Path == path)), "hidden without the option");
        var o = new GameplayOptions { Portals = true };
        p.Gameplay = o; p.Tt = w.Data.PlayerTree("SEAS", o);
        w.RaiseEpoch(p, 5);                                  // (age_5 adds the other 1)
        var why = w.Data.Check(p, w.Data.Actions(p).First(a => a.Path == path));
        t.True(why != "hidden", $"offered with Portals ({why})");
        var a1 = Place(t, w, "seas_portal_terminus", p, -60, 0);
        var a2 = Place(t, w, "seas_portal_terminus", p, 70, 40);
        t.Equal("scorpion_hut_04_dest", Join(Decos(a1)), "seas_wg_misc_0");
        // a portal without a destination: the unit only walks there
        var u0 = w.SpawnUnit("seas_worker", p, -40, -20)!;
        w.Order(u0, new Order("portal") { Target = a1 });
        t.Equal("move", u0.Task.Type, "no destination: a walk");
        // a1's rally point on a2: the destination
        new PwRemake.Sim.Commands.SetRallyCommand { Player = p.Id, Producers = new[] { a1.Id }, X = a2.Pos.X, Z = a2.Pos.Z, Target = a2.Id }.Apply(w);
        t.True(a1.Portal!.Dest == a2, "the destination");
        t.True(w.PortalWorks(a1), "functionable");
        var us = Enumerable.Range(0, 3).Select(i => w.SpawnUnit("seas_worker", p, -40, -10 + 5 * i)!).ToList();
        foreach (var u in us) w.Order(u, new Order("portal") { Target = a1 });
        t.True(us.All(u => u.Task.Type == "portal"), "into the portal");
        var through = new List<double>();
        for (int i = 0; i < (int)(40 / W_.STEP) && through.Count < 3; i++) { int n = w.Events.Count; w.Step(); foreach (var ev in w.EventsSince(n)) if (ev.Type == "portal") through.Add(w.Time); }
        t.Equal(3, through.Count, "all three through");
        t.True(through[1] - through[0] >= 1 - 1e-6 && through[2] - through[1] >= 1 - 1e-6, "one a second");
        var (rx, rz) = w.PortalRallyPos(a2);
        // (the release point between the links Bl_1 and Bl_0, the portal's place when the model's links are unknown - the
        // test data has no hs_warpgate links: then the nearest free cell beside it)
        double reach = a2.LinkWorld("Bl_0") != null ? 8 : a2.Radius + 6;
        foreach (var u in us) t.True(Math.Sqrt((u.Pos.X - rx) * (u.Pos.X - rx) + (u.Pos.Z - rz) * (u.Pos.Z - rz)) < reach, "at the destination's release point");
        t.True(us.All(u => Math.Sqrt((u.Pos.X - a1.Pos.X) * (u.Pos.X - a1.Pos.X) + (u.Pos.Z - a1.Pos.Z) * (u.Pos.Z - a1.Pos.Z)) > 100), "far from the first portal");
        // a ship or an enemy cannot; the destination destroyed: forgotten
        var foe = w.SpawnUnit("hu_worker", e, -40, 30)!;
        t.True(!w.CanEnterPortal(foe, a1), "not an enemy");
        w.KillBuilding(a2, null);
        t.True(a1.Portal.Dest == null, "the destination destroyed: none");
        w.KillBuilding(a1, null);
        t.True(a1.RuinParts?.Any(x => x.Gfx == "scorpion_hut_04_dest") == true || !w.Models.Clips("scorpion_hut_04_dest").Has("destroy"), "its part's ruin");
    }

    /// <summary>the SEAS towers' turrets (CTower.SetTurret in SetReady): none on the site, finished (construction level 4)
    /// once the tower is; the great cannon's rotator from 80 %, its barrel on the rotator once finished, "attack_front"
    /// with every shot</summary>
    public static void Towers(TestContext t)
    {
        var (w, p, e) = World(t);
        foreach (var (name, top) in new[] { ("seas_mg_nest", "seas_hq_machinegun_nest_top"), ("seas_small_cannon", "seas_hq_defense_turret_top") })
        {
            var s = Place(t, w, name, p, 0, 0, built: false);
            t.True(s.Turret == null, $"{name}: no turret on the site");
            w.AddProgress(s, 0.9);
            t.True(s.Turret == null, $"{name}: none at 90 %");
            w.FinishBuilding(s);
            t.Equal(top, s.Turret?.Gfx, $"{name}: its turret when finished");
            t.Equal(4, s.Turret!.VisLevel, "at construction level 4");
            w.KillBuilding(s, null);
        }
        // the shore cannon (a map object): the carrier's turret 7.05 m below "we"
        var sc = Place(t, w, "seas_shore_cannon", p, 0, 60);
        t.Equal("seas_carrier_turret", sc.Turret?.Gfx, "the shore cannon's turret");
        t.Near(-7.05, sc.Turret!.Spec!.OffsetModel.Y, 1e-9, "7.05 m below the link (GSF z -7.05)");
        // the great cannon
        var g = Place(t, w, "seas_great_cannon", p, 0, -60, built: false);
        w.AddProgress(g, 0.79);
        t.True(g.Turret == null, "no rotator before 80 %");
        w.AddProgress(g, 0.02);
        t.Equal("seas_hq_big_cannon_rotator", g.Turret?.Gfx, "the rotator from 80 %");
        t.Equal(4, g.Turret!.VisLevel, "finished");
        t.True(g.Parts.All(x => x.Gfx != "seas_hq_big_cannon_cannon"), "no barrel yet");
        w.FinishBuilding(g);
        var barrel = g.Parts.FirstOrDefault(x => x.Gfx == "seas_hq_big_cannon_cannon");
        t.True(barrel != null && barrel.Parent == g.Turret, "the barrel on the rotator once finished");
        // a shot: the barrel plays attack_front (after the rotator turned)
        var rg = w.TowerRangeOf("seas_great_cannon", p) ?? (100, 0);
        t.Log($"great cannon: range {rg.R:0.0}, min {rg.Min:0.0}");
        var foe = w.SpawnUnit("hu_warrior", e, 0, -60 + Math.Max(rg.Min + g.Radius + 5, Math.Min(rg.R - 5, 60)))!;
        foe.Hp = foe.MaxHp = 1e9;
        bool fired = false;
        for (int i = 0; i < (int)(60 / W_.STEP) && !fired; i++) { w.Step(); if (barrel!.Anim?.CurName == "attack_front") fired = true; }
        if (barrel!.Anim != null && barrel.Anim.Has("attack_front")) t.True(fired, "the barrel's attack_front with the shot");
        // its damage stage follows the cannon's hit points (UpdateDestructionFlags)
        g.Hp = g.MaxHp * 0.4; g.UpdateVisual();
        t.Equal(1, barrel.VisDmg, "<= 50 %: stage 1");
        g.Hp = g.MaxHp * 0.2; g.UpdateVisual();
        t.Equal(2, barrel.VisDmg, "<= 25 %: stage 2");
    }
}
