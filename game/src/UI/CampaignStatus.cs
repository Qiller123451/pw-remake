// Which campaign missions play in this version - the campaign menu greys out the others (the player's wish: missions
// not adapted yet are greyed out; "Use the debug options" still lets them start, for testing). Updated as missions
// are verified (docs/CAMPAIGN.md "Mission status").
using System.Collections.Generic;

namespace PwRemake.UI;

public enum MissionState { NotYet, Partial, Playable, Hidden }

public static class CampaignStatus
{
    // (configuration or "*", mission id) -> state, note
    static readonly Dictionary<(string, int), (MissionState state, string note)> s_table = new()
    {
        // the scripted playthroughs (tests/PwRemake.Tests/Playthrough, docs/PLAYTHROUGHS.md; the 17 missions again 2026-11-02 after
        // port/missionfix: all 119 runs won): won with every tribe of a configuration = Playable ("*": both), with some =
        // Partial (the note names the others); missing = NotYet (docs/CAMPAIGN.md "Mission status").
        // BoosterPack 1's missions 4-1 .. 4-3 (ids 41-43, after mission 4 in the menu) and MIRAGE's SEAS missions (20-22:
        // their SEAS versions, 23 "Seeking Ada") were won with every tribe (2026-11-02: PlayB41/B42/B43/Seas02).
        [("*", 0)] = (MissionState.Playable, ""),
        // the remake's own missions (game/missions): 90 Outbreak (docs/OUTBREAK.md)
        [("*", 90)] = (MissionState.Playable, ""),
        [("*", 1)] = (MissionState.Playable, ""),
        [("*", 2)] = (MissionState.Playable, ""),
        [("*", 3)] = (MissionState.Playable, ""),
        [("*", 4)] = (MissionState.Playable, ""),
        [("*", 5)] = (MissionState.Playable, ""),
        [("*", 6)] = (MissionState.Playable, ""),
        [("*", 7)] = (MissionState.Playable, ""),
        [("*", 8)] = (MissionState.Playable, ""),
        [("*", 9)] = (MissionState.Playable, ""),
        [("*", 10)] = (MissionState.Playable, ""),
        [("*", 11)] = (MissionState.Playable, ""),
        [("*", 12)] = (MissionState.Playable, ""),
        [("*", 13)] = (MissionState.Playable, ""),
        [("*", 14)] = (MissionState.Playable, ""),
        [("*", 15)] = (MissionState.Playable, ""),
        [("*", 16)] = (MissionState.Playable, ""),
        [("*", 41)] = (MissionState.Playable, ""),
        [("*", 42)] = (MissionState.Playable, ""),
        [("*", 43)] = (MissionState.Playable, ""),
        // MIRAGE's own missions (the prepare tool exports them only for the MIRAGE configuration)
        [("mirage", 20)] = (MissionState.Playable, ""),
        [("mirage", 21)] = (MissionState.Playable, ""),
        [("mirage", 22)] = (MissionState.Playable, ""),
        [("mirage", 23)] = (MissionState.Playable, ""),
        // the MIRAGE configuration's own versions of BoosterPack 1's missions 4-1 .. 4-3: left out of the menus for now
        // (the player's wish 2026-11-11; "--mission 41" still starts them)
        [("mirage", 41)] = (MissionState.Hidden, ""),
        [("mirage", 42)] = (MissionState.Hidden, ""),
        [("mirage", 43)] = (MissionState.Hidden, ""),
    };

    /// <summary>Until a mission is verified it counts as not adapted.</summary>
    public static MissionState Default = MissionState.NotYet;

    public static MissionState Of(string config, int mission) =>
        s_table.TryGetValue((config, mission), out var s) ? s.state :
        s_table.TryGetValue(("*", mission), out s) ? s.state : Default;

    /// <summary>Left out of the campaign menus (not even greyed out).</summary>
    public static bool Hidden(string config, int mission) => Of(config, mission) == MissionState.Hidden;

    public static string Note(string config, int mission) =>
        s_table.TryGetValue((config, mission), out var s) ? s.note :
        s_table.TryGetValue(("*", mission), out s) ? s.note : "";
}
