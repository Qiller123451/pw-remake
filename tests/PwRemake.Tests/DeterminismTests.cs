// Determinism across machines (docs/MULTIPLAYER.md "Determinism"): what a lockstep game needs besides "the same
// commands in the same order" - the simulation's elementary functions give the same bits everywhere (FdLibm.cs, the
// port of fdlibm that V8 uses: compared bit for bit with node's Math on tests/golden/fdlibm.json), Lua's math and
// number strings do not depend on the machine's culture, and a game played under a Polish culture is the same game.
//
//   dotnet run --project tests/PwRemake.Tests -- Determinism
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;
using PwRemake.Sim.Game;
using PwRemake.Sim.Scripting;

namespace PwRemake.Tests;

[TestClass]
public static class DeterminismTests
{
    static string Golden(TestContext t, string file)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var g = Path.Combine(d.FullName, "tests", "golden", file);
            if (File.Exists(g)) return g;
        }
        t.Skip($"tests/golden/{file} not found");
        return "";
    }

    static double D(JsonNode? n) => BitConverter.Int64BitsToDouble((long)ulong.Parse(n!.GetValue<string>(), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
    static string H(double x) => ((ulong)BitConverter.DoubleToInt64Bits(x)).ToString("x16", CultureInfo.InvariantCulture);

    /// <summary>every function on the golden's arguments: the same bits as V8 (node) - beyond 2^19·π/2 sin / cos / tan
    /// reduce by fmod(x, 2π) instead of fdlibm's long reduction: there only close (both are deterministic)</summary>
    public static void FdLibmEqualsV8(TestContext t)
    {
        var g = JsonNode.Parse(File.ReadAllText(Golden(t, "fdlibm.json")))!;
        var f1 = new Dictionary<string, Func<double, double>>
        {
            ["sin"] = SMath.Sin, ["cos"] = SMath.Cos, ["tan"] = SMath.Tan, ["atan"] = SMath.Atan, ["asin"] = SMath.Asin, ["acos"] = SMath.Acos,
            ["exp"] = SMath.Exp, ["log"] = SMath.Log,
        };
        int n = 0, approx = 0;
        foreach (var (name, f) in f1)
            foreach (var row in g[name]!.AsArray())
            {
                double x = D(row![0]), want = D(row[1]), got = f(x);
                bool huge = name is "sin" or "cos" or "tan" && Math.Abs(x) > 823549;
                if (huge) { approx++; t.True(Math.Abs(got - want) < 1e-6 || double.IsNaN(want) && double.IsNaN(got), $"{name}({x:R}) ~ {want:R}, got {got:R}"); continue; }
                t.True(H(got) == H(want) || double.IsNaN(want) && double.IsNaN(got), $"{name}({x:R}) = {want:R} ({H(want)}), got {got:R} ({H(got)})");
                n++;
            }
        foreach (var row in g["atan2"]!.AsArray())
        {
            double y = D(row![0]), x = D(row[1]), want = D(row[2]), got = SMath.Atan2(y, x);
            t.True(H(got) == H(want), $"atan2({y:R}, {x:R}) = {want:R}, got {got:R}");
            n++;
        }
        // pow: V8's differs from fdlibm 5.3's e_pow.c in some detail - both are "nearly rounded" (measured against exact
        // values: at most 0.87 / 0.78 ulp, 9 % not correctly rounded, either of them): equal in ~95 % of the cases, never
        // more than one ulp apart
        int powSame = 0, powN = 0;
        foreach (var row in g["pow"]!.AsArray())
        {
            double b = D(row![0]), p = D(row[1]), want = D(row[2]), got = SMath.Pow(b, p);
            powN++;
            if (H(got) == H(want) || double.IsNaN(want) && double.IsNaN(got)) { powSame++; continue; }
            long u = Math.Abs(BitConverter.DoubleToInt64Bits(got) - BitConverter.DoubleToInt64Bits(want));
            t.True(u <= 1, $"pow({b:R}, {p:R}) = {want:R}, got {got:R}: {u} ulp apart");
        }
        t.True(powSame >= powN * 0.9, $"pow equal to V8's in {powSame} of {powN}");
        t.Log($"{n} results bit-equal to V8's, {approx} huge trigonometric arguments close; pow: {powSame} of {powN} equal, the rest one ulp apart");
    }

    /// <summary>against the machine's C runtime (System.Math): within one unit in the last place over a million arguments
    /// (fdlibm's error is below one ulp; the C runtime is correctly rounded or close) - the functions are right, not only
    /// equal to V8</summary>
    public static void FdLibmCloseToSystemMath(TestContext t)
    {
        var r = new SimRandom(7);
        long Ulps(double a, double b) => a == b ? 0 : Math.Abs(BitConverter.DoubleToInt64Bits(a) - BitConverter.DoubleToInt64Bits(b));
        var worst = new Dictionary<string, long>();
        var differ = new Dictionary<string, int>();
        void Check(string name, double a, double b)
        {
            long u = double.IsNaN(a) && double.IsNaN(b) ? 0 : Ulps(a, b);
            if (u > worst.GetValueOrDefault(name)) worst[name] = u;
            if (u > 0) differ[name] = differ.GetValueOrDefault(name) + 1;
        }
        const int N = 200_000;
        for (int i = 0; i < N; i++)
        {
            double x = (r.Next() - 0.5) * (i % 3 == 0 ? 10 : i % 3 == 1 ? 1000 : 1e5);
            Check("sin", SMath.Sin(x), Math.Sin(x)); Check("cos", SMath.Cos(x), Math.Cos(x)); Check("tan", SMath.Tan(x), Math.Tan(x));
            Check("atan", SMath.Atan(x), Math.Atan(x));
            double y = (r.Next() - 0.5) * 500;
            Check("atan2", SMath.Atan2(y, x), Math.Atan2(y, x));
            double u = r.Next() * 2 - 1;
            Check("asin", SMath.Asin(u), Math.Asin(u)); Check("acos", SMath.Acos(u), Math.Acos(u));
            double e = (r.Next() - 0.5) * 200;
            Check("exp", SMath.Exp(e), Math.Exp(e));
            double l = r.Next() * 1e4;
            Check("log", SMath.Log(l), Math.Log(l));
            double b = r.Next() * 50, p = (r.Next() - 0.5) * 8;
            Check("pow", SMath.Pow(b, p), Math.Pow(b, p));
        }
        foreach (var (k, v) in worst.OrderBy(kv => kv.Key)) t.Log($"{k}: at most {v} ulp from System.Math, {differ.GetValueOrDefault(k)} of {N} differ");
        foreach (var k in new[] { "sin", "cos", "tan", "atan", "atan2", "asin", "acos", "exp", "log", "pow" })
            t.True(worst.GetValueOrDefault(k) <= 1, $"{k}: within 1 ulp of System.Math (worst {worst.GetValueOrDefault(k)})");
    }

    /// <summary>Lua: math.sin & co. and the ^ operator are the deterministic functions; numbers become strings with a dot
    /// whatever the culture (a Polish Windows writes 1,5)</summary>
    public static void LuaMathAndCulture(TestContext t)
    {
        var std = LuaStd(t);
        var old = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("pl-PL");
            var lua = new LuaHost(std, null);
            var r = lua.RunString("return tostring(1.5) .. '|' .. (2.25 .. '') .. '|' .. string.format('%.2f', 0.5) .. '|' .. tonumber('3.5')", "t");
            t.Equal("1.5|2.25|0.50|3.5", r?.String, "numbers as strings under pl-PL");
            var s = lua.RunString("return math.sin(1.2345)", "t")!.Number;
            t.Equal(H(SMath.Sin(1.2345)), H(s), "math.sin is FdLibm's");
            var p = lua.RunString("return 1.37 ^ 2.71", "t")!.Number;
            t.Equal(H(SMath.Pow(1.37, 2.71)), H(p), "the ^ operator is FdLibm's pow");
            var lg = lua.RunString("return math.log(10)", "t")!.Number;
            t.Equal(H(SMath.Log(10)), H(lg), "math.log(x) is FdLibm's log");
        }
        finally { CultureInfo.CurrentCulture = old; }
    }

    /// <summary>a skirmish of computer players played twice - once under the invariant culture, once under pl-PL (the
    /// world's static constructor sets the invariant culture as the default; a thread that set its own is the risk): the
    /// same hashes</summary>
    public static void SkirmishUnderAnotherCulture(TestContext t)
    {
        var c = CampaignTests.LoadCfgPublic("base");
        if (c == null) t.Skip("no configuration base");
        ulong Play(string culture)
        {
            var old = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = new CultureInfo(culture);
                var s = Skirmish.DefaultSetup("Hu", "Aje"); s.Map = ""; s.Seed = 5; s.Slots[0].Type = "ai";
                var g = SkirmishSession.Create(s, null, c!.Rules, c.Models, c.Forest, c.Brushes, c.Ai);
                ulong h = 0;
                for (int i = 0; i < 1200; i++) { g.Step(); if (i % 200 == 0) h = h * 31 + g.StateHash(); }
                return h * 31 + g.StateHash();
            }
            finally { CultureInfo.CurrentCulture = old; }
        }
        t.Equal(Play(""), Play("pl-PL"), "the same game under the invariant culture and pl-PL");
    }

    internal static string LuaStd(TestContext t)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            var g = Path.Combine(d.FullName, "game", "lua", "std");
            if (Directory.Exists(g)) return g;
        }
        t.Skip("game/lua/std not found");
        return "";
    }
}
