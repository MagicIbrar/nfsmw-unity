using System;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.CarRender
{
    /// <summary>
    /// The visual side of DamageVehicle (Physics/Behaviors/DamageVehicle.cpp; exe OnCollision 0x6A85B0, OnImpact
    /// 0x69C030): every collision picks a damage zone from the contact arm and normal, raises that zone's level to
    /// min(2, impulse / (FORCE·1000) · VisualScale) and hands the packed DamageZone.Info to CarRenderInfo.SetDamageInfo.
    /// The hit points (HIT_POINTS / HP_THRESHOLD) are tracked too, for IsDestroyed (every light broken). The car's
    /// damagespecs collection is the pvehicle's `damagespecs` ref (racers, cops, copsuv, traffic, …).
    /// Lives on the car root next to the Rigidbody, so Unity hands it the car's collisions (no PlayerCar hook needed).
    /// </summary>
    public sealed class CarDamage : MonoBehaviour
    {
        /// <summary>
        /// The gameplay option FEDatabase→settings +0x3A that DamageRacer::CanDamageVisuals (0x68CD40) tests (decomp
        /// GameplaySettings.Damage). [?] default on: the port has no such option yet.
        /// </summary>
        public static bool VisualsEnabled = true;

        public const uint DamageSpecsClass = 0xC1F0B434;

        public float Force = 20f, HitPoints, HpThreshold, SuppressDist;
        /// <summary>DamageScaleRecord {VisualScale, HitPointScale} per zone (damagespecs DZ_* fields).</summary>
        public readonly Vector2[] Records = new Vector2[DamageZone.Max];
        /// <summary>mZoneDamage (3 bits per zone).</summary>
        public uint ZoneDamage { get; private set; }
        public float DamageTotal { get; private set; }
        public bool Destroyed => DamageTotal >= 1f;
        /// <summary>DamageCopCar: smackables only count when immobile or heavier than 2000 kg.</summary>
        public bool CopRules;
        /// <summary>DamageRacer: CanDamageVisuals follows the gameplay option (cops/traffic always damage).</summary>
        public bool RacerRules;
        /// <summary>Fired with the zone and its new level when a zone's visual level rises (EventSequencer DAMAGE_n).</summary>
        public event Action<int, int> ZoneDamaged;
        /// <summary>
        /// Fired with (zone, old level, new level) when a zone's visual level rises: DamageVehicle::OnImpact sends the
        /// zone's sequencer system DAMAGE_(j+1) for every j in [old, new) (DamageZone::GetDamageStimulus, table 0x905D0C).
        /// </summary>
        public event Action<int, int, int> ZoneLevelRaised;
        /// <summary>DamageVehicle::ResetDamage: ResetParts (fragments released) and RESET_DAMAGE to the sequencer.</summary>
        public event Action DamageReset;
        /// <summary>mLastImpactSpeed: the car's velocity at its last counted collision (DamageVehicle::SetDynamicData
        /// hands it to the sequencer events as fVelocity). Unity world space [?: Unity reports the post-contact velocity].</summary>
        public Vector3 LastImpactVelocity { get; private set; }

        CarRenderInfo _info;
        Rigidbody _rb;
        Vector3 _halfExtents = new Vector3(0.9f, 0.7f, 2.2f);
        static System.Reflection.MemberInfo _noCarEffect;
        static bool _noCarEffectLooked;

        /// <summary>Adds the component and reads the car's damagespecs (pvehicle field `damagespecs`, a RefSpec).</summary>
        public static CarDamage Attach(GameObject root, string car, CarRenderInfo info)
        {
            var d = root.AddComponent<CarDamage>();
            d._info = info;
            d.CopRules = info != null && info.Cop;
            d.RacerRules = info != null && !info.Cop && !info.Traffic;
            d.Load(car.ToLowerInvariant());
            return d;
        }

        void Load(string car)
        {
            var db = GameData.Attributes;
            if (db == null) return;
            var pv = db.Get("pvehicle", car);
            uint key = AttribDatabase.Hash(RacerRules ? "racers" : CopRules ? "cops" : "traffic");
            if (pv != null && pv.TryGet("damagespecs", out int at, out _, out _) && Chunks.U32(pv.Source.Data, at) == DamageSpecsClass)
                key = Chunks.U32(pv.Source.Data, at + 4);
            if (!db.Collections.TryGetValue((DamageSpecsClass, key), out var ds)) return;
            Force = ds.Float("FORCE", 0, Force);
            HitPoints = ds.Float("HIT_POINTS", 0, 0f);
            HpThreshold = ds.Float("HP_THRESHOLD", 0, 0f);
            SuppressDist = ds.Float("SUPPRESS_DIST", 0, 0f);
            for (int z = 0; z < DamageZone.Max; z++)
                Records[z] = new Vector2(ds.Float(DamageZone.Names[z], 0, 0f), ds.Float(DamageZone.Names[z], 1, 0f));
        }

        void Awake() => _rb = GetComponent<Rigidbody>();

        void Start()
        {
            if (!_info) _info = GetComponent<CarRenderInfo>();
            var box = GetComponent<BoxCollider>();
            if (box) _halfExtents = Vector3.Scale(box.size * 0.5f, transform.lossyScale);
        }

        /// <summary>CanDamageVisuals: DamageVehicle always, DamageRacer only with the gameplay option on.</summary>
        bool CanDamageVisuals => !RacerRules || VisualsEnabled;

        void OnCollisionEnter(Collision c)
        {
            if (!_rb) _rb = GetComponent<Rigidbody>();
            if (!_rb || c.contactCount == 0) return;
            // DamageVehicle::OnCollision: |closing velocity| < 1 → no damage
            if (c.relativeVelocity.magnitude < 1f) return;
            // impact force = impulse·mass of the sim's Δv (= Unity's impulse); doubled against another object
            float force = c.impulse.magnitude;
            bool obj = c.rigidbody != null || c.collider.GetComponentInParent<Smackable>() != null;
            if (obj) force += force;
            LastImpactVelocity = _rb.linearVelocity;
            if (c.contactCount == 0) return;   // contact modification can leave a collision with no contacts
            var cp = c.GetContact(0);
            Vector3 arm = transform.InverseTransformPoint(cp.point) - transform.InverseTransformPoint(_rb.worldCenterOfMass);
            Vector3 n = transform.InverseTransformDirection(cp.normal);
            OnImpact(arm, n, force, c.collider);
        }

        /// <summary>DamageVehicle::OnImpact (DamageVehicle.cpp:150-260) in Unity car space (x right, y up, z forward = sim car space).</summary>
        public void OnImpact(Vector3 arm, Vector3 normal, float force, Collider other)
        {
            if (SuppressDist > 0f && Camera.main && (Camera.main.transform.position - transform.position).magnitude > SuppressDist) return;
            var sm = other ? other.GetComponentInParent<Smackable>() : null;
            if (sm)
            {
                if (NoCarEffect(sm)) return;
                if (CopRules)
                {
                    var orb = sm.GetComponent<Rigidbody>();
                    bool immobile = !orb || orb.isKinematic;
                    if (!immobile && (orb ? orb.mass : sm.Mass) <= 2000f) return;   // Tweak_MinMassSmackableForCopDamage
                }
            }
            if (Force <= 0f) return;
            var raised = (zone: -1, from: 0, to: 0);
            int zone = DamageZone.Classify(arm.x, arm.y, arm.z, normal.x, normal.y, normal.z, _halfExtents.x, _halfExtents.y, _halfExtents.z);
            var rec = Records[zone];
            float ratio = force / (Force * 1000f);
            int level = DamageZone.VisualLevel(force, Force, rec.x);
            if (HitPoints > 0f && DamageTotal < 1f)
            {
                float points = ratio * rec.y / HitPoints, threshold = HpThreshold / HitPoints;
                if (points > threshold) DamageTotal += points;
            }
            if (level > 0 && CanDamageVisuals)
            {
                int cur = DamageZone.Get(ZoneDamage, zone);
                if (cur <= 5 && level > cur)
                {
                    ZoneDamage = DamageZone.Set(ZoneDamage, zone, level);
                    raised = (zone, cur, level);
                    ZoneDamaged?.Invoke(zone, level);
                }
            }
            Push();
            // the sequencer stimuli after the render update, so a fragment spawned at once copies the dented model the
            // exe's VehicleFragmentConn finds on its first update (the sim sends them inside OnImpact)
            if (raised.zone >= 0) ZoneLevelRaised?.Invoke(raised.zone, raised.from, raised.to);
        }

        void Push()
        {
            if (!_info) _info = GetComponent<CarRenderInfo>();
            if (_info) _info.SetDamageInfo(RacerRules && !VisualsEnabled ? 0u : ZoneDamage);   // DamageRacer::GetZoneDamage
        }

        /// <summary>DamageVehicle::Destroy: every zone to 6 (visuals allowed) and the total to 1.</summary>
        public void Destroy()
        {
            if (DamageTotal >= 1f) return;
            if (CanDamageVisuals)
                for (int z = 0; z < DamageZone.Max; z++) ZoneDamage = DamageZone.Set(ZoneDamage, z, 6);
            DamageTotal = 1f;
            Push();
        }

        /// <summary>DamageVehicle::ResetDamage: zones, lights and hit points cleared.</summary>
        public void ResetDamage()
        {
            ZoneDamage = 0;
            DamageTotal = 0f;
            if (_info) _info.BrokenLights = 0;   // mLightDamage = 0
            DamageReset?.Invoke();
            Push();
        }

        /// <summary>smackable NO_CAR_EFFECT (DamageVehicle.cpp:158-163), when the Smackable component exposes it [ours: reflection].</summary>
        static bool NoCarEffect(Smackable sm)
        {
            if (!_noCarEffectLooked)
            {
                _noCarEffectLooked = true;
                var t = typeof(Smackable);
                _noCarEffect = (System.Reflection.MemberInfo)t.GetProperty("NoCarEffect") ?? t.GetField("NoCarEffect");
            }
            try
            {
                object v = _noCarEffect is System.Reflection.PropertyInfo p ? p.GetValue(sm) : _noCarEffect is System.Reflection.FieldInfo f ? f.GetValue(sm) : null;
                return v is bool b && b;
            }
            catch { return false; }
        }
    }
}
