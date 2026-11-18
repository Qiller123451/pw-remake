// Golden values of the JS moves (remake/src/game/systems/moves.js + moves_mirage.js) and the nests' areas (animals.js
// Area) for the C# port (src/Sim/Systems/Moves*.cs, Animals.cs):
//   node --import ./node_hooks.mjs tests/golden/sys3_dump.mjs <remake/src> <data folder> <out.json>
// tests/PwRemake.Tests/Sys3GoldenTests.cs runs it and compares:
//   moves      the MOVES table: every entry's kind (self / target / range / auto ...), AUTO_ORDER, AUTO_INVENT
//   offers     per tribe (a fresh tree and one with every upgrade of the tribe switched on) and unit class and level: the
//              moves the unit offers (World.movesOf), their cooldown (the action's time), the server's check
//              (rules.check with the unit), and its automatic moves in the order they are tried
//   areas      Area.contains on a grid, radius, randomPoint with a seeded random function (mulberry32)
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const [src, data, out] = process.argv.slice(2);
if (!src || !data || !out) { console.error('usage: sys3_dump.mjs <remake/src> <data folder> <out.json>'); process.exit(2); }
const imp = (p) => import(pathToFileURL(path.join(src, p)).href);
const { Rules } = await imp('game/rules.js');
const { MOVES, AUTO_ORDER, AUTO_INVENT } = await imp('game/systems/moves.js');
await imp('game/systems/moves_mirage.js');
const { Area } = await imp('game/systems/animals.js');
const tt = JSON.parse(fs.readFileSync(path.join(data, 'techtree.json'), 'utf8'));
const json = JSON.parse(fs.readFileSync(path.join(data, 'gamedata.json'), 'utf8'));
const R = new Rules(tt, json);

const dump = { data, mod: json.mod || '', moves: {}, autoOrder: AUTO_ORDER, autoInvent: AUTO_INVENT, scenarios: [], areas: [] };
for (const [id, m] of Object.entries(MOVES)) {
  dump.moves[id] = { self: !!m.self, target: m.target ?? null, range: m.range ?? 0, rangeOf: !!m.rangeOf, noCooldown: !!m.noCooldown,
    chase: !!m.chase, anySpirit: !!m.anySpirit, auto: !!m.auto };
}

const names = [...R.index.keys()].sort();
const tribes = ['Hu', 'Aje', 'Ninigi', 'SEAS'].filter((t) => tt.StartTT.Objects[t]);
for (const t of tribes) {
  const F = tt.Filters[t] || {};
  const ups = [];
  for (const [loc, v] of Object.entries(F.Upgrades || {})) if (v && typeof v === 'object') for (const a of Object.keys(v)) if (v[a] && typeof v[a] === 'object') ups.push(`${t}/Upgrades/${loc}/${a}`);
  for (const [label, filters] of [['fresh', []], ['upgraded', ups]]) {
    const o = { tt: R.newTree(t), tribe: t, debug: false, maxEpoch: 0 };
    o.tt.suspend(); for (const f of filters) o.tt.enable(f); o.tt.resume();
    const sc = { label: t + ':' + label, tribe: t, filters, version: o.tt.version, classes: {} };
    for (const n of names) {
      const ix = R.info(n);
      if (ix.type === 'BLDG' || (ix.tribe !== t && ix.tribe !== 'Special')) continue;
      const levels = {};
      for (let l = 1; l <= 5; l++) {
        if (!R.hasLevel(n, l)) continue;
        const e = { name: n, kind: 'unit', def: R.def(n, o), cls: ix.type, level: l, localInvents: new Set() };
        const moves = R.actionsOf(o, e).filter((a) => a.kind === 'Moves');
        if (!moves.length) continue;
        const auto = moves.filter((a) => MOVES[a.id] && MOVES[a.id].auto);
        levels[l] = {
          moves: moves.map((a) => ({ id: a.id, time: a.time, check: R.check(o, a, e), handled: !!MOVES[a.id] })),
          auto: AUTO_ORDER.map((id) => auto.find((a) => a.id === id)).filter(Boolean).map((a) => a.id),
        };
      }
      if (Object.keys(levels).length) sc.classes[n] = levels;
    }
    dump.scenarios.push(sc);
  }
}

// the nests' areas: shapes of game coordinates, an oval being the ellipse in its box (empty boxes are dropped)
function mulberry32(a) {
  return () => { a |= 0; a = (a + 0x6D2B79F5) | 0; let t = Math.imul(a ^ (a >>> 15), 1 | a); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };
}
const shapeSets = [
  [{ oval: true, x0: -20, z0: -10, x1: 20, z1: 10 }],
  [{ oval: false, x0: 15, z0: -30, x1: 40, z1: -5 }, { oval: true, x0: -50, z0: 20, x1: -10, z1: 70 }, { oval: false, x0: 5, z0: 5, x1: 5, z1: 9 }],
  [{ oval: true, x0: 100, z0: 100, x1: 160, z1: 112 }, { oval: true, x0: 120, z0: 90, x1: 131, z1: 140 }],
  [],
];
shapeSets.forEach((shapes, i) => {
  const A = new Area(shapes);
  const rand = mulberry32(1234 + i);
  const pts = [];
  for (let k = 0; k < 25; k++) pts.push(A.randomPoint(rand));
  const grid = [];
  for (let x = -60; x <= 170; x += 7.5) for (let z = -40; z <= 150; z += 6.25) grid.push(A.contains(x, z) ? 1 : 0);
  dump.areas.push({ shapes, seed: 1234 + i, empty: A.empty, radius: A.radius(), points: pts, grid });
});
const C = Area.circle(3, -4, 12);
dump.circle = { shapes: C.shapes, radius: C.radius() };

fs.writeFileSync(out, JSON.stringify(dump, (k, v) => (v === undefined ? null : v)));
console.log(`sys3_dump: ${Object.keys(dump.moves).length} moves, ${dump.scenarios.length} scenarios -> ${out}`);
