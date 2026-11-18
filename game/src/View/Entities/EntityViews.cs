// Every simulation entity drawn (the units, buildings, projectiles and felled trees of the world): a view per entity,
// made when it appears in the world's lists and freed when it is removed, synced every frame from its view-facing
// state (EntityView). What the player may see (main.js updateFow): own and seen-through objects always; others' units
// where the fog of war is visible now (not hidden from the player: camouflage, stealth), others' buildings where it
// was ever explored; the boarded and parked never. What is drawn (main.js cullUnits / freezeStill): an object whose
// sphere - or its shadow - is in the camera's view; the others are hidden and their clips stand still until they come
// back (then put on the simulation's time).
// The map's resources stay the world view's props (WorldView: trees, stones, bushes); a felled tree's log falls with
// its chop_down clip and lies until its wood is gone, its stump stays (ui/logs.js), a used-up stone or bush goes.
// Ported from remake/src/main.js (updateFow, cullUnits, freezeStill), remake/src/ui/logs.js, game/systems/combat.js
// (the projectiles' models) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;
using PwRemake.View.Materials;
using PwRemake.View.Models;
using PwRemake.View.Parts;
using PwRemake.View.Terrain;
using PwRemake.View.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.View.Entities;

public partial class EntityViews : Node3D
{
    static readonly Logger L = Log.For("View.Entities");
    readonly GameWorld W;
    readonly ModelLibrary Lib;
    readonly WorldView View;
    readonly Func<Player?> _me;
    readonly FogGrid Fog;
    readonly Func<Player?, bool> _sees;
    readonly Dictionary<Entity, EntityView> _views = new();
    readonly List<EntityView> _list = new();
    int _frame;
    /// <summary>entities animated on another clock than the steps (a cutscene's actors: the world stands still) and
    /// that clock's frame time</summary>
    public Func<Entity, bool>? Live;
    public double LiveDt;
    /// <summary>the clips an entity is drawn with instead of its own (a cutscene's actors: the cutscene player's own
    /// AnimCtl - the view never plays clips on the world's entities: docs/SAVES.md)</summary>
    public Func<Entity, AnimCtl?>? AnimOf;
    /// <summary>the clips a prop is drawn with instead of its own (a cutscene's bound prop: mission 13's carrier opening
    /// its hull in sc_4070), advanced by LiveDt</summary>
    public Func<Prop, AnimCtl?>? PropAnimOf;
    /// <summary>a cutscene hides this world object (an Entity or a Prop) while it plays (SessionSequenceStage.Hides)</summary>
    public Func<object, bool>? Hide;
    /// <summary>models drawn this frame</summary>
    public int Drawn { get; private set; }
    /// <summary>the shadow's length per metre of height (main.js SHADOW_REACH)</summary>
    const double SHADOW_REACH = 0.8;
    readonly HashSet<string> _walls, _gates;
    static readonly Dictionary<string, bool> s_files = new();

    public EntityViews(GameWorld w, ModelLibrary lib, WorldView view, Func<Player?> me, FogGrid fog, Func<Player?, bool> sees)
    {
        W = w; Lib = lib; View = view; _me = me; Fog = fog; _sees = sees;
        Name = "Entities";
        var (walls, gates) = WallModels(w.Data);
        _walls = walls; _gates = gates;
        IndexResources();
    }

    /// <summary>the model file exists in the data (a model of the manifest without its file is not asked for: the
    /// library would log an error for each)</summary>
    public static bool HasFile(ModelLibrary lib, string gfx)
    {
        var k = gfx.ToLowerInvariant();
        if (s_files.TryGetValue(k, out var ok)) return ok;
        ok = HasFileQuiet(lib, k);
        if (!ok && lib.Has(k)) L.Warn($"model {k} is in the manifest but its file (or its animation source's) is not in the data");
        s_files[k] = ok;
        return ok;
    }

    /// <summary>the load options of a model (wall pieces and gates are prepared differently)</summary>
    public ModelLoadOptions OptionsOf(string gfx) => OptionsOf(gfx, _walls, _gates);
    static readonly ModelLoadOptions s_wall = new() { WallArms = true }, s_gate = new() { Gate = true }, s_tree = new() { TreeSprites = true };

    /// <summary>a vegetation model (tree, plant, decoration: Vgtn / Ress / Deko) with foliage - its leaves are sprites
    /// of the foliage field on the map, so as a game object it needs its own (ModelLoadOptions.TreeSprites)</summary>
    public static bool NeedsTreeSprites(string fourcc, System.Text.Json.Nodes.JsonObject? extras) =>
        (fourcc == "Vgtn" || fourcc == "Ress" || fourcc == "Deko") && extras?["foliage"] is System.Text.Json.Nodes.JsonArray { Count: > 0 };
    public static ModelLoadOptions OptionsOf(string gfx, HashSet<string> walls, HashSet<string> gates)
    {
        var k = gfx.ToLowerInvariant();
        return walls.Contains(k) ? s_wall : gates.Contains(k) ? s_gate : ModelLoadOptions.Default;
    }

    /// <summary>the models of wall pieces (arms tagged by direction) and gates (their leaves) - main.js loadModels</summary>
    public static (HashSet<string> Walls, HashSet<string> Gates) WallModels(PwRemake.Sim.Rules.Rules D)
    {
        var walls = new HashSet<string> { "hu_small_wall", "hu_re_enforced_wall" }; var gates = new HashSet<string>();
        foreach (var (name, ix) in D.Index)
        {
            if (ix.Type != "BLDG") continue;
            var d = D.Def(name, null);
            if (d?.WallKind is not ("wall" or "gate")) continue;
            for (int l = 1; l <= 5; l++) if (D.Stats(name, l, null)?.Gfx is { Length: > 0 } g) (d.WallKind == "wall" ? walls : gates).Add(g.ToLowerInvariant());
        }
        return (walls, gates);
    }

    /// <summary>HasFile without the warning (the preload list)</summary>
    public static bool HasFileQuiet(ModelLibrary lib, string gfx)
    {
        var k = gfx.ToLowerInvariant();
        if (!lib.Has(k) || lib.Source.Resolve($"assets/models/{k}.glb") == null) return false;
        var src = lib.Info(k)?.Anims;
        return string.IsNullOrEmpty(src) || lib.Source.Resolve($"assets/models/{src.ToLowerInvariant()}.glb") != null;
    }

    public EntityView? ViewOf(Entity e) => _views.GetValueOrDefault(e);
    /// <summary>the model drawn for an entity (null: none)</summary>
    public ModelInstance? ModelOf(Entity e) => _views.GetValueOrDefault(e)?.M;
    /// <summary>the entity is shown to the player (fog of war, not boarded)</summary>
    public bool Shown(Entity e) => _views.TryGetValue(e, out var v) && v.Shown;
    /// <summary>in the camera's view (animations and sounds)</summary>
    public bool OnScreen(Entity e) => _views.TryGetValue(e, out var v) && v.Shown && v.OnScreen;
    /// <summary>the drawn position (interpolated)</summary>
    public Vector3 PosOf(Entity e) => _views.TryGetValue(e, out var v) && v.M != null && GodotObject.IsInstanceValid(v.M) ? v.M.Position : new Vector3((float)e.Pos.X, (float)e.Pos.Y, (float)e.Pos.Z);

    /// <summary>before every simulation step: the units' positions become the previous ones</summary>
    public void BeforeStep()
    {
        foreach (var v in _list) if (v.E is Unit u) { v.Prev = u.Pos; v.PrevH = u.Heading; }
    }

    // ============================================================================ per frame
    /// <summary>gdt: the game time of this frame (clips); alpha: the fraction of a step since the last one</summary>
    public void Sync(double gdt, double alpha, Camera3D? cam, bool first)
    {
        _frame++;
        var P = PerfProbe.P;
        P?.Begin();
        var frustum = Frustum.Of(cam, ref _planes);
        var me = _me();
        (_cover, _coverNext) = (_coverNext, _cover);
        _coverNext.Clear();
        if (cam != null) _camPos = cam.GlobalPosition;
        // ---- new and gone
        foreach (var u in W.Units) Touch(u);
        foreach (var b in W.Buildings) Touch(b);
        for (int i = _list.Count - 1; i >= 0; i--)
        {
            var v = _list[i];
            if (v.Seen == _frame && !v.E.Removed) continue;
            Free(v);
            _list.RemoveAt(i);
            _views.Remove(v.E);
        }
        P?.Mark("entities:list");
        // ---- each
        int drawn = 0;
        foreach (var v in _list)
        {
            try { if (SyncOne(v, gdt, alpha, frustum, me)) drawn++; }
            catch (Exception ex) { if (_failed.Add(v.E.Id)) L.Error($"drawing {v.E} failed", ex); }
        }
        Drawn = drawn;
        P?.Mark("entities:each");
        SyncProjectiles(frustum);
        P?.Mark("entities:projectiles");
        SyncProps(gdt, frustum);
        P?.Mark("entities:props");
        SyncResources(gdt);
        P?.Mark("entities:resources");
        SyncWrecks(gdt);
        P?.Mark("entities:wrecks");
    }
    Plane[] _planes = System.Array.Empty<Plane>();
    readonly HashSet<int> _failed = new();

    void Touch(Entity e)
    {
        if (e.Removed) return;
        if (!_views.TryGetValue(e, out var v))
        {
            v = new EntityView(e);
            _views[e] = v;
            _list.Add(v);
        }
        v.Seen = _frame;
    }

    void Free(EntityView v)
    {
        if (v.M != null && GodotObject.IsInstanceValid(v.M)) { v.M.GetParent()?.RemoveChild(v.M); v.M.QueueFree(); }
        v.M = null;
        v.HasDrawnH = false;
    }

    /// <summary>the model an entity shows now: its Gfx (a ruin after its destruction; the class file's model or a
    /// stand-in when that one has no file)</summary>
    string? ModelName(EntityView v)
    {
        var e = v.E;
        // an amphibian's swimming model on land: its land model (the transport turtle: macrolemys_water swims only -
        // its walk set is swim_1 / swim_2 -, macrolemys_land has the same shell link and walks; Medium)
        if (e is Unit au && au.Amphib && InWater(au) == false && e.Gfx is { } wg && wg.EndsWith("_water", StringComparison.OrdinalIgnoreCase)
            && HasFile(Lib, wg[..^6] + "_land")) return wg[..^6] + "_land";
        if (e is Building b && !b.Alive && b.Ruin != null) return b.Ruin;
        // MIRAGE's rising building (custom_buildups.txt CustomConstruction): its site shows paving tiles, the finished
        // model rises under them (SyncRiser)
        if (e is Building sb && sb.RisingSiteGfx is { } tiles && HasFile(Lib, tiles)) return tiles;
        // a burning resin field shows its burning model (Building.usl CImpResinField.StartBurning: SetGFX("ninigi_resin_field_fire"))
        if (e is Building rb && rb.Alive && rb.Burning != null && e.Gfx != null && HasFile(Lib, e.Gfx + "_fire")) return e.Gfx + "_fire";
        return e.Gfx;
    }

    bool Visible(EntityView v, Player? me)
    {
        var e = v.E;
        if (e.Parked || Hide?.Invoke(e) == true) return false;
        if (e is Unit u)
        {
            if (u.Inside) return false;
            if (_sees(u.Owner)) return true;
            if (!u.Alive) return v.Shown && Fog.Visible(u.Pos.X, u.Pos.Z) || u.DeadT > 0 && Fog.Explored(u.Pos.X, u.Pos.Z);
            return Fog.Visible(u.Pos.X, u.Pos.Z) && !W.HiddenFrom(u, me);
        }
        if (_sees(e.Owner)) return true;
        return Fog.Explored(e.Pos.X, e.Pos.Z) && !(e.Alive && W.HiddenFrom(e, me));
    }

    static bool InFrustum(Plane[]? f, Vector3 c, float r) => Frustum.Has(f, c, r);

    // ---- units behind buildings (UnitsBehind): the buildings on screen of the last frame, as upright cylinders
    readonly record struct Cover(float X, float Z, float R, float Y0, float Y1);
    List<Cover> _cover = new(), _coverNext = new();
    Vector3 _camPos;

    /// <summary>
    /// A unit (centre c, radius r) may be hidden by a building on screen: the line from the camera to it goes through
    /// a building's cylinder (its radius and height, widened by r) before it reaches the unit. Only those units get the
    /// silhouette pass (one more draw per surface): not the units in front of or beside a building.
    /// </summary>
    bool MayBeCovered(Vector3 c, float r)
    {
        var d = c - _camPos;
        float a = d.X * d.X + d.Z * d.Z;
        if (a < 1e-4f) return false;
        foreach (var b in _cover)
        {
            // where the segment camera -> unit (t 0..1) is inside the circle (x, z) of the building, then its heights there
            float fx = _camPos.X - b.X, fz = _camPos.Z - b.Z, R = b.R + r;
            float hb = fx * d.X + fz * d.Z, cc = fx * fx + fz * fz - R * R, disc = hb * hb - a * cc;
            if (disc < 0) continue;
            float sq = Mathf.Sqrt(disc), t0 = Math.Max(0, (-hb - sq) / a), t1 = Math.Min(1, (-hb + sq) / a);
            if (t0 > t1) continue;
            float y0 = _camPos.Y + d.Y * t0, y1 = _camPos.Y + d.Y * t1;
            if (Math.Min(y0, y1) <= b.Y1 + r && Math.Max(y0, y1) >= b.Y0) return true;
        }
        return false;
    }

    bool SyncOne(EntityView v, double gdt, double alpha, Plane[]? frustum, Player? me)
    {
        var e = v.E;
        var P = PerfProbe.P;
        P?.Sub();
        // ---- the model (a new one when the entity's model changed)
        var gfx = ModelName(v);
        if (gfx != v.Gfx || (v.M != null && !GodotObject.IsInstanceValid(v.M)))
        {
            v.ClearAttachments();
            Free(v);
            v.Gfx = gfx;
            v.Look = int.MinValue; v.AnimSerial = -1; v.MediumClip = null;
            if (gfx != null && HasFile(Lib, gfx))
            {
                var tpl = Lib.Template(gfx.ToLowerInvariant(), OptionsOf(gfx), true);
                // a tree or decoration as a game object (mission 10's DH_Darwins_Treehouse: a Deko model, the tree's
                // crown is its foliage): the leaves as the model's own sprites - the map's foliage field draws only the
                // map's plants, and the treehouse stood on a bare trunk
                if (tpl != null && NeedsTreeSprites(tpl.Fourcc, tpl.Extras))
                    tpl = Lib.Template(gfx.ToLowerInvariant(), s_tree, true) ?? tpl;
                if (tpl != null)
                {
                    v.M = Lib.Instantiate(tpl);
                    v.M.Name = $"{e.Kind}_{e.Id}_{gfx}";
                    v.Party = null;
                    AddChild(v.M);
                }
            }
        }
        var m = v.M;
        // ---- visible to the player
        v.Shown = Visible(v, me);
        // ---- where: units between the last two steps
        Vector3 pos; double heading;
        bool live = Live?.Invoke(e) == true;
        if (live) { v.Prev = e.Pos; v.PrevH = e.Heading; gdt = LiveDt; }
        if (e is Unit)
        {
            var a = v.Prev; var b = e.Pos;
            double dx = b.X - a.X, dz = b.Z - a.Z;
            if (dx * dx + dz * dz > 100) a = b;                 // (a jump: teleport, boarding)
            pos = new Vector3((float)(a.X + (b.X - a.X) * alpha), (float)(a.Y + (b.Y - a.Y) * alpha), (float)(a.Z + (b.Z - a.Z) * alpha));
            double dh = Math.IEEERemainder(e.Heading - v.PrevH, Math.Tau);
            heading = v.PrevH + dh * alpha;
            pos.Y -= (float)((Unit)e).SinkY;
        }
        else if (e is Building bd)
        {
            pos = new Vector3((float)bd.Origin.X, (float)(bd.Origin.Y - bd.SinkY), (float)bd.Origin.Z);
            heading = bd.Rot;
        }
        else { pos = new Vector3((float)e.Pos.X, (float)e.Pos.Y, (float)e.Pos.Z); heading = e.Heading; }
        // ---- in the view (or its shadow)
        float h = e is Unit uu ? (float)uu.Height : e is Building bb2 ? (float)Math.Max(3, bb2.Ht) : 2;
        float rad = (float)Math.Max(e.Radius, 1);
        var c = pos + new Vector3(0, h * 0.5f, 0);
        v.OnScreen = InFrustum(frustum, c, e is Building ? Math.Max(rad + 12, Mathf.Sqrt(rad * rad + h * h * 0.25f) + 2) : Math.Max(rad, h) + 1);
        // (a flyer's shadow falls its height and more away from it: MIRAGE's flying units at 15 m, World.Flyers)
        float lift = e is Unit lu ? (float)(lu.LiftH + lu.JumpH) : 0;
        bool near = v.OnScreen || InFrustum(frustum, c, (e is Building ? rad + 12 : Math.Max(rad, h) + 1) + (float)Math.Max(e is Building ? 0 : 12, (h + lift) * SHADOW_REACH));
        P?.SubMark("ent:model+visible");
        if (m == null) return false;
        bool draw = v.Shown && near;
        if (e is Unit wu && wu.Wreck != null) draw = false;      // (a ship's wreck stands in for it: EntityViews.Wrecks)
        m.Visible = draw;
        if (!draw) { v.Culled = true; return false; }
        // (set only when it changed: every set sends the change down the model's whole tree of nodes - a building's
        // dozens of parts - and to the renderer, also for the buildings and the units standing still)
        if (m.Position != pos) m.Position = pos;
        float hd = (float)heading;
        if (hd != v.DrawnH || !v.HasDrawnH) { m.Rotation = new Vector3(0, hd, 0); v.DrawnH = hd; v.HasDrawnH = true; }
        P?.SubMark("ent:transform");
        // ---- colours
        Color? party = e.Owner?.PartyColor is int pc ? Hex(pc) : null;
        // (and again when the "Original colours" option changed: SetPartyColor re-applies the tints)
        if (party != v.Party || v.TintEpoch != ModelMaterials.TintEpoch)
        {
            v.Party = party; v.TintEpoch = ModelMaterials.TintEpoch;
            m.SetPartyColor(party); foreach (var w in v.Weapons) w.SetPartyColor(party);
        }
        // a building, wall or built object: the original's setting textures of the biome where it stands (SettingMaterials;
        // units keep theirs - the original has none for them). Cheap when nothing changed (SetSetting).
        if (e is Building) m.SetSetting(v.Setting ??= View.SettingAt(pos.X, pos.Z));
        // a unit behind a building shows through it in its ring colour, a building covers (UnitsBehind; the original's
        // flatshades): ships and the dead do not show (SetBehind returns at once when nothing changed)
        BehindRole behind = BehindRole.None;
        if (e is Building)
        {
            behind = BehindRole.Building;
            if (v.OnScreen && UnitsBehind.Enabled) _coverNext.Add(new Cover(pos.X, pos.Z, rad + 1, pos.Y - 1, pos.Y + h + 1));
        }
        else if (e is Unit bu && bu.Alive && bu.Cls != "SHIP")
            behind = v.OnScreen && MayBeCovered(c, Math.Max(rad, h * 0.5f)) ? BehindRole.UnitCovered : BehindRole.Unit;
        m.SetBehind(behind, behind >= BehindRole.Unit ? GroundRings.RingColor(e) : default);
        // ---- the look (parts, flags, states) when it changed
        if (e.LookVersion != v.Look || v.PartsChanged())
        {
            v.Look = e.LookVersion;
            if (v.PartsChanged()) v.BuildParts();
            if (e is Unit u)
            {
                v.SyncHands(u);
                v.SyncHead(u);
                if (u.HideMask >= 0) m.SetHideMask((uint)u.HideMask);
                m.SetAnimalTint(u.PartyTinted ? party : null);
                foreach (var (bit, on) in u.VisFlags) m.SetVisFlag(bit, on);
                // a nest's young is small and grows up (animals.js growthUpdate: obj.scale = 1 - babyness * (1 - growupScale))
                m.Scale = Vector3.One * (u.Babyness > 0 ? 1f - (float)u.Babyness * (1f - m.Template.GrowupScale) : 1f) * (float)u.DrawScale;
            }
            else if (e is Building b)
            {
                var st = new BuildingState(b.VisLevel, b.VisDmg, b.VisAge, b.Night);
                if (_walls.Contains(v.Gfx!.ToLowerInvariant()))
                {
                    st.ArmMask = b.ArmMask;
                    if (b.WallParts != null) st.WallParts = WallSet(b.WallParts);
                }
                if (b.Def?.WallKind == "gate") st.GateEnds = b.GateEnds;
                m.SetBuildingState(st);
            }
        }
        else if (e is Building b2 && m.State.Night != b2.Night) m.SetNight(b2.Night);
        P?.SubMark("ent:look");
        // ---- the clips (advanced only while drawn; a model coming back is put on the simulation's time)
        // a ruin plays its clip "destroy" once from the building's destruction (BuildingCorpse.PlayAnim) - its emitters
        // are the explosion, the debris and the smoke
        if (e is Building rd && !rd.Alive && rd.Ruin != null && v.Gfx == rd.Ruin)
        {
            m.SyncAnimation("destroy", false, 1, W.Time - rd.DeadT, W.Time);
            m.Advance(gdt);
            v.SyncParts(gdt, true);
            v.SyncRuinParts(W.Time - rd.DeadT, W.Time);      // (its turret's ruin: Building.RuinParts)
            return true;
        }
        var anim = AnimOf?.Invoke(e) ?? e.Anim;
        if (anim != null)
        {
            int s = v.AnimSerial;
            if (v.Culled || !ReferenceEquals(anim, v.AnimSrc)) { s = -1; v.Culled = false; v.AnimSrc = anim; v.MediumClip = null; }
            // an amphibian walks on land and swims in the water, whatever clip the simulation's model has for moving
            var mu = e as Unit;
            var alt = mu != null && mu.Amphib && InWater(mu) is bool wet && anim.Cur != null && m.Anim != null ? MediumClip(m.Anim, anim.Cur.Clip, wet) : null;
            // a hit reaction (Unit.HitReactN: a melee blow's overlay clip, FightingObj.usl:4436-4446) over the running clip:
            // the pose of the last frame's reaction undone before the player poses this frame, the new one on top after
            if (mu != null && m.Anim != null) SyncHitReaction(v, m.Anim, mu);
            m.Anim?.OverlayRestore();
            if (alt != null) SyncMediumAnim(v, m, mu!, anim, alt, gdt);
            else
            {
                if (v.MediumClip != null) { v.MediumClip = null; s = -1; }        // (back to the simulation's clip, at its time)
                EntityView.SyncAnim(m, anim, ref s, gdt, true);
            }
            m.Anim?.OverlayApply(gdt);
            v.AnimSerial = s;
            // a flamethrower's flame burns from its first strike until the attack ends (Unit.Spraying, Particles.Firing)
            if (m.Anim != null) m.Anim.HoldFire = mu != null && mu.Spraying;
        }
        P?.SubMark("ent:anim");
        v.SyncParts(gdt, true);
        if (e is Building rp && !rp.Alive && rp.RuinParts != null) v.SyncRuinParts(W.Time - rp.DeadT, W.Time);   // (no ruin of its own)
        if (e is Building rb2) SyncRiser(v, rb2, m, gdt);
        P?.SubMark("ent:parts");
        return true;
    }

    /// <summary>
    /// MIRAGE's rising building (CBuildUpMgr / CFakeBuildUp: CustomBuildUps.cs): while the site shows its paving tiles
    /// the finished model stands Building.RiseY under (or over) the building's origin, turned as the building, finished
    /// (construction level 4) and in the owner's colour; gone when the building is finished.
    /// </summary>
    void SyncRiser(EntityView v, Building b, ModelInstance m, double gdt)
    {
        if (v.Riser != null && !GodotObject.IsInstanceValid(v.Riser)) v.Riser = null;
        bool want = b.Rising && v.Gfx != b.Gfx && b.Gfx != null && HasFile(Lib, b.Gfx);
        if (!want)
        {
            if (v.Riser != null) { v.Riser.GetParent()?.RemoveChild(v.Riser); v.Riser.QueueFree(); v.Riser = null; }
            return;
        }
        if (v.Riser == null)
        {
            var tpl = Lib.Template(b.Gfx!.ToLowerInvariant(), OptionsOf(b.Gfx!), true);
            if (tpl == null) return;
            v.Riser = Lib.Instantiate(tpl);
            v.Riser.Name = "riser_" + b.Gfx;
            m.AddChild(v.Riser);
        }
        var r = v.Riser;
        r.Position = new Vector3(0, (float)b.RiseY, 0);
        r.SetPartyColor(v.Party);
        r.SetBuildingState(new BuildingState(4, 0, b.VisAge, b.Night));
        r.SetSetting(v.Setting);
    }

    // ============================================================================ amphibians (user report: the
    // transport turtles swam over land). The simulation plays the clips of the unit's model as its walk set says - the
    // transport turtle's model macrolemys_water has only swim_1 / swim_2 (its GSF walk set), the baryonyx walk_1..4 and
    // swim_1 / swim_2 apart -, whether the unit is on land or in the water. The view draws it walking on land and
    // swimming in the water: a moving clip of the other medium is played instead (walk_<n> <-> swim_<n>), the idle of
    // the land (stand_anim_land) on land where the model has one, and the swimming-only model is swapped for its land
    // model on land (ModelName). View only: the simulation's clips, times and model stay as they are (lockstep, saves).
    // In the water: by the simulation's own rule, nothing kept in the view - the ground under the unit more than 0.5 m
    // below the water (Naval.cs: a unit "in the water"; World.GroundY floats an amphibian from 0.2 m).
    /// <summary>a new hit reaction of the unit (its counter moved): the overlay clip of the blow's side, if the blow is
    /// recent (a unit coming back on screen does not play the reactions it had off screen)</summary>
    void SyncHitReaction(EntityView v, PwRemake.View.Anim.AnimController a, Unit u)
    {
        if (u.HitReactN == v.HitReactN) return;
        v.HitReactN = u.HitReactN;
        if (W.Time - u.HitReactT < 0.3) a.HitReaction(u.HitReactSide);
    }

    public bool InWater(Unit u) => W.WaterLevel is double wl && W.Height(u.Pos.X, u.Pos.Z) < wl - 0.5;

    static readonly System.Text.RegularExpressions.Regex MOVE = new(@"^(walk|swim)_?(\d*)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>the clip of the medium for a clip of the simulation (null: the simulation's clip is right here)</summary>
    public static string? MediumClip(PwRemake.View.Anim.AnimController A, string clip, bool water)
    {
        int h = clip.IndexOf('#');
        string b = h < 0 ? clip : clip[..h], part = h < 0 ? "" : clip[h..];
        string? to = null;
        var mv = MOVE.Match(b);
        if (mv.Success)
        {
            string n = mv.Groups[2].Value.Length > 0 ? mv.Groups[2].Value : "1";
            if (water && mv.Groups[1].Value == "walk") to = A.Pick("swim_" + n, "swim_2", "swim_1", "swim");
            else if (!water && mv.Groups[1].Value == "swim") to = A.Pick("walk_" + n, "walk_2", "walk_1");
        }
        else if (!water && (b == "standanim" || b == "idle_0" || b == "stand")) to = A.Pick("stand_anim_land");
        else if (!water && !A.Has(b)) to = A.Pick("stand_anim_land", "standanim");          // (a clip the land model lacks)
        if (to == null || to == b) return null;
        return part.Length > 0 && A.Has(to + part) ? to + part : to;
    }

    // a clip of the medium: started when it changes, then played at the unit's speed (the clip's own speed where the
    // model gives one: a land walk at the pace of the steps) - not put back on the simulation's time, which is the time
    // of another clip
    void SyncMediumAnim(EntityView v, ModelInstance m, Unit u, AnimCtl sim, string clip, double dt)
    {
        var A = m.Anim!;
        var cur = sim.Cur!;
        double ts = cur.TimeScale;
        string b = clip.Split('#')[0];
        double spd = Math.Sqrt(u.Vel.X * u.Vel.X + u.Vel.Z * u.Vel.Z);
        if (W.Models.Get(v.Gfx ?? "")?.Speeds is { } sp && sp.TryGetValue(b, out var cs) && cs > 0 && spd > 0.15) ts = Math.Clamp(spd / cs, 0.4, 1.8);
        if (clip != v.MediumClip)
        {
            bool loop = cur.Loop || clip.EndsWith("#l") || b.StartsWith("walk") || b.StartsWith("swim");
            if (!A.Start(clip, loop, ts, v.MediumClip == null ? 0.3 : 0.25, "")) return;
            v.MediumClip = clip;
        }
        if (cur.IsRunning && A.Player.SpeedScale != (float)ts) A.Player.SpeedScale = (float)ts;
        if (!cur.IsRunning) return;
        if (!A.Player.IsPlaying() && A.Player.HasAnimation(A.Player.AssignedAnimation)) A.Player.Play(A.Player.AssignedAnimation);
        A.Player.Advance(dt);
    }

    readonly Dictionary<string, HashSet<string>> _wallSets = new();
    HashSet<string> WallSet(List<string> parts)
    {
        var k = string.Join(",", parts);
        if (!_wallSets.TryGetValue(k, out var s)) _wallSets[k] = s = new HashSet<string>(parts);
        return s;
    }

    public static Color Hex(int c) => new(((c >> 16) & 255) / 255f, ((c >> 8) & 255) / 255f, (c & 255) / 255f);

    // ============================================================================ props (prop.js: a mission's objects
    // without rules - decoration it places, wells, items on the ground - with their own clips and visibility)
    readonly Dictionary<Prop, (ModelInstance? M, int Look, int Serial, AnimCtl? Src)> _props = new();

    /// <summary>the model of a well or an item (its prop)</summary>
    public ModelInstance? PropModel(Entity e)
    {
        var p = e is Well w ? w.Prop : e is Item it ? it.Prop : null;
        return p != null && _props.TryGetValue(p, out var v) ? v.M : null;
    }

    void SyncProps(double gdt, Plane[]? frustum)
    {
        foreach (var p in W.Props)
        {
            if (!_props.TryGetValue(p, out var v))
            {
                ModelInstance? m = null;
                if (!string.IsNullOrEmpty(p.Model) && HasFile(Lib, p.Model) && Lib.Template(p.Model.ToLowerInvariant(), OptionsOf(p.Model), true) is { } tpl)
                {
                    m = Lib.Instantiate(tpl, p.Spec.Party is int pc ? Hex(pc) : null);
                    m.Name = "prop_" + p.Model;
                    if (p.Spec.Q != null) { var q = PwRemake.Sim.Maps.Frame.MapQuat(p.Spec.Q); m.Quaternion = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]).Normalized(); }
                    else m.Rotation = new Vector3(0, (float)p.Rot, 0);
                    AddChild(m);
                }
                _props[p] = v = (m, -1, -1, null);
            }
            var mi = v.M;
            if (mi == null) continue;
            var pos = new Vector3((float)p.Pos.X, (float)p.Pos.Y, (float)p.Pos.Z);
            if (!mi.Template.IsDynamic) mi.SetSetting(mi.Setting ?? View.SettingAt(pos.X, pos.Z));   // (a well: its setting's textures)
            bool show = p.Alive && p.Visible && Fog.Explored(p.Pos.X, p.Pos.Z) && InFrustum(frustum, pos + Vector3.Up * 2, 14) && Hide?.Invoke(p) != true;
            mi.Visible = show;
            if (!show) continue;
            mi.Position = pos;
            if (p.Spec.Q == null) mi.Rotation = new Vector3(0, (float)p.Rot, 0);
            var scene = PropAnimOf?.Invoke(p);
            var anim = scene ?? p.Anim;
            if (anim != null)
            {
                // (a cutscene's clip or the world's again: the new one from its own state)
                int s = ReferenceEquals(anim, v.Src) ? v.Serial : -1;
                EntityView.SyncAnim(mi, anim, ref s, scene != null ? LiveDt : gdt, true);
                if (s != v.Serial || !ReferenceEquals(anim, v.Src)) _props[p] = (mi, v.Look, s, anim);
            }
        }
        if (_props.Count > W.Props.Count)
            foreach (var p in _props.Keys.Where(p => !W.Props.Contains(p)).ToList()) { _props[p].M?.QueueFree(); _props.Remove(p); }
    }

    // ============================================================================ projectiles
    readonly Dictionary<Projectile, Node3D> _proj = new();
    static Mesh? s_shaft;

    void SyncProjectiles(Plane[]? frustum)
    {
        foreach (var p in W.Projectiles)
        {
            if (!p.Alive) continue;
            if (!_proj.TryGetValue(p, out var n))
            {
                n = MakeProjectile(p);
                _proj[p] = n;
                AddChild(n);
            }
            var pos = new Vector3((float)p.Pos.X, (float)p.Pos.Y, (float)p.Pos.Z);
            n.Visible = p.Delay <= 0 && Fog.Visible(p.Pos.X, p.Pos.Z) && InFrustum(frustum, pos, 2);
            if (!n.Visible) continue;
            n.Position = pos;
            var dir = new Vector3((float)(p.Pos.X - p.Prev.X), (float)(p.Pos.Y - p.Prev.Y), (float)(p.Pos.Z - p.Prev.Z));
            if (dir.LengthSquared() < 1e-6f) dir = new Vector3((float)(p.Aim.X - p.Pos.X), (float)(p.Aim.Y - p.Pos.Y), (float)(p.Aim.Z - p.Pos.Z));
            // (the model's -z along the flight: quaternion.setFromUnitVectors((0, 0, -1), dir))
            if (dir.LengthSquared() > 1e-6f) n.Quaternion = new Quaternion(new Vector3(0, 0, -1), dir.Normalized());
        }
        if (_proj.Count > 0)
        {
            List<Projectile>? gone = null;
            foreach (var (p, n) in _proj) if (!p.Alive || !W.Projectiles.Contains(p)) (gone ??= new()).Add(p);
            if (gone != null) foreach (var p in gone) { var n = _proj[p]; _proj.Remove(p); n.QueueFree(); }
        }
    }

    Node3D MakeProjectile(Projectile p)
    {
        if (p.Gfx != null && HasFile(Lib, p.Gfx) && Lib.Instantiate(p.Gfx.ToLowerInvariant()) is ModelInstance m) return m;
        // no model (a class of the data without its file): a thin shaft along the flight
        s_shaft ??= new BoxMesh { Size = new Vector3(0.06f, 0.06f, 1.1f), Material = new StandardMaterial3D { AlbedoColor = new Color(0.25f, 0.18f, 0.1f), Roughness = 1 } };
        return new MeshInstance3D { Mesh = s_shaft, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
    }

    // ============================================================================ resources (trees, stones, bushes)
    // which prop of the world view stands for a node: matched by place (the world added them in the source's order)
    readonly Dictionary<ResNode, int> _treeOf = new(), _stoneOf = new(), _bushOf = new();
    readonly Dictionary<ResNode, (ModelInstance Log, double T, string? Clip)> _logs = new();
    readonly Dictionary<ResNode, PropInstance?> _stumps = new();
    readonly HashSet<ResNode> _gone = new();
    double _resT;

    static long Key(double x, double z) => ((long)Math.Round(x * 16) << 32) ^ (long)(uint)Math.Round(z * 16);

    void IndexResources()
    {
        var S = View.Source;
        Dictionary<long, int> Index<T>(List<T> list, Func<T, (double, double)> at)
        {
            var d = new Dictionary<long, int>();
            for (int i = 0; i < list.Count; i++) { var (x, z) = at(list[i]); d.TryAdd(Key(x, z), i); }
            return d;
        }
        var trees = Index(S.Trees, t => (t.X, t.Z));
        var stones = Index(S.Stones, t => (t.X, t.Z));
        var bushes = Index(S.Bushes, t => (t.X, t.Z));
        foreach (var n in W.Resources)
        {
            var k = Key(n.Pos.X, n.Pos.Z);
            if (n.Type is "tree" && trees.TryGetValue(k, out var i)) _treeOf[n] = i;
            else if (n.Type == "stone" && stones.TryGetValue(k, out var j)) _stoneOf[n] = j;
            else if (n.Type == "bush" && bushes.TryGetValue(k, out var q)) _bushOf[n] = q;
        }
        L.Info($"resources: {_treeOf.Count} trees, {_stoneOf.Count} stones, {_bushOf.Count} bushes of {W.Resources.Count} nodes matched to the map's props");
    }

    /// <summary>the felled tree's crown hits the ground (frame 40 of its clip: ui/logs.js LEAF_FRAME) - the effects'
    /// leaf blast and camera shake (View/Fx/WorldFx)</summary>
    public Action<ResNode, string?>? LeafFall;
    const double LEAF_FRAME = 40 / 25.0;

    /// <summary>a tree felled (the "treefall" event): its standing model and leaves go, the stump stays, the log falls</summary>
    public void TreeFelled(ResNode n)
    {
        if (_logs.ContainsKey(n)) return;
        if (_treeOf.TryGetValue(n, out var i)) View.RemoveTree(i);
        if (n.StumpKind != null && HasFile(Lib, n.StumpKind)) _stumps[n] = View.AddProp(n.StumpKind, n.Pos.X, n.Pos.Z, n.Rot, n.Scale);
        if (n.TimberKind == null || !HasFile(Lib, n.TimberKind)) return;
        var m = Lib.Instantiate(n.TimberKind.ToLowerInvariant());
        if (m == null) return;
        m.Position = new Vector3((float)n.Pos.X, (float)W.Height(n.Pos.X, n.Pos.Z), (float)n.Pos.Z);
        m.Rotation = new Vector3(0, (float)(n.LogRot ?? n.Rot), 0);
        m.Scale = Vector3.One * (float)(n.Scale <= 0 ? 1 : n.Scale);
        AddChild(m);
        string? clip = m.Anim == null ? null : m.Anim.Has(n.FallClip) ? n.FallClip : m.Anim.Pick("chop_down_01", "chop_down");
        if (clip != null) m.Play(clip, false, 1, null, 0);
        _logs[n] = (m, 0, clip);
    }

    /// <summary>the log's wood is used up ("timbergone")</summary>
    public void TimberGone(ResNode n)
    {
        if (_logs.Remove(n, out var l)) l.Log.QueueFree();
    }

    void SyncResources(double gdt)
    {
        // the falling logs (3 s, held on the last frame)
        foreach (var n in _logs.Keys.ToList())
        {
            var (m, t, clip) = _logs[n];
            if (t > 3) continue;
            m.Anim?.Player.Advance(gdt);
            _logs[n] = (m, t + gdt, clip);
            if (t < LEAF_FRAME && t + gdt >= LEAF_FRAME) LeafFall?.Invoke(n, clip);
        }
        // used up or cleared (every half second: a stone, a bush, a tree taken by a building's ground)
        _resT -= Math.Max(gdt, 0.016);
        if (_resT > 0) return;
        _resT = 0.5;
        foreach (var n in W.Resources) Check(n);
        foreach (var (n, i) in _treeOf) if (!n.Alive && n.Type == "tree") Check(n);
        void Check(ResNode n)
        {
            if (n.Alive || _gone.Contains(n)) return;
            _gone.Add(n);
            if (_treeOf.TryGetValue(n, out var i)) View.RemoveTree(i);
            if (_stoneOf.TryGetValue(n, out var j) && j < View.Stones.Count) View.RemoveProp(View.Stones[j]);
            if (_bushOf.TryGetValue(n, out var k) && k < View.Bushes.Count) View.RemoveProp(View.Bushes[k]);
            if (_logs.Remove(n, out var l)) l.Log.QueueFree();
        }
    }
}
