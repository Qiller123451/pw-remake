// What a map source asks of the rules (Sim/Maps IMapRules), answered by the ported rules (Sim/Rules) - the adapter
// docs/MAPS.md "The map source" describes, one line per question (the JS passed its `D` to maps/source.js):
//   ClassGfx, Script, Exists   directly
//   ResourceValue              Rules.ResourceValue(section, name, 0) when above 0, else null
//   StatsGfx                   the gfx of Rules.Stats(name, 1, null)
//   NestSpecies                gamedata "nests" (absent in today's data: null)
// No Godot types: usable from tests and tools.
using PwRemake.Sim.Maps;

namespace PwRemake.View.World;

public sealed class RulesMapAdapter : IMapRules
{
    readonly PwRemake.Sim.Rules.Rules _r;
    public RulesMapAdapter(PwRemake.Sim.Rules.Rules rules) { _r = rules; }
    public string? ClassGfx(string name) => _r.ClassGfx(name);
    public string? Script(string name) => _r.Script(name);
    public double? ResourceValue(string section, string name) { double v = _r.ResourceValue(section, name, 0); return v > 0 ? v : null; }
    public bool Exists(string name) => _r.Exists(name);
    public string? StatsGfx(string name) => _r.Stats(name, 1, null)?.Gfx is { Length: > 0 } g ? g : null;
    public string? NestSpecies(string nestClassLower) => null;
}
