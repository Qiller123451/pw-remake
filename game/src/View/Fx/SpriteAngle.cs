// The turn of a particle sprite that points the way it flies (flag 0x20000000), from PWClient.exe - no Godot types,
// so the tests can check it (tests/PwRemake.Tests ParticleAngleTests).
//
// ProcPart 0x707e30 (0x7085a9 .. 0x7085f4): both ends of the particle's step, pos and pos + v, go through the view and
// projection matrices; angle = atan2(dx, dy) of the difference on the screen, + pi for a sprite lying on the ground
// (0x40000000), + "rotate"; stored as the particle's turn (a step shorter than 0.001 keeps the last turn). The renderer
// (0x7061b1) builds the screen sprite's corners in a frame whose y points DOWN the screen (the corner of a cell's top
// edge has the smaller y: a pivot at the cell's top, mid_y 0, gives corners at y 0 .. 2h), turns them by -angle
// (x' = c x - s y, y' = s x + c y with c, s of -angle) and adds them to the particle's place. So the client's dy is
// measured downwards and its turn is counter-clockwise on the screen. In the port's terms (view space, y up; the
// sprite's quad turned counter-clockwise by rot, the cell's top at +y):
//
//   rot = atan2(dx, -dy) + rotate
//
// - "rotate" 0: the cell's top points back along the flight (a spark's trail: fx_impact_04 / 05);
// - "rotate" pi (the healing aura's rays, the level-up's and the spirits' rays, the splashes of water, blood and the
//   hits' streaks - their pivot at the cell's foot, mid_y 237 .. 249): the cell's top points the way it flies, so a
//   ray rising from the ground stands on its foot;
// - "rotate" -1.61 (the gatlings' muzzle flashes): a quarter turn, as before.
//
// The browser remake's particles.js (and this port until 2026-11-05) used atan2(-dx, dy) - rotate: the same for a
// quarter turn (the muzzle flashes, which that formula was checked on) but half a turn off for 0 and pi - every ray
// and splash with rotate pi hung from its foot downwards, into the ground (user report: the healing area of the SEAS
// fortress lacked its yellow vertical lines).
using System;

namespace PwRemake.View.Fx;

public static class SpriteAngle
{
    /// <summary>a step this short on the screen (squared) gives no direction: the particle keeps its last turn</summary>
    public const float MIN_SQ = 1e-12f;

    /// <summary>the sprite's turn (counter-clockwise, radians) for a flight (dx, dy) on the screen (view space, y up)
    /// and the emitter's "rotate"; null: no direction (keep the particle's own turn)</summary>
    public static float? Screen(float dx, float dy, float rotate)
    {
        if (dx * dx + dy * dy <= MIN_SQ) return null;
        return MathF.Atan2(dx, -dy) + rotate;
    }

    /// <summary>where the top of a sprite's cell points on the screen (unit vector, y up) at turn rot</summary>
    public static (float X, float Y) Top(float rot) => (-MathF.Sin(rot), MathF.Cos(rot));
}
