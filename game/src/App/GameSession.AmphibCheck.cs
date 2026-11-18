// --test-amphib <game s> (with --shots): the amphibians on land and in the water, through the player's own commands -
// a baryonyx, a transport turtle and a hovercraft put down on the shore next to the player's first unit (the debug
// tools' put-down), sent into the water and back. Once a second an AMPHIB line per unit (the depth under it, in the
// water or not, the simulation's clip, the clip and model drawn) and PLACEMENT-style AMPHIB ok / FAIL lines at the end:
// on land no swim clip where the model has a walk, in the water no walk clip where it has a swim (EntityViews.Medium;
// user report: the transport turtles swam over land). Screenshots amphib_<n>_<land|water>.png.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;

namespace PwRemake.App;

public sealed partial class Autoplay
{
    double _amAt = -1, _amWait, _amLog; int _amStage, _amShot;
    (double X, double Z) _amLand, _amWater;
    readonly string[] _amClasses = { "ninigi_baryonyx", "baryonyx", "aje_transport_turtle", "seas_hovercraft" };
    readonly Dictionary<string, (int Land, int LandBad, int Water, int WaterBad)> _amSeen = new();

    void ConfigureAmphib(Dictionary<string, string> args)
    {
        if (args.TryGetValue("test-amphib", out var ta)) _amAt = Json.ParseNum(ta, 5);
        _quitWhenDone = args.ContainsKey("quit-when-done");
    }

    List<Unit> AmphibUnits() => S.World.Units.Where(u => u.Alive && u.Owner == S.Me && u.Amphib).ToList();

    void AmphibCheck(double dt)
    {
        var W = S.World;
        if (_amAt < 0 || W.Time < _amAt || S.Cinematic) return;
        _amWait -= dt;
        if (_amStage is 1 or 2 or 3) AmphibLog();
        if (_amWait > 0) return;
        switch (_amStage)
        {
            case 0:
            {
                // a shore near the player: land within 20 m of water at least 3 m deep
                if (W.WaterLevel is not double wl) { L.Info("AMPHIB FAIL no water on this map"); _amAt = -1; return; }
                var at = W.Units.FirstOrDefault(u => u.Alive && u.Owner == S.Me);
                if (at == null) { _amAt = -1; return; }
                double bd = double.MaxValue;
                (double, double)? water = null;
                for (double x = W.Play.X0 + 20; x < W.Play.X1 - 20; x += 6)
                    for (double z = W.Play.Z0 + 20; z < W.Play.Z1 - 20; z += 6)
                    {
                        double d = (x - at.Pos.X) * (x - at.Pos.X) + (z - at.Pos.Z) * (z - at.Pos.Z);
                        if (d >= bd || wl - W.Height(x, z) < 3) continue;
                        bd = d; water = (x, z);
                    }
                if (water is not var (wx, wz)) { L.Info("AMPHIB FAIL no deep water"); _amAt = -1; return; }
                // the land: from the water towards the unit, the first point 1 m above the water and 25 m further on
                double dx = at.Pos.X - wx, dz = at.Pos.Z - wz, len = Math.Max(1, Math.Sqrt(dx * dx + dz * dz));
                dx /= len; dz /= len;
                double lx = wx, lz = wz;
                for (double s = 0; s < len; s += 2) { lx = wx + dx * s; lz = wz + dz * s; if (W.Height(lx, lz) > wl + 1) break; }
                _amLand = (lx + dx * 25, lz + dz * 25);
                _amWater = (wx - dx * 25, wz - dz * 25);
                if (wl - W.Height(_amWater.X, _amWater.Z) < 2) _amWater = (wx, wz);
                for (int i = 0; i < _amClasses.Length; i++)
                {
                    var c = new PwRemake.Sim.Game.DebugToolCommand { Tool = "spawn", Class = _amClasses[i], Side = "me", Level = 1, X = _amLand.X + 6 * i, Z = _amLand.Z };
                    var name = _amClasses[i];
                    S.Submit(c, why => L.Info($"AMPHIB spawn {name}: {why ?? c.Said}"));
                }
                S.LookAt(_amLand.X, _amLand.Z); S.Camera.TDist = 45;
                L.Info($"AMPHIB shore: land {_amLand.X:0},{_amLand.Z:0} (depth {wl - W.Height(_amLand.X, _amLand.Z):0.0}), water {_amWater.X:0},{_amWater.Z:0} (depth {wl - W.Height(_amWater.X, _amWater.Z):0.0})");
                _amStage = 1; _amWait = 2; break;
            }
            case 1:
                // walking a little on land first, then into the water
                AmphibMove(_amLand.X + 20, _amLand.Z + 6);
                _amStage = 2; _amWait = 6; break;
            case 2:
                AmphibShot("land");
                AmphibMove(_amWater.X, _amWater.Z);
                _amStage = 3; _amWait = 30; break;
            case 3:
                AmphibShot("water");
                AmphibMove(_amLand.X, _amLand.Z);
                _amStage = 4; _amWait = 30; break;
            case 4:
                AmphibShot("back");
                foreach (var (k, c) in _amSeen)
                    L.Info($"AMPHIB {(c.LandBad == 0 && c.WaterBad == 0 ? "ok  " : "FAIL")} {k}: moving on land {c.Land} s ({c.LandBad} swimming), in the water {c.Water} s ({c.WaterBad} walking)");
                if (_amSeen.Count == 0) L.Info("AMPHIB FAIL no amphibian seen");
                _amAt = -1; L.Info("AMPHIB done");
                if (_quitWhenDone) S.GetTree().Quit();
                break;
        }
    }

    void AmphibMove(double x, double z)
    {
        var us = AmphibUnits();
        S.Submit(new OrderCommand { Units = us.Select(u => u.Id).ToArray(), Type = "move", X = x, Z = z }, why => L.Info($"AMPHIB move {us.Count} units to {x:0},{z:0}: {why ?? "ok"}"));
        S.LookAt(x, z);
    }

    void AmphibShot(string what)
    {
        if (_shots == null) return;
        var f = Path.Combine(_shots, $"amphib_{++_amShot}_{what}.png");
        S.GetViewport().GetTexture().GetImage().SavePng(f);
        L.Info($"AMPHIB screenshot {f}");
    }

    void AmphibLog()
    {
        var W = S.World;
        if (_real < _amLog) return;
        _amLog = _real + 1;
        foreach (var u in AmphibUnits())
        {
            var v = S.Entities.ViewOf(u);
            var m = v?.M;
            if (v == null || m == null || m.Anim == null) { L.Info($"AMPHIB {u.Name}: not drawn (gfx {u.Gfx}, view {v?.Gfx ?? "-"})"); continue; }
            double depth = (W.WaterLevel ?? -1e9) - W.Height(u.Pos.X, u.Pos.Z);
            string clip = m.Anim.Player.AssignedAnimation.ToString();
            bool moving = Math.Sqrt(u.Vel.X * u.Vel.X + u.Vel.Z * u.Vel.Z) > 0.3;
            bool wet = S.Entities.InWater(u);
            L.Info($"AMPHIB {u.Name} depth {depth:0.0} water {wet} moving {moving} sim {u.Anim?.Cur?.Clip ?? "-"} drawn {m.Template.Name}/{clip}");
            if (!moving) continue;
            var c = _amSeen.GetValueOrDefault(u.Name);
            bool hasWalk = m.Anim.Pick("walk_1", "walk_2") != null, hasSwim = m.Anim.Pick("swim_1", "swim_2", "swim") != null;
            if (wet) { c.Water++; if (hasSwim && clip.StartsWith("walk")) c.WaterBad++; }
            else { c.Land++; if (hasWalk && clip.StartsWith("swim")) c.LandBad++; }
            _amSeen[u.Name] = c;
        }
    }
}
