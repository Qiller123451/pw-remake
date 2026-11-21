// The map editor's trigger editor (docs/MAP_EDITOR.md "Triggers", plan phase 6): a window over the map with
//   left     the trigger tree: folders (paths), the triggers in them, "custom script" blocks (Lua the editor keeps as
//            text) - new trigger, new folder, duplicate, delete, up / down
//   right    the chosen trigger: name, folder, description, the flags (on at the start, once, one random action, by
//            difficulty, its folder starts off), the expression; its conditions and actions as lists (move, copy,
//            delete), "+ Condition" / "+ Action" pick a type from the catalogue (by category, with a search); the chosen
//            node's parameters in a form made from the type's parameter list and defaults: texts, numbers, choices,
//            players, and references - a region (a list, or Pick: click on the map), an object or a group (Pick: click
//            an object), a position (Pick), a variable, quest, trigger, folder, sequence, dialogue, text key (with its
//            text in the map's language); Script / Check / unknown nodes and code blocks in a code box (Lua syntax checked)
//   bottom   the problems (TriggerValidation): a click goes to the trigger and node
// custom.lua opens as text (a Lua syntax check before it is kept); the editor never writes it otherwise. Every change
// is a command of the history (undo / redo). The file is triggers.lua as TriggerScript writes it.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;
using PwRemake.App;
using PwRemake.Sim.Core;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Editor.Regions;
using PwRemake.Sim.Editor.Triggers;
using PwRemake.Sim.Game;
using PwRemake.Sim.Maps;
using PwRemake.UI;

namespace PwRemake.Editor;

public sealed class TriggerTool : EditorTool
{
    public override string Group => "Triggers";
    public override string Label => "Trigger editor";
    public override string Tip => "The map's triggers (triggers.lua): conditions and actions from the catalogue, references picked on the map";
    public override ToolCursor Cursor(ToolContext c, ToolPoint? p) => default;
    public override void Begin(ToolContext c, ToolPoint p) => MapEditor.Live?.TriggerPick(c, p);
    public override string Status(ToolContext c) => MapEditor.Live?.TriggerStatus() ?? "triggers";
    public override void BuildProps(ToolUi ui, ToolContext? c) { if (c != null && MapEditor.Live is { } ed) ed.BuildTriggerProps(ui); else ui.Note("Loading the map …"); }
}

public partial class MapEditor
{
    TriggerWindow? _trigWin;
    CanvasLayer? _trigLayer;
    static bool s_trigDemoDone;

    internal TriggerWindow OpenTriggers()
    {
        if (_trigWin == null || !IsInstanceValid(_trigWin))
        {
            _trigLayer ??= new CanvasLayer { Name = "TriggerEditor", Layer = 15 };
            if (_trigLayer.GetParent() == null) AddChild(_trigLayer);
            _trigWin = new TriggerWindow(this);
            _trigLayer.AddChild(_trigWin);
        }
        _trigWin.Visible = true;
        _trigWin.Rebuild();
        return _trigWin;
    }

    internal string TriggerStatus()
    {
        var m = Mis;
        if (_trigWin is { Picking: true } w) return "pick on the map: " + w.PickWhat + " (Esc: cancel)";
        return $"triggers: {m.Triggers.Triggers.Count()}, code blocks {m.Triggers.Items.OfType<EdCode>().Count()}";
    }

    internal void BuildTriggerProps(ToolUi ui)
    {
        var m = Mis;
        ui.Caption("Triggers");
        ui.Note($"{m.Triggers.Triggers.Count()} triggers in {m.Triggers.Folders().Count} folders, {m.Triggers.Items.OfType<EdCode>().Count()} custom script blocks{(m.Custom != null ? ", custom.lua" : "")}.");
        ui.Button("Open the trigger editor", () => OpenTriggers(), "The trigger tree, conditions, actions and their parameters", 240);
        var issues = TriggerValidation.CheckAll(m);         // (the trigger editor's check: the file loaded as the game loads it)
        int errs = issues.Count(i => i.Severity == Severity.Error);
        ui.Note(issues.Count == 0 ? "No problems found (the triggers load)." : $"{errs} errors, {issues.Count - errs} warnings (the trigger editor lists them).", 12);
        ui.Note(m.HasTriggers ? "Test map plays this map as a mission: its triggers run." : "Without triggers Test map plays a skirmish.", 12);
    }

    /// <summary>a click on the map with the trigger tool: a pick of the trigger editor</summary>
    internal void TriggerPick(ToolContext c, ToolPoint p)
    {
        if (_trigWin is not { Picking: true } w) { if (_trigWin == null || !_trigWin.Visible) c.Note("Open the trigger editor (properties panel)"); return; }
        w.Picked(c, p);
    }

    /// <summary>Test map of a map with triggers: written as a mission folder and played as a mission (its triggers
    /// run); false: the map has none (a skirmish as before)</summary>
    bool MissionTestMap()
    {
        if (!Doc.Mission.HasTriggers) return false;
        // the trigger editor's check (the file loaded as the game loads it): errors ask first - a mission whose triggers do
        // not load would start without them
        var errors = TriggerValidation.CheckAll(Doc.Mission).Where(x => x.Severity == Severity.Error).ToList();
        foreach (var i in errors.Take(10)) L.Warn("Test map: " + i);
        if (errors.Count > 0 && !_testDespiteErrors)
        {
            ShowDialog(EditorDialog.Confirm("Test map", $"The triggers have {errors.Count} error{(errors.Count == 1 ? "" : "s")}:\n{string.Join("\n", errors.Take(3).Select(e => "• " + e.Message))}" +
                $"{(errors.Count > 3 ? "\n…" : "")}\n\nThe trigger editor lists them (Check). Play the map anyway?", "Play anyway", () =>
                {
                    _testDespiteErrors = true;
                    try { TestMap(); } finally { _testDespiteErrors = false; }
                }));
            return true;
        }
        string folder;
        try
        {
            var rules = EditorSession.Rules;
            folder = TestMission.Write(Doc.Clone(), TestMission.NewFolder(), EditorSession.Config.PlayableTribes.ToList(),
                t => rules.StartOf(t)?.Units is { Count: > 0 } u ? u : new List<string> { t.ToLowerInvariant() + "_worker" });
        }
        catch (Exception e)
        {
            L.Error("Test map: the mission folder could not be written", e);
            ShowDialog(EditorDialog.Message("Test map", "The map could not be prepared as a mission: " + e.Message));
            return true;
        }
        var start = new GameStart { Configuration = EditorSession.Config.Id, Mode = GameMode.Campaign, Mission = 99, MissionFolder = folder, Difficulty = 1, Seed = 1 };
        L.Info($"Test map with triggers: {start}");
        EditorSession.Testing = true;
        Boot.Instance.Show(TestMissionLoading(start));
        return true;
    }

    /// <summary>Test map was asked to play the map although its triggers have errors (the question's "Play anyway")</summary>
    bool _testDespiteErrors;

    /// <summary>the loading screen of a Test map played as a mission</summary>
    public static LoadingScreen TestMissionLoading(GameStart start) =>
        new(start, new PwRemake.UI.MissionEntry(start.Mission, "editor_test", start.MissionFolder, EditorSession.Doc?.Name ?? "Test map",
            "Test map: the map with its triggers (map editor)", "", false, 0, ""));

    // ---------------------------------------------------------------------------------------------- demo, self-test
    /// <summary>--editor-triggers-demo: a region, a quest, a variable and two triggers at the camera's target, the trigger
    /// editor open on the first one (screenshots)</summary>
    void TriggersDemo()
    {
        var m = Mis;
        var (mx, my) = _src!.ToMap(_cam!.X, _cam.Z);
        Run(MissionEdits.NewRegion(m, true, mx + 10, my - 20, 40, 30, Doc.HeightAt(mx + 30, my - 5), "Goal"));
        Run(MissionEdits.NewRegion(m, false, mx - 50, my + 10, 30, 25, Doc.HeightAt(mx - 35, my + 22), "Camp"));
        Run(MissionEdits.NewQuest(m, "ReachTheGoal"));
        Run(MissionEdits.SetQuestText(m, m.Quests.Count - 1, "headline", "Reach the goal"));
        Run(MissionEdits.Add(m, new EdVariable { Name = "waves", Value = "0" }));
        int ri = m.Regions.Count - 2;
        Run(MissionEdits.NewTrigger(m, "Root/Goal", "Win at the goal"));
        int ti = m.Triggers.Items.Count - 1;
        Run(MissionEdits.ChangeTrigger(m, ti, t =>
        {
            t.Description = "A unit of player 0 reaches the goal: the mission is won";
            var c = EdNode.Of("REGN", true);
            c.Set("rgn_guid", ParamValue.Str(m.Regions[ri].Guid)); c.Set("obj_owner", ParamValue.Of("0")); c.Set("obj_type", ParamValue.Str("CHTR|")); c.Set("obj_count", ParamValue.Str(">0"));
            t.Conditions.Add(c);
            var q = EdNode.Of("QUES", false); q.Set("quest_guid", ParamValue.Str(m.Quests[^1].Guid)); q.Set("dest_state", ParamValue.Of("1")); t.Actions.Add(q);
            t.Actions.Add(EdNode.Of("QUIT", false));
        }));
        Run(MissionEdits.NewTrigger(m, "Root/Waves", "Wave every 90 s"));
        Run(MissionEdits.ChangeTrigger(m, m.Triggers.Items.Count - 1, t =>
        {
            t.Once = false;
            var c = EdNode.Of("TIME", true); c.Set("duration", ParamValue.Of("90")); t.Conditions.Add(c);
            var s = EdNode.Of("SPGR", false); s.Set("classes_0", ParamValue.Str("aje_warrior 2 4")); s.Set("owner", ParamValue.Of("1")); s.Set("pos", ParamValue.Str($"{mx - 35:0} {my + 22:0} 0")); t.Actions.Add(s);
            var v = EdNode.Of("VARS", false); v.Set("varname", ParamValue.Str("waves")); v.Set("operation", ParamValue.Str("+")); v.Set("value", ParamValue.Of("1")); t.Actions.Add(v);
        }));
        // (with --editor-tool: that tool's group open instead of the trigger editor - a screenshot of the regions)
        if (_args.TryGetValue("editor-tool", out var want) && EditorTools.All.FirstOrDefault(x => x.Label.Equals(want, StringComparison.OrdinalIgnoreCase)) is { } tool)
        {
            EditorSession.ToolGroup = tool.Group; EditorTools.Choose(tool);
            RegionState.Region = ri; RegionState.Shape = 0;
            BuildPalette(); BuildProperties(); MissionSelectionChanged();
            L.Info("EDITOR triggers demo ready (" + tool.Label + ")");
            return;
        }
        EditorSession.ToolGroup = "Triggers";
        BuildPalette(); BuildProperties();
        var w = OpenTriggers();
        w.Select(ti, true, 0);
        L.Info("EDITOR triggers demo ready");
    }

    /// <summary>the self-test's part for regions and triggers: a region drawn with the region tool, moved, undone; a
    /// trigger made in the trigger editor (a Region condition whose region is picked on the map, a Win action), checked
    /// by the validation and as triggers.lua, undone</summary>
    void MissionSelfTest(Action<bool, string> T)
    {
        var m = Mis; var c = Ctx();
        int regions0 = m.Regions.Count, items0 = m.Triggers.Items.Count, undo0 = History.UndoCount;
        var at = _cam!.Target;
        var (mx, my) = _src!.ToMap(at.X, at.Z);
        // a rectangle drawn with the tool
        var tool = (RegionTool)EditorTools.All.First(t => t is RegionTool);
        EditorSession.ToolGroup = "Regions"; EditorTools.Choose(tool);
        RegionState.DrawMode = RegionState.Mode.Rectangle; RegionState.AddToRegion = false;
        tool.Begin(c, c.At(mx, my));
        for (int k = 1; k <= 5; k++) tool.Step(c, c.At(mx + 6 * k, my + 4 * k), 0.05);
        tool.End(c, c.At(mx + 30, my + 20));
        History.EndMerge();
        T(m.Regions.Count == regions0 + 1 && Math.Abs(m.Regions[^1].Shapes[0].W - 30) < 1e-6, $"region tool: a 30 x 20 m rectangle drawn ({m.Regions.Count} regions)");
        T(Doc is MapDataDoc md0 && md0.Md.Regions.Count == m.Regions.Count, "the map's regions follow");
        // moved with a drag (one undo step)
        RegionState.DrawMode = RegionState.Mode.Select;
        int steps = History.UndoCount;
        tool.Begin(c, c.At(mx + 15, my + 10));
        for (int k = 1; k <= 5; k++) tool.Step(c, c.At(mx + 15 + 2 * k, my + 10), 0.05);
        tool.End(c, null); History.EndMerge();
        T(Math.Abs(m.Regions[^1].Shapes[0].X - (mx + 10)) < 1e-6 && History.UndoCount == steps + 1, "region tool: dragged 10 m east in one undo step");
        _regionLayer?.Update(true);
        T(_regionLayer != null && _regionLayer.GetNode("Labels").GetChildren().OfType<Label3D>().Any(l => l.Visible && l.Text == m.Regions[^1].Name), "the region's name is drawn on the map");
        // a trigger in the trigger editor: Region (picked on the map) -> Win
        EditorSession.ToolGroup = "Triggers"; EditorTools.Choose(EditorTools.All.First(t => t is TriggerTool));
        var w = OpenTriggers();
        w.NewTrigger();
        int ti = m.Triggers.Items.Count - 1;
        w.AddNode(true, "REGN");
        w.StartPick(PType.Region, "rgn_guid");
        T(w.Picking && !w.Visible, "a pick hides the window");
        TriggerPick(Ctx(), c.At(mx + 20, my + 10));
        T(!w.Picking && w.Visible, "the pick done: the window back");
        var tr = (EdTrigger)m.Triggers.Items[ti];
        T(tr.Conditions.Count == 1 && tr.Conditions[0].Value("rgn_guid") == m.Regions[^1].Guid, "the condition names the picked region");
        w.AddNode(false, "QUIT");
        var issues = TriggerValidation.CheckAll(m);
        T(!issues.Any(i => i.Severity == Severity.Error && i.Item == ti), "the new trigger has no problems: " + string.Join("; ", issues.Where(i => i.Item == ti)));
        var defs = PwRemake.Sim.Scripting.LuaMission.FromSource(m.Triggers.Write(), PwRemake.Sim.Scripting.LuaMission.DefaultStdDir()).Script.Triggers;
        T(defs.Any(d => d.Name == tr.Name && d.Conditions.Count == 1 && d.Conditions[0].Code == "REGN" && d.Actions.Single().Code == "QUIT"), "triggers.lua has it for the engine");
        w.Escape();
        while (History.UndoCount > undo0) History.Undo();
        T(m.Regions.Count == regions0 && m.Triggers.Items.Count == items0, "everything undone");
        L.Info("EDITOR mission selftest done");
    }
}

/// <summary>the trigger editor's window</summary>
public partial class TriggerWindow : Control
{
    readonly MapEditor _ed;
    MissionDoc M => EditorSession.Doc!.Mission;
    Tree _tree = null!;
    VBoxContainer _detail = null!;
    ScrollContainer _detailScroll = null!;
    ItemList _problems = null!;
    Label _problemsHead = null!;
    Control? _overlay;
    List<Issue> _issues = new();
    /// <summary>the chosen item of the file (-1: none), folder (when a folder is chosen), node</summary>
    int _item = -1;
    string? _folder;
    bool _nodeCond;
    int _node = -1;
    bool _self, _rebuildQueued, _showAdvanced;
    readonly List<(Button B, bool Cond, int K)> _rows = new();

    // ---- the pick from the map
    public bool Picking { get; private set; }
    public string PickWhat = "";
    PType _pickType;
    string _pickKey = "";

    public TriggerWindow(MapEditor ed)
    {
        _ed = ed;
        Name = "TriggerWindow";
        Theme = PwTheme.Theme;
        SetAnchorsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Ignore;
        // (the editor's panels darkened behind it: the window reads clearly; the map stays visible around it)
        AddChild(new ColorRect { Color = new Color(0, 0, 0, 0.5f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore });
        var center = new CenterContainer { AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(center);
        var win = new PwWindow("Trigger editor", 1180, 1240) { MouseFilter = MouseFilterEnum.Stop };
        center.AddChild(win);
        var b = win.Body;
        // ---- the top row
        var top = new HBoxContainer();
        top.AddThemeConstantOverride("separation", 4);
        b.AddChild(top);
        void Btn(string t, Action go, string tip, float w = 0) { var x = PwTheme.Btn(t, go, w); x.TooltipText = tip; top.AddChild(x); }
        Btn("New trigger", NewTrigger, "A new trigger in the chosen folder (on at the start, once)", 120);
        Btn("New folder", NewFolder, "A new folder below the chosen one (with a first trigger in it)", 110);
        Btn("Duplicate", Duplicate, "A copy of the chosen trigger (a new GUID)", 100);
        Btn("Delete", DeleteItem, "Delete the chosen trigger or code block", 80);
        Btn("▲", () => MoveItem(-1), "Up (the file's order: the engine's order)", 36);
        Btn("▼", () => MoveItem(1), "Down", 36);
        top.AddChild(new Control { CustomMinimumSize = new Vector2(16, 0) });
        Btn("New code block", NewCode, "Lua of your own in triggers.lua (a custom script block)", 140);
        Btn("custom.lua", OpenCustom, "custom.lua as text (the editor never writes it otherwise)", 110);
        Btn("Check", () => { Validate(true); }, "Check the triggers (with the game's Lua loader)", 70);
        top.AddChild(new Control { SizeFlagsHorizontal = SizeFlags.ExpandFill });
        Btn("Close", () => { Visible = false; }, "Close (Esc)", 80);
        // ---- tree | detail
        var split = new HSplitContainer { CustomMinimumSize = new Vector2(1110, 560) };
        b.AddChild(split);
        _tree = new Tree { CustomMinimumSize = new Vector2(340, 0), HideRoot = true, SelectMode = Tree.SelectModeEnum.Single };
        _tree.ItemSelected += OnTreeSelected;
        split.AddChild(_tree);
        _detailScroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _detail = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _detail.AddThemeConstantOverride("separation", 6);
        _detailScroll.AddChild(_detail);
        split.AddChild(_detailScroll);
        // ---- problems
        _problemsHead = new Label { Text = "Problems" };
        b.AddChild(_problemsHead);
        _problems = new ItemList { CustomMinimumSize = new Vector2(1110, 92), FocusMode = FocusModeEnum.None };
        _problems.ItemSelected += i => GoTo(_issues[(int)i]);
        b.AddChild(_problems);
    }

    // ============================================================================================ refresh
    /// <summary>the mission data changed: everything drawn anew (deferred) unless this window made the change</summary>
    public void OnMissionChanged(MissionPart p)
    {
        if (_self || !IsInstanceValid(this)) return;
        QueueRebuild();
    }

    void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        CallDeferred(nameof(Rebuild));
    }

    /// <summary>runs a command of this window; full: the detail is drawn anew (a structural change), else only the tree
    /// and the lists' texts (a parameter: the form stays as it is)</summary>
    void Apply(IEditCommand c, bool full = false)
    {
        _self = true;
        try { _ed.RunCommand(c); }
        finally { _self = false; }
        if (full) QueueRebuild(); else { BuildTree(); RefreshRows(); Validate(false); }
    }

    public void Rebuild()
    {
        _rebuildQueued = false;
        if (!IsInstanceValid(this)) return;
        var items = M.Triggers.Items;
        if (_item >= items.Count) { _item = -1; _node = -1; }
        if (_item >= 0 && items[_item] is EdTrigger t && _node >= t.List(_nodeCond).Count) _node = -1;
        BuildTree();
        BuildDetail();
        Validate(false);
    }

    void BuildTree()
    {
        _tree.Clear();
        var root = _tree.CreateItem();
        var folders = new Dictionary<string, TreeItem>(StringComparer.Ordinal);
        TreeItem FolderItem(string path)
        {
            if (folders.TryGetValue(path, out var f)) return f;
            int cut = path.LastIndexOf('/');
            var parent = cut > 0 ? FolderItem(path[..cut]) : root;
            var it = _tree.CreateItem(parent);
            it.SetText(0, "▸ " + (cut > 0 ? path[(cut + 1)..] : path));
            it.SetMetadata(0, "folder:" + path);
            it.SetCustomColor(0, PwTheme.Hi);
            folders[path] = it;
            if (_folder == path && _item < 0) it.Select(0);
            return it;
        }
        var items = M.Triggers.Items;
        for (int i = 0; i < items.Count; i++)
        {
            TreeItem it;
            if (items[i] is EdTrigger t)
            {
                it = _tree.CreateItem(FolderItem(t.Folder));
                it.SetText(0, t.Name);
                if (!t.Enabled) it.SetCustomColor(0, PwTheme.Dim);
                it.SetTooltipText(0, (t.Description.Length > 0 ? t.Description + "\n" : "") + $"{(t.Enabled ? "on" : "off")} at the start{(t.Once ? ", once" : "")}; {t.Conditions.Count} conditions, {t.Actions.Count} actions");
            }
            else
            {
                it = _tree.CreateItem(root);
                it.SetText(0, "{ } " + ((EdCode)items[i]).Title);
                it.SetCustomColor(0, new Color(0.6f, 0.85f, 1f));
            }
            it.SetMetadata(0, i);
            if (_issues.Any(x => x.Item == i && x.Severity == Severity.Error)) it.SetCustomColor(0, PwTheme.Lost);
            if (i == _item) { it.Select(0); _tree.ScrollToItem(it); }
        }
    }

    void OnTreeSelected()
    {
        var it = _tree.GetSelected();
        if (it == null) return;
        var md = it.GetMetadata(0);
        if (md.VariantType == Variant.Type.Int) { if (_item == md.AsInt32() && _folder == null) return; _item = md.AsInt32(); _folder = null; }
        else { _folder = md.AsString()["folder:".Length..]; _item = -1; }
        _node = -1;
        CallDeferred(nameof(BuildDetailDeferred));
    }
    void BuildDetailDeferred() => BuildDetail();

    /// <summary>chooses an item and a node (the problems' jump, the demo)</summary>
    public void Select(int item, bool cond, int node)
    {
        _item = item; _folder = null; _nodeCond = cond; _node = node;
        Rebuild();
    }

    // ============================================================================================ the detail
    void Clear() { foreach (var c in _detail.GetChildren()) { _detail.RemoveChild(c); c.QueueFree(); } _rows.Clear(); }

    static Label Head(string t) { var l = new Label { Text = t }; l.AddThemeColorOverride("font_color", PwTheme.Hi); l.AddThemeFontSizeOverride("font_size", 16); return l; }

    void BuildDetail()
    {
        Clear();
        var items = M.Triggers.Items;
        if (_item < 0 || _item >= items.Count)
        {
            if (_folder != null) BuildFolder(_folder);
            else _detail.AddChild(PwTheme.Para("Choose a trigger on the left, or make a new one. A trigger listens while it is on (and its folders are): when its conditions hold it fires its actions.", 14));
            return;
        }
        if (items[_item] is EdCode code) { BuildCode(code); return; }
        var t = (EdTrigger)items[_item];
        int ti = _item;
        _detail.AddChild(Head("Trigger"));
        MissionUi.Field(_detail, "Name", t.Name, s => { if (s.Length > 0) Apply(MissionEdits.Rename<TriggerItem>(M, ti, s)); }, "The trigger's name (SetTrigger by name follows a rename)", 120);
        var frow = new HBoxContainer(); _detail.AddChild(frow);
        MissionUi.Field(frow, "Folder", t.Folder, s => Apply(MissionEdits.ChangeTrigger(M, ti, x => x.Folder = s.Length > 0 ? s : "Root", "Trigger folder"), true), "The folder path (Root/...)", 120);
        var fm = new MenuButton { Text = "▾", TooltipText = "The map's folders" };
        foreach (var f in M.Triggers.Folders()) fm.GetPopup().AddItem(f);
        fm.GetPopup().IndexPressed += i => Apply(MissionEdits.ChangeTrigger(M, ti, x => x.Folder = M.Triggers.Folders()[(int)i], "Trigger folder"), true);
        frow.AddChild(fm);
        _detail.AddChild(PwTheme.Para($"GUID {t.Guid}", 11, PwTheme.Dim));
        _detail.AddChild(new Label { Text = "Description" });
        var desc = new TextEdit { Text = t.Description, CustomMinimumSize = new Vector2(600, 48), WrapMode = TextEdit.LineWrappingMode.Boundary };
        desc.FocusExited += () => { if (desc.Text != ((EdTrigger)M.Triggers.Items[ti]).Description) Apply(MissionEdits.ChangeTrigger(M, ti, x => x.Description = desc.Text, "Trigger description")); };
        _detail.AddChild(desc);
        var flags = new HBoxContainer(); _detail.AddChild(flags);
        void Flag(string text, bool v, Action<EdTrigger, bool> set, string tip)
        {
            var cb = new CheckBox { Text = text, ButtonPressed = v, FocusMode = FocusModeEnum.None, TooltipText = tip };
            cb.Toggled += on => Apply(MissionEdits.ChangeTrigger(M, ti, x => set(x, on), "Trigger: " + text), text == "by difficulty");
            flags.AddChild(cb);
        }
        Flag("on at the start", t.Enabled, (x, v) => x.Enabled = v, "Listening from the start of the map (else a SetTrigger switches it on)");
        Flag("once", t.Once, (x, v) => x.Once = v, "Switched off when it fires (SetTrigger can switch it on again)");
        Flag("one random action", t.Random, (x, v) => x.Random = v, "One randomly chosen action runs per firing");
        Flag("by difficulty", t.ByDifficulty, (x, v) => x.ByDifficulty = v, "Each action runs only on its own difficulty");
        Flag("its folder starts off", t.NodeOff, (x, v) => x.NodeOff = v, "The trigger's folder starts switched off (SetFolder switches it on)");
        MissionUi.Field(_detail, "Expression", t.Expr, s => Apply(MissionEdits.ChangeTrigger(M, ti, x => x.Expr = s.Trim(), "Trigger expression")), "How the conditions combine: 1 && (2 || !3) - empty: all of them", 120)
            .PlaceholderText = "all conditions (1 && 2 && ...)";
        foreach (var cond in new[] { true, false })
        {
            _detail.AddChild(new HSeparator());
            var hr = new HBoxContainer(); _detail.AddChild(hr);
            var h = Head(cond ? $"Conditions ({t.Conditions.Count})" : $"Actions ({t.Actions.Count})"); h.SizeFlagsHorizontal = SizeFlags.ExpandFill; hr.AddChild(h);
            bool cc = cond;
            var add = PwTheme.Btn(cond ? "+ Condition" : "+ Action", () => OpenCatalogue(cc), 130); hr.AddChild(add);
            var list = t.List(cond);
            for (int k = 0; k < list.Count; k++)
            {
                int kk = k;
                var row = new HBoxContainer(); _detail.AddChild(row);
                bool chosen = _nodeCond == cond && _node == k;
                var bt = new Button { Text = $"{k + 1}. {Summary(list[k], cond)}", ToggleMode = true, ButtonPressed = chosen, Alignment = HorizontalAlignment.Left, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, FocusMode = FocusModeEnum.None, CustomMinimumSize = new Vector2(500, 28) };
                bt.TooltipText = string.Join("\n", list[k].Label) + (list[k].Trail != null ? "\n" + list[k].Trail : "");
                bt.Pressed += () => { _nodeCond = cc; _node = chosen ? -1 : kk; BuildDetail(); };
                if (_issues.Any(x => x.Item == ti && x.Conditions == cond && x.Node == k)) bt.AddThemeColorOverride("font_color", _issues.Any(x => x.Item == ti && x.Conditions == cond && x.Node == k && x.Severity == Severity.Error) ? PwTheme.Lost : new Color(1, 0.85f, 0.4f));
                row.AddChild(bt);
                _rows.Add((bt, cond, k));
                void Small(string s, Action go, string tip) { var x = PwTheme.Btn(s, go, 30); x.TooltipText = tip; row.AddChild(x); }
                Small("▲", () => MoveNode(cc, kk, -1), "Up");
                Small("▼", () => MoveNode(cc, kk, 1), "Down");
                Small("⧉", () => { Apply(MissionEdits.ChangeTrigger(M, ti, x => x.List(cc).Insert(kk + 1, x.List(cc)[kk].Clone()), "Copy " + (cc ? "condition" : "action")), true); _node = kk + 1; }, "Copy");
                Small("✕", () => { Apply(MissionEdits.ChangeTrigger(M, ti, x => x.List(cc).RemoveAt(kk), "Delete " + (cc ? "condition" : "action")), true); _node = -1; }, "Delete");
                if (chosen) BuildNodeForm(t, ti, cond, k);
            }
        }
    }

    /// <summary>the list's texts again (a parameter changed)</summary>
    void RefreshRows()
    {
        if (_item < 0 || _item >= M.Triggers.Items.Count || M.Triggers.Items[_item] is not EdTrigger t) return;
        foreach (var (b, cond, k) in _rows)
            if (IsInstanceValid(b) && k < t.List(cond).Count) b.Text = $"{k + 1}. {Summary(t.List(cond)[k], cond)}";
    }

    void MoveNode(bool cond, int k, int d)
    {
        var t = (EdTrigger)M.Triggers.Items[_item];
        int to = k + d;
        if (to < 0 || to >= t.List(cond).Count) return;
        Apply(MissionEdits.ChangeTrigger(M, _item, x => { var l = x.List(cond); var n = l[k]; l.RemoveAt(k); l.Insert(to, n); }, "Move " + (cond ? "condition" : "action")), true);
        if (_node == k && _nodeCond == cond) _node = to;
    }

    /// <summary>one line for a node: its name and its parameters, references by their names</summary>
    string Summary(EdNode n, bool cond)
    {
        if (n.Form == NodeForm.Script) return "Script (Lua)  " + Flat(n.Source);
        if (n.Form == NodeForm.Check) return "Check (Lua)  " + Flat(n.Source);
        if (n.Form == NodeForm.Raw) return "(Lua)  " + Flat(n.Source);
        var ts = TriggerCatalog.Find(n.Code, cond);
        var parts = n.Params.Where(p => p.Key != "note").Select(p => $"{p.Key} = {Show(ts?.Param(p.Key)?.Type, p.Value)}");
        var s = $"{ts?.Name ?? n.Ctor}   {string.Join(", ", parts)}";
        return s.Length > 160 ? s[..157] + "..." : s;
    }
    static string Flat(string s) { var f = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)); return f.Length > 120 ? f[..117] + "..." : f; }

    /// <summary>a value as the editor shows it: a reference by its name</summary>
    string Show(PType? t, ParamValue v)
    {
        var s = v.Value;
        if (s == null) return v.Text;
        var m = M;
        string? name = t switch
        {
            PType.Region => s == "UniqueWorldRegion" ? "whole map" : m.Region(s)?.Name,
            PType.Object => m.Group(s)?.Name ?? m.Object(s)?.Name,
            PType.Group => m.Group(s)?.Name,
            PType.Quest => m.Quest(s)?.Name,
            PType.Trigger => m.Triggers.Find(s)?.Name,
            _ => null,
        };
        return name ?? (s.Length > 40 ? s[..37] + "..." : s);
    }

    void BuildFolder(string path)
    {
        _detail.AddChild(Head("Folder " + path));
        int n = M.Triggers.Triggers.Count(t => t.Folder == path || t.Folder.StartsWith(path + "/", StringComparison.Ordinal));
        _detail.AddChild(PwTheme.Para($"{n} triggers in it. A folder switched off (SetFolder, or \"its folder starts off\" on a trigger of it) silences every trigger below it.", 13));
        if (path == "Root") return;
        MissionUi.Field(_detail, "Rename", path, s =>
        {
            if (s.Length == 0 || s == path) return;
            var cs = new List<IEditCommand>();
            for (int i = 0; i < M.Triggers.Items.Count; i++)
                if (M.Triggers.Items[i] is EdTrigger t && (t.Folder == path || t.Folder.StartsWith(path + "/", StringComparison.Ordinal)))
                    cs.Add(MissionEdits.ChangeTrigger(M, i, x => x.Folder = s + x.Folder[path.Length..]));
            _folder = s;
            Apply(new CompositeEdit("Rename folder", cs), true);
        }, "The folder's path: every trigger in it moves", 80);
    }

    void BuildCode(EdCode code)
    {
        int ci = _item;
        _detail.AddChild(Head("Custom script block"));
        _detail.AddChild(PwTheme.Para("Lua the editor keeps as it is (in its place among the triggers): a local, a function, a loop that makes triggers, a trigger with a computed field. The game runs it when it loads triggers.lua.", 13));
        var (box, err) = CodeBox(code.Source, 380);
        var b = PwTheme.Btn("Apply", () =>
        {
            var e = TriggerValidation.LuaSyntax(box.Text);
            err.Text = e == null ? "" : "Lua: " + e;
            if (e != null) return;
            Apply(MissionEdits.Change<TriggerItem>(M, ci, x => ((EdCode)x).Source = box.Text.TrimEnd(), "Edit code block"), true);
        }, 120);
        _detail.AddChild(b);
    }

    /// <summary>a code box (Lua highlighted) and the line for its error</summary>
    (CodeEdit Box, Label Err) CodeBox(string text, float h, Control? into = null)
    {
        var hl = new CodeHighlighter { NumberColor = new Color(0.7f, 0.9f, 0.6f), SymbolColor = new Color(0.85f, 0.85f, 0.8f), FunctionColor = new Color(0.6f, 0.8f, 1f), MemberVariableColor = new Color(0.9f, 0.85f, 0.7f) };
        foreach (var k in PwRemake.Sim.Editor.Triggers.LuaLexer.Keywords) hl.AddKeywordColor(k, new Color(1f, 0.7f, 0.4f));
        hl.AddColorRegion("--", "", new Color(0.55f, 0.65f, 0.55f), true);
        hl.AddColorRegion("\"", "\"", new Color(0.9f, 0.8f, 0.5f));
        hl.AddColorRegion("'", "'", new Color(0.9f, 0.8f, 0.5f));
        var box = new CodeEdit { Text = text, CustomMinimumSize = new Vector2(600, h), GuttersDrawLineNumbers = true, SyntaxHighlighter = hl, IndentUseSpaces = true, IndentSize = 2 };
        box.AddThemeFontSizeOverride("font_size", 13);
        (into ?? _detail).AddChild(box);
        var err = new Label { Text = "" };
        err.AddThemeColorOverride("font_color", PwTheme.Lost);
        (into ?? _detail).AddChild(err);
        return (box, err);
    }

    // ============================================================================================ the node's form
    void BuildNodeForm(EdTrigger t, int ti, bool cond, int k)
    {
        var n = t.List(cond)[k];
        var panel = new PanelContainer();
        var sb = new StyleBoxFlat { BgColor = new Color(0.08f, 0.06f, 0.03f, 0.85f), BorderColor = new Color(PwTheme.Dim, 0.8f), ContentMarginLeft = 12, ContentMarginRight = 8, ContentMarginTop = 8, ContentMarginBottom = 8 };
        sb.SetBorderWidthAll(1);
        panel.AddThemeStyleboxOverride("panel", sb);
        var box = new VBoxContainer(); box.AddThemeConstantOverride("separation", 4);
        panel.AddChild(box);
        _detail.AddChild(panel);
        void Change(Action<EdNode> f, string what, bool full = false) => Apply(MissionEdits.ChangeTrigger(M, ti, x => f(x.List(cond)[k]), what), full);
        MissionUi.Field(box, "Note", string.Join(" - ", n.Label), s => Change(x => x.Label = s.Length > 0 ? new List<string> { TriggerScript.CommentText(s) } : new List<string>(), "Node note"), "A comment line before the node in triggers.lua", 120);
        if (n.IsCode)
        {
            box.AddChild(PwTheme.Para(n.Form switch
            {
                NodeForm.Script => "Script: an action in Lua - Script(function(api) ... end). The Game API: docs/LUA_MAPS.md §4.",
                NodeForm.Check => "Check: a condition in Lua - Check(function(api) return <true / false> end [, seconds]), checked every 0.5 s (or the seconds given).",
                _ => "A Lua expression the editor keeps as text (it must make a condition / action).",
            }, 12));
            var (cb, err) = CodeBox(n.Source, 180, box);
            box.AddChild(PwTheme.Btn("Apply", () =>
            {
                var e = TriggerValidation.LuaSyntax(cb.Text, true);
                err.Text = e == null ? "" : "Lua: " + e;
                if (e == null) Change(x => x.Source = cb.Text.TrimEnd(), "Edit code", true);
            }, 120));
            return;
        }
        var ts = TriggerCatalog.Find(n.Code, cond);
        if (ts == null) { box.AddChild(PwTheme.Para($"{n.Ctor}: a type the engine does not know (it is ignored). Its parameters stay as written.", 13, PwTheme.Lost)); }
        else
        {
            var title = Head($"{ts.Name}  ({ts.Code}, {ts.Category})"); box.AddChild(title);
            box.AddChild(PwTheme.Para(ts.Summary, 12));
        }
        var specs = ts?.Params.ToList() ?? new List<ParamSpec>();
        // parameters written that the type does not list
        foreach (var p in n.Params) if (specs.All(s => s.Key != p.Key) && p.Key != "note") specs.Add(new ParamSpec(p.Key, PType.Text, null, "not a parameter of this type"));
        string? section = null;
        bool anyAdvanced = false;
        foreach (var sp in specs)
        {
            if (sp.Key == "difficulty" && !(t.ByDifficulty || n.Get("difficulty") != null) && !cond) continue;
            bool hidden = (sp.Advanced || sp.Section is "Second objects (B)" or "Targets" or "Position from an object" or "Transporters" or "remake") && n.Get(sp.Key) == null && !_showAdvanced;
            if (sp.Advanced || sp.Section is "Second objects (B)" or "Targets" or "Position from an object" or "Transporters" or "remake") anyAdvanced = true;
            if (hidden) continue;
            if (sp.Section != section && sp.Section.Length > 0 && sp.Section != "remake") { section = sp.Section; var l = new Label { Text = sp.Section }; l.AddThemeColorOverride("font_color", PwTheme.Dim); box.AddChild(l); }
            ParamRow(box, n, sp, (v, extra) =>
            {
                var cmd = MissionEdits.ChangeTrigger(M, ti, x => x.List(cond)[k].Set(sp.Key, v), $"{ts?.Name ?? n.Ctor}: {sp.Key}");
                Apply(extra == null ? cmd : new CompositeEdit(cmd.Name, new[] { extra, cmd }), sp.Type is PType.Object or PType.Region or PType.TextKey || v == null);
            });
        }
        if (anyAdvanced)
        {
            var more = new CheckBox { Text = "More parameters (filters, second queries)", ButtonPressed = _showAdvanced, FocusMode = FocusModeEnum.None };
            more.Toggled += on => { _showAdvanced = on; BuildDetail(); };
            box.AddChild(more);
        }
    }

    /// <summary>one parameter's row: its name (help and default in the tooltip), the field the type asks for, Pick for
    /// references on the map, ↺ back to the default</summary>
    void ParamRow(VBoxContainer box, EdNode n, ParamSpec sp, Action<ParamValue?, IEditCommand?> set)
    {
        var m = M;
        var pv = n.Get(sp.Key);
        string? v = pv?.Value;
        var row = new HBoxContainer(); box.AddChild(row);
        string tip = (sp.Help.Length > 0 ? sp.Help : sp.Key) + (sp.Default != null ? $"\ndefault: {sp.Default}" : "");
        var lab = new Label { Text = sp.Key, CustomMinimumSize = new Vector2(150, 0), TooltipText = tip, MouseFilter = MouseFilterEnum.Stop };
        if (pv != null) lab.AddThemeColorOverride("font_color", PwTheme.Hi);
        row.AddChild(lab);
        string def = sp.Default != null ? $"(default: {sp.Default})" : "(not set)";
        // a Lua expression: kept as Lua
        if (pv is { Kind: ParamKind.Expr })
        {
            var e = MissionUi.Field(row, "", pv.Text, s => set(s.Length == 0 ? null : new ParamValue(ParamKind.Expr, s), null), "a Lua expression (computed when the file runs)");
            row.AddChild(new Label { Text = "(Lua)" });
            Reset(); return;
        }
        void Options(IReadOnlyList<(string Value, string Label)> items, bool allowText = false)
        {
            var o = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill, FocusMode = FocusModeEnum.None, ClipText = true, CustomMinimumSize = new Vector2(260, 0) };
            var vals = new List<string?> { null };
            o.AddItem(def);
            foreach (var (val, l) in items) { o.AddItem(l == val ? val : $"{l}  [{(val.Length > 34 ? val[..10] + "…" : val)}]"); vals.Add(val); }
            int at = v == null ? 0 : vals.IndexOf(v);
            if (v != null && at < 0) { o.AddItem($"{v}  (not on the map)"); vals.Add(v); at = vals.Count - 1; }
            o.Select(at);
            o.ItemSelected += i => set(vals[(int)i] is { } s ? ParamValue.Of(s) : null, null);
            row.AddChild(o);
        }
        void Text(IEnumerable<string>? suggest = null, string? placeholder = null)
        {
            var e = MissionUi.Field(row, "", v ?? "", s => set(s.Length == 0 ? null : ParamValue.Of(s), null), tip);
            e.PlaceholderText = placeholder ?? def;
            if (suggest != null)
            {
                var list = suggest.Distinct().Take(400).ToList();
                if (list.Count > 0)
                {
                    var mb = new MenuButton { Text = "▾", TooltipText = "Choose" };
                    foreach (var s in list) mb.GetPopup().AddItem(s);
                    mb.GetPopup().IndexPressed += i => set(ParamValue.Of(list[(int)i]), null);
                    row.AddChild(mb);
                }
            }
        }
        void Pick(PType what) { var b = PwTheme.Btn("Pick", () => StartPick(what, sp.Key), 56); b.TooltipText = "Click on the map"; row.AddChild(b); }
        void Reset()
        {
            if (pv == null) return;
            var b = PwTheme.Btn("↺", () => set(null, null), 30); b.TooltipText = "Back to the default (the parameter left out)"; row.AddChild(b);
        }
        switch (sp.Type)
        {
            case PType.Choice: Options(sp.Choices ?? Array.Empty<(string, string)>()); break;
            case PType.Player: Options(sp.Choices ?? Enumerable.Range(0, 8).Select(i => (i.ToString(CultureInfo.InvariantCulture), $"player {i}")).ToList()); break;
            case PType.Bool: Options(new[] { ("1", "yes"), ("0", "no") }); break;
            case PType.Region:
                Options(new[] { ("UniqueWorldRegion", "the whole map") }.Concat(m.Regions.Select(r => (r.Guid, r.Name))).ToList());
                Pick(PType.Region); break;
            case PType.Group: Options(m.Groups.Select(g => (g.Guid, g.Name)).ToList()); break;
            case PType.Quest: Options(m.Quests.Select(q => (q.Guid, q.Name)).ToList()); break;
            case PType.Trigger: Options(m.Triggers.Triggers.Where(x => x.Guid.Length > 0).Select(x => (x.Guid, x.Name)).ToList()); break;
            case PType.Object:
                {
                    var name = v == null || v == "NA" ? def : m.Group(v) is { } g ? "group " + g.Name : m.Object(v) is { } o ? o.Name : v + " (not on the map)";
                    row.AddChild(new Label { Text = name, SizeFlagsHorizontal = SizeFlags.ExpandFill, ClipText = true, CustomMinimumSize = new Vector2(200, 0) });
                    var mb = new MenuButton { Text = "groups ▾", TooltipText = "A group of the map" };
                    var gs = m.Groups.ToList();
                    foreach (var gr in gs) mb.GetPopup().AddItem(gr.Name);
                    mb.GetPopup().IndexPressed += i => set(ParamValue.Str(gs[(int)i].Guid), null);
                    row.AddChild(mb);
                    Pick(PType.Object);
                    break;
                }
            case PType.ObjectName: Text(m.Groups.Select(g => g.Name)); Pick(PType.ObjectName); break;
            case PType.Position: Text(m.Cameras.Select(c => $"{MissionUi.F(c.X)} {MissionUi.F(c.Y)} 0"), "x y z (map metres)"); Pick(PType.Position); break;
            case PType.Positions: Text(null, "x y z|x y z|..."); Pick(PType.Positions); break;
            case PType.Variable: Text(m.Variables.Select(x => x.Name)); break;
            case PType.QuestName: Text(m.Quests.Select(q => q.Name)); break;
            case PType.TriggerName: Text(m.Triggers.Triggers.Select(x => x.Name)); break;
            case PType.Folder: Text(m.Triggers.Folders().Select(f => f + "/")); break;
            case PType.Sequence: Text(m.Sequences); break;
            case PType.Dialog: Text(m.Dialogs); break;
            case PType.QuestionMark: Text(m.QuestionMarks.Select(q => q.Name)); break;
            case PType.ObjTypes: Text(new[] { "All", "CHTR|", "ANML|", "VHCL|", "SHIP|", "BLDG|", "CHTR|ANML|VHCL|SHIP|", "DCCO|" }); break;
            case PType.Class: Text(ObjectToolState.PaletteOf().Select(e => e.Cls).Take(400)); break;
            case PType.TextKey:
                {
                    Text(m.TextKeys);
                    // a new text: a free key of the map's texts, its text typed below
                    var nb = PwTheme.Btn("New", () =>
                    {
                        var used = new HashSet<string>(m.TextKeys, StringComparer.Ordinal);
                        int nn = 1; while (used.Contains($"_Text_{nn}")) nn++;
                        var key0 = $"_Text_{nn}";
                        set(ParamValue.Str(key0), MissionEdits.SetText(m, m.Lang, key0, "..."));
                    }, 50);
                    nb.TooltipText = $"A new text key (texts/{m.Lang}.lua), its text edited below";
                    row.AddChild(nb);
                    Reset();
                    // the text itself, in the map's language
                    var key = (v ?? "").Split('\t')[0];
                    if (key.Length == 0) return;
                    var r2 = new HBoxContainer(); box.AddChild(r2);
                    r2.AddChild(new Label { Text = $"  text ({m.Lang})", CustomMinimumSize = new Vector2(150, 0) });
                    MissionUi.Field(r2, "", m.Text(key) ?? "", s => Apply(MissionEdits.SetText(m, m.Lang, key, s.Length > 0 ? s : null)), $"texts/{m.Lang}.lua: {key}");
                    return;
                }
            default: Text(sp.Choices?.Select(c => c.Value)); break;
        }
        Reset();
    }

    // ============================================================================================ picking on the map
    public void StartPick(PType what, string key)
    {
        _pickType = what; _pickKey = key;
        PickWhat = what switch { PType.Region => "a region", PType.Object or PType.ObjectName => "an object", PType.Positions => "a point (added to the path)", _ => "a point" };
        Picking = true;
        Visible = false;
        EditorSession.ToolGroup = "Triggers";
        EditorTools.Choose(EditorTools.All.First(t => t is TriggerTool));
    }

    /// <summary>the click of a pick (the trigger tool's Begin)</summary>
    public void Picked(ToolContext c, ToolPoint p)
    {
        var m = M;
        if (_item < 0 || _item >= m.Triggers.Items.Count || m.Triggers.Items[_item] is not EdTrigger t || _node < 0 || _node >= t.List(_nodeCond).Count) { EndPick(); return; }
        ParamValue? v = null; IEditCommand? extra = null;
        switch (_pickType)
        {
            case PType.Region:
                {
                    var hit = m.Regions.Where(r => r.Shapes.Any(s => s.Contains(p.Mx, p.My))).OrderBy(r => r.Shapes.Where(s => s.Contains(p.Mx, p.My)).Min(s => s.W * s.H)).FirstOrDefault();
                    if (hit == null) { c.Note("No region there - click inside one (Esc: cancel)"); return; }
                    v = ParamValue.Str(hit.Guid); c.Note("region " + hit.Name);
                    break;
                }
            case PType.Object or PType.ObjectName:
                {
                    var o = MapEditor.NearestObject(c.Doc, p.Mx, p.My, 5);
                    if (o == null) { c.Note("No object there (Esc: cancel)"); return; }
                    if (_pickType == PType.ObjectName) v = ParamValue.Str(o.Name);
                    else { var (gc, g) = MissionEdits.EnsureGuid(o); extra = gc; v = ParamValue.Str(g); }
                    c.Note("object " + o.Name);
                    break;
                }
            case PType.Positions:
                {
                    var old = t.List(_nodeCond)[_node].Value(_pickKey);
                    var pt = $"{MissionUi.F(p.Mx)} {MissionUi.F(p.My)} {MissionUi.F(p.Y)}";
                    v = ParamValue.Str(string.IsNullOrEmpty(old) ? pt : old + "|" + pt);
                    break;
                }
            default: v = ParamValue.Str($"{MissionUi.F(p.Mx)} {MissionUi.F(p.My)} {MissionUi.F(p.Y)}"); break;
        }
        bool cond = _nodeCond; int k = _node; string key = _pickKey;
        var cmd = MissionEdits.ChangeTrigger(m, _item, x => x.List(cond)[k].Set(key, v), "Pick " + key);
        EndPick();
        Apply(extra == null ? cmd : new CompositeEdit(cmd.Name, new[] { extra, cmd }), true);
    }

    void EndPick() { Picking = false; Visible = true; }

    /// <summary>Esc: a pick cancelled, an open overlay closed, else the window</summary>
    public void Escape()
    {
        if (Picking) { EndPick(); return; }
        if (_overlay != null && IsInstanceValid(_overlay)) { _overlay.QueueFree(); _overlay = null; return; }
        Visible = false;
    }

    // ============================================================================================ the file's items
    string CurrentFolder() => _folder ?? (_item >= 0 && _item < M.Triggers.Items.Count && M.Triggers.Items[_item] is EdTrigger t ? t.Folder : "Root");

    public void NewTrigger()
    {
        var f = CurrentFolder();
        Apply(MissionEdits.NewTrigger(M, f), true);
        _item = M.Triggers.Items.FindLastIndex(i => i is EdTrigger t && t.Folder == f); _folder = null; _node = -1;
        Rebuild();
    }

    void NewFolder()
    {
        var parent = CurrentFolder();
        var name = MissionDoc.FreeName(parent + "/Folder", M.Triggers.Folders());
        Apply(MissionEdits.NewTrigger(M, name), true);
        _item = M.Triggers.Items.Count - 1; _folder = null;
        Rebuild();
    }

    void NewCode()
    {
        int at = _item >= 0 ? _item + 1 : M.Triggers.Items.Count;
        Apply(MissionEdits.Add<TriggerItem>(M, new EdCode { Source = "-- your Lua (runs when triggers.lua is loaded)\nlocal SPAWN_POINT = \"100 100 0\"", Title = "custom script" }, at, "New code block"), true);
        _item = at; _folder = null;
        Rebuild();
    }

    void Duplicate()
    {
        if (_item < 0 || M.Triggers.Items[_item] is not EdTrigger t) return;
        var c = (EdTrigger)t.Copy();
        c.Name = MissionDoc.FreeName(t.Name + " copy", M.Triggers.Triggers.Select(x => x.Name)).Replace(" copy_1", " copy");
        c.Guid = EdGuid.New();
        Apply(MissionEdits.Add<TriggerItem>(M, c, _item + 1, "Duplicate trigger"), true);
        _item++;
    }

    void DeleteItem()
    {
        if (_item < 0 || _item >= M.Triggers.Items.Count) return;
        Apply(MissionEdits.Remove<TriggerItem>(M, _item), true);
        _item = Math.Min(_item, M.Triggers.Items.Count - 1); _node = -1;
    }

    void MoveItem(int d)
    {
        int to = _item + d;
        if (_item < 0 || to < 0 || to >= M.Triggers.Items.Count) return;
        Apply(MissionEdits.Move<TriggerItem>(M, _item, to), true);
        _item = to;
    }

    /// <summary>adds a node of the type to the chosen trigger (and chooses it)</summary>
    public void AddNode(bool cond, string code)
    {
        if (_item < 0 || _item >= M.Triggers.Items.Count || M.Triggers.Items[_item] is not EdTrigger) return;
        int ti = _item;
        Apply(MissionEdits.ChangeTrigger(M, ti, x => x.List(cond).Add(EdNode.Of(code, cond)), "Add " + (cond ? "condition" : "action")), true);
        _nodeCond = cond; _node = ((EdTrigger)M.Triggers.Items[ti]).List(cond).Count - 1;
        Rebuild();
    }

    // ============================================================================================ the catalogue
    void OpenCatalogue(bool cond)
    {
        _overlay?.QueueFree();
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.45f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Stop };
        var center = new CenterContainer { AnchorRight = 1, AnchorBottom = 1 };
        dim.AddChild(center);
        var win = new PwWindow(cond ? "Add a condition" : "Add an action", 620, 700);
        center.AddChild(win);
        var search = new LineEdit { PlaceholderText = "search (name, code, what it does)", ClearButtonEnabled = true };
        win.Body.AddChild(search);
        var tree = new Tree { CustomMinimumSize = new Vector2(552, 440), HideRoot = true, Columns = 2 };
        tree.SetColumnExpand(0, false); tree.SetColumnCustomMinimumWidth(0, 190);
        win.Body.AddChild(tree);
        var help = PwTheme.Para("", 12);
        help.CustomMinimumSize = new Vector2(552, 36);
        win.Body.AddChild(help);
        void Fill()
        {
            tree.Clear();
            var root = tree.CreateItem();
            var found = TriggerCatalog.Search(search.Text, cond);
            foreach (var cat in TriggerCatalog.Categories(cond))
            {
                var ts = found.Where(x => x.Category == cat).ToList();
                if (ts.Count == 0) continue;
                var ci = tree.CreateItem(root); ci.SetText(0, cat); ci.SetCustomColor(0, PwTheme.Hi); ci.SetSelectable(0, false); ci.SetSelectable(1, false);
                foreach (var t in ts)
                {
                    var it = tree.CreateItem(ci);
                    it.SetText(0, t.Name + (t.Origin == "mirage" ? " (MIRAGE)" : ""));
                    it.SetText(1, t.Summary);
                    it.SetMetadata(0, t.Code);
                    it.SetTooltipText(0, $"{t.Code}: {t.Summary}\n{t.Params.Count} parameters");
                }
            }
        }
        Fill();
        search.TextChanged += _ => Fill();
        string? chosen = null;
        tree.ItemSelected += () => { chosen = tree.GetSelected()?.GetMetadata(0).AsString(); if (chosen != null && TriggerCatalog.Find(chosen, cond) is { } ts) help.Text = $"{ts.Name} ({ts.Code}): {ts.Summary}"; };
        void Ok() { if (string.IsNullOrEmpty(chosen)) return; dim.QueueFree(); _overlay = null; AddNode(cond, chosen); }
        tree.ItemActivated += Ok;
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddChild(PwTheme.Btn("Add", Ok, 120));
        row.AddChild(PwTheme.Btn("Cancel", () => { dim.QueueFree(); _overlay = null; }, 120));
        win.Body.AddChild(row);
        AddChild(dim);
        _overlay = dim;
        search.GrabFocus();
    }

    // ============================================================================================ custom.lua
    void OpenCustom()
    {
        _overlay?.QueueFree();
        var dim = new ColorRect { Color = new Color(0, 0, 0, 0.45f), AnchorRight = 1, AnchorBottom = 1, MouseFilter = MouseFilterEnum.Stop };
        var center = new CenterContainer { AnchorRight = 1, AnchorBottom = 1 };
        dim.AddChild(center);
        var win = new PwWindow("custom.lua", 900, 960);
        center.AddChild(win);
        win.Body.AddChild(PwTheme.Para("Your own Lua, loaded after triggers.lua: more triggers, shared functions, constants (docs/LUA_MAPS.md §3). The editor keeps it as you write it.", 13));
        var (box, err) = CodeBox(M.Custom ?? "-- custom.lua: your own code (docs/LUA_MAPS.md)\n", 480, win.Body);
        bool warned = false;
        var row = new HBoxContainer { Alignment = BoxContainer.AlignmentMode.Center };
        row.AddChild(PwTheme.Btn("Check", () => { var e = TriggerValidation.LuaSyntax(box.Text); err.Text = e == null ? "Lua syntax: fine" : "Lua: " + e; }, 110));
        row.AddChild(PwTheme.Btn("Keep", () =>
        {
            var e = TriggerValidation.LuaSyntax(box.Text);
            if (e != null && !warned) { err.Text = "Lua: " + e + "  (Keep again to keep it anyway)"; warned = true; return; }
            Apply(MissionEdits.SetCustom(M, box.Text.Trim().Length == 0 ? null : box.Text), true);
            dim.QueueFree(); _overlay = null;
        }, 110));
        row.AddChild(PwTheme.Btn("Cancel", () => { dim.QueueFree(); _overlay = null; }, 110));
        win.Body.AddChild(row);
        AddChild(dim);
        _overlay = dim;
    }

    // ============================================================================================ problems
    /// <summary>the problems list: the editor's one check (TriggerValidation.CheckAll - the file loaded as the game loads
    /// it, as the Triggers panel and Test map check); full: the Check button (says so when there are none)</summary>
    void Validate(bool full)
    {
        _issues = TriggerValidation.CheckAll(M);
        _problems.Clear();
        foreach (var i in _issues.Take(500))
        {
            int idx = _problems.AddItem(i.ToString());
            _problems.SetItemCustomFgColor(idx, i.Severity == Severity.Error ? PwTheme.Lost : new Color(1, 0.85f, 0.45f));
        }
        int e = _issues.Count(x => x.Severity == Severity.Error);
        _problemsHead.Text = _issues.Count == 0 ? "Problems: none (the file loads)" : $"Problems: {e} errors, {_issues.Count - e} warnings";
    }

    void GoTo(Issue i)
    {
        if (i.Item < 0) { if (i.Message.StartsWith("custom.lua", StringComparison.Ordinal)) OpenCustom(); return; }
        _item = i.Item; _folder = null;
        if (i.Conditions is bool c && i.Node >= 0) { _nodeCond = c; _node = i.Node; }
        if (i.Param != null && M.Triggers.Items[i.Item] is EdTrigger t && _node >= 0 && _node < t.List(_nodeCond).Count
            && TriggerCatalog.Find(t.List(_nodeCond)[_node].Code, _nodeCond)?.Param(i.Param) is { } ps && (ps.Advanced || ps.Section.Length > 0)) _showAdvanced = true;
        BuildTree(); BuildDetail();
    }
}
