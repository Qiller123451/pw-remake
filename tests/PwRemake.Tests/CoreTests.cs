using PwRemake.Sim.Core;

namespace PwRemake.Tests;

[TestClass]
public static class CoreTests
{
    public static void RandomIsDeterministic(TestContext t)
    {
        var a = new SimRandom(42); var b = new SimRandom(42);
        for (int i = 0; i < 1000; i++) t.Equal(a.NextU64(), b.NextU64(), "same seed, same numbers");
        var r = new SimRandom(7);
        for (int i = 0; i < 10000; i++) { double x = r.Next(); t.True(x >= 0 && x < 1, "Next in [0,1)"); }
    }

    public static void JsonNumbersLikeParseFloat(TestContext t)
    {
        t.Equal(12.5, Json.ParseNum("12.5"), "plain");
        t.Equal(3.0, Json.ParseNum("3abc"), "leading number");
        t.Equal(-1.0, Json.ParseNum("x", -1), "default");
    }
}
