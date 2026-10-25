using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace NfsMw.Game.TrafficPhysics
{
    /// <summary>
    /// RBVehicle::ModifyCollision for traffic and trailers (RBVehicle.cpp:129-215), on top of CarContacts' friction and
    /// bounce (which runs first: it hooks the contact event when the first car is built).
    /// <list type="bullet">
    /// <item>Against a racer or a human (DRIVER_RACER / DRIVER_HUMAN): the aivehicle's PlayerCollisionsDefault reaction
    /// by where the hit lands (ChooseReaction: front or rear half, side when |n·fwd| &lt; 0.707) adds its Elasticity
    /// and scales mass and inertia by MassScale (when &gt; 0). Its RollHeight / WeightBias CG shift is not applied:
    /// a PhysX pair shares one contact point, which CarContacts already moves for the player [?].</item>
    /// <item>Against the world: a non-player vehicle takes the impact at its body origin's height (cog.y = 0).</item>
    /// <item>Trailers have no PlayerCar, so their wall/object friction and elasticity (rigidbodyspecs) are set here.</item>
    /// </list>
    /// </summary>
    public static class TrafficContacts
    {
        sealed class Record
        {
            public VehicleSpecs Specs;
            public bool Trailer;
            public volatile float CogY;
        }

        static readonly Dictionary<int, Record> ByCollider = new Dictionary<int, Record>();
        static readonly HashSet<int> Drivers = new HashSet<int>();
        static readonly object Lock = new object();
        static bool _hooked;

        /// <summary>Starts handling a traffic car or trailer's collider (after CarContacts registered the car's).</summary>
        public static void Register(TrafficVehicle v)
        {
            var col = v ? v.GetComponent<BoxCollider>() : null;
            if (!col) return;
            col.hasModifiableContacts = true;
            lock (Lock) ByCollider[col.GetInstanceID()] = new Record { Specs = v.Specs, Trailer = v.Car == null, CogY = v.CenterOfGravity.y };
            if (!_hooked)
            {
                Physics.ContactModifyEvent += Modify;
                Physics.ContactModifyEventCCD += Modify;
                _hooked = true;
            }
        }

        public static void Unregister(Collider col)
        {
            if (!col) return;
            lock (Lock) ByCollider.Remove(col.GetInstanceID());
        }

        /// <summary>Main thread: the racers' and humans' colliders (cars that are not traffic and not cops), and the CG heights.</summary>
        public static void Refresh(IEnumerable<PlayerCar> cars)
        {
            lock (Lock)
            {
                Drivers.Clear();
                foreach (var c in cars)
                {
                    if (!c || c.Traffic || c.GetComponent<Police.CopCar>()) continue;
                    var col = c.GetComponent<BoxCollider>();
                    if (col) Drivers.Add(col.GetInstanceID());
                }
            }
        }

        public static void UpdateCog(TrafficVehicle v)
        {
            var col = v ? v.GetComponent<BoxCollider>() : null;
            if (!col) return;
            lock (Lock) if (ByCollider.TryGetValue(col.GetInstanceID(), out var r)) r.CogY = v.CenterOfGravity.y;
        }

        static void Modify(PhysicsScene scene, NativeArray<ModifiableContactPair> pairs)
        {
            lock (Lock)
            {
                if (ByCollider.Count == 0) return;
                for (int p = 0; p < pairs.Length; p++)
                {
                    var pair = pairs[p];
                    ByCollider.TryGetValue(pair.colliderInstanceID, out var a);
                    ByCollider.TryGetValue(pair.otherColliderInstanceID, out var b);
                    if (a == null && b == null) continue;
                    bool changed = false;
                    if (a != null) changed |= Apply(ref pair, a, true);
                    if (b != null) changed |= Apply(ref pair, b, false);
                    if (changed) pairs[p] = pair;
                }
            }
        }

        static bool Apply(ref ModifiableContactPair pair, Record r, bool first)
        {
            int otherBody = first ? pair.otherBodyInstanceID : pair.bodyInstanceID;
            int otherCol = first ? pair.otherColliderInstanceID : pair.colliderInstanceID;
            var rot = first ? pair.rotation : pair.otherRotation;
            var pos = first ? pair.position : pair.otherPosition;
            var origin = pos + rot * r.Specs.Pivot;
            bool world = otherBody == 0;
            bool changed = false;
            if (r.Trailer)
            {
                // the trailer's own rigidbodyspecs friction and bounce (CarContacts never sees it)
                for (int i = 0; i < pair.contactCount; i++)
                {
                    var ln = Quaternion.Inverse(rot) * pair.GetNormal(i);
                    float e = Vector3.Scale(ln, world ? r.Specs.WallE : r.Specs.ObjE).magnitude;
                    pair.SetBounciness(i, Mathf.Clamp01(Mathf.Max(e, pair.GetBounciness(i))));
                    pair.SetStaticFriction(i, world ? r.Specs.WallUs : r.Specs.ObjUs);
                    pair.SetDynamicFriction(i, world ? r.Specs.WallUk : r.Specs.ObjUk);
                }
                changed = true;
            }
            if (world)
            {
                // RBVehicle::ModifyCollision(SimSurface): cog.y = 0 for non-players
                var up = rot * Vector3.up;
                for (int i = 0; i < pair.contactCount; i++) pair.SetPoint(i, pair.GetPoint(i) + up * r.CogY);
                return true;
            }
            var reactions = r.Specs.Reactions;
            if (reactions != null && Drivers.Contains(otherCol))
            {
                var fwd = rot * Vector3.forward;
                var n = pair.GetNormal(0);
                var point = pair.GetPoint(0);
                float front = Vector3.Dot(point - origin, fwd);
                bool side = Mathf.Abs(Vector3.Dot(n, fwd)) < 0.707f;
                var rec = reactions[front > 0f ? (side ? 1 : 0) : (side ? 3 : 2)];
                for (int i = 0; i < pair.contactCount; i++)
                    pair.SetBounciness(i, Mathf.Clamp01(pair.GetBounciness(i) + rec.Elasticity));
                if (rec.MassScale > 0f)
                {
                    var m = pair.massProperties;
                    float inv = 1f / rec.MassScale;
                    if (first) { m.inverseMassScale *= inv; m.inverseInertiaScale *= inv; }
                    else { m.otherInverseMassScale *= inv; m.otherInverseInertiaScale *= inv; }
                    pair.massProperties = m;
                }
                changed = true;
            }
            return changed;
        }

    }
}
