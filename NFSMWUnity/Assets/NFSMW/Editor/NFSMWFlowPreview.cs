using System.Linq;
using System.IO;
using NfsMw.Game;
using NfsMw.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NfsMw.EditorTools
{
    /// <summary>Batch: walks the menus with a fresh career and renders each screen to Preview/flow_*.png.</summary>
    public static class NFSMWFlowPreview
    {
        static RenderTexture _rt;
        static Camera _feCam;

        public static void Render()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Directory.CreateDirectory("Preview");
            GameSession.Load();
            GameSession.Profile = new Profile { Name = "Preview", CareerStarted = true, Cash = 60000, Bounty = 25000 };
            GameSession.Profile.Cars.Add(new OwnedCar { Car = "gti" });
            GameSession.Mode = GameMode.Career;
            _rt = new RenderTexture(1280, 720, 24);
            _feCam = new GameObject("FE Cam").AddComponent<Camera>();
            _feCam.clearFlags = CameraClearFlags.Depth;
            _feCam.cullingMask = 1 << 5; // UI
            _feCam.depth = 100;
            var fe = FrontEnd.Create(_feCam);
            fe.Canvas.gameObject.layer = 5;
            Debug.Log("NFSMW icons: " + string.Join(" ", FrontEndData.Textures.All.Where(t => t.Name != null && (t.Name.Contains("ICON") || t.Name.Contains("MANUFACTURER") || t.Name.Contains("LOGO"))).Select(t => t.Name)));
            Shot(fe, new MainMenuScreen(), "mainmenu");
            Shot(fe, new CareerMenuScreen(), "careermenu");
            Shot(fe, new CarLotScreen(false), "carlot");
            Shot(fe, new RaceListScreen(RaceListScreen.Kind.Career), "races");
            Shot(fe, new CustomizeScreen(), "customize");
            Shot(fe, new PaintScreen(), "paint");
            Shot(fe, new PerformanceScreen(), "performance");
            Shot(fe, new MyCarsScreen(), "mycars");
            Shot(fe, new RivalScreen(), "rival");
            Shot(fe, new MilestonesScreen(), "milestones");
            Shot(fe, new RapSheetScreen(), "rapsheet");
            Shot(fe, new OptionsScreen(), "options");
            Shot(fe, new RaceListScreen(RaceListScreen.Kind.QuickRace), "quickrace");
            Shot(fe, new RaceListScreen(RaceListScreen.Kind.Challenge), "challenges");
            Shot(fe, new QuickCarScreen(), "quickcar");
            Shot(fe, new VisualScreen(), "visual");
            Shot(fe, PartScreen.BodyKits(), "parts_kits");
            Shot(fe, PartScreen.Rims(), "parts_rims");
            Shot(fe, new LanMenuScreen(), "lanmenu");
            var stats = new PursuitStats { Length = 143, Bounty = 6200, CostToState = 18750, CopsDamaged = 3, CopsDestroyed = 2, RoadblocksDodged = 1, Evaded = true };
            stats.Infraction("Speeding");
            stats.Infraction("Resisting Arrest");
            stats.Infraction("Ramming a Police Vehicle");
            var done = GameSession.ScorePursuit(stats);
            Shot(fe, new PursuitResultScreen(stats, 6200, done), "pursuit");
        }

        /// <summary>Batch: the in-game HUD and minimap over the chase view at the start position.</summary>
        public static void Hud()
        {
            NFSMWSetup.CreateScene();
            var boot = Object.FindFirstObjectByType<GameBootstrap>();
            boot.BuildImmediate();
            Directory.CreateDirectory("Preview");
            var hud = Object.FindFirstObjectByType<GameHud>();
            Debug.Log($"NFSMW hud: GameHud {(hud ? "created" : "missing")}, minimap {(Object.FindFirstObjectByType<Minimap>() ? "created" : "missing")}");
            var car = boot.Player.transform;
            var cam = Camera.main;
            cam.transform.position = car.position - car.forward * 6.5f + Vector3.up * 2.2f;
            cam.transform.LookAt(car.position + Vector3.up * 0.8f);
            var uiCam = new GameObject("UI Cam").AddComponent<Camera>();
            uiCam.clearFlags = CameraClearFlags.Depth;
            uiCam.cullingMask = 1 << 5;
            uiCam.depth = 100;
            foreach (var c in Object.FindObjectsByType<Canvas>(FindObjectsSortMode.None))
            {
                if (!c.isRootCanvas) continue;
                c.renderMode = RenderMode.ScreenSpaceCamera;
                c.worldCamera = uiCam;
                c.planeDistance = 1f;
                foreach (var t in c.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = 5;
            }
            var rt = new RenderTexture(1280, 720, 24);
            uiCam.targetTexture = rt;   // screen-space maths (minimap clip) against the render size
            Canvas.ForceUpdateCanvases();
            if (hud) hud.SendMessage("LateUpdate");
            var map = Object.FindFirstObjectByType<Minimap>();
            if (map) map.SendMessage("LateUpdate");
            for (int i = 0; i < 90; i++)   // let the fade-in scripts run
                foreach (var v in Object.FindObjectsByType<FEView>(FindObjectsSortMode.None)) v.Apply(1f / 30f);
            Canvas.ForceUpdateCanvases();
            cam.targetTexture = rt; cam.Render(); cam.targetTexture = null;
            uiCam.targetTexture = rt; uiCam.Render(); uiCam.targetTexture = null;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes("Preview/hud.png", tex.EncodeToPNG());
            Debug.Log("NFSMW hud: rendered Preview/hud.png");
        }

        /// <summary>Batch: customised cars in the showroom (body kit, spoiler, rims, roof scoop, hood, vinyl).</summary>
        /// <summary>The 64 minimap tiles as one image with the road graph and the career start plotted by MapPoint.</summary>
        public static void MapCheck()
        {
            GameData.Mount();
            var tiles = (Texture2D[])typeof(Minimap).GetMethod("LoadTiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static).Invoke(null, null);
            var img = new Texture2D(1024, 1024, TextureFormat.RGBA32, false);
            for (int i = 0; i < 64; i++)
            {
                var t = tiles[i];
                var rt = RenderTexture.GetTemporary(128, 128);
                Graphics.Blit(t, rt, new Vector2(1, -1), new Vector2(0, 1));
                RenderTexture.active = rt;
                img.ReadPixels(new Rect(0, 0, 128, 128), (i % 8) * 128, 1024 - 128 - (i / 8) * 128);
                RenderTexture.active = null;
                RenderTexture.ReleaseTemporary(rt);
            }
            var net = Roads.Net;
            for (int n = 0; n < net.Nodes.Length; n += 1)
            {
                var mp = Minimap.MapPoint(Coords.ToUnity(net.Nodes[n]));
                int x = (int)mp.x, y = 1023 - (int)mp.y;
                if (x >= 0 && x < 1024 && y >= 0 && y < 1024) img.SetPixel(x, y, Color.red);
            }
            var start = Minimap.MapPoint(Coords.ToUnity(new NfsMw.Core.Vec3(679.4f, 2288.0f, 153.9f)));
            for (int dx = -4; dx <= 4; dx++) for (int dy = -4; dy <= 4; dy++) img.SetPixel((int)start.x + dx, 1023 - (int)start.y + dy, Color.green);
            // the first Blacklist race's route (blue), its checkpoints (yellow) and the first 400 m (cyan)
            GameSession.Load();
            var ev = GameSession.Career.Bins.Values.SelectMany(b => b.WorldRaces).First(e => e.EventId == "15.1.1");
            var wps = new System.Collections.Generic.List<NfsMw.Core.Vec3> { ev.Start.Position };
            wps.AddRange(ev.Checkpoints.Select(c => c.Position));
            if (ev.Finish != null) wps.Add(ev.Finish.Position);
            var route = Roads.Through(wps);
            float along = 0;
            for (int i = 1; i < route.Count; i++)
            {
                along += Vector3.Distance(route[i - 1], route[i]);
                for (float f = 0; f <= 1; f += 0.05f)
                {
                    var mp = Minimap.MapPoint(Vector3.Lerp(route[i - 1], route[i], f));
                    img.SetPixel((int)mp.x, 1023 - (int)mp.y, along < 400 ? Color.cyan : Color.blue);
                }
                if (along < 450) Debug.Log($"NFSMW route {i}: {along:0} m at {route[i]}");
            }
            foreach (var w in wps)
            {
                var mp = Minimap.MapPoint(Coords.ToUnity(w));
                for (int dx = -2; dx <= 2; dx++) for (int dy = -2; dy <= 2; dy++) img.SetPixel((int)mp.x + dx, 1023 - (int)mp.y + dy, Color.yellow);
            }
            img.Apply();
            File.WriteAllBytes("Preview/mapcheck.png", img.EncodeToPNG());
            // close-up of the start area
            var s0 = Minimap.MapPoint(Coords.ToUnity(ev.Start.Position));
            int cx = Mathf.Clamp((int)s0.x - 100, 0, 824), cy = Mathf.Clamp(1023 - (int)s0.y - 100, 0, 824);
            var crop = new Texture2D(200, 200);
            crop.SetPixels(img.GetPixels(cx, cy, 200, 200));
            crop.Apply();
            var big = new RenderTexture(800, 800, 0);
            crop.filterMode = FilterMode.Point;
            Graphics.Blit(crop, big);
            RenderTexture.active = big;
            var outImg = new Texture2D(800, 800);
            outImg.ReadPixels(new Rect(0, 0, 800, 800), 0, 0);
            RenderTexture.active = null;
            File.WriteAllBytes("Preview/mapcheck_start.png", outImg.EncodeToPNG());
            Debug.Log($"NFSMW mapcheck: start at map {start}");
        }

        /// <summary>Writes named front-end textures to Preview/tex_NAME.png (args after -tex, comma separated).</summary>
        public static void DumpTextures()
        {
            FrontEndData.Load();
            var args = System.Environment.GetCommandLineArgs();
            var names = args.SkipWhile(x => x != "-tex").Skip(1).FirstOrDefault() ?? "";
            foreach (var name in names.Split(','))
            {
                var t = name.StartsWith("0x") ? FrontEndData.Textures.Get(System.Convert.ToUInt32(name.Substring(2), 16)) : FrontEndData.TextureByName(name);
                if (!t) { Debug.Log("NFSMW tex: missing " + name); continue; }
                var rt = RenderTexture.GetTemporary(t.width, t.height);
                Graphics.Blit(t, rt, new Vector2(1, -1), new Vector2(0, 1));
                RenderTexture.active = rt;
                var img = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false);
                img.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0);
                RenderTexture.active = null;
                RenderTexture.ReleaseTemporary(rt);
                File.WriteAllBytes($"Preview/tex_{name}.png", img.EncodeToPNG());
                Debug.Log($"NFSMW tex: {name} {t.width}x{t.height}");
            }
        }

        /// <summary>Logs the subtitle lines of the story movies (they stand in for the movies when MOVIES is empty).</summary>
        public static void SubtitleCheck()
        {
            FrontEndData.Load();
            foreach (var m in new[] { "storyfmv_rac01", "storyfmv_bus12", "blacklist_15" })
                foreach (var (t, line) in StoryCard.Subtitles(m))
                    Debug.Log($"NFSMW subs {m} {t:0.0}s: {line.Replace('\n', '/')}");
        }

        /// <summary>Wheel and body heights of a few cars (ride height check).</summary>
        public static void RideCheck()
        {
            GameData.Mount();
            foreach (var car in new[] { "COBALTSS", "GTI", "BMWM3GTR", "COPMIDSIZE", "GALLARDO" })
            {
                var r = CarBuilder.Build(car, Color.white);
                Bounds body = new Bounds(), wheel = new Bounds();
                bool hb = false, hw = false;
                foreach (var rend in r.Root.GetComponentsInChildren<Renderer>())
                {
                    bool w = rend.transform.IsChildOf(r.Wheels[0]);
                    if (w) { if (!hw) { wheel = rend.bounds; hw = true; } else wheel.Encapsulate(rend.bounds); }
                    else if (!System.Array.Exists(r.Wheels, x => rend.transform.IsChildOf(x))) { if (!hb) { body = rend.bounds; hb = true; } else body.Encapsulate(rend.bounds); }
                }
                var s = r.Spec;
                Debug.Log($"NFSMW ride {car}: tireR {s.TireRadiusFront:0.###}/{s.TireRadiusRear:0.###} rideH {s.RideHeightFront}/{s.RideHeightRear} hubY {r.Wheels[0].localPosition.y:0.###} " +
                          $"wheel min {wheel.min.y:0.###} max {wheel.max.y:0.###} (r {wheel.extents.y:0.###}) body min {body.min.y:0.###} max {body.max.y:0.###} wheelZ {r.Wheels[0].localPosition.z:0.##}/{r.Wheels[2].localPosition.z:0.##} bodyZ {body.min.z:0.##}..{body.max.z:0.##}");
                // side view on a ground plane at y = 0
                var ground = GameObject.CreatePrimitive(PrimitiveType.Quad);
                ground.transform.rotation = Quaternion.Euler(90, 0, 0);
                ground.transform.localScale = new Vector3(20, 20, 1);
                var light = new GameObject("L").AddComponent<Light>();
                light.type = LightType.Directional;
                light.transform.rotation = Quaternion.Euler(30, 60, 0);
                var cam = new GameObject("C").AddComponent<Camera>();
                cam.orthographic = true;
                cam.orthographicSize = 1.4f;
                cam.transform.position = new Vector3(8, 0.9f, 0);
                cam.transform.LookAt(new Vector3(0, 0.9f, 0));
                cam.backgroundColor = Color.gray;
                cam.clearFlags = CameraClearFlags.SolidColor;
                var rt = new RenderTexture(900, 500, 24);
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(900, 500);
                tex.ReadPixels(new Rect(0, 0, 900, 500), 0, 0);
                Directory.CreateDirectory("Preview");
                File.WriteAllBytes($"Preview/ride_{car}.png", tex.EncodeToPNG());
                RenderTexture.active = null;
                Object.DestroyImmediate(cam.gameObject); Object.DestroyImmediate(light.gameObject); Object.DestroyImmediate(ground);
                Object.DestroyImmediate(r.Root);
            }
        }

        public static void Visual()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Directory.CreateDirectory("Preview");
            GameSession.Load();
            GameSession.Profile = new Profile { Name = "Preview", CareerStarted = true, Cash = 60000, Bin = 1 };
            GameSession.Mode = GameMode.Career;
            _rt = new RenderTexture(1280, 720, 24);
            _feCam = new GameObject("FE Cam").AddComponent<Camera>();
            _feCam.clearFlags = CameraClearFlags.Depth;
            _feCam.cullingMask = 1 << 5;
            _feCam.depth = 100;
            var fe = FrontEnd.Create(_feCam);
            fe.Canvas.gameObject.layer = 5;
            var looks = new[]
            {
                new OwnedCar { Car = "gallardo", Paint = new Color(0.9f, 0.55f, 0.05f) },
                new OwnedCar { Car = "gallardo", Paint = new Color(0.9f, 0.55f, 0.05f), Kit = 2, Spoiler = 3, RimBrand = "BBS", RimStyle = 2, RimSize = 19, Vinyl = "GALLARDO_FLAME02" },
                new OwnedCar { Car = "gti", Paint = new Color(0.1f, 0.3f, 0.8f), Kit = 1, Spoiler = 2, SpoilerCF = true, Roof = 2, RimBrand = "OZ", RimStyle = 1, RimSize = 18, Hood = CarParts.Hoods("gti").FirstOrDefault() },
                new OwnedCar { Car = "bmwm3gtr", Paint = new Color(0.85f, 0.85f, 0.88f), Kit = 3, Spoiler = 1, RimBrand = "VOLK", RimStyle = 1, RimSize = 20, Vinyl = CarParts.VinylNames("bmwm3gtr").Skip(10).FirstOrDefault() ?? "" },
            };
            for (int i = 0; i < looks.Length; i++)
            {
                GameSession.Profile.Cars.Clear();
                GameSession.Profile.Cars.Add(looks[i]);
                Debug.Log($"NFSMW visual: {looks[i].Car} kits {string.Join(",", CarParts.Kits(looks[i].Car))} hoods {CarParts.Hoods(looks[i].Car).Count} spoilers {CarParts.Spoilers(looks[i].Car).Count} roofs {CarParts.Roofs().Count} vinyls {CarParts.VinylNames(looks[i].Car).Count} rims {CarParts.AvailableRimBrands().Count()} brands");
                Shot(fe, new VisualScreen(), "visual_" + i);
            }
        }

        /// <summary>Batch: NIS headers and NIS audio streams decode.</summary>
        public static void NisCheck()
        {
            GameData.Mount();
            NfsMw.Core.NisLibrary.Load(GameData.Root);
            Debug.Log($"NFSMW nis: {NfsMw.Core.NisLibrary.Scenes.Count} scenes, {NisAudio.StreamCount} audio streams");
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            var clip = NisAudio.Arrest();
            if (!clip) { Debug.LogError("NFSMW nis: arrest audio missing"); return; }
            var data = new float[clip.samples * clip.channels];
            clip.GetData(data, 0);
            double sum = 0; float peak = 0;
            foreach (var v in data) { sum += v * v; peak = Mathf.Max(peak, Mathf.Abs(v)); }
            Debug.Log($"NFSMW nis: {clip.name} {clip.length:0.0} s {clip.frequency} Hz {clip.channels} ch, rms {System.Math.Sqrt(sum / data.Length):0.000} peak {peak:0.00}, decoded in {t0.ElapsedMilliseconds} ms");
            var speech = CopRadio.Speech;
            int refs = 0; foreach (var l in speech.Events.Values) refs += l.Count;
            Debug.Log($"NFSMW radio: {speech.Lines.Count} lines, {speech.Events.Count} events, {refs} line refs");
            foreach (var name in new[] { "Setup_InitPursuit", "ExtraCops_RBPosition", "HeliSpecific_HeliLostVisual", "Backup_BUReminder" })
            {
                var lines = speech.Events.TryGetValue(name, out var ls) ? ls : new System.Collections.Generic.List<int>();
                var st = lines.Count > 0 ? speech.Take(lines[0], 0) : null;
                float rms = 0; if (st != null) { double q = 0; foreach (var v in st.Data[0]) q += v * v; rms = (float)System.Math.Sqrt(q / System.Math.Max(1, st.Samples)); }
                Debug.Log($"NFSMW radio: {name} lines [{string.Join(",", lines)}] first take {(st != null ? $"{st.Length:0.00} s rms {rms:0.000}" : "none")}");
            }
        }

        /// <summary>Batch: the showroom screens under each yaw convention, to match the FE_ATTRIB camera angles.</summary>
        public static void GarageAngles()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Directory.CreateDirectory("Preview");
            GameSession.Load();
            GameSession.Profile = new Profile { Name = "Preview", CareerStarted = true, Cash = 60000, Bounty = 25000 };
            GameSession.Profile.Cars.Add(new OwnedCar { Car = "gti" });
            GameSession.Mode = GameMode.Career;
            _rt = new RenderTexture(1280, 720, 24);
            _feCam = new GameObject("FE Cam").AddComponent<Camera>();
            _feCam.clearFlags = CameraClearFlags.Depth;
            _feCam.cullingMask = 1 << 5;
            _feCam.depth = 100;
            var fe = FrontEnd.Create(_feCam);
            fe.Canvas.gameObject.layer = 5;
            foreach (var (sign, off) in new[] { (1f, 0f), (-1f, 0f), (1f, 180f), (-1f, 180f) })
            {
                CarShowroom.YawSign = sign;
                CarShowroom.YawOffset = off;
                string tag = $"{(sign > 0 ? "p" : "m")}{off:0}";
                Shot(fe, new CareerMenuScreen(), "garage_safehouse_" + tag);
                Shot(fe, new CarLotScreen(false), "garage_carlot_" + tag);
                Shot(fe, new CustomizeScreen(), "garage_customize_" + tag);
            }
        }

        /// <summary>Batch: the safehouse car from the side and low front (car shadow, floor reflection), with the shadow's state logged.</summary>
        public static void GarageShadow()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Directory.CreateDirectory("Preview");
            GameSession.Load();
            GameSession.Profile = new Profile { Name = "Preview", CareerStarted = true, Cash = 60000, Bounty = 25000 };
            GameSession.Profile.Cars.Add(new OwnedCar { Car = "gti" });
            GameSession.Mode = GameMode.Career;
            _rt = new RenderTexture(1280, 720, 24);
            _feCam = new GameObject("FE Cam").AddComponent<Camera>();
            _feCam.clearFlags = CameraClearFlags.Depth;
            _feCam.cullingMask = 1 << 5;
            _feCam.depth = 100;
            var fe = FrontEnd.Create(_feCam);
            fe.Canvas.gameObject.layer = 5;
            Shot(fe, new CareerMenuScreen(), "garage_shadow_menu");
            foreach (var sh in Object.FindObjectsByType<CarShadow>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var mr = sh.GetComponent<MeshRenderer>();
                var mf = sh.GetComponent<MeshFilter>();
                Debug.Log($"NFSMW shadow: {sh.transform.parent.name} layer {sh.gameObject.layer} active {sh.gameObject.activeInHierarchy} mat {(mr.sharedMaterial ? mr.sharedMaterial.name + "/" + mr.sharedMaterial.mainTexture : "none")} bounds {mf.sharedMesh.bounds} verts {string.Join(" ", mf.sharedMesh.vertices)}");
            }
            var cam = fe.Showroom.Camera;
            foreach (var (name, pos) in new[] { ("side", new Vector3(-6, 0.6f, 0)), ("low_front", new Vector3(1.5f, 0.35f, 5)) })
            {
                cam.transform.position = new Vector3(0, -2000, 0) + pos;
                cam.transform.LookAt(new Vector3(0, -2000, 0));
                cam.fieldOfView = 45;
                var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                cam.targetTexture = _rt;
                cam.Render();
                cam.targetTexture = null;
                RenderTexture.active = _rt;
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes($"Preview/garage_shadow_{name}.png", tex.EncodeToPNG());
            }
        }

        /// <summary>Batch: HUD_SingleRace with the nitrous / speedbreaker meters at several fills (checks the mask rotation).</summary>
        public static void HudMeters()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            Directory.CreateDirectory("Preview");
            var rt = new RenderTexture(1280, 720, 24);
            var cam = new GameObject("HUD Cam").AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.25f, 0.3f, 0.35f);
            cam.cullingMask = 1 << 5;
            var canvas = FrontEndCanvas.Create("HUD Canvas", cam, 5);
            canvas.gameObject.layer = 5;
            var view = FEView.Open("HUD_SingleRace", canvas.transform);
            foreach (float f in new[] { 0f, 0.5f, 1f })
            {
                foreach (var h in new uint[] { 0xEDFB6D37, 0x5DF2F5B4 })
                {
                    var n = view.Find(h);
                    if (n != null) n.Data.MaskAngle = 175f * (1f - f);
                }
                for (int i = 0; i < 3; i++) view.Apply(1f / 30f);
                Canvas.ForceUpdateCanvases();
                cam.targetTexture = rt;
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
                RenderTexture.active = null;
                cam.targetTexture = null;
                File.WriteAllBytes($"Preview/hud_meters_{f:0.0}.png", tex.EncodeToPNG());
            }
            foreach (var nm in new[] { "TRACK_MAP", "TRACK_MAP1", "PLAYERCARINDICATOR", "TRACKMAPTARGETRING", "MINIMAP_NORTH_INDICATOR", "MAP_COLOR_TINT" })
            {
                var n = view.Find(nm);
                if (n == null) { Debug.Log($"NFSMW hud: {nm} missing"); continue; }
                var rtn = n.Rect;
                Debug.Log($"NFSMW hud: {nm} {n.Obj.Hash:X8} type {n.Obj.Type} pos {n.Data.X},{n.Data.Y} size {n.Data.SizeX}x{n.Data.SizeY} parent {n.Obj.Parent:X8} rect {(rtn ? rtn.anchoredPosition.ToString() + " " + rtn.sizeDelta : "-")} visible {n.Visible}");
            }
            Debug.Log("NFSMW flow: hud meters rendered");
        }

        static void Shot(FrontEnd fe, FEScreen screen, string name)
        {
            fe.CloseAll();
            fe.Push(screen);
            for (int i = 0; i < 40; i++) { screen.View.Apply(1f / 30f); fe.Step(default); }
            Canvas.ForceUpdateCanvases();
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            var show = fe.Showroom ? fe.Showroom.Camera : null;
            RenderTexture.active = _rt;
            GL.Clear(true, true, new Color(0.1f, 0.1f, 0.1f));
            if (show && show.enabled)
            {
                fe.Showroom.FinishMove();
                fe.Showroom.SendMessage("LateUpdate", SendMessageOptions.DontRequireReceiver);
                show.targetTexture = _rt;
                show.Render();
                show.targetTexture = null;
            }
            _feCam.targetTexture = _rt;
            _feCam.Render();
            _feCam.targetTexture = null;
            RenderTexture.active = _rt;
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            RenderTexture.active = null;
            File.WriteAllBytes($"Preview/flow_{name}.png", tex.EncodeToPNG());
            Debug.Log($"NFSMW flow: {name} ({screen.Package}) rendered");
        }
    }
}
