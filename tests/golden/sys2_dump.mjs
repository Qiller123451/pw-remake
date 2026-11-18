// Golden values of the JS remake for the sys2 port (src/Sim/Systems: WallMap, Economy, Construction, Production):
//   node --import ./tests/golden/sys2_hooks.mjs tests/golden/sys2_dump.mjs <remake/src> <data folder> <out.json>
// tests/PwRemake.Tests/Sys2GoldenTests.cs runs the same on the C# port and compares.
//
//   walls     game/wallmap.js on a 256 m world (a flat and a hilly ground, a few trees): BuildName for every
//             connection set (a hash), CalcWall for a list of lines / gates / towers / traps, then pieces put on the
//             grid one line after another (AddObj), a gate replacing a piece, a tower on a piece, a piece removed -
//             after each step the parts, pathfinder mask and arm mask of every piece (SetParts) and the gates' ends
//   formulas  per tribe: the workers' carrying capacity and time factor and the loops of a load (economy.js
//             beginWork), production / research times of every Build and Upgrades action (production.js
//             productionTime: normal, a computer player's handicaps, the debug speed), build times of every building
//             for 1..4 workers (construction.js constructionUpdate), skull costs of the pyramid
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const [src, data, out] = process.argv.slice(2);
if (!src || !data || !out) { console.error('usage: sys2_dump.mjs <remake/src> <data folder> <out.json>'); process.exit(2); }
const imp = (p) => import(pathToFileURL(path.join(src, p)).href);
const { Rules } = await imp('game/rules.js');
const { WallMap, buildName } = await imp('game/wallmap.js');
const { Economy, NODE_TF } = await imp('game/systems/economy.js');
const { Production } = await imp('game/systems/production.js');
const tt = JSON.parse(fs.readFileSync(path.join(data, 'techtree.json'), 'utf8'));
const json = JSON.parse(fs.readFileSync(path.join(data, 'gamedata.json'), 'utf8'));
const R = new Rules(tt, json);
const fnvStr = (s) => { let h = 0x811c9dc5; for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; } return h; };
const result = { data: path.basename(data) };

// ------------------------------------------------------------------ BuildName for every connection set
{
  const vals = [-10, -1, 0, 1], lines = [];
  for (let k = 0; k < 65536; k++) {
    const rel = []; let x = k;
    for (let d = 0; d < 8; d++) { rel.push(vals[x & 3]); x >>= 2; }
    lines.push(buildName(rel).join('.'));
  }
  result.buildNameHash = fnvStr(lines.join('\n'));
  result.buildNameSample = [0, 1, 5, 17, 85, 170, 255, 1000, 4369, 21845, 43690, 65535].map((k) => lines[k]);
}

// ------------------------------------------------------------------ the wall grid
const TERRAINS = {
  flat: () => 0,
  hilly: (x, z) => 4 * Math.sin(x / 23) + 3 * Math.cos(z / 17) + 0.02 * x,
};
const TREES = [[30, 30], [33, 28], [-50, 40], [-52, 44], [-48, 41], [-55, 39], [60, -60]];
const OWNER = { id: 1, tribe: 'Hu' };
// the scenario: lines, gates, towers and traps asked (CalcWall), then put down in order
const QUERIES = [
  ['hu_palisade', -40, -20, 40, -20], ['hu_palisade', -40, -40, 20, 30], ['hu_small_wall', 10, 10, 10, 90],
  ['hu_palisade', 25, 25, 45, 35], ['hu_palisade', -60, 40, -40, 40], ['aje_clay_wall', -80, -80, 80, 70],
  ['hu_small_tower', 0, -20, 0, -20], ['hu_palisade_gate', 4, -20, 4, -20], ['ninigi_pitfall', 50, 50, 50, 50],
  ['seas_fence', -100, 100, -20, 60],
];
const sceneOf = (terrain) => {
  const hf = TERRAINS[terrain];
  const trees = TREES.map(([x, z], i) => ({ id: i, type: 'tree', alive: true, pos: { x, z }, radius: 1.5 }));
  const blds = [];
  const W = {
    size: 256, waterLevel: null, mapKind: 'flat', height: hf, steepCell: null, wetCell: null,
    nav: { idx: (x, z) => Math.floor((z + 128) / 2) * 128 + Math.floor((x + 128) / 2) },
    rHash: { query: (x, z, r, fn) => { for (const t of trees) fn(t); } },
    bHash: { query: (x, z, r, fn) => { for (const b of blds) fn(b); } },
    inBoxes: () => false,
  };
  const WM = new WallMap(4, 4); WM.world = W; WM.classes = json.wallClasses;
  return { W, WM, blds };
};
const tilesN = (w) => w ? { race: w.race, cls: w.cls, type: w.type, d: w.d ?? -1, tiles: w.tiles.map((t) => [t.i, t.j, t.existing ? 1 : 0, t.gateOK ? 1 : 0]) } : null;
result.walls = {};
for (const terrain of Object.keys(TERRAINS)) {
  const { WM, blds } = sceneOf(terrain);
  const R0 = { calc: [], steps: [] };
  for (const [n, sx, sz, ex, ez] of QUERIES) R0.calc.push(tilesN(WM.calcWall(OWNER, n, sx, sz, ex, ez)));
  const kindOf = (n) => R.def(n, null).wallKind;
  const add = (name, i, j, rot = 0) => {
    const [x, z] = WM.centre(i, j);
    const b = { name, def: { wallKind: kindOf(name) }, origin: { x, z }, pos: { x, z }, owner: OWNER, built: true, alive: true, rot,
      setParts(parts, pf, mask) { this.parts = parts; this.pf = pf; this.mask = mask; }, setGateEnds(on) { this.ends = on; } };
    blds.push(b); WM.add(b); return b;
  };
  const snap = (label) => R0.steps.push({ label, pieces: blds.filter((b) => b.alive).map((b) => [b.name, b.tile ? b.tile[0] : null, b.tile ? b.tile[1] : null, (b.parts || []).join('.'), b.pf ?? -1, b.mask ?? -1, b.ends === undefined ? -1 : b.ends ? 1 : 0, b.gateD ?? -1]),
    grid: (() => { const o = []; for (let j = -6; j <= 6; j++) for (let i = -8; i <= 8; i++) o.push(WM.word(i, j) + ':' + WM.partsOf(i, j).join('.') + ':' + WM.pfMask(i, j) + ':' + WM.mask(i, j)); return fnvStr(o.join('|')); })() });
  // line 1, line 2 (crossing), line 3
  for (const q of [QUERIES[0], QUERIES[1], QUERIES[2]]) {
    const w = WM.calcWall(OWNER, q[0], q[1], q[2], q[3], q[4]);
    if (w) for (const t of w.tiles) if (!t.existing) add(q[0], t.i, t.j);
    snap('line ' + q.join(' '));
  }
  // a gate on line 1
  {
    const w = WM.calcWall(OWNER, 'hu_palisade_gate', 4, -20, 4, -20);
    R0.gate = tilesN(w);
    if (w) {
      const [i, j] = WM.tileOf(4, -20), t = WM.at(i, j);
      if (t && t.wall) { t.wall.alive = false; WM.remove(t.wall); }
      add('hu_palisade_gate', i, j, WM.constructor.prototype.gateRot ? WM.gateRot(w.d) : -Math.atan2([-1, -1, -1, 0, 1, 1, 1, 0][w.d], [-1, 0, 1, 1, 1, 0, -1, -1][w.d]));
    }
    snap('gate');
  }
  // a tower on a piece of line 1, a trap
  {
    const w = WM.calcWall(OWNER, 'hu_small_tower', -28, -20, -28, -20);
    R0.tower = tilesN(w);
    if (w) { const t = w.tiles[w.tiles.length - 1]; add('hu_small_tower', t.i, t.j); }
    snap('tower');
    const tr = WM.calcWall(OWNER, 'ninigi_pitfall', 50, 50, 50, 50);
    R0.trap = tilesN(tr);
    if (tr) add('ninigi_pitfall', tr.tiles[0].i, tr.tiles[0].j);
    snap('trap');
  }
  // a piece of line 2 removed
  {
    const b = blds.find((x) => x.alive && x.name === 'hu_palisade' && x.tile && x.tile[0] === 0 && x.tile[1] === -1) || blds.find((x) => x.alive && x.name === 'hu_palisade');
    if (b) { b.alive = false; WM.remove(b); }
    snap('removed');
  }
  // CalcWall again after all that
  R0.after = QUERIES.map(([n, sx, sz, ex, ez]) => tilesN(WM.calcWall(OWNER, n, sx, sz, ex, ez)));
  result.walls[terrain] = R0;
}

// ------------------------------------------------------------------ formulas
const tribes = json.mod === 'MIRAGE' || (json.wallClasses && json.wallClasses.seas_wall) ? ['Hu', 'Aje', 'Ninigi', 'SEAS'] : ['Hu', 'Aje', 'Ninigi', 'SEAS'];
const selfTF = (s) => (s && s.tf > 0 ? s.tf : 2.0);
const K = { wood: 5, stone: 5, food: 3 };
result.formulas = {};
for (const tribe of tribes) {
  const owner = { tt: R.newTree(tribe), tribe, debug: false, maxEpoch: 0, pyramid: null };
  const F = { workers: [], prod: [], build: [] };
  for (const [name, ix] of R.index) {
    if (ix.tribe !== tribe || ix.type !== 'CHTR') continue;
    const d = R.def(name, owner);
    if (!d || !d.can_harvest) continue;
    const lv = R.minLevel(name, owner), st = R.stats(name, lv, owner);
    const u = { cls: 'CHTR', name, stats: st };
    const row = { name, level: lv, tf: selfTF(st) };
    for (const res of ['wood', 'stone', 'food']) {
      const cap = Economy.carryCap(u, res);
      const tf = res === 'wood' ? selfTF(st) : selfTF(st) * NODE_TF;
      row[res] = [cap, Math.max(1, Math.trunc(K[res] * tf * cap / Math.max(1, cap)))];
    }
    F.workers.push(row);
  }
  const acts = R.actions(owner);
  const ai = { ...owner, aiMods: { gather: 1.2, buildTime: 0.7, researchTime: 0.8, weaponTime: 1, unitLimit: 0 } };
  const ch = { ...owner, cheat: { speed: 2.5, gather: 1 } };
  for (const a of acts) {
    if (a.kind !== 'Build' && a.kind !== 'Upgrades') continue;
    F.prod.push([a.path, Production.productionTime(a, owner), Production.productionTime(a, ai), Production.productionTime(a, ch)]);
    if (a.cat === 'Build/BLDG') {
      const wtf = F.workers.length ? F.workers[0].tf : 2;
      const D = a.time > 0 ? a.time : 10;
      F.build.push([a.path, [1, 2, 3, 4].map((N) => D * wtf * (0.1 + 0.9 * Math.pow(0.8, N - 1)))]);
    }
  }
  result.formulas[tribe] = F;
}
result.skulls = [];
for (let a = 1; a <= 5; a++) for (let b = a; b <= 5; b++) { let c = 0; for (let l = a + 1; l <= b; l++) c += R.levelupSkulls[l - 2] || 0; result.skulls.push([a, b, c]); }
result.pyramid = R.pyramid;
fs.writeFileSync(out, JSON.stringify(result));
console.log('wrote', out);
