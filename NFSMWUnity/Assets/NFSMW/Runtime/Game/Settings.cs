using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>Player options (Options screen), kept in PlayerPrefs and applied at start-up.</summary>
    public static class Settings
    {
        public static float Master = 1f, Music = 0.6f;
        public static bool Kmh, VSync = true;
        public static FullScreenMode Mode = FullScreenMode.FullScreenWindow;
        public static int Width, Height;

        static bool _loaded;

        public static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            Master = PlayerPrefs.GetFloat("nfsmw.master", Master);
            Music = PlayerPrefs.GetFloat("nfsmw.music", Music);
            Kmh = PlayerPrefs.GetInt("nfsmw.kmh", 0) == 1;
            VSync = PlayerPrefs.GetInt("nfsmw.vsync", 1) == 1;
            Mode = (FullScreenMode)PlayerPrefs.GetInt("nfsmw.mode", (int)Screen.fullScreenMode);
            Width = PlayerPrefs.GetInt("nfsmw.width", Screen.width);
            Height = PlayerPrefs.GetInt("nfsmw.height", Screen.height);
        }

        public static void Save()
        {
            PlayerPrefs.SetFloat("nfsmw.master", Master);
            PlayerPrefs.SetFloat("nfsmw.music", Music);
            PlayerPrefs.SetInt("nfsmw.kmh", Kmh ? 1 : 0);
            PlayerPrefs.SetInt("nfsmw.vsync", VSync ? 1 : 0);
            PlayerPrefs.SetInt("nfsmw.mode", (int)Mode);
            PlayerPrefs.SetInt("nfsmw.width", Width);
            PlayerPrefs.SetInt("nfsmw.height", Height);
            PlayerPrefs.Save();
        }

        /// <summary>Volume and v-sync now; the window only when it differs (a batch or test run keeps its own).</summary>
        public static void Apply(bool display = true)
        {
            Load();
            AudioListener.volume = Master;
            QualitySettings.vSyncCount = VSync ? 1 : 0;
            if (display && !Application.isBatchMode && !Application.isEditor && Width > 0 && Height > 0 &&
                (Screen.width != Width || Screen.height != Height || Screen.fullScreenMode != Mode))
                Screen.SetResolution(Width, Height, Mode);
        }

        public static void Defaults()
        {
            Master = 1f;
            Music = 0.6f;
            Kmh = false;
            VSync = true;
            Mode = FullScreenMode.FullScreenWindow;
            var r = Screen.currentResolution;
            Width = r.width;
            Height = r.height;
        }

        /// <summary>Speed for the HUD in the chosen unit.</summary>
        public static int Speed(float mps) => Mathf.RoundToInt(mps * (Kmh ? 3.6f : 2.23694f));
        public static string SpeedUnit => Kmh ? "KM/H" : "MPH";
    }
}
