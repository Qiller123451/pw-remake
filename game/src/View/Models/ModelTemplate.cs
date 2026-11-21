// A prepared model: the converted glTF made ready to draw (materials swapped, helper parts dropped, static parts
// merged, billboards made, clips cut and retargeted) and packed, so every unit, building or prop of that model is a
// cheap instance of it (ModelInstance). One template per model name and load options (ModelLibrary).
//
// Layout of a template's scene (and of every instance):
//   ModelRoot (Node3D, identity)           the JS "scene" of a loaded glTF
//   └─ Model (Node3D)                      the glTF root: turns the GSF frame (Z up, left-handed) into Godot's
//      │                                   (rotation -90° about x, scale x -1: pwexport/gsf.py GSF_MIRROR)
//      ├─ Skeleton3D                       skinned models: bones, skinned meshes, BoneAttachment3D > link_<name>
//      ├─ <meshes> / merged_<n>            rigid parts (static models: merged per material and part state)
//      ├─ <name>_sprites                   billboards of buildings (lanterns, banners: ModelSprites)
//      └─ link_<name>                      link nodes (Ride, flag, we, Bl_0.., Cr_1.., D_01..)
// Animation tracks are relative to ModelRoot ("Model/Skeleton3D:bone_x"), the same for every model, so clips are
// shared between templates (borrowed animations).
//
// Ported from the template part of remake/src/engine/assets.js loadModel (pw-exporter 98c7703).
using System.Collections.Generic;
using System.Text.Json.Nodes;
using Godot;
using PwRemake.View.Parts;

namespace PwRemake.View.Models;

public enum PartKind : byte { Mesh, Sprite, Particles }

/// <summary>A switchable part of a model (three.js kept these in each mesh's userData).</summary>
public sealed class PartRef
{
    /// <summary>Path from ModelRoot.</summary>
    public NodePath Path = new("");
    public PartKind Kind;
    public bool HasAttr;
    /// <summary>The raw GSF attribute bits (parts.js).</summary>
    public uint Attr;
    /// <summary>Static signature (construction level / damage / epoch, PartBits.StaticSig), or -1 = always.</summary>
    public int Sig = -1;
    public TimeOfDay Tod;
    /// <summary>Wall pieces: arm direction 0..7, -1 the hub, null = not a wall piece (assets.js tagWallArms).</summary>
    public int? Arm;
    public int Variant, VCount;
    /// <summary>Wall pieces by name: the component ("3b", "x1").</summary>
    public string? Comp;
    public bool Skinned;
    /// <summary>SEAS paint: this part keeps its own colours (SeasKeepParts - the Black Widow's legs, the exo's flamethrower).</summary>
    public bool SeasKeep;
    /// <summary>Surfaces whose material is party-coloured (GSF flag 0x1000): surface index -> base material.</summary>
    public (int surface, Material mat)[]? PartySurfaces;
    /// <summary>All surface materials (the animal tint of attr bit 5 recolours every surface of such a part).</summary>
    public Material[]? Surfaces;
    /// <summary>A particle emitter node (Kind Particles): its data (null: nothing to draw - no texture).</summary>
    public PwRemake.View.Fx.EmitterDef? Emitter;
    /// <summary>A part that rises out of the ground or sinks into it while its building is built (BuildUp): the top of
    /// its geometry in the model's GSF frame (z up), NaN = no geometry (it takes its group's height)</summary>
    public float Top = float.NaN;
}

public sealed class ModelLoadOptions
{
    /// <summary>Merge all non-moving rigid meshes (props/buildings without animation). null = by kind: everything
    /// except animals, vehicles and characters.</summary>
    public bool? Static;
    /// <summary>A wall piece: arms tagged by direction (wallmap.js).</summary>
    public bool WallArms;
    /// <summary>A gate: leaves modelled standing open are dropped, end pieces kept apart.</summary>
    public bool Gate;
    /// <summary>Make the camera-facing foliage of trees and plants as sprites of the model itself (the model viewer;
    /// the game draws them with a world-wide foliage field instead, props.js FoliageField).</summary>
    public bool TreeSprites;

    public static readonly ModelLoadOptions Default = new();
    public string Key => (Static == true ? "#s" : Static == false ? "#d" : "") + (WallArms ? "#w" : "") + (Gate ? "#g" : "") + (TreeSprites ? "#t" : "");
}

public sealed class ModelTemplate
{
    public string Name = "";
    public ModelInfo Info = new();
    public ModelLoadOptions Options = ModelLoadOptions.Default;
    /// <summary>GSF kind of the model: Bldg, Anim, Char, Vehi, Ship, Vgtn, Deko, Ress, Wall, Misc ...</summary>
    public string Fourcc = "";
    /// <summary>Extras of the glTF root (foliage, sounds, walksets, pf ...).</summary>
    public JsonObject Extras = new();
    /// <summary>The prepared scene (not in the tree; instances are made from Packed).</summary>
    public Node3D Scene = null!;
    public PackedScene Packed = null!;
    public List<PartRef> Parts = new();
    /// <summary>Some parts rise or sink while the building is built (the GSF build-up flags: BuildUp, ModelInstance.BuildUp.cs)</summary>
    public bool HasBuildUp;
    /// <summary>The particle emitters of the model (Parts of kind Particles with data): engine/particles.js sprays them.</summary>
    public List<PartRef> Emitters = new();
    /// <summary>link name (without "link_") -> path from ModelRoot</summary>
    public Dictionary<string, NodePath> Links = new();
    /// <summary>Clips (with the #s / #l / #e parts of start-loop-end clips), or null for a model without animation.</summary>
    public AnimationLibrary? Clips;
    float? _growupScale;
    /// <summary>A newborn animal's size relative to the grown one: the last scale key of the model's "growup" clip
    /// (animals.js growupScale; 0.5 when the model has none). A nest's young is drawn at
    /// 1 - babyness * (1 - this) (Unit.Babyness, Animals.cs GrowthUpdate).</summary>
    public float GrowupScale
    {
        get
        {
            if (_growupScale is float g) return g;
            g = 0.5f;
            if (Clips != null && ClipNames.TryGetValue("growup", out var n) && Clips.GetAnimation(n) is Animation a)
                for (int i = 0; i < a.GetTrackCount(); i++)
                {
                    if (a.TrackGetType(i) != Animation.TrackType.Scale3D) continue;
                    int k = a.TrackGetKeyCount(i);
                    if (k == 0) continue;
                    var v = (Vector3)a.TrackGetKeyValue(i, k - 1);
                    g = Mathf.Clamp(v.X, 0.1f, 1f);
                    break;
                }
            _growupScale = g;
            return g;
        }
    }
    /// <summary>Loop marks of the clips (from the model or the one it borrows from).</summary>
    public Dictionary<string, (double t0, double t1)>? Loops;
    /// <summary>Sound events of the clips (the model's, or the animation source's).</summary>
    public JsonObject? Sounds;
    /// <summary>The selection / pick volume (attribute bit 18 parts), in ModelRoot space; null = none.</summary>
    public Aabb? Pick;
    /// <summary>Box of all meshes in ModelRoot space (three's Box3.setFromObject: bind pose, hidden parts included).</summary>
    public Aabb Bounds;
    public bool HasSkeleton;
    /// <summary>Milliseconds the template took to load and prepare (performance notes).</summary>
    public double LoadMs;
    /// <summary>Where the load time went (ms per step).</summary>
    public string LoadSteps = "";
    public int MeshCount, SurfaceCount;

    /// <summary>Clip names by lower-case name (the JS looked clips up ignoring case); made once per template.</summary>
    public Dictionary<string, string> ClipNames = new();
    /// <summary>Loop marks by lower-case clip name.</summary>
    public Dictionary<string, (double t0, double t1)> LoopsLower = new();
    public bool HasClips => ClipNames.Count > 0;
    public bool IsDynamic => PartBits.IsDynamicKind(Fourcc);

    // entities.js buildModel: collision radius and height from the template size
    public float Radius => Mathf.Max(0.45f, Mathf.Min(Mathf.Min(Bounds.Size.X, Bounds.Size.Z) * 0.42f, 6f));
    public float Height => Mathf.Max(1.5f, Bounds.Size.Y);
}
