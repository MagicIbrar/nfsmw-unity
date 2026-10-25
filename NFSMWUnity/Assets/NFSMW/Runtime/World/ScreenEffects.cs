using NfsMw.Core;
using UnityEngine;

namespace NfsMw.World
{
    /// <summary>
    /// World/ScreenEffects.cpp as PC v1.3 runs it, for the player view (eView 1, ScreenEffectDB at +0x64):
    ///   ePreDisplay 0x504C50: TickSFX 0x72CC50, DoTunnelBloom 0x72AAC0, DoTinting 0x725580 (both views)
    ///   ePostDisplay 0x504D00: UpdateAllScreenEFX 0x72AA70 (Update(1/30): one-frame effects expire)
    /// What the PC draws from it:
    ///   SE_TINT (DoTinting: precip fog colour x cloud amount, and Rain's paused branch) - read only by sub_6D44B0 behind
    ///     [0x982B29] = active AND [0x901804], and 0x901804 is only ever written 0: never drawn.
    ///   SE_GLARE (DoTunnelBloom: tunnel-exit bloom) - eDisplayFrame 0x6DEFCE draws the glare quad (data[2..13], white,
    ///     worldmin.efx = COLORWRITEENABLE alpha only) into the back-buffer alpha, and the overbright pass sub_6DC040
    ///     sets its blend factor to intensity x [0x8FAE64] x 200 while the glare is active (else 0.8 x 255; 0.6 in the
    ///     front end); sub_6C3870 lerps 0.2..1 by it and sub_6D2CD0 scales by (1 - intensity). The overbright pass is not
    ///     ported, so the glare is computed and published (<see cref="Glare"/>) but not drawn [hook for the overbright port].
    ///   SE_MOTION_BLUR / SE_VISUAL_SIG / SE_FE_BLUR: no PC reader (GarageMainScreen adds FE ones).
    /// No fades or flashes come from here on PC (the camera-flash palette is reached only through the debug flag
    /// debugflash [0x9A3AB8]).
    /// </summary>
    public static class ScreenEffects
    {
        public static readonly ScreenEffectDB Player = new ScreenEffectDB();

        /// <summary>The tunnel glare for the overbright pass: active this frame, intensity and the quad (Unity space,
        /// bottom a, bottom b, top a, top b = data[2..4], [5..7], [8..10], [11..13]).</summary>
        public struct GlareState { public bool Active; public float Intensity; public Vector3 A, B, C, D; }
        public static GlareState Glare
        {
            get
            {
                var d = Player.Data[(int)ScreenEffectType.Glare];
                return new GlareState
                {
                    Active = Player.IsActive(ScreenEffectType.Glare), Intensity = d.Intensity,
                    A = Coords.ToUnity(d.Data[2], d.Data[3], d.Data[4]), B = Coords.ToUnity(d.Data[5], d.Data[6], d.Data[7]),
                    C = Coords.ToUnity(d.Data[8], d.Data[9], d.Data[10]), D = Coords.ToUnity(d.Data[11], d.Data[12], d.Data[13]),
                };
            }
        }

        const float GlareFalloff = 0.02f, GlareFallon = 0.04f, TunnelHeight = 10f;   // [0x9031DC], [0x9031E0], [0x9031F0]
        static TrackPathZone _zoneB;            // zoneB [0x9A3A24]
        static GenericRegion _regionB;          // regionB [0x9AE328]
        static Vec3 _camInside;                 // lcamPosInside [0x9AE330]
        static readonly float[] _backup = new float[12];   // dataBackup [0x9AE2C8]
        static int _lastTickFrame = -10;

        /// <summary>ePreDisplay's part: TickSFX (an update only when ePostDisplay did not run last frame), then the bloom
        /// and the tint for the player view.</summary>
        public static void PreDisplay(in PrecipitationInput f, Precipitation precip)
        {
            int frame = Time.frameCount;
            if (_lastTickFrame != frame - 1) Player.Update(1f / 30f);   // [0x3D088889]; ticS: only after a frame without a tick
            _lastTickFrame = frame;
            DoTunnelBloom(f, precip);
            DoTinting(precip);
        }

        /// <summary>ePostDisplay: UpdateAllScreenEFX.</summary>
        public static void PostDisplay() => Player.Update(1f / 30f);

        /// <summary>DoTinting 0x725580: while the view's cloud amount is above 0, SE_TINT = precip fog colour, 128, cloud.</summary>
        static void DoTinting(Precipitation p)
        {
            float cloud = p != null ? p.Cloud : 0f;
            if (!(cloud > 0f)) return;
            var def = new ScreenEffectDef { R = p.FogR, G = p.FogG, B = p.FogB, A = 128f, Intensity = cloud };
            Player.AddScreenEffect(ScreenEffectType.Tint, def, true, ScreenEffectControl.Frame);
        }

        /// <summary>
        /// DoTunnelBloom 0x72AAC0 (PC): inside a tunnel zone above the camera mover's car, the nearest BLOOM region ahead
        /// within 40 m of a zone edge puts a 10 m high quad on that edge at the ground height; its glare (data[1]) ramps
        /// +0.04 per frame to 1, and scales by distance / 17 within 17 m of the edge's line (PC: x 0.0588235, the GameCube
        /// source has / 1.7). Changing region or zone restarts the ramp. Raining: the quad also becomes the rain curtain
        /// (state 3). Outside: the glare fades 0.02 per frame, its quad following the camera's motion.
        /// </summary>
        static void DoTunnelBloom(in PrecipitationInput f, Precipitation precip)
        {
            if (!f.HasCar || f.FindZone == null) return;
            float px = f.CamPos.X, py = f.CamPos.Y;
            TrackPathZone zone = _zoneB != null && _zoneB.Contains(px, py) ? _zoneB : f.FindZone(TrackPathZones.Tunnel, px, py);
            var def = new ScreenEffectDef { R = 128f, G = 128f, B = 128f, A = 128f };
            if (zone == null || zone.Elevation <= f.CarPos.Z)
            {
                float i = Player.GetIntensity(ScreenEffectType.Glare);
                if (!(i > 0f)) return;
                def.Intensity = i - GlareFalloff;
                def.Data[1] = def.Intensity;
                float tx = f.CamPos.X - _camInside.X + f.CamDir.X, ty = f.CamPos.Y - _camInside.Y + f.CamDir.Y, tz = f.CamPos.Z - _camInside.Z + f.CamDir.Z;
                if (!(def.Intensity > 0f)) return;
                for (int k = 0; k < 4; k++)
                {
                    def.Data[2 + k * 3] = tx + _backup[k * 3];
                    def.Data[3 + k * 3] = ty + _backup[k * 3 + 1];
                    def.Data[4 + k * 3] = tz + _backup[k * 3 + 2];
                }
                Player.AddScreenEffect(ScreenEffectType.Glare, def, true, ScreenEffectControl.Frame);
                return;
            }
            _camInside = f.CamPos;
            var region = WeatherRegions.ClosestBloomInView(WeatherMan.Regions, f.CamPos, f.CamDir, out _);
            if (region == null) return;
            float len = ZoneSegments.GetSegmentNextTo(zone, region.X, region.Y, out float p0x, out float p0y, out float p1x, out float p1y);
            if (len == -1f || !(len < 40f)) return;
            float h = GroundHeight(region.X, region.Y, region.Z);      // WCollisionMgr::GetWorldHeightAtPointRigorous [?: raycast]
            var c = f.CamDir;
            float[] q =
            {
                p0x + c.X, p0y + c.Y, h + c.Z, p1x + c.X, p1y + c.Y, h + c.Z,
                p0x + c.X, p0y + c.Y, h + TunnelHeight + c.Z, p1x + c.X, p1y + c.Y, h + TunnelHeight + c.Z,
            };
            for (int k = 0; k < 12; k++) { def.Data[2 + k] = q[k]; _backup[k] = q[k]; }
            if (_regionB != region) Player.SetData(ScreenEffectType.Glare, 0f, 1);
            _regionB = region;
            if (_zoneB != zone) Player.SetData(ScreenEffectType.Glare, 0f, 1);
            _zoneB = zone;
            float nx = p1y - p0y, ny = -(p1x - p0x), nl = Mathf.Sqrt(nx * nx + ny * ny);
            if (nl > 0f) { nx /= nl; ny /= nl; }
            float d = Mathf.Abs(ny * (p0y - py) + (p0x - px) * nx);
            float prev = Player.GetData(ScreenEffectType.Glare, 1);
            def.Data[1] = d >= 17f ? (prev >= 1f ? 1f : GlareFallon + prev) : d * 0.05882353f * prev;
            def.Intensity = def.Data[1];
            Player.AddScreenEffect(ScreenEffectType.Glare, def, true, ScreenEffectControl.Frame);
            if (precip != null && precip.Rain > 0f)
            {
                precip.CurtainState = 3;
                precip.AttachRainCurtain(q[6], q[7], q[8], q[9], q[10], q[11], q[0], q[1], q[2], q[3], q[4], q[5]);
            }
        }

        static float GroundHeight(float x, float y, float z)
        {
            var from = Coords.ToUnity(x, y, z + 50f);
            return Physics.Raycast(from, Vector3.down, out var hit, 200f, ~0, QueryTriggerInteraction.Ignore) ? hit.point.y : z;
        }
    }
}
