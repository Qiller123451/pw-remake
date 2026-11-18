// The state mission scripting keeps on entities, players and the world (game/scripting.js, game/campaign/setup.js,
// game/cheats.js): the record of the campaign registry an object belongs to, the level designer's invulnerability and
// MIRAGE's disarming with their timers, parking (out of the world: OBAP), the fog-of-war reveals of SFOW, the
// highlights of OCPY, a player's script filters and the debug options' values.
//
// Fields only; the methods are in Scripting.cs (World) and src/Sim/Campaign. Ported from remake/src/game/scripting.js
// and campaign/setup.js (pw-exporter 98c7703): the JS put these as loose properties on the objects.
using System.Collections.Generic;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.World;

public abstract partial class Entity
{
    /// <summary>the campaign registry's record of this object (setup.js e.rec; null outside a mission or for an
    /// object the registry does not know)</summary>
    public PwRemake.Sim.Campaign.Rec? Rec;
    /// <summary>the level designer's invulnerability ends at this game time (SetLDInvulnerable with a timer; 0 = none)</summary>
    public double InvulnUntil;
    // (Disarmed, DisarmUntil, IsNest: Combat.Entities.cs)
    /// <summary>the human's mobile base of the start (setup.js parkFreeMains)</summary>
    public bool StartMain;

    /// <summary>the owner changed: the cached rules owner (owner + local tree) is made anew (scripting.js `e._ro = null`)</summary>
    public void OwnerChanged() => ResetRulesOwner();
}

public sealed partial class Building
{
    // (InvulnT: Combat.Entities.cs)
    /// <summary>its last pose of SetAnim (a gate that opened), kept for a saved game (setup.js playAnim e.lastAnim)</summary>
    public string? LastAnim;
}

public sealed partial class UnitTask
{
    /// <summary>the WYPT patrol this walk belongs to (engine.js task.patrol; Campaign/MissionHost.cs)</summary>
    public PwRemake.Sim.Campaign.Patrol? Patrol;
}

public sealed partial class Player
{
    /// <summary>the mission data of this player (mission.json players[i]); null outside a mission</summary>
    public System.Text.Json.Nodes.JsonNode? Data;
    /// <summary>debug option "no mission bans" (game/cheats.js): the level's AntiActions do not reach this player</summary>
    public bool UnlockAll;
    /// <summary>the filters a script switched (on / off without counting: scripting.js setFilter)</summary>
    public readonly OrderedSet<string> ScriptFilters = new();
    /// <summary>the waiting mobile bases a worker can put down free (setup.js freeMains)</summary>
    public readonly List<Entity> FreeMains = new();
    // (Cheat: Economy.cs)
}


/// <summary>A fog of war circle given to a player (scripting.js revealArea): around (X, Z) or around Follow while it
/// lives, until Until.</summary>
public sealed partial class Reveal
{
    public Player? Player;
    public double X, Z, R;
    public double Until = double.PositiveInfinity;
    public Entity? Follow;
    public bool Alive = true;
}


public sealed partial class World
{
    // (Cheats: Combat.Cheats.cs)
    /// <summary>OCPY chk_highlight: what the player was given is marked until the game time (the view draws a ring)</summary>
    public readonly OrderedMap<Entity, double> Highlights = new();
    /// <summary>the computer players' brains (main.js G.brains): player id -> brain, made by the campaign's
    /// IBrainFactory (src/Sim/Ai). The computer player's AiUpdate updates them in this order.</summary>
    public readonly OrderedMap<int, PwRemake.Sim.Campaign.ICampaignBrain> Brains = new();
    /// <summary>the running mission (src/Sim/Campaign): its part of every step (CampaignUpdate)</summary>
    public ICampaignStep? Campaign;
}

/// <summary>What the world calls of the campaign every step (main.js simulate: G.campaign.update after the brains).</summary>
public interface ICampaignStep
{
    void Update(double dt);
}
