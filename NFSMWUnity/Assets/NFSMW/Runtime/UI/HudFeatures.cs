using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// FEngHud's widgets and feature flags (FEPkg_Hud.cpp; hud.md §1-2). The FEngHud ctor (PC 0x5A6600) pushes the package,
    /// makes every object of it invisible (FEngSetAllObjectsInPackageVisibility(pkg, false), 0x5248C0), creates the widgets
    /// for the HUD type — each registers its objects (HudElement::RegisterGroup adds a group's leaves recursively,
    /// RegisterObject / Image / String / MultiImage one object) — hides the two online lines (C18C12FD / FE) when offline,
    /// and runs CurrentHudFeatures = 0; SetHudFeatures(all ones); SetHudFeatures(0). Every frame FEngHud::Update (0x58CA30)
    /// calls DetermineHudFeatures (0x57CA60) → SetHudFeatures (0x57CDB0) on a change, which calls HudElement::Toggle
    /// (0x5793F0, FeHudElement.cpp:78) on each widget whose own toggle bit changed: its objects FEngSetVisible (bit 0 cleared
    /// unless flag 8 / 0x40, recursively) when (flags &amp; mask) != 0, else FEngSetInvisible. Objects no widget registers stay
    /// invisible (the old port hid them with a hand-made list).
    /// </summary>
    public sealed class HudFeatures
    {
        sealed class Widget
        {
            public string Name;
            public ulong Mask, ToggleBits;
            public readonly List<FEView.Node> Objects = new List<FEView.Node>();
        }

        readonly FEView _v;
        readonly List<Widget> _widgets = new List<Widget>();
        ulong _current;
        public ulong Current => _current;
        /// <summary>The minimap widget's state (mask 0x40010000).</summary>
        public bool MinimapOn => (_current & 0x40010000UL) != 0;

        /// <summary>split: 0 = HUD_SingleRace / HUD_Drag (PHT_STANDARD / PHT_DRAG), 1 / 2 = the split-screen halves.</summary>
        public HudFeatures(FEView view, bool drag, int split = 0, bool career = false)
        {
            _v = view;
            bool standard = split == 0 && !drag;
            // FEngSetAllObjectsInPackageVisibility(pkg, false)
            foreach (var n in view.Nodes) view.SetInvisible(n);
            Add("Speedometer", 0x08000000, 0x8000000, G("SpeedometerGroup"));
            Add("RaceInformation", 0x04000000, 0x4000000, G("RaceInformationGroup"));
            Add("LeaderBoard", 0x18, 0x18, G("LeaderBoardGroup"));
            Add("NitrousGauge", 0x80000800, 0x800, G(0x87C38E97), O(0xEDFB6D37));
            Add("RaceOverMessage", 0x4, 0x4);
            Add("GenericMessage", 0x01000000, 0x1000000, G(0x32A7A521), O(0x6DD754EC), O(0xCAEC9D04));
            Add("TurboMeter", 0x80020000, 0x20000, G("TURBO_GROUP"));
            Add("WrongWIndi", 0x20, 0x20, O("WRONGWAYIMAGE"));
            Add("Coundown", 0x400, 0x400, G("321_GO_GROUP"));
            if (drag)
            {
                Add("EngineTempGauge", 0x40, 0x40, G("Engine_Heat_Meter_Group"), O("ENGINE_METER"));
                Add("DragTachometer", 0x2, 0x2, O("RPM_fill"), O("Drag_Turbo_Backing"), O("Drag_Turbo_Lines"), O("3rdPersonSpeedUnits"), O("SPEED_BACKING"),
                    G("RPM"), O("TAC_Lines_7500"), O("RPM_Redline"), O("3rdPersonNeedle"), O("3rdPersonGear"));
                Add("ShiftUpdater", 0x20000000, 0x20000000, O("Shift_light"), G("ENGINE_HEAT_SHIFTLIGHT_GROUP"), G("SHIFT_GROUP"));
            }
            else
            {
                Add("TimeExtension", 0x02000000, 0x2000000);
                Add("Tachometer", 0x2, 0x2, G("GaugeCluster"), G(0x045E9562));
                if (standard)
                {
                    if (career) Add("Reputation", 0x1000, 0x1000, G(0xEA903012));
                    Add("HeatMeter", 0x40004000, 0x4000, G(0xC46A80A9), O(0x862824C9), O(0x4B2CBE1B));
                    Add("CostToState", 0x1000, 0x1000, G("CTS_GROUP"));
                    Add("PursuitBoard", 0x100000, 0x100000, G(0xDE89E070), G(0x7B422BA3), G(0xE0B1430B), G(0x8674E6D4), G(0x84A226EC), G(0x6A144066), O(0x3C165F39), O(0xBEE44775));
                    Add("MilestoneBoard", 0x400000000UL, 0x400000000UL, G("MILESTONE_INFO_GROUP"), G("MILESTONE_ICON_GROUP"));
                    Add("BustedMeter", 0x800000, 0x800000);
                    Add("MenuZoneTrigger", 0x400000, 0x400000, G("Engage_Mechanic"), O("EventIcon"), G(0xDA8141D4));
                    var infr = new List<uint> { H("INFRACTIONS_HEADER_GROUP") };
                    for (int i = 1; i <= 4; i++) infr.Add(H("INFRACTION_GROUP_" + i));
                    Add("Infractions", 0x200000000UL, 0x200000000UL, G(infr.ToArray()), O("GENERIC_ICONS"), O("TOTAL_INFRACTIONS_TEXT"));
                    Add("RadarDetector", 0x200000, 0x200000, G(0x062743F5), O(0x839E7D77), O(0x9EE06631));
                }
                if (standard || split == 1) Add("Minimap", 0x40010000, 0x10000, Minimap.Registrations().ToArray());
            }
            if (split == 0)
            {
                Add("SpeedBreakerMeter", 0x40000, 0x40000, G("Speedbreaker_Meter_Group"), O("Speedbreaker_Meter_Multi_Image"), G(0x82D60021));
                Add("GetAwayMeter", 0x200, 0x200, G("GetawayDistanceMeter"));
            }
            // not an online race: the two online lines
            view.SetInvisible(view.Find(0xC18C12FD));
            view.SetInvisible(view.Find(0xC18C12FE));
            _current = 0;
            Set(0xFFFFFFFFFFFFFFFFUL);   // SetHudFeatures(0xFFFFFFFF) — the PC pushes -1 for both halves
            Set(0);
        }

        static uint H(string s) => FrontEndData.Hash(s);
        static (bool group, uint[] h) G(params uint[] h) => (true, h);
        static (bool group, uint[] h) G(string s) => (true, new[] { H(s) });
        static (bool group, uint[] h) O(uint h) => (false, new[] { h });
        static (bool group, uint[] h) O(string s) => (false, new[] { H(s) });

        void Add(string name, ulong mask, ulong toggle, params (bool group, uint[] h)[] regs)
        {
            var w = new Widget { Name = name, Mask = mask, ToggleBits = toggle };
            foreach (var (group, hs) in regs)
                foreach (var h in hs)
                {
                    var n = _v.Find(h);
                    if (n == null) continue;
                    if (group) Leaves(n, w.Objects);
                    else w.Objects.Add(n);
                }
            _widgets.Add(w);
        }

        /// <summary>HudElement::RegisterGroup: child groups recurse, everything else is added (the group itself is not).</summary>
        static void Leaves(FEView.Node g, List<FEView.Node> into)
        {
            foreach (var c in g.Children)
            {
                if (c.Obj.Type == FEObjectType.Group) Leaves(c, into);
                else into.Add(c);
            }
        }

        /// <summary>FEngHud::SetHudFeatures: HudElement::Toggle on the widgets whose toggle bit changed.</summary>
        public void Set(ulong flags)
        {
            ulong changed = flags ^ _current;
            if (changed == 0) return;
            foreach (var w in _widgets)
            {
                if ((changed & w.ToggleBits) == 0) continue;
                bool on = (flags & w.Mask) != 0;
                foreach (var n in w.Objects)
                {
                    if (on) _v.SetVisibleTree(n);   // FEngSetVisible 0x514CC0 (recursive, flag-0x40 objects untouched)
                    else _v.SetInvisible(n);        // FEngSetInvisible 0x514C70
                }
            }
            _current = flags;
        }

        /// <summary>
        /// FEngHud::DetermineHudFeatures (PC 0x57CA60, hud.md §2). All 0 unless: the player's view runs the drive camera
        /// (CM_DRIVE_CUBIC) without the look-back angle, not restarting a race, no pause, the sim active, none of Pause_Main /
        /// MC_Main / PostRace_Results pushed, the HUD resources loaded, no NIS, the widescreen setting synced. Then: minimap
        /// (track 2000, mode ≠ 2: G+0x3C in a race else G+0x3D) 0x10000 (+0x4000 offline); 0x08000000 Gauges (drag: Position);
        /// with Gauges 0x2, turbo 0x20000, drag 0x40, 0x800, offline 0x40000; racing and not a pursuit race: LeaderBoard 0x8,
        /// Position 0x04000000, else 0x100000 | hi 0x4; SplitTimeType ≠ 4 → 0x10; tollbooth 0x02000000; ScoreOn (player
        /// settings +3) and (career context or pursuit race) → 0x1000; racing 0x20; 0x4 while the race-over message is
        /// pending; always 0x31C80400 | hi 0x3; offline 0x200000.
        /// inGame stands for the camera / pause / package / NIS gates (GameHud); [?] the race-over message's pending state is
        /// not raised (the port's race-over text goes through Systems.HudMessage).
        /// </summary>
        public static ulong Determine(bool inGame, PlayerCar car, int player, RaceManager race)
        {
            if (!inGame) return 0;
            var db = FEDb.Data;
            var p = db.Player(player);
            var g = db.Gameplay;
            bool offline = GameSession.Lan == null;
            var type = race?.Event?.Type;
            bool drag = type == RaceType.Drag;
            bool pursuitRace = Systems.Police != null ? Systems.Police.IsPursuitRace : type == RaceType.Pursuit;
            ulong f = 0;
            int mapMode = race != null ? g.RacingMiniMapMode : g.ExploringMiniMapMode;
            if (mapMode != 2) { f |= 0x10000; if (offline) f |= 0x4000; }
            if (drag ? p.PositionOn : p.GaugesOn) f |= 0x08000000;
            if (p.GaugesOn)
            {
                f |= 0x2;
                if (car != null && car.Spec != null && car.Spec.Spool > 0) f |= 0x20000;
                if (drag) f |= 0x40;
                f |= 0x800;
                if (offline) f |= 0x40000;
            }
            if (race != null && !pursuitRace)
            {
                if (p.LeaderboardOn) f |= 0x8;
                if (p.PositionOn) f |= 0x04000000;
            }
            else f |= 0x100000 | 0x400000000UL;
            if (p.SplitTimeType != 4) f |= 0x10;
            if (type == RaceType.Tollbooth) f |= 0x02000000;
            if (p.ScoreOn && (GameSession.Mode == GameMode.Career || pursuitRace)) f |= 0x1000;
            if (race != null) f |= 0x20;
            f |= 0x31C80400;
            f |= 0x300000000UL;
            if (offline) f |= 0x200000;
            return f;
        }
    }
}
