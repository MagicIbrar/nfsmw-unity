using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// AI::Math (AI/Common/AIMath.cpp, AI/AIMath.h; speed.exe 0x40B1B0-0x410CC2). Geometry is the same in Unity axes:
    /// yaw = atan2(fwd.x, fwd.z) and a positive yaw rate about +y turns +z towards +x in both.
    /// Users: AIVehicleHelicopter::AvoidCamera (SegmentSphereIntersect, CopHeli), AIVehicle::GetCollNav (PredictPosition),
    /// AIActionJackKnife (TimeToImpactXZ, AngleTo: dead code on PC, no goal adds it).
    /// </summary>
    public static class AiMath
    {
        /// <summary>AngleTo: the angle (radians) between n0 and the direction p0 → p1 (UMath::ACosa of the clamped dot).</summary>
        public static float AngleTo(Vector3 p0, Vector3 n0, Vector3 p1)
        {
            var dir = (p1 - p0).normalized;
            return Mathf.Acos(Mathf.Clamp(Vector3.Dot(dir, n0), -1f, 1f));
        }

        /// <summary>TimeToIntercept: distance / closing speed along p0 → p1; 0 when not closing (or at the same point).</summary>
        public static float TimeToIntercept(Vector3 p0, Vector3 v0, Vector3 p1, Vector3 v1)
        {
            var dir = p1 - p0;
            float distance = dir.magnitude;
            if (distance <= 0f) return 0f;
            dir /= distance;
            float closing = Vector3.Dot(v0 - v1, dir);
            if (closing <= 0f) return 0f;
            return distance / closing;
        }

        /// <summary>TimeToImpactXZ: the planar intercept time, 0 when the relative path misses by more than rad0 + rad1.</summary>
        public static float TimeToImpactXZ(Vector3 pos0, Vector3 vel0, float rad0, Vector3 pos1, Vector3 vel1, float rad1)
        {
            var p0 = new Vector3(pos0.x, 0f, pos0.z);
            var p1 = new Vector3(pos1.x, 0f, pos1.z);
            var v0 = new Vector3(vel0.x, 0f, vel0.z);
            var v1 = new Vector3(vel1.x, 0f, vel1.z);
            float t = TimeToIntercept(p0, v0, p1, v1);
            if (t <= 0f) return 0f;
            var end = p0 + (v0 - v1) * t;
            // WWorldMath::NearestPointLine2D: the nearest point to p1 on the segment p0 → end
            var seg = end - p0;
            float len2 = seg.sqrMagnitude;
            float u = len2 > 1e-8f ? Mathf.Clamp01(Vector3.Dot(p1 - p0, seg) / len2) : 0f;
            var near = p0 + seg * u;
            float lateral = new Vector2(p1.x - near.x, p1.z - near.z).magnitude;
            return lateral > rad0 + rad1 ? 0f : t;
        }

        /// <summary>PredictPosition: steps of max(0.1·t, 0.1) s along the forward speed, turning at the yaw rate.</summary>
        public static Vector3 PredictPosition(float predictTime, Vector3 position, Vector3 forward, Vector3 linearVelocity, float yawRate)
        {
            float dT = Mathf.Max(predictTime * 0.1f, 0.1f);
            var result = position;
            float speed = Vector3.Dot(linearVelocity, forward);
            float theta = Mathf.Atan2(forward.x, forward.z);
            for (float step = dT; step < predictTime; step += dT)
            {
                theta += dT * yawRate;
                result.x += speed * Mathf.Sin(theta) * dT;
                result.z += speed * Mathf.Cos(theta) * dT;
                result.y += linearVelocity.y * dT;
            }
            return result;
        }

        /// <summary>SegmentSphereIntersect: the first point of p0 → p1 on the sphere (t in [0, 1]); false for a segment
        /// shorter than √0.005 m.</summary>
        public static bool SegmentSphereIntersect(Vector3 p0, Vector3 p1, Vector3 cen, float radius, out Vector3 intersect)
        {
            intersect = default;
            var d = p1 - p0;
            float a = d.sqrMagnitude;
            if (a < 0.005f) return false;
            float b = 2f * (d.x * (p0.x - cen.x) + d.y * (p0.y - cen.y) + d.z * (p0.z - cen.z));
            float c = cen.sqrMagnitude + p0.sqrMagnitude - 2f * Vector3.Dot(cen, p0) - radius * radius;
            float disc = b * b - 4f * a * c;
            if (disc < 0f) return false;
            float t = (-b - Mathf.Sqrt(disc)) / (2f * a);
            if (t < 0f || t > 1f) return false;
            intersect = p0 + d * t;
            return true;
        }

        /// <summary>AI::Math::FloatSpring (AIMath.h:10): a damped spring towards the input (the decomp's own note says the
        /// assignment order is unsure [?]).</summary>
        public sealed class FloatSpring
        {
            readonly float _c, _d;
            public float Position { get; private set; }
            float _v;

            public FloatSpring(float springK, float damperK) { _c = springK; _d = damperK; }

            public float Integrate(float value, float dT)
            {
                float v = _v;
                float dX = -v * _d * dT + (value - Position) * _c * dT + v;
                _v = dX;
                Position += dX * dT;
                return dX;
            }

            public void SetPosition(float x) { Position = x; _v = 0f; }
        }
    }
}
