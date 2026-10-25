using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Dynamics
{
    /// <summary>
    /// Attrib::Gen::rigidbodyspecs (class 0x7C90BB38; layout Generated/AttribSys/Classes/rigidbodyspecs.h) as RigidBody
    /// (Physics/Behaviors/RigidBody.cpp, speed.exe 0x670B90-0x6BB7EC) reads it. A car's collection is the pvehicle's
    /// `rigidbodyspecs` RefSpec (racers → "racers", cops → "cops", traffic → "traffic"; "cars" when unset). Vectors are
    /// in body space (x right, y up, z forward: the same as Unity car space).
    /// <list type="bullet">
    /// <item>racers: GRAVITY −9.813, WORLD_MOMENT_SCALE (3, 1.5, 3), OBJ_MOMENT_SCALE (4, 15, 3, 0),
    /// GROUND_MOMENT_SCALE (1.5, 3, 1.5), WALL_FRICTION {0.55, 0.3}, GROUND/OBJ_FRICTION {0.55, 0.45}/{0.55, 0.35},
    /// WALL_ELASTICITY 0, OBJ_ELASTICITY (0, 0, 0.05), SLEEP_VELOCITY 1, no drag (harness `physcore rb`).</item>
    /// </list>
    /// </summary>
    public sealed class RigidBodySpecs
    {
        public const uint ClassKey = 0x7C90BB38;

        public string Name = "cars";
        public Vector3 CollisionBoxPad;
        public Vector3 Drag, DragAngular;
        /// <summary>Moment::SetInertiaScale per collision kind: the inertia tensor is multiplied by it for the impulse.</summary>
        public Vector3 WorldMomentScale = Vector3.one, GroundMomentScale = Vector3.one, ObjMomentScale = Vector3.one;
        /// <summary>OBJ_MOMENT_SCALE.w: when &gt; 0, the body's mass in object collisions is multiplied by it (RigidBody.cpp:956-959).</summary>
        public float ObjMassScale;
        public Vector3 GroundElasticity, ObjElasticity, WallElasticity;
        public Vector3 Cg;
        /// <summary>{static, dynamic} (Friction(min(us, uk), us)).</summary>
        public float ObjFrictionStatic = 0.55f, ObjFrictionDynamic = 0.35f;
        public float WallFrictionStatic = 0.2f, WallFrictionDynamic = 0.1f;
        public float GroundFrictionStatic = 0.55f, GroundFrictionDynamic = 0.45f;
        public float Gravity = -9.813f, NaturalAngularDamping, SleepVelocity = 1f;
        public bool NoGroundCollisions, ImmobileObjectCollisions, NoWorldCollisions, InstanceCollisions3D, NoObjCollisions;

        /// <summary>The pvehicle's rigidbodyspecs (RefSpec at the field), else the named collection, else "cars".</summary>
        public static RigidBodySpecs ForVehicle(AttribDatabase db, string car, string fallback = "cars")
        {
            var s = new RigidBodySpecs();
            if (db == null) return s;
            var pv = car != null ? db.Get("pvehicle", car) : null;
            uint key = AttribDatabase.Hash(fallback);
            if (pv != null && pv.TryGet("rigidbodyspecs", out int at, out _, out _) && Chunks.U32(pv.Source.Data, at) == ClassKey)
                key = Chunks.U32(pv.Source.Data, at + 4);
            if (!db.Collections.TryGetValue((ClassKey, key), out var rb) && !db.Collections.TryGetValue((ClassKey, AttribDatabase.Hash("cars")), out rb))
                return s;
            s.Read(rb);
            return s;
        }

        void Read(AttribCollection rb)
        {
            Name = rb.Name ?? Name;
            Vector4 V(string f, Vector4 fb) =>
                rb.TryGet(f, out int o, out _, out _)
                    ? new Vector4(Chunks.F32(rb.Source.Data, o), Chunks.F32(rb.Source.Data, o + 4), Chunks.F32(rb.Source.Data, o + 8), Chunks.F32(rb.Source.Data, o + 12))
                    : fb;
            CollisionBoxPad = V("COLLISION_BOX_PAD", Vector4.zero);
            Drag = V("DRAG", Vector4.zero);
            DragAngular = V("DRAG_ANGULAR", Vector4.zero);
            WorldMomentScale = V("WORLD_MOMENT_SCALE", Vector4.one);
            GroundMomentScale = V("GROUND_MOMENT_SCALE", Vector4.one);
            var om = V("OBJ_MOMENT_SCALE", new Vector4(1, 1, 1, 0));
            ObjMomentScale = om;
            ObjMassScale = om.w;
            GroundElasticity = V("GROUND_ELASTICITY", Vector4.zero);
            ObjElasticity = V("OBJ_ELASTICITY", Vector4.zero);
            WallElasticity = V("WALL_ELASTICITY", Vector4.zero);
            Cg = V("CG", Vector4.zero);
            ObjFrictionStatic = rb.Float("OBJ_FRICTION", 0, ObjFrictionStatic);
            ObjFrictionDynamic = rb.Float("OBJ_FRICTION", 1, ObjFrictionDynamic);
            WallFrictionStatic = rb.Float("WALL_FRICTION", 0, WallFrictionStatic);
            WallFrictionDynamic = rb.Float("WALL_FRICTION", 1, WallFrictionDynamic);
            GroundFrictionStatic = rb.Float("GROUND_FRICTION", 0, GroundFrictionStatic);
            GroundFrictionDynamic = rb.Float("GROUND_FRICTION", 1, GroundFrictionDynamic);
            Gravity = rb.Float("GRAVITY", 0, Gravity);
            NaturalAngularDamping = rb.Float("NATURAL_ANGULAR_DAMPING", 0, 0f);
            SleepVelocity = rb.Float("SLEEP_VELOCITY", 0, SleepVelocity);
            NoGroundCollisions = rb.Bool("NO_GROUND_COLLISIONS");
            ImmobileObjectCollisions = rb.Bool("IMMOBILE_OBJECT_COLLISIONS");
            NoWorldCollisions = rb.Bool("NO_WORLD_COLLISIONS");
            InstanceCollisions3D = rb.Bool("INSTANCE_COLLISIONS_3D");
            NoObjCollisions = rb.Bool("NO_OBJ_COLLISIONS");
            // a moment scale of 0 on an axis would make that axis massless: the game's data never has one [ours: guard]
            WorldMomentScale = NonZero(WorldMomentScale);
            GroundMomentScale = NonZero(GroundMomentScale);
            ObjMomentScale = NonZero(ObjMomentScale);
        }

        static Vector3 NonZero(Vector3 v) => new Vector3(v.x > 1e-4f ? v.x : 1f, v.y > 1e-4f ? v.y : 1f, v.z > 1e-4f ? v.z : 1f);

        /// <summary>|n_body ⊙ E|: the elasticity Moment::SetElasticity gets (RigidBody.cpp:738-741, 1067-1075, 1239-1242).</summary>
        public static float Elasticity(Vector3 localNormal, Vector3 e) => Vector3.Scale(localNormal, e).magnitude;
    }
}
