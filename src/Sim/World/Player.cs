// A player: resources, storage caps, population, the army pyramid and its own tech tree.
//
// Ported from remake/src/game/player.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using PwRemake.Sim.Rules;

namespace PwRemake.Sim.World;

/// <summary>Options of a new player (player.js constructor opts).</summary>
public sealed class PlayerOpts
{
    public string? Name;
    public bool Ai;
    /// <summary>minimap / UI colour (0xRRGGBB)</summary>
    public int Color = 0x3080ff;
    /// <summary>model tint (0xRRGGBB) or null = untinted</summary>
    public int? PartyColor;
    /// <summary>the palette index (Colors.PLAYER_COLORS: 0..7 PlayerColor0..7 of the original, 8..15 the remake.s); -1 none or
    /// a custom colour (the view then draws Color / PartyColor)</summary>
    public int ColorSlot = -1;
    public int? Team;
    /// <summary>diplomacy table: this player's relation towards player j (0 hostile, 1 neutral, 2 friendly)</summary>
    public int[]? Diplomacy;
    public Rules.Rules? Rules;
    /// <summary>the game options that are tech tree filters (MIRAGE: epoch six, no titans); null = the defaults</summary>
    public GameplayOptions? Gameplay;
}

/// <summary>Who plays a player: the local human, a co-op partner, a computer player (docs/ROADMAP.md).</summary>
public enum ControlledBy { Human, Computer, Remote, Nobody }

/// <summary>Handicaps of a computer player by its difficulty 0-9 (game/ai/brain.js setDifficulty; docs/spec/ai.md §3).</summary>
public sealed class AiMods
{
    public double Gather = 1, BuildTime = 1, ResearchTime = 1, WeaponTime = 1, Attack = 1, Defense = 1;
    /// <summary>+slots per level, or null</summary>
    public int[]? Pyramid;
    public double UnitLimit;
}

public sealed partial class Player : IRulesOwner
{
    public readonly int Id;
    public string Tribe { get; set; }
    public string Name;
    public bool Ai;
    /// <summary>minimap / UI colour</summary>
    public int Color;
    /// <summary>model tint (0xRRGGBB) or null = untinted</summary>
    public int? PartyColor;
    public int ColorSlot;
    /// <summary>skirmish diplomacy: players of one team are friends</summary>
    public int Team;
    /// <summary>
    /// Diplomacy as the original keeps it (CDiplomacySrvMgr): Rel[j] = this player's relation TOWARDS player j,
    /// 0 hostile, 1 neutral, 2 friendly; one direction per pair (campaign maps: PlayerSettings/Player_&lt;i&gt;/Diplomacy,
    /// ServerApp.usl:539-551). Without a table (skirmish) the teams decide; a slot the table does not cover is neutral.
    /// (null entries: not set)
    /// </summary>
    public List<int?>? Rel;
    /// <summary>the table was made by SetRelation in a skirmish: the other pairs keep their team relation</summary>
    public bool RelTeams;
    public bool Human;
    public ControlledBy ControlledBy;
    /// <summary>a lockstep game (docs/MULTIPLAYER.md): the seats (GameStart.Seats indices) of the humans who give this
    /// player's orders - two in a co-op game's shared player; empty in a single player game</summary>
    public readonly List<int> Seats = new();
    /// <summary>food, wood, stone, skulls, power (the sequel's electricity)</summary>
    public readonly Dictionary<string, double> Res = new(StringComparer.Ordinal) { ["food"] = 0, ["wood"] = 0, ["stone"] = 0, ["skulls"] = 0, ["power"] = 0 };
    /// <summary>rescap_* default 300 (player_attrib_def.txt)</summary>
    public readonly Dictionary<string, double> Caps = new(StringComparer.Ordinal) { ["food"] = 300, ["wood"] = 300, ["stone"] = 300 };
    public double MaxUnits;
    public int Units;
    public readonly int[] AtLevel = new int[5];
    public readonly int[] QueuedAtLevel = new int[5];
    public int QueuedUnits;
    /// <summary>completed upgrade/invention action ids (UI bookkeeping)</summary>
    public readonly OrderedSet<string> Techs = new(StringComparer.Ordinal);
    /// <summary>this player's tech tree (Rules.cs / TechTree.cs)</summary>
    public TechTree? Tt { get; set; }
    /// <summary>income: [time, res, amount]</summary>
    public readonly List<(double T, string Res, double Amount)> Log = new();
    public int Kills, Lost;
    public bool Defeated, Won;
    /// <summary>PopulationMax (Player.usl: map "Population/Max", default 52)</summary>
    public double PopMax = 52;
    /// <summary>unit slots per level [l1..l5] if the map restricts them (Restrictions/Chars/Level&lt;n&gt;/Max), else the standard 25/15/8/3/1</summary>
    public double[]? Pyramid;
    /// <summary>storage limits set by a mission (PLCP): buildings no longer change them (economy.js recomputeCaps)</summary>
    public bool CapsFixed;
    /// <summary>SNFA: bit 1 = nest animals leave this player alone, bit 2 = its units don't attack wild animals on their own</summary>
    public int AnimalsNeutral;
    /// <summary>unique hero classes this player owns (NPCMgr)</summary>
    public readonly OrderedSet<string> Heroes = new(StringComparer.Ordinal);
    public AiMods? AiMods;
    /// <summary>collected and stored (game/stats.js)</summary>
    public readonly Dictionary<string, double> Gained = new(StringComparer.Ordinal) { ["food"] = 0, ["wood"] = 0, ["stone"] = 0, ["skulls"] = 0 };
    /// <summary>debug mode (skirmish option): everything is free and builds instantly</summary>
    public bool Debug { get; set; }
    /// <summary>debug option "highest epoch" (game/cheats.js), 0 = none</summary>
    public int MaxEpoch { get; set; }
    /// <summary>buildings of another tribe offer their own tribe's actions (campaign/tribes.js)</summary>
    public bool ForeignActions { get; set; }
    /// <summary>its bought army cost more than its points (game/pointbuy.js place, SL:661-688): it got one worker instead</summary>
    public bool Cheated;
    /// <summary>a skirmish: the slot of the setup it plays (game/skirmish.js; null in a campaign)</summary>
    public Game.SkirmishSlot? Slot;
    /// <summary>the game options its tree was made with (Rules.PlayerTree; a new tree after a tribe change keeps them)</summary>
    public GameplayOptions? Gameplay;

    public Player(int id, string tribe, PlayerOpts? opts = null)
    {
        opts ??= new PlayerOpts();
        Id = id;
        Tribe = tribe;
        Name = opts.Name ?? tribe;
        Ai = opts.Ai;
        Color = opts.Color;
        PartyColor = opts.PartyColor;
        ColorSlot = opts.ColorSlot;
        Team = opts.Team ?? id;
        Rel = opts.Diplomacy != null ? new List<int?>(Array.ConvertAll(opts.Diplomacy, x => (int?)x)) : null;
        Human = !opts.Ai;
        ControlledBy = opts.Ai ? ControlledBy.Computer : ControlledBy.Human;
        Gameplay = opts.Gameplay;
        Tt = opts.Rules?.PlayerTree(tribe, Gameplay);
    }

    public double this[string res] { get => Res.TryGetValue(res, out var v) ? v : 0; set => Res[res] = value; }

    /// <summary>relation towards p: 0 hostile, 1 neutral, 2 friendly</summary>
    public int Relation(Player? p)
    {
        if (p == null) return 1;
        if (p == this) return 2;
        if (Rel == null) return p.Team == Team ? 2 : 0;
        int? r = p.Id < Rel.Count ? Rel[p.Id] : null;
        return r ?? (RelTeams ? (p.Team == Team ? 2 : 0) : 1);
    }

    public void SetRelation(Player? p, int rel)
    {
        if (p == null || p == this) return;
        if (Rel == null) { Rel = new List<int?>(); RelTeams = true; }      // skirmish: the other pairs keep their team relation
        while (Rel.Count <= p.Id) Rel.Add(null);
        Rel[p.Id] = rel;
    }

    public bool IsFriend(Player? p) => p != null && Relation(p) == 2;
    public bool IsEnemy(Player? p) => p != null && Relation(p) == 0;
    public bool IsNeutral(Player? p) => p != null && Relation(p) == 1;

    public int Epoch()
    {
        int e = 1;
        for (int i = 2; i <= 5; i++) if (Tt != null ? Tt.Invented(AGES[i]) : Techs.Has(AGES[i])) e = i;
        return e;
    }
    static readonly string[] AGES = { "", "", "age_2", "age_3", "age_4", "age_5" };

    // debug mode (skirmish option, or ?debug): everything is free and builds instantly (systems/production.js,
    // systems/construction.js check `debug` for the times). Nothing is paid, so nothing is refunded either.
    public bool CanAfford(Cost cost)
    {
        if (Debug) return true;
        foreach (var r in Rules.Rules.COSTS) if (this[r] < cost[r]) return false;
        return true;
    }

    public void Pay(Cost cost)
    {
        if (Debug) return;
        foreach (var r in Rules.Rules.COSTS) Res[r] = this[r] - cost[r];
    }

    /// <summary>give resources back (cancelled production); storage caps apply to food/wood/stone (not to skulls and power)</summary>
    public void Refund(Cost cost, double frac = 1)
    {
        if (Debug) return;
        foreach (var r in Rules.Rules.COSTS)
        {
            double v = Math.Floor(cost[r] * frac);
            if (v == 0) continue;
            Res[r] = r == "skulls" || r == "power" ? this[r] + v : Math.Max(this[r], Math.Min(Caps[r], this[r] + v));
        }
    }

    /// <summary>
    /// CPlayer.AddResource (Player.usl:329-353): add resources up to the storage cap - what fits is rounded to whole
    /// units (2.5 stone of a trade cart -> 3); returns the amount that didn't fit
    /// </summary>
    public double Deliver(string r, double amount, double time)
    {
        if (r == "skulls" || r == "power") { Res[r] = this[r] + amount; if (r == "power") Log.Add((time, r, amount)); return 0; }
        double cur = this[r], cap = Caps[r];
        if (cur >= cap) return amount;
        if (cur + amount <= cap)
        {
            double v = Math.Floor(cur + amount + 0.5);
            if (v > cur) Log.Add((time, r, v - cur));
            Res[r] = v;
            return 0;
        }
        Res[r] = cap;
        Log.Add((time, r, cap - cur));
        return amount - (cap - cur);
    }

    public Dictionary<string, double> IncomePerMin(double time)
    {
        while (Log.Count > 0 && Log[0].T < time - 60) Log.RemoveAt(0);
        var res = new Dictionary<string, double>(StringComparer.Ordinal) { ["food"] = 0, ["wood"] = 0, ["stone"] = 0, ["power"] = 0 };
        foreach (var (_, r, a) in Log) res[r] = (res.TryGetValue(r, out var x) ? x : 0) + a;
        double span = Math.Min(60, Math.Max(10, time));
        foreach (var r in new[] { "food", "wood", "stone", "power" }) res[r] = Math.Floor(res[r] * 60 / span + 0.5);
        return res;
    }

    /// <summary>pyramid / population check for one more unit at level lv (1..5): null, "housing" or "level"</summary>
    public string? SlotFree(int lv, double[] pyramid)
    {
        if (Units + QueuedUnits >= Math.Min(MaxUnits, PopMax)) return "housing";
        if (AtLevel[lv - 1] + QueuedAtLevel[lv - 1] >= (Pyramid ?? pyramid)[lv - 1]) return "level";
        return null;
    }

    public override string ToString() => $"player {Id} {Name} ({Tribe})";
}
