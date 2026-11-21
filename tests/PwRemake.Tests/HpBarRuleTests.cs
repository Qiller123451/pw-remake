// The unit cards' hit point bar colours (game/src/UI/Hud/Logic/HpBarRule.cs, docs/HUD.md "Hit point bars"): the
// engine's hslicehp gradient (PWClient.exe 0x49bc1a), the slice width and the mask test of hslicebar / hslicehp, the
// owner colours of the option off (Info.usl 906-922).
using PwRemake.UI.Hud.Logic;

namespace PwRemake.Tests;

[TestClass]
public static class HpBarRuleTests
{
    public static void HealthGradient(TestContext t)
    {
        t.Equal(0x05ff00, HpBarRule.HealthRgb(100), "full: green (5, 255, 0)");
        t.Equal(0xffff00, HpBarRule.HealthRgb(50), "half: yellow (255, 255, 0)");
        t.Equal(0xfff500, HpBarRule.HealthRgb(49), "just under half: (255, 245, 0)");
        t.Equal(0xff7d00, HpBarRule.HealthRgb(25), "a quarter: orange (255, 125, 0)");
        t.Equal(0xff0000, HpBarRule.HealthRgb(0), "none: red");
        t.Equal(0x05ff00, HpBarRule.HealthRgb(150), "clamped to 100");
        t.Equal(0xff0000, HpBarRule.HealthRgb(-5), "clamped to 0");
        t.Equal(0x05ff00, HpBarRule.HealthRgb(5000, 5000), "a full building (5000 / 5000): green");
        for (int p = 0; p < 100; p++)
        {
            int a = HpBarRule.HealthRgb(p), b = HpBarRule.HealthRgb(p + 1);
            t.True((b >> 16) <= (a >> 16) && ((b >> 8) & 255) >= ((a >> 8) & 255), $"{p} -> {p + 1}: never redder");
            t.Equal(0, a & 255, $"{p}: no blue");
        }
    }

    public static void SliceAndMask(TestContext t)
    {
        t.Equal(100, HpBarRule.Slice(5000, 5000), "full");
        t.Equal(49, HpBarRule.Slice(2499, 5000), "integer division (iHP * 100) / iMaxHP");
        t.Equal(1, HpBarRule.Slice(0.3, 100), "a sliver of hit points: rounded up as the shown value");
        t.Equal(0, HpBarRule.Slice(10, 0), "no max");
        t.Equal(64, HpBarRule.SliceWidth(64, 100), "full width");
        t.Equal(31, HpBarRule.SliceWidth(64, 49), "64 * 49 / 100");
        t.Equal(0, HpBarRule.SliceWidth(64, 0), "nothing");
        t.True(HpBarRule.IsMask(255, 64, 0), "ovl_hpbar's light texel is mask");
        t.True(HpBarRule.IsMask(211, 25, 0), "ovl_hpbar's dark texel is mask");
        t.True(!HpBarRule.IsMask(58, 7, 30), "R not over 2 (G + B): kept");
        t.True(!HpBarRule.IsMask(204, 102, 51), "an orange texel: kept");
    }

    public static void OwnerColours(TestContext t)
    {
        t.Equal(0xcc6633, HpBarRule.OwnerRgb(7, 0, false, 0), "orange: ACColors color7 dark 204 102 51");
        t.Equal(0x3366cc, HpBarRule.OwnerRgb(3, 0, false, 0), "blue: color3 dark");
        t.Equal(0x123456, HpBarRule.OwnerRgb(-1, 0x123456, false, 0), "a custom colour: the player's own");
        t.Equal(HpBarRule.DARK_RESOURCES, HpBarRule.OwnerRgb(null, 0, true, 0), "resources: color11");
        t.Equal(HpBarRule.DARK_ANIMAL_NEUTRAL, HpBarRule.OwnerRgb(null, 0, false, 0), "anml_aggressive_type 0: color9");
        t.Equal(HpBarRule.DARK_ANIMAL_HOSTILE, HpBarRule.OwnerRgb(null, 0, false, 1), "1: color10");
        t.Equal(HpBarRule.DARK_ANIMAL_FRIENDLY, HpBarRule.OwnerRgb(null, 0, false, -1), "else: color8");
        t.Equal(-1, HpBarRule.CounterRgb(8), "not one of the original's eight: hslicehp");
        t.Equal(0xe5c63c, HpBarRule.CounterRgb(7), "orange's counter colour");
    }
}
