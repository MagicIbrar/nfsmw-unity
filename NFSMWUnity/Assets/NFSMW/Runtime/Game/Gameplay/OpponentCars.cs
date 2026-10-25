using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.UI;
using UnityEngine;

namespace NfsMw.Game.Gameplay
{
    /// <summary>
    /// Game.InitRacers (0x61DBE0) choices for the AI racers: the cars of generic characters
    /// (Game_FindPerformanceCandidates 0x61D8E0) and the racers' names (GRaceStatus::AddRacer 0x601F90).
    /// </summary>
    public static class OpponentCars
    {
        /// <summary>
        /// Legacy single-number rating (the car lot tier, 0 for UnlockedAt 16 up to 1 for UnlockedAt 2; cars outside the
        /// car lot rate 1). Only used when <see cref="PerformanceRatings"/> has no table (no attributes) [ours: fallback].
        /// </summary>
        public static float Rating(string car)
        {
            var lot = GameSession.Career?.CarLot.FirstOrDefault(c => string.Equals(c.Car, car, System.StringComparison.OrdinalIgnoreCase));
            return lot == null ? 1f : Mathf.Clamp01((16f - lot.UnlockedAt) / 14f);
        }

        /// <summary>Legacy: the player car's tier rating plus up to 0.25 for its packages (fallback only) [ours].</summary>
        public static float PlayerRating(OwnedCar car)
        {
            if (car == null || string.IsNullOrEmpty(car.Car)) return 0f;
            float parts = (car.Engine + car.Transmission + car.Tires + car.Suspension + car.Brakes + car.Nitrous + car.Induction) / (7f * 3f);
            return Mathf.Clamp01(Rating(car.Car) + 0.25f * Mathf.Clamp01(parts));
        }

        /// <summary>
        /// Game_FindPerformanceCandidates: perf' = clamp01(perf + 0.2), where perf = max(the opponents' highest
        /// MinimumAIPerformance, the player's performance); candidates = RandomOpponent cars whose stock rating is at most
        /// perf', shuffled, up to `max` of them (InitRacers asks for 3). Cars without a model folder are skipped.
        /// </summary>
        public static List<string> Candidates(float minAiPerformance, OwnedCar player, int max = 3)
        {
            var career = GameSession.Career;
            var list = new List<string>();
            if (career == null) return list;
            // Game.InitRacers: perf = Performance(m, m, m) maximized with every player's Physics::Info::Performance (the
            // player car's EstimatePerformance), then Game_FindPerformanceCandidates(list, 3, perf)
            if (PerformanceRatings.Count > 0)
            {
                var need = new PerformanceRatings.Perf(minAiPerformance, minAiPerformance, minAiPerformance);
                if (PerformanceRatings.Estimate(player, out var pp)) need = need.Max(pp);
                foreach (var car in PerformanceRatings.Candidates(need, int.MaxValue))
                {
                    // [ours] the port needs the car's model folder
                    if (!System.IO.Directory.Exists(System.IO.Path.Combine(GameData.Root, "CARS", car.ToUpperInvariant()))) continue;
                    list.Add(car);
                    if (list.Count >= max) break;
                }
                return list;
            }
            float perf = Mathf.Clamp01(Mathf.Max(minAiPerformance, PlayerRating(player)) + 0.2f);
            foreach (var car in career.RandomOpponents)
            {
                if (career.CarLot.All(c => !string.Equals(c.Car, car, System.StringComparison.OrdinalIgnoreCase))) continue;
                if (Rating(car) > perf + 1e-4f) continue;
                if (!System.IO.Directory.Exists(System.IO.Path.Combine(GameData.Root, "CARS", car.ToUpperInvariant()))) continue;
                list.Add(car);
            }
            for (int i = 0; i < list.Count; i++)
            {
                int j = Random.Range(0, list.Count);
                (list[i], list[j]) = (list[j], list[i]);
            }
            if (list.Count > max) list.RemoveRange(max, list.Count - max);
            return list;
        }

        /// <summary>
        /// Racer names: a career boss race names every AI BLACKLIST_RIVAL_%02d_LEADERBOARD (ChooseBossName 0x600730); in
        /// career a character's RacerName label (ChooseRacerName 0x5DD2A0); else an unused random RACERNAME_000..149
        /// (ChooseRandomName 0x5DC800).
        /// </summary>
        public static string NameFor(RaceEvent ev, Racer racer, ICollection<string> used)
        {
            FrontEndData.Load();
            bool career = GameSession.Mode == GameMode.Career;
            if (career && ev != null && ev.BossRace && ev.BinIndex > 0)
            {
                string label = $"BLACKLIST_RIVAL_{ev.BinIndex:00}_LEADERBOARD";
                string s = FrontEndData.Text(label);
                if (s != label) return s;
            }
            if (career && !string.IsNullOrEmpty(racer?.RacerName))
            {
                string s = FrontEndData.Text(racer.RacerName);
                if (s != racer.RacerName) return s;
            }
            if (FrontEndData.Text("RACERNAME_000") == "RACERNAME_000") return racer?.Name ?? "Racer";
            for (int tries = 0; tries < 64; tries++)
            {
                string s = FrontEndData.Text($"RACERNAME_{Random.Range(0, 150):000}");
                if (!used.Contains(s)) return s;
            }
            return racer?.Name ?? "Racer";
        }
    }
}
