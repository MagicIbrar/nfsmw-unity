using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>The road graph from L2RA.BUN in Unity space.</summary>
    public static class Roads
    {
        static RoadNetwork _net;

        public static RoadNetwork Net
        {
            get
            {
                if (_net == null)
                {
                    GameData.Mount();
                    _net = RoadNetwork.Read(File.ReadAllBytes(Path.Combine(GameData.Root, "TRACKS", "L2RA.BUN")));
                }
                return _net;
            }
        }

        public static Vector3 Node(int i) => Coords.ToUnity(Net.Nodes[i]);

        public static int Nearest(Vector3 unityPos, float max = 1500f) => Net.Nearest(unityPos.x, unityPos.z, max);

        /// <summary>Unity-space route through game-space waypoints.</summary>
        public static List<Vector3> Through(IList<Vec3> waypoints)
        {
            var r = new List<Vector3>();
            foreach (var p in Net.RouteThrough(waypoints))
            {
                var u = Coords.ToUnity(p);
                if (r.Count == 0 || (r[r.Count - 1] - u).sqrMagnitude > 4f) r.Add(u);
            }
            return r;
        }

        /// <summary>Unity-space route between two Unity positions along the roads.</summary>
        public static List<Vector3> Between(Vector3 from, Vector3 to) =>
            Through(new[] { Coords.ToGame3(from), Coords.ToGame3(to) });
    }

    /// <summary>
    /// Drives a car along a polyline: pure-pursuit steering with a speed-scaled look-ahead, corner speed from the
    /// path curvature ahead, simple car/wall avoidance, and a reset when stuck.
    /// </summary>
    public sealed class AIDriver : MonoBehaviour
    {
        public PlayerCar Car;
        public List<Vector3> Path = new List<Vector3>();
        public bool Loop;
        public float MaxSpeed = 70f;        // m/s
        public float CornerGrip = 1f;       // corner speed scale
        public float Aggression = 1f;       // throttle/brake scale
        /// <summary>Race catch-up −1..1 (FUN_005ff990): scales the top speed by 1 + 0.25·CatchUp.</summary>
        public float CatchUp;
        public Transform Chase;             // when set, drive at this target instead of the path end (cops)
        public float Progress { get; private set; }
        public int Index { get; private set; }
        float[] _cum = new float[0];
        float _stuck;

        // ---- scripted stimuli (Game.SendCharacterStimulus 0x60A6E0, Game.JackKnife 0x605030 via Gameplay/ScriptedWorld.cs) ----
        /// <summary>The last stimulus the scripts sent this vehicle (IVehicleAI::SendStimulus). In the exe the stimulus goes to
        /// the vehicle's EventSequencer (Main/Common/EventSequencer.cpp), not to AIVehicle: no AI has a handler for it, so the
        /// driving does not react; it is only recorded here.</summary>
        public string LastStimulus { get; private set; }
        public float LastStimulusTime { get; private set; } = -1f;
        // AIActionJackKnife (AI/Actions/AIActionJackKnife.cpp; PC 0x4071C0): mForceJackKnife, mAccelTime, SentAudioMsg
        bool _jackKnife, _jackAudioSent;
        float _jackAccelTime;
        const float kActionJackKnifeSpeed = 50f;   // mph

        void OnEnable()
        {
            ScriptedCharacters.StimulusSent += OnStimulus;
            ScriptedCharacters.JackKnifeRequested += OnJackKnife;
        }

        void OnDisable()
        {
            ScriptedCharacters.StimulusSent -= OnStimulus;
            ScriptedCharacters.JackKnifeRequested -= OnJackKnife;
        }

        void OnStimulus(PlayerCar pc, string stimulus)
        {
            if (pc != Car) return;
            LastStimulus = stimulus;
            LastStimulusTime = Time.time;
        }

        /// <summary>
        /// MJackKnife for this vehicle (AIActionJackKnife::MessageJackKnife :149-153): mForceJackKnife, so the next
        /// CanBeAttempted (:84-93) passes without the speed / time-to-impact test, but only for a tractor with a trailer
        /// (mArticulation->GetTrailer()); the forced start keeps mAccelTime 0. No PC goal lists the action and no gameplay.bin
        /// script calls Game.JackKnife, so this path is dead in the shipped data [C]; it runs here when a script asks.
        /// </summary>
        void OnJackKnife(PlayerCar pc)
        {
            if (pc != Car) return;
            var hitch = Car.GetComponent<TrafficPhysics.TractorHitch>();
            if (!hitch || !hitch.Trailer) { Debug.Log($"NFSMW ai: JackKnife on {Car.name}: no trailer (AIActionJackKnife::CanBeAttempted false)"); return; }
            _jackKnife = true;
            _jackAccelTime = 0f;
            _jackAudioSent = false;
        }

        /// <summary>
        /// AIActionJackKnife::Update (:117-147): full gas, no brake; steering 0 while mAccelTime runs (0.5 s after a proximity
        /// start, 0 when forced), then EMomentStrm "jacknife" once (the audio agent's moment stream [hook]), handbrake and full
        /// steering −1 (exe left = Unity +1); the trailer is unhitched once the tractor is below kActionJackKnifeSpeed / 5
        /// (10 mph). IsFinished is always false: the action runs until the goal changes (here: the vehicle is unspawned).
        /// </summary>
        void JackKnifeDrive(float dt)
        {
            var input = new DriveInput { Throttle = 1f, Brake = 0f };
            _jackAccelTime = Mathf.Max(_jackAccelTime - dt, 0f);
            if (_jackAccelTime > 0f) { input.Steer = 0f; Car.Input = input; return; }
            if (!_jackAudioSent) { _jackAudioSent = true; Debug.Log($"NFSMW ai: {Car.name} jack-knifes (EMomentStrm jacknife)"); }
            input.Handbrake = true;
            input.Steer = 1f;
            Car.Input = input;
            var hitch = Car.GetComponent<TrafficPhysics.TractorHitch>();
            if (hitch && hitch.Hitched && hitch.Trailer && Car.SpeedMps < kActionJackKnifeSpeed / 5f * 0.44704f) hitch.SetHitch(false);
        }

        public float Length => _cum.Length > 0 ? _cum[_cum.Length - 1] : 0;

        public void SetPath(List<Vector3> path, bool loop = false)
        {
            Path = path;
            Loop = loop;
            Index = 0;
            _cum = new float[path.Count];
            for (int i = 1; i < path.Count; i++) _cum[i] = _cum[i - 1] + Vector3.Distance(path[i - 1], path[i]);
            if (Car) Index = ClosestIndex(Car.transform.position, 0, path.Count);
        }

        int ClosestIndex(Vector3 p, int from, int to)
        {
            int best = from;
            float bd = float.MaxValue;
            for (int i = Mathf.Max(0, from); i < Mathf.Min(Path.Count, to); i++)
            {
                float d = (Path[i] - p).sqrMagnitude;
                if (d < bd) { bd = d; best = i; }
            }
            return best;
        }

        /// <summary>Distance along the path of the point nearest to p (searching around the current index).</summary>
        public float ProgressOf(Vector3 p, ref int index)
        {
            if (Path.Count < 2) return 0;
            int lo = Mathf.Max(0, index - 3), hi = Mathf.Min(Path.Count - 1, index + 12);
            float best = float.MaxValue, prog = _cum[Mathf.Clamp(index, 0, _cum.Length - 1)];
            for (int i = lo; i < hi; i++)
            {
                var a = Path[i]; var b = Path[i + 1];
                var ab = b - a;
                float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / Mathf.Max(0.01f, ab.sqrMagnitude));
                float d = (a + ab * t - p).sqrMagnitude;
                if (d < best) { best = d; prog = _cum[i] + ab.magnitude * t; index = i; }
            }
            return prog;
        }

        public Vector3 PointAt(float distance)
        {
            if (Path.Count == 0) return transform.position;
            if (Loop && Length > 0) distance = Mathf.Repeat(distance, Length);
            if (distance <= 0) return Path[0];
            for (int i = 1; i < Path.Count; i++)
                if (_cum[i] >= distance)
                {
                    float t = (distance - _cum[i - 1]) / Mathf.Max(0.01f, _cum[i] - _cum[i - 1]);
                    return Vector3.Lerp(Path[i - 1], Path[i], t);
                }
            return Path[Path.Count - 1];
        }

        bool _ghost;
        Rigidbody _rb;

        /// <summary>
        /// Off the streamed area there is no ground: the car becomes kinematic and follows its path at race pace
        /// until the world under it is loaded again (the game keeps far AI on the road the same way).
        /// </summary>
        bool Ghost(Vector3 pos)
        {
            _rb ??= Car.GetComponent<Rigidbody>();
            bool ground = Physics.Raycast(pos + Vector3.up * 3f, Vector3.down, 40f, ~0, QueryTriggerInteraction.Ignore);
            if (!ground && !_ghost && Path.Count >= 2) { _ghost = true; _rb.isKinematic = true; }
            else if (ground && _ghost)
            {
                _ghost = false;
                _rb.isKinematic = false;
                _rb.linearVelocity = Car.transform.forward * MaxSpeed * 0.6f;
            }
            if (!_ghost) return false;
            int idx = Index;
            Progress = ProgressOf(pos, ref idx) + MaxSpeed * 0.75f * Time.fixedDeltaTime;
            Index = idx;
            var p = PointAt(Progress);
            var q = PointAt(Progress + 5f);
            var dir = Flat(q - p);
            Car.transform.SetPositionAndRotation(p + Vector3.up * 0.3f, dir.sqrMagnitude > 0.01f ? Quaternion.LookRotation(dir.normalized) : Car.transform.rotation);
            return true;
        }

        void FixedUpdate()
        {
            if (!Car || Car.Spec == null) return;
            Car.AiControlled = true;
            if (_jackKnife) { JackKnifeDrive(Time.fixedDeltaTime); return; }
            var t = Car.transform;
            var pos = t.position;
            if (Chase == null && Ghost(pos)) return;
            float speed = Car.ForwardSpeed;
            Vector3 target;
            float targetSpeed = MaxSpeed * (1f + 0.25f * CatchUp);
            if (Chase != null && (Chase.position - pos).sqrMagnitude < 90f * 90f)
            {
                var rb = Chase.GetComponent<Rigidbody>();
                target = Chase.position + (rb != null ? rb.linearVelocity * 0.6f : Vector3.zero);
            }
            else if (Path.Count >= 2)
            {
                int idx = Index;
                Progress = ProgressOf(pos, ref idx);
                Index = idx;
                // a long lookahead cuts across the inside of sharp corners (into walls and fences)
                float look = Mathf.Clamp(Mathf.Abs(speed) * 0.7f, 8f, 34f);
                target = PointAt(Progress + look);
                // corner speed from the bend over the next stretch (long chords so graph zigzags don't count)
                float brakeDist = Mathf.Clamp(speed * speed / 18f, 15f, 140f);
                var p0 = PointAt(Progress + 6f);
                var p1 = PointAt(Progress + brakeDist * 0.5f + 25f);
                var p2 = PointAt(Progress + brakeDist + 55f);
                float bend = Vector3.Angle(Flat(p1 - p0), Flat(p2 - p1));
                // and a tight corner close ahead, which the long chords can step over
                for (float d = 10f; d <= brakeDist + 30f; d += 10f)
                {
                    var a0 = PointAt(Progress + d - 10f);
                    var a1 = PointAt(Progress + d + 5f);
                    var a2 = PointAt(Progress + d + 20f);
                    float near = Vector3.Angle(Flat(a1 - a0), Flat(a2 - a1));
                    // the farther it is, the more speed can still be shed before it
                    bend = Mathf.Max(bend, near * Mathf.Clamp01(1.3f - d / (brakeDist + 30f)));
                }
                targetSpeed = Mathf.Max(13f, MaxSpeed * (1f + 0.25f * CatchUp) * Mathf.Clamp01(1f - bend / (115f * CornerGrip)));
                if (!Loop && Progress > Length - 8f) targetSpeed = 0;
            }
            else { Car.Input = new DriveInput { Brake = 1, Handbrake = true }; return; }

            var to = Flat(target - pos);
            float angle = Vector3.SignedAngle(Flat(t.forward), to, Vector3.up);
            float steer = Mathf.Clamp(angle / 28f, -1f, 1f);

            // avoid cars and walls just ahead
            var origin = pos + Vector3.up * 0.7f + t.forward * 2.4f;
            float range = Mathf.Clamp(speed * 1.2f, 8f, 40f);
            bool hitL = Physics.Raycast(origin - t.right * 0.9f, Quaternion.AngleAxis(-8, Vector3.up) * t.forward, out var hl, range);
            bool hitR = Physics.Raycast(origin + t.right * 0.9f, Quaternion.AngleAxis(8, Vector3.up) * t.forward, out var hr, range);
            if (hitL && !IsSelf(hl.collider)) steer += 0.35f * (1f - hl.distance / range);
            if (hitR && !IsSelf(hr.collider)) steer -= 0.35f * (1f - hr.distance / range);
            if (Physics.Raycast(origin, t.forward, out var hc, range * 0.6f) && !IsSelf(hc.collider) && hc.rigidbody != null)
                targetSpeed = Mathf.Min(targetSpeed, Mathf.Max(5f, hc.rigidbody.linearVelocity.magnitude - 2f));

            float err = targetSpeed - speed;
            var input = new DriveInput
            {
                Steer = Mathf.Clamp(steer, -1f, 1f),
                Throttle = err > 0 ? Mathf.Clamp01(err / 3f + 0.55f) * Mathf.Min(1f, Aggression) : 0,
                // threshold braking: the rear brakes lock first (BRAKE_LOCK), so ease off while steering hard
                Brake = err < -3f ? Mathf.Clamp01(-err / 13f) * 0.8f * (1f - 0.5f * Mathf.Abs(Mathf.Clamp(steer, -1f, 1f))) : 0,
                Nitrous = err > 15f && Mathf.Abs(angle) < 8f && Aggression > 1f,
            };

            // stuck: wedged against something with the throttle on
            if (Mathf.Abs(speed) < 1.5f && input.Throttle > 0.2f) _stuck += Time.fixedDeltaTime; else _stuck = 0;
            if (_stuck > 3.5f && Path.Count >= 2)
            {
                _stuck = 0;
                var p = PointAt(Progress + 6f);
                var q = PointAt(Progress + 18f);
                Car.Teleport(p + Vector3.up * 0.6f, Quaternion.LookRotation(Flat(q - p).normalized, Vector3.up));
            }
            Car.Input = input;
        }

        void OnDestroy() { if (Car) Car.AiControlled = false; }

        bool IsSelf(Collider c) => c && c.attachedRigidbody && c.attachedRigidbody.transform == transform;

        static Vector3 Flat(Vector3 v) { v.y = 0; return v; }
    }
}
