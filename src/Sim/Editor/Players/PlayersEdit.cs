// Undoable changes of the map's players and settings (docs/MAP_EDITOR.md "Players and map settings"): the whole player
// table or the whole settings before and after (they are small), and the skirmish setup Test map plays from the map's
// players (the slots with a start location, their tribes, teams, colours, who plays them, the army budget, MIRAGE's
// options). No Godot types.
using System;
using System.Collections.Generic;
using System.Linq;
using PwRemake.Sim.Game;

namespace PwRemake.Sim.Editor;

/// <summary>a change of the players (a slot's tribe, team, colour, resources, diplomacy, the game options)</summary>
public sealed class PlayersEdit : IEditCommand
{
    readonly MapPlayerSettings _before, _after;
    public string Name { get; }
    public PlayersEdit(MapPlayerSettings before, MapPlayerSettings after, string name = "Change players") { _before = before.Clone(); _after = after.Clone(); Name = name; }
    public void Do(IEditorMapDoc doc) => doc.SetPlayers(_after.Clone());
    public void Undo(IEditorMapDoc doc) => doc.SetPlayers(_before.Clone());
    public bool TryMerge(IEditCommand next) => false;

    /// <summary>the edit made by f on a copy of the document's players (null: nothing changed)</summary>
    public static PlayersEdit Make(IEditorMapDoc doc, Action<MapPlayerSettings> f, string name = "Change players")
    {
        var b = doc.GetPlayers();
        var a = b.Clone();
        f(a);
        return new PlayersEdit(b, a, name);
    }
}

/// <summary>a change of the map's settings (name, author, descriptions, time, weather, camera, preview)</summary>
public sealed class SettingsEdit : IEditCommand
{
    readonly MapSettings _before, _after;
    public string Name { get; }
    public SettingsEdit(MapSettings before, MapSettings after, string name = "Change map settings") { _before = before.Clone(); _after = after.Clone(); Name = name; }
    public void Do(IEditorMapDoc doc) => doc.SetSettings(_after.Clone());
    public void Undo(IEditorMapDoc doc) => doc.SetSettings(_before.Clone());
    public bool TryMerge(IEditCommand next) => false;

    public static SettingsEdit Make(IEditorMapDoc doc, Action<MapSettings> f, string name = "Change map settings")
    {
        var b = doc.GetSettings();
        var a = b.Clone();
        f(a);
        return new SettingsEdit(b, a, name);
    }
}

/// <summary>the skirmish Test map plays from the map's players</summary>
public static class PlayersSetup
{
    /// <summary>the player slots that play: those with a start location on the map (ascending)</summary>
    public static List<int> ActiveSlots(IEditorMapDoc doc) =>
        doc.Objects.Where(o => o.Type == "SLOC" && o.Owner is int s && s >= 0 && s < MapPlayerSettings.SLOTS).Select(o => o.Owner!.Value).Distinct().OrderBy(s => s).ToList();

    /// <summary>the setup of a skirmish on the map: one setup slot per active player slot, on its own start location
    /// (head quarters = the slot: chosen), its tribe (Random: one its tribes allow), team and colour; the first slot a
    /// human may play is yours (human or either; none: the first), the others computer players; the army budget and
    /// MIRAGE's options of the map. playable: the configuration's playable tribes.</summary>
    public static SkirmishSetup Make(IEditorMapDoc doc, IReadOnlyCollection<string> playable, string mapKey, string mapName)
    {
        var P = doc.GetPlayers();
        var act = ActiveSlots(doc);
        if (act.Count == 0) act = new List<int> { 0, 1 };
        var s = Skirmish.DefaultSetup();
        foreach (var sl in s.Slots) sl.Type = "closed";
        int human = act.FirstOrDefault(i => P.Slots[i].Control != "computer", act[0]);
        var used = new HashSet<string>();
        for (int k = 0; k < act.Count && k < s.Slots.Count; k++)
        {
            int i = act[k];
            var ps = P.Slots[i];
            // (slot 0 of the setup is the human player: yours first, then the others in their order)
            int at = i == human ? 0 : 1 + act.Where(x => x != human).ToList().IndexOf(i);
            var sl = s.Slots[at];
            var allowed = (ps.Tribes.Count > 0 ? ps.Tribes : MapPlayerSettings.TRIBES.ToList()).Where(playable.Contains).ToList();
            sl.Type = i == human ? "human" : "ai";
            sl.Tribe = allowed.Contains(ps.Tribe) ? ps.Tribe : "Random";
            sl.Team = Math.Max(1, ps.Team);
            sl.Hq = i;
            sl.Color = string.IsNullOrEmpty(ps.Color) || used.Contains(ps.Color) ? Skirmish.FreeColor(s.Slots.Where(x => x.Type != "closed").ToList(), sl) : ps.Color;
            used.Add(sl.Color!);
            sl.Level = 5;
        }
        s.Map = mapKey; s.MapName = mapName; s.Seed = 1;
        // (Test map: everybody on their own slot's start location, whatever the map's ChooseHQ default says)
        s.Points = P.Points; s.ChooseHQ = true;
        s.Teams = Math.Max(2, act.Select(i => P.Slots[i].Team).Distinct().Count());
        s.EpochSix = P.EpochSix; s.RemoveTitans = P.RemoveTitans; s.FlyingEnabled = P.FlyingEnabled; s.Portals = P.Portals; s.BuildingLimit = P.BuildingLimit;
        var types = doc.GetSettings().GameTypes;
        s.Mode = types.Count > 0 ? Skirmish.ModeOfType(types[0]) ?? "deathmatch" : "deathmatch";
        if (s.Mode != "deathmatch") Skirmish.ApplyMode(s, s.Mode);
        return s;
    }
}
