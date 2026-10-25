using UnityEngine;

namespace NfsMw.Game.TrafficPhysics
{
    /// <summary>
    /// Physics/Behaviors/DamageVehicle.cpp (speed.exe Construct 0x6BACC0, OnCollision 0x6A85B0, OnImpact 0x69C030,
    /// OnTaskSimulate 0x68CB50, SetShockForce 0x68CBB0, SetInShock 0x68CC10): the damage behaviour of traffic and
    /// trailers. A hit (closing speed ≥ 1 m/s) picks a DamageZone from where it landed on the body box and how the
    /// normal faces (thresholds 0.5 of the dimension, 0.8 of the normal); force / (FORCE × 1000) × VisualScale gives an
    /// impact level (≤ 6) and a damage level (≤ 2) per zone; × HitPointScale / HIT_POINTS adds to the total (over
    /// HP_THRESHOLD), which past 1 destroys the vehicle (inputs then forced to full brake and e-brake). Object hits
    /// also shock it: Δv / SHOCK_FORCE (over 0.2) holds a timer that drains over SHOCK_TIME, during which traffic
    /// AI drops to neutral (AIVehicle::OnGasBrake). Beyond SUPPRESS_DIST from the camera hits are ignored.
    /// Every traffic damagespecs has HitPointScale 0, so traffic is never destroyed by hits, only marked.
    /// </summary>
    public sealed class VehicleDamage
    {
        /// <summary>DamageZone::ID.</summary>
        public enum Zone { Front, Rear, Left, Right, LFront, RFront, LRear, RRear, Top, Bottom }

        readonly VehicleSpecs _s;
        public readonly int[] ZoneDamage = new int[10];
        public float DamageTotal { get; private set; }
        public float ShockTimer { get; private set; }
        public bool Destroyed => DamageTotal >= 1f;
        public bool InShock => ShockTimer > 0f;
        /// <summary>Raised per impact: zone, impact level (0..6), new damage level (0..2) [ours: for the damage renderer].</summary>
        public event System.Action<Zone, int, int> Impact;

        public VehicleDamage(VehicleSpecs s) { _s = s; }

        public void Reset() => ShockTimer = 0f;

        /// <summary>DamageVehicle::ResetDamage (DamageCopCar::ResetDamage also clears mFlippedOver).</summary>
        public void ResetDamage()
        {
            System.Array.Clear(ZoneDamage, 0, ZoneDamage.Length);
            ShockTimer = 0f;
            DamageTotal = 0f;
            FlippedOver = 0f;
        }

        // ---- DamageCopCar (DamageCopCar.cpp; ctor 0x6B9C.., OnTask, CheckUpright, OnImpact) ----

        const float Tweak_DamageCop_UprightDot = 0.5f, Tweak_DamageCop_GroundDot = 0.5f, Tweak_DamageCop_FlippedOverDeathTime = 6f;
        /// <summary>DamageCopCar::mFlippedOver (s).</summary>
        public float FlippedOver { get; private set; }

        /// <summary>
        /// DamageCopCar::CheckUpright (its "Physics" task every 0.1 s, skipped while destroyed, animating, during an NIS or
        /// paused): while modelling and not every wheel is down, flipped = up·groundNormal &lt; 0.5 and up.y &lt; 0.5; flipped
        /// time past 6 s → Destroy(); flipped while the car's causality (ICause) is the player's vehicle → Destroy() at once.
        /// </summary>
        public void CheckUpright(float dT, bool modeling, bool allWheelsDown, Vector3 up, Vector3 groundNormal, bool causedByPlayer)
        {
            if (!modeling) return;
            bool flipped = false;
            if (!allWheelsDown && Vector3.Dot(up, groundNormal) < Tweak_DamageCop_GroundDot && up.y < Tweak_DamageCop_UprightDot) flipped = true;
            FlippedOver = flipped ? FlippedOver + dT : 0f;
            if (FlippedOver > Tweak_DamageCop_FlippedOverDeathTime) Destroy();
            if (flipped && causedByPlayer) Destroy();
        }

        /// <summary>DamageVehicle::OnTaskSimulate: the shock wears off over SHOCK_TIME.</summary>
        public void Simulate(float dT)
        {
            if (ShockTimer > 0f && _s.ShockTime > 0f)
            {
                float recover = dT / _s.ShockTime;
                ShockTimer = ShockTimer > recover ? ShockTimer - recover : 0f;
            }
            else ShockTimer = 0f;
        }

        /// <summary>
        /// DamageVehicle::OnCollision. `impulse` is the contact impulse on this body (N·s), `localArm` the hit point in the
        /// body frame, `normal` the world normal pushing this body away, `objectHit` another movable body.
        /// </summary>
        public void OnCollision(float closingSpeed, float impulse, float mass, Vector3 localArm, Vector3 localNormal, bool objectHit, bool noCarEffect, Vector3 position)
        {
            if (closingSpeed < 1f) return;
            float force = impulse;   // impulseA · mass: the impulse (N·s) [?: COLLISION_INFO.impulseA taken as Δv]
            if (objectHit)
            {
                SetShockForce(impulse, mass);
                force += force;
            }
            OnImpact(localArm, localNormal, force, position, noCarEffect);
        }

        void OnImpact(Vector3 arm, Vector3 n, float force, Vector3 position, bool noCarEffect)
        {
            if (_s.SuppressDist > 0f)
            {
                var cam = Camera.main;
                if (cam && Vector3.Distance(cam.transform.position, position) > _s.SuppressDist) return;
            }
            if (noCarEffect) return;
            float strength = _s.DamageForce * 1000f;
            if (strength <= 0f) return;
            var dim = _s.Dimension;
            const float dir = 0.8f, zone = 0.5f;
            Zone z;
            if (arm.z > dim.z * zone)
                z = arm.x > dim.x * zone && -dir > n.x ? Zone.RFront : arm.x < -dim.x * zone && dir < n.x ? Zone.LFront : Zone.Front;
            else if (arm.z < -dim.z * zone)
                z = arm.x > dim.x * zone && -dir > n.x ? Zone.RRear : arm.x < -dim.x * zone && dir < n.x ? Zone.LRear : Zone.Rear;
            else if (arm.y > dim.y * zone && n.y < -dir) z = Zone.Top;
            else if (arm.y < -dim.y * zone && dir < n.y) z = Zone.Bottom;
            else z = arm.x > 0f ? Zone.Right : Zone.Left;
            var rec = _s.Zones[(int)z];
            float ratio = force / strength;
            int impact = Mathf.Min(6, (int)(ratio * rec.x));
            int level = Mathf.Min(2, (int)(ratio * rec.x));
            if (_s.HitPoints > 0f && DamageTotal < 1f)
            {
                float pts = ratio * rec.y / _s.HitPoints, threshold = _s.HpThreshold / _s.HitPoints;
                if (pts > threshold) DamageTotal += pts;
            }
            if (level > 0 && ZoneDamage[(int)z] <= 5 && level > ZoneDamage[(int)z]) ZoneDamage[(int)z] = level;
            Impact?.Invoke(z, impact, ZoneDamage[(int)z]);
        }

        /// <summary>DamageVehicle::Destroy.</summary>
        public void Destroy()
        {
            if (DamageTotal >= 1f) return;
            for (int i = 0; i < ZoneDamage.Length; i++) ZoneDamage[i] = 6;
            DamageTotal = 1f;
        }

        void SetShockForce(float f, float mass)
        {
            if (f > 0f && _s.ShockForce > 0f) SetInShock(1f / _s.ShockForce * (f / Mathf.Max(1f, mass)));
        }

        void SetInShock(float scale)
        {
            if (_s.ShockTime <= 0f || scale <= 0.2f) return;
            ShockTimer = Mathf.Min(Mathf.Max(ShockTimer, scale), 1f);
        }
    }
}
