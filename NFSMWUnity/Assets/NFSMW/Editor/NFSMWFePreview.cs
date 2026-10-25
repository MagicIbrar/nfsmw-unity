using System.IO;
using NfsMw.UI;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace NfsMw.EditorTools
{
    /// <summary>Batch check of the FEng renderer: draws packages to Preview/fe_*.png (env NFSMW_FE = comma list).</summary>
    public static class NFSMWFePreview
    {
        public static void Render()
        {
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var list = System.Environment.GetEnvironmentVariable("NFSMW_FE");
            var names = string.IsNullOrEmpty(list)
                ? new[] { "MainMenu", "PressStart", "GarageMain", "Car_Select", "MyCarsManager", "CustomizeMain", "SafeHouseRaceSheet", "WorldMapMain", "RapSheetMain" }
                : list.Split(',');
            var camGo = new GameObject("FE Camera");
            var cam = camGo.AddComponent<Camera>();
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.12f, 0.13f, 0.12f);
            var rt = new RenderTexture(1280, 720, 24);
            cam.targetTexture = rt;
            Directory.CreateDirectory("Preview");
            FrontEndData.Load();
            foreach (var name in names)
            {
                var canvas = FrontEndCanvas.Create("FE", cam);
                FEView view;
                try { view = FEView.Open(name, canvas.transform); }
                catch (System.Exception e) { Debug.LogWarning("NFSMW FE: " + name + ": " + e.Message); Object.DestroyImmediate(canvas.gameObject); continue; }
                for (int i = 0; i < 60; i++) view.Apply(1f / 30f); // let INIT/intro scripts finish
                Canvas.ForceUpdateCanvases();
                cam.Render();
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                File.WriteAllBytes($"Preview/fe_{name}.png", tex.EncodeToPNG());
                int named = 0, shown = 0;
                foreach (var n in view.Nodes)
                {
                    if (FrontEndData.Names.ContainsKey(n.Obj.Hash)) named++;
                    if ((n.Image && n.Image.enabled) || (n.Label && n.Label.enabled)) shown++;
                }
                if (name == names[0])
                    foreach (var n in view.Nodes)
                        if (n.Label)
                            Debug.Log($"NFSMW FE text: '{n.Label.Text}' enabled {n.Label.enabled} col {n.Label.color} font {n.Label.Font?.Name} atlas {n.Label.mainTexture?.name} {n.Label.mainTexture?.width} pos {n.Rect.anchoredPosition} scale {n.Label.Scale} verts {n.Label.canvasRenderer.GetMesh()?.vertexCount}");
                Debug.Log($"NFSMW FE: {name} objects {view.Nodes.Count} drawn {shown} named {named}");
                Object.DestroyImmediate(canvas.gameObject);
            }
        }
    }
}
