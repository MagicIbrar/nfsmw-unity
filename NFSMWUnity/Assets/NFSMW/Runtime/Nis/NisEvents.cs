using System;
using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Effects;
using NfsMw.UI;
using UnityEngine;
using UnityEngine.UI;

namespace NfsMw.Game
{
    /// <summary>
    /// Runs a NIS's event sequencer (chunk 0x3B811, <see cref="EventSeqEngine"/>) on the NIS clock and carries out the
    /// events it fires. NISActivity::StartEvents resets the engine and sends "START" (0xA6A56EA1); the camera fires
    /// "ICECut%d" tags at each ICE cut and ICECompleteEventTags (0x47B520) fires the rest when the NIS is skipped;
    /// SkipOverNIS completes the sequencer. NISActivity::SetDynamicData binds a system to the car of the same channel
    /// ("car1", "cop2": system id = lookup2 hash of the channel name), and "GLOBAL" to the activity.
    /// Event effects follow the decomp's Generated/Events StaticData layouts; see specs/nis.md for what is ported.
    /// </summary>
    public sealed class NisEventRunner
    {
        public readonly EventSeqEngine Engine;
        readonly NisPlayer _player;
        readonly Dictionary<uint, string> _channelOf = new Dictionary<uint, string>();
        readonly Dictionary<string, CarTweak> _tweaks = new Dictionary<string, CarTweak>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, int> Unhandled = new Dictionary<string, int>();
        public int Fired;

        // FacePixelation (World/FacePixelate.cpp; ENISPixelate ctor 0x6231F0 sets mPixelationOn 0x9B0A74, mWidth 0x904B3C, mHeight 0x904B40)
        public bool PixelateOn;
        public float PixelWidth = 16f, PixelHeight = 16f;
        float _flash, _flashLength;

        /// <summary>
        /// ENISVisualLook (ctor 0x6236C0): IVisualTreatment +0x1DC / +0x1E0 = fDesaturation / fColourBloom, which
        /// IVisualTreatment::UpdateHeat (0x74BFBE) uses in place of the heat look's values while ≥ 0 (−1 = no override)
        /// [hook: the visual-treatment owner reads these]. fBleachFactor goes to nullsub_166 (no effect on PC), fUseHDR is
        /// not read.
        /// </summary>
        public static float VisualLookDesaturation = -1f, VisualLookColourBloom = -1f;

        /// <summary>
        /// ENISVisualLook's fFacingScale / fGrazingScale: eLightMaterial bStringHash 0x93C0EAF6 (elGetLightMaterial
        /// 0x502820) +0x90 / +0x80 while the value is in 0..2, else the saved originals (−1 = no override)
        /// [hook: light-material owner].
        /// </summary>
        public static float VisualLookFacingScale = -1f, VisualLookGrazingScale = -1f;

        /// <summary>ENISRoadNoise (0x62E9F0): Tweak_DisableRoadNoise (0x9B09B0) = fDisableRoadNoise [hook: audio owner].</summary>
        public static int DisableRoadNoise;

        /// <summary>ENISMotionBlur (0x62E320): ____icemanager_isplayingsomething (0x8F9B28) = fEnableMotionBlur != 0 [hook: renderer].</summary>
        public static bool IceManagerIsPlayingSomething = true;   // .data 0x8F9B28 = 1 (motion-blur gate of sub_6DBB20)
        Image _flashImage;
        GameObject _pixelQuad;

        /// <summary>SuspensionSpline NIS tweaks (NISCarTweaks 0x690EA0): pitch/roll pulses and the shake oscillator of one car.</summary>
        sealed class CarTweak
        {
            public float PitchTime, PitchAmp, PitchT, Pitch;
            public float RollTime, RollAmp, RollT, Roll;
            public float ShakeTime, ShakeRate, ShakeRamp, ShakeAmp, ShakeT, Shake;
        }

        public NisEventRunner(NisPlayer player, EventSeqEngineData data, float time)
        {
            _player = player;
            for (int i = 1; i <= 8; i++)
            {
                _channelOf[AttribDatabase.Hash("car" + i)] = "Car" + i;
                _channelOf[AttribDatabase.Hash("cop" + i)] = "Cop" + i;
            }
            Engine = new EventSeqEngine(data, time) { OnEvent = Handle };
        }

        /// <summary>NISActivity::StartEvents: Reset(Sim::GetTime) then ProcessStimulus("START", QUEUE_ALLOW).</summary>
        public void Start(float time)
        {
            Engine.Reset(time);
            Engine.ProcessStimulus(SeqIds.StartStimulus, time, EventSeqEngine.QueueMode.Allow);
        }

        public void FireTag(string tag) => Engine.FireEventTag(AttribDatabase.Hash(tag));

        public void Update(float time, float dt)
        {
            Engine.Update(time);
            foreach (var t in _tweaks.Values) Advance(t, dt);
            UpdateOverlays(dt);
        }

        /// <summary>SkipOverNIS: mSequencer->Complete(Sim::GetTime(), true).</summary>
        public void Complete(float time) => Engine.Complete(time, true);

        /// <summary>Extra body rotation (Unity, car local) for a NIS car channel ("Car1", "Cop2").</summary>
        public Quaternion BodyTweak(string channel)
        {
            if (channel == null || !_tweaks.TryGetValue(channel, out var t)) return Quaternion.identity;
            // [?] the outputs drive the body through SuspensionSpline::SetNISPosition (0x691110), not decoded: taken as
            // radians of pitch (nose up) and roll, the shake as an extra roll oscillation
            return Quaternion.Euler(-t.Pitch * Mathf.Rad2Deg, 0, (t.Roll + t.Shake) * Mathf.Rad2Deg);
        }

        static void Advance(CarTweak t, float dt)
        {
            // pitch / roll: a triangle pulse peaking at amp / 2 halfway through, then cleared (0x690EB6..0x690F9A)
            Pulse(ref t.PitchTime, ref t.PitchT, t.PitchAmp, ref t.Pitch, dt);
            Pulse(ref t.RollTime, ref t.RollT, t.RollAmp, ref t.Roll, dt);
            // shake: sin(2 pi phase) * amp over whole periods of `rate` seconds, ramped in and out over `ramp` (0x690FB3..)
            if (t.ShakeTime != 0 && t.ShakeRate != 0)
            {
                t.ShakeT += dt;
                if (t.ShakeT < t.ShakeTime)
                {
                    float inv = 1f / t.ShakeRate;
                    float periods = (int)(inv * t.ShakeTime), k = (int)(t.ShakeT * inv), ramp = (int)(inv * t.ShakeRamp + 0.99f);
                    float phase = (t.ShakeT - k * t.ShakeRate) * inv;
                    float env = 1;
                    if (ramp > 0)
                    {
                        if (k < ramp) env = k / ramp;
                        else if (periods - ramp < k) env = (periods - k) / ramp;
                    }
                    t.Shake = Mathf.Sin(phase * 2 * Mathf.PI) * t.ShakeAmp * env;
                }
                else { t.Shake = 0; t.ShakeAmp = 0; t.ShakeRamp = 0; t.ShakeTime = 0; }
            }
        }

        static void Pulse(ref float time, ref float elapsed, float amp, ref float output, float dt)
        {
            if (time == 0) return;
            elapsed += dt;
            if (elapsed < time)
            {
                float h = time * 0.5f, v = elapsed * 0.5f;
                if (elapsed > h) v = h - v;
                output = v / h * amp;
            }
            else { output = 0; time = 0; }
        }

        CarTweak Tweak(string channel)
        {
            if (!_tweaks.TryGetValue(channel, out var t)) _tweaks[channel] = t = new CarTweak();
            return t;
        }

        void Handle(uint system, SeqEvent e)
        {
            Fired++;
            _channelOf.TryGetValue(system, out string channel);
            if (NisCarControl.HandleEvent(_player, channel, e)) return;   // INISCarControl / INISCarEngine events (Runtime/Physics/NisCarControl.cs)
            switch (e.Name)
            {
                case "ENISCarPitch":
                    if (channel == null) break;
                    { var t = Tweak(channel); t.PitchAmp = e.F(4); t.PitchTime = e.F(8); t.PitchT = 0; }
                    return;
                case "ENISCarRoll":
                    if (channel == null) break;
                    { var t = Tweak(channel); t.RollAmp = e.F(4); t.RollTime = e.F(8); t.RollT = 0; }
                    return;
                case "ENISCarShake":
                    if (channel == null) break;
                    { var t = Tweak(channel); t.ShakeAmp = e.F(4); t.ShakeRate = e.F(8); t.ShakeRamp = e.F(12); t.ShakeTime = e.F(16); t.ShakeT = 0; }
                    return;
                case "ENISLights":
                {
                    // fCopLights, fHeadLights for the system's car
                    var car = _player.CarOf(channel);
                    if (car && car.TryGetComponent<CarFlares>(out var flares))
                    {
                        flares.CopLightsOn = e.U(4) != 0;
                        flares.Headlights = e.U(8) != 0;
                    }
                    if (car) _player.SetLightBar(car.transform, e.U(4) != 0);
                    return;
                }
                case "ENISOverlayMessage":
                {
                    string package = e.S(4) ?? "";
                    if (package.EndsWith(".fng", StringComparison.OrdinalIgnoreCase)) package = package.Substring(0, package.Length - 4);
                    _player.ShowOverlayPackage(package, e.S(8));
                    return;
                }
                case "ENISHideCharacter":
                    _player.ShowCharacter(e.S(4), e.I(8) != 0);
                    return;
                case "ENISPixelate":
                    PixelateOn = e.I(4) != 0;
                    if (PixelateOn) { PixelWidth = e.F(8); PixelHeight = e.F(12); }
                    return;
                case "ENISScreenFlash":
                    _flashLength = Mathf.Max(0.01f, e.F(4));
                    _flash = _flashLength;
                    return;
                case "ENISNukeSmack":
                    // NIS_NukeSmackablesWithinRange: smackables (and scenery models) within the radius are hidden and released
                    NukeSmackables(Coords.ToUnity(e.F(4), e.F(8), e.F(12)), e.F(16));
                    return;
                case "ENISPlayEffect":
                {
                    // [?] position in world coordinates (the Blacklist intros place it on their world-space track)
                    if (!EmitterSystem.Exists || string.IsNullOrEmpty(e.S(4))) break;
                    var frame = FxFrame.Identity;
                    frame.P = new Vector3(e.F(8), e.F(12), e.F(16));
                    EmitterSystem.Instance.Fire(AttribDatabase.Hash(e.S(4)), frame, Mathf.Max(e.F(28), e.F(20), e.F(24), 0.01f), Vector3.zero);
                    return;
                }
                case "ENISStopEffects":
                    if (EmitterSystem.Exists) EmitterSystem.Instance.KillAll();
                    return;
                case "ENISRain":
                    // ctor 0x6233D0: PrecipitationEnable = 1, SetOverRideRainIntensity(fRainIntensity) (0x73C970);
                    // NISActivity::Release puts PrecipitationEnable back and the override to 0 (NisPlayer.Finish)
                    Precipitation.NisRain(e.F(4));
                    return;
                case "ENISTimeOfDay":
                case "ENISFakeFar":
                    // MakeEvent callbacks 0x62ECB0 / 0x62DEC0 only store the data, and both destructors (0x623690,
                    // 0x622DE0) just reset the vtable: no effect on PC
                    return;
                case "ENISVisualLook":
                    VisualLookDesaturation = e.F(4);
                    VisualLookColourBloom = e.F(8);
                    VisualLookFacingScale = e.F(20) >= 0f && e.F(20) <= 2f ? e.F(20) : -1f;
                    VisualLookGrazingScale = e.F(24) >= 0f && e.F(24) <= 2f ? e.F(24) : -1f;
                    return;
                case "ENISRoadNoise":
                    DisableRoadNoise = e.I(4);
                    return;
                case "ENISMotionBlur":
                    IceManagerIsPlayingSomething = e.I(4) != 0;
                    return;
                case "ECameraPhotoFinish":
                {
                    // dtor 0x640410: only with one local player and exactly two racers (GRaceStatus::GetRacerCount == 2):
                    // IVisualTreatment::TriggerPulse(0) (0x73DEF0, the camera flash with the treatment's own length),
                    // SoundPause(true, 10), SetSoundControlState(false, 1, "ECameraPhotoFinish")
                    var race = RaceManager.Active;
                    if (race == null || race.Entrants.Count != 2) return;
                    _flashLength = 0.5f;   // [?] the pulse length is a visual-treatment attribute (+0x44)
                    _flash = _flashLength;
                    return;
                }
            }
            string key = e.Name ?? e.Id.ToString("X8");
            Unhandled[key] = Unhandled.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        internal static void NukeSmackables(Vector3 at, float radius)
        {
            float r2 = radius * radius;
            foreach (var s in UnityEngine.Object.FindObjectsByType<Smackable>(FindObjectsSortMode.None))
                if (s && (s.transform.position - at).sqrMagnitude < r2) s.gameObject.SetActive(false);
        }

        // ---- screen overlays: flash and face pixelation ----

        void UpdateOverlays(float dt)
        {
            var canvas = _player.OverlayCanvas;
            if (_flash > 0 && canvas)
            {
                if (!_flashImage)
                {
                    var go = new GameObject("NIS Flash", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                    var rt = (RectTransform)go.transform;
                    rt.SetParent(canvas.transform, false);
                    rt.anchorMin = Vector2.zero;
                    rt.anchorMax = Vector2.one;
                    rt.sizeDelta = Vector2.zero;
                    _flashImage = go.GetComponent<Image>();
                    _flashImage.raycastTarget = false;
                }
                _flash -= dt;
                // [?] ENISScreenFlash: white, fading out over its length
                _flashImage.color = new Color(1, 1, 1, Mathf.Clamp01(_flash / _flashLength));
            }
            else if (_flashImage) _flashImage.color = new Color(1, 1, 1, 0);

            var cam = Camera.main;
            var target = _player.PixelateTarget;
            bool show = PixelateOn && cam && target && !target.Hidden;
            if (!show) { if (_pixelQuad) _pixelQuad.SetActive(false); return; }
            if (!_pixelQuad)
            {
                var shader = Shader.Find("NFSMW/Pixelate");
                if (!shader) return;
                _pixelQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
                _pixelQuad.name = "NIS Face Pixelation";
                UnityEngine.Object.Destroy(_pixelQuad.GetComponent<Collider>());
                _pixelQuad.GetComponent<MeshRenderer>().sharedMaterial = new Material(shader);
                _pixelQuad.transform.SetParent(_player.transform, false);
            }
            _pixelQuad.SetActive(true);
            // FacePixelation::Render: the head's screen position; the rectangle is width x height in 640x480 units
            var head = target.HeadWorld;
            var toHead = head - cam.transform.position;
            float dist = Vector3.Dot(toHead, cam.transform.forward);
            if (dist < cam.nearClipPlane + 0.05f) { _pixelQuad.SetActive(false); return; }
            float worldPerPixel = 2f * dist * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / 480f;
            var qt = _pixelQuad.transform;
            qt.position = head;
            qt.rotation = cam.transform.rotation;
            qt.localScale = new Vector3(PixelWidth * worldPerPixel * (480f / 640f) * cam.aspect, PixelHeight * worldPerPixel, 1);
        }

        public void Dispose()
        {
            VisualLookDesaturation = VisualLookColourBloom = -1f;   // [ours] the exe keeps them until IVisualTreatment::Reset
            VisualLookFacingScale = VisualLookGrazingScale = -1f;
            DisableRoadNoise = 0;
            IceManagerIsPlayingSomething = true;   // NISActivity::Release 0x6F75D8 sets it back to 1
            if (_flashImage) UnityEngine.Object.Destroy(_flashImage.gameObject);
            if (_pixelQuad) UnityEngine.Object.Destroy(_pixelQuad);
        }
    }
}
