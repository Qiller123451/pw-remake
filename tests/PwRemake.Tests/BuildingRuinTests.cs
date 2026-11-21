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

    /// <summary>
    /// The other way round (2026-11-20, "check every building of every tribe"): every destruction clip of the model
    /// data - a "&lt;model&gt;_dest" with a clip - that nothing in the remake leaves. Used are the ruins of every building
    /// class at every level (its gfx per level, World.RuinOf), of the parts it may show (World.RuinPartsOf, every variant),
    /// a ship's and a vehicle's wreck (Combat.ShipWreck, VehicleWreck - the vehicles' were missing: 22 classes of
    /// MIRAGE), MIRAGE's pirate ship parts and the ruins of another name. What is left is listed
    /// and must be scenery (no class of the rules: a map prop's model, docs/VIEW.md "Destruction"); a ruin of a class
    /// that is never used fails the test.
    /// </summary>
    public static void BaseUnusedDestructionClips(TestContext t) => Unused(t, Sys2.Base);
    public static void MirageUnusedDestructionClips(TestContext t) => Unused(t, Sys2.Mirage);

    /// <summary>a destroyed vehicle leaves its wreck "&lt;gfx&gt;_dest" (Combat.VehicleWreck, Vehicle.usl CreateCorpse), the
    /// live model hidden, gone after 10 s; one that has no such model keeps its own death</summary>
    public static void VehicleLeavesItsWreck(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, size: 256);
        var tank = w.SpawnUnit("hu_steam_tank", p, 10, 10, 3, 0);
        t.True(tank != null, "a steam tank");
        w.Kill(tank!, null);
        t.Equal("hu_steam_tank_dest", tank!.Wreck?.Gfx, "its wreck");
        Sys2.Run(w, 9);
        t.True(!tank.Removed, "the wreck stands for 10 s");
        Sys2.Run(w, 2);
        t.True(tank.Removed, "then it is gone");
        var cart = w.SpawnUnit("hu_cart", p, 20, 10, null, 0);
        if (cart != null && w.Models.Has((cart.Gfx ?? "") + "_dest")) { w.Kill(cart, null); t.True(cart.Wreck != null, "the cart's wreck"); }
    }

    /// <summary>a building whose model an upgrade replaced (the Norse large tower's ballista: hu_large_tower_upgrade)
    /// leaves the ruin of that model (World.RuinOf on its current Stats.Gfx; CBuildingCorpse.Init(GetGfxName()))</summary>
    public static void UpgradedBuildingLeavesItsRuin(TestContext t)
    {
        var (w, p, _) = Sys2.World(t, Sys2.Base, size: 256);
        var b = w.PlaceBuilding("hu_large_tower", p, 0, 0);
        t.True(b != null, "a large tower");
        t.True(Sys1.Invent(w.Data, p, "hu_ballista_upgrade"), "the ballista upgrade");
        b!.RefreshRules();
        t.Equal("hu_large_tower_upgrade", b.Gfx?.ToLowerInvariant(), "the upgraded model");
        w.KillBuilding(b, null);
        t.Equal("hu_large_tower_upgrade_dest", b.Ruin, "the upgraded model's ruin");
    }

    /// <summary>the wagons drawn by animals (the chariot's, the Dragon Clan cart's and drum wagon's): their models have a
    /// "_dest" with a clip, but the original's scripts make no corpse of them - an animal leaves its CharacterCorpse
    /// (Animal.usl:900), only CVehicle / CShip / CBuilding make "_dest" corpses - so the remake leaves none either</summary>
    static readonly HashSet<string> WAGONS = new(StringComparer.OrdinalIgnoreCase) { "hu_chariot_trailer_dest", "ninigi_cart_dest", "ninigi_drumwagon_dest" };

    static void Unused(TestContext t, string data)
    {
        var (w, _, _) = Sys2.World(t, data, size: 256);
        var D = w.Data;
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var classGfx = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);     // a model -> a class that shows it
        foreach (var (name, info) in D.Index)
        {
            for (int l = 1; l <= 5; l++)
            {
                var g = D.Stats(name, l, null)?.Gfx;
                if (string.IsNullOrEmpty(g)) continue;
                classGfx.TryAdd(g, name);
                // a building's ruin, a ship's or a vehicle's wreck (Combat.ShipWreck / VehicleWreck)
                if (info.Type is "BLDG" or "SHIP" or "VHCL") { used.Add(g + "_dest"); used.Add(g + "_dest_water"); }
            }
            if (D.ClassGfx(name) is { } cg) classGfx.TryAdd(cg, name);
            foreach (var c in D.Composites(name))
                foreach (var g in c.Variants.Count > 0 ? c.Variants : new List<string> { c.Gfx })
                {
                    if (string.IsNullOrEmpty(g)) continue;
                    classGfx.TryAdd(g, name);
                    if (info.Type is "BLDG" or "SHIP") used.Add(g + "_dest");
                }
        }
        // the models an upgrade gives a class (a tech tree modificator "replace /Objects/<tribe>/<type>/<class>/gfx": the
        // Norse large tower's ballista, the bunker's upgrade, the Dustriders' farm types and big tent ...): Building.Stats
        // follows the owner's tree, World.RuinOf leaves "<that gfx>_dest"
        var tt = System.IO.File.ReadAllText(System.IO.Path.Combine(data, "techtree.json"));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(tt,
            "\"path\":\"/Objects/[^/\"]+/(BLDG|SHIP|VHCL|ANML|CHTR)/([^/\"]+)(?:/[^\"]*)?/gfx\",\"value\":\"([^\"]+)\""))
        {
            var g = m.Groups[3].Value;
            classGfx.TryAdd(g, m.Groups[2].Value);
            if (m.Groups[1].Value is "BLDG" or "SHIP" or "VHCL") { used.Add(g + "_dest"); used.Add(g + "_dest_water"); }
        }
        // ruins of another name (World.RUIN_GFX: MIRAGE's ninigi_large_tower) and the pirate ship's parts (PirateShip.cs)
        used.Add("ninigi_smoke_tower_dest");
        foreach (var n in new[] { "pirate_boss", "pirate_boss_tail", "pirate_boss_row", "pirate_boss_sail", "pirate_boss_cannons", "pirate_boss_cannons_reduced",
            "ninigi_pirate_ship", "ninigi_pirate_ship_tail", "ninigi_pirate_ship_row", "ninigi_pirate_ship_sail", "ninigi_pirate_ship_cannons" })
            used.Add(n + "_dest");
        var withClip = w.Models.Names.Where(m => m.EndsWith("_dest", StringComparison.OrdinalIgnoreCase) && Clips(w, m) > 0).OrderBy(m => m, StringComparer.Ordinal).ToList();
        var unused = withClip.Where(m => !used.Contains(m)).ToList();
        // a model of a class (what the ruin's name points at) whose ruin with a clip is never left = a missing destruction
        var ofClass = unused.Where(m => classGfx.ContainsKey(m[..^5]) && !WAGONS.Contains(m)).Select(m => $"{m} ({classGfx[m[..^5]]})").ToList();
        t.Log("wagons of animals (no corpse in the original's scripts): " + string.Join(", ", unused.Where(WAGONS.Contains)));
        var scenery = unused.Where(m => !classGfx.ContainsKey(m[..^5])).ToList();
        t.Log($"{withClip.Count} models \"<model>_dest\" with a clip; {withClip.Count - unused.Count} left by a class");
        t.Log("not left by any class (scenery - map props of their own): " + (scenery.Count == 0 ? "none" : string.Join(", ", scenery)));
        t.True(withClip.Count > 50, "the data's ruins");
        t.True(ofClass.Count == 0, $"{ofClass.Count} destruction clips of a class's model never played:\n      " + string.Join("\n      ", ofClass));
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
