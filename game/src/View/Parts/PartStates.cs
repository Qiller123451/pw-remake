// Applying part states to the parts of a model (template or instance): the hide mask of animals, vehicles and
// characters, the construction level / damage stage / epoch / time of day of buildings, ships and props, the arms and
// variants of wall pieces. Ported from remake/src/engine/parts.js (applyMask, applyState, applyNight)
// (pw-exporter 98c7703).
using System.Collections.Generic;
using Godot;
using PwRemake.View.Models;

namespace PwRemake.View.Parts;

/// <summary>The look of a building / static model (parts.js applyState arguments and root.userData).</summary>
public struct BuildingState
{
    /// <summary>Construction level 0..3, 4 = finished (Building constructLevel: progress * 4).</summary>
    public int Level = 4;
    /// <summary>Damage stage 0..2 (FightingObj.UpdateDestructionFlags: &lt;=50% 1, &lt;=25% 2).</summary>
    public int Damage = 0;
    /// <summary>Epoch 1..5 of the owner.</summary>
    public int Epoch = 1;
    /// <summary>Night: the lamps' night parts shown, the day-only parts hidden.</summary>
    public bool Night = false;
    /// <summary>Wall pieces: the arms shown (bit per direction 0..7), null = all.</summary>
    public int? ArmMask = null;
    /// <summary>Wall pieces: the geometry variant of [hub, arm 0..7] (wallmap.js), null = the first of each.</summary>
    public int[]? VariantPick = null;
    /// <summary>Wall pieces by name: the components shown (wallmap.js BuildName), null = not a named wall piece.</summary>
    public HashSet<string>? WallParts = null;
    /// <summary>Gates: the end pieces (attr bits 0x80 / 0x800 = render flags 7, 11) only on a diagonal gate; null = not a gate.</summary>
    public bool? GateEnds = null;

    public BuildingState() { }
    public BuildingState(int level, int damage, int epoch, bool night = false) { Level = level; Damage = damage; Epoch = epoch; Night = night; }

    public bool SameLook(in BuildingState o) => Level == o.Level && Damage == o.Damage && Epoch == o.Epoch && Night == o.Night
        && ArmMask == o.ArmMask && GateEnds == o.GateEnds && ReferenceEquals(VariantPick, o.VariantPick) && ReferenceEquals(WallParts, o.WallParts);
}

public static class PartStates
{
    /// <summary>Apply a hide mask to a model's dynamic parts (meshes and sprites carrying attribute bits).</summary>
    public static void ApplyMask(IReadOnlyList<(Node3D node, PartRef part)> parts, uint hide)
    {
        foreach (var (n, p) in parts)
            if (p.Kind != PartKind.Particles && p.HasAttr) n.Visible = PartBits.MaskVisible(p.Attr, hide);
    }

    /// <summary>A script visibility flag (SetRndInvMaskSingleFlagInv) on the model's own parts (entities.js setVisFlag).</summary>
    public static void SetVisFlag(IReadOnlyList<(Node3D node, PartRef part)> parts, int bit, bool on)
    {
        foreach (var (n, p) in parts)
            if (p.Kind != PartKind.Particles && p.HasAttr && ((p.Attr >> bit) & 1) != 0) n.Visible = on;
    }

    /// <summary>
    /// Apply a building state to a model's static part groups (signature parts), its day / night parts and, on wall
    /// pieces, its arms and variants (parts.js applyState).
    /// </summary>
    public static void ApplyState(IReadOnlyList<(Node3D node, PartRef part)> parts, in BuildingState s)
    {
        foreach (var (o, p) in parts)
        {
            bool todOk = p.Tod == TimeOfDay.Always || (p.Tod == TimeOfDay.Night) == s.Night;
            int? arm = p.Arm;
            bool armOk = arm == null || arm < 0 || s.ArmMask == null || ((s.ArmMask.Value >> arm.Value) & 1) == 1;
            // wall pieces by part name (wallmap.js buildName, ModelLibrary.TagWallArms): only the parts the WallMap named
            if (p.Comp != null) armOk = s.WallParts != null && s.WallParts.Contains(p.Comp);
            // a gate's end pieces (attr bits 0x80 / 0x800 = render flags 7, 11) only on a diagonal gate
            if (s.GateEnds != null && p.HasAttr && (p.Attr & PartBits.GATE_ENDS) != 0) armOk = armOk && s.GateEnds.Value;
            // one geometry variant per wall piece (TagWallArms): the tile's pick, different per arm
            // pick: [hub, arm 0..7] - each arm's pick comes from the edge it shares with its neighbour, so both halves of
            // a wall segment use the same variant and the top line is even
            if (armOk && p.VCount > 1) armOk = ((s.VariantPick != null ? s.VariantPick[(arm ?? -1) + 1] : 0) % p.VCount) == p.Variant;
            if (p.Sig >= 0) o.Visible = armOk && todOk && PartBits.SigVisible(p.Sig, s.Level, s.Damage, s.Epoch);
            else if (arm != null || p.Comp != null || s.GateEnds != null) o.Visible = armOk && todOk;
            else if (p.Tod != TimeOfDay.Always) o.Visible = todOk;
        }
    }
}
