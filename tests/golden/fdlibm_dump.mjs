// The elementary functions of V8 (fdlibm: src/base/ieee754.cc) on a fixed set of arguments, as the bits of the
// results - the expected values of src/Sim/Core/FdLibm.cs (tests: DeterminismTests.FdLibmEqualsV8).
//   node tests/golden/fdlibm_dump.mjs > tests/golden/fdlibm.json
const buf = new DataView(new ArrayBuffer(8));
const bits = x => { buf.setFloat64(0, x); return buf.getBigUint64(0).toString(16).padStart(16, '0'); };
let s = 12345n;
const rnd = () => { s = (s * 6364136223846793005n + 1442695040888963407n) & 0xffffffffffffffffn; return Number(s >> 11n) / 9007199254740992; };
const out = { sin: [], cos: [], tan: [], atan: [], atan2: [], asin: [], acos: [], exp: [], log: [], pow: [] };
for (let i = 0; i < 400; i++) {
  const k = i % 4;
  const x = k === 0 ? (rnd() - 0.5) * 8 : k === 1 ? (rnd() - 0.5) * 2000 : k === 2 ? (rnd() - 0.5) * 1.6e6 : (rnd() - 0.5) * 1e-3;
  out.sin.push([bits(x), bits(Math.sin(x))]); out.cos.push([bits(x), bits(Math.cos(x))]); out.tan.push([bits(x), bits(Math.tan(x))]);
  const a = (rnd() - 0.5) * (k === 0 ? 4 : k === 1 ? 200 : k === 2 ? 1e8 : 1e-6);
  out.atan.push([bits(a), bits(Math.atan(a))]);
  const y = (rnd() - 0.5) * 300, z = (rnd() - 0.5) * 300;
  out.atan2.push([bits(y), bits(z), bits(Math.atan2(y, z))]);
  const u = rnd() * 2 - 1;
  out.asin.push([bits(u), bits(Math.asin(u))]); out.acos.push([bits(u), bits(Math.acos(u))]);
  const e = (rnd() - 0.5) * (k === 3 ? 1e-6 : 100);
  out.exp.push([bits(e), bits(Math.exp(e))]);
  const l = k === 0 ? rnd() * 2 : k === 1 ? rnd() * 1e6 : k === 2 ? 1 + (rnd() - 0.5) * 1e-5 : rnd() * 1e-300;
  out.log.push([bits(l), bits(Math.log(l))]);
  const b = rnd() * (k === 0 ? 2 : 100), p = (rnd() - 0.5) * (k === 1 ? 2 : 10);
  out.pow.push([bits(b), bits(p), bits(Math.pow(b, p))]);
}
// edge cases
for (const x of [0, -0, 1, -1, Math.PI, Math.PI / 2, Math.PI / 4, 3 * Math.PI / 4, 1e-300, 709, -745, Infinity, -Infinity])
  out.sin.push([bits(x), bits(Math.sin(x))]), out.cos.push([bits(x), bits(Math.cos(x))]), out.atan.push([bits(x), bits(Math.atan(x))]), out.exp.push([bits(x), bits(Math.exp(x))]);
for (const [y, x] of [[0, 0], [0, -0], [-0, -1], [1, 0], [-1, 0], [Infinity, Infinity], [3, -Infinity], [1, 1], [-2, 1]]) out.atan2.push([bits(y), bits(x), bits(Math.atan2(y, x))]);
for (const [b, p] of [[0.8, 4], [0.8, 0], [2, 10], [2, -3], [-2, 3], [-2, 2], [10, 0.5], [0, 2], [0, -1], [1, 1e10], [1.0000001, 1e9], [0.5, 1080]]) out.pow.push([bits(b), bits(p), bits(Math.pow(b, p))]);
console.log(JSON.stringify(out));
