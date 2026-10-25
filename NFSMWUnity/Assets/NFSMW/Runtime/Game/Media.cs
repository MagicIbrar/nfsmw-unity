using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;
using NfsMw.UI;
using UnityEngine;
using UnityEngine.Video;

namespace NfsMw.Game
{
    /// <summary>Which of the game's movie screens a movie runs in; it decides skipping (see MoviePlayer.CanSkip).</summary>
    public enum MovieScreen
    {
        /// <summary>Chosen from the name: boot movies, *_tutorial, FE story movies (blacklist_NN, rap30, saf25), else in-game.</summary>
        Auto,
        /// <summary>Boot flow MovieScreen (FEMovieScreen.cpp MovieData): ealogo / eahd_bumper not bypassable, psa / intro / attract are.</summary>
        Boot,
        /// <summary>FEAnyMovieScreen (FEAnyMovie.fng, safehouse movies): Accept/Start skips; during D-Day only after 5 s.</summary>
        FrontEnd,
        /// <summary>InGameAnyMovieScreen (InGameAnyMovie.fng, EPlayRaceMovie): skips during D-Day, else only after 5 s.</summary>
        InGame,
        /// <summary>FEAnyTutorialScreen / InGameAnyTutorialScreen: the first viewing per career is not skippable [?].</summary>
        Tutorial,
    }

    /// <summary>
    /// Full-screen movies from the game's MOVIES folder, decoded at run time from the original EA VP6 files
    /// (Core/EaMovie + Core/Vp6Decoder on a worker thread; YUV planes into three R8 textures converted by the
    /// NFSMW/MovieYUV shader; the EA-XA soundtrack through an AudioSource on a child object, which is also the clock).
    /// File choice is CalculateMovieFilename (decomp FEObjectCallbacks.cpp:37, speed.exe "%sMOVIES\%s%s%s%s" at
    /// file 0x49B39C): MOVIES\&lt;name&gt;_&lt;language&gt;_ntsc.vp6, English when the language has none. Volume is
    /// MoviePlayer::GetMovieCategoryVolume's table (MoviePlayer.cpp:61). Game sound is paused while a movie plays
    /// (SoundPause eSNDPAUSE_MOVIE). Skipping follows the movie screens (MovieScreen). Tutorial movies show their
    /// SUBTITLES text (SubTitle.cpp; other movies only for non-English languages). If decoding fails, a converted copy
    /// next to the file (name.mp4 / .webm, the old path) still plays through Unity's VideoPlayer.
    /// </summary>
    public sealed class MoviePlayer : MonoBehaviour
    {
        static readonly string[] Extensions = { ".mp4", ".webm", ".mov", ".m4v" };

        /// <summary>GetLanguageName of the current language (the port is English only) [ours].</summary>
        public static string Language = "english";

        /// <summary>BuildRegion::IsPal; the PC install ships _ntsc movies only.</summary>
        public static bool Pal;

        /// <summary>Set by the career (D-Day = the prologue) to apply the D-Day skip rules; defaults to the profile stage.</summary>
        public static Func<bool> IsDDay = () => GameSession.Profile != null && GameSession.Profile.Stage == Story.Prologue;

        /// <summary>Under the QA tour (-qa) a movie ends after this many seconds [ours].</summary>
        public static float QaSeconds = 4f;
        static bool? _qa;

        /// <summary>The movie on screen, if any.</summary>
        public static MoviePlayer Current { get; private set; }
        public static bool Playing => Current != null;

        /// <summary>MovieVolumeArray (MoviePlayer.cpp:61, speed.exe strings at file 0x49A7A0): 0..127 per movie, 127 otherwise.</summary>
        static readonly (string prefix, int volume)[] VolumeTable =
        {
            ("attract_movie", 0x41), ("drag_tutorial", 0x37), ("ealogo", 0x3C), ("intro_movie", 0x37), ("sprint_tutorial", 0x37),
            ("tollbooth_tutorial", 0x37), ("bounty_tutorial", 0x37), ("pursuit_tutorial", 0x37), ("speedtrap_tutorial", 0x37),
            ("psa", 0x32), ("blacklist_", 0x2D), ("storyfmv_", 0x50),
        };

        static readonly string[] TutorialMovies = { "drag_tutorial", "speedtrap_tutorial", "tollbooth_tutorial", "bounty_tutorial", "pursuit_tutorial" };

        /// <summary>
        /// The file for a movie name: MOVIES\name_language_ntsc.vp6 (English fallback), else a converted copy
        /// (name[_english[_ntsc]].mp4/.webm/...), else null.
        /// </summary>
        public static string Find(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            GameData.Mount();
            var dir = Path.Combine(GameData.Root, "MOVIES");
            if (!Directory.Exists(dir)) return null;
            string region = Pal ? "_pal" : "_ntsc";
            foreach (var lang in new[] { Language, "english" }.Distinct())
            {
                var vp6 = Path.Combine(dir, $"{name}_{lang}{region}.vp6");
                if (File.Exists(vp6)) return vp6;
            }
            return FindConverted(name);
        }

        static string FindConverted(string name)
        {
            var dir = Path.Combine(GameData.Root, "MOVIES");
            if (!Directory.Exists(dir)) return null;
            foreach (var file in Directory.GetFiles(dir))
            {
                string stem = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file).ToLowerInvariant();
                if (!Extensions.Contains(ext)) continue;
                if (string.Equals(stem, name, StringComparison.OrdinalIgnoreCase) || string.Equals(stem, name + "_english", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stem, name + "_english_ntsc", StringComparison.OrdinalIgnoreCase))
                    return file;
            }
            return null;
        }

        /// <summary>Plays the movie if its file exists; done runs afterwards (immediately when there is none).</summary>
        public static bool TryPlay(string name, Action done) => TryPlay(name, done, MovieScreen.Auto);

        /// <summary>
        /// Plays a movie in the given screen. firstTutorialView: the career had not seen this tutorial yet (then it cannot
        /// be skipped). Returns false (and runs done at once) when there is no playable file, as FEngMovieStarter queues
        /// the movie-finished message when bFileExists fails.
        /// </summary>
        public static bool TryPlay(string name, Action done, MovieScreen screen, bool firstTutorialView = false)
        {
            var file = Find(name);
            if (file == null) { done?.Invoke(); return false; }
            if (Current) Current.Finish();
            var go = new GameObject("Movie " + name);
            DontDestroyOnLoad(go);
            var m = go.AddComponent<MoviePlayer>();
            m._name = name;
            m._done = done;
            m._screen = screen == MovieScreen.Auto ? ScreenFor(name) : screen;
            m._lockedTutorial = m._screen == MovieScreen.Tutorial && firstTutorialView;
            m._cam = go.AddComponent<Camera>();
            m._cam.clearFlags = CameraClearFlags.SolidColor;
            m._cam.backgroundColor = Color.black;
            m._cam.cullingMask = 0;
            m._cam.depth = 200;
            Current = m;
            m.PauseGameSound(true);
            if (m._screen == MovieScreen.Boot)
            {
                // FEMovieScreen ctor (0x58ED07 / 0x58ED1A): ESndGameState(1, on), sound control state FMV on
                NfsMw.Audio.SoundState.SetGameFlow(1, true);
                NfsMw.Audio.SoundState.Set(true, NfsMw.Audio.SndCtl.Fmv);
            }
            bool ok = file.EndsWith(".vp6", StringComparison.OrdinalIgnoreCase) ? m.StartDecoded(file) : false;
            if (!ok)
            {
                var copy = FindConverted(name);
                if (copy == null || !m.StartVideoPlayer(copy))
                {
                    // nothing playable: same as a missing file (done once, false)
                    m._done = null;
                    m.Finish();
                    done?.Invoke();
                    return false;
                }
            }
            m.LoadSubtitles();
            Debug.Log($"NFSMW movie: {Path.GetFileName(file)} ({m._screen}{(m._stream != null ? $", VP6 {m._stream.Info.Width}x{m._stream.Info.Height} {m._stream.Info.Fps:0.00} fps, {m._stream.Info.AudioRate} Hz" : ", converted copy")})");
            return true;
        }

        /// <summary>Ends the movie on screen (tests, the QA tour); its done callback runs.</summary>
        public static void SkipAll()
        {
            if (Current) Current.Finish();
        }

        static MovieScreen ScreenFor(string name)
        {
            string n = name.ToLowerInvariant();
            if (n == "ealogo" || n == "eahd_bumper" || n == "psa" || n == "intro_movie" || n == "attract_movie") return MovieScreen.Boot;
            if (n.EndsWith("_tutorial")) return MovieScreen.Tutorial;
            // FEAnyMovieScreen: the safehouse step machine (blacklist_NN, rap30) and DoSafeHouseIntro (saf25)
            if (n.StartsWith("blacklist_") || n == "storyfmv_rap30" || n == "storyfmv_saf25") return MovieScreen.FrontEnd;
            return MovieScreen.InGame;
        }

        static int VolumeOf(string name)
        {
            // GetMovieCategoryVolume: the last matching prefix wins; ShutJosieUp (psa = 0) is a debug switch
            int v = 0x7F;
            foreach (var (prefix, volume) in VolumeTable)
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) v = volume;
            return v;
        }

        string _name;
        MovieScreen _screen;
        bool _lockedTutorial;
        Camera _cam;
        Action _done;
        float _started;
        bool _finished;
        bool _savedListenerPause;

        // decoded path
        EaMovieStream _stream;
        Texture2D _y, _u, _v;
        Material _mat;
        MovieAudioFeed _feed;
        readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();
        double _clock;
        bool _hasPicture;
        float[] _discard;
        long _discarded;

        // converted-copy path
        VideoPlayer _video;

        bool StartDecoded(string file)
        {
            try
            {
                _stream = new EaMovieStream(file);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"NFSMW movie {_name}: {e.Message}");
                _stream = null;
                return false;
            }
            var shader = Shader.Find("NFSMW/MovieYUV");
            if (!shader)
            {
                Debug.LogWarning("NFSMW movie: shader NFSMW/MovieYUV missing");
                _stream.Dispose();
                _stream = null;
                return false;
            }
            _mat = new Material(shader);
            int w = _stream.Width, h = _stream.Height;
            _y = Plane(w, h);
            _u = Plane(w / 2, h / 2);
            _v = Plane(w / 2, h / 2);
            _mat.SetTexture("_UTex", _u);
            _mat.SetTexture("_VTex", _v);
            if (_stream.Info.AudioChannels > 0 && AudioSettings.outputSampleRate > 0)
            {
                var child = new GameObject("Movie audio");
                child.transform.SetParent(transform, false);
                var src = child.AddComponent<AudioSource>();
                src.spatialBlend = 0;
                src.ignoreListenerPause = true;
                src.bypassListenerEffects = true;
                src.playOnAwake = false;
                int rate = AudioSettings.outputSampleRate;
                src.clip = AudioClip.Create("movie silence", rate, 2, rate, false);
                src.loop = true;
                _feed = child.AddComponent<MovieAudioFeed>();
                _feed.Init(_stream, _stream.Info.AudioRate, rate, VolumeOf(_name) / 127f);
                src.Play();
            }
            _stream.Start();
            _watch.Start();
            _started = Time.unscaledTime;
            return true;
        }

        static Texture2D Plane(int w, int h)
        {
            var t = new Texture2D(w, h, TextureFormat.R8, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear, name = "movie plane" };
            var black = new byte[w * h];
            t.LoadRawTextureData(black);
            t.Apply(false);
            return t;
        }

        bool StartVideoPlayer(string file)
        {
            _video = gameObject.AddComponent<VideoPlayer>();
            _video.url = "file://" + file.Replace('\\', '/');
            _video.renderMode = VideoRenderMode.CameraNearPlane;
            _video.targetCamera = _cam;
            _video.aspectRatio = VideoAspectRatio.FitInside;
            _video.audioOutputMode = VideoAudioOutputMode.Direct;
            _video.loopPointReached += _ => Finish();
            _video.errorReceived += (_, msg) => { Debug.LogWarning($"NFSMW movie {_name}: {msg}"); Finish(); };
            _video.Play();
            _started = Time.unscaledTime;
            return true;
        }

        /// <summary>Movie time in seconds: the soundtrack's output position when it plays, else a stopwatch.</summary>
        double Clock()
        {
            double t = _watch.Elapsed.TotalSeconds;
            if (_feed != null && _feed.Running) t = _feed.Seconds;
            if (t < _clock) t = _clock; // never step back
            _clock = t;
            return t;
        }

        void Update()
        {
            if (_finished) return;
            if (SkipPressed() && CanSkip()) { Finish(); return; }
            // [ours] the QA tour (-qa) only screenshots a movie: end it after QaSeconds so the tour's waits hold
            // (its StoryCard.SkipAll does not reach this player; MoviePlayer.SkipAll does)
            _qa ??= NfsMw.QaTour.Requested;
            if (_qa == true && Time.unscaledTime - _started > QaSeconds) { Finish(); return; }
            if (_stream == null) return;

            double t = Clock();
            // no audio device (or it stopped): drop the soundtrack at the clock's pace so the decoder never stalls
            if ((_feed == null || !_feed.Running) && _stream.Info.AudioChannels > 0 && _watch.Elapsed.TotalSeconds > 0.3)
            {
                _discard ??= new float[8192];
                long due = (long)(t * _stream.Info.AudioRate) - _discarded;
                while (due > 0)
                {
                    int n = _stream.ReadAudio(_discard, 0, (int)Math.Min(_discard.Length, due * 2));
                    if (n == 0) break;
                    _discarded += n / 2;
                    due -= n / 2;
                }
            }

            var frame = _stream.Take(t);
            if (frame != null)
            {
                var p = frame.Picture;
                _y.LoadRawTextureData(p.Y);
                _y.Apply(false);
                _u.LoadRawTextureData(p.U);
                _u.Apply(false);
                _v.LoadRawTextureData(p.V);
                _v.Apply(false);
                _stream.Release(frame);
                if (!_hasPicture) SubtitleStart();
                _hasPicture = true;
            }

            if (_stream.Error != null && !_hasPicture)
            {
                // nothing decodable: try the converted copy, else end
                Debug.LogWarning($"NFSMW movie {_name}: {_stream.Error}");
                StopDecoded();
                var copy = FindConverted(_name);
                if (copy == null || !StartVideoPlayer(copy)) Finish();
                return;
            }
            // MoviePlayer::UpdateFunction: the movie is over when no frame is left and the audio has finished
            bool audioDone = _feed == null || !_feed.Running ? true : _feed.Drained;
            if (_stream.VideoDrained && audioDone && t >= _stream.DecodedFrames / _stream.Info.Fps) Finish();
        }

        static bool SkipPressed()
        {
            var kb = UnityEngine.InputSystem.Keyboard.current;
            var pad = UnityEngine.InputSystem.Gamepad.current;
            // __PAD_ACCEPT__ / __PAD_START__ (Enter / Esc on PC; Space kept from the old player [ours])
            return (kb != null && (kb.enterKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame || kb.escapeKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame)) ||
                   (pad != null && (pad.buttonSouth.wasPressedThisFrame || pad.startButton.wasPressedThisFrame));
        }

        /// <summary>The movie screens' NotificationMessage rules for __PAD_ACCEPT__ / __PAD_START__.</summary>
        bool CanSkip()
        {
            float age = Time.unscaledTime - _started;
            if (age < 0.25f) return false; // [ours] the key that started the movie must not end it
            bool bypass = age > 5f; // MoviePlayer_Bypass: 5000 ms since MoviePlayer_Play
            bool dday = IsDDay != null && IsDDay();
            switch (_screen)
            {
                case MovieScreen.Boot:
                    string n = _name.ToLowerInvariant();
                    return n != "ealogo" && n != "eahd_bumper";
                case MovieScreen.FrontEnd: return !dday || bypass;
                case MovieScreen.InGame: return dday || bypass;
                case MovieScreen.Tutorial: return !_lockedTutorial;
                default: return true;
            }
        }

        void StopDecoded()
        {
            if (_feed) { _feed.Stop(); Destroy(_feed.gameObject); _feed = null; }
            _stream?.Dispose();
            _stream = null;
        }

        void Finish()
        {
            if (_finished || !this) return;
            _finished = true;
            StopDecoded();
            PauseGameSound(false);
            if (_screen == MovieScreen.Boot)
            {
                // FEMovieScreen::NotificationMessage (0x57DE24): both off when the movie screen ends
                NfsMw.Audio.SoundState.SetGameFlow(1, false);
                NfsMw.Audio.SoundState.Set(false, NfsMw.Audio.SndCtl.Fmv);
            }
            if (Current == this) Current = null;
            var d = _done;
            _done = null;
            Destroy(gameObject);
            d?.Invoke();
        }

        void OnDestroy()
        {
            StopDecoded();
            if (Current == this)
            {
                Current = null;
                PauseGameSound(false);
            }
            if (_y) Destroy(_y);
            if (_u) Destroy(_u);
            if (_v) Destroy(_v);
            if (_mat) Destroy(_mat);
        }

        void PauseGameSound(bool pause)
        {
            // SoundPause(true/false, eSNDPAUSE_MOVIE): everything but the movie (its source ignores the listener pause)
            if (pause)
            {
                _savedListenerPause = AudioListener.pause;
                AudioListener.pause = true;
            }
            else AudioListener.pause = _savedListenerPause;
        }

        // ---- picture ----

        static Texture2D _black;

        /// <summary>
        /// The picture rectangle: the "Movie" object (FE_Movie) of FEAnyMovie.fng / InGameAnyMovie.fng / the tutorial
        /// packages is 640 x 512 FE units at the centre of the 640x480 screen (WS_ packages: 900 x 512, unused on PC where
        /// eIsWidescreen @0x4FB000 returns false). cFEngRender::RenderMovie (0x599DB0) maps the whole 1024x512 picture onto
        /// it with V flipped (the picture is bottom-up), so the frame is stretched to 4:3 proportions (the pixels are
        /// anamorphic) and 16 units at the top and bottom fall off screen. FE units scale like the port's front end: 480
        /// units = the screen height, centred.
        /// </summary>
        static Rect PictureRect() => FeRect(0, 0, MovieRectSize.x, MovieRectSize.y);

        /// <summary>Size of the movie object in 640x480 FE units.</summary>
        public static Vector2 MovieRectSize = new Vector2(640, 512);

        /// <summary>A rectangle centred on FE position (x, y) (screen centre = 0,0, +y down) with FE size (w, h).</summary>
        static Rect FeRect(float x, float y, float w, float h)
        {
            float s = Screen.height / 480f;
            return new Rect(Screen.width * 0.5f + (x - w * 0.5f) * s, Screen.height * 0.5f + (y - h * 0.5f) * s, w * s, h * s);
        }

        void OnGUI()
        {
            if (_video != null || Event.current.type != EventType.Repaint) return;
            GUI.depth = -1000;
            if (!_black)
            {
                _black = new Texture2D(1, 1);
                _black.SetPixel(0, 0, Color.black);
                _black.Apply();
            }
            GUI.DrawTexture(new Rect(0, 0, Screen.width, Screen.height), _black);
            if (_stream == null || !_hasPicture) return;
            // EFadeScreenOff(FEHASH_15_IN) as the screen opens: fade in from black [?] 0.5 s
            float fade = Mathf.Clamp01((Time.unscaledTime - _started) / 0.5f);
            _mat.SetFloat("_Fade", fade);
            Graphics.DrawTexture(PictureRect(), _y, _mat);
            DrawSubtitles();
        }

        // ---- subtitles (SubTitle.cpp) ----

        struct SubtitleLine
        {
            public int Time; // 1/10 s
            public uint Hash;
        }

        const uint NoText = 0x1A20BA; // SubTitler::RefreshText: entries with this hash clear the text
        List<SubtitleLine> _subs;
        bool _tutorial;
        int _next;
        double _subStart = -1;
        string _textA, _textB, _title;

        void LoadSubtitles()
        {
            _tutorial = TutorialMovies.Contains(_name.ToLowerInvariant());
            // ShouldShowSubTitles: only tutorials in English
            if (!_tutorial && Language == "english") return;
            try
            {
                var path = Path.Combine(GameData.Root, "SUBTITLES", _name);
                if (!File.Exists(path)) return;
                var d = File.ReadAllBytes(path);
                _subs = new List<SubtitleLine>();
                // SubtitleInfo {u16 startTime, u16 pad, u32 stringHash}, ended by startTime 0xFFFF (entry 0 is the first line)
                for (int o = 0; o + 8 <= d.Length; o += 8)
                {
                    int t = d[o] | d[o + 1] << 8;
                    if (t == 0xFFFF) break;
                    _subs.Add(new SubtitleLine { Time = t, Hash = (uint)(d[o + 4] | d[o + 5] << 8 | d[o + 6] << 16 | d[o + 7] << 24) });
                }
                FrontEndData.Load();
                if (_tutorial)
                {
                    // FEAnyTutorialScreen: the title objects get bStringHash("_LABEL", FEngHashString("TUTORIAL_xxx"))
                    string key = _name.ToLowerInvariant() switch
                    {
                        "drag_tutorial" => "TUTORIAL_DRAG",
                        "speedtrap_tutorial" => "TUTORIAL_SPEEDTRAPRACE",
                        "tollbooth_tutorial" => "TUTORIAL_TOLLBOOTH",
                        "bounty_tutorial" => "TUTORIAL_BOUNTY",
                        _ => "TUTORIAL_PURSUIT",
                    };
                    _title = Text(EaHash.Hash("_LABEL", EaHash.HashUpper(key)));
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning("NFSMW movie subtitles: " + e.Message);
                _subs = null;
            }
        }

        /// <summary>SubTitler::NotifyFirstFrame: the subtitle clock starts with the first picture.</summary>
        void SubtitleStart() => _subStart = Clock();

        static string Text(uint hash) => FrontEndData.Strings != null && FrontEndData.Strings.TryGet(hash, out var s) && !string.IsNullOrEmpty(s) ? s.Replace('^', '\n') : null;

        void DrawSubtitles()
        {
            if (_subs == null || _subStart < 0) return;
            // SubTitler::Update on each screen tick: one entry per tick once its start time has passed
            int now = (int)((_clock - _subStart) * 10);
            if (_next < _subs.Count && _subs[_next].Time <= now)
            {
                var e = _subs[_next++];
                if (_tutorial)
                {
                    if (e.Hash != NoText)
                    {
                        _textA = Text(EaHash.Hash("_A", e.Hash));
                        _textB = Text(EaHash.Hash("_B", e.Hash));
                    }
                    else _textA = _textB = null;
                }
                else _textA = e.Hash != NoText ? Text(e.Hash) : null;
            }
            float k = Screen.height / 480f;
            if (_tutorial)
            {
                // FEAnyTutorial.fng / InGameAnyTutorial.fng: title 5A0EE0D9 at (-250,-168) size 1.2 white over a black shadow
                // (F414BF3E, +2,+2); line A 599B8442 at (-230,23) and line B 2E8DA933 at (-228,73), left-justified white
                // FONT_MW_BODY next to 16x16 arrows (BA613B17 / BA613B18) at x -243. [?] wrap width and font size estimated.
                if (!string.IsNullOrEmpty(_title)) Label(_title, -250, -168, 18, TextAnchor.UpperLeft, Color.white, k, 300);
                if (!string.IsNullOrEmpty(_textA)) { Arrow(-243, 34, k); Label(_textA, -230, 23, 13, TextAnchor.UpperLeft, Color.white, k, 470); }
                if (!string.IsNullOrEmpty(_textB)) { Arrow(-243, 84, k); Label(_textB, -228, 73, 13, TextAnchor.UpperLeft, Color.white, k, 470); }
            }
            else if (!string.IsNullOrEmpty(_textA))
            {
                // FEAnyMovie.fng: string 599B8442 centred at (0,176) over the backing bar 8BD49BCC (1200 x 100, black at
                // alpha 80/255) moved to the string's top
                var old = GUI.color;
                GUI.color = new Color(0, 0, 0, 80 / 255f);
                GUI.DrawTexture(FeRect(0, 176 + 50 - 8, 1200, 100), _black);
                GUI.color = old;
                Label(_textA, 0, 176, 14, TextAnchor.UpperCenter, Color.white, k, 600);
            }
        }

        static void Label(string text, float x, float y, float size, TextAnchor anchor, Color color, float k, float width)
        {
            var style = new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(size * k), alignment = anchor, wordWrap = true, normal = { textColor = Color.black } };
            float left = anchor == TextAnchor.UpperCenter ? x - width * 0.5f : x;
            var r = new Rect(Screen.width * 0.5f + left * k, Screen.height * 0.5f + y * k, width * k, 120 * k);
            GUI.Label(new Rect(r.x + 2 * k, r.y + 2 * k, r.width, r.height), text, style);
            style.normal.textColor = color;
            GUI.Label(r, text, style);
        }

        static void Arrow(float x, float y, float k)
        {
            var old = GUI.color;
            GUI.color = new Color(1f, 0.82f, 0.1f);
            // [?] stands in for ARROW_MAIN.tga
            GUI.Label(FeRect(x, y - 6, 16, 16), ">", new GUIStyle(GUI.skin.label) { fontSize = Mathf.RoundToInt(13 * k), fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter });
            GUI.color = old;
        }
    }

    /// <summary>
    /// Feeds the movie soundtrack to the audio output (OnAudioFilterRead on the movie's AudioSource, a silent looping
    /// clip). Linear resampling when the output rate differs from the movie's. The samples written so far are the
    /// movie clock; an underrun plays silence and the same amount is dropped later, so sound stays on the clock.
    /// </summary>
    public sealed class MovieAudioFeed : MonoBehaviour
    {
        EaMovieStream _stream;
        double _step;
        float _volume;
        int _outRate;
        readonly float[] _buf = new float[8192];
        int _bufPos, _bufLen;
        float _curL, _curR, _nextL, _nextR;
        double _frac;
        long _deficit;
        long _outFrames;
        bool _primed, _stopped, _ended;
        double _latency;
        // wall time of the last callback, for smooth clock reads between callbacks
        readonly System.Diagnostics.Stopwatch _watch = System.Diagnostics.Stopwatch.StartNew();
        double _lastCallback = -1;

        public void Init(EaMovieStream stream, int srcRate, int outRate, float volume)
        {
            _stream = stream;
            _outRate = outRate;
            _step = srcRate / (double)outRate;
            _volume = volume;
            AudioSettings.GetDSPBufferSize(out int len, out int num);
            _latency = len * Math.Max(1, num) / (double)outRate;
        }

        public void Stop() => _stopped = true;

        /// <summary>True once the output callback runs.</summary>
        public bool Running => _lastCallback >= 0 && !_stopped && _watch.Elapsed.TotalSeconds - _lastCallback < 0.5;

        /// <summary>Movie seconds being heard now.</summary>
        public double Seconds
        {
            get
            {
                double since = Math.Min(_watch.Elapsed.TotalSeconds - _lastCallback, 0.1);
                return Math.Max(0, System.Threading.Interlocked.Read(ref _outFrames) / (double)_outRate + since - _latency);
            }
        }

        /// <summary>All of the soundtrack has been played.</summary>
        public bool Drained => _ended;

        bool Pull(out float l, out float r)
        {
            while (_bufPos >= _bufLen)
            {
                _bufPos = 0;
                _bufLen = _stream.ReadAudio(_buf, 0, _buf.Length);
                if (_bufLen == 0)
                {
                    l = r = 0;
                    if (_stream.AudioDrained) _ended = true;
                    else _deficit++;
                    return false;
                }
                if (_deficit > 0)
                {
                    int drop = (int)Math.Min(_deficit, _bufLen / 2);
                    _bufPos = drop * 2;
                    _deficit -= drop;
                }
            }
            l = _buf[_bufPos];
            r = _buf[_bufPos + 1];
            _bufPos += 2;
            return true;
        }

        void OnAudioFilterRead(float[] data, int channels)
        {
            if (_stream == null || _stopped) return;
            int frames = data.Length / channels;
            if (!_primed)
            {
                Pull(out _curL, out _curR);
                Pull(out _nextL, out _nextR);
                _primed = true;
            }
            for (int i = 0; i < frames; i++)
            {
                float l = _curL + (_nextL - _curL) * (float)_frac, r = _curR + (_nextR - _curR) * (float)_frac;
                _frac += _step;
                while (_frac >= 1)
                {
                    _frac -= 1;
                    _curL = _nextL;
                    _curR = _nextR;
                    Pull(out _nextL, out _nextR);
                }
                l *= _volume;
                r *= _volume;
                int o = i * channels;
                if (channels == 1) data[o] = (l + r) * 0.5f;
                else
                {
                    data[o] = l;
                    data[o + 1] = r;
                    for (int c = 2; c < channels; c++) data[o + c] = 0;
                }
            }
            System.Threading.Interlocked.Add(ref _outFrames, frames);
            _lastCallback = _watch.Elapsed.TotalSeconds;
        }
    }

    /// <summary>
    /// The licensed soundtrack: SOUND\PFDATA\MW_Music.mus (SCHl streams; MW_Music.mpf is the interactive-music map
    /// that sequences them). Plays the decodable streams in order, looping the list, while menus are open or you
    /// drive. In copies where MW_Music.mus is blanked (all zeros) it stays silent.
    /// </summary>
    public sealed class MusicPlayer : MonoBehaviour
    {
        static List<long> _streams;
        static string _path;
        AudioSource _src;
        int _next;

        public static int StreamCount => Index().Count;

        static List<long> Index()
        {
            if (_streams != null) return _streams;
            _streams = new List<long>();
            GameData.Mount();
            _path = Path.Combine(GameData.Root, "SOUND", "PFDATA", "MW_Music.mus");
            if (!File.Exists(_path)) return _streams;
            using var f = File.OpenRead(_path);
            // a blanked file: don't scan half a gigabyte of zeros
            var probe = new byte[4096];
            bool any = false;
            for (long at = 0; at < f.Length && !any; at += Math.Max(4096, f.Length / 32))
            {
                f.Position = at;
                int n = f.Read(probe, 0, probe.Length);
                for (int i = 0; i < n && !any; i++) any = probe[i] != 0;
            }
            if (!any) { Debug.Log("NFSMW music: MW_Music.mus holds no audio in this copy"); return _streams; }
            var buf = new byte[1 << 20];
            long pos = 0;
            f.Position = 0;
            int read;
            while ((read = f.Read(buf, 0, buf.Length)) > 4)
            {
                for (int i = 0; i + 4 <= read; i++)
                    if (buf[i] == 'S' && buf[i + 1] == 'C' && buf[i + 2] == 'H' && buf[i + 3] == 'l') _streams.Add(pos + i);
                pos += read - 3;
                f.Position = pos;
            }
            Debug.Log($"NFSMW music: {_streams.Count} streams in MW_Music.mus");
            return _streams;
        }

        public static MusicPlayer Ensure()
        {
            // [audio port] the interactive music system (Runtime/Audio/Music.cs, the PathFinder MPF/MUS player) replaces
            // this sequential player
            if (NfsMw.Audio.MusicSystem.Ensure()) return null;
            var existing = FindAnyObjectByType<MusicPlayer>();
            if (existing) return existing;
            if (Index().Count == 0) return null;
            var go = new GameObject("Music");
            DontDestroyOnLoad(go);
            return go.AddComponent<MusicPlayer>();
        }

        void Awake()
        {
            _src = gameObject.AddComponent<AudioSource>();
            _src.spatialBlend = 0;
            _src.volume = 0.45f;
            _next = UnityEngine.Random.Range(0, Math.Max(1, _streams.Count));
        }

        void Update()
        {
            _src.volume = 0.75f * Settings.Music;
            if (_src.isPlaying || _streams.Count == 0) return;
            var clip = NextClip();
            if (clip == null) return;
            _src.clip = clip;
            _src.Play();
        }

        /// <summary>
        /// The soundtrack is stored as short (~1.5 s) segments that MW_Music.mpf sequences; consecutive segments are
        /// joined into one clip of about a minute so playback has no gaps.
        /// </summary>
        AudioClip NextClip()
        {
            var chunks = new List<float[]>();
            int rate = 0, total = 0;
            try
            {
                using var f = File.OpenRead(_path);
                for (int k = 0; k < 120 && total < 60 * 2 * (rate == 0 ? 44100 : rate); k++)
                {
                    long at = _streams[_next];
                    _next = (_next + 1) % _streams.Count;
                    f.Position = at;
                    var buf = new byte[(int)Math.Min(4 << 20, f.Length - at)];
                    f.Read(buf, 0, buf.Length);
                    var s = EaStream.Read(buf, 0, out _);
                    if (s == null || s.Samples == 0) continue;
                    if (rate == 0) rate = s.SampleRate;
                    if (s.SampleRate != rate) break;
                    var st = s.Stereo();
                    chunks.Add(st);
                    total += st.Length;
                }
            }
            catch (Exception e) { Debug.LogWarning("NFSMW music: " + e.Message); }
            if (total == 0) return null;
            var all = new float[total];
            int o = 0;
            foreach (var c in chunks) { Buffer.BlockCopy(c, 0, all, o * 4, c.Length * 4); o += c.Length; }
            var clip = AudioClip.Create("music", total / 2, 2, rate, false);
            clip.SetData(all, 0);
            return clip;
        }
    }
}
