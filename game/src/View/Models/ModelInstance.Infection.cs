// The "infected" look of a model (docs/INFECTED_LOOK.md): ModelInstance.SetInfected(stage) - view only, a drawing
// option like the party colour; the mission (or a debug tool) decides which object shows it and how far.
//
//   stage 0  the normal look
//   stage 1  freshly infected   rot, slime at the foot, first veins; one poison cloud, flies; animals and characters:
//                               pale skin, a wound; the undead: green eyes
//   stage 2  spreading          fires and lights green, glowing veins, more clouds; more wounds, green eyes
//   stage 3  fully overrun      nearly black-green, slime half way up, a ring of poison, the whole blight; every wound
//
// Which look (InfectedLookData.KindOf): buildings (and props), vehicles / ships / walking statues (the building look on
// the moving model), animals, characters, the undead (only the glow and the mist: they are rotten already), items (a
// character's weapon or tool: rot only). The materials: InfectedLook.Material per surface (in ApplyTints, after the
// setting, party and SEAS tints, before the "units behind" stencil copy). Animals, characters and vehicles draw with
// meshes that carry their bind positions (InfectedLook.RestMesh) so their wounds and rot follow the bones. The effects
// are children of the instance: the original's fx_poison_big / fx_poison clouds (their emitters tracked as any
// model's), the lumberjack cottage's meat flies (Particles.TrackNode through ModelLibrary.OnEmitter), the blight on the
// ground (InfectionBlight; buildings: an ellipse from the footprint). The model's own fires, smoke and lights turn green
// from stage 2 (GreenFire: Particles with InfectedLook.TurnsGreen - insects, dust and water keep their colours;
// InfectedLook.GreenGlow). Attached parts follow (InfectedLookData.ChildKind): turrets and cranes of a building, the
// rider and the build-ups of a mount, a character's weapons; passengers of a transporter do not (their own entities),
// nor characters or animals on a building. A part attached later (a new rider, a weapon) takes the stage at once.
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.View.Fx;
using PwRemake.View.Materials;
using PwRemake.View.Parts;

namespace PwRemake.View.Models;

public partial class ModelInstance
{
    int _infect;
    LookKind? _infectAs;
    bool _passenger;
    readonly List<Node3D> _infectFx = new();
    Dictionary<MeshInstance3D, Mesh>? _plainMeshes;

    /// <summary>the infection stage drawn (0 = healthy; InfectedLookData.MAX_STAGE = fully overrun)</summary>
    public int Infection => _infect;
    /// <summary>the model's fires, lights and particles are drawn green (Particles reads it per emitter)</summary>
    public bool GreenFire { get; private set; }
    /// <summary>the effect nodes of the infection (clouds, flies, the blight) - tests</summary>
    public IReadOnlyList<Node3D> InfectionFx => _infectFx;
    /// <summary>the look this model wears when infected: its own (InfectedLookData.KindOf) or, an attached part, the one
    /// its parent gives it (ChildKind: a character's weapon is an item)</summary>
    public LookKind InfectionKind => _infectAs ?? InfectedLook.KindOf(Template);

    /// <summary>
    /// Draw this model infected: stage 0 (healthy) .. InfectedLookData.MAX_STAGE (fully overrun). Applied only when it
    /// changed. The attached parts follow (a mount's rider and build-ups, a building's turrets, a character's weapons;
    /// not the passengers of a transporter).
    /// </summary>
    public void SetInfected(int stage) => SetInfected(stage, _infectAs);

    /// <summary>draw this model in another model's look (a copy of an attached part alone: EntityViews' warm-up of the
    /// item and rider looks)</summary>
    internal void SetInfectedAs(int stage, LookKind kind) => SetInfected(stage, kind);

    void SetInfected(int stage, LookKind? asKind)
    {
        stage = Math.Clamp(stage, 0, InfectedLookData.MAX_STAGE);
        if (stage == _infect && asKind == _infectAs) return;
        // (another look for the same model: its meshes back first - an item does not draw from bind positions)
        if (asKind != _infectAs && _plainMeshes != null) SwapRestMeshes(false);
        _infectAs = asKind;
        _infect = stage;
        var kind = InfectionKind;
        GreenFire = InfectedLookData.Stage(stage).GreenFire;
        // (--perf: the parts of the switch - PerfProbe "inf:*", docs/DEBUG.md "Performance")
        var P = PwRemake.View.PerfProbe.P;
        long t0 = P != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        SwapRestMeshes(stage > 0 && InfectedLook.UsesRest(kind));
        GreenSprites(GreenFire);
        long t1 = P != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        ApplyTints();
        long t2 = P != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        BuildInfectionFx();
        if (P != null)
        {
            long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
            P.Add("inf:rest meshes", t1 - t0); P.Add("inf:materials", t2 - t1); P.Add("inf:effects", t3 - t2);
        }
        foreach (var a in _attached) Propagate(a);
    }

    // an attached part takes this model's stage, in the look ChildKind gives it (none: it stays as it is)
    void Propagate(ModelInstance a)
    {
        if (a._passenger) return;
        var k = InfectedLookData.ChildKind(InfectionKind, InfectedLook.KindOf(a.Template));
        if (k == null) return;
        a.SetInfected(_infect, k);
    }

    // (Attach / AttachTo: a part attached while infected - a rider after a level-up, a new weapon - is infected at once)
    void InfectAttached(ModelInstance a) { if (_infect > 0) Propagate(a); }

    /// <summary>a passenger seated on this transporter's model (Composite.SyncPassengers): its own entity - it does not
    /// take the transporter's infection</summary>
    public void MarkPassenger()
    {
        _passenger = true;
        if (_infect > 0) SetInfected(0, null);
    }

    // the surface's material with the infection (ApplyTints)
    Material Infected(Material m)
    {
        if (_infect <= 0) return m;
        var kind = InfectionKind;
        return InfectedLook.Material(m, _infect, kind, Template.Name, Template.Bounds, InfectedLook.WoundsOf(Template, kind));
    }

    // animals, characters, vehicles: their meshes with the bind positions in CUSTOM0 (InfectedLook.RestMesh), or back
    // to the template's
    void SwapRestMeshes(bool on)
    {
        foreach (var (n, _) in Parts)
        {
            if (n is not MeshInstance3D mi || mi.Mesh == null) continue;
            if (on)
            {
                _plainMeshes ??= new();
                if (_plainMeshes.ContainsKey(mi)) continue;
                // a skinned mesh: its vertices are the bind pose in its node's frame; a rigid part on a bone: where it rests
                var toModel = mi.Skin != null ? ModelLibrary.ToRoot(mi, Model) : ModelLibrary.RestToRoot(mi, Model);
                var r = InfectedLook.RestMesh(mi.Mesh, toModel);
                if (r == null) continue;
                _plainMeshes[mi] = mi.Mesh;
                mi.Mesh = r;
            }
            else if (_plainMeshes != null && _plainMeshes.TryGetValue(mi, out var plain)) mi.Mesh = plain;
        }
        if (!on) _plainMeshes = null;
    }

    static readonly Dictionary<Material, Material> s_greenSprites = new();
    Dictionary<MultiMeshInstance3D, Material>? _plainSprites;

    // the additive sprites (lantern halos: ModelSprites glow) green, or back to the template's
    void GreenSprites(bool on)
    {
        foreach (var (n, p) in Parts)
        {
            if (p.Kind != PartKind.Sprite || n is not MultiMeshInstance3D mm) continue;
            if (on)
            {
                if (_plainSprites?.ContainsKey(mm) == true || mm.MaterialOverride is not ShaderMaterial sh || !sh.Shader.Code.Contains("uniform bool green")) continue;
                if (!s_greenSprites.TryGetValue(sh, out var g))
                {
                    var c = (ShaderMaterial)sh.Duplicate();
                    c.SetShaderParameter("green", true);
                    s_greenSprites[sh] = g = c;
                }
                (_plainSprites ??= new())[mm] = sh;
                mm.MaterialOverride = g;
            }
            else if (_plainSprites != null && _plainSprites.TryGetValue(mm, out var plain)) mm.MaterialOverride = plain;
        }
        if (!on) _plainSprites = null;
    }

    void BuildInfectionFx()
    {
        foreach (var n in _infectFx) { n.GetParent()?.RemoveChild(n); n.QueueFree(); }
        _infectFx.Clear();
        // (an attached part - a rider, a turret, a crane, a weapon - has none of its own: its model's are about it)
        if (_infect <= 0 || _infectAs != null) return;
        var kind = InfectionKind;
        var tune = kind == LookKind.Building ? InfectedLookData.TuneFor(Template.Name) : null;
        var b = Template.Bounds;
        var (mist, big, flies, blight) = InfectedLookData.Effects(kind, _infect, tune, Mathf.Max(b.Size.X, b.Size.Z) * 0.5f);
        var c = b.GetCenter();
        switch (kind)
        {
            case LookKind.Animal:
                // a poison cloud about the legs and flies over the back (they move with it; the cloud low - at the chest
                // its bright core covered a mount's front at the game's camera)
                if (mist > 0) Fx("fx_poison", new Vector3(c.X, b.Size.Y * 0.25f, c.Z), Mathf.Clamp(b.Size.Y / 10f, 0.3f, 0.7f));
                if (flies > 0) Flies(new Vector3(c.X, b.End.Y * 0.95f, c.Z), Mathf.Clamp(b.Size.Y * 0.45f, 1.2f, 3f));
                return;
            case LookKind.Character or LookKind.Undead:
                // a small cloud at the feet, flies about the head
                // (small: a bright cloud the size of the legs hid the feet close up; at the game's camera a green glow at the feet)
                if (mist > 0) Fx("fx_poison", new Vector3(c.X, b.Size.Y * 0.06f, c.Z), Mathf.Clamp(b.Size.Y / 22f, 0.15f, 0.22f));
                if (flies > 0) Flies(new Vector3(c.X, b.End.Y * 0.95f, c.Z), Mathf.Clamp(b.Size.Y * 0.2f, 0.6f, 1.0f));
                return;
            case LookKind.Vehicle:
            {
                // clouds and flies along the hull (no blight: it moves)
                var vr = InfectedLookData.MistRing(mist, b.Size.X * 0.35f, b.Size.Z * 0.35f, 0.9);
                foreach (var (x, z) in vr) Fx("fx_poison", new Vector3(c.X + (float)x, b.Size.Y * 0.3f, c.Z + (float)z), Mathf.Clamp(b.Size.Y / 6f, 0.4f, 1f));
                var vf = InfectedLookData.MistRing(flies, b.Size.X * 0.2f, b.Size.Z * 0.2f, 2.1);
                foreach (var (x, z) in vf) Flies(new Vector3(c.X + (float)x, b.End.Y * 0.8f, c.Z + (float)z), Mathf.Clamp(b.Size.Y * 0.4f, 1.2f, 2.4f));
                return;
            }
            case LookKind.Building:
                break;
            default:
                return;
        }
        // a building: poison clouds round its foot (an ellipse along a wall), flies, the blight
        float hx = b.Size.X * 0.5f, hz = b.Size.Z * 0.5f;
        var ring = InfectedLookData.MistRing(mist, Mathf.Max(hx * 0.85f, 1.5f), Mathf.Max(hz * 0.85f, 1.5f), 0.6);
        foreach (var (x, z) in ring) Fx(big ? "fx_poison_big" : "fx_poison", new Vector3(c.X + (float)x, 0.4f, c.Z + (float)z), 1f);
        var fl = InfectedLookData.MistRing(flies, hx * 0.45f, hz * 0.45f, 2.1);
        float fh = Mathf.Min(2.2f, b.Size.Y * 0.6f);
        for (int i = 0; i < fl.Length; i++) Flies(new Vector3(c.X + (float)fl[i].X, fh + 1.3f * i, c.Z + (float)fl[i].Z), 2.4f);
        if (blight)
        {
            var (rx, rz) = InfectedLookData.BlightRadii(hx, hz, _infect, tune!);
            long t0 = PwRemake.View.PerfProbe.P != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
            var bl = new InfectionBlight((float)rx, (float)rz, Mathf.Min(1f, 0.55f + 0.18f * _infect)) { Position = new Vector3(c.X, 0, c.Z) };
            AddChild(bl);
            PwRemake.View.PerfProbe.P?.Add("inf:blight", System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            _infectFx.Add(bl);
        }
    }

    // one of the game's effect models at a point of this model (its emitters spray as any model's: OnInstance)
    void Fx(string gfx, Vector3 at, float scale)
    {
        long t0 = PwRemake.View.PerfProbe.P != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        var m = Library.Template(gfx, null, true) is { } t ? Library.Instantiate(t) : null;
        PwRemake.View.PerfProbe.P?.Add("inf:effect models", System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        if (m == null) return;
        m.Position = at;
        m.Scale = Vector3.One * scale;
        AddChild(m);
        _infectFx.Add(m);
    }

    // the original's meat flies (hu_lumberjack_cottage) about a point; scale: the swarm's size (and its flies')
    void Flies(Vector3 at, float scale)
    {
        long t0 = PwRemake.View.PerfProbe.P != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        if (Library.OnEmitter is not { } track || InfectedLook.FliesEmitter(Library) is not { } def) return;
        PwRemake.View.PerfProbe.P?.Add("inf:flies emitter", System.Diagnostics.Stopwatch.GetTimestamp() - t0);
        // (the emitter's axis is the GSF up: z -> y as in the models)
        var n = new Node3D { Name = "infection_flies", Position = at, Rotation = new Vector3(-Mathf.Pi / 2, 0, 0), Scale = Vector3.One * scale };
        AddChild(n);
        _infectFx.Add(n);
        track(n, def, this);
    }
}
