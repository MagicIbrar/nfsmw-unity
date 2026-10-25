using NfsMw.Game;
using UnityEngine;
using K = NfsMw.UI.CareerScreenKit;

namespace NfsMw.UI
{
    /// <summary>
    /// Showcase.fng — Showcase (quickrace/uiShowcase.cpp; PC 0x7A4680-0x7AEAE1): the car alone in the showroom, reached with
    /// key 3 (PAD_BUTTON0) from Car_Select / My Cars. With a Blacklist number (a rival's car): the title group 0x242657CE
    /// prints 0x3A64DE21 ("Blacklist #%d"), 0x89D0649C is posted, 0xB695A891 = "%d", 0x7AC3D0C9 = BLACKLIST_RIVAL_%02d_AKA
    /// and the rival's tag art streams into 0xF5A2A087; otherwise 0x3E01AD1D gets the manufacturer logo and 0xB05DD708 the
    /// car logo. CURRENT_GEN_WIDESCREEN is posted on widescreen. Accept or Back → switch back to the calling package
    /// (the port pops [ours]). The help bar's control group 0xD86AACF8 hides with a steering wheel (none here).
    /// </summary>
    public sealed class ShowcaseScreen : FEMenuScreen
    {
        public override string Package => "Showcase";
        public override bool ShowCar => true;
        readonly OwnedCar _car;
        readonly int _blacklist;

        public ShowcaseScreen(OwnedCar car, int blacklistNumber = 0) { _car = car; _blacklist = blacklistNumber; }

        protected override void Setup()
        {
            if ((float)Screen.width / Screen.height > 1.45f) View.PackageMessage(FrontEndData.Hash("CURRENT_GEN_WIDESCREEN"));
            if (_car != null)
            {
                if (_blacklist != 0)
                {
                    Printf(0x242657CE, K.Format(Lang(0x3A64DE21), _blacklist));
                    View.PackageMessage(0x89D0649C);
                    Printf(0xB695A891, _blacklist.ToString());
                    SetLanguageHash(0x7AC3D0C9, FrontEndData.Hash($"BLACKLIST_RIVAL_{_blacklist:00}_AKA"));
                    var tag = FrontEndData.TextureByName($"RIVAL_{_blacklist:00}_GRAF");
                    var img = Obj(0xF5A2A087);
                    if (tag && img != null) View.SetTexture(img, tag);
                }
                else
                {
                    // FECarRecord::GetManuLogoHash / GetLogoHash (VDB:920-945) fall back to GENERIC_LOGO_128 / GENERIC_LOGO_256
                    var manu = CarLotScreen.Badge(_car.Car) ?? FrontEndData.TextureByName("GENERIC_LOGO_128");
                    var n = Obj(0x3E01AD1D);
                    if (manu && n != null) View.SetTexture(n, manu);
                    var logo = FrontEndData.TextureByName("SECONDARY_LOGO_" + _car.Car.ToUpperInvariant()) ?? FrontEndData.TextureByName("GENERIC_LOGO_256");
                    var l = Obj(0xB05DD708);
                    if (logo && l != null) View.SetTexture(l, logo);
                }
                FE.Showroom.SetActive(true);
                FE.Showroom.Show(_car.Car, _car.Paint, _car);
            }
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            if (msg == FEHash.PadAccept || msg == FEHash.PadBack)
                FE.Defer(() => { if (FE.Top == this) FE.Pop(); });
        }
    }
}
