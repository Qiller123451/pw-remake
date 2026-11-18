// node --import ./tests/golden/sys2_hooks.mjs ...: like node_hooks.mjs, and the three.js addons the game modules of
// the systems import (GLTFLoader, SkeletonUtils, BufferGeometryUtils) resolve to sys2_three_addons.mjs
import { register } from 'node:module';
register(new URL('./sys2_resolve.mjs', import.meta.url));
