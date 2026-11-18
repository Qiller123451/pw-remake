// Stand-alone scenery: every placed object of a campaign map that is not a unit or building of the tech tree but
// that a mission may address - arena gates, barricades, cages, city houses and walls, townsfolk, items, effects -
// and the healing wells of every map (systems/landmarks.js). (The mass of anonymous landscape objects stays in the
// instanced prop field of the view.)
// A Prop can be shown / hidden (OBAP), play one of its model's animations (ACDO SetAnim), be deleted or replaced
// (DELO, REPL) and blocks the ground like the landscape objects do. It has no hit points.
//
// Ported from remake/src/game/prop.js (pw-exporter 98c7703). The view draws Model at Pos turned by Rot (or the map's
// quaternion Q), with PartyColor, while Visible, playing Anim.
using System;
using System.Collections.Generic;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

/// <summary>What a prop is made from: { model, x, z, y (null = on the ground), rot, q (stored map quaternion or null), block, party, blockRadius }</summary>
public sealed class PropSpec
{
    public string Model = "";
    public double X, Z;
    public double? Y;
    public double Rot;
    public double[]? Q;
    public bool Block;
    public int? Party;
    public double BlockRadius;
}

public sealed class Prop
{
    static readonly Logger L = Log.For("World.Prop");
    public readonly World World;
    /// <summary>(a saved game makes it again from these: game/save.js)</summary>
    public readonly PropSpec Spec;
    public string Kind => "prop";
    public string Model;
    public V3 Pos;
    public double Rot;
    public bool Alive = true;
    public bool Visible = true;
    public ModelInfo? Tpl;
    public AnimCtl? Anim;
    public List<int> Cells = new();
    public bool Blocking;
    public double AnimT;
    public string? LastAnim;
    public int LastLoops;
    public int LookVersion;

    /// <summary>
    /// the ground cells under a model's pathfinder boxes (the GSF pf table, boxes of type 1, as buildings use them) at
    /// (x, z) turned by rot; null when the model has none
    /// </summary>
    public static List<int>? PfCells(NavGrid nav, ModelManifest M, string? model, double x, double z, double rot, double scale = 1)
    {
        var pf = M.Get(model)?.Pf;
        if (pf == null) return null;
        var set = new OrderedSet<int>();
        double c = SMath.Cos(rot), s = SMath.Sin(rot);
        foreach (var r in pf)
            if (r.Length >= 6 && r[0] == 1 && r[4] > 0.5 && r[5] > 0.5)
            {
                double bx = -(r[1] + r[4] / 2) * scale, bz = -(r[2] + r[5] / 2) * scale;
                foreach (var k in nav.RectCells(x + bx * c + bz * s, z - bx * s + bz * c, r[4] / 2 * scale, r[5] / 2 * scale, rot)) set.Add(k);
            }
        return set.Count > 0 ? set.ToList() : null;
    }

    public Prop(World world, PropSpec o)
    {
        World = world;
        Spec = o;
        Model = o.Model;
        Pos = new V3(o.X, o.Y ?? world.Height(o.X, o.Z), o.Z);
        Rot = o.Rot;
        var M = world.Models;
        Tpl = M.Get(o.Model);
        if (Tpl == null) L.Warn("prop: model not loaded " + o.Model);
        var clips = M.Clips(o.Model);
        Anim = Tpl != null && clips.Count > 0 ? new AnimCtl(clips, null) : null;
        if (Anim != null) { var a = Anim.Pick("standanim", "idle", "idle_0", "stand"); if (a != null) Anim.Play(a); }
        // ground cells: the model's pathfinder boxes (GSF table, as buildings use them), else a circle like the
        // landscape objects of the map source
        if (o.Block)
        {
            Cells = PfCells(world.Nav, M, o.Model, o.X, o.Z, Rot) ?? new List<int>();
            // a bridge without boxes on an original map: the ground just under its deck (Bridges.cs)
            if (Cells.Count == 0 && world.MapWalkGrid && o.Y != null && Bridges.IsBridge(o.Model))
                Cells = Bridges.Shade(world.Nav, M, world.Height, o.Model, o.X, o.Z, Rot, o.Y.Value) ?? new List<int>();
            // a model without pathfinder boxes blocks nothing in the engine (on the original maps their walk grid has the
            // rest); the remake's own maps have no walk grid: a circle
            if (Cells.Count == 0 && o.BlockRadius > 0 && !world.MapWalkGrid) Cells = world.Nav.MarkCircle(o.X, o.Z, o.BlockRadius, 0);
        }
        Blocking = false;
        SetBlocking(true);
    }

    public void SetBlocking(bool on)
    {
        if (on == Blocking || Cells.Count == 0) return;
        Blocking = on;
        World.Nav.Mark(Cells, on ? 1 : -1);
    }

    public void SetVisible(bool on)
    {
        Visible = on;
        LookVersion++;
        SetBlocking(Visible);              // scenery that "disappears" (OBAP 0) no longer stands in the way
    }

    /// <summary>play an animation of the model: once (loops &lt;= 1; it stays on its last frame, e.g. a gate that opened)
    /// or looping. Returns false if the model has no such animation.</summary>
    public bool PlayAnim(string name, int loops = 1)
    {
        if (Anim == null || !Anim.Has(name)) return false;
        Anim.Play(name, new AnimOpts { Loop = loops > 1, Restart = true, Fade = 0 });
        AnimT = 0;
        LastAnim = name; LastLoops = loops;
        LookVersion++;
        return true;
    }

    public void Update(double dt) { if (Anim != null && Visible) Anim.Update(dt); }

    public void Remove()
    {
        if (!Alive) return;
        Alive = false;
        SetBlocking(false);
        Anim?.Stop();
        LookVersion++;
    }
}
