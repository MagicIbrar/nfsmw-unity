using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// cSlider (MenuScreens/Common/Slider.cpp; PC 0x56C9F0-0x57F47A) on the FEng engine: the objects BASE_ / FILLBAR_ /
    /// HANDLE_ / LEFT_CAP_ / RIGHT_CAP_ / VALUE_&lt;name&gt; (InitObjects :41). Draw (:59): fill = range × (cur − min) / (max − min)
    /// (a zero span becomes 0..1), FEngSetSizeX(fill), the fill's top-left at the base's top-left + (2, −12), FEngSetBottomRightU(
    /// fill, fill) — the U span is fill / texture width (FEngSetBottomRightUV :666: LowerRight.x = u / TextureInfo.Width), so the
    /// LEDs are cut, not squeezed —, the caps at (base x − 1.5, base y − 12) and (base x + fill − 7, base y − 12), VALUE "%d".
    /// A negative range (the tuning screen's left half) grows the bar leftwards from the same top-left.
    /// </summary>
    public sealed class CSlider
    {
        FEView _v;
        public FEView.Node Base, FillBar, Handle, LeftCap, RightCap, Value;
        public float MaxValue, MinValue, PrevValue, CurValue, DesiredValue, Increment, Range;

        /// <summary>cSlider::InitObjects (:41).</summary>
        public void InitObjects(FEView view, string name)
        {
            _v = view;
            if (view == null || name == null) return;
            Base = view.Find("BASE_" + name);
            FillBar = view.Find("FILLBAR_" + name);
            Handle = view.Find("HANDLE_" + name);
            LeftCap = view.Find("LEFT_CAP_" + name);
            RightCap = view.Find("RIGHT_CAP_" + name);
            Value = view.Find("VALUE_" + name);
        }

        /// <summary>cSlider::InitValues (:52).</summary>
        public void InitValues(float min, float max, float inc, float cur, float range)
        {
            MaxValue = max;
            MinValue = min;
            DesiredValue = CurValue = Mathf.Min(Mathf.Max(cur, min), max);
            Increment = inc;
            Range = range;
        }

        public void Init(FEView view, string name, float min, float max, float inc, float cur, float range)
        {
            InitObjects(view, name);
            InitValues(min, max, inc, cur, range);
        }

        /// <summary>cSlider::Update (:14): left / right step by the increment within min..max; true when it moved.</summary>
        public bool Update(int dir)
        {
            bool moved = false;
            if (dir < 0)
            {
                PrevValue = CurValue;
                CurValue = Mathf.Max(MinValue, CurValue - Increment);
                moved = !(CurValue == MinValue && PrevValue == MinValue);
            }
            else if (dir > 0)
            {
                PrevValue = CurValue;
                CurValue = Mathf.Min(MaxValue, CurValue + Increment);
                moved = !(CurValue == MaxValue && PrevValue == MaxValue);
            }
            Draw();
            return moved;
        }

        /// <summary>cSlider::SetValue (:110).</summary>
        public void SetValue(float v)
        {
            PrevValue = CurValue;
            CurValue = Mathf.Min(Mathf.Max(v, MinValue), MaxValue);
        }

        /// <summary>cSlider::Draw (:59).</summary>
        public void Draw()
        {
            if (_v == null) return;
            float d = MaxValue - MinValue;
            if (d == 0f) { MaxValue = 1f; MinValue = 0f; d = 1f; }
            float pct = (CurValue - MinValue) / d;
            float fill = Range * pct;
            _v.SetSizeX(FillBar, fill);
            var b = _v.GetTopLeft(Base);
            _v.SetTopLeft(FillBar, b.x + 2f, b.y - 12f);
            SetBottomRightU(_v, FillBar, fill);
            _v.SetTopLeft(LeftCap, b.x - 1.5f, b.y - 12f);
            _v.SetTopLeft(RightCap, b.x + fill - 7f, b.y - 12f);
            if (Value != null) _v.Printf(Value, ((int)CurValue).ToString());
        }

        /// <summary>FEngSetBottomRightU (FEngInterfaceFEObjects.hpp:203): LowerRight.x = u / the texture's width, V kept.</summary>
        public static void SetBottomRightU(FEView v, FEView.Node n, float u)
        {
            if (v == null || n?.Image == null) return;
            var tex = n.Image.texture;
            if (tex == null || tex.width <= 0) return;
            var d = n.Data;
            v.SetUVs(n, d.U0, d.V0, u / tex.width, d.V1);
        }

        /// <summary>cSlider::ToggleVisible (:93).</summary>
        public void ToggleVisible(bool on)
        {
            if (_v == null) return;
            foreach (var n in new[] { Base, FillBar, Value, Handle })
                if (on) _v.SetVisibleTree(n); else _v.SetInvisible(n);
        }

        /// <summary>cSlider::Highlight / UnHighlight (:116): HIGHLIGHT 0x249DB7B7 / UNHIGHLIGHT 0x7AB5521A on base, fill, value, handle.</summary>
        public void Highlight() { foreach (var n in new[] { Base, FillBar, Value, Handle }) _v?.SetScript(n, FEHash.Highlight); }
        public void UnHighlight() { foreach (var n in new[] { Base, FillBar, Value, Handle }) _v?.SetScript(n, FEHash.UnHighlight); }

        /// <summary>cSlider::SetPos (:130).</summary>
        public void SetPos(float x, float y)
        {
            _v?.SetTopLeft(Base, x, y);
            _v?.SetTopLeft(FillBar, x, y);
        }
    }
}
