// Rules of MIRAGE master 2.6.8 (a349cb41, 16 Nov 2026) that came after the data the remake was built from.
//
// * Buildings hidden on the minimap (Building.usl CRallyBuilding.OnInit 1770-1776, CNPCSeller.OnInit 5036-5048; the
//   lines uncommented by commit aaa63448 "Uncommented code from PR #307"; the engine fix of the minimap's building loop:
//   /home/claude/mirage_ref/hide_minimap_README.md): a CRallyBuilding whose model is "hc_" + its class name without the
//   tribe prefix (aje_rodeo -> hc_rodeo, aje_huge_farm -> hc_huge_farm, aje_weapons_builder -> hc_weapons_builder: the
//   map's TTFilters give the holy city's buildings these models) and a CNPCSeller with the model hc_taverne (the
//   tavern aje_cook_house of the holy city) set the hide flag 0x800: no mark on the minimap of any player but the
//   owner. (They also SetVisInFOW(true); the remake shows every explored building in the fog anyway.)
// * Swimming nests of aggressive animals (Nest.usl CSwimmingNest.OnInit 1516-1541, commits 95c76489 / d9691e18): with
//   spawn_amount -1 (no limit) the nest gets SPAWN_WAVE_AMOUNT (3) x spawn_max eggs - three full broods, then it is
//   empty. (MIRAGE's script writes the product into spawn_max - the nest's size - and leaves spawn_amount at -1, so in
//   MIRAGE the nest holds three times the animals for ever; the commit message and the ReadMe say what was meant, and
//   that is what the remake does: MapSource NEST.)
// * A nest made again after it was destroyed (Nest.usl Delete -> Inherit, MIRAGE's game option NestRespawn) keeps the
//   eggs left (spawn_amount) instead of starting with no limit: the remake has no nest respawn option yet (docs/
//   PORTING.md) - nothing to change.
using System;

namespace PwRemake.Sim.World;

public sealed partial class Building
{
    // (the hide flag of the model it was asked for: the model changes with the level)
    string? _hideMiniGfx;
    bool _hideMini;
    /// <summary>the minimap hide flag (SetHideMiniMap) as the class and model give it; the minimap asks
    /// World.HiddenOnMinimap</summary>
    internal bool HideMiniMapFlag(World w)
    {
        if (!ReferenceEquals(_hideMiniGfx, Gfx)) { _hideMiniGfx = Gfx; _hideMini = World.HideMiniMapRule(w, Name, Gfx); }
        return _hideMini;
    }
}

public sealed partial class World
{
    /// <summary>SPAWN_WAVE_AMOUNT of CSwimmingNest: the full broods of a nest of aggressive water animals without a limit</summary>
    public const int SWIMMING_NEST_BROODS = 3;

    /// <summary>CRallyBuilding / CNPCSeller OnInit: does a building of this class with this model hide itself on the
    /// minimap?</summary>
    public static bool HideMiniMapRule(World w, string name, string? gfx)
    {
        if (string.IsNullOrEmpty(gfx)) return false;
        var D = w.Data;
        if (D.IsA(name, "CRallyBuilding"))
            return name.Length > 4 && string.Equals(gfx, "hc_" + name.Substring(4), StringComparison.OrdinalIgnoreCase);
        if (D.IsA(name, "CNPCSeller")) return string.Equals(gfx, "hc_taverne", StringComparison.OrdinalIgnoreCase);
        return false;
    }

    /// <summary>is the building's mark left out of player p's minimap? (the hide flag 0x800 of the MIRAGE exe fix: on
    /// every minimap but the owner's)</summary>
    public bool HiddenOnMinimap(Building b, Player? p) => b.Owner != p && b.HideMiniMapFlag(this);

    /// <summary>CSwimmingNest.OnInit: the eggs of a swimming nest (spawn_amount; -1 = no limit) - a nest of aggressive
    /// animals (the species' aggressive = 1) without a limit gets 3 x spawn_max (MIRAGE only)</summary>
    public static double SwimmingNestEggs(Rules.Rules D, string species, double max, double amount)
    {
        if (D.Extra.Mod != "MIRAGE" || amount >= 0) return amount;
        var def = D.Def(species, null);
        return def != null && def.Aggressive == 1 ? max * SWIMMING_NEST_BROODS : amount;
    }
}
