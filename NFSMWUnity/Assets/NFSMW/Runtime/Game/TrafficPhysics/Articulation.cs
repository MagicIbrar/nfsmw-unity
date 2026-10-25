using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game.TrafficPhysics
{
    /// <summary>
    /// RBTractor + RBTrailer (Physics/Behaviors/RBTractor.cpp; speed.exe ctor 0x6B8060, SetHitch 0x69B100,
    /// CanCollideWith 0x69B3D0, PlaceObject 0x6A77D0) and Dynamics::Articulation (Physics/Dynamics/Articulation.cpp,
    /// empty in the decomp; speed.exe 0x48A000-0x48D198: Resolve 0x48C910, Joint::Resolve 0x48C5D0, IsJoined
    /// 0x48AEE0, Release 0x48CD20, joint limit 0x48C270, limit push-back 0x48B290 / 0x48B190, anchor move 0x48AF30,
    /// two-body solve 0x48A000, one-body solve 0x48A910, impulse response 0x486DA0, arc quaternion 0x48AD40) and
    /// CollisionGeometry::CreateJoint (0x6718F0 → 0x48D1A0 → 0x48D050). See specs/traffic_physics.md §4.
    /// <list type="bullet">
    /// <item>The tractor's pvehicle Trailer RefSpec spawns the trailer (a PVehicle of its own, DRIVER_NONE) with it.</item>
    /// <item>CreateJoint: the tractor's "5THWHEEL" bound with JointFemale meets the trailer's "5THWHEEL" with JointMale;
    /// their positions (body frame, mm/1000) are the anchors. Each constraint child of the female node (Conical
    /// 0x100 / Prismatic 0x200) with a MalePost of the same name on the male node adds a limit of atan2(half.x,
    /// 2·half.y) turns × 360°: SEMI's prismatic post gives the ±80.5° jack-knife yaw, the conical one ±11° of
    /// pitch/roll.</item>
    /// <item><see cref="Resolve"/> is Joint::Resolve, run once per sim step after the bodies' integration and
    /// collisions (RigidBody::Update :1552 → Articulation::Resolve; here at the start of the next fixed step, on the
    /// post-step state): the world anchors are pulled together onto their mass-weighted mean (each mobile body turned
    /// so its arm points at the target, then moved so the anchor sits on it), the anchors' relative velocity is
    /// cancelled with impulses over 32 sub-steps of the reduced mass through each body's own inverse inertia, and each
    /// constraint measures the post's angle in the female frame and rotates both bodies back about the target (split
    /// by mass) past its max/min, with a contact-like impulse along the limit's normal. <see cref="ExeSolver"/> off
    /// keeps the old PhysX ConfigurableJoint [ours].</item>
    /// <item>UpdateTrailer (a 0.1 s task): the trailer's brake and e-brake follow the tractor's; the hitch lets go if
    /// either body tips below up.y 0.75, their ups differ by more than dot 0.8, or either has fewer than two wheels
    /// down for over 2 s. Hitched, the pair never collide with each other.</item>
    /// </list>
    /// </summary>
    [DefaultExecutionOrder(150)]
    public sealed class TractorHitch : MonoBehaviour
    {
        const float OffGroundDetachTime = 2f, TractorDetachDot = 0.8f, UprightDetach = 0.75f, TaskTime = 0.1f;
        /// <summary>Joint::Resolve / 0x48B290: 0x20 fractional impulse steps.</summary>
        const int SolveSteps = 32;
        /// <summary>The exe's Dynamics::Articulation solver (true) or a PhysX ConfigurableJoint [ours] (false).</summary>
        public static bool ExeSolver = true;

        public TrafficVehicle Tractor { get; private set; }
        public TrafficVehicle Trailer { get; private set; }
        public bool Hitched { get; private set; }
        /// <summary>Female (tractor) and male (trailer) 5th-wheel anchors in each body frame.</summary>
        public Vector3 FifthWheel, TrailerFifthWheel;
        public float YawLimitDeg = 80f, ConeLimitDeg = 11f;

        /// <summary>One Articulation constraint (0x48B070 ctor, 0x68 bytes): mode 0 prismatic (yaw), 2 conical.</summary>
        sealed class Limit
        {
            public int Mode;
            /// <summary>The constraint frame in the female body (the constraint bound's orientation; its z is the axis).</summary>
            public Quaternion Frame;
            /// <summary>The MalePost relative to the male anchor (male body frame).</summary>
            public Vector3 PostOffset;
            /// <summary>+0x58 max / +0x5C min, in turns.</summary>
            public float Max, Min;
        }

        readonly List<Limit> _limits = new List<Limit>();
        ConfigurableJoint _joint;
        float _task, _detachTimer;
        bool _jointFound;
        Renderer[] _trailerRenderers;
        Collider[] _tractorCols, _trailerCols;

        public Renderer[] TrailerRenderers => _trailerRenderers;

        /// <summary>RBTractor ctor: the trailer comes with the tractor and is hitched at once.</summary>
        public static TractorHitch Create(TrafficVehicle tractor, Color paint)
        {
            var specs = tractor.Specs;
            if (string.IsNullOrEmpty(specs.Trailer)) return null;
            var ts = VehicleSpecs.Get(specs.Trailer);
            if (ts == null) return null;
            string model = ts.Model.ToUpperInvariant();
            if (!System.IO.Directory.Exists(System.IO.Path.Combine(GameData.Root, "CARS", model))) return null;
            CarBuilder.Result built;
            try { built = CarBuilder.Build(model, paint); }
            catch (System.Exception e) { Debug.LogWarning($"NFSMW traffic: trailer {model}: {e.Message}"); return null; }
            var root = built.Root;
            root.name = "Trailer " + ts.Vehicle;
            root.transform.SetParent(tractor.transform.parent, false);
            root.AddComponent<Rigidbody>();
            GameBootstrap.AddBodyCollider(built);
            var trailer = TrafficVehicle.Attach(root, ts, null, built.Wheels);
            if (trailer == null) { Destroy(root); return null; }
            trailer.DriverClass = 4;   // DRIVER_NONE (SimTypes.h:222; was 0, DRIVER_HUMAN)
            var hitch = tractor.gameObject.AddComponent<TractorHitch>();
            hitch.Tractor = tractor;
            hitch.Trailer = trailer;
            hitch._trailerRenderers = root.GetComponentsInChildren<Renderer>(true);
            hitch.FindJoint();
            if (!hitch._jointFound) { Destroy(hitch); return null; }   // no 5th wheels: CreateJoint fails, no trailer [ours]
            TrafficContacts.Register(trailer);
            return hitch;
        }

        /// <summary>CollisionGeometry::CreateJoint: the anchors and the limits from the two 5THWHEEL nodes.</summary>
        void FindJoint()
        {
            var female = Tractor.Specs;
            var male = Trailer.Specs;
            _jointFound = false;
            _limits.Clear();
            if (female.Geometry == null || male.Geometry == null) return;
            uint name = CollisionGeometry.NameHash("5THWHEEL");
            CollisionGeometry.Bound f = default, m = default;
            bool hf = false, hm = false;
            foreach (var b in female.Geometry.Bounds)
                if (b.Name == name && (b.Flags & CollisionGeometry.BoundFlags.JointFemale) != 0) { f = b; hf = true; break; }
            foreach (var b in male.Geometry.Bounds)
                if (b.Name == name && (b.Flags & CollisionGeometry.BoundFlags.JointMale) != 0) { m = b; hm = true; break; }
            if (!hf || !hm) return;
            FifthWheel = new Vector3(f.Position.X, f.Position.Y, f.Position.Z);
            TrailerFifthWheel = new Vector3(m.Position.X, m.Position.Y, m.Position.Z);
            _jointFound = true;
            foreach (var c in female.Geometry.ChildrenOf(f))
            {
                bool conical = (c.Flags & CollisionGeometry.BoundFlags.ConstraintConical) != 0;
                bool prismatic = (c.Flags & CollisionGeometry.BoundFlags.ConstraintPrismatic) != 0;
                if (!conical && !prismatic) continue;
                foreach (var post in male.Geometry.ChildrenOf(m))
                {
                    if ((post.Flags & CollisionGeometry.BoundFlags.MalePost) == 0 || post.Name != c.Name) continue;
                    // FUN_005CB5D0(half.x, 2·half.y) = atan2 in turns, × 360
                    float turns = Mathf.Atan2(c.HalfDims.X, c.HalfDims.Y * 2f) / (2f * Mathf.PI);
                    float deg = turns * 360f;
                    if (conical) ConeLimitDeg = deg; else YawLimitDeg = deg;
                    var q = new Quaternion(c.Qx, c.Qy, c.Qz, c.Qw);
                    if (q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w < 1e-6f) q = Quaternion.identity;
                    q.Normalize();
                    // 0x48D050: mode 0 prismatic, 2 conical; the post offset from the male anchor; max = min = the limit
                    _limits.Add(new Limit
                    {
                        Mode = conical ? 2 : 0,
                        Frame = q,
                        PostOffset = new Vector3(post.Position.X - m.Position.X, post.Position.Y - m.Position.Y, post.Position.Z - m.Position.Z),
                        Max = turns,
                        Min = turns,
                    });
                    break;
                }
            }
        }

        /// <summary>SetHitch: on, joint the pair and pose the trailer; off, release it with its brakes free.</summary>
        public void SetHitch(bool on)
        {
            if (on)
            {
                if (Trailer == null || Hitched || !_jointFound) return;
                if (!Trailer.gameObject.activeSelf) Trailer.gameObject.SetActive(true);
                Pose();
                if (ExeSolver) IgnorePair(true);
                else
                {
                    _joint = Tractor.gameObject.AddComponent<ConfigurableJoint>();
                    _joint.connectedBody = Trailer.Rb;
                    _joint.autoConfigureConnectedAnchor = false;
                    _joint.anchor = Tractor.Specs.Pivot + FifthWheel;
                    _joint.connectedAnchor = Trailer.Specs.Pivot + TrailerFifthWheel;
                    _joint.axis = Vector3.up;
                    _joint.secondaryAxis = Vector3.forward;
                    _joint.xMotion = _joint.yMotion = _joint.zMotion = ConfigurableJointMotion.Locked;
                    _joint.angularXMotion = ConfigurableJointMotion.Limited;
                    _joint.angularYMotion = ConfigurableJointMotion.Limited;
                    _joint.angularZMotion = ConfigurableJointMotion.Limited;
                    _joint.lowAngularXLimit = new SoftJointLimit { limit = -YawLimitDeg };
                    _joint.highAngularXLimit = new SoftJointLimit { limit = YawLimitDeg };
                    _joint.angularYLimit = new SoftJointLimit { limit = ConeLimitDeg };
                    _joint.angularZLimit = new SoftJointLimit { limit = ConeLimitDeg };
                    _joint.enableCollision = false;
                }
                Hitched = true;
                Trailer.Joined = true;
                _detachTimer = 0f;
            }
            else
            {
                if (Hitched && Trailer != null)
                {
                    Trailer.Brake = 0f;
                    Trailer.EBrake = 0f;
                    if (_joint) Destroy(_joint);
                    _joint = null;
                    IgnorePair(false);
                    Hitched = false;
                    Trailer.Joined = false;
                    // new EJointDetached(tractor), new EJointDetached(trailer) (RBTractor.cpp SetHitch): JOINT_DETACHED to
                    // each vehicle's event sequencer (TrailerB / TrailerLog engines in L2RA) [?: the event's handler]
                    JointDetached(Tractor);
                    JointDetached(Trailer);
                }
            }
        }

        /// <summary>RBTractor::CanCollideWith (0x69B3D0): a hitched pair never collides with each other.</summary>
        void IgnorePair(bool ignore)
        {
            if (!Tractor || !Trailer) return;
            if (_tractorCols == null || _tractorCols.Length == 0) _tractorCols = Tractor.GetComponentsInChildren<Collider>(true);
            if (_trailerCols == null || _trailerCols.Length == 0) _trailerCols = Trailer.GetComponentsInChildren<Collider>(true);
            foreach (var a in _tractorCols)
                foreach (var b in _trailerCols)
                    if (a && b && !a.isTrigger && !b.isTrigger) Physics.IgnoreCollision(a, b, ignore);
        }

        static void JointDetached(TrafficVehicle v)
        {
            var f = v ? v.GetComponentInParent<CarRender.CarFragments>() : null;
            if (f) f.Stimulus(Smackable.SeqStimuli.JointDetached);
        }

        /// <summary>
        /// RBTractor::Pose (0x69B1.., RBTractor.cpp): resetPos = tractor body position + R·(m5thWheel − mTrailer5thWheel)
        /// (both anchors from their body origins), then trailer->SetVehicleOnGround(resetPos, tractor forward) — the trailer
        /// set down on the road under its own wheels, facing the tractor's heading (TrafficVehicle.SetVehicleOnGround), with
        /// the detach timer cleared. [ours] The trailer then takes the tractor's velocity and wheel speed (the exe's
        /// SetVehicleOnGround leaves it at SetSpeed(0) and the joint pulls it along).
        /// </summary>
        public bool Pose()
        {
            if (Trailer == null || !_jointFound) return false;
            _detachTimer = 0f;
            var t = Tractor.transform;
            var rb = Trailer.Rb;
            rb.isKinematic = false;
            var resetPos = Tractor.BodyOrigin + Tractor.Rb.rotation * (FifthWheel - TrailerFifthWheel);
            bool ok = Trailer.SetVehicleOnGround(resetPos, t.forward);
            rb.linearVelocity = Tractor.Rb.linearVelocity;
            rb.angularVelocity = Vector3.zero;
            Trailer.MatchSpeed(Vector3.Dot(rb.linearVelocity, t.forward));
            return ok;
        }

        /// <summary>RBTractor::PlaceObject: a tractor put somewhere takes its trailer along, hitched.</summary>
        public void Place()
        {
            if (Trailer == null) return;
            if (Hitched) SetHitch(false);
            SetHitch(true);
            if (!Hitched) Pose();
        }

        /// <summary>The tractor went back to the pool: its trailer goes with it.</summary>
        public void Park()
        {
            SetHitch(false);
            if (Trailer) Trailer.gameObject.SetActive(false);
        }

        void FixedUpdate()
        {
            if (Trailer == null || !Trailer.isActiveAndEnabled) return;
            if (Hitched && ExeSolver && !_joint) Resolve();
            _task += Time.fixedDeltaTime;
            if (_task < TaskTime) return;
            float dT = _task;
            _task = 0f;
            UpdateTrailer(dT);
        }

        /// <summary>RBTractor::UpdateTrailer.</summary>
        void UpdateTrailer(float dT)
        {
            if (Hitched)
            {
                Trailer.Brake = Tractor.Brake;
                Trailer.EBrake = Tractor.EBrake;
            }
            else
            {
                Trailer.Brake = 0f;
                Trailer.EBrake = 0f;
                return;
            }
            var up = Tractor.Rb.rotation * Vector3.up;
            var tup = Trailer.Rb.rotation * Vector3.up;
            if (up.y < UprightDetach || tup.y < UprightDetach || Vector3.Dot(tup, up) < TractorDetachDot) { SetHitch(false); return; }
            if (Trailer.Chassis.NumWheelsOnGround < 2 || Tractor.Chassis.NumWheelsOnGround < 2) _detachTimer += dT;
            else _detachTimer = 0f;
            if (_detachTimer > OffGroundDetachTime) SetHitch(false);
        }

        // ---- Dynamics::Articulation::Joint::Resolve (0x48C5D0) --------------------------------------------------

        /// <summary>The joint's view of one body (IEntity: GetPosition, GetMatrix, GetMass, IsImmobile, velocities).</summary>
        struct Side
        {
            public Rigidbody Rb;
            public Vector3 Anchor;      // body frame (from the Rigidbody origin)
            public bool Mobile;
            public float Mass;
            public Vector3 Com;         // world
            public Matrix4x4 InvInertia; // world inverse inertia tensor

            public Vector3 Arm => Rb.rotation * Anchor;
            public Vector3 World => Rb.position + Arm;
        }

        static Side MakeSide(TrafficVehicle v, Vector3 anchor)
        {
            var rb = v.Rb;
            var s = new Side { Rb = rb, Anchor = anchor, Mobile = !rb.isKinematic, Mass = Mathf.Max(1f, rb.mass), Com = rb.worldCenterOfMass };
            var r = rb.rotation * rb.inertiaTensorRotation;
            var I = rb.inertiaTensor;
            var d = Matrix4x4.Scale(new Vector3(1f / Mathf.Max(I.x, 1e-3f), 1f / Mathf.Max(I.y, 1e-3f), 1f / Mathf.Max(I.z, 1e-3f)));
            var R = Matrix4x4.Rotate(r);
            s.InvInertia = R * d * R.transpose;
            return s;
        }

        /// <summary>Joint::Resolve: both anchors onto the mass-weighted target, the relative anchor velocity cancelled, then the limits.</summary>
        void Resolve()
        {
            var a = MakeSide(Tractor, Tractor.Specs.Pivot + FifthWheel);
            var b = MakeSide(Trailer, Trailer.Specs.Pivot + TrailerFifthWheel);
            if (!a.Mobile && !b.Mobile) return;
            var wa = a.World;
            var wb = b.World;
            float w = !b.Mobile ? 0f : !a.Mobile ? 1f : a.Mass / (a.Mass + b.Mass);
            var target = wa * w + wb * (1f - w);
            MoveAnchorTo(ref a, target);
            MoveAnchorTo(ref b, target);
            // the velocity solve (0x48A000 both mobile / 0x48A910 one) from fresh Moments (0x487160)
            a.Com = a.Rb.worldCenterOfMass;
            b.Com = b.Rb.worldCenterOfMass;
            SolvePoint(ref a, ref b, target, Vector3.zero);
            foreach (var l in _limits) ApplyLimit(l, ref a, ref b, target);
        }

        /// <summary>
        /// 0x48AF30: a mobile body is turned so its arm points at the target (the arc from arm to target − position, 0x48AD40
        /// [?: the vectors are taken as normalized by 0x488680]) and then placed so the anchor sits on the target.
        /// </summary>
        static void MoveAnchorTo(ref Side s, Vector3 target)
        {
            if (!s.Mobile) return;
            var rb = s.Rb;
            var arm = s.Arm;
            if (arm.sqrMagnitude > 0f)
            {
                var d = target - rb.position;
                if (d.sqrMagnitude > 0f)
                {
                    var q = ArcQuaternion(arm.normalized, d.normalized);
                    rb.rotation = q * rb.rotation;
                }
            }
            rb.position = target - rb.rotation * s.Anchor;
        }

        /// <summary>0x48AD40: the shortest-arc rotation taking a onto b (unit vectors).</summary>
        static Quaternion ArcQuaternion(Vector3 a, Vector3 b)
        {
            float d = Vector3.Dot(a, b);
            if (d >= 1f) return Quaternion.identity;
            var c = Vector3.Cross(a, b);
            if (d < -0.99999f)
            {
                // 180°: any axis perpendicular to a (0x4885D0 normalizes the cross; [?] a zero cross is taken as x)
                var axis = c.sqrMagnitude > 1e-12f ? c.normalized : Vector3.Cross(a, Mathf.Abs(a.x) < 0.9f ? Vector3.right : Vector3.up).normalized;
                return new Quaternion(axis.x, axis.y, axis.z, 0f);
            }
            float s = Mathf.Sqrt((1f + d) * 2f);
            float inv = 1f / s;
            return new Quaternion(c.x * inv, c.y * inv, c.z * inv, s * 0.5f);
        }

        /// <summary>0x486DA0: an impulse at r from the centre of mass: dv = J/m, dω = I⁻¹(r × J).</summary>
        static void ImpulseResponse(in Side s, Vector3 j, Vector3 r, out Vector3 dv, out Vector3 dw)
        {
            dv = j / s.Mass;
            dw = s.InvInertia.MultiplyVector(Vector3.Cross(r, j));
        }

        /// <summary>
        /// 0x48A000 (two mobile bodies) / 0x48A910 (one): the anchors' relative velocity at the target, × the reduced mass
        /// mA·mB/(mA + mB) (the mobile side's mass when the other is immobile), applied as impulses in SolveSteps
        /// fractions i/n (step 0 records only), each re-measuring the relative velocity after the previous fraction.
        /// With <paramref name="normal"/> set, only the component along it is cancelled, and only while closing (the
        /// limit's contact-like React, 0x48B290) [ours: React(Moment&amp;, plane) restricted to the plane normal].
        /// </summary>
        static void SolvePoint(ref Side a, ref Side b, Vector3 point, Vector3 normal)
        {
            bool onlyNormal = normal.sqrMagnitude > 0.5f;
            var ra = point - a.Com;
            var rbv = point - b.Com;
            Vector3 va = a.Rb.linearVelocity, wa = a.Rb.angularVelocity;
            Vector3 vb = b.Rb.linearVelocity, wb = b.Rb.angularVelocity;
            float mu = !a.Mobile ? b.Mass : !b.Mobile ? a.Mass : a.Mass * b.Mass / (a.Mass + b.Mass);
            Vector3 dva = Vector3.zero, dwa = Vector3.zero, dvb = Vector3.zero, dwb = Vector3.zero;
            int done = 0;
            for (int i = 0; i < SolveSteps; i++)
            {
                var pa = va + Vector3.Cross(wa, ra);
                var pb = vb + Vector3.Cross(wb, rbv);
                var rel = pa - pb;
                if (onlyNormal)
                {
                    float vn = Vector3.Dot(rel, normal);
                    if (vn >= 0f) break;   // separating along the limit normal: nothing to stop
                    rel = normal * vn;
                }
                var j = rel * mu;
                if (j.sqrMagnitude == 0f) break;
                done++;
                if (i == 0) continue;   // step 0: the record of the closing velocity (COLLISION_INFO), no impulse
                float frac = (float)i / SolveSteps;
                var imp = -j * frac;
                if (a.Mobile)
                {
                    ImpulseResponse(a, imp, ra, out var dv, out var dw);
                    va += dv; wa += dw; dva += dv; dwa += dw;
                }
                if (b.Mobile)
                {
                    ImpulseResponse(b, -imp, rbv, out var dv, out var dw);
                    vb += dv; wb += dw; dvb += dv; dwb += dw;
                }
            }
            if (done == 0) return;
            if (a.Mobile) { a.Rb.linearVelocity += dva; a.Rb.angularVelocity += dwa; }
            if (b.Mobile) { b.Rb.linearVelocity += dvb; b.Rb.angularVelocity += dwb; }
        }

        /// <summary>
        /// 0x48C270: the post's direction from the target in the female constraint frame. Mode 0 (prismatic) measures
        /// the yaw atan2(x, z); mode 2 (conical) first turns the frame about its own axis so the post lies in its x–z
        /// plane and measures the cone angle [?: the exe composes an x-rotation by −atan2(y, z) and a z-rotation by
        /// atan2(y, x); read here as "the cone angle, pushed back about the perpendicular"]. Past max (or −min) both
        /// bodies are rotated back about the target by the excess, split by mass (0x48B290 → 0x48B190), and the
        /// closing velocity along the limit normal is stopped.
        /// </summary>
        void ApplyLimit(Limit l, ref Side a, ref Side b, Vector3 target)
        {
            var post = b.Rb.position + b.Rb.rotation * (b.Anchor + l.PostOffset);
            var d = post - target;
            if (d.sqrMagnitude < 1e-10f) return;
            var frame = a.Rb.rotation * l.Frame;
            var dl = Quaternion.Inverse(frame) * d;
            if (l.Mode != 0)
            {
                float a2 = Mathf.Atan2(dl.y, dl.x) * Mathf.Rad2Deg;
                frame = frame * Quaternion.AngleAxis(a2, Vector3.forward);
                dl = Quaternion.Inverse(frame) * d;
            }
            float angle = Mathf.Atan2(dl.x, dl.z) / (2f * Mathf.PI);   // turns
            float delta;
            if (angle > l.Max) delta = l.Max - angle;
            else if (angle < -l.Min) delta = -l.Min - angle;
            else return;
            var axis = frame * Vector3.up;
            // the limit normal: the frame's x, facing the way the post must come back (the exe keeps one per side)
            var normal = frame * Vector3.right * (delta < 0f ? -1f : 1f);
            if (a.Mobile && b.Mobile)
            {
                float w = a.Mass / (a.Mass + b.Mass);
                RotateAboutTarget(ref a, axis, target, -(1f - w) * delta);
                RotateAboutTarget(ref b, axis, target, w * delta);
                a.Com = a.Rb.worldCenterOfMass;
                b.Com = b.Rb.worldCenterOfMass;
                // React(moB, plane{target, normal}, 32): A's post point velocity relative to B along the normal
                SolvePoint(ref b, ref a, post, normal);
            }
            else
            {
                // one mobile side takes the whole excess; its yaw rate about the frame axis is zeroed when it drives into the limit
                ref Side m = ref (a.Mobile ? ref a : ref b);
                RotateAboutTarget(ref m, axis, target, a.Mobile ? -delta : delta);
                var wl = Quaternion.Inverse(frame) * m.Rb.angularVelocity;
                if (wl.y * delta * (a.Mobile ? -1f : 1f) < 0f)
                {
                    wl.y = 0f;
                    m.Rb.angularVelocity = frame * wl;
                }
            }
        }

        /// <summary>0x48B190: the body turned about the frame's y axis through the target, its anchor kept on the target.</summary>
        static void RotateAboutTarget(ref Side s, Vector3 axis, Vector3 target, float turns)
        {
            if (!s.Mobile || turns == 0f) return;
            var rb = s.Rb;
            rb.rotation = Quaternion.AngleAxis(turns * 360f, axis) * rb.rotation;
            rb.position = target - rb.rotation * s.Anchor;
        }

        void OnDestroy()
        {
            if (_joint) Destroy(_joint);
            if (Hitched) IgnorePair(false);
            if (Trailer) Destroy(Trailer.gameObject);
        }
    }
}
