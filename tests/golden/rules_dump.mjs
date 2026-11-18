// Golden values of the JS rules (remake/src/game/rules.js + techtree.js) for the C# port (src/Sim/Rules):
//   node tests/golden/rules_dump.mjs <remake/src> <data folder> <out.json>
// Loads techtree.json + gamedata.json of a data folder as main.js does (new Rules(tt, json)) and writes, for a broad
// sample of queries, the results in a normalised shape. tests/PwRemake.Tests/RulesGoldenTests.cs runs the same
// queries on the C# rules and compares field by field (RulesGolden.cs builds the same shape).
//
// Scenarios: the neutral tree (owner null) for every class; per tribe a fresh player tree and one with filters
// switched on (epochs 2 and 3, the base's BuildObjects filter, every 5th upgrade filter of the tribe, every 3rd
// BuildObjects filter, the level filters of a few units) - the list of filters is written into the dump so the C#
// side switches on exactly the same ones in the same order.
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const [src, data, out] = process.argv.slice(2);
if (!src || !data || !out) { console.error('usage: rules_dump.mjs <remake/src> <data folder> <out.json>'); process.exit(2); }
const { Rules, prettify } = await import(pathToFileURL(path.join(src, 'game', 'rules.js')).href);
const tt = JSON.parse(fs.readFileSync(path.join(data, 'techtree.json'), 'utf8'));
const json = JSON.parse(fs.readFileSync(path.join(data, 'gamedata.json'), 'utf8'));
const R = new Rules(tt, json);

const sorted = (a) => [...a].sort();
const N = (x) => (x === undefined ? null : x);
const statsN = (s) => s && {
  exists: s.exists, level: s.level, hp: s.hp, fow: s.fow, speed: s.speed, maxspeed: s.maxspeed, tf: s.tf, scalps: s.scalps,
  gfx: N(s.gfx), size: s.size, captainlevel: s.captainlevel, aggressive: s.aggressive, carry: s.carry, limits: s.limits,
  heal: s.heal, healMod: s.healMod, abilities: s.abilities, maxworkers: s.maxworkers, agility: s.agility,
  maxPassengers: s.maxPassengers, captain: s.captain,
};
const defN = (d) => d && {
  name: d.name, tribe: d.tribe, type: d.type, path: d.path, desc: N(d.desc), script: d.script, aggressive: d.aggressive,
  can_build: d.can_build, can_harvest: d.can_harvest, maxhelpers: d.maxhelpers, repletion: d.repletion, agility: d.agility,
  density: d.density, growup: d.growup, nightactive: d.nightactive, maxworkers: d.maxworkers, caste: d.caste, standanim: N(d.standanim),
  fx: N(d.fx), formationPos: d.formationPos, wall: d.wall, gate: d.gate, coastal: d.coastal, wallKind: d.wallKind, unlimited: d.unlimited,
  maxPassengers: d.maxPassengers, buildupAutoattack: d.buildupAutoattack, delivery: d.delivery, captain: d.captain,
  captain_gfx: N(d.captain_gfx), startTT: d.startTT, globalStartTT: d.globalStartTT, unique: d.unique, attackType: d.attackType,
  ladder: d.ladder, transportClass: d.transportClass,
  levels: [1, 2, 3, 4, 5].map((l) => statsN(d.levels[l])),
};
const weaponN = (w) => w && {
  id: w.id, path: w.path, level: w.level, secondary: w.secondary, slot: w.slot, dmg: w.dmg, mindmg: w.mindmg, enddmg: w.enddmg,
  range: w.range, minrange: w.minrange, freq: w.freq, splash: w.splash, def: w.def, rdef: w.rdef, ap: w.ap, poison: w.poison,
  poisonTicks: w.poisonTicks, bulletspeed: w.bulletspeed, falloff: w.falloff, jitter: w.jitter, size: w.size, caste: N(w.caste),
  penetration: w.penetration, penAngle: w.penAngle, fx: N(w.fx), projectile: w.projectile, bonus: w.bonus, defBonus: w.defBonus,
  anims: w.anims.map((a) => ({ name: N(a.name), delay: a.delay, shootdelay: a.shootdelay, combo: a.combo, follow: a.follow })),
  parts: w.parts.map((p) => ({ gfx: p.gfx, link: p.link, kind: N(p.kind) })),
};
const wsN = (s) => s && { w: s.w.id, dmg: s.dmg, endDmg: s.endDmg, ap: s.ap, splash: s.splash, prot: s.prot, rprot: s.rprot, range: s.range, minRange: s.minRange, dur: s.dur, poison: s.poison, poisonTicks: s.poisonTicks };
const actionN = (a) => ({
  id: a.id, tribe: a.tribe, kind: a.kind, cat: a.cat, type: a.type, path: a.path, time: a.time, visible: a.visible, disabled: a.disabled,
  cost: a.cost, req: a.req, breq: a.breq, levelReq: a.levelReq, casteReq: a.casteReq, tribes: a.tribes,
  locs: a.locs.map((l) => ({ at: l.at, icon: N(l.icon), desc: N(l.desc), hidden: l.hidden, localflags: l.localflags, ui: N(l.ui) })),
  results: a.results.map((r) => ({ obj: r.obj, path: N(r.path), type: N(r.type), cls: r.cls, level: r.level, rider: N(r.rider), resultActions: r.resultActions })),
  target: a.target, targetOwner: a.targetOwner, loc: a.loc ? a.loc.at : null,
});
const textN = (t) => ({ name: t.name, medium: t.medium, long: t.long });

const names = sorted(R.index.keys());
const owner = (tribe) => ({ tt: R.newTree(tribe), tribe, debug: false, maxEpoch: 0 });
const scenario = (label, o, filters, classes) => {
  for (const f of filters) o.tt.enable(f);
  const res = { label, tribe: o ? o.tribe : null, filters, version: o ? o.tt.version : 0, classes: {} };
  for (const n of classes) {
    const ix = R.info(n);
    const d = R.def(n, o);
    const c = { def: defN(d), minLevel: R.minLevel(n, o), column: R.column(n, o), weapons: {}, weaponStats: {}, actions: null, checks: null, levelFilters: {} };
    for (let l = 1; l <= 5; l++) {
      const s = R.weaponSet(n, l, o);
      c.weapons[l] = { long: s.long ? s.long.id : null, medium: s.medium ? s.medium.id : null, short: s.short ? s.short.id : null, all: s.all.map((w) => w.id) };
      c.levelFilters[l] = R.levelFilters(n, l);
    }
    for (const w of R.weaponSet(n, 5, o).all) { c.weaponStats[w.path] = { raw: weaponN(w), stats: wsN(R.weaponStats(w, n, o)) }; }
    c.statsAt = [1, 2, 3, 4, 5].map((l) => (R.statsAt(n, l, o) || {}).level ?? null);
    const kind = ix.type === 'BLDG' ? 'building' : 'unit';
    const acts = R.actionsOf(o, { name: n, kind, def: d, cls: ix.type });
    c.actions = acts.map(actionN);
    if (o) c.checks = acts.map((a) => R.check(o, a, { level: 3, localInvents: new Set() }));
    c.text = textN(R.text(n, o));
    res.classes[n] = c;
  }
  return res;
};

const dump = { data, mod: json.mod || '', count: names.length, scenarios: [], misc: {} };
// the neutral tree, every class
dump.scenarios.push(scenario('world', null, [], names));
const tribes = ['Hu', 'Aje', 'Ninigi', 'SEAS'].filter((t) => tt.StartTT.Objects[t]);
for (const t of tribes) {
  const mine = names.filter((n) => { const ix = R.info(n); return ix.tribe === t || ix.tribe === 'Special'; });
  dump.scenarios.push(scenario(t + ':fresh', owner(t), [], mine));
  // filters: the epochs, the base, a sample of upgrades and building filters, level filters of some units
  const F = tt.Filters[t] || {};
  const ups = [], blds = [];
  for (const [loc, v] of Object.entries(F.Upgrades || {})) if (v && typeof v === 'object') for (const a of Object.keys(v)) if (v[a] && typeof v[a] === 'object') ups.push(`${t}/Upgrades/${loc}/${a}`);
  for (const b of Object.keys(F.BuildObjects || {})) blds.push(`${t}/BuildObjects/${b}`);
  const filters = [];
  const base = (json.start && json.start[t] && json.start[t].base) || null;
  for (const a of ['age_2', 'age_3']) for (const p of ups) if (p.endsWith('/' + a)) filters.push(p);
  if (base) filters.push(`${t}/BuildObjects/${base}`);
  ups.forEach((p, i) => { if (i % 5 === 0 && !filters.includes(p)) filters.push(p); });
  blds.forEach((p, i) => { if (i % 3 === 0 && !filters.includes(p)) filters.push(p); });
  const units = mine.filter((n) => R.info(n).type === 'CHTR').slice(0, 6);
  for (const u of units) for (let l = 2; l <= 5; l++) for (const f of R.levelFilters(u, l)) if (!f.includes('_Bonus') && !filters.includes(f)) filters.push(f);
  dump.scenarios.push(scenario(t + ':upgraded', owner(t), filters, mine));
}
// misc
dump.misc.prettify = Object.fromEntries(names.slice(0, 200).map((n) => [n, prettify(n)]));
dump.misc.corpseFood = Object.fromEntries(names.filter((n) => R.info(n).type === 'ANML').map((n) => [n, R.corpseFood(n)]));
dump.misc.isGate = Object.fromEntries(names.map((n) => [n, R.isA(n, 'CGate')]));
dump.misc.isWild = Object.fromEntries(names.map((n) => [n, R.isWildClass(n)]));
dump.misc.hasLevel = Object.fromEntries(names.map((n) => [n, [1, 2, 3, 4, 5].map((l) => R.hasLevel(n, l))]));
dump.misc.startLevel = {};
for (const t of tribes) for (const a of R.actions(owner(t))) if (a.kind === 'Build') dump.misc.startLevel[a.path] = R.startLevel(a);
fs.writeFileSync(out, JSON.stringify(dump, (k, v) => (v === undefined ? null : v)));
console.log(`rules_dump: ${dump.scenarios.length} scenarios, ${names.length} classes -> ${out}`);
