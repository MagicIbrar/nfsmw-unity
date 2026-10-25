using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.Audio
{
    /// <summary>
    /// NIS_RevManager (EAXSound/sfxctl/SFXCTL_NISReving.cpp:310-430) + EAXSound's calls to it:
    /// <list type="bullet">
    /// <item>OpenNISRevData when a NIS starts (NISActivity.cpp:544, mSceneHash → GetCsisEventIndex → the scene name):
    /// SOUND\NISREVDATA\&lt;name&gt;.bin. The port opens it when <see cref="NisPlayer.Active"/> starts a scene [hook-free:
    /// polled].</item>
    /// <item>Update (EAXSound::Update while INIS plays): every car's NIS car id = its channel, car1..car8 → 0..7,
    /// cop1..cop8 → 8..15 (<see cref="NisPlayer.CarOf"/>), -1 for the rest.</item>
    /// <item>Start321Reving (EAXSound::Update on gb_Is321): CloseNIS ("RevOFF") then MAIEngineRev "QRev": AI racers start a
    /// RevPat table. The port raises it when the countdown first shows "3", as the music's Set321.</item>
    /// </list>
    /// GameFlowSndState[3] (eNIS_321GO, Lua Audio_SetFlag) gates the AI racers' countdown revving in UpdateNIS: read from
    /// SoundState.GameFlow when the race runs its basicrace script (RaceManager.Driven), else the race countdown [?].
    /// </summary>
    public static class NisRevManager
    {
        public static NisRevData Data { get; private set; }
        static NisPlayer _lastNis;
        static readonly Dictionary<PlayerCar, int> Ids = new Dictionary<PlayerCar, int>();
        static readonly System.Random Rng = new System.Random();
        static bool _countdownSeen, _321Sent;
        static readonly string[] Channels = { "car1", "car2", "car3", "car4", "car5", "car6", "car7", "car8", "cop1", "cop2", "cop3", "cop4", "cop5", "cop6", "cop7", "cop8" };

        /// <summary>The NIS is playing (INIS::Get()-&gt;IsPlaying()).</summary>
        public static bool NisPlaying => NisPlayer.Active != null;

        /// <summary>GameFlowSndState[3] (eNIS_321GO); the race countdown for races Lua does not drive [?].</summary>
        public static bool Rev321 { get; private set; }

        /// <summary>Seconds left before the race starts (ICountdown::GetSecondsBeforeRaceStart), -1 without a countdown.</summary>
        public static float CountdownLeft { get; private set; } = -1f;

        /// <summary>SetNISCarID: the car's NIS channel index, -1 if it plays none.</summary>
        public static int CarIdOf(PlayerCar car) => car && Ids.TryGetValue(car, out int id) ? id : -1;

        public static void Open(string animName)
        {
            Data = null;
            if (string.IsNullOrEmpty(animName) || NisStreamSelect.Index(animName) < 0) return;
            GameData.Mount();
            Data = NisRevData.Load(Path.Combine(GameData.Root ?? "", "SOUND", "NISREVDATA", animName + ".bin"));
        }

        /// <summary>CloseNIS: the data goes and every car gets MsgRevOff.</summary>
        public static void Close()
        {
            Data = null;
            foreach (var e in EngineAudio.All) if (e && e.Model != null) e.Model.Nis.RevOff();
        }

        /// <summary>Start321Reving: CloseNIS, then "QRev" (SFXCTL_Physics::MsgRevEngine: AI racers pick a pattern).</summary>
        public static void Start321()
        {
            Close();
            foreach (var e in EngineAudio.All)
                if (e && e.Model != null) e.Model.Nis.RevEngine(e.MixState == MixMap.AiRaceCar, Rng);
        }

        /// <summary>Per frame from the mixer (EAXSound::Update order: before the cars' controllers).</summary>
        internal static void Update()
        {
            var nis = NisPlayer.Active;
            if (nis != _lastNis)
            {
                _lastNis = nis;
                if (nis) Open(nis.Scene != null ? nis.Scene.Name : null);
            }
            Ids.Clear();
            if (nis)
                for (int i = 0; i < Channels.Length; i++)
                {
                    var car = nis.CarOf(Channels[i]);
                    if (car && !Ids.ContainsKey(car)) Ids[car] = i;
                }
            var race = RaceManager.Active;
            bool countdown = race != null && race.Countdown;
            // GameFlowSndState[3] (eNIS_321GO): the basicrace countdown state's enter/exit (Audio.SetFlag, LuaDrive.AudioSetFlag)
            // when Lua drives the race; the countdown itself otherwise [?: non-driven races]
            Rev321 = race != null && race.Driven ? SoundState.GameFlow[3] != 0 : countdown;
            CountdownLeft = countdown ? race.CountdownLeft : -1f;
            if (countdown && !_countdownSeen) { _countdownSeen = true; _321Sent = false; }
            if (!countdown) _countdownSeen = false;
            // gb_Is321 = the countdown's first digit "3" (Music.cs Set321)
            if (countdown && !_321Sent && race.CountdownLeft < 4f) { _321Sent = true; Start321(); }
        }
    }
}
