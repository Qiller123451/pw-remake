// The computer player as the campaign sees it: TribeAI is the campaign's ICampaignBrain (the AI trigger actions AIBV,
// AIFT, AIDA, AILU, AIRG, AICM and GAOV reach it through MissionHost -> World.Brains), and CampaignBrains is the
// IBrainFactory a MissionSession makes its brains with (main.js createBrains for a campaign: WorldBuilder.CreateBrains
// decides which slots get one, with which difficulty, personality, map options and the Nightmare flag).
//
// The brains are also kept by the world's AiPlayers (World.Ai): its Update in the AI phase of every World.Step is the
// brains' step (main.js: G.brains update), the debug option "passive computer" (W.Cheats.Ai) is read by the attack
// module, and AiPlayers is what a skirmish uses directly (AiPlayers.ForSkirmish, SkirmishSession).
//
// Ported from remake/src/main.js createBrains and remake/src/game/campaign/actions.js AIBV ... AICM (pw-exporter 98c7703).
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Ai;

/// <summary>Makes TribeAI brains for a campaign mission (MissionSession's default IBrainFactory).</summary>
public sealed class CampaignBrains : IBrainFactory
{
    public readonly AiData Data;
    /// <summary>the orders of the brains (null: CommandHands - the brains give commands like a player)</summary>
    public IAiHands? Hands;
    public CampaignBrains(AiData data) { Data = data; }

    public ICampaignBrain? Create(W_ world, Player p, BrainOptions o, PwRemake.Sim.Campaign.Campaign campaign)
    {
        // one AiPlayers per world: its Update is the brains' step (World.AiUpdate)
        var A = world.Ai;
        if (A == null)
        {
            A = new AiPlayers(world, Data) { Human = campaign.Human?.Id ?? 0 };
            A.Resolve = id => world.ById(id);
            A.MapToGame = m => { var (x, z) = campaign.ToGame(m.X, m.Y); return new Pt(x, z); };
            A.ResolveRegion = name => campaign.Regions.Find(name) is { } r ? new AiRegionFn(r.Contains) : null;
            A.Attach();
        }
        var b = new TribeAI(world, p, Data, new TribeAIOptions
        {
            Difficulty = o.Difficulty, Behaviour = string.IsNullOrEmpty(o.Behaviour) ? "Mikrobe" : o.Behaviour, MapOptions = AiMapOptions.From(o.MapOptions),
            Multimap = o.Multimap, LevelName = o.LevelName, ExtraSquads = o.ExtraSquads,
        }, Hands);
        A.Add(b);
        return b;
    }
}

/// <summary>TribeAI as the campaign's brain (campaign/actions.js: brain.setBehaviour, startAttack, startAutoAttack,
/// setDefenceArea, lockUnits, setRegionMap, callModule; engine.js GAOV)</summary>
public sealed partial class TribeAI : ICampaignBrain
{
    void ICampaignBrain.Update(double dt) => Update(dt);
    void ICampaignBrain.SetBehaviour(string behaviour, string module) => SetBehaviour(behaviour, string.IsNullOrEmpty(module) ? "CTRL" : module);
    bool ICampaignBrain.StartAttack(BrainAttack o)
    {
        StartAttack(new AttackRequest
        {
            Type = o.Type, Targets = o.Targets.ToList(),
            Position = o.Position is { } p ? new Pt(p.X, p.Z) : null, SpawnPosition = o.SpawnPosition is { } sp ? new Pt(sp.X, sp.Z) : null,
            AttackOnTheWay = o.AttackOnTheWay, Spawn = o.Spawn, IgnoreLocations = o.IgnoreLocations, TargetOnly = o.TargetOnly, Ship = o.Ship, ShipLand = o.ShipLand,
            Behaviour = o.Behaviour,
        });
        return true;                                   // (the JS ignores startAttack's answer)
    }
    void ICampaignBrain.StartAutoAttack(IReadOnlyList<Entity> targets) => StartAutoAttack(targets.ToList());
    void ICampaignBrain.SetDefenceArea(string id, double x, double z, double radius, int maxUnits) => SetDefenceArea(id ?? "", new Pt(x, z), radius, maxUnits);
    void ICampaignBrain.LockUnits(IReadOnlyList<Unit> units, bool locked) => LockUnits(units, locked);
    void ICampaignBrain.SetRegionMap(string map, PwRemake.Sim.Campaign.Region? region, double value, bool add) =>
        SetRegionMap(map, region == null ? null : new AiRegionFn(region.Contains), value, add);
    void ICampaignBrain.CallModule(string module, string command) => CallModule(module, command);
    void ICampaignBrain.GameOver(bool lost) => OnGameOver();
}
