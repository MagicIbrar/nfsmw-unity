using NfsMw.Core;
using NfsMw.Game;

namespace NfsMw.Audio
{
    /// <summary>
    /// The sound side of NISActivity (Sim/Activities/NISActivity.cpp, speed.exe 1.3), called from NisPlayer's one-line hooks:
    /// <list type="bullet">
    /// <item><see cref="Loaded"/> = NISActivity::Load @0x6F51F0: an Arrest or Ending NIS (mNISType 1 / 2) with no skip-to name
    /// posts ESndGameState(eLOAD_BustedLoading 11, true) — the FE overlay's game-flow flag.</item>
    /// <item><see cref="ReadyToPlay"/> = NISActivity::UpdateLoading @0x6F7230 entering READY_TO_PLAY: SetSoundControlState(true, …)
    /// by kind — "NIS Load DDay" 7 (name IntroNisDD*), "NIS Play Arrest" 10, "NIS Play BLK" 9 (IntroNisBL*), "NIS Play Story" 6
    /// (IntroNis10* / EndingNIS*), "NIS Play Intro" 7; a Moment scene (type 4) and a non-story World scene set none. An arrest
    /// then calls Speech::Manager::ClearPlayback @0x70E570 and SoundAI's EAXDispatch::ArrestReply @0x717110 (the decomp's GC
    /// version has cop_speech->ReleaseResource there).</item>
    /// <item><see cref="Skipped"/> = NISActivity::SkipOverNIS @0x6EA960: ClearPlayback and the cop speech module's ReleaseResource.</item>
    /// <item><see cref="Finished"/> = ~NISActivity @0x6FCE10: EAXSound::NISFinished @0x4C1C60 (SFXObj_NISStream::NISActivityDone, then
    /// SetSoundControlState(false) for NIS_STORY, NIS_INTRO, NIS_BLK, NIS_ARREST — the PC build has no SoundPause(eSNDPAUSE_NISON)
    /// in PlayNIS @0x4B8D80 / NISFinished, the decomp's pause reason is GC only), "DDAY flying": SNDSTATE_NIS_321 off for
    /// IntroNisFlyInDD, and ESndGameState(eNIS_Busted 4, false).</item>
    /// </list>
    /// </summary>
    public static class NisSound
    {
        static NisPlayer _ready;

        enum Kind { Intro = 0, Arrest = 1, Ending = 2, World = 3, Moment = 4 }   // CAnimChooser::eType

        static bool Starts(string s, string prefix) => s != null && s.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase);

        /// <summary>mNISType: Busted → Arrest, EndingNIS* → Ending, a race-start candidate / GenericStart → Intro, a moment scene
        /// (CMomentAnim candidate) → Moment, else World [?: the port's NIS entry points stand in for CAnimChooser's type].</summary>
        static Kind KindOf(NisPlayer p)
        {
            string n = p != null && p.Scene != null ? p.Scene.Name : null;
            if (p != null && p.name == "NIS Busted" || Starts(n, "Arrest")) return Kind.Arrest;
            if (Starts(n, "EndingNIS")) return Kind.Ending;
            if (n == null || n == "GenericStart") return Kind.Intro;
            var cand = NisCandidateData.Load(GameData.Root);
            if (cand.MarkerType(n) >= 0) return Kind.Moment;
            uint h = EaHash.Hash(n);
            foreach (var rs in cand.RaceStarts) if (rs.Scene == h) return Kind.Intro;
            return Kind.World;
        }

        /// <summary>NISActivity::Load: ESndGameState(11, true) for an arrest / ending (mSkipToNIS empty: always on PC).</summary>
        public static void Loaded(NisPlayer p)
        {
            var k = KindOf(p);
            if (k == Kind.Arrest || k == Kind.Ending) SoundState.SetGameFlow(SoundState.FlowLoadBusted, true);
        }

        /// <summary>NISActivity::UpdateLoading → READY_TO_PLAY (mState = 3).</summary>
        public static void ReadyToPlay(NisPlayer p)
        {
            if (p == null || _ready == p) return;
            _ready = p;
            var k = KindOf(p);
            if (k == Kind.Moment) return;   // (iVar1 == 4) skips the sound state
            string n = p.Scene != null ? p.Scene.Name : null;
            bool dday = Starts(n, "IntroNisDD");                                           // +0x39e
            bool bl = Starts(n, "IntroNisBL");                                             // +0x39f
            bool story = bl || Starts(n, "IntroNis10") || Starts(n, "EndingNIS");         // +0x39d
            SndCtl? s = null;
            if (dday) s = SndCtl.NisIntro;                                                 // "NIS Load DDay" 7
            else if (!story && k != Kind.Intro) { if (k == Kind.Arrest) s = SndCtl.NisArrest; }   // "NIS Play Arrest" 10; other types none
            else if (!bl) s = story ? SndCtl.NisStory : SndCtl.NisIntro;                  // "NIS Play Story" 6 / "NIS Play Intro" 7
            else s = SndCtl.NisBlk;                                                        // "NIS Play BLK" 9
            if (s.HasValue) SoundState.Set(true, s.Value);
            if (k == Kind.Arrest)
            {
                CopRadio.Manager?.ClearPlayback();
                CopRadio.AI?.Dispatch.ArrestReply();
            }
        }

        static NisPlayer _playing;

        /// <summary>
        /// NISActivity::UpdatePlaying (@0x6EA750): while FadeScreen::IsFadeScreenOn (the load fade from NISActivity::Load), it posts
        /// EFadeScreenOff and ESndGameState(eLOAD_BustedLoading 11, false) (@0x6EA7DC) — the flag Load raised for an arrest / ending,
        /// which keeps EA Trax off. The port clears it on the first frame the scene clock runs (polled from the mixer; the
        /// NIS's own fade-off is NisPlayer's _fadeIn) [ours: hook-free, one frame late].
        /// </summary>
        internal static void Poll(NisPlayer p)
        {
            if (p == null) { _playing = null; return; }
            if (_playing == p || p.SceneTime <= 0f) return;
            _playing = p;
            SoundState.SetGameFlow(SoundState.FlowLoadBusted, false);
        }

        /// <summary>NISActivity::SkipOverNIS: Speech::Manager::ClearPlayback + cop speech ReleaseResource (GameSpeech::ClearCompletedRequests).</summary>
        public static void Skipped()
        {
            var m = CopRadio.Manager;
            if (m == null) return;
            m.ClearPlayback();
            m.ReleaseResource();
        }

        /// <summary>~NISActivity → EAXSound::NISFinished, the DDay fly-in's NIS_321 clear, ESndGameState(4, false).</summary>
        public static void Finished(NisPlayer p)
        {
            if (p != null && _ready == p) _ready = null;
            SoundState.Set(false, SndCtl.NisStory);
            SoundState.Set(false, SndCtl.NisIntro);
            SoundState.Set(false, SndCtl.NisBlk);
            SoundState.Set(false, SndCtl.NisArrest);
            if (p != null && p.Scene != null && string.Equals(p.Scene.Name, "IntroNisFlyInDD", System.StringComparison.OrdinalIgnoreCase))
                SoundState.Set(false, SndCtl.Nis321);   // "DDAY flying" (bStringHash("IntroNisFlyInDD") == mSceneHash)
            SoundState.SetGameFlow(SoundState.FlowNisBusted, false);
        }

        /// <summary>No NIS exists: a player torn down without Finish (scene change) must not leave a NIS state on [ours].</summary>
        internal static void NoActivity()
        {
            if (_ready == null) return;
            _ready = null;
            SoundState.Set(false, SndCtl.NisStory);
            SoundState.Set(false, SndCtl.NisIntro);
            SoundState.Set(false, SndCtl.NisBlk);
            SoundState.Set(false, SndCtl.NisArrest);
        }
    }
}
