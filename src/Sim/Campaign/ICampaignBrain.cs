// The computer players of a mission, as the campaign sees them: what the AI triggers (AIBV, AIFT, AIDA, AILU, AIRG,
// AICM, GAOV) tell a player's brain, and how the brains are made (main.js createBrains). The computer player of
// src/Sim/Ai implements both: TribeAI is an ICampaignBrain, Ai.CampaignBrains the IBrainFactory a MissionSession uses
// unless SessionOptions.Brains gives another one (SessionOptions.NoBrains: none - the AI actions then warn "no brain",
// as the JS did for a slot without one).
//
// The brains live in World.Brains (player id -> brain, the JS G.brains); the computer player's step (World.AiUpdate)
// updates them (TribeAI brains through World.Ai, AiPlayers).
using System.Collections.Generic;
using System.Text.Json.Nodes;
using PwRemake.Sim.World;
using GameWorld = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Campaign;

/// <summary>An attack order of AIFT with custom_attack (ai.md §13.1; brain.startAttack). Game coordinates.</summary>
public sealed class BrainAttack
{
    public string Type = "";
    public List<Entity> Targets = new();
    public (double X, double Z)? Position, SpawnPosition;
    public bool AttackOnTheWay, Spawn, IgnoreLocations, TargetOnly, Ship, ShipLand;
    public string? Behaviour;
}

/// <summary>How a brain is made (main.js createBrains for a campaign; the skirmish's options too).</summary>
public sealed class BrainOptions
{
    /// <summary>0-9: the map's AI_Difficulty_Easy|Medium|Hard for the campaign difficulty (CampaignMgr.usl:132-139)</summary>
    public int Difficulty = 4;
    /// <summary>the personality it starts with: Mikrobe (asleep until AIBV), Turtle, Dodo, Giraffe ...</summary>
    public string Behaviour = "Mikrobe";
    /// <summary>map_info.ai_options: walls, markplace_outpost, harbour, warpgate, hunt_animals, watermap</summary>
    public JsonNode? MapOptions;
    public bool Multimap;
    /// <summary>"Single 01" ...</summary>
    public string LevelName = "";
    /// <summary>Nightmare: the remake's bigger raids (ai/attack.js extraSquads)</summary>
    public bool ExtraSquads;
}

/// <summary>A computer player's brain as the triggers address it (game/ai.js TribeAI's public methods).</summary>
public interface ICampaignBrain
{
    /// <summary>one simulation step of the brain</summary>
    void Update(double dt);
    /// <summary>AIBV: the personality (Mikrobe asleep, Turtle, Dodo, Giraffe, FightOnly, Schnecke) of a module (CTRL)</summary>
    void SetBehaviour(string behaviour, string module);
    /// <summary>AIFT custom attack; false = it cannot</summary>
    bool StartAttack(BrainAttack order);
    /// <summary>AIFT without custom attack: attack the targets with what it has</summary>
    void StartAutoAttack(IReadOnlyList<Entity> targets);
    /// <summary>AIDA: a defence area (max 0 removes it)</summary>
    void SetDefenceArea(string id, double x, double z, double radius, int maxUnits);
    /// <summary>AILU: units the brain must not use (locked) or gets back</summary>
    void LockUnits(IReadOnlyList<Unit> units, bool locked);
    /// <summary>AIRG: a weight in an influence map for a region (null = none of that name)</summary>
    void SetRegionMap(string map, Region? region, double value, bool add);
    /// <summary>AICM</summary>
    void CallModule(string module, string command);
    /// <summary>GAOV: the game is over</summary>
    void GameOver(bool lost);
}

public interface IBrainFactory
{
    /// <summary>a brain for computer player p of the mission, or null</summary>
    ICampaignBrain? Create(GameWorld world, Player p, BrainOptions options, Campaign campaign);
}
