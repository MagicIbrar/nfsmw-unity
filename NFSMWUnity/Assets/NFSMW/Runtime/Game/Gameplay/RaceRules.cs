using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game.Gameplay
{
    /// <summary>
    /// GRaceStatus / GRacerInfo bookkeeping rules that do not depend on the scene (specs/gameplay_core.md §1):
    /// racer ordering, rankings, split points, the lap-knockout rule and the race-length split into laps.
    /// </summary>
    public static class RaceRules
    {
        /// <summary>UpdateSplits (0x5E3400): split k is recorded when the racer passes 20/40/60/80 % of the race.</summary>
        public static readonly float[] SplitPercent = { 20f, 40f, 60f, 80f };

        /// <summary>
        /// GRacerInfo::IsBehind (0x5E31D0, the comparator of CalculateRankings): finished racers by race time ahead of
        /// everyone still racing; knocked-out / blown / totalled racers behind the others (between two of them the one
        /// that stopped later is ahead); DNF by completion; else more laps, then more of the race completed.
        /// </summary>
        public static bool IsBehind(RaceManager.Entrant a, RaceManager.Entrant b)
        {
            if (a.Finished && b.Finished) return a.FinishTime > b.FinishTime;
            if (a.Finished) return false;
            if (b.Finished) return true;
            if (a.Out && b.Out) return a.StopTime < b.StopTime;
            if (a.EngineBlown && b.EngineBlown) return a.StopTime < b.StopTime;
            if (a.Totalled && b.Totalled) return a.StopTime < b.StopTime;
            if (a.DNF && b.DNF) return b.PctRaceComplete < a.PctRaceComplete;
            if (a.Out || a.EngineBlown || a.Totalled) return true;
            if (b.Out || b.EngineBlown || b.Totalled) return false;
            if (a.LapsCompleted != b.LapsCompleted) return a.LapsCompleted < b.LapsCompleted;
            return a.PctRaceComplete < b.PctRaceComplete;
        }

        /// <summary>
        /// CalculateRankings (0x5FDE80): bubble sort with IsBehind, or by point total (more first) when the event ranks by
        /// points and the two totals sum to more than 1. Sets Place (1-based) and AiRank (0 for humans, else the place
        /// among the AI). Returns the order, leader first.
        /// </summary>
        public static List<RaceManager.Entrant> CalculateRankings(List<RaceManager.Entrant> racers, bool byPoints)
        {
            var order = new List<RaceManager.Entrant>(racers);
            for (int last = order.Count - 1; last > 0; last--)
                for (int i = 0; i < last; i++)
                {
                    var a = order[i];
                    var b = order[i + 1];
                    bool swap = byPoints && a.Points + b.Points > 1f ? a.Points < b.Points : IsBehind(a, b);
                    if (swap) { order[i] = b; order[i + 1] = a; }
                }
            int humans = 0;
            for (int i = 0; i < order.Count; i++)
            {
                order[i].Place = i + 1;
                if (order[i].Human || order[i].Remote) { order[i].AiRank = 0; humans++; }
                else order[i].AiRank = i + 1 - humans;
            }
            return order;
        }

        /// <summary>
        /// Game_NotifyLapFinished (0x611850) for a lap knockout: once the number of racers with at least `lap` laps done
        /// equals racers − KnockoutsPerLap·lap, every racer still in with fewer laps is knocked out. Returns them.
        /// </summary>
        public static List<RaceManager.Entrant> LapKnockouts(List<RaceManager.Entrant> racers, int lap, int knockoutsPerLap)
        {
            var outs = new List<RaceManager.Entrant>();
            if (knockoutsPerLap <= 0) return outs;
            int done = 0;
            foreach (var e in racers) if (e.LapsCompleted >= lap) done++;
            if (done != racers.Count - knockoutsPerLap * lap) return outs;
            foreach (var e in racers)
                if (!e.Out && !e.Finished && e.LapsCompleted < lap) outs.Add(e);
            return outs;
        }

        /// <summary>UMath::Ramp (0x401AE0): clamp01((x − a) / (b − a)), 0 when b − a is not positive.</summary>
        public static float Ramp(float x, float a, float b) => b - a <= 1e-6f ? 0f : Mathf.Clamp01((x - a) / (b - a));
    }

    /// <summary>
    /// A race trigger the racers drive through (WTrigger box / plane of a checkpoint, the finish line or the wrong-way
    /// plane). Entering is tested on the segment the car moved along this frame, so a 2 m deep box is not skipped at
    /// speed: crossing the trigger's mid plane inside its width and height, or being inside the box, counts.
    /// </summary>
    public sealed class RaceTrigger
    {
        public RacePoint Point;
        public Vector3 Centre, Normal, Right;
        public float HalfDepth, HalfWidth, HalfHeight;

        /// <summary>
        /// Builds the trigger in Unity space. The heading is the record's Rotation (the start grid's convention); when it
        /// disagrees with the route direction there by more than 60° the route direction is used instead [?: our route
        /// polyline and the game's trigger layout come from different data].
        /// </summary>
        public static RaceTrigger From(RacePoint p, Vector3 routeDir, float defaultWidth = 40f)
        {
            var t = new RaceTrigger { Point = p, Centre = Coords.ToUnity(p.Position) };
            float a = p.Rotation * Mathf.Deg2Rad;
            var n = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a));
            routeDir.y = 0;
            if (routeDir.sqrMagnitude > 1e-4f)
            {
                routeDir.Normalize();
                if (Mathf.Abs(Vector3.Dot(n, routeDir)) < 0.5f) n = routeDir;
                else if (Vector3.Dot(n, routeDir) < 0) n = -n;
            }
            t.Normal = n;
            t.Right = Vector3.Cross(Vector3.up, n);
            float width = p.Dims.Y > 0 ? p.Dims.Y : p.Width > 0 ? p.Width : defaultWidth;
            t.HalfWidth = width * 0.5f;
            t.HalfDepth = Mathf.Max(1f, (p.Dims.X > 0 ? p.Dims.X : 2f) * 0.5f);
            t.HalfHeight = Mathf.Max(10f, (p.Dims.Z > 0 ? p.Dims.Z : 40f) * 0.5f);
            return t;
        }

        /// <summary>The car moved from `from` to `to`: did it enter the trigger (any direction, or only along Normal)?</summary>
        public bool Entered(Vector3 from, Vector3 to, bool alongNormalOnly = false)
        {
            float d0 = Vector3.Dot(from - Centre, Normal), d1 = Vector3.Dot(to - Centre, Normal);
            if (alongNormalOnly && d1 <= d0) return false;
            if (Inside(to)) return !alongNormalOnly || d1 >= 0;
            if ((d0 < 0) == (d1 < 0)) return false;
            float t = Mathf.Abs(d0) / Mathf.Max(1e-4f, Mathf.Abs(d0 - d1));
            var p = Vector3.Lerp(from, to, t);
            return Mathf.Abs(Vector3.Dot(p - Centre, Right)) <= HalfWidth && Mathf.Abs(p.y - Centre.y) <= HalfHeight;
        }

        public bool Inside(Vector3 p)
        {
            var d = p - Centre;
            return Mathf.Abs(Vector3.Dot(d, Normal)) <= HalfDepth && Mathf.Abs(Vector3.Dot(d, Right)) <= HalfWidth && Mathf.Abs(d.y) <= HalfHeight;
        }
    }
}
