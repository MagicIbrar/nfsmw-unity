using System.Collections.Generic;
using NfsMw.Core;

namespace NfsMw.UI
{
    /// <summary>
    /// CTextScroller (MenuScreens/Common/feWidget.cpp:648-925): a localised text word-wrapped into lines of ViewWidth − 16
    /// (FEngFont::GetCharacterWidth with kerning; newline chars '\n' / '^' end a line, an empty line counts) shown through
    /// the strings &lt;template&gt;1..ViewLines; PAD_UP / PAD_DOWN scroll one line (Scroll clamps to the last page) and the scroll
    /// bar follows (Update(view, lines, top + 1, top + 1)). WordWrapAddLines breaks at the last space inside the measured run
    /// (the space stays on the line). Joy-event textures ($…$) are measured as plain characters [?].
    /// </summary>
    public sealed class TextScroller
    {
        FEView _view;
        int _viewLines, _viewWidth, _topLine;
        string _template;
        FEFont _font;
        FEScrollBar _scrollBar;
        readonly List<string> _lines = new List<string>();
        public uint ScrollUpMsg = FEHash.PadUp, ScrollDownMsg = FEHash.PadDown;

        public int NumLines => _lines.Count;
        public int TopLine => _topLine;
        public int NumVisibleLines => _viewLines;

        /// <summary>Initialise (:658).</summary>
        public void Initialise(FEView view, int viewWidth, int viewLines, string template, FEFont font)
        {
            _view = view; _viewWidth = viewWidth; _viewLines = viewLines; _template = template; _font = font;
        }

        public void UseScrollBar(FEScrollBar bar) => _scrollBar = bar;

        /// <summary>SetTextHash (:666): GetTranslatedString (missing → the default-error string).</summary>
        public void SetTextHash(uint label) => SetText(FrontEndData.Localized(label));

        /// <summary>SetText (:678): split at newline chars, each piece word-wrapped; top line 0, scroll bar, display.</summary>
        public void SetText(string text)
        {
            _lines.Clear();
            if (_font != null && !string.IsNullOrEmpty(text))
            {
                int start = 0;
                while (start < text.Length)
                {
                    int end = start;
                    while (end < text.Length && !FEFont.IsNewline(text[end])) end++;
                    if (end == start) _lines.Add("");
                    else WordWrapAddLines(text, start, end);
                    start = end + 1;
                }
            }
            _topLine = 0;
            UpdateScrollBar();
            Display(_topLine);
        }

        void WordWrapAddLines(string s, int start, int end)
        {
            while (start < end)
            {
                int length = end - start, size = 0;
                float width = 0;
                int prev = 0;
                int p = start;
                while (width < _viewWidth - 16 && p < end)
                {
                    if (FEFont.IsNewline(s[p])) break;
                    width += _font.CharWidth(s[p], prev, 0);
                    size++;
                    p++;
                    prev = p < s.Length ? s[p] : 0;   // PrevChar = *++pChar (the decomp's quirk: the next char)
                }
                if (size < length)
                {
                    int brk = size - 1;
                    while (brk > 0 && s[start + brk] != ' ') brk--;
                    if (brk > 0) size = brk + 1;
                    _lines.Add(s.Substring(start, size));
                    start += size;
                }
                else
                {
                    _lines.Add(s.Substring(start, size));
                    start = end;
                }
                if (size == 0) break;   // [ours] guard: a zero view width
            }
        }

        /// <summary>Scroll (:759).</summary>
        public void Scroll(int amount)
        {
            if (_lines.Count <= _viewLines) return;
            int top = _topLine + amount;
            if (top < 0) top = 0;
            else if (top + _viewLines >= _lines.Count) top = _lines.Count - _viewLines;
            _topLine = top;
            Display(top);
        }

        /// <summary>HandleNotificationMessage (:772).</summary>
        public bool HandleNotificationMessage(uint msg)
        {
            if (msg == ScrollUpMsg) { Scroll(-1); UpdateScrollBar(); return true; }
            if (msg == ScrollDownMsg) { Scroll(1); UpdateScrollBar(); return true; }
            return false;
        }

        /// <summary>Display (:786): the lines from top into &lt;template&gt;1..n, blanks past the end.</summary>
        public void Display(int top)
        {
            if (_view == null) return;
            for (int i = 0; i < _viewLines; i++)
            {
                var n = _view.Find(_template.Replace("%d", (i + 1).ToString()));
                if (n == null) continue;
                int k = top + i;
                _view.SetText(n, k < _lines.Count ? _lines[k] : "");
            }
        }

        void UpdateScrollBar() => _scrollBar?.Update(_viewLines, _lines.Count, _topLine + 1, _topLine + 1);
    }
}
