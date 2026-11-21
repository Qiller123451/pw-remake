// Mouse and keyboard in the game (ui/input.js): selection (click, box, double-click, control groups), orders by the
// right mouse button with the cursor's logic (cursorFor), the formation arrow, modes armed by keys or the command bar
// (move, attack, attack-move, rally point, building placement with the 2 m placement grid and wall lines, special
// move targets, mines), the original's accelerators (Scripts/Game/options/Keys.srf; docs/spec/formations.md) and the
// original mouse cursors (assets/ui/cur + hotspots.json). The camera's own controls are RtsCameraInput's
// (game/src/View/World/RtsCamera.cs, the camera part of input.js).
// Everything the player orders goes to the simulation as a command through the session (GameSession.Submit) - never
// a world method for an order directly; the world is only read here (picking, what an order would do).
// Mouse events reach this node through _UnhandledInput: a HUD control that stops the mouse keeps it from the world.
// Ported from remake/src/ui/input.js (pw-exporter 98c7703); this file: state, mouse, keys, the frame.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.UI.PlayerInput;

public partial class GameInput : Node
{
    static readonly Logger L = Log.For("UI.Input");
    readonly GameSession G;
    GameWorld W => G.World;

    // ---- state
    /// <summary>the armed order: move | attack | amove | rally | place | target | lay | repair | mapping | callback | null</summary>
    public string? Mode { get; private set; }
    public object? ModeData { get; private set; }
    /// <summary>the entity under the mouse (refreshed 14 times a second)</summary>
    public Entity? Hover { get; private set; }
    double _hoverT;
    /// <summary>control groups 0..9 and the camera views kept under a digit with nothing selected</summary>
    public readonly Dictionary<string, List<Entity>> Groups = new();
    readonly Dictionary<string, (double X, double Z, double Yaw, double Dist)> _views = new();
    (string? K, ulong T) _lastGroupKey;
    (ulong T, Entity? E) _lastClick;
    public string? SelGroup { get; private set; }
    /// <summary>F2: the camera follows this unit (LinkCameraToPath), F3 stops it</summary>
    Unit? _follow;
    // mouse
    Vector2 _mouse, _start;
    bool _down, _dragging, _rdown, _pan, _rot;
    readonly HashSet<string> _clickTakers = new();
    Vector3? _rg;
    /// <summary>the formation arrow being drawn (right drag): press point, direction, spacing, the places, the tip</summary>
    public FormationArrow? Arrow { get; private set; }
    /// <summary>the box being dragged (screen)</summary>
    public Rect2? DragBox => _dragging ? new Rect2(Math.Min(_start.X, _mouse.X), Math.Min(_start.Y, _mouse.Y), Math.Abs(_mouse.X - _start.X), Math.Abs(_mouse.Y - _start.Y)) : null;
    readonly HashSet<Key> _keys = new();

    public sealed class FormationArrow
    {
        public double X, Z, Dir, Len, Spacing, TipX, TipZ;
        public List<(double X, double Z)> Slots = new();
    }

    public GameInput(GameSession g) { G = g; Name = "GameInput"; }
    public GameInput() : this(null!) { }

    public override void _Ready() => Cursors.Load(G.Assets);

    // ======================================================================================== selection helpers
    public List<Unit> MyUnits() => G.Selection.OwnUnits;

    /// <summary>an entity leaves the control groups (it died, was removed or given away)</summary>
    public void Forget(Entity e) { foreach (var l in Groups.Values) l.Remove(e); }

    /// <summary>what a saved game keeps of the input (docs/SAVES.md): the control groups, the camera views under the
    /// digits, the group selected</summary>
    public System.Text.Json.Nodes.JsonObject SaveState()
    {
        var g = new System.Text.Json.Nodes.JsonObject();
        foreach (var (k, l) in Groups) g[k] = new System.Text.Json.Nodes.JsonArray(l.Where(x => x.Alive).Select(x => (System.Text.Json.Nodes.JsonNode)x.Id).ToArray());
        var v = new System.Text.Json.Nodes.JsonObject();
        foreach (var (k, c) in _views) v[k] = new System.Text.Json.Nodes.JsonArray(c.X, c.Z, c.Yaw, c.Dist);
        return new System.Text.Json.Nodes.JsonObject { ["groups"] = g, ["views"] = v, ["selGroup"] = SelGroup };
    }
    /// <summary>SaveState() of a saved game back (entities by id)</summary>
    public void RestoreState(System.Text.Json.Nodes.JsonNode? s)
    {
        if (s == null) return;
        Groups.Clear(); _views.Clear();
        if (s["groups"] is System.Text.Json.Nodes.JsonObject g)
            foreach (var (k, l) in g) Groups[k] = (l as System.Text.Json.Nodes.JsonArray ?? new()).Select(x => W.ById(x.Int())).Where(x => x is { Alive: true }).Cast<Entity>().ToList();
        if (s["views"] is System.Text.Json.Nodes.JsonObject v)
            foreach (var (k, a) in v) if (a is System.Text.Json.Nodes.JsonArray c && c.Count >= 4) _views[k] = (c[0].Num(), c[1].Num(), c[2].Num(), c[3].Num());
        SelGroup = s["selGroup"] is System.Text.Json.Nodes.JsonValue sg ? sg.Str() : null;
    }

    /// <summary>GroupBar.OnChangeSelection: the pressed group button comes up when the selection is no longer its group</summary>
    public void SelectionChanged()
    {
        MarkSelectionChanged();
        if (SelGroup == null) return;
        var list = Groups.GetValueOrDefault(SelGroup)?.Where(x => x.Alive).ToList() ?? new();
        if (list.Count != G.Selection.Count || list.Any(x => !G.Selection.Contains(x))) SelGroup = null;
    }

    /// <summary>a cutscene or a menu: buttons up, no arrow, no box, no mode (the placement ghost and its dots go)</summary>
    public void CancelMouse() { _down = _dragging = _rdown = _pan = _rot = false; Arrow = null; if (Mode != null) SetMode(null); }

    public void SetMode(string? m, object? data = null)
    {
        if (Mode == "place" && m != "place") ClearGhost();
        Mode = m;
        ModeData = data;
        if (m == "place" && data is ActionDef a) MakeGhost(a);
    }

    bool Blocked => G.Cinematic || G.MenuOpen || G.Over && G.Paused;

    // ======================================================================================== mouse
    // camera (GameCameraModule.usl): middle drag pans, middle + right or Alt + left drag turns, Alt + right drag pans
    // (RtsCameraInput). Right button: an order on release - dragged more than 2 m on the ground it draws the formation
    // arrow (GameInputController.usl:1474-1630: the group goes to the press point, facing the arrow).
    /// <summary>Every mouse event, before the interface takes it: the position (the cursor and the screen-edge arrows
    /// followed only unhandled motions - over a panel the last position stayed, at the screen's edge the edge cursor
    /// stayed for good), the end of a drag released over a panel, and a note in the log of what took a click in the
    /// world's place (a control that is not the HUD's: the player "cannot select anything").</summary>
    public override void _Input(InputEvent ev)
    {
        if (G == null) return;
        if (ev is InputEventMouse m) _mouse = m.Position;
        if (ev is not InputEventMouseButton mb) return;
        if (!mb.Pressed)
        {
            bool overHud = G.Hud?.IsOverHud(mb.Position) == true;
            if (mb.ButtonIndex == MouseButton.Middle && overHud) { _rot = false; _pan = false; }
            else if (mb.ButtonIndex == MouseButton.Right && overHud) { _rot = false; _pan = false; _rdown = false; Arrow = null; }
            else if (mb.ButtonIndex == MouseButton.Left && overHud)
            {
                _rot = false;
                if (_down && _dragging && Mode == null) { _down = _dragging = false; BoxSelect(mb.ShiftPressed, mb.CtrlPressed); }   // the box ends at the panel
                else _down = _dragging = false;
            }
            return;
        }
        if (mb.ButtonIndex == MouseButton.Left && GetViewport()?.GuiGetHoveredControl() is { } c
            && !(G.Hud is Node hud && hud.IsAncestorOf(c)) && _clickTakers.Add(c.GetPath().ToString()))
            L.Info($"a click went to {c.GetPath()} ({c.GetType().Name}, mouse filter {c.MouseFilter}, visible {c.IsVisibleInTree()}) - not to the world");
    }

    public override void _UnhandledInput(InputEvent ev)
    {
        if (G == null || G.Sim == null) return;   // (not G.Mission: a skirmish and the sequel's missions have none - their input was dropped)
        if (ev is InputEventKey k) { OnKey(k); return; }
        if (Blocked) return;
        if (ev is InputEventMouseMotion mm) { OnMove(mm); return; }
        if (ev is not InputEventMouseButton mb) return;
        _mouse = mb.Position;
        if (mb.ButtonIndex is MouseButton.WheelUp or MouseButton.WheelDown)
        {
            // (the placement ghost turns with the wheel: the camera input zooms otherwise)
            if (mb.Pressed && Mode == "place" && _ghost != null) { _ghost.Rot += Math.Sign(mb.ButtonIndex == MouseButton.WheelDown ? 1 : -1) * Math.PI / 8; GetViewport().SetInputAsHandled(); }
            return;
        }
        if (mb.Pressed) OnDown(mb); else OnUp(mb);
    }

    void OnDown(InputEventMouseButton e)
    {
        if (e.ButtonIndex == MouseButton.Middle) { if (_rdown) { _rdown = false; Arrow = null; _rot = true; } else _pan = true; return; }
        if (e.ButtonIndex == MouseButton.Right)
        {
            if (_pan) { _pan = false; _rot = true; return; }
            if (e.AltPressed) { _pan = true; return; }
            if (Mode != null) { SetMode(null); return; }
            G.Hud?.Disarm();
            _rdown = true; _start = e.Position; _rg = GroundPoint(e.Position); Arrow = null;
            return;
        }
        if (e.ButtonIndex != MouseButton.Left) return;
        if (e.AltPressed) { _rot = true; return; }
        if (Mode != null) { ModeClick(e.Position, e.ShiftPressed); return; }
        _down = true; _start = e.Position; _dragging = false;
    }

    void OnMove(InputEventMouseMotion e)
    {
        _mouse = e.Position;
        if (_rdown && _rg != null) DragArrow(e.Position);
        if (_down && !_dragging && _mouse.DistanceTo(_start) > 6) _dragging = true;
    }

    void OnUp(InputEventMouseButton e)
    {
        if (e.ButtonIndex == MouseButton.Middle) { _rot = false; _pan = false; return; }
        if (e.ButtonIndex == MouseButton.Right)
        {
            if (_rot || _pan) { _rot = false; _pan = false; return; }
            if (!_rdown) return;
            _rdown = false;
            var a = Arrow;
            Arrow = null;
            if (a != null) ArrowOrder(a, e.ShiftPressed, e.CtrlPressed); else RightClick(_start, e.ShiftPressed, e.CtrlPressed);
            return;
        }
        if (e.ButtonIndex == MouseButton.Left && _rot) { _rot = false; return; }
        if (e.ButtonIndex == MouseButton.Left && Mode == "place" && _ghost is { Wall: true, Pressed: true })
        {
            _ghost.Pressed = false;
            WallRelease(e.ShiftPressed, e.CtrlPressed);
            return;
        }
        if (e.ButtonIndex != MouseButton.Left || !_down) return;
        _down = false;
        if (_dragging)
        {
            _dragging = false;
            BoxSelect(e.ShiftPressed, e.CtrlPressed);
            return;
        }
        var t = EntityAt(_mouse);
        ulong now = Time.GetTicksMsec();
        if (t != null && _lastClick.E == t && now - _lastClick.T < 350 && t.Owner == G.Me && t is not ResNode)
        {
            // double-click (GameInputController.usl:1353): the same class on screen - Ctrl: the same type (characters,
            // animals ...), Shift: all of them; on a hero: all heroes
            var all = W.Units.Cast<Entity>().Concat(W.Buildings).Where(x => x.Alive && x.Owner == G.Me && !x.Inside && !x.Parked && !x.Unselectable && OnScreen(x));
            Func<Entity, bool> same = e.ShiftPressed ? _ => true : t.Def?.Unique == true ? x => x.Def?.Unique == true : e.CtrlPressed ? x => x.Kind == t.Kind && x.Cls == t.Cls : x => x.Name == t.Name;
            G.Selection.Set(all.Where(same).ToList());
        }
        else if (t != null)
        {
            // Shift adds (never removes), Ctrl adds or removes - buildings with buildings, units win over buildings, own
            // over others (GameInputController.usl MT_MouseLBtnUp; Selection.Combine)
            if (e.ShiftPressed || e.CtrlPressed) G.Selection.Modify(new[] { t }, e.ShiftPressed, e.CtrlPressed);
            else G.Selection.Set(new[] { t });
        }
        else if (ClickMark(_mouse, e.ShiftPressed)) _hoverT = 0;     // a question mark: selected, its tooltip at once
        else if (!e.ShiftPressed && !e.CtrlPressed) G.Selection.Set(Array.Empty<Entity>());
        _lastClick = (now, t);
    }

    void BoxSelect(bool shift, bool ctrl)
    {
        float x0 = Math.Min(_start.X, _mouse.X), x1 = Math.Max(_start.X, _mouse.X), y0 = Math.Min(_start.Y, _mouse.Y), y1 = Math.Max(_start.Y, _mouse.Y);
        // an entity is inside when its centre or a good part of its rectangle is in the box
        bool InBox(Entity u)
        {
            var r = PickRect(u);
            float sx, sy;
            if (r is { } rr) { sx = (rr.X0 + rr.X1) / 2; sy = (rr.Y0 + rr.Y1) / 2; }
            else
            {
                var c = G.Entities.PosOf(u); c.Y += (float)((u as Unit)?.Height ?? 2) * 0.4f;
                if (G.Renderer.Camera.IsPositionBehind(c)) return false;
                var s = G.Renderer.Camera.UnprojectPosition(c); sx = s.X; sy = s.Y;
            }
            return (sx >= x0 && sx <= x1 && sy >= y0 && sy <= y1) ||
                   (r is { } q && Math.Max(0, Math.Min(x1, q.X1) - Math.Max(x0, q.X0)) * Math.Max(0, Math.Min(y1, q.Y1) - Math.Max(y0, q.Y0)) > 0.4 * q.Area);
        }
        List<Entity> list = W.Units.Where(u => u.Alive && u.Owner == G.Me && !u.Inside && !u.Autonomous && !u.Parked && !u.Unselectable && G.Entities.Shown(u) && InBox(u)).Cast<Entity>().ToList();
        // no unit in the box: the own buildings in it (not the wall pieces - a wall line would swamp the selection)
        if (list.Count == 0) list = W.Buildings.Where(b => b.Alive && b.Owner == G.Me && !b.Unselectable && b.Def?.WallKind != "wall" && InBox(b)).Cast<Entity>().ToList();
        // Shift: the boxed ones and the selection, Ctrl: each boxed one in or out - sorted out as a Shift click is
        // (units win over buildings; Selection.Combine)
        if (shift || ctrl) G.Selection.Modify(list, shift, ctrl);
        else G.Selection.Set(list);
    }

    public bool OnScreen(Entity e)
    {
        var c = G.Renderer.Camera;
        var p = G.Entities.PosOf(e);
        if (c.IsPositionBehind(p)) return false;
        var s = c.UnprojectPosition(p);
        var size = GetViewport().GetVisibleRect().Size;
        return s.X > 0 && s.Y > 0 && s.X < size.X && s.Y < size.Y;
    }

    // ======================================================================================== control groups (GroupMgr.usl)
    /// <summary>SetGroup: the own selection becomes group k; with nothing selected the camera view is kept instead</summary>
    public void AssignGroup(string k)
    {
        var list = G.Selection.Items.Where(x => x.Alive && x.Owner == G.Me).ToList();
        Groups[k] = list;
        if (list.Count == 0) { _views[k] = (G.Camera.X, G.Camera.Z, G.Camera.Yaw, G.Camera.TDist); G.Message($"View {k} saved"); }
        else G.Message($"Group {k} assigned");
    }
    void JumpToGroup(string k, List<Entity> list)
    {
        if (list.Count > 0) G.LookAt(list.Average(u => u.Pos.X), list.Average(u => u.Pos.Z));
        else if (_views.TryGetValue(k, out var v)) { G.Camera.X = v.X; G.Camera.Z = v.Z; G.Camera.Yaw = v.Yaw; G.Camera.TDist = v.Dist; }
    }
    /// <summary>the group bar's buttons (CGroupButton): "select" | "assign" | "double"</summary>
    public void GroupButton(string k, string how)
    {
        var list = Groups.GetValueOrDefault(k)?.Where(x => x.Alive).ToList() ?? new();
        if (how == "assign") { AssignGroup(k); return; }
        if (how == "double") { var sel = G.Selection.Items.Where(x => x.Alive).ToList(); if (list.Count > 0 || _views.ContainsKey(k)) JumpToGroup(k, sel.Count > 0 ? sel : new()); return; }
        if (SelGroup == k || list.Count == 0) return;
        G.Selection.Set(list);
        SelGroup = k;
    }

    // ======================================================================================== keyboard
    // The original's accelerators (Keys.srf; docs/spec/formations.md §B), by letter like the original's virtual keys:
    // Q A Y build / produce rows, X stances, E D C special-move rows (then 1 .. 0 press the row's buttons), V attack, B
    // move, W idle worker, H headquarters, T Z U I O P L K the next HQ / barracks / farm / machine maker / harbour /
    // temple / weapon smith / tavern, Space the last event, Del self-destruct. The remake's own: N attack-move, S stop,
    // R rally point, Ctrl+U level up, J quest log (the mission UI), Home centre, A attack (with units; V too), F the
    // build / produce menu's middle row (A's with buildings only), G the last placed building again.
    static readonly string[] HQS = { "hu_fireplace", "aje_resource_collector", "ninigi_fireplace", "seas_headquarters" };
    static readonly Dictionary<char, string[]> PICK_KEYS = new()
    {
        ['h'] = HQS, ['t'] = HQS,
        ['z'] = new[] { "hu_arena", "aje_rodeo", "ninigi_dojo", "seas_barracks" },
        ['u'] = new[] { "hu_small_animal_farm", "aje_small_farm", "ninigi_animal_farm", "seas_garage" },
        ['i'] = new[] { "hu_machine_maker", "ninigi_engineer", "seas_garage" },
        ['o'] = new[] { "hu_harbour", "aje_floating_harbour", "ninigi_harbour", "seas_carrier" },
        ['p'] = new[] { "hu_temple", "aje_temple", "ninigi_temple" },
        ['l'] = new[] { "hu_weapons_smith", "aje_weapons_builder", "ninigi_weapon_maker" },
        ['k'] = new[] { "hu_tavern", "aje_cook_house", "ninigi_teahouse" },
    };

    // a key's letter like the original's virtual key (the letter on the key; other layouts: its position)
    static char? LetterOf(InputEventKey e)
    {
        if (e.Keycode >= Key.A && e.Keycode <= Key.Z) return (char)('a' + (e.Keycode - Key.A));
        if (e.PhysicalKeycode >= Key.A && e.PhysicalKeycode <= Key.Z) return (char)('a' + (e.PhysicalKeycode - Key.A));
        return null;
    }
    static int? DigitOf(InputEventKey e)
    {
        var k = e.Keycode != Key.None ? e.Keycode : e.PhysicalKeycode;
        if (k >= Key.Key0 && k <= Key.Key9) return (int)(k - Key.Key0);
        if (k >= Key.Kp0 && k <= Key.Kp9) return (int)(k - Key.Kp0);
        return null;
    }

    void OnKey(InputEventKey e)
    {
        var code = e.Keycode != Key.None ? e.Keycode : e.PhysicalKeycode;
        if (!e.Pressed) { _keys.Remove(code); return; }
        _keys.Add(code);
        if (e.Echo) return;
        void Handled() => GetViewport()?.SetInputAsHandled();   // (the session may have ended on the key: no viewport)
        if (G.Cinematic) return;                                  // (the mission UI takes the cutscene's keys)
        if (G.MenuShown) return;                                  // (the pause menu has its own keys)
        if (code == Key.Escape)
        {
            if (G.Hud?.Armed == true) G.Hud.Disarm();                            // CmdBar_Accel_Abort
            else if (Mode != null) SetMode(null);
            else G.OpenMenu();
            Handled(); return;
        }
        if (code == Key.F1) { G.OpenHelp(); Handled(); return; }            // ShowHelp (Keys.srf F1): the help book
        // the chat (ChatWindow.usl): Enter to all, Ctrl+Enter to the allies, Shift+Enter to the enemies
        if (code is Key.Enter or Key.KpEnter && !e.AltPressed && G.HudView != null) { G.HudView.Chat.Open(e.CtrlPressed ? "allies" : e.ShiftPressed ? "enemies" : "all"); Handled(); return; }
        if (G.Over) return;
        if (code == Key.Tab) { G.Hud?.TogglePyramid(); Handled(); return; }   // SwitchPyramidForward / Backward
        var units = MyUnits();
        var digit = DigitOf(e);
        bool numpad = code >= Key.Kp0 && code <= Key.Kp9;
        if (digit != null && !(numpad && G.Hud?.Armed != true))
        {
            var k = digit.Value.ToString();
            // a command bar row is armed: the digit presses its button (CmdBar_Accel_1 .. 0)
            if (G.Hud?.Armed == true && !e.CtrlPressed) { G.Hud.PressArmed((digit.Value + 9) % 10); Handled(); return; }
            if (e.CtrlPressed) { AssignGroup(k); Handled(); return; }
            var list = Groups.GetValueOrDefault(k)?.Where(x => x.Alive).ToList() ?? new();
            ulong now = Time.GetTicksMsec();
            bool again = _lastGroupKey.K == k && now - _lastGroupKey.T < 300;
            _lastGroupKey = (k, now);
            // GameInputController.usl GetGroup: a press selects the group (an empty one: nothing); a second press within
            // 300 ms takes the camera to it - or to the view kept under the digit
            if (again) JumpToGroup(k, list);
            if (e.ShiftPressed) G.Selection.AddRange(list); else G.Selection.Set(list);
            Handled(); return;
        }
        var L = LetterOf(e);
        bool plain = !e.CtrlPressed && !e.AltPressed && !e.MetaPressed;
        if (L != null && plain)
        {
            // command bar rows (CmdBar_Accel_R..): Q / A / Y the build menu (anyone who can build) or the produce menu,
            // X the stances, E / D / C the special moves; rows counted from the bottom
            bool builds = units.Any(u => u.CanBuild);
            // (the remake: A attacks when units or an aimable tower are selected - Kacper 2026-11-11; the middle row is
            // also on F)
            bool attacks = units.Count > 0 || G.Selection.Items.Any(x => W.CanAimTower(x));
            (string, int)? row = L switch
            {
                'q' => (builds ? "build" : "produce", 2), 'a' when !attacks => (builds ? "build" : "produce", 1), 'f' => (builds ? "build" : "produce", 1), 'y' => (builds ? "build" : "produce", 0),
                'x' => ("stances", 0), 'e' => ("special", 2), 'd' => ("special", 1), 'c' => ("special", 0), _ => null,
            };
            if (row is var (kind, r)) { if (G.Hud != null && !G.Hud.ArmRow(kind, r)) G.Hud.Disarm(); Handled(); return; }
            G.Hud?.Disarm();
            if (PICK_KEYS.TryGetValue(L.Value, out var pick)) { PickNext(pick, L == 'h'); Handled(); return; }
            switch (L)
            {
                case 'g': PlaceLast(); Handled(); return;                                                        // the remake's: the last building again
                case 'a':
                case 'v': if (units.Count > 0 || G.Selection.Items.Any(x => W.CanAimTower(x))) SetMode("attack"); Handled(); return;   // CmdBar_Accel_R30: Attack
                case 'b': if (units.Count > 0 || G.Selection.Items.Any(x => W.IsMovingHarbour(x) && x is Building { Built: true })) SetMode("move"); Handled(); return;   // R40: Walk
                case 'n': if (units.Count > 0) SetMode("amove"); Handled(); return;
                case 's': if (units.Count > 0) Order(units, "stop"); Handled(); return;
                case 'r': if (G.Selection.Items.Any(x => G.CanRally(x))) SetMode("rally"); Handled(); return;
                case 'w': G.SelectIdleWorker(e.ShiftPressed ? "add" : "next"); Handled(); return;              // PickIdleWorker / AddIdleWorker
            }
        }
        if (L == 'r' && e.CtrlPressed && e.ShiftPressed && !e.AltPressed) { G.ToggleRanges(); Handled(); return; }   // ToggleRangeMarkersAttack (Keys.srf): the range circles
        if (L == 's' && e.CtrlPressed) { G.QuickSave(); Handled(); return; }                                 // Quicksave (input.js; GameSession.Saves.cs)
        if (L == 'w' && e.CtrlPressed) { G.SelectIdleWorker("all"); Handled(); return; }                    // AllIdleWorker
        if (L == 'a' && e.CtrlPressed)                                                                      // AllUnitsOnScreen
        {
            G.Selection.Set(W.Units.Where(u => u.Alive && u.Owner == G.Me && !u.Inside && !u.Parked && u.Cls != "SHIP" && OnScreen(u)).Cast<Entity>().ToList());
            Handled(); return;
        }
        if (L == 'u' && e.CtrlPressed) { if (units.Count == 1) G.LevelUp(units[0]); Handled(); return; }
        switch (code)
        {
            case Key.F2: _follow = G.Selection.Items.OfType<Unit>().FirstOrDefault(x => x.Alive); Handled(); break;    // LinkCameraToPath
            case Key.F3: _follow = null; Handled(); break;                                                       // FreeCamera
            case Key.F5: G.QuickSave(); Handled(); break;                                                        // quick save / quick load (input.js)
            case Key.F8: G.QuickLoad(); Handled(); break;
            case Key.Pause: case Key.KpDivide: G.TogglePause(); Handled(); break;                               // TacticalPause / StopTime
            case Key.Space: LastEvent(); Handled(); break;                                                       // JumpToLastEvent
            case Key.Home: G.CenterOn(G.Selection.Items.FirstOrDefault(x => x.Alive) ?? G.HomeEntity()); Handled(); break;
            case Key.Delete: if (e.ShiftPressed) G.KillSelected(); else G.KillSelected(1); Handled(); break;     // Kill (the first) / KillAll
            case Key.Period: case Key.KpPeriod: G.SelectIdleWorker(); Handled(); break;
            case Key.Backspace: if (G.Hud != null) G.Hud.Visible = !G.Hud.Visible; Handled(); break;            // HideUI
            case Key.KpAdd: G.SetSpeed("+"); Handled(); break;                                                   // AccTime
            case Key.KpSubtract: G.SetSpeed("-"); Handled(); break;                                              // DecTime
            case Key.KpMultiply: G.SetSpeed("*"); Handled(); break;                                              // ResetTime
        }
    }

    // the next own finished building (or Aje collector) of these classes, by distance from the camera's point, not one
    // already selected (PickNextFromObjQuery, Game.usl:2070); H (JumpToHQ) starts at the nearest headquarters
    void PickNext(string[] classes, bool hq)
    {
        var set = new HashSet<string>(classes);
        var all = W.Buildings.Where(b => b.Alive && b.Built && b.Owner == G.Me && set.Contains(b.Name)).Cast<Entity>()
            .Concat(W.Units.Where(u => u.Alive && u.Owner == G.Me && set.Contains(u.Name))).ToList();
        if (all.Count == 0) return;
        double cx = G.Camera.X, cz = G.Camera.Z;
        all = all.OrderBy(a => Math.Sqrt((a.Pos.X - cx) * (a.Pos.X - cx) + (a.Pos.Z - cz) * (a.Pos.Z - cz))).ToList();
        var cur = hq && !all.Any(x => G.Selection.Contains(x)) ? null : all.FirstOrDefault(x => !G.Selection.Contains(x));
        var t = cur ?? all[0];
        G.Selection.Set(new[] { t }); G.CenterOn(t);
        G.Pings.Add((t.Pos.X, t.Pos.Z, W.Time));
        G.Minimap("Finished", t.Pos.X, t.Pos.Z);         // (Game.usl:2109-2111)
    }

    // Space (JumpToLastEvent): where the last alert was (attack, warp gate ...), else the selection
    void LastEvent()
    {
        var p = G.Pings.Concat(G.HudView?.News.Pings ?? new()).OrderBy(x => x.T).LastOrDefault();
        if (p.T > 0 && W.Time - p.T < 120) { G.LookAt(p.X, p.Z); return; }
        G.CenterOn(G.Selection.Items.FirstOrDefault(x => x.Alive) ?? G.HomeEntity());
    }

    // ======================================================================================== minimap (GameCameraModule.usl:639-667)
    bool _mmDrag;
    /// <summary>a press on the minimap at world (x, z): the left button moves the camera there (dragging keeps moving it)</summary>
    public void MinimapPress(double x, double z, MouseButton button, bool shift)
    {
        // the callback mode (a target to click: the co-op command panel, the sequel's powers) takes a minimap click too
        if (button == MouseButton.Left && Mode == "callback") { var cb = ModeData as Action<double, double, Entity?>; SetMode(null); cb?.Invoke(x, z, null); return; }
        if (button == MouseButton.Left && Mode != "mapping") { G.LookAt(x, z); _mmDrag = true; }
    }
    public void MinimapDrag(double x, double z) { if (_mmDrag) G.LookAt(x, z); }
    /// <summary>a release on the minimap: the right button gives the plain order of a right-click on bare ground - walk /
    /// sail, a building's rally point, Shift queues - never gathers; the ping button's mode pings the point</summary>
    public void MinimapRelease(double x, double z, MouseButton button, bool shift)
    {
        if (button == MouseButton.Left) { _mmDrag = false; if (Mode == "mapping") { G.MapPing(x, z); SetMode(null); } return; }
        if (button != MouseButton.Right) return;
        if (Mode != null) { SetMode(null); return; }
        var units = MyUnits();
        if (units.Count > 0) { MoveGroup(units, x, z, "move", shift, false); G.PlayOrdered("move", units); }
        else { var prod = G.Selection.Items.Where(b => G.CanRally(b)).Select(b => b.Id).ToArray(); if (prod.Length > 0) G.Submit(new PwRemake.Sim.Commands.SetRallyCommand { Producers = prod, X = x, Z = z }); }
    }

    /// <summary>the corners of the camera's view on the ground (the minimap's view frame)</summary>
    public (double X, double Z)[] ViewCorners()
    {
        var size = GetViewport().GetVisibleRect().Size;
        var cam = G.Renderer.Camera;
        float y = G.Camera.Target.Y;
        var res = new (double, double)[4];
        int i = 0;
        foreach (var p in new[] { Vector2.Zero, new Vector2(size.X, 0), size, new Vector2(0, size.Y) })
        {
            var o = cam.ProjectRayOrigin(p); var d = cam.ProjectRayNormal(p);
            float t = Math.Abs(d.Y) > 1e-5f ? (y - o.Y) / d.Y : -1;
            if (t < 0 || t > 500) t = 500;
            var q = o + d * t;
            res[i++] = (q.X, q.Z);
        }
        return res;
    }

    // ======================================================================================== per frame
    public void Step(double dt)
    {
        StepWarmUp();                             // (the placement warm-up's objects go after their first frames)
        UpdateAimArea();                          // (the Attack mode's splash circle under the mouse)
        // a cutscene, a film, the mission UI holding the game (a dialogue, the quest log), the pause menu, the end of the
        // mission: a mode ends there (as ui/mission.js does when a sequence starts) - placing above all, so its ghost and
        // dots do not stay drawn over the scene
        if (Mode != null && (G.Cinematic || G.MenuOpen || G.PausedByUi || G.Over && G.Paused)) CancelMouse();
        if (G.Cinematic) { Hover = null; ClearMarks(); Cursors.Set("standard"); return; }      // a cutscene: the player has no input
        if (_follow != null) { if (_follow.Alive && !_follow.Inside) G.LookAt(_follow.Pos.X, _follow.Pos.Z); else _follow = null; }
        _hoverT -= dt;
        if (_hoverT <= 0 && !_dragging)
        {
            _hoverT = 0.07;
            bool overHud = G.Hud?.IsOverHud(_mouse) == true || G.MenuOpen;
            Hover = overHud ? null : EntityAt(_mouse);
            UpdateMarks(overHud);
            // the original mouse cursors (Data/Base/Cursors): what a right click would do here
            string cur = "standard";
            if (Mode != null) cur = Mode switch { "move" or "rally" => "walk", "amove" => "walk_aggro", "attack" => AttackModeCursor(overHud ? null : Hover, overHud ? null : GroundPoint(_mouse)), "place" or "lay" or "repair" => "build", "target" => "special", "mapping" => "mapping", _ => "standard" };
            else if (!overHud) cur = CursorFor(Hover, GroundPoint(_mouse), Godot.Input.IsKeyPressed(Key.Shift)) is { Length: > 0 } c ? c : "standard";
            if (cur == "healing") cur = "special";                // CCursorMgr: healing shows Special.cur
            var size = GetViewport().GetVisibleRect().Size;
            if (_mouse.X <= 4) cur = "sm_left"; else if (_mouse.X >= size.X - 5) cur = "sm_right";
            if (_mouse.Y <= 4) cur = cur.StartsWith("sm_") ? cur.Replace("sm_", "sm_up_") : "sm_up"; else if (_mouse.Y >= size.Y - 5) cur = cur.StartsWith("sm_") ? cur.Replace("sm_", "sm_down_") : "sm_down";
            Cursors.Set(cur);
        }
        if (Mode == "place") UpdateGhost();
    }

    public override void _ExitTree() { Cursors.Set(null); ClearGhost(); }
}
