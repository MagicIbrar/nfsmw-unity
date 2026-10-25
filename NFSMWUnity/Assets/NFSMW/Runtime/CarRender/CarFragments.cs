using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Game.TrafficPhysics;
using UnityEngine;

namespace NfsMw.CarRender
{
    /// <summary>
    /// Parts that come off a car: the vehicle's event sequencer (PhysicsObject engine from the pvehicle `EventSequencer`
    /// StringKey) driving DrawVehicle::SpawnModel (0x6B0500) fragments drawn by VehicleFragmentConn (World/
    /// VehicleFragmentConn.cpp). In the retail data only the cops (cops, copmidsize, copsport, copgto, copsuv, copsuvl,
    /// copsportghost, copgtoghost, copsuvpatrol → engine 'CopCar') and two trailers (TrailerB, TrailerLog) have an
    /// engine: racers, the player and traffic never lose parts (car_render.md §11).
    ///
    /// The 'CopCar' engine (TRACKS\L2RA.BUN pack 5, harness `carrender2 seq CopCar`): DamageVehicle::OnImpact sends the
    /// zone's system (DZ_FRONT …) DAMAGE_(j+1) for each level step; the DZ systems pass 'trigger' (EProcessStimulus) to
    /// the part systems, which fire once each:
    ///   doors   — EPlayObjectEffect "carglass" + ESpawnFragment(DAMAGE_*_DOOR, door bound, "door", hinge joint) at once;
    ///             EKillJoint at the end of the 20 s action: the door hangs on its hinge, then falls off;
    ///   hood / trunk / rear bumper — ESpawnFragment at the end of a 20 s action (the hood / trunk swing on the
    ///             VehiclePartDamage spring meanwhile);
    ///   cop lights — EDamageLights(LIGHT_COPS) + ESpawnFragment(DAMAGE_COP_LIGHTS) at once.
    /// FRONT (DAMAGE_1) → hood; RFRONT (DAMAGE_2) → hood + right door; LFRONT (DAMAGE_2) → hood + left door; REAR
    /// (DAMAGE_2) → bumper + trunk; LREAR / RREAR (DAMAGE_1) → rear door + trunk; LEFT (DAMAGE_1, then DAMAGE_2) → left
    /// front door, then cop lights; RIGHT (DAMAGE_1) → right front door, and a second DAMAGE_1 (never sent: levels stop
    /// at 2) would take the cop lights; TOP (DAMAGE_1) → cop lights. RESET_DAMAGE → EResetSequencer. The
    /// stimulus 0xFFCD8A63 (sent with EXPLOSION to vehicles by the explosion handler 0x677100) fires EDestroyVehicle and
    /// every fragment list at once: <see cref="Explosion"/>.
    /// [?] Fragment physics (Smackable / RBSmackable, joint constraints) are not decompiled: a Unity rigid body with the
    /// part's bound box, MASS from the smackable collection, the car's velocity × fInheritVelocity, and a hinge on the
    /// female joint bound (vertical axis, opening outward to the conical constraint's 52°).
    /// </summary>
    public sealed class CarFragments : MonoBehaviour
    {
        /// <summary>SpawnModel refuses once Vehicle_Part_Count (CarFragment.Count) reaches 61 (DrawVehicle.cpp:207).</summary>
        const int MaxParts = 61;
        const uint SmackableClass = 0xCE70D7DB;
        /// <summary>SpawnModel also refuses while Listable&lt;IModel, InstanceLimit&gt;::Count() &gt; 434 (DrawVehicle.cpp:191).</summary>
        const int MaxModels = 434;

        /// <summary>The live Sim::Model count: vehicle parts plus spawned (hit, not yet released) smackable props [?: the
        /// port's props carry a Smackable component from load; only Spawned ones have an exe IModel].</summary>
        static int ModelCount()
        {
            int n = CarFragment.Count;
            foreach (var s in FindObjectsByType<Smackable>(FindObjectsSortMode.None)) if (s.Spawned && !s.Gone) n++;
            return n;
        }

        static readonly uint EProcessStimulus = AttribDatabase.Hash("EProcessStimulus"), ESpawnFragment = AttribDatabase.Hash("ESpawnFragment"),
            EKillJoint = AttribDatabase.Hash("EKillJoint"), EDamageLights = AttribDatabase.Hash("EDamageLights"),
            EPlayObjectEffect = AttribDatabase.Hash("EPlayObjectEffect"), EDestroyVehicle = AttribDatabase.Hash("EDestroyVehicle"),
            EFireEventList = AttribDatabase.Hash("EFireEventList"), EResetSequencer = AttribDatabase.Hash("EResetSequencer"),
            EHidePart = AttribDatabase.Hash("EHidePart");
        /// <summary>DamageVehicle::ResetDamage stimulus (UCRC32_RESET_DAMAGE, system 'reset' of CopCar).</summary>
        public static readonly uint ResetDamage = AttribDatabase.Hash("RESET_DAMAGE");
        /// <summary>Sent with EXPLOSION to a vehicle in an explosion (0x677100); CopCar's system 0 answers with
        /// EDestroyVehicle + every fragment list.</summary>
        public const uint VehicleExplosion = 0xFFCD8A63;
        static readonly uint[] DamageStimulus =
        {
            AttribDatabase.Hash("DAMAGE_1"), AttribDatabase.Hash("DAMAGE_2"), AttribDatabase.Hash("DAMAGE_3"),
            AttribDatabase.Hash("DAMAGE_4"), AttribDatabase.Hash("DAMAGE_5"), AttribDatabase.Hash("DAMAGE_6"), AttribDatabase.Hash("DAMAGE_7"),
        };

        EventSeqEngine _seq;
        CarRenderInfo _info;
        CarDamage _damage;
        Rigidbody _rb;
        CollisionGeometry.Collection _bounds;
        readonly List<CarFragment> _frags = new List<CarFragment>();
        /// <summary>DrawCar::mParts: hidden part names with a count (HidePart / ShowPart).</summary>
        readonly Dictionary<uint, int> _hidden = new Dictionary<uint, int>();

        /// <summary>Adds the component when the car's pvehicle names an event sequencer engine that exists.</summary>
        public static CarFragments Attach(GameObject root, string car, CarRenderInfo info, CarDamage damage)
        {
            if (!root || info == null || damage == null) return null;
            var pv = GameData.Attributes?.Get("pvehicle", car.ToLowerInvariant());
            string engine = pv?.StringKey("EventSequencer");
            if (string.IsNullOrEmpty(engine)) return null;
            var data = Smackable.WorldSequences.Find(AttribDatabase.Hash(engine));
            if (data == null) return null;
            var f = root.AddComponent<CarFragments>();
            f._info = info;
            f._damage = damage;
            f._rb = root.GetComponent<Rigidbody>();
            f._bounds = VehicleSpecs.Get(car)?.Geometry;
            f._seq = new EventSeqEngine(data, Time.time) { OnEvent = f.OnSeqEvent };
            damage.ZoneLevelRaised += f.OnZoneRaised;
            damage.DamageReset += f.OnReset;
            return f;
        }

        void OnDestroy()
        {
            if (_damage) { _damage.ZoneLevelRaised -= OnZoneRaised; _damage.DamageReset -= OnReset; }
            // the joints hang on this car: their fragments go with it (detached ones live on until KILL_OFF_SCREEN)
            foreach (var f in _frags) if (f && f.Joint) Destroy(f.gameObject);
        }

        /// <summary>DamageVehicle::OnImpact: DAMAGE_(j+1) to the zone's system for j in [old, new) (QUEUE_ALLOW).</summary>
        void OnZoneRaised(int zone, int oldLevel, int newLevel)
        {
            if (_seq == null || zone < 0 || zone >= DamageZone.Names.Length) return;
            uint sys = AttribDatabase.Hash(DamageZone.Names[zone]);
            for (int j = Mathf.Max(0, oldLevel); j < newLevel && j < DamageStimulus.Length; j++) Stimulus(DamageStimulus[j], sys);
        }

        /// <summary>DamageVehicle::ResetDamage: ResetParts (child models released, parts shown) then RESET_DAMAGE.</summary>
        void OnReset()
        {
            foreach (var f in _frags) if (f) Destroy(f.gameObject);
            _frags.Clear();
            foreach (var name in new List<uint>(_hidden.Keys)) ShowPartAll(name);
            _seq?.ProcessStimulus(ResetDamage, Time.time, EventSeqEngine.QueueMode.Allow);
        }

        /// <summary>The explosion handler (0x677100): EXPLOSION then 0xFFCD8A63 to the vehicle's sequencer. One-line hook
        /// for whoever ports explosions hitting cars (ESpawnExplosion).</summary>
        public static void Explosion(GameObject car)
        {
            var f = car ? car.GetComponentInParent<CarFragments>() : null;
            if (!f || f._seq == null) return;
            float t = Time.time;
            f._seq.ProcessStimulus(Smackable.SeqStimuli.Explosion, t, EventSeqEngine.QueueMode.Allow);
            f._seq.ProcessStimulus(VehicleExplosion, t, EventSeqEngine.QueueMode.Allow);
        }

        /// <summary>System::ProcessStimulus on one system (`system` 0 = the whole engine).</summary>
        public void Stimulus(uint stimulus, uint system = 0)
        {
            if (_seq == null) return;
            float t = Time.time;
            if (system == 0) { _seq.ProcessStimulus(stimulus, t, EventSeqEngine.QueueMode.Allow); return; }
            foreach (var s in _seq.Systems) if (s.Id == system) s.ProcessStimulus(stimulus, t, EventSeqEngine.QueueMode.Allow);
        }

        void Update()
        {
            if (_seq != null && _seq.AnySystemInAction()) _seq.Update(Time.time);
            _frags.RemoveAll(f => !f);   // released (KILL_OFF_SCREEN, CarFragment)
        }

        // ---- events ------------------------------------------------------------------------------------------------

        void OnSeqEvent(uint system, SeqEvent ev)
        {
            try
            {
                if (ev.Id == EProcessStimulus)
                {
                    // {fSystemName, fStimulus, fQueueMode}
                    Stimulus(ev.U(8), ev.U(4));
                }
                else if (ev.Id == ESpawnFragment) Spawn(system, ev);
                else if (ev.Id == EKillJoint)
                {
                    // [?] the joint of the part this system spawned (the event's model is the system's context)
                    foreach (var f in _frags) if (f && f.System == system && f.Joint) { Destroy(f.Joint); f.Joint = null; }
                }
                else if (ev.Id == EDamageLights)
                {
                    // IDamageable::DamageLight(VehicleFX::LookupID(fName), true): only while visuals may be damaged
                    int bit = LightId(ev.U(4));
                    if (bit != 0 && _info) _info.BrokenLights |= bit;
                }
                else if (ev.Id == EPlayObjectEffect) PlayEffect(ev);
                else if (ev.Id == EDestroyVehicle) { if (_damage) _damage.Destroy(); }
                else if (ev.Id == EFireEventList) FireList(system, ev);
                else if (ev.Id == EResetSequencer) _seq?.Reset(Time.time);
                else if (ev.Id == EHidePart) HidePart(ev.U(4));
            }
            catch (System.Exception e) { Debug.LogWarning($"NFSMW car fragments: {ev}: {e.Message}"); }
        }

        /// <summary>EFireEventList {u32 'el' tag}: the list's events, fired as if by this system.</summary>
        void FireList(uint system, SeqEvent ev)
        {
            var pack = ev.Pack;
            int o = pack.Record(ev.U(4));
            if (o < 0) return;
            var b = pack.Bytes;
            int n = Chunks.I32(b, o);
            for (int i = 0; i < n && o + 16 + i * 16 + 16 <= b.Length; i++)
            {
                int e = o + 16 + i * 16;
                var sub = new SeqEvent { Id = Chunks.U32(b, e), Size = Chunks.I32(b, e + 4), Data = e + Chunks.I32(b, e + 8), Pack = pack };
                if (sub.Id != EFireEventList) OnSeqEvent(system, sub);
            }
        }

        /// <summary>VehicleFX::LookupID: the StringKey hash of LIGHT_* names → the VehicleFX::ID bits.</summary>
        static int LightId(uint name)
        {
            for (int i = 0; i < LightNames.Length; i++) if (AttribDatabase.Hash(LightNames[i].name) == name) return LightNames[i].bit;
            return 0;
        }

        static readonly (string name, int bit)[] LightNames =
        {
            ("LIGHT_LHEAD", 1), ("LIGHT_RHEAD", 2), ("LIGHT_CHEAD", 4), ("LIGHT_LBRAKE", 8), ("LIGHT_RBRAKE", 0x10), ("LIGHT_CBRAKE", 0x20),
            ("LIGHT_LREVERSE", 0x40), ("LIGHT_RREVERSE", 0x80), ("LIGHT_LRSIGNAL", 0x100), ("LIGHT_RRSIGNAL", 0x200), ("LIGHT_LFSIGNAL", 0x400),
            ("LIGHT_RFSIGNAL", 0x800), ("LIGHT_COPRED", 0x1000), ("LIGHT_COPBLUE", 0x2000), ("LIGHT_COPWHITE", 0x4000), ("LIGHT_COPS", 0x7000),
            ("LIGHT_LSIGNAL", 0x500), ("LIGHT_RSIGNAL", 0xA00), ("LIGHT_HEADLIGHTS", 7), ("LIGHT_REVERSE", 0xC0), ("LIGHT_BRAKELIGHTS", 0x38),
        };

        /// <summary>EPlayObjectEffect {fEffect (string), fPosition (bound name), fIntensity, fID, fTracking, fAllowHiddenSpawn}.</summary>
        void PlayEffect(SeqEvent ev)
        {
            string fx = ev.S(4);
            var emitters = string.IsNullOrEmpty(fx) ? null : Effects.EmitterSystem.Instance;
            if (emitters == null) return;
            var frame = Effects.FxFrame.Of(transform);
            if (_bounds != null && _bounds.Find(ev.U(8), out var b)) frame.P = ModelToWorld(b.Pivot);
            else frame.P = Body ? Body.position : transform.position;   // [?] position names that are no bound: the car
            Vector3 vel = _rb ? Effects.FxFrame.G(_rb.linearVelocity) : Vector3.zero;
            emitters.Fire(AttribDatabase.Hash(fx), frame, Mathf.Max(ev.F(12), 0.01f), vel);
        }

        // ---- fragments (DrawVehicle::SpawnModel 0x6B0500, VehicleFragmentConn::UpdateModel) -----------------------------

        Transform Body => _info ? _info.transform.Find("Body") : null;

        /// <summary>Model space (sim axes x right, y up, z forward, origin at the ground under the body) → world.</summary>
        Vector3 ModelToWorld(Vec3 p)
        {
            var body = Body;
            var root = transform;
            Vector3 local = (body ? body.localPosition : Vector3.zero) + new Vector3(p.X, p.Y, p.Z);
            return root.TransformPoint(local);
        }

        /// <summary>
        /// ESpawnFragment {fPartName, fCollisionName, fAttributeName, fJointName, fAllowDupes, fInheritVelocity,
        /// fHideParent, fHidePart, fAllowHiddenSpawn, fNoPhysics, fSimplePhysics}: a child model of the car with the
        /// collision node's bound and the smackable attributes; the fragment draws the slot's current model
        /// (VehicleFragmentConn: vrc->FindPart(slot), the car's replacement textures and CARSKIN material).
        /// </summary>
        void Spawn(uint system, SeqEvent ev)
        {
            uint part = ev.U(4), colName = ev.U(8), attrName = ev.U(12), jointName = ev.U(16);
            bool allowDupes = ev.U(20) != 0, hidePart = ev.U(32) != 0;
            float inherit = ev.F(24);
            if (!allowDupes) foreach (var f in _frags) if (f && f.PartName == part) return;
            if (CarFragment.Count >= MaxParts || ModelCount() > MaxModels || _bounds == null || !_bounds.Find(colName, out var bound)) return;
            int slot = SlotOf(part);
            if (slot < 0) return;
            var drawn = _info ? _info.FindCarPart(slot) : null;
            if (!drawn) return;   // VehicleFragmentConn: no model for the slot → the fragment is dropped
            var body = Body;
            if (!body) return;

            var go = new GameObject("Fragment " + VehiclePartDamageMap.SlotNames[slot]) { layer = gameObject.layer };
            go.transform.SetPositionAndRotation(transform.TransformPoint(body.localPosition), transform.rotation);
            // the part model in model space (the swing of AnimatePart is not part of the fragment's matrix)
            var model = new GameObject("Model").transform;
            model.SetParent(go.transform, false);
            model.localRotation = body.localRotation;
            var copy = Instantiate(drawn, model, false);
            copy.name = drawn.name;
            copy.transform.localPosition = Vector3.zero;
            copy.transform.localRotation = Quaternion.identity;
            copy.SetActive(true);

            // the bound box (sim frame = this frame), oriented by its quaternion [?: handedness of the stored rotation]
            var colGo = new GameObject("Bound").transform;
            colGo.SetParent(go.transform, false);
            colGo.localPosition = new Vector3(bound.Pivot.X, bound.Pivot.Y, bound.Pivot.Z);
            var q = new Quaternion(bound.Qx, bound.Qy, bound.Qz, bound.Qw);
            colGo.localRotation = q.w == 0f && q.x == 0f && q.y == 0f && q.z == 0f ? Quaternion.identity : Quaternion.Normalize(q);
            colGo.gameObject.layer = go.layer;
            var box = colGo.gameObject.AddComponent<BoxCollider>();
            box.size = new Vector3(bound.HalfDims.X, bound.HalfDims.Y, bound.HalfDims.Z) * 2f;

            float mass = 50f, kill = 8f;
            var attrs = GameData.Attributes;
            if (attrs != null && attrs.Collections.TryGetValue((SmackableClass, attrName), out var sm))
            {
                mass = sm.Float("MASS", 0, mass);
                kill = sm.Float("KILL_OFF_SCREEN", 0, kill);
            }
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Mathf.Max(1f, mass);
            rb.centerOfMass = colGo.localPosition;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            if (_rb)
            {
                rb.linearVelocity = _rb.GetPointVelocity(colGo.position) * inherit;
                rb.angularVelocity = _rb.angularVelocity * inherit;
            }
            // the fragment does not collide with its own car [?: RBSmackable vs the parent's bounds]
            foreach (var c in GetComponentsInChildren<Collider>()) Physics.IgnoreCollision(box, c);

            var frag = go.AddComponent<CarFragment>();
            frag.PartName = part;
            frag.System = system;
            frag.KillOffScreen = kill;
            if (jointName != 0 && _rb && FindFemaleJoint(jointName, out var jb))
            {
                // a door: hinged on the car's female joint (vertical post), opening outward [?: ConstraintConical limits]
                var hinge = go.AddComponent<HingeJoint>();
                hinge.connectedBody = _rb;
                hinge.autoConfigureConnectedAnchor = true;
                hinge.anchor = new Vector3(jb.Pivot.X, jb.Pivot.Y, jb.Pivot.Z);
                hinge.axis = Vector3.up;
                bool left = bound.Pivot.X < 0f;
                hinge.useLimits = true;
                hinge.limits = left ? new JointLimits { min = 0f, max = 52f } : new JointLimits { min = -52f, max = 0f };
                hinge.enableCollision = false;
                frag.Joint = hinge;
            }
            _frags.Add(frag);
            if (hidePart) HidePart(part);
        }

        bool FindFemaleJoint(uint name, out CollisionGeometry.Bound b)
        {
            foreach (var x in _bounds.Bounds)
                if (x.Name == name && (x.Flags & CollisionGeometry.BoundFlags.JointFemale) != 0) { b = x; return true; }
            return _bounds.Find(name, out b);
        }

        /// <summary>DrawCar::HidePart: counted per part name; the render side hides the CAR_PART_ID's slot model.</summary>
        void HidePart(uint part)
        {
            _hidden.TryGetValue(part, out int n);
            _hidden[part] = n + 1;
            int slot = SlotOf(part);
            if (slot >= 0 && _info) _info.HideCarPart(slot, true);
        }

        void ShowPartAll(uint part)
        {
            _hidden.Remove(part);
            int slot = SlotOf(part);
            if (slot >= 0 && _info) _info.HideCarPart(slot, false);
        }

        /// <summary>GetCarPartIDFromCrc for the damage parts (CarPartNames.cpp: "DAMAGE_&lt;SLOT&gt;"): the CARSLOTID_DAMAGE_* index.</summary>
        static int SlotOf(uint partCrc)
        {
            var names = VehiclePartDamageMap.SlotNames;
            for (int i = 1; i < names.Length; i++) if (names[i] != null && AttribDatabase.Hash("DAMAGE_" + names[i]) == partCrc) return i;
            return -1;
        }
    }
}
