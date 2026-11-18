// --use-move <action id>[:<game s>]: a test run's special move (docs/DEBUG.md, docs/SIMULATION.md "Special moves play
// out"). At that game time (default: 3 s after --spawn-at) the player's first unit that offers the move uses it - on the
// nearest enemy (a move with a target), through the player's own command (UseMoveCommand) - and the camera looks at it;
// with --shots <dir> three screenshots during the act (a quarter, half and nine tenths into it: move_1..3.png) and one
// after it (move_4.png) show the clip playing out. Logged: MOVE lines (used, the act's clip and end, the shots, its end).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Commands;
using PwRemake.Sim.Core;
using PwRemake.Sim.World;

namespace PwRemake.App;

public sealed partial class Autoplay
{
    string? _useMove; double _useMoveAt = -1; Unit? _mover; bool _moverInAct; int _moveShot; double _moveEnd;
    static readonly double[] MOVE_SHOTS = { 0.25, 0.5, 0.9 };

    void MoveShot(int n)
    {
        if (_shots == null) return;
        var f = System.IO.Path.Combine(_shots, $"move_{n}.png");
        S.GetViewport().GetTexture().GetImage().SavePng(f);
        var u = _mover;
        L.Info($"MOVE shot {f} at {S.World.Time:0.00} s: {u?.Name} clip {u?.Anim?.CurName} {u?.Anim?.Cur?.Time:0.00} s, task {u?.Task}");
    }

    void ConfigureMove(Dictionary<string, string> args)
    {
        if (!args.TryGetValue("use-move", out var um) || um.Length == 0) return;
        var p = um.Split(':');
        _useMove = p[0];
        _useMoveAt = p.Length > 1 ? Json.ParseNum(p[1], _spawnAt + 3) : _spawnAt + 3;
    }

    void MoveCheck()
    {
        var W = S.World;
        if (_mover != null)
        {
            bool inAct = World.InAct(_mover);
            if (inAct && !_moverInAct) { var a = _mover.Task.Act!; L.Info($"MOVE {_mover.Name} #{_mover.Id} plays {a.Clip ?? "(no clip)"} from {a.Start:0.00} to {a.End:0.00} s, {a.Hits.Count} effect(s) to come"); _moveShot = 0; }
            if (inAct)
            {
                var a = _mover.Task.Act!;
                _moveEnd = a.End;
                S.LookAt(_mover.Pos.X, _mover.Pos.Z);
                if (_moveShot < MOVE_SHOTS.Length && W.Time >= a.Start + MOVE_SHOTS[_moveShot] * (a.End - a.Start)) MoveShot(++_moveShot);
            }
            if (!inAct && _moverInAct) { L.Info($"MOVE over at {W.Time:0.00} s, task {_mover.Task}"); _moverInAct = false; }
            else _moverInAct = inAct;
            if (!inAct && _moveEnd > 0 && W.Time >= _moveEnd + 0.6) { MoveShot(4); _mover = null; _moveEnd = 0; }
            return;
        }
        if (_useMove == null || W.Time < _useMoveAt || S.Cinematic || S.Me == null) return;
        var id = _useMove; _useMove = null;
        var u = W.Units.FirstOrDefault(x => x.Alive && x.Owner == S.Me && W.MovesOf(x).Any(a => a.Id == id && W.MoveCheck(x, a) == null));
        if (u == null) { L.Info($"MOVE {id}: no unit of yours can use it now"); return; }
        Entity? foe = null;
        if (Moves.MOVES.TryGetValue(id, out var M) && !M.Self)
            foe = W.Units.Where(e => e.Alive && !e.Inside && u.IsEnemy(e)).OrderBy(e => V3.Dist2D(e.Pos, u.Pos)).FirstOrDefault();
        var c = new UseMoveCommand { Casters = new[] { u.Id }, ActionId = id, Target = foe?.Id ?? 0, HasGround = foe != null, X = foe?.Pos.X ?? 0, Z = foe?.Pos.Z ?? 0 };
        S.Submit(c, why => L.Info($"MOVE {id}: {u.Name} #{u.Id} on {foe?.Name ?? "-"}: {why ?? "used"} at {W.Time:0.00} s"));
        S.LookAt(u.Pos.X, u.Pos.Z); S.Camera.Dist = S.Camera.TDist = 22;
        _mover = u; _moverInAct = false;
    }
}
