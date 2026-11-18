// The list of saved games (ui/menu.js saveList: one row per save - name, what, game time, date; a click picks, a
// double click takes it), in the original's look, with the save's thumbnail and its header (mission, configuration,
// tribe, difficulty, game time, date) under the list. Used by the pause menu's Save / Load windows and the title
// screen's Load window (LoadGameScreen). Saves of another format version are listed greyed out with the reason.
// Ported from remake/src/ui/menu.js (saveList, saveWin, loadWin) (pw-exporter 98c7703); the saves: docs/SAVES.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Game;

namespace PwRemake.UI;

public partial class SavesPanel : VBoxContainer
{
    readonly ItemList _list;
    readonly TextureRect _thumb;
    readonly Label _info;
    List<(string File, SaveHeader? Header, string? Problem)> _saves = new();
    /// <summary>a save was picked (file, header; double: a double click)</summary>
    public Action<string, SaveHeader, bool>? Picked;
    public string? Selected { get; private set; }
    public SaveHeader? SelectedHeader { get; private set; }

    /// <summary>the newest save is chosen when the list is filled (the load windows)</summary>
    readonly bool _selectFirst;

    public SavesPanel(float width = 600, float height = 260, bool selectFirst = false)
    {
        _selectFirst = selectFirst;
        AddThemeConstantOverride("separation", 6);
        _list = new ItemList { CustomMinimumSize = new Vector2(width, height), FixedIconSize = new Vector2I(64, 36), IconMode = ItemList.IconModeEnum.Left, SelectMode = ItemList.SelectModeEnum.Single };
        AddChild(_list);
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 10);
        _thumb = new TextureRect { CustomMinimumSize = new Vector2(160, 90), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered };
        row.AddChild(_thumb);
        _info = PwTheme.Para("", 12);
        _info.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _info.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(_info);
        AddChild(row);
        _list.ItemSelected += i => Pick((int)i, false);
        _list.ItemActivated += i => Pick((int)i, true);
        Fill();
    }

    static string Difficulty(int d) => new[] { "easy", "medium", "hard", "nightmare" }[Math.Clamp(d, 0, 3)];
    static string Tribe(string t) => t.Length == 0 ? "the mission's tribe" : TribeInfo.Name(t);

    /// <summary>(re)reads the saves folder, newest first</summary>
    public void Fill()
    {
        _list.Clear();
        Selected = null; SelectedHeader = null;
        _thumb.Texture = null;
        _saves = SaveGame.List(AppPaths.SavesDir);
        foreach (var (file, h, problem) in _saves)
        {
            if (h == null)
            {
                int k = _list.AddItem($"{Path.GetFileNameWithoutExtension(file)} - {problem}");
                _list.SetItemDisabled(k, true);
                _list.SetItemTooltip(k, problem ?? "");
                continue;
            }
            var when = h.When == DateTime.MinValue ? "" : h.When.ToString("yyyy-MM-dd HH:mm");
            int i = _list.AddItem($"{h.Name}  ·  {h.Title}  ·  {SaveHeader.Clock(h.GameTime)}  ·  {when}", Thumb(file));
            _list.SetItemTooltip(i, Describe(h));
        }
        _info.Text = _saves.Count == 0 ? "No saved games yet." : "";
        int first = _saves.FindIndex(x => x.Header != null);
        if (_selectFirst && first >= 0) { _list.Select(first); Pick(first, false, false); }
    }

    static Texture2D? Thumb(string file)
    {
        try
        {
            var (_, bytes) = SaveGame.ReadHeader(file, true);
            if (bytes == null || bytes.Length == 0) return null;
            var img = new Image();
            return img.LoadJpgFromBuffer(bytes) == Error.Ok ? ImageTexture.CreateFromImage(img) : null;
        }
        catch (Exception) { return null; }
    }

    static string Describe(SaveHeader h)
    {
        var cfg = h.ConfigurationTitle.Length > 0 ? h.ConfigurationTitle : h.Configuration;
        return $"{h.Name}\n{h.Title} ({cfg}{(h.Mode != "Campaign" ? ", " + h.Mode : "")})\n{Tribe(h.Tribe)}, {Difficulty(h.Difficulty)}{(h.Debug ? ", debug options" : "")}\n" +
               $"game time {SaveHeader.Clock(h.GameTime)} · saved {(h.When == DateTime.MinValue ? "?" : h.When.ToString("yyyy-MM-dd HH:mm"))}" +
               (h.Kind != "manual" ? $" ({(h.Kind == "quick" ? "quick save" : "autosave")})" : "") + (h.Build != GameApp.Version ? $"\nsaved by version {h.Build}" : "");
    }

    void Pick(int i, bool dbl, bool notify = true)
    {
        if (i < 0 || i >= _saves.Count || _saves[i].Header == null) return;
        Selected = _saves[i].File; SelectedHeader = _saves[i].Header;
        _info.Text = Describe(SelectedHeader!);
        _thumb.Texture = _list.GetItemIcon(i);
        if (notify) Picked?.Invoke(Selected, SelectedHeader!, dbl);
    }

    /// <summary>deletes the selected save (the caller asked first)</summary>
    public void DeleteSelected()
    {
        if (Selected == null) return;
        try { File.Delete(Selected); } catch (Exception e) { _info.Text = "Not deleted: " + e.Message; return; }
        Fill();
    }
}

/// <summary>The title screen's "Load game": the saves, load or delete one (ui/menu.js loadWin outside a game).</summary>
public partial class LoadGameScreen : MenuScreen
{
    public LoadGameScreen() : base("Load game", minWidth: 660) { }

    protected override void Build()
    {
        var b = Window.Body;
        var panel = new SavesPanel(selectFirst: true);
        var note = PwTheme.Para("", 13, PwTheme.Lost);
        note.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        bool armed = false;
        void Load() { if (panel.Selected != null) GameSession.LoadGame(panel.Selected, m => note.Text = m); }
        panel.Picked = (f, h, dbl) => { note.Text = ""; armed = false; if (dbl) Load(); };
        b.AddChild(panel);
        b.AddChild(note);
        var del = PwTheme.Btn("Delete", () => { }, 140);
        del.Pressed += () =>
        {
            if (panel.Selected == null) return;
            if (!armed) { armed = true; note.Text = $"Press Delete again to delete \"{panel.SelectedHeader?.Name}\"."; return; }
            armed = false; note.Text = ""; panel.DeleteSelected();
        };
        b.AddChild(Row(PwTheme.Btn("Back", () => Boot.Instance.Show(new TitleScreen()), 140), del, PwTheme.Btn("Load", Load, 140)));
    }
}
