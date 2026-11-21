// The end of a campaign mission and the campaign menu's two tabs (docs/CAMPAIGN.md "Custom campaigns"): the end
// screen's Continue plays the next mission of the mission's own group - the original campaign (the tutorial, 1 - 16),
// the Booster Pack (4-1 .. 4-3), MIRAGE's own missions - and goes back to the menu's tab of that group at its end; a
// user campaign's mission continues with that campaign (CustomPlay).
using PwRemake.Sim.Campaign;
using PwRemake.UI;

namespace PwRemake.App;

public partial class GameSession
{
    /// <summary>the mission's end was a win (MissionEnded; the end screen's Continue of a user campaign)</summary>
    bool _missionWon;

    /// <summary>the next mission of the started mission's group (MissionGroups.NextInGroup), null at the group's end</summary>
    PwRemake.Sim.Campaign.MissionEntry? NextMission()
    {
        var cfg = GameApp.Config;
        if (cfg == null || Mission == null) return Mission?.Next;
        if (RemakeMissions.IsToken(Start.MissionFolder)) return null;          // (a remake mission stands alone)
        var index = PwRemake.Sim.Campaign.MissionEntry.LoadIndex(cfg.MissionsDir);
        return MissionGroups.NextInGroup(cfg, index, Start.Mission, id => CampaignStatus.Hidden(cfg.Id, id));
    }

    /// <summary>the campaign menu's tab of the started mission: 0 the original campaign, 1 the custom campaigns</summary>
    int GroupTab()
    {
        var cfg = GameApp.Config;
        if (cfg == null || Start.CustomCampaign != null || PwRemake.Sim.Editor.Triggers.CustomMission.IsCustomFolder(Start.MissionFolder)) return 1;
        // the remake's own missions: the custom tab's group "Remake missions"
        if (RemakeMissions.IsToken(Start.MissionFolder)) { GameApp.Settings.CustomSelection = "mission:" + Start.Mission; return 1; }
        var e = PwRemake.Sim.Campaign.MissionEntry.LoadIndex(cfg.MissionsDir).Find(x => x.Id == Start.Mission);
        if (e == null || MissionGroups.KindOf(cfg, e) == MissionGroupKind.Original) return 0;
        GameApp.Settings.CustomSelection = "mission:" + e.Id;
        return 1;
    }
}
