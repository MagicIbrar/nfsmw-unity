using System;
using System.Collections.Generic;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// UIWidgetMenu (MenuScreens/Common/feUIWidgetMenu.cpp) with FEToggleWidget / FESliderWidget (feWidget.cpp:140-345)
    /// and cSlider (Slider.cpp): the list screens of the options and the quick race setup (Options.fng,
    /// OptionsPCDisplay.fng, Track_Options.fng). Row n uses OPTION_NAME_n, OPTION_DATA_n, OPTION_BACKING_n, LEFT_ARROW_n /
    /// RIGHT_ARROW_n (falling back to LEFT_ARROW_0 / RIGHT_ARROW_0) and the slider objects BASE_SLIDER_n / FILLBAR_SLIDER_n
    /// / HANDLE_SLIDER_n / VALUE_SLIDER_n. Positions come from the hidden title / data masters (0xA753C46C, 0xC128B184):
    /// widget k on screen sits at the masters' top-left + k × the title master's height (Reposition, :441), titles and
    /// values centred in their column (FEngSetCenter centres strings by their measured box). Up / down scroll with wrap
    /// (ScrollWrapped, :172) skipping disabled widgets; at most iMaxWidgetsOnScreen rows show; the cursor 0x06745352 plays
    /// POS1..POSn or HIDE (UpdateCursorPos, :476). Left / right / accept go to the current widget's Act.
    /// </summary>
    public abstract class WidgetMenuScreen : FEScreen
    {
        public abstract class Widget
        {
            public WidgetMenuScreen Menu;
            public bool Enabled = true;
            public int Index;                  // 1-based, as iIndexToAdd
            public FEView.Node Title, Data, Left, Right, Backing;
            public bool MovedLastUpdate;
            protected FEView View => Menu.View;

            /// <summary>Left = -1, right = +1, accept = 0 (__PAD_LEFT__ / __PAD_RIGHT__ / __BUTTON_PRESSED__).</summary>
            public abstract void Act(int dir);
            public abstract void Draw();
            public virtual void Show(bool on)
            {
                foreach (var n in new[] { Title, Data, Left, Right, Backing }) View.SetVisible(n, on);
            }
            public virtual void SetScript(uint script)
            {
                foreach (var n in new[] { Title, Data, Left, Right, Backing }) View.Play(n, script);
            }
            public virtual void SetFocus() => SetScript(FEView.Highlight);
            public virtual void UnsetFocus() => SetScript(Enabled ? FEView.UnHighlight : FEView.Grey);
            public virtual void Position(float y)
            {
                Menu.CenterAt(Title, Menu.TitleX, y);
                Menu.CenterAt(Data, Menu.DataCenterX, y);
                Menu.CenterAt(Left, Menu.DataX, y);
                Menu.CenterAt(Right, Menu.DataX + Menu.DataW, y);
            }
            protected void SetTitle(uint hash) => View.SetText(Title, FEDb.L(hash));
            protected void SetData(string text) => View.SetText(Data, text);
            protected void SetDataHash(uint hash) => View.SetText(Data, FEDb.L(hash));
        }

        /// <summary>FEToggleWidget whose Act / Draw are given as delegates (the game's one-class-per-option widgets).</summary>
        public sealed class Toggle : Widget
        {
            readonly Action<int> _act;
            readonly Action<Toggle> _draw;
            public Toggle(Action<int> act, Action<Toggle> draw) { _act = act; _draw = draw; }
            public override void Act(int dir) { _act?.Invoke(dir); Draw(); }
            public override void Draw() => _draw?.Invoke(this);
            public void Set(uint title, uint value) { SetTitle(title); SetDataHash(value); }
            public void Set(uint title, string value) { SetTitle(title); SetData(value); }
        }

        /// <summary>FESliderWidget + cSlider: min / max / increment / value, the fill bar range (160 on the PC pages).</summary>
        public sealed class Slider : Widget
        {
            readonly uint _title;
            readonly Func<float> _get;
            readonly Action<float> _set;
            float _min, _max, _inc, _cur, _range;
            public FEView.Node Base, Fill, Handle, Value;
            float _fillU0, _fillU1, _fillW;

            public Slider(uint title, float min, float max, float inc, float range, Func<float> get, Action<float> set)
            {
                _title = title; _min = min; _max = max; _inc = inc; _range = range; _get = get; _set = set;
            }

            public float Current => _cur;

            public void Bind(int index)
            {
                string s = "SLIDER_" + index;
                Base = View.Find("BASE_" + s); Fill = View.Find("FILLBAR_" + s); Handle = View.Find("HANDLE_" + s); Value = View.Find("VALUE_" + s);
                if (Fill != null) { _fillU0 = Fill.Data.U0; _fillU1 = Fill.Data.U1; _fillW = Fill.Data.SizeX; }
                _cur = Mathf.Clamp(_get(), _min, _max);   // SetInitialValues → InitValues
            }

            /// <summary>cSlider::Update (Slider.cpp:5): steps by the increment within min..max.</summary>
            public override void Act(int dir)
            {
                float prev = _cur;
                if (dir < 0) _cur = Mathf.Max(_min, _cur - _inc);
                else if (dir > 0) _cur = Mathf.Min(_max, _cur + _inc);
                MovedLastUpdate = _cur != prev;
                if (dir != 0) _set(_cur);
                Draw();
            }

            public override void Draw()
            {
                SetTitle(_title);
                _cur = Mathf.Clamp(_get(), _min, _max);   // Draw → SetValue(database value)
                DrawSlider();
            }

            /// <summary>cSlider::Draw (Slider.cpp:47): fill = range × (cur − min) / (max − min), at the base's top-left + (2, −12).</summary>
            void DrawSlider()
            {
                float d = _max - _min;
                if (d == 0) { _max = 1; _min = 0; d = 1; }
                float pct = (_cur - _min) / d;
                float fill = _range * pct;
                if (Base != null && Fill != null)
                {
                    float bx = Base.Data.X - Base.Data.SizeX * 0.5f, by = Base.Data.Y - Base.Data.SizeY * 0.5f;
                    Fill.Data.SizeX = fill;
                    Fill.Data.X = bx + 2f + fill * 0.5f;
                    Fill.Data.Y = by - 12f + Fill.Data.SizeY * 0.5f;
                    // FEngSetBottomRightU(fill, fill_size) (FEngInterfaceFEObjects.hpp:203 → FEngSetBottomRightUV :666): LowerRight.x =
                    // fill / the texture's width — the LEDs are cut, not squeezed
                    var tex = Fill.Image != null ? Fill.Image.texture : null;
                    if (tex != null && tex.width > 0) Fill.Data.U1 = fill / tex.width;
                    else if (_fillW > 0) Fill.Data.U1 = _fillU0 + (_fillU1 - _fillU0) * Mathf.Clamp01(fill / _fillW);
                }
                if (Value != null) View.SetText(Value, ((int)_cur).ToString());
            }

            public override void Show(bool on)
            {
                foreach (var n in new[] { Title, Left, Right, Backing, Base, Fill, Value, Handle }) View.SetVisible(n, on);
            }

            public override void SetScript(uint script)
            {
                foreach (var n in new[] { Title, Left, Right, Backing }) View.Play(n, script);
            }

            /// <summary>FESliderWidget::SetFocus: title + backing HIGHLIGHT, cSlider::Highlight on base / fill / value / handle.</summary>
            public override void SetFocus()
            {
                View.Play(Title, FEView.Highlight); View.Play(Backing, FEView.Highlight);
                foreach (var n in new[] { Base, Fill, Value, Handle }) View.Play(n, FEView.Highlight);
            }

            public override void UnsetFocus()
            {
                uint s = Enabled ? FEView.UnHighlight : FEView.Grey;
                View.Play(Title, s); View.Play(Backing, s);
                foreach (var n in new[] { Base, Fill, Value, Handle }) View.Play(n, FEView.UnHighlight);
            }

            /// <summary>FESliderWidget::Position (feWidget.cpp:264): base top-left = (data centre − base width / 2, data top + 9.5).</summary>
            public override void Position(float y)
            {
                Menu.CenterAt(Title, Menu.TitleX, y);
                if (Base != null)
                {
                    float dataTop = y - Menu.RowH * 0.5f;
                    float x = Menu.DataCenterX - Base.Data.SizeX * 0.5f;
                    Menu.MoveTo(Base, x + Base.Data.SizeX * 0.5f, dataTop + 9.5f + Base.Data.SizeY * 0.5f);
                }
                DrawSlider();
                Menu.CenterAt(Left, Menu.DataX, y);
                Menu.CenterAt(Right, Menu.DataX + Menu.DataW, y);
            }
        }

        protected readonly List<Widget> Options = new List<Widget>();
        protected int MaxWidgetsOnScreen = 7;
        protected bool ScrollWrapped = true;
        /// <summary>bHasScrollBar (true unless the screen turns it off, e.g. CustomTuningScreen).</summary>
        protected bool HasScrollBar = true;
        Widget _current, _viewTop;
        FEScrollBar _scrollBar;
        FEView.Node _titleMaster, _dataMaster, _cursor;
        // master geometry in the masters' parent space (vWidgetStartPos, vDataPos, vMaxTitleSize, vMaxDataSize)
        internal float TitleX, TitleW, DataX, DataW, StartY, RowH;
        internal float DataCenterX => DataX + DataW * 0.5f;
        Vector2 _masterOrigin;

        protected Widget Current => _current;

        protected void InitWidgetMenu()
        {
            _titleMaster = View.Find(0xA753C46C);
            _dataMaster = View.Find(0xC128B184);
            _cursor = View.Find(0x06745352);
            // ctor: ScrollBar(pkg, "scrollbar", vertical, ...) — SCROLLBAR_BACKING 0x688A1551 / _HANDLE 0x3FBB7A4E / _ARROW_1 / _ARROW_2
            _scrollBar = new FEScrollBar(View, "scrollbar");
            SetInitialPositions();
        }

        /// <summary>SetInitialPositions (:425): FEngGetTopLeft of the masters (centre − size / 2 for images).</summary>
        void SetInitialPositions()
        {
            if (_titleMaster == null || _dataMaster == null) { TitleX = -150; TitleW = 200; DataX = 22; DataW = 210; StartY = -90; RowH = 24; return; }
            var t = _titleMaster.Data; var d = _dataMaster.Data;
            TitleW = Mathf.Abs(t.SizeX); RowH = Mathf.Abs(t.SizeY);
            float tlx = t.X - t.SizeX * 0.5f, tly = t.Y - t.SizeY * 0.5f;
            TitleX = tlx + TitleW * 0.5f;
            DataW = Mathf.Abs(d.SizeX);
            DataX = d.X - d.SizeX * 0.5f;
            StartY = tly;
            _masterOrigin = Abs(_titleMaster.Parent);
        }

        static Vector2 Abs(FEView.Node n)
        {
            var p = Vector2.zero;
            for (; n != null; n = n.Parent) p += new Vector2(n.Data.X, n.Data.Y);
            return p;
        }

        /// <summary>Moves a node so that its position, given in the masters' parent space, is honoured in its own parent.</summary>
        internal void MoveTo(FEView.Node n, float x, float y)
        {
            if (n == null) return;
            var off = _masterOrigin - Abs(n.Parent);
            n.Data.X = x + off.x;
            n.Data.Y = y + off.y;
        }

        /// <summary>FEngSetCenter (FEngInterfaceFEObjects.cpp:438): images by position, strings by their measured box.</summary>
        internal void CenterAt(FEView.Node n, float x, float y)
        {
            if (n == null) return;
            if (n.Label == null) { MoveTo(n, x, y); return; }
            var l = n.Label;
            float w = l.MeasureWidth(FirstLine(l.Text)) * Mathf.Abs(n.Data.SizeX);
            int lines = 1 + CountLines(l.Text);
            float h = (l.Font != null ? (l.Font.LineHeight + l.Leading) * lines : 20f) * Mathf.Abs(n.Data.SizeY);
            float xoff = (l.Justify & 1) != 0 ? -w * 0.5f : (l.Justify & 2) != 0 ? -w : 0f;
            float yoff = (l.Justify & 4) != 0 ? -h * 0.5f : (l.Justify & 8) != 0 ? -h : 0f;
            MoveTo(n, x - (xoff + w * 0.5f), y - (yoff + h * 0.5f));
        }

        static string FirstLine(string s) { int i = s.IndexOf('\n'); return i < 0 ? s : s.Substring(0, i); }
        static int CountLines(string s) { int c = 0; foreach (char ch in s) if (ch == '\n') c++; return c; }

        FEView.Node Row(string prefix, int index) => View.Find(prefix + index);

        FEView.Node Image(string prefix, int index) => View.Find(prefix + index) ?? View.Find(prefix + "0");

        /// <summary>UIWidgetMenu's pTitleName / pDataName / pBackingName / arrow names (OPTION_* by default; the cart uses PART_NAME_ / PRICE_).</summary>
        protected virtual string TitleName => "OPTION_NAME_";
        protected virtual string DataName => "OPTION_DATA_";
        protected virtual string BackingName => "OPTION_BACKING_";
        protected virtual string LeftArrowName => "LEFT_ARROW_";
        protected virtual string RightArrowName => "RIGHT_ARROW_";

        /// <summary>GetCurrentFEString / GetCurrentFEObject(name) for the widget about to be added (iIndexToAdd).</summary>
        protected FEView.Node CurrentFEObject(string name) => View.Find(name + (Options.Count + 1));

        /// <summary>The title master's top-left x (vWidgetStartPos.x) and the data column's left (vDataPos.x), in the masters' parent space.</summary>
        internal float TitleLeft => TitleX - TitleW * 0.5f;

        /// <summary>FEngSetTopLeft on a string or image: the object's box top-left at (x, y) in the masters' parent space.</summary>
        internal void TopLeftAt(FEView.Node n, float x, float y)
        {
            if (n == null) return;
            if (n.Label == null) { MoveTo(n, x + Mathf.Abs(n.Data.SizeX) * 0.5f, y + Mathf.Abs(n.Data.SizeY) * 0.5f); return; }
            var l = n.Label;
            float w = l.MeasureWidth(FirstLine(l.Text)) * Mathf.Abs(n.Data.SizeX);
            int lines = 1 + CountLines(l.Text);
            float h = (l.Font != null ? (l.Font.LineHeight + l.Leading) * lines : 20f) * Mathf.Abs(n.Data.SizeY);
            float xoff = (l.Justify & 1) != 0 ? -w * 0.5f : (l.Justify & 2) != 0 ? -w : 0f;
            float yoff = (l.Justify & 4) != 0 ? -h * 0.5f : (l.Justify & 8) != 0 ? -h : 0f;
            MoveTo(n, x - xoff, y - yoff);
        }

        /// <summary>FEngSetTopLeftY + FEngSetBottomRightX: a string's box with its top at y and its right edge at right.</summary>
        internal void RightAt(FEView.Node n, float right, float y)
        {
            if (n?.Label == null) return;
            var l = n.Label;
            float w = l.MeasureWidth(FirstLine(l.Text)) * Mathf.Abs(n.Data.SizeX);
            int lines = 1 + CountLines(l.Text);
            float h = (l.Font != null ? (l.Font.LineHeight + l.Leading) * lines : 20f) * Mathf.Abs(n.Data.SizeY);
            float xoff = (l.Justify & 1) != 0 ? -w * 0.5f : (l.Justify & 2) != 0 ? -w : 0f;
            float yoff = (l.Justify & 4) != 0 ? -h * 0.5f : (l.Justify & 8) != 0 ? -h : 0f;
            MoveTo(n, right - w - xoff, y - yoff);
        }

        /// <summary>AddStatOption / AddButtonOption for a custom widget: binds the row objects by the menu's names and shows it.</summary>
        protected void AddWidget(Widget w, bool enabled = true) => Attach(w, enabled);

        /// <summary>AddToggleOption (:271).</summary>
        protected Toggle AddToggle(Action<int> act, Action<Toggle> draw, bool enabled = true)
        {
            var w = new Toggle(act, draw);
            Attach(w, enabled);
            return w;
        }

        /// <summary>AddSliderOption (:302).</summary>
        protected Slider AddSlider(uint title, float min, float max, float inc, Func<float> get, Action<float> set, bool enabled = true, float range = 160f)
        {
            var w = new Slider(title, min, max, inc, range, get, set);
            Attach(w, enabled);
            return w;
        }

        void Attach(Widget w, bool enabled)
        {
            int i = Options.Count + 1;
            w.Menu = this; w.Index = i; w.Enabled = enabled;
            w.Title = Row(TitleName, i);
            w.Data = Row(DataName, i);
            w.Backing = Row(BackingName, i);
            w.Left = Image(LeftArrowName, i);
            w.Right = Image(RightArrowName, i);
            if (w is Slider s) { s.Bind(i); View.SetVisible(w.Data, false); }
            Options.Add(w);
            w.Show(true);
            w.Draw();
            w.Position(StartY + RowH * 0.5f + (i - 1) * RowH);
            if (!enabled) w.SetScript(FEView.Grey);
        }

        /// <summary>ClearWidgets (:363).</summary>
        protected void ClearWidgets()
        {
            foreach (var w in Options) { w.Show(false); w.UnsetFocus(); }
            // rows the new list does not use keep their package state hidden
            for (int i = 1; i <= 16; i++)
                foreach (var n in new[] { Row("OPTION_NAME_", i), Row("OPTION_DATA_", i), Row("OPTION_BACKING_", i), Row("LEFT_ARROW_", i), Row("RIGHT_ARROW_", i), Row("BASE_SLIDER_", i), Row("FILLBAR_SLIDER_", i), Row("HANDLE_SLIDER_", i), Row("VALUE_SLIDER_", i) })
                    View.SetVisible(n, false);
            Options.Clear();
            _current = null; _viewTop = null;
        }

        protected void RefreshWidgets() { foreach (var w in Options) w.Draw(); Reposition(); }

        int IndexOf(Widget w) => w == null ? 0 : Options.IndexOf(w) + 1;

        /// <summary>SetInitialOption (:386): the given widget if enabled, else the first enabled one; then SyncViewToSelection.</summary>
        protected void SetInitialOption(int number)
        {
            if (Options.Count == 0) { UpdateCursorPos(); UpdateScrollBar(); return; }
            Widget w = number > 0 && number <= Options.Count && Options[number - 1].Enabled ? Options[number - 1] : null;
            if (w == null) w = Options.Find(o => o.Enabled) ?? Options[0];
            SetOption(w);
            SyncViewToSelection();
            UpdateScrollBar();
        }

        void SetOption(Widget w)
        {
            if (_current != null && _current != w) _current.UnsetFocus();
            _current = w;
            _current?.SetFocus();
            UpdateCursorPos();
        }

        /// <summary>SyncViewToSelection (:487).</summary>
        void SyncViewToSelection()
        {
            int total = Options.Count;
            if (total > MaxWidgetsOnScreen && IndexOf(_current) <= total - MaxWidgetsOnScreen + 1) _viewTop = _current;
            else _viewTop = Options[Mathf.Max(0, total - MaxWidgetsOnScreen)];
            Reposition();
        }

        /// <summary>Reposition (:441): the visible window from pViewTop, one row height apart; the rest hidden.</summary>
        protected void Reposition()
        {
            int view = IndexOf(_viewTop);
            float y = StartY + RowH * 0.5f;
            for (int i = 0; i < Options.Count; i++)
            {
                var w = Options[i];
                int index = i + 1;
                if (view > 0 && index >= view && index < view + MaxWidgetsOnScreen)
                {
                    w.Show(true);
                    w.Draw();
                    w.Position(y);
                    y += RowH;
                }
                else w.Show(false);
            }
            UpdateCursorPos();
        }

        void UpdateCursorPos()
        {
            if (_cursor == null) return;
            int pos = _current == null ? 0 : IndexOf(_current) - IndexOf(_viewTop) + 1;
            bool on = pos > 0 && pos <= MaxWidgetsOnScreen;
            View.Play(_cursor, on ? FrontEndData.Hash("POS" + pos) : FEView.Hide);
            // the focused title's 0xABC08912 response (HIGHLIGHT + 0x424B24C5 to the package) shows the cursor's corner group
            // (557FEADD: INIT → HIDE, SHOW on 0x424B24C5); a plain view runs no responses, so the SHOW is played here
            if (on) foreach (var c in _cursor.Children) View.Play(c, FEView.Show);
        }

        /// <summary>
        /// ScrollBar.Update(iMaxWidgetsOnScreen, count, view top, current) (FEScrollBar::Update 0x589870): hidden unless the list is
        /// longer than the window, else the handle sized / placed by SetPosResized and the end arrow DIM (0x9E99) on the first / last
        /// option. Called from SetInitialOption and when Scroll / ScrollWrapped change the option (feUIWidgetMenu.cpp:186, :262).
        /// </summary>
        void UpdateScrollBar()
        {
            if (!HasScrollBar || _scrollBar == null) return;
            _scrollBar.Update(MaxWidgetsOnScreen, Options.Count, IndexOf(_viewTop), IndexOf(_current));
        }

        /// <summary>ScrollWrapped (:172) / Scroll (:118) without a Done button.</summary>
        protected void Scroll(int dir)
        {
            if (Options.Count == 0) return;
            var opt = _current;
            var view = _viewTop;
            int n = Options.Count;
            int guard = 0;
            if (dir > 0)
            {
                do
                {
                    int i = IndexOf(opt);
                    if (opt == null || i == n)
                    {
                        if (!ScrollWrapped) break;
                        opt = Options[0]; view = opt;
                    }
                    else opt = Options[i];
                } while (!opt.Enabled && ++guard < n);
                if (IndexOf(opt) >= IndexOf(view) + MaxWidgetsOnScreen) view = Options[IndexOf(view)];
            }
            else
            {
                do
                {
                    int i = IndexOf(opt);
                    if (opt == null || i == 1)
                    {
                        if (!ScrollWrapped) break;
                        opt = Options[n - 1];
                        view = Options[Mathf.Max(0, n - MaxWidgetsOnScreen)];
                    }
                    else opt = Options[i - 2];
                } while (!opt.Enabled && ++guard < n);
                if (n > MaxWidgetsOnScreen && IndexOf(opt) == IndexOf(view) - 1) view = opt;
            }
            if (view != _viewTop) { _viewTop = view; Reposition(); }
            if (opt != _current) { SetOption(opt); UpdateScrollBar(); }
        }

        /// <summary>UIWidgetMenu::NotificationMessage (:24) for the keys every widget screen shares.</summary>
        protected void TickWidgets(MenuInput input)
        {
            if (input.Up) Scroll(-1);
            if (input.Down) Scroll(1);
            if (_current != null && _current.Enabled)
            {
                if (input.Left) _current.Act(-1);
                if (input.Right) _current.Act(1);
                // [?] the exe also passes __BUTTON_PRESSED__ / __PAD_BACK__ to Act (feUIWidgetMenu.cpp:51; for every toggle that is
                // Update + Draw only): not forwarded, the cart's custom widgets read accept themselves
            }
        }
    }
}
