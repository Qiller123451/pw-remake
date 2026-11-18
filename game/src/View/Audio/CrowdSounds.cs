// The original's crowd sounds (Audio/soundeffects.txt "Additionals"; PWClient.exe CSoundEventMgrClient, docs/AUDIO.md
// "Crowd sounds"): a war drum or the din of a fight where many sound events of one sound group were heard together.
//
// Every animation sound of the GSF names a sound group (manifest sounds [t, wav, vol, maxHear, group, ...]:
// sndgroup_combat for the strikes, sndgroup_move_fighter for the soldiers' steps, sndgroup_move_dinos_* ...). Each one that
// is started (it passed the limits of the world sounds) is a trigger of its group at its place (0x4900e9 -> 0x48fd90).
// Every 0.5 s (0x490340) for each group of the table:
//   1. the triggers older than 3 s are dropped; the rest are gathered into crowds - points chained within 32 m
//      (0x7ac6b0: the boxes of two crowds closer than 32 m are joined); a crowd's place is the middle of its box;
//   2. a crowd sound not near any crowd in the last pass is stopped; the others follow the nearest crowd within 32 m;
//      one older than its virtuallength is forgotten (it plays to its end, but no longer holds the place);
//   3. a crowd of at least the group's smallest mintriggers: 1 minus the virtualvolume of every crowd sound within 32 m
//      of it is what is left; the entries with mintriggers <= triggers <= maxtriggers and a virtualvolume within that
//      are drawn by randomweight, and the drawn one plays at the crowd's middle (volume, min fade, hearing distance of
//      the entry; priority 1: the limits of the world sounds, C3DSoundMgr 0x7a0cb0).
// The table of the original (Base): sndgroup_combat 12 .. 100 strikes within 32 m -> sndgroup_fight_large (99 %, heard
// 76 m); sndgroup_move_fighter 10 .. 100 steps -> sndgroup_move_fighter (marching, 48 %); sndgroup_fight and
// sndgroup_move_few_dinos have entries but no GSF sound names those groups. The second entries of sndgroup_combat and
// sndgroup_fight ask for 8 .. 5 triggers: never (kept as the original).
//
// Plain logic (no Godot types): AudioCore owns one (AudioCore.Crowd), feeds it from AnimEvent and runs it in Frame;
// the tests drive it with their own clock. Not in the browser remake (pw-exporter 98c7703): ported from the client.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using PwRemake.Sim.Core;

namespace PwRemake.View.Audio;

/// <summary>An entry ("add-N") of a sound group in soundeffects.txt "Additionals" (loader PWClient 0x490f70; the
/// defaults are the loader's).</summary>
public sealed record CrowdAdd(string Wav, double Volume = 90, double Weight = 1, int MinTriggers = 1, int MaxTriggers = 1,
    double VirtualLength = 1, double VirtualVolume = 1, double MinFade = 0, double MaxFade = 0, double MaxHear = 0);

/// <summary>A sound group of soundeffects.txt with its entries.</summary>
public sealed class CrowdGroup
{
    public string Name = "";
    public readonly List<CrowdAdd> Adds = new();
    /// <summary>The smallest mintriggers of the entries (the loader's group minimum: a crowd with fewer is skipped).</summary>
    public int MinTriggers = int.MaxValue;
}

/// <summary>Audio/soundeffects.txt "Additionals": the sound groups that make crowd sounds.</summary>
public sealed class CrowdTable
{
    static readonly Logger L = Log.For("View.Audio");
    public readonly List<CrowdGroup> Groups = new();

    public CrowdGroup? Group(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        foreach (var g in Groups) if (string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) return g;
        return null;
    }

    /// <summary>The file (null or missing: an empty table - no crowd sounds; logged).</summary>
    public static CrowdTable Load(string? path)
    {
        if (path == null || !File.Exists(path)) { L.Info("no Audio/soundeffects.txt: no crowd sounds"); return new CrowdTable(); }
        var t = Parse(File.ReadAllText(path));
        L.Info($"crowd sounds: {t.Groups.Count} groups ({path})");
        return t;
    }

    /// <summary>The original's text format (CPropDB): `name { ... }` blocks and `key = 'value'` lines; the groups are
    /// the blocks below Root/Additionals, their entries the blocks below them.</summary>
    public static CrowdTable Parse(string text)
    {
        var root = Node.Parse(text);
        var res = new CrowdTable();
        var adds = root.Find("Root")?.Find("Additionals") ?? root.Find("Additionals");
        if (adds == null) return res;
        foreach (var gn in adds.Subs)
        {
            var g = new CrowdGroup { Name = gn.Name };
            foreach (var an in gn.Subs)
            {
                // (0x491269: an entry of randomweight <= 0, or without a wav, is not added)
                int w = an.I("randomweight", 1);
                var wav = an.S("wav");
                if (w <= 0 || string.IsNullOrEmpty(wav)) continue;
                var a = new CrowdAdd(wav, an.I("volume", 90), w, an.I("mintriggers", 1), an.I("maxtriggers", 1),
                    // (virtuallength: the loader keeps whole milliseconds, F2I(x * 1000))
                    Math.Truncate(an.D("virtuallength", 1) * 1000) / 1000, an.D("virtualvolume", 1),
                    an.D("minfadedistance", 0), an.D("maxfadedistance", 0), an.D("maxhearingdistance", 0));
                g.Adds.Add(a);
                g.MinTriggers = Math.Min(g.MinTriggers, a.MinTriggers);
            }
            res.Groups.Add(g);
        }
        return res;
    }

    /// <summary>A node of the text: its name, value ('...'), sub nodes.</summary>
    sealed class Node
    {
        public string Name = "", Value = "";
        public readonly List<Node> Subs = new();
        public Node? Find(string n) { foreach (var s in Subs) if (string.Equals(s.Name, n, StringComparison.OrdinalIgnoreCase)) return s; return null; }
        public string? S(string k) => Find(k)?.Value;
        public int I(string k, int def) => int.TryParse(S(k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : (int)D(k, def);
        public double D(string k, double def) => double.TryParse(S(k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

        public static Node Parse(string text)
        {
            var top = new Node();
            var stack = new Stack<Node>();
            stack.Push(top);
            int i = 0, n = text.Length;
            Node? last = null;
            while (i < n)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c)) { i++; continue; }
                if (c == '/' && i + 1 < n && text[i + 1] == '/') { while (i < n && text[i] != '\n') i++; continue; }
                if (c == '{') { if (last != null) stack.Push(last); last = null; i++; continue; }
                if (c == '}') { if (stack.Count > 1) stack.Pop(); last = null; i++; continue; }
                if (c == '=')
                {
                    i++;
                    while (i < n && (text[i] == ' ' || text[i] == '\t')) i++;
                    if (i < n && (text[i] == '\'' || text[i] == '"'))
                    {
                        char q = text[i++]; int s = i;
                        while (i < n && text[i] != q) i++;
                        if (last != null) last.Value = text[s..Math.Min(i, n)];
                        i++;
                    }
                    else
                    {
                        int s = i;
                        while (i < n && !char.IsWhiteSpace(text[i]) && text[i] != '{' && text[i] != '}') i++;
                        if (last != null) last.Value = text[s..i];
                    }
                    continue;
                }
                int st = i;
                while (i < n && !char.IsWhiteSpace(text[i]) && text[i] != '=' && text[i] != '{' && text[i] != '}') i++;
                last = new Node { Name = text[st..i] };
                stack.Peek().Subs.Add(last);
            }
            return top;
        }
    }
}

/// <summary>
/// The counting of CSoundEventMgrClient 0x490340 (see the header): triggers in, crowd sounds out through Play.
/// </summary>
public sealed class CrowdSounds
{
    /// <summary>Seconds between two passes (0x490340: 500 ms).</summary>
    public const double Interval = 0.5;
    /// <summary>Triggers older than this are dropped (3000 ms).</summary>
    public const double Window = 3;
    /// <summary>Points within this distance make one crowd; a crowd sound within it holds the crowd's place (32 m).</summary>
    public const double Radius = 32;

    public CrowdTable Table = new();
    /// <summary>The random numbers of the draw (rand() in the client): AudioCore.Rnd.</summary>
    public Func<double> Rnd = Random.Shared.NextDouble;
    /// <summary>Plays an entry at a place; returns the sound (null: refused by the limits - the place is held all the
    /// same, as the client's handle -1).</summary>
    public Func<CrowdGroup, CrowdAdd, V3, ISoundHandle?> Play = (_, _, _) => null;

    /// <summary>A playing crowd sound (0x490340's list per group: start, entry, place, sound, seen near a crowd in
    /// this pass, distance to the nearest crowd).</summary>
    public sealed class Active
    {
        public double Start;
        public CrowdAdd Add = null!;
        public V3 Pos;
        public ISoundHandle? Sound;
        public bool Seen;
        public double Dist;
    }

    /// <summary>A crowd of one pass: its triggers, the middle of their box.</summary>
    public readonly record struct Crowd(string Group, int Count, V3 Center);

    readonly Dictionary<string, List<(double T, V3 P)>> _triggers = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<Active>> _active = new(StringComparer.OrdinalIgnoreCase);
    double _last = double.NegativeInfinity;
    /// <summary>The crowds found in the last pass (tests, debugging).</summary>
    public readonly List<Crowd> LastCrowds = new();
    /// <summary>Crowd sounds started (the counts behind the log).</summary>
    public int Started;

    /// <summary>The crowd sounds of a group being held now.</summary>
    public IReadOnlyList<Active> ActiveOf(string group) => _active.TryGetValue(group, out var l) ? l : Array.Empty<Active>();

    /// <summary>A sound of a group was started at pos (0x48fd90; only groups of the table are kept).</summary>
    public void Trigger(string? group, V3 pos, double now)
    {
        var g = Table.Group(group);
        if (g == null || g.Adds.Count == 0) return;
        if (!_triggers.TryGetValue(g.Name, out var l)) _triggers[g.Name] = l = new List<(double, V3)>();
        l.Add((now, pos));
    }

    /// <summary>Forget everything (a game ends).</summary>
    public void Clear()
    {
        foreach (var l in _active.Values) foreach (var a in l) a.Sound?.Stop();
        _active.Clear(); _triggers.Clear(); LastCrowds.Clear();
    }

    /// <summary>Once a frame: every 0.5 s a pass over the groups.</summary>
    public void Update(double now)
    {
        if (Math.Abs(now - _last) < Interval - 1e-9) return;
        _last = now;
        LastCrowds.Clear();
        foreach (var g in Table.Groups) Pass(g, now);
    }

    void Pass(CrowdGroup g, double now)
    {
        // 1. the triggers of the last 3 s, gathered into crowds
        var trig = _triggers.TryGetValue(g.Name, out var tl) ? tl : null;
        trig?.RemoveAll(x => Math.Abs(x.T - now) > Window);
        var crowds = Gather(trig);
        if (!_active.TryGetValue(g.Name, out var act)) _active[g.Name] = act = new List<Active>();
        // 2. the crowd sounds of the last pass: stopped when no crowd was near, else forgotten after their virtuallength
        for (int i = 0; i < act.Count; i++)
        {
            var a = act[i];
            if (!a.Seen) { a.Sound?.Stop(); act.RemoveAt(i--); continue; }
            a.Seen = false;
            a.Dist = Radius;
            if (Math.Abs(a.Start - now) > a.Add.VirtualLength) act.RemoveAt(i--);
        }
        // 3. each crowd: what is left of it, a new crowd sound drawn among the entries for its size
        foreach (var (n, c) in crowds)
        {
            LastCrowds.Add(new Crowd(g.Name, n, c));
            if (n < g.MinTriggers) continue;
            double left = 1;
            foreach (var a in act)
            {
                double d = Dist(a.Pos, c);
                if (d < a.Dist) { a.Dist = d; a.Pos = c; }
                if (d < Radius) { a.Seen = true; left -= a.Add.VirtualVolume; }
            }
            if (act.Count > 0 && left <= 0) continue;
            double total = 0;
            var cand = new List<CrowdAdd>();
            foreach (var x in g.Adds)
                if (x.MinTriggers <= n && x.MaxTriggers >= n && x.VirtualVolume <= left) { cand.Add(x); total += x.Weight; }
            if (total <= 0) continue;
            // (rand() * total >> 15, then the first entry whose running sum of weights passes it)
            double r = Math.Floor(Rnd() * total), sum = 0;
            CrowdAdd pick = cand[0];
            foreach (var x in cand) { sum += x.Weight; pick = x; if (sum > r) break; }
            var na = new Active { Start = now, Add = pick, Pos = c, Seen = true, Dist = 0 };
            act.Add(na);
            na.Sound = Play(g, pick, c);
            Started++;
        }
    }

    static double Dist(V3 a, V3 b) { double dx = a.X - b.X, dy = a.Y - b.Y, dz = a.Z - b.Z; return Math.Sqrt(dx * dx + dy * dy + dz * dz); }

    /// <summary>The crowds of the triggers: points chained within 32 m are one crowd (0x7ac6b0 joins crowds whose boxes
    /// are closer than 32 m - their nearest points); each crowd's size and the middle of its box, in the order of their
    /// first trigger.</summary>
    static List<(int N, V3 C)> Gather(List<(double T, V3 P)>? pts)
    {
        var res = new List<(int, V3)>();
        if (pts == null || pts.Count == 0) return res;
        int n = pts.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Root(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }
        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
                if (Dist(pts[i].P, pts[j].P) < Radius) { int a = Root(i), b = Root(j); if (a != b) parent[Math.Max(a, b)] = Math.Min(a, b); }
        var order = new List<int>();
        var min = new Dictionary<int, V3>(); var max = new Dictionary<int, V3>(); var cnt = new Dictionary<int, int>();
        for (int i = 0; i < n; i++)
        {
            int r = Root(i); var p = pts[i].P;
            if (!cnt.ContainsKey(r)) { order.Add(r); cnt[r] = 0; min[r] = p; max[r] = p; }
            cnt[r]++;
            var a = min[r]; var b = max[r];
            min[r] = new V3(Math.Min(a.X, p.X), Math.Min(a.Y, p.Y), Math.Min(a.Z, p.Z));
            max[r] = new V3(Math.Max(b.X, p.X), Math.Max(b.Y, p.Y), Math.Max(b.Z, p.Z));
        }
        foreach (var r in order)
        {
            var a = min[r]; var b = max[r];
            res.Add((cnt[r], new V3((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5, (a.Z + b.Z) * 0.5)));
        }
        return res;
    }
}
