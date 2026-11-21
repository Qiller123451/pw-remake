// The idle animations of wild animals and of the T-rex (docs/SIMULATION.md "Animation audit": animals idle).
//
// The original plays IdleAnims.txt on wild animals too: the client's CIdleAnimMgr (Scripts/Game/mgr/IdleAnimMgr.usl,
// StartAnim) takes every animal on the screen whose set lists its class; an animal counts as busy while its task is not
// "Idle" - but a wild one (owner -1) showing its "standanim" never does - and a new idle animation starts only from a
// standing or an idle_* clip. GetRandomAnim draws Random % (sum of the chances): the set's standanim when the draw is at
// most its chance (played once: SetAnim(.., 1, 0)), else the entry whose running sum of chances reaches it (played with
// SetSLEAnim(anim, min_loops .. max_loops, intro, outro)); when it ends (its length: the timer) the next is drawn. The
// server's walking, feeding, sleeping, fighting clips replace it at once (the client's animation is only a look).
// IdleAnims.txt (Base) has sets for the wild ankylosaurus, apatosaurus, stegosaurus, wild boar, kentrosaurus ...
// (standanim 20, menace 5, rest 3, feeding 5 - 2 loops each, rest 3 .. 5); MIRAGE adds many.
//
// The T-rex (script class CTrex: "trex", MIRAGE "tred") has no standing clip in its idle: CTrex.SetIdleAnim
// (Animal.usl:4179-4193, MIRAGE 6795-6808), called whenever it falls idle and after every action of it, draws
// Random % 5: 0 AnimAction("idle_0", Random % 15, intro, outro), 1 .. 4 AnimAction("idle_<k>") once - so it is always
// in one of its idle_0 .. idle_4 clips while it stands.
//
// Both are the look of the animal, not the game: their draws come from IdleRoll (SplitMix64 of the seed, the animal,
// the step and the draw) and leave World.Rng as it was (the browser remake had neither; the JS goldens stay valid).
using System;
using PwRemake.Sim.World;

namespace PwRemake.Sim.World;

public partial class World
{
    /// <summary>A number in [0, 1) for a draw of the look of an animal (its idle animation): the same in every run and
    /// replay, without a draw from World.Rng. k: which draw of this step.</summary>
    public double IdleRoll(Unit u, int k)
    {
        ulong z = Opts.Seed * 0x9E3779B97F4A7C15UL ^ ((ulong)(uint)u.Id << 32) ^ ((ulong)StepNo << 4) ^ (ulong)k ^ 0x1D1EUL << 40;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>A T-rex (script class CTrex): its idle is CTrex.SetIdleAnim's idle_0 .. idle_4.</summary>
    public bool IsTrex(Unit u) => Data.IsA(u.Name, "CTrex");

    /// <summary>
    /// The idle animation of a standing wild animal (the roaming animal's idle and wait: Animals.RoamUpdate) - the
    /// client's CIdleAnimMgr for a wild animal in its standanim, CTrex.SetIdleAnim for a T-rex; without a set its
    /// standing clip as before.
    /// </summary>
    public void WildIdle(Unit u)
    {
        if (u.BusyAnim) return;
        var A = u.Anim!;
        if (IsTrex(u) && TrexIdle(u)) return;
        var set = Data.IdleSet(u.Name);
        if (set == null || set.Count == 0) { u.IdleCur = null; A.Play(u.IdleAnim); return; }
        // the idle animation drawn last runs until it is done (the client's timer: its length)
        if (u.IdleCur != null && A.CurName == u.IdleCur && !A.Done) return;
        // GetRandomAnim: Random % all chances; the standanim (the set's first entry, IdleAnims.txt "standanim") while
        // the running sum is below the draw, then the next entry ...
        int all = 0;
        foreach (var e in set) all += (int)e.Weight;
        if (all <= 0) { u.IdleCur = null; A.Play(u.IdleAnim); return; }
        int r = (int)Math.Floor(IdleRoll(u, 0) * all), chance = (int)set[0].Weight, idx = 1;
        while (idx < set.Count && chance < r) { chance += (int)set[idx].Weight; idx++; }
        idx--;
        var pick = set[idx];
        var name = pick.Anim.ToLowerInvariant();
        if (!A.Has(name)) { u.IdleCur = null; A.Play(u.IdleAnim); return; }
        u.IdleCur = name;
        if (idx == 0) { u.IdleLeft = A.PlayLoops(name, 1, true, true, 1, null, 0.25); return; }      // SetAnim(stand, 1, 0): once
        // GetRandomLoop: Random % (max + 1 - min) + min - of the loop part: a clip without loop marks (an animal's
        // "menace" roar, "feeding") is played once (StartAnim: SetSLEAnim gives no length -> SetAnim(anim, 1, 0))
        int span = Math.Max(1, pick.MaxLoops + 1 - pick.MinLoops);
        int loops = (int)Math.Floor(IdleRoll(u, 1) * span) + pick.MinLoops;
        if (!A.Has(name + "#l")) loops = 1;
        u.IdleLeft = A.PlayLoops(name, loops, true, true, 1, null, 0.25);
    }

    /// <summary>CTrex.SetIdleAnim (Animal.usl:4181-4192): idle_0 for Random % 15 loops (0 counts as 1) or one of idle_1 ..
    /// idle_4 once, drawn again when it is done. False when the model has none of them (its standing clip then).</summary>
    public bool TrexIdle(Unit u)
    {
        var A = u.Anim!;
        if (u.IdleCur != null && A.CurName == u.IdleCur && u.IdleCur.StartsWith("idle_", StringComparison.Ordinal) && !A.Done) return true;
        int k = (int)Math.Floor(IdleRoll(u, 2) * 5);
        var name = "idle_" + k;
        if (!A.Has(name)) { name = "idle_0"; k = 0; if (!A.Has(name)) return false; }
        int loops = k == 0 ? (int)Math.Floor(IdleRoll(u, 3) * 15) : 1;
        u.IdleCur = name;
        u.IdleLeft = A.PlayLoops(name, loops, true, true, 1, null, 0.25);
        return true;
    }
}
