// Tests of the turn of particle sprites that point the way they fly (flag 0x20000000; game/src/View/Fx/SpriteAngle.cs,
// linked into this project): PWClient.exe measures the flight's angle with the screen's y downwards and turns the
// sprite counter-clockwise, so with "rotate" pi the cell's top points along the flight - the rays of the healing area
// (fx_heal_area: pivot at the cell's foot, flying up) stand on the ground. The old formula atan2(-dx, dy) - rotate hung
// them from their foot into the ground (user report: the SEAS fortress' healing area lacked its yellow vertical lines).
using System;
using PwRemake.View.Fx;

namespace PwRemake.Tests;

[TestClass]
public static class ParticleAngleTests
{
    static (float X, float Y) TopOf(float dx, float dy, float rotate) => SpriteAngle.Top(SpriteAngle.Screen(dx, dy, rotate)!.Value);

    public static void RaysWithRotatePiPointTheWayTheyFly(TestContext t)
    {
        const float PI = 3.14f;          // (the files' "rotate" 3.14)
        var up = TopOf(0, 1, PI);
        t.True(up.Y > 0.99f, $"a ray rising on the screen stands up (top {up.X:0.00}, {up.Y:0.00})");
        var right = TopOf(1, 0, PI);
        t.True(right.X > 0.99f, $"a streak flying right points right (top {right.X:0.00}, {right.Y:0.00})");
        var diag = TopOf(1, 1, PI);
        t.True(diag.X > 0.69f && diag.Y > 0.69f, $"up and right: the top points up and right (a turn, not a mirror: {diag.X:0.00}, {diag.Y:0.00})");
        var down = TopOf(0.2f, -1, PI);
        t.True(down.Y < -0.9f, "falling: the top points down");
    }

    public static void RotateZeroTrailsBehind(TestContext t)
    {
        // fx_impact_04 / 05 sparks (rays.png, rotate 0, pivot at the foot): the cell's top points back, a trail
        var up = TopOf(0, 1, 0);
        t.True(up.Y < -0.99f, $"rotate 0, rising: the top points back down ({up.Y:0.00})");
        var left = TopOf(-1, 0, 0);
        t.True(left.X > 0.99f, $"rotate 0, flying left: the top points right ({left.X:0.00})");
    }

    public static void MuzzleFlashesKeepTheirQuarterTurn(TestContext t)
    {
        // the gatlings' muzzle flashes (rotate -1.61): the old formula atan2(-dx, dy) - rotate was checked on them; the
        // client's angle differs from it by pi - 2 * 1.61 = -0.08 rad only
        foreach (var (dx, dy) in new[] { (1f, 0f), (0f, 1f), (-0.6f, 0.8f), (0.3f, -0.95f) })
        {
            float now = SpriteAngle.Screen(dx, dy, -1.61f)!.Value, old = MathF.Atan2(-dx, dy) + 1.61f;
            float diff = MathF.IEEERemainder(now - old, 2 * MathF.PI);
            t.Near(-0.08, diff, 0.01, $"flash ({dx}, {dy}): within 0.08 rad of before");
        }
    }

    public static void NoDirectionKeepsTheTurn(TestContext t)
    {
        t.True(SpriteAngle.Screen(0, 0, 3.14f) == null, "a particle that does not move on the screen keeps its own turn");
    }
}
