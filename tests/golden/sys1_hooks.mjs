// node --import ./sys1_hooks.mjs: the remake's world modules in node without three.js (sys1_combat_dump.mjs)
import { register } from 'node:module';
register(new URL('./sys1_resolve.mjs', import.meta.url));
