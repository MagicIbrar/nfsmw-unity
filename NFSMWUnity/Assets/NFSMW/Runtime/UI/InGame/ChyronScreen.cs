using NfsMw.Audio;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// EA_Trax.fng — the EA Trax "now playing" chyron (InGame/FEPkg_Chyron.cpp; PC InitChyron 0x569C80, CreateChyronScreen
    /// 0x58D600, SummonChyron 0x595ED0, Chyron::NotificationMessage 0x595E80, Chyron::Start 0x58D670, DismissChyron 0x569CC0).
    /// The package (GLOBAL\GlobalB.lzc) is pushed as a no-control package (0x68) when SFXObj_PFEATrax::NotifyChyron
    /// (0x4F4FC0) names a new licensed song; it starts at once in the front end, else 5 s later (RealTimer · 0.00025):
    /// <list type="bullet">
    /// <item>Start: title 0xF5387816, artist 0x11DB9E17, album 0x48097852 (FEPrintf); the widest text picks the panel:
    /// ≤ 185 px → 0x57521F29 (narrow), ≤ 250 → 0x57520493, else 0x73D17048; then TRAX_POS_2 in the front end, TRAX_POS_3 in a
    /// D-Day race while autosaving [?: not reproduced], else TRAX_POS_1; then WIDESCREEN_MODE / NORMAL_MODE (front end) or
    /// WIDESCREEN_MODEHUD / NORMAL_MODEHUD (in game) to every package.</item>
    /// <item>The package's timer text 545570C6 runs 6 s, posts 0x835C45E8 (LEAVE on every part) and, 1.62 s later,
    /// 0x84CFC46F to the game; the exe keeps the package pushed and invisible — the port destroys the view then [ours].</item>
    /// <item>DismissChyron on movies / tutorials, EQuitToFE and EReloadGame: the port drops it while a movie plays or no
    /// music system exists [ours].</item>
    /// </list>
    /// </summary>
    public sealed class ChyronScreen : MonoBehaviour
    {
        static ChyronScreen _instance;
        Canvas _canvas;
        FEView _view;
        SongInfo _song;
        float _summonAt = -1;
        bool _started;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Hook()
        {
            MusicSystem.Chyron += Summon;   // SummonChyron from NotifyChyron
        }

        /// <summary>SummonChyron(title, artist, album): (re)push EA_Trax.fng and start the delay timer.</summary>
        public static void Summon(SongInfo song)
        {
            if (song == null) return;
            if (!_instance)
            {
                var go = new GameObject("EA Trax Chyron");
                DontDestroyOnLoad(go);
                _instance = go.AddComponent<ChyronScreen>();
            }
            _instance.Begin(song);
        }

        /// <summary>DismissChyron: pops the package if it is pushed.</summary>
        public static void Dismiss()
        {
            if (_instance) _instance.Drop();
        }

        void Begin(SongInfo song)
        {
            Drop();
            _song = song;
            _summonAt = Time.realtimeSinceStartup;
            _started = false;
        }

        void Drop()
        {
            if (_view) Destroy(_view.gameObject);
            _view = null;
            _summonAt = -1;
        }

        static bool InFrontEnd => !FEMenuScreen.InGame;

        void Update()
        {
            if (_summonAt < 0) return;
            if (MoviePlayer.Playing) { Drop(); return; }
            if (!_started)
            {
                // Chyron::NotificationMessage: start at once in the front end, else once 5 s have passed
                if (!InFrontEnd && Time.realtimeSinceStartup - _summonAt < 5f) return;
                Start();
            }
        }

        /// <summary>Chyron::Start (0x58D670).</summary>
        void Start()
        {
            _started = true;
            FrontEndData.Load();
            if (!FrontEndData.Packages.ContainsKey("EA_TRAX")) { Drop(); return; }
            if (!_canvas)
            {
                _canvas = FrontEndCanvas.Create("Chyron Canvas", null, 20);
                _canvas.transform.SetParent(transform, false);
            }
            _view = FEView.Open("EA_TRAX", _canvas.transform, null, false, true);
            _view.MessagesEnabled = true;
            _view.GameMessage += (msg, n) => { if (msg == 0x84CFC46F) Drop(); };
            float width = 0;
            foreach (var (hash, text) in new[] { (0xF5387816u, _song.Name), (0x11DB9E17u, _song.Artist), (0x48097852u, _song.Album) })
            {
                var n = _view.Find(hash);
                if (n == null) continue;
                _view.SetText(n, text ?? "");
                if (n.Label != null) width = Mathf.Max(width, n.Label.TextWidth);
            }
            _view.PackageMessage(width <= 185f ? 0x57521F29u : width <= 250f ? 0x57520493u : 0x73D17048u);
            bool fe = InFrontEnd;
            _view.PackageMessage(FrontEndData.Hash(fe ? "TRAX_POS_2" : "TRAX_POS_1"));
            bool wide = (float)Screen.width / Screen.height > 1.45f;
            _view.QueueMessage(FrontEndData.Hash(fe ? (wide ? "WIDESCREEN_MODE" : "NORMAL_MODE") : (wide ? "WIDESCREEN_MODEHUD" : "NORMAL_MODEHUD")), null, FEView.ToBroadcast);
        }

        void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }
    }
}
