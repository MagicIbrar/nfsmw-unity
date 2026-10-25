using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// Scrolling textures (Ecstasy TextureInfo, Texture.cpp:650; getters at speed.exe 0x6BC5C0..). ScrollType is 1
    /// smooth, 2 snap (time quantised by ScrollTimeStep) or 3 offset/scale. The time step is int16/256 s; speeds,
    /// offsets and scales are int16/1024 (per second, in UV units). GetScroll(time, speed) is the fractional part of
    /// t·speed. Materials using such a texture get that offset in _MainTex_ST every frame.
    /// </summary>
    public sealed class TextureScroll : MonoBehaviour
    {
        sealed class Entry { public Material Mat; public int Type; public float Step, SpeedS, SpeedT, OffsetS, OffsetT, ScaleS = 1, ScaleT = 1; }

        static readonly List<Entry> Entries = new List<Entry>();
        static TextureScroll _instance;

        public static void Register(Material m, GameTexture t)
        {
            if (m == null || t == null || t.ScrollType == 0) return;
            Entries.Add(new Entry
            {
                Mat = m, Type = t.ScrollType, Step = t.ScrollTimeStep / 256f,
                SpeedS = t.ScrollSpeedS / 1024f, SpeedT = t.ScrollSpeedT / 1024f,
                OffsetS = t.OffsetS / 1024f, OffsetT = t.OffsetT / 1024f,
                ScaleS = t.ScaleS != 0 ? t.ScaleS / 1024f : 1f, ScaleT = t.ScaleT != 0 ? t.ScaleT / 1024f : 1f,
            });
            if (!_instance && Application.isPlaying)
            {
                var go = new GameObject("Texture Scroll");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<TextureScroll>();
            }
        }

        /// <summary>[ours] A copy of a scrolling material (shadow-caster / reflection copies) scrolls like its source.</summary>
        public static void Mirror(Material source, Material copy)
        {
            if (source == null || copy == null) return;
            for (int i = 0, n = Entries.Count; i < n; i++)
            {
                var e = Entries[i];
                if (e.Mat != source) continue;
                Entries.Add(new Entry
                {
                    Mat = copy, Type = e.Type, Step = e.Step, SpeedS = e.SpeedS, SpeedT = e.SpeedT,
                    OffsetS = e.OffsetS, OffsetT = e.OffsetT, ScaleS = e.ScaleS, ScaleT = e.ScaleT,
                });
                return;
            }
        }

        /// <summary>TextureInfo::GetScroll: snap mode steps the time; the result is frac(t·speed).</summary>
        static float Scroll(float time, float speed, int type, float step)
        {
            if (type == 0) return 0;
            float t = type == 2 && step > 0 ? Mathf.Floor(time / step) : time;
            float p = t * speed;
            return p - (int)p;
        }

        void Update()
        {
            float time = Time.time;
            for (int i = Entries.Count - 1; i >= 0; i--)
            {
                var e = Entries[i];
                if (!e.Mat) { Entries.RemoveAt(i); continue; }
                if (e.Type == 3)
                {
                    e.Mat.mainTextureScale = new Vector2(e.ScaleS, e.ScaleT);
                    e.Mat.mainTextureOffset = new Vector2(e.OffsetS + Scroll(time, e.SpeedS, 1, e.Step), e.OffsetT + Scroll(time, e.SpeedT, 1, e.Step));
                }
                else e.Mat.mainTextureOffset = new Vector2(Scroll(time, e.SpeedS, e.Type, e.Step), Scroll(time, e.SpeedT, e.Type, e.Step));
            }
        }
    }
}
