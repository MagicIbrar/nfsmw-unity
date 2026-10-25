using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using NfsMw.UI;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// The GPS (AI/Gps.cpp; speed.exe ctor 0x41A5E0, Update 0x42C390, Engage 0x42C6C0, Render 0x41A7D0, GPS_Engage
    /// 0x42C830, GPS_Disengage 0x41ACE0, GPS_IsEngaged 0x41AD00; specs/ai_racer.md §12). A singleton activity with a
    /// GPS path nav (RoadNavGps.cs) and three states:
    /// <list type="bullet">
    /// <item>Engage(target, maxDeviation): the nav at the first player's car, a path found at once (FindPathNow); on the
    /// path → TRACKING (true), else the GPS stays down (false: the world map says "Can't establish GPS connection").</item>
    /// <item>Update (task "WorldUpdate"): the nav re-placed at the car each tick; on the path → TRACKING, else SEARCHING
    /// from the closest path point (no path point → EGPSLost, down). The look-ahead (speed along the path × 3 s,
    /// 20..80 m) moves at 10 m/s towards its target, the nav advances that far: the arrow's point. Within 30 m of the
    /// target → EGPSFinished, down. Searching farther than maxDeviation (200 by default) from the path → EGPSLost, down.</item>
    /// <item>Render (player 1's drive camera, no NIS, not paused): the MARKER_DIRECTION_AID model (GLOBAL\InGameA) at a
    /// point fixed in the camera, turned towards the nav point one camera-second ahead, pulsing while searching.</item>
    /// </list>
    /// EGPSFinished (dtor 0x62CE70) does nothing; EGPSLost (dtor 0x6409A0) puts "GPS connection lost" (0xEE236ED9) on
    /// the HUD. The route drawn on the minimap is [ours].
    /// </summary>
    [DefaultExecutionOrder(10000)]   // Render after the camera rigs have moved
    public sealed class GpsNavigation : MonoBehaviour
    {
        public enum State { Down = 0, Searching = 1, Tracking = 2 }

        // Gps.cpp:85-94, read from the exe: Update 0x42C390 pushes 20 / 80 (look-ahead clamp), ×3 (0x890604), ×10
        // (0x890BF8), arrived 30 (0x890614); Engage 0x42C6C0: maxDeviation < 0.1 → 200 (0x43480000)
        const float MinLookAhead = 20f, MaxLookAhead = 80f, LookAheadTime = 3f, DefaultMaxDeviation = 200f, ArrivedDistance = 30f, CatchupSpeed = 10f;
        const uint GpsLostLabel = 0xEE236ED9;   // "GPS connection lost"
        const string ArrowModel = "MARKER_DIRECTION_AID";

        static GpsNavigation _instance;
        /// <summary>GPS_IsEngaged: not GPS_DOWN.</summary>
        public static bool Engaged => _instance && _instance.Mode != State.Down;
        public static GpsNavigation Instance => _instance;
        /// <summary>EGPSFinished / EGPSLost, for listeners (the script, the world map icon) [ours: C# events].</summary>
        public static event System.Action Finished, Lost;

        public State Mode { get; private set; }
        public Vector3 Target { get; private set; }
        /// <summary>mPosition / mDirection: the nav point the arrow turns to, and the path direction there.</summary>
        public Vector3 Position { get; private set; }
        public Vector3 Direction { get; private set; }
        /// <summary>mDeviation: how far (0..1 of maxDeviation) a searching GPS is from its path.</summary>
        public float Deviation { get; private set; }
        public float MaxDeviation { get; private set; } = DefaultMaxDeviation;
        // Gps(): kTypePath, kPathGPS, kLaneRacing; the WRoadNav defaults leave every filter off
        readonly RoadNav _nav = new RoadNav { DecisionFilter = false, RaceFilter = false, TrafficFilter = false };
        float _pathDist = MinLookAhead;
        // Render state: mDrawn, mAngle, mScale
        bool _drawn;
        float _angle, _scale;
        static Mesh _arrowMesh;
        static Material[] _arrowMats;
        static bool _arrowTried;
        Vector3 _lastCamPos;
        bool _haveCam;
        float _routeTimer;

        static GpsNavigation Ensure()
        {
            if (_instance) return _instance;
            _instance = new GameObject("GPS").AddComponent<GpsNavigation>();   // Gps::Construct: one per game
            DontDestroyOnLoad(_instance.gameObject);
            return _instance;
        }

        static PlayerCar Player => GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;

        /// <summary>GPS_Engage (0x42C830 → Gps::Engage 0x42C6C0).</summary>
        public static bool Engage(Vector3 target, float maxDeviation) => Ensure().DoEngage(target, maxDeviation);

        /// <summary>GPS_Disengage: GPS_DOWN.</summary>
        public static void Disengage()
        {
            if (!_instance) return;
            _instance.Mode = State.Down;
            _instance.ClearRoute();
        }

        bool DoEngage(Vector3 target, float maxDeviation)
        {
            Mode = State.Down;
            Target = target;
            _nav.CancelGpsPath();
            _drawn = false;
            _scale = 0f;
            Deviation = 0f;
            MaxDeviation = maxDeviation < 0.1f ? DefaultMaxDeviation : maxDeviation;
            ClearRoute();
            var car = Player;
            if (!car) return false;
            // ICollisionBody::GetForwardVector / GetPosition
            if (!_nav.InitAtPoint(car.transform.position, car.transform.forward, true, 1f)) return false;
            if (!_nav.FindPathGps(target)) return false;
            if (!_nav.OnPath()) return false;
            Position = _nav.Position;
            Direction = _nav.Forward.normalized;
            Mode = State.Tracking;
            _pathDist = MinLookAhead;
            _routeTimer = 0f;
            return true;
        }

        void FixedUpdate()
        {
            if (Mode == State.Down) return;
            Step(Time.fixedDeltaTime);
        }

        /// <summary>Gps::Update (0x42C390).</summary>
        void Step(float dT)
        {
            var car = Player;
            var body = car ? car.GetComponent<Rigidbody>() : null;
            if (!car || !body) { Mode = State.Down; ClearRoute(); return; }
            var position = body.position;
            var velocity = body.linearVelocity;
            var heading = car.transform.forward;   // IVehicle::ComputeHeading
            _nav.InitAtPoint(position, heading, true, 1f);
            if (!_nav.OnPath())
            {
                Mode = State.Searching;
                _nav.InitAtPath(position, true);
                if (!_nav.Valid) { PostLost(); return; }
            }
            else Mode = State.Tracking;
            var pathDir = _nav.Forward.normalized;
            float want = Mathf.Clamp(Vector3.Dot(velocity, pathDir) * LookAheadTime, MinLookAhead, MaxLookAhead);
            _pathDist = want > _pathDist ? Mathf.Min(_pathDist + dT * CatchupSpeed, want) : Mathf.Max(_pathDist - dT * CatchupSpeed, want);
            _nav.IncNavPosition(_pathDist, 0f);
            Position = _nav.Position;
            Direction = _nav.Forward.normalized;
            if (Vector3.Distance(Target, car.transform.position) < ArrivedDistance)
            {
                Mode = State.Down;   // new EGPSFinished (its handler does nothing)
                ClearRoute();
                Finished?.Invoke();
                return;
            }
            if (MaxDeviation > 1e-6f && Mode == State.Searching)
            {
                float distance = Vector3.Distance(Position, car.transform.position);
                if (distance > MaxDeviation) { PostLost(); return; }
                Deviation = distance / MaxDeviation;
            }
            else Deviation = 0f;
            // [ours] the path ahead on the minimap, twice a second
            if ((_routeTimer -= dT) <= 0f)
            {
                _routeTimer = 0.5f;
                Minimap.Gps = _nav.GpsPolyline(1500f);
            }
        }

        /// <summary>EGPSLost: GPS_DOWN and the HUD message (0x6409A0: "GPS connection lost").</summary>
        void PostLost()
        {
            Mode = State.Down;
            ClearRoute();
            Systems.HudMessage?.Invoke(FrontEndData.Text(GpsLostLabel, "GPS connection lost"));
            Lost?.Invoke();
        }

        void ClearRoute() => Minimap.Gps = null;

        // ---- Gps::Render (0x41A7D0) ----

        void LateUpdate()
        {
            var cam = Camera.main;
            if (Mode == State.Down || !cam) { _haveCam = false; return; }
            float dt = Time.unscaledDeltaTime;   // RealTimeElapsed
            var camPos = cam.transform.position;
            float cameraSpeed = _haveCam && dt > 0f ? Vector3.Distance(camPos, _lastCamPos) / dt : 0f;   // |GetVelocityPosition|
            _lastCamPos = camPos;
            _haveCam = true;
            // view 1 only, no NIS, not paused, a drive camera
            if (NisPlayer.Active || (FrontEnd.Instance != null && FrontEnd.Instance.IsOpen) || Time.timeScale <= 0f) return;
            var chase = cam.GetComponent<ChaseCamera>();
            if (!chase || !chase.isActiveAndEnabled) return;
            if (Mode == State.Searching)
            {
                _scale += dt * Mathf.Lerp(1.5f, 3.5f, Deviation);
                _scale = Mathf.Repeat(_scale, 1f);
            }
            else if (_scale > 0f) _scale = Mathf.Max(_scale - dt * 1.5f, 0f);
            float extra = Mathf.Sin(_scale * 2f * Mathf.PI) * 0.1f + 1f;   // UMath::Sina (turns)
            var navPosition = Position + Direction * cameraSpeed;
            // a point fixed in the view: half the half-FOV above the view axis at 2·cos(half FOV) (camera y is down in the
            // game's camera space, so the negative angle is up) [?: GetFov taken as the horizontal FOV]
            float halfFov = chase.FovH * 0.5f * Mathf.Deg2Rad;
            if (chase.Split) halfFov *= 0.5f;   // Sim::GetUserMode() == 1
            float scale = 2f * Mathf.Cos(halfFov), a = halfFov * 0.5f;
            const float altDist = 1f;   // 1.5 in view mode 2 with the rear-view mirror on [?: not mapped]
            var vPos = camPos + cam.transform.up * (Mathf.Sin(a) * scale) + cam.transform.forward * (Mathf.Cos(a) * scale * altDist);
            var ray = vPos - navPosition;
            ray.y = 0f;
            if (ray.sqrMagnitude < 1e-8f) return;
            ray.Normalize();
            float desired = Mathf.Atan2(ray.z, ray.x);   // game (x, y) = Unity (x, z)
            if (!_drawn) _angle = desired;
            else
            {
                float dist = Mathf.Abs(_angle - desired);
                float circle = Mathf.Abs(dist - 2f * Mathf.PI);
                float rotScale = Mathf.Min(dist, circle);
                float rotation = dt * Mathf.Lerp(0.125f, 1f, Mathf.Clamp01(rotScale / Mathf.PI)) * 2f * Mathf.PI;
                float speed = rotation;
                if (_angle > desired) rotation = -rotation;
                float step = dist > Mathf.PI ? -rotation : rotation;
                if (rotScale <= speed) _angle = desired;
                else _angle += step;
                if (_angle > Mathf.PI) _angle -= 2f * Mathf.PI;
                else if (_angle < -Mathf.PI) _angle += 2f * Mathf.PI;
            }
            if (!LoadArrow()) return;
            // game axes (z up): v_ray = (cos, sin, 0)·0.2, v_left = unit(up × ray)·0.2, v_up = unit(ray × left)·0.2,
            // all × the pulse; rows (left, up, ray) at v_pos
            var r = new Vector3(Mathf.Cos(_angle), Mathf.Sin(_angle), 0f);
            var left = Vector3.Cross(new Vector3(0, 0, 1), r).normalized;
            var up = Vector3.Cross(r, left).normalized;
            float k = 0.2f * extra;
            r *= k; left *= k; up *= k;
            var rows = new[] { left.x, left.y, left.z, up.x, up.y, up.z, r.x, r.y, r.z };
            var m = Coords.InstanceMatrix(rows, Coords.ToGame3(vPos));
            for (int i = 0; i < _arrowMats.Length && i < _arrowMesh.subMeshCount; i++)
                Graphics.DrawMesh(_arrowMesh, m, _arrowMats[i], cam.gameObject.layer, cam, i, null, false, false);
            _drawn = true;
        }

        /// <summary>eModel::Init(bStringHash("MARKER_DIRECTION_AID")): the solid in GLOBAL\InGameA.bun.</summary>
        static bool LoadArrow()
        {
            if (_arrowMesh) return true;
            if (_arrowTried) return false;
            _arrowTried = true;
            try
            {
                GameData.Mount();
                var d = GameFiles.ReadBundle(Path.Combine(GameData.Root, "GLOBAL", "InGameA.bun"));
                foreach (var so in SolidList.ReadAll(d, 0, d.Length))
                {
                    if (so.Name != ArrowModel) continue;
                    var built = new MeshFactory(GameData.Textures).Build(so, PartKind.World);
                    if (built == null) break;
                    _arrowMesh = built.Mesh;
                    _arrowMats = built.Materials;
                    return true;
                }
                Debug.LogWarning("NFSMW GPS: no " + ArrowModel + " in InGameA.bun");
            }
            catch (System.Exception e) { Debug.LogWarning("NFSMW GPS arrow: " + e.Message); }
            return false;
        }

        void OnDestroy()
        {
            if (_instance == this) { _instance = null; Minimap.Gps = null; }
        }
    }
}
