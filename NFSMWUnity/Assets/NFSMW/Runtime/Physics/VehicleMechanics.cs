using System.Collections.Generic;
using NfsMw.Core;

namespace NfsMw.Dynamics
{
    /// <summary>
    /// The PVehicle mechanic table (Physics/Common/PVehicle.cpp; specs/physics.md §21, §27): which behaviour runs each
    /// mechanic of a vehicle. Mechanic keys (static init 0x887950..0x887BB7): RIGIDBODY, INPUT, SUSPENSION, ENGINE,
    /// DAMAGE, DRAW, AUDIO, AI, EFFECTS, RESET. <see cref="Lookup"/> is PVehicle::LookupBehaviorSignature (0x67F0D0, read
    /// from the exe):
    /// <list type="number">
    /// <item>AUDIO while the sound flag DAT_008f86f8 is 0 → none (it is 1 in the shipped exe).</item>
    /// <item>A per-vehicle override (the map at PVehicle +0x168; set by PVehicle::SetBehaviorOverride [?]) wins.</item>
    /// <item>Animating (PVehicle +0x15C, the NIS) and CLASS ≠ CHOPPER: SUSPENSION → SuspensionSpline, ENGINE →
    /// EngineSpline; then (any class) RESET → none, INPUT → InputNIS.</item>
    /// <item>RIGIDBODY with driver REMOTE (6) → RBRemote.</item>
    /// <item>INPUT with driver HUMAN (0) → InputPlayerDrag for STYLE_DRAG, else InputPlayer.</item>
    /// <item>DAMAGE with STYLE_DRAG → DamageDragster; ENGINE, CLASS CAR and STYLE_DRAG → EngineDragster.</item>
    /// <item>SUSPENSION, CLASS CAR and driver RACER, NONE or REMOTE (3, 4, 6) → SuspensionSimple.</item>
    /// <item>AI: the table at 0x8F8890 (filled by 0x887610): first {driver, class (null = any)} match — NONE and 5 →
    /// AIVehicleEmpty, RACER → AIVehicleRacecar, TRAFFIC → AIVehicleTraffic, COP + CHOPPER → AIVehicleHelicopter, COP →
    /// AIVehicleCopCar, HUMAN and REMOTE → AIVehicleHuman; no match → AIVehicleEmpty (the first entry's signature).</item>
    /// <item>EFFECTS for a human driver or a vehicle whose vfunc +0x24 says so (IsPlayer [?]) → EffectsPlayer.</item>
    /// <item>Otherwise the pvehicle's BEHAVIOR_MECHANIC_&lt;mechanic&gt; StringKey (inherited), else none.</item>
    /// </list>
    /// The data (harness `phys9 mech`): racers SuspensionRacer + EngineRacer + RBVehicle + DamageRacer + ResetCar +
    /// DrawRaceCar + SoundRacer; cops RBCop + SuspensionSimple + EngineRacer + DamageCopCar + DrawCopCar + SoundCop; traffic
    /// RBVehicle + SuspensionTraffic + EngineTraffic + DamageVehicle; semis RBTractor; trailers RBTrailer +
    /// SuspensionTrailer (no engine); copheli RBVehicle + SimpleChopper (ENGINE) + DamageHeli + DrawHeli + SoundHeli.
    /// Port: the Unity sims are picked from this lookup (VehicleSpecs for TrafficVehicle, PlayerCar.Behaviors for the
    /// racer sim / NisCarControl); behaviours Unity has no class for (RBRemote, InputPlayerDrag) are only named.
    /// </summary>
    public static class VehicleMechanics
    {
        public const string RigidBody = "RIGIDBODY", Input = "INPUT", Suspension = "SUSPENSION", Engine = "ENGINE",
            Damage = "DAMAGE", Draw = "DRAW", Audio = "AUDIO", Ai = "AI", Effects = "EFFECTS", Reset = "RESET";

        public static readonly string[] All = { RigidBody, Input, Suspension, Engine, Damage, Draw, Audio, Ai, Effects, Reset };

        /// <summary>DriverClass (Sim/SimTypes.h:218-226).</summary>
        public const int Human = 0, Traffic = 1, Cop = 2, Racer = 3, None = 4, Nis = 5, Remote = 6;
        /// <summary>DriverStyle: STYLE_DRAG.</summary>
        public const int StyleDrag = 1;

        /// <summary>The AI table at 0x8F8890 {driver class, vehicle class or null, signature}.</summary>
        static readonly (int driver, string cls, string sig)[] AiTable =
        {
            (None, null, "AIVehicleEmpty"), (5, null, "AIVehicleEmpty"), (Racer, null, "AIVehicleRacecar"),
            (Traffic, null, "AIVehicleTraffic"), (Cop, "CHOPPER", "AIVehicleHelicopter"), (Cop, null, "AIVehicleCopCar"),
            (Human, null, "AIVehicleHuman"), (Remote, null, "AIVehicleHuman"),
        };

        /// <summary>The state LookupBehaviorSignature reads from the PVehicle.</summary>
        public struct State
        {
            public int DriverClass, DriverStyle;
            /// <summary>PVehicle +0x118: the pvehicle CLASS (CAR, TRACTOR, TRAILER, CHOPPER, ...).</summary>
            public string VehicleClass;
            public bool Animating, IsPlayer;
            /// <summary>PVehicle +0x168: mechanic → behaviour overrides [?: none are set by the shipped game's code paths
            /// the port runs].</summary>
            public Dictionary<string, string> Overrides;
        }

        /// <summary>PVehicle::LookupBehaviorSignature (0x67F0D0): the behaviour name for a mechanic, or null for none.</summary>
        public static string Lookup(AttribCollection pvehicle, string mechanic, in State s)
        {
            if (s.Overrides != null && s.Overrides.TryGetValue(mechanic, out var ov)) return ov;
            string cls = s.VehicleClass ?? "CAR";
            if (s.Animating)
            {
                if (cls != "CHOPPER")
                {
                    if (mechanic == Suspension) return "SuspensionSpline";
                    if (mechanic == Engine) return "EngineSpline";
                }
                if (mechanic == Reset) return null;
                if (mechanic == Input) return "InputNIS";
            }
            if (mechanic == RigidBody && s.DriverClass == Remote) return "RBRemote";
            if (mechanic == Input && s.DriverClass == Human) return s.DriverStyle == StyleDrag ? "InputPlayerDrag" : "InputPlayer";
            if (mechanic == Damage && s.DriverStyle == StyleDrag) return "DamageDragster";
            if (mechanic == Engine && cls == "CAR" && s.DriverStyle == StyleDrag) return "EngineDragster";
            if (mechanic == Suspension && cls == "CAR" && (s.DriverClass == Racer || s.DriverClass == None || s.DriverClass == Remote))
                return "SuspensionSimple";
            if (mechanic == Ai)
            {
                foreach (var e in AiTable)
                    if (e.driver == s.DriverClass && (e.cls == null || e.cls == cls)) return e.sig;
                return AiTable[0].sig;
            }
            if (mechanic == Effects && (s.DriverClass == Human || s.IsPlayer)) return "EffectsPlayer";
            return Attribute(pvehicle, mechanic);
        }

        /// <summary>The pvehicle's own BEHAVIOR_MECHANIC_&lt;mechanic&gt; (an empty StringKey = none).</summary>
        public static string Attribute(AttribCollection pvehicle, string mechanic)
        {
            if (pvehicle == null) return null;
            string v = pvehicle.StringKey("BEHAVIOR_MECHANIC_" + mechanic);
            return string.IsNullOrEmpty(v) ? null : v;
        }

        /// <summary>Every mechanic's behaviour for a vehicle (PVehicle::LoadBehaviors 0x685240 builds one per non-null).</summary>
        public static Dictionary<string, string> LoadAll(string pvehicle, in State s)
        {
            var map = new Dictionary<string, string>();
            var pv = GameData.Attributes?.Get("pvehicle", (pvehicle ?? "").ToLowerInvariant());
            foreach (var m in All)
            {
                var sig = Lookup(pv, m, s);
                if (sig != null) map[m] = sig;
            }
            return map;
        }
    }
}
