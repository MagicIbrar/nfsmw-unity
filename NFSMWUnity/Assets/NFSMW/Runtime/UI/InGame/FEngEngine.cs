using System;
using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// The FEng message engine for views opened with engine: true (specs/fe_ingame.md §1). Ported from the decomp's
    /// FEng/FEngine.cpp (ProcessMessageQueue, ProcessResponses, pad processing), FEng/FEPackage.cpp (UpdateObject,
    /// IssueScriptMessages, SetCurrentButton) and FEng/FEButtonMap.cpp (GetButtonFrom); the PC build matches them
    /// (FEngine::ProcessResponses 0x5B7xxx [?], FEPackage::UpdateObject in 0x5B05B0-0x5C1CFA).
    /// Messages go through one queue per view; targets: 0 = broadcast (every engine view's package responses and its
    /// message-target list), 0xFFFFFFFC = this package, 0xFFFFFFFD = this package's own responses only, 0xFFFFFFFF = the
    /// game (the screen's NotificationMessage), 0xFFFFFFFB = sound, anything else = an object.
    /// </summary>
    public sealed partial class FEView
    {
        public const uint FF_IsButton = 0x10000000, FF_IgnoreButton = 0x04000000, FF_DontNavigate = 0x00080000, FF_MouseObject = 0x00020000;
        public const uint ToBroadcast = 0, ToGame = 0xFFFFFFFF, ToAllPackages = 0xFFFFFFFE, ToPackageGlobal = 0xFFFFFFFD,
            ToPackage = 0xFFFFFFFC, ToSound = 0xFFFFFFFB, ToInput = 0xFFFFFFFA;

        // message ids used by the engine itself (FEHash_FeBonusCards.hpp)
        public const uint MsgHighlight = 0xABC08912, MsgUnHighlight = 0x55D1E635, MsgButtonPressed = 0x0C407210,
            MsgPadAccept = 0x406415E3, MsgPadBack = 0x911AB364, MsgPadStart = 0xB5AF2461, MsgPadUp = 0x72619778,
            MsgPadDown = 0x911C0A4B, MsgPadLeft = 0x9120409E, MsgPadRight = 0xB5971BF1, MsgPadUpRight = 0x6FD81B16,
            MsgPadDownRight = 0xAB1A49C9, MsgPadDownLeft = 0x79891376, MsgPadUpLeft = 0x6FFB6F23,
            MsgLTrigger = 0x5073EF13, MsgRTrigger = 0xD9FEEC59, MsgPadButton0 = 0xC519BFBF, MsgSetCurrentButton = 0x1B3909AA,
            MsgMouseEnter = 0x13F4BD45, MsgMouseOver = 0xB30D0683, MsgMouseExit = 0xB30793C1, MsgMouseDown = 0xF459B307,
            MsgMouseHeld = 0x1E646B2E, MsgMouseClick = 0x7EABCA56, MsgRightDown = 0xCE59C3DA, MsgRightHeld = 0x0DA2F4E1, MsgRightClick = 0x98ADF589;

        /// <summary>FEDirection_Message (FEButtonMap.cpp:26): up, up-right, right, down-right, down, down-left, left, up-left.</summary>
        public static readonly uint[] DirectionMessage = { MsgPadUp, MsgPadUpRight, MsgPadRight, MsgPadDownRight, MsgPadDown, MsgPadDownLeft, MsgPadLeft, MsgPadUpLeft };
        static readonly Vector2[] DirectionVectors =
        {
            new Vector2(0, -1), new Vector2(0.70711f, -0.70711f), new Vector2(1, 0), new Vector2(0.70711f, 0.70711f),
            new Vector2(0, 1), new Vector2(-0.70711f, 0.70711f), new Vector2(-1, 0), new Vector2(-0.70711f, -0.70711f),
        };

        static readonly List<FEView> Live = new List<FEView>();
        /// <summary>RecordLastPackageButton / RecallLastPackageButton (FEngine): per package name, the button GUID.</summary>
        static readonly Dictionary<string, uint> RecordedButtons = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);

        public bool Engine { get; private set; }
        /// <summary>The queue is only drained once the owning screen has hooked GameMessage (FEMenuScreen.Open).</summary>
        public bool MessagesEnabled;
        /// <summary>MR_SetInputProcessing / FEPackage::IsInputEnabled.</summary>
        public bool InputEnabled = true;
        public Node CurrentButton { get; private set; }

        /// <summary>Messages for the game (target 0xFFFFFFFF): the screen's NotificationMessage(msg, from).</summary>
        public event Action<uint, Node> GameMessage;
        /// <summary>Messages for the sound layer (target 0xFFFFFFFB): MenuScreen::NotifySoundMessage.</summary>
        public event Action<uint, Node> SoundMessage;
        /// <summary>Package commands from responses (MR_SwitchToPackage 512, MR_Push* 513/514/516, MR_PopPackage 515).</summary>
        public event Action<uint, string> PackageCommand;

        Dictionary<uint, Node> _byGuid;
        readonly Queue<(uint msg, Node from, uint target, Node to)> _queue = new Queue<(uint, Node, uint, Node)>();

        public Node FindGuid(uint guid) => _byGuid != null && _byGuid.TryGetValue(guid, out var n) ? n : null;

        // ---- queue ---------------------------------------------------------------------------------------------

        /// <summary>FEngine::QueueMessage to a special target (0, 0xFFFFFFFC, ...).</summary>
        public void QueueMessage(uint msg, Node from, uint target) => _queue.Enqueue((msg, from, target, null));

        /// <summary>FEngine::QueueMessage to an object (null = broadcast, as in the exe).</summary>
        public void QueueMessage(uint msg, Node from, Node to) => _queue.Enqueue((msg, from, to == null ? ToBroadcast : 1u, to));

        /// <summary>cFEng::QueuePackageMessage(msg, package, obj): to the object, or to the package when obj is null.</summary>
        public void PackageMessage(uint msg, Node obj = null)
        {
            if (obj != null) QueueMessage(msg, null, obj);
            else QueueMessage(msg, null, ToPackage);
        }

        public void PackageMessage(uint msg, uint objHash) => PackageMessage(msg, Find(objHash));

        /// <summary>FEngine::ProcessMessageQueue.</summary>
        public void ProcessMessages()
        {
            int guard = 0;
            while (MessagesEnabled && _queue.Count > 0 && guard++ < 2048)
            {
                var m = _queue.Dequeue();
                if (m.to != null) { ObjectMessage(m.to, m.msg); continue; }
                switch (m.target)
                {
                    case ToBroadcast:
                        foreach (var v in Live.ToArray())
                            if (v) { v.GlobalMessage(m.msg); v.TargetMessages(m.msg); }
                        break;
                    case ToPackage: GlobalMessage(m.msg); TargetMessages(m.msg); break;
                    case ToAllPackages: foreach (var v in Live.ToArray()) if (v) v.GlobalMessage(m.msg); break;
                    case ToPackageGlobal: GlobalMessage(m.msg); break;
                    case ToGame: GameMessage?.Invoke(m.msg, m.from); break;
                    case ToSound: SoundMessage?.Invoke(m.msg, m.from); break;
                    case ToInput: break;   // Msg_Global_Disable/EnableInputs [?] hashes unknown
                }
                if (!this) return;
            }
        }

        void GlobalMessage(uint msg)
        {
            var r = Package.FindResponse(msg);
            if (r != null) Responses(r, null);
        }

        /// <summary>
        /// FEPackage::GetMessageTargets (FEMsgTargetList.cpp:198, PC 0x5B0530): the objects the Targ chunk lists for the
        /// message, in order; no list = no object gets it. (The old "no Targ chunk → every object with a response"
        /// fallback was [ours] and never ran: all 199 shipped packages have a Targ chunk, harness feeng9 scripts.)
        /// </summary>
        void TargetMessages(uint msg)
        {
            if (Package.MessageTargets.TryGetValue(msg, out var list))
                foreach (var g in list) { var n = FindGuid(g); if (n != null) ObjectMessage(n, msg); }
        }

        void ObjectMessage(Node n, uint msg)
        {
            var r = n.Obj.FindResponse(msg);
            if (r != null) Responses(r, n);
        }

        /// <summary>FEngine::ProcessResponses (FEngine.cpp:1116).</summary>
        void Responses(FEMessageResponse list, Node obj)
        {
            for (int i = 0; i < list.List.Count; i++)
            {
                var r = list.List[i];
                switch (r.Id)
                {
                    case FEResponse.SetScript:
                        if (obj != null && obj.Obj.Script(r.Param) != null) Play(obj, r.Param);
                        break;
                    case FEResponse.PostMessageToFEng:
                        if (r.Target == ToPackage || r.Target == ToGame) QueueMessage(r.Param, obj, r.Target);
                        else QueueMessage(r.Param, obj, FindGuid(r.Target));
                        break;
                    case FEResponse.PostMessageToGame: QueueMessage(r.Param, obj, ToGame); break;
                    case FEResponse.PostMessageToSound: QueueMessage(r.Param, obj, ToSound); break;
                    case FEResponse.SetActiveButton:
                    {
                        var b = r.Param != 0 ? FindGuid(r.Param) : null;
                        if (b == null && r.Param != 0) break;
                        SetCurrentButton(b, b != null);
                        break;
                    }
                    case FEResponse.SetInputProcessing: InputEnabled = r.Param == 1; break;
                    case FEResponse.RecordCurrentButton: RecordedButtons[Package.Name ?? name] = CurrentButton?.Obj.Guid ?? 0; break;
                    case FEResponse.RecallRecordedButton:
                    {
                        Node b = null;
                        if (RecordedButtons.TryGetValue(Package.Name ?? name, out var g) && g != 0) b = FindGuid(g);
                        if (b == null)
                        {
                            if (r.Param != 0) b = FindGuid(r.Param);
                            if (b == null && r.Param != 0) break;
                        }
                        SetCurrentButton(b, b != null);
                        break;
                    }
                    case FEResponse.SwitchToPackage: case FEResponse.PushPackageGlobal: case FEResponse.PushPackageCurrent:
                    case FEResponse.PushPackageNone: case FEResponse.PopPackage:
                        PackageCommand?.Invoke(r.Id, r.Str);
                        break;
                    case FEResponse.IfScriptEquals:
                        if (obj == null || obj.Script == null || obj.Script.Id != r.Param) i = BranchTarget(list, i);
                        break;
                    case FEResponse.IfScriptNotEquals:
                        if (obj != null && obj.Script != null && obj.Script.Id == r.Param) i = BranchTarget(list, i);
                        break;
                    case FEResponse.Else: i = BranchTarget(list, i); break;
                }
            }
        }

        /// <summary>FEMessageResponse::FindConditionBranchTarget: from an If, the matching Else or EndIf; from an Else, its EndIf.</summary>
        static int BranchTarget(FEMessageResponse list, int i)
        {
            bool fromIf = list.List[i].Id != FEResponse.Else;
            int depth = 0;
            for (int j = i + 1; j < list.List.Count; j++)
            {
                uint id = list.List[j].Id;
                if (id == FEResponse.IfScriptEquals || id == FEResponse.IfScriptNotEquals) depth++;
                else if (id == FEResponse.EndIf) { if (depth == 0) return j; depth--; }
                else if (id == FEResponse.Else && depth == 0 && fromIf) return j;
            }
            return list.List.Count;
        }

        // ---- buttons ---------------------------------------------------------------------------------------------

        /// <summary>FEPackage::SetCurrentButton: UNHIGHLIGHT (0x55D1E635) to the old one, HIGHLIGHT (0xABC08912) to the new one.</summary>
        public void SetCurrentButton(Node b, bool sendMsgs = true)
        {
            if (sendMsgs)
            {
                if (CurrentButton != null) { QueueMessage(MsgUnHighlight, null, CurrentButton); QueueMessage(MsgUnHighlight, CurrentButton, ToSound); }
                if (b != null) { QueueMessage(MsgHighlight, null, b); QueueMessage(MsgHighlight, b, ToSound); }
            }
            CurrentButton = b;
        }

        public void SetCurrentButton(uint hash) => SetCurrentButton(Find(hash), true);

        /// <summary>FEngDisableButton: the button map skips it (FF_IgnoreButton).</summary>
        public void DisableButton(Node n, bool disabled = true) { if (n != null) n.ButtonDisabled = disabled; }

        /// <summary>FEButtonMap::GetButtonFrom (wrap mode 0 [?]: the packages' wrap setting is not read).</summary>
        public Node ButtonFrom(Node from, int dir)
        {
            float best = 1e30f;
            Node bestN = null;
            var o = from.AbsPos;
            foreach (var n in Nodes)
            {
                if (!n.IsButton || n.ButtonDisabled || n == from) continue;
                var d = n.AbsPos - o;
                float dist = d.magnitude;
                if (dist < 0.0001f) continue;
                d /= dist;
                // screen +Y is down in FE space, as in DirectionVectors
                float a = Vector2.Dot(d, DirectionVectors[dir]);
                if (a >= 0) a *= a;
                float score = a >= 0.25f ? (1 - a) * 200f + dist : 1500f;
                if (score < best) { best = score; bestN = n; }
            }
            return best < 1500f ? bestN : null;
        }

        // ---- pad input (FEngine::ProcessPadsForPackage) -----------------------------------------------------------

        /// <summary>A direction impulse: dir 0 up, 2 right, 4 down, 6 left (FEDirection_Message).</summary>
        public void PadDirection(int dir)
        {
            if (!InputEnabled) return;
            uint msg = DirectionMessage[dir];
            var cur = CurrentButton;
            if (cur != null)
            {
                Node next = null;
                var resp = cur.Obj.FindResponse(msg);
                bool nav = (cur.Obj.Flags & FF_DontNavigate) == 0;
                if (resp != null)
                {
                    QueueMessage(msg, null, cur);
                    if (nav && !resp.Has(FEResponse.DontNavigate)) next = ButtonFrom(cur, dir);
                }
                else
                {
                    if (nav) next = ButtonFrom(cur, dir);
                    QueueMessage(msg, null, ToPackageGlobal);
                }
                QueueMessage(msg, cur, ToSound);
                if (next != null) SetCurrentButton(next, true);
            }
            else if (Package.FindResponse(msg) != null)
            {
                QueueMessage(msg, null, ToPackageGlobal);
                QueueMessage(msg, null, ToSound);
            }
        }

        /// <summary>Accept: BUTTON_PRESSED to the current button when it has a response, else PAD_ACCEPT to the package.</summary>
        public void PadAccept()
        {
            if (!InputEnabled) return;
            var cur = CurrentButton;
            if (cur != null && cur.Obj.FindResponse(MsgButtonPressed) != null)
            {
                QueueMessage(MsgButtonPressed, null, cur);
                QueueMessage(MsgButtonPressed, cur, ToSound);
            }
            else if (Package.FindResponse(MsgPadAccept) != null)
            {
                QueueMessage(MsgPadAccept, null, ToPackageGlobal);
                QueueMessage(MsgPadAccept, null, ToSound);
            }
        }

        /// <summary>Back, start, triggers and PAD_BUTTON0..9: to the current button if it responds, else to the package.</summary>
        public void PadButton(uint msg)
        {
            if (!InputEnabled) return;
            var cur = CurrentButton;
            if (cur != null && cur.Obj.FindResponse(msg) != null)
            {
                QueueMessage(msg, null, cur);
                QueueMessage(msg, cur, ToSound);
            }
            else if (Package.FindResponse(msg) != null)
            {
                QueueMessage(msg, null, ToPackageGlobal);
                QueueMessage(msg, null, ToSound);
            }
        }

        // ---- mouse (FEngine::UpdateMouseState, PC) -----------------------------------------------------------------

        /// <summary>
        /// Mouse objects (FF_MouseObject): enter 0x13F4BD45 then 0xB30D0683 while over, 0xB30793C1 on leaving; left press over
        /// it 0xF459B307, held 0x1E646B2E, released over it 0x7EABCA56 (click). p = pointer in FE space, null = no mouse.
        /// [?] DoesPointTouchObject: the object's rect, a group's = any descendant image's rect.
        /// </summary>
        public void Mouse(Vector2? p, bool left, bool right)
        {
            if (!InputEnabled || p == null) return;
            foreach (var n in Nodes)
            {
                if ((n.Obj.Flags & FF_MouseObject) == 0) continue;
                if ((n.Obj.Flags & (FF_IgnoreButton | FF_IsButton)) == (FF_IgnoreButton | FF_IsButton)) continue;
                bool over = Touches(n, p.Value);
                bool wasOver = (n.MouseBits & 1) != 0, wasLeft = (n.MouseBits & 2) != 0, wasRight = (n.MouseBits & 4) != 0;
                if (over) PackageMessage(wasOver ? MsgMouseOver : MsgMouseEnter, n);
                else if (wasOver) PackageMessage(MsgMouseExit, n);
                if (left) { if (wasLeft) PackageMessage(MsgMouseHeld, n); else if (over) PackageMessage(MsgMouseDown, n); }
                else if (wasLeft && over) PackageMessage(MsgMouseClick, n);
                if (right) { if (wasRight) PackageMessage(MsgRightHeld, n); else if (over) PackageMessage(MsgRightDown, n); }
                else if (wasRight && over) PackageMessage(MsgRightClick, n);
                n.MouseBits = (byte)((over ? 1 : 0) | (left ? 2 : 0) | (right ? 4 : 0));
            }
        }

        bool Touches(Node n, Vector2 p)
        {
            if (n.AbsSize.x > 0 && n.AbsSize.y > 0)
                return Mathf.Abs(p.x - n.AbsPos.x) <= n.AbsSize.x * 0.5f && Mathf.Abs(p.y - n.AbsPos.y) <= n.AbsSize.y * 0.5f;
            foreach (var c in n.Children) if (Touches(c, p)) return true;
            return false;
        }

        // ---- script update (FEPackage::Update / UpdateObject / UpdateObjectTracks / IssueScriptMessages) -------------

        List<Node> _updateOrder;
        Node[] _updateSnap = new Node[0];

        /// <summary>
        /// FEPackage::Update (FEPackage.cpp:765, PC 0x5C1B30): UpdateObject on every root object in package order, a group
        /// recursing into its children (UpdateGroup 0x5B1670) — a pre-order walk of the object tree (the port walked the flat
        /// package list before round 9, which only changed the order of same-frame script messages). The frame's ms is the
        /// tick count (FEngine::Update hands every package the same tDeltaTicks). Game flag FF_DirtyCode is cleared after each
        /// object (uHoldDirtyFlags = 0 while the FE renders every frame, FEngine.cpp:309).
        /// </summary>
        float _msCarry;

        void EngineUpdate(float dt)
        {
            // whole milliseconds per update with the fraction carried over [ours: the exe's FEng gets integer ticks at its
            // ~60 Hz; rounding each frame alone loses or gains time at high render rates]
            float ms = dt * 1000f + _msCarry;
            int ticks = (int)ms;
            _msCarry = ms - ticks;
            if (_updateOrder == null)
            {
                _updateOrder = new List<Node>(Nodes.Count);
                foreach (var n in Nodes) if (n.Parent == null) PreOrder(n, _updateOrder);
                _updateSnap = _updateOrder.ToArray();
            }
            // a snapshot: game messages are delivered while scripts update (SendMessageToGame), and their handlers may
            // clone or remove objects
            var snap = _updateSnap;
            for (int i = 0; i < snap.Length; i++)
            {
                var n = snap[i];
                if (n.Removed) continue;
                if (n.Script != null) StepScript(n, ticks);
                n.DirtyCode = false;
            }
        }

        static void PreOrder(Node n, List<Node> into)
        {
            into.Add(n);
            foreach (var c in n.Children) PreOrder(c, into);
        }

        /// <summary>The object tree changed (Clone / Remove): rebuild the update order before the next update.</summary>
        void TreeChanged() => _updateOrder = null;

        /// <summary>
        /// FEPackage::UpdateObject (FEPackage.cpp:259, PC 0x5C17E0), with bExecuting (every port package executes): the
        /// current script's CurTime advances by the ticks (not below 0). At or past Length: a chained script takes over
        /// (the old script's tracks at its overflowing time, its events to the end, SetCurrentScript(chain) with
        /// SetupMoveToTracks, CurTime = the overflow, the chain's events from 0); else by Flags &amp; 3: 0 = events to the end
        /// and CurTime = Length + 1, 1 = loop (events to the end, wrap, events from 0, SetupMoveToTracks), 2 = ping-pong
        /// (CurTime modulo 2·Length, no events), 3 = nothing (CurTime keeps counting). Before Length the events in
        /// [prev, cur) by the same play action. The tracks are then applied — except when the script was already finished
        /// (prev == CurTime == Length + 1) and no code changed the object this frame (FF_DirtyCode, set by the
        /// FEObject::SetTrackValue family below). The packages only use Flags 0 and 1 (harness feeng9 scripts), so the
        /// ping-pong and 3 branches are dead data-wise but kept literal.
        /// </summary>
        void StepScript(Node n, int ticks)
        {
            var s = n.Script;
            int prev = (int)n.Time, len = s.Length;
            int cur = prev + ticks;
            if (cur < 0) cur = 0;
            if (cur >= len)
            {
                var chain = s.Chain != 0 ? n.Obj.Script(s.Chain) : null;
                if (chain != null)
                {
                    Tracks(n, s, cur);
                    int over = cur - len;
                    if (s.Events.Count != 0) Issue(n, s, prev, len);
                    n.Script = s = chain;
                    n.LastScriptId = chain.Id;
                    n.Time = cur = over;
                    SetupMoveTo(n);
                    if (s.Events.Count != 0) Issue(n, s, 0, cur);
                }
                else
                {
                    switch (s.Flags & 3)
                    {
                        case 0:
                            if (s.Events.Count != 0) Issue(n, s, prev, len);
                            cur = len + 1;
                            break;
                        case 1:
                            if (len > 0)
                            {
                                if (s.Events.Count != 0) Issue(n, s, prev, len);
                                cur -= cur / len * len;
                                n.Time = cur;
                                if (s.Events.Count != 0) Issue(n, s, 0, cur);
                                SetupMoveTo(n);
                            }
                            else cur = 0;
                            break;
                        case 2:
                            cur = len > 0 ? cur - cur / (len * 2) * (len * 2) : 0;
                            break;
                    }
                    n.Time = cur;
                }
            }
            else
            {
                if (s.Events.Count != 0)
                    switch (s.Flags & 3)
                    {
                        case 0: Issue(n, s, prev, cur); break;
                        case 1:
                            if (cur < prev) { Issue(n, s, prev, len); Issue(n, s, 0, cur); }
                            else Issue(n, s, prev, cur);
                            break;
                        case 2:
                            if (prev < len) Issue(n, s, prev, cur);
                            else { Issue(n, s, prev - len, 0); Issue(n, s, 0, cur); }
                            break;
                    }
                n.Time = cur;
            }
            if (prev == (int)n.Time && prev == s.Length + 1 && !n.DirtyCode) return;
            Tracks(n, s, (int)n.Time);
        }

        /// <summary>
        /// FEPackage::UpdateObjectTracks (FEPackage.cpp:397, PC 0x5BEC90) with FEKeyInterpFast: the colour track first (when
        /// the first track writes offset 0), then every track only while the object's alpha is not 0. A track without delta
        /// keys is applied once and then skipped (InterpAction 0x80) until the script is set again or code sets its field
        /// (SetTrackValue clears the bit), so values code sets afterwards stay.
        /// </summary>
        void Tracks(Node n, FEScript s, int t)
        {
            if (s.Tracks.Count == 0) return;
            if (s.Tracks[0].Offset == 0) Track(n, s.Tracks[0], t);
            if ((int)n.Data.A == 0) return;
            foreach (var tr in s.Tracks) Track(n, tr, t);
        }

        static void Track(Node n, FETrack tr, int t)
        {
            if (n.StaticTracks != null && n.StaticTracks.Contains(tr)) return;
            float[] first = null, bk = null;
            n.MoveTo?.TryGetValue(tr, out first);
            n.BaseKeys?.TryGetValue(tr, out bk);
            var v = tr.Evaluate(t, first, bk);
            if (v != null) n.Data.Set(tr.Offset, v);
            if (tr.Keys.Count <= 1) (n.StaticTracks ??= new HashSet<FETrack>()).Add(tr);
        }

        /// <summary>
        /// FEObject::SetupMoveToTracks (FEObject.cpp:169, PC 0x5B7C30), run by SetCurrentScript and on each loop: clears the
        /// 0x80 skip bits, and for MoveTo tracks (InterpType 3 / 4) makes the first delta key "where the object is now":
        /// value − base (quaternions: value · conj(base)), the base being the object's own base key.
        /// </summary>
        void SetupMoveTo(Node n)
        {
            n.StaticTracks?.Clear();
            n.MoveTo?.Clear();
            var s = n.Script;
            if (s == null) return;
            foreach (var tr in s.Tracks)
            {
                if (tr.Interp < 3 || tr.Interp >= 5 || tr.Keys.Count < 2) continue;
                float[] bas = null;
                if (n.BaseKeys == null || !n.BaseKeys.TryGetValue(tr, out bas)) bas = tr.Keys[0].value;
                var cur = n.Data.Get(tr.Offset, bas.Length);
                if (cur == null) continue;
                float[] d;
                if (tr.ValueType == 5 && bas.Length >= 4) d = FETrack.QuatMul(cur, new[] { -bas[0], -bas[1], -bas[2], bas[3] });
                else
                {
                    d = new float[bas.Length];
                    for (int i = 0; i < d.Length; i++) d[i] = cur[i] - bas[i];
                }
                (n.MoveTo ??= new Dictionary<FETrack, float[]>())[tr] = d;
            }
        }

        /// <summary>
        /// FEPackage::IssueScriptMessages (FEPackage.cpp:185, PC 0x5BC530): from the first event with time ≥ from (unsigned)
        /// while time &lt; to (to + 1 when to is the script length; events are sorted). Targets: 0 = broadcast (0x1B3909AA =
        /// set the current button to FindObjectByGUID(0)); 0xFFFFFFFF = FEngine::SendMessageToGame — delivered at once, not
        /// queued (0x5B0BB0 calls the game interface directly); 0xFFFFFFFC / FB / FA queued; else the object with that GUID
        /// (0x1B3909AA makes it the current button). Plain views (no message engine) issue nothing.
        /// [deviation] A game event before the screen hooked GameMessage (MessagesEnabled) is queued instead, so it reaches
        /// the screen once it opens, as the queued path always did.
        /// </summary>
        void Issue(Node n, FEScript s, int from, int to)
        {
            if (!Engine || s.Events.Count == 0 || to < from) return;
            if (to == s.Length) to++;
            var ev = s.Events;
            int i = 0;
            while (i < ev.Count && (uint)ev[i].time < (uint)from) i++;
            for (; i < ev.Count && (uint)ev[i].time < (uint)to; i++)
            {
                var e = ev[i];
                switch (e.target)
                {
                    case 0:
                        if (e.message == MsgSetCurrentButton) SetCurrentButton(FindGuid(0), true);
                        else QueueMessage(e.message, n, ToBroadcast);
                        break;
                    case ToGame:
                        if (MessagesEnabled) GameMessage?.Invoke(e.message, n);
                        else QueueMessage(e.message, n, ToGame);
                        if (!this) return;
                        break;
                    case ToPackage: case ToSound: case ToInput:
                        QueueMessage(e.message, n, e.target);
                        break;
                    default:
                        var target = FindGuid(e.target);
                        if (e.message == MsgSetCurrentButton) SetCurrentButton(target, true);
                        else QueueMessage(e.message, n, target);
                        break;
                }
            }
        }

        // ---- FEng helpers (FEngInterface*.cpp) ---------------------------------------------------------------------

        /// <summary>
        /// FEngSetScript(obj, script, start_at_beginning) (FEngInterfaceFEObjects.cpp:78): a script the object does not have
        /// is ignored; otherwise FEObject::SetScript(script, false) (PC 0x5B8400) — a script that is not the current one
        /// becomes current (SetCurrentScript: SetupMoveToTracks) at time 0, the current one is left alone — and then
        /// start_at_beginning puts the time back to 0 (without SetupMoveToTracks: code-set static values stay).
        /// Before round 9 a non-restart switch resumed the script at a remembered time [ours] and a restart on the current
        /// script re-ran SetupMoveToTracks.
        /// </summary>
        public void SetScript(Node n, uint script, bool restart = true)
        {
            if (n == null) return;
            var s = n.Obj.Script(script);
            if (s == null) return;
            if (n.Script != s)
            {
                n.Script = s;
                n.LastScriptId = s.Id;
                SetupMoveTo(n);
                n.Time = 0;
            }
            if (restart) n.Time = 0;
        }

        public void SetScript(uint hash, uint script, bool restart = true) => SetScript(Find(hash), script, restart);

        /// <summary>FEngIsScriptSet: the object's current script (running or finished) is this one.</summary>
        public bool IsScriptSet(Node n, uint script) => n != null && (n.Script != null ? n.Script.Id == script : n.LastScriptId == script);
        public bool IsScriptSet(uint hash, uint script) => IsScriptSet(Find(hash), script);

        /// <summary>FEngIsScriptRunning: current and CurTime ≤ Length.</summary>
        public bool IsScriptRunning(Node n, uint script) => IsScriptSet(n, script) && n.ScriptRunning;

        /// <summary>FEngIsScriptFinished (FEngInterfaceFEObjects.cpp:136): the object's script (current or not) has
        /// CurTime ≥ Length. The port keeps one time per object, so a script that is not current counts as finished
        /// [?: the exe keeps each FEScript's own CurTime; nothing in the port's screens asks about a non-current script].</summary>
        public bool IsScriptFinished(Node n, uint script)
        {
            if (n == null || n.Obj.Script(script) == null) return false;
            return n.Script == null || n.Script.Id != script || n.Time >= n.Script.Length;
        }

        /// <summary>FEngSetInvisible (PC 0x514C70) / FEngSetVisible (0x514CC0): recursive through groups; on PC SetVisible
        /// leaves objects with flag 0x40 (console-only, hidden by the load pass) alone — the GC decomp tests flag 8 instead,
        /// which the port followed before round 9.
        /// [deviation] The exe also sets FF_DirtyCode (re-applying a finished script's tracks once); the port does not,
        /// because screens still write node data directly, which that re-application would overwrite.</summary>
        public void SetInvisible(Node n) { if (n != null) SetInvisibleTree(n); }
        public void SetVisibleTree(Node n)
        {
            if (n == null || (n.Obj.Flags & 0x40) != 0) return;
            n.Visible = true;
            foreach (var c in n.Children) SetVisibleTree(c);
        }

        // ---- FEObject::SetTrackValue family (FEObject.cpp:243-360, PC 0x5B7DE0-0x5B83FF) ------------------------------

        /// <summary>
        /// FEObject::SetTrackValue: every script of the object that has a track for the field (FEScript::FindTrack) gets
        /// its base key set (or offset, relative) and its 0x80 skip bit cleared, then the object data is set. The port's
        /// package tracks are shared, so the base keys live per node (Node.BaseKeys, read by FETrack.Evaluate).
        /// </summary>
        void SetTrackValue(Node n, int offset, float[] value, bool relative)
        {
            foreach (var sc in n.Obj.Scripts)
            {
                var tr = sc.FindTrack(offset);
                if (tr == null || tr.Keys.Count == 0) continue;
                n.BaseKeys ??= new Dictionary<FETrack, float[]>();
                if (!n.BaseKeys.TryGetValue(tr, out var bk)) bk = (float[])tr.Keys[0].value.Clone();
                else bk = (float[])bk.Clone();
                for (int i = 0; i < bk.Length && i < value.Length; i++) bk[i] = relative ? bk[i] + value[i] : value[i];
                n.BaseKeys[tr] = bk;
                n.StaticTracks?.Remove(tr);
            }
            var cur = n.Data.Get(offset, value.Length);
            for (int i = 0; i < value.Length; i++) cur[i] = relative ? cur[i] + value[i] : value[i];
            n.Data.Set(offset, cur);
        }

        /// <summary>FEObject::SetPosition (PC 0x5B8190): FF_DirtyCode unless the change is within 1e-6, then SetTrackValue(pos).</summary>
        public void SetPosition(Node n, float x, float y, float z, bool relative = false)
        {
            if (n == null) return;
            const float e = 0.000001f;
            var d = n.Data;
            bool same = relative ? Mathf.Abs(x) <= e && Mathf.Abs(y) <= e && Mathf.Abs(z) <= e
                                 : Mathf.Abs(x - d.X) <= e && Mathf.Abs(y - d.Y) <= e && Mathf.Abs(z - d.Z) <= e;
            if (!same) n.DirtyCode = true;
            SetTrackValue(n, 7, new[] { x, y, z }, relative);
        }

        public void SetPosition(Node n, float x, float y) { if (n != null) SetPosition(n, x, y, n.Data.Z); }

        /// <summary>FEObject::SetColor (PC 0x5B8390): FF_DirtyCode unless every channel is within 1, then SetTrackValue(colour).
        /// Colour as the packages store it: (B, G, R, A) ints 0..255.</summary>
        public void SetColor(Node n, int r, int g, int b, int a, bool relative = false)
        {
            if (n == null) return;
            var d = n.Data;
            bool Close(float x, float y) => Mathf.Abs(x - y) <= 1f;
            bool same = relative ? Close(r, 0) && Close(g, 0) && Close(b, 0) && Close(a, 0)
                                 : Close(r, d.R) && Close(g, d.G) && Close(b, d.B) && Close(a, d.A);
            if (!same) n.DirtyCode = true;
            SetTrackValue(n, 0, new float[] { b, g, r, a }, relative);
        }

        /// <summary>FEngSetColor(obj, 0xAARRGGBB) (FEColor(uint32): a = bits 24-31, r 16-23, g 8-15, b 0-7).</summary>
        public void SetColor(Node n, uint argb) => SetColor(n, (int)((argb >> 16) & 0xFF), (int)((argb >> 8) & 0xFF), (int)(argb & 0xFF), (int)(argb >> 24));

        /// <summary>FEngGetColor → FEngGetObjectColor: the object's colour as 0xAARRGGBB (channels clamped to a byte).</summary>
        public uint GetColor(Node n)
        {
            if (n == null) return 0;
            uint C(float v) => (uint)Mathf.Clamp((int)v, 0, 255);
            return C(n.Data.A) << 24 | C(n.Data.R) << 16 | C(n.Data.G) << 8 | C(n.Data.B);
        }

        /// <summary>FEObject::SetRotation (PC 0x5B8200): always FF_DirtyCode; the rotation tracks' base keys and the data
        /// become q (relative: base · q).</summary>
        public void SetRotation(Node n, float qx, float qy, float qz, float qw, bool relative = false)
        {
            if (n == null) return;
            n.DirtyCode = true;
            var q = new[] { qx, qy, qz, qw };
            foreach (var sc in n.Obj.Scripts)
            {
                var tr = sc.FindTrack(10);
                if (tr == null || tr.Keys.Count == 0) continue;
                n.BaseKeys ??= new Dictionary<FETrack, float[]>();
                if (!n.BaseKeys.TryGetValue(tr, out var bk)) bk = tr.Keys[0].value;
                n.BaseKeys[tr] = relative ? FETrack.QuatMul(bk, q) : (float[])q.Clone();
                n.StaticTracks?.Remove(tr);
            }
            var cur = relative ? FETrack.QuatMul(new[] { n.Data.QX, n.Data.QY, n.Data.QZ, n.Data.QW }, q) : q;
            n.Data.QX = cur[0]; n.Data.QY = cur[1]; n.Data.QZ = cur[2]; n.Data.QW = cur[3];
        }

        /// <summary>
        /// FEngSetRotationZ (FEngInterfaceFEObjects.cpp:161, PC 0x514E20): the quaternion of the matrix
        /// [[cos a, sin a], [−sin a, cos a]] (bMatrixToQuaternion: (0, 0, sin(a/2), cos(a/2)) for |a| ≤ 180°) through SetRotation.
        /// </summary>
        public void SetRotationZ(Node n, float degrees)
        {
            float half = degrees * Mathf.Deg2Rad * 0.5f;
            float s = Mathf.Sin(half), c = Mathf.Cos(half);
            // bMatrixToQuaternion takes the w ≥ 0 branch (trace > 0) for angles inside ±180°, else picks the largest
            // diagonal: the same rotation with the opposite sign, which renders the same
            if (c < 0) { s = -s; c = -c; }
            SetRotation(n, 0, 0, s, c);
        }

        /// <summary>FEngSetSize (FEngInterfaceFEObjects.cpp:629): writes Size directly (no base key) and FF_DirtyCode — a
        /// size track of the current script overwrites it when it next runs, as in the exe.</summary>
        public void SetSize(Node n, float w, float h)
        {
            if (n == null) return;
            n.Data.SizeX = w;
            n.Data.SizeY = h;
            n.DirtyCode = true;
        }

        /// <summary>FEngGetSize: images / movies / multi / coloured images Size; strings Size · the text extent; else 0.</summary>
        public Vector2 GetSize(Node n)
        {
            if (n == null) return Vector2.zero;
            var d = n.Data;
            switch (n.Obj.Type)
            {
                case FEObjectType.Image: case FEObjectType.Movie: case FEObjectType.ColoredImage: case FEObjectType.MultiImage:
                    return new Vector2(d.SizeX, d.SizeY);
                case FEObjectType.String:
                    var f = n.Label != null ? n.Label.Font : null;
                    if (f == null) return Vector2.zero;
                    string s = n.Label.Text;
                    return new Vector2(d.SizeX * f.TextWidth(s, 0), d.SizeY * f.TextHeight(s, (int)n.Obj.Leading, 0, 0, false));
                default:
                    return Vector2.zero;
            }
        }

        public void SetSizeX(Node n, float w) { if (n != null) SetSize(n, w, GetSize(n).y); }

        bool IsBox(Node n) => n.Obj.Type == FEObjectType.Image || n.Obj.Type == FEObjectType.Movie || n.Obj.Type == FEObjectType.ColoredImage || n.Obj.Type == FEObjectType.MultiImage;

        /// <summary>FEngGetTopLeft (FEngInterfaceFEObjects.cpp:213): boxes pos − size/2; strings pos + the justify offsets of
        /// Size · the text extent (the label's text unless flag 2); groups untouched (0); else pos.</summary>
        public Vector2 GetTopLeft(Node n)
        {
            if (n == null) return Vector2.zero;
            var d = n.Data;
            if (n.Obj.Type == FEObjectType.Group) return Vector2.zero;
            if (IsBox(n)) return new Vector2(d.X - d.SizeX * 0.5f, d.Y - d.SizeY * 0.5f);
            if (n.Obj.Type == FEObjectType.String)
            {
                var f = n.Label != null ? n.Label.Font : null;
                if (f == null) return Vector2.zero;
                string s = n.Label.Text;
                float w = d.SizeX * f.TextWidth(s, n.Obj.Flags), h = d.SizeY * f.TextHeight(s, (int)n.Obj.Leading, n.Obj.Flags, (uint)Mathf.Max(0, n.Obj.MaxWidth), (n.Obj.Justify & 0x10) != 0);
                return new Vector2(d.X + FEFont.XOffset(n.Obj.Justify, w), d.Y + FEFont.YOffset(n.Obj.Justify, h));
            }
            return new Vector2(d.X, d.Y);
        }

        /// <summary>FEngSetTopLeft (:262): boxes pos = tl + size/2; strings tl − the justify offsets; else pos = tl; through SetPosition.</summary>
        public void SetTopLeft(Node n, float x, float y)
        {
            if (n == null) return;
            var d = n.Data;
            float px = x, py = y;
            if (IsBox(n)) { px = x + d.SizeX * 0.5f; py = y + d.SizeY * 0.5f; }
            else if (n.Obj.Type == FEObjectType.String)
            {
                var f = n.Label != null ? n.Label.Font : null;
                if (f == null) { px = d.X; py = d.Y; }
                else
                {
                    string s = n.Label.Text;
                    float w = d.SizeX * f.TextWidth(s, 0), h = d.SizeY * f.TextHeight(s, (int)n.Obj.Leading, 0, 0, false);
                    px = x - FEFont.XOffset(n.Obj.Justify, w);
                    py = y - FEFont.YOffset(n.Obj.Justify, h);
                }
            }
            SetPosition(n, px, py, d.Z);
        }

        /// <summary>FEngGetBottomRight (:309): boxes pos + size/2; strings top-left + Size · extent; else pos.</summary>
        public Vector2 GetBottomRight(Node n)
        {
            if (n == null) return Vector2.zero;
            var d = n.Data;
            if (IsBox(n)) return new Vector2(d.X + d.SizeX * 0.5f, d.Y + d.SizeY * 0.5f);
            if (n.Obj.Type == FEObjectType.String)
            {
                var f = n.Label != null ? n.Label.Font : null;
                if (f == null) return Vector2.zero;
                string s = n.Label.Text;
                float w = d.SizeX * f.TextWidth(s, 0), h = d.SizeY * f.TextHeight(s, (int)n.Obj.Leading, 0, 0, false);
                return new Vector2(d.X + FEFont.XOffset(n.Obj.Justify, w) + w, d.Y + FEFont.YOffset(n.Obj.Justify, h) + h);
            }
            return new Vector2(d.X, d.Y);
        }

        /// <summary>FEngSetBottomRight (:360): boxes pos = br − size/2; everything else pos = br; through SetPosition.</summary>
        public void SetBottomRight(Node n, float x, float y)
        {
            if (n == null) return;
            var d = n.Data;
            if (IsBox(n)) SetPosition(n, x - d.SizeX * 0.5f, y - d.SizeY * 0.5f, d.Z);
            else SetPosition(n, x, y, d.Z);
        }

        /// <summary>FEngGetCenter (:407): strings the middle of their extent; everything else pos.</summary>
        public Vector2 GetCenter(Node n)
        {
            if (n == null) return Vector2.zero;
            var d = n.Data;
            if (n.Obj.Type == FEObjectType.String && n.Label != null && n.Label.Font != null)
            {
                var f = n.Label.Font;
                string s = n.Label.Text;
                float w = d.SizeX * f.TextWidth(s, 0), h = d.SizeY * f.TextHeight(s, (int)n.Obj.Leading, 0, 0, false);
                return new Vector2(d.X + FEFont.XOffset(n.Obj.Justify, w) + w * 0.5f, d.Y + FEFont.YOffset(n.Obj.Justify, h) + h * 0.5f);
            }
            return new Vector2(d.X, d.Y);
        }

        /// <summary>FEngSetCenter (:438): strings undo the justify offsets; everything else pos = centre; through SetPosition.</summary>
        public void SetCenter(Node n, float x, float y)
        {
            if (n == null) return;
            var d = n.Data;
            float px = x, py = y;
            if (n.Obj.Type == FEObjectType.String)
            {
                var f = n.Label != null ? n.Label.Font : null;
                if (f == null) { px = d.X; py = d.Y; }
                else
                {
                    string s = n.Label.Text;
                    float w = d.SizeX * f.TextWidth(s, 0), h = d.SizeY * f.TextHeight(s, (int)n.Obj.Leading, 0, 0, false);
                    px = x - (FEFont.XOffset(n.Obj.Justify, w) + w * 0.5f);
                    py = y - (FEFont.YOffset(n.Obj.Justify, h) + h * 0.5f);
                }
            }
            SetPosition(n, px, py, d.Z);
        }

        /// <summary>FEngSetMultiImageRot (FEngInterfaceFEObjects.cpp:180, PC 0x514FB0): PivotRot.z (dword 35) directly and
        /// FF_DirtyCode.</summary>
        public void SetMultiImageRot(Node n, float degrees)
        {
            if (n == null) return;
            n.Data.MaskAngle = degrees;
            n.DirtyCode = true;
        }

        /// <summary>FEMultiImage::SetUVs(layer, tl, br) + FF_DirtyCode (FEngSetMultiImageTopLeftUVs / BottomRightUVs,
        /// PC 0x524C00): the layer's UV rectangle directly (dwords 21-32).</summary>
        public void SetMultiImageUVs(Node n, int layer, float u0, float v0, float u1, float v1)
        {
            if (n == null || layer < 0 || layer > 2) return;
            n.Data[21 + layer * 2] = u0; n.Data[22 + layer * 2] = v0;
            n.Data[27 + layer * 2] = u1; n.Data[28 + layer * 2] = v1;
            n.DirtyCode = true;
        }

        /// <summary>FEImage::SetUVs (the FEngImage UV setters: UpperLeft / LowerRight through SetTrackValue, dwords 17-20).</summary>
        public void SetUVs(Node n, float u0, float v0, float u1, float v1)
        {
            if (n == null) return;
            SetTrackValue(n, 17, new[] { u0, v0 }, false);
            SetTrackValue(n, 19, new[] { u1, v1 }, false);
            n.DirtyCode = true;
        }

        /// <summary>FEngSetTextureHash (FEngInterfaceFEImages.cpp:21): a new texture handle and FF_DirtyCode; the same
        /// handle is ignored. Visibility is not touched (unlike SetTexture, which hides an image without texture [ours]).</summary>
        public void SetTextureHash(Node n, uint hash)
        {
            if (n?.Image == null || n.TextureHash == hash) return;
            n.TextureHash = hash;
            n.Image.texture = FrontEndData.Texture(hash);
            n.DirtyCode = true;
        }

        /// <summary>FEngSetTextureHash with a texture the FE texture library does not hold (the minimap's chops, loaded by
        /// ChoppedMiniMapManager): the hash is kept for the same-hash test, the texture drawn (null = nothing).</summary>
        public void SetTextureHash(Node n, uint hash, Texture tex)
        {
            if (n?.Image == null || (n.TextureHash == hash && n.Image.texture == tex)) return;
            n.TextureHash = hash;
            n.Image.texture = tex;
            n.DirtyCode = true;
        }

        /// <summary>
        /// FEPrintf (FEngInterfaceFEStrings.cpp:99; PC DoFEngPrintf 0x515D20): the text is cut to the string's buffer length
        /// − 1 (Sb) when that is below 0x400, set, and the object gets flags 0x400002 (FF_DirtyCode, and 2: draw the literal
        /// string, not the label). On a group every string below it gets the text (FEngGroupFEPrintf 0x515DB0, via
        /// FEPrintf(pkg, hash) 0x5253B0).
        /// </summary>
        public void Printf(Node n, string text)
        {
            if (n == null) return;
            text ??= "";
            if (n.Obj.Type == FEObjectType.Group)
            {
                foreach (var d in Descendants(n)) if (d.Obj.Type == FEObjectType.String) Printf(d, text);
                return;
            }
            if (n.Label == null) return;
            int len = n.Obj.StringBufferLength;
            if (len > 0 && len < 0x400 && text.Length > len - 1) text = text.Substring(0, len - 1);
            n.Label.Text = text;
            n.LiteralText = true;
            n.DirtyCode = true;
        }

        /// <summary>FEngSetLanguageHash (FEngInterfaceFEStrings.cpp:47): the label hash, flag 2 cleared, FF_DirtyCode — the
        /// string draws the label's localised text (RenderString 0x5A4390); a group sets every string below it.</summary>
        public void SetLanguageHash(Node n, uint hash)
        {
            if (n == null) return;
            if (n.Obj.Type == FEObjectType.Group)
            {
                foreach (var d in Descendants(n)) if (d.Obj.Type == FEObjectType.String) SetLanguageHash(d, hash);
                return;
            }
            if (n.Label == null) return;
            n.LabelHash = hash;
            n.LiteralText = false;
            n.DirtyCode = true;
            n.Label.Text = FrontEndData.Text(hash, n.Label.Text);
        }
    }
}
