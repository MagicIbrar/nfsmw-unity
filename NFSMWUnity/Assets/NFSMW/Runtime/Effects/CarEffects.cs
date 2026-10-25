using System;
using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Effects
{
    /// <summary>
    /// A car's particle effects and skid marks, after CarRenderConn (World/CarRenderConn.cpp) and the Effects behaviour
    /// (Physics/Behaviors/Effects.cpp). Reads <see cref="PlayerCar"/>'s state; idle on cars without one (showroom).
    /// <list type="bullet">
    /// <item>Tyres (CarRenderConn::UpdateTires 0x746750, TireState::DoFX 0x7457D0 / DoSkids 0x745580): per wheel on the
    /// ground and within 80 m, slip = |wheel slip speed| minus the tolerated slip (Chassis::ComputeMaxSlip) and skid =
    /// |lateral speed| (DrawCar.cpp:225). Skid marks when sqrt(max(skid*0.05-0.1,0)^2 + max(slip*0.2-0.1,0)^2) &gt; 0;
    /// the surface's TireSlipEffects / TireSlideEffects / TireDriveEffects groups (simsurface, [0] dry, [2] flat tyre)
    /// run at Ramp(slip x ecar SlipFX[axle], min, max), Ramp(skid x SkidFX[axle]) and Ramp(car speed) from the tyre's
    /// ground point in the car's frame.</item>
    /// <item>Pipes (CarRenderConn::UpdateEffects 0x7561C0): on the body's EXHAUST / LEFT_EXHAUST / RIGHT_EXHAUST
    /// markers, ecar NOSEffect while the nitrous burns, MissShiftEffect fired once on a missed shift.</item>
    /// <item>Engine: EngineBlownEffect (and DeathEffect when totaled) above the front axle (InitEmitterPositions,
    /// CARFXPOS_ENGINE: front-tyre midpoint raised by 0.2 x track).</item>
    /// <item>Collisions (Effects::OnCollision): pvehicle OnHit*/OnScrape* EffectLinkageRecords by the other surface
    /// (walls and ground use "default", cars "carbody"); hits fire the effects collection's group one-shot with
    /// intensity = its quadratic of Ramp(impact, min, max), oriented along the contact normal; scrapes keep one
    /// group running at clamp((speed - min) / (max - min), 0.1, 1) until 0.2 s without contact.</item>
    /// <item>Smackables: a prop knocked loose fires its smackable OnDetached effect (EffectsSmackable::OnCollision).</item>
    /// </list>
    /// PlayerCar's per-wheel slip, contact and surface are private, so they are read by reflection; without them the
    /// tyres fall back to the lateral speed at the contact and the car's Grounded flag. Not ported: contrails (Xenon),
    /// the damage/blow-off pipe states (no health or blow-off flag in the port). Rain: the TireState rain variants via
    /// VehicleRenderConn::CheckForRain (round 9).
    /// </summary>
    public sealed class CarEffects : MonoBehaviour
    {
        const uint Exhaust = 0xA5B28001, LeftExhaust = 0xBCF8A18B, RightExhaust = 0xBD7CF15E;   // Generated/Hash.hpp
        const float MaxDistanceForVehicleEffects = 80f;      // CarRenderConn.cpp:840
        const float MinSpeedForImpacts = 1f;                  // Effects.cpp:208 (compared with the squared speed)
        const float ScrapeTimeOut = 0.2f;
        static readonly uint CarBody = AttribDatabase.Hash("carbody");

        Transform _body;
        string _model;
        readonly List<FxFrame> _pipeMarkers = new List<FxFrame>();
        Vector3 _enginePos;
        PlayerCar _car;
        Rigidbody _rb;
        CarFx _fx;
        bool _ready;
        readonly TireState[] _tires = new TireState[4];
        VehicleFx[] _pipes = Array.Empty<VehicleFx>(), _engines = Array.Empty<VehicleFx>();
        readonly ScrapeFx _scrape = new ScrapeFx();
        int _shiftCount;

        /// <summary>Adds the component to a built car (one line in CarBuilder.Build): body, wheels and the parts' exhaust markers.</summary>
        public static CarEffects Attach(CarBuilder.Result r, string model, IEnumerable<Solid> parts)
        {
            var fx = r.Root.AddComponent<CarEffects>();
            fx._body = r.Body;
            fx._model = model;
            foreach (var s in parts)
                foreach (var (hash, m) in s.Markers)
                    if (hash == Exhaust || hash == LeftExhaust || hash == RightExhaust)
                        fx._pipeMarkers.Add(FxFrame.FromMatrix(m));
            // CARFXPOS_ENGINE: front tyre midpoint, z + (fl.y - fr.y) * 0.2, in game car space
            if (r.Wheels[0] && r.Wheels[1] && r.Body)
            {
                var fl = Coords.ToGame(r.Body.InverseTransformPoint(r.Wheels[0].position));
                var fr = Coords.ToGame(r.Body.InverseTransformPoint(r.Wheels[1].position));
                var avg = (fl + fr) * 0.5f;
                fx._enginePos = new Vector3(avg.x, avg.y, avg.z + (fl.y - fr.y) * 0.2f);
            }
            return fx;
        }

        bool Init()
        {
            if (_ready) return true;
            if (!_car) _car = GetComponent<PlayerCar>();
            if (!_car || !_body) return false;
            var db = EmitterSystem.Db;
            if (db == null) return false;
            _rb = GetComponent<Rigidbody>();
            _fx = db.Car(_model);
            for (int i = 0; i < 4; i++) _tires[i] = new TireState();
            _pipes = new VehicleFx[_pipeMarkers.Count];
            for (int i = 0; i < _pipes.Length; i++) _pipes[i] = new VehicleFx { Local = _pipeMarkers[i] };
            var engine = FxFrame.Identity;
            engine.P = _enginePos;
            _engines = new[] { new VehicleFx { Local = engine } };
            _shiftCount = _car.ShiftCount;
            _ready = true;
            _ = EmitterSystem.Instance;
            WorldEffects.LoadTrack();
            return true;
        }

        void OnDisable()
        {
            foreach (var t in _tires) t?.Free();
            foreach (var p in _pipes) p.Stop();
            foreach (var e in _engines) e.Stop();
            _scrape.Pause();
        }

        void OnDestroy()
        {
            foreach (var t in _tires) t?.Free();
            foreach (var p in _pipes) p.Group?.Release();
            foreach (var e in _engines) e.Group?.Release();
            _scrape.Group?.Release();
        }

        /// <summary>
        /// VehicleRenderConn::CheckForRain (0x744C70) for view 1: the view's Rain intensity (+0x28C) &gt; 0 and the camera
        /// mover within Tweak_MaxDistForTireRain 60 m ([0x8B2F38]) of the car; then !NoRain (+0x234) within 10 m or for the
        /// mover's anchor car, else !NoRainAhead (+0x238) — which Rain::Update (0x757D8B) copies from NoRain every frame on
        /// PC, so both branches read "the camera is not under a tunnel / overpass".
        /// </summary>
        bool CheckForRain()
        {
            var rain = NfsMw.World.Rain.Instance;
            var cam = Camera.main;
            if (rain == null || !NfsMw.World.Rain.Enabled || cam == null || !(rain.Precip.Rain > 0f)) return false;
            if ((cam.transform.position - transform.position).magnitude >= 60f) return false;
            return !rain.Precip.NoRain;
        }

        // ---- PlayerCar wheel state ---------------------------------------------------------------------------------

        struct WheelData { public bool Contact; public float Slip, Radius; public uint Surface; public bool Valid; }

        WheelData ReadWheel(int i)
        {
            var r = new WheelData { Contact = _car.Grounded, Radius = i < 2 ? _car.Spec?.TireRadiusFront ?? 0.33f : _car.Spec?.TireRadiusRear ?? 0.33f };
            if (_car.WheelState(i, out var contact, out var slip, out var radius, out var surface))
            {
                r.Contact = contact; r.Slip = slip; r.Radius = radius; r.Surface = surface; r.Valid = true;
            }
            return r;
        }

        static float Ramp(float x, float min, float max)
        {
            if (max <= min) return x >= min ? 1f : 0f;
            return Mathf.Clamp01((x - min) / (max - min));
        }

        // ---- per frame ----------------------------------------------------------------------------------------------

        void LateUpdate()
        {
            if (!Init() || !EmitterSystem.Enabled) return;
            float dt = Time.deltaTime;
            if (dt <= 0f) return;
            var sys = EmitterSystem.Instance;
            var carFrame = FxFrame.Of(_body);
            var velU = _rb ? _rb.linearVelocity : Vector3.zero;
            var vel = FxFrame.G(velU);
            float speed = velU.magnitude;
            var cam = Camera.main;
            bool candofx = !cam || (cam.transform.position - transform.position).sqrMagnitude < MaxDistanceForVehicleEffects * MaxDistanceForVehicleEffects;

            UpdateTires(dt, carFrame, vel, speed, candofx);
            UpdateVehicleEffects(dt, carFrame, vel, candofx);
            _scrape.Update(sys, this, dt, vel);
            UpdateSmackables();
        }

        void UpdateTires(float dt, in FxFrame carFrame, Vector3 vel, float speed, bool candofx)
        {
            var db = EmitterSystem.Db;
            var up = transform.up;
            // VehicleRenderConn::CheckForRain (0x744C70), CarRenderConn::Update 0x7565C8 → CF_ISRAINING for TireState
            bool raining = CheckForRain();
            // Chassis::ComputeMaxSlip: the slip the tyre tolerates before it counts (as PlayerCar's slip limit)
            float tolerance = _car.Gear < 0 ? 71f : Ramp(speed, 10f, 71f) + 0.5f;
            for (int i = 0; i < 4; i++)
            {
                var t = _tires[i];
                var wv = _car.WheelVisuals != null && i < _car.WheelVisuals.Length ? _car.WheelVisuals[i] : null;
                if (!wv) continue;
                var wd = ReadWheel(i);
                var hub = wv.position;
                var contact = hub - up * wd.Radius;
                var tirePos = FxFrame.G(hub);
                int axle = i >> 1;
                int game = i < 2 ? i : 5 - i;   // port FL FR RL RR -> game FL FR RR RL
                if (wd.Contact && candofx)
                {
                    float skid = _rb ? Mathf.Abs(Vector3.Dot(_rb.GetPointVelocity(contact), wv.right)) : 0f;
                    float slip = Mathf.Abs(wd.Slip);
                    slip = slip > tolerance ? slip - tolerance : 0f;
                    float sk = Mathf.Max(Mathf.Abs(skid * 0.05f) - 0.1f, 0f);
                    float sl = Mathf.Max(Mathf.Abs(slip * 0.2f) - 0.1f, 0f);
                    float marks = Mathf.Sqrt(sk * sk + sl * sl);
                    if (marks > 0f && t.HasPrev)
                        DoSkids(t, marks, tirePos - t.PrevPos, FxFrame.G(wv.right), tirePos, _fx.TireSkidWidth[Mathf.Min(game, _fx.TireSkidWidth.Length - 1)]);
                    else t.Skids.MakeNoSkid();
                    float slipFx = slip * _fx.SlipFX[Mathf.Min(axle, _fx.SlipFX.Length - 1)];
                    float skidFx = skid * _fx.SkidFX[Mathf.Min(axle, _fx.SkidFX.Length - 1)];
                    // TireState::DoFX (CarRenderConn.cpp:338)
                    if (Mathf.Abs(slipFx) < 1.192092896e-07f && Mathf.Abs(skidFx) < 1.192092896e-07f && speed < 1.192092896e-07f)
                        t.Free();
                    else
                    {
                        t.SetSurface(db, wd.Surface, _car.TireDamage > 0.5f, raining);
                        var frame = carFrame;
                        frame.P = FxFrame.G(contact);
                        t.Slip.Update(slipFx, vel, frame, dt);
                        t.Skid.Update(skidFx, vel, frame, dt);
                        // PC DoFX 0x7457D0 updates the drive effect unconditionally: the decomp's numCopsActiveView < 2
                        // and !INIS::Exists() tests (CarRenderConn.cpp:355) are not in the exe
                        t.Drive.Update(speed, vel, frame, dt);
                    }
                }
                else t.Skids.MakeNoSkid();
                t.PrevPos = tirePos;
                t.HasPrev = true;
            }
        }

        /// <summary>TireState::DoSkids (CarRenderConn.cpp:275), in game space.</summary>
        void DoSkids(TireState t, float intensity, Vector3 deltaPos, Vector3 tireDir, Vector3 tirePos, float width)
        {
            if (intensity <= 0.3f || width <= 0f) { t.Skids.MakeNoSkid(); return; }
            var d = new Vector3(-deltaPos.y, deltaPos.x, 0f);
            if (d.sqrMagnitude < 1e-10f) return;
            d.Normalize();
            float dot = Mathf.Abs(Vector3.Dot(tireDir, d));
            float half = width * Mathf.Max(dot, 0.5f) * 0.5f;
            d *= half;
            var right = tirePos + d;
            var left = tirePos - d;
            if (!Ground(right, out float re) || !Ground(left, out float le)) { t.Skids.MakeNoSkid(); return; }
            var centre = new Vector3(tirePos.x, tirePos.y, (re + le) * 0.5f);
            d.z = re * 0.5f - le * 0.5f;
            d = d.normalized * half;
            float scale = Mathf.Max(dot * intensity, 0.3f);
            t.Skids.MakeSkid(centre, d, Mathf.Min(scale, 1f));
        }

        static readonly RaycastHit[] Hits = new RaycastHit[8];

        /// <summary>WWorldPos::HeightAtPoint: the static ground under a game-space point.</summary>
        bool Ground(Vector3 g, out float height)
        {
            height = g.z;
            var from = FxFrame.U(g) + Vector3.up * 1.5f;
            int n = Physics.RaycastNonAlloc(from, Vector3.down, Hits, 4f, ~0, QueryTriggerInteraction.Ignore);
            float best = float.MaxValue;
            for (int i = 0; i < n; i++)
            {
                if (Hits[i].rigidbody) continue;   // the car itself, other cars, loose props
                if (Hits[i].distance < best) { best = Hits[i].distance; height = Hits[i].point.y; }
            }
            return best < float.MaxValue;
        }

        void UpdateVehicleEffects(float dt, in FxFrame carFrame, Vector3 vel, bool visible)
        {
            if (!visible)
            {
                foreach (var p in _pipes) p.Stop();
                foreach (var e in _engines) e.Stop();
                _shiftCount = _car.ShiftCount;
                return;
            }
            bool missShift = _car.ShiftCount != _shiftCount && _car.LastShift == PlayerCar.ShiftStatus.Missed;
            _shiftCount = _car.ShiftCount;
            foreach (var p in _pipes)
            {
                if (_car.NosFiring) p.Update(carFrame, _fx.NosEffect, dt, 1f, vel);
                else if (missShift) p.Fire(carFrame, _fx.MissShiftEffect, 1f, vel);
                else p.Stop();
            }
            foreach (var e in _engines)
            {
                if (_fx.DeathEffect != 0 && _car.Totaled) e.Update(carFrame, _fx.DeathEffect, dt, 1f, vel);
                else if (_car.EngineBlown) e.Update(carFrame, _fx.EngineBlownEffect, dt, 1f, vel);
                else e.Stop();
            }
        }

        // ---- collisions (Physics/Behaviors/Effects.cpp) --------------------------------------------------------------

        void OnCollisionEnter(Collision c) => Collide(c, true);
        void OnCollisionStay(Collision c) => Collide(c, false);

        void Collide(Collision c, bool enter)
        {
            if (!_ready || c.contactCount == 0 || !EmitterSystem.Enabled) return;
            var db = EmitterSystem.Db;
            if (c.contactCount == 0) return;   // contact modification can leave a collision with no contacts
            var cp = c.GetContact(0);
            var n = cp.normal;
            var closing = -c.relativeVelocity;   // this car's velocity relative to the other body
            var other = c.collider;
            EffectLink[] hits, scrapes;
            uint surface = 0;
            var otherCar = c.rigidbody ? c.rigidbody.GetComponent<PlayerCar>() : null;
            // props only matter on first contact (a fixed prop scraped along is a wall)
            var smack = enter && other && !otherCar ? other.GetComponentInParent<Smackable>() : null;
            if (otherCar)
            {
                hits = _fx.OnHitObject;
                scrapes = _fx.OnScrapeObject;
                surface = CarBody;
            }
            else if (smack)
            {
                hits = _fx.OnHitObject;
                scrapes = _fx.OnScrapeObject;
                if (enter) QueueSmackable(smack, closing.magnitude, cp.point, n);
            }
            else if (Vector3.Dot(n, Vector3.up) > 0.7f || WorldCollision.IsGround(other))
            {
                // EffectsCar::OnHitGround: with three or more wheels down a belly contact is a bottom-out
                bool belly = _car.WheelsOnGround > 2 && Vector3.Dot(transform.up, n) > 0.7f;
                hits = belly ? _fx.OnBottomOut : _fx.OnHitGround;
                scrapes = belly ? _fx.OnBottomScrape : _fx.OnScrapeGround;
            }
            else
            {
                hits = _fx.OnHitWorld;
                scrapes = _fx.OnScrapeWorld;
            }
            var pos = FxFrame.G(cp.point);
            var ng = FxFrame.G(n);
            var vel = _rb ? FxFrame.G(_rb.linearVelocity) : Vector3.zero;
            if (enter && closing.sqrMagnitude > MinSpeedForImpacts)
            {
                // Effects::DoHit: intensity = Ramp(impact, min, max); the impulse is taken as the normal closing speed [?]
                var link = db.Lookup(hits, surface);
                float impact = Mathf.Abs(Vector3.Dot(closing, n));
                if (link.HasValue)
                {
                    float k = Ramp(impact, link.Value.MinSpeed, link.Value.MaxSpeed);
                    if (k > 0f) FireEffect(link.Value.EffectKey, pos, ng, k, vel);
                }
            }
            // Effects::DoScrape on the sliding velocity
            var sliding = closing - n * Vector3.Dot(closing, n);
            float speed = sliding.magnitude;
            if (speed > 1f)
            {
                var link = db.Lookup(scrapes, surface);
                if (!link.HasValue || speed < link.Value.MinSpeed || link.Value.MaxSpeed <= link.Value.MinSpeed) { _scrape.Pause(); return; }
                float k = (speed - link.Value.MinSpeed) / (link.Value.MaxSpeed - link.Value.MinSpeed);
                _scrape.Set(link.Value.EffectKey, pos, ng, Mathf.Clamp(k, 0.1f, 1f));
            }
        }

        /// <summary>World_OneShotEffect (WorldConn.cpp:400): the effects collection's group, one-shot, Z along the normal.</summary>
        static void FireEffect(uint effectKey, Vector3 pos, Vector3 normal, float intensity, Vector3 ownerVel)
        {
            var e = EmitterSystem.Db?.Effect(effectKey);
            if (e == null || e.GroupKey == 0) return;
            var cam = Camera.main;
            if (cam && (FxFrame.G(cam.transform.position) - pos).magnitude > e.VisualCullDist) return;
            float k = e.Intensity(intensity);
            if (k <= 0f) return;
            var vel = e.InheritVelocity > 0f ? ownerVel * e.InheritVelocity : Vector3.zero;
            // sim up (Y) rotated onto the normal, then swizzled: render Z follows the normal
            var frame = FxFrame.Delta(Vector3.forward, normal, pos);
            EmitterSystem.Instance.Fire(e.GroupKey, frame, k, vel, 0x11400000);
        }

        // ---- smackables ----------------------------------------------------------------------------------------------

        struct PendingSmack { public Smackable S; public float Impact, Time; public Vector3 Point, Normal; }
        readonly List<PendingSmack> _smacks = new List<PendingSmack>();
        static readonly HashSet<int> Detached = new HashSet<int>();

        void QueueSmackable(Smackable s, float impact, Vector3 point, Vector3 normal)
        {
            if (Detached.Contains(s.GetInstanceID())) return;
            _smacks.Add(new PendingSmack { S = s, Impact = impact, Point = point, Normal = normal, Time = Time.time });
        }

        /// <summary>EffectsSmackable::OnCollision: once a prop comes loose (Smackable gives it a Rigidbody), its OnDetached effect.</summary>
        void UpdateSmackables()
        {
            for (int i = _smacks.Count - 1; i >= 0; i--)
            {
                var p = _smacks[i];
                if (!p.S) { _smacks.RemoveAt(i); continue; }
                var rb = p.S.GetComponent<Rigidbody>();
                if (rb)
                {
                    _smacks.RemoveAt(i);
                    int id = p.S.GetInstanceID();
                    if (!Detached.Add(id)) continue;
                    var db = GameData.Attributes;
                    if (db == null || string.IsNullOrEmpty(p.S.Kind)) continue;
                    if (!db.Collections.TryGetValue((Smackable.ClassKey, AttribDatabase.Hash(p.S.Kind)), out var col)) continue;
                    var link = EmitterSystem.Db.Lookup(FxDatabase.Links(col, "OnDetached"), CarBody);
                    if (!link.HasValue) continue;
                    float k = Ramp(p.Impact, link.Value.MinSpeed, link.Value.MaxSpeed);
                    if (k > 0f) FireEffect(link.Value.EffectKey, FxFrame.G(p.Point), FxFrame.G(-p.Normal), k, FxFrame.G(rb.linearVelocity));
                }
                else if (Time.time - p.Time > 0.5f) _smacks.RemoveAt(i);
            }
            // respawned props can come loose again
            if (Detached.Count > 256) Detached.Clear();
        }

        // ---- effect holders -----------------------------------------------------------------------------------------

        /// <summary>TireState (CarRenderConn.cpp:32): three surface effects and a skid maker per tyre.</summary>
        sealed class TireState
        {
            public readonly TireFx Slip = new TireFx(), Skid = new TireFx(), Drive = new TireFx();
            public readonly SkidMarks.Maker Skids = new SkidMarks.Maker();
            public Vector3 PrevPos;
            public bool HasPrev;
            uint _surface = uint.MaxValue;
            bool _flat, _raining;

            public void Free() { Slip.Free(); Skid.Free(); Drive.Free(); }

            /// <summary>TireState::SetSurface (0x738D50, CarRenderConn.cpp:363): [2] flat tyre when the list has 3, else [1]
            /// while raining when it has 2 or more, else [0]; re-picked when surface, rain or flat change (UpdateWorld 0x738F50).</summary>
            public void SetSurface(FxDatabase db, uint key, bool flat, bool raining)
            {
                if (key == _surface && flat == _flat && raining == _raining) return;
                _surface = key;
                _flat = flat;
                _raining = raining;
                if (!db.Surfaces.TryGetValue(key, out var s) && !db.Surfaces.TryGetValue(AttribDatabase.Hash("default"), out s)) return;
                Slip.Set(Pick(s.Slip, flat, raining));
                Skid.Set(Pick(s.Slide, flat, raining));
                Drive.Set(Pick(s.Drive, flat, raining));
            }

            static TireEffect Pick(TireEffect[] a, bool flat, bool raining) =>
                a.Length == 0 ? default : flat && a.Length >= 3 ? a[2] : raining && a.Length > 1 ? a[1] : a[0];
        }

        /// <summary>TireState::Effect (CarRenderConn.cpp:120-228).</summary>
        sealed class TireFx
        {
            uint _key;
            float _min, _max;
            FxGroup _group;
            bool _needsInit = true;
            int _zeroFrames;

            public void Set(TireEffect r)
            {
                _min = r.MinSpeed;
                _max = r.MaxSpeed;
                if (_key != r.GroupKey)
                {
                    _key = r.GroupKey;
                    _needsInit = true;
                    _zeroFrames = 0;
                }
            }

            public void Free()
            {
                _group?.Release();
                _group = null;
                _needsInit = true;
                _zeroFrames = 0;
            }

            void LazyInit()
            {
                _group?.Release();
                _group = null;
                if (_key == 0 || _key == FxDatabase.NullKey) return;
                _group = EmitterSystem.Instance.Create(_key, EmitterSystem.TireEffect);
                _group?.SetEnabled(true);
                _needsInit = false;
                _zeroFrames = 0;
            }

            public void Update(float speed, Vector3 vel, in FxFrame frame, float dt)
            {
                float intensity = Ramp(speed, _min, _max);
                if (intensity <= 0f) { if (_group != null) Free(); return; }
                if (_group != null && _group.Dead) { _group = null; _needsInit = true; }
                if (_needsInit) LazyInit();
                if (_group == null) return;
                _group.Frame = frame;
                _group.InheritVelocity = vel;
                _group.Intensity = intensity;
                _group.Update(dt);
                if (_group.NumParticles == 0) _zeroFrames++;
                if (_zeroFrames < 11) return;
                Free();
            }
        }

        /// <summary>VehicleRenderConn::Effect (VehicleRenderConn.cpp:30-120): one marker's effect.</summary>
        sealed class VehicleFx
        {
            public FxFrame Local;
            public FxGroup Group;
            uint _key;

            public void Stop() => Group?.SetEnabled(false);

            public void Fire(in FxFrame world, uint key, float intensity, Vector3 vel)
            {
                if (key == 0) return;
                EmitterSystem.Instance.Fire(key, Local.In(world), intensity, vel, EmitterSystem.OneShotEffect);
            }

            public void Update(in FxFrame world, uint key, float dt, float intensity, Vector3 vel)
            {
                if (Group != null && Group.Dead) Group = null;
                if (key != _key)
                {
                    Group?.Kill();
                    Group = null;
                    _key = key;
                    if (key == 0) return;
                }
                if (Group == null && key != 0) Group = EmitterSystem.Instance.Create(key, EmitterSystem.VehicleEffect);
                if (Group == null) return;
                Group.Frame = Local.In(world);
                Group.SetEnabled(true);
                Group.Intensity = intensity;
                Group.MakeOneShot(false);
                Group.InheritVelocity = vel;
                Group.Update(dt);
            }
        }

        /// <summary>The scrape's Sim::Effect / WorldEffectConn (WorldConn.cpp:291): one running group, paused 0.2 s after the last contact.</summary>
        sealed class ScrapeFx
        {
            public FxGroup Group;
            uint _key;
            Vector3 _pos, _normal;
            float _mag, _timeOut;
            bool _paused = true;

            public void Set(uint effectKey, Vector3 pos, Vector3 normal, float mag)
            {
                if (effectKey != _key)
                {
                    Group?.Kill();
                    Group = null;
                    _key = effectKey;
                }
                _pos = pos;
                _normal = normal;
                _mag = mag;
                _paused = false;
                _timeOut = ScrapeTimeOut;
            }

            public void Pause()
            {
                _paused = true;
                Group?.SetEnabled(false);
            }

            public void Update(EmitterSystem sys, CarEffects owner, float dt, Vector3 ownerVel)
            {
                if (_paused) return;
                if ((_timeOut -= dt) < 0f) { Pause(); return; }
                var e = EmitterSystem.Db?.Effect(_key);
                if (e == null || e.GroupKey == 0) return;
                if (Group != null && Group.Dead) Group = null;
                if (Group == null)
                {
                    Group = sys.Create(e.GroupKey, 0x11800000);
                    if (Group == null) return;
                }
                float k = e.Intensity(_mag);
                var cam = Camera.main;
                float dist = cam ? (FxFrame.G(cam.transform.position) - _pos).magnitude : 0f;
                if (k > 0f && dist < e.VisualCullDist)
                {
                    Group.SetEnabled(true);
                    // the live effect turns the up axis (0, 1, 0) onto the render-space normal (WorldConn.cpp:338)
                    Group.Frame = FxFrame.Delta(Vector3.up, _normal, _pos);
                    Group.InheritVelocity = e.InheritVelocity > 0f ? ownerVel * e.InheritVelocity : Vector3.zero;
                    Group.Intensity = k;
                    Group.Update(dt);
                }
                else Group.SetEnabled(false);
            }
        }
    }
}
