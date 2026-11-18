// The title screen. First choice: the game configuration - the official game (Norsemen, Dustriders, Dragon Clan) or
// MIRAGE (adds the SEAS as a playable tribe and the mod's rules). The choice is remembered (settings.json
// "configuration") and changes the menu art, the rules, the missions and the tribes offered after it.
// Then the main menu (menu.js title; captions and tooltips: MainMenu.srf's texts): Campaign, Skirmish, Load Game,
// Multiplayer (host / join: MultiplayerMenu, docs/MULTIPLAYER.md), Options (OptionsPanel), Help, Controls, Game data,
// Quit (docs/MENUS.md).
using System;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Content;

namespace PwRemake.UI;

public partial class TitleScreen : MenuScreen
{
    public TitleScreen() : base("ParaWorld", minWidth: 520) { }
    Label _note = null!;

    static string TribeNames(GameConfiguration c) => string.Join(", ", c.PlayableTribes.Select(TribeInfo.Name));

    protected override void Build()
    {
        var b = Window.Body;
        b.AddChild(PwTheme.Heading("ParaWorld", 26));
        b.AddChild(PwTheme.Para("Remake · Godot · " + GameApp.Version, 13, PwTheme.Dim, HorizontalAlignment.Center));

        var g = Group("Game");
        b.AddChild(g);
        var group = new ButtonGroup();
        foreach (var c in GameApp.Configurations)
        {
            var problems = c.Problems();
            var cb = new CheckBox
            {
                Text = $"{c.Title}  —  {TribeNames(c)}", ButtonGroup = group, ButtonPressed = c == GameApp.Config,
                Disabled = problems.Count > 0, FocusMode = FocusModeEnum.None,
                TooltipText = problems.Count > 0 ? string.Join("\n", problems) : (c.Description != "" ? c.Description : c.Data),
            };
            var cc = c;
            cb.Toggled += on =>
            {
                if (!on || GameApp.Config == cc) return;
                GameApp.UseConfiguration(cc);
                GameApp.Settings.Save();
                Boot.Instance.Show(new TitleScreen());       // the menu art and the texts may differ
            };
            g.AddChild(cb);
        }
        if (GameApp.Config != null && GameApp.Config.Problems().Count > 0)
            g.AddChild(PwTheme.Para(string.Join("\n", GameApp.Config.Problems()), 12, PwTheme.Lost));

        var btns = new VBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        btns.AddThemeConstantOverride("separation", 6);
        b.AddChild(new Control { CustomMinimumSize = new Vector2(0, 6) });
        b.AddChild(btns);
        bool ok = GameApp.Config != null && GameApp.Config.Problems().Count == 0;
        // the main menu's buttons (MainMenu.srf: their captions and tooltips from the game's texts)
        var tx = GameTexts.For(GameApp.Config);
        Button Add(string key, string fallback, string tt, Action go, bool enabled = true)
        {
            var b = PwTheme.Btn(tx.Get("_UI_MainMenu_Button_" + key, fallback), go);
            b.TooltipText = tt.Length > 0 ? tx.Get("_UI_MainMenu_Tooltip_" + tt, "") : "";
            b.Disabled = !enabled;
            btns.AddChild(Center(b));
            return b;
        }
        Add("StartCampaign", "Campaign", "StartCampaign", () => Boot.Instance.Show(new CampaignMenu()), ok);
        // Continue Campaign (MainMenu.usl Init / OnContinueCampaign): the newest single player save, loaded at once
        // (docs/SAVES.md); greyed out when there is none
        var last = PwRemake.Sim.Game.SaveGame.NewestCampaign(AppPaths.SavesDir);
        var cont = Add("ContinueCampaign", "Continue Campaign", "ContinueCampaign", () =>
        {
            if (last is { } l) PwRemake.App.GameSession.LoadGame(l.File, m => { _note.Text = m; _note.Visible = true; });
        }, last != null);
        if (last is { } ls) cont.TooltipText = (cont.TooltipText.Length > 0 ? cont.TooltipText + "\n" : "") + $"{ls.Header.Name} · {ls.Header.Title}";
        _note = PwTheme.Para("", 12, PwTheme.Lost);
        _note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _note.CustomMinimumSize = new Vector2(440, 0);
        _note.Visible = false;
        btns.AddChild(_note);
        // the remake's own campaigns (docs/SEQUEL.md): Containment is played with MIRAGE (its menu offers the switch)
        btns.AddChild(Center(PwTheme.Btn("Containment (1964)", () => Boot.Instance.Show(new Sequel.SequelMenu()))));
        btns.AddChild(Center(PwTheme.Btn("Scenarios", () => Boot.Instance.Show(new Sequel.ScenarioMenu()))));
        Add("Skirmish", "Skirmish", "Skirmish", () => Boot.Instance.Show(new SkirmishMenu()), ok);
        Add("Multiplayer", "Multiplayer", "Multiplayer", () => Boot.Instance.Show(new MultiplayerMenu()), ok);   // (docs/MULTIPLAYER.md)
        Add("LoadGame", "Load game", "LoadGame", () => Boot.Instance.Show(new LoadGameScreen()));      // (SavesPanel.cs; docs/SAVES.md)
        Add("Options", "Options", "Options", () => Boot.Instance.Show(new OptionsScreen()));           // (OptionsPanel.cs)
        btns.AddChild(Center(PwTheme.Btn("Help", () => { Help.HelpWindow? h = null; h = new Help.HelpWindow(null, () => h!.QueueFree()); AddChild(h); })));   // the game's help book (game/src/UI/Help)
        btns.AddChild(Center(PwTheme.Btn("Controls", () => Boot.Instance.Show(new ControlsScreen()))));  // (the keys: Help/ControlsList.cs)
        btns.AddChild(Center(PwTheme.Btn("Game data …", () => Boot.Instance.Show(new SetupScreen()))));
        Add("Quit", "Exit game", "Quit", () => Boot.Instance.Quit());
    }

    static CenterContainer Center(Control c) { var cc = new CenterContainer(); cc.AddChild(c); return cc; }
}

/// <summary>Names and menu icons of the tribes (the icon row in menue__decoration__menu_tribeselecticons.png).</summary>
/// <summary>The keys and the mouse (menu.js help: "Controls" on the title screen; in the game: the pause menu's).</summary>
public partial class ControlsScreen : MenuScreen
{
    public ControlsScreen() : base("Controls", minWidth: 760) { }
    protected override void Build()
    {
        Help.ControlsList.Fill(Window.Body);
        var c = new CenterContainer();
        c.AddChild(PwTheme.Btn("Back", () => Boot.Instance.Show(new TitleScreen()), 160));
        Window.Body.AddChild(c);
    }
}

public static class TribeInfo
{
    public static readonly string[] All = { "Hu", "Aje", "Ninigi", "SEAS" };

    public static string Name(string tribe) => tribe switch
    {
        "Hu" => "Norsemen", "Aje" => "Dustriders", "Ninigi" => "Dragon Clan", "SEAS" => "SEAS", _ => tribe,
    };

    /// <summary>Row of the tribe in the 21 px icon strip (the browser remake's TRIBE_INFO).</summary>
    /// (the strip: Dustriders, Dragon Clan, Norsemen, SEAS, "?" - the browser remake had the first three swapped)
    public static int IconRow(string tribe) => tribe switch { "Aje" => 0, "Ninigi" => 1, "Hu" => 2, "SEAS" => 3, _ => 4 };

    public static Texture2D? Icon(string tribe)
    {
        var strip = PwTheme.Tex("decoration__menu_tribeselecticons");
        if (strip == null) return null;
        return new AtlasTexture { Atlas = strip, Region = new Rect2(0, IconRow(tribe) * 21, 38, 21) };
    }
}
