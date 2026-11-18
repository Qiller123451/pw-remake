// MIRAGE: the computer player sends its damaged units to heal at a fountain or a SEAS well and back to where they stood.
//
// Reference: the MIRAGE fix delivered to the modder (mirage_ai_fountains_v2, built on master 3a17f55 and the modder's
// own AiGoalUseFountains.usl; not in master yet):
//   Ai/goals/AiGoalUseFountains.usl     the goal (HEAL_BELOW 0.6, MAX_DISTANCE 200, MIN_FILL 30, MAX_ORDERS 6 per think,
//                                       RETRY_THINKS 10, SetThinkWait(40)); NeedsHealing, CollectPlaces, FindPlace,
//                                       SendToPlace ("Action_AILock" + /FountainHeal or /SeasWellHeal, then "Q_Action
//                                       /Walk" back), ForgetOldOrders
//   Ai/Modules/AiModuleDefenseDefault.usl  every AI player gets the goal (every game mode and difficulty); the goal skips
//                                       Mikrobe and a human player's AI assist
//   Server/classes/FightingObj/FightingObj.usl  the heal lock: m_bAIHealOrder, HandleGamePlayCommand (other orders are
//                                       ignored while the heal walk is on, but queued ones, Kill, LevelUp, Dismount,
//                                       LeaveTransport), IsAIHealWalkActive (not while trapped, asleep or frozen)
//   README.md, ANSWERS.md
//
// Only with MIRAGE's data: the base game's AI (Data/Base/Scripts/Ai) has no such goal and its units never go to a
// fountain by themselves. Mikrobe (a sleeping brain) skips it as the goal does - TribeAI.Update returns before the
// modules while Paused; the remake has no AI assist for a human player.
//
// In the remake: the places are the map's wells (World.Wells: neutral, "hitpoints" = Fill) and the finished SEAS wells
// (Building.SeasWell: energy) of the player and its allies; the heal order is "fountain" with the walk back as its
// UnitTask.Then (the queued walk), given through IAiHands.HealOrder - AiOrderCommand.HealLock sets Unit.AiHealLock, and
// World.AiHealLocked keeps the brain's other orders (AiOrderCommand) and special moves (CommandHands.UseMove) away
// from the unit while the walk is on. Human players' units never get the lock (only this goal sets it).
//
// Not a JS module (the browser remake's computer player had no such goal). docs/AI.md "Healing at fountains".
using System;
using System.Collections.Generic;
using PwRemake.Sim.World;

namespace PwRemake.Sim.Ai;

public sealed partial class TribeAI
{
    const double FNT_HEAL_BELOW = 0.6;      // units below 60 % of their hit points go
    const double FNT_MAX_DISTANCE = 200;    // only places this close to the unit
    const double FNT_MIN_FILL = 30;         // a fountain / well needs at least this much healing left
    const int FNT_MAX_ORDERS = 6;           // units sent per think
    const int FNT_RETRY_THINKS = 10;        // a unit sent once is not sent again for this many thinks
    /// <summary>SetThinkWait(40): ticks between the goal's thinks</summary>
    const int FNT_THINK_WAIT = 40;

    int _fntThink;
    double _fntNext = -1;
    /// <summary>units sent recently and the think they were sent in (m_aiSentHandle / m_aiSentThink)</summary>
    readonly List<(Unit U, int Think)> _fntSent = new();

    /// <summary>does this brain run the goal? MIRAGE's data only (the base game has none)</summary>
    public bool UsesFountains => string.Equals(D.Extra.Mod, "MIRAGE", StringComparison.OrdinalIgnoreCase);

    /// <summary>the goal's timer (Update: every 40 ticks; its own clock, no random numbers - the other modules' timers
    /// stay as they were)</summary>
    void FountainsTick(double now)
    {
        if (!UsesFountains) return;
        if (_fntNext < 0) _fntNext = now + FNT_THINK_WAIT * TICK;
        if (now < _fntNext) return;
        _fntNext = now + FNT_THINK_WAIT * TICK;
        var s = Census();
        if (s.Base != null) FountainsThink(s);
    }

    /// <summary>CAiGoalUseFountains.Think: up to six damaged units to the nearest place they may use</summary>
    public void FountainsThink(Census S)
    {
        if (!UsesFountains || Paused) return;
        _fntThink++;
        // ForgetOldOrders: units sent RETRY_THINKS thinks ago may be sent again
        _fntSent.RemoveAll(x => _fntThink - x.Think >= FNT_RETRY_THINKS);
        var places = FountainPlaces();
        if (places.Count == 0) return;
        int orders = 0;
        foreach (var u in S.Units)
        {
            if (orders >= FNT_MAX_ORDERS) break;
            if (!NeedsHealing(u)) continue;
            var place = FountainFor(u, places);
            if (place == null) continue;
            SendToFountain(u, place);
            orders++;
        }
    }

    static bool FntUser(Unit u) => u.Cls is "CHTR" or "ANML" or "VHCL";

    /// <summary>NeedsHealing: a damaged unit of this player that is free to go - fighting or not (v2)</summary>
    bool NeedsHealing(Unit u)
    {
        if (!u.Alive || u.Owner != P || !FntUser(u) || u.Parked || u.Autonomous) return false;
        double max = Math.Floor(u.MaxHp), hp = Math.Floor(u.Hp);              // ("maxhitpoints" / "hitpoints": whole numbers)
        if (max <= 0 || hp <= 0) return false;
        if (hp >= max * FNT_HEAL_BELOW) return false;
        if (u.Inside) return false;                                            // in a transport (IsLinked)
        if (u.Flying) return false;                                            // FountainHeal only flies them there
        if (Locked.Has(u)) return false;                                       // locked for scripts (ai_lock: AILU)
        if (u.Task.Type == "fountain") return false;                           // already on the way (FntnHeal / SWHeal)
        if (u.Active("trapped")) return false;                                 // can't walk now (trapped, asleep, frozen)
        foreach (var x in _fntSent) if (x.U == u) return false;
        return true;
    }

    /// <summary>CollectPlaces: the neutral fountains, then the finished SEAS wells of this player and its allies, with
    /// enough left in them</summary>
    List<Entity> FountainPlaces()
    {
        var res = new List<Entity>();
        foreach (var w in W.Wells)
            if (w.Usable && Math.Floor(w.Fill) >= FNT_MIN_FILL) res.Add(w);        // CFountain: "hitpoints" = water left
        foreach (var b in W.Buildings)
        {
            if (b.SeasWell == null || !b.Alive || !b.Built || b.Owner == null) continue;
            if (b.Owner != P && !P.IsFriend(b.Owner)) continue;                     // the AI's own opinion: 2
            if (b.SeasWell.Energy < FNT_MIN_FILL) continue;                         // CSeasWell: energy left
            res.Add(b);
        }
        return res;
    }

    /// <summary>FindPlace: the nearest place on the unit's land within MAX_DISTANCE that it may use (a later place at
    /// the same distance wins, as in the goal)</summary>
    Entity? FountainFor(Unit u, List<Entity> places)
    {
        int land = W.LandIslandNear(u.Pos.X, u.Pos.Z, u.Radius + 4);
        Entity? best = null; double bd = FNT_MAX_DISTANCE;
        foreach (var e in places)
        {
            double dx = e.Pos.X - u.Pos.X, dy = e.Pos.Y - u.Pos.Y, dz = e.Pos.Z - u.Pos.Z;
            double d = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (d > bd) continue;
            int pl = W.LandIslandNear(e.Pos.X, e.Pos.Z, e.Radius + 4);
            if (land >= 0 && pl >= 0 && pl != land) continue;
            // (the server would refuse what the unit may not use - an ally's well that is not mutual, poison ...: such a
            // place is not chosen, so the unit is not locked to nothing)
            if (!W.CanUseWell(u, e)) continue;
            bd = d; best = e;
        }
        return best;
    }

    /// <summary>SendToPlace: the locked heal walk, then the walk back to where the unit stood</summary>
    void SendToFountain(Unit u, Entity place)
    {
        var back = new Order("move") { X = u.Pos.X, Z = u.Pos.Z };
        Hands.HealOrder(P, u, new Order("fountain") { Target = place, Then = back });
        _fntSent.Add((u, _fntThink));
    }
}
