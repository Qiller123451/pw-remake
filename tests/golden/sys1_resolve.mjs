// resolve hook for sys1_combat_dump.mjs: 'three' -> the do-nothing stub, 'three/addons/...' -> sys1_three_addons.mjs
export async function resolve(spec, ctx, next) {
  if (spec === 'three') return { url: new URL('./sys1_three.mjs', import.meta.url).href, shortCircuit: true };
  if (spec.startsWith('three/')) return { url: new URL('./sys1_three_addons.mjs', import.meta.url).href, shortCircuit: true };
  return next(spec, ctx);
}
