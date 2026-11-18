// resolve hook: 'three' -> the stub next to this file
export async function resolve(spec, ctx, next) {
  if (spec === 'three' || spec.startsWith('three/')) return { url: new URL('./three_stub.mjs', import.meta.url).href, shortCircuit: true };
  return next(spec, ctx);
}
