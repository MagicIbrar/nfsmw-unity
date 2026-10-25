using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game.TrafficPhysics
{
    /// <summary>
    /// What a pvehicle asks of the simulation (specs/traffic_physics.md §1): its behaviour per mechanic
    /// (pvehicle BEHAVIOR_MECHANIC_* StringKeys; every traffic car is RBVehicle + SuspensionTraffic + EngineTraffic +
    /// DamageVehicle, a semi is RBTractor with a Trailer RefSpec, a trailer RBTrailer + SuspensionTrailer, cops
    /// SuspensionSimple), the part specs (CarSpec), the collision geometry root (Physics/Bounds.h; RigidBody dimension =
    /// root half extents, body origin = root pivot in model space), damagespecs, and the aivehicle's
    /// PlayerCollisionsDefault collisionreactions (AIVehicle.cpp:700-721).
    /// </summary>
    public sealed class VehicleSpecs
    {
        public struct Reaction
        {
            public float Elasticity, RollHeight, WeightBias, MassScale, StunSpeed, StunTime;
        }

        public string Vehicle, Model, Class;
        public string RigidBody, Suspension, Engine, Damage, Reset;
        public CarSpec Spec;
        /// <summary>tires SECTION_WIDTH (mm): the wheel arms sit at TRACK_WIDTH − width (SuspensionTraffic.cpp:369).</summary>
        public float SectionWidthFront = 255f, SectionWidthRear = 255f;
        /// <summary>RigidBody dimension (half extents, m) and where the body origin sits in the model (CollisionGeometry root).</summary>
        public Vector3 Dimension = new Vector3(0.9f, 0.7f, 2.3f), Pivot = new Vector3(0, 0.7f, 0);
        public bool HasBounds;
        public CollisionGeometry.Collection Geometry;
        /// <summary>pvehicle Trailer RefSpec (the trailer's pvehicle name), or null.</summary>
        public string Trailer;
        // damagespecs (Generated/AttribSys/Classes/damagespecs.h)
        public float DamageForce, HitPoints, HpThreshold, SuppressDist, ShockTime, ShockForce;
        /// <summary>DamageScaleRecord {VisualScale, HitPointScale} per DamageZone (VehicleDamage.Zone order).</summary>
        public readonly Vector2[] Zones = new Vector2[10];
        /// <summary>FRONT, FRONTSIDE, REAR, REARSIDE (RBVehicle::ChooseReaction).</summary>
        public Reaction[] Reactions;
        public float WallUs = 0.2f, WallUk = 0.1f, ObjUs = 0.55f, ObjUk = 0.35f;
        public Vector3 WallE, ObjE;

        public bool IsTractor => Class == "TRACTOR" || RigidBody == "RBTractor";
        public bool IsTrailer => Class == "TRAILER" || RigidBody == "RBTrailer";

        static readonly Dictionary<string, VehicleSpecs> Cache = new Dictionary<string, VehicleSpecs>();
        static CollisionGeometry _geometry;
        static readonly string[] ZoneFields = { "DZ_FRONT", "DZ_REAR", "DZ_LEFT", "DZ_RIGHT", "DZ_LFRONT", "DZ_RFRONT", "DZ_LREAR", "DZ_RREAR", "DZ_TOP", "DZ_BOTTOM" };
        static readonly string[] ReactionFields = { "FRONT_REACTION", "FRONTSIDE_REACTION", "REAR_REACTION", "REARSIDE_REACTION" };

        /// <summary>The vehicles' collision geometry (GLOBAL/GLOBALB.BUN chunk 0x8003B900), loaded once.</summary>
        public static CollisionGeometry Geometries
        {
            get
            {
                if (_geometry != null) return _geometry;
                GameData.Mount();
                try { _geometry = CollisionGeometry.Read(GameFiles.ReadBundle(Path.Combine(GameData.Root, "GLOBAL", "GLOBALB.BUN"))); }
                catch (System.Exception e) { Debug.LogWarning("NFSMW traffic physics: no vehicle bounds: " + e.Message); _geometry = new CollisionGeometry(); }
                return _geometry;
            }
        }

        public static VehicleSpecs Get(string vehicle)
        {
            if (string.IsNullOrEmpty(vehicle)) return null;
            vehicle = vehicle.ToLowerInvariant();
            if (Cache.TryGetValue(vehicle, out var s)) return s;
            GameData.Mount();
            var db = GameData.Attributes;
            var pv = db?.Get("pvehicle", vehicle);
            if (pv == null) { Cache[vehicle] = null; return null; }
            s = new VehicleSpecs
            {
                Vehicle = vehicle,
                Model = pv.StringKey("MODEL") ?? vehicle.ToUpperInvariant(),
                Class = pv.StringKey("CLASS") ?? "CAR",
                RigidBody = pv.StringKey("BEHAVIOR_MECHANIC_RIGIDBODY") ?? "RBVehicle",
                Suspension = pv.StringKey("BEHAVIOR_MECHANIC_SUSPENSION") ?? "",
                Engine = pv.StringKey("BEHAVIOR_MECHANIC_ENGINE") ?? "",
                Damage = pv.StringKey("BEHAVIOR_MECHANIC_DAMAGE") ?? "",
                Reset = pv.StringKey("BEHAVIOR_MECHANIC_RESET") ?? "",
                Spec = CarSpec.FromAttributes(db, vehicle),
            };
            var tr = pv.Refs("Trailer");
            if (tr.Length > 0 && tr[0].key != 0 && db.Collections.TryGetValue(tr[0], out var trailer)) s.Trailer = trailer.Name;
            // tires SECTION_WIDTH of the stock tires ref
            foreach (var r in pv.Refs("tires"))
                if (db.Collections.TryGetValue(r, out var ti))
                {
                    var w = ti.Pair("SECTION_WIDTH", (255f, 255f));
                    s.SectionWidthFront = w.front;
                    s.SectionWidthRear = w.rear;
                    break;
                }
            // collision geometry: Lookup(UCrc32(MODEL)) = the StringKey's 32-bit Jenkins hash
            if (Geometries.ByName.TryGetValue(AttribDatabase.Hash(s.Model), out var col) && col.Bounds.Length > 0)
            {
                var root = col.Bounds[0];
                s.Geometry = col;
                s.Dimension = new Vector3(root.HalfDims.X, root.HalfDims.Y, root.HalfDims.Z);
                s.Pivot = new Vector3(root.Pivot.X, root.Pivot.Y, root.Pivot.Z);
                s.HasBounds = true;
            }
            else
            {
                // no geometry [ours]: a box from the chassis, its bottom on the road
                var c = s.Spec;
                s.Dimension = new Vector3(Mathf.Max(c.TrackFront, c.TrackRear) * 0.5f + 0.1f, 0.7f, c.WheelBase * 0.5f + 1f);
                s.Pivot = new Vector3(0, s.Dimension.y, 0);
            }
            foreach (var r in pv.Refs("damagespecs"))
                if (db.Collections.TryGetValue(r, out var dm))
                {
                    s.DamageForce = dm.Float("FORCE");
                    s.HitPoints = dm.Float("HIT_POINTS");
                    s.HpThreshold = dm.Float("HP_THRESHOLD");
                    s.SuppressDist = dm.Float("SUPPRESS_DIST");
                    s.ShockTime = dm.Float("SHOCK_TIME");
                    s.ShockForce = dm.Float("SHOCK_FORCE");
                    for (int i = 0; i < ZoneFields.Length; i++)
                    {
                        var z = dm.Pair(ZoneFields[i]);
                        s.Zones[i] = new Vector2(z.front, z.rear);
                    }
                    break;
                }
            ReadReactions(db, pv, s);
            var cs = s.Spec;
            s.WallUs = cs.WallFrictionStatic; s.WallUk = Mathf.Min(cs.WallFrictionStatic, cs.WallFrictionDynamic);
            s.ObjUs = cs.ObjFrictionStatic; s.ObjUk = cs.ObjFrictionDynamic;
            s.WallE = cs.WallElasticity; s.ObjE = cs.ObjElasticity;
            Cache[vehicle] = s;
            return s;
        }

        /// <summary>
        /// An AI racer's behaviours (specs/ai_racer.md §11): PVehicle::LookupBehaviorSignature (0x67F0D0, read from the exe)
        /// gives a CAR-class vehicle with driver RACER (3), NONE (4) or REMOTE (6) BEHAVIOR_MECHANIC_SUSPENSION =
        /// SuspensionSimple (0x67F322-0x67F35F) whatever the pvehicle names (SuspensionRacer on all 56 racers), and with
        /// driver style DRAG (+0x144 == 1) ENGINE = EngineDragster (0x67F2E7) and DAMAGE = DamageDragster (0x67F2B9). The
        /// part specs are the racer's own (performance upgrades included), so the cached pvehicle record is copied with
        /// that CarSpec [ours: a per-car copy]. Null without a pvehicle or bounds data we can run.
        /// </summary>
        public static VehicleSpecs ForRacer(string vehicle, CarSpec spec, bool drag)
        {
            var b = Get(vehicle);
            if (b == null || spec == null) return null;
            var s = (VehicleSpecs)b.MemberwiseClone();
            s.Spec = spec;
            // the mechanic table (Dynamics.VehicleMechanics = LookupBehaviorSignature 0x67F0D0) for a RACER driver
            var pv = GameData.Attributes?.Get("pvehicle", s.Vehicle);
            var st = new Dynamics.VehicleMechanics.State
            {
                DriverClass = Dynamics.VehicleMechanics.Racer, DriverStyle = drag ? Dynamics.VehicleMechanics.StyleDrag : 0, VehicleClass = s.Class ?? "CAR",
            };
            s.Suspension = Dynamics.VehicleMechanics.Lookup(pv, Dynamics.VehicleMechanics.Suspension, st) ?? s.Suspension;
            s.Engine = Dynamics.VehicleMechanics.Lookup(pv, Dynamics.VehicleMechanics.Engine, st) ?? "";
            s.Damage = Dynamics.VehicleMechanics.Lookup(pv, Dynamics.VehicleMechanics.Damage, st) ?? "";
            s.RigidBody = Dynamics.VehicleMechanics.Lookup(pv, Dynamics.VehicleMechanics.RigidBody, st) ?? s.RigidBody;
            // EngineDragster = EngineRacer + TrafficVehicle.DriverStyle (EngineRacer.Dragster) [ours: one class]
            if (s.Engine == "EngineDragster" || string.IsNullOrEmpty(s.Engine)) s.Engine = "EngineRacer";
            s.WallUs = spec.WallFrictionStatic; s.WallUk = Mathf.Min(spec.WallFrictionStatic, spec.WallFrictionDynamic);
            s.ObjUs = spec.ObjFrictionStatic; s.ObjUk = spec.ObjFrictionDynamic;
            s.WallE = spec.WallElasticity; s.ObjE = spec.ObjElasticity;
            return s;
        }

        /// <summary>aivehicle PlayerCollisions has no AIGoalTraffic record for traffic, so PlayerCollisionsDefault applies.</summary>
        static void ReadReactions(AttribDatabase db, AttribCollection pv, VehicleSpecs s)
        {
            foreach (var r in pv.Refs("aivehicle"))
            {
                if (!db.Collections.TryGetValue(r, out var ai) || !ai.TryGet("PlayerCollisionsDefault", out int at, out _, out _)) continue;
                var key = (Chunks.U32(ai.Source.Data, at), Chunks.U32(ai.Source.Data, at + 4));
                if (!db.Collections.TryGetValue(key, out var cr)) continue;
                s.Reactions = new Reaction[ReactionFields.Length];
                for (int i = 0; i < ReactionFields.Length; i++)
                {
                    if (!cr.TryGet(ReactionFields[i], out int o, out _, out _)) continue;
                    var d = cr.Source.Data;
                    s.Reactions[i] = new Reaction
                    {
                        Elasticity = Chunks.F32(d, o), RollHeight = Chunks.F32(d, o + 4), WeightBias = Chunks.F32(d, o + 8),
                        MassScale = Chunks.F32(d, o + 12), StunSpeed = Chunks.F32(d, o + 16), StunTime = Chunks.F32(d, o + 20),
                    };
                }
                return;
            }
        }

        /// <summary>A named node of this vehicle's collision geometry (Collection::GetBounds).</summary>
        public bool FindBound(string name, out CollisionGeometry.Bound b)
        {
            b = default;
            return Geometry != null && Geometry.Find(CollisionGeometry.NameHash(name), out b);
        }
    }
}
