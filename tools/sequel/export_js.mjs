// Exports the data of the browser remake's own campaigns (pw-exporter remake/src/game/sequel, game/scenarios) as
// JSON for the Godot port's mission folders (game/campaigns/*/NN_*/mission.json) - everything but the mission
// scripts (the `on` handlers, written by hand as mission.lua) and the scenarios' make(tribe, difficulty), which is
// evaluated here for every tribe and difficulty (variants).
//   node tools/sequel/export_js.mjs <pw-exporter>/remake <out dir>
// Writes <out>/containment/<NN>.json, <out>/scenarios/<NN>.json, <out>/cast.json, <out>/texts.json, <out>/classes.json.
import fs from 'fs';
import path from 'path';
import os from 'os';
import { pathToFileURL } from 'url';

const [, , remake, out] = process.argv;
if (!remake || !out) { console.error('usage: node export_js.mjs <remake folder> <out dir>'); process.exit(1); }
// the modules import three.js (for vectors in their scripts): a stub folder copy without it
const tmp = fs.mkdtempSync(path.join(os.tmpdir(), 'sqexport-'));
for (const rel of ['game/sequel', 'game/scenarios']) {
  const d = path.join(tmp, rel);
  fs.mkdirSync(d, { recursive: true });
  for (const f of fs.readdirSync(path.join(remake, 'src', rel))) {
    if (!f.endsWith('.js')) continue;
    let s = fs.readFileSync(path.join(remake, 'src', rel, f), 'utf8');
    s = s.replace(/import \* as THREE from 'three';/, 'const THREE = { Vector3: class { constructor(x, y, z) { this.x = x; this.y = y; this.z = z; } } };');
    fs.writeFileSync(path.join(d, f), s);
  }
}
// (maps.js, runtime.js import the engine: not needed - only the tables)
const imp = (rel) => import(pathToFileURL(path.join(tmp, rel)).href);
const { MISSIONS } = await imp('game/sequel/missions.js');
const { SCENARIOS, SCENARIO_TRIBES, TRIBE_NAMES, KIT } = await imp('game/scenarios/missions.js');
const { CAST } = await imp('game/sequel/cast.js');
const { SEQUEL_TEXTS } = await imp('game/sequel/texts.js');
fs.writeFileSync(path.join(tmp, 'game/sequel/rules_only.js'), fs.readFileSync(path.join(tmp, 'game/sequel/rules.js'), 'utf8'));
const { SEQUEL_CLASSES, SEQUEL_HEROES } = await imp('game/sequel/rules.js');

const clean = (o) => JSON.parse(JSON.stringify(o, (k, v) => (typeof v === 'function' ? undefined : v instanceof RegExp ? { regex: v.source } : v)));
const write = (rel, o) => { const f = path.join(out, rel); fs.mkdirSync(path.dirname(f), { recursive: true }); fs.writeFileSync(f, JSON.stringify(o, null, 1) + '\n'); };
const pad = (n) => String(n).padStart(2, '0');
for (const m of MISSIONS) { const { on, ...d } = m; write(`containment/${pad(m.id)}.json`, { ...clean(d), handlers: Object.keys(on || {}) }); }
for (const sc of SCENARIOS) {
  const { on, make, ...d } = sc;
  const variants = {};
  for (const t of SCENARIO_TRIBES) {
    const v = [0, 1, 2].map((df) => clean(make ? make(t, df) : {}));
    const same = v.every((x) => JSON.stringify(x) === JSON.stringify(v[0]));
    variants[t] = same ? v[0] : { 0: v[0], 1: v[1], 2: v[2] };
  }
  write(`scenarios/${pad(sc.id)}.json`, { ...clean(d), variants, handlers: Object.keys(on || {}) });
}
write('cast.json', CAST);
write('texts.json', clean(SEQUEL_TEXTS));
write('classes.json', { classes: clean(SEQUEL_CLASSES), heroes: clean(SEQUEL_HEROES) });
write('scenario_tribes.json', { tribes: SCENARIO_TRIBES, names: TRIBE_NAMES, kit: KIT });
fs.rmSync(tmp, { recursive: true, force: true });
console.log(`${MISSIONS.length} missions, ${SCENARIOS.length} scenarios`);
