// The settings of the Options window applied to the running game - at its start and again whenever a setting changes
// (main.js applySettings): the renderer (shadows, resolution, frame cap, antialiasing), grass, particles, weather, the
// HUD (size, tooltips near the mouse, the build menu opening by itself, income per minute, the minimap's rotation and
// colours), the camera's edge scrolling, the range circles ("Show attack ranges"), the mission UI's mentor hints and
// subtitles. Sound and the window: SettingsApply.
using System;

namespace PwRemake.App;

public partial class GameSession
{
    /// <summary>Ctrl+Shift+R (the original's ToggleRangeMarkersAttack key): "Show attack ranges" on / off - the own
    /// shooting buildings' range circles (RangeRings) - kept in settings.json, with a line on the message bar</summary>
    public void ToggleRanges()
    {
        var s = GameApp.Settings;
        s.ShowRanges = !s.ShowRanges;
        s.Save();
        if (Ranges != null) Ranges.ShowAll = s.ShowRanges;
        Message(s.ShowRanges ? "Attack ranges shown (Ctrl+Shift+R)" : "Attack ranges hidden (Ctrl+Shift+R)");
    }

    /// <summary>applies the settings to this game (cheap when nothing changed: only what differs is redone)</summary>
    public void ApplySettings(AppSettings s)
    {
        if (Renderer != null)
        {
            var r = Renderer.Settings;
            string shadows = s.Shadows switch { "off" => "off", "high" => "high", _ => "medium" };
            double res = Math.Clamp(s.RenderScale, 0.25, 2);
            if (r.Shadows != shadows || r.ResScale != res || r.FpsCap != s.FpsCap || r.Antialias != s.Antialias)
            {
                r.Shadows = shadows; r.ResScale = res; r.FpsCap = s.FpsCap; r.Antialias = s.Antialias;
                Renderer.ApplySettings();
            }
            r.Weather = s.Weather;
        }
        if (View != null)
        {
            var grass = s.Grass is "off" or "dense" ? s.Grass : "on";
            if (View.Options.Render.Grass != grass) View.SetGrass(grass);
            if (View.Weather != null) View.Weather.Enabled = s.Weather;
        }
        SetParticles(s.Particles);
        if (HudView != null)
        {
            if (HudView.UiScale != s.UiScale) { HudView.UiScale = s.UiScale; HudView.Relayout(); }
            HudView.TipNear = s.TipNear;
            HudView.Model.AutoMenu = s.AutoMenu;
            HudView.Model.ShowIncome = s.IncomePerMinute;
            if (HudView.Minimap is { } mm) { mm.Rotate = s.MinimapRotate; if (mm.PlayerColors != s.MinimapPlayerColors) { mm.PlayerColors = s.MinimapPlayerColors; mm.Invalidate(); } }
        }
        if (_camInput != null) _camInput.EdgeScroll = s.EdgeScroll;
        if (Ranges != null) Ranges.ShowAll = s.ShowRanges;
        if (Presenter != null) { Presenter.State.MentorTexts = s.MentorTexts; Presenter.State.Subtitles = s.Subtitles; Presenter.State.CinemaBars = s.CutsceneBars; }
    }
}
