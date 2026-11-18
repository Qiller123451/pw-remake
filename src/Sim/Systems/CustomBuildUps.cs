// MIRAGE's custom construction of buildings (settings/custom_buildups.txt, read by MirageSrvMgr.CustomBuildUp into
// CBuilding's m_avCranes / m_bCustomConstruction / m_fStartHeight / m_fHeightOffset / m_bFakeCorpse):
//
// * CustomConstruction = 1 (CBuildUpMgr, CFakeBuildUp - Mirage.usl 17259-17440; Building.usl BuildUp, SetProcess,
//   UpdateGfx, OnPostLoad): a building whose model has no construction stages rises out of the ground. While it is a
//   site the building itself shows paving tiles (GetFakeGfx: seas_temple_paving_tiles_01, the SEAS' own
//   seas_hq_paving_tiles_02 - FightingObj.usl 14010) and a second object, the finished model (GetTtGfx), stands
//   StartHeight m deep under it and comes up with the construction: its height is the building's + HeightOffset -
//   (StartHeight - progress * StartHeight), progress the whole per cent of the construction (BuildupProcess / 100). When
//   the building is finished (construction level 4) the risen model goes and the building shows its own model.
// * Cranes: construction cranes at these places (metres in the building's frame, [x y z] of the game: AddCranesCustom
//   links them to the building at "NOPE" with the offset) besides the ones at the model's Cr_1 .. Cr_4; each is the
//   tribe's crane_01 or, at random, crane_02 (the SEAS: always crane_01), playing "build" while it is built.
// * FakeCorpse = 1 (CBuilding.CreateBuildingCorpse -> CFakeCorpse "mirage_bldg_corpse", Mirage.usl 15677): a finished
//   building leaves no "<gfx>_dest" ruin but its own model at destruction level 2 with a fire (fire_corpses: the model
//   Fire_Molotov) for 3 s; then it sinks (MoveAction down at 1 m/s) and is gone. Classes whose script overrides
//   CreateBuildingCorpse do not get it (the towers, the launch pads, the Amazon temple - MirageClasses.cs has its own).
//
// The table is the file of MIRAGE master (a349cb41, 16 Nov 2026; the data export has no copy of it), used with MIRAGE
// only. Not ported: the "scorpion" hack (the risen SEAS Scorpion held at frame 0 of "stand_up": the remake's model
// stands in its rest pose) and CFakeCorpse's SetNoParticles (the corpse's own emitters keep playing).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

/// <summary>one class of custom_buildups.txt</summary>
public sealed record CustomBuildUp(double StartHeight, double HeightOffset, bool Custom, bool FakeCorpse, double[][] Cranes);

public static class CustomBuildUps
{
    static CustomBuildUp B(double start = 0, double offset = 0, bool custom = false, bool corpse = false, params double[][] cranes) => new(start, offset, custom, corpse, cranes);
    static double[] C(double x, double y, double z) => new[] { x, y, z };

    /// <summary>MIRAGE Scripts/Server/settings/custom_buildups.txt (master a349cb41)</summary>
    public static readonly IReadOnlyDictionary<string, CustomBuildUp> TABLE = new Dictionary<string, CustomBuildUp>(StringComparer.Ordinal)
    {
        ["aje_amazon_temple"] = B(corpse: true, cranes: new[] { C(14, 14, 0), C(-14, -14, 0) }),
        ["aje_launchpad_zeppelin"] = B(cranes: new[] { C(5, 4, 0), C(-5, -5, 0) }),
        ["aje_special_box"] = B(4.5, 0, true, true, C(-2.5, 5, 0)),
        ["aje_sensor_tower"] = B(8.0, 0, true, true, C(8, 2, 0)),
        ["crash_launchpad"] = B(cranes: new[] { C(5, 4, 0), C(-5, -5, 0) }),
        ["hu_goods"] = B(5.0, 0, true, true, C(5, 0, 0)),
        ["hu_launchpad_zeppelin"] = B(cranes: new[] { C(5, 4, 0), C(-5, -5, 0) }),
        ["hu_mine"] = B(9.0, 0, false, true, C(3.5, 6, 0)),
        ["hu_special_box"] = B(4.5, 0, true, true, C(-2.5, 5, 0)),
        ["hu_mud_pit"] = B(corpse: true),
        ["hu_punching_bag"] = B(corpse: true, cranes: new[] { C(5, 4, 0), C(-5, -5, 0) }),
        ["ninigi_launchpad_zeppelin"] = B(cranes: new[] { C(5, 4, 0), C(-5, -5, 0) }),
        ["pirate_fortress_module_01"] = B(corpse: true),
        ["ninigi_special_box"] = B(4.5, 0, true, true, C(-2.5, 5, 0)),
        ["seas_disruptor"] = B(cranes: new[] { C(4, 4, 0) }),
        ["seas_final_boss"] = B(19.0, 0, true, false, C(5, 5, 0), C(-5, -5, 0)),
        ["seas_fortress"] = B(12.0, 3.99, true, false, C(10, 10, 0)),
        ["seas_great_cannon"] = B(2.1, 0, true, false, C(14, 14, 0), C(-14, -14, 0)),
        ["seas_laboratory"] = B(15.0, 0, false, true, C(-3, 3, 0)),
        ["seas_launchpad_zeppelin"] = B(cranes: new[] { C(5, 4, 0), C(-5, -5, 0) }),
        ["seas_mg_nest"] = B(cranes: new[] { C(3, 3, 0) }),
        ["seas_mine"] = B(9.0, 0, true, true, C(3.5, 6, 0)),
        ["seas_outpouring"] = B(corpse: true, cranes: new[] { C(4, 4, 0) }),
        ["seas_dummy_portal_terminus"] = B(25.0, 0, true, true, C(4, 12.73, 0), C(-4, -12.73, 0)),
        ["seas_pub"] = B(9.0, 0, true, true, C(-2.5, 5, 0)),
        ["seas_small_cannon"] = B(5.0, 0, true, false, C(4, 4, 0)),
        ["seas_speaker"] = B(15.0, 0, true, true, C(3, 3, 0)),
        ["seas_special_box"] = B(4.5, 0, true, true, C(-2.5, 5, 0)),
        ["seas_staircase"] = B(10.0, 0, true, true, C(8, -2, 0)),
        ["seas_tesla_machine"] = B(3.0, 0, false, true, C(-2.5, 5, 0)),
        ["seas_dummy_warpgate"] = B(25.0, 0, true, true, C(4, 12.73, 0), C(-4, -12.73, 0)),
        ["seas_jail_part_02"] = B(18.0, 0, true, false, C(14, 14, 0), C(-14, -14, 0)),
        ["seas_wall_gate"] = B(corpse: true, cranes: new[] { C(5, 4, -1) }),
        ["ninigi_launchpad_pirate_boss"] = B(corpse: true, cranes: new[] { C(10, 0, 2), C(-10, -6, 2) }),
    };

    /// <summary>the class's entry with MIRAGE's data; null: none (or another configuration)</summary>
    public static CustomBuildUp? Of(Rules.Rules data, string cls) =>
        data.Extra.Mod == "MIRAGE" && TABLE.TryGetValue(cls.ToLowerInvariant(), out var c) ? c : null;

    /// <summary>GetFakeGfx: the paving tiles a rising building's site shows</summary>
    public static string SiteGfx(string tribe) => tribe == "SEAS" ? "seas_hq_paving_tiles_02" : "seas_temple_paving_tiles_01";

    /// <summary>CBuildUpMgr.GetPosByLevel: how far above the building's origin the risen model stands (negative: under
    /// the ground) at a construction progress 0..1 (the whole per cent counts: BuildupProcess)</summary>
    public static double RiseY(CustomBuildUp c, double progress)
    {
        double p = Math.Floor(Math.Clamp(progress, 0, 1) * 100 + 1e-9) / 100;
        return c.HeightOffset - (c.StartHeight - p * c.StartHeight);
    }

    /// <summary>the script classes whose CreateBuildingCorpse replaces CBuilding's (no fake corpse for them)</summary>
    static readonly string[] OWN_CORPSE =
    {
        "CTower", "CAjeAmazonTemple", "CHuAvatarLaunchpad", "CNinigiPirateBossLaunchpad", "CSeasScorpionLaunchpad", "CZeppelinLaunchPad",
        "CSwimmingHarbour", "CCrashLaunchPad", "CFault", "CSandStorm", "CSandTrap", "CVolcano", "CQuicksand", "CCrashQuicksand",
    };
    public static bool OwnCorpse(Rules.Rules data, string cls) => OWN_CORPSE.Any(c => data.IsA(cls, c));
}

public sealed partial class Building
{
    /// <summary>MIRAGE's custom construction of this class (CustomBuildUps); null: none</summary>
    public CustomBuildUp? CustomBuild => CustomBuildUps.Of(World.Data, Name);

    /// <summary>the site shows paving tiles and the finished model rises out of the ground (CustomConstruction); the
    /// view draws the risen model at RiseY</summary>
    public bool Rising => !Built && Alive && CustomBuild is { Custom: true };
    /// <summary>the paving tiles of a rising site (GetFakeGfx by the building's tribe)</summary>
    public string? RisingSiteGfx => Rising ? CustomBuildUps.SiteGfx(Def is { Tribe.Length: > 0 } d ? d.Tribe : Owner?.Tribe ?? "") : null;
    /// <summary>the risen model's height over the building's origin now</summary>
    public double RiseY => CustomBuild is { } c ? CustomBuildUps.RiseY(c, Progress) : 0;

    /// <summary>AddCranesCustom: the cranes at the table's places (Building.AddCranes)</summary>
    void AddCustomCranes()
    {
        var c = CustomBuild;
        if (c == null || c.Cranes.Length == 0) return;
        var tribe = Owner != null ? Owner.Tribe.ToLowerInvariant() : "seas";
        for (int i = 0; i < c.Cranes.Length; i++)
        {
            // (Random.GetInt() % 2: crane_02 half the time, never for the SEAS - a choice of the eye only, taken from the
            // building's id so that the world's random numbers stay as they were)
            bool two = tribe != "seas" && ((Id * 7 + i * 3) & 1) == 1;
            var gfx = $"{tribe}_crane_0{(two ? 2 : 1)}";
            if (!World.Models.Has(gfx)) gfx = $"{tribe}_crane_01";
            if (!World.Models.Has(gfx)) continue;
            var clips = World.Models.Clips(gfx);
            var o = c.Cranes[i];
            var spec = new CompositeSpec { Kind = "crane", Gfx = gfx, Link = "NOPE", Offset = new System.Text.Json.Nodes.JsonArray(o[0], o[1], o[2]) };
            spec.OffsetModel = new V3(-o[0], o[2], -o[1]);
            Cranes.Add(new AttachedPart { Spec = spec, Gfx = gfx, Link = "NOPE", Anim = clips.Count > 0 ? new AnimCtl(clips, this) : null, Fourcc = World.Models.Fourcc(gfx) });
        }
    }
}

public sealed partial class World
{
    /// <summary>CBuilding.CreateBuildingCorpse with m_bFakeCorpse (CFakeCorpse): the building's own model at destruction
    /// level 2 with a fire for 3 s, then it sinks; false: not such a class (Construction.BuildingDestroyed)</summary>
    bool FakeCorpse(Building e)
    {
        if (e.CustomBuild is not { FakeCorpse: true } || CustomBuildUps.OwnCorpse(Data, e.Name) || string.IsNullOrEmpty(e.Gfx)) return false;
        e.Ruin = e.Gfx;
        e.VisDmg = 2; e.DmgStage = 2;
        e.RuinSinkAt = 3;
        // (its addons and linked objects go over to the corpse - AddAddons, TransferLinkedObjs: the live parts stay on it,
        // no ruins of their own; AddFX("fire_corpses") at the corpse: its model Fire_Molotov - MIRAGE mirage_misc.txt -
        // at the building's origin)
        var keep = e.Parts.ToList();
        if (Models.Has("fire_molotov")) keep.Add(new AttachedPart { Gfx = "fire_molotov", Link = "NOPE", Fourcc = Models.Fourcc("fire_molotov") });
        e.RuinParts = keep.Count > 0 ? keep : null;
        return true;
    }
}
