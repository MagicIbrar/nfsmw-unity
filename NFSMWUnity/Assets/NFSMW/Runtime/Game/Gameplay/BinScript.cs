using System.Collections.Generic;
using NfsMw.Core;

namespace NfsMw.Game.Gameplay
{
    /// <summary>
    /// The per-bin overrides of the `race_bin` stategraph (career_stategraphs.txt, owners race_bin_NN): text messages sent
    /// while the rival is locked, when Bin.GetNumRacesWon equals N right after a win (rival_locked NotifyRacePlacement),
    /// when Bin.GetNumChallengesPassed reaches N (NotifyChallengePassed, once each), or when the bin starts (initial).
    /// Two of them also set the world heat (Game.SetWorldHeat 0x612660 = IPerpetrator::SetHeat on the player car).
    /// </summary>
    public static class BinScript
    {
        enum When { Wins, Challenges, Initial }

        readonly struct Hook
        {
            public readonly When When;
            public readonly int N, Sms, Heat;
            public Hook(When when, int n, int sms, int heat = 0) { When = when; N = n; Sms = sms; Heat = heat; }
        }

        static Hook W(int n, int sms, int heat = 0) => new Hook(When.Wins, n, sms, heat);
        static Hook C(int n, int sms) => new Hook(When.Challenges, n, sms);
        static Hook I(int sms) => new Hook(When.Initial, 0, sms);

        static readonly Dictionary<int, Hook[]> Hooks = new Dictionary<int, Hook[]>
        {
            [15] = new[] { W(1, 123), C(1, 124), C(3, 137) },
            [14] = new[] { I(125), I(126), W(1, 18), W(2, 27), C(2, 19), C(3, 22) },
            [13] = new[] { W(1, 23), W(2, 24), W(4, 31), C(1, 35), C(2, 25), C(3, 26) },
            [12] = new[] { W(1, 127), W(2, 17), W(4, 40), C(1, 33) },
            [11] = new[] { W(2, 128), W(4, 130), C(2, 38) },
            [10] = new[] { W(3, 49), C(2, 132) },
            [9] = new[] { W(2, 53), W(5, 55), C(2, 43) },
            [8] = new[] { W(2, 59), W(4, 60), C(3, 61) },
            [7] = new[] { W(1, 66), W(4, 65, 1), W(7, 134), C(2, 133) },
            [6] = new[] { W(3, 135), C(2, 100) },
            [5] = new[] { C(3, 101) },
            [4] = new[] { W(4, 136), C(4, 102) },
            [3] = new[] { W(2, 81), W(4, 82) },
            [2] = new[] { W(4, 86, 5) },
            [1] = new[] { W(4, 90), W(6, 91) },
        };

        /// <summary>race_bin_01's rival_unlocked override sends 93 and 94 instead of SMSRivalChallenge.</summary>
        public static IEnumerable<int> RivalUnlockedSms(BlacklistBin bin)
        {
            if (bin == null) yield break;
            if (bin.Index == 1) { yield return 93; yield return 94; }
            else if (bin.RivalSms > 0) yield return bin.RivalSms;
        }

        /// <summary>The bin's `initial` state messages (the bin was just entered).</summary>
        public static void OnEnterBin(BlacklistBin bin)
        {
            if (bin == null || !Hooks.TryGetValue(bin.Index, out var hooks)) return;
            foreach (var h in hooks) if (h.When == When.Initial) Send(h);
        }

        /// <summary>
        /// rival_locked NotifyRacePlacement with Placing 1 on one of the bin's world races (`rivalLocked` = the rival was
        /// still locked when the race ended; the state only moves on at the next EnteringGameplay).
        /// </summary>
        public static void OnRaceWon(BlacklistBin bin, int racesWon, bool rivalLocked)
        {
            if (bin == null || !rivalLocked || !Hooks.TryGetValue(bin.Index, out var hooks)) return;
            foreach (var h in hooks)
                if (h.When == When.Wins && h.N == racesWon) Send(h);
        }

        /// <summary>rival_locked NotifyChallengePassed: each `chal &gt;= N` message is sent once.</summary>
        public static void OnChallengePassed(BlacklistBin bin, int challengesPassed)
        {
            if (bin == null || !Hooks.TryGetValue(bin.Index, out var hooks)) return;
            foreach (var h in hooks)
                if (h.When == When.Challenges && challengesPassed >= h.N) Send(h);
        }

        static void Send(Hook h)
        {
            var p = GameSession.Profile;
            if (p == null || p.Inbox.Contains(h.Sms) || Sms.IsPending(h.Sms)) return;
            if (h.Heat > 0 && p.Car != null) CarParts.SetHeat(p.Car, h.Heat);
            Sms.Show(h.Sms);
        }
    }
}
