// The game's own interface texts in the player's language, read from the installation as the original reads them:
// the string tables Data/locale/<lang>/Texts/*.ltf (and the add-ons' Data/<folder>/Locale/<lang>/Texts/*.ltf), lines
// "key";"text" (UTF-8 with BOM; "" is a quote, \n a line break). English ('uk') fills the gaps of another language.
// The menus take their captions from here (the options window's "_UI_OptionsM_*", the main menu's "_UI_MainMenu_*" ...)
// with an English fallback when the installation is not there (tests, the setup screen).
// The toolkit's gamedata.json "ui" holds only the few keys the browser remake used; the menus need more.
// Ported from pwexport/campaign.py TextTable._ltf (pw-exporter 98c7703).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using PwRemake.Sim.Core;

namespace PwRemake.Sim.Content;

public sealed class GameTexts
{
    static readonly Logger L = Log.For("Content.Texts");
    static readonly ConcurrentDictionary<string, GameTexts> s_cache = new();
    static readonly Regex Line = new(@"^\s*""([^""]*)"";""((?:[^""]|"""")*)""", RegexOptions.Compiled);

    readonly Dictionary<string, string> _t = new(StringComparer.OrdinalIgnoreCase);
    public int Files { get; private set; }
    public int Count => _t.Count;

    /// <summary>The texts of a configuration's installation (read once per installation, folders and language).</summary>
    public static GameTexts For(GameConfiguration? c)
    {
        if (c == null || string.IsNullOrEmpty(c.Install)) return new GameTexts();
        var key = $"{c.Install}|{string.Join("+", c.Folders)}|{c.Language}";
        return s_cache.GetOrAdd(key, _ => Read(c.Install, c.Folders, string.IsNullOrEmpty(c.Language) ? "uk" : c.Language));
    }

    /// <summary>Reads the string tables of an installation: per language (lang, then 'uk'), the base locale folder and
    /// then each data folder's own (a later folder's file of the same name replaces an earlier one, as the game's file
    /// system does); a key keeps its first text.</summary>
    public static GameTexts Read(string install, IEnumerable<string> folders, string lang)
    {
        var g = new GameTexts();
        var data = CiDir(install, "Data");
        if (data == null) return g;
        foreach (var lg in lang.Equals("uk", StringComparison.OrdinalIgnoreCase) ? new[] { "uk" } : new[] { lang, "uk" })
        {
            var dirs = new List<string?> { CiDir(data, "locale", lg, "Texts") };
            foreach (var f in folders) dirs.Add(CiDir(data, f, "locale", lg, "Texts"));
            var files = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in dirs)
                if (d != null)
                    foreach (var f in Directory.EnumerateFiles(d, "*.ltf").Where(f => !Path.GetFileName(f).StartsWith("._")))
                        files[Path.GetFileName(f)] = f;
            foreach (var f in files.Values) g.ReadLtf(f);
        }
        L.Info($"texts ({lang}): {g.Count} from {g.Files} files of {install}");
        return g;
    }

    /// <summary>One .ltf file ("key";"text" lines).</summary>
    public void ReadLtf(string path)
    {
        string txt;
        try { txt = File.ReadAllText(path); }      // (UTF-8; the BOM is dropped)
        catch (Exception e) { L.Warn($"cannot read {path}: {e.Message}"); return; }
        Files++;
        foreach (var line in txt.Split('\n'))
        {
            var m = Line.Match(line);
            if (!m.Success || m.Groups[1].Value is "Suit Localization File" or "Suit Localization Master File") continue;
            var v = m.Groups[2].Value.Replace("\\n", "\n").Replace("\"\"", "\"");
            _t.TryAdd(m.Groups[1].Value, v);
        }
    }

    public bool Has(string key) => _t.ContainsKey(key);

    /// <summary>The text of a key, else the fallback.</summary>
    public string Get(string key, string fallback) => _t.TryGetValue(key, out var v) && v.Length > 0 ? v : fallback;

    /// <summary>A label's text without the trailing colon the original's labels have ("Master Volume:").</summary>
    public string Label(string key, string fallback) => Get(key, fallback).Trim().TrimEnd(':').Trim();

    /// <summary>a folder below root by its parts, letters in any case (the installation comes from Windows)</summary>
    static string? CiDir(string root, params string[] parts)
    {
        var cur = root;
        foreach (var p in parts)
        {
            if (!Directory.Exists(cur)) return null;
            var direct = Path.Combine(cur, p);
            if (Directory.Exists(direct)) { cur = direct; continue; }
            var hit = Directory.EnumerateDirectories(cur).FirstOrDefault(d => string.Equals(Path.GetFileName(d), p, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return null;
            cur = hit;
        }
        return Directory.Exists(cur) ? cur : null;
    }
}
