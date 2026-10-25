using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;
using NfsMw.UI;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// The career story as the game runs it (specs/career_flow.md, from the gameplay.bin stategraph scripts):
    /// New Career → the D-Day prologue in the BMW (16.1.0, 16.2.2, 16.2.3, 16.1.1, 16.2.1, chained by PostRaceActivity),
    /// the scripted loss to Razor → car lot (UnlockedAt 16) → spawn at career_start_l2ra → drive to the Rosewood
    /// safehouse (SMS 15 / 121, GPS) → safehouse intro → Blacklist #15.
    /// </summary>
    public static class Story
    {
        public const string Prologue = "prologue", CarLot = "carlot", ToSafehouse = "to_safehouse", Free = "career";
        // speed.exe table 0x8A37E4 (16.8.1, the Rog and Razor NIS, sits between 16.1.1 and 16.2.1)
        static readonly string[] PrologueOrder = { "16.1.0", "16.2.2", "16.2.3", "16.1.1", "16.2.1" };
        const string PrologueCar = "M3GTRCAREERSTART";
        static readonly Vec3 SafehouseTarget = new Vec3(1768.4f, 2497.5f, 147.7f);   // drive_to_safe_house gpstarget

        static GameBootstrap Boot => GameBootstrap.Instance;
        /// <summary>A rival beaten in the race just finished (its bin), for the safehouse sequence after the results.</summary>
        public static int PendingRivalBeaten;

        /// <summary>
        /// race_bin rival_locked → rival_unlocked: once the bin's wins, bounty and milestones are met, Mia's SMS
        /// (SMSRivalChallenge) says the rival will race you. Called after races, pursuits and safehouse visits.
        /// </summary>
        public static void CheckProgress()
        {
            if (P == null || P.Stage != Free || GameSession.Mode != GameMode.Career) return;
            if (GameSession.ScriptsDrive) return;   // Drive: race_bin's check_rival → rival_unlocked does this
            var bin = GameSession.CurrentBin;
            if (bin == null || !GameSession.CanChallengeBoss) return;
            // rival_unlocked StateEnter (#310): Game.UnlockRace on every BossRaces entry
            Gameplay.RaceDatabase.OnRivalUnlocked(bin);
            // rival_unlocked: EAddSMS(SMSRivalChallenge) (race_bin_01 overrides it with 93 and 94)
            foreach (int id in Gameplay.BinScript.RivalUnlockedSms(bin))
                if (!P.Inbox.Contains(id) && !Sms.IsPending(id)) Sms.Show(id);
        }

        /// <summary>
        /// After a rival falls (FE step machine 0x5302c0): region unlock (new bin 12 or 8), the next rival's bio, its
        /// Blacklist movie (blacklist_NN), at bin 13 Mia's police-database movie (storyfmv_rap30), then the bin's entry SMS.
        /// </summary>
        public static void AfterRace()
        {
            CheckProgress();
            int beaten = PendingRivalBeaten;
            if (beaten <= 0) return;
            PendingRivalBeaten = 0;
            // uiRepSheetRivalFlow (fe_career_screens.md §3.3): result, markers, region unlock, bio, movies, entry SMS;
            // its Next (0x530359) clears post-rival mode (FEDatabase flag 0x20000)
            if (P != null) { P.PostRivalMode = false; P.Save(); }
            RivalFlow.Start(beaten);
        }

        static void WhenClosed(FrontEnd fe, Action then)
        {
            void H() { fe.Closed -= H; then(); }
            fe.Closed += H;
        }

        /// <summary>Where the player comes back out of the menu zone they entered.</summary>
        public static RacePoint ZoneRespawn;
        static Profile P => GameSession.Profile;

        /// <summary>New career: reset to bin 16 with no cars and no cash, then straight into the prologue.</summary>
        public static void NewCareer(string alias)
        {
            GameSession.SplitScreen = false;   // a career is single player
            GameSession.Profile = new Profile { Name = alias, CareerStarted = true, Bin = 16, Cash = 0, Stage = Prologue };
            GameSession.Profile.Save();
            GameSession.Mode = GameMode.Career;
            RunNextPrologue();
        }

        /// <summary>Resume Career: wherever the story stands.</summary>
        public static void Resume()
        {
            GameSession.Mode = GameMode.Career;
            GameSession.SplitScreen = false;   // a career is single player
            switch (string.IsNullOrEmpty(P.Stage) ? Free : P.Stage)
            {
                case Prologue: RunNextPrologue(); break;
                case CarLot: OpenCarLot(); break;
                case ToSafehouse: EnterAt(GameSession.Career.Point("career_start_l2ra"), BeginDriveToSafehouse); break;
                default:
                    var spawn = P.HasSpawn ? new RacePoint { Position = new Vec3(P.SpawnX, P.SpawnY, P.SpawnZ), Rotation = P.SpawnRot } : GameSession.Career.Point("menu_gate_safehouse/zone_safehouse_1/respawn");
                    EnterAt(spawn, null);
                    break;
            }
        }

        // ---- prologue -------------------------------------------------------------------------------------------

        static RaceEvent NextPrologueEvent()
        {
            foreach (var id in PrologueOrder)
                if (!P.EventsDone.Contains(id)) return GameSession.Career.EventById(id);
            return null;
        }

        static void RunNextPrologue()
        {
            var ev = NextPrologueEvent();
            if (ev == null) { AfterPrologue(); return; }
            GameSession.QuickCar = new OwnedCar { Car = GameSession.Career.CarForPreset(ev.PlayerCar ?? PrologueCar) ?? "bmwm3gtre46", Paint = new Color(0.82f, 0.83f, 0.85f) };
            if (GameSession.ScriptsDrive)
            {
                // Drive: the story movie, then the race loads and its script plays the time card (EShowMessageScreen),
                // the NIS and the race; the PostRaceActivity chain runs the next D-Day race
                RaceManager.Script = null;
                RaceManager.OnEnd = null;
                GameFlow.StartRace(ev);
                return;
            }
            // the event's intro: a time card (IntroMessageID), then the story movie, then the race with its intro NIS
            StoryCard.Run(ev.IntroMessageId >= 0 ? $"DDAY_TIMELAPSE_{ev.IntroMessageId + 1}" : null, ev.IntroMovie, () =>
            {
                RaceManager.Script = ScriptFor(ev);
                RaceManager.OnEnd = PrologueRaceOver;
                GameFlow.StartRace(ev, skipMovie: true);
            });
        }

        /// <summary>A prologue race ended: chain to the next (PostRaceActivity), or restart a lost one.</summary>
        static bool PrologueRaceOver(RaceManager race, bool won)
        {
            var ev = race.Event;
            RaceManager.OnEnd = null;
            RaceManager.Script = null;
            bool scripted = ev.EventId == "16.1.0" || ev.EventId == "16.2.1";   // these end by script, whatever the result
            if (won || scripted)
            {
                if (!P.EventsDone.Contains(ev.EventId)) P.EventsDone.Add(ev.EventId);
                if (won && ev.Cash > 0) P.Cash += Mathf.RoundToInt(ev.Cash);
                P.Save();
            }
            var fe = FrontEnd.Create();
            string title = scripted ? (ev.EventId == "16.2.1" ? "Engine blown" : "Race over") : won ? "You won!" : "You lost";
            string body = scripted ? "" : won ? $"{ev.TypeName}  +${ev.Cash:N0}" : "Restart the race";
            race.Cleanup(false);
            if (scripted && ev.EventId == "16.2.1")
            {
                // OutroMovie storyfmv_bus12: Razor takes the BMW, Cross arrests you, Mia picks you up → car lot
                StoryCard.Run(null, ev.OutroMovie, () => { P.Stage = CarLot; P.Save(); OpenCarLot(); });
                return true;
            }
            fe.Push(new MessageScreen(title, body));
            void Next() { fe.Closed -= Next; RunNextPrologue(); }
            fe.Closed += Next;
            return true;
        }

        /// <summary>Per-event scripts of the prologue (stategraph handlers of 16_1_0_partial_dday and 16_2_1_d_day_intro).</summary>
        static Action<RaceManager> ScriptFor(RaceEvent ev)
        {
            switch (ev.EventId)
            {
                case "16.1.0":
                {
                    // trigger call_from_mia → ECellCall(10); 8 s later the AI takes both cars, fade, race over
                    float called = -1;
                    return race =>
                    {
                        if (called < 0 && race.PlayerNear(ev, "call_from_mia", 40f)) { called = race.RaceTime; Sms.Show(10, call: true); }
                        if (called >= 0 && race.RaceTime - called > 8f && !StoryCard.Fading) StoryCard.FadeOut(3f, () => race.ForceEnd(false));
                    };
                }
                case "16.2.1":
                {
                    // call_from_mia → ECellCall(10) + 15 s timer; kill_engine or the timer → SabotageEngine; 1 s → fade
                    float called = -1, blown = -1;
                    return race =>
                    {
                        if (called < 0 && race.PlayerNear(ev, "call_from_mia", 40f)) { called = race.RaceTime; Sms.Show(10, call: true); }
                        if (blown < 0 && (race.PlayerNear(ev, "kill_engine", 80f) || (called >= 0 && race.RaceTime - called > 15f)))
                        {
                            blown = race.RaceTime;
                            if (race.PlayerCarObject) race.PlayerCarObject.Sabotage();
                        }
                        if (blown >= 0 && race.RaceTime - blown > 1.5f && !StoryCard.Fading) StoryCard.FadeOut(1.5f, () => race.ForceEnd(false));
                    };
                }
            }
            return null;
        }

        // ---- after the prologue -----------------------------------------------------------------------------------

        static void AfterPrologue()
        {
            P.Stage = CarLot;
            P.Save();
            OpenCarLot();
        }

        // ---- the scripts' calls in Drive mode (LuaDrive) ------------------------------------------------------------

        /// <summary>
        /// Game.JumpToCarLot (0x605250, 16.2.1's `done`): SetTimeOfDay, EQuitToFE to the car lot — the career Car_Select
        /// with the bin-16 cars.
        /// </summary>
        public static void JumpToCarLot()
        {
            if (P == null) return;
            if (RaceManager.Active) RaceManager.Active.Cleanup(false);
            if (!P.EventsDone.Contains(RaceIndex.DDayEnd)) P.EventsDone.Add(RaceIndex.DDayEnd);
            P.Stage = CarLot;
            P.Save();
            OpenCarLot();
        }

        /// <summary>A D-Day race is over (Drive: EnterPostRaceFlow / its win / its `done`): the prologue moves on.</summary>
        public static void OnDDayRaceOver(RaceEvent ev)
        {
            if (P == null || ev?.EventId == null || P.EventsDone.Contains(ev.EventId)) return;
            P.EventsDone.Add(ev.EventId);
            P.Save();
        }

        /// <summary>
        /// A D-Day race quit (Drive: its `abandoned` → `done`, no PostRaceActivity): the prologue cannot go on in free roam,
        /// so the race starts again [ours: the exe's D-Day results offer no way out but on].
        /// </summary>
        public static void PrologueRaceAbandoned(RaceEvent ev)
        {
            if (P == null || P.Stage != Prologue || GameFlow.RaceLoading || RaceManager.Active != null) return;
            Debug.Log($"NFSMW story: {ev?.EventId} abandoned in the prologue; it starts again");
            if (Boot) Boot.StartCoroutine(NextFrame(() =>
            {
                if (P != null && P.Stage == Prologue && !GameFlow.RaceLoading && RaceManager.Active == null) RunNextPrologue();
            }));
        }

        static IEnumerator NextFrame(Action a) { yield return null; a(); }

        /// <summary>
        /// Game.PlayTutorial (0x6124E0): a career drag / tollbooth / speedtrap race not seen yet plays its tutorial movie
        /// (career flags +0xAC: 0x40 drag, 0x100 tollbooth, 0x80 speedtrap); else MNotifyMovieFinished at once (the
        /// LuaGameHost wait posts it when no movie is up).
        /// </summary>
        public static void PlayTutorial(RaceEvent ev)
        {
            if (P == null || ev == null || GameSession.Mode != GameMode.Career) return;
            int bit = ev.Type == RaceType.Drag ? 0x40 : ev.Type == RaceType.Tollbooth ? 0x100 : ev.Type == RaceType.SpeedTrap ? 0x80 : 0;
            if (bit == 0 || (P.TutorialsSeen & bit) != 0) return;
            P.TutorialsSeen |= bit;
            P.Save();
            string movie = bit == 0x40 ? "drag_tutorial" : bit == 0x100 ? "tollbooth_tutorial" : "speedtrap_tutorial";
            StoryCard.Run(null, movie, null);
        }

        /// <summary>Game.JumpToCarLot: the career Car_Select with the bin-16 cars, paid with the prologue winnings.</summary>
        static void OpenCarLot()
        {
            GameFlow.EndGameplay();   // EQuitToFE: GameplayActivity goes with the world
            if (Boot) Boot.UnloadWorld();
            var fe = FrontEnd.Create();
            fe.CloseAll();
            fe.Push(new NewCareerScreen());
        }

        /// <summary>The starter car is bought: into Rockport at the Rosewood car lot (career_start_l2ra).</summary>
        public static void StarterBought()
        {
            P.Stage = ToSafehouse;
            P.Save();
            EnterAt(GameSession.Career.Point("career_start_l2ra"), BeginDriveToSafehouse);
        }

        static void EnterAt(RacePoint at, Action then)
        {
            GameSession.Mode = GameMode.Career;
            Boot.StartCoroutine(EnterRoutine(at, then));
        }

        static IEnumerator EnterRoutine(RacePoint at, Action then)
        {
            var fe = FrontEnd.Create();
            fe.CloseAll();
            var loading = fe.Push(new LoadingScreen());
            PlayerCar.ResetTime();
            Time.timeScale = 1f;
            yield return GameFlow.EnterWorldRoutine(at.Position, at.Rotation);   // CareerGame loading → GameplayActivity
            if (fe.Top == loading) fe.Pop();
            then?.Invoke();
        }

        /// <summary>race_bin_16/drive_to_safe_house: SMS 15, GPS to the safehouse, SMS 121 after 120 s, then the intro.</summary>
        static void BeginDriveToSafehouse()
        {
            // Drive: race_bin_16/drive_to_safe_house runs it (SetCopsEnabled(false), EAddSMS(15), the GPS, the 'hurry'
            // timer → SMS 121, the target trigger → Game.DoSafeHouseIntro)
            if (GameSession.ScriptsDrive) return;
            if (Systems.Police != null) Systems.Police.Enabled = false;    // SetCopsEnabled(false)
            Sms.Show(15);
            var d = new GameObject("Drive To Safehouse").AddComponent<GpsDrive>();
            d.Target = Coords.ToUnity(SafehouseTarget);
            d.Reminder = 121;
            d.Arrived = SafehouseIntro;
        }

        /// <summary>Game.DoSafeHouseIntro: the safehouse becomes the spawn point, storyfmv_saf25, rival bio, safehouse.</summary>
        static void SafehouseIntro() => SafehouseIntro(null);

        /// <summary>
        /// Game.DoSafeHouseIntro (0x605560; Drive: drive_to_safe_house's at_target with its RespawnMarker): the safehouse
        /// becomes the spawn point, storyfmv_saf25, the rival bio (whose EEnterBin(15) starts Blacklist #15).
        /// </summary>
        public static void SafehouseIntro(RacePoint marker)
        {
            if (P == null) return;
            var respawn = marker ?? GameSession.Career.Point("menu_gate_safehouse/zone_safehouse_1/respawn");
            SetSpawn(respawn);
            P.Stage = Free;
            P.Bin = 15;
            // EEnterBin(15): race_bin_15 runs `initial` -> `unlock_races` (the InitiallyUnlocked engage triggers); Drive:
            // the script itself, once LuaFlow starts race_bin_15
            if (!GameSession.ScriptsDrive && GameSession.Career.Bins.TryGetValue(15, out var bin15)) Gameplay.RaceDatabase.OnUnlockRaces(bin15);
            GpsNavigation.Disengage();
            P.Save();
            if (Systems.Police != null) Systems.Police.Enabled = true;
            if (respawn != null) Boot.PlacePlayer(respawn.Position, respawn.Rotation);
            StoryCard.Run(null, "storyfmv_saf25", () =>
            {
                var fe = FrontEnd.Create();
                fe.Push(new CareerMenuScreen());
                fe.Push(new RivalBioScreen(15));   // DoSafeHouseIntro queues SafeHouseRivalBio.fng after saf25 (career_flow.md §1.3)
            });
        }

        public static void SetSpawn(RacePoint at)
        {
            if (at == null) return;
            P.HasSpawn = true;
            (P.SpawnX, P.SpawnY, P.SpawnZ, P.SpawnRot) = (at.Position.X, at.Position.Y, at.Position.Z, at.Rotation);
        }
    }

    /// <summary>Free-roam GPS errand: the route on the minimap, a reminder SMS after 120 s, arrival within 15 m.</summary>
    public sealed class GpsDrive : MonoBehaviour
    {
        public Vector3 Target;
        public int Reminder;
        public Action Arrived;
        /// <summary>GPS_Engage's maxDeviation: 2000 as drive_to_safe_house's cellusage call passes it
        /// (Game.NavigatePlayerTo(target, item, 2000)).</summary>
        public float MaxDeviation = 2000f;
        float _started, _engageAt;

        void Start() => _started = Time.time;

        void Update()
        {
            var car = GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;
            if (!car) return;
            if (FrontEnd.Instance != null && FrontEnd.Instance.IsOpen) return;
            // the exe's GPS (Gameplay/Gps.cs: GPS_Engage, its arrow and route); engaged again after a loss [ours]
            if (!GpsNavigation.Engaged && Time.time >= _engageAt)
            {
                _engageAt = Time.time + 2f;
                GpsNavigation.Engage(Target, MaxDeviation);
            }
            if (Reminder > 0 && Time.time - _started > 120f) { Sms.Show(Reminder); Reminder = 0; }
            var d = car.transform.position - Target;
            d.y = 0;
            if (d.magnitude < 15f)
            {
                Destroy(gameObject);
                Arrived?.Invoke();
            }
        }

        void OnDestroy()
        {
            var g = GpsNavigation.Instance;
            if (g && (g.Target - Target).sqrMagnitude < 1f) GpsNavigation.Disengage();
        }
    }

    /// <summary>
    /// The career's text messages (LANGUAGES: SMS_MESSAGE_&lt;id&gt;, _FROM, _SUBJECT): a phone pop-up in the HUD, kept in the
    /// profile's inbox. Calls (ECellCall) use the same text.
    /// <para>GManager::AddSMS (0x5F5120): ids 10..14, 95 and 96 (the voiced cell calls) and every ECellCall go out at
    /// once; any other message waits in the pending list until GManager::CanPlaySMS (0x5EC040: free roam, no race, NIS,
    /// pursuit or menu, a player car) and is then put in the inbox and shown (UpdatePendingSMS 0x5EDFE0).</para>
    /// </summary>
    public sealed class Sms : MonoBehaviour
    {
        static Sms _instance;
        static readonly List<int> Pending = new List<int>();
        readonly Queue<(string from, string subject, string text, bool call)> _queue = new Queue<(string, string, string, bool)>();
        (string from, string subject, string text, bool call)? _current;
        float _until;
        Texture2D _panel;

        /// <summary>The message is waiting for GManager::CanPlaySMS.</summary>
        public static bool IsPending(int id) { SyncPending(); return Pending.Contains(id); }

        static Profile _pendingOf;

        /// <summary>
        /// The pending list belongs to the loaded career: GManager::SaveGameplayData writes it (+0x2A8) and LoadGameplayData
        /// (LoadSMSInfo 0x5F50C0) reads it back, so a message queued before a save still arrives after a resume.
        /// </summary>
        static void SyncPending()
        {
            var p = GameSession.Profile;
            if (ReferenceEquals(_pendingOf, p)) return;
            _pendingOf = p;
            Pending.Clear();
            if (p?.SmsPending != null) foreach (int id in p.SmsPending) if (!p.Inbox.Contains(id) && !Pending.Contains(id)) Pending.Add(id);
            Profile.Saving -= SavePending;
            Profile.Saving += SavePending;
        }

        static void SavePending(Profile p)
        {
            if (ReferenceEquals(p, _pendingOf)) p.SmsPending = new List<int>(Pending);
        }

        static bool Immediate(int id) => (id >= 10 && id <= 14) || id == 95 || id == 96;

        public static void Show(int id, bool call = false)
        {
            FrontEndData.Load();
            string text = FrontEndData.Text($"SMS_MESSAGE_{id}");
            if (text == $"SMS_MESSAGE_{id}") return;
            Ensure();
            SyncPending();
            if (!call && !Immediate(id))
            {
                var prof = GameSession.Profile;
                if ((prof == null || !prof.Inbox.Contains(id)) && !Pending.Contains(id)) Pending.Add(id);
                return;
            }
            // the immediate ids go through DispatchSMSMessage (0x5DDD20): only a message not received yet — it is marked
            // unread (2) with the next sort order and plays as a voiced cell call (ECellCall(id))
            if (!call)
            {
                var prof = GameSession.Profile;
                if (prof != null && prof.Inbox.Contains(id)) return;
                call = true;
            }
            Deliver(id, call);
        }

        static void Ensure()
        {
            if (_instance) return;
            _instance = new GameObject("SMS").AddComponent<Sms>();
            DontDestroyOnLoad(_instance.gameObject);
        }

        static void Deliver(int id, bool call)
        {
            var p = GameSession.Profile;
            if (p != null && !p.Inbox.Contains(id)) { p.Inbox.Add(id); p.Save(); }
            _instance._queue.Enqueue((FrontEndData.Text($"SMS_MESSAGE_{id}_FROM"), FrontEndData.Text($"SMS_MESSAGE_{id}_SUBJECT"), FrontEndData.Text($"SMS_MESSAGE_{id}").Replace('^', '\n'), call));
        }

        /// <summary>
        /// GManager::CanPlaySMS (0x5EC040): speech on, game flow in game (state 6), no movie / NIS (0x9885C8), no photo
        /// finish (0x91CF41), GRaceStatus roaming, FEManager::IsOkayToRequestPauseSimulation(0, 1, 1), no cop speech 0xCC
        /// playing, and the first player's vehicle: not animating (IVehicle +0xBC IsAnimating, NIS control), not staging
        /// (+0x20), not loading (+0x5C), and its AI's pursuit (IVehicleAI +0xA0, GetPursuit [?: slot]) null. The cop
        /// speech check is not ported [ours: story cards count as movies].
        /// </summary>
        static bool CanPlay()
        {
            var boot = GameBootstrap.Instance;
            if (!boot || !boot.Player || RaceManager.Active != null || NisPlayer.Active || StoryCard.Showing || MoviePlayer.Playing) return false;
            if (boot.Player.Staging) return false;
            if (Systems.Police != null && Systems.Police.InPursuit) return false;
            return FrontEnd.Instance == null || !FrontEnd.Instance.IsOpen;
        }

        void Update()
        {
            SyncPending();
            if (Pending.Count > 0 && CanPlay())
            {
                foreach (int id in Pending.ToArray()) Deliver(id, false);
                Pending.Clear();
            }
            if (_current != null && Time.unscaledTime > _until) _current = null;
            if (_current == null && _queue.Count > 0)
            {
                _current = _queue.Dequeue();
                _until = Time.unscaledTime + Mathf.Clamp(_current.Value.text.Length * 0.06f, 6f, 14f);
            }
        }

        void OnGUI()
        {
            if (_current == null || (FrontEnd.Instance != null && FrontEnd.Instance.IsOpen && !(FrontEnd.Instance.Top is LoadingScreen))) return;
            var m = _current.Value;
            if (!_panel) { _panel = new Texture2D(1, 1); _panel.SetPixel(0, 0, new Color(0.02f, 0.04f, 0.05f, 0.82f)); _panel.Apply(); }
            float s = Screen.height / 720f, w = 520 * s, x = Screen.width - w - 24 * s, y = 24 * s;
            var head = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(17 * s), fontStyle = FontStyle.Bold, normal = { textColor = new Color(0.66f, 0.92f, 0.3f) } };
            var body = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(16 * s), wordWrap = true, normal = { textColor = Color.white } };
            float h = body.CalcHeight(new GUIContent(m.text), w - 24 * s) + 56 * s;
            GUI.DrawTexture(new Rect(x, y, w, h), _panel);
            GUI.Label(new Rect(x + 12 * s, y + 8 * s, w - 24 * s, 24 * s), (m.call ? "INCOMING CALL: " : "SMS: ") + m.from + (string.IsNullOrEmpty(m.subject) ? "" : "  -  " + m.subject), head);
            GUI.Label(new Rect(x + 12 * s, y + 38 * s, w - 24 * s, h - 44 * s), m.text, body);
        }
    }

    /// <summary>
    /// Story movies and time cards. A movie plays when a converted copy is in MOVIES (MoviePlayer); otherwise its
    /// subtitles (SUBTITLES\&lt;name&gt;: {u32 time in 1/10 s, u32 string hash} pairs, 0xFFFF ends) run over black, so the
    /// story is told either way. A time card is the SixDaysLater package with its DDAY_TIMELAPSE text.
    /// </summary>
    public sealed class StoryCard : MonoBehaviour
    {
        public static bool Fading => _fade != null;
        static StoryCard _fade;
        static readonly List<StoryCard> Live = new List<StoryCard>();
        public static bool Showing => Live.Count > 0;

        /// <summary>Ends the cards on screen (tests, the QA tour).</summary>
        public static void SkipAll()
        {
            MoviePlayer.SkipAll();
            foreach (var c in Live.ToArray()) if (c) c.Finish();
        }

        void Awake() => Live.Add(this);
        void OnDestroy() => Live.Remove(this);

        List<(float, string)> _lines;
        string _card;
        float _t, _end, _fadeTime = -1;
        Action _done;
        Texture2D _black;

        public static void Run(string timeCard, string movie, Action done)
        {
            Action afterCard = () =>
            {
                if (string.IsNullOrEmpty(movie)) { done?.Invoke(); return; }
                // TryPlay runs done itself before returning false, so only try it when the movie exists; a movie that
                // exists but fails to open has already finished through done
                if (MoviePlayer.Find(movie) != null)
                {
                    MoviePlayer.TryPlay(movie, done);
                    return;
                }
                var lines = Subtitles(movie);
                if (lines.Count == 0) { done?.Invoke(); return; }
                var c = Make("Story " + movie);
                c._lines = lines;
                c._end = lines[lines.Count - 1].Item1 + 1.5f;
                c._done = done;
            };
            if (string.IsNullOrEmpty(timeCard)) { afterCard(); return; }
            FrontEndData.Load();
            var card = Make("Time card");
            card._card = FrontEndData.Text(timeCard).Replace('^', '\n');
            card._end = 4f;
            card._done = afterCard;
        }

        /// <summary>Fades the screen to black over seconds, then runs done (scripted race endings).</summary>
        public static void FadeOut(float seconds, Action done)
        {
            var c = Make("Fade");
            c._fadeTime = seconds;
            c._end = seconds + 0.5f;
            c._done = done;
            _fade = c;
        }

        static StoryCard _hold;

        /// <summary>
        /// EFadeScreenNoLoadingBarOn / Off (the D-Day race's scripted end): the screen fades to black and stays black until
        /// the Off event [ours: 1 s fade; 30 s at most].
        /// </summary>
        public static void FadeHold(bool on)
        {
            if (on)
            {
                if (_hold) return;
                _hold = Make("Fade hold");
                _hold._fadeTime = 1f;
                _hold._end = 30f;
                _fade = _hold;
            }
            else if (_hold)
            {
                var h = _hold;
                _hold = null;
                h.Finish();
            }
        }

        static StoryCard _loadingBar;

        /// <summary>
        /// EFadeScreenOn(true) / EFadeScreenOff for the track streamer's loading bar (GameFlow.OnLoadingBar, 0x64B770): the
        /// screen fades to black while the sim waits for the road ahead [ours: 0.25 s fade, no bar drawn; 60 s at most].
        /// </summary>
        public static void LoadingBar(bool on)
        {
            if (on)
            {
                if (_loadingBar) return;
                _loadingBar = Make("Loading bar");
                _loadingBar._fadeTime = 0.25f;
                _loadingBar._end = 60f;
            }
            else if (_loadingBar)
            {
                var b = _loadingBar;
                _loadingBar = null;
                b.Finish();
            }
        }

        static StoryCard Make(string name)
        {
            var go = new GameObject(name);
            DontDestroyOnLoad(go);
            return go.AddComponent<StoryCard>();
        }

        public static List<(float, string)> Subtitles(string movie)
        {
            var list = new List<(float, string)>();
            try
            {
                FrontEndData.Load();
                var path = Path.Combine(GameData.Root, "SUBTITLES", movie);
                if (!File.Exists(path)) return list;
                var d = File.ReadAllBytes(path);
                // SubtitleInfo {u16 startTime (1/10 s), u16 pad, u32 string hash}, ended by startTime 0xFFFF; entry 0 is the
                // first line (cro06's "Six Days Ago")
                for (int o = 0; o + 8 <= d.Length; o += 8)
                {
                    uint t = BitConverter.ToUInt16(d, o), h = BitConverter.ToUInt32(d, o + 4);
                    if (t == 0xFFFF) { list.Add((list.Count > 0 ? list[list.Count - 1].Item1 + 4f : 0f, "")); break; }
                    list.Add((t / 10f, FrontEndData.Strings.TryGet(h, out var s) ? s.Replace('^', '\n') : ""));
                }
            }
            catch (Exception e) { Debug.LogWarning("NFSMW subtitles: " + e.Message); }
            return list;
        }

        void Update()
        {
            _t += Time.unscaledDeltaTime;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool skip = _fadeTime < 0 && _t > 0.5f && kb != null && (kb.enterKey.wasPressedThisFrame || kb.escapeKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame);
            if (_t >= _end || skip) Finish();
        }

        void Finish()
        {
            var d = _done;
            _done = null;
            if (_fade == this) _fade = null;
            Live.Remove(this);
            Destroy(gameObject);
            d?.Invoke();
        }

        void OnGUI()
        {
            if (!_black) { _black = new Texture2D(1, 1); _black.SetPixel(0, 0, Color.black); _black.Apply(); }
            GUI.depth = -100;
            var full = new Rect(0, 0, Screen.width, Screen.height);
            if (_fadeTime > 0)
            {
                var old = GUI.color;
                GUI.color = new Color(0, 0, 0, Mathf.Clamp01(_t / _fadeTime));
                GUI.DrawTexture(full, _black);
                GUI.color = old;
                return;
            }
            GUI.DrawTexture(full, _black);
            float s = Screen.height / 720f;
            string text = _card;
            if (_lines != null)
            {
                text = "";
                foreach (var (t, line) in _lines) if (t <= _t) text = line;
            }
            var style = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.RoundToInt((_card != null ? 40 : 24) * s), alignment = TextAnchor.MiddleCenter, wordWrap = true,
                fontStyle = _card != null ? FontStyle.Bold : FontStyle.Normal, normal = { textColor = _card != null ? new Color(0.85f, 0.85f, 0.8f) : Color.white },
            };
            GUI.Label(new Rect(Screen.width * 0.1f, _card != null ? 0 : Screen.height * 0.62f, Screen.width * 0.8f, _card != null ? Screen.height : Screen.height * 0.3f), text, style);
            var hint = new GUIStyle(style) { fontSize = Mathf.RoundToInt(14 * s), fontStyle = FontStyle.Normal, normal = { textColor = new Color(1, 1, 1, 0.4f) } };
            GUI.Label(new Rect(0, Screen.height - 40 * s, Screen.width - 30 * s, 30 * s), "Enter: skip", hint);
        }
    }
}
