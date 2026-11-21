// The map editor's dialogs, in the game's windows (PwWindow over the darkened editor): New map (name, size from the
// original maps' sizes, setting from the converted data, ground height, water level, players), Open map (My maps - the
// user's maps folder with its .pwmap files -, the installation's multiplayer maps and the remake's maps, all maps of the
// installation, the campaign missions; Import .pwmap), Save as (a name in My maps), the file pickers of Export / Import
// .pwmap, and the question before unsaved changes are lost (Save / Discard / Cancel).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Editor;
using PwRemake.UI;

namespace PwRemake.Editor;

public partial class EditorDialog : Control
{
    public PwWindow Window { get; }

    EditorDialog(string caption, float width)
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        Theme = PwTheme.Theme;
        MouseFilter = MouseFilterEnum.Stop;
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.5f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        var center = new CenterContainer { AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(center);
        Window = new PwWindow(caption, width);
        center.AddChild(Window);
    }

    public void Close() => QueueFree();

    static HBoxContainer Row(string label, Control c, float labelWidth = 150)
    {
        var h = new HBoxContainer();
        h.AddThemeConstantOverride("separation", 10);
        h.AddChild(new Label { Text = label, CustomMinimumSize = new Vector2(labelWidth, 0) });
        c.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        h.AddChild(c);
        return h;
    }

    static HBoxContainer Buttons(params Button[] bs)
    {
        var h = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        h.AddThemeConstantOverride("separation", 12);
        foreach (var b in bs) h.AddChild(b);
        return h;
    }

    static SpinBox Spin(double min, double max, double step, double value, string suffix = "") =>
        new() { MinValue = min, MaxValue = max, Step = step, Value = value, Suffix = suffix, CustomMinimumSize = new Vector2(120, 0) };

    /// <summary>"New map": the spec of a flat map (ok gets it)</summary>
    public static EditorDialog NewMap(Action<NewMapSpec> ok)
    {
        var d = new EditorDialog("New map", 480);
        var b = d.Window.Body;
        var name = new LineEdit { Text = "New map", PlaceholderText = "the map's name" };
        b.AddChild(Row("Name", name));
        var size = new OptionButton();
        var sizes = EditorSession.Sizes();
        foreach (var s in sizes) size.AddItem($"{s} x {s} m", s);
        size.Select(Math.Max(0, sizes.FindIndex(s => s >= 512)));
        b.AddChild(Row("Size", size));
        var setting = new OptionButton();
        var sets = EditorSession.Settings();
        foreach (var s in sets) setting.AddItem(s);
        setting.Select(Math.Max(0, sets.IndexOf("Jungle")));
        b.AddChild(Row("Setting", setting));
        var ground = Spin(0, 200, 0.5, 20, " m");
        b.AddChild(Row("Ground height", ground));
        var water = Spin(0, 200, 0.5, 16, " m");
        var noWater = new CheckBox { Text = "no water" };
        noWater.Toggled += on => water.Editable = !on;
        var wrow = new HBoxContainer(); wrow.AddThemeConstantOverride("separation", 10);
        water.SizeFlagsHorizontal = SizeFlags.ExpandFill; wrow.AddChild(water); wrow.AddChild(noWater);
        b.AddChild(Row("Water level", wrow));
        var players = Spin(2, 16, 1, 2);
        b.AddChild(Row("Start locations", players));
        b.AddChild(PwTheme.Para("A flat map of the setting's first ground material; the start locations on a circle round the centre (they are moved in a later phase).", 12));
        b.AddChild(Buttons(
            PwTheme.Btn("Create", () =>
            {
                var spec = new NewMapSpec
                {
                    Name = name.Text.Trim().Length > 0 ? name.Text.Trim() : "New map", Size = size.GetSelectedId(), Setting = sets[Math.Max(0, setting.Selected)],
                    BaseHeight = ground.Value, Water = noWater.ButtonPressed ? null : water.Value, Players = (int)players.Value,
                };
                d.Close();
                ok(spec);
            }, 150),
            PwTheme.Btn("Cancel", d.Close, 150)));
        return d;
    }

    /// <summary>"Open map": the maps of a source (ok gets the folder or file); import: "Import .pwmap ..."</summary>
    public static EditorDialog OpenMap(Action<string> ok, Action? import = null)
    {
        var d = new EditorDialog("Open map", 680);
        var b = d.Window.Body;
        var source = new OptionButton();
        foreach (var k in EditorSession.OpenSources) source.AddItem(k);
        source.Select(Math.Clamp(EditorSession.OpenSource, 0, EditorSession.OpenSources.Length - 1));
        b.AddChild(Row("Show", source, 80));
        var list = new ItemList { CustomMinimumSize = new Vector2(620, 380), SelectMode = ItemList.SelectModeEnum.Single };
        var info = PwTheme.Para("", 12);
        List<EditorSession.OpenItem> maps = new();
        void Fill()
        {
            list.Clear();
            EditorSession.OpenSource = source.Selected;
            try { maps = EditorSession.OpenList(source.Selected); }
            catch (Exception e) { info.Text = "The map list could not be read: " + e.Message; maps = new(); }
            foreach (var m in maps)
            {
                int i = list.AddItem(m.Label);
                // (a map for another configuration - map.lua requires - greyed, with the reason)
                if (m.Unfit != null) { list.SetItemDisabled(i, true); list.SetItemCustomFgColor(i, new Color(0.55f, 0.55f, 0.55f)); list.SetItemTooltip(i, "This map " + m.Unfit + ": open it with that configuration (title screen)."); }
            }
            int first = maps.FindIndex(m => m.Unfit == null);
            if (first >= 0) list.Select(first);
            info.Text = maps.Count == 0
                ? (source.Selected == 0 ? $"No maps of your own yet: Save puts them into {EditorSession.UserMapsDir}." : "No maps found.")
                : $"{maps.Count} maps{(maps.Count(m => m.Unfit != null) is int nu and > 0 ? $" ({nu} greyed: for another configuration - map.lua requires)" : "")}. " + (source.Selected == 0 ? "Save writes them where they are." : "Save writes a copy into My maps (the installation's maps stay as they are).");
        }
        source.ItemSelected += _ => Fill();
        b.AddChild(list);
        b.AddChild(info);
        void Go()
        {
            var sel = list.GetSelectedItems();
            if (sel.Length == 0 || sel[0] >= maps.Count || maps[sel[0]].Unfit != null) return;
            d.Close();
            ok(maps[sel[0]].Path);
        }
        list.ItemActivated += _ => Go();
        var btns = new List<Button> { PwTheme.Btn("Open", Go, 150) };
        if (import != null) btns.Add(PwTheme.Btn("Import .pwmap …", () => { d.Close(); import(); }, 170));
        btns.Add(PwTheme.Btn("Cancel", d.Close, 150));
        b.AddChild(Buttons(btns.ToArray()));
        Fill();
        return d;
    }

    /// <summary>"Save as": a name for the map in My maps (ok gets the folder); a name taken asks before it is replaced</summary>
    public static EditorDialog SaveAs(string name, Action<string> ok, Action<EditorDialog> show)
    {
        var d = new EditorDialog("Save as", 520);
        var b = d.Window.Body;
        var field = new LineEdit { Text = EditorSession.SafeName(name), PlaceholderText = "the map's folder name" };
        b.AddChild(Row("Name", field, 80));
        var where = PwTheme.Para("", 12);
        void Where() => where.Text = $"Saved as the folder {System.IO.Path.Combine(EditorSession.UserMapsDir, EditorSession.SafeName(field.Text))} - listed in the skirmish and multiplayer setup under \"My maps\".";
        field.TextChanged += _ => Where();
        Where();
        b.AddChild(where);
        void Go()
        {
            var n = EditorSession.SafeName(field.Text);
            if (n.Length == 0) return;
            var path = System.IO.Path.Combine(EditorSession.UserMapsDir, n);
            d.Close();
            if (System.IO.Directory.Exists(path) || System.IO.File.Exists(path))
                show(Confirm("Replace the map?", $"My maps has a map \"{n}\" already. Replace it?", "Replace", () => ok(path)));
            else ok(path);
        }
        field.TextSubmitted += _ => Go();
        b.AddChild(Buttons(PwTheme.Btn("Save", Go, 150), PwTheme.Btn("Cancel", d.Close, 150)));
        field.CallDeferred(Control.MethodName.GrabFocus);
        return d;
    }

    /// <summary>a question with a button that does it and Cancel</summary>
    public static EditorDialog Confirm(string caption, string text, string yes, Action go)
    {
        var d = new EditorDialog(caption, 460);
        d.Window.Body.AddChild(PwTheme.Para(text, 14, PwTheme.Text));
        d.Window.Body.AddChild(Buttons(PwTheme.Btn(yes, () => { d.Close(); go(); }, 160), PwTheme.Btn("Cancel", d.Close, 140)));
        return d;
    }

    /// <summary>a file picker for .pwmap files (save: Export; else: Import) - the system's own dialog where there is one</summary>
    public static FileDialog PwmapFile(bool save, string dir, string file, Action<string> ok)
    {
        var fd = new FileDialog
        {
            FileMode = save ? FileDialog.FileModeEnum.SaveFile : FileDialog.FileModeEnum.OpenFile, Access = FileDialog.AccessEnum.Filesystem,
            Filters = new[] { "*.pwmap ; ParaWorld Remake map" }, Title = save ? "Export the map as .pwmap" : "Import a .pwmap map",
            CurrentDir = dir, CurrentFile = file, UseNativeDialog = true, Size = new Vector2I(900, 600),
        };
        fd.FileSelected += p => { fd.QueueFree(); ok(p); };
        fd.Canceled += fd.QueueFree;
        return fd;
    }

    /// <summary>"The map has unsaved changes": save first (save: null when the map has no place of its own - then "Save as"
    /// comes first), go on (losing them) or stay</summary>
    public static EditorDialog Unsaved(string what, Action discard, Action? save = null)
    {
        var d = new EditorDialog("Unsaved changes", 520);
        var b = d.Window.Body;
        b.AddChild(PwTheme.Para($"The map has changes that are not saved. {what} anyway? The changes are lost.", 14, PwTheme.Text));
        var bs = new List<Button>();
        if (save != null) bs.Add(PwTheme.Btn("Save first", () => { d.Close(); save(); }, 140));
        bs.Add(PwTheme.Btn("Discard changes", () => { d.Close(); discard(); }, 180));
        bs.Add(PwTheme.Btn("Cancel", d.Close, 120));
        b.AddChild(Buttons(bs.ToArray()));
        return d;
    }

    /// <summary>a message with OK</summary>
    public static EditorDialog Message(string caption, string text)
    {
        var d = new EditorDialog(caption, 440);
        d.Window.Body.AddChild(PwTheme.Para(text, 14, PwTheme.Text));
        d.Window.Body.AddChild(Buttons(PwTheme.Btn("OK", d.Close, 140)));
        return d;
    }
}

/// <summary>The question before the game quits with unsaved changes of the map editor's map (Boot.RequestQuit): Save
/// (when the map has its own place in My maps), back to the editor (to Save as), quit without saving, cancel.</summary>
public static class EditorQuit
{
    public static Control Dialog(Action quit, Action toEditor, Action cancel)
    {
        var doc = EditorSession.Doc!;
        var d = EditorDialog.Message("Unsaved changes", $"The map \"{doc.Name}\" in the map editor has changes that are not saved. Quit anyway? The changes are lost.");
        var body = d.Window.Body;
        body.RemoveChild(body.GetChild(body.GetChildCount() - 1));          // (Message's OK)
        var h = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        h.AddThemeConstantOverride("separation", 12);
        if (EditorSession.SavesInPlace(doc))
            h.AddChild(PwTheme.Btn("Save and quit", () =>
            {
                try { EditorSession.Save(doc, doc.SourcePath!); EditorSession.History!.MarkSaved(); quit(); }
                catch (Exception e) { PwRemake.Sim.Core.Log.For("Editor").Error("save before quitting", e); toEditor(); }
            }, 160));
        else h.AddChild(PwTheme.Btn("Back to the editor", toEditor, 180));
        h.AddChild(PwTheme.Btn("Quit without saving", quit, 190));
        h.AddChild(PwTheme.Btn("Cancel", cancel, 120));
        body.AddChild(h);
        return d;
    }
}
