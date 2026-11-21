// Which configuration a map needs (docs/MAP_FORMAT.md "map.lua": requires; docs/MAP_EDITOR.md "Saving and sharing"). The
// user's maps folder serves every configuration, so a map made with a mod (MIRAGE) that uses what only the mod has would
// show in the official game's lists and fail there. When the editor saves or exports a map it decides map.lua's
// `requires`: the mod's name when the editor runs a mod's configuration and the map uses something of it - an object of
// a class the official game does not have (a placed object, a mission's world object), a class a trigger names (spawns,
// counts ...), one of the mod's game options (no titans, flying units, portals, building limits), a player slot that may
// only play the mod's tribes (SEAS) - else "" (any configuration). The lists filter by it: MapFiles.RequirementProblem.
// No Godot types.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor.Triggers;

namespace PwRemake.Sim.Editor;

/// <summary>what the decision knows of the configurations: the editor's mod ("" = the official game), the official game's
/// class names (ObjectPalette.ClassNamesOf, lower case) and playable tribes, the editor configuration's class names</summary>
public sealed class RequirementContext
{
    public string Mod = "";
    public ISet<string>? BaseClasses;
    public IReadOnlyCollection<string> BaseTribes = new[] { "Hu", "Aje", "Ninigi" };
    public ISet<string>? ModClasses;
}

public static class MapRequirement
{
    /// <summary>map.lua's requires for the map: ctx.Mod when the map uses something only the mod has (Reasons), else ""</summary>
    public static string Decide(IEditorMapDoc doc, RequirementContext ctx) => ctx.Mod.Length > 0 && Reasons(doc, ctx, 1).Count > 0 ? ctx.Mod : "";

    /// <summary>what of the mod the map uses (sentences; at most max)</summary>
    public static List<string> Reasons(IEditorMapDoc doc, RequirementContext ctx, int max = 20)
    {
        var res = new List<string>();
        if (ctx.Mod.Length == 0) return res;
        var bc = ctx.BaseClasses;
        // the objects (with a mission's world objects: they are in the editor's list)
        if (bc != null && bc.Count > 0)
        {
            var cls = doc.Objects.Where(o => o.Type != "SLOC" && !bc.Contains(o.Cls.ToLowerInvariant())).Select(o => o.Cls).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
            if (cls.Count > 0) res.Add($"classes the official game does not have: {string.Join(", ", cls.Take(6))}{(cls.Count > 6 ? $" (+{cls.Count - 6})" : "")}");
            // the classes the triggers name (a spawn's "aje_warrior 2 4", a count's class list ...): the mod's own
            if (ctx.ModClasses is { Count: > 0 } mc)
            {
                var named = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in doc.Mission.Triggers.Triggers)
                    foreach (var (n, cond) in t.Conditions.Select(x => (x, true)).Concat(t.Actions.Select(x => (x, false))))
                    {
                        var spec = TriggerCatalog.Find(n.Code, cond);
                        foreach (var p in n.Params)
                        {
                            // (a class parameter; a spawn's classes_0 .. classes_4: "class level count")
                            bool isClass = spec?.Param(p.Key)?.Type == PType.Class || p.Key.StartsWith("classes_", StringComparison.Ordinal);
                            if (!isClass || p.Value.Value is not { Length: > 0 } v) continue;
                            foreach (var w in v.Split(new[] { ' ', '|', ',', ';', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                var lc = w.ToLowerInvariant();
                                if (mc.Contains(lc) && !bc.Contains(lc)) named.Add(w);
                            }
                        }
                    }
                if (named.Count > 0) res.Add($"triggers name classes the official game does not have: {string.Join(", ", named.Take(6))}");
            }
        }
        // the mod's game options (MIRAGE: no titans, flying units, portals, building limits; the sixth stage off needs nothing)
        var P = doc.GetPlayers();
        var opts = new List<string>();
        if (P.RemoveTitans) opts.Add("no titans");
        if (P.FlyingEnabled) opts.Add("flying units");
        if (P.Portals) opts.Add("portals");
        if (P.BuildingLimit) opts.Add("building limits");
        if (opts.Count > 0) res.Add($"{ctx.Mod}'s game options: {string.Join(", ", opts)}");
        // tribes only the mod has (a slot that starts as SEAS, or may play nothing else)
        var bt = new HashSet<string>(ctx.BaseTribes, StringComparer.OrdinalIgnoreCase);
        var act = PlayersSetup.ActiveSlots(doc);
        foreach (int i in act)
        {
            var s = P.Slots[i];
            bool modTribe = s.Tribe != "Random" && s.Tribe.Length > 0 && !bt.Contains(s.Tribe);
            bool onlyMod = s.Tribes.Count > 0 && !s.Tribes.Any(bt.Contains);
            if (modTribe || onlyMod) { res.Add($"player {i + 1} plays {(modTribe ? s.Tribe : string.Join(" / ", s.Tribes))}"); break; }
        }
        // a mission's players (map.lua's mission table): a tribe only the mod has
        if (doc.Mission.Mission?["players"] is System.Text.Json.Nodes.JsonArray mp)
            foreach (var p in mp.OfType<System.Text.Json.Nodes.JsonObject>())
                if (p["present"].Bool() && p["tribe"].Str() is { Length: > 0 } tr && !bt.Contains(tr)) { res.Add($"the mission's player {p["id"].Int() + 1} is {tr}"); break; }
        return res.Take(max).ToList();
    }
}
