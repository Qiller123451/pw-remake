// The computer player in the skirmish game types Defender and Domination ("KingOfTheHill" in the scripts;
// docs/spec/ai_gametypes.md). TribeAI methods; S = the census.
//
//   * The game type and the defender role are read once when the brain starts (UM:61-79, 324-342): the defender is
//     the player owning a defender_object (the crystal of head quarter 1).
//   * Defender, the defending player: always Schnecke (CM:292), no plan attacks (the attack tables' defender rows),
//     its idle army guards the first damaged crystal, else the last one (GV:399-428, 985-1011). Nobody builds walls
//     in a Defender game (DM:675); the attackers build no towers (DM:148) and take on a crystal first (AO:1314).
//   * Domination: every strategy think goes through the flags, the nearest first (DA:866-941): a neutral flag gets a
//     "KotH Pyramid" attack (one flag on the map) or "KotH Violence", an enemy one "KotH Siege" when a building of
//     the enemy stands near it, else "KotH Suicide" and "KotH Violence" by turns; nothing else is attacked. Units
//     within 30 m of the flag stay there when such an attack is over (GA:414-461).
//   * The last 5 minutes of a domination or defender timer: a pyramid attack on what the timer's holder has near the
//     flag / the crystal (FM:1126-1180). The original also "attacks" its own or an ally's units and starts it again on
//     every fight think [bug]: here only an enemy holder, one such attack at a time.
//
// Ported from remake/src/game/ai/gametypes.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Ai;

/// <summary>what stands around a spot (SearchForDisturbance): found, the attack type, the list, their centre, wild</summary>
public sealed class Disturb
{
    public bool Found;
    public string? Type;
    public List<Entity> List = new();
    public Pt? Pos;
    public bool Wild;
}

public sealed partial class TribeAI
{
    static readonly Dictionary<string, string> GAME_TYPE = new() { ["deathmatch"] = "Deathmatch", ["domination"] = "KingOfTheHill", ["defender"] = "Defender" };
    /// <summary>the towers SearchForDisturbance looks for (DA:788-798)</summary>
    static readonly HashSet<string> TOWERS = new(StringComparer.Ordinal) { "hu_small_tower", "hu_large_tower", "aje_small_tower", "aje_medium_tower", "aje_tesla_tower", "ninigi_small_tower",
        "ninigi_large_tower", "ninigi_telescope_tower", "ninigi_smoke_tower", "seas_turret_tower" };

    /// <summary>Deathmatch | KingOfTheHill | Defender</summary>
    public string GameType = "Deathmatch";
    /// <summary>this player owns a defender_object (the defender of a Defender game)</summary>
    public bool IsDefender;
    int _kothCount;

    void GameTypeInit()
    {
        GameType = W.Go != null && GAME_TYPE.TryGetValue(W.Opts.Mode, out var t) ? t : "Deathmatch";
        IsDefender = W.Buildings.Any(b => b.Alive && b.Owner == P && b.Name == "defender_object");
    }
    /// <summary>the domination flags, the nearest to the start first (GetKothSectors, UM:17-58)</summary>
    List<(double X, double Z, Player? Owner)> KothFlags(Census S)
    {
        var at = Start ?? S.Home;
        var list = W.Go != null ? W.Go.Flags.Select(f => (f.X, f.Z, f.Owner)).ToList() : new List<(double X, double Z, Player? Owner)>();
        return at != null ? list.OrderBy(f => Hyp(f.X - at.Value.X, f.Z - at.Value.Z)).ToList() : list.ToList();
    }
    /// <summary>SearchForDisturbance (DA:703-812): what stands around a spot (a flag, a pest patrol's place). Hostile wild
    /// animals within 90 m, sorted from the start location -> "suicide" on them, unless the first is an allosaurus and
    /// the epoch is 3 or lower (then nothing, found false - the list stays as the original leaves it); else enemies
    /// within 60 m (Dodo always, Giraffe from epoch 3, Schnecke from epoch 5) -> "blitz"; else the enemy towers of the
    /// script's list within 60 m -> "blitz" in epochs 1 and 2, "towersiege" later. (Nests are no objects in the
    /// remake: the original's NEST entries are left out.)</summary>
    Disturb Disturbance(Census S, Pt at)
    {
        var from = Start ?? S.Home ?? at; int age = S.Age;
        static Pt Centre(List<Entity> l) => new(l.Sum(e => e.Pos.X) / l.Count, l.Sum(e => e.Pos.Z) / l.Count);
        var wild = new List<Entity>();
        W.UHash.Query(at.X, at.Z, 90, u => { if (u.Alive && u.Wild && !u.Naval && u.Def!.Aggressive > 0 && at.Dist(u) < 90) wild.Add(u); });
        if (wild.Count > 0)
        {
            wild = wild.OrderBy(a => from.Dist(a)).ToList();
            if (wild[0].Name.Contains("_allosaurus") && age <= 3) return new Disturb { Found = false, List = wild, Wild = true };
            return new Disturb { Found = true, Type = "suicide", List = wild, Pos = Centre(wild), Wild = true };
        }
        var outp = new List<Entity>(); string L0 = Letter;
        if (L0 == "D" || (L0 == "G" && age >= 3) || (L0 == "S" && age >= 5))
        {
            void Q(Entity e) { if (e.Alive && !e.Inside && e.Owner != null && P.IsEnemy(e.Owner) && at.Dist(e) < 60) outp.Add(e); }
            W.UHash.Query(at.X, at.Z, 60, Q); W.BHash.Query(at.X, at.Z, 70, Q);
            if (outp.Count > 0) return new Disturb { Found = true, Type = "blitz", List = outp, Pos = Centre(outp), Wild = false };
        }
        W.BHash.Query(at.X, at.Z, 70, b => { if (b.Alive && b.Owner != null && P.IsEnemy(b.Owner) && TOWERS.Contains(b.Name) && at.Dist(b) < 60) outp.Add(b); });
        if (outp.Count == 0) return new Disturb { Found = false, List = outp, Wild = false };
        outp = outp.OrderBy(a => from.Dist(a)).ToList();
        return new Disturb { Found = true, Type = age <= 2 ? "blitz" : "towersiege", List = outp, Pos = Centre(outp), Wild = false };
    }
    void KothThink(Census S)
    {
        var flags = KothFlags(S);
        if (flags.Count == 0) return;
        foreach (var f in flags)
        {
            var pos = new Pt(f.X, f.Z); var owner = f.Owner;
            if (owner == null)
            {
                var d = Disturbance(S, pos);
                // an aggressive animal at a flag in the first epoch: nothing this think
                if (d.Wild && S.Age < 2 && d.List.Any(u => u.Def!.Aggressive > 0)) return;
                RequestAttack(S, flags.Count == 1 ? "KotH Pyramid" : "KotH Violence", null, d.List, pos);
            }
            else if (P.IsEnemy(owner))
            {
                var d = Disturbance(S, pos);
                string type;
                if (d.List.Any(e => e is Building)) type = "KotH Siege";
                else { type = _kothCount != 0 ? "KotH Violence" : "KotH Suicide"; _kothCount = _kothCount != 0 ? 0 : 1; }
                RequestAttack(S, type, owner, d.List, pos);
            }
        }
    }
    /// <summary>the defender's guard point: the first damaged crystal, else the last one (CheckForDefenseObj)</summary>
    Pt? CrystalGuard()
    {
        if (GameType != "Defender" || !IsDefender) return null;
        var own = W.Buildings.Where(b => b.Alive && b.Owner == P && b.Name == "defender_object").ToList();
        if (own.Count == 0) return null;
        var c = own.FirstOrDefault(b => b.Hp < b.MaxHp) ?? own[^1];
        return Pt.Of(c);
    }
    /// <summary>CheckForTimers (FM:1126-1180), on the fight think</summary>
    void TimerThink(Census S)
    {
        if (W.Go == null || GameType == "Deathmatch" || Attacks.Any(a => a.Timer && a.State != "over")) return;
        Player? holder; Pt at; double left;
        if (GameType == "KingOfTheHill")
        {
            var fl = KothFlags(S);
            if (fl.Count == 0 || fl[0].Owner == null) return;
            var dl = W.Go.Dom != null && !W.Go.Dom.Paused ? W.Go.Dom.Left : (double?)null;
            if (dl == null) return;                                    // (no timer / paused)
            holder = fl[0].Owner; at = new Pt(fl[0].X, fl[0].Z); left = dl.Value;
        }
        else
        {
            var c = W.Buildings.FirstOrDefault(b => b.Alive && b.Name == "defender_object");
            var dl = W.Go.Def?.Left;
            if (c == null || dl == null) return;
            holder = c.Owner; at = Pt.Of(c); left = dl.Value;
        }
        if (!(left >= 0 && left < 300) || holder == null || !P.IsEnemy(holder)) return;
        var targets = new List<Entity>();
        void Q(Entity e) { if (e.Alive && e.Owner == holder && !e.Inside && at.Dist(e) < 60) targets.Add(e); }
        W.UHash.Query(at.X, at.Z, 60, Q); W.BHash.Query(at.X, at.Z, 70, Q);
        if (targets.Count == 0) return;
        var a = RequestAttack(S, "pyramid", holder, targets, at, true);
        if (a != null) a.Timer = true;
    }
}
