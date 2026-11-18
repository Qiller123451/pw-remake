// The game's menu (Esc; ui/menu.js main / settings / confirm, in the original's look: PwWindow + PwTheme): resume, the
// debug tools (a game with debug options), the Options window (OptionsPanel: the same as the title screen's, applied at
// once, kept in settings.json), restart the mission, quit to the campaign menu, exit the game. The game stands still while it is
// open (GameSession.MenuOpen). Save game / Load game (ui/menu.js saveWin / loadWin; docs/SAVES.md): a name and the
// saves (picking one replaces it), or the saves to load from (asked first: what is not saved is lost).
// Ported from remake/src/ui/menu.js (main, settings, confirm) (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using Godot;
using PwRemake.App;
using PwRemake.View.Audio;

namespace PwRemake.UI.Game;

public partial class PauseMenu : CanvasLayer
{
    readonly GameSession S;
    Control _root = null!;
    Control? _win;

    public PauseMenu(GameSession s) { S = s; Name = "PauseMenu"; Layer = 30; ProcessMode = ProcessModeEnum.Always; }

    public override void _Ready()
    {
        _root = new Control { Theme = PwTheme.Theme, MouseFilter = Control.MouseFilterEnum.Stop };
        _root.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        _root.AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.45f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = Control.MouseFilterEnum.Ignore });
        AddChild(_root);
        Main();
    }

    /// <summary>shows a window in the middle of the screen (the one before goes)</summary>
    public PwWindow Open(string caption, float width = 420)
    {
        _win?.QueueFree();
        var c = new CenterContainer { MouseFilter = Control.MouseFilterEnum.Ignore };
        c.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        var w = new PwWindow(caption, width);
        c.AddChild(w);
        _root.AddChild(c);
        _win = c;
        return w;
    }

    public void Close() => S.CloseMenu();

    /// <summary>the help book (game/src/UI/Help) in place of the menu's window; back: what Close / Esc do</summary>
    public void Help(string? page, Action back)
    {
        _win?.QueueFree();
        var h = new PwRemake.UI.Help.HelpWindow(page, back);
        _root.AddChild(h);
        _win = h;
    }

    public void Main()
    {
        var w = Open("Menu", 360);
        var b = w.Body;
        b.AddChild(PwTheme.Heading(S.Sim.Title, 18));
        bool sk = S.Skirmish != null;
        b.AddChild(PwTheme.Btn("Resume game", Close));
        if (S.World.Cheats != null || S.Start.Debug != null) b.AddChild(PwTheme.Btn("Debug tools", () => new DebugTools(S, this).Show()));
        bool net = S.Net != null;                   // (a networked game: the game runs on; saving is the host's, no loading, no restart)
        if (net) b.AddChild(PwTheme.Para("Network game: the game goes on while this menu is open.", 12, PwTheme.Dim, HorizontalAlignment.Center));
        var save = PwTheme.Btn("Save game", SaveWin);
        if (net && S.CannotSave() is { } why) { save.Disabled = true; save.TooltipText = why; }
        b.AddChild(save);
        if (!net) b.AddChild(PwTheme.Btn("Load game", LoadWin));
        b.AddChild(PwTheme.Btn("Options", () => Options()));
        b.AddChild(PwTheme.Btn("Help", () => Help(null, Main)));
        b.AddChild(PwTheme.Btn("Controls", () => { var w = Open("Controls", 760); PwRemake.UI.Help.ControlsList.Fill(w.Body); w.Body.AddChild(PwTheme.Btn("Back", Main)); }));
        if (!net) b.AddChild(PwTheme.Btn(sk ? "Restart skirmish" : "Restart mission", () => Confirm(sk ? "Restart the skirmish?" : "Restart the mission?", () => S.EndAction("retry"))));
        var to = net ? "the multiplayer menu (leave the game)" : sk ? "the skirmish menu" : "the campaign menu";
        b.AddChild(PwTheme.Btn("Quit to " + to, () => Confirm($"Quit to {to}?", () => S.EndAction("menu"))));
        b.AddChild(PwTheme.Btn("Exit game", () => Confirm("Exit the game?", () => S.EndAction("exit"))));
        var v = PwTheme.Para($"ParaWorld Remake {GameApp.Version} · {GameApp.Config?.Title} · {(sk ? $"skirmish · {S.World.Opts.Mode}" : $"mission {S.Mission!.Data.Id} · difficulty {new[] { "easy", "medium", "hard", "nightmare" }[Math.Clamp(S.Start.Difficulty, 0, 3)]}")}", 11);
        b.AddChild(v);
    }

    void Confirm(string q, Action yes) => Confirm(q, yes, Main);
    void Confirm(string q, Action yes, Action no)
    {
        var w = Open("", 360);
        w.Body.AddChild(PwTheme.Heading(q, 18));
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddChild(PwTheme.Btn("Yes", yes, 120));
        row.AddChild(PwTheme.Btn("No", no, 120));
        w.Body.AddChild(row);
    }

    // ---------------------------------------------------------------- saved games (ui/menu.js saveWin / loadWin)
    public void SaveWin()
    {
        var w = Open("Save game", 660);
        var b = w.Body;
        var why = S.CannotSave();
        var name = new LineEdit { Text = S.SaveTitle(), MaxLength = 60, CustomMinimumSize = new Vector2(400, 0), SelectAllOnFocus = true };
        var row = new HBoxContainer();
        row.AddChild(new Label { Text = "Name", CustomMinimumSize = new Vector2(80, 0) });
        row.AddChild(name);
        b.AddChild(row);
        var panel = new SavesPanel();
        string? over = null;           // the save picked in the list: saving replaces it
        var note = PwTheme.Para(why ?? "", 13, PwTheme.Lost);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        void Save()
        {
            if (why != null) return;
            try
            {
                var n = name.Text.Trim().Length > 0 ? name.Text.Trim() : S.SaveTitle();
                // a pick of a quick save / autosave is not replaced: a new file of the name
                S.SaveGameNow(n, "manual", over != null && System.IO.Path.GetFileName(over) is not (GameSession.QuickFile or GameSession.AutoFile) ? over : null);
                Close();
                S.Message($"Saved \"{n}\"", "good");
            }
            catch (Exception e) { note.Text = "The game could not be saved: " + e.Message; }
        }
        panel.Picked = (f, h, dbl) => { over = f; name.Text = h.Name; if (dbl) Save(); };
        name.TextChanged += t => { if (over != null && panel.SelectedHeader?.Name != t) over = null; };
        name.TextSubmitted += _ => Save();
        b.AddChild(panel);
        b.AddChild(note);
        var r = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        r.AddChild(PwTheme.Btn("Back", Main, 140));
        var sv = PwTheme.Btn("Save", Save, 140); sv.Disabled = why != null;
        r.AddChild(sv);
        b.AddChild(r);
        name.CallDeferred(Control.MethodName.GrabFocus);
    }

    public void LoadWin()
    {
        var w = Open("Load game", 660);
        var b = w.Body;
        var panel = new SavesPanel(selectFirst: true);
        var note = PwTheme.Para("", 13, PwTheme.Lost);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        void Load()
        {
            var f = panel.Selected;
            if (f == null) return;
            Confirm("Load this game? What is not saved of the running game is lost.", () => { if (!GameSession.LoadGame(f, m => S.Message(m, "bad"))) LoadWin(); }, LoadWin);
        }
        panel.Picked = (f, h, dbl) => { note.Text = ""; if (dbl) Load(); };
        b.AddChild(panel);
        b.AddChild(note);
        var r = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        r.AddChild(PwTheme.Btn("Back", Main, 140));
        r.AddChild(PwTheme.Btn("Delete", () => { if (panel.Selected is { } f) Confirm($"Delete \"{panel.SelectedHeader?.Name}\"?", () => { try { System.IO.File.Delete(f); } catch (Exception) { } LoadWin(); }, LoadWin); }, 140));
        r.AddChild(PwTheme.Btn("Load", Load, 140));
        b.AddChild(r);
    }

    // ---------------------------------------------------------------- options (ui/menu.js settings: game/src/UI/OptionsPanel.cs)
    public void Options(int? tab = null)
    {
        var w = Open(OptionsPanel.Title, 680);
        w.Body.AddChild(new OptionsPanel(Main, tab));
    }

    public override void _Input(InputEvent e)
    {
        if (e is InputEventKey { Pressed: true, Echo: false } k && k.Keycode == Key.Escape)
        {
            GetViewport()?.SetInputAsHandled();   // first: closing can take the menu out of the tree
            Close();
        }
    }
}
