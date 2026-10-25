using System;
using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;
using UnityEngine.InputSystem;

namespace NfsMw.UI
{
    /// <summary>
    /// Shared pieces of the career front-end screens (specs/fe_career_screens.md §0): package messages routed through
    /// the objects' MsgR responses, the track-map streamer (§0.2), rival art (§0.3), mouse position in FE space and the
    /// common header texts.
    /// </summary>
    public static class CareerScreenKit
    {
        // script / message hashes (§0)
        public const uint Leave = 0x033113AC, Normal = 0x6EBBFB68, Pulse = 0x03826A28, Snap = 0x001CBF71, UnSnap = 0x7EFE8FF4,
            Flip = 0x0015970A, Pos1 = 0x001B20C2, Pos2 = 0x001B20C3, Read = 0x001C0CBB, TypeIn = 0x5A8E4EBE;

        /// <summary>
        /// Sends a package message: every object with a MsgR response for it plays the response's script when it has
        /// one by that id, and objects owning a script named like the message play it (the packages use both forms).
        /// [?] Only the "play script" response command is modelled; command lists that forward other messages are not.
        /// </summary>
        public static bool Message(FEView view, uint msg)
        {
            if (view == null) return false;
            bool any = false;
            foreach (var n in view.Nodes)
            {
                bool done = false;
                foreach (var r in n.Obj.Responses)
                    if (r.msg == msg && n.Obj.Script(r.target) != null) { view.Play(n, r.target); done = any = true; }
                if (!done && n.Obj.Script(msg) != null) { view.Play(n, msg); any = true; }
            }
            return any;
        }

        /// <summary>Sets the text of an object and of every string under it (a label and its drop shadow).</summary>
        public static void Text(FEView view, uint hash, string text)
        {
            var n = view.Find(hash);
            if (n == null) return;
            if (n.Label != null) view.SetText(n, text);
            foreach (var d in view.Descendants(n)) if (d.Label != null) view.SetText(d, text);
        }

        public static void Text(FEView view, string name, string text) => Text(view, FrontEndData.Hash(name), text);

        /// <summary>Sets the text of every object with this hash (the packages repeat hashes for shadow copies).</summary>
        public static void TextAll(FEView view, uint hash, string text)
        {
            foreach (var n in view.Nodes)
                if (n.Obj.Hash == hash)
                {
                    if (n.Label != null) view.SetText(n, text);
                    foreach (var d in view.Descendants(n)) if (d.Label != null) view.SetText(d, text);
                }
        }

        /// <summary>Plays a script on an object and makes it visible (packages often start objects hidden).</summary>
        public static void Play(FEView view, uint obj, uint script, bool show = true)
        {
            var n = view.Find(obj);
            if (n == null) return;
            if (show) view.SetVisible(n, true);
            view.Play(n, script);
        }

        /// <summary>A localised label (English.bin), falling back to the given English text.</summary>
        public static string L(uint label, string english) => FrontEndData.Text(label, english);
        public static string L(string label, string english = null)
        {
            var s = FrontEndData.Strings?.Get(label);
            return s ?? english ?? label;
        }

        /// <summary>printf-style "%$d" (thousands separators), "%d", "%s" — enough for the label formats used here.</summary>
        public static string Format(string fmt, params object[] args)
        {
            var sb = new System.Text.StringBuilder();
            int a = 0;
            for (int i = 0; i < fmt.Length; i++)
            {
                char c = fmt[i];
                if (c != '%' || i + 1 >= fmt.Length) { sb.Append(c == '^' ? '\n' : c); continue; }
                int j = i + 1;
                if (fmt[j] == '%') { sb.Append('%'); i = j; continue; }
                bool sep = false;
                if (fmt[j] == '$') { sep = true; j++; }
                int prec = -1;
                while (j < fmt.Length && (char.IsDigit(fmt[j]) || fmt[j] == '.'))
                {
                    if (fmt[j] == '.') prec = 0;
                    else if (prec >= 0) prec = prec * 10 + (fmt[j] - '0');
                    j++;
                }
                if (j >= fmt.Length) break;
                char t = fmt[j];
                object v = a < args.Length ? args[a++] : "";
                switch (t)
                {
                    // bPrintf (bPrintf.cpp:385 / :528): '$' groups with the language's locale (Core/LangLocalize.cs)
                    case 'd': case 'i': case 'u': { long n = Convert.ToInt64(v); sb.Append(sep ? Localize.Group(n) : n.ToString(System.Globalization.CultureInfo.InvariantCulture)); break; }
                    case 'f': sb.Append(Localize.Fixed(Convert.ToDouble(v), prec < 0 ? 6 : prec, sep)); break;
                    default: sb.Append(v); break;
                }
                i = j;
            }
            return sb.ToString();
        }

        /// <summary>Absolute FE position of an object (sum of its parents' positions; the career packages' groups are unscaled).</summary>
        public static Vector2 AbsPos(FEView.Node n)
        {
            var p = Vector2.zero;
            for (var x = n; x != null; x = x.Parent) p += new Vector2(x.Data.X, x.Data.Y);
            return p;
        }

        /// <summary>Moves an object so its absolute FE position is p.</summary>
        public static void SetAbsPos(FEView.Node n, Vector2 p)
        {
            var parent = n.Parent != null ? AbsPos(n.Parent) : Vector2.zero;
            n.Data.X = p.x - parent.x;
            n.Data.Y = p.y - parent.y;
        }

        /// <summary>The mouse pointer in a view's FE space (640x480, origin at the centre, +Y down); null without a mouse.</summary>
        public static Vector2? Mouse(FEView view)
        {
            var m = UnityEngine.InputSystem.Mouse.current;
            if (m == null || view == null) return null;
            var canvas = view.GetComponentInParent<Canvas>();
            var cam = canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle((RectTransform)view.transform, m.position.ReadValue(), cam, out var local)) return null;
            return new Vector2(local.x, -local.y);
        }

        /// <summary>Pixel movement of the mouse this frame converted to FE units.</summary>
        public static Vector2 MouseDelta(FEView view)
        {
            var m = UnityEngine.InputSystem.Mouse.current;
            if (m == null) return Vector2.zero;
            float scale = 480f / Mathf.Max(1, Screen.height);
            var d = m.delta.ReadValue() * scale;
            return new Vector2(d.x, -d.y);
        }

        public static bool KeyPressed(Key k) => Keyboard.current != null && Keyboard.current[k].wasPressedThisFrame;

        /// <summary>
        /// CareerSettings.CaseFileName (FEDatabase.cpp:433): "MW-%d-%.4sXXX", number 1069..7663, the first four letters of
        /// the profile name upper-cased. Made once per career and kept in the rap sheet record.
        /// </summary>
        public static string CaseFile()
        {
            var p = GameSession.Profile;
            if (p == null) return "MW-0000-XXXX";
            var r = p.RapSheet ??= new RapSheetRecord();
            if (string.IsNullOrEmpty(r.CaseFile))
            {
                string name = (p.Name ?? "").ToUpperInvariant();
                r.CaseFile = $"MW-{UnityEngine.Random.Range(0x42D, 0x42D + 0x19B3)}-{(name.Length > 4 ? name.Substring(0, 4) : name)}XXX";
            }
            // bSNPrintf(CaseFileName, 13, …) (PC 0x57F770): at most 12 characters — "MW-nnnn-NAME" (the XXX only shows for
            // names shorter than four letters); older port saves kept up to 16
            if (r.CaseFile.Length > 12) r.CaseFile = r.CaseFile.Substring(0, 12);
            return r.CaseFile;
        }

        /// <summary>Header lines shared by the Blacklist screens: 0xB514E2D8 "Bounty: %$d" and 0xF91A59F6 "Cash: %$d".</summary>
        public static void BountyCash(FEView view)
        {
            var p = GameSession.Profile;
            if (p == null) return;
            Text(view, 0xB514E2D8, L(0xCE6B99B1, "Bounty:") + " " + Localize.Group(p.Bounty));
            Text(view, 0xF91A59F6, L(0x073B79E0, "Cash:") + " " + Localize.Group(p.Cash));
        }

        // ---- rival art (uiRepSheetRivalStreamer.cpp) -------------------------------------------------------------

        /// <summary>
        /// Rival photo 0xC1F62308, tag 0xF5A2A087 and background 0x2CBE1DD0 from GLOBAL/RIVALS.BIN for Blacklist bin
        /// (in-game packages use the _IG variants), then the package's "loaded" messages 0xC0942E85 / 0x8C9D4547 / 0xD22B95D0.
        /// </summary>
        public static void RivalArt(FEView view, int bin, bool inGame)
        {
            string ig = inGame ? "_IG" : "";
            // uiRepSheetRivalStreamer::Init hides the images; TexturesLoadedCallback (all three loaded) posts the message, sets the
            // texture and shows each one (the port's RIVALS.BIN textures are resident)
            void Set(uint obj, string tex, uint msg)
            {
                var t = FrontEndData.TextureByName(tex) ?? FrontEndData.TextureByName(tex.Replace("_IG", ""));
                foreach (var n in view.Nodes)
                    if (n.Obj.Hash == obj)
                    {
                        view.SetVisible(n, false);
                        if (t != null) { view.SetTexture(n, t); view.SetVisible(n, true); }
                    }
                Message(view, msg);
            }
            Set(0xC1F62308, $"RIVAL_{bin:00}{ig}", 0xC0942E85);
            Set(0xF5A2A087, $"RIVAL_{bin:00}{ig}_GRAF", 0x8C9D4547);
            Set(0x2CBE1DD0, $"RIVAL_{bin:00}{ig}_BG", 0xD22B95D0);
        }

        /// <summary>A rival's language label: BLACKLIST_RIVAL_%02d_NAME / _AKA / _BIO / _CAR / _STRENGTH.</summary>
        public static string Rival(int bin, string field) => L($"BLACKLIST_RIVAL_{bin:00}_{field}", "");

        // ---- track map (quickrace/uiTrackMapStreamer.cpp) ---------------------------------------------------------

        static readonly Dictionary<string, Texture2D> TrackMaps = new Dictionary<string, Texture2D>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<string> TrackMapsTried = new HashSet<string>();

        /// <summary>
        /// A texture of TRACKS/L2RA/TrackMaps.bin: TRACK_MAP (whole city), TRACK_MAP_UNLOCK_1 / _2 (regions still locked).
        /// The 63 MB pack is read once and only the requested maps are kept.
        /// </summary>
        public static Texture2D TrackMap(string name)
        {
            if (TrackMaps.TryGetValue(name, out var t) && t) return t;
            if (!TrackMapsTried.Add(name.ToUpperInvariant())) return null;
            try
            {
                GameData.Mount();
                var path = Path.Combine(GameData.Root, "TRACKS", "L2RA", "TrackMaps.bin");
                if (!File.Exists(path)) return null;
                var d = GameFiles.ReadBundle(path);
                foreach (var g in TexturePack.ReadAll(d, 0, d.Length))
                {
                    var n = g.Name.ToUpperInvariant();
                    if (n != "TRACK_MAP" && n != "TRACK_MAP_UNLOCK_1" && n != "TRACK_MAP_UNLOCK_2" && n != name.ToUpperInvariant()) continue;
                    var tex = TextureLibrary.ToUnity(g);
                    tex.wrapMode = TextureWrapMode.Clamp;
                    TrackMaps[n] = tex;
                }
            }
            catch (Exception e) { Debug.LogWarning("NFSMW track map: " + e.Message); }
            return TrackMaps.TryGetValue(name, out t) ? t : null;
        }

        /// <summary>uiWorldMap.cpp:844: TRACK_MAP_UNLOCK_1 while bins 16..13 (Camden and Rockport closed), _2 for 12..9, else TRACK_MAP.</summary>
        public static string RegionMapName()
        {
            int bin = GameSession.Mode == GameMode.Career ? GameSession.Profile?.Bin ?? 0 : 0;
            return bin >= 13 ? "TRACK_MAP_UNLOCK_1" : bin > 8 ? "TRACK_MAP_UNLOCK_2" : "TRACK_MAP";
        }

        /// <summary>
        /// World (game X, Y) → 0..1 over the track map. [?] The TrackInfo calibration (+0xAC upper-left, +0xB4 width) is not
        /// parsed; this is the HUD minimap fit (u·1024 = X·0.1538 + 188, v·1024 = −Y·0.1538 + 779.3), assuming TRACK_MAP
        /// covers the same area as MINI_MAP (spec §1.3).
        /// </summary>
        public static Vector2 MapUV(Vector3 unityPos)
        {
            var p = Minimap.MapPoint(unityPos);
            return p / 1024f;
        }
    }

    /// <summary>
    /// UITrackMapStreamer (§0.2): Zoom = visible fraction of the texture (1 = all), Pan = UV centre; ZoomTo / PanTo run a
    /// cubic Hermite from (value, velocity) to (target, 0) over Duration seconds; the TRACK_MAP multi-image gets
    /// TL = pan − zoom/2, BR = pan + zoom/2.
    /// </summary>
    public sealed class TrackMapStreamer
    {
        sealed class Cubic
        {
            public Vector2 Value, Velocity, From, FromVel, To;
            public float T = 1, Duration = 1;
            public bool Moving => T < Duration;

            public void Snap(Vector2 v) { Value = To = v; Velocity = Vector2.zero; T = Duration; }

            public void Go(Vector2 target, float duration)
            {
                From = Value; FromVel = Velocity; To = target; Duration = Mathf.Max(0.01f, duration); T = 0;
            }

            public void Step(float dt)
            {
                if (!Moving) return;
                T = Mathf.Min(Duration, T + dt);
                float s = T / Duration, s2 = s * s, s3 = s2 * s;
                float h00 = 2 * s3 - 3 * s2 + 1, h10 = s3 - 2 * s2 + s, h01 = -2 * s3 + 3 * s2;
                float d00 = 6 * s2 - 6 * s, d10 = 3 * s2 - 4 * s + 1, d01 = -6 * s2 + 6 * s;
                Value = h00 * From + h10 * Duration * FromVel + h01 * To;
                Velocity = (d00 * From + d10 * Duration * FromVel + d01 * To) / Duration;
                if (!Moving) Velocity = Vector2.zero;
            }
        }

        readonly Cubic _zoom = new Cubic(), _pan = new Cubic();
        public FEView View;
        public FEView.Node Map;
        public float Duration = 1f;

        public TrackMapStreamer(FEView view, FEView.Node map, string texture)
        {
            View = view;
            Map = map;
            _zoom.Snap(Vector2.one);
            _pan.Snap(new Vector2(0.5f, 0.5f));
            var tex = CareerScreenKit.TrackMap(texture) ?? CareerScreenKit.TrackMap("TRACK_MAP");
            if (map != null && tex != null)
            {
                foreach (var n in view.Nodes) if (n == map) { view.SetTexture(n, tex); if (n.Mask) n.Image.material = null; }
                view.SetVisible(map, true);
            }
            Apply();
        }

        public bool Zooming => _zoom.Moving;
        public float Zoom => _zoom.Value.x;
        /// <summary>GetZoomFactor() = 1 / zoom.</summary>
        public float Factor => 1f / Mathf.Max(0.01f, _zoom.Value.x);
        /// <summary>GetPan() = pan − 0.5.</summary>
        public Vector2 Pan => _pan.Value - new Vector2(0.5f, 0.5f);

        public void SetZoom(float z) { _zoom.Snap(new Vector2(z, z)); Apply(); }
        public void SetPan(Vector2 uv) { _pan.Snap(uv); Apply(); }
        public void ZoomTo(float z) => _zoom.Go(new Vector2(z, z), Duration);
        public void PanTo(Vector2 uv) => _pan.Go(uv, Duration);
        public void ResetZoom() => SetZoom(1);
        public void ResetPan() => SetPan(new Vector2(0.5f, 0.5f));

        public void Update(float dt)
        {
            _zoom.Step(dt);
            _pan.Step(dt);
            Apply();
        }

        void Apply()
        {
            if (Map == null) return;
            float h = Mathf.Max(_zoom.Value.x, _zoom.Value.y) * 0.5f;
            var c = _pan.Value;
            Map.Data.U0 = c.x - h; Map.Data.V0 = c.y - h;
            Map.Data.U1 = c.x + h; Map.Data.V1 = c.y + h;
        }
    }
}
