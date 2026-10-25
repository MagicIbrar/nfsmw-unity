using System.Collections.Generic;
using NfsMw.Core;
using NfsMw.Game;

namespace NfsMw.UI
{
    /// <summary>
    /// BootFlowManager (MenuScreens/Loading/FEBootFlowManager.cpp; PC ctor 0x58E9C0, JumpToScreen 0x57DAF0, DoAttract
    /// 0x57DB80, ChangeToNextBootFlowScreen 0x57DBB0, JumpToHead 0x58EC50) with MovieScreen (FEMovieScreen.cpp; ctor 0x58ECA0,
    /// NotificationMessage 0x57DDA0). eIsWidescreen (0x4FB000) is constant false on PC, so the list is the NTSC table
    /// 0x8F3C94 — DiscErrorPC, MC_Bootup, LS_EAlogo, LS_PSA, MW_LS_Splash, MC_Main, MainMenu — or, when BuildRegion::IsPal
    /// (the registry language's region 1 / 5..9), the PAL table 0x8F3CB0 with MW_LS_AttractFMV before the splash.
    /// Movie screens (MovieData: LS_EALogo "ealogo" not bypassable, LS_PSA "psa" bypassable with subtitles, MW_LS_AttractFMV
    /// "attract_movie" bypassable) go to the next screen when the movie ends (0xC3960EB9, also at once when the file is
    /// missing — the PC install's MOVIES has none of the three) or on accept / start when bypassable; the splash's accept /
    /// start after INIT_COMPLETE goes next; its SCREEN_TICK after SplashScreenMovieTimeout (30 s, 0x8F3CD0) without input
    /// calls DoAttract (no attract entry → the timer is parked at 0x7FFFFFFF). The last screen destroys the manager.
    /// Port: DiscErrorPC (the CD check), MC_Bootup / MC_Main (memory-card boot load: the port's profile slot files are read
    /// by FEDb / GameSession) are passed straight through [N/A by design].
    /// </summary>
    public static class BootFlow
    {
        static readonly string[] Ntsc = { "DiscErrorPC", "MC_Bootup", "LS_EAlogo", "LS_PSA", "MW_LS_Splash", "MC_Main", "MainMenu" };
        static readonly string[] Pal = { "DiscErrorPC", "MC_Bootup", "LS_EAlogo", "LS_PSA", "MW_LS_AttractFMV", "MW_LS_Splash", "MC_Main", "MainMenu" };
        /// <summary>SplashScreenMovieTimeout (.data 0x8F3CD0).</summary>
        public const float SplashScreenMovieTimeout = 30f;

        static string[] _list;
        static int _current = -1;
        public static bool Active => _list != null;

        static readonly Dictionary<string, (string movie, bool bypassable)> MovieData = new Dictionary<string, (string, bool)>
        {
            ["LS_EAlogo"] = ("ealogo", false), ["LS_EA_hidef"] = ("eahd_bumper", false), ["LS_PSA"] = ("psa", true),
            ["MW_LS_IntroFMV"] = ("intro_movie", true), ["MW_LS_AttractFMV"] = ("attract_movie", true),
        };

        /// <summary>BootFlowManager::Init + the first screen (FEManager::SetFirstScreen).</summary>
        public static void Start()
        {
            _list = Localize.IsPal ? Pal : Ntsc;
            _current = 0;
            Enter();
        }

        /// <summary>ChangeToNextBootFlowScreen (0x57DBB0).</summary>
        public static void Next()
        {
            if (_list == null) return;
            _current++;
            Enter();
        }

        /// <summary>DoAttract (0x57DB80): jump to the first screen whose name contains "Attract"; false when there is none.</summary>
        public static bool DoAttract()
        {
            if (_list == null) return false;
            int i = System.Array.FindIndex(_list, n => n.Contains("Attract"));
            if (i < 0) return false;
            _current = i;
            Enter();
            return true;
        }

        static void Enter()
        {
            if (_list == null || _current >= _list.Length) { _list = null; return; }
            string name = _list[_current];
            if (_current == _list.Length - 1) _list = null;   // the tail destroys the manager
            var fe = FrontEnd.Create();
            switch (name)
            {
                case "DiscErrorPC": case "MC_Bootup": case "MC_Main":
                    Next();
                    return;
                case "MW_LS_Splash":
                    fe.CloseAll();
                    fe.Push(new PressStartScreen());
                    return;
                case "MainMenu":
                    fe.CloseAll();
                    fe.Push(new MainMenuScreen());
                    return;
            }
            if (MovieData.TryGetValue(name, out var m))
            {
                fe.CloseAll();
                // MoviePlayer runs the MovieScreen (sound states, skip rules by name); the end or a missing file goes next
                MoviePlayer.TryPlay(m.movie, Next, MovieScreen.Boot);
                return;
            }
            Next();
        }
    }
}
