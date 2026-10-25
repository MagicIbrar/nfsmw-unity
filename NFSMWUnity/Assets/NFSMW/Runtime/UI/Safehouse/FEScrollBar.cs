using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// FEScrollBar (Common/feWidget.cpp:501; PC ctor 0x5750D0): the objects &lt;name&gt;_Backing / _Handle / _Arrow_1 / _Arrow_2 /
    /// _Backing_End_1 / _Backing_End_2. Update(view, list, head, selected): hidden unless the list is longer than the view;
    /// else the handle is resized to view/list of the backing and placed at (head − 1) · (backing − handle) / (list − view)
    /// (SetPosResized, vertical), and the arrow at the end the selection sits on plays DIM (first / last item).
    /// Show / hide run the objects' SHOW / HIDE scripts with the visibility (FEScrollBar::SetVisible / SetInvisible).
    /// </summary>
    public sealed class FEScrollBar
    {
        static readonly uint Dim = FrontEndData.Hash("DIM"), Normal = FrontEndData.Hash("NORMAL");
        readonly FEView _v;
        readonly FEView.Node _backing, _handle, _arrow1, _arrow2, _end1, _end2;
        readonly bool _vertical;
        readonly float _backingTop, _backingSize;
        public bool Visible { get; private set; }

        public FEScrollBar(FEView view, string name, bool vertical = true)
        {
            _v = view;
            _vertical = vertical;
            _backing = view.Find(name + "_Backing");
            _handle = view.Find(name + "_Handle");
            _arrow1 = view.Find(name + "_Arrow_1");
            _arrow2 = view.Find(name + "_Arrow_2");
            _end1 = view.Find(name + "_Backing_End_1");
            _end2 = view.Find(name + "_Backing_End_2");
            if (_backing != null)
            {
                // FEngGetTopLeft / FEngGetSize of the backing (images are centred on their position)
                _backingSize = Mathf.Abs(vertical ? _backing.Data.SizeY : _backing.Data.SizeX);
                _backingTop = (vertical ? _backing.Data.Y : _backing.Data.X) - _backingSize / 2;
            }
        }

        void Set(FEView.Node n, bool on)
        {
            if (n == null) return;
            if (on) { _v.SetVisibleTree(n); _v.SetScript(n, FEHash.Show); }
            else { _v.SetInvisible(n); _v.SetScript(n, FEHash.Hide); }
        }

        public void SetGroupVisible(bool on)
        {
            Visible = on;
            Set(_backing, on); Set(_handle, on); Set(_arrow1, on); Set(_arrow2, on);
            if (!on) { Set(_end1, false); Set(_end2, false); }
        }

        public void Update(int viewItems, int listItems, int viewHead, int selected = -1)
        {
            if (selected == -1) selected = viewHead;
            if (listItems <= viewItems || listItems == 0) { SetGroupVisible(false); return; }
            SetGroupVisible(true);
            if (_handle != null && _backing != null)
            {
                float bar = (float)viewItems / listItems * _backingSize;
                float range = (_backingSize - bar) / Mathf.Max(1f, listItems - viewItems);
                float top = _backingTop + (viewHead - 1) * range;
                if (_vertical) { _handle.Data.SizeY = bar; _handle.Data.Y = top + bar / 2; }
                else { _handle.Data.SizeX = bar; _handle.Data.X = top + bar / 2; }
            }
            if (selected == 1) _v.SetScript(_arrow1, Dim);
            else if (selected == listItems) _v.SetScript(_arrow2, Dim);
        }
    }
}
