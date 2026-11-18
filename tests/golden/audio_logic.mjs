// Golden traces of the sound logic: the browser remake's engine/audio.js, music.js and atmo.js run in node on the real
// sounds.json with a fake AudioContext / <audio> that record what would play. The operations and their results go to
// tests/golden/audio_logic.json; the C# port (game/src/View/Audio) replays the same operations with the same random
// numbers (mulberry32, seed 1) and must give the same results (tests/PwRemake.Tests/AudioTests.cs LogicMatchesJs).
//
//   node tests/golden/audio_logic.mjs <pw-exporter>/remake/src/engine <data>/assets/sounds.json
import fs from 'fs';
import path from 'path';

const [engineDir, soundsJson] = process.argv.slice(2);
const here = path.dirname(new URL(import.meta.url).pathname);
const strip = (f) => fs.readFileSync(path.join(engineDir, f), 'utf8').replace(/^import .*$/gm, '').replace(/^export /gm, '');
const THREE = { Vector3: class { constructor(x = 0, y = 0, z = 0) { this.x = x; this.y = y; this.z = z; } set(x, y, z) { this.x = x; this.y = y; this.z = z; return this; } } };
const { Audio, Music, Atmo, playlistName } = new Function('THREE', strip('music.js') + strip('atmo.js') + strip('audio.js') + '\nreturn { Audio, Music, Atmo, playlistName };')(THREE);

// the random numbers: mulberry32 (the C# test has the same)
let s32 = 1;
Math.random = () => { s32 = (s32 + 0x6D2B79F5) | 0; let t = Math.imul(s32 ^ (s32 >>> 15), 1 | s32); t = (t + Math.imul(t ^ (t >>> 7), 61 | t)) ^ t; return ((t ^ (t >>> 14)) >>> 0) / 4294967296; };

// ---------------------------------------------------------------- a fake WebAudio that records the started sounds
let starts = [];
const voiceEnded = [];
class FNode {
  constructor() { this.gain = { value: 1, setValueAtTime(v) { this.value = v; }, linearRampToValueAtTime(v) { this.target = v; }, cancelScheduledValues() {} }; this.pan = { value: 0 }; this.playbackRate = { value: 1 }; this.kind = ''; }
  connect(n) { this.to = n; return n; }
}
let A;
const ctx = {
  state: 'running', currentTime: 0, destination: new FNode(),
  createGain: () => { const n = new FNode(); n.kind = 'gain'; return n; },
  createStereoPanner: () => { const n = new FNode(); n.kind = 'pan'; return n; },
  createBufferSource: () => {
    const s = new FNode(); s.kind = 'src'; s.loop = false;
    s.listeners = [];
    s.addEventListener = (t, f) => s.listeners.push(f);
    s.stop = () => {};
    s.start = (t, off) => {
      let gain = 1, pan = 0, panner = false, n = s.to;
      while (n) { if (n.kind === 'gain' && !Object.values(A.bus).includes(n)) gain = n.gain.value; if (n.kind === 'pan') { pan = n.pan.value; panner = true; } if (Object.values(A.bus).includes(n)) break; n = n.to; }
      const ch = Object.keys(A.bus).find((k) => A.bus[k] === n);
      starts.push({ file: s.buffer.file, ch, gain, pan, panner, rate: s.playbackRate.value, loop: !!s.loop, off: off || 0 });
      if (ch === 'voice') voiceEnded.push(s);
    };
    return s;
  },
  resume: () => Promise.resolve(),
};
// the <audio> element of the music
class FEl {
  constructor(src) { this.src = src; this.dataset = {}; this.loop = false; this.volume = 1; this.currentTime = 0; this.ended = false; this.failed = false; this.paused = true; this.duration = 1000; }
  addEventListener(t, f) { if (t === 'loadedmetadata') f(); }
  play() { this.paused = false; return Promise.resolve(); }
  pause() { this.paused = true; }
}
globalThis.window = { AudioContext: function () { return ctx; }, Audio: FEl };
globalThis.requestAnimationFrame = () => {};

A = new Audio('assets/');
A.db = JSON.parse(fs.readFileSync(soundsJson, 'utf8'));
A.mus.setData(A.db.music);
A.init();
A.buffer = (file) => Promise.resolve(/missing/.test(file) ? null : { file, duration: 2 });
const M = A.mus, T = A.atmo;
const tick = () => new Promise((r) => setImmediate(r));
const r6 = (x) => Math.round(x * 1e9) / 1e9;
const res = (st) => st.map((x) => [x.file, x.ch, r6(x.gain), r6(x.pan), r6(x.rate), x.loop, r6(x.off), x.panner]);

// ---------------------------------------------------------------- the operations
const P = (x, z) => [x, 0, z];
const ops = [
  { op: 'listener', x: 100, z: 100, yaw: 0.3 },
  { op: 'play', name: 'ui_click' },
  { op: 'play', name: 'UI_Click' },
  { op: 'play', name: 'fx_cbigbuildingexplosion', pos: P(110, 95) },
  { op: 'play', name: 'fx_cbigbuildingexplosion', pos: P(160, 100) },
  { op: 'play', name: 'fx_cbigbuildingexplosion', pos: P(300, 300) },        // beyond 81 x 1.6
  { op: 'play', name: 'sfx_proj_metal_small_hit_unit', pos: P(90, 120) },
  { op: 'play', name: 'sfx_proj_metal_small_hit_unit', pos: P(100, 100) },
  { op: 'play', name: 'voice_aje_allosaurus_selected', pos: P(100, 100) },
  { op: 'play', name: 'voice_aje_allosaurus_gag' }, { op: 'play', name: 'voice_aje_allosaurus_gag' },
  { op: 'play', name: 'voice_aje_allosaurus_gag' }, { op: 'play', name: 'voice_aje_allosaurus_gag' }, { op: 'play', name: 'voice_aje_allosaurus_gag' },
  { op: 'play', name: 'no_such_event' },
  { op: 'playFirst', names: ['ui_no_gold', 'ui_no_storage'] },
  { op: 'hit', w: 'Proj_Metal_Small', t: 'Wood', pos: P(105, 104) },
  { op: 'hit', w: 'Proj_Metal_Small', t: 'Glass', pos: P(105, 104) },
  { op: 'hit', w: 'Proj_Wood_Small', t: '', pos: P(120, 80) },
  { op: 'hit', w: 'NoWeapon', t: 'Unit', pos: P(105, 104) },
  { op: 'anim', ev: [0.96, '04_step/step_huge_dino_carn1.wav', 48, 66.0, 'x'], pos: P(101, 99) },
  { op: 'anim', ev: [0.5, '02_battle/weapon_small_att3.wav', 55, 0, ''], pos: P(130, 99) },
  { op: 'ack', names: ['voice_aje_allosaurus_selected', 'voice_selected'] },
  { op: 'ack', names: ['voice_aje_allosaurus_task_goto'] },          // still speaking: nothing
  { op: 'voiceEnd' },
  { op: 'ack', names: ['voice_x_task_goto', 'voice_aje_allosaurus_task_goto'] },
  { op: 'playFileOffset', file: '05_ui/ui_click_success.wav', off: 1.5 },
  { op: 'playFileOffset', file: '05_ui/ui_click_success.wav', off: 2.5 },
  { op: 'listener', x: 0, z: 0, yaw: -1.2 },
  ...[...Array(30).keys()].map((i) => ({ op: 'play', name: 'sfx_proj_metal_small_hit_soil', pos: P(i - 15, 10) })),   // the cap: 25
  // ---- music
  { op: 'update', t: 0 },
  { op: 'start', level: 'Single 05', atmos: 'savanna.txt', tribe: 'Aje' },
  { op: 'update', t: 1 },
  { op: 'end' }, { op: 'update', t: 2 },
  { op: 'attack', x: 1, z: 1, t: 3 }, { op: 'attack', x: 1, z: 1, t: 3.2 }, { op: 'attack', x: 5, z: 5, t: 3.5 }, { op: 'update', t: 4 },
  { op: 'attack', x: 9, z: 9, t: 4 }, { op: 'update', t: 4.1 },
  { op: 'update', t: 9 }, { op: 'attack', x: 20, z: 20, t: 9.5 }, { op: 'update', t: 12 }, { op: 'update', t: 19 }, { op: 'update', t: 20 },
  { op: 'trigger', track: '24_location_temple', loop: false }, { op: 'update', t: 21 }, { op: 'end' }, { op: 'update', t: 22 },
  { op: 'trigger', track: 'music/31_location_seas.mp3', loop: true }, { op: 'update', t: 23 },
  ...[...Array(30).keys()].map((i) => ({ op: 'update', t: 24 + i * 0.25, l: [0, 0], homes: [[10, 10]] })),
  ...[...Array(30).keys()].map((i) => ({ op: 'update', t: 32 + i * 0.25, l: [0, 0], homes: [] })),
  { op: 'seq', on: true }, { op: 'update', t: 40 }, { op: 'seq', on: false }, { op: 'update', t: 41 },
  { op: 'attack', x: 1, z: 1, t: 42 }, { op: 'attack', x: 2, z: 1, t: 42 }, { op: 'attack', x: 3, z: 1, t: 42 }, { op: 'attack', x: 4, z: 1, t: 42 }, { op: 'update', t: 42 },
  { op: 'stopCombat' },
  { op: 'stop' },
  { op: 'start', level: 'Tutorial', atmos: 'jungle.txt', tribe: 'Hu' }, { op: 'update', t: 0 },
  { op: 'start', level: 'my map', atmos: 'northland.txt', tribe: 'Ninigi' }, { op: 'update', t: 0 },
  { op: 'start', level: '', atmos: 'unknown.txt', tribe: 'SEAS' }, { op: 'update', t: 0 },
  // ---- ambience
  { op: 'atmoStart', name: 'jungle.txt' },
  { op: 'tick', state: Atmo.state(2, true, true), dt: 0.1, n: 10 },
  { op: 'tick', state: Atmo.state(2, true, false), dt: 0.1, n: 1 },
  { op: 'tick', state: Atmo.state(8, true, true), dt: 0.1, n: 2 },
  { op: 'tick', state: Atmo.state(8, true, true), dt: 0.1, n: 5 },
  { op: 'tick', state: Atmo.state(8, true, true), dt: 0.1, n: 3000 },
  { op: 'tick', state: Atmo.state(0, false, true), dt: 0.1, n: 1200 },
  { op: 'tick', state: Atmo.state(1, false, true), dt: 0.1, n: 20 },
  { op: 'atmoStart', name: 'arena_txt' },
  { op: 'tick', state: Atmo.state(3, false, false), dt: 0.25, n: 800 },
  { op: 'atmoStart', name: 'No Atmos' },
  { op: 'tick', state: 0, dt: 0.1, n: 5 },
];

const out = [];
let atmoT = 0;
for (const o of ops) {
  starts = [];
  let r = null;
  switch (o.op) {
    case 'listener': A.listener.set(o.x, 0, o.z); A.yaw = o.yaw; break;
    case 'play': await A.play(o.name, o.pos ? new THREE.Vector3(...o.pos) : null); break;
    case 'playFirst': await A.playFirst(o.names, o.pos ? new THREE.Vector3(...o.pos) : null); break;
    case 'hit': A.hit(o.w, o.t, new THREE.Vector3(...o.pos)); await tick(); break;
    case 'anim': A.animEvent(o.ev, new THREE.Vector3(...o.pos)); await tick(); break;
    case 'ack': await A.ack(o.names); await tick(); break;
    case 'voiceEnd': for (const s of voiceEnded.splice(0)) for (const f of s.listeners) f(); break;
    case 'playFileOffset': await A.playFile(o.file, 100, null, 0, 'voice', 1, 0, o.off); break;
    case 'update': M.update(o.t, o.l ? { x: o.l[0], z: o.l[1] } : undefined, o.homes ? () => o.homes.map(([x, z]) => ({ x, z })) : undefined); break;
    case 'start': M.startGame({ levelName: o.level, atmos: o.atmos, tribe: o.tribe }); break;
    case 'stop': M.stopGame(); break;
    case 'end': if (M.el) M.el.ended = true; break;
    case 'attack': M.attack(o.x, o.z, o.t); break;
    case 'trigger': M.trigger(o.track, o.loop); break;
    case 'seq': M.sequence(o.on); break;
    case 'stopCombat': M.attacks = []; M.S('Combat').on = false; M.refresh(); break;
    case 'atmoStart': T.start(o.name); await tick(); break;
    case 'tick': for (let k = 0; k < o.n; k++) { atmoT += o.dt; T.update(o.dt, o.state, atmoT); await tick(); } break;
  }
  if (['update', 'start', 'stop', 'end', 'attack', 'trigger', 'seq', 'stopCombat'].includes(o.op)) {
    const st = M.state(), cur = M.current >= 0 ? M.sw[M.current] : null;
    r = { playing: st.playing, track: st.track, on: st.on, attacks: st.attacks, file: cur ? cur.file : '', el: M.el ? [M.el.src.replace(/^assets\//, ''), M.el.loop, r6(M.el.currentTime)] : null };
  } else if (o.op === 'tick' || o.op === 'atmoStart') {
    r = { cur: T.cur, starts: res(starts) };
  } else r = { starts: res(starts), active: A.active, busy: !!A.voiceBusy };
  out.push(r);
}
fs.writeFileSync(path.join(here, 'audio_logic.json'), JSON.stringify({ source: 'remake/src/engine/audio.js, music.js, atmo.js (pw-exporter 98c7703)', seed: 1, ops, results: out }) + '\n');
console.log(`${ops.length} operations; ${out.reduce((n, r) => n + (r.starts ? r.starts.length : 0), 0)} sounds started`);
