// The fog of war of a cutscene (player report, mission 16 "Showdown!": "the edges of the map are black, even during
// the cutscene").
//
// A cutscene's trigger (SQNZ) names the area it shows: disable_fow, fow_pos, fow_radius. The original's client stamps
// that circle into the fog every update while the scene runs (PWClient 0x44eb81: CSequenceInfo +0xa the flag, +0xc the
// position, +0x18 the radius) - whatever the position, the map's corner 0, 0 too. Mission 12's sc_4030 and sc_4038
// reveal "0.0, 0.0, 0.0" with radius 1000: every camera of both looks inside that circle. This port skipped a zero
// position, so those scenes showed unexplored ground. (The rest of the report - never seen ground drawn black instead
// of the original's fog clouds - is the view's: game/src/View/Terrain/FogOfWar.cs.)
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Scripting;

namespace PwRemake.Tests;

[TestClass]
public static class CutsceneFogTests
{
    /// <summary>sc_4030 of mission 12 (fow_pos 0, 0, 0, radius 1000): its cameras' targets are seen while it plays,
    /// and not after it (explored then); mission 16's sc_7050 (500, 500, radius 250): the crater's middle is seen</summary>
    public static void SequenceRevealsItsArea(TestContext t)
    {
        foreach (var (mission, seq, pos, radius) in new[]
        {
            (12, "Cpn_single_001/single_12/sc_4030.seq", new MapPos(0, 0, 0), 1000.0),
            (16, "Cpn_single_001/single_16/sc_7050.seq", new MapPos(500, 500, 50), 250.0),
        })
        {
            var s = CampaignTests.Session(t, "base", mission, "");
            var C = s.Campaign; var W = s.World;
            var fog = W.FogOf(C.Human) ?? W.EnableFog(C.Human!);
            W.FogUpdate();
            var data = C.Data.Sequences.First(kv => kv.Key.EndsWith(seq.Split('/').Last())).Value!;
            // the camera targets inside the circle (sc_4030: all of them)
            var targets = data["play"]!["cams"]!.AsObject().Select(kv => kv.Value!["target"]!.AsArray())
                .Select(a => (X: (double)a[0]!, Y: (double)a[1]!))
                .Where(p => (p.X - pos.X) * (p.X - pos.X) + (p.Y - pos.Y) * (p.Y - pos.Y) < (radius - 20) * (radius - 20)).ToList();
            t.True(targets.Count > 0, $"{seq}: cameras aim into its circle");
            int hidden = targets.Count(p => { var (x, z) = C.ToGame(p.X, p.Y); return !fog.Visible(x, z); });
            t.Log($"mission {mission} {seq}: {targets.Count} camera targets in the circle, {hidden} not seen before the scene");
            var r = new SequenceRequest { Path = seq, Fow = true, FowPos = pos, FowRadius = radius, Trigger = "test" };
            s.Host.PlaySequence(r);
            W.FogUpdate();
            foreach (var p in targets)
            {
                var (x, z) = C.ToGame(p.X, p.Y);
                t.True(fog.Visible(x, z), $"{seq}: the camera target ({p.X:0}, {p.Y:0}) is seen while the scene plays");
            }
            s.Host.SequenceFinished(r);
            W.FogUpdate();
            int seenAfter = targets.Count(p => { var (x, z) = C.ToGame(p.X, p.Y); return fog.Visible(x, z); });
            t.True(seenAfter <= targets.Count - hidden, $"{seq}: the scene's circle is gone at its end ({seenAfter} still seen)");
        }
    }
}
