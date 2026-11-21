// The world view under the map editor (docs/MAP_EDITOR.md "How it is built"): the trees and the decoration of an area
// drawn anew from a fresh map source - forest squares and plants painted, things standing on raised or lowered ground.
// The rest of the view follows the editor in place already (TerrainMesh.RefreshHeights / RefreshSplat, RefreshBiomes,
// the grass's Invalidate, WaterSurface.SetLevel).
using System;
using PwRemake.Sim.Maps;

namespace PwRemake.View.World;

public partial class WorldView
{
    /// <summary>the trees and decoration in the game rectangle x0..x1, z0..z1 again from `fresh` (the editor's map source
    /// made anew from its document): those drawn there go, fresh's there come; the view's source takes fresh's lists</summary>
    public void RefreshVegetation(MapSource fresh, double x0, double z0, double x1, double z1)
    {
        bool In(double x, double z) => x >= x0 && x <= x1 && z >= z0 && z <= z1;
        foreach (var t in Trees)
        {
            if (t.Gone || !In(t.Src.X, t.Src.Z)) continue;
            t.Gone = true;
            Props.Remove(t.Prop);
            foreach (var h in t.Sprites) Foliage.Remove(h);
        }
        foreach (var e in Decor)
        {
            if (e.Gone || !In(e.X, e.Z)) continue;
            e.Gone = true;
            Props.Remove(e.Prop);
            foreach (var h in e.Sprites) Foliage.Remove(h);
            foreach (var g in e.Glows) g.QueueFree();
        }
        Source.Trees = fresh.Trees;
        Source.Decor = fresh.Decor;
        int n = 0;
        foreach (var t in fresh.Trees) if (In(t.X, t.Z)) { AddTree(t); n++; }
        foreach (var d in fresh.Decor) if (In(d.X, d.Z)) { AddDecor(d); n++; }
        Props.Build();
        Foliage.Rebuild();
        L.Debug($"vegetation of ({x0:0}, {z0:0}) .. ({x1:0}, {z1:0}) drawn anew: {n} trees and decorations");
    }
}
