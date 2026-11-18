// a do-nothing three.js for loading the remake's modules in node (nothing here is ever drawn)
class Any { constructor() { return new Proxy(this, { get: (t, k) => (k in t ? t[k] : () => new Any()) }); } }
const C = () => class extends Any {};
export const Vector2 = C(), Vector3 = C(), Vector4 = C(), Quaternion = C(), Matrix3 = C(), Matrix4 = C(), Euler = C(), Color = C(),
  Object3D = C(), Group = C(), Mesh = C(), InstancedMesh = C(), Points = C(), LineSegments = C(), Sprite = C(), Box3 = C(), Sphere = C(),
  BufferGeometry = C(), BufferAttribute = C(), Float32BufferAttribute = C(), InstancedBufferAttribute = C(), PlaneGeometry = C(),
  BoxGeometry = C(), SphereGeometry = C(), CylinderGeometry = C(), ShapeGeometry = C(), Shape = C(), Path = C(), Raycaster = C(),
  MeshBasicMaterial = C(), MeshLambertMaterial = C(), MeshStandardMaterial = C(), ShaderMaterial = C(), SpriteMaterial = C(),
  PointsMaterial = C(), LineBasicMaterial = C(), Texture = C(), DataTexture = C(), CanvasTexture = C(), TextureLoader = C(),
  AnimationMixer = C(), AnimationClip = C(), Clock = C(), Plane = C(), Ray = C(), Frustum = C(), Triangle = C(), Line3 = C();
export const MathUtils = { clamp: (v, a, b) => Math.max(a, Math.min(b, v)), lerp: (a, b, t) => a + (b - a) * t, degToRad: (d) => d * Math.PI / 180 };
export const DoubleSide = 2, FrontSide = 0, BackSide = 1, SRGBColorSpace = 'srgb', RepeatWrapping = 1000, LinearFilter = 1006, NearestFilter = 1003,
  AdditiveBlending = 2, NormalBlending = 1, LoopOnce = 2200, LoopRepeat = 2201, DynamicDrawUsage = 35048, RGBAFormat = 1023, UnsignedByteType = 1009,
  FloatType = 1015, RedFormat = 1028, LinearMipmapLinearFilter = 1008;
export default {};
