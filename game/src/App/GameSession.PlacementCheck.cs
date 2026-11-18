// --test-placement <game s> (with --shots <dir>): the placement checked on screen, through the player's own input path
// (the build mode set as the command bar does, mouse events pushed into the viewport): a wall line - its ghost under
// the cursor before the press and along the drag -, placed; a tower over the placed wall (its dots green on the wall);
// gates in the line, straight and diagonal, built and under construction (both end pieces at their wings); the straight
// gate destroyed and a new one put into its gap through the ghost (the cursor off the tile's centre: the ghost snaps
// into the gap, green), the same for one of the map's own two-tile gates of the player (a campaign mission); a harbour
// on the shore (dots, turned to the sea) where the map has water; a placement cancelled by the pause menu. A screenshot
// of each (placement_NN_<what>.png in the shots folder) and PLACEMENT ok / FAIL lines in the log.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;
using PwRemake.Sim.World;
using PwRemake.UI.Mission.Logic;

namespace PwRemake.App;

public sealed partial class Autoplay
{
    double _plAt = -1, _plWait; int _plStage, _plShot;
    double _px, _pz, _ax, _az;              // the wall line's middle and its direction
    ActionDef? _plWall;
    double _dx, _dz;                        // a tile in the middle of the diagonal line
    ActionDef? _plGate;                     // the tribe's gate
    readonly List<Building> _gapGates = new();   // gates destroyed to put a new one into their gap
    Building? _gapOld; (int I, int J) _gapTile; double _gapX, _gapZ, _gapRot;

    void ConfigurePlacement(Dictionary<string, string> args)
    {
        if (args.TryGetValue("test-placement", out var tp)) _plAt = Json.ParseNum(tp, 5);
        if (args.ContainsKey("ghosts-only")) { _plStage = 20; _px = double.NaN; }      // (only the ghosts of every building)
        if (args.TryGetValue("ghost-rounds", out var gr)) _ghostRounds = Math.Max(1, (int)Json.ParseNum(gr, 1));
        _quitWhenDone = args.ContainsKey("quit-when-done");
    }

    ActionDef? BuildAction(params string[] names)
    {
        var W = S.World;
        foreach (var n in names)
            foreach (var a in W.Data.Actions(S.Me))
                if (a.Kind == "Build" && a.Results.Count > 0 && a.Results[0].Obj == n) return a;
        return null;
    }

    void PlShot(string what)
    {
        var dir = _shots ?? "user://";
        var f = Path.Combine(dir, $"placement_{++_plShot:00}_{what}.png");
        S.GetViewport().GetTexture().GetImage().SavePng(f);
        L.Info($"PLACEMENT screenshot {f}");
    }

    void Check(bool ok, string what) => L.Info($"PLACEMENT {(ok ? "ok  " : "FAIL")} {what}");

    Vector2? ScreenOf(double x, double z) => Screen(x, S.World.Height(x, z), z);

    void MoveTo(double x, double z, bool leftHeld = false)
    {
        if (ScreenOf(x, z) is not { } p) return;
        Input.ParseInputEvent(new InputEventMouseMotion { Position = p, GlobalPosition = p, ButtonMask = leftHeld ? MouseButtonMask.Left : 0 });
    }

    void Button(double x, double z, bool pressed)
    {
        if (ScreenOf(x, z) is not { } p) return;
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = MouseButton.Left, Pressed = pressed, Position = p, GlobalPosition = p, ButtonMask = pressed ? MouseButtonMask.Left : 0 });
    }

    void PlacementCheck(double dt)
    {
        var W = S.World;
        // (the real time of every frame: what a ghost's first showing costs - BUILDLAG lines)
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if (_frameTs != 0 && _ghostFrames.Count < 2000) _ghostFrames.Add((now - _frameTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        _frameTs = now;
        if (_plAt < 0 || W.Time < _plAt || S.Cinematic) return;
        _plWait -= dt;
        if (_plWait > 0) return;
        var I = S.Input;
        switch (_plStage)
        {
            case 0:
            {
                _plWall = BuildAction("hu_palisade", "aje_bone_palisade", "ninigi_defense_skewer", "seas_fence");
                if (_plWall == null) { Check(false, "no wall action"); _plAt = -1; return; }
                // a free stretch near the headquarters: a straight line of five tiles the wall placer accepts
                var hq = W.Buildings.FirstOrDefault(b => b.Alive && b.Owner == S.Me) as Entity ?? W.Units.First(u => u.Alive && u.Owner == S.Me);
                bool found = false;
                for (int r = 30; r <= 90 && !found; r += 10)
                    for (int k = 0; k < 16 && !found; k++)
                    {
                        double a = k * Math.PI / 8, x = hq.Pos.X + Math.Cos(a) * r, z = hq.Pos.Z + Math.Sin(a) * r;
                        if (!S.Fog.Explored(x, z)) continue;
                        var line = W.WallLineOf(S.Me, _plWall, x - 16, z, x + 16, z);
                        var diag = W.WallLineOf(S.Me, _plWall, x - 12, z + 28, x + 12, z + 52);
                        if (line != null && line.Count == 5 && diag != null && diag.Count == 4) { _px = x; _pz = z; found = true; }
                    }
                if (!found) { Check(false, "no free stretch for a wall line"); _plAt = -1; return; }
                S.LookAt(_px, _pz + 12); S.Camera.TDist = 75;
                I.SetMode("place", _plWall);
                _plStage = 1; _plWait = 1.5; break;
            }
            case 1:
                MoveTo(_px - 16, _pz);
                _plStage = 2; _plWait = 0.6; break;
            case 2:
                Check(I.Mode == "place", "build mode on");
                PlShot("wall_hover");
                Button(_px - 16, _pz, true);
                _plStage = 3; _plWait = 0.3; break;
            case 3:
                MoveTo(_px + 16, _pz, true);
                _plStage = 4; _plWait = 0.6; break;
            case 4:
                PlShot("wall_drag");
                Button(_px + 16, _pz, false);
                _plStage = 5; _plWait = 1.0; break;
            case 5:
            {
                int n = W.Buildings.Count(b => b.Alive && b.Owner == S.Me && b.Def?.WallKind == "wall");
                Check(n >= 5, $"the wall line placed ({n} pieces)");
                Check(I.Mode == null, "build mode ended after the line");
                // the diagonal line too, put down at once (for the diagonal gate)
                var diag = W.WallLineOf(S.Me, _plWall!, _px - 12, _pz + 28, _px + 12, _pz + 52)!;
                W.PlaceWall(S.Me, _plWall!, diag.Select(t => (t.I, t.J, t.X, t.Z, t.State == "have")).ToList(), new List<Unit>(), false);
                _dx = diag[diag.Count / 2].X; _dz = diag[diag.Count / 2].Z;
                // everything finished (the builders would take a while)
                foreach (var b in W.Buildings.Where(b => b.Alive && b.Owner == S.Me && b.Def?.WallKind == "wall" && !b.Built)) W.FinishBuilding(b);
                var tower = BuildAction("hu_small_tower", "aje_small_tower", "ninigi_small_tower", "seas_small_tower");
                if (tower != null) { I.SetMode("place", tower); }
                _plStage = 6; _plWait = 1.0; break;
            }
            case 6:
                MoveTo(_px + 8, _pz);
                _plStage = 7; _plWait = 0.8; break;
            case 7:
            {
                if (I.Mode == "place")
                {
                    var tw = (ActionDef)I.ModeData!;
                    var dots = W.PlaceGridOf(tw.Results[0].Obj, _px + 8, _pz, 0, S.Me);
                    var WM = W.Walls!;
                    var onWall = dots.Where(d => WM.At(WM.TileOf(d.X, d.Z).I, WM.TileOf(d.X, d.Z).J)?.Wall is { Alive: true } w && w.Owner == S.Me).ToList();
                    Check(onWall.Count > 0 && onWall.All(d => d.Ok), $"tower: the dots over the own wall are green ({onWall.Count(d => d.Ok)}/{onWall.Count})");
                    PlShot("tower_on_wall");
                }
                else Check(false, "no tower action");
                I.SetMode(null);
                // gates: a straight one in the line, a diagonal one in the diagonal line - one finished, one a site
                var gate = BuildAction("hu_palisade_gate", "aje_bone_palisade_gate", "ninigi_defense_skewer_gate", "seas_gate");
                _plGate = gate;
                if (gate != null)
                {
                    var g1 = W.StartConstruction(S.Me, gate, _px, _pz, 0, new List<Unit>()) as Building;
                    if (g1 != null) _gapGates.Add(g1);
                    var g2 = W.StartConstruction(S.Me, gate, _dx, _dz, 0, new List<Unit>()) as Building;
                    Check(g1 != null && g2 != null, $"two gates placed ({g1?.Name}, {g2?.Name})");
                    if (g1 != null && !g1.Built) W.FinishBuilding(g1);
                    if (g2 != null) { g2.Progress = 0.6; g2.LookChanged(); }
                    Check(g2 != null && g2.GateEnds, "the diagonal gate shows its end pieces");
                }
                S.LookAt(_px, _pz + 20); S.Camera.TDist = 60;
                _plStage = 8; _plWait = 1.5; break;
            }
            case 8:
                PlShot("gates");
                S.LookAt(_dx, _dz); S.Camera.TDist = 35;
                _plStage = 9; _plWait = 1.2; break;
            case 9:
                PlShot("gate_diagonal_site");
                // a gap: the straight gate destroyed, and one of the map's own two-tile gates of the player
                if (W.Buildings.FirstOrDefault(b => b.Alive && b.Owner == S.Me && b.Def?.WallKind == "gate" && b.Tile2 != null && S.Fog.Explored(b.Pos.X, b.Pos.Z)) is { } og) _gapGates.Add(og);
                _plStage = _plGate != null ? 40 : 50; break;
            case 40:
            {
                if (_gapGates.Count == 0) { I.SetMode(null); _plStage = 50; break; }
                var g = _gapOld = _gapGates[0]; _gapGates.RemoveAt(0);
                _gapTile = g.Tile!.Value; _gapX = g.Origin.X; _gapZ = g.Origin.Z; _gapRot = g.Rot;
                bool two = g.Tile2 != null;
                W.Damage(g, 1e9, null);
                if (g.Alive) { L.Info($"PLACEMENT ({g.Name} survives: no gap)"); break; }
                S.LookAt(_gapX, _gapZ); S.Camera.TDist = 40;
                I.SetMode("place", _plGate);
                L.Info($"PLACEMENT {g.Name} destroyed ({(two ? "a map's two-tile gate" : "one tile")}): a new gate into its gap");
                _plStage = 41; _plWait = 1.0; break;
            }
            case 41:
            {
                // the cursor on the gap's (first) tile, off its centre
                var (cx, cz) = W.Walls!.Centre(_gapTile.I, _gapTile.J);
                MoveTo(cx + 1.5, cz + 1.2);
                _plStage = 42; _plWait = 0.8; break;
            }
            case 42:
            {
                var gi = I.GhostInfo;
                bool at = gi is var (_, gx, gz) && Math.Abs(gx - _gapX) < 0.01 && Math.Abs(gz - _gapZ) < 0.01;
                Check(I.Mode == "place" && I.GhostOk && at, $"the gate's ghost snaps into the gap of {_gapOld!.Name}, green (at {gi?.X:0.0}, {gi?.Z:0.0}; the old gate stood at {_gapX:0.0}, {_gapZ:0.0})");
                PlShot("gate_gap_" + _gapOld.Name);
                var (cx, cz) = W.Walls!.Centre(_gapTile.I, _gapTile.J);
                Button(cx + 1.5, cz + 1.2, true);
                _plStage = 43; _plWait = 0.2; break;
            }
            case 43:
            {
                var (cx, cz) = W.Walls!.Centre(_gapTile.I, _gapTile.J);
                Button(cx + 1.5, cz + 1.2, false);
                _plStage = 44; _plWait = 1.0; break;
            }
            case 44:
            {
                var ng = W.Buildings.FirstOrDefault(b => b.Alive && b.Owner == S.Me && b.Def?.WallKind == "gate" && b.Tile == _gapTile);
                Check(ng != null && Math.Abs(ng.Origin.X - _gapX) < 0.01 && Math.Abs(ng.Origin.Z - _gapZ) < 0.01 && Math.Abs(ng.Rot - _gapRot) < 1e-6, $"a new gate in the gap ({ng?.Name})");
                if (ng != null && !ng.Built) W.FinishBuilding(ng);
                I.SetMode(null);
                _plStage = 45; _plWait = 1.0; break;
            }
            case 45:
                PlShot("gate_gap_rebuilt");
                _plStage = 40; break;
            case 50:
            {
                // a harbour on the shore, where the map has water near the explored land
                var harbour = W.Data.Actions(S.Me).FirstOrDefault(a => a.Kind == "Build" && a.Results.Count > 0 && W.Data.Def(a.Results[0].Obj, S.Me) is { Coastal: true } d && !a.Results[0].Obj.Contains("rally"));
                (double X, double Z)? shore = null;
                if (harbour != null && W.WaterLevel != null)
                {
                    double bd = double.MaxValue;
                    for (double x = W.Play.X0 + 16; x < W.Play.X1 - 16; x += 8)
                        for (double z = W.Play.Z0 + 16; z < W.Play.Z1 - 16; z += 8)
                        {
                            double d = (x - _px) * (x - _px) + (z - _pz) * (z - _pz);
                            if (d >= bd || !S.Fog.Explored(x, z) || W.ShoreNormal(x, z) == null) continue;
                            if (W.CoastalAt(harbour.Results[0].Obj, x, z, S.Me).Ok) { bd = d; shore = (x, z); }
                        }
                }
                if (shore is var (hx, hz))
                {
                    S.LookAt(hx, hz); S.Camera.TDist = 80;
                    I.SetMode("place", harbour);
                    _ax = hx; _az = hz;
                    _plStage = 10; _plWait = 1.2;
                }
                else { L.Info($"PLACEMENT (no shore near the explored land: {(harbour?.Results[0].Obj ?? "no harbour action")}, water {W.WaterLevel})"); _plStage = 12; }
                break;
            }
            case 10:
                MoveTo(_ax + 3, _az + 3);
                _plStage = 11; _plWait = 0.8; break;
            case 11:
                PlShot("harbour");
                _plStage = 12; break;
            case 12:
            {
                // the pause menu ends the placing: the ghost and the dots go
                var any = BuildAction("hu_house", "hu_farm", "aje_house", "ninigi_house", "seas_small_tent") ?? _plWall!;
                I.SetMode("place", any);
                S.OpenMenu();
                _plStage = 13; _plWait = 0.3; break;
            }
            case 13:
                Check(I.Mode == null && !HasGhostNodes(), "the pause menu ended the placing (no ghost, no dots)");
                S.CloseMenu();
                _plStage = 20; break;
            case 20:
                // the ghost of every building of the tribe: only its shape in the ghost colour (no emitters - smoke,
                // fire, steam, the SEAS fans -, sprites, lights, shadows, clips)
                _ghosts = W.Data.Actions(S.Me).Where(a => a.Kind == "Build" && a.Results.Count > 0 && W.Data.Info(a.Results[0].Obj)?.Type == "BLDG"
                        && W.Data.Def(a.Results[0].Obj, S.Me) is { } d && d.WallKind is not ("wall" or "trap"))
                    .GroupBy(a => a.Results[0].Obj).Select(g => g.First()).ToList();
                _ghostAt = 0; _ghostBad = 0; _ghostEmit = 0; _ghostRound = 1;
                L.Info($"BUILDLAG {_ghosts.Count} ghosts, {_ghostRounds} rounds; {S.Models.Loaded.Count()} model templates loaded");
                _plStage = 14; _plWait = 0.3; break;
            case 15:
                MoveTo(_px, _pz - 30);
                _plStage = 14; _plWait = 0.5; break;
            case 14:
            {
                if (_ghostAt > 0 && I.Mode == "place")
                {
                    var name = _ghosts![_ghostAt - 1].Results[0].Obj;
                    var leaks = I.GhostLeaks();
                    var gfx = I.GhostObj?.Template;
                    if (gfx != null && gfx.Emitters.Count > 0) { _ghostEmit++; if (_ghostEmit <= 2 && _ghostRounds == 1) PlShot("ghost_" + name); }
                    if (leaks.Count > 0) { _ghostBad++; Check(false, $"ghost of {name}: {string.Join(", ", leaks)}"); }
                    LogGhostTime(name);
                }
                I.SetMode(null);
                if (_ghostAt >= _ghosts!.Count && _ghostRound < _ghostRounds)
                {
                    LogRound();
                    _ghostRound++; _ghostAt = 0;
                }
                if (_ghostAt >= _ghosts!.Count)
                {
                    LogRound();
                    if (_ghostRounds > 1) FirstTimeCosts();
                    Check(_ghostBad == 0, $"the ghosts of {_ghosts.Count} buildings show only the building ({_ghostEmit} with emitters, {_ghostBad} with more)");
                    _plAt = -1; L.Info("PLACEMENT done");
                    if (_quitWhenDone) S.GetTree().Quit();
                    break;
                }
                int had = S.Models.Loaded.Count();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                I.SetMode("place", _ghosts[_ghostAt++]);
                _ghostSetup = sw.Elapsed.TotalMilliseconds; _ghostLoadedNow = S.Models.Loaded.Count() > had;
                _ghostFrames.Clear();
                if (double.IsNaN(_px))
                {
                    var hq = W.Buildings.FirstOrDefault(b => b.Alive && b.Owner == S.Me) as Entity ?? W.Units.First(u => u.Alive && u.Owner == S.Me);
                    _px = hq.Pos.X + 25; _pz = hq.Pos.Z + 30;
                    S.LookAt(_px, _pz - 30); S.Camera.TDist = 60;
                }
                _plStage = 15; _plWait = 0.3; break;
            }
        }
    }

    List<ActionDef>? _ghosts; int _ghostAt, _ghostBad, _ghostEmit;

    // --ghost-rounds <n> (with --ghosts-only): the ghosts n times over. A BUILDLAG line per ghost: the time of its set-up
    // (SetMode -> MakeGhost; its model's template loaded then or before) and the frames while it showed (the worst one
    // holds the first drawing of the ghost's materials); per round the sums. The first round against the second is
    // what a building's first placement costs more (docs/GAME_SESSION.md "Command line").
    int _ghostRounds = 1, _ghostRound;
    long _frameTs; double _ghostSetup; bool _ghostLoadedNow;
    readonly List<double> _ghostFrames = new();
    double _rSetup, _rSetupMax, _rWorst, _rWorstMax, _rOver; int _rLoaded, _rN;

    void LogGhostTime(string name)
    {
        var f = _ghostFrames.OrderBy(x => x).ToList();
        double worst = f.Count > 0 ? f[^1] : 0, med = f.Count > 0 ? f[f.Count / 2] : 0;
        L.Info($"BUILDLAG round {_ghostRound} {name}: set up {_ghostSetup:0.0} ms (model {(_ghostLoadedNow ? "loaded now" : "loaded before")}), frames {f.Count}: worst {worst:0} ms, median {med:0} ms");
        _rN++; _rSetup += _ghostSetup; _rSetupMax = Math.Max(_rSetupMax, _ghostSetup); _rWorst += worst; _rWorstMax = Math.Max(_rWorstMax, worst);
        _rOver += Math.Max(0, worst - med); if (_ghostLoadedNow) _rLoaded++;
    }

    void LogRound()
    {
        L.Info($"BUILDLAG round {_ghostRound} total: {_rN} ghosts, {_rLoaded} models loaded then; set up {_rSetup:0} ms (max {_rSetupMax:0}), worst frames {_rWorst:0} ms (max {_rWorstMax:0}), above the median {_rOver:0} ms");
        _rN = _rLoaded = 0; _rSetup = _rSetupMax = _rWorst = _rWorstMax = _rOver = 0;
    }

    /// <summary>the other first-time costs: the build cards greyed (a menu's locked / dimmed cards) and the models of
    /// the units the tribe can make, twice each (the second time: from the caches)</summary>
    void FirstTimeCosts()
    {
        var W = S.World;
        var builds = W.Data.Actions(S.Me).Where(a => a.Kind == "Build" && a.Results.Count > 0).ToList();
        for (int round = 1; round <= 2; round++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew(); int n = 0;
            foreach (var a in builds)
            {
                var icon = UiAtlas.Entry(a.Id, 3) != null ? a.Id : UiAtlas.IconFor(a.Results[0].Obj);
                if (icon == null) continue;
                PwRemake.UI.Hud.HudSprites.Gray(icon, 3, 0.5f, 1f); PwRemake.UI.Hud.HudSprites.Gray(icon, 3, 0.75f, 0.4f); n++;
            }
            L.Info($"BUILDLAG grey cards round {round}: {n} cards in {sw.Elapsed.TotalMilliseconds:0.0} ms");
        }
        // (what every greyed card cost before the sheets' pixels were kept: its whole sheet read back and decompressed)
        if (builds.Select(a => UiAtlas.Entry(a.Id, 3) ?? UiAtlas.Entry(UiAtlas.IconFor(a.Results[0].Obj), 3)).FirstOrDefault(e => e != null) is { } e0)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var img = PwRemake.UI.Mission.HudArt.Tex(e0.File)?.GetImage();
            if (img != null && img.IsCompressed()) img.Decompress();
            L.Info($"BUILDLAG a card sheet read back ({e0.File}, {img?.GetWidth()} x {img?.GetHeight()}): {sw.Elapsed.TotalMilliseconds:0.0} ms - once per greyed card before");
        }
        int now = 0, before = 0; double total = 0, max = 0; string maxName = "";
        foreach (var a in builds)
        {
            var obj = a.Results[0].Obj;
            if (W.Data.Info(obj)?.Type == "BLDG") continue;
            var g = W.Data.Stats(obj, 1, S.Me)?.Gfx ?? W.Data.ClassGfx(obj);
            if (string.IsNullOrEmpty(g) || !PwRemake.View.Entities.EntityViews.HasFileQuiet(S.Models, g)) continue;
            int had = S.Models.Loaded.Count();
            var sw = System.Diagnostics.Stopwatch.StartNew();
            S.Models.Template(g.ToLowerInvariant(), S.Entities.OptionsOf(g), true);
            double ms = sw.Elapsed.TotalMilliseconds;
            if (S.Models.Loaded.Count() > had) { now++; total += ms; if (ms > max) { max = ms; maxName = g; } } else before++;
        }
        L.Info($"BUILDLAG unit models: {now} loaded now in {total:0} ms (max {max:0} ms {maxName}), {before} loaded before");
    }
    bool _quitWhenDone;

    bool HasGhostNodes() => S.GetChildren().Any(n => n.Name == "PlacementGhost" || n.Name == "PlaceGrid") && S.GetChildren().Any(n => (n.Name == "PlacementGhost" || n.Name == "PlaceGrid") && !n.IsQueuedForDeletion());
}
