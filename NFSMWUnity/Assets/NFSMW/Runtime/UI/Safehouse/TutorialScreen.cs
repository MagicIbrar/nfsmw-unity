using NfsMw.Game;

namespace NfsMw.UI
{
    /// <summary>
    /// FEAnyTutorial.fng — FEAnyTutorialScreen (Common/FEAnyTutorialScreen.cpp; PC 0x525D50-0x5599B7): LaunchMovie(name,
    /// caller) pushes the package, DismissChyron and EFadeScreenOff(15_IN) run, the Movie object plays MOVIES\&lt;name&gt;_
    /// &lt;LANGUAGE&gt;_ntsc.vp6 (FEngMovieStarter, FEObjectCallbacks.cpp: 0xC3960EB9 to the package when the file is missing or
    /// the movie ends); accept / back / start dismiss it and the caller gets 0xC3960EB9. Port: the movie plays through
    /// MoviePlayer.TryPlay over the package [ours: the FE Movie object is not a video surface]; the tutorial title objects
    /// keep the package's text ("tollbooth").
    /// </summary>
    public sealed class TutorialScreen : FEMenuScreen
    {
        public override string Package => "FEAnyTutorial";
        public override bool Overlay => true;
        readonly string _movie;
        bool _done;
        /// <summary>The caller's 0xC3960EB9 [ours: a callback].</summary>
        public System.Action Done;

        public TutorialScreen(string movie) { _movie = movie; }

        protected override void Setup()
        {
            ChyronScreen.Dismiss();
            if (!MoviePlayer.TryPlay(_movie, Finish)) Finish();
        }

        void Finish()
        {
            if (_done) return;
            _done = true;
            FE.Defer(() => { if (FE.Top == this) FE.Pop(); Done?.Invoke(); });
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            if (msg == FEHash.PadAccept || msg == FEHash.PadBack || msg == FEHash.PadStart) { MoviePlayer.SkipAll(); Finish(); }
        }
    }
}
