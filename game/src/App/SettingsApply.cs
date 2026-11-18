// The settings applied at once wherever they matter (main.js applySettings): the sound (volumes, sound on / off), the
// window and the frame rate (Boot.ApplyDisplaySettings), and the running game, if any (GameSession.ApplySettings).
// The Options window calls it on every change; the file is written when the window closes (Save).
using System;
using PwRemake.Sim.Core;
using PwRemake.View.Audio;

namespace PwRemake.App;

public static class SettingsApply
{
    public static void All()
    {
        var s = GameApp.Settings;
        PwRemake.View.Materials.ModelMaterials.SetOriginalColours(s.OriginalColors);
        PwRemake.View.Materials.UnitsBehind.SetEnabled(s.ShowUnitsBehind);
        PwRemake.View.Materials.SettingMaterials.SetEnabled(s.SettingTextures);
        try { GameAudio.Current?.ApplySettings(s); } catch (Exception e) { Log.For("App").WarnOnce("apply-audio", $"sound settings: {e.Message}"); }
        try { if (Boot.Instance != null) Boot.Instance.ApplyDisplaySettings(); } catch (Exception e) { Log.For("App").WarnOnce("apply-display", $"display settings: {e.Message}"); }
        try { GameSession.Current?.ApplySettings(s); } catch (Exception e) { Log.For("App").WarnOnce("apply-game", $"game settings: {e.Message}"); }
    }
}
