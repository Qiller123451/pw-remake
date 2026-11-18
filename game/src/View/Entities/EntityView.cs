// One simulation entity drawn: its ModelInstance and the models attached to it (the weapon in hand, a worker's tool,
// the carried load, the attached parts of compose.js - riders, turrets, build-ups, flags, cranes, passengers), kept
// in step with the entity's view-facing state (docs/SIMULATION.md "What the view draws"): the model, the clip
// (AnimCtl: clip, time, speed, loop - restarted when its Serial changes), the part states (building level, damage,
// epoch, night, wall arms and parts, gate ends; animal / vehicle render mask; script visibility flags), the party
// colour, the turret angles. The position is interpolated between the last two simulation steps.
// The JS entities owned their three.js models (entities.js buildModel, attachWeapon, attachTool, setCarry,
// updateVisual, compose.js attach); here the view builds them from the plain data.
// Ported from remake/src/game/entities.js and game/compose.js (the drawing side) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;
using PwRemake.View.Anim;
using PwRemake.View.Models;
using PwRemake.View.Parts;

namespace PwRemake.View.Entities;

public sealed class EntityView
{
    public readonly Entity E;
    public ModelInstance? M;
    /// <summary>MIRAGE's rising building: the finished model coming up out of the ground on its site (a child of M:
    /// EntityViews.SyncRiser)</summary>
    public ModelInstance? Riser;
    /// <summary>the model shown (the entity's Gfx, a ruin, a stand-in)</summary>
    public string? Gfx;
    public int Look = int.MinValue;
    public int AnimSerial = -1;
    /// <summary>the AnimCtl the clips were last taken from (the entity's own, or a cutscene's: EntityViews.AnimOf)</summary>
    public object? AnimSrc;
    public Color? Party;
    /// <summary>ModelMaterials.TintEpoch the colours were applied at (the "Original colours" option)</summary>
    public int TintEpoch = -1;
    /// <summary>a building's setting textures: the setting of the biome where it stands (WorldView.SettingAt, once)</summary>
    public string? Setting;
    /// <summary>positions of the last two steps (units: interpolated)</summary>
    public V3 Prev, Cur;
    public double PrevH, CurH;
    public bool Shown, OnScreen, Culled;
    /// <summary>the heading the model was last turned to (EntityViews.SyncOne turns it only when it changes)</summary>
    public float DrawnH; public bool HasDrawnH;
    /// <summary>an amphibian: the clip of its medium playing instead of the simulation's (null: the simulation's;
    /// EntityViews.MediumClip)</summary>
    public string? MediumClip;
    /// <summary>the frame it was last seen in the world's lists</summary>
    public int Seen;
    // the attachments
    public readonly List<ModelInstance> Weapons = new();
    public ModelInstance? Tool, Load;
    public readonly List<(AttachedPart Sim, ModelInstance View, ModelInstance? Host, int Serial)> Parts = new();
    public string Key = "";
    /// <summary>the unit's hit reactions seen (Unit.HitReactN; EntityViews.SyncHitReaction)</summary>
    public int HitReactN;
    /// <summary>a beheaded corpse's head (Unit.Head) drawn at the corpse's origin, and its model name</summary>
    public ModelInstance? Head;
    string? _headKey;
    string? _toolKey, _loadKey;
    object? _weapon;

    public EntityView(Entity e) { E = e; Prev = Cur = e.Pos; PrevH = CurH = Heading(e); }

    public static double Heading(Entity e) => e is Building b ? b.Rot : e.Heading;

    /// <summary>the parts the entity has now (in order: parents first)</summary>
    public static IEnumerable<AttachedPart> SimParts(Entity e)
    {
        if (e is Unit u && u.Comp != null)
        {
            foreach (var p in u.Comp.Parts) yield return p;
            if (u.Comp.Wagon != null) yield return u.Comp.Wagon;
            foreach (var p in u.Comp.Riding) yield return p;
        }
        else if (e is Building b)
        {
            // a destroyed building: the ruins of its parts (Building.RuinParts) - the live parts went with it
            if (!b.Alive && (b.Ruin != null || b.RuinParts != null))
            {
                if (b.RuinParts != null) foreach (var p in b.RuinParts) yield return p;
                yield break;
            }
            foreach (var p in b.Parts) yield return p;
            foreach (var p in b.Cranes) yield return p;
        }
    }

    /// <summary>the attached parts of the model changed (by reference): rebuild them</summary>
    public bool PartsChanged()
    {
        int i = 0;
        foreach (var p in SimParts(E))
        {
            if (i >= Parts.Count || !ReferenceEquals(Parts[i].Sim, p)) return true;
            i++;
        }
        return i != Parts.Count;
    }

    public void ClearAttachments()
    {
        if (M == null) return;
        foreach (var w in Weapons) M.Detach(w);
        Weapons.Clear();
        if (Tool != null) { M.Detach(Tool); Tool = null; }
        if (Load != null) { M.Detach(Load); Load = null; }
        RemoveParts();
        if (Head != null) { M.Detach(Head); Head = null; }
        _weapon = null; _toolKey = null; _loadKey = null; _headKey = null;
    }

    /// <summary>
    /// A beheaded corpse's head (CCharacter.DiePerHeadOff: the object "&lt;class&gt;_head", a static mesh, made at the
    /// victim's place and turn; Unit.Head): drawn at the corpse model's origin in its turn - the head models are made in
    /// the character's own frame - and gone with the corpse. Nothing when the converted data has no such model.
    /// </summary>
    public void SyncHead(Unit u)
    {
        if (M == null || u.Head == _headKey) return;
        _headKey = u.Head;
        if (Head != null) { M.Detach(Head); Head = null; }
        if (u.Head != null && EntityViews.HasFile(M.Library, u.Head)) Head = M.AttachAtOrigin(u.Head.ToLowerInvariant(), Party);
    }

    /// <summary>weapon, tool and load (entities.js attachWeapon / attachTool / setCarry)</summary>
    public void SyncHands(Unit u)
    {
        if (M == null) return;
        if (!ReferenceEquals(_weapon, u.HeldWeapon))
        {
            foreach (var w in Weapons) M.Detach(w);
            Weapons.Clear();
            _weapon = u.HeldWeapon;
            if (u.HeldWeapon != null)
                foreach (var p in u.HeldWeapon.Parts)
                {
                    var gfx = p.Gfx.Count > 0 ? p.Gfx[0] : null;
                    if (gfx == null || !EntityViews.HasFile(M.Library, gfx)) continue;
                    if (M.Attach(gfx.ToLowerInvariant(), p.Link, Party) is ModelInstance o) Weapons.Add(o);
                }
        }
        var tk = u.ToolShown && u.ToolGfx != null ? u.ToolGfx + "@" + u.ToolLink : null;
        if (tk != _toolKey)
        {
            _toolKey = tk;
            if (Tool != null) { M.Detach(Tool); Tool = null; }
            if (tk != null && EntityViews.HasFile(M.Library, u.ToolGfx!)) Tool = M.Attach(u.ToolGfx!.ToLowerInvariant(), u.ToolLink ?? "HndR", Party);
        }
        var look = u.CarryShown && u.Carry?.Look is { Length: >= 2 } l ? l : null;
        var lk = look != null ? look[1] + "@" + look[0] : null;
        if (lk != _loadKey)
        {
            _loadKey = lk;
            if (Load != null) { M.Detach(Load); Load = null; }
            if (look != null && EntityViews.HasFile(M.Library, look[1])) Load = M.Attach(look[1].ToLowerInvariant(), look[0], Party);
        }
        bool wv = u.WeaponsVisible && Tool == null && Load == null;
        foreach (var w in Weapons) w.Visible = wv;
    }

    // parts may hang on other parts: the last first, each detached from the model it hangs on
    void RemoveParts()
    {
        for (int i = Parts.Count - 1; i >= 0; i--)
        {
            var (_, v, host, _) = Parts[i];
            if (host != null && GodotObject.IsInstanceValid(host)) host.Detach(v);
            else if (GodotObject.IsInstanceValid(v)) { v.GetParent()?.RemoveChild(v); v.QueueFree(); }
        }
        Parts.Clear();
    }

    /// <summary>the attached parts (compose.js attach): each on its link of the entity's model or of its parent part</summary>
    public void BuildParts()
    {
        if (M == null) return;
        RemoveParts();
        var made = new Dictionary<AttachedPart, ModelInstance>();
        foreach (var p in SimParts(E))
        {
            ModelInstance? host = p.Parent == null ? M : made.GetValueOrDefault(p.Parent);
            ModelInstance? v = null;
            if (host != null && host.Template != null && !string.IsNullOrEmpty(p.Gfx) && EntityViews.HasFile(M.Library, p.Gfx))
            {
                // (a ruin part: where the live part hung - RuinLink; a link the model lacks: its origin - MIRAGE's gallimimus nest)
                var link = (p.From != null ? RuinLink(host, p) : null) ?? host.LinkOrOrigin(p.Link, p.Gfx);
                if (link != null) v = host.AttachTo(link, p.Gfx.ToLowerInvariant(), Party);
                // MIRAGE's offsets (AddObjCustomized): metres in the link's frame - the frame the part hangs in
                if (v != null && p.Spec?.Offset is System.Text.Json.Nodes.JsonArray o && o.Count == 3) v.Position = PwRemake.View.Compose.Composite.OffsetOf(o);
                // a script's decoration: its own visibility flags (SetRndInvMaskSingleFlagInv - the colossus growing on its pad)
                if (v != null && p.VisFlags != null) foreach (var (bit, on) in p.VisFlags) v.SetVisFlag(bit, on);
            }
            // (a part that cannot be drawn keeps its place in the list: an empty node)
            if (v == null) { v = new ModelInstance { Name = "missing_" + p.Gfx }; M.AddChild(v); host = null; }
            made[p] = v;
            Parts.Add((p, v, host, -1));
        }
    }

    /// <summary>
    /// A ruin part's place (Building.RuinParts): the link the live part hung at, on the live model of the building (or
    /// of the parent part) - a ruin model need not have it (seas_hq_machinegun_nest_dest ...). A node in the host's GSF
    /// frame (its "Model" node; a part hung on a link is in that frame already, ModelInstance.ToLinkFrame) at the link's
    /// position from the model data (ModelFileInfo: the game's frame, turned back by the root's turn - the same for
    /// every converted model); the links the turrets hang on are not turned. Null: the live model has no such link.
    /// </summary>
    Node3D? RuinLink(ModelInstance host, AttachedPart p)
    {
        var liveHost = p.Parent?.From ?? (E as Building)?.Gfx;
        if (liveHost == null || E.World.Models.Link(liveHost, p.Link) is not V3 l) return null;
        // the converted root's turn and mirror (the building's model keeps it; a part on a link has lost it) and the
        // Model node's place (kept by both)
        var root = M!.Model.Transform.Basis;
        var at = new Vector3((float)l.X, (float)l.Y, (float)l.Z) - host.Model.Position;
        var n = new Node3D { Name = "ruinlink_" + p.Link, Position = root.Inverse() * at };
        host.Model.AddChild(n);
        return n;
    }

    /// <summary>a destroyed building's ruin parts play "destroy" once from the destruction (CBuildingCorpse.PlayAnim)</summary>
    public void SyncRuinParts(double start, double now)
    {
        foreach (var (p, v, _, _) in Parts)
            if (p.From != null && v.Template != null) v.SyncAnimation("destroy", false, 1, start, now);
    }

    /// <summary>per frame: the parts' clips, turret angles and building states</summary>
    public void SyncParts(double dt, bool advance)
    {
        for (int i = 0; i < Parts.Count; i++)
        {
            var (p, v, host, serial) = Parts[i];
            if (v.Template == null) continue;
            // turrets: the angle about the link (the JS rotation.z; Yaw is its negation)
            if (p.Yaw != 0 || v.Rotation.Z != 0) v.Rotation = new Vector3(0, 0, (float)-p.Yaw);
            if (p.Anim != null && v.Anim != null)
            {
                int s = serial;
                SyncAnim(v, p.Anim, ref s, dt, advance);
                if (s != serial) Parts[i] = (p, v, host, s);
            }
            // (a script's decoration has its own state - the pirate ship's parts on its pad at destruction level 2, 1, 0 -
            // on any host: AttachedPart.OwnState)
            if ((p.Fourcc != "Char" && p.Fourcc != "Anim" && E is Building) || p.OwnState)
                v.SetBuildingState(new BuildingState(p.VisLevel, p.VisDmg, 1, p.Night));
        }
    }

    /// <summary>
    /// The model follows the simulation's clip (AnimCtl): a new start (Serial) plays the clip part with the sim's fade
    /// from the sim's time; afterwards the model advances by the frame's game time and is put back on the sim's time
    /// when it drifts (a model that was off screen or paused).
    /// </summary>
    public static void SyncAnim(ModelInstance m, AnimCtl sim, ref int serial, double dt, bool advance)
    {
        var A = m.Anim;
        var cur = sim.Cur;
        if (A == null || cur == null) return;
        var P = A.Player;
        if (sim.Serial != serial)
        {
            serial = sim.Serial;
            if (!A.Start(cur.Clip, cur.Loop, cur.TimeScale, sim.Fade, sim.Phase)) return;
            Seek(P, cur.Time);
            P.SpeedScale = (float)cur.TimeScale;
            if (!cur.IsRunning) P.Pause();
            return;
        }
        if (!cur.IsRunning)
        {
            // a held pose (a clip played once at its end, a paused clip)
            if (Math.Abs(PwRemake.View.Anim.AnimPos.Position(P) - cur.Time) > 0.02) Seek(P, cur.Time);
            return;
        }
        if (P.SpeedScale != (float)cur.TimeScale) P.SpeedScale = (float)cur.TimeScale;
        if (!advance) return;
        if (!P.IsPlaying() && P.HasAnimation(P.AssignedAnimation)) P.Play(P.AssignedAnimation);
        P.Advance(dt);
        double d = Math.Abs(PwRemake.View.Anim.AnimPos.Position(P) - cur.Time);
        if (cur.Loop && cur.Duration > 0) d = Math.Min(d, cur.Duration - d);
        if (d > 0.25) Seek(P, cur.Time);
    }

    static void Seek(AnimationPlayer p, double t)
    {
        if (!p.HasAnimation(p.AssignedAnimation)) return;
        p.Seek(Math.Clamp(t, 0, Math.Max(0, PwRemake.View.Anim.AnimPos.Length(p))), true);
    }
}
