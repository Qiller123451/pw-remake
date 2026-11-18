// The player's settings (user folder, settings.json - readable and editable by hand). Everything the menus change is
// here; the game reads it once at start, the Options window (game/src/UI/OptionsPanel.cs, docs/MENUS.md) applies a
// change at once (SettingsApply) and writes the file when it closes. Unknown keys are kept as they are.
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;

namespace PwRemake.App;

public sealed class AppSettings
{
    /// <summary>The content folder (empty: content/ beside the game, docs/CONTENT.md).</summary>
    [JsonPropertyName("contentRoot")] public string ContentRoot { get; set; } = "";
    /// <summary>The configuration picked last on the title screen (base, mirage ...).</summary>
    [JsonPropertyName("configuration")] public string Configuration { get; set; } = "";
    [JsonPropertyName("language")] public string Language { get; set; } = "uk";

    // the campaign menu's last choices
    [JsonPropertyName("campaignMission")] public int CampaignMission { get; set; } = 0;
    /// <summary>"" = the mission's own tribe; Hu, Aje, Ninigi, SEAS.</summary>
    [JsonPropertyName("campaignTribe")] public string CampaignTribe { get; set; } = "";
    /// <summary>0 easy, 1 medium, 2 hard, 3 nightmare (the remake's own: hard with bigger computer raids).</summary>
    [JsonPropertyName("difficulty")] public int Difficulty { get; set; } = 1;
    /// <summary>MIRAGE's game options of a campaign mission - single player and co-op (the campaign menu's / co-op
    /// lobby's "Sixth stage", "No titans", "Flying units": GameStart.Gameplay); MIRAGE's defaults: sixth stage on, titans on, flying units off</summary>
    [JsonPropertyName("campaignGameplay")] public PwRemake.Sim.Rules.GameplayOptions CampaignGameplay { get; set; } = new();

    // the skirmish menu (game/src/UI/SkirmishMenu.cs): the last setup per configuration, the own army presets
    [JsonPropertyName("skirmish")] public Dictionary<string, SkirmishSetup> Skirmish { get; set; } = new();
    /// <summary>"My Army": presets saved on the army page (the browser remake's localStorage pwr.pbPresets)</summary>
    [JsonPropertyName("armyPresets")] public List<PbPreset> ArmyPresets { get; set; } = new();
    /// <summary>the army budget of the campaign's missions: "&lt;configuration&gt;:&lt;mission id&gt;" -> the bonus points
    /// the mission before earned (main.js G.endMission; CampaignMgr.usl:145-183)</summary>
    /// <summary>the remake's own campaigns (docs/SEQUEL.md): the Containment mission chosen last, the highest one open
    /// (each won mission opens the next), the scenario and tribe chosen last, which tribes won each scenario</summary>
    [JsonPropertyName("sequelMission")] public int SequelMission { get; set; } = 1;
    [JsonPropertyName("sequelUnlocked")] public int SequelUnlocked { get; set; } = 1;
    [JsonPropertyName("scenarioMission")] public int ScenarioMission { get; set; } = 1;
    [JsonPropertyName("scenarioTribe")] public string ScenarioTribe { get; set; } = "Hu";
    [JsonPropertyName("scenarioWon")] public Dictionary<string, List<string>> ScenarioWon { get; set; } = new();
    [JsonPropertyName("campaignCredits")] public Dictionary<string, double> CampaignCredits { get; set; } = new();

    // the multiplayer menu (game/src/UI/MultiplayerMenu.cs, docs/MULTIPLAYER.md): the player's name, the last address
    // joined, the port hosted on, the game type hosted last ("skirmish" | "coop"), the co-op lobby's last choices
    [JsonPropertyName("playerName")] public string PlayerName { get; set; } = "";
    [JsonPropertyName("mpAddress")] public string MpAddress { get; set; } = "127.0.0.1";
    [JsonPropertyName("mpPort")] public int MpPort { get; set; } = 1905;
    [JsonPropertyName("mpMode")] public string MpMode { get; set; } = "skirmish";
    [JsonPropertyName("mpSceneRule")] public string MpSceneRule { get; set; } = "all";

    /// <summary>The debug options of the next game (docs/DEBUG.md), and whether they are used.</summary>
    [JsonPropertyName("debug")] public DebugOptions Debug { get; set; } = new();
    [JsonPropertyName("useDebug")] public bool UseDebug { get; set; }

    // game (the Options window's "Gameplay" and "Controls" tabs: game/src/UI/OptionsPanel.cs)
    [JsonPropertyName("gameSpeed")] public double GameSpeed { get; set; } = 1.0;
    [JsonPropertyName("incomePerMinute")] public bool IncomePerMinute { get; set; }
    /// <summary>"Enable autosave" (OptionsMenu.usl) every AutosaveMinutes of real time (0 in older settings: off)</summary>
    [JsonPropertyName("autosave")] public bool Autosave { get; set; } = true;
    [JsonPropertyName("autosaveMinutes")] public int AutosaveMinutes { get; set; } = 10;
    [JsonPropertyName("mentorTexts")] public bool MentorTexts { get; set; } = true;
    /// <summary>the mission's objectives panel (info bar and timers, upper right) folded away: its toggle button or
    /// Shift+J (game/src/UI/Mission/HudViews.cs TopStackView)</summary>
    [JsonPropertyName("objectivesHidden")] public bool ObjectivesHidden { get; set; }
    /// <summary>the cutscenes' subtitles ("Show Subtitles")</summary>
    [JsonPropertyName("subtitles")] public bool Subtitles { get; set; } = true;
    /// <summary>"Cutscene black bars": the in-engine cutscenes letterboxed as the original (its wide-screen filter over
    /// the picture). Off (default, the user's choice): full screen, the subtitles over the picture (CutsceneView)</summary>
    [JsonPropertyName("cutsceneBars")] public bool CutsceneBars { get; set; } = false;
    /// <summary>the start films at start-up (sf_logo, sek_logo, partner, intro: IntroScreen; the original's
    /// Global/PlayIntro); --no-intro skips them once</summary>
    [JsonPropertyName("introFilms")] public bool IntroFilms { get; set; } = true;
    /// <summary>"Rotate Minimap": the minimap turns with the camera (the browser remake's default since the user asked)</summary>
    [JsonPropertyName("minimapRotate")] public bool MinimapRotate { get; set; } = true;
    [JsonPropertyName("minimapPlayerColors")] public bool MinimapPlayerColors { get; set; } = true;
    /// <summary>colour of the health bars over units and buildings (as the original's "alternative hitpoint bar
    /// colouring", OptionsMenu.usl AlternativeColoringPFHP): "health" = the health gradient red .. green of
    /// Effects/Selection.tga (default), "player" = the owner's colour (wild animals: their circle colour)</summary>
    [JsonPropertyName("hpBarColors")] public string HpBarColors { get; set; } = "health";
    /// <summary>the build / produce menu opens when workers or a building are selected (settings.autoMenu)</summary>
    [JsonPropertyName("autoMenu")] public bool AutoMenu { get; set; } = true;
    /// <summary>tooltips next to what the mouse is over (off: the original window above the minimap)</summary>
    [JsonPropertyName("tipNear")] public bool TipNear { get; set; } = true;
    /// <summary>"Show attack ranges": a faint circle round each of the player's own finished shooting buildings (towers,
    /// turrets, cannons, bunkers, nests: View/Entities/RangeRings.cs); Ctrl+Shift+R toggles it in the game. A selected or
    /// hovered one and the building being placed show theirs either way. Also the range markers of the selected objects
    /// (MIRAGE's CRangeMarkerMgr: View/Entities/RangeMarkers.cs).</summary>
    [JsonPropertyName("showRanges")] public bool ShowRanges { get; set; } = true;
    /// <summary>the camera scrolls when the mouse is at the edge of the screen</summary>
    [JsonPropertyName("edgeScroll")] public bool EdgeScroll { get; set; } = true;

    // graphics
    [JsonPropertyName("fpsCap")] public int FpsCap { get; set; } = 0;
    [JsonPropertyName("vsync")] public bool VSync { get; set; } = true;
    [JsonPropertyName("shadows")] public string Shadows { get; set; } = "on";
    [JsonPropertyName("grass")] public string Grass { get; set; } = "on";
    [JsonPropertyName("particles")] public string Particles { get; set; } = "on";
    [JsonPropertyName("renderScale")] public double RenderScale { get; set; } = 1.0;
    [JsonPropertyName("uiScale")] public double UiScale { get; set; } = 0;
    [JsonPropertyName("weather")] public bool Weather { get; set; } = true;
    /// <summary>FXAA (WorldRenderer: not MSAA, which breaks the fog of war's depth pass)</summary>
    [JsonPropertyName("antialias")] public bool Antialias { get; set; } = true;
    /// <summary>"Original colours (no player colours)": no model tinted with its owner's colour, the SEAS' paint red
    /// (ModelMaterials.OriginalColours; applied at once)</summary>
    [JsonPropertyName("originalColors")] public bool OriginalColors { get; set; }
    /// <summary>"Show units behind buildings": a unit covered by a building drawn over it as a flat silhouette in its
    /// owner's colour (UnitsBehind; the original's "Show Flatshades", off by default there)</summary>
    [JsonPropertyName("showUnitsBehind")] public bool ShowUnitsBehind { get; set; } = true;
    /// <summary>"Setting textures on buildings": buildings, walls and built scenery in the original's textures of the
    /// setting (biome) they stand in - snowy in the Icewaste, sandy in the Savanna ... (SettingMaterials), and the shader
    /// snow on those without an Icewaste texture (SnowCover); off: plain textures, no snow. Applied at once.
    /// (Replaces "snowOnBuildings", 2026-11-11.)</summary>
    [JsonPropertyName("settingTextures")] public bool SettingTextures { get; set; } = true;
    /// <summary>the performance line (F9) shown</summary>
    [JsonPropertyName("perf")] public bool Perf { get; set; }
    /// <summary>(older settings; read once into Window)</summary>
    [JsonPropertyName("fullscreen")] public bool Fullscreen { get; set; }
    /// <summary>The game window: "windowed", "maximized" (the default: the whole screen, with the task bar),
    /// "fullscreen" (borderless, the screen's own resolution) or "exclusive" (exclusive fullscreen). F11 / Alt+Enter
    /// switch between fullscreen and the last other mode.</summary>
    [JsonPropertyName("window")] public string Window { get; set; } = "";
    /// <summary>size of the window in "windowed" mode (0: the project's 1600 x 900)</summary>
    [JsonPropertyName("windowWidth")] public int WindowWidth { get; set; }
    [JsonPropertyName("windowHeight")] public int WindowHeight { get; set; }

    public static readonly string[] WindowModes = { "windowed", "maximized", "fullscreen", "exclusive" };
    public string WindowMode => System.Array.IndexOf(WindowModes, Window) >= 0 ? Window : Fullscreen ? "fullscreen" : "maximized";

    // sound (0..1); Sound off = the original's "Mute"
    [JsonPropertyName("sound")] public bool Sound { get; set; } = true;
    [JsonPropertyName("volMaster")] public double VolMaster { get; set; } = 0.8;
    [JsonPropertyName("volMusic")] public double VolMusic { get; set; } = 0.6;
    [JsonPropertyName("volEffects")] public double VolEffects { get; set; } = 0.8;
    [JsonPropertyName("volSpeech")] public double VolSpeech { get; set; } = 1.0;
    /// <summary>Unit sounds: what units say and do (acknowledgements, death cries, their animations' sounds)</summary>
    [JsonPropertyName("volUnits")] public double VolUnits { get; set; } = 0.8;
    [JsonPropertyName("volAmbience")] public double VolAmbience { get; set; } = 0.6;
    /// <summary>the interface's clicks and feedback ("UI Sound Volume")</summary>
    [JsonPropertyName("volUi")] public double VolUi { get; set; } = 0.8;

    /// <summary>The defaults of the Options window's "Restore default" (graphics) and "Reset" (volumes).</summary>
    public static readonly AppSettings Defaults = new();
    public void ResetGraphics()
    {
        var d = Defaults;
        Shadows = d.Shadows; Grass = d.Grass; Particles = d.Particles; Weather = d.Weather; RenderScale = d.RenderScale;
        Antialias = d.Antialias; FpsCap = d.FpsCap; VSync = d.VSync;
    }
    public void ResetVolumes()
    {
        var d = Defaults;
        VolMaster = d.VolMaster; VolMusic = d.VolMusic; VolEffects = d.VolEffects; VolSpeech = d.VolSpeech; VolUnits = d.VolUnits; VolAmbience = d.VolAmbience; VolUi = d.VolUi;
        Sound = true;
    }

    /// <summary>Keys this version does not know (kept when saving).</summary>
    [JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

    public static AppSettings Load()
    {
        var s = Json.Load<AppSettings>(AppPaths.SettingsFile) ?? new AppSettings();
        s.Debug ??= new DebugOptions();
        s.Skirmish ??= new(); s.ArmyPresets ??= new(); s.CampaignCredits ??= new(); s.CampaignGameplay ??= new();
        // (older settings: autosave off was 0 minutes)
        if (s.AutosaveMinutes <= 0) { s.Autosave = false; s.AutosaveMinutes = 10; }
        return s;
    }

    public void Save()
    {
        try { Json.Save(AppPaths.SettingsFile, this); }
        catch (System.Exception ex) { Log.For("App").Error("cannot save settings", ex); }
    }
}
