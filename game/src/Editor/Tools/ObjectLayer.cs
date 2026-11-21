// The map editor's objects drawn on the map (docs/MAP_EDITOR.md "Objects"): every object of the document - units,
// buildings, animals, nests, resources, decoration, items, start locations - as the game's own model (ModelLibrary: the
// class's model of its level, the owner's party colour, a standing clip, trees with their leaves, buildings in their
// biome's setting textures), where the map puts it (MapSource's rules: decoration, wells and effects at their stored
// height and with their whole quaternion - tilted plateaus and cliffs -, the rest on the ground, turned by the map's
// heading). Objects without a model (start locations, item spawns, the engine's helpers) are markers: a post with a
// flag in the player's colour and a label. The layer follows the document (Sync: what was added, removed or changed),
// draws the selection and the hovered object (rings on the ground) and the ghost of the class being placed, and picks
// objects under the mouse (a vertical cylinder per object: its model's size).
//
// Wall pieces, gates, towers and traps go through a wall map of their own (WallMap.Detached: the game's rules on the
// document's objects and the editor's ground): every wall piece shows the parts of its connections (WallMap.PartsOf -
// not every arm at once), a gate its end pieces only when diagonal, all of them at their tile's height (WallMap.ZOf), as
// in the game. The ghost of a wall class counts too: the pieces next to it show the joins it will make.
//
// The world view (WorldView) draws the map without its objects (IEditorMapDoc.ToViewSource): terrain, water, the forest
// blocks, plants and grass - so every object here can be edited.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Maps;
using PwRemake.View.Models;
using PwRemake.View.Parts;
using WallMap = PwRemake.Sim.World.WallMap;
using PwRemake.View.World;

namespace PwRemake.Editor;

public partial class ObjectLayer : Node3D
{
    sealed class ObjView
    {
        public Node3D Node = null!;
        public string Key = "";
        public double R = 1, H = 2;
        public ModelInstance? Model;
    }

    readonly WorldView _view;
    readonly MapSource _src;
    readonly Func<string, int, string?> _classModel;
    readonly Dictionary<MapObject, ObjView> _views = new();
    readonly Dictionary<string, (double R, double H)> _sizes = new(StringComparer.Ordinal);
    readonly MeshInstance3D _rings;
    readonly ImmediateMesh _ringMesh = new();
    ObjView? _ghost;
    string? _ghostKey;
    MapObject? _ghostObj;
    /// <summary>the models of wall pieces and gates (loaded with their parts tagged) and the wall class table</summary>
    HashSet<string>? _wallGfx, _gateGfx;
    Dictionary<string, int[]>? _wallClasses;
    /// <summary>a player slot's colour (null: the palette's); set by the editor from the map's players</summary>
    public Func<int, Color>? SlotColor;

    public ObjectLayer(WorldView view, MapSource src, Func<string, int, string?> classModel)
    {
        Name = "EditorObjects";
        _view = view; _src = src; _classModel = classModel;
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, VertexColorUseAsAlbedo = true, NoDepthTest = true,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha, RenderPriority = 10,
        };
        _rings = new MeshInstance3D { Name = "Rings", Mesh = _ringMesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(_rings);
    }

    public int Count => _views.Count;
    public bool Has(MapObject o) => _views.ContainsKey(o);
    /// <summary>the drawn node of an object (null: none)</summary>
    public Node3D? NodeOf(MapObject o) => _views.TryGetValue(o, out var v) ? v.Node : null;
    public (double R, double H) SizeOf(MapObject o) => _views.TryGetValue(o, out var v) ? (v.R, v.H) : (1, 2);

    // ---------------------------------------------------------------- what an object looks like

    /// <summary>the model an object is drawn with (null: a marker)</summary>
    public string? ModelOf(MapObject o)
    {
        if (o.Type is "SLOC" or "ITSP" or "QMRK" or "DMGL") return o.Type == "ITSP" ? HasModel("item_treasure_chest") : null;
        if (o.Type == "ITEM" || o.Type == "DOFL") return HasModel(o.Type == "ITEM" ? "item_treasure_chest" : _classModel(o.Cls, 1) ?? "domination_flag");
        return HasModel(_classModel(o.Cls, o.Level + 1));
    }
    string? HasModel(string? m) => m != null && _view.Library.Has(m.ToLowerInvariant()) ? m.ToLowerInvariant() : null;

    /// <summary>objects drawn at their stored height with their whole quaternion (MapSource: decoration, wells, effects,
    /// the scenery of unknown classes)</summary>
    static bool Stored(MapObject o) => o.Type is "DCCO" or "DECO" or "VGTN" or "WOOD" or "CFXE" or "FNTN";

    string KeyOf(MapObject o) => $"{o.Type}|{o.Cls}|{o.Level}|{o.Owner}";

    Color? PartyOf(MapObject o)
    {
        if (o.Owner is not int ow || ow < 0) return null;
        if (SlotColor != null) return SlotColor(ow);
        return PwRemake.View.Materials.PartyColors.All[ow % PwRemake.View.Materials.PartyColors.All.Length].Light;
    }

    ObjView Make(MapObject o, bool ghost = false)
    {
        var v = new ObjView { Key = KeyOf(o) };
        string? model = ModelOf(o);
        ModelInstance? m = null;
        if (model != null)
        {
            WallTables();
            var opts = PwRemake.View.Entities.EntityViews.OptionsOf(model, _wallGfx!, _gateGfx!);
            var tpl = _view.Library.Template(model, ReferenceEquals(opts, ModelLoadOptions.Default) ? null : opts, true);
            if (tpl != null && PwRemake.View.Entities.EntityViews.NeedsTreeSprites(tpl.Fourcc, tpl.Extras))
                tpl = _view.Library.Template(model, new ModelLoadOptions { TreeSprites = true }, true) ?? tpl;
            if (tpl != null) m = _view.Library.Instantiate(tpl, PartyOf(o));
        }
        if (m != null)
        {
            var clip = m.Anim?.Pick("standanim", "stand_anim", "idle", "standing", "stand");
            if (clip != null) m.Play(clip, true);
            v.Model = m; v.Node = m;
            if (!_sizes.TryGetValue(model!, out var sz))
            {
                var b = m.Template.Bounds;
                sz = (Math.Clamp(Math.Max(b.Size.X, b.Size.Z) / 2, 0.6, 40), Math.Clamp(b.End.Y, 1, 60));
                _sizes[model!] = sz;
            }
            (v.R, v.H) = sz;
        }
        else
        {
            v.Node = Marker(o);
            v.R = 1.5; v.H = 5;
        }
        v.Node.Name = ghost ? "Ghost" : o.Cls;
        Place(v, o);
        AddChild(v.Node);
        return v;
    }

    /// <summary>a marker: a post with a flag in the player's colour and the object's label</summary>
    Node3D Marker(MapObject o)
    {
        var root = new Node3D();
        var col = PartyOf(o) ?? (o.Type == "SLOC" ? new Color(1f, 0.85f, 0.2f) : new Color(0.85f, 0.85f, 0.9f));
        var mat = new StandardMaterial3D { AlbedoColor = col, Roughness = 0.8f };
        var post = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 0.12f, BottomRadius = 0.15f, Height = 5 }, Position = new Vector3(0, 2.5f, 0), MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.35f, 0.25f, 0.15f) } };
        var flag = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(1.6f, 1.0f, 0.06f) }, Position = new Vector3(0.85f, 4.4f, 0), MaterialOverride = mat };
        var disc = new MeshInstance3D { Mesh = new CylinderMesh { TopRadius = 1.4f, BottomRadius = 1.4f, Height = 0.08f }, Position = new Vector3(0, 0.05f, 0), MaterialOverride = mat };
        root.AddChild(post); root.AddChild(flag); root.AddChild(disc);
        string text = o.Type switch
        {
            "SLOC" => $"Start {(o.Owner ?? 0) + 1}", "ITSP" => "Item spawn", "DOFL" => "Flag", _ => o.Name,
        };
        root.AddChild(new Label3D
        {
            Text = text, Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, Position = new Vector3(0, 6.2f, 0), FontSize = 64, PixelSize = 0.02f,
            Modulate = col, OutlineSize = 12, NoDepthTest = true,
        });
        return root;
    }

    void Place(ObjView v, MapObject o)
    {
        var (x, z) = _src.ToGame(o.X, o.Y);
        double y = Stored(o) ? o.Z : o.Cls.StartsWith("fishshoal", StringComparison.OrdinalIgnoreCase) && _src.Water is double w ? w - 0.3 : _view.Ground.At(x, z);
        v.Node.Position = new Vector3((float)x, (float)y, (float)z);
        if (Stored(o) && ObjectOps.Tilted(o))
        {
            var q = Frame.MapQuat(o.Q);
            v.Node.Quaternion = new Quaternion((float)q[0], (float)q[1], (float)q[2], (float)q[3]).Normalized();
        }
        else v.Node.Rotation = new Vector3(0, (float)Frame.MapHeading(o.Rot), 0);
        if (v.Model != null && !v.Model.Template.IsDynamic) v.Model.SetSetting(_view.SettingAt(x, z));
    }

    // ---------------------------------------------------------------- following the document

    /// <summary>the document's objects now: new ones made, changed ones moved (or made again: another class, level or
    /// owner), removed ones gone</summary>
    public void Sync(IReadOnlyList<MapObject> objs)
    {
        var live = new HashSet<MapObject>(objs);
        foreach (var o in _views.Keys.Where(o => !live.Contains(o)).ToList()) { _views[o].Node.QueueFree(); _views.Remove(o); }
        foreach (var o in objs)
        {
            if (_views.TryGetValue(o, out var v))
            {
                if (v.Key == KeyOf(o)) { Place(v, o); continue; }
                v.Node.QueueFree();
            }
            _views[o] = Make(o);
        }
        UpdateWalls();
    }

    /// <summary>the objects within the map rectangle drawn at their place again (the ground under them changed)</summary>
    public void PlaceIn(IReadOnlyList<MapObject> objs, double x0, double y0, double x1, double y1)
    {
        foreach (var o in objs)
            if (o.X >= x0 && o.X <= x1 && o.Y >= y0 && o.Y <= y1 && _views.TryGetValue(o, out var v)) Place(v, o);
        UpdateWalls();
    }

    /// <summary>everything made again (the players' colours changed)</summary>
    public void Rebuild(IReadOnlyList<MapObject> objs)
    {
        foreach (var v in _views.Values) v.Node.QueueFree();
        _views.Clear();
        Sync(objs);
    }

    public void Advance(double dt)
    {
        foreach (var v in _views.Values) v.Model?.Advance(dt);
        _ghost?.Model?.Advance(dt);
    }

    // ---------------------------------------------------------------- the ghost of the class being placed

    /// <summary>shows the object that a click would place (null: none)</summary>
    public void Ghost(MapObject? o)
    {
        bool wasWall = _ghostObj != null && IsWall(_ghostObj);
        if (o == null)
        {
            if (_ghost != null) { _ghost.Node.QueueFree(); _ghost = null; _ghostKey = null; }
            _ghostObj = null;
            if (wasWall) UpdateWalls();
            return;
        }
        string key = KeyOf(o);
        if (_ghost == null || _ghostKey != key)
        {
            _ghost?.Node.QueueFree();
            _ghost = Make(o, ghost: true);
            _ghostKey = key;
        }
        else Place(_ghost, o);
        _ghostObj = o;
        if (wasWall || IsWall(o)) UpdateWalls();
    }
    public (double R, double H) GhostSize => _ghost != null ? (_ghost.R, _ghost.H) : (1, 2);

    // ---------------------------------------------------------------- walls

    void WallTables()
    {
        if (_wallClasses != null) return;
        var R = EditorSession.Rules;
        (_wallGfx, _gateGfx) = PwRemake.View.Entities.EntityViews.WallModels(R);
        _wallClasses = WallMap.ClassTable(R);
    }

    bool IsWall(MapObject o) { WallTables(); return _wallClasses!.ContainsKey(o.Cls.ToLowerInvariant()); }

    /// <summary>the wall map of the document's wall-map objects (and the ghost): each wall piece gets the parts of its
    /// connections, each gate its end pieces, each of them its tile's height - what the game's WallMap gives them</summary>
    void UpdateWalls()
    {
        WallTables();
        if (_wallClasses!.Count == 0) return;
        var wm = WallMap.Detached(_src.WallGrid, _wallClasses, (x, z) => _view.Ground.At(x, z), _src.Water, _src.Kind);
        var put = new List<(ObjView V, int I, int J, int Type, int D)>();
        void Put(MapObject o, ObjView v)
        {
            if (v.Model == null || !_wallClasses.ContainsKey(o.Cls.ToLowerInvariant())) return;
            var (x, z) = _src.ToGame(o.X, o.Y);
            // (the owner as the wall map keeps it: pieces of different owners never join; none = 15)
            if (wm.Put(o.Cls, o.Owner is int ow && ow >= 0 ? ow : 15, x, z, Frame.MapHeading(o.Rot)) is { } t) put.Add((v, t.I, t.J, t.Type, t.D));
        }
        foreach (var (o, v) in _views) Put(o, v);
        if (_ghost != null && _ghostObj != null) Put(_ghostObj, _ghost);
        foreach (var (v, i, j, type, d) in put)
        {
            if (type == WallMap.WT.TRAP) continue;                 // (traps lie on the ground and have no arms)
            var st = new BuildingState(4, 0, 1);
            if (type == WallMap.WT.WALL) { st.WallParts = new HashSet<string>(wm.PartsOf(i, j)); st.ArmMask = wm.Mask(i, j); }
            else if (type == WallMap.WT.GATE) st.GateEnds = d >= 0 && d % 2 == 0;
            if (type != WallMap.WT.TOWER) v.Model!.SetBuildingState(st);
            var p = v.Node.Position;
            v.Node.Position = new Vector3(p.X, (float)wm.ZOf(i, j), p.Z);
        }
    }

    // ---------------------------------------------------------------- picking

    /// <summary>the object under a ray (from, dir: world): its model's cylinder the ray meets first; null: none</summary>
    public MapObject? Pick(Vector3 from, Vector3 dir)
    {
        MapObject? best = null;
        double bt = double.MaxValue;
        foreach (var (o, v) in _views)
        {
            var p = v.Node.Position;
            // the closest approach of the ray to the object's vertical axis (p.y .. p.y + h)
            double dx = dir.X, dz = dir.Z, ox = from.X - p.X, oz = from.Z - p.Z;
            double a = dx * dx + dz * dz;
            double t = a > 1e-9 ? -(ox * dx + oz * dz) / a : 0;
            if (t < 0) continue;
            double cx = ox + dx * t, cz = oz + dz * t, y = from.Y + dir.Y * t;
            double r = Math.Max(v.R * 0.8, 0.9);
            if (cx * cx + cz * cz > r * r) continue;
            if (y < p.Y - 1 || y > p.Y + v.H + 1) continue;
            if (t < bt) { bt = t; best = o; }
        }
        return best;
    }

    /// <summary>the objects whose place is inside a rectangle of the screen</summary>
    public List<MapObject> InRect(Camera3D cam, Rect2 r)
    {
        var outp = new List<MapObject>();
        foreach (var (o, v) in _views)
        {
            var p = v.Node.Position + new Vector3(0, 0.5f, 0);
            if (cam.IsPositionBehind(p)) continue;
            if (r.HasPoint(cam.UnprojectPosition(p))) outp.Add(o);
        }
        return outp;
    }

    // ---------------------------------------------------------------- rings on the ground

    /// <summary>draws the rings: the selected objects (gold), the hovered one (white), the ghost (green)</summary>
    public void DrawRings(IEnumerable<MapObject> selected, MapObject? hover, Vector3? ghostAt)
    {
        _ringMesh.ClearSurfaces();
        bool any = false;
        void Ring(Vector3 c, double r, Color col)
        {
            if (!any) { _ringMesh.SurfaceBegin(Mesh.PrimitiveType.Lines); any = true; }
            const int N = 40;
            for (int k = 0; k < N; k++)
            {
                double a0 = Math.Tau * k / N, a1 = Math.Tau * (k + 1) / N;
                float x0 = c.X + (float)(Math.Cos(a0) * r), z0 = c.Z + (float)(Math.Sin(a0) * r), x1 = c.X + (float)(Math.Cos(a1) * r), z1 = c.Z + (float)(Math.Sin(a1) * r);
                _ringMesh.SurfaceSetColor(col); _ringMesh.SurfaceAddVertex(new Vector3(x0, (float)_view.Ground.At(x0, z0) + 0.15f, z0));
                _ringMesh.SurfaceSetColor(col); _ringMesh.SurfaceAddVertex(new Vector3(x1, (float)_view.Ground.At(x1, z1) + 0.15f, z1));
            }
        }
        foreach (var o in selected)
            if (_views.TryGetValue(o, out var v)) { Ring(v.Node.Position, v.R + 0.4, new Color(1f, 0.82f, 0.25f)); Ring(v.Node.Position, v.R + 0.55, new Color(1f, 0.82f, 0.25f, 0.6f)); }
        if (hover != null && _views.TryGetValue(hover, out var hv)) Ring(hv.Node.Position, hv.R + 0.4, new Color(1, 1, 1, 0.8f));
        if (ghostAt is Vector3 g && _ghost != null) Ring(g, _ghost.R + 0.4, new Color(0.5f, 1f, 0.5f, 0.9f));
        if (any) _ringMesh.SurfaceEnd();
    }
}
