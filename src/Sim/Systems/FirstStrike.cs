// First strike: a unit ordered to attack strikes while it runs up to its victim (the engine's "FrstStrk" event).
//
// The original: FightingObj.FollowEnemy (base FightingObj.usl:5953-6080, MIRAGE 7967-8090 and 16029) starts the
// engine's advance action with CAdvanceActionParams.m_sFirstStrike = GetFirstStrikeAnim() - only for an order of the
// player (the branch without bNoUserCommand; the automatic follows and the guard / aggression walks leave it empty).
// While the unit runs, the engine's advance / attack-walk action (PWServer 0x49A1E0) plays that clip when it comes
// into first-strike range of a character, animal or vehicle target (Settings Server/FirstStrike/MinDist, MaxDist,
// DistFact, MaxHitDist) and sends the unit the event "FrstStrk" at once, with the time until the hit (GetFloat(0)).
// CFightingObj.HandleEvent -> OnDoFirstStrike (base FightingObj.usl:2585, MIRAGE 3732): when the unit's current task
// is a fight (CFight), its enemy takes TakeDmg(this, factor, timeToHit) - the normal damage formula with the primary
// weapon, landing timeToHit later - and the fight's follow timer is reset.
//   base:   factor 1.0; who: CCharacter with the clip first_strike_0 (character.usl:548); nobody else (animals,
//           vehicles: FightingObj's "")
//   MIRAGE: factor 1.5, or with the techtree's /fsm = 1 a shot (CreateProjectileAndShoot: Bela); who: CCharacter
//           with the techtree's /fsa (a list "first_strike_0|first_strike_1", one picked at random, if the model has
//           it - character.usl:857), CAnimal with the clip first_strike (Animal.usl:2082), the brachiosaurus only
//           without a build-up on its back (Animal.usl:2925)
//
// The engine's part (PWServer.exe 0x49A1E0, read 2026-11-18; the attack-walk action's tick):
//   settings (Settings.cfg, read once): Server/FirstStrike/MinDist 3, MaxDist 8, DistFact 0.9, MaxHitDist 9 - but the
//   tick then sets MaxHitDist to 0.5 itself (0x49A341: the setting is never used); only for a CHTR / ANML / VHCL target.
//   P = the target's position predicted the clip's hit time ahead; r = the advance's distance (the approach, measured
//   between the centres); the strike starts when MinDist^2 + r^2 < |P - A|^2 <= MaxDist^2 + r^2 (2D). The strike point
//   is S = P + DistFact * r towards the attacker; the nearest free spot W to S must lie within MaxHitDist of S and at
//   least MinDist from the attacker. Then the unit lunges to W with the clip and gets "FrstStrk" with the time to the
//   hit. Not read: two further conditions of the tick (the attacker's action state >= 2 and an age <= 20 of something
//   it keeps - left out here).
using System;
using System.Collections.Generic;
using PwRemake.Sim.Content;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

/// <summary>The engine settings Server/FirstStrike/* with PWServer.exe's defaults (0x49A22F-0x49A341).</summary>
public sealed class FirstStrikeSettings
{
    /// <summary>the strike starts beyond sqrt(MinDist^2 + r^2) from the predicted target; the lunge's end at least this
    /// far from the attacker</summary>
    public double MinDist = 3.0;
    /// <summary>... and within sqrt(MaxDist^2 + r^2)</summary>
    public double MaxDist = 8.0;
    /// <summary>the lunge ends DistFact x r before the predicted target</summary>
    public double DistFact = 0.9;
    /// <summary>the free spot may be this far from the lunge's end (the exe overrides the setting 9 with 0.5)</summary>
    public double MaxHitDist = 0.5;
}

public sealed partial class UnitTask
{
    /// <summary>attack: the first strike of this follow is done (a new follow may strike again)</summary>
    public bool FsDone;
    /// <summary>attack: the first strike's clip runs until then (no walk clip over it; the next blow waits for the hit)</summary>
    public double FsUntil, FsHit;
    /// <summary>attack: the first strike's lunge from FsFrom (at FsStart) to FsTo (at FsHit)</summary>
    public double FsStart, FsFromX, FsFromZ, FsToX, FsToZ;
    /// <summary>attack: the clip of this follow's first strike (GetFirstStrikeAnim, asked once per follow as FollowEnemy
    /// asks it once per advance action - MIRAGE's random pick too); FsPicked: asked</summary>
    public string? FsClip;
    public bool FsPicked;
}

public sealed partial class World
{
    /// <summary>Server/FirstStrike/* (see FirstStrikeSettings)</summary>
    public FirstStrikeSettings FirstStrikeCfg = new();
    static readonly Logger LFs = Log.For("Combat.FirstStrike");

    /// <summary>the damage factor of OnDoFirstStrike: base 1.0, MIRAGE 1.5</summary>
    public double FirstStrikeFactor => Data.Extra.Mod == "MIRAGE" ? 1.5 : 1.0;

    /// <summary>GetFirstStrikeAnim: the clip of the unit's first strike, null: it has none</summary>
    public string? FirstStrikeAnim(Unit u)
    {
        var A = u.Anim;
        if (A == null) return null;
        if (Data.Extra.Mod == "MIRAGE")
        {
            if (u.Cls == "CHTR")
            {
                // MIRAGE character.usl:857 - the techtree's fsa, "a|b": one at random (Random.GetInt()%n)
                string? fsa = FsaOf(u);
                if (string.IsNullOrEmpty(fsa)) return null;
                var parts = fsa.Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return null;
                var pick = parts[(int)Math.Floor(Rng.Next() * parts.Length) % parts.Length];
                return A.Has(pick) ? pick.ToLowerInvariant() : null;
            }
            if (u.Cls == "ANML")
            {
                // Animal.usl:2082 CAnimal; 2925 CBrachiosaurus: not with a build-up (a platform, a catapult ...)
                if (u.Name.Contains("brachiosaurus", StringComparison.OrdinalIgnoreCase) && HasBuildUp(u)) return null;
                return A.Has("first_strike") ? "first_strike" : null;
            }
            return null;
        }
        // base character.usl:548
        return u.Cls == "CHTR" && A.Has("first_strike_0") ? "first_strike_0" : null;
    }

    /// <summary>the techtree's /fsa of the unit's class (its owner's tree: the level bonus data copy it), else the start tree's</summary>
    string? FsaOf(Unit u)
    {
        string path = u.Def?.Path is { Length: > 0 } p ? p : Data.Index.TryGetValue(u.Name, out var ie) ? ie.Path : "";
        if (path == "") return null;
        return u.Owner?.Tt?.Get(path + "/fsa") ?? TT.Scalar(TT.NodeAt(Data.Base.Start, path + "/fsa"));
    }
    /// <summary>MIRAGE's /fsm = 1: the first strike is a shot (CreateProjectileAndShoot)</summary>
    bool FirstStrikeShoots(Unit u)
    {
        string path = u.Def?.Path is { Length: > 0 } p ? p : Data.Index.TryGetValue(u.Name, out var ie) ? ie.Path : "";
        if (path == "") return false;
        var v = u.Owner?.Tt?.Get(path + "/fsm") ?? TT.Scalar(TT.NodeAt(Data.Base.Start, path + "/fsm"));
        return TT.Num(v) == 1;
    }
    /// <summary>HasBuildUp: an add-on on the animal's back (not its rider)</summary>
    static bool HasBuildUp(Unit u)
    {
        var c = u.Comp;
        if (c == null) return false;
        foreach (var part in c.Parts) if (!ReferenceEquals(part, c.Rider)) return true;
        return false;
    }

    /// <summary>
    /// The time from the start of the clip to its hit: the clip's first sound event of a blow or shot (the GSF sound
    /// table: weapon_*_att 0.48 / 0.84 s, projectile_*_shot 0.8 s, a dinosaur's *_hit), else half the clip.
    /// </summary>
    public double FirstStrikeHitTime(Unit u, string clip)
    {
        var info = Models.Get(u.Gfx);
        var src = info?.Anims != null ? Models.Get(info.Anims) ?? info : info;
        if (src != null && src.Sounds.TryGetValue(clip, out var evs))
        {
            double best = double.NaN;
            foreach (var e in evs)
            {
                var w = e.Wav;
                if (w.Contains("_hit", StringComparison.OrdinalIgnoreCase) || w.Contains("weapon_", StringComparison.OrdinalIgnoreCase) || w.Contains("_shot", StringComparison.OrdinalIgnoreCase))
                    if (double.IsNaN(best) || e.T < best) best = e.T;
            }
            if (!double.IsNaN(best) && best > 0) return best;
        }
        double d = u.Anim?.Duration(clip) ?? 0;
        return d > 0 ? d * 0.5 : 0.5;
    }

    /// <summary>
    /// The advance action's first-strike check (PWServer 0x49A1E0), every step of a chase of a user-ordered fight
    /// (AttackUpdate) at a character / animal / vehicle target: see the header. Once per follow (UnitTask.FsDone: a new
    /// chase resets it). approach: the chase's approach distance (to the victim's surface). True: it struck.
    /// </summary>
    public bool TryFirstStrike(Unit u, UnitTask t, Entity tgt, double approach)
    {
        if (t.FsDone || !t.User || tgt is not Unit v || u.Inside || u.Flying || u.OnWall != null) return false;
        if (v.Inside || !v.Alive || (v.Cls != "CHTR" && v.Cls != "ANML" && v.Cls != "VHCL")) return false;
        var cfg = FirstStrikeCfg;
        if (!t.FsPicked) { t.FsPicked = true; t.FsClip = FirstStrikeAnim(u); }
        string? clip = t.FsClip;
        if (clip == null) { t.FsDone = true; return false; }
        double hitT = FirstStrikeHitTime(u, clip);
        // the target where it will be at the hit; r = the advance's distance between the centres
        double px = v.Pos.X + v.Vel.X * hitT, pz = v.Pos.Z + v.Vel.Z * hitT;
        double r = approach + v.Radius;
        double dx = u.Pos.X - px, dz = u.Pos.Z - pz, d2 = dx * dx + dz * dz;
        if (!(d2 > cfg.MinDist * cfg.MinDist + r * r && d2 <= cfg.MaxDist * cfg.MaxDist + r * r)) return false;
        // the lunge's end: DistFact x r before the target, towards the attacker; a free spot within MaxHitDist of it
        double d = Math.Sqrt(d2);
        double sx = px + dx / d * cfg.DistFact * r, sz = pz + dz / d * cfg.DistFact * r;
        if (!u.CanWalkInto(NavFor(u), sx, sz)) return false;     // (the free spot = S itself: within 0.5 m)
        double ex = sx - u.Pos.X, ez = sz - u.Pos.Z;
        if (ex * ex + ez * ez < cfg.MinDist * cfg.MinDist) return false;
        t.FsDone = true;
        double len = u.Anim!.Duration(clip);
        u.Anim.Play(clip, new AnimOpts { Loop = false, Restart = true, Fade = 0.1, OnDone = new AnimDone("busy") });
        u.BusyAnim = true;
        t.FsStart = Time; t.FsFromX = u.Pos.X; t.FsFromZ = u.Pos.Z; t.FsToX = sx; t.FsToZ = sz;
        t.FsUntil = Time + (len > 0 ? len : hitT);
        t.FsHit = Time + hitT;
        OnDoFirstStrike(u, v, hitT);
        return true;
    }

    /// <summary>the first strike's lunge: from where it started to the strike point by the time of the hit (straight, the
    /// way was checked free at its end); true while it runs</summary>
    public bool FirstStrikeLunge(Unit u, UnitTask t)
    {
        if (!(t.FsHit > Time) || t.FsHit <= t.FsStart) return false;
        double k = Math.Clamp((Time - t.FsStart) / (t.FsHit - t.FsStart), 0, 1);
        double nx = t.FsFromX + (t.FsToX - t.FsFromX) * k, nz = t.FsFromZ + (t.FsToZ - t.FsFromZ) * k;
        if (u.CanWalkInto(NavFor(u), nx, nz)) { u.Pos.X = nx; u.Pos.Z = nz; }
        u.Vel = V3.Zero;
        u.Heading = Geo.HeadingTo(t.FsToX - t.FsFromX, t.FsToZ - t.FsFromZ);
        return true;
    }

    /// <summary>OnDoFirstStrike (base FightingObj.usl:2585, MIRAGE 3732): the fight's enemy takes the primary weapon's
    /// damage x factor after timeToHit (MIRAGE /fsm = 1: a shot instead)</summary>
    public void OnDoFirstStrike(Unit u, Unit enemy, double timeToHit)
    {
        var w = u.Weapons.Long;
        if (w == null) return;
        if (Data.Extra.Mod == "MIRAGE" && FirstStrikeShoots(u) && w.Projectile != null) { FireShot(u, w, enemy); }
        else TakeDmg(u, enemy, FirstStrikeFactor, timeToHit, w);
        LFs.Debug(() => $"{u.Name}#{u.Id} first strike at {enemy.Name}#{enemy.Id}, hit in {timeToHit:0.00} s");
        Emit("firstStrike", new Args { ["attacker"] = u, ["target"] = enemy, ["delay"] = timeToHit });
    }
}
