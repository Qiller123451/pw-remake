// The army screen of a campaign mission with an army budget (GameInfoWindow in GIWT_SINGLEPLAYER, GIW:343-397): the
// level's army preset of the human player's tribe, fixed and paid from the budget max(Credits, MinCredits) - Credits =
// the bonus points of the mission before (the settings' campaign credits, written when a mission is won) - the shop
// limited to the units the level allows, its resources (a "-1" one: the tribe's start stock, locked - the original
// gives 0 [?engine]). The menu (game/src/UI/ArmyScreen) shows it; the army goes into GameStart.Army.
//
// Ported from remake/src/ui/menu.js campaignArmy (pw-exporter 98c7703), without the DOM.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;

namespace PwRemake.Sim.Campaign;

public sealed class CampaignArmy
{
    public string Tribe = "";
    /// <summary>the budget (the points the gauge names) and the points to spend (with the Aje collector's price)</summary>
    public double Credits, Total;
    /// <summary>the classes the shop offers (null: all of the tribe)</summary>
    public HashSet<string>? Allowed;
    /// <summary>the most cards per level (null: the pyramid's places)</summary>
    public int?[] Limits = new int?[5];
    public bool Buildings = true;
    /// <summary>the level's preset as an army (the Default button)</summary>
    public Func<Army> Defaults = () => new Army();

    /// <summary>the pyramid's blocked places per level (the highest ids of a level first)</summary>
    public int[] Blocked => Enumerable.Range(0, 5).Select(i => Limits[i] is int m ? Math.Max(0, PointBuy.SLOT_LEVELS[i][1] - PointBuy.SLOT_LEVELS[i][0] + 1 - m) : 0).ToArray();

    static readonly Regex GENERIC = new("^(worker|Resource|Technics|Nature|Blocked)$", RegexOptions.CultureInvariant);

    /// <summary>the army screen of a mission (mission.json: its players), played with `tribe` ("" = the mission's);
    /// credits: the bonus points the mission before earned (0: none). Null: the mission has no human player or the game
    /// data no price lists - the mission starts with its preset army.</summary>
    public static CampaignArmy? Of(JsonNode? mission, Rules.Rules D, string tribe, double credits)
    {
        var PB = new PointBuy(D, null);
        var hp = (mission?["players"] as JsonArray)?.FirstOrDefault(p => p?["type"].Str() == "human");
        if (hp == null || !PB.Ready) return null;
        string mt = hp["tribe"].Str();
        tribe = string.IsNullOrEmpty(tribe) ? mt : tribe;
        // another tribe than the mission's: its equivalents of the preset's and the shop's classes (campaign/tribes.js)
        var swap = tribe != mt ? new TribeSwap(D, mt, tribe) : null;
        string? Conv(string c)
        {
            if (swap == null || GENERIC.IsMatch(c)) return c;
            var (tribal, e) = swap.EquivalentOf(c);
            return tribal ? e : c;
        }
        var pb = hp["point_buy"];
        var src = (pb?["tribes"]?[tribe] ?? pb?["tribes"]?[mt]) as JsonObject;
        var units = new List<KeyValuePair<string, string>>();
        foreach (var (slot, c) in PbPreset.UnitsOf(src)) { var x = Conv(c); if (!string.IsNullOrEmpty(x)) units.Add(new(slot, x)); }
        var au = hp["allowed_units"];
        string own = au?["infantry"]?[tribe] != null ? tribe : mt;
        var allowed = new HashSet<string>();
        foreach (var l in new[] { au?["infantry"]?[own], au?["cavalry"]?[own], hp["heroes"]?["_by_tribe"]?[own] })
            if (l is JsonArray a) foreach (var c in a) { var x = Conv(c.Str()); if (!string.IsNullOrEmpty(x)) allowed.Add(x); }
        var res = hp["resources"];
        var def = D.StartOf(tribe)?.Res;
        var lockedRes = new Dictionary<string, double>();
        foreach (var r in PointBuy.RES) if (res?[r] is JsonValue v && v.Num() < 0) lockedRes[r] = def != null && def.TryGetValue(r, out var dv) ? dv : 0;
        var preset = new PbPreset { Units = units, Res = new() };
        foreach (var r in PointBuy.RES) if (res?[r] is JsonValue v) preset.Res[r] = v.Num();
        bool buildings = !(hp["include_buildings"] is JsonValue ib && ib.TryGetValue(out bool b) && !b);
        var cardsOnly = new Army();
        foreach (var (k, c) in units) if (int.TryParse(k, out int s)) cardsOnly.Cards[s] = c;
        var minC = mission?["min_credits"];
        // max(Credits, MinCredits) as the original (GIW), and never less than the fixed preset itself: played with another
        // tribe the preset is that tribe's (the level's own list, or the swapped classes) and can cost more than the
        // mission's least credits (mission 11 as the Norsemen: 10800 of 3000; mission 5: the Norse Bela 2400 of 2200) -
        // the fixed cards the budget could not pay were dropped, heroes among them, and the mission was lost at once
        double have = Math.Max(Math.Max(credits, minC is JsonValue ? minC.Num() : 0), PB.Cost(cardsOnly, tribe));
        double total = PB.Total(have, tribe, mp: false, buildings: buildings);
        var o = new CampaignArmy
        {
            Tribe = tribe, Credits = have, Total = total, Allowed = allowed, Buildings = buildings,
            Defaults = () => PB.Load(preset, tribe, total, fix: true, buildings: buildings, mp: false, lockedRes: lockedRes),
        };
        if (hp["unit_limits"] is JsonArray ul) for (int i = 0; i < 5 && i < ul.Count; i++) o.Limits[i] = ul[i]?["max"] is JsonValue mv ? mv.Int() : null;
        return o;
    }

    /// <summary>the army screen of a co-op seat (docs/COOP.md "Armies"): seat 1's is the mission's (Of); seats 2 and 3 get
    /// the same budget and shop in their tribe without the story heroes - the heroes are seat 1's and split by the lobby's
    /// hero split (CoopRuntime.AfterBuild), a second copy would be a hero twice. Null: no army screen.</summary>
    public static CampaignArmy? ForCoopSeat(JsonNode? mission, Rules.Rules D, string tribe, double credits, int seat)
    {
        var o = Of(mission, D, tribe, credits);
        if (o == null || seat <= 1) return o;
        var PB = new PointBuy(D, null);
        bool Hero(string card) => D.Def(PB.Resolve(card, o.Tribe).Cls, null)?.Unique == true;
        if (o.Allowed != null) o.Allowed.RemoveWhere(Hero);
        var baseDefaults = o.Defaults;
        o.Defaults = () =>
        {
            var a = baseDefaults();
            foreach (var k in a.Cards.Where(kv => Hero(kv.Value)).Select(kv => kv.Key).ToList()) { a.Cards.Remove(k); a.Fixed.Remove(k); }
            return a;
        };
        return o;
    }
}
