// Sending the lobby's map to a player who lacks it (docs/MULTIPLAYER.md "Maps"; Warcraft 3 style). A player whose copy
// of the skirmish lobby's map is missing or another version asks the host for it ("map-request" with the content hash);
// the host sends the map as a .pwmap over the lobby's own connection in chunks ("map-data": the hash, the offset, the
// total, base64 bytes), at most Window chunks unacknowledged ("map-ack": the bytes received - the host's loop never
// waits on a slow player, and the lobby shows the progress at the player's name). The player checks what arrived
// (Store: the size, the zip's entries and their unpacked sizes, the content hash = the lobby's, a map.lua that reads)
// and keeps it in its maps folder's downloaded/<hash>.pwmap; then its copy is the host's (MapFiles.FindFor) and the
// map-check clears the problem. Nothing of the map runs while it is checked: map.lua and the other data files are read
// by LuaData (data only), a mission's triggers.lua / custom.lua run in the mission's sandbox when it is played - as for
// any map (docs/LUA_MAPS.md "The sandbox"). The host cannot start before everybody has the map (the problem stays
// until the player's map-check says the copy is the host's).
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using PwRemake.Sim.Maps;

namespace PwRemake.Net;

public static class MapTransfer
{
    /// <summary>the largest map sent (the .pwmap's bytes): the original maps are up to 2.4 MB, a mission with a big
    /// world.lua up to ~6 MB zipped</summary>
    public const int MaxBytes = 32 << 20;
    /// <summary>the bytes of one "map-data" message (before base64)</summary>
    public const int Chunk = 64 << 10;
    /// <summary>chunks the host sends ahead of the player's acknowledgement</summary>
    public const int Window = 8;
    /// <summary>the most the entries of a received .pwmap may unpack to (a zip that claims or holds more is refused)</summary>
    public const long MaxUnpacked = 512L << 20;
    const int MaxEntries = 20000;
    static readonly DateTimeOffset ZipTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>the bytes the host sends for a map: a .pwmap file as it is; a map folder zipped as it is (every file at its
    /// path, sorted, a fixed time stamp: the same content hash as the folder - MapFiles.ContentHash)</summary>
    public static byte[] Pack(string path)
    {
        if (File.Exists(path)) return File.ReadAllBytes(path);
        var src = new PwMapReader.Source(path);
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var rel in src.List().Where(r => !r.EndsWith(".tmp", StringComparison.Ordinal)).OrderBy(r => r, StringComparer.Ordinal))
            {
                var e = zip.CreateEntry(rel, rel.EndsWith(".png", StringComparison.Ordinal) ? CompressionLevel.NoCompression : CompressionLevel.SmallestSize);
                e.LastWriteTime = ZipTime;
                using var s = e.Open();
                var b = src.Get(rel) ?? Array.Empty<byte>();
                s.Write(b, 0, b.Length);
            }
        return ms.ToArray();
    }

    /// <summary>the map of a lobby's setup on this machine as the bytes to send (null: no such map, another content, or too big)</summary>
    public static byte[]? PackFor(Sim.Content.GameConfiguration cfg, string mapId, string hash, out string? why)
    {
        why = null;
        var path = MapFiles.FindFor(cfg, mapId, hash);
        if (path == null || !PwMapReader.IsMap(path)) { why = $"the host has no map {mapId}"; return null; }
        if (MapFiles.ContentHash(path) != hash) { why = "the host's map changed - choose it again"; return null; }
        var b = Pack(path);
        if (b.Length > MaxBytes) { why = $"the map is too big to send ({b.Length / 1048576.0:0.0} MB, at most {MaxBytes >> 20} MB): copy its .pwmap by hand"; return null; }
        return b;
    }

    /// <summary>checks a received map and keeps it as dir/&lt;hash&gt;.pwmap (dir: the user's maps' downloaded/); its path.
    /// Throws InvalidDataException with the reason (too big, not a zip, an entry outside the map, unpacking to more than
    /// it claims, another content hash, no readable map.lua).</summary>
    public static string Store(byte[] data, string hash, string dir)
    {
        if (!MapFiles.IsHash(hash)) throw new InvalidDataException("not a map hash: " + hash);
        if (data.Length > MaxBytes) throw new InvalidDataException($"the map is too big ({data.Length} bytes)");
        // the zip's entries: names inside the map, sizes as claimed, all of it not more than MaxUnpacked
        ZipArchive za;
        try { za = new ZipArchive(new MemoryStream(data, false), ZipArchiveMode.Read); _ = za.Entries.Count; }
        catch (Exception e) { throw new InvalidDataException("not a .pwmap (zip): " + e.Message); }
        try
        {
            using var _za = za;
            if (za.Entries.Count > MaxEntries) throw new InvalidDataException($"{za.Entries.Count} files in the map");
            long total = 0;
            var buf = new byte[81920];
            foreach (var e in za.Entries)
            {
                var n = e.FullName.Replace('\\', '/');
                if (n.Contains("..") || n.StartsWith('/') || n.Contains(':')) throw new InvalidDataException($"a file outside the map: {e.FullName}");
                if (n.EndsWith('/')) continue;
                if (e.Length < 0 || e.Length > MaxUnpacked || (total += e.Length) > MaxUnpacked) throw new InvalidDataException("the map unpacks to too much");
                long got = 0;
                using var s = e.Open();
                int r;
                while ((r = s.Read(buf, 0, buf.Length)) > 0) if ((got += r) > e.Length) throw new InvalidDataException($"{e.FullName} unpacks to more than it says");
            }
        }
        catch (InvalidDataException) { throw; }
        catch (Exception e) { throw new InvalidDataException("not a .pwmap: " + e.Message); }
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, hash + PwMapPaths.Ext);
        var tmp = Path.Combine(dir, "." + hash + "-" + Guid.NewGuid().ToString("N")[..8] + ".part");
        File.WriteAllBytes(tmp, data);
        try
        {
            var h = MapFiles.ContentHash(tmp);
            if (h != hash) throw new InvalidDataException($"the map's content is not the host's (hash {h}, the lobby's {hash})");
            PwMapReader.ReadHeader(tmp);                     // (map.lua: data only - LuaData's token check, an empty environment)
            File.Move(tmp, target, true);
        }
        catch (Exception e)
        {
            try { File.Delete(tmp); } catch (IOException) { }
            throw e as InvalidDataException ?? new InvalidDataException("the map does not read: " + e.Message);
        }
        return target;
    }
}

/// <summary>A player's side of the lobby's map (docs/MULTIPLAYER.md "Maps"): compares its copy with the host's hash
/// whenever the map or the hash changes (map-check), asks the host for the map when it lacks it or has another version
/// (map-request; NetClient receives and stores it), and checks again once it is there. The game's NetSession and the
/// tests run it after every Poll.</summary>
public sealed class LobbyMapSync
{
    readonly NetClient _c;
    readonly Func<Sim.Content.GameConfiguration?> _cfg;
    string? _checked, _requested;
    /// <summary>false: never asks the host for a map (the problem stays: the player copies it by hand)</summary>
    public bool Download = true;
    /// <summary>a line for the log / the lobby's status (the last thing that happened)</summary>
    public string? Status { get; private set; }

    public LobbyMapSync(NetClient c, Func<Sim.Content.GameConfiguration?> cfg)
    {
        _c = c; _cfg = cfg;
        c.MapReceived += (_, path) => { Status = "the map arrived: " + path; _checked = null; };
        c.MapFailed += why => { Status = "the map could not be received: " + why; _checked = null; };
    }

    public void Update()
    {
        var l = _c.Lobby;
        var sk = l?.Mode == "skirmish" && l.State == "lobby" ? l.Skirmish : null;
        if (sk?.MapHash == null || _c.MyPeer == null) return;
        var key = sk.Map + "|" + sk.MapHash;
        if (key == _checked || _c.MapReceiving == sk.MapHash) return;
        _checked = key;
        var problem = LobbyMap.Problem(_cfg(), sk);
        if (problem != null && Download && !_c.IsHost && _requested != key && MapFiles.IsHash(sk.MapHash))
        {
            // (once per map and hash: a failed transfer leaves the problem with its reason)
            _requested = key;
            Status = "asking the host for the map " + (sk.MapName ?? sk.Map);
            _c.RequestMap(sk.MapHash);
            return;
        }
        if (problem != null && _c.MapError is { } err && _requested == key) problem += " (it could not be received: " + err + ")";
        _c.SendMapCheck(sk.MapHash, problem);
    }
}
