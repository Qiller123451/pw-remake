// Golden values of the JS combat (remake/src/game/systems/combat.js, effects.js, items.js addScalps) for the C# port
// (src/Sim/Systems): node --no-warnings --import ./sys1_hooks.mjs sys1_combat_dump.mjs <remake/src> <data folder> <out.json>
//
// Builds the JS World headless (three.js stubbed: sys1_three.mjs; the models are the manifest's names only - no
// clips, a radius of 0.5) and writes, in the same order the C# test (tests/PwRemake.Tests/Sys1GoldenTests.cs) asks:
//   damage  every unit class with a weapon (its lowest level; every 4th also at level 5) hits a sample of unit and
//           building classes with each of its weapons: TakeDmg's damage and the hit points lost (Damage: ceil, >= 1)
//   auras   a layout of aura sources (warcry, warpaint, cauldrons, drums, pennant, scarecrow, skull protector) and
//           every hero at its highest level among friends and enemies: after one aura update every unit's buckets
//           and aura values, and a hit of every unit on its neighbour
//   area    splash damage of the catapults / mortars at victims 0 .. R m away (falloff dmg -> enddmg)
//   poison  two poisons on one victim, stepped for 30 s: the hit points after every step
//   skulls  kills of every class by another player: the skulls and kills the killer gets
import fs from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { deep } from './sys1_three.mjs';

const [src, data, out] = process.argv.slice(2);
if (!src || !data || !out) { console.error('usage: sys1_combat_dump.mjs <remake/src> <data folder> <out.json>'); process.exit(2); }
const imp = (p) => import(pathToFileURL(path.join(src, p)).href);
const { Rules } = await imp('game/rules.js');
const { World } = await imp('game/world.js');
const { Player } = await imp('game/player.js');
console.warn = () => {};
const R = new Rules(JSON.parse(fs.readFileSync(path.join(data, 'techtree.json'), 'utf8')), JSON.parse(fs.readFileSync(path.join(data, 'gamedata.json'), 'utf8')));
const manifest = JSON.parse(fs.readFileSync(path.join(data, 'assets', 'manifest.json'), 'utf8')).models;
const models = new Set(Object.keys(manifest).map((k) => k.toLowerCase()));
const tpl = () => ({ clips: [], radius: 0.5, height: 1.8, info: {}, scene: Object.assign(deep(), { children: [], traverse() {} }), links: {} });
const newWorld = () => new World({ scene: { add() {}, remove() {} }, data: R, templates: (n) => (n && models.has(String(n).toLowerCase()) ? tpl() : null), templateInfo: () => null, height: () => 0, size: 512 });

const tribes = ['Hu', 'Aje', 'Ninigi', ...(R.index && [...R.index.values()].some((e) => e.tribe === 'SEAS') ? ['SEAS'] : [])];
const sorted = (a) => [...a].sort();
const names = sorted(R.index.keys());
const info = (n) => R.info(n);
const unitTypes = new Set(['CHTR', 'ANML', 'VHCL', 'SHIP']);
// (heroes are of the tribe 'Special': a Hu player owns them here)
const unitClasses = names.filter((n) => unitTypes.has(info(n).type) && (tribes.includes(info(n).tribe) || info(n).tribe === 'World' || info(n).tribe === 'Special'));
// the first aggressive wild animal (scarecrow and hero auras on wild animals)
const wildBeast = () => unitClasses.find((n) => info(n).tribe === 'World' && info(n).type === 'ANML' && R.def(n, null).aggressive === 1);
const tribeOf = (n) => (tribes.includes(info(n).tribe) ? info(n).tribe : info(n).tribe === 'World' ? null : 'Hu');
const bldgClasses = names.filter((n) => info(n).type === 'BLDG' && tribes.includes(info(n).tribe));
// an upgrade of a player as production.js completeUpgrade switches it (the filters of its results)
function invent(p, id) {
  const a = R.actions(p).find((x) => x.id === id);
  if (!a) return false;
  for (const r of a.results) {
    const pth = (r.path || '').replace(/^\/?Filters\//, '');
    if (!pth || r.cls === 'local') continue;
    if (r.type === 'deactivate') p.tt.disable(pth); else p.tt.enable(pth);
  }
  if (!a.results.length) p.tt.enable(a.path.replace(/^\/Actions\//, ''));
  return true;
}
const r6 = (x) => (x == null ? null : Math.round(x * 1e6) / 1e6);
const res = { tribes, damage: null, auras: null, area: null, poison: null, skulls: null };

// ------------------------------------------------------------------ damage per hit
{
  const W = newWorld();
  const P = (id, tribe) => { const p = new Player(id, tribe, { rules: R }); W.players.push(p); return p; };
  const atk = {}, vic = {}, vet = {};
  tribes.forEach((t, i) => { atk[t] = P(i, t); });
  tribes.forEach((t, i) => { vic[t] = P(10 + i, t); });
  tribes.forEach((t, i) => { vet[t] = P(20 + i, t); });
  const ownerOf = (set, n) => (tribeOf(n) ? set[tribeOf(n)] : null);
  // (units on a grid of 8 m from (-240, -240), buildings on a grid of 40 m from (-220, 60))
  let ui = 0, bi = 0;
  const place = () => { const i = ui++; return [-240 + (i % 60) * 8, -240 + Math.floor(i / 60) * 8]; };
  const victims = [];
  unitClasses.forEach((n, i) => { if (i % 3 !== 0) return; const [px, pz] = place(); const v = W.spawnUnit(n, ownerOf(vic, n), px, pz); if (v) victims.push(v); });
  bldgClasses.forEach((n, i) => { if (i % 4 !== 0) return; const k = bi++; const b = W.placeBuilding(n, ownerOf(vic, n), -220 + (k % 12) * 40, 60 + Math.floor(k / 12) * 40, 0, true); if (b) victims.push(b); });
  const rows = [];
  unitClasses.forEach((n, i) => {
    const levels = [R.minLevel(n, ownerOf(atk, n))];
    if (i % 4 === 0 && R.hasLevel(n, 5) && levels[0] !== 5) levels.push(5);
    for (const lv of levels) {
      const set = lv === levels[0] ? atk : vet;
      const [px, pz] = place();
      const A = W.spawnUnit(n, ownerOf(set, n), px, pz, lv);
      if (!A || !A.weapons.long) continue;
      for (const w of A.weapons.all) {
        const dm = [], loss = [];
        for (const V of victims) {
          const hp0 = V.hp; V.hp = 1e9;
          const d = W.takeDmg(A, V, 1, 0, w);
          loss.push(1e9 - V.hp); dm.push(r6(d));
          V.hp = hp0; V.alive = true;
        }
        rows.push({ a: n, lvl: lv, w: w.id, dmg: dm, loss });
      }
    }
  });
  res.damage = { victims: victims.map((v) => [v.name, v.level]), rows };
}

// ------------------------------------------------------------------ auras
{
  const W = newWorld();
  const pl = {};
  tribes.forEach((t, i) => { const p = new Player(i, t, { rules: R }); W.players.push(p); pl[t] = p; });
  for (const t of tribes) for (const id of ['warcry', 'warpaint', 'drums', 'pennant']) invent(pl[t], id);
  const ents = [];
  const spawn = (n, t, x, z, lv) => { const u = W.spawnUnit(n, t ? pl[t] : null, x, z, lv); if (u) { u.radius = 0.5; ents.push(u); } return u; };
  const bld = (n, t, x, z) => { const b = W.placeBuilding(n, pl[t], x, z, 0, true); if (b) ents.push(b); return b; };
  // a group per source: the source, a friend, an enemy next to it
  const groups = [['hu_warrior', 'Hu', 3], ['hu_warrior', 'Hu', 4], ['hu_warrior', 'Hu', 5], ['aje_warrior', 'Aje', 3], ['aje_warrior', 'Aje', 5],
    ['hu_scout', 'Hu', 0], ['hu_rhino', 'Hu', 0], ['ninigi_parasaurolophus_drums', 'Ninigi', 0], ['ninigi_smokebomb_thrower', 'Ninigi', 0]];
  let gx = -200;
  const friendOf = { Hu: 'hu_spearman', Aje: 'aje_spearman', Ninigi: 'ninigi_archer', SEAS: 'seas_marine' };
  const enemyOf = { Hu: 'aje_spearman', Aje: 'hu_spearman', Ninigi: 'hu_archer', SEAS: 'ninigi_archer' };
  for (const [n, t, lv] of groups) {
    if (!R.exists(n)) continue;
    const s = spawn(n, t, gx, -150, lv || undefined);
    if (!s) continue;
    spawn(friendOf[t], t, gx + 3, -150); spawn(enemyOf[t], tribes.find((q) => q !== t), gx - 3, -150);
    gx += 50;
  }
  gx = -200;
  for (const [n, t] of [['hu_magic_cauldron', 'Hu'], ['aje_scarecrow', 'Aje'], ['ninigi_cauldron', 'Ninigi'], ['aje_skull_protector', 'Aje'], ['ninigi_smoke_tower', 'Ninigi']]) {
    if (!R.exists(n)) continue;
    const b = bld(n, t, gx, -50);
    if (!b) continue;
    spawn(friendOf[t], t, gx + 8, -50); spawn(enemyOf[t], tribes.find((q) => q !== t), gx - 8, -50);
    if (n === 'aje_scarecrow') spawn(wildBeast(), null, gx, -42);
    if (n === 'ninigi_smoke_tower') bld('ninigi_fireplace', 'Ninigi', gx, -30);
    gx += 70;
  }
  // every hero at its highest level, a friend of each type and an enemy beside it
  gx = -200;
  let gz = 100;
  const heroes = names.filter((n) => tribeOf(n) && R.isA(n, 'CHero'));
  for (const n of heroes) {
    const t = tribeOf(n);
    let lv = 5; while (lv > 1 && !R.hasLevel(n, lv)) lv--;
    const h = spawn(n, t, gx, gz, lv);
    if (!h) continue;
    spawn(friendOf[t], t, gx + 3, gz); spawn(enemyOf[t], tribes.find((q) => q !== t), gx - 3, gz);
    spawn(wildBeast(), null, gx, gz + 4);
    gx += 45; if (gx > 220) { gx = -200; gz += 60; }
  }
  W.auraUpdate();
  const auraOf = (e) => {
    const a = e.st.aura;
    return { name: e.name, b: Object.values(e.st.bonus).map(r6), warcry: a.warcry || 0, warpaint: a.warpaint || 0, drums: !!a.drums, pennant: !!a.pennant,
      invisible: !!a.invisible, cauldron: !!a.ninigiCauldron, skull: !!a.skullProtect, slowhand: r6(a.slowhand || 0), kleemann: r6(a.kleemann || 0),
      noAnimalAggro: !!a.noAnimalAggro, drain: (a.drainBy || []).length };
  };
  const hits = [];
  for (let i = 0; i + 1 < ents.length; i++) {
    const A = ents[i], V = ents[i + 1];
    if (A.kind !== 'unit' || !A.weapons.long) { hits.push(null); continue; }
    const hp0 = V.hp; V.hp = 1e9;
    const d = W.takeDmg(A, V, 1, 0);
    hits.push(r6(d));
    V.hp = hp0;
  }
  res.auras = { ents: ents.map(auraOf), hits };
}

// ------------------------------------------------------------------ area damage
{
  const W = newWorld();
  const pl = tribes.map((t, i) => { const p = new Player(i, t, { rules: R }); W.players.push(p); return p; });
  const rows = [];
  let x = -200;
  for (const n of unitClasses) {
    const p = pl[tribes.indexOf(info(n).tribe)];
    if (!p) continue;
    const A = W.spawnUnit(n, p, x, -200);
    if (!A) continue;
    const w = A.weapons.all.find((q) => A.cs(q).splash > 0);
    if (!w) { A.alive = false; continue; }
    const cs = A.cs(w);
    A.weapon = w;
    const other = pl[(tribes.indexOf(info(n).tribe) + 1) % pl.length];
    const vs = [];
    const R0 = cs.splash;
    for (let k = 0; k < 6; k++) { const v = W.spawnUnit(other.tribe === 'Hu' ? 'hu_spearman' : other.tribe === 'Aje' ? 'aje_spearman' : other.tribe === 'Ninigi' ? 'ninigi_spearman' : 'seas_marine', other, x + 0.5 + k * R0 / 5, -170); if (v) { v.hp = 1e9; v.radius = 0.5; vs.push(v); } }
    const c = { x: x, y: 0, z: -170 };
    const nv = W.areaDamage(A, c, R0, cs.dmg, cs.endDmg, {});
    rows.push({ a: n, w: w.id, R: R0, n: nv, loss: vs.map((v) => 1e9 - v.hp), d: vs.map((v) => r6(Math.max(0, Math.hypot(v.pos.x - c.x, v.pos.z - c.z) - v.radius))) });
    for (const v of vs) v.alive = false;
    A.alive = false;
    x += 25; if (x > 220) break;
  }
  res.area = rows;
}

// ------------------------------------------------------------------ poison
{
  const W = newWorld();
  const p0 = new Player(0, 'Aje', { rules: R }), p1 = new Player(1, 'Hu', { rules: R });
  W.players.push(p0, p1);
  const A = W.spawnUnit('aje_spearman', p0, 0, 0), V = W.spawnUnit('hu_warrior', p1, 5, 0);
  V.hp = V.maxHp = 1000;
  const hps = [];
  W.applyPoison(V, 7, 4, A);
  for (let i = 0; i < 600; i++) {
    W.time = Math.round((W.time + 0.05) * 1e9) / 1e9;
    if (i === 40) W.applyPoison(V, 12, 3, A);
    if (i === 90) W.applyPoison(V, 5, 6, A);
    if (V.st.poison) W.poisonUpdate(V);
    hps.push(V.hp);
  }
  res.poison = hps;
}

// ------------------------------------------------------------------ skulls
{
  const W = newWorld();
  const pl = tribes.map((t, i) => { const p = new Player(i, t, { rules: R }); W.players.push(p); return p; });
  const killer = new Player(9, 'Hu', { rules: R }); W.players.push(killer);
  const rows = [];
  let x = -240;
  for (const n of unitClasses) {
    const t = info(n).tribe;
    const u = W.spawnUnit(n, t === 'World' ? null : pl[tribes.indexOf(t)], x, 0);
    x += 3;
    if (!u) continue;
    const s0 = killer.res.skulls, k0 = killer.kills;
    u.lastDamager = killer;
    W.kill(u, null);
    rows.push([n, killer.res.skulls - s0, killer.kills - k0]);
  }
  res.skulls = rows;
}

fs.writeFileSync(out, JSON.stringify(res));
console.log(`${out}: damage ${res.damage.rows.length} rows x ${res.damage.victims.length} victims, auras ${res.auras.ents.length}, area ${res.area.length}, skulls ${res.skulls.length}`);
