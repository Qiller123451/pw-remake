// The debug options in a running game (game/cheats.js W.cheats): who "you" are and each side's factors. The game's
// start (cheats.js applyCheats: the campaign / skirmish setup) fills World.Cheats from the normalised DebugOptions;
// combat reads the damage and defence factors (cheatDamage), economy and production the others.
//
// Ported from remake/src/game/cheats.js sideOf, cheatDamage (pw-exporter 98c7703).
using PwRemake.Sim.Game;

namespace PwRemake.Sim.World;

/// <summary>W.cheats = { human, me, foe, gather, speed, ai }</summary>
public sealed class WorldCheats
{
    /// <summary>the id of the human player ("you")</summary>
    public int Human;
    public DebugSide Me = new(), Foe = new();
    public double Gather = 1, Speed = 1;
    public string Ai = "normal";
    /// <summary>the whole map visible: no fog of war, every place explored (the view's fog reads it)</summary>
    public bool Reveal;

    /// <summary>the running game's options from the start's (normalised) debug options</summary>
    public static WorldCheats From(int human, DebugOptions c) => new()
    {
        Human = human, Me = c.Me.Normalized(), Foe = c.Foe.Normalized(), Gather = c.Gather, Speed = c.Speed, Ai = c.Ai,
    };
}

public sealed partial class World
{
    /// <summary>the debug options of this game (cheats.js W.cheats); null: a normal game</summary>
    public WorldCheats? Cheats;

    /// <summary>which side of the debug options a player is on: "me", "foe" (hostile to you, or no owner) or null (allies,
    /// neutral players, no options)</summary>
    public string? SideOf(Player? p)
    {
        var C = Cheats;
        if (C == null) return null;
        if (p == null) return "foe";
        if (p.Id == C.Human) return "me";
        var me = Players.Find(q => q.Id == C.Human);
        return me != null && (me.IsEnemy(p) || p.IsEnemy(me)) ? "foe" : null;
    }

    /// <summary>damage after the options: x the attacker's side's damage, / the victim's side's defence (combat.js damage)</summary>
    public double CheatDamage(double d, Entity? A, Entity V)
    {
        var C = Cheats;
        if (C == null) return d;
        string? a = A != null ? SideOf(A.Owner) : null, v = SideOf(V.Owner);
        if (a != null) d *= (a == "me" ? C.Me : C.Foe).Dmg;
        if (v != null) d /= (v == "me" ? C.Me : C.Foe).Def;
        return d;
    }
}
