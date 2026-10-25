using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game.Police
{
    /// <summary>
    /// The pursuit helicopter (AIVehicleHelicopter + AIActionHeliPursuit / AIActionHeliExit; ai_cops.md §5): a kinematic
    /// body that follows a lead point ahead of the player, side-swipes it on skid-hit heat rows, orbits the last known
    /// location in search mode, and leaves when its fuel runs out. Its altitude comes from the HeliSheet flight sheets
    /// (AIVehicleHelicopter::CheckHeliSheet / FilterHeliAltitude, HeliSheets.cs); a ground raycast stands in until they
    /// are loaded, and keeps it 1.5 m above the ground [ours]. The copheli mechanic table (harness `phys9 mech`): RBVehicle,
    /// SimpleChopper as ENGINE (flight: <see cref="Chopper"/>), DamageHeli (DamageVehicle with an empty DoAutoDestruct),
    /// DrawHeli (the rotor wash, <see cref="Wash"/>; HeliRenderConn draws the model at body − R·pivot), SoundHeli (only
    /// opens the EAXSound connection: WorldAudio's SFXCTL_Helicopter is that connection).
    /// </summary>
    public sealed class CopHeli : MonoBehaviour
    {
        enum Mode { StraightLine, SkidApproach, SkidStrike, Search }
        enum ExitMode { SeekUp, SeekCar, FlyOut }

        public PursuitSystem Police;
        public float Fuel;
        public bool Exiting { get; private set; }
        public bool Finished { get; private set; }
        public float TimeSinceSeen { get; private set; } = 99f;

        Rigidbody _rb;
        Vector3 _vel, _dest, _destVel, _look;
        float _driveSpeed, _visTimer, _thinkTimer;
        Mode _mode = Mode.StraightLine;
        ExitMode _exit;
        Vector3 _skidOffset, _searchDest, _exitSeek, _lastSeenAt;
        float _skidTimer, _searchAngle;
        int _collisionAbort;
        bool _maxDecel;
        Transform _visual;
        // SimpleChopper (Physics/Behaviors/SimpleChopper.cpp, exe 0x6932C0-0x6B6EA6) state: mLastBodyOffset,
        // mLastAngVelocity, mPreviousVelocity; the body's orientation and angular velocity (RigidBody integration)
        Vector3 _lastBodyOffset, _lastAngVel, _prevVel, _angVel;
        Quaternion _rot = Quaternion.identity;
        float _dimY, _modelLift;
        ChopperSpecs _specs = new ChopperSpecs();
        float _washTimer;
        readonly HeliWash _wash = new HeliWash();
        Dynamics.WorldQueries.Pos _wpos;

        const float ThinkRate = 0.125f * 60f;
        const float Pursuit_Time_Max = 10f, YdeltForSkidKnock = 13f, SkidHitLead = 0.23f, kSkidKnockTime = 8f, kTimeBetweenSkidHitAttempts = 5f, StrikeStartD = 4f;
        const float LeadPositionTime = 0.45f, Exit_Height = 25f, kHeliVisualSphere = 75f;
        // AIVehicleHelicopter.cpp:16-22: FarLeadPositionTime, OozeFactor, OozeFactorSM, bSmoothingVelScale (bOozeAround and
        // bCrossOoze true); NeverIgnoreHeliSheet (AIActionHeliPursuit.cpp:31) = true, so the skid hits use the sheet too
        const float FarLeadPositionTime = 0.65f, OozeFactor = 0.3f, OozeFactorSM = 0.4f, SmoothingVelScale = 0.3f;
        // mHeliSheetCoord (destination), mSecondaryHeliSheetCoord (working position), mThirdHeliSheetCoord (look-ahead)
        readonly HeliSheetCoordinate _sheet = new HeliSheetCoordinate(), _sheet2 = new HeliSheetCoordinate(), _sheet3 = new HeliSheetCoordinate();

        public Vector3 Velocity => _vel;

        /// <summary>
        /// Builds the COPHELI model on a kinematic body (falls back to a box when the model cannot be built). The transform
        /// is the sim body: its origin is the collision root's pivot (PVehicle::Construct 0x689820, COPHELI root half
        /// 0.942 × 1.036 × 2.83, pivot (0.013, 1.043, −0.725) in model space; harness `physcol bounds COPHELI`), so the model
        /// hangs at −pivot and the box is the root box (the heli has no child primitives). Without the pack: the old
        /// 2.6 × 2.6 × 11 box on the model origin [ours].
        /// </summary>
        public static CopHeli Create(PursuitSystem police, Vector3 pos, Vector3 fwd, float fuel)
        {
            var go = new GameObject("Cop COPHELI");
            go.transform.SetPositionAndRotation(pos, Quaternion.LookRotation(RoadGraph.FlatDir(fwd)));
            Transform visual = null;
            try
            {
                var built = CarBuilder.Build("COPHELI", Color.white);
                foreach (var w in built.Wheels) if (w) Object.Destroy(w.gameObject);
                built.Root.transform.SetParent(go.transform, false);
                visual = built.Root.transform;
                CarShadow.SetHelicopter(built.Root);   // DrawAmbientShadow CARTYPE_COPHELI rules (car render agent)
            }
            catch (System.Exception e) { Debug.LogWarning("NFSMW police: COPHELI model: " + e.Message); }
            if (!visual)
            {
                var body = GameObject.CreatePrimitive(PrimitiveType.Capsule);
                Object.Destroy(body.GetComponent<Collider>());
                body.transform.SetParent(go.transform, false);
                body.transform.localRotation = Quaternion.Euler(90, 0, 0);
                body.transform.localScale = new Vector3(2.4f, 5f, 2.4f);
                visual = body.transform;
            }
            var box = go.AddComponent<BoxCollider>();
            var h = go.AddComponent<CopHeli>();
            CollisionGeometry.Collection col = null;
            try { col = Dynamics.CollisionBounds.UseCollisionGeometry ? Dynamics.CollisionBounds.Vehicle("copheli") : null; } catch { }
            if (col != null)
            {
                var root = col.Bounds[0];
                var half = new Vector3(Mathf.Abs(root.HalfDims.X), Mathf.Abs(root.HalfDims.Y), Mathf.Abs(root.HalfDims.Z));
                box.size = 2f * half;
                box.center = Vector3.zero;
                visual.localPosition -= new Vector3(root.Pivot.X, root.Pivot.Y, root.Pivot.Z);
                h._dimY = half.y;
                h._modelLift = root.Pivot.Y;
            }
            else
            {
                box.size = new Vector3(2.6f, 2.6f, 11f);
                box.center = new Vector3(0, 1.3f, 0);
                h._dimY = 1.3f;
            }
            var rb = go.AddComponent<Rigidbody>();
            rb.isKinematic = true;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
            h.Police = police;
            h.Fuel = fuel;
            h._rb = rb;
            h._visual = visual;
            h._dest = pos;
            h._look = pos + fwd;
            h._rot = go.transform.rotation;
            h._specs = ChopperSpecs.ForVehicle("copheli");
            return h;
        }

        /// <summary>Fuel out, pursuit over, bail or flee: AIGoalHeliExit.</summary>
        public void Exit()
        {
            if (Exiting) return;
            Exiting = true;
            _exit = ExitMode.SeekUp;
            // AIActionHeliExit::BeginAction (:106-111): the seek position is the heli's own, its x/z restricted to the road net
            // (RestrictPointToRoadNet, AIVehicleHelicopter.cpp:347-361: a traffic nav at the point along the player's heading,
            // dirWeight 0), then Exit_Height up
            var p = transform.position;
            _exitSeek = p;
            var nav = new RoadNav { DecisionFilter = false, TrafficFilter = true };
            nav.SetLaneType(RoadGraph.Lane.Traffic);
            var player = Police && Police.Player ? Police.Player.transform.forward : transform.forward;
            if (nav.InitAtPoint(p, player, false, 0f)) { _exitSeek.x = nav.Position.x; _exitSeek.z = nav.Position.z; }
            _exitSeek.y += Exit_Height;
        }

        void FixedUpdate()
        {
            if (!Police || !Police.Player) return;
            float dt = Time.fixedDeltaTime;
            _thinkTimer += dt;
            if (_thinkTimer >= 1f / ThinkRate)
            {
                float tdt = _thinkTimer;
                _thinkTimer = 0;
                Think(tdt);
            }
            Fly(dt);
        }

        void Think(float dt)
        {
            var player = Police.Player;
            Fuel -= dt;
            if (Fuel <= 0) { Fuel = 0; Exit(); }
            // AIVehiclePursuit::Update: LOS every 0.25 s
            _visTimer += dt;
            TimeSinceSeen += dt;
            if (_visTimer >= 0.25f)
            {
                _visTimer -= 0.25f;
                if (CanSeeTarget(player)) TimeSinceSeen = -0.25f;
            }
            if (Exiting) ExitUpdate(player);
            else PursuitUpdate(player, dt);
        }

        /// <summary>
        /// AIVehicleHelicopter::CanSeeTarget (:119-202): candidate inside the 75 m sphere, or within heliLOSdistance while
        /// facing the player; then not while the view is in a tunnel or overpass (AmIinATunnel(view, 1), approximated
        /// by the camera in a tunnel/overpass zone [?]) and only with a clear ray to the player +1 m, which records
        /// mLastPlaceHeliSawPerp. Unseen: a perp hidden from helicopters latches mPerpHiddenFromMe (unseen until it
        /// leaves the zone); otherwise within 150 m (2 × the sphere) it still counts while the player is within 20 m of
        /// the last sighting. Unlike the cars, the helicopter does not test PursuitMeterCanShowBusted.
        /// </summary>
        bool CanSeeTarget(PlayerCar player)
        {
            bool hidden = Police.PerpHiddenFromHelicopters;
            if (hidden && _perpHiddenFromMe) return false;
            _perpHiddenFromMe = false;
            var p = transform.position;
            var tp = player.transform.position;
            float d = Vector3.Distance(p, tp);
            bool seen = d <= kHeliVisualSphere;
            if (!seen && d < Police.Row.HeliLOS) seen = Vector3.Dot(transform.forward, tp - p) > 0;
            if (seen)
            {
                if (PoliceZones.CameraInTunnel()) seen = false;
                else if (CopCar.WorldRay(p, (tp + Vector3.up - p).normalized, Vector3.Distance(tp + Vector3.up, p), out _)) seen = false;
                else _lastSeenAt = tp;
            }
            if (!seen)
            {
                if (hidden) _perpHiddenFromMe = true;
                else if (d < kHeliVisualSphere * 2f && (tp - _lastSeenAt).sqrMagnitude < 400f) return true;
                return false;
            }
            return true;
        }

        bool _perpHiddenFromMe;

        // ---- AIActionHeliPursuit ----

        void PursuitUpdate(PlayerCar player, float dt)
        {
            _skidTimer -= dt;
            if (_collisionAbort > 0) _collisionAbort--;
            if (!Police.PerpInSight)
            {
                if (_mode != Mode.Search) { NextSearchDest(); _mode = Mode.Search; }
            }
            else if (_mode == Mode.Search) _mode = Mode.StraightLine;
            switch (_mode)
            {
                case Mode.StraightLine: StraightLine(player); break;
                case Mode.SkidApproach:
                case Mode.SkidStrike: SkidHit(player); break;
                case Mode.Search: Search(); break;
            }
        }

        void StraightLine(PlayerCar player)
        {
            var my = transform.position;
            var pp = player.transform.position;
            var pb = player.GetComponent<Rigidbody>();
            var pv = pb ? pb.linearVelocity : Vector3.zero;
            float pSpeed = pv.magnitude;
            var unitVel = pSpeed > 1e-4f ? pv / pSpeed : Vector3.zero;
            var perp2Me = my - pp;
            bool skidEnabled = Police.SkidHitEnabled && _skidTimer < -kTimeBetweenSkidHitAttempts;
            float dist = perp2Me.magnitude;
            if (dist < 35f && dist > 5f)
            {
                float ydif = perp2Me.y;
                var flat = perp2Me; flat.y = 0;
                var uv = unitVel; uv.y = 0;
                if (Vector3.Dot(flat, uv) / dist > 0.707f && skidEnabled && ydif < YdeltForSkidKnock)
                {
                    _mode = Mode.SkidApproach;
                    _skidTimer = kSkidKnockTime;
                    _skidOffset = Vector3.zero;
                }
            }
            var dir = player.transform.forward;
            float lead = Mathf.Min(45f, pSpeed * 0.4f + 30f);
            if (skidEnabled) lead *= 0.75f;
            var seek = pp + dir * lead;
            float height = seek.y + (_skidTimer > 0 ? 2f : dist > 55f || _skidTimer > -kTimeBetweenSkidHitAttempts ? 12f : 6f);
            var meToSeek = seek - my; meToSeek.y = 0;
            float distToSeek = meToSeek.magnitude;
            meToSeek = distToSeek > 1e-4f ? meToSeek / distToSeek : Vector3.zero;
            float adjust = Vector3.Dot(meToSeek, pv);
            if (adjust < 0) adjust *= 0.5f;
            float closing;
            var look = seek;
            if (distToSeek > 15f) closing = distToSeek > 300f ? 50f : 30f;
            else
            {
                closing = distToSeek > 3f ? 20f : distToSeek;
                if (distToSeek < 5f && pSpeed < 15f) look = pp;
            }
            _driveSpeed = adjust + closing;
            _destVel = pv;
            seek.y = height;
            _dest = seek;
            _look = look;
        }

        void SkidHit(PlayerCar player)
        {
            var my = transform.position;
            var pp = player.transform.position;
            var pb = player.GetComponent<Rigidbody>();
            var pv = pb ? pb.linearVelocity : Vector3.zero;
            var perp2Me = my - pp;
            var fwd = player.transform.forward;
            var right = player.transform.right;
            float dot = Vector3.Dot(fwd, perp2Me), d2 = perp2Me.sqrMagnitude, dotRight = Vector3.Dot(perp2Me, right);
            Vector3 seek;
            if (_mode == Mode.SkidApproach)
            {
                _skidOffset = right * (dotRight < 0 ? -6f : 6f);
                var local = _skidOffset + pv * SkidHitLead;
                local.y += 1.8f;
                if (perp2Me.y < 2f) local.y += 3f;
                seek = pp + local;
                var flat = seek - my; flat.y = 0;
                if (flat.magnitude < StrikeStartD && Mathf.Abs(dotRight) > 1.9f)
                {
                    _mode = Mode.SkidStrike;
                    if (_skidTimer < 2f) _skidTimer = 2f;
                }
            }
            else
            {
                var local = pv * 0.092f - _skidOffset * 0.5f;
                local.y += 0.4f;
                seek = pp + local;
            }
            _look = pp + pv;
            _driveSpeed = 100f;
            _destVel = pv;
            _dest = seek;
            if (dot > 0) { if (d2 > 1600f || my.y < pp.y) _skidTimer = 0; }
            else if (d2 > 144f) _skidTimer = 0;
            if (_skidTimer <= 0) _mode = Mode.StraightLine;
        }

        void NextSearchDest()
        {
            float radius = Police.Row.SearchModeRoadblockRadius;
            float r = NfsMw.Core.SimRandom.RandFloatRange(radius * 0.7f) + radius * 0.2f;   // SetNextPerpSearchDest: SimRandom_FloatRange
            _searchAngle += 0.125f;
            if (_searchAngle >= 1f) _searchAngle -= 1f;
            _searchDest = Police.LastKnownLocation + PoliceData.RotateInXZ(_searchAngle, new Vector3(r, 0, 0));
            // SetNextPerpSearchDest (AIActionHeliPursuit.cpp:371): FilterHeliAltitude, then 5 m up
            if (!FilterHeliAltitude(ref _searchDest) && !HeliSheets.Ready) _searchDest.y = Mathf.Max(_searchDest.y, Ground(_searchDest));
            _searchDest.y += 5f;
        }

        void Search()
        {
            var my = transform.position;
            if (new Vector2(my.x - _searchDest.x, my.z - _searchDest.z).magnitude < 30f) NextSearchDest();
            _driveSpeed = 70f;
            _destVel = _vel;
            _dest = _searchDest;
            _look = _searchDest;
        }

        // ---- AIActionHeliExit ----

        void ExitUpdate(PlayerCar player)
        {
            var pp = player.transform.position;
            var my = transform.position;
            var dir = player.transform.forward;
            switch (_exit)
            {
                case ExitMode.SeekUp:
                    if (my.y - pp.y > 15f) { _exitSeek = pp + dir * 85f; _exitSeek.y += Exit_Height; _exit = ExitMode.SeekCar; }
                    break;
                case ExitMode.SeekCar:
                    if ((_exitSeek - my).sqrMagnitude < 25f)
                    {
                        float r = Vector3.Dot(player.transform.right, _vel) >= 0 ? -9f : 3f;
                        var d = dir + player.transform.right * r;
                        _exitSeek = pp - d * 200f;
                        _exitSeek.y = pp.y + Exit_Height + 5f;
                        _exit = ExitMode.FlyOut;
                    }
                    break;
            }
            _look = _exitSeek;
            _driveSpeed = 100f;
            _destVel = _vel;
            _dest = _exitSeek;
            // done (and unspawned) once flying out, above, behind and far from the player
            if (_exit == ExitMode.FlyOut && my.y - pp.y > Exit_Height && Vector3.Dot(dir, my - pp) < 0 && Vector3.Distance(my, pp) > 150f)
                Finished = true;
        }

        // ---- flight (AIVehicleHelicopter::OnDriving, :418-514) ----

        void Fly(float dt)
        {
            var p = _rb.position;
            var work = p + _vel * LeadPositionTime;
            var comp = _destVel; comp.y = 0;
            var dest = _dest + comp * LeadPositionTime;
            AvoidCamera(ref dest);
            var smoothing = Vector3.zero;
            if (HeliSheets.Ready) CheckHeliSheet(p, p + _vel * FarLeadPositionTime, work, ref dest, ref smoothing);
            else
            {
                // [ours] until the sheets are loaded: never below the ground under the destination + 4 m
                float floor = Ground(dest) + 4f;
                if (dest.y < floor && _mode != Mode.SkidStrike) dest.y = floor;
            }
            var move = dest - work;
            float moveLen = move.magnitude, yDelt = move.y;
            move.y = 0;
            float rbSpeed = _vel.magnitude;
            var unit = move.sqrMagnitude > 1e-6f ? move.normalized : Vector3.zero;
            float closing = Vector3.Dot(_vel - comp, unit), closingRaw = Vector3.Dot(_vel, unit);
            float drive = _driveSpeed;
            _maxDecel = false;
            if (closing > 3f)
            {
                float avail = Mathf.Min(80f, rbSpeed * 0.2f + 30f);
                float use = Mathf.Min(avail, closing * 2f);
                if (moveLen <= closing * closing / (1.55f * use)) { drive *= 0.4f; _maxDecel = true; }
            }
            else if (closingRaw < -1f && moveLen < 15f) _maxDecel = true;
            if (_mode == Mode.SkidApproach || _mode == Mode.SkidStrike) _maxDecel = true;
            if (moveLen > 2f) move *= drive / moveLen;
            if (yDelt >= 0 && yDelt < 7f) { yDelt *= 5f; if (_destVel.y > 0) yDelt += _destVel.y * 2f; }
            else if (yDelt < 0 && yDelt > -5f) { yDelt *= 3f; if (_destVel.y < 0) yDelt += _destVel.y; }
            move.y = yDelt;
            // the look-ahead's sheet ooze: 30 % of the desired velocity (OnDriving, AIVehicleHelicopter.cpp:503-507)
            float lenSmooth = smoothing.magnitude;
            if (lenSmooth > 0.1f) move = move * (1f - SmoothingVelScale) + smoothing * (drive / lenSmooth * SmoothingVelScale);
            Chopper(dt, move, _look - p);
            Wash(dt);
        }

        // ---- SimpleChopper (Physics/Behaviors/SimpleChopper.cpp; OnTaskSimulate 0x6A2430, pitch/roll 0x6A2030) ----

        const float Max_Chopper_Accel = 80f, Min_Chopper_Accel = 30f, Chopper_Ratio = 2f;   // DAT_008f8dcc/dd0/dd4

        /// <summary>
        /// SimpleChopper::OnTaskSimulate [C, 0x6A2430]: the body position swings about a point dimension.y above it
        /// (mLastBodyOffset = R·(0, −dim.y, 0)); the desired velocity is capped at chopperspecs MAX_SPEED_MPS; the
        /// acceleration toward it is Δv·2 (Chopper_Ratio), limited to min(80, 0.6·speed + 30) m/s² (80 with
        /// MaxDeceleration) when Δv·2 &gt; 1; the rotor cancels GRAVITY exactly (−m·GRAVITY up, so the body never falls).
        /// The angular velocity is SET each tick: pitch/roll from SetTorqueToMatchPitchAndRoll on the measured
        /// acceleration (dt &gt; 0.005), yaw = −8·(ideal × actual).y clamped ±1.3 toward the desired facing (both flattened),
        /// then smoothed (ω + 7·ω_last)/8. The kinematic body integrates it like RigidBody::DoIntegration
        /// (q += ½ ω q). [ours] the body never goes below the ground + 1.5 m (the exe's heli collides with the world
        /// through its rigid body; WORLD_MOMENT_SCALE 1e6 there).
        /// </summary>
        void Chopper(float dt, Vector3 desiredVel, Vector3 facing)
        {
            var p = _rb.position - _lastBodyOffset;
            _lastBodyOffset = _rot * new Vector3(0f, -_dimY, 0f);
            p += _lastBodyOffset;

            float desiredSpeed = desiredVel.magnitude;
            if (desiredSpeed > _specs.MaxSpeedMps) desiredVel *= _specs.MaxSpeedMps / desiredSpeed;
            float speed = _vel.magnitude;
            float maxAccel = Mathf.Min(speed * 0.6f + Min_Chopper_Accel, Max_Chopper_Accel);
            if (_maxDecel) maxAccel = Max_Chopper_Accel;
            var deltaVel = desiredVel - _vel;
            float fAccel = deltaVel.magnitude * Chopper_Ratio;
            var accel = fAccel > 1f && (fAccel > maxAccel || _maxDecel) ? deltaVel * (maxAccel / fAccel) : deltaVel * Chopper_Ratio;
            // ResolveForce(accel·m − m·GRAVITY·up) + the rigid body's own m·GRAVITY: the net acceleration is accel
            _vel += accel * dt;

            var working = Vector3.zero;
            if (dt > 0.005f) working = PitchAndRoll((_vel - _prevVel) / dt);
            var idealF = facing; idealF.y = 0f;
            idealF = idealF.sqrMagnitude > 1e-12f ? idealF.normalized : Vector3.zero;
            var actualF = _rot * Vector3.forward; actualF.y = 0f;
            var torque = Vector3.Cross(idealF, actualF);
            var ang = working;
            ang.y = Mathf.Clamp(-torque.y * 8f, -1.3f, 1.3f);
            ang = (ang + _lastAngVel * 7f) * 0.125f;
            _angVel = ang;
            _lastAngVel = ang;

            // RigidBody::DoIntegration: x += v·dt; q += ½(ω·dt)q (the ±30 rad/s bound never binds here)
            var np = p + _vel * dt;
            float w = _angVel.magnitude;
            if (w > 1e-6f) _rot = Quaternion.AngleAxis(w * dt * Mathf.Rad2Deg, _angVel / w) * _rot;
            _rot = Quaternion.Normalize(_rot);
            float minY = Ground(np) + 1.5f + _modelLift;
            if (np.y < minY && _mode != Mode.SkidStrike) { np.y = minY; if (_vel.y < 0) _vel.y = 0; }
            _prevVel = _vel;
            _rb.MovePosition(np);
            _rb.MoveRotation(_rot);
        }

        /// <summary>
        /// SimpleChopper::SetTorqueToMatchPitchAndRoll [C, 0x6A2030]: the local acceleration is (4·mLastAccelVector + a)·0.2
        /// (mLastAccelVector is cleared in the ctor and never written, so 0.2·a); pitch = −a.z·9/80·PITCH_ANG (+0.035·a.y
        /// when climbing above 15 m/s), roll = −a.x·12/80·ROLL_ANG, both clamped ±0.1 turn; the ideal forward/right get
        /// y = Sina(angle) (sine of turns) and the local x/z rate is −(ideal × actual)·PITCH/ROLL_ALIGN_SCALE, clamped ±1.
        /// Returns the world angular velocity with no yaw part.
        /// </summary>
        Vector3 PitchAndRoll(Vector3 worldAccel)
        {
            var inv = Quaternion.Inverse(_rot);
            var a = (inv * worldAccel) * 0.2f;
            float pitch = -(a.z * (9f / Max_Chopper_Accel)) * _specs.PitchAng;
            if (a.y > 0f && _vel.magnitude > 15f && _vel.y > 0f) pitch += a.y * 0.035f;
            pitch = Mathf.Clamp(pitch, -0.1f, 0.1f);
            var fwd = _rot * Vector3.forward;
            var idealF = fwd; idealF.y = Mathf.Sin(pitch * 2f * Mathf.PI);
            var t = inv * Vector3.Cross(idealF.normalized, fwd);
            var ang = Vector3.zero;
            ang.x = Mathf.Clamp(-t.x * _specs.PitchAlignScale, -1f, 1f);
            float roll = Mathf.Clamp(-(a.x * (12f / Max_Chopper_Accel)) * _specs.RollAng, -0.1f, 0.1f);
            var right = _rot * Vector3.right;
            var idealR = right; idealR.y = Mathf.Sin(roll * 2f * Mathf.PI);
            t = inv * Vector3.Cross(idealR.normalized, right);
            ang.z = Mathf.Clamp(-t.z * _specs.RollAlignScale, -1f, 1f);
            return _rot * ang;
        }

        // ---- DrawHeli wash (Physics/Behaviors/DrawHeli.cpp, OnTask 0x693990) ----

        const float Tweak_WashAltMin = 5f, Tweak_WashAltMax = 30f;

        /// <summary>
        /// DrawHeli::OnTask [C, 0x693990] (the "HeliWash" task, once per second): on a valid WWorldPos face the "heliwash"
        /// effect sits on the ground under the body with magnitude (1 − Ramp(altitude, 5, 30))·groundNormal, paused above
        /// 30 m or off the faces; the WorldEffectConn runs it every frame (HeliWash).
        /// </summary>
        void Wash(float dt)
        {
            if ((_washTimer -= dt) <= 0f)
            {
                _washTimer += 1f;
                if (_washTimer <= 0f) _washTimer = 1f;
                if (_wpos == null) _wpos = new Dynamics.WorldQueries.Pos();
                var pos = _rb.position;
                _wpos.Update(pos, out var normal, out _);
                if (_wpos.OnValidFace)
                {
                    float groundY = _wpos.HeightAtPoint(pos);
                    float altitude = pos.y - groundY;
                    float intensity = 1f - Mathf.Clamp01((altitude - Tweak_WashAltMin) / (Tweak_WashAltMax - Tweak_WashAltMin));
                    if (intensity > 0f) _wash.Set(new Vector3(pos.x, groundY, pos.z), normal * intensity);
                    else _wash.Pause();
                }
                else _wash.Pause();
            }
            _wash.Update(dt, _vel);
        }

        void OnDisable() => _wash.Pause();
        void OnDestroy() => _wash.Release();

        /// <summary>WorldEffectConn::Update (WorldConn.cpp:292-360) for the wash: intensity = |magnitude| through the
        /// effect's EmitterQuadratic, the group turned from up onto the normal, culled past VisualCullDist.</summary>
        sealed class HeliWash
        {
            static readonly uint Key = AttribDatabase.Hash("heliwash");
            Effects.FxGroup _group;
            Vector3 _pos, _mag;
            bool _paused = true;

            public void Set(Vector3 pos, Vector3 mag) { _pos = pos; _mag = mag; _paused = false; }
            public void Pause() { _paused = true; _group?.SetEnabled(false); }
            public void Release() { _group?.Release(); _group = null; }

            public void Update(float dt, Vector3 ownerVel)
            {
                if (_paused || !Effects.EmitterSystem.Enabled) return;
                var e = Effects.EmitterSystem.Db?.Effect(Key);
                if (e == null || e.GroupKey == 0) return;
                if (_group != null && _group.Dead) _group = null;
                if (_group == null) _group = Effects.EmitterSystem.Instance.Create(e.GroupKey, Effects.EmitterSystem.WorldEffect);
                if (_group == null) return;
                float k = e.Intensity(_mag.magnitude);
                var gp = Effects.FxFrame.G(_pos);
                var cam = Camera.main;
                float dist = cam ? (Effects.FxFrame.G(cam.transform.position) - gp).magnitude : 0f;
                if (k > 0f && dist < e.VisualCullDist)
                {
                    _group.SetEnabled(true);
                    _group.Frame = Effects.FxFrame.Delta(Vector3.up, Effects.FxFrame.G(_mag.normalized), gp);
                    _group.InheritVelocity = e.InheritVelocity > 0f ? Effects.FxFrame.G(ownerVel) * e.InheritVelocity : Vector3.zero;
                    _group.Intensity = k;
                    _group.Update(dt);
                }
                else _group.SetEnabled(false);
            }
        }

        /// <summary>FilterHeliAltitude (AIVehicleHelicopter.cpp:237): a point below the sheet goes up to it; false off the sheet.</summary>
        bool FilterHeliAltitude(ref Vector3 point)
        {
            if (!_sheet.GetElevation(point, out float e, out _)) return false;
            if (point.y < e) point.y = e;
            return true;
        }

        /// <summary>
        /// CheckHeliSheet (AIVehicleHelicopter.cpp:256-344): the destination is filtered up to the sheet; a look-ahead
        /// (0.65 s) under the sheet gives a smoothing velocity up its slope plus 0.4 × the sheet normal's side component
        /// (× |cross|); a working position (0.45 s) under the sheet lifts the destination to the same height and turns the
        /// body's velocity towards it, keeping its speed, oozed round by 0.3 × the side normal.
        /// </summary>
        void CheckHeliSheet(Vector3 myPos, Vector3 lookAhead, Vector3 working, ref Vector3 dest, ref Vector3 smoothing)
        {
            FilterHeliAltitude(ref dest);
            if (_sheet3.GetElevation(lookAhead, out float e3, out var n3) && e3 > lookAhead.y)
            {
                lookAhead.y = e3;
                var me2look = (lookAhead - myPos).normalized;
                var side = new Vector3(n3.x, 0f, n3.z);
                float cross = Mathf.Abs(side.x * me2look.z - side.z * me2look.x);
                smoothing = me2look + side * (OozeFactorSM * cross);
            }
            if (_sheet2.GetElevation(working, out float e2, out var n2) && e2 > working.y)
            {
                var adjusted = working;
                adjusted.y = e2;
                var me2adjusted = adjusted - myPos;
                float lenAdjusted = me2adjusted.magnitude, lenDest = (dest - myPos).magnitude;
                if (lenAdjusted > 0.2f) adjusted = myPos + me2adjusted * (lenDest / lenAdjusted);
                if (adjusted.y > dest.y) dest.y = adjusted.y;
                me2adjusted = me2adjusted.sqrMagnitude > 1e-8f ? me2adjusted.normalized : Vector3.zero;
                var side = new Vector3(n2.x, 0f, n2.z);
                float cross = Mathf.Abs(side.x * me2adjusted.z - side.z * me2adjusted.x);
                me2adjusted += side * (OozeFactor * cross);
                _vel = me2adjusted * _vel.magnitude;   // IRigidBody::SetLinearVelocity(me2adjusted · GetSpeed())
            }
        }

        /// <summary>
        /// AvoidCamera (AIVehicleHelicopter.cpp:363-411): a 7 m sphere (CameraRadiusToAvoid) 4 m in front of view 1's camera
        /// (rad − 0.5 when the heli is already inside it, over 2 m); when the 0.2 s lead path (CameraAvoidLeadTime) enters it,
        /// the destination is the lead point pushed out to the sphere's tangent plane and the velocity gets
        /// |v·n|·((7 − r)/2 + 1.2) along the normal (KeepOutScale).
        /// </summary>
        void AvoidCamera(ref Vector3 dest)
        {
            const float CameraRadiusToAvoid = 7f, CameraAvoidLeadTime = 0.2f, KeepOutScale = 1.2f;
            var cam = Camera.main;
            if (!cam) return;
            var my = _rb.position;
            var work = my + _vel * CameraAvoidLeadTime;
            var c = cam.transform.position + cam.transform.forward * 4f;
            float rad = Vector3.Distance(my, c);
            float camRad = CameraRadiusToAvoid;
            if (rad < CameraRadiusToAvoid && rad > 2f) camRad = rad - 0.5f;
            if (!AiMath.SegmentSphereIntersect(my, work, c, camRad, out var hit)) return;
            var normal = (hit - c).normalized;   // UMath::Direction(intersect, camera)
            float dot = Vector3.Dot(hit - work, normal);
            dest = work + normal * dot;
            float push = (CameraRadiusToAvoid - camRad) * 0.5f + KeepOutScale;
            _vel += normal * (Mathf.Abs(Vector3.Dot(_vel, normal)) * push);
        }

        static float Ground(Vector3 p)
        {
            return CopCar.WorldRay(new Vector3(p.x, p.y + 300f, p.z), Vector3.down, 600f, out var hit) ? hit.point.y : p.y - 20f;
        }

        void OnCollisionEnter(Collision c)
        {
            if (_skidTimer <= 0) return;
            var car = c.rigidbody ? c.rigidbody.GetComponent<PlayerCar>() : null;
            _collisionAbort += car && Police && car == Police.Player ? 5 : 2;
            if (_collisionAbort > 20) _skidTimer = 0;
        }
    }

    /// <summary>
    /// Attrib::Gen::chopperspecs (class 0x5D898EE7; Generated/AttribSys/Classes/chopperspecs.h) as SimpleChopper reads it
    /// through BehaviorSpecsPtr: the pvehicle's chopperspecs RefSpec, or "default" when that collection does not exist
    /// (AttributeStructPtr: Change(key_default)). copheli names EEC2271A, which is not in attributes.bin, so the heli flies
    /// on chopperspecs/default: PITCH_ANG 0.05, PITCH_ALIGN_SCALE 6, ROLL_ANG 0.06, ROLL_ALIGN_SCALE 4.5, MAX_SPEED_MPS 100
    /// (harness `phys9 chopper`). The other fields (yaw/strafe/air resistance) are not read by SimpleChopper.
    /// </summary>
    public sealed class ChopperSpecs
    {
        public float PitchAng = 0.05f, PitchAlignScale = 6f, RollAng = 0.06f, RollAlignScale = 4.5f, MaxSpeedMps = 100f;

        public static ChopperSpecs ForVehicle(string pvehicle)
        {
            var s = new ChopperSpecs();
            try
            {
                var db = GameData.Attributes;
                if (db == null) return s;
                uint cls = AttribDatabase.Hash("chopperspecs");
                uint key = AttribDatabase.Hash("default");
                var pv = db.Get("pvehicle", pvehicle);
                if (pv != null && pv.TryGet("chopperspecs", out int at, out _, out _) && Chunks.U32(pv.Source.Data, at) == cls)
                {
                    uint k = Chunks.U32(pv.Source.Data, at + 4);
                    if (db.Collections.ContainsKey((cls, k))) key = k;
                }
                if (!db.Collections.TryGetValue((cls, key), out var c)) return s;
                s.PitchAng = c.Float("PITCH_ANG", 0, s.PitchAng);
                s.PitchAlignScale = c.Float("PITCH_ALIGN_SCALE", 0, s.PitchAlignScale);
                s.RollAng = c.Float("ROLL_ANG", 0, s.RollAng);
                s.RollAlignScale = c.Float("ROLL_ALIGN_SCALE", 0, s.RollAlignScale);
                s.MaxSpeedMps = c.Float("MAX_SPEED_MPS", 0, s.MaxSpeedMps);
            }
            catch (System.Exception e) { Debug.LogWarning("NFSMW heli: chopperspecs: " + e.Message); }
            return s;
        }
    }
}
