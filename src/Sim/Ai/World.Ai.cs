// The computer players in the world: their turn in the step (World.Step's AI phase: main.js `for (const b of
// G.brains.values()) b.update(s)`), and the fields the brains keep on entities (the JS set u.aiFlag, u.aiStuckT ...
// on the objects themselves).
using PwRemake.Sim.Ai;

namespace PwRemake.Sim.World;

public sealed partial class World
{
    /// <summary>the computer players of this game (null: none) - AiPlayers.Attach sets it</summary>
    public AiPlayers? Ai;

    /// <summary>main.js: G.brains update - the computer players of AiPlayers (a skirmish's, the campaign's TribeAI brains),
    /// then any other campaign brain of World.Brains (an IBrainFactory of its own)</summary>
    partial void AiUpdate(double dt)
    {
        Ai?.Update(dt);
        if (Brains.Count == 0) return;
        foreach (var b in Brains.Values) if (b is not TribeAI t || t.Owner != Ai) b.Update(dt);
    }

    /// <summary>W.recomputeCaps(p) for the brain's handicaps (economy.js)</summary>
    public void AiRecomputeCaps(Player p) => RecomputeCapsOf(p);

    /// <summary>is the unit held to the heal walk a computer player sent it on (MIRAGE FightingObj.usl m_bAIHealOrder /
    /// IsAIHealWalkActive; Fountains.cs)? Only while the walk ("fountain" task) is on and the unit is not trapped, asleep or
    /// frozen (all of them the "trapped" state here); otherwise the lock ends - for good, as the original clears
    /// m_bAIHealOrder the first time an order finds the walk over</summary>
    public bool AiHealLocked(Unit u)
    {
        if (!u.AiHealLock) return false;
        if (u.Alive && u.Task.Type == "fountain" && !u.Active("trapped")) return true;
        u.AiHealLock = false;
        return false;
    }

    /// <summary>the land label of a grid cell (World._island[k]; -1: water, cliff) - the mine belts' path costs</summary>
    public int IslandCell(int k)
    {
        if (_island == null) LandIsland(0, 0);
        return _island![k];
    }
}

public abstract partial class Entity
{
    /// <summary>the computer player chose this mode / variant for it (an Aje farm mode, a unit's ObjFlag upgrade)</summary>
    public string? AiFlag;
}

public sealed partial class Unit
{
    /// <summary>when it would not march with its squad (left out of the pool for five minutes)</summary>
    public double? AiStuckT;
    /// <summary>a squad's march: waits for the last one; its best distance to the aim, the attack, when</summary>
    public bool AiWait;
    public double? AiBest;
    public int AiAtk;
    public double AiBestT;
    /// <summary>the target the fight gave it and when</summary>
    public Entity? AiTgt;
    public double AiTgtT;
    /// <summary>a worker that took cover: when</summary>
    public double AiHidT;
    /// <summary>a transport's stance before a crossing</summary>
    public int? FerryStance;
    /// <summary>MIRAGE: a computer player sent it to heal (AiGoalUseFountains "Action_AILock"): its other orders do not turn
    /// it around until it gets there (World.AiHealLocked; Fountains.cs)</summary>
    public bool AiHealLock;
}
