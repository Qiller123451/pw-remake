// Writing the level settings of a map (the original's description tree: Root/Base/..., Root/PlayerSettings/...) from
// the map editor: a map keeps them twice - the tree (MapData.InfoTree = PwMap.Settings: what map.lua saves) and the
// flattened key/values (MapData.Description: what MapSource reads). Set writes both. Detach first gives the map a tree
// of its own (a deep copy): Test map's snapshot (MapDataDoc.CloneData) shares the tree of the map it was taken from.
using System.Collections.Generic;
using PwRemake.Sim.Maps;

namespace PwRemake.Sim.Editor;

public static class LevelSettings
{
    /// <summary>gives the map a tree of its own (a deep copy; a new "Root" when it has none)</summary>
    public static InfoNode Detach(MapData md)
    {
        static InfoNode Copy(InfoNode n) { var c = new InfoNode { Name = n.Name, Value = n.Value }; foreach (var s in n.Subs) c.Subs.Add(Copy(s)); return c; }
        md.InfoTree = md.InfoTree != null ? Copy(md.InfoTree) : new InfoNode { Name = "Root" };
        return md.InfoTree;
    }

    /// <summary>sets "Root/A/B/C" to value in the tree (nodes made as needed) and in the flattened key/values; null removes
    /// the value. Call Detach once before a batch of writes.</summary>
    public static void Set(MapData md, string path, string? value)
    {
        if (value == null) md.Description.Remove(path); else md.Description[path] = value;
        var root = md.InfoTree ??= new InfoNode { Name = "Root" };
        var parts = path.Split('/');
        if (parts.Length < 2 || parts[0] != root.Name) return;
        var n = root;
        for (int i = 1; i < parts.Length; i++)
        {
            var sub = n.Sub(parts[i]);
            if (sub == null)
            {
                if (value == null) return;
                sub = new InfoNode { Name = parts[i] };
                n.Subs.Add(sub);
            }
            n = sub;
        }
        n.Value = value ?? "";
    }

    /// <summary>removes every key below a path from the flattened key/values and the tree</summary>
    public static void RemoveBelow(MapData md, string path)
    {
        foreach (var k in new List<string>(md.Description.Keys)) if (k.StartsWith(path + "/", System.StringComparison.Ordinal)) md.Description.Remove(k);
        var parts = path.Split('/');
        var n = md.InfoTree;
        if (n == null || parts[0] != n.Name) return;
        for (int i = 1; i < parts.Length - 1 && n != null; i++) n = n.Sub(parts[i]);
        n?.Subs.RemoveAll(s => s.Name == parts[^1]);
    }
}
