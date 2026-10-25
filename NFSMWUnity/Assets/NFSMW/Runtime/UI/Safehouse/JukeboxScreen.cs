using System.Collections.Generic;
using System.Linq;
using NfsMw.Audio;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// EA_Trax_Jukebox.fng — UIEATraxScreen (Safehouse/options/uiEATraxJukebox.cpp; PC 0x52C520-0x55A4AE). A Scrollerina
    /// (Common/feScrollerina.cpp) of 4 slots over the profile's JukeboxEntry playlist: slot i prints number E454E9A5+i
    /// ("%.2d"), artist 66646FC4+i, title 2890C8AF+i, album 77BD189E+i and playability F3EBDC4E+i (0x9CCE9F86 Off /
    /// 0x5278C50B Front End / 0x5C1B351C In Game / 0x9CCE64C4 Both), backing MOUSE_REGION_&lt;i+1&gt;, scroll bar "ScrollBar"; the
    /// selected slot's strings and backing play HIGHLIGHT. Header 0xCA74A2FA = play state (0x4CA36B89 Sequential /
    /// 0xA9C9C8F7 Random). Keys: L / R triggers toggle the play state, left / right cycle the song's playability, up / down
    /// scroll (or move the song while grabbed), key 1 (PAD_BUTTON5) grabs / drops, key 2 (PAD_BUTTON4) previews the song
    /// (MControlPathfinder(song.PathEvent) "Pathfinder5"). Back: unchanged → leave (0x587C018B), else Yes / No 0xE9CB802F
    /// (yes 0x775DBA97 restores the originals and leaves). EXIT_COMPLETE: EATraxInit(-1) stops the preview, OptionsDirty,
    /// switch to MainMenu_Sub (the options menu). Every change posts "EATraxInit" to the music system [hook: the music
    /// system reads FEDb.Data.Playlist / PlayState].
    /// </summary>
    public sealed class JukeboxScreen : FEMenuScreen
    {
        public override string Package => "EA_Trax_Jukebox";
        const int NumSlots = 4;
        const uint MsgRestore = 0x775DBA97, LeaveScreen = 0x587C018B;

        sealed class Slot { public FEView.Node Backing; public readonly List<FEView.Node> Strings = new List<FEView.Node>(); }
        readonly List<Slot> _slots = new List<Slot>();
        List<JukeboxEntry> _list;
        List<JukeboxEntry> _original;
        int _originalPlayState;
        int _selected, _top;          // 0-based datum index and view head
        bool _grabbed;
        FEScrollBar _bar;

        static List<SongInfo> Songs => MusicSystem.Instance ? MusicSystem.Instance.Songs : new List<SongInfo>();

        /// <summary>The profile playlist; built from the songs' DefPlay (FE 1, IG 2, AL 3) when empty [FEDatabase default].</summary>
        public static List<JukeboxEntry> Playlist()
        {
            var list = FEDb.Data.Playlist;
            var songs = Songs;
            if (list.Count != songs.Count)
            {
                list.Clear();
                for (int i = 0; i < songs.Count; i++)
                {
                    var s = songs[i];
                    int p = s == null ? 0 : s.DefPlay == "FE" ? 1 : s.DefPlay == "IG" ? 2 : s.DefPlay == "AL" ? 3 : 0;
                    list.Add(new JukeboxEntry { SongIndex = i, Playability = p });
                }
            }
            return list;
        }

        protected override void Setup()
        {
            _list = Playlist();
            _original = _list.Select(e => new JukeboxEntry { SongIndex = e.SongIndex, Playability = e.Playability }).ToList();
            _originalPlayState = FEDb.Data.Audio.PlayState;
            for (int i = 0; i < NumSlots; i++)
            {
                var slot = new Slot { Backing = Obj("MOUSE_REGION_" + (i + 1)) };
                foreach (uint b in new uint[] { 0xE454E9A5, 0x66646FC4, 0x2890C8AF, 0x77BD189E, 0xF3EBDC4E }) slot.Strings.Add(Obj(b + (uint)i));
                _slots.Add(slot);
            }
            _bar = new FEScrollBar(View, "ScrollBar");
            Print();
            RefreshHeader();
        }

        static uint PlayabilityLabel(int p) => p switch { 0 => 0x9CCE9F86u, 1 => 0x5278C50Bu, 2 => 0x5C1B351Cu, 3 => 0x9CCE64C4u, _ => 0u };

        void RefreshHeader() => SetLanguageHash(0xCA74A2FA, FEDb.Data.Audio.PlayState == 0 ? 0x4CA36B89u : 0xA9C9C8F7u);

        /// <summary>Scrollerina::Print + DrawScrollBar.</summary>
        void Print()
        {
            var songs = Songs;
            for (int i = 0; i < NumSlots; i++)
            {
                var slot = _slots[i];
                int k = _top + i;
                bool has = k < _list.Count;
                foreach (var n in slot.Strings) if (n != null) { if (has) View.SetVisibleTree(n); else View.SetInvisible(n); }
                if (slot.Backing != null) { if (has) View.SetVisibleTree(slot.Backing); else View.SetInvisible(slot.Backing); }
                if (!has) continue;
                var e = _list[k];
                var s = e.SongIndex >= 0 && e.SongIndex < songs.Count ? songs[e.SongIndex] : null;
                Printf(slot.Strings[0], (k + 1).ToString("00"));
                Printf(slot.Strings[1], s?.Artist ?? "");
                Printf(slot.Strings[2], s?.Name ?? "");
                Printf(slot.Strings[3], s?.Album ?? "");
                Printf(slot.Strings[4], Lang(PlayabilityLabel(e.Playability)));
            }
            _bar.Update(NumSlots, _list.Count, _top + 1, _selected + 1);
        }

        void SlotScript(int slotIndex, uint script)
        {
            if (slotIndex < 0 || slotIndex >= NumSlots) return;
            var slot = _slots[slotIndex];
            foreach (var n in slot.Strings) SetScript(n, script);
            SetScript(slot.Backing, script);
        }

        void Select(int index)
        {
            int oldSlot = _selected - _top;
            _selected = Mathf.Clamp(index, 0, Mathf.Max(0, _list.Count - 1));
            if (_selected < _top) _top = _selected;
            if (_selected >= _top + NumSlots) _top = _selected - NumSlots + 1;
            int newSlot = _selected - _top;
            if (oldSlot != newSlot) { SlotScript(oldSlot, FEHash.UnHighlight); SlotScript(newSlot, FEHash.Highlight); }
            Print();
        }

        bool Changed()
        {
            if (FEDb.Data.Audio.PlayState != _originalPlayState) return true;
            if (_list.Count != _original.Count) return true;
            for (int i = 0; i < _list.Count; i++)
                if (_list[i].SongIndex != _original[i].SongIndex || _list[i].Playability != _original[i].Playability) return true;
            return false;
        }

        static void EATraxInit()
        {
            // MControlPathfinder(true, 0, 0, 0).Send("EATraxInit") [hook: the music system re-reads FEDb.Data.Playlist]
            if (MusicSystem.Instance) MusicSystem.Instance.PlayState = FEDb.Data.Audio.PlayState;
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.InitComplete:
                    SlotScript(_selected - _top, FEHash.Highlight);
                    break;
                case FEHash.PadLTrigger:
                case FEHash.PadRTrigger:
                    FEDb.Data.Audio.PlayState = FEDb.Data.Audio.PlayState == 0 ? 1 : 0;
                    RefreshHeader();
                    EATraxInit();
                    break;
                case FEHash.PadLeft:
                case FEHash.PadRight:
                    if (_list.Count == 0) break;
                    var e = _list[_selected];
                    e.Playability = msg == FEHash.PadLeft ? (e.Playability + 3) % 4 : (e.Playability + 1) % 4;
                    Print();
                    EATraxInit();
                    break;
                case FEHash.PadUp:
                case FEHash.PadDown:
                    if (_list.Count == 0) break;
                    int dir = msg == FEHash.PadUp ? -1 : 1;
                    if (_grabbed)
                    {
                        int to = _selected + dir;
                        // MoveTrack (0x52C5D0) → Scrollerina::MoveSelected (0x5A5320): UISND_COMMON_WRONG at the end, else
                        // UISND_COMMON_UP (both directions), then the move
                        FeSound.Play(to < 0 || to >= _list.Count ? FeSoundId.CommonWrong : FeSoundId.CommonUp);
                        if (to < 0 || to >= _list.Count) break;
                        (_list[_selected], _list[to]) = (_list[to], _list[_selected]);
                        Select(to);
                        EATraxInit();
                    }
                    else
                    {
                        if (_selected + dir < 0 || _selected + dir >= _list.Count) break;   // ScrollTracks: IsAtHead / IsAtTail → nothing
                        FeSound.Play(FeSoundId.CommonUp);   // Scrollerina::ScrollPrev / ScrollNext (0x5A8CB0 / 0x5A8C50): not wrapped, not at the end
                        Select(_selected + dir);
                    }
                    break;
                case FEHash.PadButton5:
                    _grabbed = !_grabbed;
                    break;
                case FEHash.PadButton4:
                    if (_list.Count == 0) break;
                    var song = Songs.ElementAtOrDefault(_list[_selected].SongIndex);
                    if (song != null && MusicSystem.Instance) MusicSystem.Instance.MessageStartPathfinder(song.PathEvent);
                    break;
                case FEHash.PadBack:
                    if (!Changed()) View.PackageMessage(LeaveScreen);
                    else DialogInterface.ShowTwoButtons(this, "", DialogTitle.Alert, 0x70E01038, 0x417B25E4, MsgRestore, FEView.MsgPadBack, FEView.MsgPadBack, true, 1, Lang(0xE9CB802F));
                    EATraxInit();
                    break;
                case MsgRestore:
                    for (int i = 0; i < _original.Count && i < _list.Count; i++) { _list[i].SongIndex = _original[i].SongIndex; _list[i].Playability = _original[i].Playability; }
                    FEDb.Data.Audio.PlayState = _originalPlayState;
                    View.PackageMessage(LeaveScreen);
                    break;
                case FEHash.ExitComplete:
                    if (MusicSystem.Instance) MusicSystem.Instance.MessageStartPathfinder(0xFFFFFFFF);   // EATraxInit(-1): stop the preview
                    FEDb.OptionsDirty = FEDb.OptionsDirty || Changed();
                    if (FEDb.OptionsDirty) FEDb.Save();
                    FE.Defer(() => { if (FE.Top == this) FE.Replace(new OptionsMainScreen(false)); });
                    break;
            }
        }
    }
}
