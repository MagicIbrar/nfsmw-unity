using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// The game's HUD package (HUD_Player1 from InGameSplitScreen.bun, laid out for the top half of the screen and
    /// moved down for single player): speed digits, gear, needle, shift light, nitrous, race time, position,
    /// countdown and wrong-way sign, driven from the player car and the race.
    /// </summary>
    public sealed class GameHud : MonoBehaviour
    {
        public PlayerCar Car;
        FEView _view;
        Canvas _canvas;
        FEView.Node _d1, _d2, _d3, _gear, _units, _needle, _shift, _time, _pos, _go, _goShadow, _wrong, _complete, _race, _percent;
        float _needleLength, _needleX;
        int _split;

        /// <summary>split: 0 = single player (HUD_Player1 enlarged into the lower screen), 1 / 2 = split-screen halves.</summary>
        public static GameHud Create(PlayerCar car, int split = 0)
        {
            FrontEndData.Load();
            // speed.exe FUN_005694e0: HUD_SingleRace unless split screen (HUD_Player1/2)
            string package = split == 2 ? "HUD_Player2" : split == 1 ? "HUD_Player1" : "HUD_SingleRace";
            if (!FrontEndData.Packages.ContainsKey(package)) return null;
            var go = new GameObject("HUD");
            var hud = go.AddComponent<GameHud>();
            hud.Car = car;
            hud._canvas = FrontEndCanvas.Create("HUD Canvas", null, 5);
            hud._canvas.transform.SetParent(go.transform, false);
            hud._split = split;
            hud._map = Minimap.Create(go.transform, car, split);
            hud.Open(package);
            if (split <= 1) Systems.HudMessage = hud.Message;
            return hud;
        }

        Minimap _map;
        HudExtras _extras;
        string _package;
        bool _drag;

        /// <summary>
        /// Opens a HUD package (FEPkg_Hud.cpp GetHudPackageName: HUD_Drag.fng in drag races, HUD_SingleRace.fng otherwise)
        /// and re-seats the minimap, which only the circuit/sprint HUD has.
        /// </summary>
        void Open(string package)
        {
            if (_view) Destroy(_view.gameObject);
            // the HUD runs on the FEng engine (script events, package / object responses, flag-bit-0 visibility, the PC load
            // pass): FEngHud has no pad input and no NotificationMessage for it, so game messages are dropped
            _view = FEView.Open(package, _canvas.transform, null, false, true);
            _view.MessagesEnabled = true;
            _package = package;
            _wide = false;   // FEngHud ctor: mCurrentWidescreenSetting(false)
            _features = null;
            _drag = package == "HUD_Drag";
            _posTotal = null;
            _cdStep = -1;
            _lastShiftCount = -1;
            _turboPsi = float.NaN;
            Bind();
            _features = new HudFeatures(_view, _drag, _split, GameSession.Mode == GameMode.Career);
            if (_map) _map.Bind(_drag ? null : _view, _split);
            _msg = new HudGenericMessage(_view);
            _pursuit = new HudPursuitBoard(_view, _msg);
            // FEngHud vtbl+0x10 (0x569570) with true: FADE_IN (the clusters' FADEIN) and DEACTIVATE (82D60021's FADEOUT)
            // [?: the caller is not traced; sent when the HUD opens]
            _view.PackageMessage(FrontEndData.Hash("FADE_IN"));
            _view.PackageMessage(FrontEndData.Hash("DEACTIVATE"));
            // time extension, radar, CTS, milestone board, infractions, leaderboard paging (HudExtras.cs)
            _extras = _split == 0 && !_drag ? new HudExtras(_view, Car, _msg) : null;
            if (_extras != null) _extras.On = On;
        }

        void Bind()
        {
            _d1 = _view.Find("SPEED_DIGIT_1");
            _d2 = _view.Find("SPEED_DIGIT_2");
            _d3 = _view.Find("SPEED_DIGIT_3");
            _gear = _view.Find("3rdPersonGear");
            _units = _view.Find("3rdPersonSpeedUnits");
            _needle = _view.Find("3rdPersonNeedle");
            _shift = _view.Find("Shift_light");
            _time = _view.Find("CurrentLapTime");
            _pos = _view.TextOf(_view.Find("POSITION_GROUP"));
            _go = _view.Find("321_GO");
            _goShadow = _view.Find("321_GO_SHADOW");
            _wrong = _view.Find("WRONGWAYIMAGE");
            _complete = _view.Find("Complete_Text");
            if (_needle != null) (_needleLength, _needleX) = (_needle.Data.SizeX, _needle.Data.X);
            foreach (var d in new[] { _d1, _d2, _d3 }) _view.SetText(d, "0");   // the package ships "500"
            _view.SetText(_gear, "1");
            // visibility: HudFeatures (the FEngHud ctor hides the whole package; the widgets show what they registered).
            // The hand-made hide list of earlier rounds is gone with it.
            if (_drag)
            {
                // HudResourceManager: DRAG_RPM_<dial>_LINES and the drag redline angle for the dial
                int range = _tachRange = Dial(Car.Spec.MaxRpm);
                var lines = FrontEndData.TextureByName($"DRAG_RPM_{range * 1000}_LINES");
                if (lines) _view.SetTexture(_view.Find("TAC_Lines_7500"), lines);
                var red = _view.Find("RPM_REDLINE");
                _view.SetMultiImageRot(red, DragRedlineAngle(range, Car.Spec.RedLine));
                _shiftGroup = _view.Find("SHIFT_GROUP");
                _shiftMsg = _view.Find("ShiftMessage");
                _shiftMsgShadow = _view.Find("ShiftMessage_Shadow");
                _heatShiftLight = _view.Find("ENGINE_HEAT_SHIFTLIGHT_GROUP");
                _heatMeter = _view.Find("ENGINE_METER");
                _heatIcon = _view.Find("ENGINE_HEAT_ICON_GROUP");
            }
            else if (_split == 0)
            {
                // FADEIN / WIDE come from the FADE_IN / WIDESCREEN_MODE package messages (Open, SetWideScreenMode)
                int range = _tachRange = Dial(Car.Spec.MaxRpm);
                var red = _view.Find("RPM_REDLINE");
                _view.SetMultiImageRot(red, RedlineAngle(range, Car.Spec.RedLine));   // LoadingCompleteCallback phase 2: FEngSetMultiImageRot(CDFCE1B0)
                CustomHud(range);
            }
            _race = _view.Find("RaceInformationGroup");
            _percent = _view.Find(0x9CB5C95D);
            _view.SetText(_complete, "COMPLETE");
        }

        static readonly HashSet<int> _customPacks = new HashSet<int> { 0 };   // HUDS_Custom_00 comes with FrontEndData

        /// <summary>
        /// The standard HUD's custom style (HudResourceManager, FEPkg_Hud.cpp:131-416; PC GetCustomHudTexPackFilename 0x58C8B0,
        /// pack callback 0x595CC0, textures callback 0x58C930): the custom index = HUDINDEX of the car's CUSTOM_HUD part (slot
        /// 132; 0 without one), GLOBAL\HUDS_Custom_%2.2d.bin loaded, then five textures: TACH_FILL_nn → 05D19F25 (colour:
        /// HUD backing, slot 133), &lt;dial&gt;_LINES_nn → TAC_Lines_7500 309878BC (character colour, 135), TURBO_LINES_nn →
        /// C62AD685 (135), TACH_NEEDLE_nn → 3rdPersonNeedle F0250DAC (needle colour, 134), TURBO_NEEDLE_nn → 6D5ECE44 (134);
        /// each colour (0xFF RRGGBB from the part's RED / GREEN / BLUE) only when the slot holds a part; 3rdPersonSpeedUnits
        /// C3383B63 the character colour too. The car: the career car, else a quick race's (not in a pursuit race).
        /// (The decomp's CUSTOMHUD_* names are another build's: the PC formats are TACH_FILL / TURBO_LINES / TACH_NEEDLE /
        /// TURBO_NEEDLE_%2.2d.) Before round 9 only the needle and lines of style 00 were set.
        /// </summary>
        void CustomHud(int dial)
        {
            var race = RaceManager.Active;
            bool pursuitRace = Systems.Police != null && Systems.Police.IsPursuitRace;
            var owned = GameSession.Mode == GameMode.Career || (race != null && !pursuitRace) ? GameSession.ActiveCar : null;
            CarPartDb.Part Part(int slot) { try { return CarParts.Installed(owned, slot); } catch { return null; } }
            int idx = Part(132)?.I(EaHash.HashUpper("HUDINDEX")) ?? 0;
            if (_customPacks.Add(idx))
            {
                var path = System.IO.Path.Combine(GameData.Root, "GLOBAL", $"HUDS_Custom_{idx:00}.bin");
                if (System.IO.File.Exists(path))
                {
                    var d = GameFiles.ReadBundle(path);
                    FrontEndData.Textures.Add(TexturePack.ReadAll(d, 0, d.Length));
                }
            }
            uint Colour(int slot)
            {
                var p = Part(slot);
                if (p == null) return 0;
                var (r, g, b) = p.Rgb;
                return 0xFF000000u | (uint)(r & 0xFF) << 16 | (uint)(g & 0xFF) << 8 | (uint)(b & 0xFF);
            }
            (uint obj, int slot, string tex)[] set =
            {
                (0x05D19F25, 133, $"TACH_FILL_{idx:00}"), (0x309878BC, 135, $"{dial * 1000}_LINES_{idx:00}"), (0xC62AD685, 135, $"TURBO_LINES_{idx:00}"),
                (0xF0250DAC, 134, $"TACH_NEEDLE_{idx:00}"), (0x6D5ECE44, 134, $"TURBO_NEEDLE_{idx:00}"),
            };
            foreach (var (obj, slot, tex) in set)
            {
                var n = _view.Find(obj);
                uint c = Colour(slot);
                if (c != 0) _view.SetColor(n, c);
                _view.SetTextureHash(n, EaHash.HashUpper(tex));
            }
            uint cc = Colour(135);
            if (cc != 0) _view.SetColor(_view.Find(0xC3383B63), cc);
        }

        HudFeatures _features;
        HudGenericMessage _msg;
        HudPursuitBoard _pursuit;
        bool? _wide;

        /// <summary>The widget's feature bits are on (FEngHud::Update runs a widget's Update only then).</summary>
        bool On(ulong mask) => _features == null || (_features.Current & mask) != 0;

        /// <summary>The video setting's widescreen flag [?: the aspect ratio stands for FEDatabase video WideScreen].</summary>
        static bool WideSetting => (float)Screen.width / Screen.height > 1.45f;

        /// <summary>FEngHud::SetWideScreenMode (0x5696C0, every frame): on a change of the setting WIDESCREEN_MODE / NORMAL_MODE
        /// to the package and Minimap::AdjustForWidescreen(wide); the HUD starts as "not wide".</summary>
        void SetWideScreenMode()
        {
            bool wide = WideSetting;
            if (_wide == wide) return;
            _wide = wide;
            _view.PackageMessage(FrontEndData.Hash(wide ? "WIDESCREEN_MODE" : "NORMAL_MODE"));
            if (_map) _map.AdjustForWidescreen(wide);
        }

        ChaseCamera _cam;

        /// <summary>
        /// DetermineHudFeatures' camera gate: the player's view must run the drive camera (CM_DRIVE_CUBIC) without the
        /// look-back angle. [?] The port's ChaseCamera has no public look-back state: its condition (B held by a human
        /// driver outside the front end) is read here; the director's TrackCar / TrackCop / ICE shots are not the drive camera.
        /// </summary>
        bool DriveCamera(bool menu)
        {
            if (!_cam || _cam.Target != Car.transform)
            {
                _cam = null;
                foreach (var c in FindObjectsOfType<ChaseCamera>()) if (c.Target == Car.transform) { _cam = c; break; }
            }
            if (_cam && _cam.Director.Action != NfsMw.CameraAction.Drive) return false;
            var kb = UnityEngine.InputSystem.Keyboard.current;
            bool lookBack = Car.UseDeviceInput && !menu && kb != null && kb.bKey.isPressed;
            return !lookBack;
        }

        void LateUpdate()
        {
            if (!Car)
            {
                Destroy(gameObject);
                return;
            }
            if (_split == 0)
            {
                var active = RaceManager.Active;
                string want = active != null && active.Event != null && active.Event.Type == RaceType.Drag ? "HUD_Drag" : "HUD_SingleRace";
                if (want != _package && FrontEndData.Packages.ContainsKey(want)) Open(want);
            }
            bool menu = FrontEnd.Instance != null && FrontEnd.Instance.IsOpen;
            bool nis = NisPlayer.Active;
            // FEngHud::Update (0x58CA30): DetermineHudFeatures → SetHudFeatures on a change, then SetWideScreenMode
            bool inGame = !menu && !nis && _wide == WideSetting && DriveCamera(menu);
            _features?.Set(HudFeatures.Determine(inGame, Car, _split == 2 ? 1 : 0, RaceManager.Active));
            SetWideScreenMode();
            _canvas.enabled = !menu && !nis;
            if (menu || nis) return;

            int mph = Mathf.Clamp(Settings.Speed(Car.SpeedMps), 0, 999);
            // Speedometer::Update (FeSpeedometer.cpp; PC 0x57A540): the unit label KM/H (0x8569A25F) or MPH (0x8569AB44),
            // the digits FEPrintf "%d"
            _view.SetLanguageHash(_units, Settings.Kmh ? 0x8569A25Fu : 0x8569AB44u);
            var prof = GameSession.Profile;
            if (prof != null && Car.SpeedMps > prof.BestTopSpeed && Car.SpeedMps < 120f) prof.BestTopSpeed = Car.SpeedMps;
            int hund = mph / 100, tens = mph / 10 % 10;
            // HUD_SingleRace numbers its digits from the right (DIGIT_3 = hundreds); hide leading zeros (Hud_Speedometer)
            var dh = _split == 0 ? _d3 : _d1;
            var dt = _d2;
            var du = _split == 0 ? _d1 : _d3;
            _view.Printf(dh, hund.ToString());
            _view.Printf(dt, tens.ToString());
            _view.Printf(du, (mph % 10).ToString());
            if (_split == 0 && On(0x08000000))
            {
                // Speedometer::Update: FEngSetInvisible DIGIT_3 when h == 0, DIGIT_2 when h == 0 and t == 0, else FEngSetVisible
                if (hund > 0) _view.SetVisibleTree(dh); else _view.SetInvisible(dh);
                if (hund > 0 || tens > 0) _view.SetVisibleTree(dt); else _view.SetInvisible(dt);
            }
            _view.Printf(_gear, Car.Gear < 0 ? "R" : Car.Gear == 0 ? "N" : Car.Gear.ToString());   // Tachometer: "%c"
            float rpm01 = Mathf.InverseLerp(0, Car.Spec.MaxRpm, Car.Rpm);
            if (_needle != null && _drag)       // DragTachometer::CalcAngleForRPMDrag: -43..44.5 degrees over the dial
                _view.SetRotationZ(_needle, -43f + 87.5f * Mathf.Clamp01(Car.Rpm / (_tachRange * 1000f)));
            else if (_needle != null && _split == 0) // HUD_SingleRace: a needle turning about the tach centre
                _view.SetRotationZ(_needle, 66f + 228f * Mathf.Clamp01(Car.Rpm / (_tachRange * 1000f)));   // speed.exe 0x57A670 → FEngSetRotationZ 0x514E20
            else if (_needle != null) // split-screen packages: a straight rev bar, grown from its left end
            {
                float w = _needleLength * Mathf.Clamp01(rpm01);
                _needle.Data.SizeX = w;
                _needle.Data.X = _needleX - (_needleLength - w) / 2;
            }
            if (_drag) DragShift();
            else if (On(0x2)) TachShiftLight();

            var race = RaceManager.Active;
            if (race != null)
            {
                if (On(0x04000000)) RaceInformation(race);
                if (On(0x400)) Countdown(race);
            }
            else _cdStep = -1;
            Pursuit();
            Gauges();
            if (On(0x01000000)) _msg?.Update();   // GenericMessage::Update (mask 0x01000000: on whenever the HUD shows)
            if (_map) _map.Tick(_split != 2 && On(0x40010000), Car, _split != 0 && GameBootstrap.Instance ? GameBootstrap.Instance.Player2 : null);
            if (On(0x20)) WrongWay(race != null && race.WrongWay && race.Entrants.Count > 0 && race.Entrants[0].Car == Car);
            Turbo(race);
            if (_split == 0 && !_drag) Board(race);
            if (_split <= 1) HudExtras.HudActions(_features == null || _features.Current != 0);
            if (race != null && _split <= 1) RaceStatsTracker.Tick(race, Time.deltaTime);
            _extras?.Tick(race);
        }

        FEView.Node _shiftGroup, _shiftMsg, _shiftMsgShadow, _heatShiftLight, _heatMeter, _heatIcon;
        int _lastShiftCount = -1;
        string _shiftScript, _heatScript, _iconScript;
        const float WarningHeat = 0.5f;   // warningPulseMinRpm

        /// <summary>
        /// ShiftUpdater, EngineTempGauge and the drag needle's perfect-launch colour (FeShiftUpdater.cpp,
        /// FeEngineTempGauge.cpp, FETachometerDrag.cpp).
        /// </summary>
        void DragShift()
        {
            var p = Car.Potential;
            string want = null;
            bool overheat = false;
            if (!Car.EngineBlown)
            {
                if (p == PlayerCar.ShiftPotential.Good) want = "PulseBlue";
                else if (p == PlayerCar.ShiftPotential.Perfect) want = "PulseGreen";
                else if (p == PlayerCar.ShiftPotential.Miss) { if (Car.EngineHeat > WarningHeat) overheat = true; else want = "PulseRed"; }
            }
            _view.SetVisible(_shift, want != null);
            if (want != null && want != _shiftScript) _view.Play(_shift, FrontEndData.Hash(want));
            _shiftScript = want;
            _view.SetVisible(_heatShiftLight, overheat);
            if (overheat && _heatScript != "OVERHEAT_PULSE") _view.Play(_heatShiftLight, FrontEndData.Hash("OVERHEAT_PULSE"));
            _heatScript = overheat ? "OVERHEAT_PULSE" : null;

            // a rated gear change (2nd and up): EARLY / GOOD / PERFECT / OVERREV with its message
            if (_lastShiftCount < 0) _lastShiftCount = Car.ShiftCount;
            if (Car.ShiftCount != _lastShiftCount)
            {
                _lastShiftCount = Car.ShiftCount;
                var st = Car.LastShift;
                if (Car.Gear >= 2 && st >= PlayerCar.ShiftStatus.Normal && !Car.EngineBlown)
                {
                    var (label, fallback, script) = st switch
                    {
                        PlayerCar.ShiftStatus.Good => (0x27D2DD45u, "Good Shift!", "GOOD"),
                        PlayerCar.ShiftStatus.Perfect => (0x0598B065u, "Perfect Shift!", "PERFECT"),
                        PlayerCar.ShiftStatus.Missed => (0xDF61B3E5u, "Over Rev!", "OVERREV"),
                        _ => (0x2202B5B9u, "Early Shift!", "EARLY"),
                    };
                    string text = FrontEndData.Text(label, fallback);
                    _view.SetText(_shiftMsg, text);
                    _view.SetText(_shiftMsgShadow, text);
                    _view.SetVisible(_shiftGroup, true);
                    _view.Play(_shiftGroup, FrontEndData.Hash(script));
                    _view.SetVisible(_shift, false);
                }
            }

            // engine temperature: the bar turns heat x 26.5 - 26.5 degrees; the icon pulses above 0.5
            _view.SetMultiImageRot(_heatMeter, Car.EngineHeat * 26.5f - 26.5f);
            string icon = Car.EngineHeat > WarningHeat ? "OVERHEAT_PULSE" : Car.EngineHeat > 0.1f ? "ACTIVATE" : "INIT";
            if (icon != _iconScript && _heatIcon != null) { _view.Play(_heatIcon, FrontEndData.Hash(icon)); _iconScript = icon; }

            // the needle shows the inverse colour inside the perfect-launch band while staging
            if (_needle != null)
            {
                bool inBand = Car.Staging && Car.InPerfectLaunchRange;
                if (inBand != _needleInverted)
                {
                    // Tachometer iface +0x14 toggled: the needle colour becomes ~c | 0xFF000000 (FEngSetColor)
                    _needleInverted = inBand;
                    _view.SetColor(_needle, ~_view.GetColor(_needle) | 0xFF000000u);
                }
            }
        }

        bool _needleInverted;

        /// <summary>FEPkg_Hud.cpp: the drag tach's redline angle for a dial and redline rpm.</summary>
        static float DragRedlineAngle(int dial, float red) => dial switch
        {
            7 => red >= 6500 ? 39.25f : red >= 6000 ? 32.25f : red >= 5500 ? 26f : 19.25f,
            8 => red >= 7500 ? 40f : red >= 7000 ? 34f : red >= 6500 ? 28.25f : red >= 6000 ? 22.5f : 17.25f,
            9 => red >= 8500 ? 42f : red >= 8000 ? 37f : red >= 7500 ? 32.25f : red >= 7000 ? 27f : 22f,
            _ => red >= 9500 ? 41.5f : red >= 9000 ? 37f : red >= 8500 ? 31.5f : red >= 8000 ? 27f : 22.75f,
        };

        int _tachRange = 10;   // dial top in thousands of rpm (7..10)

        /// <summary>The HUD's time strings: Timer(sec).PrintToString(4) (PrintTime with flags 4, English).</summary>
        public static string TimeString(float sec) => PrintTime(sec, 4);

        /// <summary>
        /// Timer::PrintToString (Misc/Timer.cpp:239 / :435; PC FUN_006547A0 → 0x6541B0), English formats: Timer(float) packs
        /// (int)(sec · 4000 + 0.5) ticks; an unset timer (0 ticks) prints as zero. Hours → "hh:mm:ss" (no hundredths); minutes →
        /// "mm:ss.hh"; seconds → "ss.hh"; else "0.hh"; the leading field without a leading zero (a space). Flags: 1 negative
        /// (also set by a negative time), 2 plus sign — the sign replaces the first character, shifting the string right when
        /// the leading field has two digits (dropping the last character unless 8); 4 only picks the template ("00" or "--",
        /// every digit place of which is overwritten); 16 drop the hundredths (no hours).
        /// [?] Other languages' separators (French ' ", German :, Nordic . ,) are not ported (the port runs English).
        /// </summary>
        public static string PrintTime(float sec, int flags)
        {
            int packed = (int)(sec * 4000f + 0.5f);
            int h = 0, m = 0, s = 0, hh = 0;
            if (packed != 0 && packed != 0x7FFFFFFF)
            {
                int p = packed < 0 ? -packed : packed;
                int whole = p / 4000, frac = p % 4000;
                h = whole / 3600; whole %= 3600;
                m = whole / 60; s = whole % 60;
                hh = (frac >> 2) / 10;
                if (packed < 0) flags |= 1;
            }
            string Two(int v, bool lead) { var c = new char[2]; for (int n = 0; n < 2; n++) { int d = v % 10; c[1 - n] = !lead && d == 0 && v < 10 ? ' ' : (char)('0' + d); v /= 10; } return new string(c); }
            string r;
            int lead;
            if (h != 0) { r = Two(h, false) + ":" + Two(m, true) + ":" + Two(s, true); lead = h; }
            else if (m != 0) { r = Two(m, false) + ":" + Two(s, true) + "." + Two(hh, true); lead = m; }
            else if (s != 0) { r = Two(s, false) + "." + Two(hh, true); lead = s; }
            else { r = "0." + Two(hh, true); lead = 10; }
            if ((flags & 3) != 0 && lead > 9)
            {
                string t = (flags & 8) != 0 ? r : r.Substring(0, r.Length - 1);
                r = r[0] + t;
            }
            if (h == 0 && (flags & 16) != 0 && r.Length >= 3) r = r.Substring(0, r.Length - 3);
            if ((flags & 1) != 0) r = "-" + r.Substring(1);
            else if ((flags & 2) != 0) r = "+" + r.Substring(1);
            return r;
        }

        int _cdStep = -1;
        float _goAt;

        /// <summary>
        /// RaceInformation::Update (FeRaceInformation.cpp:31; PC 0x57A900): tollbooth races HIDE POSITION_GROUP and APPEAR
        /// Milestone_TollBooth (each unless set) and print the tollbooths passed / total into 1CDDD8D0 / 18A7ACD2, other races
        /// INIT / HIDE them and print the position (9C183BF8) and the racer count (3BBD6268); sprint, speedtrap and tollbooth:
        /// Complete_Text = "COMPLETE" (0x59BB1918) and "%d %%" of the race, else "LAP" (0xBF9C) and "%d/%d" lap / laps;
        /// CurrentLapTime = "SUDDEN DEATH!" (0x733C8147) or the time string.
        /// [?] Feeders: the lap time is the race clock (tollbooths: the time left), position = Entrant.Place, sudden death is
        /// not raised (no port race sets it).
        /// </summary>
        void RaceInformation(RaceManager race)
        {
            var me = race.Entrants.Find(e => e.Car == Car);
            var ev = race.Event;
            var posGroup = _view.Find("POSITION_GROUP");
            var tollIcon = _view.Find("Milestone_TollBooth");
            if (ev.Type == RaceType.Tollbooth)
            {
                if (!_view.IsScriptSet(posGroup, FEView.Hide)) _view.SetScript(posGroup, FEView.Hide, true);
                if (!_view.IsScriptSet(tollIcon, FEView.Appear)) _view.SetScript(tollIcon, FEView.Appear, true);
                _view.Printf(_view.Find(0x1CDDD8D0), (_extras?.TollsPassed ?? 0).ToString());
                _view.Printf(_view.Find(0x18A7ACD2), (_extras?.TollsTotal ?? 0).ToString());
            }
            else
            {
                if (!_view.IsScriptSet(posGroup, FEView.Init)) _view.SetScript(posGroup, FEView.Init, true);
                if (!_view.IsScriptSet(tollIcon, FEView.Hide)) _view.SetScript(tollIcon, FEView.Hide, true);
                _view.Printf(_view.Find(0x9C183BF8), (me?.Place ?? 0).ToString());
                _view.Printf(_view.Find(0x3BBD6268), race.Entrants.Count.ToString());
            }
            switch (ev.Type)
            {
                case RaceType.Sprint: case RaceType.SpeedTrap: case RaceType.Tollbooth:
                    _view.SetLanguageHash(_complete, 0x59BB1918);
                    _view.Printf(_percent, $"{(int)(race.CompletionOf(me) * 100f)} %");
                    break;
                default:
                    _view.SetLanguageHash(_complete, 0xBF9C);
                    _view.Printf(_percent, $"{Mathf.Min(race.LapOf(me), Mathf.Max(1, ev.Laps))}/{Mathf.Max(1, ev.Laps)}");
                    break;
            }
            float t = race.TimeLeft >= 0 ? race.TimeLeft : race.RaceTime;   // tollbooths count down
            _view.Printf(_time, PrintTime(t, 4));
        }

        /// <summary>
        /// Countdown::Update (FeCountdown.cpp:21; PC 0x5679B0): THREE (0x03C3C2F7) / TWO (0xE479) / ONE (0xCE01) on
        /// 321_GO_GROUP with "%d" in both strings a second apart, GO (0x535) with the "GO!" label (0x61223AB5), HIDE 6 s later.
        /// [?] The steps follow RaceManager's countdown clock (the exe's BeginCountdown and its own second timer are the
        /// gameplay owner's); the tollbooth-arm NIS triggers and the 321 sound state are not this widget's in the port.
        /// </summary>
        void Countdown(RaceManager race)
        {
            var group = _view.Find("321_GO_GROUP");
            int step = race.Countdown ? Mathf.Clamp(Mathf.CeilToInt(race.CountdownLeft - 1f), 0, 3) : 0;
            if (!race.Countdown && _cdStep < 0) return;
            if (step != _cdStep)
            {
                _cdStep = step;
                if (step > 0)
                {
                    _view.SetScript(group, step == 3 ? 0x03C3C2F7u : step == 2 ? 0xE479u : 0xCE01u, true);
                    _view.Printf(_go, step.ToString());
                    _view.Printf(_goShadow, step.ToString());
                }
                else
                {
                    _view.SetScript(group, 0x535, true);
                    _view.SetLanguageHash(_go, 0x61223AB5);
                    _view.SetLanguageHash(_goShadow, 0x61223AB5);
                    _goAt = Time.time;
                }
            }
            if (step == 0 && Time.time - _goAt >= 6f) { _view.SetScript(group, FEView.Hide, true); _cdStep = -2; }
        }

        /// <summary>
        /// NitrousGauge (FeNitrousGauge.cpp; PC Update 0x568330, mask 0x80000800): EDFB6D37's mask turned
        /// nos · −min + min degrees (min 175, HUD_Drag −48, split screen 180); SetNos (fed every frame): empty → INIT,
        /// lower than last frame → ACTIVATE, else PULSE (each unless set). The speedbreaker arc (SpeedBreakerMeter, mask
        /// 0x40000) the same way with the speedbreaker fill.
        /// </summary>
        void Gauges()
        {
            if (_split != 0) return;
            float min = _drag ? -48f : 175f;
            float nos = Mathf.Clamp01(Car.Nos);
            var nosIcon = _view.Find(0x27DDF583);
            uint nosScript = nos <= 0 ? FEView.Init : nos < _lastNos ? 0x77031C70u : 0x03826A28u;
            if (nosIcon != null && !_view.IsScriptSet(nosIcon, nosScript)) _view.SetScript(nosIcon, nosScript, true);
            _lastNos = nos;
            if (On(0x80000800)) _view.SetMultiImageRot(_view.Find(0xEDFB6D37), nos * -min + min);
            if (On(0x40000)) _view.SetMultiImageRot(_view.Find(0x5DF2F5B4), min * (1f - Mathf.Clamp01(Car.SpeedBreaker)));
        }

        float _lastNos = -1;

        /// <summary>Tachometer::Update (FeTachometer.cpp): Shift_light GREEN (0x02DDC8F0) for a shift potential above 1, else INIT.</summary>
        void TachShiftLight()
        {
            if (_shift == null) return;
            uint want = (int)Car.Potential > 1 ? 0x02DDC8F0u : FEView.Init;
            if (!_view.IsScriptSet(_shift, want)) _view.SetScript(_shift, want, true);
        }

        float _wrongSince = -1, _rightSince = -1;
        bool _isWrong;

        /// <summary>
        /// WrongWIndi (FeWrongWIndi.cpp): 2 s of wrong way (game time) → 0x47510B1E (widescreen 0x908E787E: blink, slide
        /// away); 2 s back on course → HIDE.
        /// </summary>
        void WrongWay(bool wrong)
        {
            if (_wrong == null) return;
            if (wrong != _isWrong)
            {
                _isWrong = wrong;
                if (wrong) { _wrongSince = Time.time; _rightSince = -1; }
                else { _rightSince = Time.time; _wrongSince = -1; }
            }
            if (wrong && _wrongSince >= 0 && Time.time - _wrongSince >= 2f)
            {
                _wrongSince = -1;
                uint s = WideSetting ? 0x908E787Eu : 0x47510B1Eu;
                if (!_view.IsScriptSet(_wrong, s)) _view.SetScript(_wrong, s, true);
            }
            else if (!wrong && _rightSince >= 0 && Time.time - _rightSince >= 2f)
            {
                _rightSince = -1;
                if (!_view.IsScriptSet(_wrong, FEView.Hide)) _view.Play(_wrong, FEView.Hide);
            }
        }

        float _turboPsi = float.NaN;

        /// <summary>
        /// TurboMeter (FeTurboMeter.cpp; PC Update 0x57BB40, mask 0x80020000 — the feature bits show TURBO_GROUP only for a
        /// turbo car [?: CarSpec spool &gt; 0 stands for HUD vtbl+0x20]): on a psi change the needle turns
        /// −(min + (psi + 20)/40 · (max − min)) degrees, ±45 (drag 200..270), with FEngSetRotationZ.
        /// </summary>
        void Turbo(RaceManager race)
        {
            if (_split != 0 || !On(0x80020000)) return;
            float psi = Car.BoostPsi;
            if (psi == _turboPsi) return;
            _turboPsi = psi;
            bool drag = race != null && race.Event != null && race.Event.Type == RaceType.Drag;
            float minA = drag ? 200f : -45f, maxA = drag ? 270f : 45f;
            float boost = (psi + 20f) / 40f;
            _view.SetRotationZ(_view.Find("3rdperson_TurboDial"), -(minA + boost * (maxA - minA)));
        }

        /// <summary>
        /// Systems.HudMessage → GenericMessage::RequestGenericMessage(text, false, 0x8AB83EDB, no icon, Priority_4) [?: the
        /// port's callers (race over, speed trap, GPS, perfect launch, cop takedowns) do not pass a priority; Priority_4 is the
        /// takedown message's, so the exe-faithful PursuitBoard request of the same frame replaces the gameplay copy].
        /// </summary>
        public void Message(string text)
        {
            if (!this || _view == null || _msg == null) return;
            _msg.Request(text ?? "", false, HudGenericMessage.ScriptNormal, 0, 0, HudGenericMessage.Priority4);
        }

        /// <summary>
        /// HeatMeter (FeHeatMeter.cpp; PC Update 0x5666A0, mask 0x40004000 — on only with the minimap) and PursuitBoard
        /// (mask 0x100000, HudWidgets.cs), fed as LocalPlayer::UpdateHud does: the board only out of races or in a pursuit
        /// race. Heat = the pursuit's heat when above 0, else the car's; n = (int)heat, f = heat − n: 862824C9 turned
        /// 175 − 2·min(f, 0.5)·175, 4B2CBE1B 175 − 2·(f &gt; 0.5 ? f − 0.5 : 0)·175 (the PC code; the decomp's second half is
        /// wrong); heat ≥ 1: 0x41E1FEDC (f &lt; 0.5) else INIT on 7F91DA62, "x%d" printed into it, shown; else hidden; the icon
        /// 6F85ED55 INIT (no heat) / Warning 0xDA600155 (f &gt; 0.75) / ACTIVATE.
        /// </summary>
        void Pursuit()
        {
            if (_split != 0) return;
            var police = Systems.Police;
            var race = RaceManager.Active;
            bool feed = race == null || (police != null && police.IsPursuitRace);
            if (feed && _pursuit != null) _pursuit.Feed(police);
            if (_pursuit != null && On(0x100000)) _pursuit.Update();
            if (!On(0x40004000)) return;
            float pursuitHeat = police != null && police.InPursuit ? police.HeatValue : 0f;
            float h = pursuitHeat > 0f ? pursuitHeat : (GameSession.ActiveCar?.Heat ?? 0);
            int lvl = (int)h;
            float f = h - lvl;
            _view.SetMultiImageRot(_view.Find(0x862824C9), Mathf.Min(f, 0.5f) * 2f * -175f + 175f);
            _view.SetMultiImageRot(_view.Find(0x4B2CBE1B), (f > 0.5f ? f - 0.5f : 0f) * 2f * -175f + 175f);
            var x = _view.Find(0x7F91DA62);
            if (h >= 1f)
            {
                uint want = f < 0.5f ? 0x41E1FEDCu : FEView.Init;
                if (!_view.IsScriptSet(x, want)) _view.SetScript(x, want, true);
                _view.Printf(x, $"x{lvl}");
                _view.SetVisibleTree(x);
            }
            else _view.SetInvisible(x);
            var icon = _view.Find(0x6F85ED55);
            uint iconScript = h > 0 ? (f > 0.75f ? 0xDA600155u : 0x77031C70u) : FEView.Init;
            if (icon != null && !_view.IsScriptSet(icon, iconScript)) _view.SetScript(icon, iconScript, true);
        }

        /// <summary>speed.exe FUN_00569730: the dial for a max rpm (strict thresholds: exactly 7000 gets the 8000 dial).</summary>
        static int Dial(float maxRpm) => maxRpm < 7000 ? 7 : maxRpm < 8000 ? 8 : maxRpm < 9000 ? 9 : 10;

        /// <summary>speed.exe FUN_0057C000: the redline mask angle (degrees) for a dial and redline rpm.</summary>
        static float RedlineAngle(int dial, float red) => dial switch
        {
            7 => red >= 6500 ? 164.5f : red >= 6000 ? 149.5f : red >= 5500 ? 131.5f : 113.5f,
            8 => red >= 7500 ? 165f : red >= 7000 ? 152f : red >= 6500 ? 138f : red >= 6000 ? 123f : 110f,
            9 => red >= 8500 ? 166f : red >= 8000 ? 154f : red >= 7500 ? 140.5f : red >= 7000 ? 127f : 115f,
            _ => red >= 9500 ? 167f : red >= 9000 ? 156f : red >= 8500 ? 145f : red >= 8000 ? 134f : 123f,
        };

        FEView.Node _posTotal;

        /// <summary>The leaderboard rows are HudExtras.Leaderboard (FeLeaderBoard.cpp); the position block is RaceInformation.</summary>
        void Board(RaceManager race) { }

        void OnDestroy()
        {
            if (Systems.HudMessage == (System.Action<string>)Message) Systems.HudMessage = null;
            if (_canvas) Destroy(_canvas.gameObject);
        }
    }
}
