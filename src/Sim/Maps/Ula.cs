// Reader for original ParaWorld map files (.ula) - multiplayer maps, campaign maps and community maps.
// Format notes: docs/MAP_FORMAT.md of the toolkit (reverse engineered; the Python twin is pwexport/ula.py, the formal
// description pwexport/data/ksy/paraworld_surf.ksy).
//
//   container : u32 2, u32 total size, u32 block count N, u32 ?, N x {u32 ?, u32 size, u32 packed size, u32 1},
//               then N zlib streams (256 KB blocks)
//   'SURF'    : chunk directory - LInf (level info), Terr (terrain), Objs (placed objects), IOMG (landscape
//               decoration instances), PaFi, Rgns, Frst, GWFl{GrWa, Flck}, Trgr, Ques, DlgS, AI
//   Terr      : u32 1, u32 width, u32 height (metres), u32 blocks x, u32 blocks y, u32 setting, f32 water level,
//               u32 1, u8 0, u32 count, heights u16[count] (1/128 m, 2 m grid, stored in 16x16 blocks),
//               u32 count, materials u8[count] (4 m grid, 8x8 blocks; index into the setting's 8 ground materials)
//   PaFi      : the pathfinder's grids, 2 m cells (w/2 x h/2): u32 W, H, 2 numbers, then the land grid as
//               u32 W, H, words + words x u32 (one bit per cell, row-major, lowest bit first; 1 = walkable terrain),
//               then more grids (water, one per start location with the objects) - see Pathing()
//
// ParseUla(bytes) -> MapData (MapData.cs). Coordinates are map coordinates: x east 0..w, y south 0..h, z up (metres;
// left-handed, docs/MAP_FORMAT.md).
// Ported from remake/src/game/maps/ula.js (pw-exporter 98c7703).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Maps;

public static class Ula
{
    static readonly Logger L = Log.For("Maps.Ula");

    public static readonly string[] SETTINGS = { "Northland", "Savanna", "Jungle", "Icewaste", "Ashvalley", "TestSet", "Cave1", "Cave2", "Cave3" };
    // ground texture sets the remake has (tools/build_terrain.py); other settings fall back to the closest one
    // (the caves have their own sets: Cave2 is the volcano)
    public static readonly IReadOnlyDictionary<string, string> KNOWN_SETTINGS = new Dictionary<string, string>
    {
        ["Northland"] = "Northland", ["Savanna"] = "Savanna", ["Jungle"] = "Jungle", ["Icewaste"] = "Icewaste", ["Ashvalley"] = "Ashvalley",
        ["Cave1"] = "Cave1", ["Cave2"] = "Cave2", ["Cave3"] = "Cave3", ["TestSet"] = "Jungle",
    };

    static byte[] Inflate(ByteView bytes)
    {
        // (the JS DecompressionStream('deflate'): a zlib stream; what came out before a damaged end is kept)
        using var src = new MemoryStream(bytes.A, bytes.Off, bytes.Length, false);
        using var z = new ZLibStream(src, CompressionMode.Decompress);
        using var dst = new MemoryStream();
        try { z.CopyTo(dst); }
        catch (InvalidDataException e) { L.Debug($"damaged zlib block: {e.Message}"); }
        return dst.ToArray();
    }

    /// <summary>container -> one byte array</summary>
    public static byte[] Unpack(byte[] buf)
    {
        var dv = new ByteView(buf);
        uint total = dv.U32(4), n = dv.U32(8);
        var blocks = new List<(uint size, uint packed)>();
        int o = 16;
        for (uint i = 0; i < n; i++) { blocks.Add((dv.U32(o + 4), dv.U32(o + 8))); o += 16; }
        var output = new byte[total];
        long w = 0;
        foreach (var (size, packed) in blocks)
        {
            // the zlib header: 0x78 and a valid check (CMF*256+FLG divisible by 31) - a stray 0x78 before the first block
            // (seas_map.ula: 4 bytes between the block table and the data, "78 ae d6 e4") is not one
            while (o + 1 < buf.Length && !(buf[o] == 0x78 && ((buf[o] << 8) | buf[o + 1]) % 31 == 0)) o++;
            var part = Inflate(dv.Sub(o, (long)o + packed));
            long len = Math.Min(Math.Min(size, total - w), part.Length);
            if (len > 0 && w < total) Array.Copy(part, 0, output, w, len);
            w += size; o = (int)Math.Min(int.MaxValue, (long)o + packed);
        }
        return output;
    }

    /// <summary>SURF directory -> {tag: bytes}</summary>
    public static Dictionary<string, ByteView> Chunks(ByteView b)
    {
        var output = new Dictionary<string, ByteView>();
        uint end = b.U32(4);
        long o = 16;
        while (o + 16 <= end)
        {
            string tag = Latin1.Decode(b.Sub(o, o + 4)).TrimEnd('\0');
            if (b.U32((int)o + 8) == 0x2a)
            {
                uint size = b.U32((int)o + 16), off = b.U32((int)o + 20);
                output[tag] = b.Sub(off, (long)off + size);
                o += 24;
            }
            else o += 16;                                                         // group header, children follow
        }
        return output;
    }

    // ---------------------------------------------------------------- LInf
    sealed class Reader
    {
        public readonly ByteView B;
        public int O;
        public Reader(ByteView b, int o = 0) { B = b; O = o; }
        public uint U32() { uint v = B.U32(O); O += 4; return v; }
        public double F32() { double v = B.F32(O); O += 4; return v; }
        /// <summary>u32 length with the closing 0, then the characters (up to the first 0)</summary>
        public string Str()
        {
            uint l = U32();
            var raw = B.Sub(O, (long)O + l);
            O = (int)Math.Min(int.MaxValue, (long)O + l);
            int z = raw.IndexOf(0);
            return Latin1.Decode(z >= 0 ? raw.Sub(0, z) : raw);
        }
    }

    sealed class LevelInfoResult
    {
        public Dictionary<string, string> Kv = new();
        public MapPreview? Preview;
        public Dictionary<string, string> Description = new();
        public InfoNode? Tree;
    }

    // LInf (pwexport/ula.py level_info): u32 count + count x (key, value) length-prefixed strings; 8 slot numbers and 8
    // player key/value lists; the 200 x 200 preview picture of the map list (stored B G R A; drawn by the engine's
    // minimap renderer: docs/MINIMAP.md); the description tree ("Root/Base/DefaultCamera" ...). Strings: u32 length
    // with the closing 0, then the characters.
    static LevelInfoResult LevelInfo(ByteView b)
    {
        var r = new Reader(b);
        Dictionary<string, string> Kvs()
        {
            uint n = r.U32();
            var kv = new Dictionary<string, string>();
            for (uint i = 0; i < n && r.O < b.Length; i++) { string k = r.Str(); kv[k] = r.Str(); }
            return kv;
        }
        var output = new LevelInfoResult { Kv = Kvs() };
        try
        {
            r.O += 8 * 4;                                                         // slot numbers
            for (int i = 0; i < 8; i++) Kvs();                                    // player slots
            r.O += 1;
            uint w = r.U32(), h = r.U32(), npix = r.U32(), nbytes = r.U32(), bpp = r.U32();
            if ((ulong)w * h == npix && (ulong)npix * bpp == nbytes && bpp == 4 && (long)r.O + nbytes <= b.Length)
            {
                var px = b.Sub(r.O, (long)r.O + nbytes).ToArray();
                for (int i = 0; i + 3 < px.Length; i += 4) (px[i], px[i + 2]) = (px[i + 2], px[i]);
                output.Preview = new MapPreview { W = (int)w, H = (int)h, Rgba = px };
            }
            r.O = (int)Math.Min(int.MaxValue, (long)r.O + nbytes + 16);
            int depth = 0;
            InfoNode Tree(string prefix)
            {
                // (the JS recursion ends in a RangeError on a damaged tree, caught below; C# cannot catch a stack overflow)
                if (++depth > 4000) throw new InvalidDataException("description tree too deep");
                uint n = r.U32();
                string name = r.Str(), value = r.Str(), key = prefix.Length > 0 ? prefix + "/" + name : name;
                if (value.Length > 0) output.Description[key] = value;
                var node = new InfoNode { Name = name, Value = value };
                for (uint i = 0; i < n; i++) node.Subs.Add(Tree(key));
                depth--;
                return node;
            }
            if (r.O + 12 <= b.Length) output.Tree = Tree("");
            foreach (var k in new List<string>(output.Description.Keys))
            {
                string s = k.Substring(k.LastIndexOf('/') + 1);
                if (!output.Description.ContainsKey(s)) output.Description[s] = output.Description[k];
            }
        }
        catch (Exception)
        {
            /* an older map without the rest: the key/values are enough */
        }
        return output;
    }

    // ---------------------------------------------------------------- Terr
    static MapData Terrain(ByteView b)
    {
        int w = (int)b.U32(4), h = (int)b.U32(8);
        int bx = (int)b.U32(12), by = (int)b.U32(16);
        uint setting = b.U32(20);
        double water = b.F32(24);
        int o = 33;
        uint hc = b.U32(o); o += 4;
        int hx = bx * 16, hy = by * 16;
        if (hc != (uint)(hx * hy)) throw new InvalidDataException($"unexpected terrain size {hc} ({hx}x{hy})");
        // heights: 16x16 blocks, row-major blocks, row-major samples inside a block
        var heights = new float[hx * hy];
        for (int B = 0; B < bx * by; B++)
        {
            int X0 = (B % bx) * 16, Y0 = B / bx * 16;
            for (int j = 0; j < 16; j++) for (int i = 0; i < 16; i++) { heights[(Y0 + j) * hx + X0 + i] = (float)(b.U16(o) / 128.0); o += 2; }
        }
        uint mc = b.U32(o); o += 4;
        int mx = bx * 8, my = by * 8;
        var mats = new byte[mx * my];
        if (mc == (uint)(mx * my))
        {
            for (int B = 0; B < bx * by; B++)
            {
                int X0 = (B % bx) * 8, Y0 = B / bx * 8;
                for (int j = 0; j < 8; j++) for (int i = 0; i < 8; i++) mats[(Y0 + j) * mx + X0 + i] = (byte)Math.Max(0, b.At(o++));
            }
        }
        return new MapData
        {
            W = w, H = h, TerrainSetting = setting < SETTINGS.Length ? SETTINGS[setting] : "Jungle", Water = water,
            Hx = hx, Hy = hy, Heights = heights, Mx = mx, My = my, Mats = mats,
        };
    }

    // ---------------------------------------------------------------- pathfinding grid
    // The map editor stores the pathfinder's grids with the map (chunk PaFi, docs/MAP_FORMAT.md). The first one says
    // where the terrain alone can be walked on: not on steep slopes (mountains, cliffs), not in the water. Cell (i, j)
    // covers map x 2i .. 2i + 2, y 2j .. 2j + 2. The per-start-location grids after it also leave out the placed
    // objects (the game blocks those itself). null when the map has none or it does not fit the terrain.
    public static WalkGrid? Pathing(ByteView? bv, int w, int h)
    {
        if (bv is not ByteView b || b.Length < 28) return null;
        uint W = b.U32(0), H = b.U32(4), words = b.U32(24);
        if (W != (uint)(w / 2) || H != (uint)(h / 2) || b.U32(16) != W || b.U32(20) != H) return null;
        if (words != (uint)Math.Ceiling(W * (double)H / 32) || 28 + (long)words * 4 > b.Length) return null;
        var cells = new byte[W * H];
        for (int k = 0; k < W * H; k++) cells[k] = (byte)((b[28 + (k >> 3)] >> (k & 7)) & 1);
        return new WalkGrid { W = (int)W, H = (int)H, Cells = cells };
    }

    // ---------------------------------------------------------------- objects (see tools/maps/objects.py)
    static (string s, int next)? LStr(ByteView b, int o)
    {
        if (o + 4 > b.Length) return null;
        uint n = b.U32(o);
        if (n < 1 || n > 200 || o + 4 + n > b.Length || b.At(o + 3 + (int)n) != 0) return null;
        for (int k = o + 4; k < o + 3 + n; k++) if (b[k] < 32 || b[k] > 126) return null;
        return (Latin1.Decode(b.Sub(o + 4, o + 3 + n)), o + 4 + (int)n);
    }

    static Dictionary<string, string> Attrs(ByteView b, int a, int z)
    {
        for (int o = a; o < Math.Min(z, b.Length - 8); o++)
        {
            uint c = b.U32(o);
            if (c < 1 || c > 300) continue;
            int p = o + 4;
            var kv = new Dictionary<string, string>();
            int ok = 0;
            for (int i = 0; i < c; i++)
            {
                var k = LStr(b, p);
                if (k == null) break;
                p = k.Value.next;
                var v = LStr(b, p);
                if (v == null)
                {
                    if (p + 5 <= b.Length && b.U32(p) == 1 && b.At(p + 4) == 0) v = ("", p + 5);
                    else break;
                }
                kv[k.Value.s] = v.Value.s; p = v.Value.next; ok++;
            }
            if (ok == c) return kv;
        }
        return new Dictionary<string, string>();
    }

    static readonly Regex NumberSuffix = new(@"_[0-9]+\z", RegexOptions.CultureInvariant);

    static List<MapObject> Objects(ByteView b, int w, int h)
    {
        var output = new List<MapObject>();
        static bool Up(int c) => c >= 65 && c <= 90;
        for (int i = 0; i + 45 < b.Length; i++)
        {
            if (!(Up(b[i]) && Up(b[i + 1]) && Up(b[i + 2]) && Up(b[i + 3]))) continue;
            double x = b.F32(i + 9), y = b.F32(i + 13), z = b.F32(i + 17);
            if (!(x >= 0 && x <= w && y >= 0 && y <= h && z > -50 && z < 400)) continue;
            double qx = b.F32(i + 21), qy = b.F32(i + 25), qz = b.F32(i + 29), qw = b.F32(i + 33);
            if (Math.Abs(qx * qx + qy * qy + qz * qz + qw * qw - 1) > 0.05) continue;     // (NaN passes, as in the JS)
            uint ln = b.U32(i + 37);
            if (ln < 2 || ln > 80 || b.At(i + 40 + (int)ln) != 0) continue;
            bool ok = true;
            for (int k = i + 41; k < i + 40 + ln; k++) if (b[k] < 32 || b[k] > 126) { ok = false; break; }
            if (!ok) continue;
            string name = Latin1.Decode(b.Sub(i + 41, i + 40 + ln));
            int owner = b[i + 8];
            // the engine applies the stored quaternion as its conjugate (Direct3D row vectors): the heading is
            // -2 atan2(z, w) counter-clockwise in map space; q = the stored quaternion (tilted landscape pieces need all)
            output.Add(new MapObject
            {
                Type = Latin1.Decode(b.Sub(i, i + 4)), Name = name, Cls = NumberSuffix.Replace(name, "", 1), X = x, Y = y, Z = z,
                Rot = -2 * SMath.Atan2(qz, qw), Q = new[] { qx, qy, qz, qw }, Owner = owner == 0xff ? null : owner, At = i,
            });
            i += 40 + (int)ln;
        }
        for (int k = 0; k < output.Count; k++) output[k].Attr = Attrs(b, output[k].At + 41, k + 1 < output.Count ? output[k + 1].At : b.Length);
        return output;
    }

    // IOMG: u32 ?, u32 blocks x, u32 blocks y, u32 class count, class names, u32 block count,
    //       per block: u32 n, n x {f32 x, y, z, quaternion x, y, z, w, u32 class}
    static List<MapPlant> Plants(ByteView? bv)
    {
        var output = new List<MapPlant>();
        if (bv is not ByteView b || b.Length < 16) return output;
        uint nc = b.U32(12);
        var names = new List<string>();
        int o = 16;
        for (uint i = 0; i < nc; i++) { uint l = b.U32(o); names.Add(Latin1.Decode(b.Sub(o + 4, (long)o + 3 + l))); o = (int)(o + 4 + l); }
        uint nb = b.U32(o); o += 4;
        for (uint k = 0; k < nb && o + 4 <= b.Length; k++)
        {
            uint n = b.U32(o); o += 4;
            for (uint i = 0; i < n; i++, o += 32)
            {
                double qx = b.F32(o + 12), qy = b.F32(o + 16), qz = b.F32(o + 20), qw = b.F32(o + 24);
                uint ci = b.U32(o + 28);
                output.Add(new MapPlant
                {
                    Name = ci < names.Count ? names[(int)ci] : "", X = b.F32(o), Y = b.F32(o + 4), Z = b.F32(o + 8),
                    Rot = -2 * SMath.Atan2(qz, qw), Q = new[] { qx, qy, qz, qw },
                });
            }
        }
        return output;
    }

    // Rgns (pwexport/triggers.py regions): u32 version, u32 n handles + n x 4 bytes, u32 n regions: {u32 flags, guid
    // (16 bytes), name, u32 colour, u32 n shapes: {u32 type (low byte 1 rect | 2 oval, bit 29 enabled), u32, f32 x y z
    // w h d}, note}. Shapes are map boxes x .. x+w, y .. y+h (an oval is the ellipse inscribed in its box). The nests'
    // areas are regions "<nest>_ActionArea", "_SafeArea", "_HotspotArea" (Nest.usl).
    public static List<MapRegion> Regions(ByteView? bv)
    {
        var output = new List<MapRegion>();
        if (bv is not ByteView b || b.Length < 12) return output;
        var r = new Reader(b);
        try
        {
            r.U32();
            uint handles = r.U32();
            r.O = (int)Math.Min(int.MaxValue, r.O + 4L * handles);
            for (uint n = r.U32(), i = 0; i < n; i++)
            {
                r.U32(); r.O += 16;
                var reg = new MapRegion { Name = r.Str() };
                r.U32();
                for (uint k = r.U32(), j = 0; j < k; j++)
                {
                    uint t = r.U32(); r.U32();
                    double x = r.F32(), y = r.F32(); r.F32(); double w = r.F32(), h = r.F32(); r.F32();
                    reg.Shapes.Add(new RegionShape { Oval = (t & 0xff) == 2, Enabled = (t & 0x20000000) != 0, X = x, Y = y, W = w, H = h });
                }
                r.Str();
                output.Add(reg);
            }
        }
        catch (Exception) { /* a damaged tail: what was read */ }
        return output;
    }

    /// <summary>"12:0" -> seconds of the day; 12:00 when missing, 8:30 when malformed (ServerApp.usl)</summary>
    public static double DayTime(string? v)
    {
        if (string.IsNullOrEmpty(v)) v = "12:0";
        var t = v.Split(':');
        if (t.Length == 2)
        {
            double a = JsNum.ToNumber(t[0]), b = JsNum.ToNumber(t[1]);
            if (double.IsFinite(a) && double.IsFinite(b)) return a * 3600 + b * 60;
        }
        return 8.5 * 3600;
    }

    static readonly Regex UlaExt = new(@"\.ula\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static MapData ParseUla(byte[] buf, string fileName = "")
    {
        var blob = new ByteView(Unpack(buf));
        if (Latin1.Decode(blob.Sub(0, 4)) != "SURF") throw new InvalidDataException("not a ParaWorld map");
        var c = Chunks(blob);
        ByteView? Chunk(string tag) => c.TryGetValue(tag, out var v) ? v : null;
        var li = c.TryGetValue("LInf", out var linf) ? LevelInfo(linf) : new LevelInfoResult();
        var info = li.Kv;
        if (!c.TryGetValue("Terr", out var terr)) throw new InvalidDataException("map without terrain (Terr)");
        var md = Terrain(terr);
        string? Info(string k) => info.TryGetValue(k, out var v) ? v : null;
        string? Desc(string k) => li.Description.TryGetValue(k, out var v) ? v : null;

        md.Info = info; md.File = fileName; md.Preview = li.Preview; md.Description = li.Description; md.InfoTree = li.Tree;
        // the camera's angle the level starts with (radians; the minimap's diamond follows it: docs/MINIMAP.md)
        string? cam = Desc("DefaultCamera");
        md.DefaultCamera = cam != null && cam != "" && double.IsFinite(JsNum.ToNumber(cam)) ? JsNum.ToNumber(cam) : 0.785;
        // the time of day the level starts at ("Root/Base/StartTime" = "12:0"; ServerApp.usl: 12:00 when missing,
        // 8:30 when malformed), in seconds of the day
        md.StartTime = DayTime(Desc("StartTime"));
        // the level info names the setting ("Setting": "Cave1"); the number in the terrain header is the fallback
        string? set = Info("Setting");
        md.Setting = set != null && KNOWN_SETTINGS.TryGetValue(set, out var ks) ? ks : KNOWN_SETTINGS.TryGetValue(md.TerrainSetting, out var kt) ? kt : "Jungle";
        string? ln = Info("LevelName"), mn = Info("MapName");
        md.Name = !string.IsNullOrEmpty(ln) ? ln : !string.IsNullOrEmpty(mn) ? mn : UlaExt.Replace(fileName, "", 1);
        md.MaxPlayers = JsNum.Or(Info("MaxPlayers"), JsNum.Or(Info("StartLocations"), 2));
        md.Objects = c.TryGetValue("Objs", out var objs) ? Objects(objs, md.W, md.H) : new List<MapObject>();
        md.Plants = Plants(Chunk("IOMG"));
        md.Forest = Forest.ParseForest(Chunk("Frst"));
        md.Walk = Pathing(Chunk("PaFi"), md.W, md.H);
        md.Regions = Regions(Chunk("Rgns"));
        // the setting by its own name: forest kinds are per setting
        md.SettingName = set != null && Array.IndexOf(SETTINGS, set) >= 0 ? set : md.TerrainSetting;
        L.Debug($"{fileName}: {md.Name}, {md.W}x{md.H} m, {md.Setting}, {md.Objects.Count} objects, {md.Plants.Count} plants, " +
                $"{md.Forest.Blocks.Count} forest blocks, {md.Regions.Count} regions, walk grid {(md.Walk != null ? "yes" : "no")}");
        return md;
    }

    public static MapData ParseUlaFile(string path) => ParseUla(File.ReadAllBytes(path), Path.GetFileName(path));

    /// <summary>only the level info and the terrain header of a map (the skirmish menu's map list and preview: name,
    /// setting, size, players, game types, the stored preview picture, the description tree) - no heights, objects or
    /// plants. Heights / Objects stay empty; W, H, Hx, Hy are set.</summary>
    public static MapData ParseHeader(byte[] buf, string fileName = "")
    {
        var blob = new ByteView(Unpack(buf));
        if (Latin1.Decode(blob.Sub(0, 4)) != "SURF") throw new InvalidDataException("not a ParaWorld map");
        var c = Chunks(blob);
        var li = c.TryGetValue("LInf", out var linf) ? LevelInfo(linf) : new LevelInfoResult();
        var md = new MapData { Info = li.Kv, File = fileName, Preview = li.Preview, Description = li.Description, InfoTree = li.Tree };
        if (c.TryGetValue("Terr", out var terr))
        {
            md.W = (int)terr.U32(4); md.H = (int)terr.U32(8);
            md.Hx = (int)terr.U32(12) * 16; md.Hy = (int)terr.U32(16) * 16;
            md.TerrainSetting = SETTINGS.Length > terr.U32(20) ? SETTINGS[terr.U32(20)] : "Jungle";
        }
        string? Info(string k) => md.Info.TryGetValue(k, out var v) ? v : null;
        string? cam = md.Description.TryGetValue("DefaultCamera", out var cv) ? cv : null;
        md.DefaultCamera = cam != null && cam != "" && double.IsFinite(JsNum.ToNumber(cam)) ? JsNum.ToNumber(cam) : 0.785;
        string? set = Info("Setting");
        md.Setting = set != null && KNOWN_SETTINGS.TryGetValue(set, out var ks) ? ks : KNOWN_SETTINGS.TryGetValue(md.TerrainSetting, out var kt) ? kt : "Jungle";
        md.SettingName = set != null && Array.IndexOf(SETTINGS, set) >= 0 ? set : md.TerrainSetting;
        string? ln = Info("LevelName"), mn = Info("MapName");
        md.Name = !string.IsNullOrEmpty(ln) ? ln : !string.IsNullOrEmpty(mn) ? mn : UlaExt.Replace(fileName, "", 1);
        md.MaxPlayers = JsNum.Or(Info("MaxPlayers"), JsNum.Or(Info("StartLocations"), 2));
        return md;
    }
}
