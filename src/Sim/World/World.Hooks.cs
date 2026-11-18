// The calls of world.js and entities.js into the systems (game/systems/*.js) that return nothing: declared here as
// partial methods. A partial method without an implementation compiles to nothing (the call disappears); the port of
// a system implements it in its own file under src/Sim/Systems:
//
//   // src/Sim/Systems/Economy.cs
//   public partial class World { partial void DepleteNode(ResNode n) { ... } }
//
// (They are private to World - every system file is part of World. A system whose method others outside World call,
// such as commands or the computer player, adds a public method beside it.) The calls that need a value back are
// stubs in World.Stubs.cs. docs/SIMULATION.md lists both with the system each belongs to.
//
// Also the state the systems keep in the world (world.js constructor fields of systems' data): the system port fills
// the partial classes.
using System.Collections.Generic;

namespace PwRemake.Sim.World;

/// <summary>a nest of an original map that respawns wild animals (systems/animals.js wildNestsUpdate)</summary>
public sealed partial class WildNest { }
/// <summary>a healing well / a lake of healing or damage (systems/landmarks.js)</summary>
public sealed partial class Well { }
public sealed partial class Lake { }
/// <summary>the level's weather program (systems/landmarks.js weatherUpdate)</summary>
public sealed partial class WeatherProgram { }
/// <summary>the end of a skirmish (systems/gameover.js)</summary>
public sealed partial class GameOverState { }
/// <summary>an item (artefact) on the map or carried (systems/items.js)</summary>
public sealed partial class Item { }
/// <summary>a player sees what the host sees (MIRAGE parasite)</summary>
public sealed partial class Watcher { }
/// <summary>a fog of war circle given to a player (scripting revealArea)</summary>
public sealed partial class Reveal { }

public sealed partial class World
{
    /// <summary>nests of original maps: respawn wild animals (systems/animals.js)</summary>
    public List<WildNest> WildNests = new();
    /// <summary>healing wells, lakes of healing / damage (systems/landmarks.js)</summary>
    public List<Well> Wells = new();
    public List<Lake> Lakes = new();
    public WeatherProgram? Weather;
    /// <summary>the end of a skirmish (systems/gameover.js); null: none</summary>
    public GameOverState? Go;
    /// <summary>items (artefacts) on the map or carried (u.item): systems/items.js</summary>
    public List<Item> Items = new();
    /// <summary>{player, host, until}: a player sees what the host sees (MIRAGE parasite)</summary>
    public List<Watcher> Watchers = new();
    /// <summary>RevealArea(): fog of war circles given to players (scripting)</summary>
    public List<Reveal> Reveals = new();

    // ------------------------------------------------------------------ the step (World.Step)
    /// <summary>the computer players' turn in the step (main.js: G.brains update) - src/Sim/Ai</summary>
    partial void AiUpdate(double dt);
    /// <summary>the campaign's turn in the step: world events to the trigger conditions, then the trigger engine
    /// (main.js: G.campaign.update) - src/Sim/Campaign</summary>
    partial void CampaignUpdate(double dt);
    /// <summary>every event passes the game's statistics (game/stats.js on)</summary>
    partial void StatsOn(WorldEvent e);
    /// <summary>game/stats.js update (once a step)</summary>
    partial void StatsUpdate();

    // ------------------------------------------------------------------ effects.js
    /// <summary>auras and timed effects (auraUpdate + periodic every AURA_TICK)</summary>
    partial void EffectsTick(double dt);
    /// <summary>a camouflage layer on / off (moves.js setCamo)</summary>
    partial void SetCamo(Unit u, string layer, bool on);

    // ------------------------------------------------------------------ economy.js
    /// <summary>a resource node is used up / removed</summary>
    partial void DepleteNode(ResNode n);
    /// <summary>storage caps and unit limit of a player from its buildings</summary>
    partial void RecomputeCaps(Player? p);
    partial void StartGather(Unit u, Entity? target);
    partial void GatherUpdate(Unit u, double dt);
    partial void StartTrade(Unit u, Entity? target);
    partial void TradeUpdate(Unit u, double dt);

    // ------------------------------------------------------------------ construction.js
    partial void StartBuild(Unit u, Entity? target);
    partial void StartRepair(Unit u, Entity? target);
    partial void BuildUpdate(Unit u, double dt);
    partial void ConstructionUpdate(Building b, double dt);

    // ------------------------------------------------------------------ production.js
    /// <summary>a producer's queue (buildings, the Aje collector)</summary>
    partial void ProductionUpdate(Entity e, double dt);
    /// <summary>a new worker at its rally point goes to work</summary>
    partial void RallyJob(Unit u, double x, double z);

    // ------------------------------------------------------------------ combat.js
    partial void PoisonUpdate(Unit u);
    /// <summary>a shot after a delay (u.pending)</summary>
    partial void Fire(Unit u, Rules.Weapon? w, Entity? target);
    partial void AttackUpdate(Unit u, double dt);
    partial void Engage(Unit u, Entity e, EngageOpts opts);
    partial void TowerUpdate(Building b, double dt);
    partial void ProjectileUpdate(Projectile p, double dt);

    // ------------------------------------------------------------------ healing.js
    partial void HealUpdate(Unit u, double dt);
    partial void SupportUpdate(Unit u, double dt);
    partial void StopHealing(Unit u);

    // ------------------------------------------------------------------ moves.js
    partial void SpecialUpdate(Unit u, double dt);
    partial void CastUpdate(Unit u, double dt);
    partial void CastCancel(Unit u);
    /// <summary>an entrenched unit digs out (an order ends it)</summary>
    partial void DigOut(Unit u);

    // ------------------------------------------------------------------ animals.js
    partial void RoamUpdate(Unit u, double dt);
    partial void FleeUpdate(Unit u, double dt);
    partial void FatigueUpdate(Unit u, double dt);
    partial void WildNestsUpdate(double dt);

    // ------------------------------------------------------------------ transport.js
    partial void LeaveTransport(Unit u);
    partial void UnloadAll(Unit u, bool here);
    partial void BoardUpdate(Unit u, double dt);
    partial void PassengerUpdate(Unit u, double dt);

    // ------------------------------------------------------------------ naval.js
    partial void HarbourUpdate(Building b, double dt);

    // ------------------------------------------------------------------ siege.js
    partial void Undock(Unit u, Order o);
    partial void DockOrder(Unit u, Entity? target, bool user);
    partial void DockUpdate(Unit u, double dt);
    partial void ClimbSiege(Unit u, Entity? target, bool user);
    /// <summary>a docked siege vehicle leaves its wall (DropStates)</summary>
    partial void SiegeGone(Unit u);

    // ------------------------------------------------------------------ wallwalk.js
    partial void UseWall(Unit u, Entity? target, bool user);
    partial void WallUpdate(Unit u, double dt);
    partial void ClimbUpdate(Unit u, double dt);
    partial void WallFall(Unit u, bool hurt);
    partial void SetOnWall(Unit u, bool on);

    // ------------------------------------------------------------------ landmarks.js
    partial void FountainUpdate(Unit u, double dt);
    partial void WellsUpdate(double dt);
    partial void LakesUpdate(double dt);
    partial void WeatherUpdate();

    // ------------------------------------------------------------------ items.js
    partial void PickupUpdate(Unit u, double dt);

    // ------------------------------------------------------------------ finishing.js
    partial void FinishUpdate(Unit u, double dt);
    /// <summary>the victim of a finishing move (dead)</summary>
    partial void FinishedUpdate(Unit u);

    // ------------------------------------------------------------------ gameover.js
    partial void GameOverUpdate(double dt);

    // ------------------------------------------------------------------ scripting.js
    /// <summary>an order to attack a neutral player's object makes both players hostile</summary>
    partial void DeclareWar(Player a, Player b);
}
