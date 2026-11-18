// Test runs from the command line (docs/GAME_SESSION.md): --autoplay selects all own units and attack-moves them to
// the nearest enemy every 20 s of game time (through the input's own order path: the selection, then a group
// attack-move command), --shots <dir> --shot-every <s> saves screenshots during the run, --log-every <s> writes the
// state of the game to the log (time, units per player, the frame times), --spawn <class>[:foe|wild][,...] puts
// objects down next to the player's first unit at game time --spawn-at <s> (default 3; the debug tools' SpawnObject;
// --spawn-ring: on a circle, sides p<id> for every player's colour)
// and centres the camera on the first (view checks: a carrier, a tower with its turret ...; class:side:level for a
// level), --use-move <id>[:<game s>] uses a special move (GameSession.AutoplayMove.cs). Everything goes through
// the same API as the player's input.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;

namespace PwRemake.App;

public sealed partial class Autoplay
{
    static readonly Logger L = Log.For("Game.Autoplay");
    readonly GameSession S;
    readonly bool _fight;
    double _orderT = 3, _shotT, _shotEvery = 0, _logT, _logEvery = 10, _real;
    string? _shots;
    double _menuAt = -1, _menuClose = -1;
    bool _menuTools;
    string _menuPage = "";
    int _optionsTab;
    int _shotN;
    string[]? _spawn;
    double _spawnAt = 3;
    /// <summary>--spawn-select: one second after the spawn, the objects put down are selected and the camera shows them
    /// all (their rings in a screenshot)</summary>
    bool _spawnSelect;
    /// <summary>--spawn-together: every object at the first one's place (a crowd around a building: UnitsBehind)</summary>
    bool _spawnTogether, _spawnRing;
    int _preSpawnId;

    public Autoplay(GameSession s, bool fight = true) { S = s; _fight = fight; }

    public void Configure(Dictionary<string, string> args)
    {
        if (args.TryGetValue("shots", out var d) && d.Length > 0) { _shots = d; Directory.CreateDirectory(d); _shotEvery = 10; }
        if (args.TryGetValue("shot-every", out var se)) _shotEvery = Json.ParseNum(se, 10);
        if (args.TryGetValue("log-every", out var le)) _logEvery = Json.ParseNum(le, 10);
        _shotT = Math.Min(4, _shotEvery);
        if (args.TryGetValue("input-check", out var ic)) _inAt = Json.ParseNum(ic, 5);
        if (args.TryGetValue("mark-check", out var mc)) _mkAt = Json.ParseNum(mc, 5);
        // --menu-at <game s> [--debug-tools | --menu-page save|load|options [--options-tab n]]: the pause menu (or its
        // debug tools, its save / load window, its Options at tab n) opened for a screenshot, closed again
        if (args.TryGetValue("spawn", out var sp) && sp.Length > 0) _spawn = sp.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (args.TryGetValue("spawn-at", out var sa)) _spawnAt = Json.ParseNum(sa, 3);
        _spawnSelect = args.ContainsKey("spawn-select");
        _spawnTogether = args.ContainsKey("spawn-together");
        _spawnRing = args.ContainsKey("spawn-ring");          // the entries evenly on a 14 m circle 34 m from your unit (sides p<id>: every player's colour in one view)
        if (args.TryGetValue("menu-at", out var ma)) { _menuAt = Json.ParseNum(ma, -1); _menuTools = args.ContainsKey("debug-tools"); _menuPage = args.GetValueOrDefault("menu-page", ""); _optionsTab = (int)Json.ParseNum(args.GetValueOrDefault("options-tab", "0"), 0); }
        ConfigurePlacement(args);
        ConfigureAmphib(args);
        ConfigureMove(args);
        // --help-at <game s> [--help-select <class>]: the own unit / building of that class selected (default: a worker),
        // F1 pressed through the input; the help book's page logged and shot
        if (args.TryGetValue("help-at", out var ha)) { _helpAt = Json.ParseNum(ha, -1); _helpSelect = args.TryGetValue("help-select", out var hs) ? hs : "_worker"; _helpPage = args.TryGetValue("help-page", out var hp) ? hp : null; }
        // --cine <sequence file or id> [--cine-at <game s>]: the mission plays that sequence (as its trigger would) - the
        // cutscene stage's checks: CINE lines with the lip sync's jaw weights, the scene's time of day, the hidden objects
        if (args.TryGetValue("cine", out var ci) && ci.Length > 0) { _cine = ci; _cineAt = Json.ParseNum(args.GetValueOrDefault("cine-at", "2"), 2); }
        L.Info($"autoplay: fight {_fight}, screenshots {(_shots ?? "-")} every {_shotEvery} s");
    }

    string? _cine; double _cineAt = 2, _cineLog;
    void CineCheck()
    {
        var M = S.Mission;
        if (_cine != null && M != null && S.World.Time >= _cineAt && !S.Cinematic)
        {
            var key = _cine.ToLowerInvariant();
            var path = M.Campaign.Data.Sequences.Select(kv => kv.Key).FirstOrDefault(k => k.ToLowerInvariant().EndsWith(key) || k.ToLowerInvariant().Contains("/" + key + ".")) ?? _cine;
            _cine = null;
            bool ok = M.Host.PlaySequence(new PwRemake.Sim.Scripting.SequenceRequest { Path = path, Reason = "--cine" });
            L.Info($"CINE play {path}: {(ok ? "started" : "not played")}");
        }
        if (S.Cinematic && _real >= _cineLog)
        {
            _cineLog = _real + 1;
            var st = S.Stage;
            var lips = string.Join(", ", st.LipWeights().Select(l => $"{l.Cls} {l.W:0.00} mute {l.Mute:0.00} ({l.Jaw})"));
            L.Info($"CINE t={S.Presenter.State.Cine?.Player?.T:0.0} day={(st.Day is { } d ? $"{d.Tod / 3600:0.00} h weather {d.Weather}" : "-")} hidden={st.HiddenCount} lips=[{lips}]");
        }
    }

    // --input-check <game s>: mouse events pushed into the viewport as the player's would come - a left click on an own
    // unit selects it, a right click on the ground 8 m away orders it there, a box drag selects it again; logged as
    // INPUT ok / FAIL lines
    double _inAt = -1; int _inStage; Unit? _inU; double _inWait; Vector2 _inP; PwRemake.Sim.Core.V3 _inFrom;
    void Mouse(MouseButton b, bool pressed, Vector2 at)
    {
        Input.ParseInputEvent(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = b, Pressed = pressed, Position = at, GlobalPosition = at });
    }
    // --mark-check <game s>: the first question mark the mission's triggers show with a text is shown (the trigger's
    // QMRK action through the campaign), the camera goes there, the mouse moves over it (its tooltip) and clicks it (it
    // is picked); logged as MARK ok / FAIL lines, the mouse stays over it (a screenshot shows the tooltip)
    double _mkAt = -1, _mkWait; int _mkStage; PwRemake.UI.Mission.Logic.QuestionMarkState? _mk;
    void MarkCheck(double dt)
    {
        if (_mkAt < 0 || S.World.Time < _mkAt || S.Cinematic || S.MenuOpen || S.Mission?.Engine is not { } E) return;
        _mkWait -= dt;
        if (_mkWait > 0) return;
        switch (_mkStage)
        {
            case 0:
                var act = E.Script.Triggers.SelectMany(t => t.Actions).FirstOrDefault(a => a.Code == "QMRK" && a.P.TryGetValue("questiontooltip", out var k)
                    && E.Script.Texts.ContainsKey(k) && a.P.TryGetValue("questionstate", out var st) && st != "STATE_INVISIBLE");
                if (act == null) { L.Info("MARK FAIL no question mark with a text in this mission"); _mkAt = -1; return; }
                S.Mission.Campaign.SetQuestionMark(act.P["questionmark"], act.P["questionstate"], E.Text(act.P["questiontooltip"]));
                _mk = S.Presenter.QuestionMarks.GetValueOrDefault(act.P["questionmark"]);
                if (_mk == null || S.Input.MarkPos(_mk) is not { } at) { L.Info("MARK FAIL the mark has no place"); _mkAt = -1; return; }
                S.World.SetFogRevealAll(true);
                S.LookAt(at.X, at.Z); S.Camera.Dist = S.Camera.TDist = 30;
                _mkStage = 1; _mkWait = 1.5; break;
            case 1:
                var p = S.Input.MarkPos(_mk!)!.Value;
                if (Screen(p.X, S.World.Height(p.X, p.Z) + 2, p.Z) is not { } sp) { _mkWait = 0.5; return; }
                Input.ParseInputEvent(new InputEventMouseMotion { Position = sp, GlobalPosition = sp });
                _mkStage = 2; _mkWait = 0.5; break;
            case 2:
                L.Info($"MARK {(S.Input.HoverMark == _mk && S.HudView?.CurrentTip?.Body == _mk!.Tooltip ? "ok  " : "FAIL")} hover {_mk!.Name} ({_mk.State}): tooltip \"{S.HudView?.CurrentTip?.Body}\"");
                var q = S.Input.MarkPos(_mk)!.Value;
                if (Screen(q.X, S.World.Height(q.X, q.Z) + 2, q.Z) is { } cp) { Mouse(MouseButton.Left, true, cp); Mouse(MouseButton.Left, false, cp); }
                _mkStage = 3; _mkWait = 0.5; break;
            case 3:
                L.Info($"MARK {(S.Input.PickedMark == _mk && S.Selection.Count == 0 ? "ok  " : "FAIL")} click picks {_mk!.Name}: tooltip \"{S.HudView?.CurrentTip?.Title}\" / \"{S.HudView?.CurrentTip?.Body}\"");
                _mkAt = -1; break;
        }
    }
    Vector2? Screen(double x, double y, double z)
    {
        var c = S.Renderer.Camera; var p = new Vector3((float)x, (float)y, (float)z);
        return c.IsPositionBehind(p) ? null : c.UnprojectPosition(p);
    }
    void InputCheck(double dt)
    {
        var W = S.World;
        if (_inAt < 0 || W.Time < _inAt || S.Cinematic || S.MenuOpen) return;
        _inWait -= dt;
        if (_inWait > 0) return;
        switch (_inStage)
        {
            case 0:
                _inU = W.Units.FirstOrDefault(u => u.Alive && u.Owner == S.Me && S.Entities.OnScreen(u));
                if (_inU == null) { S.CenterOn(W.Units.FirstOrDefault(u => u.Alive && u.Owner == S.Me)); _inWait = 1; return; }
                S.Selection.Clear();
                var p = S.Entities.PosOf(_inU);
                if (Screen(p.X, p.Y + _inU.Height * 0.5, p.Z) is not { } sp) return;
                _inP = sp;
                Mouse(MouseButton.Left, true, sp); Mouse(MouseButton.Left, false, sp);
                _inStage = 1; _inWait = 0.5; break;
            case 1:
                L.Info($"INPUT {(S.Selection.Contains(_inU) ? "ok  " : "FAIL")} left click at {_inP} selects {_inU}");
                var g = S.Entities.PosOf(_inU!);
                if (Screen(g.X + 8, W.Height(g.X + 8, g.Z), g.Z) is { } rp) { Mouse(MouseButton.Right, true, rp); Mouse(MouseButton.Right, false, rp); }
                _inFrom = _inU!.Pos;
                _inStage = 2; _inWait = 1.0; break;
            case 2:
                bool moved = _inU!.Task.Type == "move" || Math.Abs(_inU.Pos.X - _inFrom.X) + Math.Abs(_inU.Pos.Z - _inFrom.Z) > 0.5;
                L.Info($"INPUT {(moved ? "ok  " : "FAIL")} right click on the ground 8 m east: task {_inU.Task}, moved {_inU.Pos.X - _inFrom.X:0.0}, {_inU.Pos.Z - _inFrom.Z:0.0}");
                S.Selection.Clear();
                var q = S.Entities.PosOf(_inU);
                if (Screen(q.X, q.Y + 1, q.Z) is { } bp)
                {
                    Mouse(MouseButton.Left, true, bp - new Vector2(60, 60));
                    Input.ParseInputEvent(new InputEventMouseMotion { Position = bp + new Vector2(60, 60), GlobalPosition = bp + new Vector2(60, 60), ButtonMask = MouseButtonMask.Left });
                    Mouse(MouseButton.Left, false, bp + new Vector2(60, 60));
                }
                _inStage = 3; _inWait = 0.5; break;
            case 3:
                L.Info($"INPUT {(S.Selection.Contains(_inU!) ? "ok  " : "FAIL")} box drag selects {_inU} ({S.Selection.Count} selected)");
                _inAt = -1; break;
        }
    }

    public void Step(double dt)
    {
        _real += dt;
        var W = S.World;
        InputCheck(dt);
        MarkCheck(dt);
        if (_spawn != null && W.Time >= _spawnAt && !S.Cinematic) { _preSpawnId = W.Units.Cast<Entity>().Concat(W.Buildings).Select(e => e.Id).DefaultIfEmpty(0).Max(); Spawn(_spawn); _spawn = null; }
        if (_spawnSelect && _spawn == null && W.Time >= _spawnAt + 1)
        {
            _spawnSelect = false;
            var me = S.Me;
            var put = W.Units.Cast<Entity>().Concat(W.Buildings).Where(e => e.Alive && e.Owner == me && e.Id > _preSpawnId && !e.Inside).ToList();
            S.Selection.Set(put);
            // the camera over all of them
            if (put.Count > 0)
            {
                double cx = put.Average(e => e.Pos.X), cz = put.Average(e => e.Pos.Z);
                double far = put.Max(e => Math.Sqrt((e.Pos.X - cx) * (e.Pos.X - cx) + (e.Pos.Z - cz) * (e.Pos.Z - cz)) + SelectionRing.Of(e));
                S.LookAt(cx, cz); S.Camera.Dist = S.Camera.TDist = Math.Max(40, far * 2.2);
            }
        }
        PlacementCheck(dt);
        AmphibCheck(dt);
        MoveCheck();
        if (_fight && !S.Paused && !S.Over)
        {
            if (W.Time >= _orderT) { _orderT = W.Time + 20; Order(); }
        }
        if (_menuAt >= 0 && W.Time >= _menuAt && !S.Cinematic)
        {
            _menuAt = -1;
            S.OpenMenu();
            if (_menuTools && S.MenuNode != null) new PwRemake.UI.Game.DebugTools(S, S.MenuNode).Show();
            if (_menuPage == "save") S.MenuNode?.SaveWin();
            if (_menuPage == "load") S.MenuNode?.LoadWin();
            if (_menuPage == "options") S.MenuNode?.Options(_optionsTab);
            _shotT = _real + 1.0; _menuClose = _real + 2.5;
            L.Info("autoplay: the menu is open");
        }
        if (_menuClose >= 0 && _real >= _menuClose) { _menuClose = -1; S.CloseMenu(); }
        HelpCheck();
        CineCheck();
        if (_shots != null && _shotEvery > 0 && _real >= _shotT)
        {
            _shotT += _shotEvery;
            // (in a cutscene's timeline the world stands still: the scene's time in the name)
            var ct = S.Presenter?.State.Cine?.Player?.T;
            var f = Path.Combine(_shots, ct is double cts ? $"shot_{++_shotN:00}_{W.Time:0000}_scene{cts:000.0}.png" : $"shot_{++_shotN:00}_{W.Time:0000}.png");
            S.GetViewport().GetTexture().GetImage().SavePng(f);
            L.Info($"screenshot {f} (game {W.Time:0.0} s{(S.Cinematic ? ", cutscene" : "")})");
        }
        if (_real >= _logT)
        {
            _logT += _logEvery;
            var per = W.Players.Select(p => $"p{p.Id} {p.Tribe}: {W.Units.Count(u => u.Alive && u.Owner == p)} units {W.Buildings.Count(b => b.Alive && b.Owner == p)} bldgs").ToList();
            per.Add($"wild {W.Units.Count(u => u.Alive && u.Owner == null)}");
            L.Info($"t={W.Time:0.0} real={_real:0} fps={Engine.GetFramesPerSecond()} | {string.Join(" | ", per)} | {S.PerfLine}");
        }
    }

    double _helpAt = -1, _helpShot = -1, _chatT = -1, _histT = -1, _histShot = -1; string _helpSelect = ""; string? _helpPage;
    void HelpCheck()
    {
        var W = S.World;
        if (_helpAt >= 0 && W.Time >= _helpAt && !S.Cinematic && !S.MenuOpen)
        {
            _helpAt = -1;
            Entity? e = W.Units.Where(u => u.Alive && u.Owner == S.Me).Cast<Entity>().Concat(W.Buildings.Where(b => b.Alive && b.Owner == S.Me))
                .FirstOrDefault(x => x.Name.Contains(_helpSelect, StringComparison.OrdinalIgnoreCase));
            if (e != null) { S.Selection.Set(new List<Entity> { e }); S.CenterOn(e); }
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.F1, PhysicalKeycode = Key.F1, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.F1, PhysicalKeycode = Key.F1, Pressed = false });
            L.Info($"help: F1 with {(e == null ? "nothing" : e.Name)} selected");
            _helpShot = _real + 1.5;
        }
        if (_helpShot >= 0 && _real >= _helpShot)
        {
            _helpShot = -1;
            var h = S.MenuNode?.FindChild("HelpWindow", true, false) as PwRemake.UI.Help.HelpWindow;
            if (_helpPage != null && h != null) { L.Info($"help: F1 opened {h.Current}; now {_helpPage}"); h.Navigate(_helpPage); _helpPage = null; _helpShot = _real + 1.5; return; }
            L.Info($"HELP {(h != null ? "ok  " : "FAIL")} the help book is open at {h?.Current ?? "-"} ({h?.Book.All.Count ?? 0} index entries)");
            if (_shots != null) S.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(_shots, "help.png"));
            // then the chat: Enter, a request to computer player 1, the history (the chat button's right click)
            S.CloseMenu();
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, PhysicalKeycode = Key.Enter, Pressed = true });
            Input.ParseInputEvent(new InputEventKey { Keycode = Key.Enter, PhysicalKeycode = Key.Enter, Pressed = false });
            _chatT = _real + 1.0;
        }
        if (_chatT >= 0 && _real >= _chatT)
        {
            _chatT = -1;
            var chat = S.HudView.Chat;
            L.Info($"CHAT {(chat.IsOpen ? "ok  " : "FAIL")} Enter opens the chat bar");
            chat.SendLine("AI1: food");
            _histT = _real + 2.0;
        }
        if (_histT >= 0 && _real >= _histT)
        {
            _histT = -1;
            var chat = S.HudView.Chat;
            L.Info($"CHAT {(chat.History.Any(h => h.Text.Contains("AI1: food")) ? "ok  " : "FAIL")} the line is in the history ({chat.History.Count} lines: {string.Join(" | ", chat.History.TakeLast(3).Select(h => h.Text))})");
            chat.ShowHistory();
            _histShot = _real + 1.0;
        }
        if (_histShot >= 0 && _real >= _histShot)
        {
            _histShot = -1;
            if (_shots != null) S.GetViewport().GetTexture().GetImage().SavePng(Path.Combine(_shots, "chat_history.png"));
        }
    }

    void Spawn(string[] what)
    {
        var W = S.World; var me = S.Me;
        if (me == null) return;
        Entity? at = W.Units.FirstOrDefault(u => u.Alive && u.Owner == me) ?? (Entity?)W.Buildings.FirstOrDefault(b => b.Alive && b.Owner == me);
        double x0 = at?.Pos.X ?? S.Camera.X, z0 = at?.Pos.Z ?? S.Camera.Z;
        // --spawn-together with a building first: the first place around the unit (22 m out and farther) it fits on
        double ta = 0, tr = 22;
        var first = what.Length > 0 ? what[0].Split(':')[0] : "";
        if (_spawnTogether && W.Data.Info(first)?.Type == "BLDG")
            for (double r = 22; r <= 90; r += 8)
            {
                bool found = false;
                for (double a = 0; a < Math.Tau && !found; a += 0.5)
                    if (W.PlacementOf(first, x0 + Math.Cos(a) * r, z0 + Math.Sin(a) * r, 0, me).Ok) { ta = a; tr = r; found = true; }
                if (found) break;
            }
        for (int i = 0; i < what.Length; i++)
        {
            var parts = what[i].Split(':');
            string side = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "me";
            int level = parts.Length > 2 ? (int)Json.ParseNum(parts[2], 1) : 1;      // class:side:level (a hero at level 3+ has its special move)
            // "<class>*<n>[:side]": a crowd of n in a block, 3 m apart (the sound checks: a crowded base, a big battle)
            int n = 1; var cls = parts[0];
            if (cls.IndexOf('*') is int st and > 0) { n = Math.Max(1, (int)Json.ParseNum(cls[(st + 1)..], 1)); cls = cls[..st]; }
            // (in a ring around the unit; a water building finds its place in the water nearby: PlacementOf)
            double a = _spawnTogether ? ta : i * 2.1, r = _spawnTogether ? tr : 22 + 8 * i;
            double cx = x0 + Math.Cos(a) * r, cz = z0 + Math.Sin(a) * r;
            if (_spawnRing) { double ra = i * Math.Tau / what.Length; cx = x0 + 34 + Math.Cos(ra) * 14; cz = z0 + Math.Sin(ra) * 14; }
            int row = (int)Math.Ceiling(Math.Sqrt(n));
            for (int k = 0; k < n; k++)
            {
                // the debug tools' command (applied at the next step, recorded in a saved game like every change of the world)
                double ox = n == 1 ? 0 : (k % row - (row - 1) / 2.0) * 3, oz = n == 1 ? 0 : (k / row - (row - 1) / 2.0) * 3;
                var c = new PwRemake.Sim.Game.DebugToolCommand { Tool = "spawn", Class = cls, Side = side, Level = level, X = cx + ox, Z = cz + oz };
                var name = what[i];
                if (k == 0 || k == n - 1) S.Submit(c, why => L.Info($"spawn {name}: {why ?? c.Said} at {c.X:0.0}, {c.Z:0.0}"));
                else S.Submit(c, _ => { });
            }
            if (i == 0) { S.LookAt(_spawnRing ? x0 + 34 : cx, _spawnRing ? z0 : cz); S.Camera.Dist = S.Camera.TDist = 40; }
        }
    }

    void Order()
    {
        var me = S.Me; var W = S.World;
        var own = W.Units.Where(u => u.Alive && u.Owner == me && !u.Inside && !u.Parked && !u.Autonomous && !u.Unselectable).ToList();
        if (own.Count == 0) return;
        // the nearest enemy of the group's middle (a hostile player's or a wild animal)
        double cx = own.Average(u => u.Pos.X), cz = own.Average(u => u.Pos.Z);
        Entity? best = null; double bd = double.MaxValue;
        // (units first - a nest or a building only when no hostile unit is left)
        foreach (var pass in new[] { W.Units.Cast<Entity>(), W.Buildings.Where(b => !b.IsNest) })
        {
            foreach (var e in pass)
            {
                if (!e.Alive || e.Parked || e.Inside || !own[0].IsEnemy(e)) continue;
                double d = (e.Pos.X - cx) * (e.Pos.X - cx) + (e.Pos.Z - cz) * (e.Pos.Z - cz);
                if (d < bd) { bd = d; best = e; }
            }
            if (best != null) break;
        }
        S.Selection.Set(own);
        S.LookAt(cx, cz);                      // (the screenshots show the fight)
        if (best == null) { L.Info($"autoplay: {own.Count} units, no enemy in sight"); return; }
        S.Input.MoveGroup(own, best.Pos.X, best.Pos.Z, "attackmove", false, false);
        S.PlayOrdered("amove", own);
        L.Info($"autoplay: {own.Count} units attack-move to {best.Name} #{best.Id} at {best.Pos.X:0}, {best.Pos.Z:0} ({Math.Sqrt(bd):0} m)");
    }
}
