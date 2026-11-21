// "Your colour" of a campaign mission (docs/CAMPAIGN.md "Your colour"; Kacper: "an option to pick your starting colour
// in the campaigns, but block the colours used by the enemy"): the skirmish's palette (the original's eight party
// colours, the remake's eight) and its RGB picker (PlayerColorCell), without the colours the mission's other players
// have - enemies, allies, neutral and computer players of the map's players (greyed out: "used by <name>").
//
// The choice is remembered per campaign (settings.json campaignColors, Key), so it is not picked again every mission;
// when the remembered colour is one the next mission's players have, that mission gives its own colour (the choice
// stays remembered for the others). It goes into the GameStart (GameStart.Color: the world gives it to the human
// player - Campaign.MissionColors), so saves, replays and the other machines show the same.
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Campaign;
using PwRemake.Sim.Game;
using SimColors = PwRemake.Sim.Rules.Colors;

namespace PwRemake.UI;

public static class CampaignColor
{
    /// <summary>the remembered colour's key of a mission start: "user:&lt;campaign&gt;" (a user campaign's), "maps" (My
    /// maps), "remake" (the remake's missions: Outbreak ...), "campaign" (the original campaign), "booster", "mod",
    /// "other:&lt;folder&gt;" (the configuration's other groups)</summary>
    public static string Key(int mission, string folder, CustomCampaignRef? cc)
    {
        if (cc != null) return "user:" + cc.Campaign;
        if (PwRemake.Sim.Editor.Triggers.CustomMission.IsCustomFolder(folder)) return "maps";
        if (RemakeMissions.IsToken(folder)) return "remake";
        var cfg = GameApp.Config;
        if (cfg == null) return "campaign";
        var e = PwRemake.Sim.Campaign.MissionEntry.LoadIndex(cfg.MissionsDir).Find(x => x.Id == mission);
        if (e == null) return "campaign";
        return MissionGroups.KindOf(cfg, e) switch
        {
            MissionGroupKind.Original => "campaign",
            MissionGroupKind.BoosterPack => "booster",
            MissionGroupKind.Mod => "mod",
            _ => "other:" + MissionGroups.SourceFolder(e),
        };
    }
    public static string Key(GameStart s) => Key(s.Mission, s.MissionFolder, s.CustomCampaign);

    /// <summary>the colour remembered for a campaign ("" none: the mission's)</summary>
    public static string Remembered(string key) => GameApp.Settings.CampaignColors.GetValueOrDefault(key) ?? "";
    public static void Remember(string key, string id)
    {
        var d = GameApp.Settings.CampaignColors;
        if (string.IsNullOrEmpty(id)) d.Remove(key); else d[key] = id;
        GameApp.Settings.Save();
    }

    /// <summary>the colours the other players of a mission folder have (none when it cannot be read)</summary>
    public static List<TakenColor> Taken(string? folder, IEnumerable<int>? except = null)
    {
        if (string.IsNullOrEmpty(folder)) return new();
        try { return MissionColors.Used(folder, except); }
        catch (Exception e) { PwRemake.Sim.Core.Log.For("UI.Campaign").Debug($"colours of {folder}: {e.Message}"); return new(); }
    }

    /// <summary>the colour a mission started now gives the human: the campaign's remembered one when no other player
    /// of the mission has it, else null (the mission's own)</summary>
    public static string? For(string? folder, string key) => MissionColors.Resolve(Remembered(key), Taken(folder));

    /// <summary>GameStart.Color of a start (its folder resolved on this machine)</summary>
    public static string? For(GameStart s)
    {
        var cfg = GameApp.Config;
        var folder = cfg != null ? MissionSession.MissionFolderOf(s, cfg) : null;
        return For(folder, Key(s));
    }

    /// <summary>the swatch of a mission's own colour for its human player (grey: none)</summary>
    public static Color? MissionDefault(string? folder)
    {
        if (string.IsNullOrEmpty(folder)) return null;
        try
        {
            int human = PwRemake.Sim.Coop.CoopPlan.HumanSlot(PwRemake.Sim.Coop.CoopPlan.MissionSlots(folder));
            int ci = MissionColors.ColorSlotOf(folder, human);
            return ci >= 0 ? PlayerColorCell.ToColor(SimColors.PLAYER_COLORS[ci].Id) : null;
        }
        catch (Exception) { return null; }
    }
}
