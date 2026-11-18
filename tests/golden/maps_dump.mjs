// Golden data of the map port: runs the JS remake's map code (maps/ula.js, maps/source.js, maps/forest.js, mapgen.js,
// ground.js) in node on the real map files and writes what the C# port (src/Sim/Maps) must give - compared by
// tests/PwRemake.Tests/MapsGoldenTests.cs. Re-run after a change of the JS or to add maps:
//
//   node --import ./tests/golden/node_hooks.mjs tests/golden/maps_dump.mjs <remake src> <out dir> <objbrush.json> \
//        <data dir>=<map.ula>,<map.ula>... [<data dir>=...]
//
// <data dir> = the toolkit's built game data (gamedata.json, techtree.json, forest.json, assets/manifest.json) the
// maps are read with (base for the official maps, mirage for the MIRAGE ones). Every question source.js asks the rules
// (D.classGfx, D.script ...) and the manifest is recorded with its answer, so the C# test answers the same without the
// Rules port. Big arrays are compared by FNV-1a hashes, the rest by value. One gzipped JSON per map
// (<Data folder>__<map>.json.gz), generated.json (the random map), ground.json (ground brushes).
import fs from 'node:fs';
import zlib from 'node:zlib';
import path from 'node:path';
import { pathToFileURL } from 'node:url';

const [, , SRC, OUT, BRUSHES, ...groups] = process.argv;
const imp = (p) => import(pathToFileURL(path.join(SRC, p)).href);
const { parseUla } = await imp('game/maps/ula.js');
const { originalSource, generatedSource, slopeWalkable } = await imp('game/maps/source.js');
const { setForestData, forestItems } = await imp('game/maps/forest.js');
const { Rules } = await imp('game/rules.js');
const { HeightField } = await imp('engine/terrain.js');
const { GroundBrushes, levelGround, brushCells } = await imp('game/ground.js');
const { mapQuat } = await imp('game/maps/frame.js');

const fnv = (bytes) => { let h = 0x811c9dc5; for (let i = 0; i < bytes.length; i++) { h ^= bytes[i]; h = Math.imul(h, 0x01000193) >>> 0; } return h; };
const fnvF32 = (a) => fnv(new Uint8Array(Float32Array.from(a).buffer));
const fnvStr = (s) => { let h = 0x811c9dc5; for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; } return h; };
const attrHash = (a) => fnvStr(Object.entries(a || {}).map(([k, v]) => k + '=' + v).join('\n'));
const sum = (l, f) => l.reduce((s, e) => s + f(e), 0);
const json = (p) => JSON.parse(fs.readFileSync(p, 'utf8'));
const nul = (v) => (v === undefined || Number.isNaN(v) ? null : v);
fs.mkdirSync(OUT, { recursive: true });

// deterministic sample points over [-r, r]^2
const points = (n, r, salt) => { const out = []; for (let k = 0; k < n; k++) out.push([((k * 7919 + salt) % 1000) / 1000 * 2 * r - r, ((k * 104729 + salt * 3) % 997) / 997 * 2 * r - r]); return out; };

// a Rules / manifest that records every question and answer
function recorder(D, models) {
  const log = { classGfx: {}, script: {}, resourceValue: {}, exists: {}, statsGfx: {}, nests: {}, models: {} };
  const R = {
    classGfx: (n) => (log.classGfx[n] = D.classGfx(n)),
    script: (n) => (log.script[n] = D.script(n)),
    resourceValue: (s, n, d) => { const v = D.resourceValue(s, n, d); log.resourceValue[s + '|' + n] = v ?? null; return v; },
    exists: (n) => (log.exists[n] = D.exists(n)),
    stats: (n, l, o) => { const s = D.stats(n, l, o); log.statsGfx[n] = s && s.gfx != null ? s.gfx : null; return s; },
    extra: { nests: new Proxy(D.extra.nests || {}, { get: (t, k) => { const v = t[k]; if (typeof k === 'string') log.nests[k] = v ?? null; return v; } }) },
  };
  const M = new Proxy(models, { get: (t, k) => { const v = t[k]; if (typeof k === 'string') log.models[k] = !!v; return v; } });
  return { R, M, log };
}

for (const g of groups) {
  const [dataDir, list] = g.split('=');
  const D = new Rules(json(path.join(dataDir, 'techtree.json')), json(path.join(dataDir, 'gamedata.json')));
  const models = json(path.join(dataDir, 'assets/manifest.json')).models;
  const forestJson = path.join(dataDir, 'forest.json');
  setForestData(json(forestJson));
  for (const file of list.split(',')) {
    const buf = fs.readFileSync(file);
    const md = await parseUla(buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength), path.basename(file));
    const { R, M, log } = recorder(D, models);
    const S = originalSource(md, R, null, M);
    const skipped = originalSource(md, R, { skipObjects: new Set() }, M);
    const ox = S.origin[0], oy = S.origin[1];
    const hf = new HeightField(S.size, S.meshCell, S.height);
    const slope = slopeWalkable((x, z) => hf.at(x, z));
    const pts = points(400, S.size / 2 + 20, 17);
    const fi = forestItems(md, (x, y) => S.height(x - ox, y - oy));
    const splat = S.splat(hf);
    const out = {
      file, dataDir, forestJson,
      md: {
        name: md.name, file: md.file, w: md.w, h: md.h, hx: md.hx, hy: md.hy, mx: md.mx, my: md.my, water: md.water, setting: md.setting, settingName: md.settingName,
        defaultCamera: md.defaultCamera, startTime: md.startTime, maxPlayers: md.maxPlayers, info: md.info, description: md.description,
        preview: md.preview ? { w: md.preview.w, h: md.preview.h, hash: fnv(md.preview.rgba) } : null,
        heights: fnvF32(md.heights), mats: fnv(md.mats),
        walk: md.walk ? { w: md.walk.w, h: md.walk.h, hash: fnv(md.walk.cells), ones: sum(Array.from(md.walk.cells), (c) => c) } : null,
        objects: md.objects.map((o) => [o.type, o.name, o.cls, o.x, o.y, o.z, o.rot, o.owner, o.at, Object.keys(o.attr).length, attrHash(o.attr)]),
        plants: { n: md.plants.length, names: fnvStr(md.plants.map((p) => p.name).join('|')), x: sum(md.plants, (p) => p.x), y: sum(md.plants, (p) => p.y), z: sum(md.plants, (p) => p.z), rot: sum(md.plants, (p) => p.rot),
          first: md.plants.slice(0, 20).map((p) => [p.name, p.x, p.y, p.z, p.rot, ...p.q]) },
        regions: md.regions.map((r) => [r.name, r.shapes.map((s) => [s.oval ? 1 : 0, s.enabled ? 1 : 0, s.x, s.y, s.w, s.h])]),
        forest: { w: md.forest.w, h: md.forest.h, n: md.forest.blocks.length, hash: fnv(Uint8Array.from(md.forest.blocks.flatMap((b) => [b.x & 255, b.y & 255, ...b.b]))),
          trees: fi.trees.length, deco: fi.deco.length, tx: sum(fi.trees, (t) => t.x), ty: sum(fi.trees, (t) => t.y), trot: sum(fi.trees, (t) => t.rot), tkind: sum(fi.trees, (t) => t.kind * 31 + t.amount),
          dx: sum(fi.deco, (t) => t.x), dkind: sum(fi.deco, (t) => t.kind), first: fi.trees.slice(0, 10).map((t) => [t.x, t.y, t.rot, t.kind, t.amount]) },
        quats: md.objects.slice(0, 10).map((o) => mapQuat(o.q)),
      },
      source: {
        name: S.name, setting: S.setting, size: S.size, meshCell: S.meshCell, play: S.play, water: S.water, origin: S.origin, wallGrid: S.wallGrid,
        textures: S.textures, defaultCamera: S.defaultCamera, startTime: S.startTime, levelName: S.levelName, atmos: S.atmos, aiOptions: S.aiOptions,
        weather: S.weather, itemPool: S.itemPool, mp: S.mp, starts: S.starts, bases: S.bases, startSlots: S.startSlots.map(nul), models: [...S.models],
        trees: { n: S.trees.length, x: sum(S.trees, (t) => t.x), z: sum(S.trees, (t) => t.z), rot: sum(S.trees, (t) => t.rot), wood: sum(S.trees, (t) => t.wood),
          inside: S.trees.filter((t) => t.inside).length, big: S.trees.filter((t) => t.big).length, forest: S.trees.filter((t) => t.forest).length,
          names: fnvStr(S.trees.map((t) => t.model + '/' + t.stump + '/' + t.timber).join('|')), first: S.trees.slice(0, 20).map((t) => [t.model, t.x, t.z, t.rot, t.wood, t.inside, t.stump, t.timber, t.big]) },
        decor: { n: S.decor.length, x: sum(S.decor, (t) => t.x), z: sum(S.decor, (t) => t.z), rot: sum(S.decor, (t) => t.rot), y: sum(S.decor, (t) => t.y || 0),
          block: S.decor.filter((t) => t.block).length, forest: S.decor.filter((t) => t.forest).length, q: S.decor.filter((t) => t.q).length,
          names: fnvStr(S.decor.map((t) => t.model).join('|')), first: S.decor.slice(0, 20).map((t) => [t.model, t.x, t.z, t.rot, t.block, t.y ?? null]) },
        stones: S.stones.map((s) => [s.model, s.x, s.z, s.rot, s.amount]),
        bushes: S.bushes.map((s) => [s.model, s.x, s.z, s.rot, s.amount]),
        fish: S.fish.map((s) => [s.model, s.x, s.z, s.amount]),
        animals: S.animals.map((a) => [a.species, a.x, a.z]),
        nests: S.nests.map((n) => ({ name: n.name, species: n.species, x: n.x, z: n.z, max: nul(n.max), rate: n.rate, amount: nul(n.amount), swim: n.swim, grown: n.grown,
          action: n.areas.action, safe: n.areas.safe, hotspot: n.areas.hotspot, table: n.table, start: n.start.map((s) => s.born) })),
        wells: S.wells.map((w) => [w.model, w.cls, w.x, w.z, w.y, w.rot]),
        lakes: S.lakes, items: S.items, itemSpawns: S.itemSpawns, flags: S.flags,
        owned: S.owned.map((o) => [o.type, o.cls, o.x, o.z, o.rot, o.owner, o.level]),
        skipped: { owned: skipped.owned.length, decor: skipped.decor.length, trees: skipped.trees.length },
        hf: { n: hf.n, hash: fnvF32(hf.h) },
        samples: pts.map(([x, z]) => [x, z, S.height(x, z), hf.at(x, z), S.walkable ? (S.walkable(x, z) ? 1 : 0) : null, slope(x, z) ? 1 : 0, S.soundMat(x, z), hf.normalY(x, z)]),
        splat: pts.slice(0, 60).map(([x, z]) => splat(x, z)),
      },
      rules: log,
    };
    const id = path.basename(path.dirname(path.dirname(path.dirname(file)))) + '__' + path.basename(file, '.ula');
    fs.writeFileSync(path.join(OUT, id + '.json.gz'), zlib.gzipSync(JSON.stringify(out), { level: 9 }));
    console.log(id, md.objects.length, 'objects', S.trees.length, 'trees', fs.statSync(path.join(OUT, id + '.json.gz')).size, 'bytes');
  }
}

// ---------------------------------------------------------------- the random map
{
  const out = [];
  for (const [players, seed] of [[2, 1234], [4, 77]]) {
    const S = generatedSource({ seed, slots: Array.from({ length: players }, () => ({ type: 'human' })) });
    const hf = new HeightField(S.size, S.meshCell, S.height);
    const splat = S.splat(hf);
    const pts = points(300, S.size / 2, 5);
    out.push({
      players, seed, size: S.size, play: S.play, starts: S.starts, models: [...S.models], hf: { n: hf.n, hash: fnvF32(hf.h) },
      trees: { n: S.trees.length, x: sum(S.trees, (t) => t.x), z: sum(S.trees, (t) => t.z), scale: sum(S.trees, (t) => t.scale), names: fnvStr(S.trees.map((t) => t.model).join('|')),
        first: S.trees.slice(0, 10).map((t) => [t.model, t.x, t.z, t.rot, t.scale, t.wood, t.inside, t.big]) },
      decor: { n: S.decor.length, x: sum(S.decor, (t) => t.x), names: fnvStr(S.decor.map((t) => t.model).join('|')), block: S.decor.filter((d) => d.block).length },
      stones: S.stones.map((s) => [s.x, s.z, s.rot]), bushes: S.bushes.map((s) => [s.x, s.z, s.rot]),
      animals: S.animals.map((a) => [a.species, a.x, a.z, a.home[0], a.home[1]]),
      samples: pts.map(([x, z]) => [x, z, S.height(x, z), hf.at(x, z)]),
      splat: pts.slice(0, 60).map(([x, z]) => splat(x, z)),
    });
  }
  fs.writeFileSync(path.join(OUT, 'generated.json'), JSON.stringify(out));
  console.log('generated', fs.statSync(path.join(OUT, 'generated.json')).size, 'bytes');
}

// ---------------------------------------------------------------- ground brushes
if (BRUSHES && fs.existsSync(BRUSHES)) {
  const bj = json(BRUSHES);
  const brushes = new GroundBrushes(bj);
  const names = Object.keys(bj.brushes).sort().filter((_, i) => i % 7 === 0).slice(0, 12);
  const out = { file: BRUSHES, mpp: brushes.mpp, cases: [] };
  const fn = (x, z) => 20 + 6 * Math.sin(x * 0.05) * Math.cos(z * 0.07) + x * 0.05;
  for (const [k, name] of names.entries()) {
    const hf = new HeightField(256, 2, fn);
    const b = brushes.get(name);
    const x = -40 + k * 7.3, z = 30 - k * 5.1, heading = k * 0.7;
    const r = levelGround(hf, b, brushes.mpp, x, z, heading);
    const half = 128;
    const cellOf = (px, pz) => { const i = Math.floor((px + half) / 4), j = Math.floor((pz + half) / 4); return [i, j, i * 4 + 2 - half, j * 4 + 2 - half]; };
    out.cases.push({ name, x, z, heading, w: b.w, h: b.h, rect: r, hash: fnvF32(hf.h), at: hf.at(x, z), cells: brushCells(b, brushes.mpp, x, z, heading, cellOf) });
  }
  fs.writeFileSync(path.join(OUT, 'ground.json'), JSON.stringify(out));
  console.log('ground', out.cases.length, 'brushes');
}
