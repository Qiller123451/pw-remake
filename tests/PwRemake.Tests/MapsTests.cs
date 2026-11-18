// Unit tests of the map code (src/Sim/Maps) that need no golden data: number conversions, the coordinate frame,
// regions, the slope rule, the queries of MapData / MapSource. MapsGoldenTests compares everything with the JS.
using System;
using System.IO;
using System.Linq;
using PwRemake.Sim.Maps;

namespace PwRemake.Tests;

[TestClass]
public static class MapsTests
{
    const string Cpn = "/mnt/user-data/uploads/Paraworld/Data/Base/Maps/Cpn_single_001";

    public static void JsNumbersLikeUnaryPlus(TestContext t)
    {
        t.Equal(0.0, JsNum.ToNumber(""), "'' = 0");
        t.Equal(0.0, JsNum.ToNumber("  "), "blank = 0");
        t.Equal(12.5, JsNum.ToNumber(" 12.5 "), "trimmed");
        t.Equal(16.0, JsNum.ToNumber("0x10"), "hex");
        t.True(double.IsNaN(JsNum.ToNumber("3abc")), "not parseFloat: 3abc = NaN");
        t.True(double.IsNaN(JsNum.ToNumber(null)), "undefined = NaN");
        t.Equal(0.5, JsNum.ToNumber(".5"), ".5");
        t.Equal(1000.0, JsNum.ToNumber("1e3"), "1e3");
        t.Equal(2.0, JsNum.Or("abc", 2), "+'abc' || 2");
        t.Equal(2.0, JsNum.Or("0", 2), "+'0' || 2");
    }

    public static void DayTimeAsServerApp(TestContext t)
    {
        t.Equal(12 * 3600.0, Ula.DayTime(null), "missing = 12:00");
        t.Equal(12 * 3600.0, Ula.DayTime(""), "empty = 12:00");
        t.Equal(6 * 3600.0 + 30 * 60, Ula.DayTime("6:30"), "6:30");
        t.Equal(8.5 * 3600, Ula.DayTime("noon"), "malformed = 8:30");
        t.Equal(8.5 * 3600, Ula.DayTime("1:2:3"), "three parts = 8:30");
    }

    public static void Latin1IsWindows1252(TestContext t)
    {
        t.Equal("€ä", Latin1.Decode(new byte[] { 0x80, 0xE4 }), "0x80 = euro, 0xE4 = a umlaut");
    }

    public static void FrameHeadingAndQuaternion(TestContext t)
    {
        t.Near(Math.PI, Frame.MapHeading(0), 1e-15, "rot 0 faces +z (pi)");
        t.Near(Math.PI, Frame.MapHeading(double.NaN), 1e-15, "NaN = 0");
        // a turn by a about the map's z: stored q = (0, 0, sin(-a/2), cos(-a/2)) (rot = -2 atan2(qz, qw) = a)
        double a = 0.7;
        double[] q = { 0, 0, Math.Sin(-a / 2), Math.Cos(-a / 2) };
        double rot = -2 * Math.Atan2(q[2], q[3]);
        t.Near(a, rot, 1e-12, "rot from q");
        var w = Frame.MapQuat(q);
        // a rotation about y by heading h is (0, sin(h/2), 0, cos(h/2)) up to sign
        double h = Frame.MapHeading(rot);
        double s = Math.Sign(w[1]) == Math.Sign(Math.Sin(h / 2)) ? 1 : -1;
        t.Near(0, w[0], 1e-12, "x"); t.Near(0, w[2], 1e-12, "z");
        t.Near(Math.Sin(h / 2), s * w[1], 1e-12, "y = sin(h/2)"); t.Near(Math.Cos(h / 2), s * w[3], 1e-12, "w = cos(h/2)");
    }

    public static void RegionShapes(TestContext t)
    {
        var r = new MapRegion { Name = "R" };
        r.Shapes.Add(new RegionShape { X = 0, Y = 0, W = 10, H = 10 });
        r.Shapes.Add(new RegionShape { Oval = true, X = 100, Y = 100, W = 20, H = 10 });
        r.Shapes.Add(new RegionShape { X = 200, Y = 200, W = 10, H = 10, Enabled = false });
        t.True(r.ContainsMap(5, 5), "in the rectangle");
        t.True(r.ContainsMap(10, 10), "on its edge");
        t.True(r.ContainsMap(110, 105), "in the oval's centre");
        t.True(!r.ContainsMap(101, 101), "in the oval's box corner, not in the oval");
        t.True(!r.ContainsMap(205, 205), "a disabled shape");
    }

    public static void SlopeRule(TestContext t)
    {
        var flat = MapSource.SlopeWalkable((x, z) => 10);
        var steep = MapSource.SlopeWalkable((x, z) => x);                // 45 degrees
        var gentle = MapSource.SlopeWalkable((x, z) => x * 0.5);         // 26.6 degrees
        t.True(flat(0, 0), "flat ground"); t.True(!steep(0, 0), "45 degrees"); t.True(gentle(0, 0), "26 degrees (limit 32)");
        t.True(!MapSource.SlopeWalkable((x, z) => x * 0.5, 22)(0, 0), "26 degrees with a 22 degree limit");
    }

    public static void GeneratedMapBasics(TestContext t)
    {
        var S = MapSource.GeneratedSource();
        t.Equal("generated", S.Kind, "kind");
        t.Equal(640.0, S.Size, "world size");
        t.Equal(2, S.Starts.Count, "two bases");
        t.True(S.Trees.Count > 1000, "a jungle");
        t.Near(1.5, S.Height(-150, 150), 1e-9, "the base ground is flat at 1.5 m");
        t.True(S.Walkable == null, "no walk grid: the slope rule");
        t.True(S.WalkTest()(-150, 150), "the base is walkable");
        var S4 = MapSource.GeneratedSource(new MapSourceConfig { Players = 4 });
        t.Equal(4, S4.Starts.Count, "four bases");
        t.Equal(S.Trees.Count == S4.Trees.Count && S.Trees[0].X == S4.Trees[0].X, false, "other bases, other forest");
    }

    public static void OriginalMapQueries(TestContext t)
    {
        string f = Path.Combine(Cpn, "single_01.ula");
        if (!File.Exists(f)) t.Skip(f + " not staged");
        var md = Ula.ParseUlaFile(f);
        t.True(md.Hx * 2 >= md.W - 32 && md.Heights.Length == md.Hx * md.Hy, "height grid");
        t.True(md.Walk != null, "the campaign map has its walk grid");
        var S = MapSource.OriginalSource(md, new NoRules(), null, _ => false);
        // the map's corner (0, 0) is game (-ox, -oy); a grid sample is the height there
        var (gx, gz) = S.ToGame(2 * 10, 2 * 7);
        t.Near(md.Heights[7 * md.Hx + 10], S.Height(gx, gz), 1e-9, "height at a sample");
        t.Near(md.HeightAtMap(123.4, 456.7), S.Height(S.ToGame(123.4, 456.7).x, S.ToGame(123.4, 456.7).z), 1e-9, "MapData and MapSource agree");
        var (mx, my) = S.ToMap(gx, gz);
        t.Near(20, mx, 1e-12, "ToMap(ToGame) x"); t.Near(14, my, 1e-12, "ToMap(ToGame) y");
        // water depth: below the water level somewhere, dry elsewhere
        int wet = 0, dry = 0;
        for (double x = -S.Size / 2; x < S.Size / 2; x += 16) for (double z = -S.Size / 2; z < S.Size / 2; z += 16)
        {
            double d = S.WaterDepth(x, z);
            t.True(d >= 0, "depth >= 0");
            if (d > 0) { wet++; t.Near(S.Water!.Value - S.Height(x, z), d, 1e-12, "depth = water - ground"); } else dry++;
            t.Equal(md.WalkableMap(S.ToMap(x, z).mx, S.ToMap(x, z).my), S.Walkable!(x, z), "walk grid in both coordinates");
        }
        t.True(wet > 0 && dry > 0, $"water and land ({wet} / {dry})");
        foreach (var r in md.Regions.Where(r => r.Shapes.Any(s => s.Enabled && !s.Oval)).Take(5))
        {
            var s = r.Shapes.First(s => s.Enabled && !s.Oval);
            var (cx, cz) = S.ToGame(s.X + s.W / 2, s.Y + s.H / 2);
            t.True(S.RegionContains(r.Name, cx, cz), $"region {r.Name} contains its shape's centre");
        }
    }

    sealed class NoRules : IMapRules
    {
        public string? ClassGfx(string name) => null;
        public string? Script(string name) => null;
        public double? ResourceValue(string section, string name) => null;
        public bool Exists(string name) => false;
        public string? StatsGfx(string name) => null;
        public string? NestSpecies(string nestClassLower) => null;
    }
}
