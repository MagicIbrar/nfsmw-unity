using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using NfsMw.Game;
using NfsMw.Game.Gameplay;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// The HUD minimap as the exe draws it: Frontend/HUD/FeMinimap.cpp (Minimap; PC ctor 0x59D500, Update 0x59DB50,
    /// SetupMinimap 0x579900, UpdateTrackMapArt 0x567550, UpdateElementArt 0x5675D0, UpdateCopElements 0x579E00,
    /// UpdateAiRacerElements 0x57A110, UpdatePlayer2Element 0x594EA0, UpdateGameplayIcons 0x57A300, AdjustForWidescreen
    /// 0x5678A0), FeMinimapStreamer.cpp (ChoppedMiniMapManager) and HudResourceManager::ChooseMinimapTextureName (0x57BEC0),
    /// all on the HUD package's own objects: TRACK_MAP1..4 (multi-images under the circular MINIMAP_MASK) take the 2×2
    /// block of 128-px map chops nearest the player, the TRACK_MAP group shifts by the player's offset in that block and
    /// turns about it, and the MMICON_* / gameplay icon images are placed around the map centre (1024 px per map width).
    /// Before round 9 this was [ours]: its own canvas tiles, a fitted world → map transform and its own icons and route.
    /// The widget's objects are registered in HudFeatures (mask 0x40010000, toggle bit 0x10000).
    /// </summary>
    public sealed class Minimap : MonoBehaviour
    {
        public PlayerCar Car;
        /// <summary>[ours, kept for GpsNavigation] The GPS path. The exe draws no route on the minimap (the GPS target's icon
        /// pulses, UpdateIconElement); nothing reads this any more.</summary>
        public static List<Vector3> Gps;

        // ---- TrackInfo calibration (World/TrackInfo.hpp; chunk 0x34201 in GLOBAL\GlobalB.lzc) ---------------------------

        static bool _calLoaded;
        // track 2000 "Most Wanted World" (harness feeeng9 trackinfo): TrackMapCalibrationUpperLeft, MapWidthMetres
        static float _calX = -1224.8894f, _calY = -1591.1449f, _calW = 6659.332f;

        static void LoadCalibration()
        {
            if (_calLoaded) return;
            _calLoaded = true;
            try
            {
                GameData.Mount();
                var path = Path.Combine(GameData.Root, "GLOBAL", "GlobalB.lzc");
                if (!File.Exists(path)) return;
                var d = GameFiles.ReadBundle(path);
                foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x34201))
                    for (int o = c.DataStart; o + 0x120 <= c.End; o += 0x120)
                        if (Chunks.U16(d, o + 0x8A) == 2000 && Chunks.F32(d, o + 0xB4) > 0)
                        {
                            _calX = Chunks.F32(d, o + 0xAC); _calY = Chunks.F32(d, o + 0xB0); _calW = Chunks.F32(d, o + 0xB4);
                            return;
                        }
            }
            catch (System.Exception e) { Debug.LogWarning("NFSMW: TrackInfo: " + e.Message); }
        }

        /// <summary>Minimap::ConvertPos (PC 0x567510): ((X − upper-left X) / width, (upper-left Y − Y) / width + 1) over the
        /// track map, X / Y = world (Unity x / z).</summary>
        public static Vector2 ConvertPos(Vector3 unityPos)
        {
            LoadCalibration();
            return new Vector2((unityPos.x - _calX) / _calW, (_calY - unityPos.z) / _calW + 1f);
        }

        /// <summary>The 1024-px map pixel of a world position (ConvertPos × 1024); CareerScreenKit's world map uses it.</summary>
        public static Vector2 MapPoint(Vector3 unityPos) => ConvertPos(unityPos) * 1024f;

        // ---- the chopped maps (ChoppedMiniMapManager) ------------------------------------------------------------------

        static string _loadedHeader;
        static readonly Dictionary<string, Texture2D> _chops = new Dictionary<string, Texture2D>();

        /// <summary>
        /// LoadingCompleteCallback phase 1 + ChoppedMiniMapManager: TRACKS\L2RA\&lt;header&gt;.BIN holds 64 chunks 0x3A100 (an LZ
        /// header + an embedded texture pack with &lt;header&gt;_CHOP&lt;n&gt;, 128×128); the textures by upper-case name.
        /// The exe decompresses the four chops in use (UncompressMaps); the port keeps the whole set of the current map.
        /// </summary>
        static void LoadMap(string header)
        {
            if (header == _loadedHeader) return;
            foreach (var t in _chops.Values) if (t) Destroy(t);
            _chops.Clear();
            _loadedHeader = header;
            if (header == null) return;
            GameData.Mount();
            var path = Path.Combine(GameData.Root, "TRACKS", "L2RA", header + ".BIN");
            if (!File.Exists(path)) return;
            var d = File.ReadAllBytes(path);
            foreach (var c in Chunks.Read(d, 0, d.Length))
            {
                if (c.Id != 0x3A100) continue;
                var b = Compression.IsCompressed(d, c.DataStart) ? Compression.Decompress(d, c.DataStart) : d[c.DataStart..c.End];
                foreach (var t in TexturePack.ReadAll(b, 0, b.Length))
                {
                    var tex = TextureLibrary.ToUnity(t);
                    tex.wrapMode = TextureWrapMode.Clamp;
                    FEView.RegisterTextureSource(tex, t);   // AlphaBlend 2 (additive), as every chop ships
                    _chops[t.Name.ToUpperInvariant()] = tex;
                }
            }
        }

        /// <summary>For the editor's map check (NFSMWFlowPreview.MapCheck): the 64 chops of MINI_MAP.BIN, row-major.</summary>
        static Texture2D[] LoadTiles()
        {
            LoadMap("MINI_MAP");
            var r = new Texture2D[64];
            for (int i = 0; i < 64; i++) _chops.TryGetValue("MINI_MAP_CHOP" + i, out r[i]);
            return r;
        }

        /// <summary>
        /// HudResourceManager::ChooseMinimapTextureName (FEPkg_Hud.cpp:180): not for the drag HUD; roaming: MINI_MAP_UNLOCK_1
        /// from career bin 13 on, MINI_MAP_UNLOCK_2 from bin 9, else MINI_MAP; a race: a pursuit race by its region (college
        /// UNLOCK_1, coastal UNLOCK_2, else MINI_MAP), any other MINI_MAP_&lt;event id&gt; ('.' → '_', FixDot 0x56E540), upper case;
        /// null when TRACKS\L2RA\&lt;name&gt;.BIN does not exist (the tiles then get textures that are not loaded: blank).
        /// [?] The exe picks the map when the HUD loads; the port checks every frame (a race starting or ending swaps it).
        /// </summary>
        static string ChooseMapName(RaceManager race)
        {
            string name;
            if (race == null || race.Event == null)
            {
                int bin = GameSession.Mode == GameMode.Career ? GameSession.Profile?.Bin ?? 0 : 0;
                name = bin >= 13 ? "MINI_MAP_UNLOCK_1" : bin >= 9 ? "MINI_MAP_UNLOCK_2" : "MINI_MAP";
            }
            else if (Systems.Police != null && Systems.Police.IsPursuitRace)
                name = race.Event.RegionIndex == 0 ? "MINI_MAP_UNLOCK_1" : race.Event.RegionIndex == 1 ? "MINI_MAP_UNLOCK_2" : "MINI_MAP";
            else name = ("MINI_MAP_" + (race.Event.EventId ?? "")).Replace('.', '_').ToUpperInvariant();
            if (_exists.TryGetValue(name, out bool ok)) return ok ? name : null;
            GameData.Mount();
            ok = File.Exists(Path.Combine(GameData.Root, "TRACKS", "L2RA", name + ".BIN"));
            _exists[name] = ok;
            return ok ? name : null;
        }

        static readonly Dictionary<string, bool> _exists = new Dictionary<string, bool>();

        // ---- Minimap::kGameplayIconInfo (PC 0x8F3AB8): per GIcon type the world-map item bit and the element name ------

        static readonly (uint item, string element)[] IconInfo =
        {
            (0, null), (0x100, "MMICON_SPRINT_%d"), (0x80, "MMICON_CIRCUIT_%d"), (0x400, "MMICON_DRAG_%d"), (0x200, "MMICON_KNOCKOUT_%d"),
            (0x1000, "MMICON_TOLLBOOTH_%d"), (0x800, "MMICON_SPEEDTRAP_%d"), (0x8000, "MINIMAP_ICON_EVENT_RIVAL"),
            (0x200000, "SAFEHOUSEINDICATOR_%d"), (0x800000, "CAR_LOT_%d"), (0x400000, "SHOP_%d"), (0x2000000, "HIDING_SPOT_%d"),
            (0x4000000, "PURSUIT_BREAKER_%d"), (0x100000, "SPEED_TRAP_CAM_%d"), (0x100000, "SPEED_TRAP_RACE_CAM_%d"),
            (0x1000000, "MMICON_CHECKPOINT"), (0, null),
        };
        const int MaxElements = 8, MapPieces = 4;
        const float MinimapMaxSpeed = 100f;            // MinimapMaxSpeed 0x8F3AB0
        const bool ShowNonPursuitCops = false;         // MinimapShowNonPursuitCops 0x91CF00 (bss)
        const bool ShowPursuitCops = true;             // MinimapShowPursuitCops 0x8F3AAC

        static uint H(string s) => EaHash.HashUpper(s);
        static string Fmt(string element, int i) => element.Replace("%d", i.ToString());

        /// <summary>The Minimap ctor's Register* calls (for HudFeatures): (group, hashes).</summary>
        public static List<(bool group, uint[] h)> Registrations()
        {
            var r = new List<(bool, uint[])>();
            void O(string s) => r.Add((false, new[] { H(s) }));
            for (int i = 0; i < MapPieces; i++) O("TRACK_MAP" + (i + 1));
            O("TRACK_MAP"); O("MINIMAP_NORTH_INDICATOR"); O("PLAYERCARINDICATOR"); O("PLAYERCARINDICATOR2"); O("TRACKMAPTARGETRING"); O("MAP_COLOR_TINT");
            r.Add((true, new[] { H("HELICOPTER_ICON_GROUP") }));
            O("HELICOPTER_LINE_OF_SIGHT");
            for (int i = 0; i < MaxElements; i++)
            {
                O("MMICON_COPCAR_" + i); O("MMICON_AIRACER_" + i);
                foreach (var (item, element) in IconInfo)
                    if (item != 0 && (i == 0 || element.Contains("%d"))) O(Fmt(element, i));
            }
            O("MMICON_CHECKPOINT");
            r.Add((false, new[] { 0xE8741681u }));
            return r;
        }

        // ---- binding -----------------------------------------------------------------------------------------------

        FEView _v;
        int _split;
        readonly FEView.Node[] _art = new FEView.Node[MapPieces];
        readonly Vector4[] _artUVs = new Vector4[MapPieces];   // each piece's layer-0 (mask) UVs: TL x, y, BR x, y
        FEView.Node _layout, _north, _player, _player2, _heli, _heliLos, _checkpoint, _gpsGlow;
        readonly FEView.Node[] _cops = new FEView.Node[MaxElements], _racers = new FEView.Node[MaxElements];
        readonly FEView.Node[,] _icons = new FEView.Node[17, MaxElements];
        Vector3 _mapDefaultPos;
        Vector2 _trackMapCentre, _trackTarget;
        float _speedZoom, _polyRotation, _pivotX, _pivotY;
        bool _rotate;
        int _copFlash = -1;
        bool _bound;

        /// <summary>GameHud ticks the widget (Tick) in FEngHud::Update's order; the editor's HUD preview still sends LateUpdate.</summary>
        void LateUpdate() { }

        /// <summary>A minimap component on the HUD object; Bind attaches it to the package.</summary>
        public static Minimap Create(Transform parent, PlayerCar car = null, int split = 0)
        {
            var go = new GameObject("Minimap");
            go.transform.SetParent(parent, false);
            var map = go.AddComponent<Minimap>();
            map.Car = car;
            map._split = split;
            return map;
        }

        /// <summary>The Minimap ctor: the objects, the pieces' mask UVs (GetUVs(0)), the gameplay icons made invisible,
        /// TRACK_MAP's position as the default and its centre (FEngGetCenter) as the map centre.</summary>
        public void Bind(FEView view, int split)
        {
            _v = view;
            _split = split;
            _bound = view != null;
            if (!_bound) return;
            for (int i = 0; i < MapPieces; i++)
            {
                _art[i] = view.Find("TRACK_MAP" + (i + 1));
                var d = _art[i]?.Data ?? default;
                _artUVs[i] = new Vector4(d.M1U0, d.M1V0, d.M1U1, d.M1V1);
            }
            _layout = view.Find("TRACK_MAP");
            _north = view.Find("MINIMAP_NORTH_INDICATOR");
            _player = view.Find("PLAYERCARINDICATOR");
            _player2 = view.Find("PLAYERCARINDICATOR2");
            _heli = view.Find("HELICOPTER_ICON_GROUP");
            _heliLos = view.Find("HELICOPTER_LINE_OF_SIGHT");
            for (int i = 0; i < MaxElements; i++)
            {
                _cops[i] = view.Find("MMICON_COPCAR_" + i);
                _racers[i] = view.Find("MMICON_AIRACER_" + i);
                for (int t = 0; t < IconInfo.Length; t++)
                {
                    var (item, element) = IconInfo[t];
                    if (item == 0) continue;
                    _icons[t, i] = i == 0 || element.Contains("%d") ? view.Find(Fmt(element, i)) : null;
                    view.SetInvisible(_icons[t, i]);
                }
            }
            _checkpoint = view.Find("MMICON_CHECKPOINT");
            _gpsGlow = view.Find(0xE8741681);
            _speedZoom = 0f;
            _polyRotation = 0f;
            _pivotX = _pivotY = 0f;
            _copFlash = -1;
            if (_layout != null)
            {
                _mapDefaultPos = new Vector3(_layout.Data.X, _layout.Data.Y, _layout.Data.Z);
                _trackMapCentre = view.GetCenter(_layout);
            }
        }

        /// <summary>Minimap::AdjustForWidescreen (FEngHud::SetWideScreenMode on a change): ±120 on the centre, the pieces and
        /// the player indicator (whose y goes to the centre); MinimapPivotX −120 when moving outwards, else 0.</summary>
        public void AdjustForWidescreen(bool outwards)
        {
            if (!_bound) return;
            float adj = outwards ? -120f : 120f;
            _pivotX = outwards ? -120f : 0f;
            _trackMapCentre.x += adj;
            foreach (var a in _art) if (a != null) a.Data.X += adj;
            if (_player != null) { _player.Data.X += adj; _player.Data.Y = _trackMapCentre.y; }
        }

        // ---- Minimap::Update ----------------------------------------------------------------------------------------

        static Vector2 Pos2D(Transform t) => new Vector2(t.position.x, t.position.z);
        static Vector2 Dir2D(Transform t) => new Vector2(t.forward.x, t.forward.z);
        /// <summary>bAngToDeg(bATan(dir.y, dir.x)): degrees 0..360 clockwise from the map's north.</summary>
        static float Heading(Vector2 dir)
        {
            float a = Mathf.Atan2(dir.x, dir.y) * Mathf.Rad2Deg;
            return a < 0 ? a + 360f : a;
        }

        /// <summary>
        /// Minimap::Update, when the widget is visible: rotate with the player unless split screen or the minimap mode (in a
        /// race RacingMiniMapMode, else ExploringMiniMapMode) is 0; SetupMinimap (with last frame's zoom); the heading;
        /// the player's map position; the speed zoom 1 − speed/100 clamped up to 1 (so always 1); UpdateTrackMapArt; heading 0
        /// when not rotating; then the cops, AI racers, player 2 and the gameplay icons.
        /// </summary>
        public void Tick(bool visible, PlayerCar car, PlayerCar player2)
        {
            if (!_bound || !visible || !car) return;
            var race = RaceManager.Active;
            LoadMap(ChooseMapName(race));
            var gs = FEDb.Data.Gameplay;
            _rotate = _split == 0 && (race != null ? gs.RacingMiniMapMode : gs.ExploringMiniMapMode) != 0;
            SetupMinimap(car);
            _polyRotation = Heading(Dir2D(car.transform));
            _trackTarget = ConvertPos(car.transform.position);
            float speed = Mathf.Clamp(Mathf.Abs(car.SpeedMps), 0f, MinimapMaxSpeed);
            _speedZoom = 1f - speed / MinimapMaxSpeed;
            if (_speedZoom < 1f) _speedZoom = 1f;
            UpdateTrackMapArt();
            if (!_rotate) _polyRotation = 0f;
            UpdateCopElements();
            UpdateAiRacerElements(race, car);
            if (_split != 0 && player2) UpdateElementArt(Pos2D(player2.transform), Dir2D(player2.transform), _player2, false);
            UpdateGameplayIcons(car);
        }

        /// <summary>
        /// Minimap::SetupMinimap: the player's map position × 8 chops; the 2×2 block of chops nearest it (the decimals turned
        /// into the offset from the block's centre); each piece gets &lt;header&gt;_CHOP&lt;n&gt;; the pieces' base UVs
        /// (zoom − 1 .. 1 per corner) and their mask UVs shifted by −offset·zoom; TRACK_MAP's position = default − offset·128
        /// and its pivot = offset·128 + (MinimapPivotX, MinimapPivotY) — written to the data directly, as the exe does.
        /// </summary>
        void SetupMinimap(PlayerCar car)
        {
            const int numChops = 8;
            var map = ConvertPos(car.transform.position) * numChops;
            int xs = (int)map.x, ys = (int)map.y;
            float xd = map.x - xs, yd = map.y - ys;
            var chops = new int[4];
            if (xd < 0.5f)
            {
                if (yd < 0.5f) { chops[0] = (ys - 1) * numChops + xs - 1; chops[1] = (ys - 1) * numChops + xs; chops[2] = ys * numChops + xs - 1; chops[3] = ys * numChops + xs; }
                else { chops[0] = ys * numChops + xs - 1; chops[1] = ys * numChops + xs; chops[2] = (ys + 1) * numChops + xs - 1; chops[3] = (ys + 1) * numChops + xs; yd -= 1f; }
            }
            else
            {
                if (yd < 0.5f) { chops[0] = (ys - 1) * numChops + xs; chops[1] = (ys - 1) * numChops + xs + 1; chops[2] = ys * numChops + xs; chops[3] = ys * numChops + xs + 1; xd -= 1f; }
                else { chops[0] = ys * numChops + xs; chops[1] = ys * numChops + xs + 1; chops[2] = (ys + 1) * numChops + xs; chops[3] = (ys + 1) * numChops + xs + 1; xd -= 1f; yd -= 1f; }
            }
            for (int i = 0; i < MapPieces; i++)
            {
                string name = $"{_loadedHeader}_CHOP{chops[i]}";
                Texture2D tex = null;
                if (_loadedHeader != null && chops[i] >= 0) _chops.TryGetValue(name, out tex);
                _v.SetTextureHash(_art[i], EaHash.HashUpper(name), tex);
            }
            float uvScale = _speedZoom - 1f;
            float xDisp = xd * _speedZoom, yDisp = yd * _speedZoom;
            _v.SetUVs(_art[0], uvScale, uvScale, 1f, 1f);
            _v.SetUVs(_art[1], 0f, uvScale, 1f - uvScale, 1f);
            _v.SetUVs(_art[2], uvScale, 0f, 1f, 1f - uvScale);
            _v.SetUVs(_art[3], 0f, 0f, 1f - uvScale, 1f - uvScale);
            for (int i = 0; i < MapPieces; i++)
            {
                var uv = _artUVs[i];
                _v.SetMultiImageUVs(_art[i], 0, uv.x - xDisp, uv.y - yDisp, uv.z - xDisp, uv.w - yDisp);
            }
            if (_layout != null)
            {
                const float sectionSize = 128f;
                _layout.Data.X = _mapDefaultPos.x - xDisp * sectionSize;
                _layout.Data.Y = _mapDefaultPos.y - yDisp * sectionSize;
                _layout.Data.Z = _mapDefaultPos.z;
                _layout.Data.PivotX = xDisp * sectionSize + _pivotX;
                _layout.Data.PivotY = yDisp * sectionSize + _pivotY;
                _layout.Data.PivotZ = 0f;
            }
        }

        /// <summary>Minimap::UpdateTrackMapArt: static map → the player indicator turns (heading), the map and north stay
        /// 0; rotating → the indicator 0, the map and north −heading (FEngSetRotationZ).</summary>
        void UpdateTrackMapArt()
        {
            if (!_rotate)
            {
                _v.SetRotationZ(_player, _polyRotation);
                _v.SetRotationZ(_layout, 0f);
                _v.SetRotationZ(_north, 0f);
            }
            else
            {
                _v.SetRotationZ(_player, 0f);
                _v.SetRotationZ(_layout, -_polyRotation);
                _v.SetRotationZ(_north, -_polyRotation);
            }
        }

        /// <summary>
        /// Minimap::UpdateElementArt: (map position − player's) · zoom turned by −heading; beyond 0.06 of the map width it is
        /// pinned to the 0.06 rim, fading from 0.125 to nothing at 0.23 (a pulsing GPS icon stays opaque); centred at
        /// map centre + 1024 · offset (FEngSetCenter), made visible, turned to its own heading − the map's, alpha set on its
        /// colour; a GPS icon also gets the selection glow E8741681 there.
        /// </summary>
        void UpdateElementArt(Vector2 pos, Vector2 dir, FEView.Node art, bool pulse)
        {
            if (art == null) return;
            var mp = ConvertPos(new Vector3(pos.x, 0f, pos.y));
            float ex = (mp.x - _trackTarget.x) * _speedZoom, ey = (mp.y - _trackTarget.y) * _speedZoom;
            float sa = Mathf.Sin(_polyRotation * Mathf.Deg2Rad), ca = Mathf.Cos(_polyRotation * Mathf.Deg2Rad);
            float rx = ex * ca + ey * sa, ry = ey * ca - ex * sa;
            float distance = Mathf.Sqrt(rx * rx + ry * ry), alpha = 1f;
            if (distance > 0f && distance > 0.06f)
            {
                float k = 0.06f / distance;
                rx *= k; ry *= k;
                if (distance > 0.125f) alpha = 1f - (distance - 0.125f) * 9.523809f;
                if (distance > 0.23f) alpha = 0f;
                if (pulse) alpha = 1f;
                distance = 0.06f;
            }
            float sx = _trackMapCentre.x + rx * 1024f, sy = _trackMapCentre.y + ry * 1024f;
            _v.SetCenter(art, sx, sy);
            _v.SetVisibleTree(art);
            _v.SetRotationZ(art, Heading(dir) - _polyRotation);
            uint color = _v.GetColor(art) & 0x00FFFFFFu;
            _v.SetColor(art, color | (uint)((int)(alpha * 255f) << 24));
            if (pulse)
            {
                _v.SetVisibleTree(_gpsGlow);
                _v.SetCenter(_gpsGlow, sx, sy);
            }
        }

        /// <summary>
        /// Minimap::UpdateCopElements: the flash counter cycles 0..8; with the player in a pursuit that is not bailed (or the
        /// show-all switch, off), every active cop: the helicopter on HELICOPTER_ICON_GROUP and the line of sight (TRACKING
        /// while it saw its target within 0.25 s, else INIT, when it was within engagement range), cars on MMICON_COPCAR_n
        /// when within engagement range (MinimapShowPursuitCops), coloured 0xFFCCCCCC, flashing blue 0xFF0000FF (counter
        /// 0..2) / red 0xFFA00000 (4..6) while in pursuit; at most 8 cars; the rest and an absent helicopter invisible.
        /// [?] The port's helicopter (CopHeli) counts as within engagement range while it is in the pursuit.
        /// </summary>
        void UpdateCopElements()
        {
            int artIter = 0;
            bool heliFound = false;
            if (_copFlash++ > 7) _copFlash = 0;
            var police = Systems.Police;
            if (police != null && (ShowNonPursuitCops || police.InPursuit))
            {
                var heli = police.HudHelicopter;
                if (heli && !heli.Finished)
                {
                    uint tracking = EaHash.HashUpper("TRACKING");
                    if (heli.TimeSinceSeen <= 0.25f) { if (!_v.IsScriptSet(_heliLos, tracking)) _v.SetScript(_heliLos, tracking, true); }
                    else if (!_v.IsScriptSet(_heliLos, FEView.Init)) _v.SetScript(_heliLos, FEView.Init, true);
                    heliFound = true;
                    UpdateElementArt(Pos2D(heli.transform), Dir2D(heli.transform), _heli, false);
                    UpdateElementArt(Pos2D(heli.transform), Dir2D(heli.transform), _heliLos, false);
                }
                foreach (var car in police.CopCars)
                {
                    if (artIter > 7) break;
                    var cc = car ? car.GetComponent<NfsMw.Game.Police.CopCar>() : null;
                    if (cc == null) continue;
                    if (ShowNonPursuitCops || (cc.WithinEngagementRadius && ShowPursuitCops))
                        UpdateElementArt(Pos2D(car.transform), Dir2D(car.transform), _cops[artIter], false);
                    else _v.SetInvisible(_cops[artIter]);
                    uint flash = 0xFFCCCCCC;
                    if (cc.InPursuit)
                    {
                        if (_copFlash < 3) flash = 0xFF0000FF;
                        else if (_copFlash >= 4 && _copFlash < 7) flash = 0xFFA00000;
                    }
                    _v.SetColor(_cops[artIter], flash);
                    artIter++;
                }
            }
            for (int i = artIter; i < MaxElements; i++) _v.SetInvisible(_cops[i]);
            if (!heliFound) { _v.SetInvisible(_heli); _v.SetInvisible(_heliLos); }
        }

        /// <summary>Minimap::UpdateAiRacerElements: every active AI racer (VEHICLE_AIRACERS) on MMICON_AIRACER_n, the rest invisible.</summary>
        void UpdateAiRacerElements(RaceManager race, PlayerCar player)
        {
            int artIter = 0;
            if (race != null)
                foreach (var e in race.Entrants)
                {
                    if (e.Human || !e.Car || e.Car == player || !e.Car.gameObject.activeInHierarchy || artIter >= MaxElements) continue;
                    UpdateElementArt(Pos2D(e.Car.transform), Dir2D(e.Car.transform), _racers[artIter], false);
                    artIter++;
                }
            for (int i = artIter; i < MaxElements; i++) _v.SetInvisible(_racers[i]);
        }

        readonly List<(GIcon icon, int dist)> _sorted = new List<(GIcon, int)>();

        /// <summary>
        /// Minimap::UpdateGameplayIcons: the glow hidden; GManager::GatherVisibleIcons (PC 0x5E4940: the visible icons
        /// sorted by squared 2D distance to the player) placed in order, up to 8 per type, when the type's world-map item
        /// is enabled (GameplaySettings::IsMapItemEnabled 0x56D540: MapItems &amp; item); UpdateIconElement turns them 0
        /// and pulses the GPS target; unused images invisible.
        /// [?] The icons come from the Lua triggers and GManager's pool as the world map reads them (WorldMapScreen
        /// AddTriggerIcons: speed traps shown while events may be engaged); the GPS target is an icon flagged 0x80 or within
        /// 30 m of the GPS target; MapItems is the profile's (the world map's toggles).
        /// </summary>
        void UpdateGameplayIcons(PlayerCar player)
        {
            _v.SetInvisible(_gpsGlow);
            var placed = new int[IconInfo.Length];
            var set = NfsMw.Game.Lua.LuaFlow.Triggers?.Triggers;
            _sorted.Clear();
            if (set != null)
            {
                var pp = Pos2D(player.transform);
                foreach (var ic in set.Icons)
                {
                    if (ic == null || ic.Type <= 0 || ic.Type >= IconInfo.Length) continue;
                    bool shown = ic.Type == GIcon.SpeedTrap ? Availability.AllowEngageEvents : GIcon.IsVisible(ic.Type, ic.Flags);
                    if (!shown) continue;
                    var d = new Vector2(ic.Position.x, ic.Position.z) - pp;
                    _sorted.Add((ic, (int)(d.x * d.x + d.y * d.y)));
                }
                _sorted.Sort((a, b) => a.dist.CompareTo(b.dist));
            }
            int mapItems = GameSession.Profile?.MapItems ?? -1;
            bool gps = GpsNavigation.Engaged && GpsNavigation.Instance != null;
            foreach (var (ic, _) in _sorted)
            {
                var (item, _) = IconInfo[ic.Type];
                if (item == 0 || placed[ic.Type] >= MaxElements) continue;
                if (((uint)mapItems & item) == 0) continue;
                var image = _icons[ic.Type, placed[ic.Type]];
                placed[ic.Type]++;
                if (image == null) continue;
                // UpdateIconElement: not an area unlock, the GPS down and the icon GPSing → GPSing cleared
                if (ic.Type != GIcon.AreaUnlock && !gps && (ic.Flags & GIcon.FlagGpsing) != 0) ic.Flags &= ~GIcon.FlagGpsing;
                bool gpsing = (ic.Flags & GIcon.FlagGpsing) != 0 || (gps && (GpsNavigation.Instance.Target - ic.Position).sqrMagnitude < 30f * 30f);
                UpdateElementArt(new Vector2(ic.Position.x, ic.Position.z), new Vector2(1f, 0f), image, gpsing);
                _v.SetRotationZ(image, 0f);
            }
            for (int t = 0; t < IconInfo.Length; t++)
                for (int i = placed[t]; i < MaxElements; i++)
                    if (_icons[t, i] != null) _v.SetInvisible(_icons[t, i]);
        }
    }
}
