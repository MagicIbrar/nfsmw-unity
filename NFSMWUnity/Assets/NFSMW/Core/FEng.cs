using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace NfsMw.Core
{
    /// <summary>FEObjType (FEng/FEObject.h:19). Before the FE remainder round 7 was taken for MultiImage and 12 for ColoredImage.</summary>
    public enum FEObjectType { None = 0, Image = 1, String = 2, Model = 3, List = 4, Group = 5, CodeList = 6, Movie = 7, Effect = 8, ColoredImage = 9, AnimImage = 10, SimpleImage = 11, MultiImage = 12 }

    /// <summary>
    /// Per-object animatable data block ("SA", FEObjData + the type's extension), dword by dword: 0-3 colour (B, G, R, A ints,
    /// 0..255), 4-6 pivot, 7-9 position, 10-13 rotation quaternion, 14-16 size; images 17-20 UVs (FEImageData, 0x54 bytes);
    /// multi-images (type 12, FEMultiImageData 0x90): 21-26 top-left UVs of textures 1-3, 27-32 bottom-right UVs, 33-35 PivotRot
    /// (the mask pivot and angle in degrees); coloured images (type 9, FEColoredImageData 0x94): 21-36 four vertex colours
    /// (B, G, R, A ints; TL, TR, BR, BL — FERenderObject::AddPoly 0x582660 vertex order). Script tracks write by dword
    /// offset (FEKeyTrack::LongOffset), so the same offset means different fields per type: the indexer is the raw view.
    /// </summary>
    public struct FEObjectData
    {
        public float B, G, R, A;  // 0..255
        public float PivotX, PivotY, PivotZ;
        public float X, Y, Z;     // relative to the parent, screen centre = 0,0, +Y down (640x480 space)
        public float QX, QY, QZ, QW;
        public float SizeX, SizeY, SizeZ;
        public float U0, V0, U1, V1; // images only
        // multi-images (type 12): UV rectangles of the three mask textures, and the mask transform (pivot, angle)
        public float M1U0, M1V0, M2U0, M2V0, M3U0, M3V0, M1U1, M1V1, M2U1, M2V1, M3U1, M3V1;
        public float MaskPivotU, MaskPivotV, MaskAngle;
        /// <summary>Dword 36: the last vertex colour's alpha of a coloured image (type 9).</summary>
        public float Ext36;
        public const int Dwords = 37;

        public float this[int dword]
        {
            get
            {
                switch (dword)
                {
                    case 0: return B; case 1: return G; case 2: return R; case 3: return A;
                    case 4: return PivotX; case 5: return PivotY; case 6: return PivotZ;
                    case 7: return X; case 8: return Y; case 9: return Z;
                    case 10: return QX; case 11: return QY; case 12: return QZ; case 13: return QW;
                    case 14: return SizeX; case 15: return SizeY; case 16: return SizeZ;
                    case 17: return U0; case 18: return V0; case 19: return U1; case 20: return V1;
                    case 21: return M1U0; case 22: return M1V0; case 23: return M2U0; case 24: return M2V0; case 25: return M3U0; case 26: return M3V0;
                    case 27: return M1U1; case 28: return M1V1; case 29: return M2U1; case 30: return M2V1; case 31: return M3U1; case 32: return M3V1;
                    case 33: return MaskPivotU; case 34: return MaskPivotV; case 35: return MaskAngle; case 36: return Ext36;
                    default: return 0;
                }
            }
            set
            {
                switch (dword)
                {
                    case 0: B = value; break; case 1: G = value; break; case 2: R = value; break; case 3: A = value; break;
                    case 4: PivotX = value; break; case 5: PivotY = value; break; case 6: PivotZ = value; break;
                    case 7: X = value; break; case 8: Y = value; break; case 9: Z = value; break;
                    case 10: QX = value; break; case 11: QY = value; break; case 12: QZ = value; break; case 13: QW = value; break;
                    case 14: SizeX = value; break; case 15: SizeY = value; break; case 16: SizeZ = value; break;
                    case 17: U0 = value; break; case 18: V0 = value; break; case 19: U1 = value; break; case 20: V1 = value; break;
                    case 21: M1U0 = value; break; case 22: M1V0 = value; break; case 23: M2U0 = value; break; case 24: M2V0 = value; break;
                    case 25: M3U0 = value; break; case 26: M3V0 = value; break;
                    case 27: M1U1 = value; break; case 28: M1V1 = value; break; case 29: M2U1 = value; break; case 30: M2V1 = value; break;
                    case 31: M3U1 = value; break; case 32: M3V1 = value; break;
                    case 33: MaskPivotU = value; break; case 34: MaskPivotV = value; break; case 35: MaskAngle = value; break; case 36: Ext36 = value; break;
                }
            }
        }

        /// <summary>Vertex colour i (0 TL, 1 TR, 2 BR, 3 BL) of a coloured image as (B, G, R, A).</summary>
        public (float b, float g, float r, float a) VertexColor(int i) => (this[21 + i * 4], this[22 + i * 4], this[23 + i * 4], this[24 + i * 4]);

        /// <summary>Reads SA. intExt: the extension holds ints (coloured images' vertex colours), else floats.</summary>
        public static FEObjectData Read(byte[] d, int o, int len, bool intExt = false)
        {
            var r = new FEObjectData { U0 = 0, V0 = 0, U1 = 1, V1 = 1 };
            r.M1U1 = r.M1V1 = r.M2U1 = r.M2V1 = r.M3U1 = r.M3V1 = 1;
            r.MaskPivotU = r.MaskPivotV = 0.5f;
            int n = Math.Min(len / 4, Dwords);
            for (int i = 0; i < n; i++)
                r[i] = i < 4 || intExt && i >= 21 ? Chunks.I32(d, o + i * 4) : Chunks.F32(d, o + i * 4);
            return r;
        }

        /// <summary>Reads the n dwords a track writes from offset (FEKeyTrack::LongOffset).</summary>
        public float[] Get(int offset, int n)
        {
            if (offset < 0 || offset >= Dwords) return null;
            var v = new float[Math.Max(1, n)];
            for (int i = 0; i < v.Length; i++) v[i] = this[offset + i];
            return v;
        }

        /// <summary>Writes one track value from its dword offset.</summary>
        public void Set(int offset, float[] v)
        {
            for (int i = 0; i < v.Length && offset + i < Dwords; i++) this[offset + i] = v[i];
        }
    }

    public sealed class FETrack
    {
        public int ValueType;    // FI byte 0 (0 bool, 1 int, 2 float, 3 vec2, 4 vec3, 5 quat, 6 colour)
        public int ValueSize;    // FI byte 1
        public int Interp;       // FI byte 2 InterpType (0 none, 1 / 3 linear, 2 / 4 not interpolated: FEKeyInterp writes nothing)
        public int Action;       // FI byte 3 InterpAction (0 clamp, 1 loop, 2 ping-pong; FEKeyInterpLinear.cpp)
        public int Length;       // FI dword 1: the track length (FEKeyTrack::Length; loop / ping-pong / none use it)
        public int Offset;       // "To": field index in dwords (0 colour, 4 pivot, 7 position, 10 rotation, 14 size, 17/19 UVs)
        public readonly List<(int time, float[] value)> Keys = new List<(int, float[])>(); // Keys[0] = the base key (its time is -1 in the packages)

        /// <summary>
        /// FEKeyInterp / FEKeyInterpFast (FEng/FEKeyInterp.cpp, FEKeyInterpLinear.cpp): the value at time t (ms), or null when
        /// nothing is written (no keys, or interpolator types 2 / 4, which FEKeyInterp leaves out). The first Kd key is
        /// always the base key (FEPackageReader::ReadScriptTags), the rest are deltas on it. Linear (1 / 3), by
        /// InterpAction &amp; 0x7F: 0 clamp (before the first delta key = base + first; after the last = base + last), 1 loop over
        /// the track Length (wrapping from the last key to the first), 2 ping-pong (t past Length mirrored, key value
        /// only). Ints and colours lerp as n1 + (int)((n2 − n1)·t + 0.5) (FELerpInteger / FELerpColor), floats and vectors
        /// linearly, quaternions as base · slerp(q1, q2) (FELerpQuaternion; base · key at the ends). None (0): the key value
        /// itself (FEInterpNone copies it without the base). firstDelta: MoveTo tracks (types 3 / 4,
        /// FEObject::SetupMoveToTracks) replace the first delta key with (object value at SetScript − base).
        /// </summary>
        public float[] Evaluate(int t, float[] firstDelta = null, float[] baseKey = null)
        {
            if (Keys.Count == 0) return null;
            if (Interp != 0 && Interp != 1 && Interp != 3) return null;
            // baseKey: the object's own base key when code changed it (FEObject::SetTrackValue writes the BaseKey of every
            // script's track for the field; the port keeps that per view node, the package tracks are shared)
            var baseV = baseKey ?? Keys[0].value;
            int deltas = Keys.Count - 1;
            float[] K(int i) => i == 0 ? baseV : i == 1 && firstDelta != null ? firstDelta : Keys[i].value;
            if (Interp == 0)
            {
                // FEInterpNone (FEKeyInterpNone.cpp:14, PC 0x5B5840): t <= Length → the key at t (the previous one when
                // it starts later); past Length GetKeyAt(Length − t) is the base key (negative time) and BaseKey has no
                // next node, so the base value. Dead on PC: no shipped track uses InterpType 0 (harness feeng9 scripts).
                if (t < 0 || deltas == 0 || t > Length) return (float[])baseV.Clone();
                int k = KeyAt(t);
                if (k > 1 && Keys[k].time > t) k--;
                return (float[])K(k).Clone();
            }
            if (deltas == 0) return (float[])baseV.Clone();
            int key, prev = -1;
            float f = 0;
            switch (Action & 0x7F)
            {
                default:
                case 0:
                    key = DeltaAt(t);
                    if (key > 1 && t < Keys[key].time)
                    {
                        prev = key - 1;
                        float div = Keys[key].time - Keys[prev].time;
                        if (div > 0.00001f) f = (t - Keys[prev].time) / div;
                    }
                    else { prev = key; f = 1; }
                    break;
                case 1:
                    key = DeltaAt(t);
                    prev = key;
                    if (Keys[key].time < t)
                    {
                        float div = (Length - Keys[key].time) + Keys[1].time;
                        if (div > 0.00001f) f = (t - Keys[key].time) / div;
                        key = 1;
                    }
                    else if (Keys[key].time != t)
                    {
                        if (key > 1)
                        {
                            prev = key - 1;
                            float div = Keys[key].time - Keys[prev].time;
                            if (div > 0.00001f) f = (t - Keys[prev].time) / div;
                        }
                        else
                        {
                            prev = KeyAt(Length);
                            float div = (Length - Keys[prev].time) + Keys[key].time;
                            if (div > 0.00001f) f = (t + Length - Keys[prev].time) / div;
                        }
                    }
                    break;
                case 2:
                    if (t > Length) t = Length * 2 - t;
                    key = DeltaAt(t);
                    prev = key;
                    f = 1;
                    break;
            }
            var r = new float[baseV.Length];
            if (f == 0f || f == 1f)
            {
                var v = f == 0f ? K(prev) : K(key);
                if (ValueType == 5) return QuatMul(baseV, v);
                for (int c = 0; c < r.Length; c++) r[c] = baseV[c] + (c < v.Length ? v[c] : 0);
                return r;
            }
            var a = K(prev);
            var b = K(key);
            if (ValueType == 5) return QuatMul(baseV, Slerp(a, b, f));
            bool integer = ValueType == 1 || ValueType == 6;
            for (int c = 0; c < r.Length; c++)
            {
                float n1 = c < a.Length ? a[c] : 0, n2 = c < b.Length ? b[c] : 0;
                r[c] = baseV[c] + n1 + (integer ? (int)((n2 - n1) * f + 0.5f) : (n2 - n1) * f);
            }
            return r;
        }

        /// <summary>FEKeyTrack::GetKeyAt: t &lt; 0 → the base key; else the first delta key with time ≥ t (the last one if none).</summary>
        int KeyAt(int t)
        {
            if (t < 0 || Keys.Count < 2) return 0;
            return DeltaAt(t);
        }

        /// <summary>FEKeyTrack::GetDeltaKeyAt (index into Keys, ≥ 1).</summary>
        int DeltaAt(int t)
        {
            int i = 1;
            while (i + 1 < Keys.Count && Keys[i].time < t) i++;
            return i;
        }

        /// <summary>FEQuaternion::operator* (FEMath.h:55), arrays (x, y, z, w).</summary>
        public static float[] QuatMul(float[] p, float[] q)
        {
            float x = p[0], y = p[1], z = p[2], w = p[3];
            return new[]
            {
                (y * q[2] - z * q[1]) + (q[3] * x + q[0] * w),
                (z * q[0] - x * q[2]) + (q[3] * y + q[1] * w),
                (x * q[1] - y * q[0]) + (q[3] * z + q[2] * w),
                q[3] * w - (q[0] * x + q[1] * y + q[2] * z),
            };
        }

        /// <summary>FELerpQuaternion's slerp (shortest way; nlerp from q1 by (q·q1)·t above a dot of 0.999, as the decomp).</summary>
        static float[] Slerp(float[] q1, float[] q2, float t)
        {
            var q = (float[])q2.Clone();
            float dot = q1[0] * q[0] + q1[1] * q[1] + q1[2] * q[2] + q1[3] * q[3];
            if (dot < 0) { for (int i = 0; i < 4; i++) q[i] = -q[i]; dot = -dot; }
            if (dot < 0.999f)
            {
                float ang = (float)Math.Acos(dot), sa = (float)Math.Sin(ang), st = (float)Math.Sin(ang * t), si = (float)Math.Sin(ang * (1 - t));
                var r = new float[4];
                for (int i = 0; i < 4; i++) r[i] = (q1[i] * si + q[i] * st) * (1f / sa);
                return r;
            }
            var m = QuatMul(q, q1);
            var n = new float[4];
            for (int i = 0; i < 4; i++) n[i] = q1[i] + m[i] * t;
            float len = (float)Math.Sqrt(n[0] * n[0] + n[1] * n[1] + n[2] * n[2] + n[3] * n[3]);
            if (len > 1e-6f) for (int i = 0; i < 4; i++) n[i] /= len;
            return n;
        }
    }

    public sealed class FEScript
    {
        public uint Id;           // hash of the script name (INIT, HIGHLIGHT, ...)
        public int Length;        // ms
        public uint Flags;
        public uint Chain;        // script to start when this one ends
        public readonly List<FETrack> Tracks = new List<FETrack>();
        /// <summary>Script events (EV: message, target, time), sorted by time in every shipped package (harness feeng9 scripts),
        /// which FEPackage::IssueScriptMessages (PC 0x5BC530) relies on.</summary>
        public readonly List<(int time, uint message, uint target)> Events = new List<(int, uint, uint)>();

        /// <summary>FEScript::FindTrack (FEScript.cpp:92, PC 0x5B2910): the first track whose LongOffset is the field's
        /// dword offset (FETrackOffsets: 0 colour, 4 pivot, 7 position, 10 rotation, 14 size, 17 / 19 UVs, 21.. extensions).</summary>
        public FETrack FindTrack(int offset)
        {
            foreach (var t in Tracks) if (t.Offset == offset) return t;
            return null;
        }
    }

    /// <summary>One response command (FEResponse: ResponseID, ResponseParam, ResponseTarget; FEMessageResponseCommands).</summary>
    public sealed class FEResponse
    {
        public const uint SetScript = 0, PostMessageToFEng = 1, PostMessageToGame = 2, PostMessageToSound = 3,
            SetActiveButton = 256, SetInputProcessing = 257, RecordCurrentButton = 258, RecallRecordedButton = 259, DontNavigate = 260,
            SwitchToPackage = 512, PushPackageGlobal = 513, PushPackageCurrent = 514, PopPackage = 515, PushPackageNone = 516,
            IfScriptEquals = 768, IfScriptNotEquals = 769, Else = 1280, EndIf = 1281;
        public uint Id, Param, Target;
        public string Str;
    }

    /// <summary>A message id and its command list (FEMessageResponse).</summary>
    public sealed class FEMessageResponse
    {
        public uint Msg;
        public readonly List<FEResponse> List = new List<FEResponse>();
        public bool Has(uint id) => List.Exists(r => r.Id == id);
    }

    public sealed class FEObject
    {
        /// <summary>Fields this reader doesn't interpret, by tag (e.g. multi-image extra textures and UVs).</summary>
        public readonly Dictionary<string, byte[]> Extra = new Dictionary<string, byte[]>();
        public FEObjectType Type;
        public uint Guid, Hash, Flags;
        public int Resource;
        public uint Parent;       // parent GUID, 0 = package root
        public FEObjectData Data;
        public string Text;       // literal text (strings)
        public uint Label;        // localisation hash (strings)
        public int Justify;
        public float Leading, MaxWidth;
        public uint ImageFlags;
        public readonly List<FEScript> Scripts = new List<FEScript>();
        public readonly List<(uint msg, uint target, uint param)> Responses = new List<(uint, uint, uint)>();
        /// <summary>
        /// The full MsgR lists (FEMessageResponse.cpp): per message id, the commands in order (Ri id, Ru / Rs param,
        /// Rt target). Responses above keeps only the Ru values (older readers).
        /// </summary>
        public readonly List<FEMessageResponse> MessageResponses = new List<FEMessageResponse>();
        public int Order;         // position in the package (draw order tie-break)
        public FEMessageResponse FindResponse(uint msg) => MessageResponses.Find(r => r.Msg == msg);

        /// <summary>
        /// Starts invisible: game flag bit 0 (FEngSetInvisible / FEGameInterface::RenderObject test Flags &amp; 1). Was
        /// (Flags &amp; 0x90000000), i.e. FF_IsButton | FF_HideInEdit, which hid every button of a plain view.
        /// </summary>
        public bool Hidden => (Flags & 1) != 0;
        /// <summary>Game flag 2 on a string: draw the literal string, not the label's localised text (cFEngRender::RenderString
        /// 0x5A4390); FEPrintf / FESetString set it, FEngSetLanguageHash clears it.</summary>
        public bool LiteralText => (Flags & 2) != 0;
        /// <summary>Sb: the FEString buffer length (FEWideString::SetLength); FEPrintf (DoFEngPrintf 0x515D20) cuts the printed
        /// text to Sb − 1 characters when Sb &lt; 0x400. −1 = no Sb tag.</summary>
        public int StringBufferLength = -1;
        /// <summary>FEObject.h flags: FF_HideInEdit (editor only), FF_IsButton, FF_IgnoreButton, FF_DontNavigate, FF_MouseObject;
        /// game flags (low 16 bits): 1 invisible, 0x8 not made visible by code, 0x10 game-mode tint, 0x40 console only (hidden on PC).</summary>
        public const uint FlagInvisible = 1, FlagConsoleOnly = 0x40, FF_IsButton = 0x10000000, FF_HideInEdit = 0x80000000;
        public FEScript Script(uint id) => Scripts.Find(s => s.Id == id);
    }

    /// <summary>
    /// FEng package (.fng) — the front end/HUD layout format. Chunks: PkHd (name, path), TypS, ResC (RsNm names,
    /// RsRq), ObjC (FObj objects: ObjD tagged fields, Scrp scripts, MsgR responses), PkgR, Targ.
    /// Object fields are 2-char tags: Ot type, Oh hash, OP {guid, hash, flags, resource}, PA parent, SA data block,
    /// St text (UTF-16), SH label hash, Sj/Sl/Sw/Sb text layout, If image flags. Stored in FRONTEND\FrontB.lzc
    /// (chunk 0x30210: name hash + JDLZ-compressed package) and GLOBAL bundles.
    /// </summary>
    public sealed class FEPackage
    {
        public string Name;
        public readonly List<string> Resources = new List<string>();
        public readonly List<FEObject> Objects = new List<FEObject>();
        public readonly Dictionary<uint, FEObject> ByGuid = new Dictionary<uint, FEObject>();
        /// <summary>Package-level responses (PkgR chunk): pad and global messages handled by the package itself.</summary>
        public readonly List<FEMessageResponse> PackageResponses = new List<FEMessageResponse>();
        /// <summary>Targ chunk (FEMsgTargetList): message id → GUIDs of the objects that respond to it, in package order.</summary>
        public readonly Dictionary<uint, List<uint>> MessageTargets = new Dictionary<uint, List<uint>>();
        public FEMessageResponse FindResponse(uint msg) => PackageResponses.Find(r => r.Msg == msg);

        public static FEPackage Read(byte[] p)
        {
            var pkg = new FEPackage();
            if (p.Length < 8 || Chunks.U32(p, 0) != 0xE76E4546) throw new FormatException("not an FEng package");
            int end = 8 + Chunks.I32(p, 4);
            int order = 0;
            foreach (var (tag, o, s) in Sub(p, 8, Math.Min(end, p.Length)))
            {
                if (tag == "PkHd")
                {
                    int n = o + 24;
                    pkg.Name = Chunks.CString(p, n, o + s - n);
                }
                else if (tag == "ResÌ")
                {
                    foreach (var (t2, o2, s2) in Sub(p, o, o + s))
                        if (t2 == "RsNm")
                            foreach (var name in Encoding.ASCII.GetString(p, o2, s2).Split('\0'))
                                if (name.Length > 0) pkg.Resources.Add(name);
                }
                else if (tag == "ObjÌ")
                {
                    foreach (var (t2, o2, s2) in Sub(p, o, o + s))
                    {
                        if (t2 != "FObê") continue;
                        var obj = new FEObject { Order = order++ };
                        foreach (var (t3, o3, s3) in Sub(p, o2, o2 + s2))
                        {
                            if (t3 == "ObjD") ReadFields(p, o3, o3 + s3, obj);
                            else if (t3 == "Scrp") obj.Scripts.Add(ReadScript(p, o3, o3 + s3));
                            else if (t3 == "MsgR") ReadResponses(p, o3, o3 + s3, obj);
                        }
                        pkg.Objects.Add(obj);
                        pkg.ByGuid[obj.Guid] = obj;
                    }
                }
                else if (tag == "PkgR") ReadCommands(p, o, o + s, pkg.PackageResponses);
                else if (tag == "Targ")
                {
                    // Tc count, then Mt tags: message id followed by the target GUIDs (FEPackageReader ReadMessageTargetListChunk)
                    for (int k = o; k + 4 <= o + s;)
                    {
                        string t = Encoding.ASCII.GetString(p, k, 2);
                        int len = Chunks.U16(p, k + 2);
                        if (t == "Mt" && len >= 4)
                        {
                            var list = new List<uint>();
                            for (int j = 4; j + 4 <= len; j += 4) list.Add(Chunks.U32(p, k + 4 + j));
                            pkg.MessageTargets[Chunks.U32(p, k + 4)] = list;
                        }
                        k += 4 + len;
                    }
                }
            }
            return pkg;
        }

        static IEnumerable<(string tag, int off, int size)> Sub(byte[] p, int o, int end)
        {
            while (o + 8 <= end)
            {
                string tag = Encoding.GetEncoding(28591).GetString(p, o, 4);
                int s = Chunks.I32(p, o + 4);
                if (s < 0 || o + 8 + s > end) yield break;
                yield return (tag, o + 8, s);
                o += 8 + s;
            }
        }

        static void ReadFields(byte[] p, int o, int end, FEObject obj)
        {
            int saAt = -1, saLen = 0;
            while (o + 4 <= end)
            {
                string tag = Encoding.ASCII.GetString(p, o, 2);
                int len = Chunks.U16(p, o + 2);
                int v = o + 4;
                switch (tag)
                {
                    case "Ot": obj.Type = (FEObjectType)Chunks.I32(p, v); break;
                    case "Oh": obj.Hash = Chunks.U32(p, v); break;
                    case "OP":
                        obj.Guid = Chunks.U32(p, v);
                        obj.Flags = Chunks.U32(p, v + 8);
                        obj.Resource = Chunks.I32(p, v + 12);
                        break;
                    case "PA": obj.Parent = Chunks.U32(p, v); break;
                    case "SA":
                        saAt = v; saLen = len;
                        if (len > 84) { var rest = new byte[len - 84]; Buffer.BlockCopy(p, v + 84, rest, 0, rest.Length); obj.Extra["SA+"] = rest; }
                        break;
                    case "St": obj.Text = Encoding.Unicode.GetString(p, v, len).TrimEnd('\0'); break;
                    case "SH": obj.Label = Chunks.U32(p, v); break;
                    case "Sj": obj.Justify = Chunks.I32(p, v); break;
                    case "Sl": obj.Leading = Chunks.I32(p, v); break;
                    case "Sw": obj.MaxWidth = Chunks.I32(p, v); break;
                    case "If": obj.ImageFlags = Chunks.U32(p, v); break;
                    case "Sb":
                        if (len >= 4) obj.StringBufferLength = Chunks.I32(p, v);
                        goto default;   // kept in Extra too (older probes read it there)
                    default:
                        var raw = new byte[len];
                        Buffer.BlockCopy(p, v, raw, 0, len);
                        obj.Extra[tag] = raw;
                        break;
                }
                o = v + len;
            }
            // after the loop: the extension's type (vertex colours are ints) depends on Ot
            if (saAt >= 0) obj.Data = FEObjectData.Read(p, saAt, saLen, obj.Type == FEObjectType.ColoredImage);
        }

        static FEScript ReadScript(byte[] p, int o, int end)
        {
            var sc = new FEScript();
            FETrack cur = null;
            int running = 0;
            while (o + 4 <= end)
            {
                string tag = Encoding.ASCII.GetString(p, o, 2);
                int len = Chunks.U16(p, o + 2);
                int v = o + 4;
                switch (tag)
                {
                    case "Sh":
                        sc.Id = Chunks.U32(p, v);
                        sc.Length = Chunks.I32(p, v + 4);
                        sc.Flags = Chunks.U32(p, v + 8);
                        break;
                    case "Sc": sc.Chain = Chunks.U32(p, v); break;
                    case "FI":
                        // ReadScriptTags (PC 0x5C3610): Length is a 24-bit field (FEKeyTrack.h:72, sign-extended by FEInterpLinear)
                        // and LongOffset defaults to the running sum of the earlier tracks' ParamSize / 4; a "To" tag
                        // overrides it with its first byte. Every shipped track has a To tag (harness feeng9 scripts).
                        cur = new FETrack { ValueType = p[v], ValueSize = p[v + 1], Interp = p[v + 2], Action = p[v + 3], Length = len >= 8 ? (Chunks.I32(p, v + 4) << 8) >> 8 : sc.Length, Offset = running };
                        running += p[v + 1] >> 2;
                        sc.Tracks.Add(cur);
                        break;
                    case "To": if (cur != null) cur.Offset = p[v]; break;
                    case "Kd":
                        if (cur == null) break;
                        int vs = Math.Max(4, cur.ValueSize);
                        int step = 4 + vs;
                        for (int k = v; k + step <= v + len; k += step)
                        {
                            var val = new float[vs / 4];
                            for (int i = 0; i < val.Length; i++)
                                val[i] = cur.ValueType == 6 || cur.ValueType == 1 ? Chunks.I32(p, k + 4 + i * 4) : Chunks.F32(p, k + 4 + i * 4);
                            cur.Keys.Add((Chunks.I32(p, k), val));
                        }
                        break;
                    case "EV":
                        for (int k = v; k + 12 <= v + len; k += 12)
                            sc.Events.Add((Chunks.I32(p, k + 8), Chunks.U32(p, k), Chunks.U32(p, k + 4)));
                        break;
                }
                o = v + len;
            }
            return sc;
        }

        static void ReadResponses(byte[] p, int o, int end, FEObject obj)
        {
            int start = o;
            uint msg = 0;
            while (o + 4 <= end)
            {
                string tag = Encoding.ASCII.GetString(p, o, 2);
                int len = Chunks.U16(p, o + 2);
                int v = o + 4;
                if (tag == "Mi") msg = Chunks.U32(p, v);
                else if (tag == "Ru") obj.Responses.Add((msg, Chunks.U32(p, v), 0));
                o = v + len;
            }
            ReadCommands(p, start, end, obj.MessageResponses);
        }

        /// <summary>Mi message id, MC count, Ri command id, Ru uint param, Rs string param, Rt target (FEPackageReader.cpp:891).</summary>
        static void ReadCommands(byte[] p, int o, int end, List<FEMessageResponse> into)
        {
            FEMessageResponse cur = null;
            FEResponse r = null;
            while (o + 4 <= end)
            {
                string tag = Encoding.ASCII.GetString(p, o, 2);
                int len = Chunks.U16(p, o + 2);
                int v = o + 4;
                if (v + len > end) break;
                switch (tag)
                {
                    case "Mi":
                        uint id = Chunks.U32(p, v);
                        cur = into.Find(x => x.Msg == id);
                        if (cur == null) { cur = new FEMessageResponse { Msg = id }; into.Add(cur); }
                        else cur.List.Clear();
                        r = null;
                        break;
                    case "Ri": if (cur != null) { r = new FEResponse { Id = Chunks.U32(p, v) }; cur.List.Add(r); } break;
                    case "Ru": if (r != null) r.Param = Chunks.U32(p, v); break;
                    case "Rs": if (r != null) r.Str = Encoding.ASCII.GetString(p, v, len).TrimEnd('\0'); break;
                    case "Rt": if (r != null) r.Target = Chunks.U32(p, v); break;
                }
                o = v + len;
            }
        }

        /// <summary>
        /// All packages in a bundle: chunk 0x30210 = name hash + package (raw or JDLZ); chunk 0x30203 = a package with
        /// no hash in front (InGameB's HUD_SingleRace, the single-player HUD).
        /// </summary>
        public static Dictionary<string, FEPackage> ReadBundle(byte[] d)
        {
            var r = new Dictionary<string, FEPackage>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x30210).Concat(Chunks.FindAll(d, 0, d.Length, 0x30203)))
            {
                byte[] p;
                int at = c.DataStart + (c.Id == 0x30203 ? 0 : 4);
                if (Jdlz.IsJdlz(d, at)) p = Jdlz.Decompress(d, at);
                else
                {
                    p = new byte[c.End - at];
                    Buffer.BlockCopy(d, at, p, 0, p.Length);
                }
                try
                {
                    var pkg = Read(p);
                    var key = pkg.Name ?? c.Start.ToString();
                    if (key.EndsWith(".fng", StringComparison.OrdinalIgnoreCase)) key = key.Substring(0, key.Length - 4);
                    r[key] = pkg;
                }
                catch (FormatException) { }
            }
            return r;
        }
    }

    /// <summary>RealFontOld::Glyph (RealFontOld.hpp:47): 16 bytes when the font flags have 0x40000, else 12 [?: no 12-byte font ships].</summary>
    public sealed class FEGlyph
    {
        public int Code, Width, Height, X, Y, OffsetX, OffsetY, Advance;
        /// <summary>+0x8 mAdvanceY, +0xB mNumKern, +0xC mKernIndex (first entry in the kern table for 0x40000 fonts).</summary>
        public int AdvanceY, NumKern, KernIndex;
    }

    /// <summary>
    /// Font chunk 0x30201 (loader @0x591EB0 -> FEngFont ctor 0x585BA0): name[0x100], texture name[0x100] (atlas looked up by
    /// upper-case bStringHash), then a RealFontOld "FNTF" block (RealFontOld.hpp:150): +0x08 u16 version, +0x0A u16 glyph
    /// count, +0x0C flags, +0x10/11 centre, +0x12/13 ascent / descent, +0x14 glyph table offset, +0x18 kern table offset,
    /// +0x24 FONT_HEIGHT state (= FEngFont::Height). Text layout = FEngFont (Frontend/FEngFont.cpp, PC 0x586050-0x59AC60):
    /// see LineWidth / TextHeight / CharWidth and the renderer in Runtime/UI/FEText.cs.
    /// </summary>
    public sealed class FEFont
    {
        public string Name, Texture;
        public int LineHeight;
        public readonly Dictionary<int, FEGlyph> Glyphs = new Dictionary<int, FEGlyph>();
        /// <summary>FNTF +0x0C mFlags (0x40000 = 16-byte glyphs with per-glyph kern runs).</summary>
        public int Flags, Version;
        /// <summary>FEngFont+0x28 fBaselineOffset / +0x2C fLeadingScale from ExtraFontDataTable 0x8F48B8 (by the hash of the
        /// full chunk name, before the language suffix is cut), else 0 / 1.</summary>
        public float BaselineOffset, LeadingScale = 1;
        /// <summary>Hash of the name with a trailing "_&lt;LANGUAGE&gt;" removed (FEngFont::FontHash; the packages' font resource).</summary>
        public uint Hash, RawHash;
        public int KernCount => _kern == null ? 0 : _kern.Length / 4;
        byte[] _kern;                 // kern entries (4 bytes: s16 previous char, s8 kern, u8 char), after the count dword
        readonly List<FEGlyph> _ordered = new List<FEGlyph>();

        /// <summary>ExtraFontDataTable (PC 0x8F48B8, same as FEngFont.cpp:309): {font hash, baseline offset, leading scale}.</summary>
        static readonly (uint hash, float baseline, float leading)[] ExtraFontData =
        {
            (0xDCA5485A, 18f, 2f), (0x833A8678, 22f, 2f), (0xF88A75F9, 18f, 1f), (0x71C777D7, 23f, 1f),
        };

        /// <summary>eLanguages names (PC table 0x8F40F8, 16 entries) cut from the end of a font name with its '_'.</summary>
        static readonly string[] LanguageNames =
            { "ENGLISH", "FRENCH", "GERMAN", "ITALIAN", "SPANISH", "DUTCH", "SWEDISH", "DANISH", "KOREAN", "CHINESE", "JAPANESE", "THAI", "POLISH", "FINNISH", "RUSSIAN", "CZECH" };

        public static FEFont Read(byte[] d, Chunk c)
        {
            var f = new FEFont
            {
                Name = Chunks.CString(d, c.DataStart, 0x100),
                Texture = Chunks.CString(d, c.DataStart + 0x100, 0x100),
            };
            f.RawHash = EaHash.HashUpper(f.Name);
            f.Hash = EaHash.HashUpper(StripLanguage(f.Name));
            foreach (var e in ExtraFontData)
                if (e.hash == f.RawHash) { f.BaselineOffset = e.baseline; f.LeadingScale = e.leading; }
            int b = c.DataStart + 0x200;
            if (b + 0x80 > c.End || Chunks.U32(d, b) != 0x46544E46) return f; // "FNTF"
            f.Version = Chunks.U16(d, b + 8);
            int count = Chunks.U16(d, b + 10);
            f.Flags = Chunks.I32(d, b + 0x0C);
            int table = b + Chunks.I32(d, b + 0x14);
            int stride = (f.Flags & 0x40000) != 0 ? 16 : 12;
            f.LineHeight = Chunks.I32(d, b + 0x24);
            for (int i = 0; i < count && table + i * stride + stride <= c.End; i++)
            {
                int g = table + i * stride;
                var gl = new FEGlyph
                {
                    Code = Chunks.U16(d, g), Width = d[g + 2], Height = d[g + 3],
                    X = Chunks.U16(d, g + 4), Y = Chunks.U16(d, g + 6),
                    AdvanceY = (sbyte)d[g + 8], OffsetX = (sbyte)d[g + 9], OffsetY = (sbyte)d[g + 10], NumKern = d[g + 11],
                    KernIndex = stride == 16 ? Chunks.U16(d, g + 12) : 0,
                    Advance = stride == 16 ? (short)Chunks.U16(d, g + 14) : 0,
                };
                f.Glyphs[gl.Code] = gl;
                f._ordered.Add(gl);
            }
            int kt = Chunks.I32(d, b + 0x18);
            if (kt > 0 && b + kt + 4 <= c.End)
            {
                int n = Chunks.I32(d, b + kt);
                int start = b + kt + 4;
                int bytes = Math.Max(0, Math.Min(c.End - start, (f.Flags & 0x40000) != 0 ? (c.End - start) : n * 4));
                bytes -= bytes % 4;
                f._kern = new byte[bytes];
                Buffer.BlockCopy(d, start, f._kern, 0, bytes);
                f._kernCount = n;
            }
            return f;
        }

        int _kernCount;

        static string StripLanguage(string name)
        {
            // FEngFont ctor 0x585BA0: the first position n where name[n..] equals a language name and name[n-1] == '_'
            for (int n = 1; n < name.Length; n++)
                if (name[n - 1] == '_')
                    foreach (var l in LanguageNames)
                        if (string.Equals(name.Substring(n), l, StringComparison.OrdinalIgnoreCase)) return name.Substring(0, n - 1);
            return name;
        }

        public static List<FEFont> ReadAll(byte[] d, int start, int end)
        {
            var r = new List<FEFont>();
            foreach (var c in Chunks.FindAll(d, start, end, 0x30201)) r.Add(Read(d, c));
            return r;
        }

        // ---- FEngFont text metrics (PC 0x5722C0 ConvertCharacter, 0x586050 GetCharacterWidth, 0x586130 GetLineWidth,
        //      0x586210 GetTextWidth, 0x591F60 GetNextWordWidth, 0x591FD0 GetTextHeight, 0x572300 / 0x572330 offsets) ----

        /// <summary>IsNewlineChar (FEngFont.cpp:43): '\n' and '^'.</summary>
        public static bool IsNewline(int c) => c == '\n' || c == '^';

        /// <summary>FEngFont::ConvertCharacter: 0xFF80.. → low byte, 0x99 → ™, 0x9C → œ, 0xA0 → space.</summary>
        public static int ConvertCharacter(int c)
        {
            c &= 0xFFFF;
            if (c >= 0xFF80) c &= 0xFF;
            if (c == 0x99) return 0x2122;
            if (c == 0x9C) return 0x153;
            if (c == 0xA0) return 0x20;
            return c;
        }

        /// <summary>RealFontOld::Font::GetGlyph (0x569BF0): table[c − 0x20] when its code matches, else the binary search.</summary>
        public FEGlyph Glyph(int unicode)
        {
            int i = unicode - 0x20;
            if (i >= 0 && i < _ordered.Count && _ordered[i].Code == unicode) return _ordered[i];
            return Glyphs.TryGetValue(unicode, out var g) ? g : null;
        }

        /// <summary>RealFontOld::Font::GetKern (0x7E89D0): 0 unless the glyph has kern pairs; flags 0x40000 = the glyph's
        /// own run (mKernIndex, mNumKern), else a linear search of the whole table for (prev, glyph).</summary>
        public int Kern(FEGlyph g, int prev)
        {
            if (g == null || g.NumKern == 0 || _kern == null) return 0;
            if ((Flags & 0x40000) == 0)
            {
                for (int i = 0; i < _kernCount && i * 4 + 4 <= _kern.Length; i++)
                    if ((short)BitConverter.ToUInt16(_kern, i * 4) == (short)prev && _kern[i * 4 + 3] == (g.Code & 0xFFFF))
                        return (sbyte)_kern[i * 4 + 2];
                return 0;
            }
            for (int i = 0; i < g.NumKern; i++)
            {
                int at = (g.KernIndex + i) * 4;
                if (at + 4 > _kern.Length) break;
                if ((short)BitConverter.ToUInt16(_kern, at) == (short)prev) return (sbyte)_kern[at + 2];
            }
            return 0;
        }

        /// <summary>FEngFont::GetCharacterWidth(c, prev, flags): advance + kern; newline chars (unless flags 0x20) and '\r'
        /// are 0; flags 0x80 turns a no-break space into a plain space without kerning.</summary>
        public float CharWidth(int c, int prev, uint flags)
        {
            if ((flags & 0x20) == 0 && IsNewline(c)) return 0;
            if (c == '\r') return 0;
            int u = ConvertCharacter(c);
            if ((flags & 0x80) != 0 && u == 0xA0) { prev = 0; u = 0x20; }
            var g = Glyph(u);
            if (g == null) return 0;
            float w = 0;
            if ((short)prev != 0) w += Kern(g, prev);
            return w + g.Advance;
        }

        /// <summary>FEngFont::GetLineWidth: up to the first newline; with word wrap and a max width, stops at the last space
        /// once the line is too wide (the overflowing width when the line has no space).</summary>
        public float LineWidth(string s, int start, uint flags, uint maxWidth = 0, bool wordWrap = false)
        {
            float last = 0, w = 0;
            if (s == null) return 0;
            for (int i = start, k = 0; i < s.Length && s[i] != 0; i++, k++)
            {
                int c = s[i];
                if (IsNewline(c)) break;
                if (c == ' ') last = w;
                w += CharWidth(c, k == 0 ? 0 : s[i - 1], flags);
                if (maxWidth != 0 && maxWidth < w && wordWrap)
                {
                    if (last > 0) w = last;
                    break;
                }
            }
            return w;
        }

        /// <summary>FEngFont::GetTextWidth: the widest line (flags 0x20: newlines are not line breaks).</summary>
        public float TextWidth(string s, uint flags)
        {
            float w = LineWidth(s, 0, 0);
            for (int i = 0; i < s.Length; i++)
            {
                if ((flags & 0x20) == 0 && IsNewline(s[i])) continue;
                float n = LineWidth(s, i + 1, 0);
                if (n > w) w = n;
            }
            return w;
        }

        /// <summary>FEngFont::GetNextWordWidth(at): from s[at] to the next space / newline / end (flags 0x200: 0).</summary>
        public float NextWordWidth(string s, int at, uint flags)
        {
            float w = 0;
            if ((flags & 0x200) != 0) return 0;
            for (int i = at; i < s.Length; i++)
            {
                w += CharWidth(s[i], i > 0 ? s[i - 1] : 0, flags);
                int nx = i + 1 < s.Length ? s[i + 1] : 0;
                if (nx == ' ' || nx == 0 || IsNewline(nx)) break;
            }
            return w;
        }

        /// <summary>FEngFont::GetTextHeight(s, leading, flags, maxWidth, wordWrap) (PC 0x591FD0).</summary>
        public float TextHeight(string s, int leading, uint flags, uint maxWidth, bool wordWrap)
        {
            float h = 0, lineW = 0;
            if (s == null) return 0;
            bool lastNotReturn = true, newLine = false;
            int prev = 0;
            for (int i = 0; i < s.Length && s[i] != 0; i++)
            {
                int c = s[i];
                if (IsNewline(c)) { lastNotReturn = false; lineW = 0; h += leading + LineHeight; newLine = false; }
                else if (c != '\r')
                {
                    if (Glyph(c & 0xFF) != null) lastNotReturn = true;
                    if (wordWrap && maxWidth != 0)
                    {
                        if (c == ' ' && maxWidth < lineW + NextWordWidth(s, i, flags)) newLine = true;
                        lineW += CharWidth(c, prev, flags);
                    }
                    if (newLine) { newLine = false; lastNotReturn = false; lineW = 0; h += leading + LineHeight; }
                }
                prev = c;
            }
            if (lastNotReturn) h += LineHeight;
            return h;
        }

        /// <summary>FEngFont::CalculateXOffset: justify bit 0 = centre, bit 1 = right.</summary>
        public static float XOffset(int justify, float lineWidth) => (justify & 1) != 0 ? lineWidth * -0.5f : (justify & 2) != 0 ? -lineWidth : 0;
        /// <summary>FEngFont::CalculateYOffset: justify bit 2 = vertical centre, bit 3 = bottom.</summary>
        public static float YOffset(int justify, float height) => (justify & 4) != 0 ? height * -0.5f : (justify & 8) != 0 ? -height : 0;
    }

    /// <summary>LANGUAGES\*.bin chunk 0x39000: {header size, count, hash table offset, string offset}, sorted {hash, offset} pairs.</summary>
    public sealed class StringTable
    {
        readonly Dictionary<uint, string> _strings = new Dictionary<uint, string>();
        public int Count => _strings.Count;

        public static StringTable Read(byte[] d)
        {
            var t = new StringTable();
            foreach (var c in Chunks.Read(d, 0, d.Length))
            {
                if (c.Id != 0x39000) continue;
                int b = c.DataStart;
                int n = Chunks.I32(d, b + 4), table = b + Chunks.I32(d, b + 8), strings = b + Chunks.I32(d, b + 12);
                var enc = Encoding.GetEncoding(28591);
                var hist = WideCharHistogram.Read(d, b, c.End);   // LoaderLanguage 0x57E960 (Core/LangLocalize.cs)
                for (int i = 0; i < n; i++)
                {
                    uint h = Chunks.U32(d, table + i * 8);
                    int s = strings + Chunks.I32(d, table + i * 8 + 4);
                    int e = s;
                    while (e < c.End && d[e] != 0) e++;
                    t._strings[h] = hist != null ? hist.Unpack(d, s, e, out _) : enc.GetString(d, s, e - s);   // GetLocalizedWideString 0x57E920
                }
            }
            return t;
        }

        public string Get(uint hash) => _strings.TryGetValue(hash, out var s) ? s : null;
        public string Get(string label) => Get(EaHash.HashUpper(label));
        public bool TryGet(uint hash, out string s) => _strings.TryGetValue(hash, out s);
    }
}
