using System;
using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// IconScrollerMenu (Common/feIconScrollerMenu.cpp; FEIconScrollerMenu.hpp) on a plain view: the package's option_N
    /// images are handed out in order — two book-end images (STRINGHASH_END_OF_SCROLLER 0x43B6310F) first and last, the
    /// options between them — each at XPos = the running width (icon spacing −5), and the list slides so that the current
    /// option sits on ICON_SCROLL_REGION's centre: AnimateCubic (tCubic1D, 0.2 s Hermite) seeks −current.XPos, every icon
    /// is scaled by IconScroller::Scale (0 outside ±width/2, ramps to 1 within 1.5 px of the centre), pulled towards the
    /// centre by OrigWidth·(1−scale)³, clipped at the region's edges, and tinted between IdleColor and FadeColor by its
    /// scale (grey options at alpha 150). The current option's name goes to 0x5E7B09C9 / its shadow 0x0DFB7A2E;
    /// ENDPADLEFT 0xD7118934 / ENDPADRIGHT 0xB9B17747 are sent at the list ends. Scrolling does not wrap (IconScroller::Scroll).
    /// [ours] BUTTON_PRESSED runs the option at once (the exe plays the leave scripts and acts on EXIT_COMPLETE).
    /// </summary>
    public abstract class CarouselScreen : FEScreen
    {
        protected sealed class Option
        {
            public string Label, Icon;
            /// <summary>IconOption(tex_hash, name_hash, desc_hash): the texture and label hashes (Icon / Label are the port's legacy forms).</summary>
            public uint IconHash, NameHash, DescHash;
            public Action Run;
            public Func<bool> Enabled = () => true;
            /// <summary>IconOption::IsGreyOut: skipped by the scroll and drawn at alpha 150.</summary>
            public bool GreyOut;
            public object Tag;
            internal FEView.Node Node;
            internal float XPos, OrigW, OrigH, OrigY;
            internal bool BookEnd;
        }

        protected readonly List<Option> Options = new List<Option>();
        protected int Selected;
        readonly List<Option> _all = new List<Option>();   // book ends + options, in option_N order
        FEView.Node _title, _titleShadow, _master, _region;
        float _xCenter, _yCenter, _width = 350f, _addPos;
        const float IconSpacing = -5f, MaxFadeTime = 9f;
        float _fadeTime;
        bool _fadingIn, _fadingOut, _built, _justScrolled = true, _allowFade;
        uint _idleColor = 0xFFFFFFFF, _fadeColor = 0x00FFFFFF;
        // tCubic1D AnimateCubic (Misc/Point.cpp): Val, dVal, ValDesired, duration 0.2
        float _val, _dVal, _valDesired, _time;
        readonly float[] _coeff = new float[4];
        int _cubicState;   // 0 arrived, 1 seeking, 2 restart
        const float CubicDuration = 0.2f;
        public const uint EndOfScroller = 0x43B6310F;

        protected abstract void Build();
        protected virtual string Title => null;
        /// <summary>IconScroller::SetIdleColor / SetFadeColor (the pause menu uses 0xFFFFAE40 / 0x00FFAE40).</summary>
        protected void SetColors(uint idle, uint fade) { _idleColor = idle; _fadeColor = fade; }

        public override void Open()
        {
            Build();
            _master = View.Find("OPTION_MASTER");
            _region = View.Find("ICON_SCROLL_REGION");
            _title = View.Find(0x5E7B09C9) ?? FindText("Option title");
            _titleShadow = View.Find(0x0DFB7A2E);
            if (_region != null) { _xCenter = _region.Data.X; _yCenter = _region.Data.Y; View.SetVisible(_region, false); }
            else if (_master != null) { _xCenter = _master.Data.X; _yCenter = _master.Data.Y; }
            if (_master != null) View.SetVisible(_master, false);
            if (Title != null) SetText("TITLE", Title);
            Selected = Mathf.Clamp(Selected, 0, Mathf.Max(0, Options.Count - 1));
            Layout(true);
            _allowFade = true;
            StartFadeIn();
            RefreshHeader();
            OnSelectionChanged();
        }

        /// <summary>IconScroller::AddOption for every book end and option: the next option_N image, its size and position.</summary>
        void AssignNodes()
        {
            _all.Clear();
            _addPos = 0;
            int next = 1;
            FEView.Node Take()
            {
                var n = View.Find("option_" + next);
                next++;
                return n;
            }
            void Add(Option o)
            {
                var n = Take();
                if (n == null) return;
                o.Node = n;
                o.OrigW = Mathf.Abs(n.Data.SizeX); o.OrigH = Mathf.Abs(n.Data.SizeY); o.OrigY = n.Data.Y;
                o.XPos = _addPos;
                _addPos += o.OrigW + IconSpacing;
                var tex = o.IconHash != 0 ? FrontEndData.Texture(o.IconHash) : null;
                if (tex == null && !string.IsNullOrEmpty(o.Icon)) tex = FrontEndData.TextureByName(o.Icon);
                if (tex == null && !o.BookEnd) tex = FrontEndData.TextureByName("VISUAL_PART_BODY");
                View.SetTexture(n, tex);
                n.Visible = tex != null;
                _all.Add(o);
            }
            for (int i = 0; i < 2; i++) Add(new Option { BookEnd = true, IconHash = EndOfScroller, GreyOut = true });
            foreach (var o in Options) Add(o);
            for (int i = 0; i < 2; i++) Add(new Option { BookEnd = true, IconHash = EndOfScroller, GreyOut = true });
            // option_N images the list does not use stay off screen (RemoveAll parks them at 696969)
            for (var n = Take(); n != null; n = Take()) View.SetVisible(n, false);
        }

        void Layout(bool snap)
        {
            if (!_built) { AssignNodes(); _built = true; }
            var cur = Current;
            if (cur != null)
            {
                _valDesired = -cur.XPos;
                if (snap) { _val = _valDesired; _dVal = 0; _cubicState = 0; }
                else if (_valDesired != _val) _cubicState = 2;
            }
            PositionAll();
        }

        Option Current => Selected >= 0 && Selected < Options.Count ? Options[Selected] : null;

        /// <summary>IconScroller::Update: the spline seek and PositionOption for every icon.</summary>
        void Step(float dt)
        {
            if (_fadingIn) { _fadeTime += 1f; if (_fadeTime >= MaxFadeTime) { _fadeTime = MaxFadeTime; _fadingIn = false; } }
            else if (_fadingOut) { _fadeTime -= 1f; if (_fadeTime <= 0) _fadeTime = 0; }
            // cPoint::SplineSeek(&AnimateCubic, RealTimeElapsed, 0, 0)
            if (_cubicState == 2)
            {
                _time = 0; _cubicState = 1;
                float dv = _valDesired - _val;
                _coeff[0] = (_dVal + 0f) - 2f * dv;
                _coeff[1] = dv * 3f - 0f - (_dVal + _dVal);
                _coeff[2] = _dVal;
                _coeff[3] = _val;
            }
            if (_cubicState == 1)
            {
                _time += CubicDuration > 1e-5f ? dt / CubicDuration : 1f;
                if (_time > 1f) { _time = 1f; _val = _valDesired; _dVal = 0; _cubicState = 0; }
                else
                {
                    float t = _time;
                    _val = ((_coeff[0] * t + _coeff[1]) * t + _coeff[2]) * t + _coeff[3];
                    _dVal = (2f * _coeff[1] + 3f * _coeff[0] * t) * t + _coeff[2];
                }
            }
            PositionAll();
        }

        void PositionAll()
        {
            foreach (var o in _all) PositionOption(o);
        }

        /// <summary>IconScroller::Scale (feIconScrollerMenu.cpp:530).</summary>
        static float Scale(float x, float center, float scrollSize)
        {
            float pos = center + scrollSize * 0.5f, neg = center - scrollSize * 0.5f;
            if (x < neg || x > pos) return 0f;
            if (x >= neg && x < center - 1.5f) return (x - neg) / (scrollSize * 0.5f);
            if (x <= pos && x > center + 1.5f) return (pos - x) / (scrollSize * 0.5f);
            return 1f;
        }

        /// <summary>IconScroller::PositionOption (:546) + UpdateFade (:589), alignment eSA_MIDDLE.</summary>
        void PositionOption(Option o)
        {
            var n = o.Node;
            if (n == null) return;
            float xpos = _xCenter + (_val + o.XPos);
            float scale = Scale(xpos, _xCenter, _width);
            float k = (1f - scale) * (1f - scale) * (1f - scale);
            xpos += xpos < _xCenter ? o.OrigW * k : -o.OrigW * k;
            bool visible = !(xpos < _xCenter - _width * 0.5f || xpos > _xCenter + _width * 0.5f) && n.Image != null && n.Image.texture != null;
            n.Visible = visible;
            if (_fadingIn || _fadingOut) scale *= _fadeTime / MaxFadeTime;
            n.Data.X = xpos;
            n.Data.Y = _yCenter;   // FEngSetCenter(obj, xpos, fYCenter); eSA_MIDDLE keeps the centre as the icon shrinks
            n.Data.SizeX = o.OrigW * scale;
            n.Data.SizeY = o.OrigH * scale;
            // UpdateFade: IdleColor ↔ FadeColor by scale; greyed options at alpha 150
            float a1 = _idleColor >> 24, r1 = (_idleColor >> 16) & 0xFF, g1 = (_idleColor >> 8) & 0xFF, b1 = _idleColor & 0xFF;
            float a2 = _fadeColor >> 24, r2 = (_fadeColor >> 16) & 0xFF, g2 = (_fadeColor >> 8) & 0xFF, b2 = _fadeColor & 0xFF;
            int a = (int)(a1 * scale + a2 * (1f - scale)) & 0xFF;
            bool grey = o.GreyOut || (!o.BookEnd && !o.Enabled());
            n.Data.A = grey ? 150 : Mathf.Clamp(a, 0, 255);
            n.Data.R = Mathf.Clamp((int)(r1 * scale + r2 * (1f - scale)) & 0xFF, 0, 255);
            n.Data.G = Mathf.Clamp((int)(g1 * scale + g2 * (1f - scale)) & 0xFF, 0, 255);
            n.Data.B = Mathf.Clamp((int)(b1 * scale + b2 * (1f - scale)) & 0xFF, 0, 255);
        }

        protected void StartFadeIn() { if (_allowFade) { _fadingIn = true; _fadingOut = false; _fadeTime = 0; } }
        protected void StartFadeOut() { _fadingOut = true; _fadingIn = false; }

        /// <summary>IconScrollerMenu::RefreshHeader: the option name (and shadow), the end-of-list messages.</summary>
        protected void RefreshHeader()
        {
            var cur = Current;
            string name = cur == null ? "" : cur.NameHash != 0 ? FEDb.L(cur.NameHash) : cur.Label ?? "";
            if (_title != null) View.SetText(_title, name);
            if (_titleShadow != null) View.SetText(_titleShadow, name);
            if (Options.Count > 0 && Selected == 0) CareerScreenKit.Message(View, 0xD7118934);
            if (Options.Count > 0 && Selected == Options.Count - 1) CareerScreenKit.Message(View, 0xB9B17747);
        }

        /// <summary>The highlighted option changed (live previews).</summary>
        protected virtual void OnSelectionChanged() { }

        public override void Resume() => Refresh();

        protected virtual void Refresh() { }

        /// <summary>Rebuilds the option images after Options changed (RemoveAll + AddInitialBookEnds + AddOption…).</summary>
        protected void Relayout()
        {
            foreach (var o in _all) if (o.Node != null) View.SetVisible(o.Node, false);
            _built = false;
            Selected = Mathf.Clamp(Selected, 0, Mathf.Max(0, Options.Count - 1));
            Layout(true);
            RefreshHeader();
        }

        /// <summary>IconScroller::Scroll: to the next option that is not greyed out, without wrapping.</summary>
        protected bool Scroll(int dir)
        {
            if (Options.Count == 0) return false;
            int i = Selected;
            if (dir < 0)
            {
                if (i == 0) return false;
                do { i--; } while (i > 0 && Options[i].GreyOut);
            }
            else
            {
                if (i == Options.Count - 1) return false;
                do { i++; } while (i < Options.Count - 1 && Options[i].GreyOut);
            }
            if (Options[i].GreyOut || i == Selected) return false;
            SetSelection(i);
            return true;
        }

        protected void SetSelection(int index)
        {
            if (index < 0 || index >= Options.Count) return;
            var old = Current;
            Selected = index;
            if (old?.Node != null) View.Play(old.Node, FEView.UnHighlight);
            if (Current?.Node != null) View.Play(Current.Node, FEView.Highlight);
            _justScrolled = true;
            Layout(false);
            RefreshHeader();
            OnSelectionChanged();
        }

        public override void Tick(MenuInput input)
        {
            Step(Time.unscaledDeltaTime);
            if (Options.Count == 0) { if (input.Back) OnBack(); return; }
            if (input.Left) Scroll(-1);
            if (input.Right) Scroll(1);
            if (input.Accept && Current != null && Current.Enabled() && !Current.GreyOut) Current.Run?.Invoke();
            else if (input.Back) OnBack();
        }

        protected virtual void OnBack() => FE.Pop();
    }
}
