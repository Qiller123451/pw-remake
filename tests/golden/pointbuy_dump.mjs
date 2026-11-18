// Golden values of the JS army budget (remake/src/game/pointbuy.js) for the C# port (src/Sim/Game/PointBuy.cs):
//   node tests/golden/pointbuy_dump.mjs <remake/src> <data folder> <out.json>
// Loads techtree.json + gamedata.json + ai.json of a data folder as main.js does and writes a list of queries
// { op, args, r } - prices, rates, totals, shops, presets, loaded armies, the computer players' armies - and a scripted
// shopping session (buy, buy lowest, sell, move, resources +/-) with the army after every step.
// tests/PwRemake.Tests/PointBuyTests.cs runs the same queries on the C# PointBuy and compares.
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const [src, data, out] = process.argv.slice(2);
if (!src || !data || !out) { console.error('usage: pointbuy_dump.mjs <remake/src> <data folder> <out.json>'); process.exit(2); }
const { Rules } = await import(pathToFileURL(path.join(src, 'game', 'rules.js')).href);
const { PointBuy } = await import(pathToFileURL(path.join(src, 'game', 'pointbuy.js')).href);
const tt = JSON.parse(fs.readFileSync(path.join(data, 'techtree.json'), 'utf8'));
const json = JSON.parse(fs.readFileSync(path.join(data, 'gamedata.json'), 'utf8'));
const ai = fs.existsSync(path.join(data, 'ai.json')) ? JSON.parse(fs.readFileSync(path.join(data, 'ai.json'), 'utf8')) : null;
const D = new Rules(tt, json);
const PB = new PointBuy(D, ai);

const N = (x) => (x === undefined ? null : x);
const sortObj = (o) => { const r = {}; for (const k of Object.keys(o || {}).sort((a, b) => +a - +b)) r[k] = o[k]; return r; };
const armyN = (a) => a && { cards: sortObj(a.cards), fixed: sortObj(a.fixed || {}), res: { food: a.res.food || 0, wood: a.res.wood || 0, stone: a.res.stone || 0 },
  min: { food: (a.min || {}).food || 0, wood: (a.min || {}).wood || 0, stone: (a.min || {}).stone || 0 },
  locked: Object.keys(a.locked || {}).filter((k) => a.locked[k]).sort(), collector: !!a.collector };
const presetN = (p) => p && { name: p.name, cost: p.cost, standard: !!p.standard, kind: N(p.kind), units: Object.entries(p.units || {}).map(([k, v]) => [k, v]), res: p.res || {} };
const Q = [];
const q = (op, args, r) => Q.push({ op, args, r: N(r) });

const TRIBES = ['Hu', 'Aje', 'Ninigi', 'SEAS'];
const pb = json.pointBuy || { costs: {}, heroes: {} };
q('ready', [], PB.ready);
// prices: every class of the price lists and the heroes, every level, every tribe
const names = new Set(['special_eusmilus', 'aje_resource_collector', 'nonexistent_class', 'worker']);
for (const t of TRIBES) for (const n of Object.keys(pb.costs[t] || {})) names.add(n);
for (const h of Object.keys(pb.heroes || {})) names.add(h);
for (const t of TRIBES) for (const n of [...names].sort()) q('prices', [n, t], [1, 2, 3, 4, 5].map((l) => PB.price(n, l, t)));
for (const n of [...names].sort()) q('isHero', [n], PB.isHero(n));
for (const t of [...TRIBES, 'Nobody']) for (const r of ['food', 'wood', 'stone', 'iron']) {
  q('rate', [r, t], PB.rate(r, t));
  for (const a of [0, 1, 7, 33, 100, 999]) q('resCost', [r, a, t], PB.resCost(r, a, t));
  for (const p of [0, 1, 2, 3, 100, 1001, 2999]) q('resFor', [r, p, t], PB.resFor(r, p, t));
}
for (const t of TRIBES) for (const c of [-1, 0, 500, 1000, 3000, 10000]) for (const mp of [true, false]) for (const b of [true, false]) q('total', [c, t, mp, b], PB.total(c, t, { mp, buildings: b }));
for (const t of TRIBES) for (const w of ['worker', 'Resource', 'Technics', 'Nature', 'Stina_s0', 'hu_warrior']) q('resolve', [w, t], PB.resolve(w, t));
// shops: whole and limited to a few classes
for (const t of TRIBES) {
  const shop = PB.shop(t);
  q('shop', [t, null], shop.map((e) => [e.cls, e.tab, N(e.prices)]));
  const allowed = shop.filter((e, i) => i % 3 === 0).map((e) => e.cls).concat(['Stina_s0']);
  q('shop', [t, allowed], PB.shop(t, new Set(allowed)).map((e) => [e.cls, e.tab, N(e.prices)]));
}
// presets and loading them
const CREDITS = [-1, 0, 500, 1000, 2000, 3000, 10000, 1234];
for (const t of TRIBES) for (const c of CREDITS) {
  const list = PB.presets(t, c);
  q('presets', [t, c], list.map(presetN));
  for (const kind of ['standard', 'boomer', 'rusher', 'none']) q('preset', [t, c, kind], presetN(PB.preset(t, c, kind)));
  for (const p of list) {
    for (const opt of [{}, { fixed: true, mp: false }, { buildings: false, mp: false }, { mp: true, lockedRes: { wood: 150 } }]) {
      const total = PB.total(c, t, { mp: opt.mp !== false, buildings: opt.buildings !== false });
      for (const tot of [total, -1, Math.floor(total / 2)]) q('load', [presetN(p), t, tot, opt], armyN(PB.load(p, t, tot, opt)));
    }
  }
}
for (const t of TRIBES) for (const b of [true, false]) for (const mp of [true, false]) q('clear', [t, b, mp], armyN(PB.clear(t, { buildings: b, mp })));
// the computer players' armies
for (const t of TRIBES) for (const c of [-1, 500, 1000, 1500, 3000, 10000, 2000000]) for (const sub of ['G1', 'D1', 'D2', 'R3', 'XX']) for (const d of [0, 4, 8]) q('aiPreset', [t, c, sub, d], presetN(PB.aiPreset(t, c, sub, d)));
for (const t of TRIBES) for (const n of ['warrior', 'scout', 'hu_worker']) q('genericClass', [n, t], PB.genericClass(n, t));
// a scripted shopping session per tribe and budget: a small LCG picks the steps; the army after every step
let seed = 12345;
const rnd = (n) => { seed = (seed * 1103515245 + 12345) % 2147483648; return seed % n; };
for (const t of TRIBES) for (const c of [1000, 3000, 10000]) {
  const total = PB.total(c, t);
  const shop = PB.shop(t);
  if (!shop.length) continue;
  const start = PB.load(PB.preset(t, c, 'standard'), t, total);
  const army = JSON.parse(JSON.stringify(start));
  const steps = [];
  for (let i = 0; i < 120; i++) {
    const k = rnd(8);
    let st;
    if (k <= 1) { const e = shop[rnd(shop.length)], lv = 1 + rnd(5), blocked = rnd(4) === 0 ? [0, 2, 1, 0, 0] : null; st = ['buy', e.cls, lv, blocked, PB.buy(army, e.cls, lv, total, t, blocked)]; }
    else if (k === 2) { const e = shop[rnd(shop.length)]; st = ['buyLowest', e.cls, null, null, PB.buyLowest(army, e.cls, total, t)]; }
    else if (k === 3) { const ks = Object.keys(army.cards); const s = ks.length ? +ks[rnd(ks.length)] : rnd(52); st = ['sell', s, null, null, PB.sell(army, s)]; }
    else if (k <= 5) {
      const ks = Object.keys(army.cards); const from = ks.length ? +ks[rnd(ks.length)] : 0, to = rnd(53) - 1;
      const ok = PB.canMove(army, from, to, total, t);
      if (ok) PB.move(army, from, to);
      st = ['move', from, to, null, ok];
    } else { const r = ['food', 'wood', 'stone'][rnd(3)], step = [1, 10, 100, 1000, -1, -10, -100, -1000][rnd(8)]; st = ['addRes', r, step, null, PB.addRes(army, r, step, total, t)]; }
    steps.push({ step: st, army: armyN(army), cost: PB.cost(army, t), left: PB.left(army, total, t), free: [1, 2, 3, 4, 5].map((l) => PB.freeSlot(army, l)) });
  }
  q('session', [t, c, total, armyN(start)], steps);
}
// game/skirmish.js: the default setup, opening slots, game types, the start screen's checks
const SK = await import(pathToFileURL(path.join(src, 'game', 'skirmish.js')).href);
const slotN = (s) => ({ type: s.type, tribe: s.tribe, personality: s.personality, level: s.level, team: s.team, color: s.color, hp: s.hp, hq: s.hq });
const setupN = (s) => ({ mode: s.mode, points: s.points, warpgate: s.warpgate, fog: s.fog, lockDiplomacy: s.lockDiplomacy, handicap: s.handicap, chooseHQ: s.chooseHQ, teams: s.teams, defenderMin: s.defenderMin, slots: s.slots.map(slotN) });
{
  const s = SK.defaultSetup({});
  q('skDefault', [], setupN(s));
  const ops = [['open', 2], ['open', 3], ['validate', 4], ['validate', 2], ['mode', 'domination'], ['validate', 4], ['team', 1, 1], ['team', 2, 1], ['team', 3, 1], ['validate', 8],
    ['color', 2, 'blue'], ['validate', 8], ['color', 2, 'green'], ['mode', 'defender'], ['validate', 8], ['hq', 0, 3], ['validate', 8], ['hq', 0, 0], ['hq', 1, 0], ['validate', 8],
    ['hq', 1, 5], ['validate', 4], ['hq', 1, 1], ['hq', 2, 2], ['hq', 3, 3], ['validate', 8], ['mode', 'deathmatch'], ['close', 2], ['open', 2], ['open', 7], ['validate', 8], ['teams', 3], ['open', 5], ['validate', 8]];
  for (const op of ops) {
    let r = null;
    if (op[0] === 'open') SK.openSlot(s, op[1]);
    else if (op[0] === 'close') s.slots[op[1]].type = 'closed';
    else if (op[0] === 'mode') SK.applyMode(s, op[1]);
    else if (op[0] === 'team') s.slots[op[1]].team = op[2];
    else if (op[0] === 'color') s.slots[op[1]].color = op[2];
    else if (op[0] === 'hq') s.slots[op[1]].hq = op[2];
    else if (op[0] === 'teams') s.teams = op[1];
    else if (op[0] === 'validate') r = SK.validate(s, op[1]);
    q('skOp', op, { r: N(r), setup: setupN(s) });
  }
}
fs.writeFileSync(out, JSON.stringify({ queries: Q }));
console.log(`${Q.length} queries -> ${out}`);
