// The map editor's tools (docs/MAP_EDITOR.md "Tools"): each tool of the palette is an EditorTool - its group (Terrain,
// Paint, Objects ...), its name, its settings in the properties panel, what the left mouse does over the map (Begin,
// Step every frame while held, End) and what the cursor shows. The editor screen (MapEditor.cs) only passes the mouse
// on; the tools make commands on the document through the history (ctx.Run: a held stroke's steps merge into one undo
// step - the screen ends the merge when the button goes up).
//
// The tools of each phase register in EditorTools.All (one line per phase); the palette shows a group's tools in that
// order, one of them chosen per group (EditorTools.Chosen).
using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using PwRemake.Sim.Editor;
using PwRemake.Sim.Maps;
using PwRemake.View.World;

namespace PwRemake.Editor;

/// <summary>a point on the ground: game (X, Y = height, Z) and map (Mx, My) coordinates</summary>
public readonly record struct ToolPoint(double X, double Y, double Z, double Mx, double My);

/// <summary>what a tool works with</summary>
public sealed class ToolContext
{
    public IEditorMapDoc Doc = null!;
    public EditHistory History = null!;
    public MapSource Source = null!;
    public WorldView View = null!;
    /// <summary>Ctrl / Shift held (Ctrl: a tool's other way - lower, erase, pick)</summary>
    public bool Ctrl, Shift;
    /// <summary>a short note in the status line</summary>
    public Action<string> Note = _ => { };
    /// <summary>the settings panel built again (a picked height, a changed choice)</summary>
    public Action Rebuild = () => { };

    /// <summary>does the command and keeps it for undo; merge: part of the stroke going on (one undo step)</summary>
    public void Run(IEditCommand? c, bool merge = true) { if (c != null) History.Execute(c, merge); }

    /// <summary>map (mx, my) -> the ground point there</summary>
    public ToolPoint At(double mx, double my)
    {
        var (x, z) = Source.ToGame(mx, my);
        return new ToolPoint(x, Doc.HeightAt(mx, my), z, mx, my);
    }
}

/// <summary>what the cursor shows for a tool</summary>
public struct ToolCursor
{
    /// <summary>the brush's radius (0: none) and outline</summary>
    public double Radius;
    public bool Square, Other;
    /// <summary>lines on the ground (game x0, z0 -> x1, z1)</summary>
    public List<(double X0, double Z0, double X1, double Z1, Color C)>? Lines;
}

public abstract class EditorTool
{
    /// <summary>the palette group (Terrain, Paint, Objects, Players, Regions, Triggers)</summary>
    public abstract string Group { get; }
    public abstract string Label { get; }
    public virtual string Tip => "";
    /// <summary>a held stroke: Step every frame while the button is down</summary>
    public virtual void Begin(ToolContext c, ToolPoint p) { }
    /// <summary>every frame while the left button is held; p: the ground under the mouse (null: none)</summary>
    public virtual void Step(ToolContext c, ToolPoint? p, double dt) { }
    /// <summary>the button went up</summary>
    public virtual void End(ToolContext c, ToolPoint? p) { }
    /// <summary>the tool was chosen or left (a tool's pending clicks forgotten)</summary>
    public virtual void Reset() { }
    /// <summary>its settings in the properties panel</summary>
    public virtual void BuildProps(ToolUi ui, ToolContext? c) { }
    public virtual ToolCursor Cursor(ToolContext c, ToolPoint? p) => new() { Radius = EditorSession.Brush.Radius, Square = EditorSession.Brush.Shape.Form == BrushForm.Square, Other = c.Ctrl };
    /// <summary>the tool's part of the status line</summary>
    public virtual string Status(ToolContext c) => Label;
}

public static class EditorTools
{
    /// <summary>every tool, in palette order (phase 2: the terrain and paint tools; phases 3, 4: objects, players)</summary>
    public static readonly List<EditorTool> All = new(TerrainToolSet.Create()
        .Concat(ObjectToolSet.Create())                   // (phases 3 and 4: Tools/ObjectTool.cs - objects, players)
        .Concat(MissionToolSet.Create()));                // (phases 5 and 6: Tools/RegionTools.cs, TriggerEditor.cs - regions, triggers)

    static readonly Dictionary<string, EditorTool> s_chosen = new();

    public static IEnumerable<EditorTool> In(string group) => All.Where(t => t.Group == group);

    /// <summary>the chosen tool of a group (the first one until another is chosen); null: the group has none</summary>
    public static EditorTool? Chosen(string group) => s_chosen.TryGetValue(group, out var t) ? t : In(group).FirstOrDefault();

    public static void Choose(EditorTool t)
    {
        if (Chosen(t.Group) is { } old && old != t) old.Reset();
        s_chosen[t.Group] = t;
    }

    /// <summary>the tool the left mouse uses now: the chosen one of the open group</summary>
    public static EditorTool? Current => Chosen(EditorSession.ToolGroup);
}
