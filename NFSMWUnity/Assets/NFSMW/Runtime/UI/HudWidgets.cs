using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// GenericMessage (Frontend/HUD/FeGenericMessage.cpp; PC ctor 0x59DEE0, Update 0x567F80, RequestGenericMessage 0x5655F0 [?:
    /// the iface slot], mask 0x01000000 but updated every frame): the HUD's one message line. A request below the current
    /// priority is refused; otherwise E0BA07EC gets INIT (unless set), the text goes into every string of 32A7A521 (FEPrintf
    /// on the group), the message script plays on 32A7A521 (single-frame requests only start it when not already set), and
    /// the icon 6DD754EC gets its script and texture, or HIDE. Update: once the message script stops running (or after the
    /// first frame of a single-frame request) 32A7A521 and the icon get HIDE and the priority drops to none.
    /// Priorities as IFengHud.h: Priority_1 = 5 (highest) … Priority_5 = 1.
    /// </summary>
    public sealed class HudGenericMessage
    {
        public const int PriorityNone = 0, Priority5 = 1, Priority4 = 2, Priority3 = 3, Priority2 = 4, Priority1 = 5;
        public const uint ScriptNormal = 0x8AB83EDB;

        readonly FEView _v;
        readonly FEView.Node _line, _icon, _outer;
        int _priority;
        uint _script;
        int _frames;
        bool _oneFrame;

        public HudGenericMessage(FEView view)
        {
            _v = view;
            _line = view.Find(0x32A7A521);
            _icon = view.Find(0x6DD754EC);
            _outer = view.Find(0xE0BA07EC);
        }

        /// <summary>IGenericMessage::IsGenericMessageShowing.</summary>
        public bool Showing => _priority > PriorityNone;
        public int Priority => _priority;

        /// <summary>GenericMessage::RequestGenericMessage(string, singleFrame, fengHash, iconTextureHash, iconFengHash, priority).</summary>
        public bool Request(string text, bool singleFrame, uint script, uint iconTexture, uint iconScript, int priority)
        {
            if (priority < _priority) return false;
            if (!_v.IsScriptSet(_outer, FEView.Init)) _v.SetScript(_outer, FEView.Init, true);
            _priority = priority;
            _script = script;
            _frames = 0;
            _oneFrame = singleFrame;
            if (text != null)
            {
                if (text.Length > 63) text = text.Substring(0, 63);   // mStringBuffer[64], bSafeStrCpy
                if (script != 0)
                {
                    if (_oneFrame) { if (!_v.IsScriptSet(_line, script)) _v.SetScript(_line, script, true); }
                    else _v.SetScript(_line, script, true);
                }
                _v.Printf(_line, text);
            }
            if (iconScript != 0 && iconTexture != 0)
            {
                if (_oneFrame) { if (!_v.IsScriptSet(_icon, iconScript)) _v.SetScript(_icon, iconScript, true); }
                else _v.SetScript(_icon, iconScript, true);
                _v.SetTextureHash(_icon, iconTexture);
            }
            else if (!_v.IsScriptSet(_icon, FEView.Hide)) _v.SetScript(_icon, FEView.Hide, true);
            return true;
        }

        /// <summary>GenericMessage::RequestGenericMessageZoomOut: E1C034FC on E0BA07EC (unless set).</summary>
        public void ZoomOut()
        {
            if (!_v.IsScriptSet(_outer, 0xE1C034FC)) _v.SetScript(_outer, 0xE1C034FC, true);
        }

        /// <summary>GenericMessage::Update (runs whatever the feature flags).</summary>
        public void Update()
        {
            if (_priority <= PriorityNone) return;
            if (!_v.IsScriptRunning(_line, _script) || (_oneFrame && _frames != 0))
            {
                if (!_v.IsScriptSet(_line, FEView.Hide)) _v.SetScript(_line, FEView.Hide, true);
                _priority = PriorityNone;
                if (!_v.IsScriptSet(_icon, FEView.Hide)) _v.SetScript(_icon, FEView.Hide, true);
            }
            _frames++;
        }
    }

    /// <summary>
    /// PursuitBoard (Frontend/HUD/FePursuitBoard.cpp; PC ctor 0x59FC80, Update 0x57AEE0, setters 0x568C80-0x568EA0 / 0x595A40 /
    /// 0x595AF0; mask 0x00100000) and its feeder, the IPursuitBoard block of LocalPlayer::UpdateHud (PC 0x6F17F2-0x6F19C8).
    /// </summary>
    public sealed class HudPursuitBoard
    {
        static readonly uint Appear = FEView.Appear, Leave = FrontEndData.Hash("LEAVE"), Hide = FEView.Hide, Init = FEView.Init, Show = FEView.Show;
        const uint Flash = 0x026DED57, Pulse = 0x03826A28, CooldownOn = 0x13F51124, Switch = 0x92975065, Active = 0x4F90CF9B;

        readonly FEView _v;
        readonly HudGenericMessage _msg;
        readonly FEView.Node _board, _meter, _icons, _summary, _coolMeter, _timer, _backupTimer, _copsNumbers, _copsTakenOut, _copsDamaged, _total;
        readonly FEView.Node _bar0, _bar1, _bar2, _bar3, _bar4, _coolBar, _backupBacking, _hidingBacking;
        readonly float _w0, _w1, _w3, _w4, _wCool;

        bool _inPursuit, _heli;
        float _timeUntilHidden, _timeUntilBusted, _timeUntilBackup, _duration, _coolRemaining, _coolRequired = 60f;
        int _engaged, _destroyed, _damaged, _rep;

        public HudPursuitBoard(FEView view, HudGenericMessage msg)
        {
            _v = view;
            _msg = msg;
            _board = view.Find(0xDE89E070);
            _meter = view.Find(0x7B422BA3);
            _icons = view.Find(0xE0B1430B);
            _summary = view.Find(0x8674E6D4);
            _coolMeter = view.Find(0x84A226EC);
            _timer = view.Find(0xFC39CB0A);
            _backupTimer = view.Find(0xBEE44775);
            _copsNumbers = view.Find(0x814918CA);
            _copsTakenOut = view.Find(0xA16F9F1E);
            _copsDamaged = view.Find(0x5FA70D4C);
            _total = view.Find(0x875E92EB);
            _bar0 = view.Find(0xE0E0169B);
            _bar1 = view.Find(0x8EEEBD33);
            _bar2 = view.Find(0x47A4E2A9);
            _bar3 = view.Find(0x8E3653A6);
            _bar4 = view.Find(0x30F39A6F);
            _coolBar = view.Find(0xCF817638);
            _backupBacking = view.Find(0x8C20A763);
            _hidingBacking = view.Find(0x5C2A4F20);
            // the original widths, read in the ctor (the package's SA sizes)
            _w0 = _bar0?.Data.SizeX ?? 0; _w1 = _bar1?.Data.SizeX ?? 0; _w3 = _bar3?.Data.SizeX ?? 0; _w4 = _bar4?.Data.SizeX ?? 0;
            _wCool = _coolBar?.Data.SizeX ?? 0;
        }

        // ---- the feeder (LocalPlayer::UpdateHud) -------------------------------------------------------------------

        /// <summary>
        /// LocalPlayer::UpdateHud's IPursuitBoard block, run when not racing or in a pursuit race: SetInPursuit(pursuit and not
        /// bailed); with a pursuit: IsInView (IsPerpInSight), PursuitDuration, CoolDownTimeRemaining / Required, NumCopsInPursuit
        /// = GetNumCopsRemainingInWave, NumCopsDestroyed (+ the most recent type, bonus multiplier, rep points),
        /// NumCopsDamaged, TotalNumCopsInvolved, HeliInvolved, TimeUntilBusted(TimeUntilBusted, IsPerpBusted),
        /// TimeUntilBackup = GetTimeToBackupSpawned; with the perpetrator: IsHiding / TimeUntilHidden = IsPartiallyHidden,
        /// PursuitRep = pending rep (normal + cop destruction).
        /// [?] PursuitSystem stands in for IPursuit / IPerpetrator: cooldown remaining / required from CooldownProgress (the
        /// port's evade level), the pending rep = Bounty, HowHidden = PerpHowHidden.
        /// </summary>
        public void Feed(PursuitSystem police)
        {
            bool pursuit = police != null && police.InPursuit;
            SetInPursuit(pursuit);
            if (!pursuit) return;
            SetPursuitDuration(police.PursuitTime);
            float prog = Mathf.Clamp01(police.CooldownProgress);
            SetCooldownTimeRequired(1f);
            SetCooldownTimeRemaining(1f - prog);
            SetNumCopsInPursuit(police.NumCopsRemainingInWave);
            var (type, mult, rep) = police.MostRecentCopDestroyed();
            SetNumCopsDestroyed(police.Stats.CopsDestroyed, type, mult, rep);
            SetNumCopsDamaged(police.Stats.CopsDamaged);
            _heli = police.IsHeliInPursuit;
            SetTimeUntilBusted(police.TimeUntilBusted, police.IsPerpBusted);
            _timeUntilBackup = police.BackupEta;
            float howHidden = police.PerpHowHidden;
            _timeUntilHidden = howHidden;
            _rep = Mathf.RoundToInt(police.Bounty);
        }

        // ---- setters (IPursuitBoard) -----------------------------------------------------------------------------

        public void SetInPursuit(bool on) => _inPursuit = on;
        public void SetPursuitDuration(float t) => _duration = t >= 0 ? t : 0;
        public void SetCooldownTimeRemaining(float t) => _coolRemaining = t;
        public void SetCooldownTimeRequired(float t) => _coolRequired = t;

        /// <summary>SetTimeUntilBusted (0x595A40): busted → 1, else at most 0.99; reaching 1 raises "BUSTED" (0x532B5186),
        /// script 0x9D73BC15, Priority_1.</summary>
        public void SetTimeUntilBusted(float t, bool busted)
        {
            t = busted ? 1f : Mathf.Min(t, 0.99f);
            if (_timeUntilBusted == t) return;
            _timeUntilBusted = t;
            if (t >= 1f) _msg?.Request(FrontEndData.Text(0x532B5186, "BUSTED"), false, 0x9D73BC15, 0, 0, HudGenericMessage.Priority1);
        }

        /// <summary>SetNumCopsInPursuit (0x568D60): up → "active" 0x4F90CF9B on Cops_Icon 3787231C (unless running); else
        /// FB12D252 on it (unless set), and down → 0x579DBC92 on 3B9919A8 (unless set).</summary>
        public void SetNumCopsInPursuit(int n)
        {
            if (_engaged == n) return;
            var icon = _v.Find(0x3787231C);
            if (n > _engaged) { if (!_v.IsScriptRunning(icon, Active)) _v.SetScript(icon, Active, true); }
            else
            {
                if (!_v.IsScriptSet(icon, 0xFB12D252)) _v.SetScript(icon, 0xFB12D252, true);
                var b = _v.Find(0x3B9919A8);
                if (n < _engaged && !_v.IsScriptSet(b, 0x579DBC92)) _v.SetScript(b, 0x579DBC92, true);
            }
            _engaged = n;
        }

        /// <summary>The cop-type names for the takedown message (SetNumCopsDestroyed, FePursuitBoard.cpp:326): localised
        /// format strings with the combo bounty as %$d.</summary>
        static uint CopLabel(string type)
        {
            switch (type)
            {
                case "copcross": case "copsport": return 0x8FE02B9F;
                case "copmidsize": return 0xF49A550A;
                case "copghost": return 0x902311DA;
                case "copgto": return 0x9BFD379F;
                case "copgtoghost": return 0x65E97524;
                case "copsporthench": return 0x4EE07213;
                case "copsportghost": return 0x4ED00512;
                case "copsuv": case "copsuvpatrol": return 0x9BFD6AD3;
                case "copsuvl": return 0x1BAAC57F;
                default: return 0;
            }
        }

        /// <summary>SetNumCopsDestroyed (0x595AF0): on an increase the localised type string formatted with rep × multiplier
        /// (bSNPrintf into 64 chars) as a message: script 0x8AB83EDB, icon COPS_TAKENOUT_ICON with COPS (0x13FF94), Priority_4.</summary>
        public void SetNumCopsDestroyed(int n, string type, int mult, int rep)
        {
            if (_destroyed == n) return;
            if (n > _destroyed)
            {
                uint label = CopLabel(type);
                string fmt = label != 0 ? FrontEndData.Text(label, null) : null;
                if (fmt != null)
                {
                    string text = CareerScreenKit.Format(fmt, rep * mult);
                    if (text.Length > 63) text = text.Substring(0, 63);
                    _msg?.Request(text, false, HudGenericMessage.ScriptNormal, EaHash.HashUpper("COPS_TAKENOUT_ICON"), 0x0013FF94, HudGenericMessage.Priority4);
                }
            }
            _destroyed = n;
        }

        /// <summary>SetNumCopsDamaged (0x568E30): up → "active" on the damaged counter (unless set).</summary>
        public void SetNumCopsDamaged(int n)
        {
            if (_damaged == n) return;
            if (n > _damaged && !_v.IsScriptSet(_copsDamaged, Active)) _v.SetScript(_copsDamaged, Active, true);
            _damaged = n;
        }

        // ---- PursuitBoard::Update ---------------------------------------------------------------------------------

        bool Set(FEView.Node n, uint s) => _v.IsScriptSet(n, s);
        void Play(FEView.Node n, uint s) => _v.SetScript(n, s, true);

        /// <summary>PursuitBoard::Update (FePursuitBoard.cpp:53, PC 0x57AEE0), when the widget's feature bit is on.</summary>
        public void Update()
        {
            if (!_inPursuit)
            {
                if (Set(_board, Appear)) Play(_board, Leave);
                if (!Set(_meter, Leave) && !Set(_meter, Hide)) Play(_meter, Leave);
                if (Set(_coolMeter, Appear) || Set(_coolMeter, CooldownOn)) Play(_coolMeter, Leave);
                if (Set(_icons, Appear)) Play(_icons, Leave);
                return;
            }
            if (!Set(_board, Appear)) Play(_board, Appear);
            _v.Printf(_timer, GameHud.PrintTime(_duration, 4));
            _v.Printf(_total, CareerScreenKit.Format("%$d", _rep));
            if (!Set(_summary, Appear)) Play(_summary, Appear);

            if (_timeUntilBusted <= -1f)
            {
                if (!Set(_coolMeter, CooldownOn))
                {
                    Play(_meter, Switch);
                    NfsMw.Audio.FeSound.Play(NfsMw.Audio.FeSoundId.CommonScrollStart);   // g_pEAXSound->PlayUISoundFX(UISND_COMMON_SCROLL_START)
                }
                float left = _v.GetTopLeft(_coolBar).x;
                _v.SetSizeX(_coolBar, _wCool * (1f - _coolRemaining / _coolRequired));
                _v.SetTopLeft(_coolBar, left, _v.GetTopLeft(_coolBar).y);
                if (_timeUntilHidden > 0f) { if (!Set(_hidingBacking, Appear)) Play(_hidingBacking, Appear); }
                else if (Set(_hidingBacking, Appear)) Play(_hidingBacking, Leave);
                return;
            }

            if (Set(_coolMeter, CooldownOn))
            {
                Play(_coolMeter, Switch);
                NfsMw.Audio.FeSound.Play(NfsMw.Audio.FeSoundId.CommonScrollStart);
            }
            else if (Set(_meter, Hide) || Set(_meter, Leave)) Play(_meter, Appear);
            if (Set(_hidingBacking, Appear)) Play(_hidingBacking, Leave);

            int report = _engaged - (_heli ? 1 : 0);
            _v.Printf(_copsNumbers, CareerScreenKit.Format("%$d", report));
            _v.Printf(_copsTakenOut, CareerScreenKit.Format("%$d", _destroyed));
            _v.Printf(_copsDamaged, CareerScreenKit.Format("%$d", _damaged));

            if (_timeUntilBackup > 0f)
            {
                if (_timeUntilBackup > 10f) { if (!Set(_backupBacking, Appear)) Play(_backupBacking, Appear); }
                else if (!Set(_backupBacking, Flash)) Play(_backupBacking, Flash);
                _v.Printf(_backupTimer, GameHud.PrintTime(_timeUntilBackup, 4));
            }
            else if (!Set(_backupBacking, Leave)) Play(_backupBacking, Leave);

            float t = _timeUntilBusted;
            // busted end, kept at its right edge
            float right = _v.GetBottomRight(_bar0).x;
            _v.SetSizeX(_bar0, (t > 0.5f ? (t - 0.5f) * 2f : 0f) * _w0);
            SetBottomRightX(_bar0, right);
            if (t > 0.5f) { if (!Set(_bar0, Flash)) Play(_bar0, Flash); }
            else if (!Set(_bar0, Init)) Play(_bar0, Init);

            right = _v.GetBottomRight(_bar1).x;
            _v.SetSizeX(_bar1, (t > 0.5f ? 0.9f : t > 0.1f ? t * 2f - 0.1f : 0f) * _w1);
            SetBottomRightX(_bar1, right);

            if (t < 0.1f && t > -0.1f) { if (!Set(_bar2, Pulse)) Play(_bar2, Pulse); }
            else if (!Set(_bar2, Show)) Play(_bar2, Show);

            float leftX = _v.GetTopLeft(_bar3).x;
            _v.SetSizeX(_bar3, (t < -0.5f ? 1f : t < -0.1f ? -t * 2f - 0.1f : 0f) * _w3);
            SetTopLeftX(_bar3, leftX);

            leftX = _v.GetTopLeft(_bar4).x;
            _v.SetSizeX(_bar4, (t < -0.5f ? -(t + 0.5f) * 2f : 0f) * _w4);
            SetTopLeftX(_bar4, leftX);
            if (t < -0.5f) { if (!Set(_bar4, Flash)) Play(_bar4, Flash); }
            else if (!Set(_bar4, Init)) Play(_bar4, Init);
        }

        /// <summary>FEngSetTopLeftX / FEngSetBottomRightX: keep the other coordinate.</summary>
        void SetTopLeftX(FEView.Node n, float x) { if (n != null) _v.SetTopLeft(n, x, _v.GetTopLeft(n).y); }
        void SetBottomRightX(FEView.Node n, float x) { if (n != null) _v.SetBottomRight(n, x, _v.GetBottomRight(n).y); }
    }
}
