using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using NfsMw.Game;
using UnityEngine.Rendering;

namespace NfsMw.EditorTools
{
    public static class NFSMWSetup
    {
        const string ScenePath = "Assets/Scenes/FreeRoam.unity";
        static readonly string[] Shaders = { "NFSMW/World", "NFSMW/WorldTransparent", "NFSMW/WorldDecal", "NFSMW/Car", "NFSMW/UIRoundClip", "NFSMW/Glow", "NFSMW/UIMultiImage", "NFSMW/UIBlend", "NFSMW/Sky", "NFSMW/SkyBox", "NFSMW/VisualTreatment", "NFSMW/Particle", "NFSMW/Character", "NFSMW/CharacterShadow", "NFSMW/Pixelate", "NFSMW/MovieYUV", "NFSMW/RainDrop", "NFSMW/WorldReflect", "NFSMW/WorldNormalMap", "NFSMW/GlossyWindow", "NFSMW/Tree", "NFSMW/Sun", "NFSMW/WorldNoFog", "NFSMW/SunQuery", "NFSMW/WorldShadowCg", "Standard" };

        [MenuItem("NFSMW/Create Free Roam Scene")]
        public static void CreateScene()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
            var boot = new GameObject("NFS Most Wanted").AddComponent<GameBootstrap>();
            boot.StartInFrontEnd = true;
            var cam = Camera.main;
            if (cam) cam.gameObject.AddComponent<ChaseCamera>();
            Directory.CreateDirectory("Assets/Scenes");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene(ScenePath, true) };
            IncludeShaders();
            Selection.activeObject = boot;
            Debug.Log("NFSMW: created " + ScenePath + " — press Play to drive.");
        }

        /// <summary>Adds every runtime shader (found by Shader.Find at runtime) to the always-included list. The build calls
        /// it each time, so a shader added to <see cref="Shaders"/> reaches the player without re-creating the scene.</summary>
        public static void IncludeShaders()
        {
            foreach (var s in Shaders) AlwaysIncludeShader(s);
            AssetDatabase.SaveAssets();
        }

        static void AlwaysIncludeShader(string name)
        {
            var shader = Shader.Find(name);
            if (!shader) { Debug.LogWarning("NFSMW: shader " + name + " not found; it will be missing from the build"); return; }
            // check.sh compiles only C#: a broken shader (e.g. a renamed global) otherwise shows up as magenta in play
            if (ShaderUtil.ShaderHasError(shader)) Debug.LogError("NFSMW: shader " + name + " has compile errors; it will draw magenta");
            var gs = AssetDatabase.LoadAssetAtPath<GraphicsSettings>("ProjectSettings/GraphicsSettings.asset");
            var so = new SerializedObject(gs);
            var list = so.FindProperty("m_AlwaysIncludedShaders");
            for (int i = 0; i < list.arraySize; i++)
                if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) return;
            list.InsertArrayElementAtIndex(list.arraySize);
            list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
            so.ApplyModifiedProperties();
        }

        /// <summary>Batch check: builds the start area in edit mode and renders views to Preview/*.png.</summary>
        public static void BatchPreview()
        {
            CreateScene();
            var boot = Object.FindFirstObjectByType<GameBootstrap>();
            var t0 = System.Diagnostics.Stopwatch.StartNew();
            boot.BuildImmediate();
            Debug.Log($"NFSMW preview: built in {t0.ElapsedMilliseconds} ms, sections loaded {boot.World.LoadedCount}");
            Directory.CreateDirectory("Preview");
            var cam = Camera.main;
            cam.farClipPlane = 3000f;
            var car = boot.Player.transform;
            var renderers = boot.World.GetComponentsInChildren<Renderer>();
            Debug.Log($"NFSMW preview: world renderers={renderers.Length} colliders={boot.World.GetComponentsInChildren<Collider>().Length} car={car.position} missing textures={GameData.Textures.Missing.Count}");
            foreach (var m in GameData.Textures.Missing.Take(20)) Debug.Log($"NFSMW preview: missing texture {m:X8}");

            ProbeSurface(car.position + car.forward * 4f);
            ProbeSurface(car.position - car.right * 4f);
            Shot(cam, car.position - car.forward * 6.5f + Vector3.up * 2.2f, car.position + Vector3.up * 0.8f, "chase.png");
            Shot(cam, car.position + car.right * 3.8f + car.forward * 3.5f + Vector3.up * 1.3f, car.position + Vector3.up * 0.5f, "car.png");
            Shot(cam, car.position + new Vector3(-60f, 45f, -60f), car.position, "start_area.png");
            Shot(cam, car.position + new Vector3(0, 350f, -250f), car.position, "overview.png");
        }

        /// <summary>Batch: street-level views in several districts (Rosewood, Camden, the city) to check the scenery.</summary>
        public static void WorldShots()
        {
            CreateScene();
            var boot = Object.FindFirstObjectByType<GameBootstrap>();
            boot.BuildImmediate();
            Directory.CreateDirectory("Preview");
            var cam = Camera.main;
            cam.farClipPlane = 3000f;
            // minimap points (u, v) of district streets -> Unity x = (u - 188) / 0.1538, z = -(v - 779.3) / 0.1538
            foreach (var r in boot.World.GetComponentsInChildren<Renderer>())
                if (r.bounds.size.x > 1200f || r.bounds.size.z > 1200f)
                    Debug.Log($"NFSMW world: big {r.name} size {r.bounds.size} centre {r.bounds.center} mat {r.sharedMaterial?.name} shader {r.sharedMaterial?.shader.name}");
            foreach (var r in boot.World.GetComponentsInChildren<Renderer>())
                if (r.name.StartsWith("SKYDOME"))
                    foreach (var m in r.sharedMaterials)
                        if (m && m.mainTexture is Texture2D t)
                        {
                            var rt = RenderTexture.GetTemporary(t.width, t.height);
                            Graphics.Blit(t, rt);
                            RenderTexture.active = rt;
                            var img = new Texture2D(t.width, t.height, TextureFormat.RGBA32, false);
                            img.ReadPixels(new Rect(0, 0, t.width, t.height), 0, 0);
                            RenderTexture.active = null;
                            RenderTexture.ReleaseTemporary(rt);
                            var px = img.GetPixels();
                            float a = 0; foreach (var c in px) a += c.a;
                            Debug.Log($"NFSMW world: sky texture {t.name} {t.width}x{t.height} mean alpha {a / px.Length:0.00}");
                            File.WriteAllBytes($"Preview/sky_{t.name}.png", img.EncodeToPNG());
                        }
            var spots = new (string name, float u, float v)[] { ("city", 760, 690), ("camden", 470, 720), ("rosewood", 330, 300), ("coast", 830, 470) };
            foreach (var (name, u, v) in spots)
            {
                var p = new Vector3((u - 188f) / 0.1538f, 0, -(v - 779.3f) / 0.1538f);
                int node = Roads.Nearest(p, 800f);
                if (node >= 0) p = Roads.Node(node);
                boot.World.Stream(p, true);
                var fwd = Vector3.forward;
                if (node >= 0 && Roads.Net.Links[node].Count > 0) fwd = Vector3.ProjectOnPlane(Roads.Node(Roads.Net.Links[node][0]) - p, Vector3.up).normalized;
                foreach (var r in boot.World.GetComponentsInChildren<Renderer>())
                    if (r.sharedMaterial && (r.sharedMaterial.name.StartsWith("SKY") || r.name.StartsWith("SKY") || r.bounds.size.y > 1000f))
                        Debug.Log($"NFSMW world: {name} sky? {r.name} mat {r.sharedMaterial.name} shader {r.sharedMaterial.shader.name} size {r.bounds.size} active {r.gameObject.activeInHierarchy}");
                // what lies under given pixels of the back view (camden overpass frame, rosewood floating card)
                {
                    bool back = name != "rosewood";
                    cam.transform.position = back ? p + Vector3.up * 3f + fwd * 8f : p + Vector3.up * 3f - fwd * 8f;
                    cam.transform.LookAt(back ? p - fwd * 40f + Vector3.up * 6f : p + fwd * 40f + Vector3.up * 6f);
                    var px = name == "camden" ? new[] { new Vector2(340, 720 - 120), new Vector2(200, 720 - 90), new Vector2(560, 720 - 160) }
                           : name == "rosewood" ? new[] { new Vector2(460, 720 - 90), new Vector2(700, 720 - 300), new Vector2(700, 720 - 330) } : new Vector2[0];
                    var cam0 = cam.targetTexture;
                    cam.targetTexture = new RenderTexture(1280, 720, 24);
                    foreach (var q in px)
                    {
                        var ray = cam.ScreenPointToRay(q);
                        var hits = new System.Collections.Generic.List<(float, string)>();
                        foreach (var r in boot.World.GetComponentsInChildren<Renderer>(true))
                            if (r.bounds.IntersectRay(ray, out float dd) && dd > 0 && dd < 1500f && !r.name.StartsWith("SKY"))
                            {
                                var mf = r.GetComponent<MeshFilter>();
                                hits.Add((dd, $"{r.name} active {r.gameObject.activeInHierarchy} enabled {r.enabled} mats [{string.Join(",", System.Array.ConvertAll(r.sharedMaterials, m => m ? m.name + "/" + m.shader.name + "/q" + m.renderQueue : "null"))}] tris {(mf && mf.sharedMesh ? mf.sharedMesh.triangles.Length / 3 : 0)} size {r.bounds.size}"));
                            }
                        hits.Sort((x, y) => x.Item1.CompareTo(y.Item1));
                        Debug.Log($"NFSMW world: probe {name} pixel {q} -> {hits.Count} renderers");
                        foreach (var h in hits.GetRange(0, System.Math.Min(10, hits.Count))) Debug.Log($"NFSMW world:    {h.Item1:0} m {h.Item2}");
                    }
                    cam.targetTexture = cam0;
                }
                if (name == "camden")
                    foreach (var r in boot.World.GetComponentsInChildren<Renderer>(true))
                    {
                        if (!r.name.StartsWith("XB_CityEast_6Story1U_1a")) continue;
                        var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                        var cols = mesh.colors32;
                        var tris = new System.Collections.Generic.List<int>();
                        for (int sm = 0; sm < mesh.subMeshCount; sm++)
                        {
                            mesh.GetTriangles(tris, sm);
                            int amin = 255, amax = 0;
                            foreach (var vi in tris) if (cols.Length > vi) { amin = System.Math.Min(amin, cols[vi].a); amax = System.Math.Max(amax, cols[vi].a); }
                            var m = sm < r.sharedMaterials.Length ? r.sharedMaterials[sm] : null;
                            var t = m ? m.mainTexture as Texture2D : null;
                            Debug.Log($"NFSMW world: bld sub {sm} tris {tris.Count / 3} vcolA {amin}..{amax} mat {m?.name} shader {m?.shader.name} cutoff {(m && m.HasProperty("_Cutoff") ? m.GetFloat("_Cutoff") : -9)} cull {(m && m.HasProperty("_Cull") ? m.GetFloat("_Cull") : -9)} tex {(t ? t.name + " " + t.format : "none")}");
                        }
                        break;
                    }
                if (name == "rosewood")
                {
                    var eye = p + Vector3.up * 3f - fwd * 8f;
                    var up = new Ray(eye, (fwd * 40f + Vector3.up * 30f).normalized);
                    foreach (var r in boot.World.GetComponentsInChildren<Renderer>())
                        if (r.bounds.IntersectRay(up, out float dd) && dd < 600f && r.bounds.size.magnitude > 200f)
                            Debug.Log($"NFSMW world: overhead {r.name} mat {r.sharedMaterial?.name} at {dd:0} m size {r.bounds.size}");
                }
                Shot(cam, p + Vector3.up * 3f - fwd * 8f, p + fwd * 40f + Vector3.up * 6f, $"world_{name}.png");
                Shot(cam, p + Vector3.up * 3f + fwd * 8f, p - fwd * 40f + Vector3.up * 6f, $"world_{name}_back.png");
                if (System.Environment.GetCommandLineArgs().Contains("-nocull"))
                {
                    foreach (var r in boot.World.GetComponentsInChildren<Renderer>())
                        foreach (var m in r.sharedMaterials) if (m && m.HasProperty("_Cull")) m.SetFloat("_Cull", 0);
                    Shot(cam, p + Vector3.up * 3f + fwd * 8f, p - fwd * 40f + Vector3.up * 6f, $"world_{name}_back_nocull.png");
                    Shot(cam, p + Vector3.up * 3f - fwd * 8f, p + fwd * 40f + Vector3.up * 6f, $"world_{name}_nocull.png");
                }
            }
        }

        /// <summary>Batch: preview renders, then the drive test.</summary>
        public static void BatchAll()
        {
            BatchPreview();
            BatchDriveTest();
        }

        /// <summary>Batch check: drives the car with full throttle for a few seconds using manual physics steps.</summary>
        public static void BatchDriveTest()
        {
            CreateScene();
            var boot = Object.FindFirstObjectByType<GameBootstrap>();
            boot.BuildImmediate();
            var car = boot.Player;
            car.UseDeviceInput = false;
            Physics.simulationMode = SimulationMode.Script;
            const float dt = 1f / 60f;
            var start = car.transform.position;
            for (int i = 0; i < 90; i++) { car.Step(dt); Physics.Simulate(dt); } // settle
            float settledY = car.transform.position.y;
            car.Input = new DriveInput { Throttle = 1 };
            float best = 0;
            int maxGear = 0;
            for (int i = 0; i < 60 * 8; i++)
            {
                car.Input.Steer = AutoSteer(car.transform);
                car.Step(dt);
                Physics.Simulate(dt);
                best = Mathf.Max(best, car.SpeedMps);
                maxGear = Mathf.Max(maxGear, car.Gear);
                if (i % 60 == 59)
                {
                    string w = car.DebugWheels();
                    Debug.Log($"NFSMW drive: t={(i + 1) / 60}s speed {car.SpeedMps * 2.237f:0} mph gear {car.Gear} rpm {car.Rpm:0} pos {car.transform.position} wheels {w}");
                }
            }
            var end = car.transform.position;
            Physics.simulationMode = SimulationMode.FixedUpdate; // a project setting: don't leave it on Script
            Debug.Log($"NFSMW drive: start {start} settled y {settledY:0.00} end {end} moved {(end - start).magnitude:0.0} m, top {best * 2.237f:0} mph, gear {maxGear}, rpm {car.Rpm:0}, up {car.transform.up}");
            Directory.CreateDirectory("Preview");
            var cam = Camera.main;
            Shot(cam, end - car.transform.forward * 6.5f + Vector3.up * 2.2f, end + Vector3.up * 0.8f, "drive_end.png");
        }

        /// <summary>Logs the solid, material and texture under a point (for checking what the road is drawn with).</summary>
        static void ProbeSurface(Vector3 at)
        {
            foreach (var hit in Physics.RaycastAll(at + Vector3.up * 3f, Vector3.down, 10f))
            {
                var mc = hit.collider as MeshCollider;
                var mr = hit.collider.GetComponent<MeshRenderer>();
                if (!mc || !mr) continue;
                var mesh = mc.sharedMesh;
                int sub = -1;
                for (int s = 0, tri = hit.triangleIndex * 3; s < mesh.subMeshCount; s++)
                {
                    var d = mesh.GetSubMesh(s);
                    if (tri >= d.indexStart && tri < d.indexStart + d.indexCount) { sub = s; break; }
                }
                var mat = sub >= 0 && sub < mr.sharedMaterials.Length ? mr.sharedMaterials[sub] : null;
                var tex = mat ? mat.mainTexture as Texture2D : null;
                var uv = hit.textureCoord;
                string px = "";
                if (tex)
                {
                    var rt = RenderTexture.GetTemporary(64, 64);
                    Graphics.Blit(tex, rt);
                    var prev = RenderTexture.active;
                    RenderTexture.active = rt;
                    var read = new Texture2D(64, 64, TextureFormat.RGBA32, false);
                    read.ReadPixels(new Rect(0, 0, 64, 64), 0, 0);
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                    var c = read.GetPixelBilinear(uv.x, uv.y);
                    px = $" texel {c}";
                }
                for (int s = 0; s < mr.sharedMaterials.Length; s++)
                {
                    var m2 = mr.sharedMaterials[s];
                    var t2 = m2 ? m2.mainTexture : null;
                    var d2 = mesh.GetSubMesh(s);
                    Debug.Log($"NFSMW probe:    sub {s} tris {d2.indexCount / 3} {(m2 ? m2.shader.name + " q" + m2.renderQueue + " cut " + (m2.HasProperty("_Cutoff") ? m2.GetFloat("_Cutoff") : 0) : "-")} tex {(t2 ? t2.name + " " + t2.width + "x" + t2.height : "NONE " + (m2 ? m2.name : ""))}");
                }
                Debug.Log($"NFSMW probe: {hit.collider.name} sub {sub} mat {(mat ? mat.name + "/" + mat.shader.name : "-")} tex {(tex ? tex.name + " " + tex.format + " " + tex.width + "x" + tex.height : "-")} uv {uv}{px}");
            }
        }

        /// <summary>Test autopilot: steer toward the side with more free space ahead (walls, curbs).</summary>
        static float AutoSteer(Transform t)
        {
            var o = t.position + Vector3.up * 0.6f;
            float Ray(float ang)
            {
                var d = Quaternion.AngleAxis(ang, Vector3.up) * t.forward;
                return Physics.Raycast(o, d, out var h, 60f) ? h.distance : 60f;
            }
            float l = Ray(-25f) + Ray(-12f), r = Ray(25f) + Ray(12f);
            return Mathf.Clamp((r - l) / 40f, -1f, 1f);
        }

        static void Shot(Camera cam, Vector3 from, Vector3 at, string file)
        {
            cam.transform.position = from;
            cam.transform.LookAt(at);
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            cam.Render();
            RenderTexture.active = rt;
            var tex = new Texture2D(1280, 720, TextureFormat.RGB24, false);
            tex.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0);
            tex.Apply();
            File.WriteAllBytes(Path.Combine("Preview", file), tex.EncodeToPNG());
            cam.targetTexture = null;
            RenderTexture.active = null;
            Object.DestroyImmediate(rt);
            Debug.Log("NFSMW preview: wrote Preview/" + file);
        }
    }
}
