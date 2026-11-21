// The map editor's "Test map" in the game session (docs/MAP_EDITOR.md): a skirmish whose setup's map is a key of the
// editor ("editor:<n>", EditorPlaytest) is played on the edited map from memory - the map source is built from the
// editor's snapshot, not read from a file - and its end ("Back to Main menu", the pause menu's quit) returns to the
// editor, which still has the map as it was. Retry plays the same snapshot again.
using PwRemake.Sim.Editor;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using SimRules = PwRemake.Sim.Rules.Rules;

namespace PwRemake.App;

public partial class GameSession
{
    /// <summary>this game is the map editor's Test map</summary>
    public bool EditorTest => (Skirmish != null && EditorPlaytest.IsKey(Start.Skirmish?.Map))
        || (Mission != null && PwRemake.Sim.Editor.Triggers.TestMission.IsTestFolder(Start.MissionFolder));   // (a map with triggers: played as a mission)

    /// <summary>the map source of an editor test map (null: the setup's map is a file or the random jungle) - the same
    /// rules adapter as a map file's (WorldBuilder.MapSourceOf)</summary>
    static MapSource? EditorMapOf(SkirmishSetup setup, SimRules rules, PwRemake.Sim.Content.ModelManifest manifest, ForestData? forest) =>
        EditorPlaytest.IsKey(setup.Map) ? EditorPlaytest.Source(setup.Map, new PwRemake.Sim.Campaign.RulesMapAdapter(rules), n => manifest.Has(n), forest) : null;

    /// <summary>this game is a custom map played as a mission (the campaign menu's Custom campaigns: a user's map with
    /// triggers, CustomPlay)</summary>
    public bool CustomMap => Mission != null && PwRemake.Sim.Editor.Triggers.CustomMission.IsCustomFolder(Start.MissionFolder);

    /// <summary>the end screen's and the pause menu's "menu" of a test map: back to the editor; false: not a test map's.
    /// A custom map: Retry plays it again; the next mission of a user campaign's map plays the campaign's next mission,
    /// else the next mission / the menu go back to the campaign menu's Custom campaigns.</summary>
    bool EditorEndAction(string a)
    {
        if (CustomMap)
        {
            if (a is "exit" or "load" or "stats") return false;
            if (a == "retry") { Boot.Instance.Show(new PwRemake.UI.LoadingScreen(Start, PwRemake.UI.CustomPlay.EntryOf(Start))); return true; }
            if (a == "next" && Start.CustomCampaign != null) { PwRemake.UI.CustomPlay.Continue(Start, _missionWon); return true; }
            Boot.Instance.Show(new PwRemake.UI.CampaignMenu(1));
            return true;
        }
        if (!EditorTest) return false;
        // a map with triggers plays as a mission (its folder written by Test map): Retry plays it again, every other end
        // (the next mission, the menu) is back to the editor
        if (Mission != null && a == "retry") { Boot.Instance.Show(PwRemake.Editor.MapEditor.TestMissionLoading(Start)); return true; }
        if (a is "exit" or "load" or "stats" || (Mission == null && a != "menu")) return false;
        L.Info("test map over: back to the map editor");
        Boot.Instance.Show(new PwRemake.Editor.MapEditor());
        return true;
    }
}
