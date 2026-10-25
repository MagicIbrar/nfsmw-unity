using NfsMw.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace NfsMw.CarRender
{
    /// <summary>
    /// The shared lighting inputs of the car effect, set as shader globals once per frame:
    /// • the three shaper lights of the current rig (elSetupLights 0x502130; ShaperRig in Core/CarRenderData.cs) as
    ///   _MW_CarLightDir0..2 / _MW_CarLightCol0..2 (world space, toward the light). In game (game flow 6) the rig is
    ///   ShaperLightsCarsInGame (sun-relative); in the front end CarRenderInfo::Render (0x74E3E5) picks it by
    ///   FEManager::GetGarageType (Safehouse / CShop / BackRoom / CarLot / QRace, all WORLD_SPACE) — the showroom
    ///   sets <see cref="SetGarage"/>;
    /// • the NIS characters' rig ShaperLightsCharacters (0x8F2F10, CAMERA_SPACE; WorldModel::RenderModel 0x73B9C0) as
    ///   _MW_CharLightDir0..1 / _MW_CharLightCol0..1, rebuilt from the main camera's axes every frame;
    /// • the environment cube: speed.exe renders one eEnvMap around the player car that every car samples
    ///   (UpdateEnvironmentMapCameras 0x756060: view 1's camera anchor + EnvMapEyeOffset (0, 0, 0.95) @0x9053B0,
    ///   EnvMapCamOffset 0 @0x9B0AA0). eEnvMap::UpdateCameras 0x6DA350: six 256² faces ([0x8F8FF4]) at the car, near
    ///   0.5 / far 100 ([0x8F9010] / [0x8F9014]); the face schedule (table 0x8F9028 by eFrameCounter % period, period
    ///   {6, 4, 1} for g_CarEnvironmentMapEnable [0x9017B8] = 1 / 2 / 3): option 1 one face per frame (F, R, B, L, U, D in
    ///   turn), 2 three per frame (F and U every frame + one of R, B, L, D), 3 all six. Each face (eDisplayFrame, game
    ///   flow 6 only): clear black, the SKYDOME_XENON sky layer 3 at scale 0.0065 (DrawSkyEnvMap [0x8F9364] = 1), the
    ///   scenery (StuffScenery(view, 0) with g_CarEnvironmentMapUpdateData [0x9017BC] != 0, 2 at the top preset), then
    ///   the world light flares and every car's flares (RenderVehicleFlares(view, 0, 1)) when the light-glow option is on.
    ///   No cars, fragments, smackables (WorldModels), rain or particles are drawn into the faces, and no shadow
    ///   technique (sub_6E0AB0 picks them for view 1 only). Cars under 18 px (DisableEnvMapSize [0x8F9334] == 0 or
    ///   pixel &lt; 18) draw with render flag 1 = no env map (CarRenderInfo publishes _MW_CarEnvOff per renderer).
    ///   [ours] Unity's RenderToCubemap with a culling mask: car renderers live on <see cref="CarLayer"/> (29) so the
    ///   cube excludes them; the scenery keeps its normal LOD models (the exe's env cull flags 0x1000 pick a lower one [?]).
    /// </summary>
    public sealed class CarEnvironment : MonoBehaviour
    {
        static CarEnvironment _instance;
        Camera _cam;
        RenderTexture _cube;
        int _frame;

        public static Transform Focus;
        /// <summary>[0x8F8FF4]: the env-map face size.</summary>
        public static int Resolution = 256;
        /// <summary>g_CarEnvironmentMapEnable [0x9017B8]: 1 at performance level 3+, (preset 0x6C1990); 0 = no env map
        /// (a 1x1 target), 1 / 2 / 3 = 1 / 3 / 6 faces per frame.</summary>
        public static int Option = 1;
        /// <summary>g_CarEnvironmentMapUpdateData [0x9017BC] != 0: the scenery is drawn into the faces (1 at level 4,
        /// 2 at level 5; 0 below: scenery only while g_ShadowDetail &lt; 2, with cull flag 0x1000).</summary>
        public static bool UpdateData = true;
        /// <summary>EnvMapEyeOffset (game z up 0.95) above the car's position.</summary>
        public const float EyeHeight = 0.95f;
        /// <summary>[ours] The Unity layer of every car renderer (parts, wheels, brakes, shadow, fragments): the env-map
        /// camera and the road reflection view mask it out, as the exe's env views draw no cars.</summary>
        public const int CarLayer = 29;
        /// <summary>DisableEnvMapSize: cars whose projected size is under 18 px draw without the env map (0x74E160; the
        /// GC decomp has 5).</summary>
        public const float DisableEnvMapSize = 18f;
        const float NearPlane = 0.5f, FarPlane = 100f, SkyScale = 0.0065f;   // [0x8F9010], [0x8F9014], 0x6DB473

        // the face schedule of table 0x8F9028 (F R B L U D = Unity +Z +X -Z -X +Y -Y)
        static readonly CubemapFace[] Order = { CubemapFace.PositiveZ, CubemapFace.PositiveX, CubemapFace.NegativeZ, CubemapFace.NegativeX, CubemapFace.PositiveY, CubemapFace.NegativeY };
        static readonly CubemapFace[] Option2Cycle = { CubemapFace.PositiveX, CubemapFace.NegativeZ, CubemapFace.NegativeX, CubemapFace.NegativeY };

        static Mesh _skyMesh;
        static Material[] _skyMats;

        /// <summary>Sky/SkyRender.cs hook: the SKYDOME_XENON mesh and its skybox.efx materials (drawn at 0.0065 into the faces).</summary>
        public static void SetSkyDome(Mesh mesh, Material[] mats) { _skyMesh = mesh; _skyMats = mats; }

        public static void Ensure()
        {
            if (_instance || !Application.isPlaying) return;
            var go = new GameObject("NFSMW Car Environment") { hideFlags = HideFlags.DontSave };
            _instance = go.AddComponent<CarEnvironment>();
        }

        /// <summary>The hidden env-map camera (null before Awake); other systems skip it when they draw per camera.</summary>
        public static Camera EnvCamera => _instance ? _instance._cam : null;

        void Awake()
        {
            _cam = gameObject.AddComponent<Camera>();
            _cam.enabled = false;
            _cam.nearClipPlane = NearPlane;
            _cam.farClipPlane = FarPlane;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = Color.black;               // 0x6BD3B0: D3DCLEAR 3, 0xFF000000
            _cam.allowHDR = false;
            _cam.allowMSAA = false;
            _cam.useOcclusionCulling = false;
            _cube = new RenderTexture(Resolution, Resolution, 16, RenderTextureFormat.ARGB32)
            {
                dimension = TextureDimension.Cube, name = "NFSMW car envmap", useMipMap = false, autoGenerateMips = false,
            };
            _cube.Create();
        }

        void OnDestroy()
        {
            if (_cube) _cube.Release();
            Shader.SetGlobalFloat("_MW_CarEnvSet", 0f);
            Shader.SetGlobalFloat("_MW_CarLightsSet", 0f);
            Shader.SetGlobalFloat("_MW_CharLightsSet", 0f);
            Shader.SetGlobalFloat("_MW_CarTodSet", 0f);
            if (_instance == this) _instance = null;
        }

        void LateUpdate()
        {
            UpdateTunnelFade(Time.deltaTime);
            SetLights();
            CarRenderInfo.ProcessBuildQueue();
            if (Option <= 0) return;
            var focus = Focus ? Focus : FindFocus();
            var main = Camera.main;
            if (!focus && !main) return;
            // the FE never renders the env views (game flow 6 only): the showroom keeps the last cube
            if (Rig != ShaperRig.Kind.CarsInGame) return;
            transform.position = (focus ? focus.position : main.transform.position) + Vector3.up * EyeHeight;
            transform.rotation = Quaternion.identity;
            int mask = main ? main.cullingMask : -1;
            mask &= ~(1 << CarLayer) & ~(1 << World.RoadReflection.Layer) & ~(1 << 31);
            if (!UpdateData && World.WorldShadows.Enabled && World.WorldShadows.Detail >= 2) mask = 0;   // sky and flares only
            _cam.cullingMask = mask;
            try
            {
                // the env-map views take no shadow technique (sub_6E0AB0 picks them for view 1 only): no sun shadow map
                var shadows = QualitySettings.shadows;
                QualitySettings.shadows = ShadowQuality.Disable;
                try
                {
                    int period = Option == 1 ? 6 : Option == 2 ? 4 : 1;
                    int row = _frame % period;
                    if (Option == 1) RenderFace(Order[row]);
                    else if (Option == 2) { RenderFace(CubemapFace.PositiveZ); RenderFace(CubemapFace.PositiveY); RenderFace(Option2Cycle[row]); }
                    else foreach (var f in Order) RenderFace(f);
                }
                finally { QualitySettings.shadows = shadows; }
                _frame++;
            }
            catch (System.Exception) { return; }
            Shader.SetGlobalTexture("_MW_CarEnvMap", _cube);
            Shader.SetGlobalFloat("_MW_CarEnvSet", 1f);
        }

        void RenderFace(CubemapFace face)
        {
            // StuffSkyLayer(view 0x12-0x17, layer 3): the dome at ScaleFactor 0.0065 around the eye (0x6DB473), flags 0x20000
            if (_skyMesh && _skyMats != null)
            {
                var m = Matrix4x4.TRS(transform.position, Quaternion.identity, Vector3.one * SkyScale);
                for (int i = 0; i < _skyMats.Length; i++)
                    if (_skyMats[i]) Graphics.DrawMesh(_skyMesh, m, _skyMats[i], 0, _cam, i, null, ShadowCastingMode.Off, false);
            }
            // light-glow option: eRenderWorldLightFlares (no world packs on PC), RenderVehicleFlares(view, 0, 1), the sun flare
            if (LightFlares.Instance) LightFlares.Instance.DrawEnv(_cam, transform.position);
            _cam.RenderToCubemap(_cube, 1 << (int)face);
        }

        Transform FindFocus()
        {
            if (Time.frameCount % 30 != 0 && _focus) return _focus;
            _focus = null;
            foreach (var pc in FindObjectsByType<PlayerCar>(FindObjectsSortMode.None))
                if (pc.UseDeviceInput && pc.InputSlot == 0 && !pc.AiControlled) { _focus = pc.transform; break; }
            return _focus;
        }

        Transform _focus;

        // ---- light rigs -------------------------------------------------------------------------------------------------

        /// <summary>The rig of the car effect this frame: CarsInGame in game, a garage rig in the front end showroom.</summary>
        public static ShaperRig.Kind Rig { get; private set; } = ShaperRig.Kind.CarsInGame;

        /// <summary>
        /// Showroom hook: the platform set in use (FRONTEND\PLATFORMS\*.BIN) selects the garage rig as
        /// FEManager::GetGarageType does for CarRenderInfo::Render; null = back in game (CarsInGame).
        /// </summary>
        public static void SetGarage(string platformFile)
        {
            Rig = platformFile == null ? ShaperRig.Kind.CarsInGame : ShaperRig.GarageOf(platformFile);
            SetLights();
            SetGarageCube(Rig);
        }

        // ---- the front end's static garage cubes (eInitFEEnvMapPlat 0x6BF690) ----------------------------------------

        /// <summary>
        /// eInitFEEnvMapPlat (0x6BF690; called by EndGameFLowLoadingFrontEnd 0x64B6EB, sub_6DB0D0 and
        /// GarageMainScreen::RefreshBackground 0x7B4183, only while [0x8F945B] = 1, which sub_6BD4B0 clears when the device
        /// has no cube maps): for the six suffixes XPOS XNEG YPOS YNEG ZPOS ZNEG (0x8F9314) it streams the texture
        /// "%s_%s" (FEManager::GetGaragePrefixFromType 0x516E90: 1 QRACE, 2 CAREER, 3 / 4 CSHOP, 5 CARLOT, else "")
        /// from GLOBAL\DYNTEX.BIN (256² BGRA); EnvMapTextureLoadedCallback (0x6BF5A0) copies each, once all six are in,
        /// byte for byte into face 0..5 of the car env cube [0x982A64] (LockRect / memcpy of +0x40 bytes). The FE never
        /// renders the cube views, so the showroom cars reflect this fixed cube, looked up with the view-space reflection
        /// vector (Car.shader _MW_CarEnvView). [?] face rows: the raw D3D face data is uploaded as-is (SetPixelData).
        /// </summary>
        static void SetGarageCube(ShaperRig.Kind rig)
        {
            if (rig == ShaperRig.Kind.CarsInGame)
            {
                Shader.SetGlobalFloat("_MW_CarEnvView", 0f);
                if (_instance && _instance._cube) Shader.SetGlobalTexture("_MW_CarEnvMap", _instance._cube);
                return;
            }
            string prefix = rig == ShaperRig.Kind.QRace ? "QRACE" : rig == ShaperRig.Kind.Safehouse ? "CAREER"
                : rig == ShaperRig.Kind.CShop || rig == ShaperRig.Kind.BackRoom ? "CSHOP" : rig == ShaperRig.Kind.CarLot ? "CARLOT" : "";
            var cube = GarageCube(prefix);
            if (!cube) return;   // not loaded: the cube keeps its last contents, as the exe does
            Shader.SetGlobalTexture("_MW_CarEnvMap", cube);
            Shader.SetGlobalFloat("_MW_CarEnvSet", 1f);
            Shader.SetGlobalFloat("_MW_CarEnvView", 1f);
        }

        static readonly string[] FaceSuffix = { "XPOS", "XNEG", "YPOS", "YNEG", "ZPOS", "ZNEG" };   // 0x8F9314, D3D face order
        static readonly System.Collections.Generic.Dictionary<string, Cubemap> GarageCubes = new System.Collections.Generic.Dictionary<string, Cubemap>();
        static System.Collections.Generic.Dictionary<uint, GameTexture> _dynTex;

        static Cubemap GarageCube(string prefix)
        {
            if (GarageCubes.TryGetValue(prefix, out var c)) return c;
            GarageCubes[prefix] = null;
            try
            {
                if (_dynTex == null)
                {
                    _dynTex = new System.Collections.Generic.Dictionary<uint, GameTexture>();
                    string path = System.IO.Path.Combine(GameData.Root ?? "", "GLOBAL", "DYNTEX.BIN");
                    if (!System.IO.File.Exists(path)) return null;
                    var d = GameFiles.ReadBundle(path);
                    foreach (var t in TexturePack.ReadAll(d, 0, d.Length))
                        if (t.Name != null && (t.Name.EndsWith("POS") || t.Name.EndsWith("NEG"))) _dynTex[t.Hash] = t;
                }
                var faces = new GameTexture[6];
                for (int i = 0; i < 6; i++)
                {
                    if (!_dynTex.TryGetValue(EaHash.Hash(prefix + "_" + FaceSuffix[i]), out faces[i]) || faces[i].Format != TexFormat.Bgra32 ||
                        faces[i].Width != faces[0].Width || faces[i].Height != faces[i].Width) return null;
                }
                int size = faces[0].Width;
                c = new Cubemap(size, TextureFormat.BGRA32, false) { name = "NFSMW FE envmap " + prefix, filterMode = FilterMode.Bilinear };
                for (int i = 0; i < 6; i++)
                {
                    var raw = new byte[size * size * 4];
                    System.Buffer.BlockCopy(faces[i].Data, 0, raw, 0, raw.Length);
                    c.SetPixelData(raw, 0, (CubemapFace)i);
                }
                c.Apply(false, true);
                GarageCubes[prefix] = c;
                return c;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning("NFSMW FE env cube " + prefix + ": " + e.Message);
                return null;
            }
        }

        /// <summary>The rig's light directions from the scene sun (_MW_LightDir, toward the sun, Unity world space), and
        /// the characters' camera-space rig from the main camera.</summary>
        public static void SetLights()
        {
            Vector4 sun = Shader.GetGlobalVector("_MW_LightDir");
            Vector3 s = sun.sqrMagnitude > 1e-6f ? (Vector3)sun : new Vector3(0.4f, 0.75f, 0.5f);
            s.Normalize();
            var rig = ShaperRig.Table(Rig);
            for (int i = 0; i < 3; i++)
            {
                var l = rig[i];
                // Unity (x, y up, z) ↔ game (x, z, y up)
                var (gx, gy, gz) = ShaperRig.Direction(l, s.x, s.z, s.y);
                Shader.SetGlobalVector("_MW_CarLightDir" + i, new Vector4(gx, gz, gy, 0f));
                Shader.SetGlobalVector("_MW_CarLightCol" + i, new Vector4(l.R * l.Scale, l.G * l.Scale, l.B * l.Scale, 1f));
            }
            Shader.SetGlobalFloat("_MW_CarLightsSet", 1f);
            SetCharacterLights();
            SetTimeOfDayConstants();
        }

        /// <summary>
        /// ShaperLightsCharacters through elSetupLights mode 1: d = −y·camRight − z·camUp + x·camForward of
        /// S2C(θ+180, 90−φ), rebuilt from the rendering camera every frame (WorldModel::RenderModel passes view->Camera).
        /// L0 = (−0.815, 0.242, −0.527)·(right, up, forward), colour 0.265; L1 = (0.470, 0, −0.883), colour 0.59.
        /// </summary>
        static void SetCharacterLights()
        {
            var cam = Camera.main;
            if (!cam) { Shader.SetGlobalFloat("_MW_CharLightsSet", 0f); return; }
            var t = cam.transform;
            for (int i = 0; i < 2; i++)
            {
                var l = ShaperRig.Characters[i];
                var (r, u, f) = ShaperRig.CameraSpace(l);
                var d = (t.right * r + t.up * u + t.forward * f).normalized;
                Shader.SetGlobalVector("_MW_CharLightDir" + i, new Vector4(d.x, d.y, d.z, 0f));
                Shader.SetGlobalVector("_MW_CharLightCol" + i, new Vector4(l.R * l.Scale, l.G * l.Scale, l.B * l.Scale, 1f));
            }
            Shader.SetGlobalFloat("_MW_CharLightsSet", 1f);
        }

        // ---- time-of-day constants of the car effect ------------------------------------------------------------------

        /// <summary>[0x982BA8]: eDisplayFrame (0x6DE354) eases it toward 1 at 0.3/s while AmIinATunnel(player view, 1)
        /// ([0x982BA4]) and toward 0 at 2/s outside, by WorldTimeElapsed.</summary>
        public static float TunnelFade { get; private set; }

        static void UpdateTunnelFade(float worldTimeElapsed)
        {
            bool inTunnel = false;
            try { inTunnel = Game.Police.PoliceZones.CameraInTunnel(); } catch (System.Exception) { }
            TunnelFade = inTunnel ? Mathf.Min(1f, TunnelFade + 0.3f * worldTimeElapsed) : Mathf.Max(0f, TunnelFade - 2f * worldTimeElapsed);
        }

        /// <summary>
        /// The two car.efx inputs that follow the world lighting:
        /// • INVSHADOWSTRENGTH (effect parameter 53; set by 0x6D7660 / 0x6E0200 when g_ShadowDetail [0x901830] > 0) =
        ///   [0x982BA8] tunnel fade + 1 − [0x8FAE68], with [0x8FAE68] = 0.1 + (0.9 − 0.1)·[0x8FAE64] and [0x8FAE64] =
        ///   clamp(sunZ − 0.14, 0, 0.537) / 0.537 · (1 − cloud) (0x6C83D0; TimeOfDay.ShadowStrength). Midday, clear sky:
        ///   0.1 (a shadowed car pixel ×0.3); dusk or overcast: 0.9 (×1.1); in a tunnel up to +1. The .efx default is 0.
        /// • the specular scale: sub_6C76E0 multiplies SpecularMin / SpecularRange by the time-of-day CarSpecScale
        ///   (TOD +0x60, blended from the timeofdaylighting sets).
        /// Published as _MW_CarInvShadow / _MW_CarSpecScale with _MW_CarTodSet = 1 while the world lighting runs (the
        /// shader keeps the material's _InvShadowStrength and a scale of 1 otherwise, e.g. in the front end) [ours].
        /// </summary>
        static void SetTimeOfDayConstants()
        {
            var driver = Sky.TimeOfDayDriver.Instance;
            var tod = driver ? driver.Tod : null;
            if (tod == null) { Shader.SetGlobalFloat("_MW_CarTodSet", 0f); return; }
            float shadow = 0.1f + (0.9f - 0.1f) * tod.ShadowStrength(driver.Clouds.Cloud);
            Shader.SetGlobalFloat("_MW_CarInvShadow", TunnelFade + 1f - shadow);
            Shader.SetGlobalFloat("_MW_CarSpecScale", tod.S[0x60 / 4]);
            Shader.SetGlobalFloat("_MW_CarTodSet", 1f);
        }
    }
}
