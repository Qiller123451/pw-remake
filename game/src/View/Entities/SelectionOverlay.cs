// What is drawn over the world for the player (ui/overlay.js, ui/decals.js): the selection circles (on the ground in
// the owner's party colour - the player colour chosen at the start; wild animals green or orange-red (ACColors.txt
// AnimalFriendly / AnimalHostile), other objects without an owner grey - and merged where they
// overlap: GroundRings), the highlight rings a mission hands over (OCPY), the red ring under the targets of the
// selection's attacks, the hover circle (the faint version of the owner's colour), the order markers (a shrinking
// ring where an order went), the places of the formation arrow, the rally flags of the selected producers (the
// tribe's <tribe>_rally_point model), the hero rings (the lower left quarter of Effects/Selection.tga in the player's
// colour, 3 m around the hero) and the buff decals (Effects/UnitDecals.tga tiles), and in 2D: the health bars
// (selected, hovered, damaged, or all with Alt) as thin lines like the original's, low across the front of the
// circle (a unit's with its level emblem at the left end); coloured by the option
// AppSettings.HpBarColors: the health gradient of Effects/Selection.tga for the player's own objects and the circle's
// colour for everyone else's (default), or the owner's colour for all - the
// construction and production bars, the formation arrow, the drag box,
// and the session's message line while there is no HUD.
// Ported from remake/src/ui/overlay.js and remake/src/ui/decals.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.View.Models;

namespace PwRemake.View.Entities;

public partial class SelectionOverlay : Node3D
{
    readonly GameSession S;
    GroundRings _ground = null!;
    /// <summary>Effects/Selection.tga whole (assets/ui/selection.png from the toolkit's ui step): the level emblems of
    /// the health bars; null when the data was prepared before it existed (bars without emblems)</summary>
    public Texture2D? Sheet { get; private set; }
    readonly List<(double X, double Z, double T, int C)> _markers = new();
    readonly Dictionary<Entity, (string Name, ModelInstance Obj)> _flags = new();
    OverlayCanvas _canvas = null!;
    // decals (ui/decals.js)
    static readonly Regex HERO = new("^(stina_|special_eusmilus_|bela_|cole_|babbage_|hermit_|tesla_|lovelace_|mayor_|darwin_|schliemann_|livingstone_)", RegexOptions.IgnoreCase);
    static readonly Regex DECAL_CLS = new("^(CHTR|ANML|VHCL)$");
    const float DECAL_SIZE = 3, DECAL_LIFT = 0.2f;
    Texture2D? _heroTex, _buffTex;
    readonly List<MeshInstance3D> _decals = new();
    int _nd;
    readonly Dictionary<Entity, (double T, int? Tile)> _buffCache = new();

    static readonly Dictionary<string, string> RALLY = new() { ["Hu"] = "hu_rally_point", ["Aje"] = "aje_rally_point", ["Ninigi"] = "ninigi_rally_point", ["SEAS"] = "seas_rally_point" };

    public SelectionOverlay(GameSession s) { S = s; Name = "Overlay"; }

    public override void _Ready()
    {
        var layer = new CanvasLayer { Name = "OverlayLayer", Layer = 5 };
        AddChild(layer);
        _canvas = new OverlayCanvas(S, this) { MouseFilter = Control.MouseFilterEnum.Ignore };
        _canvas.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(_canvas);
        _heroTex = LoadTex("assets/ui/heroring.png");
        _buffTex = LoadTex("assets/ui/unitdecals.png", "Texture/Effects/UnitDecals_(0512).dds");
        Sheet = LoadTex("assets/ui/selection.png", "Texture/Effects/Selection_(0256).dds");
        _ground = new GroundRings(S);
        AddChild(_ground);
    }

    // rel: the prepared copy; original: the game's own texture (installation's Data/<folder>/...), read when the data
    // was prepared before the copy existed (selection.png: the health bars' level emblems)
    Texture2D? LoadTex(string rel, string? original = null)
    {
        var f = S.Assets.Resolve(rel);
        if (f == null && original != null && S.Assets.Config.GameFile(original) is { } o)
        {
            try
            {
                var dds = new Image();
                if (dds.LoadDdsFromBuffer(System.IO.File.ReadAllBytes(o)) == Error.Ok)
                {
                    if (dds.IsCompressed()) dds.Decompress();
                    dds.GenerateMipmaps();
                    return ImageTexture.CreateFromImage(dds);
                }
            }
            catch (Exception) { /* unreadable: as missing */ }
        }
        if (f == null) { Log.For("View.Overlay").Warn($"{rel} is not in the data: no decals of it"); return null; }
        var img = Image.LoadFromFile(f);
        img?.GenerateMipmaps();
        return img == null ? null : ImageTexture.CreateFromImage(img);
    }

    /// <summary>a ring where an order went (overlay.js marker)</summary>
    public void Marker(double x, double z, int color) => _markers.Add((x, z, 0, color));
    /// <summary>the message line while there is no HUD</summary>
    public void Message(string text, string kind) => _canvas?.Message(text, kind);

    /// <summary>a circle on the ground (GroundRings); rgb 0xRRGGBB; circles of one key merge</summary>
    void Put(double x, double z, double r, int rgb, double op = 1, string? key = null, double? w = null) => _ground.Add(x, z, r, rgb, (float)op, key, w);
    /// <summary>an object's selection ring: a circle (a long building: round its footprint's middle, SelectionRing.Shape)</summary>
    void PutRing(Entity e, double x, double z, int rgb, double op, string key)
    {
        var (r, ax, az, cx, cz) = SelectionRing.Shape(e);
        _ground.Add(x + cx, z + cz, r, rgb, (float)op, key, null, ax, az);
    }

    /// <summary>the selection circle's radius: round the model's selection volume (SelectionRing - a walker's legs, a
    /// brachiosaurus' neck, a ship's hull), at least a building's footprint / a flat plot's whole pick volume
    /// (Building.PickR) / a bit wider than a unit's body</summary>
    public static double RingR(Entity e) => SelectionRing.Of(e);
    /// <summary>a question mark's circle (its model is about 2 m across)</summary>
    const double MARK_R = 1.4;
    (double X, double Z) At(Entity e) { var p = S.Entities.PosOf(e); return e is Building ? (e.Pos.X, e.Pos.Z) : (p.X, p.Z); }

    public void Step(double dt)
    {
        var W = S.World;
        if (!S.Cinematic)
        {
            foreach (var e in S.Selection.Items)
            {
                if (!e.Alive) continue;
                var (x, z) = At(e);
                int rgb = GroundRings.RingRgbOf(e);
                PutRing(e, x, z, rgb, 1, $"sel/{rgb:x6}");
            }
            // objects a mission handed over (OCPY chk_highlight): a steady thin ring until the time is up; selecting
            // and deselecting it ends it as in the original
            var HL = W.Highlights;
            foreach (var (e, until) in HL.ToList())
            {
                bool sel = S.Selection.Contains(e);
                if (!e.Alive || e.Owner != S.Me || W.Time >= until || (_hlSel.Contains(e) && !sel)) { HL.Delete(e); _hlSel.Remove(e); continue; }
                if (sel) { _hlSel.Add(e); continue; }
                double r = Math.Max(0.5, e.Radius);
                var (x, z) = At(e);
                Put(x, z, r, 0x7dff6a, 0.9, "hl", 0.3);
            }
            var tg = new HashSet<Entity>();
            foreach (var e in S.Selection.Items)
                if (e is Unit u && u.Alive && u.Owner == S.Me && u.Task.Type == "attack" && u.Task.Target is { Alive: true } t && !u.Task.Auto) tg.Add(t);
            foreach (var t in tg) { var (x, z) = At(t); Put(x, z, t.Radius * 1.4 + 0.3 * Math.Sin(W.Time * 8), 0xff3a2a, 0.8, "target"); }
            var h = S.Input.Hover;
            if (h != null && h.Alive && !S.Selection.Contains(h) && (h is not Unit || S.VisibleToMe(h)))
            {
                var (x, z) = At(h);
                PutRing(h, x, z, GroundRings.RingRgbOf(h), GroundRings.FAINT, "hover");
            }
            // a question mark picked (the original selects it) / under the mouse: a neutral ring round its model
            var inp = S.Input;
            if (inp.PickedMark is { } pm && inp.MarkPos(pm) is { } pp) Put(pp.X, pp.Z, MARK_R, GroundRings.RingRgb(null), 1, "qmark");
            if (inp.HoverMark is { } hm && hm != inp.PickedMark && inp.MarkPos(hm) is { } hp) Put(hp.X, hp.Z, MARK_R, GroundRings.RingRgb(null), GroundRings.FAINT, "qmark-hover");
            for (int i = _markers.Count - 1; i >= 0; i--)
            {
                var m = _markers[i];
                double t = m.T + dt;
                if (t > 0.7) { _markers.RemoveAt(i); continue; }
                _markers[i] = (m.X, m.Z, t, m.C);
                Put(m.X, m.Z, 3.5 * (1 - t / 0.7) + 0.5, m.C, 1 - t / 0.7, "marker" + i);
            }
            // the formation arrow being drawn (right-drag): the places the units will take
            if (S.Input.Arrow is { } fa) foreach (var (x, z) in fa.Slots) Put(x, z, 0.9, 0x7dff6a, 0.85, "arrow");
            UpdateFlags(dt);
            foreach (var e in S.Selection.Items)
                if (e.Alive && e.Rally is var (rx, rz) && S.CanRally(e) && !_flags.ContainsKey(e)) Put(rx, rz, 1.2 + 0.2 * Math.Sin(W.Time * 5), 0xffe070, 0.9, "rally");
        }
        else UpdateFlags(dt, true);
        _ground.Flush();
        UpdateDecals();
        _canvas.QueueRedraw();
    }
    readonly HashSet<Entity> _hlSel = new();

    // the rally flag of every selected own producer
    void UpdateFlags(double dt, bool none = false)
    {
        var W = S.World;
        var want = new HashSet<Entity>();
        if (!none) foreach (var e in S.Selection.Items) if (e.Alive && e.Rally != null && S.CanRally(e)) want.Add(e);
        foreach (var e in _flags.Keys.ToList())
        {
            if (want.Contains(e)) continue;
            _flags[e].Obj.QueueFree();
            _flags.Remove(e);
        }
        foreach (var e in want)
        {
            var (x, z) = e.Rally!.Value;
            bool water = W.IsWater(x, z, 0.4);
            var name = RALLY.TryGetValue(e.Owner!.Tribe, out var n) ? n + (water ? "_harbour" : "") : null;
            if (!_flags.TryGetValue(e, out var f) || f.Name != name)
            {
                if (_flags.Remove(e, out var old)) old.Obj.QueueFree();
                if (name == null || !EntityViews.HasFile(S.Models, name)) continue;
                var obj = S.Models.Instantiate(name, e.Party is int pc ? EntityViews.Hex(pc) : null);
                if (obj == null) continue;
                AddChild(obj);
                var clip = obj.Anim?.Pick("standanim", "idle", "work") ?? obj.Anim?.Names.FirstOrDefault();
                if (clip != null) obj.Play(clip);
                f = (name, obj);
                _flags[e] = f;
            }
            f.Obj.Position = new Vector3((float)x, (float)(water ? W.WaterLevel ?? W.Height(x, z) : W.Height(x, z)), (float)z);
            f.Obj.Advance(dt);
        }
    }

    // ---------------------------------------------------------------- decals (ui/decals.js)
    MeshInstance3D Decal(Texture2D tex)
    {
        if (_nd >= _decals.Count)
        {
            var m = new MeshInstance3D { Mesh = new ImmediateMesh(), CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            m.MaterialOverride = new StandardMaterial3D
            {
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, DisableFog = true,
                CullMode = BaseMaterial3D.CullModeEnum.Disabled, RenderPriority = 5, DepthDrawMode = BaseMaterial3D.DepthDrawModeEnum.Disabled,
            };
            AddChild(m);
            _decals.Add(m);
        }
        var d = _decals[_nd++];
        d.Visible = true;
        ((StandardMaterial3D)d.MaterialOverride).AlbedoTexture = tex;
        return d;
    }

    // lays the 3 x 3 grid around (x, z); uv rectangle (u0, v0, u1, v1) with v down (Godot)
    void Lay(MeshInstance3D m, double x, double z, float r, Vector4 uv, Color col)
    {
        var im = (ImmediateMesh)m.Mesh;
        im.ClearSurfaces();
        im.SurfaceBegin(Mesh.PrimitiveType.Triangles);
        var W = S.World;
        Vector3 P(int i, int j) { double px = x + (i - 1) * r, pz = z + (j - 1) * r; return new Vector3((float)px, (float)W.Height(px, pz) + DECAL_LIFT, (float)pz); }
        Vector2 T(int i, int j) => new(uv.X + (uv.Z - uv.X) * i / 2f, uv.Y + (uv.W - uv.Y) * j / 2f);
        void V(int i, int j) { im.SurfaceSetColor(col); im.SurfaceSetUV(T(i, j)); im.SurfaceAddVertex(P(i, j)); }
        for (int j = 0; j < 2; j++) for (int i = 0; i < 2; i++) { V(i, j); V(i, j + 1); V(i + 1, j); V(i + 1, j); V(i, j + 1); V(i + 1, j + 1); }
        im.SurfaceEnd();
        ((StandardMaterial3D)m.MaterialOverride).VertexColorUseAsAlbedo = true;
    }

    // the tile of the buff decal the player sees on e (null: none); worked out four times a second
    int? BuffTile(Entity e)
    {
        var W = S.World;
        if (_buffCache.TryGetValue(e, out var c) && W.Time - c.T < 0.25 && c.T <= W.Time) return c.Tile;
        int? tile = null;
        var have = Buffs.BuffsOf(W, e);
        if (have.Count > 0)
        {
            bool own = e.Owner == S.Me; int rel = e.Owner != null && !own ? S.Me.Relation(e.Owner) : -1;
            var b = W.Data.Extra.Buffs.FirstOrDefault(x => x.Tile >= 0 && have.Contains(x.Attrib));   // (only buffs with a tile compete: 0x484abf)
            if (b != null && (own || b.Vis == 1 || (b.Vis == 2 && rel == 0) || (b.Vis == 3 && rel == 2))) tile = b.Tile;
        }
        _buffCache[e] = (W.Time, tile);
        return tile;
    }

    void UpdateDecals()
    {
        _nd = 0;
        var W = S.World;
        if (!S.Cinematic)
            foreach (var u in W.Units)
            {
                if (!u.Alive || u.Inside || !DECAL_CLS.IsMatch(u.Cls) || !S.Entities.OnScreen(u)) continue;
                var p = S.Entities.PosOf(u);
                if (_heroTex != null && u.Owner != null && HERO.IsMatch(u.Name))
                    Lay(Decal(_heroTex), p.X, p.Z, DECAL_SIZE, new Vector4(0.02f, 0.02f, 0.98f, 0.98f), EntityViews.Hex(u.Owner.Color));
                if (_buffTex != null && BuffTile(u) is int tile && tile >= 0)
                {
                    float uu = tile % 4 * 0.25f, vv = tile / 4 * 0.25f;
                    Lay(Decal(_buffTex), p.X, p.Z, DECAL_SIZE, new Vector4(uu, vv + 0.25f, uu + 0.25f, vv), Godot.Colors.White);
                }
            }
        for (int i = _nd; i < _decals.Count; i++) _decals[i].Visible = false;
        if (_buffCache.Count > 2 * W.Units.Count + 64) foreach (var e in _buffCache.Keys.Where(e => !e.Alive).ToList()) _buffCache.Remove(e);
    }
}

/// <summary>The 2D part of the overlay: health bars, progress bars, the formation arrow, the drag box, messages.</summary>
public partial class OverlayCanvas : Control
{
    readonly GameSession S;
    readonly SelectionOverlay O;
    readonly List<(string Text, string Kind, double T)> _msgs = new();
    public OverlayCanvas(GameSession s, SelectionOverlay o) { S = s; O = o; Name = "OverlayCanvas"; }

    public void Message(string text, string kind) { _msgs.Add((text, kind, 0)); if (_msgs.Count > 5) _msgs.RemoveAt(0); }

    public override void _Process(double delta)
    {
        for (int i = _msgs.Count - 1; i >= 0; i--) { var m = _msgs[i]; _msgs[i] = (m.Text, m.Kind, m.T + delta); if (m.T > 6) _msgs.RemoveAt(i); }
    }

    static Color C(string hex) => Color.FromHtml(hex);

    public override void _Draw()
    {
        if (S.Mission == null || S.Cinematic) { DrawMessages(); return; }
        var cam = S.Renderer.Camera;
        var size = GetViewportRect().Size;
        var hover = S.Input.Hover;
        bool alt = Godot.Input.IsKeyPressed(Key.Alt);
        var right = cam.GlobalTransform.Basis.X; right.Y = 0;
        right = right.LengthSquared() < 1e-6f ? Vector3.Right : right.Normalized();
        var toCam = new Vector3(-right.Z, 0, right.X);                      // horizontal, towards the camera
        // the screen line of a horizontal segment of half length hl (m) through p, across the view; null off screen
        (float X0, float X1, float Y)? SpanAt(Vector3 p, float hl)
        {
            Vector3 a = p - right * hl, b = p + right * hl;
            if (cam.IsPositionBehind(a) || cam.IsPositionBehind(b)) return null;
            Vector2 s0 = cam.UnprojectPosition(a), s1 = cam.UnprojectPosition(b);
            float x0 = Math.Min(s0.X, s1.X), x1 = Math.Max(s0.X, s1.X), y = Mathf.Round((s0.Y + s1.Y) / 2);
            if (x1 < -60 || x0 > size.X + 60 || y < -60 || y > size.Y + 60) return null;
            return (x0, x1, y);
        }
        // one bar: background, then the filled part (th px thick); returns its left end
        float Line((float X0, float X1, float Y) L, double f, Color fill, Color back, float th = 2)
        {
            float w = Math.Max(14, L.X1 - L.X0), x = Mathf.Round((L.X0 + L.X1) / 2 - w / 2);
            DrawRect(new Rect2(x - 1, L.Y - 1, w + 2, th + 2), back);
            DrawRect(new Rect2(x, L.Y, Mathf.Round(w * (float)Math.Clamp(f, 0, 1)), th), fill);
            return x;
        }
        var sheet = O.Sheet;
        bool byPlayer = GameApp.Settings.HpBarColors == "player";
        // fill and background of an object's health bar
        (Color Fill, Color Back) Colours(Entity e, double f)
        {
            // the health gradient only for the player's own objects; everyone else's (allies, enemies, wild animals) in
            // the colour of their circle (Kacper: "only green and progressive for our units, otherwise the colour of
            // the faction")
            if (!byPlayer && e.Owner == S.Me) return (GroundRings.HpGradient(f), GroundRings.BAR_BACK);
            var c = GroundRings.RingColor(e);
            return (c.Lerp(Godot.Colors.White, 0.4f), new Color(c.R * 0.12f, c.G * 0.12f, c.B * 0.12f, 0.85f));
        }
        void One(Entity e)
        {
            if (!e.Alive || e is ResNode) return;
            bool show = S.Selection.Contains(e) || e == hover || e.Hp < e.MaxHp - 0.5 || alt;
            if (!show || !S.Entities.Shown(e) || !S.VisibleToMe(e)) return;
            double f = e.Hp / e.MaxHp;
            float r = (float)SelectionOverlay.RingR(e);
            var pos = S.Entities.PosOf(e);
            bool own = e.Owner == S.Me;
            if (e is Building b)
            {
                // low across the front of the circle
                // (a long building's ring: its front towards the camera, its width the stadium's - SelectionRing.Shape)
                var (sr, sax, saz, scx, scz) = SelectionRing.Shape(b);
                r = (float)sr;
                var p = new Vector3((float)(b.Pos.X + scx), 0, (float)(b.Pos.Z + scz)) + toCam * (r * 0.7f + Math.Abs((float)sax * toCam.X + (float)saz * toCam.Z));
                p.Y = (float)S.World.Height(p.X, p.Z) + 0.1f;
                if (SpanAt(p, r * 0.85f) is not { } L) return;
                var bc = Colours(e, f);
                Line(L, f, bc.Fill, bc.Back);
                var L2 = (L.X0, L.X1, L.Y + 5);
                if (!b.Built && own) Line(L2, b.Progress, GroundRings.BAR_BUILD, GroundRings.BAR_BACK);
                else if (e.Queue.Count > 0 && own && S.Selection.Contains(e) && e.Queue[0].Total > 0) Line(L2, e.Queue[0].T / e.Queue[0].Total, GroundRings.BAR_QUEUE, GroundRings.BAR_BACK);
                return;
            }
            // a unit: low across the front of its circle like a building's (Kacper: "more or less at the bottom just like
            // for buildings"), at the unit's own foot height (flyers, ships); the level emblem at the left end
            var foot = pos + toCam * (r * 0.7f);
            foot.Y = pos.Y + 0.1f;
            if (SpanAt(foot, r * 0.85f) is not { } U) return;
            var uc = Colours(e, f);
            float x = Line(U, f, uc.Fill, uc.Back);
            // (no emblem on wild animals: they have no level or epoch - Kacper 2026-11-12)
            if (sheet != null && e.Level >= 1 && !(e is Unit wu && (wu.Wild || wu.Owner == null)))
            {
                var src = GroundRings.Emblem(e.Level);
                DrawTextureRectRegion(sheet, new Rect2(x - 2 - src.Size.X / 2, U.Y + 1 - src.Size.Y / 2, src.Size), src);
            }
            if (e.Queue.Count > 0 && own && S.Selection.Contains(e) && e.Queue[0].Total > 0) Line((U.X0, U.X1, U.Y - 5), e.Queue[0].T / e.Queue[0].Total, GroundRings.BAR_QUEUE, GroundRings.BAR_BACK);
        }
        foreach (var u in S.World.Units) One(u);
        foreach (var b in S.World.Buildings) One(b);
        // the formation arrow (CFormPreview): from the press point to the cursor
        if (S.Input.Arrow is { } a)
        {
            var W = S.World;
            var p0 = new Vector3((float)a.X, (float)W.Height(a.X, a.Z) + 0.4f, (float)a.Z);
            var p1 = new Vector3((float)a.TipX, (float)W.Height(a.TipX, a.TipZ) + 0.4f, (float)a.TipZ);
            if (!cam.IsPositionBehind(p0) && !cam.IsPositionBehind(p1))
            {
                var s0 = cam.UnprojectPosition(p0); var s1 = cam.UnprojectPosition(p1);
                float ang = Mathf.Atan2(s1.Y - s0.Y, s1.X - s0.X), hl = 16;
                var col = new Color(160 / 255f, 1, 130 / 255f, 0.95f);
                DrawLine(s0, s1, col, 3, true);
                DrawLine(s1, s1 - hl * new Vector2(Mathf.Cos(ang - 0.45f), Mathf.Sin(ang - 0.45f)), col, 3, true);
                DrawLine(s1, s1 - hl * new Vector2(Mathf.Cos(ang + 0.45f), Mathf.Sin(ang + 0.45f)), col, 3, true);
            }
        }
        if (S.Input.DragBox is { } box)
        {
            DrawRect(box, new Color(160 / 255f, 1, 130 / 255f, 0.12f));
            DrawRect(box, new Color(160 / 255f, 1, 130 / 255f, 0.95f), false, 1);
        }
        DrawMessages();
    }

    void DrawMessages()
    {
        if (_msgs.Count == 0) return;
        var font = ThemeDB.FallbackFont;
        var size = GetViewportRect().Size;
        float y = size.Y * 0.18f;
        foreach (var (text, kind, t) in _msgs)
        {
            float a = (float)Math.Clamp(6 - t, 0, 1);
            var col = kind switch { "good" => new Color(0.75f, 1, 0.6f, a), "bad" => new Color(1, 0.55f, 0.45f, a), "warn" => new Color(1, 0.85f, 0.4f, a), _ => new Color(1, 0.95f, 0.8f, a) };
            var w = font.GetStringSize(text, HorizontalAlignment.Left, -1, 16).X;
            DrawString(font, new Vector2((size.X - w) / 2 + 1, y + 1), text, HorizontalAlignment.Left, -1, 16, new Color(0, 0, 0, a));
            DrawString(font, new Vector2((size.X - w) / 2, y), text, HorizontalAlignment.Left, -1, 16, col);
            y += 22;
        }
    }
}
