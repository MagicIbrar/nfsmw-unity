using System.Collections.Generic;
using UnityEngine;

namespace NfsMw.Game.Police
{
    /// <summary>
    /// AIRoadBlock (AI/Common/AIRoadBlock.cpp; ai_cops.md §3.4): the parked cars, the sawhorses and spike belts, the
    /// dodge flag and the "cheat" timer (the perp loitering within 300 m of the centre).
    /// </summary>
    public sealed class CopRoadblock
    {
        public readonly List<CopCar> Cars = new List<CopCar>();
        public readonly List<GameObject> Props = new List<GameObject>();
        public Vector3 Centre, Dir;
        public int SpikeStrips;
        public bool Dodged, Heavy;
        public int CopsDamaged, CopsDestroyed;
        public bool PerpCheating { get; private set; }
        float _cheatTime;

        public int NumCops => Cars.Count;

        /// <summary>GetMinDistanceToTarget (:60-125): nearest cop (3D and level xz), engagement within 300 m, the cheat timer.</summary>
        public float MinDistanceToTarget(Vector3 target, float dt, bool cooldown, out float distXZ, out CopCar nearest)
        {
            float min3 = float.MaxValue, minxz = float.MaxValue;
            nearest = null;
            foreach (var c in Cars)
            {
                if (!c || c.Destroyed) continue;
                var p = c.transform.position;
                float d3 = Vector3.Distance(p, target), dxz = new Vector2(p.x - target.x, p.z - target.z).magnitude;
                if (d3 < min3) { min3 = d3; nearest = c; }
                if (Mathf.Abs(p.y - target.y) < 1.5f && dxz < minxz) minxz = dxz;
                if (d3 < 300f) c.SetWithinEngagementRadius();
            }
            distXZ = minxz;
            if (dt > 0)
            {
                PerpCheating = false;
                if (minxz < 500f)
                {
                    if ((target - Centre).sqrMagnitude < 90000f)
                    {
                        _cheatTime += dt;
                        if (_cheatTime > 20f || cooldown) PerpCheating = true;
                    }
                    else _cheatTime = 0;
                }
            }
            return min3;
        }

        /// <summary>The perp is past the centre plane (AICopManager::UpdateRoadBlocks, :1970-2012).</summary>
        public bool PerpPast(Vector3 target) => Vector3.Dot((Centre - target).normalized, Dir) < 0;

        /// <summary>ReleaseAllSmackables.</summary>
        public void ReleaseProps()
        {
            foreach (var p in Props) if (p) Object.Destroy(p);
            Props.Clear();
        }

        /// <summary>
        /// XO_Sawhorse_1b_00 / XO_SpikeBelt_1b_DW_00 stand-ins: the scenery models are not loaded here, so a sawhorse is
        /// a light pushable box and a spike belt a flat dark strip [?: sizes guessed].
        /// </summary>
        public static GameObject MakeProp(ElementType type, Vector3 pos, Vector3 facing, Vector3 normal)
        {
            var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var rot = Quaternion.LookRotation(Vector3.ProjectOnPlane(facing, normal).normalized, normal);
            go.transform.SetPositionAndRotation(pos, rot);
            var r = go.GetComponent<Renderer>();
            var shader = Shader.Find("Standard");
            if (type == ElementType.Spikes)
            {
                go.name = "XO_SpikeBelt_1b_DW_00";
                go.transform.localScale = new Vector3(6f, 0.06f, 0.5f);
                go.transform.position += normal * 0.03f;
                if (shader) r.sharedMaterial = new Material(shader) { color = new Color(0.08f, 0.08f, 0.08f) };
                Object.Destroy(go.GetComponent<Collider>());
                go.AddComponent<SpikeStrip>();
            }
            else
            {
                go.name = "XO_Sawhorse_1b_00";
                go.transform.localScale = new Vector3(2f, 1f, 0.25f);
                go.transform.position += normal * 0.5f;
                if (shader) r.sharedMaterial = new Material(shader) { color = new Color(0.95f, 0.45f, 0.1f) };
                var rb = go.AddComponent<Rigidbody>();
                rb.mass = 20f;   // smackable "sawhorse" MASS not read [?]
            }
            return go;
        }
    }

    /// <summary>
    /// A spike belt (Physics/Behaviors/SpikeStrip.cpp; ai_cops.md §4): every grounded, undamaged wheel of any car over
    /// it is boxed (0.15 × r × r at the wheel centre, swept back by v·dt); a box touching the belt punctures that wheel.
    /// </summary>
    public sealed class SpikeStrip : MonoBehaviour
    {
        public int Punctures;
        static readonly Collider[] Found = new Collider[16];

        void FixedUpdate()
        {
            var t = transform;
            var half = new Vector3(t.localScale.x * 0.5f, 0.5f, t.localScale.z * 0.5f);
            int n = Physics.OverlapBoxNonAlloc(t.position, half + new Vector3(1.5f, 1f, 1.5f), Found, t.rotation, ~0, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < n; i++)
            {
                var rb = Found[i].attachedRigidbody;
                var car = rb ? rb.GetComponent<PlayerCar>() : null;
                if (!car || car.Spec == null) continue;
                var tires = car.GetComponent<TireState>();
                for (int w = 0; w < 4; w++)
                {
                    if (tires && tires.State(w) != TireState.Tire.None) continue;
                    var hub = car.WheelVisuals[w] ? car.WheelVisuals[w].position : (Vector3?)null;
                    if (hub == null) continue;
                    float r = w < 2 ? car.Spec.TireRadiusFront : car.Spec.TireRadiusRear;
                    var v = rb.linearVelocity * Time.fixedDeltaTime;
                    if (!Touches(hub.Value, r, half) && !Touches(hub.Value - v * 0.5f, r, half) && !Touches(hub.Value - v, r, half)) continue;
                    if (!tires) tires = car.gameObject.AddComponent<TireState>();
                    tires.Puncture(w);
                    Punctures++;
                }
            }
        }

        /// <summary>The wheel box (0.15 wide, r deep and high around the hub) against the belt's box.</summary>
        bool Touches(Vector3 hub, float r, Vector3 half)
        {
            var l = transform.InverseTransformPoint(hub);
            // local space is scaled: convert to metres
            var s = transform.localScale;
            var m = new Vector3(l.x * s.x, l.y * s.y, l.z * s.z);
            return Mathf.Abs(m.x) < half.x + 0.075f && Mathf.Abs(m.z) < half.z + r && m.y > -r && m.y < r + 0.15f;
        }
    }

    /// <summary>
    /// DamageRacer tire state (Physics/Behaviors/DamageRacer.cpp:54-100): Puncture → PUNCTURED, and BLOWN 0.5 s later
    /// (180 s with run-flat tires, not modelled). A blown wheel brakes fully with ×0.3 traction (PlayerCar.BlownTires).
    /// </summary>
    public sealed class TireState : MonoBehaviour
    {
        public enum Tire { None, Punctured, Blown }
        readonly Tire[] _state = new Tire[4];
        readonly float[] _timer = new float[4];
        PlayerCar _car;
        public const float BlowOutTime = 0.5f;

        public Tire State(int wheel) => _state[wheel];
        public int BlownCount { get { int n = 0; foreach (var s in _state) if (s == Tire.Blown) n++; return n; } }

        public void Puncture(int wheel)
        {
            if (_state[wheel] != Tire.None) return;
            _state[wheel] = Tire.Punctured;
            _timer[wheel] = BlowOutTime;
            Debug.Log($"NFSMW police: {name} tire {wheel} punctured");
        }

        void FixedUpdate()
        {
            if (!_car) _car = GetComponent<PlayerCar>();
            for (int i = 0; i < 4; i++)
            {
                if (_state[i] != Tire.Punctured) continue;
                _timer[i] -= Time.fixedDeltaTime;
                if (_timer[i] > 0) continue;
                _state[i] = Tire.Blown;
                if (_car) _car.BlownTires |= 1 << i;
            }
        }

        /// <summary>A repair or respawn clears the damage.</summary>
        public void Repair()
        {
            for (int i = 0; i < 4; i++) { _state[i] = Tire.None; _timer[i] = 0; }
            if (!_car) _car = GetComponent<PlayerCar>();
            if (_car) _car.BlownTires = 0;
        }
    }
}
