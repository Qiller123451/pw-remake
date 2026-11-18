// The minimap, drawn the way the original's engine draws it (remake/docs/MINIMAP.md; CMiniMap in PWClient.exe, set up
// by Game/UI/MiniMap2.usl and IngameScreen.usl).
//
// * Area: the playable rectangle only - an original map minus its 128 m border - in 4 m cells, scaled into the 164
//   texel square (46..210) of a 256 x 256 texture that the 230 px control shows; the longer side fills it.
// * Orientation: a diamond. The texture is turned so that the level's default camera direction, snapped to the nearest
//   diagonal, points up; with the option "Rotate Minimap" it follows the camera. The background picture
//   hud/minimap_rotmap.tga (gold ring, frame, dark grid) turns with it.
// * Ground: not the painted textures but the setting's colour table Texture/Scape/<Setting>/minimap_colors.tga: one
//   of five relief shades by the slope to the south-east, shallow water within 0.5 m below the water level, deep water,
//   forest on top. Fog of war: unexplored cells are left out (the dark grid shows), explored ones are drawn half
//   transparent (table row 3), visible ones solid (row 0).
// * Dots: square texels of fixed size, turned with the map - resources (half transparent white, stones bigger),
//   buildings (the owner's colour lightened), units (the owner's colour); objects without an owner (wild animals) are
//   not drawn. "Use Player Colors" off: own green, allies blue, everyone else red. Buildings seen once and now in the
//   fog stay as dimmed dots.
// * The camera's ground footprint in white lines; markers ("pings") as pulsing squares: attacks (red, 3.2 s at
//   1.3 Hz), finished production or research (blue), the player's own map pings.
// * Buttons on the frame (MiniMap2.usl): landscape and resources (+ forest) on / off, map ping, zoom 1 .. 7.
//
// The JS drew into a 2D canvas; here the map square is rasterised into a 256 x 256 RGBA buffer (Tex) on the
// processor - the cells, the dots, the camera lines and the pings, in texture space before the turn - and the view
// (HudMinimap.cs) draws the background picture and this buffer turned by Angle, then the mission's markers.
//
// No Godot types. Ported from remake/src/ui/minimap.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;

namespace PwRemake.UI.Hud.Logic;

/// <summary>What the minimap is made of: the map's rectangle, its ground and the setting's colour table.</summary>
public sealed class MinimapSource
{
    /// <summary>the playable rectangle (the camera and the clicks stay in it)</summary>
    public double X0, X1, Z0, Z1;
    /// <summary>an original map's half size (MapSource.Half: its origin, unless extended for co-op): the minimap shows
    /// ± (half - 128 m) around the world's centre; null: the Play rect</summary>
    public double[]? Origin;
    public Func<double, double, double> Height = (_, _) => 0;
    public double? Water;
    public string Setting = "Jungle";

    /// <summary>from a map source of src/Sim/Maps (campaign and skirmish maps)</summary>
    public static MinimapSource From(Sim.Maps.MapSource S) => new()
    {
        X0 = S.Play.X0, X1 = S.Play.X1, Z0 = S.Play.Z0, Z1 = S.Play.Z1, Origin = S.Half ?? S.Origin, Height = S.Height, Water = S.Water, Setting = S.Setting,
    };
    /// <summary>from the world's map (tests, flat maps)</summary>
    public static MinimapSource From(IWorldMap m) => new()
    {
        X0 = m.Play.X0, X1 = m.Play.X1, Z0 = m.Play.Z0, Z1 = m.Play.Z1, Height = m.Height, Water = m.Water, Setting = m.Setting,
    };
}

public sealed class MinimapModel
{
    public const int TEX = 256, MAP0 = 46, MAPN = 164;      // the render texture, the map square inside it
    public const double CELL = 4;                            // m per cell
    public const double BORDER = 128;                        // m cut off an original map on every side
    // (the user found units and buildings hard to see at the engine's sizes: units 3 texels and buildings at least 4 or
    // their footprint, both with a dark rim)
    public const double SIZE_UNIT = 3, SIZE_BUILDING = 4, SIZE_RESOURCE = 3, SIZE_STONE = 5, SIZE_MARKER = 18;  // texels (the engine's 1, 1.2, 1.5, 2.5)
    /// <summary>marker types (MiniMap2.usl:238-262): colour, size (100 = full), seconds alive, pulses per second</summary>
    public static readonly Dictionary<string, (int[] Color, double Size, double Life, double Freq)> MARKERS = new()
    {
        ["Attack"] = (new[] { 255, 0, 0 }, 80, 3.2, 1.3), ["Finished"] = (new[] { 100, 100, 255 }, 100, 1.2, 0.7),
        ["AIHelp"] = (new[] { 255, 255, 255 }, 100, 1.2, 0.7), ["Ping"] = (new[] { 255, 255, 255 }, 100, 3.2, 0.7),
    };
    const double LIGHT = 255.0 * 106 / 256, DARK = 150.0 / 256;

    public sealed class Marker { public string Type = ""; public double X, Z, T, Size, Life, Freq; public int[] Color = { 255, 255, 255 }; }

    readonly IHudContext H;
    public readonly MinimapSource S;
    /// <summary>the minimap's rectangle (the playable area)</summary>
    public readonly double RX0, RX1, RZ0, RZ1;
    public readonly int GW, GH;
    /// <summary>texels per metre at zoom 1</summary>
    public readonly double K;
    readonly double _cx, _cz;
    /// <summary>the setting's colour table: 6 rows of 21 colours [r, g, b, a] (null: none - the minimap stays empty)</summary>
    public readonly int[][][]? Table;
    public int Zoom = 1;
    public bool Scape = true, Resources = true;
    public readonly List<Marker> Markers = new();
    /// <summary>the level's default camera, snapped to a diagonal</summary>
    public readonly double Phi;
    /// <summary>options: "Rotate Minimap" (follows the camera), "Use Player Colors"</summary>
    public bool Rotate, PlayerColors = true;
    /// <summary>the 256 x 256 RGBA picture of the map square before the turn (Update fills it)</summary>
    public readonly byte[] Tex = new byte[TEX * TEX * 4];

    byte[]? _col;                 // relief / water column per cell
    byte[]? _forest;              // 1: forest
    readonly byte[] _cells;       // the cells as RGBA (paint)
    int _fowSeen = int.MinValue, _forestSeen = int.MinValue;
    bool _dirty = true;
    double _t;

    public MinimapModel(IHudContext host, MinimapSource s, int[][][]? table)
    {
        H = host; S = s;
        // the playable rectangle in 4 m cells
        var o = s.Origin;
        if (o != null && o.Length >= 2) { RX0 = -o[0] + BORDER; RX1 = o[0] - BORDER; RZ0 = -o[1] + BORDER; RZ1 = o[1] - BORDER; }
        else { RX0 = s.X0; RX1 = s.X1; RZ0 = s.Z0; RZ1 = s.Z1; }
        GW = Math.Max(1, (int)Math.Round((RX1 - RX0) / CELL, MidpointRounding.AwayFromZero));
        GH = Math.Max(1, (int)Math.Round((RZ1 - RZ0) / CELL, MidpointRounding.AwayFromZero));
        K = MAPN / Math.Max(RX1 - RX0, RZ1 - RZ0);
        _cx = (RX0 + RX1) / 2; _cz = (RZ0 + RZ1) / 2;
        Table = table;
        _cells = new byte[GW * GH * 4];
        Phi = SnapDiamond(host.Camera.Yaw);
    }

    /// <summary>the setting's colour table from assets/terrain/&lt;Setting&gt;/setting.json "minimap" (pipeline/build_terrain.py:
    /// Texture/Scape/&lt;Setting&gt;/minimap_colors.tga, 6 rows of 21 colours); null with game data built before it</summary>
    public static int[][][]? LoadTable(string? settingJsonPath)
    {
        if (settingJsonPath == null || Json.TryParseFile(settingJsonPath) is not JsonObject o || o["minimap"] is not JsonArray t) return null;
        if (t.Count != 6 || t[0] is not JsonArray r0 || r0.Count < 15) return null;
        return t.Select(row => (row as JsonArray ?? new JsonArray()).Select(c => (c as JsonArray ?? new JsonArray()).Select(v => v.Int()).ToArray()).ToArray()).ToArray();
    }

    // ---------------------------------------------------------------- ground
    /// <summary>the relief shade of each cell (columns 0..4 of the colour table; 10 shallow, 11 deep water): s = ((h north -
    /// h south) + (h west - h east)) / 2 over the neighbouring cells; the light comes from the north-west</summary>
    public static byte[] ReliefCells(int gw, int gh, Func<int, int, double> hAt, double? water)
    {
        var h = new float[gw * gh];
        for (int j = 0; j < gh; j++) for (int i = 0; i < gw; i++) h[j * gw + i] = (float)hAt(i, j);     // (the JS kept them in a Float32Array)
        var col = new byte[gw * gh];
        double Hh(int i, int j) => h[Math.Max(0, Math.Min(gh - 1, j)) * gw + Math.Max(0, Math.Min(gw - 1, i))];
        for (int j = 0; j < gh; j++) for (int i = 0; i < gw; i++)
            {
                double v = h[j * gw + i];
                if (water != null && v <= water) { col[j * gw + i] = (byte)(v <= water - 0.5 ? 11 : 10); continue; }
                double s = ((Hh(i, j - 1) - Hh(i, j + 1)) + (Hh(i - 1, j) - Hh(i + 1, j))) / 2;
                col[j * gw + i] = (byte)(s < -3 ? 0 : s < -0.1 ? 1 : s > 3 ? 4 : s > 0.1 ? 3 : 2);
            }
        return col;
    }

    /// <summary>the cells as RGBA: state(k) 0 unexplored (transparent), 1 explored (row 3), 2 visible (row 0); forest
    /// (column 12) over the ground</summary>
    public static void PaintCells(byte[] data, byte[] col, byte[]? forest, int[][][] table, Func<int, int> state, bool scape = true, bool woods = true)
    {
        for (int k = 0; k < col.Length; k++)
        {
            int st = state(k), o = k * 4;
            if (st == 0) { data[o] = data[o + 1] = data[o + 2] = data[o + 3] = 0; continue; }
            var row = table[st == 2 ? 0 : 3];
            double r = 0, g = 0, b = 0, a = 0;
            if (scape) { var c = row[col[k]]; r = c[0]; g = c[1]; b = c[2]; a = c[3] / 255.0; }
            if (woods && forest != null && forest[k] != 0)
            {
                var f = row[12]; double fa = f[3] / 255.0, oa = fa + a * (1 - fa), ga = a * (1 - fa);      // forest over the ground
                r = (f[0] * fa + r * ga) / oa; g = (f[1] * fa + g * ga) / oa; b = (f[2] * fa + b * ga) / oa; a = oa;
            }
            // (a Uint8ClampedArray rounds half to even)
            data[o] = Clamp8(r); data[o + 1] = Clamp8(g); data[o + 2] = Clamp8(b); data[o + 3] = Clamp8(Math.Round(a * 255, MidpointRounding.AwayFromZero));
        }
    }
    static byte Clamp8(double v) => (byte)Math.Clamp(Math.Round(v, MidpointRounding.ToEven), 0, 255);

    /// <summary>fixed orientation: the angle snapped to the nearest odd multiple of 45 degrees (the engine's quadrant table)</summary>
    public static double SnapDiamond(double a) => Math.PI / 4 + JsRound((a - Math.PI / 4) / (Math.PI / 2)) * Math.PI / 2;
    static double JsRound(double v) => Math.Floor(v + 0.5);

    /// <summary>per cell: relief shade and water (buildBackground)</summary>
    public void BuildBackground()
    {
        _col = ReliefCells(GW, GH, (i, j) => S.Height(RX0 + (i + 0.5) * CELL, RZ0 + (j + 0.5) * CELL), S.Water);
        _dirty = true;
    }

    /// <summary>forest cells: those with a standing tree within 4 m of their centre (the engine's forest layer: a 4 m query
    /// of the forest blocks and the trees)</summary>
    public void BuildForest()
    {
        var f = _forest ??= new byte[GW * GH];
        Array.Clear(f);
        foreach (var r in H.World.Resources)
        {
            if (!r.Alive || r.Type != "tree") continue;
            double fx = (r.Pos.X - RX0) / CELL - 0.5, fz = (r.Pos.Z - RZ0) / CELL - 0.5;
            for (int j = (int)Math.Ceiling(fz - 1); j <= (int)Math.Floor(fz + 1); j++)
                for (int i = (int)Math.Ceiling(fx - 1); i <= (int)Math.Floor(fx + 1); i++)
                    if (i >= 0 && j >= 0 && i < GW && j < GH && (i - fx) * (i - fx) + (j - fz) * (j - fz) <= 1) f[j * GW + i] = 1;
        }
    }

    // ---------------------------------------------------------------- mapping
    /// <summary>the turn of the texture (radians)</summary>
    public double Angle => Rotate ? H.Camera.Yaw : Phi;

    /// <summary>the point the zoomed map centres on: the camera's, kept so that the map still fills the square</summary>
    public (double X, double Z) Focus()
    {
        if (Zoom <= 1) return (_cx, _cz);
        double e = MAPN / 2.0 / (K * Zoom);
        var c = H.Camera;
        static double Cl(double v, double a, double b) => a > b ? (a + b) / 2 : Math.Max(a, Math.Min(b, v));
        return (Cl(c.X, RX0 + e, RX1 - e), Cl(c.Z, RZ0 + e, RZ1 - e));
    }
    /// <summary>world -> texture texel (before the turn)</summary>
    public (double X, double Y) W2T(double x, double z, (double X, double Z)? f = null)
    {
        var F = f ?? Focus(); double s = K * Zoom;
        return (TEX / 2.0 + (x - F.X) * s, TEX / 2.0 + (z - F.Z) * s);
    }
    /// <summary>world -> control pixel of the 256 px control (after the turn): the mission's markers</summary>
    public (double X, double Y) W2M(double x, double z)
    {
        var (tx, ty) = W2T(x, z);
        double a = Angle, c = Math.Cos(a), s = Math.Sin(a), dx = tx - TEX / 2.0, dy = ty - TEX / 2.0;
        return (TEX / 2.0 + dx * c - dy * s, TEX / 2.0 + dx * s + dy * c);
    }
    /// <summary>control pixel (256 px control) -> world point, kept in the playable rectangle</summary>
    public (double X, double Z) M2W(double mx, double my)
    {
        double a = -Angle, c = Math.Cos(a), s = Math.Sin(a), dx = mx - TEX / 2.0, dy = my - TEX / 2.0;
        double tx = dx * c - dy * s, ty = dx * s + dy * c;
        var F = Focus(); double k = K * Zoom;
        return (Math.Max(S.X0, Math.Min(S.X1, F.X + tx / k)), Math.Max(S.Z0, Math.Min(S.Z1, F.Z + ty / k)));
    }

    // ---------------------------------------------------------------- markers
    /// <summary>a pulsing marker (MiniMap2.usl NewMarker / NewMarkerReuse: the Attack and Finished markers are reused)</summary>
    public void AddMarker(string type, double x, double z, int[]? color = null)
    {
        var M = MARKERS.TryGetValue(type, out var m) ? m : MARKERS["Ping"];
        if (type == "Attack" || type == "Finished") Markers.RemoveAll(x0 => x0.Type == type);
        Markers.Add(new Marker { Type = type, X = x, Z = z, T = H.World.Time, Color = color ?? M.Color, Size = M.Size, Life = M.Life, Freq = M.Freq });
        _t = 0;
    }

    // ---------------------------------------------------------------- the colours
    /// <summary>the colour of an owner's objects: its minimap colour (the table's player colours by colour slot) or by
    /// relation ("Use Player Colors" off)</summary>
    public int[] OwnerColor(Player p, int row, Entity? e, ISet<Entity>? sel = null)
    {
        var T = Table![row];
        var me = H.Me;
        if (!PlayerColors)
        {
            if (p == me) return T[sel != null && e != null && sel.Contains(e) ? 0 : 1];
            return T[me.IsFriend(p) && p.IsFriend(me) ? 2 : 3];
        }
        // (the table has the original's eight; the remake's palette colours and those of the whole spectrum: the
        // player's dark colour for buildings - rows 1 / 4 -, its light one for units - rows 2 / 5 -, as the table's pairs)
        if (p.ColorSlot >= 0 && p.ColorSlot < PwRemake.Sim.Rules.Colors.ORIGINAL_COLORS && 12 + p.ColorSlot < T.Length) return T[12 + p.ColorSlot];
        int rgb = row % 3 == 2 && p.PartyColor is int light ? light : p.Color;
        return new[] { (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255 };
    }

    // ---------------------------------------------------------------- drawing
    /// <summary>
    /// The 3D view's rule (EntityViews.Visible, World.HiddenFrom): an enemy that is camouflaged (Aje shaman, disguised
    /// ninja, entrenched), in a smoke cloud or under a smoke tower is not drawn - the original's unit loop skips objects
    /// with hide flag 0x800, which camouflage sets.
    /// </summary>
    bool HiddenFromMe(Entity e) => e.Alive && H.World.HiddenFrom(e, H.Me);
    bool Sees(Player? p) => p != null && (p == H.Me || (H.Campaign && H.Me.IsFriend(p) && p.IsFriend(H.Me)));
    int Seen(double x, double z) { var f = H.Fog; return f == null || f.Visible(x, z) ? 2 : f.Explored(x, z) ? 1 : 0; }

    // (the cells' fog states of the last paint: the fog's version changes with every step of the fog, its cells seldom -
    // the same states, the same layers and woods paint the same cells)
    byte[]? _st, _stPainted;
    bool _layersChanged = true, _pScape, _pRes;
    void Paint()
    {
        var st = _st ??= new byte[GW * GH];
        for (int j = 0; j < GH; j++)
            for (int i = 0; i < GW; i++)
                st[j * GW + i] = (byte)Seen(RX0 + (i + 0.5) * CELL, RZ0 + (j + 0.5) * CELL);
        if (!_layersChanged && _stPainted != null && st.AsSpan().SequenceEqual(_stPainted)) return;
        _layersChanged = false;
        (_stPainted ??= new byte[GW * GH]).AsSpan().Clear();
        st.AsSpan().CopyTo(_stPainted);
        PaintCells(_cells, _col!, _forest, Table!, k => st[k], Scape, Resources);
        _cellsVersion++;
    }
    int _cellsVersion;

    /// <summary>layers or zoom changed: repaint at the next Update</summary>
    public void Invalidate() { _dirty = true; _layersChanged = true; _t = 0; }

    /// <summary>redraws the picture (30 times a second, or now with force); true when Tex changed</summary>
    public long PaintTicks; public int Paints;
    byte[]? _base;
    (int, double, double, double, double) _baseKey;
    public bool Update(double dt, bool force = false)
    {
        _t -= dt;
        if (_t > 0 && !force) return false;
        _t = 1.0 / 30;
        if (Table == null || _col == null) return false;
        var W = H.World;
        if (_forestSeen != W.ForestVersion || _forest == null) { _forestSeen = W.ForestVersion; BuildForest(); _dirty = true; _layersChanged = true; }
        if (_dirty || Scape != _pScape || Resources != _pRes) { _layersChanged = true; _pScape = Scape; _pRes = Resources; }
        if (_fowSeen != H.FogVersion || _dirty) { long p0 = System.Diagnostics.Stopwatch.GetTimestamp(); _fowSeen = H.FogVersion; _dirty = false; Paint(); PaintTicks += System.Diagnostics.Stopwatch.GetTimestamp() - p0; Paints++; }
        var F = Focus();
        var (ax, ay) = W2T(RX0, RZ0, F); var (bx, by) = W2T(RX1, RZ1, F);
        // the cells scaled into the square: made again only when the cells or the zoom / focus changed (a texel loop of
        // the whole map 30 times a second), else copied
        var key = (_cellsVersion, ax, ay, bx, by);
        if (_base == null || key != _baseKey)
        {
            Array.Clear(Tex);
            BlitCells(ax, ay, bx - ax, by - ay);
            _base ??= new byte[Tex.Length];
            Buffer.BlockCopy(Tex, 0, _base, 0, Tex.Length);
            _baseKey = key;
        }
        else Buffer.BlockCopy(_base, 0, Tex, 0, Tex.Length);
        DrawDots(F);
        DrawCamera(F);
        DrawMarkers(F);
        return true;
    }

    // the cells scaled into (x, y, w, h), clipped to the map square (nearest cell; the JS smoothed when zoomed)
    void BlitCells(double x, double y, double w, double h)
    {
        int tx0 = Math.Max(MAP0, (int)Math.Floor(x)), tx1 = Math.Min(MAP0 + MAPN, (int)Math.Ceiling(x + w));
        int ty0 = Math.Max(MAP0, (int)Math.Floor(y)), ty1 = Math.Min(MAP0 + MAPN, (int)Math.Ceiling(y + h));
        for (int ty = ty0; ty < ty1; ty++)
        {
            int j = (int)Math.Floor((ty + 0.5 - y) / h * GH);
            if (j < 0 || j >= GH) continue;
            for (int tx = tx0; tx < tx1; tx++)
            {
                int i = (int)Math.Floor((tx + 0.5 - x) / w * GW);
                if (i < 0 || i >= GW) continue;
                int s = (j * GW + i) * 4, d = (ty * TEX + tx) * 4;
                Tex[d] = _cells[s]; Tex[d + 1] = _cells[s + 1]; Tex[d + 2] = _cells[s + 2]; Tex[d + 3] = _cells[s + 3];
            }
        }
    }

    /// <summary>source-over blend of a colour into texel (x, y) of the map square</summary>
    void Blend(int x, int y, double r, double g, double b, double a)
    {
        if (x < MAP0 || y < MAP0 || x >= MAP0 + MAPN || y >= MAP0 + MAPN || a <= 0) return;
        int d = (y * TEX + x) * 4;
        double da = Tex[d + 3] / 255.0, oa = a + da * (1 - a);
        if (oa <= 0) return;
        Tex[d] = Clamp8((r * a + Tex[d] * da * (1 - a)) / oa);
        Tex[d + 1] = Clamp8((g * a + Tex[d + 1] * da * (1 - a)) / oa);
        Tex[d + 2] = Clamp8((b * a + Tex[d + 2] * da * (1 - a)) / oa);
        Tex[d + 3] = Clamp8(oa * 255);
    }
    void FillRect(int x0, int y0, int n, double r, double g, double b, double a)
    {
        for (int y = y0; y < y0 + n; y++) for (int x = x0; x < x0 + n; x++) Blend(x, y, r, g, b, a);
    }

    readonly List<(int X, int Y, int N, double A)> _rims = new();
    readonly List<(int X, int Y, int N, double R, double G, double B, double A)> _fills = new();
    readonly HashSet<Entity> _sel = new();
    void DrawDots((double X, double Z) F)
    {
        var W = H.World;
        // (rims first, under every dot: a neighbour's rim never darkens a dot)
        var rims = _rims; var fills = _fills;
        rims.Clear(); fills.Clear();
        double s0 = K * Zoom, ox = TEX / 2.0 - F.X * s0, oz = TEX / 2.0 - F.Z * s0;
        void Dot(double x, double z, double size, int[] c, double a, bool rim = false, bool light = false)
        {
            double tx = ox + x * s0, ty = oz + z * s0;
            int x0 = (int)JsRound(tx - size / 2), y0 = (int)JsRound(ty - size / 2), n = (int)JsRound(size);
            if (rim) rims.Add((x0 - 1, y0 - 1, n + 2, 0.75 * a));
            if (light) fills.Add((x0, y0, n, Math.Floor(c[0] * DARK + LIGHT), Math.Floor(c[1] * DARK + LIGHT), Math.Floor(c[2] * DARK + LIGHT), a));
            else fills.Add((x0, y0, n, c[0], c[1], c[2], a));
        }
        double tpm = K * Zoom;          // texels per metre
        var T = Table!;
        if (Resources)
        {
            foreach (var r in W.Resources)
            {
                if (!r.Alive || r.Type == "tree" || r.Amount <= 0) continue;
                int s = Seen(r.Pos.X, r.Pos.Z);
                if (s == 0) continue;
                var c = T[s == 2 ? 0 : 3][13];
                Dot(r.Pos.X, r.Pos.Z, r.Type == "stone" ? SIZE_STONE : SIZE_RESOURCE, c, c[3] / 255.0);
            }
            foreach (var n in W.WildNests)
            {
                int s = Seen(n.X, n.Z);
                if (s != 0) { var c = T[s == 2 ? 0 : 3][13]; Dot(n.X, n.Z, SIZE_RESOURCE, c, c[3] / 255.0); }
            }
        }
        var sel = _sel; sel.Clear(); sel.UnionWith(H.Selection);
        foreach (var b in W.Buildings)
        {
            if (!b.Alive || b.Parked || b.Owner == null) continue;
            int s = Sees(b.Owner) ? 2 : Seen(b.Pos.X, b.Pos.Z);
            if (s == 0 || s != 2 && HiddenFromMe(b)) continue;
            // MIRAGE 2.6.8: the holy city's hc_ rally buildings and its tavern hide on every minimap but their owner's
            // (Building.usl CRallyBuilding / CNPCSeller SetHideMiniMap; World.HiddenOnMinimap)
            if (W.HiddenOnMinimap(b, H.Me)) continue;
            Dot(b.Pos.X, b.Pos.Z, Math.Max(SIZE_BUILDING, Math.Min(12, b.Radius * 1.4 * tpm)), OwnerColor(b.Owner, s == 2 ? 1 : 4, b, sel), s == 2 ? 1 : 0.5, true, true);
        }
        var fog = H.Fog;
        foreach (var u in W.Units)
        {
            if (!u.Alive || u.Parked || u.Inside || u.Owner == null) continue;
            if (!Sees(u.Owner) && (fog != null && !fog.Visible(u.Pos.X, u.Pos.Z) || HiddenFromMe(u))) continue;
            Dot(u.Pos.X, u.Pos.Z, SIZE_UNIT, OwnerColor(u.Owner, 2, u, sel), 1, true);
        }
        foreach (var r in rims) FillRect(r.X, r.Y, r.N, 0, 0, 0, r.A);
        foreach (var f in fills) FillRect(f.X, f.Y, f.N, f.R, f.G, f.B, f.A);
    }

    /// <summary>a 1 texel line (the canvas' 1 px stroke between texel centres)</summary>
    void Line(double x0, double y0, double x1, double y1, double r, double g, double b, double a, int width = 1)
    {
        double len = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        int n = Math.Max(1, (int)Math.Ceiling(len));
        for (int i = 0; i <= n; i++)
        {
            double t = (double)i / n, x = x0 + (x1 - x0) * t, y = y0 + (y1 - y0) * t;
            int xi = (int)Math.Floor(x), yi = (int)Math.Floor(y);
            Blend(xi, yi, r, g, b, a);
            if (width > 1) { if (Math.Abs(x1 - x0) > Math.Abs(y1 - y0)) Blend(xi, yi + 1, r, g, b, a); else Blend(xi + 1, yi, r, g, b, a); }
        }
    }

    /// <summary>the camera's ground footprint (white lines)</summary>
    void DrawCamera((double X, double Z) F)
    {
        var corners = H.ViewCorners();
        if (corners == null || corners.Count < 2) return;
        for (int i = 0; i < corners.Count; i++)
        {
            var (ax, ay) = W2T(corners[i].X, corners[i].Z, F);
            var (bx, by) = W2T(corners[(i + 1) % corners.Count].X, corners[(i + 1) % corners.Count].Z, F);
            Line(ax, ay, bx, by, 255, 255, 255, 1);
        }
    }

    /// <summary>the markers: a square in the marker's colour, growing and fading once per pulse (the original's textures
    /// minimap_ping*.tga are not in the game's files)</summary>
    void DrawMarkers((double X, double Z) F)
    {
        double now = H.World.Time;
        Markers.RemoveAll(m => now - m.T >= m.Life);
        foreach (var m in Markers)
        {
            double age = now - m.T, ph = (age * m.Freq) % 1, s = SIZE_MARKER * m.Size / 100 * (0.35 + 0.65 * ph);
            var (x, y) = W2T(m.X, m.Z, F);
            double l = x - s / 2, t = y - s / 2, a = 1 - ph;
            Line(l, t, l + s, t, m.Color[0], m.Color[1], m.Color[2], a, 2);
            Line(l + s, t, l + s, t + s, m.Color[0], m.Color[1], m.Color[2], a, 2);
            Line(l + s, t + s, l, t + s, m.Color[0], m.Color[1], m.Color[2], a, 2);
            Line(l, t + s, l, t, m.Color[0], m.Color[1], m.Color[2], a, 2);
        }
    }

    // ---------------------------------------------------------------- the buttons (MiniMap2.usl:84-195)
    /// <summary>the frame's buttons: positions in the 270 px window, the art (hud/minimap_buttons.tga) by state, the help key</summary>
    public List<(string Id, int X, int Y, int W, int Hh, string Key)> Buttons() => new()
    {
        ("scape", 8, 225, 30, 30, "mm_show_landscape"), ("res", 38, 225, 30, 30, "mm_show_resources"),
        ("ping", 226, 49, 30, 30, "mm_show_mapping"), ("zoomin", 219, 219, 42, 21, "mm_zoom_in"), ("zoomout", 219, 240, 42, 21, "mm_zoom_out"),
    };
    /// <summary>the atlas name of a button's look</summary>
    public string ButtonArt(string id, bool hover) => id switch
    {
        "scape" => $"cmd_btn_minmap_scape_{(Scape ? "down" : "up")}{(hover ? "hover" : "")}",
        "res" => $"cmd_btn_minmap_res_{(Resources ? "down" : "up")}{(hover ? "hover" : "")}",
        "ping" => $"cmd_btn_minmap_pingall_{(H.InputMode == "mapping" ? "down" : "up")}{(hover ? "hover" : "")}",
        "zoomin" => $"cmd_btn_minmap_zoomin_{(Zoom >= 7 ? "dis" : hover ? "hov" : "std")}",
        _ => $"cmd_btn_minmap_zoomout_{(Zoom <= 1 ? "dis" : hover ? "hov" : "std")}",
    };
    public void PressButton(string id)
    {
        switch (id)
        {
            case "scape": Scape = !Scape; Invalidate(); break;
            case "res": Resources = !Resources; Invalidate(); break;
            case "ping": H.SetInputMode(H.InputMode == "mapping" ? null : "mapping"); break;
            case "zoomin": Zoom = Math.Min(7, Zoom + 1); Invalidate(); break;
            case "zoomout": Zoom = Math.Max(1, Zoom - 1); Invalidate(); break;
        }
    }
}
