using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Effects;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>
    /// Sound::CollisionEvent (EAXSound/SoundCollision.cpp): one impact or scrape, created from a world effect's audio
    /// attributes. Description = the audioimpact/audioscrape DESCRIPTION bits | SD_IS_PRIMARY when the camera's car is
    /// the actor or actee; intensity = clamp(magnitude, 0, 1) x 127. An impact picks its stitch level
    /// clamp((int)(mag x (levels-1) + 0.5)) and the next variant of a shared counter, with that level's volume.
    /// IsDescribed(mask) = all mask bits set.
    /// </summary>
    public sealed class CollisionEvent
    {
        public Vector3 InitialPoint, CurrentPoint, Normal, Velocity;
        public float Time;               // CollisionTime (reset by Update)
        public int Intensity, Volume = 0x7FFF, Description;
        public Stitch ImpactStitch;
        public CollisionAudioData.Impact AudioFx;
        public string CsisEffect;
        public object Actor, Actee;
        public bool Active;
        public int RefCount = 1;
        internal CollisionAudio.State Owner;

        public bool IsDescribed(int d) => (Description & d) == d;
        public float Age => UnityEngine.Time.time - Time;

        static int _counter;

        /// <summary>CollisionEvent::CollisionEvent + InitAsImpact / InitAsScrape.</summary>
        internal static CollisionEvent Create(CollisionAudioData data, (uint cls, uint key) attrs, Vector3 pos, Vector3 normal, Vector3 vel,
            float magnitude, object actor, object actee, bool impact)
        {
            var e = new CollisionEvent
            {
                InitialPoint = pos, CurrentPoint = pos, Normal = normal, Velocity = vel, Time = UnityEngine.Time.time, Actor = actor, Actee = actee,
            };
            float mag = Mathf.Clamp01(magnitude);
            e.Intensity = (int)(mag * 127f);
            CollisionAudioData.Impact im = null;
            CollisionAudioData.Scrape sc = null;
            if (attrs.cls == CollisionAudioData.ImpactClass) data.Impacts.TryGetValue(attrs.key, out im);
            else if (attrs.cls == CollisionAudioData.ScrapeClass) data.Scrapes.TryGetValue(attrs.key, out sc);
            e.Description = im != null ? im.Description : sc != null ? sc.Description : 0;
            var player = SndMixer.PlayerRef;
            if (player && (ReferenceEquals(actor, player) || ReferenceEquals(actee, player))) e.Description |= CollisionAudioData.SdPrimary;
            if ((e.Description & 6) == 6 && e.Intensity <= 9) return e;   // smackable-vs-wall taps stay silent (THRESHOLD_SMAKEABLE_VS_WORLD)
            if (impact && im != null)
            {
                e.AudioFx = im;
                int levels = 0;
                while (levels < 4 && im.Levels[levels].Length > 0) levels++;
                if (levels == 0) return e;
                int sel = Mathf.Clamp((int)(mag * (levels - 1) + 0.5f), 0, levels - 1);
                _counter++;
                int index = _counter % im.Levels[sel].Length;
                e.Volume = im.Volumes[sel];
                e.ImpactStitch = StitchPlayer.Get(0, im.Levels[sel][index]);
            }
            else if (!impact && sc != null && !string.IsNullOrEmpty(sc.CsisEffect))
            {
                e.CsisEffect = sc.CsisEffect;
                e.Description |= CollisionAudioData.SdScrape;
                e.Active = true;
            }
            return e;
        }

        /// <summary>CollisionEvent::Update (a live scrape's new contact).</summary>
        public void Update(Vector3 p, Vector3 n, Vector3 v, float mag)
        {
            Intensity = (int)(Mathf.Clamp01(mag) * 127f);
            CurrentPoint = p;
            Normal = n;
            Velocity = v;
            Time = UnityEngine.Time.time;
        }

        /// <summary>CollisionEvent::Release.</summary>
        public void Release()
        {
            Active = false;
            Owner?.Detach();
            RefCount--;
        }
    }

    /// <summary>
    /// The COLLISION mix state (CSTATEMGR_Collision, States/Managers/STATEMGR_Collision.cpp: 10 states, PC @0x4E5D30) and
    /// SFXObj_Collision (CARSFX/SFXObj_Collision.cpp, obj 0 with SFXCTL_3DColPos ctl 0 at the first contact and
    /// SFXCTL_3DScrapePos ctl 1 at the current one), fed by world effects through <see cref="OneShot"/> (World_OneShotEffect,
    /// WorldConn.cpp:389) and <see cref="LiveConn"/> (WorldEffectConn::Update, WorldConn.cpp:291).
    /// <list type="bullet">
    /// <item>CollisionEvent::Play / PlayScrape: nothing beyond 100 m of the view or without attributes; an impact needs its
    /// stitch. GetFreeState: a smokable impact is dropped when an attached smokable impact under 0.25 s old of the same
    /// actor/actee pair exists (unless the new one is over 70 and the old under); then a free state; else the attached
    /// state of lowest GetCollisionPriority is stolen if the new one ranks higher.</item>
    /// <item>InitSFX: volume / azimuth / pitch / reverb slots by description (0x412 smokable car 10, 2 smokable world 11,
    /// car+wall front 7 / side 8, car bottom-out 6, rollover 5, car side 4, car front 3); a primary two-car hit or an EVENT
    /// impact plays its StreamSweetner moment (the lowest-index entry whose threshold ≤ intensity); a scrape starts
    /// Csis::FX_Scrape, an impact its stitch.</item>
    /// <item>UpdateParams: inputs 0..2 cleared; on the first update of an impact input 0 two-car / 2 smokable / 1 car, and
    /// input 4 = intensity x 0xFF; a finished stitch detaches.</item>
    /// <item>ProcessUpdate: the stitch gets vol = out(VolSlot) x event volume, the azimuth taken on the first update and
    /// the reverb slot; a scrape sets Csis::FX_Scrape (volume out 12, pitch 18, azimuth 1, dry 0x7FFF, wet 15, low-pass
    /// 25000, impulse_magnitude = intensity) and detaches 0.25 s after the contact ends (SCRAPE_FADE_OUT_MS only times the
    /// detach; deleting the Csis instance lets the patch end the loop).</item>
    /// </list>
    /// FX_Scrape (IG_GLOBAL/FX_Misc_MB) runs in the AEMS interpreter: its tables map the magnitude to volume and pitch and
    /// its oscillators / shuffles / ramps make the flutter (SFXObj_Collision.cpp:120-220).
    /// </summary>
    public static class CollisionAudio
    {
        internal sealed class State
        {
            public int Inst;
            public SndObj Obj;
            public SndPos ColPos, ScrapePos;
            public CollisionEvent Event;
            public bool FirstUpdate;
            public int VolSlot, AzimSlot, PitchSlot, ReverbSlot, InitialAz;
            public StitchPlayer Stitch;
            public CsisObj Scrape;
            public float ScrapeFade;
            public bool Attached => Event != null;

            public void Detach()
            {
                Stitch?.Stop();
                Stitch = null;
                Scrape?.Release();
                Scrape = null;
                if (Event != null) { Event.Owner = null; Event.Active = false; }
                Event = null;
                for (int i = 0; i < 16; i++) Obj.In[i] = 0;
                Obj.Attach(false);
            }
        }

        static readonly List<State> States = new List<State>();
        static Transform _holder;
        static AemsHost _aems;
        static CollisionAudioData _data;
        const string ScrapeBank = "IG_GLOBAL/FX_Misc_MB.abk";

        public static CollisionAudioData Data
        {
            get
            {
                if (_data == null) _data = MomentStream.Data;
                return _data;
            }
        }

        static void Ensure()
        {
            if (States.Count > 0 && _holder) return;
            var m = SndMixer.Instance;
            if (!m) return;
            States.Clear();
            _holder = new GameObject("NFSMW Collisions").transform;
            _holder.SetParent(m.transform, false);
            _aems = AemsHost.Create(_holder, "AEMS Scrapes");
            for (int i = 0; i < 10; i++)
            {
                int inst = m.Acquire(MixMap.Collision);
                if (inst < 0) break;
                var st = new State
                {
                    Inst = inst,
                    Obj = new SndObj(m.Map, MixMap.ObjectId(false, MixMap.Collision, inst, 0)),
                    ColPos = new SndPos(new SndObj(m.Map, MixMap.ObjectId(true, MixMap.Collision, inst, 0))),
                    ScrapePos = new SndPos(new SndObj(m.Map, MixMap.ObjectId(true, MixMap.Collision, inst, 1))),
                };
                States.Add(st);
            }
        }

        /// <summary>Sound::DistanceToView: distance to the camera.</summary>
        static float DistanceToView(Vector3 p)
        {
            var cam = SndMixer.Cam;
            return cam ? Vector3.Distance(cam.position, p) : 100000f;
        }

        /// <summary>GetCollisionPriority (STATEMGR_Collision.cpp).</summary>
        static int Priority(CollisionEvent c)
        {
            if (c.IsDescribed(0x400)) return 10;
            bool hi = c.Intensity > 100;
            if (c.IsDescribed(1))
            {
                if (c.IsDescribed(4) || c.IsDescribed(8)) return hi ? 9 : 8;
                if (c.IsDescribed(2)) return hi ? 7 : 6;
                return 7;
            }
            if (c.IsDescribed(0x10))
            {
                if (c.IsDescribed(4) || c.IsDescribed(8)) return hi ? 6 : 5;
                if (c.IsDescribed(2) && hi) return 5;
                return 4;
            }
            if (c.IsDescribed(2) && c.IsDescribed(4) && hi) return 4;
            return 3;
        }

        /// <summary>CSTATEMGR_Collision::GetFreeState.</summary>
        static State GetFreeState(CollisionEvent e)
        {
            if (e.IsDescribed(2))
                foreach (var s in States)
                {
                    var c = s.Event;
                    if (c == null || c.Age > 0.25f || !c.IsDescribed(2) || c.IsDescribed(0x200)) continue;
                    if (e.Intensity <= 70 || c.Intensity >= 70)
                    {
                        bool same = (Equals(e.Actor, c.Actor) && Equals(e.Actee, c.Actee)) || (Equals(e.Actor, c.Actee) && Equals(e.Actee, c.Actor));
                        if (same) return null;
                    }
                }
            foreach (var s in States) if (!s.Attached) return s;
            int pri = Priority(e), lowest = 11;
            State low = null;
            foreach (var s in States)
            {
                if (!s.Attached) continue;
                int p = Priority(s.Event);
                if (p < lowest) { lowest = p; low = s; }
            }
            if (low != null && pri > lowest) { low.Detach(); return low; }
            return null;
        }

        static bool Attach(CollisionEvent e)
        {
            Ensure();
            var s = GetFreeState(e);
            if (s == null) return false;
            s.Event = e;
            e.Owner = s;
            s.Obj.Attach(true);
            InitSfx(s);
            return true;
        }

        /// <summary>SFXObj_Collision::InitSFX.</summary>
        static void InitSfx(State s)
        {
            var e = s.Event;
            s.FirstUpdate = true;
            s.AzimSlot = 0; s.PitchSlot = 16; s.ReverbSlot = 13; s.VolSlot = 7;
            if (e.IsDescribed(0x412)) { s.VolSlot = 10; s.AzimSlot = 2; s.PitchSlot = 17; }
            else if (e.IsDescribed(2)) { s.VolSlot = 11; s.AzimSlot = 2; s.PitchSlot = 17; s.ReverbSlot = 14; }
            else if (e.IsDescribed(0x10))
            {
                if (e.IsDescribed(4)) s.VolSlot = e.IsDescribed(0x80) ? 7 : 8;
                else if (e.IsDescribed(0x40)) s.VolSlot = 6;
                else if (e.IsDescribed(0x20)) s.VolSlot = 5;
                else if (e.IsDescribed(0x100)) s.VolSlot = 4;
                else if (e.IsDescribed(0x80)) s.VolSlot = 3;
            }
            if (((e.IsDescribed(1) && e.IsDescribed(8)) || e.IsDescribed(0x400)) && e.AudioFx != null)
            {
                (uint moment, int threshold)? sweet = null;
                for (int n = e.AudioFx.Streams.Length - 1; n >= 0; n--)
                    if (e.AudioFx.Streams[n].threshold <= e.Intensity) sweet = e.AudioFx.Streams[n];
                if (sweet != null)
                {
                    // EMomentStrm: the car among actor/actee is followed (car2car / collision moments)
                    var car = e.Actor as Component ?? e.Actee as Component;
                    MomentStream.Send(sweet.Value.moment, e.InitialPoint.sqrMagnitude < 1f ? e.InitialPoint + Vector3.right : e.InitialPoint, car ? car.transform : null);
                }
            }
            s.ColPos.Update(e.InitialPoint, e.Velocity, 0f);
            s.ScrapePos.Update(e.CurrentPoint, e.Velocity, 0f);
            if (e.IsDescribed(0x200))
            {
                s.ScrapeFade = 1f;
                // new Csis::FX_Scrape(0, 0, 0, 0, 0, 25000, 0, 0x7FFF, 0)
                s.Scrape = new CsisObj(_aems, "FX_Scrape", new[] { 0, 0, 0, 0, 0, 25000, 0, 0x7FFF, 0 }, ScrapeBank);
            }
            else s.Stitch = new StitchPlayer(_holder, 0, e.ImpactStitch, 0, 0);
        }

        /// <summary>
        /// World_OneShotEffect's audio half: the effect's AudioQuadratic of the hit intensity, culled at AudioCullDist,
        /// attributes by ChooseAudioAttributes against <paramref name="owner"/>'s axes; an audioimpact plays, an audioscrape
        /// is created and released at once (it fades). Positions and normals are Unity world space; the normal points
        /// against the owner where it was hit (a head-on wall hit has normal · forward &lt; -0.707 = FRONT).
        /// </summary>
        public static void OneShot(uint effectKey, Vector3 pos, Vector3 normal, float intensity, Transform owner, object actor, object actee, Vector3 vel)
        {
            if (!Application.isPlaying || SoundState.Paused) return;
            if (!Data.Effects.TryGetValue(effectKey, out var fx)) return;
            float d = DistanceToView(pos);
            if (d > fx.AudioCullDist) return;
            float k = fx.Intensity(intensity);
            if (k <= 0f) return;
            var attrs = owner ? fx.Choose(true, Vector3.Dot(owner.forward, normal), Vector3.Dot(owner.up, normal)) : fx.Choose(false, 0, 0);
            var e = Play(attrs, pos, normal, vel, k, actor, actee);
            e?.Release();
        }

        /// <summary>AudioEvent::CreateInstance by the attribute class (audioimpact → Play, audioscrape → PlayScrape).</summary>
        static CollisionEvent Play((uint cls, uint key) attrs, Vector3 pos, Vector3 normal, Vector3 vel, float mag, object actor, object actee)
        {
            if (attrs.key == 0 || DistanceToView(pos) > 100f) return null;
            bool impact = attrs.cls == CollisionAudioData.ImpactClass;
            if (!impact && attrs.cls != CollisionAudioData.ScrapeClass) return null;
            var e = CollisionEvent.Create(Data, attrs, pos, normal, vel, mag, actor, actee, impact);
            if (impact)
            {
                if (e.ImpactStitch == null || !Attach(e)) return null;
                return null;   // CollisionEvent::Play returns no handle: the state owns it
            }
            if (e.CsisEffect == null || !Attach(e)) return null;
            e.RefCount++;
            return e;
        }

        /// <summary>WorldEffectConn: a live (scrape) effect with its audio event, paused when the contact stops.</summary>
        public sealed class LiveConn
        {
            uint _key;
            bool _silent;
            CollisionEvent _event;

            /// <summary>One frame of the effect (WorldEffectConn::Update with a serviced packet).</summary>
            public void Update(uint effectKey, Vector3 pos, Vector3 normal, float intensity, Transform owner, object actor, object actee, Vector3 vel)
            {
                if (effectKey != _key) { Pause(); _key = effectKey; _silent = false; }
                if (!Data.Effects.TryGetValue(effectKey, out var fx)) return;
                float k = fx.Intensity(intensity);
                float d = DistanceToView(pos);
                if (_event == null)
                {
                    if (_silent || k <= 0f || d >= fx.AudioCullDist || SoundState.Paused) return;
                    var attrs = owner ? fx.Choose(true, Vector3.Dot(owner.forward, normal), Vector3.Dot(owner.up, normal)) : fx.Choose(false, 0, 0);
                    _event = Play(attrs, pos, normal, vel, k, actor, actee);
                    if (_event == null) _silent = true;
                }
                else if (k > 0f && d < fx.AudioCullDist) _event.Update(pos, normal, vel, k);
                else { _event.Release(); _event = null; }
            }

            /// <summary>Service failed (paused / timed out): release the event.</summary>
            public void Pause()
            {
                _event?.Release();
                _event = null;
            }
        }

        /// <summary>SFXObj_Collision::UpdateParams (before the mixer).</summary>
        internal static void Tick(float dt)
        {
            foreach (var s in States)
            {
                for (int i = 0; i < 3; i++) s.Obj.Set(i, 0);
                if (!s.Attached) continue;
                var e = s.Event;
                if (s.Stitch != null && !s.Stitch.Playing) { s.Detach(); continue; }
                if (s.FirstUpdate && s.Scrape == null)
                {
                    if (e.IsDescribed(8)) s.Obj.Set(0, 0x7FFF);
                    else if (e.IsDescribed(2)) s.Obj.Set(2, 0x7FFF);
                    else if (e.IsDescribed(0x10)) s.Obj.Set(1, 0x7FFF);
                    s.Obj.Set(4, e.Intensity * 0xFF);
                }
                s.Obj.In[15] = 1;
                s.ColPos.Update(e.InitialPoint, e.Velocity, dt);
                s.ScrapePos.Update(e.CurrentPoint, e.Velocity, dt);
            }
        }

        /// <summary>SFXObj_Collision::ProcessUpdate (after the mixer).</summary>
        internal static void Apply(float dt)
        {
            float paused = SoundState.Paused ? 0f : 1f;
            foreach (var s in States)
            {
                if (!s.Attached) continue;
                var e = s.Event;
                if (s.FirstUpdate) { s.InitialAz = s.Obj.Out(s.AzimSlot, MixMap.Azim); s.FirstUpdate = false; }
                int vol = s.Obj.Mapped ? (s.Obj.Out(s.VolSlot, MixMap.Vol) * e.Volume) >> 15 : e.Volume;
                if (s.Stitch != null && !s.Stitch.Update(vol, s.InitialAz, paused)) { s.Detach(); continue; }
                if (s.Scrape != null)
                {
                    if (!e.Active)
                    {
                        s.ScrapeFade -= dt / 0.25f;   // SCRAPE_FADE_OUT_MS
                        if (s.ScrapeFade < 0f) { s.Detach(); continue; }
                    }
                    var c = s.Scrape;
                    c[0] = s.Obj.Out(12, MixMap.Vol);
                    c[1] = s.Obj.Out(18, MixMap.Pitch);
                    c[2] = s.Obj.Out(1, MixMap.Azim);
                    c[7] = 0x7FFF;
                    c[8] = s.Obj.Out(15, MixMap.Vol);
                    c[5] = 25000;
                    c[4] = e.Intensity;
                }
            }
            if (_aems) _aems.Gain = paused;
        }
    }

    /// <summary>
    /// The physics Effects behaviour's collision half (Physics/Behaviors/Effects.cpp Effects::OnCollision, EffectsCar,
    /// EffectsSmackable) for the audio: hit = the On*Hit EffectLinkageRecord of the other surface (EffectLookup::Init:
    /// "default", overridden by the surface or its nearest ancestor), Ramp(impact, min, max) → World_OneShotEffect; scrape =
    /// sliding speed over 1 m/s, intensity clamp((v - min)/(max - min), 0.1, 1) → the live scrape effect, paused 0.2 s after
    /// the last contact. Cars: other car = OnHitObject/OnScrapeObject on "carbody", smackable = object, ground (normal up
    /// or ground collider) = OnHitGround/OnScrapeGround or with 3+ wheels down and the normal along the car's up
    /// OnBottomOut/OnBottomScrape, else OnHitWorld/OnScrapeWorld. A smackable that comes loose fires its OnDetached record
    /// once (closing speed: EffectsSmackable::OnCollision uses Length(closingVel)), then its own On* records while loose. A
    /// hit's impact is COLLISION_INFO impulseA, the body's speed change (the contact impulse / its mass), falling back to the
    /// normal closing speed when the contact carries no impulse [ours]; static world surfaces are "default" (the port's wall
    /// colliders carry no simsurface).
    /// </summary>
    public sealed class CollisionEffects : MonoBehaviour
    {
        static readonly uint CarBody = AttribDatabase.Hash("carbody");
        const float ScrapeTimeOut = 0.2f;
        public PlayerCar Car;
        public Smackable Smack;
        CarFx _fx;
        EffectLink[] _sOnHitWorld, _sOnScrapeWorld, _sOnHitObject, _sOnScrapeObject, _sOnHitGround, _sOnScrapeGround, _sOnDetached;
        readonly CollisionAudio.LiveConn _scrape = new CollisionAudio.LiveConn();
        float _scrapeTimeOut;
        bool _scraping, _detachedFired;
        Rigidbody _rb;

        public static CollisionEffects ForCar(PlayerCar car, string pvehicle)
        {
            if (!car) return null;
            var e = car.GetComponent<CollisionEffects>();
            if (e) return e;
            e = car.gameObject.AddComponent<CollisionEffects>();
            e.Car = car;
            var db = EmitterSystem.Db;
            e._fx = db != null ? db.Car(pvehicle) : new CarFx();
            return e;
        }

        public static CollisionEffects ForSmackable(Smackable s)
        {
            if (!s) return null;
            var e = s.GetComponent<CollisionEffects>();
            if (e) return e;
            e = s.gameObject.AddComponent<CollisionEffects>();
            e.Smack = s;
            var db = GameData.Attributes;
            AttribCollection col = null;
            if (db != null && !string.IsNullOrEmpty(s.Kind)) db.Collections.TryGetValue((Smackable.ClassKey, AttribDatabase.Hash(s.Kind)), out col);
            e._sOnDetached = FxDatabase.Links(col, "OnDetached");
            e._sOnHitWorld = FxDatabase.Links(col, "OnHitWorld"); e._sOnScrapeWorld = FxDatabase.Links(col, "OnScrapeWorld");
            e._sOnHitObject = FxDatabase.Links(col, "OnHitObject"); e._sOnScrapeObject = FxDatabase.Links(col, "OnScrapeObject");
            e._sOnHitGround = FxDatabase.Links(col, "OnHitGround"); e._sOnScrapeGround = FxDatabase.Links(col, "OnScrapeGround");
            return e;
        }

        static float Ramp(float x, float min, float max)
        {
            if (max <= min) return x >= min ? 1f : 0f;
            return Mathf.Clamp01((x - min) / (max - min));
        }

        struct PendingSmack { public Smackable S; public float Impact, Time; public Vector3 Point, Normal; }
        readonly List<PendingSmack> _smacks = new List<PendingSmack>();

        /// <summary>EffectsSmackable::OnCollision with objADetached: once the prop the car hit comes loose (Smackable gives it
        /// a Rigidbody), its OnDetached record with the closing speed of that hit.</summary>
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
                    var fx = ForSmackable(p.S);
                    if (fx._detachedFired) continue;
                    fx._detachedFired = true;
                    var link = EmitterSystem.Db.Lookup(fx._sOnDetached, CarBody);
                    if (!link.HasValue) continue;
                    float k = Ramp(p.Impact, link.Value.MinSpeed, link.Value.MaxSpeed);
                    if (k > 0f) CollisionAudio.OneShot(link.Value.EffectKey, p.Point, -p.Normal, k, p.S.transform, p.S, Car, rb.linearVelocity);
                }
                else if (Time.time - p.Time > 0.5f) _smacks.RemoveAt(i);
            }
        }

        void Update()
        {
            if (_smacks.Count > 0) UpdateSmackables();
            // Effects::OnTaskSimulate: pause the scrape 0.2 s after the last contact
            if (!_scraping) return;
            if ((_scrapeTimeOut -= Time.deltaTime) >= 0f) return;
            _scraping = false;
            _scrape.Pause();
        }

        void OnDisable() { _scraping = false; _scrape.Pause(); }

        void OnCollisionEnter(Collision c) => Collide(c, true);
        void OnCollisionStay(Collision c) => Collide(c, false);

        void Collide(Collision c, bool enter)
        {
            if (c.contactCount == 0 || EmitterSystem.Db == null) return;
            if (!_rb) _rb = GetComponent<Rigidbody>();
            if (c.contactCount == 0) return;   // contact modification can leave a collision with no contacts
            var cp = c.GetContact(0);
            var n = cp.normal;
            var closing = -c.relativeVelocity;
            var other = c.collider;
            var otherCar = c.rigidbody ? c.rigidbody.GetComponent<PlayerCar>() : null;
            var smack = other && !otherCar ? other.GetComponentInParent<Smackable>() : null;
            object self = Car ? (object)Car : Smack;
            object actee = otherCar ? (object)otherCar : smack;
            EffectLink[] hits, scrapes;
            uint surface = 0;
            if (Smack)
            {
                if (!_rb) return;   // still planted (its OnDetached comes through the car that knocks it loose)
                if (otherCar || smack) { hits = _sOnHitObject; scrapes = _sOnScrapeObject; surface = otherCar ? CarBody : 0; }
                else if (Vector3.Dot(n, Vector3.up) > 0.7f || WorldCollision.IsGround(other)) { hits = _sOnHitGround; scrapes = _sOnScrapeGround; }
                else { hits = _sOnHitWorld; scrapes = _sOnScrapeWorld; }
            }
            else
            {
                if (!Car || _fx == null) return;
                if (otherCar) { hits = _fx.OnHitObject; scrapes = _fx.OnScrapeObject; surface = CarBody; }
                else if (smack)
                {
                    hits = _fx.OnHitObject;
                    scrapes = _fx.OnScrapeObject;
                    if (enter) _smacks.Add(new PendingSmack { S = smack, Impact = closing.magnitude, Point = cp.point, Normal = n, Time = Time.time });
                }
                else if (Vector3.Dot(n, Vector3.up) > 0.7f || WorldCollision.IsGround(other))
                {
                    bool belly = Car.WheelsOnGround > 2 && Vector3.Dot(transform.up, n) > 0.7f;
                    hits = belly ? _fx.OnBottomOut : _fx.OnHitGround;
                    scrapes = belly ? _fx.OnBottomScrape : _fx.OnScrapeGround;
                }
                else { hits = _fx.OnHitWorld; scrapes = _fx.OnScrapeWorld; }
            }
            var vel = _rb ? _rb.linearVelocity : Vector3.zero;
            if (enter && closing.sqrMagnitude > 1f)
            {
                var link = EmitterSystem.Db.Lookup(hits, surface);
                if (link.HasValue)
                {
                    // COLLISION_INFO impulseA = force x 1/mass (RigidBody.cpp:759/1116): this body's speed change, from the
                    // contact impulse Unity resolved; the normal closing speed when there is none (kinematic or custom-resolved)
                    float impulse = _rb && _rb.mass > 0f && c.impulse.sqrMagnitude > 1e-6f ? c.impulse.magnitude / _rb.mass : Mathf.Abs(Vector3.Dot(closing, n));
                    float k = Ramp(impulse, link.Value.MinSpeed, link.Value.MaxSpeed);
                    if (k > 0f) CollisionAudio.OneShot(link.Value.EffectKey, cp.point, n, k, transform, self, actee, vel);
                }
            }
            var sliding = closing - n * Vector3.Dot(closing, n);
            float speed = sliding.magnitude;
            if (speed > 1f)
            {
                var link = EmitterSystem.Db.Lookup(scrapes, surface);
                if (!link.HasValue || speed < link.Value.MinSpeed || link.Value.MaxSpeed <= link.Value.MinSpeed) { _scraping = false; _scrape.Pause(); return; }
                float k = Mathf.Clamp((speed - link.Value.MinSpeed) / (link.Value.MaxSpeed - link.Value.MinSpeed), 0.1f, 1f);
                _scrape.Update(link.Value.EffectKey, cp.point, n, k, transform, self, actee, vel);
                _scraping = true;
                _scrapeTimeOut = ScrapeTimeOut;
            }
        }
    }
}
