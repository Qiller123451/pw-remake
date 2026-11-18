// three_stub.mjs and the few more names the systems' imports touch at load time
export * from './three_stub.mjs';
class Any { constructor() { return new Proxy(this, { get: (t, k) => (k in t ? t[k] : () => new Any()) }); } }
const C = () => class extends Any {};
export const ShaderChunk = {}, UniformsLib = {}, ShaderLib = {}, UniformsUtils = { merge: (a) => Object.assign({}, ...a), clone: (u) => u };
export const Material = C(), SkinnedMesh = C(), Bone = C(), Skeleton = C(), AnimationUtils = { subclip: () => null }, PropertyBinding = C(), KeyframeTrack = C(),
  Scene = C(), PerspectiveCamera = C(), OrthographicCamera = C(), WebGLRenderer = C(), WebGLRenderTarget = C(), Light = C(), DirectionalLight = C(),
  AmbientLight = C(), HemisphereLight = C(), PointLight = C(), Fog = C(), FogExp2 = C(), RingGeometry = C(), CircleGeometry = C(), ConeGeometry = C(),
  TorusGeometry = C(), EdgesGeometry = C(), WireframeGeometry = C(), Line = C(), LineLoop = C(), LineDashedMaterial = C(), MeshPhongMaterial = C(),
  RawShaderMaterial = C(), DepthTexture = C(), CubeTexture = C(), InstancedBufferGeometry = C(), InterleavedBuffer = C(), InterleavedBufferAttribute = C(),
  Uint16BufferAttribute = C(), Uint32BufferAttribute = C(), Int32BufferAttribute = C(), Spherical = C(), Cylindrical = C(), Box2 = C(), Curve = C(),
  CatmullRomCurve3 = C(), LoadingManager = C(), FileLoader = C(), ImageLoader = C(), CanvasRenderingContext2D = C();
export const ClampToEdgeWrapping = 1001, MirroredRepeatWrapping = 1002, NearestMipmapNearestFilter = 1004, LinearMipmapNearestFilter = 1007,
  UnsignedShortType = 1012, HalfFloatType = 1016, RGFormat = 1030, AlphaFormat = 1021, LuminanceFormat = 1022, DepthFormat = 1026,
  NoBlending = 0, SubtractiveBlending = 3, MultiplyBlending = 4, CustomBlending = 5, OneFactor = 201, ZeroFactor = 200, SrcAlphaFactor = 204,
  OneMinusSrcAlphaFactor = 205, LoopPingPong = 2202, NormalAnimationBlendMode = 2500, AdditiveAnimationBlendMode = 2501, StaticDrawUsage = 35044,
  LinearSRGBColorSpace = 'srgb-linear', NoColorSpace = '', ACESFilmicToneMapping = 4, NoToneMapping = 0, PCFSoftShadowMap = 2, BasicShadowMap = 0,
  LessEqualDepth = 3, AlwaysDepth = 1, GreaterEqualDepth = 5, EqualDepth = 4, NeverDepth = 0, CullFaceNone = 0, TrianglesDrawMode = 0;
