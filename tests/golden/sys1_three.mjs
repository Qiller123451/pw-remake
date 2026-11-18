// A do-nothing three.js for running the remake's world in node (sys1_combat_dump.mjs) - like three_stub.mjs, but with
// working Vector2 / Vector3 (the entities keep their positions in them) and the names the engine modules touch when
// they load. Unknown methods of the vectors return the vector itself.
// anything: every property is another such thing (kept once set), calls and `new` give one too
export const deep = () => {
  const f = function () {};
  return new Proxy(f, {
    get: (t, k) => (k === Symbol.toPrimitive ? () => 0 : typeof k === 'symbol' || k === 'then' ? undefined : Object.prototype.hasOwnProperty.call(t, k) && k !== 'name' && k !== 'length' ? t[k] : (t[k] = deep())),
    set: (t, k, v) => { t[k] = v; return true; },
    apply: () => deep(), construct: () => deep(),
  });
};
const C = () => class { constructor() { return deep(); } };
const chain = (o) => new Proxy(o, { get: (t, k) => (k in t ? t[k] : typeof k === 'string' ? () => t : undefined) });
export class Vector2 {
  constructor(x = 0, y = 0) { this.x = x; this.y = y; return chain(this); }
  set(x, y) { this.x = x; this.y = y; return this; }
  copy(v) { this.x = v.x; this.y = v.y; return this; }
  clone() { return new Vector2(this.x, this.y); }
  length() { return Math.hypot(this.x, this.y); }
}
export class Vector3 {
  constructor(x = 0, y = 0, z = 0) { this.x = x; this.y = y; this.z = z; return chain(this); }
  set(x, y, z) { this.x = x; this.y = y; this.z = z; return this; }
  copy(v) { this.x = v.x; this.y = v.y; this.z = v.z; return this; }
  clone() { return new Vector3(this.x, this.y, this.z); }
  add(v) { this.x += v.x; this.y += v.y; this.z += v.z; return this; }
  sub(v) { this.x -= v.x; this.y -= v.y; this.z -= v.z; return this; }
  addScaledVector(v, s) { this.x += v.x * s; this.y += v.y * s; this.z += v.z * s; return this; }
  multiplyScalar(s) { this.x *= s; this.y *= s; this.z *= s; return this; }
  lengthSq() { return this.x * this.x + this.y * this.y + this.z * this.z; }
  length() { return Math.sqrt(this.lengthSq()); }
  normalize() { const l = this.length() || 1; return this.multiplyScalar(1 / l); }
  distanceTo(v) { return Math.hypot(this.x - v.x, this.y - v.y, this.z - v.z); }
  lerpVectors(a, b, k) { this.x = a.x + (b.x - a.x) * k; this.y = a.y + (b.y - a.y) * k; this.z = a.z + (b.z - a.z) * k; return this; }
}
// a model's box: 8 x 6 x 8 m (the models are not loaded)
export class Box3 {
  constructor() { this.min = new Vector3(-4, 0, -4); this.max = new Vector3(4, 6, 4); }
  setFromObject() { return this; }
  getSize(v) { return v.set(8, 6, 8); }
  getCenter(v) { return v.set(0, 3, 0); }
}
export const Vector4 = C(), Quaternion = C(), Matrix3 = C(), Matrix4 = C(), Euler = C(), Color = C(),
  Object3D = C(), Group = C(), Mesh = C(), InstancedMesh = C(), Points = C(), LineSegments = C(), Sprite = C(), Sphere = C(),
  BufferGeometry = C(), BufferAttribute = C(), Float32BufferAttribute = C(), InstancedBufferAttribute = C(), InstancedBufferGeometry = C(), PlaneGeometry = C(),
  BoxGeometry = C(), SphereGeometry = C(), CylinderGeometry = C(), ShapeGeometry = C(), Shape = C(), Path = C(), Raycaster = C(),
  MeshBasicMaterial = C(), MeshLambertMaterial = C(), MeshStandardMaterial = C(), MeshDepthMaterial = C(), ShaderMaterial = C(), SpriteMaterial = C(), Material = C(),
  PointsMaterial = C(), LineBasicMaterial = C(), Texture = C(), DataTexture = C(), CanvasTexture = C(), TextureLoader = C(),
  AnimationMixer = C(), AnimationClip = C(), Clock = C(), Plane = C(), Ray = C(), Frustum = C(), Triangle = C(), Line3 = C(),
  Fog = C(), Scene = C(), PerspectiveCamera = C(), WebGLRenderer = C(), DirectionalLight = C(), HemisphereLight = C(), PointLight = C(), PropertyBinding = C();
export const ShaderChunk = {}, ShaderLib = {}, UniformsLib = { fog: {}, lights: {} }, UniformsUtils = { merge: (a) => Object.assign({}, ...a), clone: (u) => ({ ...u }) };
export const MathUtils = { clamp: (v, a, b) => Math.max(a, Math.min(b, v)), lerp: (a, b, t) => a + (b - a) * t, degToRad: (d) => d * Math.PI / 180 };
export const DoubleSide = 2, FrontSide = 0, BackSide = 1, SRGBColorSpace = 'srgb', NoColorSpace = '', RepeatWrapping = 1000, LinearFilter = 1006, NearestFilter = 1003,
  AdditiveBlending = 2, NormalBlending = 1, CustomBlending = 5, AddEquation = 100, OneFactor = 201, SrcAlphaFactor = 204, LoopOnce = 2200, LoopRepeat = 2201,
  DynamicDrawUsage = 35048, RGBAFormat = 1023, UnsignedByteType = 1009, FloatType = 1015, RedFormat = 1028, LinearMipmapLinearFilter = 1008, PCFShadowMap = 1;
export default {};
