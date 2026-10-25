using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using NfsMw.Game.Gameplay;
using NfsMw.Game.Police;
using UnityEngine;
using UnityEngine.InputSystem;
using K = NfsMw.UI.CareerScreenKit;

namespace NfsMw.UI
{
    /// <summary>
    /// The world map (WorldMapMain.fng, uiWorldMap.cpp; specs/fe_career_screens.md §1). TRACK_MAP shows the city through
    /// the track-map streamer; the package's icon pools (MMICON_SPRINT_n, SAFEHOUSEINDICATOR_n, MMICON_COPCAR_n, …) are
    /// placed over it for the current view (NAVIGATION: shops, safehouses, car lots; EVENT: the races on offer;
    /// PURSUIT: safehouses and cops). A cursor snaps to icons; Enter on an icon offers the GPS. Zoom levels 1 / 2 / 3.3 / 5
    /// (clamped, PC 0x552CB0), mouse wheel zoom and left-drag pan as speed.exe 0x552DA0 / 0x511B80.
    /// Keys [?]: arrows / WASD / right stick move the cursor, mouse moves it directly; wheel, Q / E or the shoulders zoom;
    /// 4 or Tab switches NAVIGATION / EVENT (PC_key_4 'Exit' glyph); 2 opens the icon filter list (PC_key_2 'Toggle').
    /// </summary>
    public sealed class WorldMapScreen : FEScreen
    {
        public override string Package => "WorldMapMain";

        enum MapView { Navigation = 0, Event = 1, Race = 2, Pursuit = 3 }

        /// <summary>kGameplayIconInfo (exe 0x8F3AB8): GIcon type, item flag, pool element, world-map title, big icon texture.</summary>
        sealed class IconInfo
        {
            public int Type, Flag;
            public string Element, Title;
            public uint TitleLabel, Tex;
            public IconInfo(int type, int flag, string element, uint label, string title, uint tex)
            { Type = type; Flag = flag; Element = element; TitleLabel = label; Title = title; Tex = tex; }
        }

        static readonly IconInfo[] Icons =
        {
            new IconInfo(1, 0x100, "MMICON_SPRINT_{0}", 0xB94FD70E, "Sprint", 0x2521E5EB),
            new IconInfo(2, 0x80, "MMICON_CIRCUIT_{0}", 0x034FA2C1, "Circuit", 0xE9638D3E),
            new IconInfo(3, 0x400, "MMICON_DRAG_{0}", 0x6F547E4C, "Drag", 0xAAAB31E9),
            new IconInfo(4, 0x200, "MMICON_KNOCKOUT_{0}", 0x4930F5FC, "Lap Knockout", 0x3A015595),
            new IconInfo(5, 0x1000, "MMICON_TOLLBOOTH_{0}", 0xA15E4505, "Tollbooth", 0x1A091045),
            new IconInfo(6, 0x800, "MMICON_SPEEDTRAP_{0}", 0xEE7386EA, "Speedtrap Race", 0x66C9A7B6),
            new IconInfo(7, 0x8000, "MINIMAP_ICON_EVENT_RIVAL", 0x213CC8D1, "Challenge", 0x9F562D57),
            new IconInfo(8, 0x200000, "SAFEHOUSEINDICATOR_{0}", 0xDED7B298, "Safe House", 0x0ED39F69),
            new IconInfo(9, 0x800000, "CAR_LOT_{0}", 0xB9D71959, "Car Lot", 0x4EAEE18B),
            new IconInfo(10, 0x400000, "SHOP_{0}", 0xE0C8090F, "Shop", 0x0CF07089),
            new IconInfo(11, 0x2000000, "HIDING_SPOT_{0}", 0xA31E817A, "Hiding Spot", 0xF172B456),
            new IconInfo(12, 0x4000000, "PURSUIT_BREAKER_{0}", 0xDAC9D619, "Pursuit Breaker", 0xD01A771E),
            new IconInfo(13, 0x100000, "SPEED_TRAP_CAM_{0}", 0x5BDCF050, "Photo Ticket", 0x66C9A7B6),
        };
        // cops, the helicopter and roadblocks (AddCops :949, AddRoadBlocks :988): eWorldMapItemType bits (FEDatabase.hpp:80
        // WMIT_COP_CAR 1<<2, WMIT_COP_HELI 1<<3, WMIT_ROADBLOCK 1<<5; the player car is 1<<0) and their toggle labels
        // 0xEAD6EF6C / 0xEAD9BD85 / 0x411F1F86
        const int CopFlag = 0x4, HeliFlag = 0x8, RoadblockFlag = 0x20, CopType = 100, HeliType = 101, RoadblockType = 102;
        const float MaxZoom = 5f, SnapDist = 20f;
        static readonly float[] ZoomFactors = { 1f, 2f, 3.3f, 5f };
        static readonly (uint label, string text)[] ZoomLabels = { (0x213587BF, "Zoom All"), (0x0A9BE7D7, "Zoom 1.5X"), (0x0A9BE7D8, "Zoom 2.0X"), (0x0A9BE7DA, "Zoom 3.0X") };

        sealed class Item
        {
            public FEView.Node Node;
            public Vector2 InitialPos, InitialSize;
            public Vector3 World;
            public int Type, Flag;
            public IconInfo Info;
            public bool Hidden, Player;
            public PlayerCar Cop;
            /// <summary>HeliItem (uiWorldMap.hpp:172): the chopper's icon group plus its line-of-sight cone image.</summary>
            public CopHeli Heli;
            public FEView.Node Cone;
            public int Flash;
        }

        /// <summary>The icon the GPS is leading to (GIcon::mGPSingIcon), and the errand drawing the route.</summary>
        public static Vector3? GpsTarget { get; private set; }
        static bool _gpsHooked;

        TrackMapStreamer _map;
        Vector2 _mapTopLeft, _mapSize, _centre;
        readonly List<Item> _items = new List<Item>();
        readonly Dictionary<string, int> _poolUsed = new Dictionary<string, int>();
        readonly List<int> _toggleTypes = new List<int>();
        MapView _view;
        int _zoom, _toggleRow;
        bool _toggleMode, _cursorMoving, _leftHeld;
        Vector2 _cursor, _cursorFrom;
        Item _selected;
        FEView.Node _cursorNode;
        Vector2? _lastMouse;

        public override void Open()
        {
            var p = GameSession.Profile;
            var mapNode = View.Find("TRACK_MAP");
            _map = new TrackMapStreamer(View, mapNode, K.RegionMapName()) { Duration = 0.5f };
            if (mapNode != null)
            {
                _mapSize = new Vector2(mapNode.Data.SizeX, mapNode.Data.SizeY);
                _centre = K.AbsPos(mapNode);
                _mapTopLeft = _centre - _mapSize / 2;
            }
            else { _mapSize = new Vector2(340, 340); _centre = new Vector2(-111, 11); _mapTopLeft = _centre - _mapSize / 2; }
            _cursorNode = View.Find(0xF156F6C5);
            _cursor = _cursorNode != null ? K.AbsPos(_cursorNode) : _centre;   // the cursor starts where the package puts it (Setup :854)
            View.SetVisible(_cursorNode, true);
            bool pursuit = Systems.Police != null && Systems.Police.InPursuit;
            _view = pursuit ? MapView.Pursuit : (MapView)Mathf.Clamp(p?.MapView ?? 0, 0, 1);
            _zoom = Mathf.Clamp(pursuit ? p?.MapPursuitZoom ?? 1 : p?.MapZoom ?? 0, 0, 3);
            HideAllPools();
            Populate();
            PanToPlayer();
            _map.SetZoom(1f / ZoomFactors[_zoom]);
            RefreshHeader();
        }

        public override void Close()
        {
            var p = GameSession.Profile;
            p?.Save();
            // ~WorldMap (:190) also calls IHud::RefreshMiniMapItems [?: the port's minimap does not filter by MapItems]
        }

        // ---- items ----------------------------------------------------------------------------------------------

        void HideAllPools()
        {
            foreach (var n in View.Nodes)
            {
                string name = n.Name;
                if (name.StartsWith("MMICON_") || name.StartsWith("SAFEHOUSEINDICATOR_") || name.StartsWith("CAR_LOT_") || name.StartsWith("SHOP_") ||
                    name.StartsWith("HIDING_SPOT_") || name.StartsWith("PURSUIT_BREAKER_") || name.StartsWith("SPEED_TRAP_") ||
                    name == "MINIMAP_ICON_EVENT_RIVAL" || name == "PLAYERCARINDICATOR" || name == "HELICOPTER_ICON_GROUP" || name == "HELICOPTER_LINE_OF_SIGHT" ||
                    n.Obj.Hash == 0x69585E84 || (n.Obj.Hash >= 0x393C39FE && n.Obj.Hash <= 0x393C3A02))
                    View.SetVisible(n, false);
            }
            for (int i = 1; i <= 7; i++) View.SetVisible(View.Find("OPTION_NAME_" + i), false);
            for (uint g = 0xC9784C95; g <= 0xC9784C9B; g++) View.SetVisible(View.Find(g), false);
        }

        FEView.Node NextPool(string element)
        {
            if (!element.Contains("{0}"))
            {
                if (_poolUsed.ContainsKey(element)) return null;
                _poolUsed[element] = 1;
                return View.Find(element);
            }
            _poolUsed.TryGetValue(element, out int n);
            var node = View.Find(string.Format(element, n));
            if (node == null && n == 0) node = View.Find(string.Format(element, 1));   // SAFEHOUSEINDICATOR starts at 0, others too [?]
            _poolUsed[element] = n + 1;
            return node;
        }

        Item Add(FEView.Node node, Vector3 world, int type, int flag, IconInfo info)
        {
            if (node == null) return null;
            var uv = K.MapUV(world);
            var it = new Item
            {
                Node = node, World = world, Type = type, Flag = flag, Info = info,
                InitialPos = _mapTopLeft + new Vector2(uv.x * _mapSize.x, uv.y * _mapSize.y),
                InitialSize = new Vector2(node.Data.SizeX, node.Data.SizeY),
            };
            int mask = GameSession.Profile?.MapItems ?? -1;
            it.Hidden = flag != 0 && (mask & flag) == 0;
            View.SetVisible(node, !it.Hidden);
            View.Play(node, FEView.Init);
            _items.Add(it);
            return it;
        }

        void AddIcon(IconInfo info, Vector3 world)
        {
            if (Add(NextPool(info.Element), world, info.Type, info.Flag, info) != null && !_toggleTypes.Contains(info.Type)) _toggleTypes.Add(info.Type);
        }

        /// <summary>Populate (:1053 / :1060 / :1073): the player car, then this view's icons.</summary>
        void Populate()
        {
            // ClearItems (:592): hide, ResetSize (the pool keeps its authored size for the next use), drop
            foreach (var it in _items)
            {
                it.Node.Data.SizeX = it.InitialSize.x; it.Node.Data.SizeY = it.InitialSize.y;
                View.SetVisible(it.Node, false);
                if (it.Cone != null) View.SetVisible(it.Cone, false);
            }
            _items.Clear();
            _poolUsed.Clear();
            _toggleTypes.Clear();
            _selected = null;
            var car = GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;
            if (car)
            {
                var pl = Add(View.Find("PLAYERCARINDICATOR"), car.transform.position, 0, 0, null);
                if (pl != null)
                {
                    pl.Player = true;
                    var f = car.transform.forward;
                    SetAngle(pl.Node, Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg);
                }
            }
            bool career = GameSession.Mode == GameMode.Career && GameSession.Career != null && GameSession.Profile != null;
            int bin = GameSession.Profile?.Bin ?? 15;
            IEnumerable<MenuZone> zones = GameSession.Career?.Zones ?? Enumerable.Empty<MenuZone>();
            if (career) zones = zones.Where(z => bin <= z.BinIndex);
            switch (_view)
            {
                case MapView.Navigation:
                    // SetupNavigation (:1053): AddIcons in the order custom shop, safehouse, car lot (the toggle rows follow)
                    View.SetVisible(_cursorNode, true);
                    foreach (var z in zones.Where(z => z.Type != "safehouse" && z.Type != "carlot")) AddIcon(Icons[9], Coords.ToUnity(z.Position));
                    foreach (var z in zones.Where(z => z.Type == "safehouse")) AddIcon(Icons[7], Coords.ToUnity(z.Position));
                    foreach (var z in zones.Where(z => z.Type == "carlot")) AddIcon(Icons[8], Coords.ToUnity(z.Position));
                    break;
                case MapView.Event:
                {
                    // SetupEvent (:1060): sprint, circuit, drag, knockout, tollbooth, speedtrap, rival, then the speed-trap
                    // cameras (kType_SpeedTrap) and checkpoints (kType_Checkpoint: no CHECKPOINT pool in WorldMapMain)
                    View.SetVisible(_cursorNode, true);
                    var evs = new List<(int type, Vector3 at)>();
                    if (career)
                        foreach (var ev in GameSession.AvailableEvents())
                        {
                            var at = GameSession.EngagePoint(ev);
                            if (at == null || !Game.Gameplay.RaceDatabase.IsAvailable(ev, RaceIndex.ContextCareer)) continue;
                            int type = ev.BossRace ? 7 : ev.Type switch
                            {
                                RaceType.Sprint => 1, RaceType.Circuit => 2, RaceType.Drag => 3, RaceType.Knockout => 4,
                                RaceType.Tollbooth => 5, RaceType.SpeedTrap => 6, _ => 0,
                            };
                            if (type > 0) evs.Add((type, Coords.ToUnity(at.Position)));
                        }
                    for (int t = 1; t <= 7; t++)
                        foreach (var e in evs) if (e.type == t) AddIcon(Icons[t - 1], e.at);
                    AddTriggerIcons(GIcon.SpeedTrap);
                    break;
                }
                case MapView.Pursuit:
                    // SetupPursuit (:1073): safehouses, pursuit breakers, hiding spots, cops (+ chopper), roadblocks
                    Hide(0xA808E057, 0x95FDFC4E);
                    foreach (var z in zones.Where(z => z.Type == "safehouse")) AddIcon(Icons[7], Coords.ToUnity(z.Position));
                    AddTriggerIcons(GIcon.PursuitBreaker);
                    AddTriggerIcons(GIcon.HidingSpot);
                    AddCops();
                    AddRoadBlocks();
                    break;
            }
        }

        /// <summary>
        /// AddIcons (:1031) for the world's GIcons of one type: GManager::GatherVisibleIcons over the gameplay triggers
        /// that carry an icon (Runtime/Game/Triggers.cs GTrigger.Icon). [?] speed-trap cameras never get the on-map flag
        /// in the port's trigger set, so they are taken as shown while events may be engaged; pursuit breakers and hiding
        /// spots come from GManager's own pool (Triggers.cs GameIcons: IconMarker track markers, type-8 track path zones).
        /// </summary>
        void AddTriggerIcons(int giconType)
        {
            var set = NfsMw.Game.Lua.LuaFlow.Triggers?.Triggers;
            var info = Icons.FirstOrDefault(i => i.Type == giconType);
            if (set == null || info == null) return;
            foreach (var ic in set.Icons)   // the triggers' icons + GManager's pool (GameIcons: hiding spots, pursuit breakers)
            {
                if (ic == null || ic.Type != giconType) continue;
                bool shown = ic.Type == GIcon.SpeedTrap ? Availability.AllowEngageEvents : GIcon.IsVisible(ic.Type, ic.Flags);
                if (shown) AddIcon(info, ic.Position);
            }
        }

        static float Heading(Transform t) => Mathf.Atan2(t.forward.x, t.forward.z) * Mathf.Rad2Deg;

        /// <summary>
        /// AddCops (:949): every cop that was within the engagement radius gets MMICON_COPCAR_n (CopItem, flashing); the
        /// chopper is a HeliItem on HELICOPTER_ICON_GROUP (0xE26BE422) with the HELICOPTER_LINE_OF_SIGHT cone (0x21390E47);
        /// the toggle rows "Helicopter" (0xEAD9BD85) and "Cop Cars" (0xEAD6EF6C) appear when one was placed.
        /// </summary>
        void AddCops()
        {
            if (Systems.Police == null) return;
            int n = 0;
            foreach (var cop in Systems.Police.CopCars)
            {
                var cc = cop ? cop.GetComponent<CopCar>() : null;
                if (cc == null || !cc.WithinEngagementRadius) continue;   // IPursuitAI::WasWithinEngagementRadius
                var it = Add(NextPool("MMICON_COPCAR_{0}"), cop.transform.position, CopType, CopFlag, null);
                if (it == null) break;
                it.Cop = cop;
                SetAngle(it.Node, Heading(cop.transform));
                n++;
            }
            var heli = UnityEngine.Object.FindObjectOfType<CopHeli>();
            if (heli && !heli.Finished)
            {
                var it = Add(View.Find(0xE26BE422), heli.transform.position, HeliType, HeliFlag, null);
                if (it != null)
                {
                    it.Heli = heli;
                    it.Cone = View.Find(0x21390E47);
                    SetAngle(it.Node, Heading(heli.transform));
                    if (it.Cone != null) { SetAngle(it.Cone, Heading(heli.transform)); View.SetVisible(it.Cone, !it.Hidden); }
                    if (!_toggleTypes.Contains(HeliType)) _toggleTypes.Add(HeliType);
                }
            }
            if (n > 0 && !_toggleTypes.Contains(CopType)) _toggleTypes.Add(CopType);
        }

        /// <summary>
        /// AddRoadBlocks (:988): IRoadBlock::GetList → MMICON_ROADBLOCK_n at the block's centre, turned to its direction,
        /// and the "Roadblocks" row (0x411F1F86). [ours] the blocks are reached through the cop cars standing in them.
        /// </summary>
        void AddRoadBlocks()
        {
            if (Systems.Police == null) return;
            var seen = new HashSet<CopRoadblock>();
            int n = 0;
            foreach (var cop in Systems.Police.CopCars)
            {
                var rb = cop ? cop.GetComponent<CopCar>()?.Roadblock : null;
                if (rb == null || !seen.Add(rb)) continue;
                var it = Add(NextPool("MMICON_ROADBLOCK_{0}"), rb.Centre, RoadblockType, RoadblockFlag, null);
                if (it == null) break;
                SetAngle(it.Node, Mathf.Atan2(rb.Dir.x, rb.Dir.z) * Mathf.Rad2Deg);
                n++;
            }
            if (n > 0 && !_toggleTypes.Contains(RoadblockType)) _toggleTypes.Add(RoadblockType);
        }

        static void SetAngle(FEView.Node n, float deg)
        {
            float half = deg * Mathf.Deg2Rad * 0.5f;
            n.Data.QX = 0; n.Data.QY = 0;
            n.Data.QZ = Mathf.Sin(half); n.Data.QW = Mathf.Cos(half);
        }

        // ---- frame ----------------------------------------------------------------------------------------------

        public override void Tick(MenuInput input)
        {
            float dt = Time.unscaledDeltaTime;
            Inputs(input);
            if (FE.Top != this) return;
            UpdateCursor(false, dt);
            _map.Update(dt);
            UpdateCursor(true, dt);
            if (!_toggleMode) Mouse();
            PlaceItems();
            PlaceCursor();
        }

        /// <summary>SCREEN_TICK (:226): every item scaled about the map centre, panned, clamped to the map box.</summary>
        void PlaceItems()
        {
            float zoom = _map.Factor;
            var pan = _map.Pan;
            foreach (var it in _items)
            {
                if (it.Cop)
                {
                    var uv = K.MapUV(it.Cop.transform.position);   // [ours] follows the car (the exe keeps the position it had when the map opened)
                    it.InitialPos = _mapTopLeft + new Vector2(uv.x * _mapSize.x, uv.y * _mapSize.y);
                    // CopItem::Draw (:35): 0xFF0000FF for frames 1-2, 0xFFA00000 for 5-6, else 0xFFCCCCCC, on an 8-frame cycle
                    it.Flash = it.Flash % 8 + 1;
                    var c = it.Flash < 3 ? new Color32(0, 0, 255, 255) : it.Flash >= 5 && it.Flash <= 6 ? new Color32(160, 0, 0, 255) : new Color32(204, 204, 204, 255);
                    it.Node.Data.R = c.r; it.Node.Data.G = c.g; it.Node.Data.B = c.b;
                }
                var p = _centre + (it.InitialPos - _centre) * zoom - new Vector2(pan.x * _mapSize.x, pan.y * _mapSize.y) * zoom;
                float s = ((zoom - 1) / (MaxZoom - 1)) * 0.5f + 1;
                it.Node.Data.SizeX = it.InitialSize.x * s;   // HeliItem::UpdateScale scales the group the same way
                it.Node.Data.SizeY = it.InitialSize.y * s;
                bool clamped = Clamp(ref p);
                K.SetAbsPos(it.Node, p);
                bool shown = !clamped && !it.Hidden;
                View.SetVisible(it.Node, shown);
                if (it.Cone != null)
                {
                    // HeliItem::Draw (:52): the line-of-sight cone's width breathes with sin(real seconds) · 32 + 32
                    it.Cone.Data.SizeX = Mathf.Sin(Time.realtimeSinceStartup) * 32f + 32f;
                    K.SetAbsPos(it.Cone, p);
                    View.SetVisible(it.Cone, shown);
                }
            }
        }

        /// <summary>ClampToMapBounds (:609): [TL.x+8, BR.x−8] × [TL.y+26, BR.y−32], first violated axis only.</summary>
        bool Clamp(ref Vector2 p)
        {
            float x0 = _mapTopLeft.x + 8, x1 = _mapTopLeft.x + _mapSize.x - 8, y0 = _mapTopLeft.y + 26, y1 = _mapTopLeft.y + _mapSize.y - 32;
            if (p.x < x0) { p.x = x0; return true; }
            if (p.x > x1) { p.x = x1; return true; }
            if (p.y < y0) { p.y = y0; return true; }
            if (p.y > y1) { p.y = y1; return true; }
            return false;
        }

        bool InBox(Vector2 p) => !Clamp(ref p);

        Vector2 ClampAll(Vector2 p)
        {
            p.x = Mathf.Clamp(p.x, _mapTopLeft.x + 8, _mapTopLeft.x + _mapSize.x - 8);
            p.y = Mathf.Clamp(p.y, _mapTopLeft.y + 26, _mapTopLeft.y + _mapSize.y - 32);
            return p;
        }

        void PlaceCursor()
        {
            if (_cursorNode == null) return;
            K.SetAbsPos(_cursorNode, _cursor);
            View.SetVisible(_cursorNode, !_toggleMode);
        }

        Vector2 _velocity;

        /// <summary>UpdateCursor (:660): follow the zoom, else move by the stick / keys and unsnap, or snap when idle.</summary>
        void UpdateCursor(bool zoomPass, float dt)
        {
            if (_map.Zooming)
            {
                var pan = _map.Pan;
                _cursor = ClampAll(_centre + (_cursorFrom - _centre) * _map.Factor - new Vector2(pan.x * _mapSize.x, pan.y * _mapSize.y) * _map.Factor);
                return;
            }
            if (zoomPass || _toggleMode) return;
            if (_velocity != Vector2.zero)
            {
                if (!_cursorMoving) { _cursorMoving = true; K.Message(View, 0x9F710838); }
                MoveCursor(_velocity * dt);
                if (_selected != null && (ItemPos(_selected) - _cursor).magnitude >= SnapDist)
                {
                    View.Play(_cursorNode, K.UnSnap, true);
                    _selected = null;
                    RefreshHeader();
                }
            }
            else
            {
                if (_cursorMoving) { _cursorMoving = false; K.Message(View, 0x7E6687DA); }
                if (SnapCursor(_cursor)) RefreshHeader();
            }
        }

        Vector2 ItemPos(Item it) => K.AbsPos(it.Node);

        /// <summary>
        /// MoveCursor (:720): zoomed in, pushing past the box edge pans the map instead — by the movement on the first edge
        /// crossed only (x past a side, else y past the top / bottom), as a fraction of the map size.
        /// </summary>
        void MoveCursor(Vector2 d)
        {
            var next = _cursor + d;
            if (_zoom != 0 && d != Vector2.zero)
            {
                float x0 = _mapTopLeft.x + 8, x1 = _mapTopLeft.x + _mapSize.x - 8, y0 = _mapTopLeft.y + 26, y1 = _mapTopLeft.y + _mapSize.y - 32;
                var ex = Vector2.zero;
                if (next.x < x0 || next.x > x1) ex.x = d.x / _mapSize.x;
                else if (next.y < y0 || next.y > y1) ex.y = d.y / _mapSize.y;
                if (ex != Vector2.zero) PanBy(ex);
            }
            _cursor = ClampAll(next);
        }

        void PanBy(Vector2 excess)
        {
            float m = 0.5f - 0.5f / _map.Factor;
            var cur = _map.Pan;
            var p = new Vector2(Mathf.Clamp(cur.x + excess.x, -m, m), Mathf.Clamp(cur.y + excess.y, -m, m));
            _map.SetPan((p + cur) * 0.5f + new Vector2(0.5f, 0.5f));
        }

        /// <summary>SnapCursor (:767; PC 0x530A60 measures from the pointer): the nearest shown item within 20.</summary>
        bool SnapCursor(Vector2 from)
        {
            Item best = null;
            float bd = SnapDist;
            foreach (var it in _items)
            {
                if (it.Hidden || !it.Node.Visible) continue;
                float d = (ItemPos(it) - from).magnitude;
                if (d < bd) { bd = d; best = it; }
            }
            if (best != null)
            {
                _cursor = ItemPos(best);
                if (best == _selected) return false;
                _selected = best;
                View.Play(_cursorNode, K.Snap, true);
                return true;
            }
            if (_selected == null) return false;
            View.Play(_cursorNode, K.UnSnap, true);
            _selected = null;
            return true;
        }

        /// <summary>PC mouse (0x552DA0 / 0x511B80): pointer drives the cursor in the box, wheel zooms, left-drag pans.</summary>
        void Mouse()
        {
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var at = K.Mouse(View);
            if (mouse == null || at == null) return;
            bool moved = _lastMouse == null || (_lastMouse.Value - at.Value).sqrMagnitude > 0.01f;
            _lastMouse = at;
            var p = at.Value;
            if (InBox(p))
            {
                if (moved && _velocity == Vector2.zero && !_map.Zooming)
                {
                    bool changed = SnapCursor(p);
                    if (_selected == null) _cursor = p;
                    if (changed) RefreshHeader();
                }
                if (mouse.leftButton.wasPressedThisFrame)
                {
                    _leftHeld = true;
                    if (_selected != null && (ItemPos(_selected) - p).magnitude < SnapDist) Pressed();
                }
            }
            if (!mouse.leftButton.isPressed) _leftHeld = false;
            float wheel = mouse.scroll.ReadValue().y;
            if (wheel > 0) ScrollZoom(+1);
            else if (wheel < 0) ScrollZoom(-1);
            if (_leftHeld && _zoom != 0)
            {
                var d = -K.MouseDelta(View) * 2f * (1f / _map.Factor);
                if (d != Vector2.zero) PanBy(new Vector2(d.x / _mapSize.x, d.y / _mapSize.y));
            }
        }

        // ---- zoom / pan -----------------------------------------------------------------------------------------

        /// <summary>PanToPlayer (:830): the player's (u, v) clamped to [m, 1 − m], m = 0.5 / factor.</summary>
        void PanToPlayer()
        {
            var pl = _items.FirstOrDefault(i => i.Player);
            float f = ZoomFactors[_zoom], m = 0.5f / f;
            var uv = pl != null ? K.MapUV(pl.World) : new Vector2(0.5f, 0.5f);
            _map.SetPan(new Vector2(Mathf.Clamp(uv.x, m, 1 - m), Mathf.Clamp(uv.y, m, 1 - m)));
        }

        /// <summary>ScrollZoom (:528, PC clamps): animate to 1 / factor and pan toward the cursor (:803).</summary>
        void ScrollZoom(int dir)
        {
            int z = Mathf.Clamp(_zoom + dir, 0, ZoomFactors.Length - 1);
            if (z == _zoom) return;
            float cur = _map.Factor;
            _zoom = z;
            float f = ZoomFactors[z];
            var rel = new Vector2((_cursor.x - _centre.x) / _mapSize.x, (_cursor.y - _centre.y) / _mapSize.y) * (1f / cur);
            var t = _map.Pan + new Vector2(0.5f, 0.5f) + rel;
            _cursorFrom = _mapTopLeft + new Vector2(t.x * _mapSize.x, t.y * _mapSize.y);
            float m = 0.5f / f;
            _map.ZoomTo(1f / f);
            _map.PanTo(new Vector2(Mathf.Clamp(t.x, m, 1 - m), Mathf.Clamp(t.y, m, 1 - m)));
            var p = GameSession.Profile;
            if (p != null) { if (_view == MapView.Pursuit) p.MapPursuitZoom = z; else p.MapZoom = z; }
            RefreshHeader();
        }

        // ---- input ----------------------------------------------------------------------------------------------

        void Inputs(MenuInput input)
        {
            var kb = Keyboard.current;
            var pad = Gamepad.current;
            _velocity = Vector2.zero;
            if (!_toggleMode)
            {
                var v = Vector2.zero;
                if (kb != null)
                {
                    if (kb.leftArrowKey.isPressed || kb.aKey.isPressed) v.x -= 1;
                    if (kb.rightArrowKey.isPressed || kb.dKey.isPressed) v.x += 1;
                    if (kb.upArrowKey.isPressed || kb.wKey.isPressed) v.y -= 1;
                    if (kb.downArrowKey.isPressed || kb.sKey.isPressed) v.y += 1;
                }
                if (pad != null)
                {
                    var s = pad.rightStick.ReadValue() + pad.leftStick.ReadValue();
                    if (s.magnitude > 0.2f) v += new Vector2(s.x, -s.y);
                }
                // FRONTENDACTION_R*: ±data × 14 FE units per (30 Hz) frame [?]
                _velocity = Vector2.ClampMagnitude(v, 1f) * 14f * 30f;

                bool zoomOut = K.KeyPressed(Key.Q) || K.KeyPressed(Key.Minus) || K.KeyPressed(Key.NumpadMinus) || K.KeyPressed(Key.PageDown) || (pad != null && (pad.leftShoulder.wasPressedThisFrame || pad.leftTrigger.wasPressedThisFrame));
                bool zoomIn = K.KeyPressed(Key.E) || K.KeyPressed(Key.Equals) || K.KeyPressed(Key.NumpadPlus) || K.KeyPressed(Key.PageUp) || (pad != null && (pad.rightShoulder.wasPressedThisFrame || pad.rightTrigger.wasPressedThisFrame));
                if (zoomOut) ScrollZoom(-1);
                if (zoomIn) ScrollZoom(+1);

                // PAD_LEFT / PAD_RIGHT: NAVIGATION <-> EVENT (not in the pursuit view)
                bool sw = K.KeyPressed(Key.Digit4) || K.KeyPressed(Key.Tab) || (pad != null && (pad.dpad.left.wasPressedThisFrame || pad.dpad.right.wasPressedThisFrame));
                if (sw && _view != MapView.Pursuit)
                {
                    if (_selected != null) View.Play(_cursorNode, K.UnSnap, true);
                    _view = _view == MapView.Navigation ? MapView.Event : MapView.Navigation;
                    if (GameSession.Profile != null) GameSession.Profile.MapView = (int)_view;
                    HideAllPools();
                    Populate();
                    RefreshHeader();
                }
            }
            else
            {
                if (input.Up) { _toggleRow = (_toggleRow + _toggleTypes.Count - 1) % Mathf.Max(1, _toggleTypes.Count); DrawToggles(); }
                if (input.Down) { _toggleRow = (_toggleRow + 1) % Mathf.Max(1, _toggleTypes.Count); DrawToggles(); }
            }

            // BUTTON4: the icon filter list
            if (K.KeyPressed(Key.Digit2) || (pad != null && pad.buttonWest.wasPressedThisFrame)) SetToggleMode(!_toggleMode);
            bool start = pad != null && pad.startButton.wasPressedThisFrame;   // PAD_START (:265): SetLastButton 0, then as PAD_BACK
            if (input.Accept && !start) Pressed();
            else if (input.Back || start)
            {
                if (_toggleMode) SetToggleMode(false);
                else FE.Pop();
            }
        }

        /// <summary>BUTTON_PRESSED: flip a filter row, or the GPS dialog for the selected icon / to switch the GPS off.</summary>
        void Pressed()
        {
            if (_toggleMode)
            {
                if (_toggleTypes.Count == 0) return;
                int type = _toggleTypes[_toggleRow];
                int flag = FlagOf(type);
                var p = GameSession.Profile;
                if (p != null) p.MapItems ^= flag;   // ItemTypeToggle::Act (:64): GameplaySettings::SetMapItem + UISND_COMMON_LEFT
                NfsMw.Audio.FeSound.Play(NfsMw.Audio.FeSoundId.CommonLeft);
                bool on = p == null || (p.MapItems & flag) != 0;
                foreach (var it in _items) if (it.Type == type) it.Hidden = !on;   // UpdateIconVisibility (:578)
                DrawToggles();
                RefreshHeader();
                return;
            }
            if (_selected != null && !_selected.Player && _selected.Info != null)
            {
                var target = _selected.World;
                FE.Push(new ConfirmScreen("", K.L(0x96AC0A32, "Do you want to activate the GPS system for this location?"), () => EngageGps(target)));
            }
            else if (_selected == null && GpsTarget != null)
                FE.Push(new ConfirmScreen("", K.L(0xA6BE2EBB, "Would you like to de-activate GPS?"), () => { ClearGps(); RefreshHeader(); }));
        }

        /// <summary>0xA16CA7BD: drop the current route, then GPS_Engage(icon); success closes the map.</summary>
        void EngageGps(Vector3 target)
        {
            ClearGps();
            // GPS_Engage(icon, 0) (Game/Gameplay/Gps.cs: the exe's GPS draws the MARKER_DIRECTION_AID arrow and puts
            // "GPS connection lost" on the HUD itself); false → "Can't establish GPS connection"
            if (!GpsNavigation.Engage(target, 0f))
            {
                FE.Push(new MessageScreen("", K.L(0x7AFDF4CC, "Can't establish GPS connection")));
                return;
            }
            GpsTarget = target;
            if (!_gpsHooked)
            {
                _gpsHooked = true;
                GpsNavigation.Finished += () => GpsTarget = null;
                GpsNavigation.Lost += () => GpsTarget = null;
            }
            if (FE.Top == this) FE.Pop();
        }

        /// <summary>Disengages the world-map GPS (the story's own errands keep theirs).</summary>
        public static void ClearGps()
        {
            if (GpsTarget != null) GpsNavigation.Disengage();
            GpsTarget = null;
            Minimap.Gps = null;
        }

        static int FlagOf(int type) => type == CopType ? CopFlag : type == HeliType ? HeliFlag : type == RoadblockType ? RoadblockFlag
            : Icons.FirstOrDefault(i => i.Type == type)?.Flag ?? 0;

        static string TitleOf(int type)
        {
            if (type == CopType) return K.L(0xEAD6EF6C, "Cop Cars");
            if (type == HeliType) return K.L(0xEAD9BD85, "Helicopter");
            if (type == RoadblockType) return K.L(0x411F1F86, "Roadblocks");
            var i = Icons.FirstOrDefault(x => x.Type == type);
            return i == null ? "" : K.L(i.TitleLabel, i.Title);
        }

        void SetToggleMode(bool on)
        {
            if (on == _toggleMode) return;
            _toggleMode = on;
            K.Message(View, on ? 0x5C28136Du : 0x947E6205u);
            var list = View.Find(0x78B4E2A9);
            View.Play(list, FEView.Appear);
            _toggleRow = 0;
            DrawToggles();
            RefreshHeader();
        }

        /// <summary>ItemTypeToggle rows: OPTION_NAME_n with the type's icon in C9784C95+n; NORMAL when shown, GREY when hidden.</summary>
        void DrawToggles()
        {
            int mask = GameSession.Profile?.MapItems ?? -1;
            for (int i = 0; i < 7; i++)
            {
                var name = View.Find("OPTION_NAME_" + (i + 1));
                var group = View.Find(0xC9784C95u + (uint)i);
                var icon = View.Find(0xA713B090u + (uint)i);
                bool has = _toggleMode && i < _toggleTypes.Count;
                View.SetVisible(name, has);
                View.SetVisible(group, has);
                if (!has) continue;
                int type = _toggleTypes[i];
                bool on = (mask & FlagOf(type)) != 0;
                View.SetText(name, TitleOf(type));
                // ItemTypeToggle::SetIcon (:117): the pool element's texture and colour, except cop cars (white, 0xDAC364E9),
                // roadblocks (0xFFFFED00, 0x123F07E2) and the player car (0xFFABDA4D, 0xADA85247)
                var pool = _items.FirstOrDefault(x => x.Type == type)?.Node;
                if (icon != null)
                {
                    if (type == CopType) { var t = FrontEndData.Texture(0xDAC364E9); if (t) View.SetTexture(icon, t); icon.Data.R = icon.Data.G = icon.Data.B = 255; }
                    else if (type == RoadblockType) { var t = FrontEndData.Texture(0x123F07E2); if (t) View.SetTexture(icon, t); icon.Data.R = 255; icon.Data.G = 237; icon.Data.B = 0; }
                    else if (pool?.Image != null)
                    {
                        View.SetTexture(icon, pool.Image.texture as Texture2D);
                        icon.Data.R = pool.Data.R; icon.Data.G = pool.Data.G; icon.Data.B = pool.Data.B;
                    }
                }
                View.Play(name, i == _toggleRow ? FEView.Highlight : on ? FEView.UnHighlight : FEView.Grey);
                View.Play(group, on ? K.Normal : FEView.Grey);
            }
        }

        // ---- header ---------------------------------------------------------------------------------------------

        /// <summary>RefreshHeader (:1144): view title, zoom label, the selection's type / distance / icon, GPS and back texts.</summary>
        void RefreshHeader()
        {
            var (vl, vt) = _view switch
            {
                MapView.Event => (0xDFD23484u, "EVENT"), MapView.Race => (0xF74B357Du, "RACE"), MapView.Pursuit => (0xFEA872D4u, "PURSUIT"), _ => (0xBF55E8B2u, "LOCATIONS"),
            };
            K.TextAll(View, 0xD259525F, K.L(vl, vt));
            K.TextAll(View, 0xCB76CE5B, K.L(ZoomLabels[_zoom].label, ZoomLabels[_zoom].text));
            var title = View.Find(0x9331FD4F);
            var dist = View.Find(0xFEEEB39B);
            var big = View.Find(0x9A5AB124);
            if (_selected != null)
            {
                // DrawItemType (:1094): the type's mWorldMapTitle [?: the player car's row of kGameplayIconInfo is unknown — the profile name]
                string t = _selected.Player ? (GameSession.Profile?.Name ?? "Player") : _selected.Type >= CopType ? TitleOf(_selected.Type) : _selected.Info != null ? K.L(_selected.Info.TitleLabel, _selected.Info.Title) : "";
                View.SetText(title, t);
                var car = GameBootstrap.Instance ? GameBootstrap.Instance.Player : null;
                if (!_selected.Player && car)
                {
                    var d = _selected.World - car.transform.position;
                    d.y = 0;
                    float m = d.magnitude;
                    View.SetText(dist, Settings.Kmh ? $"{Localize.Fixed(m * 0.001f, 1, true)} {K.L(0x8569A26A, "KMs")}" : $"{Localize.Fixed(m * 0.000625f, 1, true)} {K.L(0x867DCFD9, "Miles")}");
                }
                else View.SetText(dist, "");
                var tex = _selected.Info != null ? FrontEndData.Texture(_selected.Info.Tex) : null;
                if (big != null) { if (tex) View.SetTexture(big, tex); View.SetVisible(big, tex != null); }
            }
            else
            {
                View.SetText(title, "");
                View.SetText(dist, "");
                View.SetVisible(big, false);
            }
            // GPS button (PC: the 'Activate' key group 5EE23227) and the back text
            var gps = View.Find(0x5EE23227);
            string gpsText;
            bool gpsOn;
            if (_toggleMode)
            {
                bool rowOn = _toggleTypes.Count == 0 || ((GameSession.Profile?.MapItems ?? -1) & FlagOf(_toggleTypes[_toggleRow])) != 0;
                gpsText = rowOn ? K.L(0x2C35EC64, "Hide Icons") : K.L(0xBA0A6A2B, "Show Icons");
                gpsOn = true;
            }
            else if (_selected != null && _selected.Info != null) { gpsText = K.L(0x43512519, "Activate GPS"); gpsOn = true; }
            else if (GpsTarget != null) { gpsText = K.L(0xF1D0D8A5, "Deactivate GPS"); gpsOn = true; }
            else { gpsText = K.L(0x43512519, "Activate GPS"); gpsOn = false; }
            View.SetText(View.Find(0x9ACD367A), gpsText);
            K.Text(View, 0x29456CC8, gpsText);
            View.Play(gps, gpsOn ? K.Normal : FEView.Grey);
            View.Play(View.Find(0x32490131), gpsOn ? K.Normal : FEView.Grey);
            string back = _toggleMode ? K.L(0x58B828ED, "Done") : K.L(0x001335F0, "Back");
            foreach (var n in View.Nodes) if (n.Obj.Hash == 0x001335F0 || n.Obj.Hash == 0x51F0064F) View.SetText(n, back);
        }
    }
}
