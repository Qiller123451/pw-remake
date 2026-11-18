// The temple guards' tents of mission 3 (MIRAGE's map: aje_tent_44..46 of the player "Temple guards",
// _PN_SP03_GUARDS) can be clicked and hovered as in the original (Kacper, 2026-11-11), though the map clears their
// selectable and hitable bits: Campaign.SELECTABLE_ANYWAY sets the selectable bit only. They stay unhittable and the
// guards' (the player cannot attack them or give them orders). Base mission 3's guards have no tents.
using System.Linq;
using PwRemake.Sim.World;

namespace PwRemake.Tests;

[TestClass]
public static class TempleGuardTentsTests
{
    public static void Mirage03TentsSelectable(TestContext t)
    {
        var s = CampaignTests.Session(t, "mirage", 3);
        var C = s.Campaign; var W = s.World;
        var tents = new[] { "aje_tent_44", "aje_tent_45", "aje_tent_46" }.Select(n => C.Objects.All.FirstOrDefault(r => r.Name == n)?.Entity as Building).ToList();
        if (tents.Any(b => b == null)) { t.Skip("the temple guards' tents are not on this map"); return; }
        foreach (var b in tents)
        {
            t.Equal("Temple guards", b!.Owner?.Name, $"{b.Rec!.Name}: the temple guards'");
            t.True(!b.Unselectable && b.Rec.Selectable, $"{b.Rec.Name}: selectable (info window, tooltip)");
            t.True(b.Untargetable && !b.Rec.Hitable, $"{b.Rec.Name}: still not hitable");
            var war = W.SpawnUnit("hu_warrior", C.Human, b.Pos.X + b.Radius + 3, b.Pos.Z, 3)!;
            t.True(!W.CanTarget(war, b), $"{b.Rec.Name}: the player's fighters cannot attack it");
        }
    }
}
