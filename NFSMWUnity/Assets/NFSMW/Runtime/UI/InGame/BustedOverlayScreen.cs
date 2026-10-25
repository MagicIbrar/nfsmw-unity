using NfsMw.Game;

namespace NfsMw.UI
{
    /// <summary>
    /// BUSTED_OVERLAY.fng — BustedOverlayScreen (MenuScreens/InGame/FeBustedOverlay.cpp; PC ctor 0x58D340, empty
    /// NotificationMessage 0x57D440): the police ticker over the arrest NIS. During an Arrest NIS the line 0xF1ED545A is
    /// picked from the pursuit: ≥ 2 roadblocks dodged → 0x626AC042 (or 0x626AC045 with &gt; 1 spike strip dodged), else
    /// ≥ 2 cops destroyed → 0x626AC044, else cost to state &gt; 10000 → 0x626AC046, else 0x626AC043; an Intro NIS shows
    /// 0x626AC047. [ours] Not pushed by anything yet: the arrest NIS (NisPlayer.Busted) belongs to the NIS / police code;
    /// call Show(stats, arrest) from there. It is an overlay so the NIS keeps drawing under it.
    /// </summary>
    public sealed class BustedOverlayScreen : FEMenuScreen
    {
        readonly PursuitData _data;
        readonly bool _arrest;

        public override string Package => "BUSTED_OVERLAY";
        public override bool Overlay => true;

        public BustedOverlayScreen(PursuitStats stats, bool arrest = true)
        {
            _data = PursuitData.From(stats, 0, null);
            _arrest = arrest;
        }

        public static BustedOverlayScreen Show(PursuitStats stats, bool arrest = true) => FrontEnd.Create().Push(new BustedOverlayScreen(stats, arrest));

        protected override void Setup()
        {
            const uint Ticker = 0xF1ED545A;
            if (!_arrest) { SetLanguageHash(Ticker, 0x626AC047); return; }
            uint label = 0x626AC043;
            if (_data.RoadblocksDodged >= 2) label = _data.SpikesDodged > 1 ? 0x626AC045u : 0x626AC042u;
            else if (_data.CopsDestroyed >= 2) label = 0x626AC044;
            else if (_data.CostToState > 10000f) label = 0x626AC046;
            string time = FETimer.Print(_data.Length);
            int infractions = _data.Stats?.Infractions.Count ?? 0;
            string fmt = Lang(label);
            string text = label switch
            {
                0x626AC042 => CareerScreenKit.Format(fmt, _data.CopsDamaged, _data.CopsDestroyed, _data.RoadblocksDodged, _data.CostToState, time),
                0x626AC044 => CareerScreenKit.Format(fmt, time, _data.CopsDamaged, _data.CopsDestroyed, infractions),
                0x626AC045 => CareerScreenKit.Format(fmt, time, _data.SpikesDodged, _data.RoadblocksDodged, infractions),
                0x626AC046 => CareerScreenKit.Format(fmt, time, _data.CostToState, infractions),
                _ => CareerScreenKit.Format(fmt, time, infractions),
            };
            Printf(Ticker, text);
        }
    }
}
