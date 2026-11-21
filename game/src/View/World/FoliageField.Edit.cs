// Sprites added after the field was built (the map editor: trees and plants painted, docs/MAP_EDITOR.md): Rebuild makes
// the multimeshes of the groups that grew again; Remove hides a handle's sprites for good (their size 0 also in the
// lists, so a rebuilt group does not bring them back).
using System;

namespace PwRemake.View.World;

public partial class FoliageField
{
    /// <summary>hides the sprites of a handle, also for later rebuilds of its group</summary>
    public void Remove(FoliageHandle? h)
    {
        if (h == null) return;
        Hide(h);
        for (int i = h.Start; i < h.Start + h.Count && i < h.G.List.Count; i++) { var s = h.G.List[i]; s.Size = 0; h.G.List[i] = s; }
        for (int i = h.DStart; i < h.DStart + h.DCount && i < h.G.DList.Count; i++) { var s = h.G.DList[i]; s.Size = 0; h.G.DList[i] = s; }
    }

    /// <summary>the groups with sprites added since they were built get their multimeshes anew; new groups theirs</summary>
    public void Rebuild()
    {
        foreach (var g in _groups.Values)
        {
            if (g.Mesh != null && g.Mesh.Multimesh.InstanceCount != g.List.Count) { RemoveChild(g.Mesh); g.Mesh.QueueFree(); g.Mesh = null; }
            if (g.DMesh != null && g.DMesh.Multimesh.InstanceCount != g.DList.Count) { RemoveChild(g.DMesh); g.DMesh.QueueFree(); g.DMesh = null; }
        }
        Build();
    }
}
