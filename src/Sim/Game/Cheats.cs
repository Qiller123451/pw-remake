// The debug options in a running game (docs/DEBUG.md; DebugOptions.cs is what the menu chose): set up once the players
// and objects exist (before the computer players start), and asked by the systems while the game runs (the damage of
// combat, the storage and gathering of economy, the speed of production read W.Cheats / player.Cheat).
//
// "You" is the human player. "Enemies" are the players hostile to you when the game starts (for damage: whoever is
// hostile at the moment of the hit) and everything without an owner (wild animals, neutral buildings). Allies and
// neutral players play by the normal rules.
//
// Ported from remake/src/game/cheats.js (pw-exporter 98c7703): applyCheats, raiseEpoch, sideOf, cheatDamage (the
// options themselves - defaultCheats, normCheats, cheatSummary, configCheats - are DebugOptions.cs; spawnable,
// spawnObject, objectReport are the debug tools').
using System;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Game;

public static class Cheats
{
    static readonly Regex AgeId = new(@"^age_\d$", RegexOptions.CultureInvariant);

    /// <summary>which side a player is on: "me" (the human), "foe" (hostile to the human, or nobody's), null (allied / neutral)</summary>
    public static string? SideOf(GameWorld W, Player? p)
    {
        var C = W.Cheats;
        if (C == null) return null;
        if (p == null) return "foe";
        if (p.Id == C.Human) return "me";
        var me = W.Players.FirstOrDefault(q => q.Id == C.Human);
        return me != null && (me.IsEnemy(p) || p.IsEnemy(me)) ? "foe" : null;
    }

    /// <summary>damage after the options: × the attacker's side's damage, ÷ the victim's side's defence (combat.js damage)</summary>
    public static double CheatDamage(GameWorld W, double d, Entity? A, Entity V)
    {
        var C = W.Cheats;
        if (C == null) return d;
        var a = A != null ? SideOf(W, A.Owner) : null; var v = SideOf(W, V.Owner);
        if (a != null) d *= (a == "me" ? C.Me : C.Foe).Dmg;
        if (v != null) d /= (v == "me" ? C.Me : C.Foe).Def;
        return d;
    }

    /// <summary>set the options up in a game whose players and objects exist (main.js, before the computer players
    /// start). c = DebugOptions.ForGame() (null: a normal game), me = the human player</summary>
    public static void Apply(GameWorld W, DebugOptions? c, Player? me)
    {
        // the first-shot delay: every player's (the original's without debug options)
        W.FirstShot = new FirstShotDelay { On = c?.FirstShot ?? true, Seconds = c?.FirstShot == true ? c.FirstShotDelay : null };
        if (c == null || me == null) { W.Cheats = null; return; }
        var D = W.Data;
        W.Cheats = new WorldCheats { Human = me.Id, Me = Copy(c.Me), Foe = Copy(c.Foe), Gather = c.Gather, Speed = c.Speed, Ai = c.Ai, Reveal = c.Reveal };
        var foes = W.Players.Where(p => p != me && (me.IsEnemy(p) || p.IsEnemy(me))).ToList();
        if (c.Free) me.Debug = true;
        // epochs: the highest an action may reach (Rules.Check), the start epoch through the upgrades themselves
        me.MaxEpoch = c.Me.MaxEpoch;
        foreach (var p in foes) p.MaxEpoch = c.Foe.MaxEpoch;
        int n0 = W.Events.Count;
        RaiseEpoch(W, me, c.Me.StartEpoch, c.Me.MaxEpoch);
        foreach (var p in foes) RaiseEpoch(W, p, c.Foe.StartEpoch, c.Foe.MaxEpoch);
        // (no "Epoch III reached!" messages for the epochs a game starts in)
        for (int i = W.Events.Count - 1; i >= 0; i--)
        {
            var e = W.Events[i];
            if (e.Type == "upgrade" && e.Data["action"] is PwRemake.Sim.Rules.ActionDef a && AgeId.IsMatch(a.Id)) W.Events.RemoveAt(i);
        }
        // your economy and army
        foreach (var (r, v) in c.Res.All()) if (v != null) me.Res[r] = v.Value;
        me.Cheat = new PlayerCheat { Storage = c.Storage, PopMax = c.PopMax, NoHousing = c.NoHousing, Gather = c.Gather, Speed = c.Speed };
        if (c.PopMax != null) me.PopMax = c.PopMax.Value;
        if (c.Pyramid.Any(v => v != null))
        {
            var bas = me.Pyramid ?? D.Pyramid;
            me.Pyramid = bas.Select((v, i) => i < c.Pyramid.Length && c.Pyramid[i] != null ? c.Pyramid[i]!.Value : v).ToArray();
            me.Cheat.Pyramid = me.Pyramid.ToArray();
            // more places in the pyramid raise the unit limit with them (the game's 52 = 25 + 15 + 8 + 3 + 1): at least the
            // pyramid's sum, unless the limit is set itself
            if (c.PopMax == null) { double floor = me.Pyramid.Sum(); me.Cheat.PopFloor = floor; me.PopMax = Math.Max(me.PopMax, floor); }
        }
        W.RecomputeCapsOf(me);
        // (reveal: the view's fog of war reads W.Cheats.Reveal - everything visible and explored)
    }
    static DebugSide Copy(DebugSide s) => new() { StartEpoch = s.StartEpoch, MaxEpoch = s.MaxEpoch, Dmg = s.Dmg, Def = s.Def };

    /// <summary>give player p the epoch upgrades up to `epoch` (capped by `max`), as if researched at its headquarters</summary>
    public static void RaiseEpoch(GameWorld W, Player p, int epoch, int max = 0)
    {
        var D = W.Data;
        int top = max > 0 ? Math.Min(epoch, max) : epoch;
        for (int k = p.Epoch() + 1; k <= top; k++)
        {
            var id = "age_" + k;
            var a = D.Actions(p).FirstOrDefault(x => x.Kind == "Upgrades" && x.Id == id);
            if (a == null) break;
            // the building that offers it (headquarters, fireplace, ...), else any of the player's objects
            Entity? at = W.Buildings.FirstOrDefault(b => b.Alive && b.Owner == p && D.ActionsOf(b.RulesOwner(), b.Name, b.Kind, b.Def?.Type).Any(x => x.Id == id))
                ?? (Entity?)W.Buildings.FirstOrDefault(b => b.Alive && b.Owner == p) ?? W.Units.FirstOrDefault(u => u.Alive && u.Owner == p);
            W.CompleteUpgrade(at, a, p);
        }
    }
}
