// Finishing moves: a fighter whose next blow would kill an enemy it fights - and that fights it - kills it with a
// move of its own: the pair of animations of Scripts/Server/settings/FinishingMoves.txt, the victim pulled to the
// fighter's link point and carried along until the move ends.
//
// Reference: misc/FinishingMoveMgr.usl, task/FinishingMove.usl, task/Fight.usl:419-437, FightingObj.InitFinishingMove
// (FO:7250); docs/spec/combat.md §7. Data: gamedata.json "finishing" (pipeline/build_data.py finishing_moves):
// fighter class -> [{e: victim classes, a: [[fighter anim, victim anim]], link, delay, del}].
//
// The check comes with every blow (after the special moves): the fighter is not in a transport and not already in a
// move, the victim is not the last it finished, the victim's hit points are no more than the blow's damage (the
// victim's last damage taken, else the fighter's), the fighter has a move against the victim's class and its link,
// and each is the other's enemy. Then the victim dies at once (skulls, no death cry: OnKill + DieFastAndSilent) -
// a character as a corpse, an animal as a carcass - and plays its part of the move; the fighter is invulnerable
// until its animation ends. Moves with DeleteCorpse leave nothing.
//
// Ported from remake/src/game/systems/finishing.js (pw-exporter 98c7703). The JS placed the victim's model on the
// fighter's link matrix; here the victim's position follows the link's rest position on the fighter (Unit.Pos,
// Heading) - the view poses the model.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

/// <summary>A finishing move of the data: victim classes, animation pairs (fighter, victim), the link, the pull delay,
/// whether the corpse goes (DeleteCorpse).</summary>
public sealed class FinishingMoveDef
{
    public List<string> E = new();
    public List<(string Fighter, string? Victim)> A = new();
    public string Link = "";
    public double Delay;
    public bool Del;
}

/// <summary>The victim's side of a finishing move (V.finish): by whom, its animation, the link, when it started, the pull
/// time, where it stood, DeleteCorpse, carried by the link already.</summary>
public sealed class FinishState
{
    public Unit By = null!;
    public string? Anim;
    public string Link = "";
    public double T0, Delay;
    public V3 From;
    public bool Del, Linked;
}

public sealed partial class World
{
    Dictionary<string, List<FinishingMoveDef>>? _finishing;

    Dictionary<string, List<FinishingMoveDef>> FinishingTable()
    {
        if (_finishing != null) return _finishing;
        _finishing = new(StringComparer.Ordinal);
        if (Data.Extra.Finishing is JsonObject all)
            foreach (var (k, v) in all)
            {
                var l = new List<FinishingMoveDef>();
                if (v is JsonArray arr)
                    foreach (var m in arr)
                    {
                        if (m is not JsonObject o) continue;
                        var d = new FinishingMoveDef { Link = o["link"].Str(), Delay = o["delay"].Num(0), Del = o["del"].Bool() };
                        if (o["e"] is JsonArray e) foreach (var x in e) d.E.Add(x.Str());
                        if (o["a"] is JsonArray a) foreach (var p in a) if (p is JsonArray pa && pa.Count > 0) d.A.Add((pa[0].Str(), pa.Count > 1 && pa[1] != null ? pa[1].Str() : null));
                        l.Add(d);
                    }
                _finishing[k] = l;
            }
        return _finishing;
    }

    public FinishingMoveDef? FinishingMoveOf(Unit u, Entity V)
    {
        if (!FinishingTable().TryGetValue(u.Name.ToLowerInvariant(), out var moves)) return null;
        var v = V.Name.ToLowerInvariant();
        return moves.FirstOrDefault(m => m.E.Contains(v));
    }

    /// <summary>Fight.usl: before the blow; true = the move replaces it</summary>
    public bool StartFinishingMove(Unit u, Entity Ve, Weapon? w)
    {
        if (Ve is not Unit V || !V.Alive || u.Inside || V.Inside || u.Finishing || V.Finish != null || u.LastFinishVictim == V) return false;
        var mv = FinishingMoveOf(u, V);
        if (mv == null) return false;
        double blow = Math.Ceiling(V.DmgCache != 0 && !double.IsNaN(V.DmgCache) ? V.DmgCache : (u.Cs(w ?? u.Weapon)?.Dmg ?? 0));
        if (!(V.Hp <= blow)) return false;
        if (!(V.Task.Type == "attack" && V.Task.Target == u)) return false;          // each the other's current enemy
        if (!u.FindLink(mv.Link)) return false;
        var pairs = mv.A.Where(p => u.Anim!.Has(p.Fighter)).ToList();
        if (pairs.Count == 0) return false;
        var (fa, ea) = pairs[(int)Math.Floor(Rng.Next() * pairs.Count)];
        // the victim: dead at once, its part of the move
        u.LastFinishVictim = V;
        V.Finish = new FinishState { By = u, Anim = ea != null && V.Anim!.Has(ea) ? ea : null, Link = mv.Link, T0 = Time, Delay = mv.Delay != 0 ? mv.Delay : 0.4, From = V.Pos, Del = mv.Del, Linked = false };
        V.InvulnT = 0;
        Kill(V, u);
        // the fighter: its animation, invulnerable meanwhile
        double len = u.Anim!.Duration(fa); if (len == 0) len = 2;
        u.Finishing = true;
        u.Task = new UnitTask("finish") { Victim = V, Until = Time + len, User = u.Task.User };
        u.Path = new NavPath(); u.Vel = V3.Zero;
        u.BusyAnim = true;
        u.Anim.Play(fa, new AnimOpts { Loop = false, Restart = true, Fade = 0.05 });
        Emit("finishingMove", new Args { ["unit"] = u, ["victim"] = V });
        return true;
    }

    partial void FinishUpdate(Unit u, double dt)
    {
        var t = u.Task;
        u.Vel = V3.Zero;
        if (Time < t.Until && u.Alive) return;
        u.Finishing = false;
        u.BusyAnim = false;
        var V = t.Victim as Unit;
        if (V?.FinishMove != null) FinishDone(V);
        u.Task = new UnitTask("attack") { Target = V, User = t.User };
        EndAttack(u);
        if (t.Next != null) Order(new[] { u }, t.Next);              // an order given during the move
    }

    /// <summary>the link's position on the fighter (its rest position turned by the heading)</summary>
    V3 FinishLinkPos(Unit A, string link) => Models.Link(A.Gfx, link) is V3 l ? LinkToWorld(A.Pos, A.Heading, l) : A.Pos;

    /// <summary>the victim (a dead unit) during the move: pulled to the link in `delay` s, then carried by it</summary>
    partial void FinishedUpdate(Unit u)
    {
        var V = u;
        var f = V.FinishMove;
        if (f == null) return;
        var A = f.By;
        if (A == null || !A.Alive || !A.Finishing) { FinishDone(V); return; }
        if (!A.FindLink(f.Link)) return;
        double k = Math.Min(1, (Time - f.T0) / Math.Max(0.01, f.Delay));
        var p = FinishLinkPos(A, f.Link);
        if (k < 1) { V.Pos = V3.Lerp(f.From, p, k); return; }
        // linked: the victim's model frame on the link's frame (the view)
        f.Linked = true;
        V.Pos = p; V.Heading = A.Heading;
    }

    /// <summary>the move is over: the victim lies where the link left it (or is gone: DeleteCorpse)</summary>
    void FinishDone(Unit V)
    {
        var f = V.FinishMove;
        V.Finish = null;
        V.Pos = new V3(V.Pos.X, Height(V.Pos.X, V.Pos.Z), V.Pos.Z);
        V.LookChanged();
        if (f != null && f.Del) { V.SilentDeath = true; if (V.CorpseNode != null && V.CorpseNode.Alive) DepleteNode(V.CorpseNode); }
    }
}
