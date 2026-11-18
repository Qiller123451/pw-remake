// The state of special moves on the entities (systems/moves.js, moves_mirage.js): fields the JS set on units and tasks.
//
// Ported from remake/src/game/systems/moves.js and moves_mirage.js (pw-exporter 98c7703).
using System.Collections.Generic;
using PwRemake.Sim.Core;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

public sealed partial class Unit
{
    /// <summary>the Norse warrior's defensive mode (defensive_mode_on / _off)</summary>
    public bool Defensive;
    // (RageT, RageHit, LastTarget: Combat.Entities.cs)
    /// <summary>MIRAGE: the minigun lumberjack's explosive rounds are on</summary>
    public bool Rounds;
    /// <summary>MIRAGE: Babbage going up to become the exo suit (until)</summary>
    public double SuitT;
    /// <summary>MIRAGE: the exo suit's driver class (Babbage climbs out when it is destroyed)</summary>
    public string? Driver;
    /// <summary>the automatic moves of the unit by its tree's version (moves.js u.autoMovesCache)</summary>
    public AutoMovesList? AutoMoves;
    /// <summary>the act a move's Run just began (Moves.Act / Moves.At) - taken over by the caller (World.BeginAct) into the
    /// unit's task; null otherwise</summary>
    public MoveAct? NewAct;
}

/// <summary>A move playing out (the original's CSpecialActionTask after StartSpecialActionTimer, docs/SIMULATION.md
/// "Special moves play out"): the move's clip runs from Start to End, the unit stands in it and cannot be given another
/// order (SetUnStoppable(true): the order follows the act), its effects land at their times (Hits) - a stun ends the act
/// and the effects not landed yet are lost. Times are game times (World.Time).</summary>
public sealed class MoveAct
{
    /// <summary>the clip played (lower case; null: the model has none of the move's clips)</summary>
    public string? Clip;
    public double Start, End;
    /// <summary>the effects to come, by time (Delayed entries); Next: the first one not landed yet</summary>
    public List<ActHit> Hits = new();
    public int Next;
    /// <summary>a plain AnimAction of the original (no unstoppable task: reveal, oracle, illusion, fireworks): a new order
    /// ends it at once</summary>
    public bool Breakable;
    /// <summary>a model in the right hand for the act (Moves.Hold): put away when it ends</summary>
    public bool Tool;
}

/// <summary>an effect of an act: DELAYED[Kind](world, A) at the game time T</summary>
public sealed class ActHit
{
    public double T;
    public string Kind = "";
    public Args A = new();
}

/// <summary>u.autoMovesCache: the automatic moves in AUTO_ORDER, for one version of the unit's tree</summary>
public sealed class AutoMovesList
{
    public int V;
    public List<ActionDef> List = new();
}

public sealed partial class UnitTask
{
    /// <summary>special: the move (MOVES entry)</summary>
    public MoveDef? Move;
    /// <summary>special: the point clicked (JS t.pos)</summary>
    public V3? MovePos;
    /// <summary>special: the task before (JS t.prev)</summary>
    public UnitTask? Prev;
    /// <summary>special: time to the next path (JS t.repathT)</summary>
    public double RepathT;
    /// <summary>special: a spirit as the target (Resurrect, reanimation: the JS's t.target)</summary>
    public Spirit? Spirit;
    /// <summary>special, phase "act": the move playing out (null: still walking into range)</summary>
    public MoveAct? Act;
    /// <summary>cast (World.Cast): its kind (JS t.kind) and arguments (t.args)</summary>
    public string? CastKind;
    public Args? CastArgs;
}

/// <summary>MIRAGE parasite: a player sees what the host sees (moves_mirage.js W.watchers)</summary>
public sealed partial class Watcher
{
    public Player? Player;
    public Entity? Host;
    public double Until;
}
