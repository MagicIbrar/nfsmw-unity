using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;
using UnityEngine.UI;

namespace NfsMw.UI
{
    /// <summary>
    /// Text drawn with a game font (FNTF glyph atlas), laid out by FEngFont::RenderString (PC 0x59AC60; decomp
    /// Frontend/FEngFont.cpp:220). Rules, from the PC code:
    /// * format (FEString +0x6C, the package's Sj): bit 0 centre / bit 1 right per line, bit 2 vertical centre / bit 3
    ///   bottom on the whole text height (GetTextHeight), bit 4 word wrap at MaxWidth (Sw).
    /// * flags = the object flags (+0x1C): 0x20 '\n' / '^' are not line breaks, 0x80 no-break space → space, 0x200 no
    ///   next-word measuring, 0x800 no '$' handling.
    /// * '\n' and '^' break lines; a line advances by font height + (int)(Leading · fLeadingScale).
    /// * word wrap: at a space whose next word would pass MaxWidth (measured from the line start) the line breaks and
    ///   that space is skipped (PC: goto next char); spaces at the start of a wrapped line are skipped.
    /// * '$$' prints '$' (and turns joy-event textures off for the string); a single '$' with joy textures allowed ends
    ///   the string there (PC returns before cached->Render(): nothing of it is drawn) [?: the decomp draws $NAME$ icons;
    ///   the PC path never does].
    /// * MaxWidth without word wrap: a first line wider than MaxWidth is squeezed to it (RenderString 0x5A4390).
    /// * glyph quad: x + kern + OffsetX, y + OffsetY + fBaselineOffset, width max(w, 4), height h; UVs
    ///   (u − 1, v − 1)..(u + w + 1, v + h + 1) over the texture size (PrintCharacter 0x585D60); kern only after the first
    ///   character; advance = GetCharacterWidth (advance + kern).
    /// </summary>
    [RequireComponent(typeof(CanvasRenderer))]
    public sealed class FEText : MaskableGraphic
    {
        public FEFont Font;
        public int Justify;      // FEString format: bit0 centre, bit1 right, bit2 middle, bit3 bottom, bit4 wrap
        public float MaxWidth, Leading;
        /// <summary>The string object's flags (FEObject +0x1C), see the class comment.</summary>
        public uint Flags;
        public Vector2 Scale = Vector2.one;
        string _text = "";
        Texture2D _atlas;

        public string Text
        {
            get => _text;
            set
            {
                if (_text == value) return;
                _text = value ?? "";
                SetVerticesDirty();
            }
        }

        public override Texture mainTexture
        {
            get
            {
                if (_atlas == null && Font != null) _atlas = FrontEndData.Texture(EaHash.HashUpper(Font.Texture));
                return _atlas != null ? _atlas : s_WhiteTexture;
            }
        }

        Vector2 _lastScale;

        void LateUpdate()
        {
            if (_lastScale != Scale)
            {
                _lastScale = Scale;
                SetVerticesDirty();
            }
        }

        bool Wrap => (Justify & 0x10) != 0;
        uint MaxW => MaxWidth > 0 ? (uint)MaxWidth : 0u;
        int LeadingPx => Font == null ? 0 : (int)(Leading * Font.LeadingScale);

        /// <summary>FEngFont::GetLineWidth of one line (no wrap), in font pixels.</summary>
        public float MeasureWidth(string line) => Font == null ? 0 : Font.LineWidth(line ?? "", 0, Flags);

        /// <summary>FEngFont::GetTextWidth (widest line) and GetTextHeight with this string's leading and wrap.</summary>
        public float TextWidth => Font == null ? 0 : Font.TextWidth(_text, Flags);
        public float TextHeight => Font == null ? 0 : Font.TextHeight(_text, LeadingPx, Flags, MaxW, Wrap);

        protected override void OnPopulateMesh(VertexHelper vh)
        {
            vh.Clear();
            if (Font == null || string.IsNullOrEmpty(_text)) return;
            var tex = mainTexture;
            float tw = tex.width, th = tex.height;
            string s = _text;
            uint flags = Flags, maxW = MaxW;
            bool wrap = Wrap;
            int leading = LeadingPx;
            float lineStep = Font.LineHeight + leading;
            float y = FEFont.YOffset(Justify, Font.TextHeight(s, leading, flags, maxW, wrap));
            float x = FEFont.XOffset(Justify, Font.LineWidth(s, 0, flags, maxW, wrap));
            float lineStart = x;
            var col = (Color32)color;
            bool allowJoy = true;
            var quads = new List<(float x0, float y0, float x1, float y1, float u0, float v0, float u1, float v1)>();
            int index = 0;
            for (int i = 0; i < s.Length && s[i] != 0; i++, index++)
            {
                int c = s[i];
                if (c == ' ' && x == lineStart && wrap) continue;
                if ((flags & 0x20) == 0 && FEFont.IsNewline(c))
                {
                    if (i + 1 >= s.Length) break;
                    x = FEFont.XOffset(Justify, Font.LineWidth(s, i + 1, flags, maxW, wrap));
                    y += lineStep;
                    lineStart = x;
                    continue;
                }
                if (maxW != 0 && c == ' ' && wrap && (x - lineStart) + Font.NextWordWidth(s, i, flags) > maxW)
                {
                    x = FEFont.XOffset(Justify, Font.LineWidth(s, i + 1, flags, maxW, wrap));
                    y += lineStep;
                    lineStart = x;
                    continue;
                }
                if ((flags & 0x820) == 0 && c == '$')
                {
                    if (i + 1 < s.Length && s[i + 1] == '$') { i++; index++; allowJoy = false; }
                    else if (allowJoy) { quads.Clear(); break; }
                }
                int u = FEFont.ConvertCharacter(c);
                var g = Font.Glyph(u);
                if (g == null) continue;
                int prev = index != 0 && i > 0 ? s[i - 1] : 0;
                float kern = prev != 0 ? Font.Kern(g, prev) : 0;
                float gw = Mathf.Max(g.Width, 4);
                float x0 = x + kern + g.OffsetX, y0 = y + g.OffsetY + Font.BaselineOffset;
                quads.Add((x0, y0, x0 + gw, y0 + g.Height, (g.X - 1) / tw, (g.Y - 1) / th, (g.X + g.Width + 1) / tw, (g.Y + g.Height + 1) / th));
                x += Font.CharWidth(u, prev, (uint)Justify);
            }
            // cFEngRender::RenderString (PC 0x5A4390, FEngRender.cpp:428): with a MaxWidth, no word wrap and a first line
            // (GetLineWidth, flags 0) wider than MaxWidth, the string is squeezed horizontally by MaxWidth / width — a scale
            // applied before the object's own matrix
            float squeeze = 1f;
            if (maxW != 0 && !wrap)
            {
                float lw = Font.LineWidth(s, 0, 0u);
                if (lw > maxW) squeeze = maxW / lw;
            }
            foreach (var q in quads)
            {
                var p0 = new Vector2(q.x0 * squeeze * Scale.x, -q.y0 * Scale.y);
                var p1 = new Vector2(q.x1 * squeeze * Scale.x, -q.y1 * Scale.y);
                int k = vh.currentVertCount;
                vh.AddVert(new Vector3(p0.x, p0.y), col, new Vector2(q.u0, q.v0));
                vh.AddVert(new Vector3(p1.x, p0.y), col, new Vector2(q.u1, q.v0));
                vh.AddVert(new Vector3(p1.x, p1.y), col, new Vector2(q.u1, q.v1));
                vh.AddVert(new Vector3(p0.x, p1.y), col, new Vector2(q.u0, q.v1));
                vh.AddTriangle(k, k + 1, k + 2);
                vh.AddTriangle(k, k + 2, k + 3);
            }
        }
    }
}
