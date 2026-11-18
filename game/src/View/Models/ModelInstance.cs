// One drawn model: a unit, building, prop, attached part. A Node3D the game positions and turns (the model's own
// frame turn and mirror are inside, in the "Model" child), made from a ModelTemplate (ModelLibrary.Instantiate).
//
// What the simulation's per-entity view state drives, as plain data:
//   model name                         -> ModelLibrary.Instantiate(name, party)
//   animation clip / start / speed / loop -> SyncAnimation(clip, loop, speed, startTime, now) (or Play(...))
//   part state                         -> SetBuildingState(level, damage, epoch, night) / SetAnimalLook(...) /
//                                         SetHideMask(mask) / SetVisFlag(bit, on) / SetNight(night)
//   party colour                       -> SetPartyColor(colour) (+ SetAnimalTint for owned animals and vehicles)
//   seen through buildings             -> SetBehind(role, colour) (UnitsBehind)
//   the setting textures (buildings)   -> SetSetting(setting) (SettingMaterials: the biome where it stands)
//   attached parts                     -> Attach(gfx, link) / Composite (riders, turrets, build-ups, flags)
// and gives back what the simulation asked three.js for: link positions (LinkWorld), clip lengths (Anim.Duration).
//
// Ported from remake/src/engine/assets.js (cloneModel, toLinkFrame, tintParty), remake/src/game/entities.js
// (findLink, linkWorld, setVisFlag, attachWeapon/attachTool) and remake/src/game/compose.js (attach, linksOf)
// (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.View.Anim;
using PwRemake.View.Materials;
using PwRemake.View.Parts;

namespace PwRemake.View.Models;

public partial class ModelInstance : Node3D
{
    public ModelLibrary Library { get; }
    public ModelTemplate Template { get; }
    /// <summary>The instanced template scene (ModelRoot): identity under this node.</summary>
    public Node3D Root { get; }
    /// <summary>The glTF root: GSF frame -> Godot (turned and mirrored).</summary>
    public Node3D Model { get; }
    /// <summary>null for a model without clips</summary>
    public AnimController? Anim { get; }
    (Node3D node, PartRef part)[]? _parts;
    Dictionary<string, Node3D>? _links;
    readonly List<ModelInstance> _attached = new();
    Color? _party, _animalTint;
    int _tintEpoch = ModelMaterials.TintEpoch;
    BehindRole _behind; Color _behindCol; int _behindEpoch = UnitsBehind.Epoch;
    string? _setting; int _settingEpoch = SettingMaterials.Epoch;
    BuildingState _state = new();
    bool _stateSet;
    uint? _hide;
    /// <summary>Its particle emitters spray (false: a placement preview, a warm-up copy - engine/particles.js untrack).</summary>
    public bool Sprays = true;
    /// <summary>The colour this instance's party materials are tinted with (null = untinted).</summary>
    public Color? Party => _party;
    public BuildingState State => _state;

    /// <summary>(for Godot only; use ModelLibrary.Instantiate)</summary>
    public ModelInstance() { Library = null!; Template = null!; Root = null!; Model = null!; }

    internal ModelInstance(ModelLibrary lib, ModelTemplate tpl)
    {
        Library = lib; Template = tpl;
        Name = tpl.Name;
        Root = tpl.Packed.Instantiate<Node3D>();
        AddChild(Root);
        Model = Root.GetNode<Node3D>("Model");
        if (tpl.HasClips)
        {
            Anim = new AnimController(Root, tpl.Clips!, tpl.ClipNames, tpl.LoopsLower);
            // (three.js culled skinned meshes by their bind box, so the remake switched culling off for them; Godot
            // culls a skinned mesh by the box of its posed bones)
        }
    }

    /// <summary>The model's switchable parts, resolved on first use (an instance that never switches pays nothing).</summary>
    public IReadOnlyList<(Node3D node, PartRef part)> Parts
    {
        get
        {
            if (_parts == null)
            {
                var l = new List<(Node3D, PartRef)>(Template.Parts.Count);
                foreach (var p in Template.Parts) if (Root.GetNodeOrNull<Node3D>(p.Path) is Node3D n) l.Add((n, p));
                _parts = l.ToArray();
            }
            return _parts;
        }
    }

    // ------------------------------------------------------------------------------------------- part states

    /// <summary>Building / ship / prop look: construction level 0..3 (4 finished), damage 0..2, epoch 1..5, night.</summary>
    public void SetBuildingState(int level, int damage, int epoch, bool? night = null)
    {
        var s = _state;
        s.Level = level; s.Damage = damage; s.Epoch = epoch;
        if (night != null) s.Night = night.Value;
        SetBuildingState(s);
    }

    /// <summary>The full building state (wall arms, variants, gate ends too). Applied only when it changed.</summary>
    public void SetBuildingState(in BuildingState s)
    {
        if (_stateSet && _state.SameLook(s)) return;
        _state = s; _stateSet = true;
        PartStates.ApplyState(Parts, s);
        // attached parts of buildings (turrets, cranes) follow the building's state, in the first epoch (entities.js updateVisual)
        foreach (var a in _attached)
            if (a.Template.Fourcc != "Char" && a.Template.Fourcc != "Anim") a.SetBuildingState(new BuildingState(s.Level, s.Damage, 1, s.Night));
    }

    /// <summary>switch a building's or ship's model between day and night (its lights); true when it changed</summary>
    public bool SetNight(bool night)
    {
        if (_state.Night == night && _stateSet) return false;
        var s = _state; s.Night = night;
        SetBuildingState(s);
        return true;
    }

    /// <summary>Hide mask of an animal / vehicle / character (PartBits.AnimalMask, or 1 &lt;&lt; 16 for characters).</summary>
    public void SetHideMask(uint hide)
    {
        if (_hide == hide) return;
        _hide = hide;
        PartStates.ApplyMask(Parts, hide);
    }

    /// <summary>What an animal / vehicle wears: saddle, gear, wounds (compose.js updateMask).</summary>
    public void SetAnimalLook(in AnimalLook look) => SetHideMask(PartBits.AnimalMask(look));

    /// <summary>script visibility flag (SetRndInvMaskSingleFlagInv) on the model's own parts (the medic's kit: bit 16)</summary>
    public void SetVisFlag(int bit, bool on) { _hide = null; PartStates.SetVisFlag(Parts, bit, on); }

    // ------------------------------------------------------------------------------------------- colours

    /// <summary>
    /// Party colour: party-colour materials (GSF flag 0x1000) tinted with the owner's colour, SEAS paint recoloured
    /// (ModelMaterials.SeasPainted); null = untinted. Called again with the same colour it re-applies the tints when
    /// the "Original colours" option changed since (ModelMaterials.TintEpoch).
    /// </summary>
    public void SetPartyColor(Color? party)
    {
        if (_party == party && _tintEpoch == ModelMaterials.TintEpoch) return;
        _party = party;
        _tintEpoch = ModelMaterials.TintEpoch;
        ApplyTints();
        foreach (var a in _attached) a.SetPartyColor(party);
    }

    /// <summary>
    /// An owned animal's or vehicle's party parts (attr bit 5: harness, cloth): their materials times the player's
    /// colour times 1.6 (compose.js updateMask). null = untinted (wild).
    /// </summary>
    public void SetAnimalTint(Color? party)
    {
        if (_animalTint == party) return;
        _animalTint = party;
        ApplyTints();
    }

    /// <summary>
    /// "Show units behind buildings" (UnitsBehind): a building's model marks where it covers, a unit's shows through
    /// there in col (its ring colour). Its attached parts follow (a building's riders and passengers do not cover).
    /// Called again with the same values it re-applies the materials when the option changed (UnitsBehind.Epoch).
    /// </summary>
    public void SetBehind(BehindRole role, Color col)
    {
        if (_behind == role && _behindCol == col && _behindEpoch == UnitsBehind.Epoch) return;
        _behind = role; _behindCol = col; _behindEpoch = UnitsBehind.Epoch;
        ApplyTints();
        foreach (var a in _attached) a.SetBehind(ChildRole(a), col);
    }

    /// <summary>The setting whose textures this model's building atlases take (lower case: "icewaste"; null: plain).</summary>
    public string? Setting => _setting;

    /// <summary>
    /// The original's setting textures (SettingMaterials): the surfaces whose texture has a variant in the setting draw
    /// with it ("icewaste": the snowy atlases). For buildings, walls, built objects and the map's decoration - not units.
    /// The attached parts follow (cranes, turrets; not riders or animals). Called again with the same setting it
    /// re-applies the materials when the option changed (SettingMaterials.Epoch); cheap otherwise.
    /// </summary>
    public void SetSetting(string? setting)
    {
        if (_setting == setting && _settingEpoch == SettingMaterials.Epoch) return;
        _setting = setting; _settingEpoch = SettingMaterials.Epoch;
        ApplyTints();
        foreach (var a in _attached) if (TakesSetting(a)) a.SetSetting(setting);
    }

    static bool TakesSetting(ModelInstance a) => a.Template.Fourcc is not ("Char" or "Anim" or "Vehi");

    BehindRole ChildRole(ModelInstance a) =>
        _behind == BehindRole.Building && a.Template.Fourcc is "Char" or "Anim" ? BehindRole.None : _behind;

    void ApplyTints()
    {
        // the "Original colours" option: no player colour on any model (the models' own colours)
        Color? partyCol = ModelMaterials.OriginalColours ? null : _party, animalCol = ModelMaterials.OriginalColours ? null : _animalTint;
        foreach (var (n, p) in Parts)
        {
            if (n is not MeshInstance3D mi || p.Surfaces == null) continue;
            bool animal = animalCol != null && p.HasAttr && ((p.Attr >> PartBits.PARTYCOL) & 1) != 0;
            for (int s = 0; s < p.Surfaces.Length; s++)
            {
                Material m0 = p.Surfaces[s];
                // the setting's texture first (SettingMaterials): the colours are laid on the variant
                Material m = _setting != null ? SettingMaterials.Variant(m0, _setting) : m0;
                bool party = false;
                // party materials (GSF flag 0x1000): texture × colour × 2; unowned and "Original colours": white × 2
                // (their textures are painted at half brightness: ModelMaterials.PartyTinted)
                if (p.PartySurfaces != null)
                    foreach (var (ps, pm) in p.PartySurfaces) if (ps == s) { m = ModelMaterials.PartyTinted(_setting != null ? SettingMaterials.Variant(pm, _setting) : pm, partyCol ?? ModelMaterials.PARTY_NEUTRAL); party = true; break; }
                // SEAS: their red paint in the owner's colour (the SEAS models have few party-colour materials)
                // (not on a part that keeps its colours: SeasKeepParts)
                if (!party && partyCol != null && !p.SeasKeep && ModelMaterials.Tag(m)?.Paint == true)
                {
                    var pm = ModelMaterials.SeasPainted(m, partyCol.Value);
                    party = pm != m; m = pm;
                }
                // (not on a party material: it already has the colour × 2; was colour² × 1.6 on the harness of
                // dilophosaurus, gallimimus, iguanodon, mammoth, megaloceros ...)
                if (animal && !party) m = ModelMaterials.AnimalTinted(m, animalCol!.Value);
                var mb = UnitsBehind.Apply(m, _behind, _behindCol);
                mi.SetSurfaceOverrideMaterial(s, party || animal || mb != m0 ? mb : null);
            }
        }
    }

    // ------------------------------------------------------------------------------------------- links & parts

    /// <summary>The link node "link_&lt;name&gt;" (Ride, flag, we, con, Dri1, Bl_0, Cr_1, D_01, Proj, HndR ...).</summary>
    public Node3D? FindLink(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        if (_links == null)
        {
            _links = new Dictionary<string, Node3D>();
            foreach (var (k, p) in Template.Links) if (Root.GetNodeOrNull<Node3D>(p) is Node3D n) _links[k] = n;
        }
        return _links.GetValueOrDefault(name);
    }

    public IEnumerable<string> LinkNames => Template.Links.Keys;

    static readonly HashSet<string> _missingLogged = new();
    /// <summary>
    /// A link for a part the classes hang on this model, or - when the model has no link of that name - the model's
    /// own origin in its GSF frame (the "Model" node: a part hung there sits as on a link at the origin). The toolkit's
    /// scene export does the same (pwexport/scene.py: "no such link: hang it on the parent's root"). The one case in
    /// the data: MIRAGE's gallimimus nest (aje_gallimimus_nest, model ninigi_dilophosaurus_nest) gets its eggs
    /// nest_birds at "Nest" with the offset {0, 0, -0.1} (AddObjCustomized), a link the model does not have - placed by
    /// its offset at the origin, lowered 10 cm, as the mod's author set it up. Composites tests (ViewPartsTests) list
    /// every composite whose link its model lacks.
    /// Only static models (buildings, decorations): on an animal or a vehicle the origin is the ground under its belly,
    /// not a place for a part - MIRAGE's level flags at "flag" on models without that link (muraenosaurus,
    /// saltasaurus, tarbosaurus, gigantosaurus, the scorpion and pirate boss, the SEAS spider, the cannon turtle) stay
    /// undrawn, as before.
    /// </summary>
    public Node3D? LinkOrOrigin(string? name, string? gfx = null)
    {
        var l = FindLink(name);
        if (l != null || string.IsNullOrEmpty(name) || Template == null) return l;
        // (MIRAGE's pilot_lvl4: the floating harbour's own model on itself - not a second copy)
        bool origin = !Template.IsDynamic && !string.Equals(gfx, Template.Name, StringComparison.OrdinalIgnoreCase);
        lock (_missingLogged)
            if (_missingLogged.Add(Template.Name + "/" + name))
                PwRemake.Sim.Core.Log.For("View.Models").Info($"{Template.Name} has no link \"{name}\": " + (origin ? "the part hangs at the model's origin" : "the part is not drawn"));
        return origin ? Model : null;
    }

    /// <summary>A link's position in the world (the instance must be in the scene tree; the pose of its skeleton now).</summary>
    public Vector3? LinkWorld(string name) => FindLink(name)?.GlobalPosition;

    /// <summary>A link's position in this instance's frame (the frame the game positions), without the scene tree.</summary>
    public Vector3? LinkLocal(string name)
    {
        var l = FindLink(name);
        if (l == null) return null;
        return ModelLibrary.ToRoot(l, this).Origin;
    }

    /// <summary>
    /// A model hung on a link of another: the link frame is already the parent's GSF frame (Z up, mirrored), so the
    /// model's own root turn and mirror go (assets.js toLinkFrame).
    /// </summary>
    public void ToLinkFrame() => Model.Transform = new Transform3D(Basis.Identity, Model.Position);

    /// <summary>
    /// Attach a model to a link node (compose.js attach; weapons, tools, carried goods: entities.js attachWeapon ...).
    /// The part gets this model's party colour unless one is given; it is advanced with this model (Advance).
    /// originIfMissing: a composite's part (LinkOrOrigin) - hung at the model's origin when the link is missing.
    /// </summary>
    public ModelInstance? Attach(string? gfx, string? link, Color? party = null, bool originIfMissing = false)
    {
        var l = originIfMissing ? LinkOrOrigin(link, gfx) : FindLink(link);
        if (l == null || string.IsNullOrEmpty(gfx)) return null;
        var tpl = Library.Template(gfx, null, true);
        if (tpl == null) return null;
        var obj = Library.Instantiate(tpl, party ?? _party);
        if (_behind != BehindRole.None) obj.SetBehind(ChildRole(obj), _behindCol);
        if (_setting != null && TakesSetting(obj)) obj.SetSetting(_setting);
        obj.ToLinkFrame();
        l.AddChild(obj);
        _attached.Add(obj);
        return obj;
    }

    /// <summary>Attach to a node of this model (a link of an attached part, a seat ...).</summary>
    public ModelInstance? AttachTo(Node3D link, string gfx, Color? party = null)
    {
        var tpl = Library.Template(gfx, null, true);
        if (tpl == null) return null;
        var obj = Library.Instantiate(tpl, party ?? _party);
        if (_behind != BehindRole.None) obj.SetBehind(ChildRole(obj), _behindCol);
        if (_setting != null && TakesSetting(obj)) obj.SetSetting(_setting);
        obj.ToLinkFrame();
        link.AddChild(obj);
        _attached.Add(obj);
        return obj;
    }

    /// <summary>A model at this one's origin, in its turn (not on a link: the part keeps its own frame turn and mirror) -
    /// a beheaded corpse's head (EntityView.SyncHead).</summary>
    public ModelInstance? AttachAtOrigin(string gfx, Color? party = null)
    {
        var tpl = Library.Template(gfx, null, true);
        if (tpl == null) return null;
        var obj = Library.Instantiate(tpl, party ?? _party);
        if (_behind != BehindRole.None) obj.SetBehind(ChildRole(obj), _behindCol);
        AddChild(obj);
        _attached.Add(obj);
        return obj;
    }

    public void Detach(ModelInstance part)
    {
        _attached.Remove(part);
        part.GetParent()?.RemoveChild(part);
        part.QueueFree();
    }

    public IReadOnlyList<ModelInstance> Attached => _attached;

    // ------------------------------------------------------------------------------------------- animation

    /// <summary>Play a clip (AnimController.Play); false if the model has no such clip.</summary>
    public bool Play(string? clip, bool loop = true, double speed = 1, double? startOffset = null, double? fade = null) =>
        Anim != null && Anim.Play(clip, new PlayOptions { Loop = loop, Ts = speed, StartAt = startOffset, Fade = fade });

    /// <summary>
    /// Animation sound events of the model (its GSF sound table, or the one of the model it borrows its clips from:
    /// footsteps, weapon swings, roars) - called as the clips pass them (entities.js buildModel setEvents).
    /// </summary>
    public void OnAnimEvents(Action<AnimEvent> cb) => Anim?.SetEvents(Template.Sounds, cb);

    string? _syncClip; double _syncStart = double.NaN;

    /// <summary>
    /// Follow the simulation's animation state: clip, loop, speed and the game time the clip started. A new clip or a
    /// new start plays it; a view made late (or a clip started earlier) begins at the right time of a clip played once.
    /// </summary>
    public void SyncAnimation(string? clip, bool loop, double speed, double startTime, double now)
    {
        if (Anim == null || string.IsNullOrEmpty(clip)) return;
        if (clip == _syncClip && startTime == _syncStart) { if (Anim.TimeScale != speed) Anim.SetTimeScale(speed); return; }
        bool restart = clip == _syncClip;
        bool first = _syncClip == null;
        _syncClip = clip; _syncStart = startTime;
        double late = (now - startTime) * speed;
        double? at = !loop && late > 0.05 ? late : null;
        Anim.Play(clip, new PlayOptions { Loop = loop, Ts = speed, Restart = restart, StartAt = at, Fade = first ? 0 : null });
    }

    /// <summary>Advance the animation of this model and its attached parts (the game's frame time × game speed).</summary>
    public void Advance(double dt)
    {
        Anim?.Update(dt);
        foreach (var a in _attached) a.Advance(dt);
    }
}
