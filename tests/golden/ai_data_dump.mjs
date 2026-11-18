// Golden values of the JS computer player's tables (remake/src/game/ai/data.js) for the C# port (src/Sim/Ai/Data.cs):
//   node tests/golden/ai_data_dump.mjs <remake/src> <data folder | -> <out.json>
// Loads ai.json of a data folder (or, with "-", none: the built-in fallbacks) as main.js does (setAiData) and writes
// a list of queries { op, args, r } in a normalised shape. tests/PwRemake.Tests/AiDataGoldenTests.cs runs the same
// queries on the C# AiData and compares.
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const [src, data, out] = process.argv.slice(2);
if (!src || !data || !out) { console.error('usage: ai_data_dump.mjs <remake/src> <data folder | -> <out.json>'); process.exit(2); }
const D = await import(pathToFileURL(path.join(src, 'game', 'ai', 'data.js')).href);
const json = data === '-' ? null : JSON.parse(fs.readFileSync(path.join(data, 'ai.json'), 'utf8'));
D.setAiData(json);

const N = (x) => (x === undefined ? null : x);
const rows = (l) => (l || []).map((e) => [e.request, N(e.objFlag), N(e.count), N(e.unique)]);
const army = (a) => a && a.map((e) => ({ min: e.min, max: e.max, alternatives: e.alternatives.map((x) => ({ cls: x.cls, level: N(x.level), objFlag: N(x.objFlag) })) }));
const mix = (m) => {
  if (!m) return null;
  const o = {};
  for (const k of Object.keys(m).sort()) o[k] = m[k].map((g) => ({ weight: N(g.weight), units: g.units.map((u) => ({ cls: u.cls, npc: N(u.npc), objFlag: N(u.objFlag) })) }));
  return o;
};
const beh = (b) => b && {
  params: Object.keys(b.params || {}).map((k) => [k, b.params[k]]), paused: !!b.paused, economyOff: !!b.economyOff, brainWash: !!b.brainWash,
  variants: b.variants || 1, kindergarten: b.kindergarten !== false, personality: N(b.personality), valueTable: N(b.valueTable), subStrategy: N(b.subStrategy),
};
const level = (l) => ({ class: l.class, wait: l.wait, controlWait: l.controlWait, gift: l.gift, attackStrength: l.attackStrength, quickAttackStrength: l.quickAttackStrength,
  pyramidAttackUnits: l.pyramidAttackUnits, gather: l.gather, buildTime: l.buildTime, researchTime: l.researchTime, unitLimit: l.unitLimit, weaponDuration: l.weaponDuration });
// a context of table conditions; has: the classes the player owns
const ctxOf = (c) => ({ ...c, cls: D.level(c.d).class, has: (x) => c.has.includes(x) });

const Q = [];
const q = (op, args, r) => Q.push({ op, args, r: N(r) });
const TRIBES = ['Hu', 'Aje', 'Ninigi', 'SEAS'], CLS = ['Easy', 'Medium', 'Hard'];
for (let d = 0; d <= 9; d++) { q('level', [d], level(D.level(d))); for (const mm of [true, false]) q('gift', [d, mm], D.gift(d, mm)); }
for (const c of CLS) {
  for (let a = 1; a <= 5; a++) q('maxWorkers', [c, a], D.maxWorkers(c, a));
  q('squadTimeout', [c], D.squadTimeout(c)); q('kindergartenWait', [c], D.kindergartenWait(c));
  for (const g of ['suicide', 'pureviolence', 'blitz', 'quick', 'rider', 'siege', 'stealth', 'guerilla', 'singleplayer', 'nonsense']) q('squad', [g, c], D.squad(g, c));
}
for (const n of ['Dodo', 'Giraffe', 'Schnecke', 'Turtle', 'FightOnly', 'Mikrobe', 'Singleplayer_L3_1', 'Singleplayer_L8_2', 'Singleplayer_L9_9', 'Nope']) q('behaviour', [n], beh(D.behaviour(n)));
q('behaviourNames', [], D.behaviourNames());
const HAS = [[], ['hu_warrior', 'hu_mammoth', 'aje_shaman', 'ninigi_ninja', 'aje_brachiosaurus', 'hu_rhino']];
for (const tribe of TRIBES) for (const letter of ['D', 'G', 'S', 'X']) for (let age = 1; age <= 5; age++) for (const d of [0, 4, 9]) for (const multimap of [true, false]) {
  const has = HAS[(age + d) % 2];
  const c = { tribe, letter, age, d, multimap, levelName: multimap ? null : 'Single 05', player: 1, tactic: 1, harbour: multimap, watermap: multimap && age > 2, warpgate: d === 9, ownWarpgate: false, has, defenderGame: false, defender: false };
  q('buildList', [c], rows(D.buildList(ctxOf(c))));
  q('counterShips', [c], rows(D.counterShips(ctxOf(c))));
}
const LEVELS = [null, 'Single 01', 'Single 03', 'Single 04', 'Single 09', 'Single 13'];
for (const tribe of TRIBES) for (const letter of ['D', 'G', 'S', 'X']) for (let tactic = 1; tactic <= 5; tactic++) for (const multimap of [true, false]) for (const [dg, df] of [[false, false], [true, false], [true, true]]) {
  const levelName = multimap ? null : LEVELS[(tactic + letter.charCodeAt(0)) % LEVELS.length];
  const c = { tribe, letter, age: 1, d: 4, multimap, levelName, player: tactic % 3, tactic, harbour: false, watermap: false, warpgate: false, ownWarpgate: false, has: [], defenderGame: dg, defender: df };
  q('attackPlan', [c], D.attackPlan(ctxOf(c)));
}
const AI = json;
for (const tribe of [...TRIBES, 'Nobody']) {
  q('hasArmies', [tribe], D.hasArmies(tribe));
  const names = new Set(['SuicideAttack_3', 'PureViolenceAttack_2', 'SiegeAttack_4', 'BlitzAttack_1', 'Quick', 'ShipAttack_3', 'OP_ResAll', 'RessourceOutpost', 'NoSuchArmy']);
  const T = AI && AI.armies && AI.armies[tribe];
  if (T) for (const t of ['Dodo', 'Singleplayer']) for (const k of Object.keys(T[t] || {})) names.add(k);
  for (const table of ['Dodo', 'Giraffe', 'Schnecke', 'Singleplayer', 'GiraffeHard']) for (const name of [...names].sort()) q('army', [tribe, table, name], army(D.army(tribe, table, name)));
  for (let age = 1; age <= 6; age++) q('unitMix', [tribe, age], mix(D.unitMix(tribe, age)));
  q('housing', [tribe], rows(D.housing(tribe)));
  for (const type of ['RessourceOutpost', 'AttackOutpost', 'OP_ResAll', 'OP_AttackAll', 'EmptyOutpost']) {
    const o = D.outpost(type, tribe);
    q('outpost', [type, tribe], o && o.map((r) => [r.request, r.objFlag || '', r.count || 1]));
  }
}
for (const name of ['L07_aje_easy', 'b43_ninjaattack', 'Level_8_Guards1', 'NoSuchArmy']) q('armyAny', [name], army(D.armyAny(name)));
for (let set = 0; set <= 3; set++) q('heroRequests', [set], rows(D.heroRequests(set)));
const spawn = new Set(['hu_warrior', 'aje_spearman', 'ninigi_archer', 'seas_warrior', 'no_such_class']);
if (AI && AI.spawnLevels) for (const k of Object.keys(AI.spawnLevels).slice(0, 40)) spawn.add(k);
for (const c of spawn) q('spawnLevel', [c], D.spawnLevel(c));
fs.writeFileSync(out, JSON.stringify({ hasAiData: D.hasAiData(), queries: Q }));
console.log(`${Q.length} queries`);
