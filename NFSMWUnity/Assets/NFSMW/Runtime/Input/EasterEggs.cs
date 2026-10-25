using System;
using System.Collections.Generic;
using UnityEngine;

namespace NfsMw.Controls
{
    /// <summary>
    /// EasterEggs (Misc/EasterEggs.cpp; PC ctor sub_652F70, Activate 0x65C210, UnActivate 0x65C2A0, HandleJoy 0x65C410,
    /// KeyboardInput 0x65D9E0, ActivateEasterEgg 0x65C2E0, ClearNonPersistent 0x652FB0, IsEasterEggUnlocked 0x653050,
    /// ClearGroup 0x6530B0): the cheat codes. On PC the table (0x8F8520, 0x34-byte entries: char code[32], type, item, group,
    /// unlock message, unlocked, persistent, enabled) has two entries — "burgerking" (EASTER_EGG_BURGER_KING 3) and
    /// "castrol" (EASTER_EGG_CASTROL 4), both persistent and enabled — and a 32-slot ring of typed characters / `fe`
    /// actions (the GameCube's 8 buttons became 32). Activate / UnActivate come from the SplashScreen's ctor / dtor
    /// (0x5A3030 / 0x57DC30): codes are typed at the press-start screen; KeyboardEditString::NotifyChar (0x5A8700) feeds
    /// KeyboardInput. Activating plays UISND_COMMON_SELECT, clears the ring, and the first time: BK → the FE message
    /// FEHASH_BK_UNLOCK (0x98257537); Castrol → FEHASH_CASTROL_UNLOCK (0x6521E5C2) + FEPlayerCarDB::AwardBonusCars;
    /// (unlock-all 0 → DAT_00926124, skip D-Day 1 → SkipDDayRaces + SkipCareerIntro (0x926126 / 0x926125), 2 → MikeMannBuild
    /// = 0, demo cheat 5 → FEHASH_DEMO_CHEAT + TryAwardDemoMarker — table types the PC data never uses).
    /// TrackLoader::Unload (GameFlow.cpp) calls ClearNonPersistent when the world is left.
    /// </summary>
    public sealed class EasterEggs
    {
        public enum Special { UnlockAllThings = 0, SkipDDay = 1, DisableMikeMannBuild = 2, BurgerKing = 3, Castrol = 4, DemoCheat = 5, LanguagesToggle = 6, XboxSafezone = 7, EReg2_200 = 8, EReg2Vinyl = 9, Preorder = 10 }

        public sealed class Entry
        {
            public string Code;                 // char[32] at +0 (the GC had 8 buttons)
            public uint Type, Item, Group, UnlockMessage;
            public bool Unlocked, Persistent, Enabled;
        }

        public const uint FEHASH_BK_UNLOCK = 0x98257537, FEHASH_CASTROL_UNLOCK = 0x6521E5C2, FEHASH_DEMO_CHEAT = 0xA6813B08;
        const int RingSize = 32;

        public static readonly EasterEggs Instance = new EasterEggs();

        /// <summary>The unlock switches the cheats set (Config.cpp globals): DAT_00926124 UnlockAllThings (GRaceDatabase::GetIsAvailable
        /// answers yes), 0x926125 SkipCareerIntro (Game.SkipCareerIntro), 0x926126 SkipDDayRaces, 0x92612C MikeMannBuild.</summary>
        public static bool UnlockAllThings, SkipCareerIntro, SkipDDayRaces;
        /// <summary>0x92612C is in .data's zero-filled tail (past the raw 0x24000 bytes): 0 at start; only ActivateEasterEgg (0x65C377) writes it.</summary>
        public static int MikeMannBuild = 0;

        /// <summary>A cheat was activated (type, first time): the FE's QueueGameMessage(FEHASH_*) [hook for the front end].</summary>
        public static event Action<Special, bool> Activated;

        public readonly Entry[] Table =
        {
            new Entry { Code = "burgerking", Type = (uint)Special.BurgerKing, Persistent = true, Enabled = true },
            new Entry { Code = "castrol", Type = (uint)Special.Castrol, Persistent = true, Enabled = true },
        };

        readonly char[] _ring = new char[RingSize];
        uint _count, _start;
        public bool ButtonsEnabled { get; private set; }
        public bool HaveUnlockMessage { get; private set; }
        public uint UnlockMessage { get; private set; }
        ActionQueue[] _queues = new ActionQueue[2];

        /// <summary>Activate (0x65C210): an `fe` ActionQueue ("movie q") per port, buttons on, the ring cleared.</summary>
        public void Activate()
        {
            for (int i = 0; i < 2; i++)
            {
                _queues[i]?.Dispose();
                _queues[i] = new ActionQueue(i, "fe", "movie q", false);
                _queues[i].Enable(true);
            }
            ButtonsEnabled = true;
            HaveUnlockMessage = false;
            UnlockMessage = 0;
            ClearButtons();
        }

        public void UnActivate()
        {
            for (int i = 0; i < _queues.Length; i++) { _queues[i]?.Dispose(); _queues[i] = null; }
            ButtonsEnabled = false;
            HaveUnlockMessage = false;
            UnlockMessage = 0;
            ClearButtons();
        }

        /// <summary>ClearNonPersistent: the non-persistent entries lock again (both PC entries are persistent).</summary>
        public void ClearNonPersistent()
        {
            foreach (var e in Table) if (!e.Persistent) e.Unlocked = false;
        }

        public void ClearButtons() => _count = 0;

        /// <summary>
        /// KeyboardInput (0x65D9E0): with the buttons enabled the character goes into the ring (slot (count &amp; 31)); then
        /// every entry whose code length is at most the count is compared with the last strlen(code) characters typed, and a
        /// match activates it. (The count is not wrapped: after 32 characters the ring overwrites itself.)
        /// </summary>
        public void KeyboardInput(char key)
        {
            if (!ButtonsEnabled) return;
            _ring[_count & (RingSize - 1)] = key;
            _count++;
            for (int egg = 0; egg < Table.Length; egg++)
            {
                var code = Table[egg].Code ?? "";
                uint n = (uint)code.Length;
                if (n == 0 || n > _count) continue;
                bool match = true;
                for (int i = 0; i < n && match; i++)
                    if (code[i] != _ring[(_count - n + i) & (RingSize - 1)]) match = false;
                if (match) ActivateEasterEgg(egg);
            }
        }

        /// <summary>
        /// HandleJoy (0x65C410): every queued `fe` action with value 1 is a button in the ring (up to 32); at 32 the ring is
        /// compared with each entry's 32 buttons; no match drops the oldest. The PC table holds typed codes, so no pad
        /// sequence can match — kept for the structure.
        /// </summary>
        public void HandleJoy()
        {
            if (!ButtonsEnabled) return;
            foreach (var q in _queues)
            {
                if (q == null) continue;
                while (!q.IsEmpty)
                {
                    var a = q.GetAction();
                    if (a.HasValue && a.Value.Data == 1f && _count < RingSize)
                    {
                        _ring[(_start + _count) & (RingSize - 1)] = (char)a.Value.Id;
                        _count++;
                        if (_count == RingSize)
                        {
                            bool found = false;
                            for (int egg = 0; egg < Table.Length && !found; egg++)
                            {
                                var code = Table[egg].Code ?? "";
                                found = code.Length == RingSize;
                                for (int b = 0; found && b < RingSize; b++) if (code[b] != _ring[(_start + b) & (RingSize - 1)]) found = false;
                                if (found) ActivateEasterEgg(egg);
                            }
                            if (!found) { _start = (_start + 1) & (RingSize - 1); _count--; }
                        }
                    }
                    q.PopAction();
                }
            }
        }

        /// <summary>ActivateEasterEgg (0x65C2E0).</summary>
        void ActivateEasterEgg(int egg)
        {
            var e = Table[egg];
            if (!e.Enabled) return;
            ClearGroup(e.Group);
            bool was = e.Unlocked;
            e.Unlocked = true;
            HaveUnlockMessage = true;
            UnlockMessage = e.UnlockMessage;
            ClearButtons();
            // g_pEAXSound->PlayUISoundFX(UISND_COMMON_SELECT) [hook: the FE's UI sound player]
            Debug.Log($"NFSMW cheats: '{e.Code}' entered ({(Special)e.Type}{(was ? ", already unlocked" : "")})");
            if (was) { Activated?.Invoke((Special)e.Type, false); return; }
            switch ((Special)e.Type)
            {
                case Special.UnlockAllThings: UnlockAllThings = true; break;
                case Special.SkipDDay: SkipDDayRaces = true; SkipCareerIntro = true; break;
                case Special.DisableMikeMannBuild: MikeMannBuild = 0; break;
                case Special.BurgerKing: break;        // cFEng::QueueGameMessage(FEHASH_BK_UNLOCK) — the Burger King challenge opens (Activated)
                case Special.Castrol: Game.GameSession.AwardBonusCars(); break;   // FEHASH_CASTROL_UNLOCK + FEPlayerCarDB::AwardBonusCars
                case Special.DemoCheat: break;
            }
            Activated?.Invoke((Special)e.Type, true);
        }

        void ClearGroup(uint group)
        {
            if (group == 0) return;
            foreach (var e in Table) if (e.Group == group) e.Unlocked = false;
        }

        public bool IsEasterEggUnlocked(uint type, uint item)
        {
            foreach (var e in Table) if (e.Type == type && e.Item == item) return e.Unlocked;
            return false;
        }

        public bool IsEasterEggUnlocked(Special egg) => IsEasterEggUnlocked((uint)egg, 0);
    }
}
