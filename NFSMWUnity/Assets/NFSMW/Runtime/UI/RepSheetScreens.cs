using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;
using UnityEngine.InputSystem;
using K = NfsMw.UI.CareerScreenKit;

namespace NfsMw.UI
{
    /// <summary>
    /// The Blacklist overview (SafeHouseReputationOverview.fng / InGameReputationOverview.fng, uiRepSheetMain.cpp;
    /// specs/fe_career_screens.md §3.1): the viewed rival's photo, tag and numbers against what is needed, an icon scroller
    /// of Race Events / Milestone Events / Bounty / Bio, Up / Down through the rivals already reached, key 2 'Challenge
    /// Rival' (BUTTON4) when the boss races are open. Safehouse: career menu "Blacklist"; in game: HUD PAD_DOWN
    /// (ERaceSheetOn(0)) [?] key L / d-pad down.
    /// </summary>
    public sealed class RepSheetMainScreen : CarouselScreen
    {
        /// <summary>iCurrentViewBin (uiRepSheetMain.cpp:19): the bin every Blacklist page shows.</summary>
        public static int ViewBin = 15;
        readonly bool _inGame;

        public RepSheetMainScreen(bool inGame = false)
        {
            _inGame = inGame;
            ViewBin = Mathf.Clamp(GameSession.Profile?.Bin ?? 15, 1, 15);
        }

        public override string Package => _inGame && FrontEndData.Packages.ContainsKey("InGameReputationOverview") ? "InGameReputationOverview" : "SafeHouseReputationOverview";
        public override bool ShowCar => !_inGame;
        public override string Platform => "CAREER_SAFEHOUSE.BIN";
        protected override string Title => null;

        /// <summary>Texture name for a hash (the scroller looks icons up by name).</summary>
        static string TexName(uint hash) => FrontEndData.Textures?.All.FirstOrDefault(t => t.Hash == hash)?.Name;

        protected override void Build()
        {
            Options.Add(new Option { Label = K.L(0x84E4A54C, "Race Events"), Icon = TexName(0xEFC9662E) ?? "MAIN_ICON_QUICK_RACE", Run = () => Go(0) });
            Options.Add(new Option { Label = K.L(0x216F1B81, "Milestone Events"), Icon = TexName(0xD807E9B3) ?? "MAIN_ICON_BOUNTY", Run = () => Go(1) });
            Options.Add(new Option { Label = K.L(0xE451941E, "Bounty"), Icon = TexName(0x021A4B0C) ?? "MAIN_ICON_BOUNTY", Run = () => Go(2) });
            Options.Add(new Option { Label = K.L(0x2D159737, "Bio"), Icon = TexName(0xE97E4E83) ?? "MAIN_ICON_TOP_15", Run = () => Go(4) });
        }

        /// <summary>BUTTON_PRESSED → RaceSheet (0), Milestones (1), Bounty (2), RivalBio (4).</summary>
        void Go(int id)
        {
            switch (id)
            {
                case 0: FE.Push(new SafehouseRaceSheetScreen(_inGame)); break;   // (InGame|SafeHouse)RaceSheet.fng (uiRepSheetRaceEvents)
                case 1: FE.Push(new MilestonesScreen(_inGame)); break;
                case 2: FE.Push(new BountyScreen(_inGame)); break;
                default: FE.Push(new RivalBioScreen(ViewBin, _inGame)); break;
            }
        }

        public override void Open()
        {
            base.Open();
            K.TextAll(View, 0xC4324EA7, K.L(_inGame ? 0x2E3919E9u : 0xCACE5999u, "Blacklist"));
            K.TextAll(View, 0x7C4F7112, K.L(_inGame ? 0x2E3919E9u : 0xCACE5999u, "Blacklist"));
            K.TextAll(View, 0xB71B576D, K.L(_inGame ? 0x2E3919E9u : 0xCACE5999u, "Blacklist"));   // Setup (PC 0x551070): TITLE_GROUP
            // Setup: the DEFEATED stamp master 0x7FE4020F hidden, its texture = GetDefeatedTexture (PC 0x511570) by language,
            // visible once streamed (NotifyTextureLoaded 0x52EE10; the port's textures are resident)
            var stampMaster = View.Find(0x7FE4020F);
            if (stampMaster != null)
            {
                var tex = FrontEndData.Texture(DefeatedTexture());
                if (tex) View.SetTexture(stampMaster, tex);
                View.SetVisible(stampMaster, true);
            }
            if (!_inGame)
            {
                var car = GameSession.ActiveCar;
                if (car != null && FE.Showroom) FE.Showroom.Show(car.Car, car.Paint, car);
            }
            UpdateInfo();
        }

        protected override void Refresh() => UpdateInfo();

        /// <summary>uiRepSheetMain::GetDefeatedTexture (PC 0x511570): the DEFEATED stamp per language (English 0x087B7D0A).</summary>
        static uint DefeatedTexture() => Localize.CurrentLanguage switch
        {
            Language.French => 0x087B81CD, Language.German => 0x087B846E, Language.Italian => 0x087B8ECE, Language.Spanish => 0x087BB8D4,
            Language.Dutch => 0x087B79BD, Language.Swedish => 0x087BB9BF, Language.Danish => 0x087B7723, Language.Polish => 0x087BABFB,
            Language.Finnish => 0x087B80AD, Language.Korean => 0x087B96BC, Language.Chinese => 0x087B73C4, Language.Japanese => 0x087B90AB,
            Language.Thai => 0x087BBC0D, _ => 0x087B7D0A,
        };

        /// <summary>UpdateInfo (:201) for ViewBin: numbers, ticks, rival art and the boss state.</summary>
        void UpdateInfo()
        {
            var p = GameSession.Profile;
            int cur = Mathf.Clamp(p?.Bin ?? 15, 0, 15);
            var bin = GameSession.Career != null && GameSession.Career.Bins.TryGetValue(ViewBin, out var b) ? b : null;
            K.BountyCash(View);
            if (bin != null && p != null)
            {
                int won = GameSession.RacesWonInBin(bin), needWon = Mathf.Min(bin.RequiredRacesWon, bin.WorldRaces.Count);
                int done = GameSession.ChallengesDoneInBin(bin), needDone = Mathf.Min(bin.RequiredChallenges, bin.Milestones.Count);
                Text(0x15D80973, won.ToString());
                Text(0xDE7AD199, needWon.ToString());
                Text(0xD802FBA8, done.ToString());
                Text(0x7242962E, needDone.ToString());
                Text(0x322B18F9, RapStable.TotalBounty().Grouped());   // BOUNTY_DATA "%$d" GetTotalBounty
                Text(0x055C6E5F, bin.RequiredBounty.Grouped());
                Tick(0x4C3B1536, won >= needWon);
                Tick(0x4C3B1537, done >= needDone);
                Tick(0x4C3B1538, p.Bounty >= bin.RequiredBounty);
            }
            K.Text(View, 0x242657CE, K.Format(K.L(_inGame ? 0x96CA2471u : 0x3A64DE21u, "Blacklist %d"), ViewBin));
            K.Text(View, 0xB695A891, ViewBin.ToString("00"));
            K.Text(View, 0x7AC3D0C9, K.Rival(ViewBin, "AKA"));
            K.Text(View, 0x79CF0442, K.Rival(ViewBin, "CHALLENGE"));
            K.RivalArt(View, ViewBin, _inGame);
            // the rival arrows only while there is more than one rival to page through
            bool arrows = cur < 15;
            View.SetVisible(View.Find(0x47B22FCA), arrows);
            View.SetVisible(View.Find(0x72AD598C), arrows);
            View.SetVisible(View.Find(0x1E617478), arrows);
            bool beaten = p != null && (p.BossesBeaten.Contains(ViewBin) || ViewBin > cur);
            bool available = ViewBin == cur && GameSession.CanChallengeBoss;
            var stamp = View.Find(0x34D4433B);
            if (beaten)
            {
                View.SetVisible(View.Find(0x55F6AA1A), false);
                View.SetVisible(stamp, true);
                K.Message(View, 0xB4C144B1);   // DEFEATED stamp
            }
            else
            {
                View.Play(stamp, FEView.Hide);
                var btn = View.Find(0x55F6AA1A);
                View.SetVisible(btn, available);
                if (available) View.Play(btn, FEView.Appear);
            }
        }

        void Text(uint hash, string text) => K.Text(View, hash, text);

        void Tick(uint hash, bool met)
        {
            var n = View.Find(hash);
            if (n == null) return;
            View.SetVisible(n, met);
            View.Play(n, met ? 0xE6361F46u : FEView.Hide);
        }

        /// <summary>ScrollRival: Down = next rival down the list (wraps to 15), Up = back toward the current one.</summary>
        void ScrollRival(bool down)
        {
            int cur = Mathf.Clamp(GameSession.Profile?.Bin ?? 15, 1, 15);
            if (cur >= 15) return;
            int was = ViewBin;
            if (down) { ViewBin--; if (ViewBin < cur) ViewBin = 15; }
            else { ViewBin++; if (ViewBin > 15) ViewBin = cur; }
            if (ViewBin == was) return;
            View.Play(View.Find(0xC1F62308), down ? 0xAF9D73F2u : 0x9E5E6B5Fu);
            UpdateInfo();
        }

        public override void Tick(MenuInput input)
        {
            if (input.Down) { ScrollRival(true); return; }
            if (input.Up) { ScrollRival(false); return; }
            if (K.KeyPressed(Key.Digit2) || (Gamepad.current != null && Gamepad.current.buttonWest.wasPressedThisFrame))
            {
                // BUTTON4: the rival challenge when open and not beaten
                if (ViewBin == (GameSession.Profile?.Bin ?? 0) && GameSession.CanChallengeBoss) { FE.Replace(new RivalScreen(_inGame ? 1 : 0)); return; }   // switch to (InGame)RivalChallenge
            }
            base.Tick(input);
        }
    }

    /// <summary>
    /// SafeHouseRivalBio.fng / InGameRivalBio.fng (uiRepSheetRivalBio.cpp, §3.2): the rival's name, AKA, bio, car and
    /// strength labels with photo, tag and background. Key 2 plays the rival's movie (blacklist_%02d), key 3 shows the rival's
    /// car in Showcase.fng. In the post-rival flow (§3.3 stage 4) Enter continues (back does nothing there).
    /// </summary>
    public sealed class RivalBioScreen : FEScreen
    {
        readonly int _bin;
        readonly bool _inGame, _postRival;
        public RivalBioScreen(int bin, bool inGame = false, bool postRival = false) { _bin = Mathf.Clamp(bin, 1, 15); _inGame = inGame; _postRival = postRival; }
        public override string Package => _inGame && FrontEndData.Packages.ContainsKey("InGameRivalBio") ? "InGameRivalBio" : "SafeHouseRivalBio";

        public override void Open()
        {
            K.Text(View, 0x242657CE, K.Format(K.L(_inGame ? 0x96CA2471u : 0x3A64DE21u, "Blacklist %d"), _bin));
            K.Text(View, 0xB695A891, _bin.ToString("00"));
            K.Text(View, 0x7AC3D0C9, K.Rival(_bin, "NAME"));
            K.Text(View, 0xB1F2748D, K.Rival(_bin, "AKA"));
            K.Text(View, 0x27E1D6D8, K.Rival(_bin, "BIO"));
            K.Text(View, 0xCB5BF41A, K.Rival(_bin, "CAR"));
            K.Text(View, 0xA6F07BF3, K.Rival(_bin, "STRENGTH"));
            K.RivalArt(View, _bin, _inGame);
            if (_postRival)
            {
                K.Message(View, 0x0B21A45F);
                View.Play(View.Find(0x77B27FFB), 0x5708CE2B);   // 'next rival'
                View.SetVisible(View.Find(0x77B27FFB), true);
            }
            else
            {
                K.Message(View, 0xAF922178);
                Hide(0xC2769470);   // Continue
            }
            View.Play(View.Find(0x4E052CCB), FEView.Show);
        }

        public override void Tick(MenuInput input)
        {
            // NotificationMessage (PC 0x551C80): PAD_BUTTON0 (key 3) → the rival's preset car "BL%d" (bin 1: "E3_DEMO_BMW") in
            // Showcase.fng with BlackListNumber = the bin; PAD_BUTTON4 (key 2) → FEAnyMovieScreen::LaunchMovie("blacklist_%02d");
            // both only outside post-rival mode. Accept in post-rival mode → uiRepSheetRivalFlow Next / StartFlow(5).
            if (!_postRival && input.Key3)
            {
                var car = PresetCar(_bin == 1 ? "E3_DEMO_BMW" : $"BL{_bin}");
                if (car != null) FE.Push(new ShowcaseScreen(car, _bin));
                return;
            }
            if (!_postRival && K.KeyPressed(Key.Digit2)) { StoryCard.Run(null, $"blacklist_{_bin:00}", null); return; }
            if (_postRival ? input.Accept : input.Back) FE.Pop();
        }

        static Dictionary<string, (string car, int physicsLevel, uint[] parts)> _presets;

        /// <summary>FEPlayerCarDB::CreateNewPresetCar (VehicleDB.cpp) for viewing: the GlobalB PresetCar's car and parts (not added
        /// to the stable).</summary>
        static OwnedCar PresetCar(string name)
        {
            if (_presets == null)
            {
                var path = System.IO.Path.Combine(GameData.Root ?? "", "GLOBAL", "GlobalB.lzc");
                _presets = System.IO.File.Exists(path) ? ShopRules.PresetRides(GameFiles.ReadBundle(path)) : new Dictionary<string, (string, int, uint[])>();
            }
            if (!_presets.TryGetValue(name, out var preset)) return null;
            var car = new OwnedCar { Car = preset.car, PerfFormat = 1 };
            for (int slot = 0; slot < preset.parts.Length; slot++)
            {
                if (preset.parts[slot] == 0) continue;
                var part = CarParts.Db?.PartsFor(CarParts.TypeName(car.Car), slot).FirstOrDefault(x => x.NameHash == preset.parts[slot]);
                if (part != null) CarParts.SetInstalled(car, slot, part);
            }
            return car;
        }
    }

    /// <summary>
    /// The 3×3 event grid shared by SafeHouseBounty / SafeHouseMilestones (and the InGame* twins): EVENT_ICON_1..9 with
    /// HIGHLIGHT / UNHIGHLIGHT / GREY, the selection bar 06745352 (POSn), "n / count" (0x5A856A34 / 0x2D4D22C8), the
    /// header and description, and TRACK_MAP panned and zoomed to the entry (ResetZoom, PanTo, ZoomTo(0.27)) with the
    /// arrow 0xAF51DD73 turned to its heading. Rows scroll three at a time.
    /// </summary>
    public abstract class EventGridScreen : FEScreen
    {
        protected sealed class Entry
        {
            public uint Icon;
            public string Header, Description, Reward;
            public bool Locked, Done;
            public RacePoint At;
            public Action Run;
        }

        protected readonly List<Entry> Entries = new List<Entry>();
        protected readonly bool InGame;
        protected int Sel, Top;
        TrackMapStreamer _map;

        protected EventGridScreen(bool inGame) { InGame = inGame; }
        // GarageMainScreen renders the safehouse garage behind every safehouse FE package (the exe is in its front end there);
        // the in-game variants sit over the world [ours: the port keeps the city loaded]
        public override bool ShowCar => !InGame;
        public override string Platform => "CAREER_SAFEHOUSE.BIN";
        protected abstract void Fill();
        protected abstract string TitleText { get; }

        public override void Open()
        {
            Fill();
            var map = View.Find("TRACK_MAP");
            View.Play(View.Find(0x99344537), FEView.Init);
            View.SetVisible(View.Find(0x99344537), true);
            _map = new TrackMapStreamer(View, map, K.RegionMapName()) { Duration = 1f };
            K.Text(View, "TITLE", TitleText);
            K.Text(View, 0xC4324EA7, TitleText);
            K.BountyCash(View);
            // [?] the package alternates the bounty (0xB514E2D8) and cash (0xF91A59F6) boxes through its p- / OFf scripts and the
            // 0x0339155A / 0x71A545A9 responses, which a plain view does not run: the cash box keeps its INIT → HIDE state here
            // instead of both showing on top of each other (the plain view's APPEAR stand-in faded it in)
            View.SetInvisible(View.Find(0xF91A59F6));
            // FEngHidePCObjects (0x516230): flag-0x40 (console help) objects stay hidden
            foreach (var n in View.Nodes) if ((n.Obj.Flags & 0x40) != 0) View.SetInvisible(n);
            Hide(0xBBF970CD, 0x1C8FC866, 0x7AF67920, 0xADA491AE, 0x6B67D70B);   // EVENT ID / medal debug lines, race best-times box, lock panel
            Select(0);
        }

        protected void Select(int i)
        {
            if (Entries.Count == 0) { Draw(); return; }
            Sel = Mathf.Clamp(i, 0, Entries.Count - 1);
            if (Sel < Top) Top = Sel / 3 * 3;
            if (Sel >= Top + 9) Top = Sel / 3 * 3 - 6;
            Draw();
            var e = Entries[Sel];
            if (_map != null && e.At != null)
            {
                var uv = K.MapUV(Coords.ToUnity(e.At.Position));
                _map.ResetZoom();
                _map.PanTo(uv);
                _map.ZoomTo(0.27f);
                var arrow = View.Find(0xAF51DD73);
                if (arrow != null)
                {
                    float deg = 90f - e.At.Rotation;   // [?] game heading (from +X, counter-clockwise) to an FE angle from up, clockwise
                    float half = deg * Mathf.Deg2Rad * 0.5f;
                    arrow.Data.QZ = Mathf.Sin(half); arrow.Data.QW = Mathf.Cos(half);
                    View.SetVisible(arrow, true);
                }
            }
        }

        void Draw()
        {
            for (int k = 0; k < 9; k++)
            {
                int i = Top + k;
                var n = View.Find("EVENT_ICON_" + (k + 1));
                bool has = i < Entries.Count;
                View.SetVisible(n, has);
                Medal(k, has ? Entries[i] : null);
                if (!has) continue;
                var e = Entries[i];
                var tex = FrontEndData.Texture(e.Icon);
                if (tex) View.SetTexture(n, tex);
                // ArraySlot::Update: GREY only for greyed-out / disabled data (never set here); a locked datum keeps its normal look
                View.Play(n, i == Sel ? FEView.Highlight : FEView.UnHighlight);
            }
            var bar = View.Find(0x06745352);
            View.SetVisible(bar, Entries.Count > 0);
            if (Entries.Count > 0) View.Play(bar, FrontEndData.Hash("POS" + (Sel - Top + 1)));
            K.Text(View, 0x5A856A34, Entries.Count > 0 ? (Sel + 1).ToString() : "0");
            K.Text(View, 0x2D4D22C8, Entries.Count.ToString());
            if (Entries.Count == 0)
            {
                K.Text(View, 0xB5117FDE, "");
                K.Text(View, 0x028049D6, "");
                return;
            }
            var s = Entries[Sel];
            K.Text(View, 0xB5117FDE, s.Header);
            K.Text(View, 0x028049D6, s.Description);
            var icon = View.Find(0xF97EC5D5);
            var t = FrontEndData.Texture(s.Icon);
            if (icon != null && t) View.SetTexture(icon, t);
            View.SetText(View.Find(0xB21D69BD), s.Reward ?? "");
            View.SetVisible(View.Find(0xB21D69BD), s.Reward != null);
            View.SetVisible(View.Find(0xB8B17C4C), s.Reward != null);
            K.Message(View, s.Locked ? 0xC5DD9D68u : 0x38091FA1u);
        }

        /// <summary>MEDAL_THUMB_n / CHECK_n: locked → 0x0018ED48, done → 0x028FEADD (milestones) or the tick, else hidden.</summary>
        protected virtual void Medal(int slot, Entry e)
        {
            var m = View.Find("MEDAL_THUMB_" + (slot + 1));
            if (m != null)
            {
                var tex = e == null ? null : e.Locked ? FrontEndData.Texture(0x0018ED48) : e.Done ? FrontEndData.Texture(0x028FEADD) : null;
                if (tex) View.SetTexture(m, tex);
                View.SetVisible(m, tex != null);
            }
            var c = View.Find("CHECK_" + (slot + 1));
            if (c != null)
            {
                var lockTex = FrontEndData.Texture(0x0018ED48);
                if (e != null && e.Locked && lockTex) View.SetTexture(c, lockTex);
                View.SetVisible(c, e != null && (e.Locked && lockTex != null || e.Done));
            }
        }

        public override void Tick(MenuInput input)
        {
            _map?.Update(Time.unscaledDeltaTime);
            if (Entries.Count > 0)
            {
                // ArrayScroller 3×3: left / right wrap inside the row, up / down by a row
                int row = Sel / 3 * 3, rowEnd = Mathf.Min(row + 2, Entries.Count - 1);
                if (input.Left) Select(Sel == row ? rowEnd : Sel - 1);
                if (input.Right) Select(Sel == rowEnd ? row : Sel + 1);
                if (input.Up && Sel - 3 >= 0) Select(Sel - 3);
                if (input.Down && Sel + 3 < Entries.Count) Select(Sel + 3);
                else if (input.Down && Sel / 3 < (Entries.Count - 1) / 3) Select(Entries.Count - 1);
                if (input.Accept)
                {
                    // BUTTON_PRESSED: Bounty lists no locked datum; Milestones ignores a done one (theMilestone = null) and lets a
                    // locked one through (uiRepSheetMilestones.cpp:32 tests IsChecked only)
                    var e = Entries[Sel];
                    if (!e.Done) e.Run?.Invoke();
                    return;
                }
            }
            // PAD_BUTTON4 (key 2), safehouse only: watch the tutorial (0x99344537 HIDE, then INIT on 0xC3960EB9)
            if (!InGame && TutorialMovie != null && (K.KeyPressed(Key.Digit2) || (Gamepad.current != null && Gamepad.current.buttonWest.wasPressedThisFrame)))
            {
                View.Play(View.Find(0x99344537), FEView.Hide);
                FE.Push(new TutorialScreen(TutorialMovie) { Done = () => View.Play(View.Find(0x99344537), FEView.Init) });
                return;
            }
            if (input.Back) FE.Pop();
        }

        /// <summary>DialogInterface::ShowTwoButtons(pkg, in game "InGameDialog.fng" else "", dialog_alert, Yes 0x70E01038, No 0x417B25E4,
        /// yes, no, no, first button 2 (No), blurb label); yes runs the action.</summary>
        protected void Ask(uint label, Action yes) =>
            DialogInterface.ShowTwoButtons(this, InGame ? "InGameDialog.fng" : "", DialogTitle.Alert, 0x70E01038, 0x417B25E4,
                DialogInterface.MsgYes, DialogInterface.MsgNo, DialogInterface.MsgNo, true, 1, K.L(label, ""),
                r => { if (r == DialogInterface.MsgYes) yes?.Invoke(); });

        /// <summary>The tutorial movie of the page ("bounty_tutorial" / "pursuit_tutorial") and its career SpecialFlags bit (0x400 / 0x200).</summary>
        protected abstract string TutorialMovie { get; }
        protected abstract int TutorialFlag { get; }

        /// <summary>
        /// dialog_message_yes (uiRepSheetBounty.cpp:105 / uiRepSheetMilestones.cpp:126): the first time (flag clear) the tutorial
        /// plays first (0x99344537 HIDE, TRACK_MAP hidden, flag set) and its end (0xC3960EB9) jumps; else the jump at once.
        /// </summary>
        protected void Engage(RacePoint at, bool pursuit)
        {
            var p = GameSession.Profile;
            if (p != null && TutorialMovie != null && (p.TutorialsSeen & TutorialFlag) == 0)
            {
                View.Play(View.Find(0x99344537), FEView.Hide);
                View.SetVisible(View.Find("TRACK_MAP"), false);
                p.TutorialsSeen |= TutorialFlag;
                p.Save();
                FE.Push(new TutorialScreen(TutorialMovie) { Done = () => JumpTo(at, pursuit) });
                return;
            }
            JumpTo(at, pursuit);
        }

        /// <summary>
        /// WarpToMarker(marker, pursuit) / OverrideFreeRoamStartMarker + QueueFreeRoamPursuit: out of the menus at the
        /// marker, a pursuit started at the car's heat.
        /// </summary>
        protected void JumpTo(RacePoint at, bool pursuit)
        {
            if (at == null) return;
            FE.CloseAll();
            var boot = GameBootstrap.Instance;
            if (boot == null || boot.Player == null) return;
            Story.ZoneRespawn = null;
            boot.PlacePlayer(at.Position, at.Rotation);
            Systems.Ensure();
            if (pursuit) Systems.Police?.ForcePursuit(Mathf.Clamp(GameSession.ActiveCar?.Heat ?? 1, 1, 5));
        }
    }

    /// <summary>
    /// Pursuit start points (SafeHouseBounty.fng / InGameBounty.fng, uiRepSheetBounty.cpp, §3.4): the gameplay.bin
    /// bounty_jump markers ordered by LocalizationTag; the first four always, 5–8 from Blacklist #12, the rest from #8.
    /// Enter jumps there with a pursuit after the confirmation.
    /// </summary>
    public sealed class BountyScreen : EventGridScreen
    {
        public BountyScreen(bool inGame = false) : base(inGame) { }
        public override string Package => InGame && FrontEndData.Packages.ContainsKey("InGameBounty") ? "InGameBounty" : "SafeHouseBounty";
        protected override string TitleText => InGame ? K.L(0x6DDFA694, "BOUNTY") : K.L(0xE451941E, "Bounty");
        protected override string TutorialMovie => "bounty_tutorial";
        protected override int TutorialFlag => 0x400;

        protected override void Fill()
        {
            var db = GameSession.Career?.Db;
            if (db == null) return;
            int cur = GameSession.Profile?.Bin ?? 15;
            var marks = db.OfClass("gameplay").Where(c => c.Name != null && c.Name.StartsWith("bounty_jump/") && c.Has("Position"))
                .OrderBy(c => c.Has("LocalizationTag") ? c.Int("LocalizationTag") : 99).ToList();
            for (int i = 0; i < marks.Count; i++)
            {
                var c = marks[i];
                int tag = c.Has("LocalizationTag") ? c.Int("LocalizationTag") : i + 1;
                // Setup (:157): only the open spawn points become data; the rest are not listed at all (CHECK_n stay hidden)
                bool open = i < 4 || (i < 8 && cur < 13) || cur < 9;
                if (!open) continue;
                var at = new RacePoint { Name = c.Name, Position = c.Vector3("Position"), Rotation = c.Float("Rotation") };
                Entries.Add(new Entry
                {
                    Icon = i < 4 ? 0x8A21B882u : i < 8 ? 0x895EC0AEu : 0x9129E7FBu,
                    Header = K.L($"BLACKLIST_BOUNTY_SPAWN_POINT_{tag:00}", c.Name.Substring(12)),
                    Description = K.L($"BLACKLIST_BOUNTY_SPAWN_POINT_{tag:00}_DESC", ""),
                    At = at,
                    Run = () => Ask(0xCD195D0B, () => Engage(at, true)),
                });
            }
        }
    }

    /// <summary>
    /// The viewed rival's pursuit milestones (SafeHouseMilestones.fng / InGameMilestones.fng, uiRepSheetMilestones.cpp,
    /// §3.5), replacing the old PostRace_Pursuit table: icon by milestone type (FEDatabase.cpp:1154), header
    /// BLACKLIST_PURSUIT_MILESTONES_%02d_SHORT, description with the goal, bounty reward, MEDAL_THUMB done / locked.
    /// Enter on a not-done one jumps to its SpawnPoint with a pursuit; the bin's photo-ticket speed traps follow (jump without
    /// a pursuit). The first jump plays pursuit_tutorial (career flag 0x200); key 2 watches it.
    /// </summary>
    public sealed class MilestonesScreen : EventGridScreen
    {
        /// <summary>Straight from the career menu: the current rival's milestones.</summary>
        public MilestonesScreen() : this(false) { RepSheetMainScreen.ViewBin = Mathf.Clamp(GameSession.Profile?.Bin ?? 15, 1, 15); }
        public MilestonesScreen(bool inGame) : base(inGame) { }
        public override string Package => InGame && FrontEndData.Packages.ContainsKey("InGameMilestones") ? "InGameMilestones" : "SafeHouseMilestones";
        protected override string TitleText => InGame ? K.L(0x578B767B, "MILESTONES") : K.L(0x216F1B81, "Milestone Events");
        protected override string TutorialMovie => "pursuit_tutorial";
        protected override int TutorialFlag => 0x200;

        /// <summary>The BLACKLIST_PURSUIT_MILESTONES_nn label of a milestone type.</summary>
        internal static int Tag(string type) => type switch
        {
            "pursuit_length" => 1, "pursuit_evasion_time" => 2, "bounty_in_pursuit" => 3, "cost_to_state_in_pursuit" => 5,
            "roadblocks_dodged" => 6, "cops_damaged" => 7, "total_infractions" => 8, _ => 0,
        };

        /// <summary>cFrontendDatabase::GetMilestoneIconHash (FEDatabase.cpp:1154) by the Attrib hash of the type.</summary>
        public static uint IconFor(string type)
        {
            uint k = AttribDatabase.Hash(type ?? "");
            switch (k)
            {
                case 0x850A64BC: return 0x88E8DE9E;
                case 0x3FD1884D: case 0x4FC942CA: return 0x0FE608E6;
                case 0xFD989A3A: return 0x87807869;
                case 0x5392E4FD: return 0x831B7EBE;
                case 0x033FA23A: return 0x950FCEBC;
                case 0xEB45F99D: return 0xC43959D2;
                case 0x9E3EBB78: return 0x3FFE9EC9;
                case 0xCDF36FC2: return 0xE621B2EF;
                case 0xA61CAC24: return 0x6784A80E;
                case 0x2377E50D: return 0xB4E6456B;
            }
            return 0x950FCEBC;
        }

        protected override void Fill()
        {
            var career = GameSession.Career;
            var p = GameSession.Profile;
            if (career == null || !career.Bins.TryGetValue(RepSheetMainScreen.ViewBin, out var bin)) return;
            int cur = p?.Bin ?? 15;
            foreach (var m in bin.Milestones)
            {
                int tag = Tag(m.Type);
                // RefreshHeader (PC 0x52FB70): goal "%$0.0f" (IsMilestoneTimeFormat types / 60 [?: the PC multiplies GMilestone +0xC by
                // 546.117 = 32768 / 60; the member's unit is not decompiled]), FEPrintf(desc label as the format, goal, goal)
                float g = m.Type == "pursuit_length" || m.Type == "pursuit_evasion_time" ? m.Goal / 60f : m.Goal;
                string goal = Localize.Fixed(g, 0, true);
                string desc = tag > 0 ? K.Format(K.L($"BLACKLIST_PURSUIT_MILESTONES_{tag:00}", m.Title + " %s"), goal, goal) : $"{m.Title} {m.Format(m.Goal)}";
                var c = career.Db.Get("gameplay", m.Name);
                RacePoint at = null;
                var spawn = c != null && c.Has("SpawnPoint") ? c.Keys("SpawnPoint").FirstOrDefault() : 0;
                if (spawn != 0 && career.Db.Collections.TryGetValue((AttribDatabase.Hash("gameplay"), spawn), out var sp) && sp.Has("Position"))
                    at = new RacePoint { Name = sp.Name, Position = sp.Vector3("Position"), Rotation = sp.Float("Rotation") };
                Entries.Add(new Entry
                {
                    Icon = IconFor(m.Type),
                    Header = tag > 0 ? K.L($"BLACKLIST_PURSUIT_MILESTONES_{tag:00}_SHORT", m.Title) : m.Title,
                    Description = desc,
                    Reward = m.Bounty.Grouped(),
                    Done = p != null && p.Milestones.Contains(m.Name),
                    Locked = RepSheetMainScreen.ViewBin < cur,
                    At = at,
                    Run = () => Ask(0xA5A8409A, () => Engage(at, true)),
                });
            }
            // then every GSpeedTrap of the bin (AddSpeedtrap 0x51F3C0): icon GetRaceIconHash(SpeedTrap) 0x66C9A7B6, header "Photo
            // Ticket" 0xF3B3D8DC, bounty, description 0xB14018BD with the trigger speed (PC: SpeedoUnits 1 -> m/s x 3.6 "KM/H"
            // 0x8569A25F, else x 2.237 "MPH" 0x8569AB44, truncated, "%$0.0f %s"); completed = done; no pursuit on the jump
            foreach (var t in bin.SpeedTraps)
            {
                bool kmh = FEDb.Data.Gameplay.SpeedoUnits == 1;
                float mps = t.Threshold / 3.6f;   // WorldSpeedTrap.Threshold is km/h (Systems' trigger test)
                int v = (int)(kmh ? mps * 3.6f : mps * 2.23699f);
                string speed = Localize.Fixed(v, 0, true) + " " + K.L(kmh ? 0x8569A25Fu : 0x8569AB44u, kmh ? "KM/H" : "MPH");
                var tat = t.Point;
                Entries.Add(new Entry
                {
                    Icon = 0x66C9A7B6,
                    Header = K.L(0xF3B3D8DC, "Photo Ticket"),
                    Description = K.Format(K.L(0xB14018BD, "Get clocked at over %s to successfully complete this challenge."), speed),
                    Reward = Localize.Fixed(t.Bounty, 0, true),
                    Done = p != null && p.Milestones.Contains(t.Name),
                    Locked = RepSheetMainScreen.ViewBin < cur,
                    At = tat,
                    Run = () => Ask(0xBF1DCD38, () => Engage(tat, false)),
                });
            }
        }
    }

    /// <summary>
    /// SafehouseMarkers.fng (uiMarkerSelect.cpp, §3.6): the beaten rival's six reward cards (RewardsForWinner of the bin's
    /// last boss race), ordered misc / visual / body / performance and lightly shuffled; pick two. A pink slip gives the
    /// rival's car (FEPlayerCarDB::AwardRivalCar), cash pays out, anything else goes to Profile.Markers (ShopMarkers).
    /// </summary>
    public sealed class MarkerSelectScreen : FEScreen
    {
        public override string Package => "SafehouseMarkers";

        /// <summary>MarkerSelectInfos (uiMarkerSelect.cpp:187) by ShopRules marker: icon, category icon, name, category name, blurb, category blurb.</summary>
        static readonly Dictionary<int, uint[]> Infos = new Dictionary<int, uint[]>
        {
            [ShopRules.M_Brakes] = new uint[] { 0x4887F351, 0xC61C8D3A, 0x140000B8, 0x946FD2EF, 0x862BFA1B, 0x734A54CD },
            [ShopRules.M_Engine] = new uint[] { 0x4F424E0F, 0xC61C8D3A, 0x1ABA5B76, 0x946FD2EF, 0x8CE654D9, 0x734A54CD },
            [ShopRules.M_Nos] = new uint[] { 0x6FEA04C8, 0xC61C8D3A, 0x972BB750, 0x946FD2EF, 0x081C7393, 0x734A54CD },
            [ShopRules.M_Induction] = new uint[] { 0x8E284227, 0xC61C8D3A, 0x963CB08B, 0x946FD2EF, 0xE684E82E, 0x734A54CD },
            [ShopRules.M_Chassis] = new uint[] { 0x00190EB6, 0xC61C8D3A, 0x15A0581B, 0x946FD2EF, 0x87CC517E, 0x734A54CD },
            [ShopRules.M_Tires] = new uint[] { 0x7373F1EF, 0xC61C8D3A, 0x7CA5D234, 0x946FD2EF, 0x0BAE16D7, 0x734A54CD },
            [ShopRules.M_Transmission] = new uint[] { 0xD142D3E3, 0xC61C8D3A, 0x047F128A, 0x946FD2EF, 0x54C74A2D, 0x734A54CD },
            [ShopRules.M_Body] = new uint[] { 0xAF393DBA, 0x73272ED2, 0x8D3EAD36, 0x4F7990C7, 0x44E9D2F9, 0x2D078425 },
            [ShopRules.M_Hood] = new uint[] { 0xF375276E, 0x73272ED2, 0x7C9F56CA, 0x4F7990C7, 0x0BA79B6D, 0x2D078425 },
            [ShopRules.M_Spoiler] = new uint[] { 0xC51A4F62, 0x73272ED2, 0xACF029DE, 0x4F7990C7, 0x649B4FA1, 0x2D078425 },
            [ShopRules.M_Rims] = new uint[] { 0xC19491CC, 0x73272ED2, 0x7CA4B8DB, 0x4F7990C7, 0x0BACFD7E, 0x2D078425 },
            [ShopRules.M_RoofScoop] = new uint[] { 0x25A4375E, 0x73272ED2, 0x8E9F2DAD, 0x4F7990C7, 0xBF1F2090, 0x2D078425 },
            [ShopRules.M_Vinyl] = new uint[] { 0xD35F04C0, 0xE69D4F7C, 0x420BAE25, 0x4D3BF011, 0xB437A788, 0xDC884F2F },
            [ShopRules.M_Decal] = new uint[] { 0xA9135927, 0xE69D4F7C, 0, 0x4D3BF011, 0, 0xDC884F2F },
            [ShopRules.M_Paint] = new uint[] { 0x0DB89E17, 0xE69D4F7C, 0, 0x4D3BF011, 0, 0xDC884F2F },
            [ShopRules.M_CustomHud] = new uint[] { 0x8BA602FC, 0xE69D4F7C, 0x972B9E81, 0x4D3BF011, 0x081C5AC4, 0xDC884F2F },
            [ShopRules.M_JailFree] = new uint[] { 0x28D4FE33, 0xBDAA5794, 0xCDF089BA, 0x9D8DCEE9, 0x7B0067DD, 0x015BCE87 },
            [ShopRules.M_PinkSlip] = new uint[] { 0x67E2461D, 0xBDAA5794, 0x692B39AA, 0x9D8DCEE9, 0x163B17CD, 0x015BCE87 },
            [ShopRules.M_Cash] = new uint[] { 0x28D128D2, 0xBDAA5794, 0x7C9C5DDF, 0x9D8DCEE9, 0x0BA4A282, 0x015BCE87 },
            [ShopRules.M_ImpoundBox] = new uint[] { 0xB06C3D98, 0xBDAA5794, 0xA43FDA9C, 0x9D8DCEE9, 0x5BEB005F, 0x015BCE87 },
            [ShopRules.M_ImpoundRelease] = new uint[] { 0x189F4EF0, 0xBDAA5794, 0x46148621, 0x9D8DCEE9, 0xFDBFABE4, 0x015BCE87 },
        };
        static readonly uint[] CategoryOrder = { 0xBDAA5794, 0xE69D4F7C, 0x73272ED2, 0xC61C8D3A };

        sealed class Card
        {
            public string Name;
            public int Marker, Cash;
            public bool Picked;
            public uint[] Info => Infos.TryGetValue(Marker, out var i) ? i : Infos[ShopRules.M_Cash];
        }

        readonly int _beaten;
        readonly List<Card> _cards = new List<Card>();
        int _sel;

        public MarkerSelectScreen(int beatenBin) { _beaten = beatenBin; }

        /// <summary>The bin's reward cards: RewardsForWinner of its boss race(s) → RewardMarkerType / UpgradePartID / CashReward.</summary>
        public static List<(string name, int marker, int cash)> Rewards(int bin)
        {
            var list = new List<(string, int, int)>();
            var career = GameSession.Career;
            if (career == null || !career.Bins.TryGetValue(bin, out var b)) return list;
            uint cls = AttribDatabase.Hash("gameplay");
            foreach (var ev in b.BossRaces.AsEnumerable().Reverse())
            {
                if (!career.Db.Collections.TryGetValue((cls, ev.Key), out var c) || !c.Has("RewardsForWinner")) continue;
                foreach (var k in c.Keys("RewardsForWinner"))
                {
                    if (!career.Db.Collections.TryGetValue((cls, k), out var r)) continue;
                    string type = r.Has("RewardMarkerType") ? r.Text("RewardMarkerType") : null;
                    string part = r.Has("UpgradePartID") ? r.Text("UpgradePartID") : null;
                    string name = type == null || type == "backroom" ? part : type;
                    int m = ShopRules.MarkerOf(name);
                    if (m <= 0) continue;
                    list.Add((name, m, r.Has("CashReward") ? r.Int("CashReward") : 0));
                }
                if (list.Count > 0) break;
            }
            return list;
        }

        public override void Open()
        {
            foreach (uint cat in CategoryOrder)
                foreach (var (name, m, cash) in Rewards(_beaten).Take(6))
                    if (Infos.TryGetValue(m, out var i) && i[1] == cat) _cards.Add(new Card { Name = name, Marker = m, Cash = cash });
            for (int i = 0; i < Mathf.Min(3, _cards.Count); i++)
            {
                int spot = UnityEngine.Random.Range(0, Mathf.Min(3, _cards.Count));
                (_cards[i], _cards[spot]) = (_cards[spot], _cards[i]);
            }
            K.RivalArt(View, _beaten, false);
            K.Text(View, 0xBDB541B3, K.L(0x9A375734, "Choose Markers"));
            // SetUnlockIcon (uiMarkerSelect.cpp:54-81): the "new" badges — parts 0x9F04347D, performance 0x5B032D25, visual
            // 0x96B11F47 when DoesCategoryHaveNewUnlock, and the car badge 0x7F8AAF09 when a lot car's UnlockedAt is the current bin
            if (ShopUnlocks.DoesCategoryHaveNewUnlock(1)) K.Message(View, 0x9F04347D);
            if (ShopUnlocks.DoesCategoryHaveNewUnlock(2)) K.Message(View, 0x5B032D25);
            if (ShopUnlocks.DoesCategoryHaveNewUnlock(3)) K.Message(View, 0x96B11F47);
            int curBin = GameSession.Profile?.Bin ?? 16;
            if (GameSession.Career != null && GameSession.Career.CarLot.Any(l => l.UnlockedAt == curBin)) K.Message(View, 0x7F8AAF09);
            K.Text(View, 0x7603F3D5, K.L(0x9A375734, "Choose Markers"));
            for (int i = 0; i < 6; i++)
            {
                var b = View.Find("BUTTON_" + (i + 1));
                View.SetVisible(b, i < _cards.Count);
                View.SetVisible(View.Find("MARKER_GROUP_" + (i + 1)), i < _cards.Count);
            }
            Hide(0x92B96B1D);   // unlocked-items strip (shop unlocks are another screen's business)
            Redraw();
            Highlight(0, true);
        }

        int Picked => _cards.Count(c => c.Picked);

        void Highlight(int i, bool on)
        {
            var b = View.Find("BUTTON_" + (i + 1));
            if (b == null || i >= _cards.Count) return;
            View.Play(b, _cards[i].Picked ? (on ? 0x6B718FA1u : 0xC5DECC84u) : on ? FEView.Highlight : FEView.UnHighlight);
        }

        /// <summary>Redraw: card faces, the header / blurb of the selected card, remaining picks and the rival line.</summary>
        void Redraw()
        {
            for (int i = 0; i < _cards.Count; i++)
            {
                var info = _cards[i].Info;
                var tex = FrontEndData.Texture(_cards[i].Picked ? info[0] : info[1]);
                if (tex) View.SetTexture(View.Find("BUTTON_" + (i + 1)), tex);
            }
            if (_cards.Count > 0)
            {
                var c = _cards[_sel];
                var info = c.Info;
                if (c.Picked)
                {
                    K.Text(View, 0x4960F369, K.L(info[2], ShopRules.MarkerNames[c.Marker]));
                    string blurb = K.L(info[4], "");
                    K.Text(View, 0xEB0A8ABD, c.Marker == ShopRules.M_Cash ? K.Format(blurb, c.Cash) : blurb);
                }
                else
                {
                    K.Text(View, 0x4960F369, K.L(info[3], ""));
                    K.Text(View, 0xEB0A8ABD, K.L(info[5], ""));
                }
            }
            string remain = K.Format(K.L(0x5BB3A130, "Selections Remaining: %d"), 2 - Picked);
            K.Text(View, 0x38DEAC6B, remain);
            K.Text(View, 0x5FC23571, remain);
            K.Text(View, 0xD6C0E097, K.Format(K.L(0xAE5BC899, "You can now select %d markers from %s."), 2 - Picked, K.Rival(_beaten, "AKA")));
        }

        public override void Tick(MenuInput input)
        {
            if (_cards.Count == 0) { if (input.Accept || input.Back) FE.Pop(); return; }
            int was = _sel;
            if (input.Left) _sel = (_sel + _cards.Count - 1) % _cards.Count;
            if (input.Right) _sel = (_sel + 1) % _cards.Count;
            var m = UnityEngine.InputSystem.Mouse.current;
            var at = K.Mouse(View);
            bool click = false;
            if (at != null)
                for (int i = 0; i < _cards.Count; i++)
                {
                    var b = View.Find("BUTTON_" + (i + 1));
                    if (b == null) continue;
                    var c = K.AbsPos(b);
                    if (Mathf.Abs(at.Value.x - c.x) < 32 && Mathf.Abs(at.Value.y - c.y) < 32)
                    {
                        if (m.delta.ReadValue().sqrMagnitude > 0) _sel = i;
                        if (m.leftButton.wasPressedThisFrame) { _sel = i; click = true; }
                    }
                }
            if (_sel != was) { Highlight(was, false); Highlight(_sel, true); Redraw(); }
            if (input.Accept || click)
            {
                if (Picked >= 2) { FE.Pop(); return; }
                var card = _cards[_sel];
                if (card.Picked) return;
                card.Picked = true;
                View.Play(View.Find("BUTTON_" + (_sel + 1)), CareerScreenKit.Flip);
                var p = GameSession.Profile;
                if (card.Marker == ShopRules.M_PinkSlip) ShopMarkers.AwardRivalCar(_beaten);
                else if (card.Marker == ShopRules.M_Cash) { if (p != null) p.Cash += card.Cash; }
                else ShopMarkers.Add(card.Marker);
                p?.Save();
                if (Picked >= 2)
                {
                    K.Text(View, 0xBDB541B3, K.L(0x8098A54C, "Continue"));
                    K.Text(View, 0x7603F3D5, K.L(0x8098A54C, "Continue"));
                }
                Redraw();
            }
        }
    }

    /// <summary>SafehouseRegionUnlock.fng (uiSafehouseRegionUnlock.cpp, §3.7; PC NotificationMessage 0x51F630): rival art of the
    /// beaten bin, 0xD6C0E097 = "Camden Beach borough has now been unlocked." (0x29E4B193) for bin 13 / "Rockport borough has now
    /// been unlocked." (0x2B0BCA2D) for bin 9 (other bins leave the package text); only accept continues the flow.</summary>
    public sealed class RegionUnlockScreen : FEScreen
    {
        public override string Package => "SafehouseRegionUnlock";
        readonly int _beaten;
        public RegionUnlockScreen(int beatenBin) { _beaten = beatenBin; }

        public override void Open()
        {
            K.RivalArt(View, _beaten, false);
            if (_beaten == 13) K.Text(View, 0xD6C0E097, K.L(0x29E4B193, "Camden Beach borough has now been unlocked."));
            else if (_beaten == 9) K.Text(View, 0xD6C0E097, K.L(0x2B0BCA2D, "Rockport borough has now been unlocked."));
        }

        public override void Tick(MenuInput input)
        {
            if (input.Accept) FE.Pop();   // uiRepSheetRivalFlow::Next
        }
    }

    /// <summary>
    /// uiRepSheetRivalFlow (§3.3): after the last boss race of a bin — result, marker selection, region unlock (beaten
    /// bin 13 or 9), autosave, the next rival's bio, his Blacklist movie, at bin 13 Mia's rap-sheet movie, then the bin's
    /// entry SMS and back to the game. Stage 0 is SafeHouseRivalChallenge in post-rival mode (InGame/RivalScreen.cs).
    /// </summary>
    public static class RivalFlow
    {
        public static bool Active { get; private set; }
        static int _stage, _beaten;

        public static void Start(int beatenBin)
        {
            _beaten = beatenBin;
            _stage = -1;
            Active = true;
            Next();
        }

        static void WhenClosed(FrontEnd fe)
        {
            void H() { fe.Closed -= H; Next(); }
            fe.Closed += H;
        }

        static void Push(FEScreen s)
        {
            var fe = FrontEnd.Create();
            fe.Push(s);
            WhenClosed(fe);
        }

        public static void Next()
        {
            if (!Active) return;
            _stage++;
            int bin = _beaten - 1;
            var p = GameSession.Profile;
            switch (_stage)
            {
                case 0:
                    Push(RivalScreen.PostRivalMode(_beaten));   // SafeHouseRivalChallenge with the DEFEATED stamp (uiRepSheetRival, post-rival mode)
                    return;
                case 1:
                    if (MarkerSelectScreen.Rewards(_beaten).Count > 0) { Push(new MarkerSelectScreen(_beaten)); return; }
                    Next();
                    return;
                case 2:
                    if (_beaten == 13 || _beaten == 9) { Push(new RegionUnlockScreen(_beaten)); return; }
                    Next();
                    return;
                case 3:
                    p?.Save();
                    Next();
                    return;
                case 4:
                    if (bin >= 1) { RepSheetMainScreen.ViewBin = bin; Push(new RivalBioScreen(bin, false, true)); return; }
                    Next();
                    return;
                case 5:
                    if (bin >= 1) { StoryCard.Run(null, $"blacklist_{bin:00}", Next); return; }
                    Next();
                    return;
                case 6:
                    // uiRepSheetRivalFlow::Next (PC 0x5302C0) stage 6: CurrentBin 13 without the rap sheet → SetHasRapSheet, the
                    // storyfmv_rap30 movie and stage 6 again (mStage--); else StartCareerFreeRoam (the port's default below)
                    var rs = p != null ? p.RapSheet ??= new RapSheetRecord() : null;
                    if (bin == 13 && rs != null && !rs.HasRapSheet)
                    {
                        rs.HasRapSheet = true;
                        p.Save();
                        _stage--;
                        StoryCard.Run(null, "storyfmv_rap30", Next);
                        return;
                    }
                    Next();
                    return;
                default:
                    Active = false;
                    var b = GameSession.Career != null && GameSession.Career.Bins.TryGetValue(bin, out var x) ? x : null;
                    if (b != null && b.EntrySms > 0) Sms.Show(b.EntrySms);
                    return;
            }
        }
    }
}
