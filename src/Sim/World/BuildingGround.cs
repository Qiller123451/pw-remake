// The ground under a building put down in the game (World.AdaptTerrain, docs/TERRAIN.md "Ground under buildings").
//
// The original levels and paints the ground under a new building with the building model's ground brush,
// Texture/Scape/ObjBrush/<gfx>.tga (Maps/Ground.cs). It has brushes for 101 models - most houses of the Norsemen,
// Dustriders and Dragon Clan, five of the SEAS' - and a building without one changes no ground: the Dustriders' and
// Dragon Clan's towers, the Dragon Clan's cauldron, the SEAS' laboratory (the hospital), big tent, fortress, cannons,
// MIRAGE's own buildings ... The remake gives every building a brush (Kacper 2026-11-11: "not every placed building
// changes the terrain under it, e.g. the SEAS hospital doesn't"): the original's where it has one; else one made from
// the building's footprint (the GSF pathfinder boxes) and the brush of its tribe whose levelled core is nearest in size
// - its core stretched over the footprint, its rim (the soft falloff, the earth and the grass) kept as wide as it is,
// so the ground looks like the tribe's own (gravel or paving under the house, earth around it).
//
// Which buildings change the ground (Changes): every building but walls and gates (the original's bWall: a line of
// pieces on any slope), coastal buildings (they stand in the water), traps (hidden from the enemy: a patch of fresh
// ground would give them away) and a tower put on a wall piece (it stands on the wall). A free-standing tower does -
// the original has brushes for the Norsemen's. (A ladder stands on a wall too.)
using System;
using System.Collections.Generic;
using PwRemake.Sim.Content;
using PwRemake.Sim.Maps;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

public static class BuildingGround
{
    /// <summary>the original brushes a tribe's buildings take theirs from (by the model's name)</summary>
    static readonly Dictionary<string, string> PREFIX = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hu"] = "hu_", ["Aje"] = "aje_", ["Ninigi"] = "ninigi_", ["SEAS"] = "seas_",
    };
    /// <summary>brushes that are no house's: wall pieces, gates, bridges, harbours (long, or in the water)</summary>
    static bool Template(string name) =>
        !(name.Contains("wall") || name.Contains("palisade") || name.Contains("gate") || name.Contains("bridge") || name.Contains("harbour") || name.Contains("anvil") || name.Contains("carrier"));

    /// <summary>whether a building of this kind levels and paints the ground it is put on (header)</summary>
    public static bool Changes(ObjectDef? def, bool onWall = false, bool dock = false) =>
        def != null && !def.Coastal && !dock && !onWall && !def.Ladder && def.WallKind is not ("wall" or "gate" or "trap");

    /// <summary>gives the brushes a fallback for the models the original has no brush for (idempotent per rules)</summary>
    public static void Use(GroundBrushes? brushes, Rules.Rules D, ModelManifest M)
    {
        if (brushes == null || ReferenceEquals(brushes.FallbackOwner, D)) return;
        // a model's tribe: the tribe of the building classes drawn with it (the first in the tech tree's order)
        var tribeOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in D.IndexOrder)
        {
            var ix = D.Index[name];
            if (ix.Type != "BLDG") continue;
            var g = D.ClassGfx(name) ?? D.Stats(name, 1, null)?.Gfx;
            if (!string.IsNullOrEmpty(g) && !tribeOf.ContainsKey(g)) tribeOf[g] = ix.Tribe;
        }
        brushes.FallbackOwner = D;
        brushes.Fallback = gfx => Make(brushes, gfx, tribeOf.TryGetValue(gfx, out var t) ? t : null, Footprint.FootBoxes(M, gfx));
        brushes.ClearMade();
    }

    /// <summary>
    /// A brush for a model without its own: the footprint boxes (Footprint.FootBoxes: the building's local frame, which
    /// is the brush's turned by half a turn - brush (x, y) = local (-x, -z), Ground.LevelGround), the tribe's original
    /// brush whose core (alpha above 90 %) is nearest the footprint in size, mapped axis by axis: inside the footprint
    /// onto the core, outside it at the original's scale (the rim keeps its width). Null without a footprint or without
    /// any original brush.
    /// </summary>
    public static GroundBrush? Make(GroundBrushes brushes, string gfx, string? tribe, double[][] foot)
    {
        // the footprint in the brush's frame (metres from the model's origin)
        double f0x = 1e9, f1x = -1e9, f0z = 1e9, f1z = -1e9;
        foreach (var b in foot)
        {
            if (b[2] <= 0 || b[3] <= 0) continue;
            f0x = Math.Min(f0x, -b[0] - b[2]); f1x = Math.Max(f1x, -b[0] + b[2]);
            f0z = Math.Min(f0z, -b[1] - b[3]); f1z = Math.Max(f1z, -b[1] + b[3]);
        }
        if (f1x - f0x < 0.5 || f1z - f0z < 0.5) return null;
        double fw = f1x - f0x, fh = f1z - f0z, mpp = brushes.Mpp;
        // the tribe's template (else any house's)
        string? prefix = tribe != null && PREFIX.TryGetValue(tribe, out var pf) ? pf : null;
        if (prefix == null) foreach (var (t, p) in PREFIX) if (gfx.StartsWith(p, StringComparison.OrdinalIgnoreCase)) { prefix = p; break; }
        (GroundBrush B, double C0x, double C1x, double C0z, double C1z)? best = null;
        double bestScore = 1e18;
        for (int pass = 0; pass < 2 && best == null; pass++)
            foreach (var name in brushes.OriginalNames())
            {
                if (!Template(name) || pass == 0 && prefix != null && !name.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var tb = brushes.Original(name);
                if (tb?.M == null || Core(tb, mpp) is not var (c0x, c1x, c0z, c1z)) continue;
                double cw = c1x - c0x, ch = c1z - c0z;
                double ax = fw / cw, az = fh / ch;
                double score = Math.Max(ax, 1 / ax) * Math.Max(az, 1 / az);
                if (score < bestScore - 1e-12) { bestScore = score; best = (tb, c0x, c1x, c0z, c1z); }
            }
        if (best is not var (T, cx0, cx1, cz0, cz1)) return null;
        // the rim beyond the core (the widest side of the template)
        double rim = Math.Max(Math.Max(cx0 + T.W * mpp / 2, T.W * mpp / 2 - cx1), Math.Max(cz0 + T.H * mpp / 2, T.H * mpp / 2 - cz1));
        double hx = Math.Max(-f0x, f1x) + rim, hz = Math.Max(-f0z, f1z) + rim;
        int W = 2 * (int)Math.Ceiling(hx / mpp), H = 2 * (int)Math.Ceiling(hz / mpp);
        var A = new byte[W * H];
        var Mt = new byte[W * H];
        static double Map(double p, double f0, double f1, double c0, double c1) =>
            p < f0 ? c0 - (f0 - p) : p > f1 ? c1 + (p - f1) : c0 + (p - f0) * (c1 - c0) / (f1 - f0);
        for (int v = 0; v < H; v++)
            for (int u = 0; u < W; u++)
            {
                double bx = (u + 0.5 - W / 2.0) * mpp, bz = (v + 0.5 - H / 2.0) * mpp;
                double qx = Map(bx, f0x, f1x, cx0, cx1), qz = Map(bz, f0z, f1z, cz0, cz1);
                int tu = (int)Math.Floor(qx / mpp + T.W / 2.0), tv = (int)Math.Floor(qz / mpp + T.H / 2.0);
                bool inside = tu >= 0 && tv >= 0 && tu < T.W && tv < T.H;
                // (the materials of every pixel - the 4 m cells under the whole brush take them, Ground.BrushCells -
                // beyond the template's edge its edge's: no levelling there)
                tu = Math.Clamp(tu, 0, T.W - 1); tv = Math.Clamp(tv, 0, T.H - 1);
                A[v * W + u] = inside ? T.A[tv * T.W + tu] : (byte)0;
                Mt[v * W + u] = T.M![tv * T.W + tu];
            }
        return new GroundBrush { W = W, H = H, A = A, M = Mt };
    }

    /// <summary>the levelled core of a brush (alpha above 90 %), in metres from its centre</summary>
    static (double, double, double, double)? Core(GroundBrush b, double mpp)
    {
        int u0 = int.MaxValue, u1 = -1, v0 = int.MaxValue, v1 = -1;
        for (int v = 0; v < b.H; v++)
            for (int u = 0; u < b.W; u++)
                if (b.A[v * b.W + u] > 230) { u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v); }
        if (u1 < 0) return null;
        return ((u0 - b.W / 2.0) * mpp, (u1 + 1 - b.W / 2.0) * mpp, (v0 - b.H / 2.0) * mpp, (v1 + 1 - b.H / 2.0) * mpp);
    }
}
