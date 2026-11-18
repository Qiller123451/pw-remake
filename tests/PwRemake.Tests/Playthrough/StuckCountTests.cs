// The playthrough bot's teleport (CampaignBot.go): a unit that makes no progress towards go()'s goal for 60 s is
// teleported. Mission 12 (base, as the Norsemen) counted 23-48 teleports, none of a unit that could not get on: the
// fleet stood shooting a carrier for a minute on hunt()'s attack orders, and when go() sent it after the next carrier
// the count that had begun at the last go() teleported the whole fleet at once; the army that had stood guard at the
// tower ruin was teleported when the next step sent it home. Now the count runs only while go() keeps sending the unit.
// Mission 9's army sent to the launch ramp's centre, 20 m inside the rocks round it, stood at their edge and was
// teleported onto it 45 times a run: a goal on blocked ground is measured from the ring of free ground round it.
using System;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;

namespace PwRemake.Tests.Playthrough;

[TestClass]
public static class StuckCountTests
{
    sealed class Probe : CampaignBot
    {
        public Probe(MissionSession s, PlayPresenter p) : base(s, p) { }
        public override string Play() => "";
        public bool Go(Unit u, double mx, double my) => go(new[] { u }, mx, my, "probe");
        public (double X, double Y) Map(Entity e) => C.ToMap(e.Pos.X, e.Pos.Z);
        public void Put(Unit u, double mx, double my) => C.Teleport(Registry.Get(u), mx, my);
    }

    /// <summary>a unit sent to the centre of a big building stands at its wall, where its walk ends: there, not stuck</summary>
    public static void GoalOnBlockedGround(TestContext t)
    {
        var s = CampaignTests.Session(t, "base", 3, "Hu", presenter: new PlayPresenter());
        var bot = new Probe(s, (PlayPresenter)s.Host.Presenter);
        var W = s.World; var nav = W.Nav;
        var u = W.Units.First(x => x.Alive && x.Owner == s.Human && !x.Parked && !x.Inside && x.Speed > 0 && !x.Naval);
        // the biggest building whose centre is blocked ground
        var b = W.Buildings.Where(x => x.Alive && !x.Parked && nav.Block[nav.Idx(x.Pos.X, x.Pos.Z)] != 0).OrderByDescending(x => x.Radius).First();
        var (bx, by) = bot.Map(b);
        var (fx, fz) = nav.Center(nav.NearestFree(nav.Idx(b.Pos.X, b.Pos.Z)));
        double ring = Math.Sqrt((fx - b.Pos.X) * (fx - b.Pos.X) + (fz - b.Pos.Z) * (fz - b.Pos.Z));
        t.True(ring > 8, $"{b.Name}: the nearest free ground is {ring:0.#} m from its centre (more than go()'s 7 m)");
        // put at its wall on the far side from the nearest free cell, still on the ring's free ground
        double ax = b.Pos.X - (fx - b.Pos.X), az = b.Pos.Z - (fz - b.Pos.Z);
        var k = nav.NearestFree(nav.Idx(ax, az)); var (px, pz) = nav.Center(k);
        var (pmx, pmy) = s.Campaign.ToMap(px, pz);
        bot.Put(u, pmx, pmy);
        W.Order(new[] { u }, new Order("hold"));
        bool there = true;
        for (int i = 0; i < 70; i++) { there &= bot.Go(u, bx, by); s.Run(1); }
        t.True(there, $"a unit at {b.Name}'s wall ({Math.Sqrt((u.Pos.X - b.Pos.X) * (u.Pos.X - b.Pos.X) + (u.Pos.Z - b.Pos.Z) * (u.Pos.Z - b.Pos.Z)):0.#} m from its centre) is there");
        t.Equal(0, bot.Teleports, "and is not teleported onto it");
    }

    public static void GoCountsOnlyWhileSending(TestContext t)
    {
        var s = CampaignTests.Session(t, "base", 3, "Hu", presenter: new PlayPresenter());
        var bot = new Probe(s, (PlayPresenter)s.Host.Presenter);
        var W = s.World;
        var u = W.Units.First(x => x.Alive && x.Owner == s.Human && !x.Parked && !x.Inside && x.Speed > 0);
        var (mx, my) = bot.Map(u);
        // sent 30 m away for a second, then told to hold where it is: it stands, doing what it was told, for 70 s
        bot.Go(u, mx + 30, my);
        W.Order(new[] { u }, new Order("hold"));
        var at = u.Pos;
        s.Run(70);
        t.True(V3.Dist(at, u.Pos) < 8, $"the unit stood (moved {V3.Dist(at, u.Pos):0.#} m)");
        // sent on again: its count starts now - no teleport
        bot.Go(u, mx - 30, my + 30);
        t.Equal(0, bot.Teleports, "a unit go() left alone for 70 s is not teleported when go() sends it on");
        // a unit that cannot move while go() keeps sending it is still teleported after 60 s (the cheat still rescues)
        double speed = u.Speed, run = u.RunSpeed;
        u.Speed = 0; u.RunSpeed = 0;
        var (gx, gy) = (mx + 40, my - 40);
        for (int i = 0; i < 64 && bot.Teleports == 0; i++) { bot.Go(u, gx, gy); s.Run(1); }
        u.Speed = speed; u.RunSpeed = run;
        t.Equal(1, bot.Teleports, "a unit that makes no progress while go() sends it is teleported after 60 s");
    }
}
