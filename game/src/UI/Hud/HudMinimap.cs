// The minimap window in the bottom right corner (hud/minimap_background.tga, 270 x 270; IngameScreen.usl:260): the
// 230 px map control at (30, 32) showing the 256 x 256 picture of MinimapModel turned by its angle over the turned
// background hud/minimap_rotmap.tga, the mission's markers (ui/mission.js drawMinimap: a ring running outwards in the
// marker's colour), the frame's buttons (landscape, resources, map ping, zoom). Mouse: a left click or drag moves the
// camera there (the map-ping mode pings instead); a right click gives the selection the order of a right-click on bare
// ground there - walk / sail, Shift queues - or sets the rally point of the selected producers (input.js minimapDown /
// minimapMove / minimapUp).
//
// Ported from remake/src/ui/minimap.js (Minimap.update, minimapButtons), ui/mission.js drawMinimap and the minimap part
// of ui/input.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Commands;
using PwRemake.Sim.World;
using PwRemake.UI.Hud.Logic;

namespace PwRemake.UI.Hud;

public partial class HudMinimap : HudPanel
{
    public const int W = 270, H = 270;
    static readonly Rect2 MAP = new(30, 32, 230, 230);
    const float K = 230f / MinimapModel.TEX;

    public MinimapModel? Model { get; private set; }
    ImageTexture? _tex;
    Image? _img;
    bool _drag;

    public HudMinimap(Hud hud) : base(hud) { Name = "Minimap"; Size = new Vector2(W, H); }

    /// <summary>sets the minimap up for a map (main.js: new Minimap(G, canvas, S, table); buildBackground)</summary>
    public void Init(MinimapSource src, int[][][]? table)
    {
        Model = new MinimapModel(Hud.Host, src, table);
        Model.BuildBackground();
        Model.Update(0, true);
        Upload();
    }

    void Upload()
    {
        if (Model == null) return;
        // (one image, its data replaced: a new Image 30 times a second)
        if (_img == null) _img = Image.CreateFromData(MinimapModel.TEX, MinimapModel.TEX, false, Image.Format.Rgba8, Model.Tex);
        else _img.SetData(MinimapModel.TEX, MinimapModel.TEX, false, Image.Format.Rgba8, Model.Tex);
        if (_tex == null) _tex = ImageTexture.CreateFromImage(_img); else _tex.Update(_img);
        QueueRedraw();
    }

    /// <summary>a frame of the game (30 redraws a second; the mission's markers move every frame)</summary>
    public void Tick(double dt)
    {
        if (Model == null) return;
        var P = PwRemake.View.PerfProbe.P;
        long t0 = P != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0;
        long pt0 = Model.PaintTicks; int pn0 = Model.Paints;
        bool up = Model.Update(dt);
        if (P != null) { long t = System.Diagnostics.Stopwatch.GetTimestamp(); P.Add("hud:minimap update", t - t0, 0); t0 = t; if (Model.Paints != pn0) P.Add("hud:minimap paint", Model.PaintTicks - pt0, 0); }
        if (up) { Upload(); if (P != null) P.Add("hud:minimap upload", System.Diagnostics.Stopwatch.GetTimestamp() - t0, 0); }
        else if (Hud.Host is IHudHost h && h.Mission != null && h.Mission.Markers.Count > 0) QueueRedraw();
        else if (Extra != null) QueueRedraw();
    }

    protected override void Paint()
    {
        var full = new Rect2(0, 0, W, H);
        Solid.Add(new Rect2(0, 0, W, H));
        var m = Model;
        // the window's picture, the map control over it (minimap.js: the canvas over .mmframe)
        if (!HudSprites.Pic(this, "hud_minimap_background.png", full)) DrawRect(full, new Color(0.1f, 0.07f, 0.04f, 0.8f), false, 3);
        // the map control: background picture and map, turned about the texture's centre
        if (m != null)
        {
            float a = (float)m.Angle;
            var c = MAP.Position + new Vector2(115, 115);
            DrawSetTransform(c, a, new Vector2(K, K));
            var o = new Vector2(-MinimapModel.TEX / 2f, -MinimapModel.TEX / 2f);
            HudSprites.Pic(this, "hud_minimap_rotmap.png", new Rect2(o, new Vector2(MinimapModel.TEX, MinimapModel.TEX)));
            if (_tex != null) DrawTextureRect(_tex, new Rect2(o, new Vector2(MinimapModel.TEX, MinimapModel.TEX)), false);
            DrawSetTransform(Vector2.Zero, 0, Vector2.One);
            DrawMissionMarkers(m);
        }
        // the frame's buttons
        if (m != null)
            foreach (var (id, x, y, w, hh, key) in m.Buttons())
            {
                var r = new Rect2(x, y, w, hh);
                HudSprites.Draw(this, m.ButtonArt(id, IsHover("btn_" + id)), null, r);
                var k = key; var bid = id;
                var h = AddHit(r, "btn_" + id, () => Tooltips.ButtonTip(Hud.Data, k));
                h.Click = (_, _, _) => { m.PressButton(bid); Hud.Host.UiSound("UI_click"); m.Update(0, true); Upload(); };
            }
    }

    // campaign: the quest / hint markers of the mission (ui/mission.js drawMinimap): a ring running outwards (MM: frequency
    // 0.7), a black dot with the marker's colour inside
    /// <summary>a mode's own markers (the sequel's: sequel_ui.js drawMinimap - a ring that breathes)</summary>
    public Func<IEnumerable<(double X, double Z, int Color)>>? Extra;

    void DrawMissionMarkers(MinimapModel m)
    {
        if (Extra != null)
        {
            float t = (float)(Time.GetTicksMsec() / 1000.0);
            foreach (var (x, z, c) in Extra())
            {
                var (tx, ty) = m.W2M(x, z);
                var p = MAP.Position + new Vector2((float)tx, (float)ty) * K;
                var col = HudSprites.Rgb(c);
                DrawArc(p, (4 + 2 * Mathf.Sin(t * 4)) * K, 0, Mathf.Tau, 24, col, 2 * K);
                DrawCircle(p, 1.6f * K, col);
            }
        }
        if (Hud.Host is not IHudHost host || host.Mission == null) return;
        foreach (var mk in host.Mission.Markers)
        {
            if (!PwRemake.UI.Mission.MissionPresenter.MarkerLit(mk)) continue;
            var (wx, wz) = host.MapToWorld(mk.X, mk.Y);
            var (tx, ty) = m.W2M(wx, wz);
            var p = MAP.Position + new Vector2((float)tx, (float)ty) * K;
            var col = HudSprites.Rgb(mk.Color);
            float k = (float)host.Mission.RingPhase(mk, 0.85);
            DrawArc(p, (4 + k * 9 + 1) * K, 0, Mathf.Tau, 32, new Color(0, 0, 0, 0.55f * (1 - k)), 2 * K);
            DrawArc(p, (4 + k * 9) * K, 0, Mathf.Tau, 32, new Color(col, 1 - k * 0.85f), 2 * K);
            DrawCircle(p, 4.2f * K, Colors.Black);
            DrawCircle(p, 3f * K, col);
        }
    }

    (double X, double Z) MapCoords(Vector2 local)
    {
        var t = (local - MAP.Position) / K;
        return Model!.M2W(t.X, t.Y);
    }
    bool InMap(Vector2 p) => MAP.HasPoint(p) && p.DistanceTo(MAP.GetCenter()) <= 118;

    protected override void OnLeftDown(InputEventMouseButton e, Hit? h)
    {
        if (h != null || Model == null || !InMap(e.Position)) return;
        if (Hud.Host.InputMode == "mapping") return;
        var (x, z) = MapCoords(e.Position);
        if (ModeOrder(x, z, e.ShiftPressed)) return;
        Hud.Host.MoveCamera(x, z);
        _drag = true;
    }

    protected override void OnMotion(InputEventMouseMotion e)
    {
        if (!_drag || Model == null) return;
        if ((e.ButtonMask & MouseButtonMask.Left) == 0) { _drag = false; return; }
        var (x, z) = MapCoords(e.Position);
        Hud.Host.MoveCamera(x, z);
    }

    protected override bool OnLeftUp(InputEventMouseButton e, Hit? h)
    {
        bool was = _drag;
        _drag = false;
        if (h != null || Model == null || !InMap(e.Position)) return was;
        if (Hud.Host.InputMode == "mapping") { var (x, z) = MapCoords(e.Position); Hud.MapPing(x, z); Hud.Host.SetInputMode(null); return true; }
        return was;
    }

    /// <summary>remake (Kacper: "click on the minimap when I have units selected and order them to move / attack there"):
    /// a left click on the map while an order mode is armed gives that order at the point - Move (M) walks there, Attack
    /// (A) and Attack-move (N) attack-move there (the units fight whatever they meet on the way), Rally sets the rally
    /// point; Shift queues and keeps the mode, as a click in the world does (GameInput.ModeClick). The right button gives
    /// the plain move (below). Other modes (place, repair, target ...) keep the camera click.</summary>
    bool ModeOrder(double x, double z, bool shift)
    {
        var host = Hud.Host; var mode = host.InputMode;
        if (mode is not ("move" or "amove" or "attack" or "rally")) return false;
        var me = host.Me;
        if (mode == "rally")
        {
            var prods = host.Selection.Where(b => Hud.Model.G.CanRally(b)).Select(b => b.Id).ToArray();
            if (prods.Length == 0) return false;
            Hud.Model.G.Send(new SetRallyCommand { Producers = prods, X = x, Z = z });
        }
        else
        {
            var units = host.Selection.OfType<Unit>().Where(u => u.Alive && u.Owner == me && !u.Inside).ToList();
            if (units.Count == 0) return false;
            Hud.Model.G.Send(new GroupMoveCommand { Units = units.Select(u => u.Id).ToArray(), Type = mode == "move" ? "move" : "attackmove", X = x, Z = z, Queue = shift });
            host.Ordered(mode == "move" ? "move" : mode == "attack" ? "attack" : "amove", units);
        }
        if (!shift) host.SetInputMode(null);
        return true;
    }

    public override void _GuiInput(InputEvent e)
    {
        // the right button on the map, on release: the order of a right-click on bare ground (never gathering)
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false } mb && Model != null && InMap(mb.Position) && HitAt(mb.Position) == null)
        {
            var host = Hud.Host;
            var (x, z) = MapCoords(mb.Position);
            if (host.InputMode != null) host.SetInputMode(null);
            else
            {
                var me = host.Me;
                var units = host.Selection.OfType<Unit>().Where(u => u.Alive && u.Owner == me && !u.Inside).ToList();
                if (units.Count > 0)
                {
                    Hud.Model.G.Send(new GroupMoveCommand { Units = units.Select(u => u.Id).ToArray(), Type = "move", X = x, Z = z, Queue = mb.ShiftPressed });
                    host.Ordered("move", units);
                }
                else
                {
                    var prods = host.Selection.Where(b => Hud.Model.G.CanRally(b)).Select(b => b.Id).ToArray();
                    if (prods.Length > 0) Hud.Model.G.Send(new SetRallyCommand { Producers = prods, X = x, Z = z });
                }
            }
            AcceptEvent();
            return;
        }
        base._GuiInput(e);
    }
}
