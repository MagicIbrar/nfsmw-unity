using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace NfsMw.Game.Police
{
    /// <summary>
    /// AIPursuit's formation side (AI/Common/AIPursuit.cpp): the active formation and its timers (mActiveFormation,
    /// mActiveFormationTime, mFormationAttemptCount, mInFormationTimer, mBreakerTimer, mCollapseActive), the formation
    /// pick (:1725-1774), UpdateFormation (:1340-1537) with UpdateOutOfFormationOffsets (:1539) and SetupCollapse
    /// (:1239-1338). Shared by the player's pursuit (<see cref="PursuitSystem"/>) and the AI racers' (<see cref="RacerPursuit"/>).
    /// </summary>
    public sealed class PursuitFormationState
    {
        public int Type = PoliceData.STAGGER_FOLLOW;
        public Formation Formation;
        /// <summary>mActiveFormationTime: seconds left before the next pick.</summary>
        public float ActiveTime;
        public int Attempts;
        public float InFormationTimer, BreakerTimer = -1f;
        public bool CollapseActive;

        /// <summary>IPursuit::IsFinisherActive (:2635).</summary>
        public bool FinisherActive => BreakerTimer >= 0f;
        /// <summary>IPursuit::TimeToFinisherAttempt (:2639).</summary>
        public float TimeToFinisherAttempt => (Formation != null ? Formation.TimeToFinisher : 0f) - InFormationTimer;

        /// <summary>The ctor's InitFormation(0) with STAGGER_FOLLOW.</summary>
        public void Reset(HeatRow row)
        {
            Type = PoliceData.STAGGER_FOLLOW;
            Formation = new Formation(Type, row);
            ActiveTime = 0f;
            Attempts = 0;
            InFormationTimer = 0f;
            BreakerTimer = -1f;
            CollapseActive = false;
        }

        /// <summary>EndCurrentFormation (:1010): the next tick picks again; the finisher timer is dropped.</summary>
        public void EndCurrent()
        {
            ActiveTime = 0f;
            BreakerTimer = -1f;
        }

        /// <summary>
        /// The formation pick (:1725-1774): when the active time is over (not during a finisher, busted or bailed, and
        /// speech_finished), every other pick is STAGGER_FOLLOW for StaggerFormationTime; with an intact undodged roadblock
        /// FOLLOW; odd picks are weighted from CopFormations. The next pick comes after duration + rand[0, 3).
        /// </summary>
        public void Pick(HeatRow row, CopRoadblock roadblock, bool busted, bool bailed, bool speechFinished)
        {
            if (ActiveTime > 0f || FinisherActive || busted || bailed || !speechFinished || row.Formations == null || row.Formations.Length == 0) return;
            int pick = PoliceData.STAGGER_FOLLOW;
            float time = row.StaggerFormationTime;
            if (roadblock != null && !roadblock.Dodged && roadblock.CopsDamaged == 0 && roadblock.CopsDestroyed == 0) pick = PoliceData.FOLLOW;
            else if ((Attempts & 1) != 0)
            {
                float sum = row.Formations.Sum(f => f.weight), r = NfsMw.Core.SimRandom.RandFloatRange(sum);
                foreach (var f in row.Formations)
                {
                    r -= f.weight;
                    if (r <= 0) { pick = f.type; time = f.duration; break; }
                }
            }
            Attempts++;
            if (pick != Type || Formation == null) { Type = pick; Formation = new Formation(pick, row); }
            ActiveTime = NfsMw.Core.SimRandom.RandFloatRange(3f) + time;   // AIPursuit::OnTask: SimRandom_FloatRange(3) + duration
        }

        /// <summary>
        /// UpdateFormation (:1340-1537): cops within 60 m (+ the target's distance to its road) of the target are assigned
        /// the formation's slots (EvenOutOffsets, AssignClosestOffsets), the rest hold slots 25 m ahead or behind, three
        /// lanes wide (UpdateOutOfFormationOffsets). A slow target in sight starts the collapse; a finisher (box-in,
        /// rolling block) fires once enough cops have held their slots for TimeToFinisher. Returns true the tick a collapse
        /// is set up (the caller sends heavy support away, :1447-1456).
        /// </summary>
        public bool Update(PlayerCar target, Vector3 targetVel, HeatRow row, IList<CopCar> pursuers, PerpRoads roads, bool jerk, bool busted, bool bailed, bool perpInSight, float dt)
        {
            if (Formation == null) Formation = new Formation(Type, row);
            Formation.Update(Mathf.Max(0, Formation.TimeToFinisher - InFormationTimer), target);
            var tp = target.transform.position;
            float limit = 60f + (roads.Current.Valid ? Vector3.Distance(tp, roads.Current.Position) : 0);
            var candidates = new List<CopCar>();
            var rel = new List<Vector3>();
            foreach (var c in pursuers)
            {
                if (!c || c.IsSupport) continue;
                c.InFormation = false;
                if (c.Destroyed || !c.DrivableToTarget) continue;
                var off = c.transform.position - tp;
                if (off.magnitude > limit) continue;
                candidates.Add(c);
                rel.Add(target.transform.InverseTransformDirection(off));
            }
            var slots = Formation.EvenOut(rel);
            if (rel.Count > 0 && slots.Count > 0)
            {
                var assign = Formation.AssignClosest(rel, slots);
                for (int i = 0; i < candidates.Count; i++)
                {
                    if (assign[i] < 0) continue;
                    var s = slots[assign[i]];
                    var c = candidates[i];
                    c.InPositionOffset = s.InPosition;
                    c.PursuitOffset = s.Offset;
                    c.InFormation = true;
                    c.InPositionGoal = s.Goal;
                }
            }
            // the rest hold slots 25 m ahead or behind, three lanes wide
            int k = 0;
            var spare = new List<CopCar>();
            var spareRel = new List<Vector3>();
            var spareSlots = new List<Formation.Target>();
            foreach (var c in pursuers)
            {
                if (!c || c.InFormation || c.IsSupport) continue;
                c.InPosition = false;
                spare.Add(c);
                spareRel.Add(target.transform.InverseTransformDirection(c.transform.position - tp));
                spareSlots.Add(new Formation.Target { Offset = Formation.OutOfFormationOffset(k++) });
            }
            if (spare.Count > 0)
            {
                var a = Formation.AssignClosest(spareRel, spareSlots);
                for (int i = 0; i < spare.Count; i++)
                {
                    if (a[i] < 0) continue;
                    spare[i].PursuitOffset = spareSlots[a[i]].Offset;
                    spare[i].InPositionOffset = Vector3.zero;
                    spare[i].InPositionGoal = CopGoal.None;
                }
            }
            int inFormation = 0, inPosition = 0;
            float gross = 0;
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                if (c.InFormation)
                {
                    inFormation++;
                    var d = new Vector2(c.PursuitOffset.x - rel[i].x, c.PursuitOffset.z - rel[i].z).magnitude;
                    if (d < 4f) { gross += d; inPosition++; c.InPosition = true; continue; }
                }
                c.InPosition = false;
            }

            bool collapseStarted = false;
            float collapseSpeed = PoliceData.Kph(jerk ? 125f : row.CollapseSpeedKmh);
            if (BreakerTimer >= 0 && BreakerTimer < Formation.FinisherTime && !busted && !bailed) BreakerTimer += dt;
            else if (target.SpeedMps < collapseSpeed && inFormation > 0 && !busted && perpInSight && !bailed)
            {
                CollapseActive = SetupCollapse(target, targetVel, candidates, row.MaxCopsCollapsing, row.CollapseInnerRadius, row.CollapseOuterRadius);
                collapseStarted = CollapseActive;
            }
            else if (!busted && (BreakerTimer >= 0 || CollapseActive))
            {
                CollapseActive = false;
                InFormationTimer = 0;
                BreakerTimer = -1f;
                foreach (var c in candidates) { c.InPositionGoal = CopGoal.None; c.StartPursuit(c.Pursuit); }
                foreach (var c in pursuers)
                {
                    if (!c || c.Destroyed) continue;
                    if (c.Goal == CopGoal.PullOver || c.Goal == CopGoal.Ram || c.Goal == CopGoal.Pit)
                    {
                        if (c.IsSupport && c.Goal == c.SupportGoal) continue;
                        c.InPositionGoal = CopGoal.None;
                        c.StartPursuit(c.Pursuit);
                    }
                }
            }
            else if (Formation.HasFinisher && inPosition != 0 && !CollapseActive && !busted && !bailed)
            {
                float avg = gross / inPosition, rate = Formation.FinisherTolerance * 4f;
                InFormationTimer = Mathf.Max(0, InFormationTimer + Mathf.Clamp((2f * rate - avg) / rate, -1f, 1f) * dt);
                if (InFormationTimer >= Formation.TimeToFinisher)
                {
                    if (inPosition >= Formation.MinFinisherCops)
                    {
                        BreakerTimer = 0;
                        foreach (var c in candidates)
                            if (c.InFormation && c.InPositionGoal != CopGoal.None) c.DoInPositionGoal();
                    }
                    else InFormationTimer = Formation.TimeToFinisher - 0.01f;
                }
            }
            else InFormationTimer = 0;
            return collapseStarted;
        }

        /// <summary>
        /// SetupCollapse (:1239-1338): cops within 60 m ring the slow target. The nearest MaxCopsCollapsing take the inner
        /// circle, the rest the outer; each circle is sorted by angle with the front-most cop in front; goal PullOver.
        /// </summary>
        public static bool SetupCollapse(PlayerCar target, Vector3 targetVel, List<CopCar> cops, int maxInner, float inner, float outer)
        {
            inner = Mathf.Max(3f, inner);
            outer = Mathf.Max(inner + 1f, outer);
            var tp = target.transform.position;
            var front = target.SpeedMps < PoliceData.Kph(5f) ? RoadGraph.FlatDir(target.transform.forward) : RoadGraph.FlatDir(targetVel);
            var side = RoadGraph.RightOf(front);
            var list = new List<(CopCar cop, float angle, float dist)>();
            foreach (var c in cops)
            {
                var off = c.transform.position - tp;
                if (off.magnitude > 60f || !c.DrivableToTarget || c.IsSupport || c.Goal == CopGoal.FleePursuit) continue;
                float f = Vector3.Dot(front, off), s = Vector3.Dot(side, off);
                list.Add((c, f == 0 && s == 0 ? 0 : Formation.CheapAtan(f, s), off.magnitude));
            }
            if (list.Count == 0) return false;
            int innerStart = 0, numInner = list.Count;
            if (list.Count > maxInner)
            {
                list.Sort((a, b) => a.dist.CompareTo(b.dist));
                // qsort ascending: the nearest come first; the code puts the first (count - max) on the outer circle [C]
                innerStart = list.Count - maxInner;
                numInner = maxInner;
                Circle(list.GetRange(0, innerStart), outer, front, side);
            }
            Circle(list.GetRange(innerStart, numInner), inner, front, side);
            return true;
        }

        /// <summary>AssignCopsInCircle (:1314).</summary>
        static void Circle(List<(CopCar cop, float angle, float dist)> cops, float radius, Vector3 front, Vector3 side)
        {
            cops.Sort((a, b) => a.angle <= b.angle ? -1 : 1);
            int frontmost = 0;
            float smallest = 4f;
            for (int i = 0; i < cops.Count; i++)
                if (Mathf.Abs(cops[i].angle) < smallest) { smallest = Mathf.Abs(cops[i].angle); frontmost = i; }
            float step = 2f * Mathf.PI / cops.Count;
            for (int i = 0; i < cops.Count; i++)
            {
                float a = i * step;
                var c = cops[(i + frontmost) % cops.Count].cop;
                c.InPositionOffset = new Vector3(Mathf.Sin(a) * radius, 0, Mathf.Cos(a) * radius);
                c.InPositionGoal = CopGoal.PullOver;
                c.DoInPositionGoal();
            }
        }
    }
}
