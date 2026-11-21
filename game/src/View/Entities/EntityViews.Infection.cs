// The infected look (docs/INFECTED_LOOK.md) of the objects out of sight, prepared ahead (Kacper 2026-11-20, Outbreak:
// "a huge lag spike when I went near the first infected settlement"). A model took its look (ModelInstance.SetInfected)
// only when it was first drawn, so a village coming into view made every one of its buildings' and units' infected
// materials, bind-pose meshes, clouds, flies and blight in one frame (--perf-to V1_hu_kennel_4: 90 - 140 ms of the
// session's frame in the container, the CPU's part alone), and the graphics driver compiled the infected shaders at
// their first drawing on top of it.
//
// Now an infected object out of sight is prepared in the background: its colours, setting textures, "units behind"
// role, parts and states set as the drawing would set them, then its infected look - a few objects per frame within a
// time budget (more in the game's first seconds, while the mission infects its villages; Outbreak: 92 buildings at
// game time 0.05 s). The first model of each look (template, stage, kind, role) is also drawn once out of sight - deep
// under the ground below the camera, its shadow too - as the placement warm-up does (GameInput.WarmUpPlacement), so its
// shaders and pipelines (colour, depth and shadow passes) are compiled then, not when the village comes into view. Off with --no-warmup (the costs as they were).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using Godot;
using PwRemake.Sim.World;
using PwRemake.View.Materials;
using PwRemake.View.Models;

namespace PwRemake.View.Entities;

public partial class EntityViews
{
    /// <summary>infected objects out of sight are prepared ahead (false: when first drawn - tests, --no-warmup)</summary>
    public bool PrepareInfectedAhead = true;
    /// <summary>the time per frame spent on preparing (ms); in the game's first seconds START_BUDGET_MS</summary>
    public const double BUDGET_MS = 4, START_BUDGET_MS = 60, START_SECONDS = 5;
    readonly List<EntityView> _infectQ = new();
    readonly HashSet<EntityView> _infectQueued = new();
    // the looks drawn once out of sight (their shaders compiled), the node holding them and the frames it has been drawn
    readonly HashSet<(ModelTemplate, int, LookKind, BehindRole)> _warmed = new();
    Node3D? _warm;
    int _warmFrames;
    /// <summary>objects prepared and looks drawn once (the log, tests)</summary>
    public int InfectedPrepared { get; private set; }
    public int InfectedWarmed => _warmed.Count;
    double _infectMs, _loggedMs;
    bool _infectLogged;

    void QueueInfected(EntityView v)
    {
        if (!PrepareInfectedAhead || !_infectQueued.Add(v)) return;
        _infectQ.Add(v);
    }

    /// <summary>end of the frame's sync: queued objects prepared within the budget; the warm-up node goes after its frames</summary>
    void PrepareInfected(Camera3D? cam)
    {
        if (_warm != null && ++_warmFrames >= 3) { _warm.QueueFree(); _warm = null; }
        if (_infectQ.Count == 0)
        {
            if (!_infectLogged && InfectedPrepared > 0)
            {
                _infectLogged = true;
                // (a batch worth telling: the start's, a big wave's - not every unit spawned out of sight)
                if (_infectMs - _loggedMs > 50)
                    L.Info($"infected look prepared ahead: {InfectedPrepared} objects in {_infectMs:0} ms, {_warmed.Count} looks drawn once out of sight");
                _loggedMs = _infectMs;
                // (the start's batch - the materials, meshes and effects of a whole map's infected objects - lives on:
                // collected now, while the title shows, not by the next collection in the middle of the game)
                if (W.Time < START_SECONDS && _infectMs > 100) System.GC.Collect(1);
            }
            return;
        }
        double budget = W.Time < START_SECONDS ? START_BUDGET_MS : BUDGET_MS;
        var sw = Stopwatch.StartNew();
        int i = 0;
        for (; i < _infectQ.Count && (i == 0 || sw.Elapsed.TotalMilliseconds < budget); i++)
        {
            var v = _infectQ[i];
            _infectQueued.Remove(v);
            try { PrepareOne(v, cam); }
            catch (Exception ex) { if (_failed.Add(v.E.Id)) L.Error($"preparing the infected look of {v.E} failed", ex); }
        }
        _infectQ.RemoveRange(0, i);
        _infectMs += sw.Elapsed.TotalMilliseconds;
        if (_infectQ.Count > 0) _infectLogged = false;
    }

    // what SyncOne sets when the object is drawn, set now: colours, setting textures, the "units behind" role, parts
    // and states, then the infected look
    void PrepareOne(EntityView v, Camera3D? cam)
    {
        var e = v.E;
        var m = v.M;
        if (e.Removed || m == null || !GodotObject.IsInstanceValid(m) || m.Infection == e.Infection || !(e.Alive || e is Unit)) return;
        // (where it stands first: the blight is laid on the ground under the model as it is made - InfectionBlight)
        Vector3 pos; double heading;
        if (e is Building bd) { pos = new Vector3((float)bd.Origin.X, (float)(bd.Origin.Y - bd.SinkY), (float)bd.Origin.Z); heading = bd.Rot; }
        else { pos = new Vector3((float)e.Pos.X, (float)e.Pos.Y, (float)e.Pos.Z); heading = e.Heading; if (e is Unit su) pos.Y -= (float)su.SinkY; }
        if (m.Position != pos) m.Position = pos;
        float hd = (float)heading;
        if (hd != v.DrawnH || !v.HasDrawnH) { m.Rotation = new Vector3(0, hd, 0); v.DrawnH = hd; v.HasDrawnH = true; }
        Color? party = e.Owner?.PartyColor is int pc ? Hex(pc) : null;
        if (party != v.Party || v.TintEpoch != ModelMaterials.TintEpoch)
        {
            v.Party = party; v.TintEpoch = ModelMaterials.TintEpoch;
            m.SetPartyColor(party); foreach (var w in v.Weapons) w.SetPartyColor(party);
        }
        if (e is Building b) m.SetSetting(v.Setting ??= View.SettingAt((float)b.Origin.X, (float)b.Origin.Z));
        var role = e is Building ? BehindRole.Building : e is Unit u && u.Alive && u.Cls != "SHIP" ? BehindRole.Unit : BehindRole.None;
        m.SetBehind(role, role >= BehindRole.Unit ? GroundRings.RingColor(e) : default);
        if (e.LookVersion != v.Look || v.PartsChanged()) SyncLook(v, m, party);
        m.SetInfected(e.Infection);
        InfectedPrepared++;
        if (e.Infection > 0 && cam != null) WarmLook(m, party, role, cam);
    }

    // the first model of a look drawn once out of sight: a copy with the same colours, role and stage behind the camera,
    // never culled, no shadows, no emitters - its draw calls compile the shaders and pipelines (the pixels are clipped).
    // Its attached parts too, each in the look it wears there (a rider: the character look; a weapon: the item look -
    // other shaders): a copy of each part's model alone
    void WarmLook(ModelInstance m, Color? party, BehindRole role, Camera3D cam)
    {
        WarmOne(m, party, role, cam, null);
        foreach (var a in AttachedParts(m)) if (a.Infection > 0) WarmOne(a, party, role, cam, a.InfectionKind);
    }

    void WarmOne(ModelInstance m, Color? party, BehindRole role, Camera3D cam, LookKind? asKind)
    {
        var key = (m.Template, m.Infection, m.InfectionKind, role);
        if (!_warmed.Add(key)) return;
        var c = Lib.Instantiate(m.Template, party);
        c.Sprays = false;
        c.SetPartyColor(party);
        if (m.Setting != null) c.SetSetting(m.Setting);
        c.SetBehind(role, role >= BehindRole.Unit ? Colors.White : default);
        if (asKind is { } k) c.SetInfectedAs(m.Infection, k); else c.SetInfected(m.Infection);
        if (_warm == null || !GodotObject.IsInstanceValid(_warm))
        {
            // (deep under the ground below the camera: hidden by the terrain where the view meets them, and their shadows
            // fall further down - away from the light, past the shadow maps' far end - so the shadow pass is drawn and
            // compiled too, with nothing to see; the cull margin keeps them from being culled in either pass)
            var at = cam.GlobalPosition;
            _warm = new Node3D { Name = "InfectedWarmUp" };
            AddChild(_warm);
            _warm.GlobalPosition = new Vector3(at.X, at.Y - 1500, at.Z);
        }
        _warmFrames = 0;
        // (spread out so they do not all stand in one spot; the size does not matter - nothing of them is seen)
        c.Position = new Vector3((_warmed.Count % 8) * 12 - 42, 0, (_warmed.Count / 8 % 8) * 12);
        _warm.AddChild(c);
        Quiet(c);
    }

    // the models attached below a model (riders, turrets, weapons, tools: ModelInstance children at its links), not
    // the infection's own effect models
    static IEnumerable<ModelInstance> AttachedParts(ModelInstance m)
    {
        var fx = new HashSet<Node3D>(m.InfectionFx);
        var stack = new Stack<Node>();
        foreach (var c in m.GetChildren()) stack.Push(c);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            if (n is Node3D n3 && fx.Contains(n3)) continue;
            if (n is ModelInstance mi) yield return mi;
            foreach (var c in n.GetChildren()) stack.Push(c);
        }
    }

    void Quiet(Node n)
    {
        if (n is GeometryInstance3D gi) gi.ExtraCullMargin = 16384;
        if (n is PwRemake.View.Fx.InfectionBlight bl) bl.Unfit();
        if (n is Light3D l) l.Visible = false;
        if (n is ModelInstance mi) { mi.Sprays = false; View.Particles?.Untrack(mi); }
        foreach (var c in n.GetChildren()) Quiet(c);
    }
}
