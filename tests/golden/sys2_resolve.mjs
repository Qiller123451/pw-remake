// resolve hook: 'three' -> three_stub.mjs, 'three/addons/...' -> sys2_three_addons.mjs (nothing is ever drawn)
export async function resolve(spec, ctx, next) {
  if (spec === 'three') return { url: new URL('./sys2_three.mjs', import.meta.url).href, shortCircuit: true };
  if (spec.startsWith('three/')) return { url: new URL('./sys2_three_addons.mjs', import.meta.url).href, shortCircuit: true };
  return next(spec, ctx);
}
