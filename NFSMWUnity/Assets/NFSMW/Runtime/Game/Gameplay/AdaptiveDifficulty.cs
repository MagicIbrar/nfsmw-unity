using System.Linq;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.Game.Gameplay
{
    /// <summary>
    /// GRaceStatus adaptive difficulty (specs/gameplay_core.md §1.8): a career-wide bonus in [-1, 1] that moves after each
    /// career race by how the player did, and shifts every opponent's base skill (AIVehicle::ComputeSkill).
    /// </summary>
    public static class AdaptiveDifficulty
    {
        /// <summary>GRaceStatus::eAdaptiveGainReason.</summary>
        public enum Reason { EngineBlown = 0, VehicleDestroyed = 1, FinalizeRace = 2, RacerKnockedOut = 3, RacerFinished = 4, Restart = 5 }

        /// <summary>GetAdaptiveDifficutly (0x5DC180): the bonus in career context, else 0.</summary>
        public static float Current =>
            GameSession.Mode == GameMode.Career && GameSession.Profile != null && !GameSession.SplitScreen && GameSession.Lan == null
                ? Mathf.Clamp(GameSession.Profile.AdaptiveBonus, -1f, 1f) : 0f;

        static readonly float[] Up = { 0.5f, 0.75f, 1f }, Down = { 0.5f, 0.375f, 0.25f };

        /// <summary>
        /// ComputeSkill's career branch (AIVehicle.cpp, ai_racer.md §7): base = clamp(cs + d·(d > 0 ? Up(cs) : Down(cs))),
        /// Up = {0.5, 0.75, 1.0} and Down = {0.5, 0.375, 0.25} over cs = 0, 0.5, 1.
        /// </summary>
        public static float ApplyToSkill(float cs)
        {
            float d = Current;
            if (d == 0) return Mathf.Clamp01(cs);
            var table = d > 0 ? Up : Down;
            float x = Mathf.Clamp01(cs) * 2f;
            int i = Mathf.Min((int)x, 1);
            float k = Mathf.Lerp(table[i], table[i + 1], x - i);
            return Mathf.Clamp01(cs + d * k);
        }

        /// <summary>
        /// UpdateAdaptiveDifficulty (0x5FF2E0): once per race, career single player, racing, a post-race screen, a known
        /// race length, at least two racers. R(x,a,b) = UMath::Ramp; the bonus stays in [-1, 1].
        /// </summary>
        public static void Update(RaceManager race, Reason reason, RaceManager.Entrant who)
        {
            if (race == null || race.AdaptiveComputed || GameSession.Mode != GameMode.Career || GameSession.Profile == null) return;
            if (GameSession.SplitScreen || GameSession.Lan != null || race.Entrants.Count < 2 || race.Event == null || race.Event.NoPostRaceScreen) return;
            float len = race.RouteLength;
            if (len <= 0) return;
            var all = race.Entrants;
            RaceManager.Entrant humanWinner = null, aiWinner = null, humanOut = null;
            foreach (var e in all)
            {
                if (e.Human)
                {
                    if (e.Busted) return;
                    if (e.Finished && e.Place == 1) humanWinner = e;
                    else if (race.Event.Type == RaceType.Knockout && e.Out) humanOut = e;
                }
                else if (e.Finished && e.Place == 1) aiWinner = e;
            }
            float b = GameSession.Profile.AdaptiveBonus;
            if (reason == Reason.EngineBlown || reason == Reason.VehicleDestroyed || reason == Reason.Restart)
            {
                if (reason != Reason.Restart && (who == null || !who.Human)) return;
                float h = 0, a = 0;
                foreach (var e in all)
                {
                    if (e.Out || (reason == Reason.Restart && (e.Totalled || e.EngineBlown))) continue;
                    if (e.Human) h = Mathf.Max(h, e.PctRaceComplete); else a = Mathf.Max(a, e.PctRaceComplete);
                }
                if (a > h && h > 0) b += -0.4f * RaceRules.Ramp((a - h) * len * 0.01f, 0, 300) * h * 0.01f;
            }
            else if (race.Event.Type == RaceType.SpeedTrap)
            {
                if (all.Any(e => !e.Finished)) return;
                float p = all.Where(e => e.Human).Select(e => e.Points).DefaultIfEmpty(0).Max();
                float m = all.Where(e => !e.Human).Select(e => e.Points).DefaultIfEmpty(0).Max();
                float top = Mathf.Max(p, m);
                if (top <= 0 || m <= 0 || p <= 0) return;
                float f = (p - m) / top;
                b += f <= 0 ? -0.2f * RaceRules.Ramp(-f, 0, 0.2f) : 0.2f * RaceRules.Ramp(f, 0.05f, 0.2f);
            }
            else if (humanWinner != null)
            {
                float best = 0;
                foreach (var e in all)
                    if (e != humanWinner && !e.Finished && !e.Totalled && !e.EngineBlown) best = Mathf.Max(best, e.PctRaceComplete);
                float gap = (100f - best) * len * 0.01f;
                if (gap > 0 && best > 0) b += 0.4f * RaceRules.Ramp(gap, 200, 750);
            }
            else if (aiWinner != null)
            {
                bool any = false;
                foreach (var e in all)
                {
                    if (!e.Human || e.Finished) continue;
                    float d = (100f - e.PctRaceComplete) * len * 0.01f;
                    if (d <= 0) continue;
                    b += (-0.3f * RaceRules.Ramp(d, 0, 300) - 0.1f) * e.PctRaceComplete * 0.01f;
                    any = true;
                }
                if (!any) return;
            }
            else if (humanOut != null)
            {
                int laps = Mathf.Max(1, race.Event.Laps);
                float d = Mathf.Repeat((100f - humanOut.PctRaceComplete) * len * 0.01f, len / laps);
                if (d <= 0) return;
                b += (-0.3f * RaceRules.Ramp(d, 0, 300) - 0.1f) * humanOut.PctRaceComplete * 0.01f;
            }
            else return;
            race.AdaptiveComputed = true;
            // stored as a signed 16-bit fraction (careerSettings short, SyncronizeAdaptiveBonus 0x5DC1A0)
            GameSession.Profile.AdaptiveBonus = Mathf.Round(Mathf.Clamp(b, -1f, 1f) * 32767f) / 32767f;
            Debug.Log($"NFSMW adaptive difficulty: {reason} -> {GameSession.Profile.AdaptiveBonus:0.000}");
        }
    }
}
