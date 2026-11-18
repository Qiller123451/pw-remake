// The JSON chunk of a .glb file, read with System.Text.Json: the root's extras (foliage sprites, walk sets, sounds,
// path finder boxes), the materials' extras (GSF flags, source texture) and the texture file of each material. Godot's
// GLTFDocument puts node extras on the nodes as metadata "extras" (used for the part attributes), but the big root
// extras and the image URIs are easier and faster to read here than through Godot dictionaries.
using System;
using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using PwRemake.Sim.Core;

namespace PwRemake.View.Models;

public sealed class GlbJson
{
    public JsonObject Root { get; }
    /// <summary>The whole file.</summary>
    public byte[] Bytes { get; }
    GlbJson(JsonObject root, byte[] bytes) { Root = root; Bytes = bytes; }

    public static GlbJson Read(string path)
    {
        var b = File.ReadAllBytes(path);
        if (b.Length < 20 || BitConverter.ToUInt32(b, 0) != 0x46546C67) throw new InvalidDataException($"{path}: not a glb file");
        int len = (int)BitConverter.ToUInt32(b, 12);
        if (BitConverter.ToUInt32(b, 16) != 0x4E4F534A) throw new InvalidDataException($"{path}: first chunk is not JSON");
        var node = JsonNode.Parse(Encoding.UTF8.GetString(b, 20, len)) as JsonObject;
        return new GlbJson(node ?? throw new InvalidDataException($"{path}: empty JSON chunk"), b);
    }

    /// <summary>
    /// The .glb without its pictures: images, textures and the materials' texture slots removed (the materials keep
    /// their indices). Godot's GLTFDocument then neither reads nor decodes the PNGs (it does even with
    /// HANDLE_BINARY_DISCARD_TEXTURES: over a second for a character's sheets); the textures come from the shared
    /// TextureCache instead.
    /// </summary>
    public static byte[] WithoutImages(byte[] glb, JsonObject json)
    {
        var j = (JsonObject)json.DeepClone();
        j.Remove("images"); j.Remove("textures"); j.Remove("samplers");
        if (j["materials"] is JsonArray mats)
            foreach (var m in mats)
            {
                if (m is not JsonObject mo) continue;
                mo.Remove("normalTexture"); mo.Remove("occlusionTexture"); mo.Remove("emissiveTexture");
                if (mo["pbrMetallicRoughness"] is JsonObject pbr) { pbr.Remove("baseColorTexture"); pbr.Remove("metallicRoughnessTexture"); }
            }
        if (j["extensionsUsed"] is JsonArray eu) for (int i = eu.Count - 1; i >= 0; i--) if (eu[i].Str() == "KHR_texture_transform") eu.RemoveAt(i);
        // models without geometry (only link / emitter nodes: the waterfalls' dummies, ninigi_gatling_arrow, MIRAGE's
        // pirate_boss) declare an empty buffer and have no BIN chunk: three.js reads them, Godot stops with "Buffer 0 has
        // no data" - without any buffer view the buffer is not needed
        if (j["bufferViews"] is not JsonArray { Count: > 0 } && j["buffers"] is JsonArray) j.Remove("buffers");
        var text = Encoding.UTF8.GetBytes(j.ToJsonString());
        int pad = (4 - text.Length % 4) % 4;
        int oldJsonLen = (int)BitConverter.ToUInt32(glb, 12);
        int restStart = 20 + oldJsonLen;
        int rest = glb.Length - restStart;
        var outb = new byte[20 + text.Length + pad + rest];
        BitConverter.GetBytes(0x46546C67u).CopyTo(outb, 0);
        BitConverter.GetBytes(2u).CopyTo(outb, 4);
        BitConverter.GetBytes((uint)outb.Length).CopyTo(outb, 8);
        BitConverter.GetBytes((uint)(text.Length + pad)).CopyTo(outb, 12);
        BitConverter.GetBytes(0x4E4F534Au).CopyTo(outb, 16);
        text.CopyTo(outb, 20);
        for (int i = 0; i < pad; i++) outb[20 + text.Length + i] = (byte)' ';
        Array.Copy(glb, restStart, outb, 20 + text.Length + pad, rest);
        return outb;
    }

    public JsonArray Nodes => Root["nodes"] as JsonArray ?? new JsonArray();
    public JsonArray Materials => Root["materials"] as JsonArray ?? new JsonArray();

    /// <summary>The extras of the scene's first root node (the model's root: fourcc, foliage, sounds, walksets, pf).</summary>
    public JsonObject RootExtras
    {
        get
        {
            int idx = 0;
            if (Root["scenes"] is JsonArray sc && sc.Count > 0 && sc[0]?["nodes"] is JsonArray rn && rn.Count > 0) idx = rn[0].Int();
            return (Nodes.Count > idx ? Nodes[idx]?["extras"] as JsonObject : null) ?? new JsonObject();
        }
    }

    /// <summary>The image URI of a texture reference ({index}) of a material, or null.</summary>
    public string? TextureUri(JsonNode? texRef)
    {
        if (texRef?["index"] is not JsonNode ix) return null;
        var tex = (Root["textures"] as JsonArray)?[ix.Int()];
        if (tex?["source"] is not JsonNode src) return null;
        var uri = (Root["images"] as JsonArray)?[src.Int()]?["uri"].Str();
        return string.IsNullOrEmpty(uri) ? null : uri;
    }

    /// <summary>Parse an attribute that the exporter wrote as a number or as a string of digits.</summary>
    public static uint ParseAttr(object? v) => v switch
    {
        null => 0,
        double d => (uint)(long)d,
        float f => (uint)(long)f,
        long l => (uint)l,
        int i => (uint)i,
        string s => long.TryParse(s, out var x) ? (uint)x : (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y) ? (uint)(long)y : 0),
        _ => 0,
    };
}
