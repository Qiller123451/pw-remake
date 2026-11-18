// Every building plays its destruction (docs/EFFECTS.md "destroyed", docs/VIEW.md "Destruction"): each building class
// of both configurations is put down finished and destroyed, and what it leaves is checked against the models: its
// ruin "<gfx>_dest" (World.RuinOf, MIRAGE's ninigi_large_tower -> ninigi_smoke_tower_dest) and the ruins of its
// attached parts "<part>_dest" (World.RuinPartsOf: the original's CTower.CreateBuildingCorpse makes the turret's
// corpse apart - the SEAS machine-gun nest's and small cannon's collapse is all in their turret's ruin; their own ruins
// have no clip). A class resolves its destruction when one of the models it leaves has a clip (the ruins' one clip is
// "destroy" - every ruin of the GSF archives at hand, 2026-11-13) whenever one of the models it could leave has one.
// The clip counts come from the data's manifest (nanims) or the model file when there is one.
// Also the reach of the shooting buildings (World.TowerRange / TowerRangeOf: the range circles, RangeRings): the same
// value TowerUpdate fires at, for a building and for its placement ghost.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Tests;

[TestClass]
public static class BuildingRuinTests
{
    public static void BaseBuildingsPlayTheirDestruction(TestContext t) => Check(t, Sys2.Base, new[] { "seas_hq_machinegun_nest", "seas_hq_defense_turret" });
    public static void MirageBuildingsPlayTheirDestruction(TestContext t) => Check(t, Sys2.Mirage, new[] { "seas_hq_machinegun_nest", "seas_hq_defense_turret", "seas_mg_nest", "seas_small_cannon" });

    static readonly string[] TRIBES = { "Hu", "Aje", "Ninigi", "SEAS" };

    static int Clips(W_ w, string? model) => model == null ? 0 : Math.Max(w.Models.Get(model)?.NAnims ?? 0, w.Models.Clips(model).Names.Count);

    static void Check(TestContext t, string data, string[] mustUseTurret)
    {
        var (w, _, _) = Sys2.World(t, data, size: 512);
        var players = TRIBES.ToDictionary(tr => tr, tr => { var p = new Player(10 + Array.IndexOf(TRIBES, tr), tr, new PlayerOpts { Rules = w.Data, Team = 3 }); w.Players.Add(p); return p; });
        int n = 0, withClip = 0;
        var fixedByParts = new List<string>();
        var bad = new List<string>();
        var turretOf = new Dictionary<string, string>();
        double x = -200;
        foreach (var (name, info) in w.Data.Index.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (info.Type != "BLDG") continue;
            var owner = players.GetValueOrDefault(info.Tribe) ?? players["Hu"];
            var def = w.Data.Def(name, owner);
            if (def == null || def.WallKind == "wall") continue;               // (wall pieces leave no ruin: CWall)
            Building? b;
            try { b = w.PlaceBuilding(name, owner, x, 0); }
            catch (Exception) { b = null; }
            if (b == null) continue;
            x += 1; if (x > 200) x = -200;
            n++;
            // what it could leave: its own ruin and those of every part it may show (the composites, not the cranes)
            // (MIRAGE's Amazon temple: no ruin model - its corpse is the temple itself at destruction level 2, CAjeAmazonTempleCorpse)
            // (MIRAGE's fake corpses - custom_buildups.txt FakeCorpse: the building itself with its parts, no part ruins)
            bool fake = CustomBuildUps.Of(w.Data, name) is { FakeCorpse: true } && !CustomBuildUps.OwnCorpse(w.Data, name);
            var own = fake ? b.Gfx : w.RuinOf(b) ?? (w.Data.IsA(name, "CAjeAmazonTemple") && b.Gfx == "amazon_temple" ? "amazon_temple" : null);
            var gfx = (b.Stats?.Gfx ?? "").ToLowerInvariant();
            var partRuins = w.Data.Composites(name).Where(s => s.Cond != "construction")
                .SelectMany(s => s.Variants.Count > 0 ? s.Variants : new List<string> { s.Gfx })
                .Select(g => (g + "_dest").ToLowerInvariant()).Where(w.Models.Has).Where(_ => !fake).Distinct().ToList();
            w.KillBuilding(b, null);
            var left = new List<string>();
            if (b.Ruin != null) left.Add(b.Ruin);
            if (b.RuinParts != null) left.AddRange(b.RuinParts.Select(p => p.Gfx));
            t.Equal(own, b.Ruin, $"{name}: its ruin");
            foreach (var p in b.Parts.Where(p => !fake && w.Models.Has(p.Gfx + "_dest")))
                t.True(b.RuinParts?.Any(r => r.From == p.Gfx && r.Gfx == (p.Gfx + "_dest").ToLowerInvariant() && r.Link == p.Link) == true, $"{name}: the part {p} leaves its ruin");
            var could = partRuins.Append(own).Where(m => m != null).ToList();
            bool canPlay = could.Any(m => Clips(w, m) > 0), plays = left.Any(m => Clips(w, m) > 0);
            if (canPlay) withClip++;
            if (canPlay && !plays) bad.Add($"{name} ({gfx}): leaves {string.Join(", ", left)}; could {string.Join(", ", could)}");
            if (plays && (own == null || Clips(w, own) == 0)) fixedByParts.Add($"{name} ({string.Join(" + ", left.Select(m => $"{m}:{Clips(w, m)}"))})");
            if (b.RuinParts != null) turretOf[name] = string.Join(",", b.RuinParts.Select(p => p.Gfx));
            // the part ruins hang where the live parts did and go with the building's ruin
            foreach (var r in b.RuinParts ?? new()) t.True(r.Parent == null || b.RuinParts!.Contains(r.Parent), $"{name}: {r} hangs on a ruin part");
        }
        t.Log($"{n} building classes destroyed; {withClip} have a ruin with a clip");
        t.Log("play their destruction only through a part's ruin (or a ruin of another name): " + (fixedByParts.Count == 0 ? "none" : "\n      " + string.Join("\n      ", fixedByParts)));
        t.Log("part ruins: " + string.Join("; ", turretOf.Select(kv => kv.Key + " -> " + kv.Value)));
        t.True(n > 50, "building classes checked");
        foreach (var c in mustUseTurret)
            t.True(turretOf.TryGetValue(c, out var r) && r.EndsWith("_top_dest"), $"{c}: its turret's ruin plays the collapse ({(turretOf.TryGetValue(c, out var rr) ? rr : "none")})");
        t.True(bad.Count == 0, $"{bad.Count} building classes do not play their destruction:\n      " + string.Join("\n      ", bad));
    }

    /// <summary>a destroyed building's ruin parts keep the live part's link and turn; the live parts are not drawn on the
    /// ruin (EntityView.SimParts) - checked here on the simulation's side: the nest keeps no live turret among them</summary>
    public static void TurretRuinKeepsItsTurn(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, "SEAS");
        var b = w.PlaceBuilding("seas_hq_machinegun_nest", p, 0, 0)!;
        t.True(b.Turret != null, "the nest has its gun turret");
        b.Turret!.Yaw = 1.25;
        w.KillBuilding(b, null);
        t.Equal("seas_hq_machinegun_nest_dest", b.Ruin, "the nest's ruin");
        var r = b.RuinParts?.SingleOrDefault();
        t.True(r != null && r.Gfx == "seas_hq_machinegun_nest_top_dest" && r.From == "seas_hq_machinegun_nest_top" && r.Link == "we", $"the turret's ruin at \"we\" ({r})");
        t.Near(1.25, r!.Yaw, 1e-9, "turned as the turret was");
    }

    // ------------------------------------------------------------------ the range circles
    /// <summary>every shooting building's range: what TowerUpdate fires at (weapon range + half the radius), the same for
    /// its placement ghost; the towers of every tribe, the SEAS nests, cannons and the bunker shoot; an upgrade of the
    /// range shows</summary>
    public static void TowerRangeIsTheFiringRange(TestContext t)
    {
        foreach (var data in new[] { Sys2.Base, Sys2.Mirage })
        {
            var (w, _, _) = Sys2.World(t, data, size: 512);
            var players = TRIBES.ToDictionary(tr => tr, tr => { var p = new Player(10 + Array.IndexOf(TRIBES, tr), tr, new PlayerOpts { Rules = w.Data, Team = 3 }); w.Players.Add(p); return p; });
            var shooters = new List<string>();
            double x = -200;
            foreach (var (name, info) in w.Data.Index.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                if (info.Type != "BLDG" || !players.TryGetValue(info.Tribe, out var owner)) continue;
                Building? b;
                try { b = w.PlaceBuilding(name, owner, x, 0); } catch (Exception) { b = null; }
                if (b == null) continue;
                x += 1; if (x > 200) x = -200;
                var r = w.TowerRange(b);
                var g = w.TowerRangeOf(name, owner, b.Rot);
                t.Equal(r.HasValue, g.HasValue, $"{name}: the ghost shoots as the building");
                if (r is not var (R, min)) continue;
                var cs = b.Cs(b.Weapons.Long)!;
                t.Near(cs.Range + b.Radius * 0.5, R, 1e-9, $"{name}: the range TowerUpdate fires at");
                t.Near(b.Weapons.Long!.MinRange, min, 1e-9, $"{name}: its minimum range");
                t.Near(R, g!.Value.R, 0.6, $"{name}: the ghost's range ({g.Value.R:0.0}) as the building's ({R:0.0})");
                shooters.Add($"{name} {R:0.#}{(min > 0 ? $" (min {min:0.#})" : "")}");
            }
            t.Log($"{System.IO.Path.GetFileName(data)}: {shooters.Count} shooting buildings: {string.Join(", ", shooters)}");
            foreach (var must in new[] { "hu_small_tower", "aje_small_tower", "ninigi_small_tower", "seas_turret_tower", "seas_hq_machinegun_nest", "seas_hq_defense_turret", "hu_bunker" })
                if (w.Data.Exists(must)) t.True(shooters.Any(s => s.StartsWith(must + " ")), $"{must} shows a range");
        }
    }
}
