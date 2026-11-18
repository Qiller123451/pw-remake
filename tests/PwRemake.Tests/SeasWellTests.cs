// MIRAGE's SEAS healing well (seas_outpouring, CSeasWell; src/Sim/Systems/MirageBuildings.cs) with the original's
// numbers: starting_energy 1000, max_energy 2000, + replenish_step 2 every replenish_time 1 s once finished; a unit sent
// to it gets what it lacks (or what is left), takes no damage for 0.1 s per hit point healed and loses its poison;
// own and allied characters, animals, vehicles only (player report: "MIRAGE's healing well SEAS building currently does
// nothing").
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class SeasWellTests
{
    const string WELL = "seas_outpouring";

    static (W_ W, Player P, Player E, Building Well) Setup(TestContext t, bool built = true)
    {
        var (w, p, e) = Sys2.World(t, Sys2.Mirage, "SEAS");
        var b = w.PlaceBuilding(WELL, p, 0, 0, 0, built);
        if (b == null) t.Skip("no model for " + WELL);
        return (w, p, e, b!);
    }

    /// <summary>the build action: the SEAS worker, epoch 4, 250 wood + 100 stone, Defence row</summary>
    public static void BuildAction(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Mirage, "SEAS");
        var a = w.Data.Actions(p).First(x => x.Path == "/Actions/SEAS/Build/BLDG/" + WELL);
        t.Equal("hidden", w.Data.Check(p, a), "not before epoch 4");
        w.RaiseEpoch(p, 4);
        a = w.Data.Actions(p).First(x => x.Path == "/Actions/SEAS/Build/BLDG/" + WELL);
        t.Equal(null, w.Data.Check(p, a), "epoch 4");
        t.Equal("seas_worker", a.Locs[0].At, "built by the worker");
        t.Equal(250.0, a.Cost.Wood, "wood"); t.Equal(100.0, a.Cost.Stone, "stone");
        t.Equal("CSeasWell", w.Data.Script(WELL), "script class");
    }

    /// <summary>energy 1000 from the site on; a site neither refills nor heals; finished: + 2 a second up to 2000</summary>
    public static void EnergyAndRefill(TestContext t)
    {
        var (w, p, _, b) = Setup(t, built: false);
        t.True(b.SeasWell != null, "a SEAS well");
        t.Equal(1000.0, b.SeasWell!.Energy, "starting_energy at the site");
        var u = w.SpawnUnit("seas_worker", p, 12, 0)!;
        u.Hp = u.MaxHp / 2;
        t.True(!w.CanUseWell(u, b), "a site heals nobody");
        Sys2.Run(w, 5);
        t.Equal(1000.0, b.SeasWell.Energy, "no refill while a site");
        w.FinishBuilding(b);
        t.Equal(2000.0, b.SeasWell.Max, "max_energy");
        Sys2.Run(w, 10.01);
        t.Near(1020, b.SeasWell.Energy, 0.01, "2 a second");
        b.SeasWell.Energy = 1999;
        Sys2.Run(w, 3);
        t.Equal(2000.0, b.SeasWell.Energy, "full: capped at 2000");
        t.True(b.SeasWell.T == null, "the refill stops when full");
    }

    /// <summary>sent there (order "fountain"), a damaged unit walks up and is fully healed; the well loses that much;
    /// no damage for 0.1 s per hit point healed; its poison is gone</summary>
    public static void HealsWhatIsMissing(TestContext t)
    {
        var (w, p, _, b) = Setup(t);
        var u = w.SpawnUnit("seas_worker", p, 40, 0)!;
        double lack = Math.Round(u.MaxHp * 0.6);
        u.Hp = u.MaxHp - lack;
        w.ApplyPoison(u, 1, 50, null);
        b.SeasWell!.Energy = 1000;
        t.True(w.CanUseWell(u, b), "own unit may drink");
        w.Order(u, new Order("fountain") { Target = b });
        double e0 = 0;
        for (int i = 0; i < 400 && u.Task.Type == "fountain"; i++) { e0 = b.SeasWell.Energy; lack = u.MaxHp - u.Hp; w.Step(); }
        t.Equal(u.MaxHp, u.Hp, "fully healed");
        t.True(Math.Sqrt(u.Pos.X * u.Pos.X + u.Pos.Z * u.Pos.Z) <= u.Radius + 7 + 0.5 || b.SurfDist(u.Pos.X, u.Pos.Z) <= 3 + u.Radius, "healed next to the well");
        t.Near(e0 - lack, b.SeasWell.Energy, 2.01, "the well lost what it gave");
        t.True(u.St.Poison == null, "poison gone");
        t.Equal("idle", u.Task.Type, "done");
        double hp = u.Hp;
        w.Damage(u, 50, null);
        t.Equal(hp, u.Hp, "no damage just after drinking");
        Sys2.Run(w, lack * 0.1 + 0.2);
        w.Damage(u, 50, null);
        t.Equal(hp - 50, u.Hp, $"damage again after {lack * 0.1} s");
    }

    /// <summary>a well with less than the unit lacks gives all it has and is empty, then refills</summary>
    public static void GivesWhatIsLeft(TestContext t)
    {
        var (w, p, _, b) = Setup(t);
        var u = w.SpawnUnit("seas_worker", p, 6, 0)!;
        u.Hp = u.MaxHp * 0.25;
        double lack = u.MaxHp - u.Hp;
        b.SeasWell!.Energy = Math.Floor(lack / 2);
        double give = b.SeasWell.Energy, hp = u.Hp;
        w.SeasWellArrived(b, u);
        t.Near(hp + give, u.Hp, 1e-9, "healed by what was left");
        t.Equal(0.0, b.SeasWell.Energy, "empty");
        t.True(b.SeasWell.T != null, "refilling");
        // a unit at full health takes nothing
        b.SeasWell.Energy = 500;
        u.Hp = u.MaxHp;
        w.SeasWellArrived(b, u);
        t.Equal(500.0, b.SeasWell.Energy, "nothing for a healthy unit");
    }

    /// <summary>who may use it: own and allied characters, animals, vehicles; not enemies, not ships, not blood-infected
    /// units; units that only stand next to it get nothing</summary>
    public static void WhoMayDrink(TestContext t)
    {
        var (w, p, e, b) = Setup(t);
        var mine = w.SpawnUnit("seas_worker", p, 6, 0)!;
        var foe = w.SpawnUnit("aje_worker", e, 6, 6);
        mine.Hp = mine.MaxHp / 2;
        Sys2.Run(w, 5);
        t.Equal(mine.MaxHp / 2, mine.Hp, "standing next to it heals nothing");
        if (foe != null) { foe.Hp = foe.MaxHp / 2; t.True(!w.CanUseWell(foe, b), "not an enemy"); w.SeasWellArrived(b, foe); t.Equal(foe.MaxHp / 2, foe.Hp, "an enemy arriving gets nothing"); }
        var ally = new Player(3, "SEAS", new PlayerOpts { Rules = w.Data, Team = 1 });
        w.Players.Add(ally);
        var au = w.SpawnUnit("seas_worker", ally, 8, 0)!;
        t.True(w.CanUseWell(au, b), "an ally may");
        mine.St.T["noHeal"] = w.Time + 60;
        t.True(!w.CanUseWell(mine, b), "not with blood infection");
        var shipName = w.Data.Index.Keys.FirstOrDefault(n => n.StartsWith("seas_") && w.Data.Info(n)?.Type == "SHIP");
        if (shipName != null)
        {
            var ship = w.SpawnUnit(shipName, p, 10, 10);
            if (ship != null) t.True(!w.CanUseWell(ship, b), "not a ship");
        }
    }
}
