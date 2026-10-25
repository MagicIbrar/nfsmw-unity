using System.Collections.Generic;
using NfsMw.Dynamics;
using UnityEngine;

namespace NfsMw
{
    /// <summary>
    /// The PVehicle side of a car (Physics/Common/PVehicle.cpp, exe 0x6681D0-0x68A0F5; the decomp file is empty, so this
    /// is from the exe: specs/physics.md §21). PlayerCar is the PVehicle plus its racer behaviours; this part holds the
    /// vehicle-level state the behaviours and the AI share.
    /// <list type="bullet">
    /// <item>Physics modes / physics LOD (SetPhysicsMode 0x688390 → leave 0x6865D0, enter 0x6864A0): SIMULATED runs every
    /// behaviour; EMULATED (AIVehicle::EnableSimplePhysics) pauses DRAW, SUSPENSION, ENGINE, RIGIDBODY, EFFECTS, RESET,
    /// AUDIO and DAMAGE (only INPUT and AI keep running) and turns collision modelling off
    /// (ICollisionBody::DisableModeling), so the AI moves the body itself (AIVehicle::UpdateSimplePhysics); INACTIVE
    /// stops triggering.</item>
    /// <item>IsOffWorld (0x680840, every tick while not INACTIVE): with the suspension paused, off world = no ground face
    /// under or above the body (GetWorldHeightAtPointRigorous); otherwise on world when the body's own WWorldPos has a
    /// surface, else off world when two or more wheels have none (SimSurface::kNull). Racers and cops switch to the
    /// simple physics while off world (AIVehicleRacecar::ShouldDoSimplePhysics, AIVehicleCopCar::Update): that is the
    /// physics LOD — a car on track sections whose collision is not loaded.</item>
    /// <item>Offscreen / on-screen time (OnTaskSimulate 0x686060): while the render behaviour exists and the car is
    /// renderable, out of view → offscreen += dt and on-screen = 0, in view → offscreen = 0 and on-screen += dt.</item>
    /// <item>SetSpeed (0x670210): velocity = forward·speed, then EngineRacer::MatchSpeed (gear guessed from the shift
    /// points, rpm from the wheel speed, clutch engaged) and SuspensionRacer::MatchSpeed (wheels roll with the road,
    /// no lock, drift and burnout reset).</item>
    /// <item>SetVehicleOnGround (0x685810): the car is placed on the ground face under the reset point (searched from
    /// dimension.y above it), each wheel on its own face (WWorldPos 0.05), the body turned to the plane through the four
    /// wheel contacts, velocities zeroed, suspension/engine/AI/reset behaviours reset and SetSpeed(0). Returns false
    /// (and the car is left at the reset point, off world) when there is no ground there.</item>
    /// </list>
    /// </summary>
    public sealed partial class PlayerCar
    {
        /// <summary>PhysicsMode (Sim/SimTypes.h:233-237).</summary>
        public enum PhysicsModes { Inactive = 0, Simulated = 1, Emulated = 2 }

        public PhysicsModes PhysicsMode { get; private set; } = PhysicsModes.Simulated;
        /// <summary>IVehicle::IsOffWorld (PVehicle +0x188): no world collision under the car (see the class notes).</summary>
        public bool IsOffWorld { get; private set; }
        /// <summary>IVehicle::GetOffscreenTime / GetOnScreenTime (s).</summary>
        public float OffscreenTime { get; private set; }
        public float OnScreenTime { get; private set; }
        /// <summary>The body's WWorldPos (ISimable::GetWPos): the world face under the body origin.</summary>
        public WorldQueries.Pos WPos => _wpos ?? (_wpos = new WorldQueries.Pos(0.025f));

        WorldQueries.Pos _wpos;

        /// <summary>RBVehicle invulnerability (CarContacts): 1 manual reset, 2 reset, 3 control switch, 4 physics switch;
        /// DrawCar::OnService makes the car flash for 1 and 2 (pkt.SetFlashing). [hook for the car renderer]</summary>
        public int Invulnerability => CarContacts.GetInvulnerability(this);
        Renderer[] _renderers;
        bool _savedGravity = true, _savedDetect = true;

        /// <summary>
        /// IVehicle::SetPhysicsMode (0x688390). EMULATED: the sim stops (Step does nothing but the off-world test), the body
        /// stops colliding and falling (DisableModeling) and keeps the velocity the AI gives it; back to SIMULATED restores
        /// both. [hook for the AI: AIVehicleRacecar/AIVehicleCopCar switch on IsOffWorld]
        /// </summary>
        public void SetPhysicsMode(PhysicsModes mode)
        {
            if (mode == PhysicsMode) return;
            var rb = _rb ? _rb : GetComponent<Rigidbody>();
            // leave (0x6865D0)
            if (PhysicsMode == PhysicsModes.Simulated && rb)
            {
                _savedGravity = rb.useGravity;
                _savedDetect = rb.detectCollisions;
                rb.useGravity = false;
                rb.detectCollisions = false;
            }
            var old = PhysicsMode;
            PhysicsMode = mode;
            // enter (0x6864A0)
            if (mode == PhysicsModes.Simulated && rb)
            {
                rb.useGravity = _savedGravity;
                rb.detectCollisions = _savedDetect;
                // AIVehicle::DisableSimplePhysics: 1 s of passing through other cars after the switch back
                if (old == PhysicsModes.Emulated) CarContacts.SetInvulnerability(this, CarContacts.InvulnerablePhysicsSwitch, 1f);
            }
        }

        /// <summary>
        /// PVehicle::OnTaskSimulate's vehicle part (0x686060) for a car whose racer sim does not run (traffic and cops run
        /// on TrafficVehicle with this component disabled): the screen timers and the off-world test.
        /// [hook for TrafficVehicle: call once per sim step]
        /// </summary>
        public void TickPVehicle(float dt)
        {
            CarContacts.TickInvulnerability(this, dt);
            UpdateScreenTimes(dt);
            if (PhysicsMode != PhysicsModes.Inactive) UpdateOffWorld();
        }

        // ---- the mechanic table (PVehicle::LookupBehaviorSignature 0x67F0D0; Dynamics.VehicleMechanics) -----------------

        Core.AttribCollection _pvehicle;
        bool _pvehicleRead;

        /// <summary>IVehicle::GetDriverClass (Sim/SimTypes.h): 0 human, 1 traffic, 2 cop, 3 racer — from the components
        /// the game code attaches (the same rule CarContacts resolves).</summary>
        public int DriverClass => Traffic ? VehicleMechanics.Traffic : GetComponent<Game.Police.CopCar>() ? VehicleMechanics.Cop
            : AiControlled ? VehicleMechanics.Racer : VehicleMechanics.Human;

        /// <summary>The state LookupBehaviorSignature reads: driver class and style, the pvehicle CLASS, animating (NIS).</summary>
        public VehicleMechanics.State MechanicState
        {
            get
            {
                var pv = PVehicleAttributes;
                var nis = GetComponent<Game.NisCarControl>();
                return new VehicleMechanics.State
                {
                    DriverClass = DriverClass, DriverStyle = Dragster ? VehicleMechanics.StyleDrag : 0,
                    VehicleClass = pv?.StringKey("CLASS") ?? "CAR", Animating = nis && nis.Animating, IsPlayer = !AiControlled && !Traffic,
                };
            }
        }

        Core.AttribCollection PVehicleAttributes
        {
            get
            {
                if (!_pvehicleRead && Spec != null)
                {
                    _pvehicleRead = true;
                    _pvehicle = GameData.Attributes?.Get("pvehicle", (Spec.Name ?? "").ToLowerInvariant());
                }
                return _pvehicle;
            }
        }

        /// <summary>The behaviour running a mechanic for this car now (null = none), e.g. "SuspensionRacer", "ResetCar".</summary>
        public string BehaviorSignature(string mechanic) => VehicleMechanics.Lookup(PVehicleAttributes, mechanic, MechanicState);

        /// <summary>Does the car have a behaviour for this mechanic (PVehicle::LoadBehaviors makes one per non-null signature)?
        /// E.g. RESET: racers (ResetCar) yes; cops, traffic, trailers and an animating car no.</summary>
        public bool HasBehavior(string mechanic) => BehaviorSignature(mechanic) != null;

        /// <summary>
        /// IRigidBody::GetTriggerFlags = RBVehicle::GetTriggerFlags (0x69AFD0): 0x64 for the player's car, 0x68 for any other
        /// vehicle, | 0x20000 for a cop; 0 while the body sleeps (RigidBody 0x699F60; the racer sim never sleeps on its
        /// wheels, an external sim's body sleeps through Rigidbody.Sleep). WTriggerManager tests it against fFlags &gt;&gt; 8
        /// (WTrigger.Activates). [hook for Game/Triggers.cs: WTriggerBody.TriggerFlags]
        /// </summary>
        public uint TriggerFlags
        {
            get
            {
                var rb = _rb ? _rb : GetComponent<Rigidbody>();
                bool asleep = !enabled && rb && rb.IsSleeping();
                return Core.WTrigger.VehicleTriggerFlags(!AiControlled && !Traffic, DriverClass == VehicleMechanics.Cop, asleep);
            }
        }

        /// <summary>ISuspension::GetToleratedSlip = Chassis::ComputeMaxSlip (Chassis.cpp:70-76): Ramp(speed, 10, 71) + 0.5 m/s,
        /// 71 in reverse — the slip a tyre tolerates before the effects (DrawCar) and the audio (SoundCar) count it.</summary>
        public float ToleratedSlip => Gear < 0 ? 71f : Ratio01(SpeedMps, 10f, 71f) + 0.5f;

        /// <summary>
        /// SoundCar::OnService's wheel slip (SoundCar.cpp:162-172, Tweak_AudioSlipToleranceScale 0.2): the longitudinal slip
        /// moved toward 0 by 0.2 × the tolerated slip (0 inside that band); the skid sent with it is −GetWheelSkid (the
        /// lateral speed at the contact). [hook for the audio code: SetWheelSlip(idx, slip, skid)]
        /// </summary>
        public (float slip, float skid) SoundWheelSlip(int i)
        {
            var w = WheelAudio(i);
            float tol = ToleratedSlip * 0.2f;
            float s = w.slip > tol ? w.slip - tol : w.slip < -tol ? w.slip + tol : 0f;
            float lat = w.contact ? w.vx * Mathf.Tan(Mathf.Clamp(w.alphaDeg, -80f, 80f) * Mathf.Deg2Rad) : 0f;
            return (s, -lat);
        }

        /// <summary>
        /// ITransmission::IsShiftingGear (EngineRacer.cpp:188-190: mGearShiftTimer &gt; 0, the timer a gear change sets to
        /// SHIFT_SPEED·ratio, ×0.25 for a downshift; it also forces the smoothed rpm path, :1312). The racer sim's
        /// _shiftTimer; for a car on an external sim (TrafficVehicle) its EngineRacer / EngineTraffic shift timer
        /// (EngineTraffic.IsShifting, same rule). Read by the audio (EAX_CarState::IsShifting → SFXCTL_HybridMotor).
        /// </summary>
        public bool IsShifting
        {
            get
            {
                if (enabled) return _shiftTimer > 0f;
                var tv = GetComponent<Game.TrafficPhysics.TrafficVehicle>();
                if (!tv) return false;
                return tv.Racer != null ? tv.Racer.IsShifting : tv.Engine != null && tv.Engine.IsShifting;
            }
        }

        void UpdateScreenTimes(float dt)
        {
            if (_renderers == null || _renderers.Length == 0) _renderers = GetComponentsInChildren<Renderer>();
            bool any = false, visible = false;
            foreach (var r in _renderers)
            {
                if (!r || !r.enabled || !r.gameObject.activeInHierarchy) continue;
                any = true;
                if (r.isVisible) { visible = true; break; }
            }
            if (!any) { OffscreenTime = 0f; OnScreenTime = 0f; }
            else if (!visible) { OffscreenTime += dt; OnScreenTime = 0f; }   // [ours] Renderer.isVisible stands in for DrawCar::InView
            else { OffscreenTime = 0f; OnScreenTime += dt; }
        }

        // ---- external sim read-outs (traffic, cops and AI racers on TrafficVehicle) --------------------------------

        /// <summary>
        /// [ours] One wheel of an external sim (TrafficVehicle's Chassis) into this car's read-outs, so WheelState,
        /// WheelAudio and the effects see it: contact, longitudinal slip ω·r − v (m/s), slip angle (rad), the wheel's
        /// road speed (m/s), load (N), angular speed (rad/s) and the simsurface under it. Call after SetExternalSimState.
        /// </summary>
        public void SetExternalWheelState(int i, bool contact, float slip, float slipAngleRad, float roadSpeed, float load, float omega, WorldCollision.Surface surface)
        {
            if (_w == null || i < 0 || i >= _w.Length || _w[i] == null) return;
            var w = _w[i];
            w.Contact = contact;
            w.SlipX = slip;
            w.AlphaDeg = slipAngleRad * Mathf.Rad2Deg;
            w.Vx = roadSpeed;
            w.Load = contact ? load : 0f;
            w.Omega = omega;
            w.Surface = contact ? surface : WorldCollision.Surface.Null;
            w.OnWorld = contact && surface.Valid;
            w.Locked = contact && Mathf.Abs(omega) < 1e-3f && Mathf.Abs(roadSpeed) > 2f;
            w.Traction = 1f;
        }

        /// <summary>[ours] The external engine's nitrous: NosFiring (flames, audio) and the tank 0..1.</summary>
        public void SetExternalNos(bool firing, float tank)
        {
            _nosOn = firing;
            Nos = Mathf.Clamp01(tank);
        }

        /// <summary>Is wheel i (0 FL, 1 FR, 2 RL, 3 RR) on a world face (its road surface is not SimSurface::kNull)?</summary>
        public bool WheelOnWorld(int i) => _w != null && i >= 0 && i < _w.Length && _w[i] != null && _w[i].OnWorld;

        /// <summary>
        /// ResetCar::ValidTerrain(checktires) (ResetCar.cpp:123-142): invalid when the body's WWorldPos surface is kNull
        /// and at least checktires wheels have a kNull road surface (an external sim feeds the wheels through
        /// SetExternalWheelState).
        /// </summary>
        public bool ValidTerrain(int checkTires)
        {
            if (!WPos.SurfaceIsNull && WPos.OnValidFace) return true;
            if (_w == null || _w[0] == null) return checkTires > 4;
            int invalid = 0;
            foreach (var w in _w) if (w != null && !w.OnWorld) invalid++;
            return invalid < checkTires;
        }

        /// <summary>0x680840 (see the class notes).</summary>
        void UpdateOffWorld()
        {
            var pos = transform.position;
            if (PhysicsMode != PhysicsModes.Simulated)
            {
                IsOffWorld = !WorldQueries.HeightRigorous(pos, out _, out _);
                return;
            }
            WPos.FindClosestFace(pos, true);
            if (!WPos.SurfaceIsNull && WPos.OnValidFace) { IsOffWorld = false; return; }
            if (_w == null || _w[0] == null) { IsOffWorld = true; return; }
            int none = 0;
            foreach (var w in _w) if (w != null && !w.OnWorld) none++;
            IsOffWorld = none >= 2;
        }

        // ---- SetSpeed (0x670210) ------------------------------------------------------------------------------------

        /// <summary>IVehicle::SetSpeed: forward velocity, the engine and the wheels matched to it.</summary>
        public void SetSpeed(float speed)
        {
            var rb = _rb ? _rb : GetComponent<Rigidbody>();
            if (!rb || Spec == null) return;
            if (!rb.isKinematic) rb.linearVelocity = transform.forward * speed;
            MatchEngineSpeed(speed);
            if (_w != null)
                foreach (var w in _w)
                {
                    if (w == null) continue;
                    // SuspensionRacer::Tire::MatchSpeed (SuspensionRacer.cpp:200-211)
                    w.Omega = speed / Mathf.Max(0.05f, w.Radius);
                    w.Traction = 1f;
                    w.Locked = false;
                    w.SlipX = 0f;
                    w.TauPrev = 0f;
                    w.PrevVx = speed;
                }
            _drift = 0f;
            _driftState = 0;
        }

        /// <summary>
        /// EngineRacer::MatchSpeed (EngineRacer.cpp:560-570): GuessGear (the highest gear whose rpm at this speed is over
        /// the previous gear's up-shift point; reverse below 0), GuessRPM (idle + wheel ω·ratio·(red − idle)/red, wheel
        /// radius = mean of the front and rear tyres), clutch engaged, shift timer 0.
        /// </summary>
        void MatchEngineSpeed(float speed)
        {
            var s = Spec;
            int top = Mathf.Max(1, s.ForwardGears);
            int gear;
            if (speed < 0f) gear = -1;
            else
            {
                gear = 1;
                for (int g = 1; g < top; g++)
                {
                    float shiftAt = _up != null && g < _up.Length ? _up[g] : s.RedLine;
                    if (GuessRpm(speed, g) <= shiftAt) break;
                    gear = g + 1;
                }
            }
            Gear = gear;
            Rpm = GuessRpm(speed, gear);
            _we = Rpm * RPM2RAD;
            _shiftTimer = 0f;
            _clutch = 0;
        }

        float GuessRpm(float speed, int gear)
        {
            var s = Spec;
            float r = (s.TireRadiusFront + s.TireRadiusRear) * 0.5f;
            if (r <= 0f) return s.IdleRpm;
            float dw = Mathf.Abs(speed) / r, wMax = s.RedLine * RPM2RAD, wMin = s.IdleRpm * RPM2RAD;
            float ratio = s.FinalGear * GearRatio(gear);
            return Mathf.Clamp((wMin + dw * ratio * (wMax - wMin) / wMax) * RAD2RPM, s.IdleRpm, s.RedLine);
        }

        // ---- SetVehicleOnGround (0x685810) ---------------------------------------------------------------------------

        static readonly WorldQueries.Pos WheelPos = new WorldQueries.Pos(0.05f);

        /// <summary>
        /// IVehicle::SetVehicleOnGround(resetPos, initialVec): see the class notes. The Unity car's design pose has the
        /// road at local y = 0 (RestLift), so the four wheel contacts define where that plane goes. [hook: the camera's
        /// "CameraMessagePort" jump message is the camera code's]
        /// </summary>
        public bool SetVehicleOnGround(Vector3 resetPos, Vector3 initialVec)
        {
            var rb = _rb ? _rb : GetComponent<Rigidbody>();
            if (!rb) return false;
            var fwd = new Vector3(initialVec.x, 0f, initialVec.z);
            if (fwd.sqrMagnitude < 1e-6f) fwd = transform.forward;
            var rot = Quaternion.LookRotation(fwd.normalized, Vector3.up);   // Util_GenerateMatrix(initialVec, null)
            float dimY = _box ? _box.size.y * 0.5f : 0.6f;
            var probe = resetPos + Vector3.up * dimY;
            bool found = WorldQueries.HeightRigorous(probe, out float h, out _);
            var pos = resetPos;
            if (found)
            {
                pos.y = h;
                if (_w != null && _w[0] != null)
                {
                    // each wheel's own face (0.25 of the way toward the hub sits the probe in the exe; the hub XZ here)
                    var contacts = new Vector3[4];
                    bool all = true;
                    for (int i = 0; i < 4; i++)
                    {
                        var hub = _w[i].Droop;
                        var p = new Vector3(pos.x, probe.y, pos.z) + rot * new Vector3(hub.x, 0f, hub.z);
                        WheelPos.Invalidate();
                        WheelPos.FindClosestFace(p, false);
                        if (!WheelPos.OnValidFace) { all = false; break; }
                        contacts[i] = new Vector3(p.x, WheelPos.HeightAtPoint(p), p.z);
                    }
                    if (all)
                    {
                        // the plane through the axle and side mid-points (front − rear, right − left), the heading kept
                        var front = (contacts[0] + contacts[1]) * 0.5f;
                        var rear = (contacts[2] + contacts[3]) * 0.5f;
                        var left = (contacts[0] + contacts[2]) * 0.5f;
                        var right = (contacts[1] + contacts[3]) * 0.5f;
                        var along = front - rear;
                        var across = right - left;
                        var up = Vector3.Cross(along, across).normalized;   // Unity: cross(fwd, right) = up
                        if (up.y > 0.3f)
                        {
                            var f2 = Vector3.ProjectOnPlane(fwd, up).normalized;
                            if (f2.sqrMagnitude > 0.5f) rot = Quaternion.LookRotation(f2, up);
                        }
                        // the local road plane (y = 0 at the hubs' XZ) through the contacts' centre
                        var centre = (contacts[0] + contacts[1] + contacts[2] + contacts[3]) * 0.25f;
                        var hubMean = Vector3.zero;
                        for (int i = 0; i < 4; i++) hubMean += new Vector3(_w[i].Droop.x, 0f, _w[i].Droop.z);
                        pos = centre - rot * (hubMean * 0.25f);
                    }
                }
            }
            Teleport(pos + rot * Vector3.up * RestLift, rot);
            SetSpeed(0f);
            ChaseCamera.CarPlaced(this);   // CameraMessagePort: the following camera snaps (camera agent)
            IsOffWorld = !found;
            return found;
        }
    }
}
