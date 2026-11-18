// The statistics screen's content (the original's CStatisticScreen, StatisticScreen.usl 864-982; docs/spec/
// ui_groups7_9.md §4): tabs with a row per player - Overview, Economical, Military, Buildings, Fights, Invention, Quests
// (campaign) - and the time line, a graph of one category for the players ticked. Opened from the end screen ("View
// Statistics"). The Overview's columns are the engine's sums in the original [?engine]: here the totals of the other tabs.
//
// No Godot types (HudStatsWindow.cs draws it). Ported from remake/src/ui/stats.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Scripting;
using PwRemake.Sim.World;

namespace PwRemake.UI.Hud.Logic;

public sealed class StatsModel
{
    public sealed record Column(string Key, string Default, Func<PlayerStats, string> Value);
    public sealed record Tab(string Key, string Default, List<Column>? Columns);
    public sealed record Graph(string Key, string Default, Func<StatsSample, double> Value);

    readonly World W;
    public readonly List<Player> Players;
    public readonly List<Tab> Tabs = new();
    /// <summary>the time line's categories (SS:130-145): the stock over time, or the collected total</summary>
    public static readonly Graph[] GRAPHS =
    {
        new("AllUnits", "Units", v => v.Units), new("AllCivilians", "Civilians", v => v.Civil), new("AllMilitary", "Military", v => v.Military),
        new("AllBuildings", "Buildings", v => v.Buildings), new("AllResource", "Resource", v => v.Food + v.Wood + v.Stone + v.Skulls),
        new("AllFood", "Food", v => v.Food), new("AllWood", "Wood", v => v.Wood), new("AllStone", "Stone", v => v.Stone), new("AllSkulls", "Skulls", v => v.Skulls),
        new("AllResourceTotal", "Resource(total)", v => v.CFood + v.CWood + v.CStone + v.CSkulls), new("AllFoodTotal", "Food(total)", v => v.CFood),
        new("AllWoodTotal", "Wood(total)", v => v.CWood), new("AllStoneTotal", "Stone(total)", v => v.CStone), new("AllSkullsTotal", "Skulls(total)", v => v.CSkulls),
    };

    /// <summary>quests: the campaign's quests (null outside a campaign: no Quests tab)</summary>
    public StatsModel(World w, IEnumerable<QuestState>? quests)
    {
        W = w;
        // (a campaign level's ShowStatistics)
        Players = w.Players.Where(p => !(p.Data?["show_statistics"]?.GetValueKind() == System.Text.Json.JsonValueKind.False)).ToList();
        static string N(double v) => Sim.Rules.TT.JsString(v);
        static string Hms(double? t) => t == null ? "\u0001failed" : $"{Math.Floor(t.Value / 3600)}h {Math.Floor(t.Value / 60) % 60}min {Math.Floor(t.Value % 60)}s";
        Tabs.Add(new("Overview", "Overview", new()
        {
            new("Col_Economical", "Economical", t => N(t.Food + t.Wood + t.Stone + t.Skulls)), new("Col_Units", "Units", t => N(t.Workers + t.Infantry + t.Animals + t.Vehicles + t.Ships)),
            new("Col_Buildings", "Buildings", t => N(t.Eco + t.Mil + t.Def)), new("Col_Invention", "Invention", t => N(1 + t.Epochs.Count(x => x != null))),
        }));
        Tabs.Add(new("Economical", "Economical", new()
        {
            new("Col_Food", "Food", t => N(t.Food)), new("Col_Wood", "Wood", t => N(t.Wood)), new("Col_Stone", "Stone", t => N(t.Stone)), new("Col_Skulls", "Skulls", t => N(t.Skulls)),
            new("Col_Workers", "Workers", t => N(t.Workers)), new("Col_Tributesend", "Send Tribute", t => N(t.TributeSent)),
        }));
        Tabs.Add(new("Military", "Military", new()
        {
            new("Col_Infantry", "Infantry", t => N(t.Infantry)), new("Col_Animals", "Animals", t => N(t.Animals)), new("Col_Vehicles", "Vehicles", t => N(t.Vehicles)), new("Col_Ships", "Ships built", t => N(t.Ships)),
        }));
        Tabs.Add(new("Buildings", "Buildings", new()
        {
            new("Col_Economybuildingsbuilt", "Economy Buildings", t => N(t.Eco)), new("Col_Battlebuildingsbuilt", "Military Buildings", t => N(t.Mil)), new("Col_Defensebuildingsbuilt", "Defense Building", t => N(t.Def)),
        }));
        Tabs.Add(new("Fights", "Fights", new()
        {
            new("Col_Unitslost", "Units lost", t => N(t.UnitsLost)), new("Col_Buildingslost", "Buildings lost", t => N(t.BuildingsLost)), new("Col_Enemieskilled", "Enemies killed", t => N(t.Killed)),
            new("Col_Buildingsdestroyed", "Buildings destroyed", t => N(t.Razed)), new("Col_Animalskilled", "Animals killed", t => N(t.AnimalKills)),
        }));
        Tabs.Add(new("Invention", "Invention", new[] { 2, 3, 4, 5 }.Select(n => new Column("Col_Level" + n, "Epoch " + n, t => Hms(t.Epochs[n]))).ToList()));
        if (quests != null)
        {
            var q = quests.Where(x => x.Visible).ToList();
            var main = q.Where(x => !x.Bonus.Any(v => v > 0)).ToList();
            static string Done(List<QuestState> l) => l.Count(x => x.Accomplished) + " / " + l.Count;
            var side = q.Where(x => !main.Contains(x)).ToList();
            Tabs.Add(new("Quest", "Quests", new()
            {
                new("Col_QuestsAll", "Quests (All)", _ => Done(q)), new("Col_QuestsMain", "Quests (Main)", _ => Done(main)), new("Col_QuestsBonus", "Quests (Bonus)", _ => Done(side)),
            }));
        }
        Tabs.Add(new("TimeLine", "TimeLine", null));
    }

    /// <summary>a label of the screen (_UI_StatScre_&lt;key&gt;)</summary>
    public string Ui(string k, string d) => W.Data.Ui("_UI_StatScre_" + k, d);
    /// <summary>a cell; "failed" (an epoch not reached) in the game's words</summary>
    public string Cell(Column c, Player p)
    {
        var v = c.Value(W.GameStats.Table(p));
        return v == "\u0001failed" ? Ui("Label_Failed", "failed") : v;
    }

    /// <summary>the time line of a category for the players shown: (t0, t1, max, lines per player id of (t, v))</summary>
    public (double T0, double T1, double Max, Dictionary<int, List<(double T, double V)>> Lines) Series(int graph, ISet<int> shown)
    {
        var S = W.GameStats.Series; var f = GRAPHS[graph].Value;
        var lines = new Dictionary<int, List<(double, double)>>();
        if (S.Count < 2) return (0, 0, 1, lines);
        double t0 = S[0].T, t1 = S[^1].T, max = 1;
        foreach (var s in S) foreach (var p in Players) if (shown.Contains(p.Id) && s.V.TryGetValue(p.Id, out var v)) max = Math.Max(max, f(v));
        foreach (var p in Players)
        {
            if (!shown.Contains(p.Id)) continue;
            var l = new List<(double, double)>();
            foreach (var s in S) if (s.V.TryGetValue(p.Id, out var v)) l.Add((s.T, f(v)));
            lines[p.Id] = l;
        }
        return (t0, t1, max, lines);
    }
}
