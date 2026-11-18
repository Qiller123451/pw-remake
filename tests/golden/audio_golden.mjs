// Golden values of the IMA ADPCM decoder: the browser remake's decodeWav (remake/src/engine/audio.js) run in node on
// wav files, written as tests/golden/audio_adpcm.json; the C# port (game/src/View/Audio/WavDecoder.cs) must give the
// same samples (tests/PwRemake.Tests/AudioTests.cs AdpcmMatchesJs).
//
//   node tests/golden/audio_golden.mjs <pw-exporter>/remake/src/engine/audio.js <Audio/Sound folder> [more folders]
//
// It also writes two synthetic IMA ADPCM files (random nibbles: every byte is valid ADPCM) - a stereo one and a mono
// one with an odd data size - so the stereo layout is covered although the game's own wavs are mono.
import fs from 'fs';
import path from 'path';
import crypto from 'crypto';

const [audioJs, ...roots] = process.argv.slice(2);
const here = path.dirname(new URL(import.meta.url).pathname);
const src = fs.readFileSync(audioJs, 'utf8');
// decodeWav and its tables, without the module's imports (three.js)
const body = src.slice(src.indexOf('const IMA_STEP'), src.indexOf('export class Audio')).replace('export function', 'function');
const decodeWav = new Function(body + '\nreturn decodeWav;')();
const ctx = { createBuffer: (ch, n, rate) => { const d = [...Array(ch)].map(() => new Float32Array(n)); return { numberOfChannels: ch, length: n, sampleRate: rate, getChannelData: (c) => d[c] }; } };

// a small deterministic generator for the synthetic files
let seed = 12345;
const rnd = () => { seed = (seed * 1103515245 + 12345) & 0x7fffffff; return seed / 0x80000000; };
function synth(file, ch, align, blocks, extra) {
  const data = Buffer.alloc(blocks * align + extra);
  for (let i = 0; i < data.length; i++) data[i] = Math.floor(rnd() * 256);
  for (let k = 0; k < blocks; k++) for (let c = 0; c < ch; c++) {
    const o = k * align + 4 * c;
    data.writeInt16LE(Math.floor(rnd() * 65536) - 32768, o);
    data[o + 2] = Math.floor(rnd() * 100);       // (some above 88: the decoder clamps)
    data[o + 3] = 0;
  }
  const fmt = Buffer.alloc(20);
  fmt.writeUInt16LE(0x11, 0); fmt.writeUInt16LE(ch, 2); fmt.writeUInt32LE(22050, 4); fmt.writeUInt32LE(22050 * align / 1017, 8);
  fmt.writeUInt16LE(align, 12); fmt.writeUInt16LE(4, 14); fmt.writeUInt16LE(2, 16); fmt.writeUInt16LE((align - 4 * ch) * 2 / ch + 1, 18);
  const chunk = (id, b) => { const h = Buffer.alloc(8); h.write(id, 0, 'ascii'); h.writeUInt32LE(b.length, 4); return Buffer.concat([h, b, b.length & 1 ? Buffer.alloc(1) : Buffer.alloc(0)]); };
  const body = Buffer.concat([Buffer.from('WAVE'), chunk('fmt ', fmt), chunk('data', data)]);
  const riff = Buffer.alloc(8); riff.write('RIFF', 0, 'ascii'); riff.writeUInt32LE(body.length, 4);
  fs.writeFileSync(file, Buffer.concat([riff, body]));
}
synth(path.join(here, 'audio_stereo_ima.wav'), 2, 512, 5, 100);
synth(path.join(here, 'audio_mono_ima.wav'), 1, 256, 7, 33);

function golden(file) {
  const b = fs.readFileSync(file);
  const ab = b.buffer.slice(b.byteOffset, b.byteOffset + b.length);
  const out = decodeWav(ctx, ab);
  if (!out) return null;
  const ch = out.numberOfChannels, n = out.length, pcm = Buffer.alloc(n * ch * 2);
  const chans = [...Array(ch).keys()].map((c) => out.getChannelData(c));
  for (let i = 0; i < n; i++) for (let c = 0; c < ch; c++) pcm.writeInt16LE(Math.round(chans[c][i] * 32768), (i * ch + c) * 2);
  return { channels: ch, rate: out.sampleRate, frames: n, sha256: crypto.createHash('sha256').update(pcm).digest('hex'),
    head: [...Array(Math.min(12, n * ch)).keys()].map((i) => pcm.readInt16LE(i * 2)) };
}
const res = { source: 'remake/src/engine/audio.js decodeWav (pw-exporter 98c7703)', synthetic: {}, files: {} };
for (const f of ['audio_stereo_ima.wav', 'audio_mono_ima.wav']) res.synthetic[f] = golden(path.join(here, f));
const walk = (d) => fs.readdirSync(d, { withFileTypes: true }).flatMap((e) => (e.isDirectory() ? walk(path.join(d, e.name)) : [path.join(d, e.name)]));
for (const root of roots) for (const f of walk(root).filter((x) => /\.wav$/i.test(x)).sort()) {
  const g = golden(f);
  // (null: not IMA ADPCM - the browser decodes it; the C# decoder must say null too)
  res.files[path.relative(root, f).split(path.sep).join('/')] = g;
}
fs.writeFileSync(path.join(here, 'audio_adpcm.json'), JSON.stringify(res, null, 1) + '\n');
console.log(`synthetic 2, files ${Object.keys(res.files).length} (${Object.values(res.files).filter(Boolean).length} IMA ADPCM)`);
