using System.Collections.Generic;
using UnityEngine;

namespace NfsMw.Game.Police
{
    /// <summary>
    /// PursuitFormation and its kinds (AI/Common/AIPursuit.cpp:150-500; ai_cops.md §2.5). Slots are in the target's
    /// frame: x = right, z = ahead. A slot has a priority (lower fills first), an in-position offset and the goal the
    /// cop takes for the finisher.
    /// </summary>
    public sealed class Formation
    {
        public struct Slot
        {
            public Vector3 Offset, InPosition;
            public int Priority;
            public CopGoal Goal;
        }

        public readonly int Type;
        public readonly List<Slot> Slots = new List<Slot>();
        public int MaxCops, MinFinisherCops = 1;
        public bool HasFinisher;
        public float FinisherTolerance = 1f, FinisherTime = 2f, TimeToFinisher = 4f;
        readonly float _tightness;

        static readonly Vector3[] BoxBase = { new Vector3(0, 0, 14), new Vector3(-3.5f, 0, 0), new Vector3(3.5f, 0, 0), new Vector3(0, 0, -7.5f) };
        static readonly Vector3[] RollBase = { new Vector3(0, 0, 14), new Vector3(-2.5f, 0, 14), new Vector3(2.5f, 0, 10), new Vector3(-5, 0, 14), new Vector3(5, 0, 14) };

        public Formation(int type, HeatRow row)
        {
            Type = type;
            switch (type)
            {
                case PoliceData.PIT:
                    Add(new Vector3(4, 0, -2.7f), 1, CopGoal.Pit, new Vector3(-10, 0, -2.7f));
                    Add(new Vector3(-4, 0, -2.7f), 1, CopGoal.Pit, new Vector3(10, 0, -2.7f));
                    MaxCops = 1;
                    HasFinisher = true;
                    TimeToFinisher = 1.2f;
                    FinisherTolerance = 0.5f;
                    break;
                case PoliceData.BOX_IN:
                {
                    _tightness = row?.BoxinTightness ?? 0.5f;
                    FinisherTime = row?.BoxinDuration ?? 2f;
                    float foff = 2f - _tightness * 5f, fscale = 0.7f - _tightness * 0.5f;
                    var f0 = BoxBase[3] * fscale; f0.z = foff;
                    Add(BoxBase[0], 1, CopGoal.Ram, f0);
                    Add(BoxBase[1], 2, CopGoal.Ram, BoxBase[1] * fscale);
                    Add(BoxBase[2], 2, CopGoal.Ram, BoxBase[2] * fscale);
                    Add(BoxBase[3], 4, CopGoal.Ram, BoxBase[3] * fscale);
                    MaxCops = 4;
                    MinFinisherCops = 2;
                    HasFinisher = true;
                    break;
                }
                case PoliceData.ROLLING_BLOCK:
                {
                    _tightness = row?.RollingBlockTightness ?? 0.5f;
                    FinisherTime = row?.RollingBlockDuration ?? 2f;
                    float fscale = 1f - _tightness * 0.8f, foff = 2f - _tightness * 5f;
                    int[] pri = { 1, 2, 2, 3, 3 };
                    for (int i = 0; i < 5; i++)
                    {
                        var f = RollBase[i] * fscale; f.z = foff;
                        Add(RollBase[i], pri[i], CopGoal.Ram, f);
                    }
                    MaxCops = 4;
                    MinFinisherCops = 2;
                    HasFinisher = true;
                    break;
                }
                case PoliceData.HERD:
                    Add(new Vector3(-3, 0, 0), 1, CopGoal.None, Vector3.zero);
                    Add(new Vector3(-3, 0, 5), 2, CopGoal.None, Vector3.zero);
                    Add(new Vector3(-3, 0, -5), 3, CopGoal.None, Vector3.zero);
                    MaxCops = 3;
                    break;
                case PoliceData.STAGGER_FOLLOW:
                    Add(new Vector3(0, 0, -13), 1, CopGoal.None, Vector3.zero);
                    Add(new Vector3(0, 0, 13), 1, CopGoal.None, Vector3.zero);
                    Add(new Vector3(3.5f, 0, -13), 2, CopGoal.None, Vector3.zero);
                    Add(new Vector3(-3.5f, 0, 13), 2, CopGoal.None, Vector3.zero);
                    Add(new Vector3(-3.5f, 0, -13), 3, CopGoal.None, Vector3.zero);
                    Add(new Vector3(3.5f, 0, 13), 3, CopGoal.None, Vector3.zero);
                    MaxCops = 6;
                    break;
                default:   // FOLLOW, HELI_PURSUIT
                    Add(new Vector3(0, 0, -13), 1, CopGoal.None, Vector3.zero);
                    Add(new Vector3(3.5f, 0, -13), 2, CopGoal.None, Vector3.zero);
                    Add(new Vector3(-3.5f, 0, -13), 2, CopGoal.None, Vector3.zero);
                    Add(new Vector3(0, 0, -17), 3, CopGoal.None, Vector3.zero);
                    Add(new Vector3(3.5f, 0, -17), 4, CopGoal.None, Vector3.zero);
                    Add(new Vector3(-3.5f, 0, -17), 4, CopGoal.None, Vector3.zero);
                    MaxCops = 6;
                    break;
            }
        }

        void Add(Vector3 offset, int priority, CopGoal goal, Vector3 inPosition) =>
            Slots.Add(new Slot { Offset = offset, Priority = priority, Goal = goal, InPosition = inPosition });

        /// <summary>
        /// PursuitFormation::Update: box-in and rolling block tighten as the finisher approaches
        /// (f = TimeToFinisherAttempt / TimeToFinisher, 1 → 0); herd pushes its line toward the kerb.
        /// </summary>
        public void Update(float timeToFinisherAttempt, PlayerCar target)
        {
            float f = Mathf.Clamp01(timeToFinisherAttempt / TimeToFinisher);
            if (Type == PoliceData.BOX_IN)
            {
                float ft = _tightness * 0.2f + 0.2f, scale = f * ft + (1f - ft);
                for (int i = 0; i < 4; i++) { var s = Slots[i]; s.Offset = BoxBase[i] * scale; Slots[i] = s; }
            }
            else if (Type == PoliceData.ROLLING_BLOCK)
            {
                float ft = _tightness * 0.4f, scale = f * ft + (1f - ft);
                for (int i = 0; i < 5; i++) { var s = Slots[i]; s.Offset = RollBase[i] * scale; Slots[i] = s; }
            }
            else if (Type == PoliceData.HERD && target) Herd(target);
        }

        /// <summary>HerdFormation::Update (:450-500): x = −clamp(roadOffset − rightmost traffic lane offset + 2, 1, 3).</summary>
        void Herd(PlayerCar target)
        {
            var nav = new RoadNav { DecisionFilter = false };
            if (!nav.InitAtPoint(target.transform.position, target.transform.forward, true, 0f)) return;
            var zones = RoadGraph.ZonesAt(nav.Segment, nav.Fwd);
            if (zones == null || zones.Length == 0) return;
            var dir = RoadGraph.FlatDir(nav.Forward);
            float roadOffset = Vector3.Dot(target.transform.position - nav.Position, RoadGraph.RightOf(dir));
            float sign = nav.Fwd ? 1f : -1f, rightmost = 0;
            foreach (var z in zones)
                if (z.Type == (int)RoadGraph.Lane.Traffic) rightmost = Mathf.Max(rightmost, z.Offset * sign);
            float crowd = Mathf.Max(1f, Mathf.Min(3f, roadOffset - rightmost + 2f));
            for (int i = 0; i < Slots.Count; i++) { var s = Slots[i]; s.Offset.x = -crowd; Slots[i] = s; }
        }

        // ---- slot assignment (AIPursuit.cpp:1047-1168) ----

        public struct Target { public Vector3 Offset, InPosition; public CopGoal Goal; }

        /// <summary>
        /// EvenOutOffsets: while fewer slots are taken than candidates (and MaxCops), take the free slot with the lowest
        /// priority number whose summed distance to all candidates is no larger than the best so far.
        /// </summary>
        public List<Target> EvenOut(List<Vector3> rel)
        {
            var taken = new List<Target>();
            var free = new List<int>();
            for (int i = 0; i < Slots.Count; i++) free.Add(i);
            while (rel.Count > taken.Count && taken.Count < MaxCops)
            {
                int best = -1, bestPri = 0;
                float bestDist = 0;
                foreach (int i in free)
                {
                    var s = Slots[i];
                    if (best >= 0 && s.Priority > bestPri) continue;
                    float sum = 0;
                    foreach (var c in rel) sum += Vector3.Distance(c, s.Offset);
                    if (best < 0 || sum <= bestDist) { best = i; bestDist = sum; bestPri = s.Priority; }
                }
                if (best < 0) break;
                taken.Add(new Target { Offset = Slots[best].Offset, InPosition = Slots[best].InPosition, Goal = Slots[best].Goal });
                free.Remove(best);
            }
            return taken;
        }

        /// <summary>
        /// AssignClosestOffsets: distances with along-track gaps at a quarter; repeatedly take the cop whose farthest
        /// remaining slot is farthest, and give that slot to its nearest unassigned cop. Returns slot index per cop (−1 = none).
        /// </summary>
        public static int[] AssignClosest(List<Vector3> rel, List<Target> slots)
        {
            int rows = rel.Count, cols = slots.Count;
            var result = new int[rows];
            for (int i = 0; i < rows; i++) result[i] = -1;
            if (rows == 0 || cols == 0) return result;
            var dist = new float[rows * cols];
            var max = new float[rows];
            for (int i = 0; i < rows; i++)
            {
                var c = rel[i]; c.z *= 0.25f;
                float m = 0;
                for (int j = 0; j < cols; j++)
                {
                    var o = slots[j].Offset; o.z *= 0.25f;
                    float d = new Vector2(c.x - o.x, c.z - o.z).magnitude;
                    dist[i * cols + j] = d;
                    m = Mathf.Max(m, d);
                }
                max[i] = m;
            }
            const float Assigned = -1f;
            int left = cols;
            do
            {
                int cop = -1;
                float far = 0;
                for (int i = 0; i < rows; i++)
                    if (max[i] != Assigned && max[i] > far) { far = max[i]; cop = i; }
                if (cop < 0) continue;
                int slot = -1;
                for (int j = 0; j < cols; j++)
                    if (dist[cop * cols + j] != Assigned && dist[cop * cols + j] == far) { slot = j; break; }
                if (slot < 0) continue;
                cop = -1;
                float close = 100000f;
                for (int i = 0; i < rows; i++)
                {
                    float d = dist[i * cols + slot];
                    if (d != Assigned && max[i] != Assigned && d < close) { close = d; cop = i; }
                }
                if (cop < 0) continue;
                result[cop] = slot;
                max[cop] = Assigned;
                for (int i = 0; i < rows; i++)
                {
                    dist[i * cols + slot] = Assigned;
                    if (max[i] < 0) continue;
                    float m = 0;
                    for (int j = 0; j < cols; j++) m = Mathf.Max(m, dist[i * cols + j]);
                    max[i] = m;
                }
            } while (--left > 0);
            return result;
        }

        /// <summary>UpdateOutOfFormationOffsets (:1539-1592): the i-th spare cop holds (((i/2)+1)%3 − 1)·3.5, ±(25 + 5·(i/6)).</summary>
        public static Vector3 OutOfFormationOffset(int i)
        {
            int r = i / 6, s = 1 - (i % 2) * 2, c = (i / 2 + 1) % 3 - 1;
            return new Vector3(c * 3.5f, 0, s * (r * 5f + 25f));
        }

        /// <summary>cheap_atan_like_function (:1223): a monotonic pseudo-angle in (−4, 4].</summary>
        public static float CheapAtan(float f, float s)
        {
            if (f > 0) return s > 0 ? (f > s ? s / f : 2f - f / s) : (f > -s ? s / f : -2f - f / s);
            return s > 0 ? (-f > s ? s / f + 4f : 2f - f / s) : (-f > -s ? s / f - 4f : -2f - f / s);
        }
    }
}
