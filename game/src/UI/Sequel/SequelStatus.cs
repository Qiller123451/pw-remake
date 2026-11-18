// Which missions of the remake's own campaigns play in this version - their menus grey out the others (the project's
// rule, as CampaignStatus for the original campaign; "Use the debug options" still lets them start, for testing).
// Updated as missions are verified by their scripted playthroughs (tests/PwRemake.Tests/SequelPlaythroughTests.cs,
// docs/SEQUEL.md "Status").
using System.Collections.Generic;

namespace PwRemake.UI.Sequel;

public static class SequelStatus
{
    // ("sequel" | "scenario", id) -> state, note
    static readonly Dictionary<(string, int), (MissionState State, string Note)> s_table = new()
    {
        [("sequel", 1)] = (MissionState.Playable, ""),
        [("sequel", 2)] = (MissionState.Playable, ""),
        [("sequel", 3)] = (MissionState.Playable, ""),
        [("sequel", 4)] = (MissionState.Playable, ""),
        [("sequel", 5)] = (MissionState.Playable, ""),
        [("sequel", 6)] = (MissionState.Playable, ""),
        [("sequel", 7)] = (MissionState.Playable, ""),
        [("sequel", 8)] = (MissionState.Playable, ""),
        [("sequel", 9)] = (MissionState.Playable, ""),
        [("sequel", 10)] = (MissionState.Playable, ""),
        [("sequel", 11)] = (MissionState.Playable, ""),
        [("sequel", 12)] = (MissionState.Playable, ""),
        [("sequel", 13)] = (MissionState.Playable, ""),
        [("sequel", 14)] = (MissionState.Playable, ""),
        [("sequel", 15)] = (MissionState.Playable, ""),
        [("sequel", 16)] = (MissionState.Playable, ""),
        [("scenario", 1)] = (MissionState.Playable, ""),
        [("scenario", 2)] = (MissionState.Playable, ""),
        [("scenario", 3)] = (MissionState.Playable, ""),
        [("scenario", 4)] = (MissionState.Playable, ""),
        [("scenario", 5)] = (MissionState.Playable, ""),
    };

    public static MissionState Of(string kind, int id) => s_table.TryGetValue((kind, id), out var s) ? s.State : MissionState.NotYet;
    public static string Note(string kind, int id) => s_table.TryGetValue((kind, id), out var s) ? s.Note : "";
}
