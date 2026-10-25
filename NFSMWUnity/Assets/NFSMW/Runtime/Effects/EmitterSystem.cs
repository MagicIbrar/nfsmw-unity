using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Effects
{
    /// <summary>
    /// A frame in game space (Z up; Unity world with Y and Z swapped, see <see cref="Coords"/>), in the row-vector
    /// convention of bMatrix4: X, Y, Z are the local axes, P the origin. Emitters read their cone direction from Z (v2)
    /// and their volume/velocity axes from X, Y, Z (Emitter::GetConeVelocity / SpawnParticles, EmitterSystem.cpp:479, 1831).
    /// </summary>
    public struct FxFrame
    {
        public Vector3 X, Y, Z, P;

        public static readonly FxFrame Identity = new FxFrame { X = Vector3.right, Y = Vector3.up, Z = Vector3.forward };

        /// <summary>Unity world -> game space.</summary>
        public static Vector3 G(Vector3 u) => new Vector3(u.x, u.z, u.y);
        /// <summary>Game space -> Unity world.</summary>
        public static Vector3 U(Vector3 g) => new Vector3(g.x, g.z, g.y);

        public Vector3 Point(Vector3 l) => P + X * l.x + Y * l.y + Z * l.z;
        public Vector3 Dir(Vector3 l) => X * l.x + Y * l.y + Z * l.z;

        /// <summary>
        /// The game-space frame of a Unity transform whose local space holds game-local coordinates through
        /// Coords.ToUnity (a car's Body, a showroom backdrop root).
        /// </summary>
        public static FxFrame Of(Transform space) => new FxFrame
        {
            X = G(space.TransformDirection(Vector3.right)),
            Y = G(space.TransformDirection(Vector3.forward)),
            Z = G(space.TransformDirection(Vector3.up)),
            P = G(space.position),
        };

        /// <summary>A game-local bMatrix4 (16 floats, rows X, Y, Z, P), e.g. a position marker or a library placement.</summary>
        public static FxFrame FromMatrix(float[] m) => new FxFrame
        {
            X = new Vector3(m[0], m[1], m[2]),
            Y = new Vector3(m[4], m[5], m[6]),
            Z = new Vector3(m[8], m[9], m[10]),
            P = new Vector3(m[12], m[13], m[14]),
        };

        /// <summary>eMulMatrix(local, world): this local frame placed in <paramref name="world"/>.</summary>
        public FxFrame In(in FxFrame world) => new FxFrame { X = world.Dir(X), Y = world.Dir(Y), Z = world.Dir(Z), P = world.Point(P) };

        /// <summary>The shortest rotation taking <paramref name="from"/> to <paramref name="to"/> (UQuat::BuildDeltaAxis) as a frame at <paramref name="pos"/>.</summary>
        public static FxFrame Delta(Vector3 from, Vector3 to, Vector3 pos)
        {
            var q = Quaternion.FromToRotation(from, to.sqrMagnitude > 1e-8f ? to.normalized : from);
            return new FxFrame { X = q * Vector3.right, Y = q * Vector3.up, Z = q * Vector3.forward, P = pos };
        }
    }

    /// <summary>EmitterParticle (EmitterSystem.h, 0x40 bytes). Kept unpacked: the game's 8-bit velocity compression is left out.</summary>
    public struct FxParticle
    {
        public Vector3 Pos, Vel, Acc;
        public float Life;          // seconds left (game: u16 in 1/1024 s)
        public float Size;          // half size, m
        public Color32 Colour;
        public ushort Angle;        // bAngle
        public byte InitialAngle, RotOffset;
        public ushort AnimFrame;    // 0..65535 over the flip-book
        public Vector4 Uv;          // u0, v0, u1, v1
    }

    /// <summary>EmitterControlState (EmitterSystem.h).</summary>
    public enum FxControlState { Error, NotStarted, Delaying, OnCycle, OffCycle, On, Off }

    /// <summary>Emitter (EmitterSystem.h, 0x90 bytes): the spawn state of one emitterdata inside a group.</summary>
    public sealed class FxEmitter
    {
        public readonly EmitterData D;
        public readonly FxGroup Group;
        public readonly List<FxParticle> Particles = new List<FxParticle>();
        internal FxControlState State = FxControlState.NotStarted;
        internal float Time, Accumulation;
        internal uint Seed;
        internal bool Enabled = true, OneShot, Orphaned;
        internal float MinIntensity, MaxIntensity = 1f;

        internal FxEmitter(EmitterData d, FxGroup g, uint seed)
        {
            D = d;
            Group = g;
            Seed = seed;
        }
    }

    /// <summary>
    /// EmitterGroup (EmitterSystem.h, 0x80 bytes): up to six emitters sharing a frame, an intensity and an inherited
    /// velocity. Owners either drive it (<see cref="Update"/> each frame, the tyre and pipe effects) or set
    /// <see cref="AutoUpdate"/> and let the system run it (one-shots, world placements).
    /// </summary>
    public sealed class FxGroup
    {
        public readonly EmitterGroupData Data;
        public readonly List<FxEmitter> Emitters = new List<FxEmitter>();
        public uint Flags;
        public FxFrame Frame = FxFrame.Identity;
        public Vector3 InheritVelocity;   // game space
        public float Intensity = 1f, FarClip;
        public bool AutoUpdate, Enabled = true;
        /// <summary>Unity layer the particles draw on (31 = showroom).</summary>
        public int Layer;
        /// <summary>SetOldSurfaceEffectFlag (0x80000): the owner let go; deleted once its particles are gone.</summary>
        public bool Released;
        internal bool Dead;
        internal int ZeroFrames;

        internal FxGroup(EmitterGroupData data, uint flags) { Data = data; Flags = flags; FarClip = data.FarClip; }

        public int NumParticles
        {
            get
            {
                int n = 0;
                foreach (var e in Emitters) n += e.Particles.Count;
                return n;
            }
        }

        public void SetFrame(in FxFrame f) => Frame = f;

        /// <summary>EmitterGroup::Enable / Disable.</summary>
        public void SetEnabled(bool on)
        {
            Enabled = on;
            foreach (var e in Emitters) e.Enabled = on;
        }

        /// <summary>EmitterGroup::MakeOneShot (EmitterSystem.cpp:624).</summary>
        public bool MakeOneShot(bool forceAll)
        {
            bool any = false;
            foreach (var e in Emitters)
            {
                bool one = forceAll;
                if (!one && e.D.IsOneShot) { any = true; one = true; }
                if (one) e.OneShot = true;
            }
            return forceAll || any;
        }

        /// <summary>Let the group finish: no more spawning, deleted when its last particle dies (SetOldSurfaceEffectFlag + UnSubscribe).</summary>
        public void Release() => Released = true;

        /// <summary>Delete the group and its particles now.</summary>
        public void Kill() => Dead = true;

        /// <summary>EmitterGroup::Update (EmitterSystem.cpp:1554).</summary>
        public void Update(float dt) => EmitterSystem.Instance.UpdateGroup(this, dt);
    }

    /// <summary>
    /// The particle system (Ecstasy/EmitterSystem.cpp, PC speed.exe 0x4FDFD0-0x50A800): emitter groups from the
    /// emittergroup/emitterdata attributes (<see cref="FxDatabase"/>), at most 1024 particles, 500 emitters and 200 groups.
    /// Particles are simulated in game space with the exe's maths: spawn counts, cone/disc/box velocities, drag, gravity
    /// along -Z, the four-key cubic colour/size/rotation curves and the alpha kill. They are drawn as camera-facing (or
    /// axis-constrained) quads with the texture's own blend (TextureInfo AlphaBlendType: 1 blend, 2 additive) and the
    /// particle effect's modulate-2x (the ps_1_1 'mul_x2 r0, t0, v0' effect at speed.exe 0x566398, taken as
    /// particles.efx [?]). Billboards are built per camera, so split screen and the showroom camera each face their own.
    /// Not ported: the next-gen "Xenon" spark lines (fuelcell_effect XenonEffect refs, PC DrawXenonEmitters 0x6C6770,
    /// not decompiled), NIS pool expansion, the particle texture pages (0x3BD00, Xenon only).
    /// </summary>
    [DefaultExecutionOrder(10001)]   // after the cars (their tyre and pipe effects spawn in LateUpdate) and the cameras
    public sealed class EmitterSystem : MonoBehaviour
    {
        public const int MaxParticles = 0x400, MaxEmitters = 500, MaxGroups = 200;
        /// <summary>Creation context flags (EmitterSystem.cpp; meanings from their use).</summary>
        public const uint AlwaysClose = 0x20000, Gameplay = 0x40000, ForcedOneShot = 0x400000, WorldEffect = 0x8000000,
            OneShotEffect = 0x10c00000, VehicleEffect = 0x10800000, TireEffect = 0x40000000;

        static EmitterSystem _instance;
        static FxDatabase _db;
        static uint _seed;   // EmitterRandomSeed
        public static bool Enabled = true;

        readonly List<FxGroup> _groups = new List<FxGroup>();
        int _numParticles, _numEmitters;
        readonly List<Vector3> _interestPos = new List<Vector3>(), _interestDir = new List<Vector3>();

        public static EmitterSystem Instance
        {
            get
            {
                if (_instance) return _instance;
                var go = new GameObject("Emitter System");
                if (Application.isPlaying) DontDestroyOnLoad(go);
                return _instance = go.AddComponent<EmitterSystem>();
            }
        }

        public static bool Exists => _instance;

        /// <summary>The emitter tables of the mounted attributes.bin (built once).</summary>
        public static FxDatabase Db
        {
            get
            {
                if (_db == null && GameData.Attributes != null) _db = new FxDatabase(GameData.Attributes);
                return _db;
            }
        }

        public int NumParticles => _numParticles;

        /// <summary>QA diagnostic: the emitters with particles within <paramref name="radius"/> m of a point, as
        /// "name tex=hash n=count near=dist size=max".</summary>
        public string Near(Vector3 point, float radius)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var g in _groups)
                foreach (var em in g.Emitters)
                {
                    int n = 0;
                    float near = float.MaxValue, size = 0;
                    foreach (var p in em.Particles)
                    {
                        float d = Vector3.Distance(FxFrame.U(p.Pos), point);
                        if (d > radius) continue;
                        n++;
                        near = Mathf.Min(near, d);
                        size = Mathf.Max(size, p.Size);
                    }
                    if (n > 0) sb.Append($"{g.Data.Name}/{em.D.Name}{(g.AutoUpdate ? " world" : "")} tex={em.D.TextureHash:X8} n={n} near={near:F1} size={size:F1}; ");
                }
            return sb.ToString();
        }
        public int NumGroups => _groups.Count;

        // ---- bRandom (bMath.cpp:83) ------------------------------------------------------------------------------
        public static uint RandomInt(int range, ref uint seed)
        {
            if (range == 0) return 0;
            uint result = seed;
            uint next = result ^ 0x1d872b41;
            uint temp = next ^ (next >> 5);
            seed = temp ^ (next ^ (temp << 0x1b));
            return result - (result / (uint)range) * (uint)range;
        }

        public static float Random(float range, ref uint seed) => range * 4.656613e-10f * RandomInt(0x7fffffff, ref seed);

        static uint _defaultSeed = 0x2C0FFEE;
        static float Random(float range) => Random(range, ref _defaultSeed);

        // ---- groups -------------------------------------------------------------------------------------------------

        /// <summary>EmitterSystem::CreateEmitterGroup (EmitterSystem.cpp:833). Null when the group is unknown, empty or the pools are full.</summary>
        public FxGroup Create(uint groupKey, uint flags, int layer = 0)
        {
            if (!Enabled || groupKey == 0 || groupKey == FxDatabase.NullKey) return null;
            var data = Db?.Group(groupKey);
            if (data == null || data.Emitters.Length == 0) return null;
            int n = data.Emitters.Length;
            bool full = _numEmitters + n > MaxEmitters || _groups.Count > MaxGroups - 1;
            if (full && (flags & Gameplay) != 0)
            {
                // make room: world effects first, then anything that is not gameplay
                foreach (var g in _groups)
                    if ((g.Flags & WorldEffect) != 0 && (g.Flags & Gameplay) == 0 && (g.Flags & 0x4000000) == 0) { g.Dead = true; break; }
                Sweep();
            }
            if (_numEmitters + n > MaxEmitters || _groups.Count >= MaxGroups) return null;
            var grp = new FxGroup(data, flags) { Layer = layer };
            // EmitterGroup::SetEmitters (EmitterSystem.cpp:560): intensity windows only when there is one per emitter
            bool ranges = data.Intensity.Length == n;
            for (int i = 0; i < n; i++)
            {
                var em = new FxEmitter(data.Emitters[i], grp, (uint)(int)Random(2.1474836e+09f, ref _seed));
                em.OneShot = false;
                if (ranges)
                {
                    var r = data.Intensity[i];
                    if (r.min >= r.max) r = (0f, 1f);
                    em.MinIntensity = r.min;
                    em.MaxIntensity = r.max;
                }
                else { em.MinIntensity = 0f; em.MaxIntensity = 999999f; }
                grp.Emitters.Add(em);
            }
            _numEmitters += n;
            _groups.Add(grp);
            return grp;
        }

        /// <summary>
        /// A fire-and-forget effect (VehicleRenderConn::Effect::Fire, World_OneShotEffect): every emitter one-shot, run
        /// by the system, deleted when done.
        /// </summary>
        public FxGroup Fire(uint groupKey, in FxFrame frame, float intensity, Vector3 inheritVelocity, uint flags = OneShotEffect, int layer = 0)
        {
            var g = Create(groupKey, flags, layer);
            if (g == null) return null;
            g.Intensity = intensity;
            g.MakeOneShot(true);
            g.AutoUpdate = true;
            g.Frame = frame;
            g.InheritVelocity = inheritVelocity;
            return g;
        }

        /// <summary>CleanParticlesOnRaceRestart / KillEverything: every non-static group and particle.</summary>
        public void KillAll(bool includeAutoWorld = false)
        {
            foreach (var g in _groups)
                if (includeAutoWorld || (g.Flags & AlwaysClose) == 0) g.Dead = true;
            Sweep();
        }

        void Sweep()
        {
            for (int i = _groups.Count - 1; i >= 0; i--)
            {
                var g = _groups[i];
                if (!g.Dead) continue;
                foreach (var e in g.Emitters) _numParticles -= e.Particles.Count;
                _numEmitters -= g.Emitters.Count;
                g.Emitters.Clear();
                _groups.RemoveAt(i);
            }
        }

        static Camera[] _cams = new Camera[8];

        /// <summary>The enabled cameras without allocating (Camera.GetAllCameras into a reused buffer).</summary>
        internal static int Cameras(out Camera[] cams)
        {
            if (_cams.Length < Camera.allCamerasCount) _cams = new Camera[Camera.allCamerasCount + 4];
            cams = _cams;
            return Camera.GetAllCameras(_cams);
        }

        // ---- distance tests (EmitterSystem::IsCloseEnough, EmitterSystem.cpp:1263) ---------------------------------

        void UpdateInterestPoints()
        {
            _interestPos.Clear();
            _interestDir.Clear();
            int count = Cameras(out var cams);
            for (int c = 0; c < count; c++)
            {
                var cam = cams[c];
                if (!cam || !cam.enabled || cam.cameraType != CameraType.Game || cam.targetTexture) continue;
                if ((cam.cullingMask & 1) == 0) continue;   // world cameras only (the showroom draws layer 31)
                _interestPos.Add(FxFrame.G(cam.transform.position));
                _interestDir.Add(FxFrame.G(cam.transform.forward));
                if (_interestPos.Count == 2) break;
            }
        }

        public bool IsCloseEnough(Vector3 pos, float farClip, bool frustum, float cosFov)
        {
            if (_interestPos.Count == 0) return true;
            for (int i = 0; i < _interestPos.Count; i++)
            {
                var d = pos - _interestPos[i];
                float dist = d.magnitude;
                if (dist >= farClip) continue;
                if (!frustum) return true;
                if (dist < 1e-4f || Vector3.Dot(d / dist, _interestDir[i]) > cosFov) return true;
            }
            return false;
        }

        bool IsCloseEnough(FxGroup g, bool frustum, float cosFov) =>
            (g.Flags & AlwaysClose) != 0 || g.Layer != 0 || IsCloseEnough(g.Frame.P, g.FarClip, frustum, cosFov);

        // ---- update -------------------------------------------------------------------------------------------------

        void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (Enabled) Step(dt);
            Render();
        }

        /// <summary>EmitterSystem::Update (EmitterSystem.cpp:1523).</summary>
        void Step(float dt)
        {
            UpdateInterestPoints();
            WorldEffects.Service(this);
            for (int i = 0; i < _groups.Count; i++)
            {
                var g = _groups[i];
                if (g.Dead) continue;
                if (g.AutoUpdate) UpdateGroup(g, dt);
                else if ((g.Flags & TireEffect) != 0 && !g.Released && g.NumParticles == 0 && ++g.ZeroFrames > 10)
                    RemoveEmitters(g);
                // a group without emitters is deleted (owners see Dead and start a new one, NotifyTireStateEffectOfEmitterDelete)
                if (g.Emitters.Count == 0) g.Dead = true;
                if (g.Released && g.NumParticles == 0) g.Dead = true;
            }
            Sweep();
            UpdateParticles(dt);
        }

        void RemoveEmitters(FxGroup g)
        {
            foreach (var e in g.Emitters) _numParticles -= e.Particles.Count;
            _numEmitters -= g.Emitters.Count;
            g.Emitters.Clear();
        }

        internal void UpdateGroup(FxGroup g, float dt)
        {
            if (!Enabled || !g.Enabled || g.Dead) return;
            bool close = IsCloseEnough(g, (g.Flags & Gameplay) == 0, 0.7f);
            for (int i = 0; i < g.Emitters.Count; i++)
            {
                var em = g.Emitters[i];
                float rollover = 0f;
                bool spawn = em.Enabled && !g.Released && ControlUpdate(em, dt, out rollover);
                if (spawn && close && dt + rollover > 0f) SpawnParticles(em, dt + rollover, g.Intensity);
                bool remove = false;
                if (em.State == FxControlState.Off)
                {
                    em.Orphaned = true;
                    if (em.Particles.Count == 0) remove = true;
                }
                if (g.Released && em.Particles.Count == 0) remove = true;
                if (remove)
                {
                    g.Emitters.RemoveAt(i--);
                    _numEmitters--;
                }
            }
        }

        static bool HasOnCycle(EmitterData d) => d.OnCycle != 0f || d.OnCycleVariance != 0f;
        static bool HasOffCycle(EmitterData d) => d.OffCycle != 0f || d.OffCycleVariance != 0f;

        static uint _aseed = 0x1234567;

        /// <summary>EmitterControl::Update (EmitterSystem.cpp:1586).</summary>
        static bool ControlUpdate(FxEmitter em, float dt, out float rollover)
        {
            var d = em.D;
            rollover = 0f;
            float startDelay = d.StartDelay;
            if (d.StartDelayRandomVariance) startDelay = Random(startDelay);
            // the attribute or the group's creation flag; the emitter's own ONE_SHOT bit (MakeOneShot) is not read here
            bool oneShot = d.IsOneShot || (em.Group.Flags & ForcedOneShot) != 0;
            bool hasDelay = startDelay != 0f;
            bool hasOn = HasOnCycle(d), hasOff = HasOffCycle(d);
            // a continuous emitter with an on cycle but no off cycle just runs
            if (hasOn && !oneShot && !hasOff) hasOn = false;
            switch (em.State)
            {
                case FxControlState.NotStarted:
                    if (hasDelay)
                    {
                        em.State = FxControlState.Delaying;
                        em.Time = startDelay - dt;
                        return false;
                    }
                    if (hasOn)
                    {
                        em.State = FxControlState.OnCycle;
                        em.Time = Mathf.Max(0f, d.OnCycle + Random(d.OnCycleVariance, ref _aseed) - dt);
                        return true;
                    }
                    em.Time = 0f;
                    em.State = FxControlState.On;
                    // A one-shot without an on cycle spawns Life x NumParticles every update from ECS_ON (speed.exe
                    // 0x4FF190 and 0x509E66 agree with the decomp). For an attribute one-shot that is what the world
                    // fountains do; a fired group (flag 0x400000) would never end, so it bursts once here [?].
                    if ((em.Group.Flags & ForcedOneShot) != 0) em.State = FxControlState.Off;
                    return true;
                case FxControlState.Delaying:
                    em.Time -= dt;
                    if (em.Time > 0f) return false;
                    rollover = em.Time;
                    if (hasOn)
                    {
                        em.State = FxControlState.OnCycle;
                        em.Time += d.OnCycle + Random(d.OnCycleVariance, ref _aseed);
                        return true;
                    }
                    em.Time = 0f;
                    em.State = (em.Group.Flags & ForcedOneShot) != 0 ? FxControlState.Off : FxControlState.On;
                    return true;
                case FxControlState.OnCycle:
                    em.Time -= dt;
                    if (em.Time > 0f) return true;
                    rollover = em.Time + dt;
                    if (oneShot)
                    {
                        em.State = FxControlState.Off;
                        em.Time = 0f;
                        return true;
                    }
                    em.State = FxControlState.OffCycle;
                    em.Time += d.OffCycle + Random(d.OffCycleVariance, ref _aseed);
                    return true;
                case FxControlState.OffCycle:
                    em.Time -= dt;
                    if (em.Time > 0f) return false;
                    rollover = em.Time;
                    em.State = FxControlState.OnCycle;
                    em.Time += d.OnCycle + Random(d.OnCycleVariance, ref _aseed);
                    return true;
                case FxControlState.On:
                    return true;
                default:
                    return false;
            }
        }

        const float DegToAng = 182.04445f;   // 65536 / 360

        /// <summary>Emitter::SpawnParticles (EmitterSystem.cpp:1710).</summary>
        void SpawnParticles(FxEmitter em, float dt, float intensity)
        {
            if (intensity <= 0f || intensity > em.MaxIntensity || intensity < em.MinIntensity) return;
            var d = em.D;
            var frame = em.Group.Frame;
            uint seed = em.Seed;
            float angleRange = d.InitialAngleRange * DegToAng;
            float angle = -angleRange * 0.5f;
            float num = intensity * d.NumParticles;
            float numVar = d.NumParticlesVariance * num;
            float inherit = d.MotionInherit, inheritVar = d.MotionInheritVariance * inherit;
            float life = d.Life, lifeVar = d.LifeVariance * life;
            float speed = d.Speed, speedVar = d.SpeedVariance * speed;
            float fnum = num - numVar;
            life -= lifeVar;
            inherit -= inheritVar;
            speed -= speedVar;
            // effectively_one_shot: the emitter's ONE_SHOT bit (set by MakeOneShot) or the creation flag, not the attribute
            bool oneShot = em.OneShot || (em.Group.Flags & ForcedOneShot) != 0;
            bool hasOn = HasOnCycle(d), hasOff = HasOffCycle(d);
            if (hasOn && !oneShot && !hasOff) hasOn = false;
            float use = dt;
            if (oneShot && !hasOn) use = life;
            fnum *= use;
            int now = (int)fnum;
            if (now == 0)
            {
                em.Accumulation += fnum;
                if (em.Accumulation > 1f)
                {
                    now = 1;
                    em.Accumulation -= 1f;
                }
            }

            bool animated = d.AnimType != FxAnimation.None;
            for (int i = 0; i < now; i++)
            {
                Vector3 vel = Vector3.zero, acc = Vector3.zero;
                float pspeed = speed + Random(speedVar, ref seed);
                if (speed != 0f)
                {
                    var dir = d.SpreadAsDisc ? DiscVelocity(d, frame, ref seed) : ConeVelocity(d, frame, ref seed);
                    vel = dir * pspeed;
                }
                else if (d.EliminateUnnecessaryRandomness)
                {
                    var rv = new Vector3(Random(d.VelocityDelta.X, ref seed), Random(d.VelocityDelta.Y, ref seed), Random(d.VelocityDelta.Z, ref seed));
                    var ra = new Vector3(Random(d.AccelDelta.X, ref seed), Random(d.AccelDelta.Y, ref seed), Random(d.AccelDelta.Z, ref seed));
                    vel = new Vector3(d.VelocityStart.X, d.VelocityStart.Y, d.VelocityStart.Z) + rv;
                    acc = new Vector3(d.AccelStart.X, d.AccelStart.Y, d.AccelStart.Z) + ra;
                }
                else
                {
                    // start +- delta along the emitter's own axes; the acceleration stays in world axes
                    float r = Random(d.VelocityDelta.Z, ref seed);
                    vel = frame.Z * (d.VelocityStart.Z - d.VelocityDelta.Z + r + r);
                    r = Random(d.VelocityDelta.Y, ref seed);
                    vel += frame.Y * (d.VelocityStart.Y - d.VelocityDelta.Y + r + r);
                    r = Random(d.VelocityDelta.X, ref seed);
                    vel += frame.X * (d.VelocityStart.X - d.VelocityDelta.X + r + r);
                    r = Random(d.AccelDelta.X, ref seed);
                    acc.x = d.AccelStart.X - d.AccelDelta.X + r + r;
                    r = Random(d.AccelDelta.Y, ref seed);
                    acc.y = d.AccelStart.Y - d.AccelDelta.Y + r + r;
                    r = Random(d.AccelDelta.Z, ref seed);
                    acc.z = d.AccelStart.Z - d.AccelDelta.Z + r + r;
                }
                var local = new Vector3(
                    d.VolumeCenter.X + (Random(d.VolumeExtent.X, ref seed) - d.VolumeExtent.X * 0.5f),
                    d.VolumeCenter.Y + (Random(d.VolumeExtent.Y, ref seed) - d.VolumeExtent.Y * 0.5f),
                    d.VolumeCenter.Z + (Random(d.VolumeExtent.Z, ref seed) - d.VolumeExtent.Z * 0.5f));
                var pos = frame.Point(local);
                float plife = life + Random(lifeVar, ref seed);
                float fangle = angle + Random(angleRange, ref seed);
                if (inherit != 0f)
                {
                    float k = Mathf.Clamp01(inherit + Random(inheritVar, ref seed));
                    if (!d.MotionLive) vel += em.Group.InheritVelocity * k;
                }
                int angle16 = (int)fangle & 0xfffe;
                int pangle = angle16;
                byte smAngle = (byte)(int)(angle16 * (1f / 256f));
                if (d.RandomRotationDirection) pangle = angle16 | (int)(seed & 1);
                if (plife > 60f) plife = 60f;
                if (_numParticles >= MaxParticles) continue;

                var p = new FxParticle
                {
                    Pos = pos,
                    Vel = vel,
                    Acc = acc,
                    Life = plife,
                    InitialAngle = smAngle,
                    Angle = (ushort)pangle,
                    RotOffset = (byte)(int)(Mathf.Clamp01(Random(d.RotationVariance, ref seed)) * 255f),
                    Uv = new Vector4(0, 0, 1, 1),
                };
                if (animated)
                {
                    int frames = (int)d.AnimType * (int)d.AnimType;
                    float maxIndex = frames - 1f;
                    float start = d.AnimRandomStartFrame ? Random(maxIndex) : 0f;
                    p.AnimFrame = (ushort)(int)(start / maxIndex * 65535f);
                    p.Uv = AnimatedUv(d.AnimType, (int)start);
                }
                // GetInitialParticleColorAndSize: the curves at t = 0
                var extra = d.ExtraAt(0f);
                p.Size = extra.X;
                int a0 = p.Angle, ad = (int)extra.Y;
                p.Angle = (ushort)((a0 & 1) == 0 ? a0 + ad : a0 - ad);
                p.Colour = ToColour(d.ColourAt(0f));
                em.Particles.Add(p);
                _numParticles++;
            }
            em.Seed = seed;
        }

        static Color32 ToColour(FxVec4 c) => new Color32(
            (byte)Mathf.Clamp((int)(c.X * 255f), 0, 255), (byte)Mathf.Clamp((int)(c.Y * 255f), 0, 255),
            (byte)Mathf.Clamp((int)(c.Z * 255f), 0, 255), (byte)Mathf.Clamp((int)(c.W * 255f), 0, 255));

        /// <summary>GetAnimatedUVs (EmitterSystem.cpp:505) on the full texture: cell (frame % n, frame / n) of an n x n grid.</summary>
        static Vector4 AnimatedUv(FxAnimation type, int frame)
        {
            int n = (int)type;
            if (n <= 0) return new Vector4(0, 0, 1, 1);
            int j = frame / n, i = frame - j * n;
            float w = 1f / n;
            return new Vector4(i * w, j * w, (i + 1) * w, (j + 1) * w);
        }

        /// <summary>Emitter::GetConeVelocity (EmitterSystem.cpp:479): the frame's Z turned by random X, Y, Z angles within the spread.</summary>
        static Vector3 ConeVelocity(EmitterData d, in FxFrame f, ref uint seed)
        {
            float full = d.SpreadAngle, half = full * 0.5f;
            float ax = Random(full, ref seed) - half, ay = Random(full, ref seed) - half, az = Random(full, ref seed) - half;
            var dir = f.Z;
            dir = Quaternion.AngleAxis(ax, Vector3.right) * dir;
            dir = Quaternion.AngleAxis(ay, Vector3.up) * dir;
            dir = Quaternion.AngleAxis(az, Vector3.forward) * dir;
            return dir;
        }

        /// <summary>Emitter::GetDiscVelocity (EmitterSystem.cpp:450): a random direction in the local XY plane, tilted about Y within the spread.</summary>
        static Vector3 DiscVelocity(EmitterData d, in FxFrame f, ref uint seed)
        {
            float full = d.SpreadAngle, half = full * 0.5f;
            float at = (Random(full, ref seed) - half) * Mathf.Deg2Rad;
            float az = (Random(360f, ref seed) - 180f) * Mathf.Deg2Rad;
            var p = new Vector3(Mathf.Cos(az), Mathf.Sin(az), 0f);
            // eRotateY by angle_t
            float c = Mathf.Cos(at), s = Mathf.Sin(at);
            p = new Vector3(p.x * c - p.z * s, p.y, p.x * s + p.z * c);
            return f.Dir(p);
        }

        /// <summary>EmitterSystem::UpdateParticles (EmitterSystem.cpp:965).</summary>
        void UpdateParticles(float dt)
        {
            if (_numParticles == 0 || dt <= 0f) return;
            foreach (var g in _groups)
            {
                bool keep = IsCloseEnough(g, false, 0.7f);
                foreach (var em in g.Emitters)
                {
                    var list = em.Particles;
                    if (list.Count == 0) continue;
                    if (!keep) { _numParticles -= list.Count; list.Clear(); continue; }
                    var d = em.D;
                    float drag = d.Drag, gravity = d.Gravity, life = d.Life;
                    bool anim = d.AnimType != FxAnimation.None;
                    int frames = (int)d.AnimType * (int)d.AnimType;
                    uint killAt = (uint)(int)d.AlphaToKillAt;
                    int w = 0;
                    for (int r = 0; r < list.Count; r++)
                    {
                        var p = list[r];
                        if (p.Life <= dt) continue;
                        if (anim)
                        {
                            uint step = (uint)(int)(dt * d.AnimFps / frames * 65535f);
                            uint cur = p.AnimFrame + step;
                            if (cur + step > 65535) cur -= 65535;
                            cur = (ushort)cur;
                            int index = (int)(cur / 65535f * (frames - 1f));
                            p.Uv = AnimatedUv(d.AnimType, index);
                            p.AnimFrame = (ushort)cur;
                        }
                        if (drag > 0f) p.Vel += p.Vel * Mathf.Max(-1f, -dt * drag * p.Vel.magnitude);
                        if (gravity != 0f) p.Vel.z -= gravity * dt;
                        else p.Vel += p.Acc * dt;
                        p.Pos += p.Vel * dt;
                        p.Life -= dt;
                        float t = life > 0f ? 1f - p.Life / life : 1f;
                        var extra = d.ExtraAt(t);
                        p.Size = extra.X;
                        int pangle = (int)(p.InitialAngle * 257f);
                        int adelta = (int)(extra.Y + p.RotOffset / 255f * extra.Y);
                        p.Angle = (ushort)((pangle & 1) == 0 ? pangle + adelta : pangle - adelta);
                        p.Colour = ToColour(d.ColourAt(t));
                        if (!d.NoKillAtAlpha && p.Colour.a <= killAt) continue;
                        list[w++] = p;
                    }
                    _numParticles -= list.Count - w;
                    list.RemoveRange(w, list.Count - w);
                }
            }
        }

        // ---- rendering ------------------------------------------------------------------------------------------------

        sealed class Bucket
        {
            public readonly List<Vector3> V = new List<Vector3>();
            public readonly List<Vector2> Uv = new List<Vector2>();
            public readonly List<Color> C = new List<Color>();
            public readonly List<int> T = new List<int>();
            public void Clear() { V.Clear(); Uv.Clear(); C.Clear(); T.Clear(); }
        }

        readonly Dictionary<(uint tex, int layer), Bucket> _buckets = new Dictionary<(uint, int), Bucket>();
        readonly Dictionary<(int cam, uint tex, int layer), Mesh> _meshes = new Dictionary<(int, uint, int), Mesh>();
        readonly Dictionary<uint, Material> _materials = new Dictionary<uint, Material>();
        static Shader _shader;
        static bool _modulate2xAlpha;

        Material MaterialFor(uint tex)
        {
            if (_materials.TryGetValue(tex, out var m)) return m;
            if (!_shader)
            {
                _shader = Shader.Find("NFSMW/Particle");
                _modulate2xAlpha = _shader;   // NFSMW/Particle doubles alpha itself
                if (!_shader) _shader = Shader.Find("NFSMW/WorldDecal");
            }
            var t = GameData.Textures?.Get(tex);
            if (!_shader || !t) { _materials[tex] = null; return null; }
            var src = GameData.Textures.Source(tex);
            bool additive = src != null && src.AlphaBlend == 2;
            m = new Material(_shader) { name = "Particles " + (src?.Name ?? tex.ToString("X8")), mainTexture = t, renderQueue = 3100 };
            m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
            m.SetFloat("_DstBlend", (float)(additive ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
            if (m.HasProperty("_Additive")) m.SetFloat("_Additive", additive ? 1f : 0f);
            _materials[tex] = m;
            return m;
        }

        void Render()
        {
            if (_numParticles == 0) return;
            int count = Cameras(out var cams);
            for (int c = 0; c < count; c++)
            {
                var cam = cams[c];
                if (!cam || !cam.enabled || cam.cameraType != CameraType.Game) continue;
                foreach (var b in _buckets.Values) b.Clear();
                var ct = cam.transform;
                Vector3 right = ct.right, up = ct.up, fwd = ct.forward, eye = ct.position;
                bool any = false;
                foreach (var g in _groups)
                {
                    if ((cam.cullingMask & (1 << g.Layer)) == 0) continue;
                    foreach (var em in g.Emitters)
                    {
                        if (em.Particles.Count == 0) continue;
                        uint tex = em.D.TextureHash;
                        if (!_buckets.TryGetValue((tex, g.Layer), out var b)) _buckets[(tex, g.Layer)] = b = new Bucket();
                        AddQuads(b, em, right, up, fwd);
                        any = true;
                    }
                }
                if (!any) continue;
                foreach (var kv in _buckets)
                {
                    var b = kv.Value;
                    if (b.V.Count == 0) continue;
                    var mat = MaterialFor(kv.Key.tex);
                    if (!mat) continue;
                    var key = (cam.GetInstanceID(), kv.Key.tex, kv.Key.layer);
                    if (!_meshes.TryGetValue(key, out var mesh) || !mesh)
                    {
                        mesh = new Mesh { name = "Particles" };
                        mesh.MarkDynamic();
                        _meshes[key] = mesh;
                    }
                    mesh.Clear();
                    mesh.SetVertices(b.V);
                    mesh.SetUVs(0, b.Uv);
                    mesh.SetColors(b.C);
                    mesh.SetTriangles(b.T, 0, false);
                    mesh.bounds = new Bounds(eye, Vector3.one * 2000f);
                    Graphics.DrawMesh(mesh, Matrix4x4.identity, mat, kv.Key.layer, cam);
                }
            }
        }

        void AddQuads(Bucket b, FxEmitter em, Vector3 camRight, Vector3 camUp, Vector3 camFwd)
        {
            var c = em.D.AxisConstraint;
            bool constrained = c == FxConstraint.XY || c == FxConstraint.XZ || c == FxConstraint.YZ;
            float alphaScale = _modulate2xAlpha ? 1f : 2f;   // WorldDecal fallback: rgb x2 in the shader, alpha x2 here
            foreach (var p in em.Particles)
            {
                if (p.Size <= 0f) continue;
                float ang = p.Angle * (360f / 65536f);
                Vector3 r, u;
                if (constrained)
                {
                    // GetConstraintBasis (EmitterSystem.cpp:82): the sprite lies in a game-space plane, turned by its angle
                    float rad = ang * Mathf.Deg2Rad, cs = Mathf.Cos(rad), sn = Mathf.Sin(rad);
                    Vector3 gx, gy;
                    if (c == FxConstraint.XY) { gx = new Vector3(cs, sn, 0); gy = new Vector3(-sn, cs, 0); }
                    else if (c == FxConstraint.XZ) { gx = new Vector3(cs, 0, -sn); gy = new Vector3(sn, 0, cs); }
                    else { gx = new Vector3(0, cs, sn); gy = new Vector3(0, -sn, cs); }
                    r = FxFrame.U(gx) * p.Size;
                    u = FxFrame.U(gy) * p.Size;
                }
                else
                {
                    // PlatRotateScaleParticle (EmitterSystem.cpp:1140): the view's right/up turned about its forward axis
                    var q = Quaternion.AngleAxis(ang, camFwd);
                    r = q * camRight * p.Size;
                    u = q * camUp * p.Size;
                }
                var pos = FxFrame.U(p.Pos);
                int i0 = b.V.Count;
                b.V.Add(pos - r + u); b.Uv.Add(new Vector2(p.Uv.x, p.Uv.y));
                b.V.Add(pos + r + u); b.Uv.Add(new Vector2(p.Uv.z, p.Uv.y));
                b.V.Add(pos + r - u); b.Uv.Add(new Vector2(p.Uv.z, p.Uv.w));
                b.V.Add(pos - r - u); b.Uv.Add(new Vector2(p.Uv.x, p.Uv.w));
                var col = new Color(p.Colour.r / 255f, p.Colour.g / 255f, p.Colour.b / 255f, p.Colour.a / 255f * alphaScale);
                b.C.Add(col); b.C.Add(col); b.C.Add(col); b.C.Add(col);
                b.T.Add(i0); b.T.Add(i0 + 1); b.T.Add(i0 + 2);
                b.T.Add(i0); b.T.Add(i0 + 2); b.T.Add(i0 + 3);
            }
        }

        void OnDestroy()
        {
            foreach (var m in _meshes.Values) if (m) Destroy(m);
            foreach (var m in _materials.Values) if (m) Destroy(m);
            if (_instance == this) _instance = null;
        }
    }
}
