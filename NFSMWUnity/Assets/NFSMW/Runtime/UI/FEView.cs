using System;
using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;
using UnityEngine.UI;

namespace NfsMw.UI
{
    /// <summary>
    /// Shows one FEng package with its original layout, textures, fonts and scripts (INIT on open, others on demand).
    /// Objects are flattened under one rect and ordered by absolute Z (larger Z is further back).
    /// Space: 640x480 (854 wide on widescreen), origin at the centre, +Y down — as in the packages.
    /// </summary>
    public sealed partial class FEView : MonoBehaviour
    {
        void OnDestroy()
        {
            Live.Remove(this);
            foreach (var n in Nodes) if (n.Mask) Destroy(n.Mask);
            if (_back != null) { if (Application.isPlaying) Destroy(_back.gameObject); else DestroyImmediate(_back.gameObject); }
        }

        public static readonly uint Init = FrontEndData.Hash("INIT"), Highlight = FrontEndData.Hash("HIGHLIGHT"),
            UnHighlight = FrontEndData.Hash("UNHIGHLIGHT"), Show = FrontEndData.Hash("SHOW"), Hide = FrontEndData.Hash("HIDE"), Appear = FrontEndData.Hash("APPEAR"),
            Grey = FrontEndData.Hash("GREY"), Pressed = FrontEndData.Hash("PRESSED"), Active = FrontEndData.Hash("ACTIVE"), Inactive = FrontEndData.Hash("INACTIVE");

        public sealed class Node
        {
            public FEObject Obj;
            public FEObjectData Data;
            public Node Parent;
            public readonly List<Node> Children = new List<Node>();
            public RectTransform Rect;
            public RawImage Image;
            public FEText Label;
            public Material Mask;      // multi-image: base texture cut by a mask texture
            public FEScript Script;
            public float Time;
            public bool Visible = true;
            public float Z;
            public string Name => FrontEndData.Names.TryGetValue(Obj.Hash, out var n) ? n : Obj.Hash.ToString("X8");
            /// <summary>Engine views: FF_IsButton (0x10000000) minus the PC-hidden ones; ButtonDisabled = FF_IgnoreButton (FEngDisableButton).</summary>
            public bool IsButton, ButtonDisabled;
            /// <summary>Absolute FE position (centre) and size of the last layout, for mouse hit tests.</summary>
            public Vector2 AbsPos, AbsSize;
            internal byte MouseBits;
            /// <summary>FF_DirtyCode: code changed the object this frame (the SetTrackValue family, FEPrintf, …); a finished
            /// script's tracks are applied once more (FEPackage::UpdateObject), then the flag clears.</summary>
            internal bool DirtyCode;
            /// <summary>The object's own track base keys where code set the field (FEObject::SetTrackValue); the package
            /// tracks are shared by every view, the exe's are per object.</summary>
            internal Dictionary<FETrack, float[]> BaseKeys;
            /// <summary>Taken out of the package (FEPackage::RemoveObject).</summary>
            internal bool Removed;
            /// <summary>FEImage Handle: the texture hash (FEngSetTextureHash ignores the same hash).</summary>
            public uint TextureHash;
            /// <summary>Strings: game flag 2 (draw the literal string, not the label) and the label hash (FEString::LabelHash).</summary>
            public bool LiteralText;
            public uint LabelHash;
            /// <summary>The texture the blend state was last set for (FEngRender.cs ApplyBlend).</summary>
            internal Texture BlendTex;
            /// <summary>Script state of FEKeyInterpFast / SetupMoveToTracks: tracks applied once (InterpAction 0x80) and the
            /// MoveTo tracks' first delta keys (FEngEngine.cs).</summary>
            internal HashSet<FETrack> StaticTracks;
            internal Dictionary<FETrack, float[]> MoveTo;
            /// <summary>The last script set or chained to (plain views drop Script when it ends; FEngIsScriptSet still sees it).</summary>
            public uint LastScriptId;
            /// <summary>FEngIsScriptRunning: the current script has not reached its end (engine views keep a finished script set).</summary>
            public bool ScriptRunning => Script != null && Time <= Script.Length;
        }

        public FEPackage Package { get; private set; }
        public readonly List<Node> Nodes = new List<Node>();
        readonly Dictionary<uint, Node> _byHash = new Dictionary<uint, Node>();
        RectTransform _content;
        bool _sortDirty = true;

        /// <summary>Objects at or behind this depth go to the back layer (drawn behind the 3D car) when one is given.</summary>
        public float BackDepth = 600f;
        RectTransform _back;

        /// <summary>
        /// Builds the package and runs INIT. Menus pass appear: the game sends APPEAR when a screen comes up, and many
        /// packages (rival, rap sheet, …) start hidden in INIT and only fade in on it.
        /// </summary>
        public static FEView Open(string package, Transform parent, Transform backParent = null, bool appear = false)
            => Open(package, parent, backParent, appear, false);

        /// <summary>
        /// engine: run the package on the FEng message engine (FEngine.cpp / FEPackage.cpp, see the "message engine" region):
        /// visibility from the object flag bit 0, the PC load-time pass (0x525520: flag 0x40 objects hidden and no longer
        /// buttons), script events, MsgR / PkgR responses, the current button and pad / mouse input. INIT chains replace
        /// the forced APPEAR of plain views.
        /// </summary>
        public static FEView Open(string package, Transform parent, Transform backParent, bool appear, bool engine)
        {
            FrontEndData.Load();
            if (!FrontEndData.Packages.TryGetValue(package, out var pkg)) throw new KeyNotFoundException("FEng package " + package);
            var go = new GameObject(package, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var view = go.AddComponent<FEView>();
            if (backParent != null)
            {
                var back = new GameObject(package + " (back)", typeof(RectTransform));
                back.transform.SetParent(backParent, false);
                view._back = (RectTransform)back.transform;
                view._back.anchorMin = view._back.anchorMax = new Vector2(0.5f, 0.5f);
                view._back.sizeDelta = new Vector2(640, 480);
            }
            view.Engine = engine;
            view.Build(pkg);
            if (appear && !engine)
            {
                // [ours] plain views have no message engine: the screen's opening messages are stood in for by playing APPEAR
                // on the objects whose responses set APPEAR (MR_SetScript, the way the exe's packages reach an object's APPEAR
                // besides code) and on the objects INIT leaves at alpha 0 (the "start hidden, fade in on APPEAR" packages this
                // stand-in exists for, whose APPEAR the screen code plays in the exe). Objects already showing after INIT with
                // no response for APPEAR keep their INIT state — before round 9 every object with an APPEAR script got it,
                // which turned Car_Select's black stat-label shadows white at 0.7 over their labels.
                bool any = false;
                foreach (var n in view.Nodes)
                    if ((int)n.Data.A == 0 || n.Obj.MessageResponses.Exists(r => r.List.Exists(c => c.Id == FEResponse.SetScript && c.Param == Appear)))
                        any |= view.Play(n, Appear);
                if (any) view.Apply(0);
            }
            return view;
        }

        void Build(FEPackage pkg)
        {
            Package = pkg;
            var rt = (RectTransform)transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(640, 480);
            _content = rt;
            var byGuid = new Dictionary<uint, Node>();
            foreach (var o in pkg.Objects)
            {
                var n = new Node { Obj = o, Data = o.Data, Visible = !o.Hidden };
                if (Engine) n.IsButton = (o.Flags & FF_IsButton) != 0 && (o.Flags & FF_IgnoreButton) == 0;
                byGuid[o.Guid] = n;
                Nodes.Add(n);
                if (!_byHash.ContainsKey(o.Hash)) _byHash[o.Hash] = n;
            }
            foreach (var n in Nodes)
                if (n.Obj.Parent != 0 && byGuid.TryGetValue(n.Obj.Parent, out var p) && p != n)
                {
                    n.Parent = p;
                    p.Children.Add(n);
                }

            foreach (var n in Nodes) CreateVisual(n, pkg);
            // speed.exe 0x525520 (package loaded, every package): FEngHidePCObjects 0x516230 hides every flag-0x40 object
            // (recursively, FUN_00514C70) and clears its FF_IsButton; then FEngTransferFlagsToChildren(4) 0x516270.
            // Plain views get it too (they used a console-glyph heuristic, HideConsoleButtons, until the FE remainder round).
            foreach (var n in Nodes)
                if ((n.Obj.Flags & 0x40) != 0) { SetInvisibleTree(n); n.IsButton = false; }
            if (Engine)
            {
                _byGuid = new Dictionary<uint, Node>();
                foreach (var n in Nodes) _byGuid[n.Obj.Guid] = n;
                Live.Add(this);
            }
            // cFEngGameInterface::PackageWasLoaded (0x525520) ends by setting iGameMode: 1 while GRaceStatus exists (in game),
            // else 2 in rap-sheet mode (FEDatabase FEGameMode bit 0x200), else 0 — the colour flag-0x10 objects take
            // [?: "GRaceStatus exists" = FEMenuScreen.InGame, the world loaded and no front-end screen on the stack]
            GameMode = FEMenuScreen.InGame ? 1 : FEDb.Is(FEGameMode.RapSheet) ? 2 : 0;
            foreach (var n in Nodes) Play(n, Init);
            Apply(0);
        }

        /// <summary>The canvas object of a drawn node (images, strings, movies).</summary>
        void CreateVisual(Node n, FEPackage pkg)
        {
            // cFEngRender::RenderObject (0x5A45B0) draws types 1 image, 2 string, 7 movie, 9 coloured image, 12 multi-image
            // (and nothing for an object with flag 0x40); anim / simple images (10, 11) are not drawn on PC
            var ty = n.Obj.Type;
            if (ty != FEObjectType.Image && ty != FEObjectType.MultiImage && ty != FEObjectType.ColoredImage && ty != FEObjectType.String && ty != FEObjectType.Movie)
                return;
            var go = new GameObject(n.Name, typeof(RectTransform));
            go.transform.SetParent(_content, false);
            n.Rect = (RectTransform)go.transform;
            n.Rect.anchorMin = n.Rect.anchorMax = new Vector2(0.5f, 0.5f);
            string res = n.Obj.Resource >= 0 && n.Obj.Resource < pkg.Resources.Count ? pkg.Resources[n.Obj.Resource] : null;
            go.AddComponent<CanvasRenderer>();
            if (n.Obj.Type == FEObjectType.String)
            {
                n.Label = go.AddComponent<FEText>();
                n.Label.Font = FrontEndData.FontFor(res);
                // RenderString 0x5A4390: the label's localised text unless game flag 2, else the literal string
                n.LabelHash = n.Obj.Label;
                n.LiteralText = n.Obj.LiteralText;
                n.Label.Text = n.LiteralText ? n.Obj.Text ?? "" : FrontEndData.Text(n.Obj.Label, n.Obj.Text ?? "");
                n.Label.Justify = n.Obj.Justify;
                n.Label.MaxWidth = n.Obj.MaxWidth;
                n.Label.Leading = n.Obj.Leading;
                n.Label.Flags = n.Obj.Flags;
                n.Label.raycastTarget = false;
            }
            else
            {
                n.Image = ty == FEObjectType.ColoredImage ? go.AddComponent<FEColoredImage>() : go.AddComponent<RawImage>();
                n.Image.texture = FrontEndData.Texture(res);
                n.TextureHash = !string.IsNullOrEmpty(res) && !res.StartsWith("(") ? EaHash.HashUpper(System.IO.Path.GetFileNameWithoutExtension(res)) : 0;
                n.Image.raycastTarget = false;
                if (n.Obj.Extra.TryGetValue("M1", out var m1) && m1.Length >= 4 && BitConverter.ToUInt32(m1, 0) != 0)
                {
                    var mask = FrontEndData.Texture(BitConverter.ToUInt32(m1, 0));
                    var shader = mask ? Shader.Find("NFSMW/UIMultiImage") : null;
                    if (shader)
                    {
                        n.Mask = new Material(shader);
                        n.Mask.SetTexture("_Mask", mask);
                        n.Image.material = n.Mask;
                    }
                }
            }
        }

        /// <summary>
        /// FEObject::Clone (copy ctor PC 0x5C31A0) + FEPackage::AddObject (0x50DB90): a new object at the end of the package's
        /// root list (no parent) with copies of the source's scripts (each with its CurTime and base keys) and responses;
        /// its current script is the source's current one (SetCurrentScript: SetupMoveToTracks), the flags are copied and
        /// it gets a new GUID (not found by GUID lookups). The exe zeroes the clone's data; uiCredits (its one caller)
        /// then copies the prototype's FEObjData over it — folded in here: the clone starts with the source's data.
        /// The port shares the package object (scripts, responses); the per-object state lives on the node.
        /// </summary>
        public Node Clone(Node src)
        {
            if (src == null) return null;
            var n = new Node { Obj = src.Obj, Data = src.Data, Visible = src.Visible, IsButton = false };
            if (src.BaseKeys != null) n.BaseKeys = new Dictionary<FETrack, float[]>(src.BaseKeys);
            Nodes.Add(n);
            CreateVisual(n, Package);
            if (src.Label != null && n.Label != null) { n.Label.Text = src.Label.Text; n.LiteralText = src.LiteralText; n.LabelHash = src.LabelHash; }
            if (src.Script != null)
            {
                n.Script = src.Script;
                n.LastScriptId = src.Script.Id;
                n.Time = src.Time;
                SetupMoveTo(n);
            }
            _sortDirty = true;
            TreeChanged();
            return n;
        }

        /// <summary>FEPackage::RemoveObject + delete (a credits line that reached the top).</summary>
        public void Remove(Node n)
        {
            if (n == null || !Nodes.Remove(n)) return;
            n.Removed = true;
            n.Parent?.Children.Remove(n);
            TreeChanged();
            if (n.Mask) Destroy(n.Mask);
            if (n.Rect) { if (Application.isPlaying) Destroy(n.Rect.gameObject); else DestroyImmediate(n.Rect.gameObject); }
            if (CurrentButton == n) SetCurrentButton(null, false);
        }

        void SetInvisibleTree(Node n)
        {
            n.Visible = false;
            foreach (var c in n.Children) SetInvisibleTree(c);
        }

        // ---- lookups -------------------------------------------------------------------------------------------

        public Node Find(string name) => _byHash.TryGetValue(FrontEndData.Hash(name), out var n) ? n : null;
        public Node Find(uint hash) => _byHash.TryGetValue(hash, out var n) ? n : null;

        public IEnumerable<Node> Descendants(Node n)
        {
            foreach (var c in n.Children)
            {
                yield return c;
                foreach (var d in Descendants(c)) yield return d;
            }
        }

        // ---- control -------------------------------------------------------------------------------------------

        public bool Play(Node n, uint script, bool recursive = false)
        {
            if (n == null) return false;
            bool any = false;
            var s = n.Obj.Script(script);
            if (s != null)
            {
                // MR_SetScript (FEngine::ProcessResponses): SetCurrentScript (SetupMoveToTracks) and CurTime = 0, also when
                // the script is already current
                n.Script = s;
                n.Time = 0;
                n.LastScriptId = s.Id;
                SetupMoveTo(n);
                any = true;
            }
            if (recursive)
                foreach (var c in n.Children) any |= Play(c, script, true);
            return any;
        }

        public bool Play(string name, string script, bool recursive = true) => Play(Find(name), FrontEndData.Hash(script), recursive);

        public void SetText(Node n, string text)
        {
            if (n?.Label != null) n.Label.Text = text ?? "";
        }

        public void SetText(string name, string text) => SetText(Find(name), text);

        public void SetTexture(Node n, Texture2D tex)
        {
            if (n?.Image == null) return;
            n.Image.texture = tex;
            n.TextureHash = 0;
            n.Visible = tex != null;
        }

        /// <summary>
        /// FEngSetVisible / FEngSetInvisible, recursive (visibility is per object). Making visible skips objects with flag
        /// 0x40 and their subtrees, as FEngSetVisible does on PC (0x514CC0): the console-only objects the load pass hid stay
        /// hidden (before round 9 this helper revealed them, e.g. MainMenu's console "GAME STATS GROUP" label).
        /// </summary>
        public void SetVisible(Node n, bool visible)
        {
            if (n == null) return;
            if (visible && (n.Obj.Flags & 0x40) != 0) return;
            n.Visible = visible;
            foreach (var c in n.Children) SetVisible(c, visible);
        }

        /// <summary>First string object under (or at) n.</summary>
        public Node TextOf(Node n)
        {
            if (n == null) return null;
            if (n.Label != null) return n;
            foreach (var d in Descendants(n)) if (d.Label != null) return d;
            return null;
        }

        // ---- animation & layout --------------------------------------------------------------------------------

        void Update() => Apply(UnityEngine.Time.unscaledDeltaTime);

        public void Apply(float dt)
        {
            // every view runs FEPackage::UpdateObject (FEngEngine.cs StepScript); plain views issue no script events.
            // Before the FE remainder round plain views dropped a script at its end and re-applied tracks every frame.
            EngineUpdate(dt);
            foreach (var n in Nodes) if (n.Parent == null) Layout(n, Vector2.zero, Vector2.one, 0f, null, 0f, true);
            if (_sortDirty)
            {
                if (_back != null)
                    foreach (var n in Nodes)
                        if (n.Rect != null)
                        {
                            var want = n.Z >= BackDepth ? _back : _content;
                            if (n.Rect.parent != want) n.Rect.SetParent(want, false);
                        }
                var drawn = Nodes.FindAll(x => x.Rect != null);
                // larger Z is further back; on equal Z the earlier object in the package draws on top
                drawn.Sort((a, b) => b.Z != a.Z ? b.Z.CompareTo(a.Z) : b.Obj.Order.CompareTo(a.Obj.Order));
                for (int i = 0; i < drawn.Count; i++) drawn[i].Rect.SetAsLastSibling();
                _sortDirty = false;
            }
            if (Engine && MessagesEnabled) ProcessMessages();
        }

        /// <summary>
        /// cFEngRender::MakeRenderMatrix colour (FEngRender.cpp:43, PC 0x5848B0): an object in a group gets
        /// (context · own + 128) >> 8 per channel, the context being the group's own composite clamped to 0..255
        /// (GenerateRenderContext); a root object its own colour. Drawn through FEngColorToEpolyColor (PC 0x56FA90: rgb / 2,
        /// alpha whole) and fe.efx (rgb × 2), so the rgb lose their lowest bit. (Before round 9: float parent × own / 255.)
        /// </summary>
        static (int r, int g, int b, int a) Compose((int r, int g, int b, int a)? ctx, in FEObjectData d)
        {
            int r = (int)d.R, g = (int)d.G, b = (int)d.B, a = (int)d.A;
            if (ctx == null) return (r, g, b, a);
            var c = ctx.Value;
            return ((c.r * r + 128) >> 8, (c.g * g + 128) >> 8, (c.b * b + 128) >> 8, (c.a * a + 128) >> 8);
        }

        static Color ToColor((int r, int g, int b, int a) c)
        {
            float Rgb(int v) => v <= 0 ? 0f : Mathf.Min(1f, ((v >> 1) * 2) / 255f);
            return new Color(Rgb(c.r), Rgb(c.g), Rgb(c.b), Mathf.Clamp(c.a, 0, 255) / 255f);
        }

        /// <summary>cFEngGameInterface::iGameMode for this package (0 front end, 1 in game, 2 rap sheet).</summary>
        public int GameMode { get; private set; }

        /// <summary>gNormal 0xFFE6E6C8, gTint 0xFFFFAF41, gRapsheet 0xFFAAE646 (FEGameInterface.cpp:18; PC static inits
        /// 0x8863F0 / 0x886400 / 0x886410), as (B, G, R, A).</summary>
        static readonly float[][] GameModeColours = { new float[] { 0xC8, 0xE6, 0xE6, 0xFF }, new float[] { 0x41, 0xAF, 0xFF, 0xFF }, new float[] { 0x46, 0xE6, 0xAA, 0xFF } };

        void Layout(Node n, Vector2 parentPos, Vector2 parentScale, float parentAngle, (int r, int g, int b, int a)? parentCtx, float parentZ, bool parentVisible)
        {
            // cFEngGameInterface::RenderObject (PC 0x516630): an object with game flag 0x10 that reaches rendering (FEngine::
            // RenderObject skips data alpha 0) gets its colour overwritten by the game mode's, every frame
            if ((n.Obj.Flags & 0x10) != 0 && parentVisible && (int)n.Data.A != 0 && n.Obj.Type != FEObjectType.Group)
                n.Data.Set(0, GameModeColours[GameMode]);
            var d = n.Data;
            float own = 2f * Mathf.Atan2(d.QZ, d.QW == 0 && d.QZ == 0 ? 1 : d.QW);
            // cFEngRender::MakeRenderMatrix (0x5848B0): v · Size · T(−pivot) · R · T(+pivot) · T(pos), then the parent's matrix —
            // the pivot (after the object's own scale) moves the rotation centre: origin = pos + pivot − R·pivot
            float px = d.X, py = d.Y;
            if ((d.PivotX != 0 || d.PivotY != 0) && own != 0)
            {
                float co = Mathf.Cos(own), so = Mathf.Sin(own);
                px += d.PivotX - (d.PivotX * co - d.PivotY * so);
                py += d.PivotY - (d.PivotX * so + d.PivotY * co);
            }
            var local = new Vector2(px * parentScale.x, py * parentScale.y);
            float c = Mathf.Cos(parentAngle), s = Mathf.Sin(parentAngle);
            var pos = parentPos + new Vector2(local.x * c - local.y * s, local.x * s + local.y * c);
            float angle = parentAngle + own;
            var composite = Compose(parentCtx, d);
            var color = ToColor(composite);
            float z = parentZ + d.Z;
            // FEngine::RenderGroup / cFEngGameInterface::RenderObject: the flag-1 test is per object (a group with bit 0 still
            // renders its children, e.g. the PC key groups, flags 0x40020009); a group only drops its children at alpha 0.
            // RenderGroup (PC 0x5BB9C0) and RenderObject (0x5B7690) both test the object's own data alpha (Col.a == 0 → not
            // drawn, a group's whole subtree skipped) — before the composite, so this also covers coloured images, which
            // draw their vertex colours only. parentVisible = every ancestor group passed that test.
            bool visible = n.Visible && parentVisible && (int)d.A != 0;
            if (Math.Abs(z - n.Z) > 0.001f) { n.Z = z; _sortDirty = true; }

            bool isGroup = n.Obj.Type == FEObjectType.Group;
            var scale = isGroup ? new Vector2(parentScale.x * d.SizeX, parentScale.y * d.SizeY) : parentScale;
            n.AbsPos = pos;
            n.AbsSize = isGroup || n.Label != null ? Vector2.zero : new Vector2(Mathf.Abs(d.SizeX * parentScale.x), Mathf.Abs(d.SizeY * parentScale.y));
            if (n.Rect != null)
            {
                n.Rect.anchoredPosition = new Vector2(pos.x, -pos.y);
                // the full quaternion (FE +Y down -> Unity +Y up: (-x, y, -z, w)); the HUD meters use 180 deg flips about
                // tilted in-plane axes to mirror their arcs, which a Z-only angle drops
                var q = new Quaternion(-d.QX, d.QY, -d.QZ, d.QW);
                float qn = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
                q = qn > 1e-5f ? new Quaternion(q.x / qn, q.y / qn, q.z / qn, q.w / qn) : Quaternion.identity;
                n.Rect.localRotation = Quaternion.Euler(0, 0, -parentAngle * Mathf.Rad2Deg) * q;
                bool show = visible && color.a > 0.002f;
                if (n.Image != null)
                {
                    float w = d.SizeX * parentScale.x, h = d.SizeY * parentScale.y;
                    n.Rect.sizeDelta = new Vector2(Mathf.Abs(w), Mathf.Abs(h));
                    n.Rect.localScale = new Vector3(Mathf.Sign(w == 0 ? 1 : w), Mathf.Sign(h == 0 ? 1 : h), 1);
                    if (n.Image is FEColoredImage cbv)
                    {
                        // RenderCBVImage 0x59A790: the four vertex colours only (no object / parent colour)
                        Color V(int i) { var (b, g, r, a) = d.VertexColor(i); return ToColor(((int)r, (int)g, (int)b, (int)a)); }
                        cbv.SetCorners(V(0), V(1), V(2), V(3));
                        n.Image.color = Color.white;
                        show = visible;
                    }
                    else n.Image.color = color;
                    // texture rows are stored top first, so v=0 is the image top
                    n.Image.uvRect = new Rect(d.U0, d.V1, d.U1 - d.U0, d.V0 - d.V1);
                    if (n.Mask)
                    {
                        var r = n.Image.uvRect;
                        n.Mask.SetVector("_Rect", new Vector4(r.x, r.y, r.width, r.height));
                        n.Mask.SetVector("_MaskRect", new Vector4(d.M1U0, d.M1V0, d.M1U1, d.M1V1));
                        n.Mask.SetVector("_MaskRot", new Vector4(d.MaskPivotU, d.MaskPivotV, -d.MaskAngle * Mathf.Deg2Rad, 0));   // stored in degrees, clockwise on screen
                    }
                    if (n.Image.texture != n.BlendTex) ApplyBlend(n);
                    show &= n.Image.texture != null;
                    if (n.Image.enabled != show) n.Image.enabled = show;
                }
                else if (n.Label != null)
                {
                    n.Rect.sizeDelta = Vector2.zero;
                    n.Label.Scale = new Vector2(d.SizeX * parentScale.x, d.SizeY * parentScale.y);
                    n.Label.color = color;
                    if (n.Label.enabled != show) n.Label.enabled = show;
                }
            }
            if (n.Children.Count > 0)
            {
                var ctx = (Mathf.Clamp(composite.r, 0, 255), Mathf.Clamp(composite.g, 0, 255), Mathf.Clamp(composite.b, 0, 255), Mathf.Clamp(composite.a, 0, 255));
                bool drawn = parentVisible && (int)d.A != 0;
                foreach (var ch in n.Children) Layout(ch, pos, scale, angle, ctx, z, drawn);
            }
        }
    }
}
