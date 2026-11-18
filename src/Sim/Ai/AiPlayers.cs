// The computer players of a game: one TribeAI per computer slot, created from the mission's or the skirmish's
// settings (main.js createBrains), updated in the AI phase of every World.Step (World.Ai.cs), and the entry points of
// the campaign's AI trigger actions (IMissionHost: AiBehavior, AiAttack, AiAutoAttack, AiDefendArea, AiLockUnits,
// SetAggression, AiRegionMap, AiModuleCommand, AiGameOver - campaign/actions.js AIBV ... AICM, engine.js GAOV).
//
// Ported from remake/src/game/ai.js and main.js createBrains (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;
using W_ = PwRemake.Sim.World.World;

namespace PwRemake.Sim.Ai;

/// <summary>a computer slot of a skirmish (ServerApp.usl:454-488): its level 0-9 and personality (null: random)</summary>
public sealed record SkirmishAiSlot(Player Player, double Level, string? Behaviour = null);

/// <summary>a computer slot of a campaign map (pw-campaign/1 players[i]): control "ai_Mikrobe" ..., AI_Difficulty_Easy /
/// Medium / Hard</summary>
public sealed record CampaignAiSlot(Player Player, string Control, double? Easy, double? Medium, double? Hard);

public sealed class AiPlayers
{
    static readonly Logger L = Log.For("Ai");
    public readonly W_ W;
    public readonly AiData Data;
    /// <summary>the brains by player id (= slot)</summary>
    public readonly OrderedMap<int, TribeAI> Brains = new();
    /// <summary>debug option "passive computer" (game/cheats.js ai: 'passive'): no attack of the computer players' own on
    /// the human player (a mission's scripted attacks still come)</summary>
    public bool Passive;
    /// <summary>the human player's id (W.cheats.human)</summary>
    public int Human;
    /// <summary>an attack of a computer player set out (the interface's warning: main.js G.onAiAttack)</summary>
    public Action<TribeAI, Attack>? OnAiAttack;
    /// <summary>the host's object ids -> entities (the campaign's registry; default: World.ById)</summary>
    public Func<int, Entity?> Resolve;
    /// <summary>a region of the map by name or GUID for AIRG (the campaign's regions; null: unknown = the whole map)</summary>
    public Func<string, IAiRegion?>? ResolveRegion;
    /// <summary>map coordinates (x east, y south) -> game coordinates (default: x - size/2, z = y - size/2)</summary>
    public Func<MapPos, Pt> MapToGame;

    public AiPlayers(W_ w, AiData data)
    {
        W = w; Data = data;
        Resolve = id => w.ById(id);
        MapToGame = m => new Pt(m.X - w.Map.Size / 2, m.Y - w.Map.Size / 2);
    }

    /// <summary>World.Ai = this (its Update runs in the AI phase of every step)</summary>
    public AiPlayers Attach() { W.Ai = this; return this; }

    public TribeAI Add(TribeAI b) { b.Owner = this; Brains.Set(b.P.Id, b); return b; }
    public TribeAI? Get(int slot) => Brains.Get(slot);

    /// <summary>every brain, once per step (main.js: G.brains.values() update); a failing brain logs and goes on</summary>
    public void Update(double dt)
    {
        foreach (var b in Brains.Values)
        {
            long t0 = W.Profile != null ? System.Diagnostics.Stopwatch.GetTimestamp() : 0, b0 = W.Profile != null ? GC.GetAllocatedBytesForCurrentThread() : 0;
            try { b.Update(dt); }
            catch (Exception e) { if (L.IsOn(LogLevel.Error) && Log.FirstTime("ai|" + b.P.Id + "|" + e.Message)) L.Error($"computer player {b.P.Id} failed", e); }
            if (W.Profile != null) W.Profile.Add(StepProfile.Key("ai:", b.P.Id + " " + b.P.Tribe), System.Diagnostics.Stopwatch.GetTimestamp() - t0, GC.GetAllocatedBytesForCurrentThread() - b0);
        }
    }

    // ------------------------------------------------------------------ creating the brains (main.js createBrains)
    /// <summary>
    /// A campaign map: every AI slot sleeps ('ai_Mikrobe') until a trigger wakes it (AIBV); its difficulty is the map's
    /// AI_Difficulty_Easy|Medium|Hard for the chosen campaign difficulty (CampaignMgr.usl:132-139), else 1 / 4 / 8.
    /// difficulty: 0 easy, 1 medium, 2 hard, 3 Nightmare (the map as hard, the raids with the remake's bigger squads:
    /// extraSquads); missionId: the "Single NN" level name of the attack plans.
    /// </summary>
    public static AiPlayers ForCampaign(W_ w, AiData data, IEnumerable<CampaignAiSlot> slots, int difficulty, int missionId, AiMapOptions? mapOptions = null, IAiHands? hands = null)
    {
        var A = new AiPlayers(w, data);
        bool nightmare = difficulty == 3;
        int cd = nightmare ? 2 : difficulty;
        string levelName = "Single " + missionId.ToString("00");
        foreach (var s in slots)
        {
            if (!Regex.IsMatch(s.Control ?? "", "^ai_")) continue;
            double? v = cd == 0 ? s.Easy : cd == 1 ? s.Medium : cd == 2 ? s.Hard : s.Medium;
            double diff = v ?? (cd >= 0 && cd <= 2 ? new[] { 1, 4, 8 }[cd] : 4);
            string beh = s.Control!.Length > 3 ? s.Control[3..] : "Mikrobe";
            A.Add(new TribeAI(w, s.Player, data, new TribeAIOptions { Difficulty = diff, Behaviour = beh, MapOptions = mapOptions ?? new AiMapOptions(), Multimap = false, LevelName = levelName, ExtraSquads = nightmare }, hands));
        }
        return A;
    }
    /// <summary>a campaign map from its pw-campaign/1 JSON (players[i].control / ai_difficulty, map.ai_options); players
    /// by slot: the world's player with that id</summary>
    public static AiPlayers ForCampaign(W_ w, AiData data, JsonNode mission, int difficulty, int missionId, int humanSlot = 0, IAiHands? hands = null)
    {
        var slots = new List<CampaignAiSlot>();
        if (mission["players"] is JsonArray pl)
            foreach (var p in pl)
            {
                if (p == null || !p["present"].Bool(true)) continue;
                int id = p["id"].Int(-1);
                var player = w.Players.FirstOrDefault(x => x.Id == id);
                if (player == null || id == humanSlot) continue;
                var ad = p["ai_difficulty"];
                double? D(string k) => ad?[k] is JsonValue jv ? jv.Num() : null;
                slots.Add(new CampaignAiSlot(player, p["control"].Str(), D("easy"), D("medium"), D("hard")));
            }
        var A = ForCampaign(w, data, slots, difficulty, missionId, AiMapOptions.From(mission["map"]?["ai_options"]), hands);
        A.Human = humanSlot;
        return A;
    }
    /// <summary>A skirmish: one brain per computer slot, its level and personality (ServerApp.usl:454-488) on a
    /// multiplayer map (the map's Root/AIOptions over the defaults: harbours where there is water).</summary>
    public static AiPlayers ForSkirmish(W_ w, AiData data, IEnumerable<SkirmishAiSlot> slots, AiMapOptions? mapAiOptions = null, int human = 0, IAiHands? hands = null)
    {
        var A = new AiPlayers(w, data) { Human = human };
        foreach (var s in slots)
        {
            var mo = mapAiOptions?.Clone() ?? new AiMapOptions { Harbour = w.WaterNav != null, Watermap = w.WaterNav != null, Walls = false, MarkplaceOutpost = false, HuntAnimals = true };
            mo.Warpgate = w.Opts.Warpgate;
            string beh = s.Behaviour ?? new[] { "Dodo", "Giraffe", "Schnecke" }[w.Rng.Int(3)];
            // (a skirmish map is no custom map: MIRAGE's CustomMapSettings.txt lists the campaign's levels only)
            A.Add(new TribeAI(w, s.Player, data, new TribeAIOptions { Difficulty = s.Level, Behaviour = beh, Multimap = true, Custommap = false, MapOptions = mo }, hands));
        }
        return A;
    }

    // ------------------------------------------------------------------ the campaign's AI actions (IMissionHost)
    /// <summary>AIBV / AIST: false if the player has no brain (or the behaviour / command is unknown)</summary>
    public bool AiBehavior(int slot, string behavior, string module)
    {
        var b = Get(slot);
        if (b == null) return false;
        b.SetBehaviour(behavior, string.IsNullOrEmpty(module) ? "CTRL" : module);
        return true;
    }
    /// <summary>AIFT custom_attack = 1: false if the player has no brain</summary>
    public bool AiAttack(int slot, AiAttackOrder o)
    {
        var b = Get(slot);
        if (b == null) return false;
        b.StartAttack(new TribeAI.AttackRequest
        {
            Type = o.Type, Targets = o.Targets.Select(Resolve).Where(e => e != null).Select(e => e!).ToList(),
            Position = o.Position is { } p ? MapToGame(p) : null, SpawnPosition = o.SpawnPosition is { } sp ? MapToGame(sp) : null,
            AttackOnTheWay = o.AttackOnTheWay, Spawn = o.Spawn, IgnoreLocations = o.IgnoreLocations, TargetOnly = o.TargetOnly, Ship = o.Ship, ShipLand = o.ShipLand,
            Behaviour = string.IsNullOrEmpty(o.Behaviour) ? null : o.Behaviour,
        });
        return true;
    }
    /// <summary>AIFT custom_attack = 0</summary>
    public void AiAutoAttack(int slot, IReadOnlyList<int> targets) => Get(slot)?.StartAutoAttack(targets.Select(Resolve).Where(e => e != null).Select(e => e!).ToList());
    /// <summary>AIDA: max &lt;= 0 removes the area</summary>
    public void AiDefendArea(int slot, string id, MapPos pos, double radius, int maxUnits) => Get(slot)?.SetDefenceArea(id ?? "", MapToGame(pos), radius, maxUnits);
    /// <summary>AILU</summary>
    public void AiLockUnits(int slot, IReadOnlyList<int> units, bool locked) => Get(slot)?.LockUnits(units.Select(Resolve).OfType<Unit>().ToList(), locked);
    /// <summary>AIAM: /AggroState_n (0 stand ground, 1 defensive, 2 aggressive; -1 passive) - world.setAggro</summary>
    public void SetAggression(IReadOnlyList<int> units, int state) => W.SetAggro(units.Select(Resolve).OfType<Unit>().ToList(), state);
    /// <summary>AIRG: slot -1 = every computer player; the region by name (ResolveRegion; unknown: the whole map)</summary>
    public void AiRegionMap(int slot, string map, string region, double value, bool add)
    {
        var r = ResolveRegion?.Invoke(region);
        if (slot < 0) { foreach (var b in Brains.Values) b.SetRegionMap(map, r, value, add); }
        else Get(slot)?.SetRegionMap(map, r, value, add);
    }
    /// <summary>AICM</summary>
    public void AiModuleCommand(int slot, string module, string command) => Get(slot)?.CallModule(module, command);
    /// <summary>GAOV: the computer players are told that the game is over</summary>
    public void AiGameOver() { foreach (var b in Brains.Values) b.OnGameOver(); }
    /// <summary>a request of a player to a computer player ("AI&lt;n&gt;: help", the diplomacy window's Send / Request)</summary>
    public string? PlayerRequest(int slot, Player from, string word) => Get(slot)?.OnPlayerRequest(from, word);
}
