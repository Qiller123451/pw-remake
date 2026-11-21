// "Test map" of the map editor: a skirmish on the edited map straight from memory, without saving it. The editor
// registers a snapshot of its document (IEditorMapDoc.Clone: the game cannot change the edited map) under a key; the
// skirmish setup's map is that key ("editor:<n>"), and the game session builds the map source from the snapshot
// instead of reading a file (GameSession.Editor.cs). The key lives in the GameStart, so Retry plays the same snapshot;
// a saved game of a test run loads only while the editor still holds it. No Godot types.
using System;
using PwRemake.Sim.Core;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor;

public static class EditorPlaytest
{
    static readonly Logger L = Log.For("Editor");
    public const string Prefix = "editor:";
    static string? s_key;
    static IEditorMapDoc? s_doc;
    static int s_n;

    /// <summary>is this skirmish map a test map of the editor</summary>
    public static bool IsKey(string? map) => map != null && map.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>keeps a snapshot of the document for a test run (the previous one goes) and returns its key</summary>
    public static string Register(IEditorMapDoc doc)
    {
        s_doc = doc.Clone();
        s_key = Prefix + (++s_n);
        L.Info($"test map {s_key}: {doc.Name}, {doc.HeightsW * doc.Cell} x {doc.HeightsH * doc.Cell} m");
        return s_key;
    }

    /// <summary>the snapshot of a key (null: not this editor's - an old saved game)</summary>
    public static IEditorMapDoc? Doc(string key) => key == s_key ? s_doc : null;

    /// <summary>the map source of a test map, built from a fresh copy of the snapshot (Retry starts from the same
    /// ground, whatever the last run's buildings levelled); throws when the key is not held</summary>
    public static MapSource Source(string key, IMapRules rules, Func<string, bool> hasModel, ForestData? forest)
    {
        var d = Doc(key) ?? throw new InvalidOperationException("The map editor's test map is no longer in memory (open the editor and press Test map again).");
        return d.Clone().ToSource(rules, hasModel, forest);
    }

    /// <summary>forgets the snapshot</summary>
    public static void Clear() { s_doc = null; s_key = null; }
}
