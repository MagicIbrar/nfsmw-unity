using System.IO;
using NfsMw.Core;
using NfsMw.Game;

namespace NfsMw.UI
{
    /// <summary>
    /// MC_ProfileManager.fng — UIProfileManager (Database/uiProfileManager.cpp; PC create 0x5599C0, ctor 0x54F420, Setup
    /// 0x543300, Refresh 0x543180, NotificationMessage 0x543280), the main menu's "Alias Manager" (IsMemcardEnabled 0x8F8708 = 1 on
    /// PC). IconScrollerMenu: Create New (0x43798644, 0x55423473), Load (0x2287E063, 0x0018ECFF), Save (0x228B7E32, 0x001C8ACE;
    /// IsGreyOut while no profile is loaded, then FEngSetColor back to its colour), Delete (0x0D9035CE, 0x56B00632); the PMPopDelete
    /// row is compiled out (`if (false)`). Header 0xBCB18F38 "alias manager"; with a profile NAME_GROUP visible and the name in
    /// 0xEB406FEC. Back → MainMenu.fng; 0x7E998E5E (the memory card screens' "done") → RefreshCurrentRide + Refresh.
    /// Each option is MemcardEnter(pkg, pkg, op): create 0x61, load 0x411 (+ 0x3A2BE557 / 0x8867412D), save 0x2251, delete 0x31 —
    /// the MC_* memory card screens with their file list and alias keyboard. [ours] the port keeps one profile file per slot
    /// (Profile.PathFor, FEDb's nfsmw_fedb_&lt;slot&gt;.json): create = a fresh profile under the alias, load = re-read the slot,
    /// save = write it, delete = remove the slot's files; create / delete ask first (yes / no on the option's description) in
    /// place of the memory card dialogs.
    /// MC_Deleteprofile.fng — UIDeleteProfile (0x5608E0 / 0x559A20 / Setup 0x54F4C0): Create New + Delete (0x9F014666), header
    /// 0xE6F55DF0, back → MC_ProfileManager; reached only from the memory card flow (the "profile full" path), not ported.
    /// </summary>
    public sealed class ProfileManagerScreen : IconMenuScreen
    {
        public override string Package => FrontEndData.Packages != null && FrontEndData.Packages.ContainsKey("MC_ProfileManager") ? "MC_ProfileManager" : "MainMenu_Sub";
        protected override uint TitleHash => 0xBCB18F38;
        const uint MsgMemcardDone = 0x7E998E5E;

        /// <summary>cFrontendDatabase::bProfileLoaded.</summary>
        static bool ProfileLoaded => GameSession.Profile != null && GameSession.Profile.CareerStarted;

        protected override void Setup()
        {
            AddOption(0x43798644, 0x55423473, () => FEDialog.ShowYesNo(FE, FEDb.L(0x55423473), CreateNew));
            AddOption(0x2287E063, 0x0018ECFF, Load);
            AddOption(0x228B7E32, 0x001C8ACE, Save);
            int save = Options.Count - 1;
            AddOption(0x0D9035CE, 0x56B00632, () => FEDialog.ShowYesNo(FE, FEDb.L(0x56B00632), Delete));
            Options[save].GreyOut = !ProfileLoaded;   // Refresh: mpSave->IsGreyOut = !bProfileLoaded
        }

        public override void Open()
        {
            base.Open();
            Refresh();
        }

        public override void Resume()
        {
            base.Resume();
            Refresh();
        }

        /// <summary>UIProfileManager::Refresh (0x543180).</summary>
        protected override void Refresh()
        {
            var n = View.Find("NAME_GROUP");
            if (ProfileLoaded)
            {
                View.SetVisible(n, true);
                SafehouseUi.SetLang(View, 0xEB406FEC, GameSession.Profile.Name);
            }
            else View.SetVisible(n, false);
        }

        /// <summary>The memory card flow's 0x7E998E5E: RefreshCurrentRide + Refresh (and the Save row's grey state).</summary>
        void MemcardDone()
        {
            GameSession.PrepareProfile();
            FE.Replace(new ProfileManagerScreen());
        }

        void CreateNew()
        {
            string alias = GameSession.Profile != null && !string.IsNullOrEmpty(GameSession.Profile.Name) ? GameSession.Profile.Name : "Player";
            GameSession.Profile = new Profile { Name = alias };
            GameSession.Profile.Save();
            FEDb.Reload();
            MemcardDone();
        }

        void Load()
        {
            GameSession.Profile = Profile.Load();
            FEDb.Reload();
            MemcardDone();
        }

        void Save()
        {
            if (!ProfileLoaded) return;
            GameSession.Profile.Save();
            FEDb.Save();
            MemcardDone();
        }

        void Delete()
        {
            try
            {
                var p = Profile.PathFor();
                if (File.Exists(p)) File.Delete(p);
            }
            catch (IOException) { }
            GameSession.Profile = new Profile();
            FEDb.Reload();
            MemcardDone();
        }

        protected override void OnBack() => FE.Replace(new MainMenuScreen());
    }
}
