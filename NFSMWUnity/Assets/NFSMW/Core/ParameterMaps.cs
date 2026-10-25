using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// World parameter maps (World/ParameterMaps.cpp; PC 0x5DF1F0-0x609903), chunk 0x8003B600 in TRACKS\L2RA.BUN.
    /// Each nested 0x8003B601 layer is a quadtree over world X/Y that picks one "parameter set" (a small struct of
    /// float/int fields) per position:
    ///   0x3B602 header (0x24): u32 NameHash (bStringHash of the layer name), f32 QuadLeft, QuadTop, QuadRight,
    ///           QuadBottom, i32 NumberOfQuadNodes, NumberOfParameterSets, SizeOfParameterSet, NumberOfFields
    ///   0x3B603 i32 FieldTypes[NumberOfFields] (0 float, 1 int)
    ///   0x3B604 i32 FieldOffsets[NumberOfFields] (bytes into a set)
    ///   0x3B605 parameter data (NumberOfParameterSets * SizeOfParameterSet)
    ///   0x3B607 quad nodes, 4 x u8 (Child0..3; Child0 == 0 marks a leaf whose set index is Child1)
    ///   0x3B608 quad nodes, 4 x u16 (same meaning)
    /// L2RA layers (2026-10-24): Ambience, Reverb, Speech, Screen Tint SunRise/Midday (+2 unnamed tints), Clouds, Rain,
    /// Wind, Normal Fog, Rain Fog, Fog Blend Distance and one unnamed int layer (0xFA9F8DAA).
    /// Readers in the exe (PC code 0x73D560-0x75EBD0; the IDA range 0x5DF1F0.. is GRaceParameters): Rain::UpdateAndRender
    /// (Clouds 0x753AC0 / Rain 0x7539D0), Rain::Wind 0x753B50 (Wind field 1), FogQuery::CalculateFogInfo 0x753D90 (Normal
    /// Fog / Rain Fog / Fog Blend Distance; result unread), SFXCTL_Tunnel::UpdateCityVerb 0x4BEA70 (Reverb fields 0, 2),
    /// SFXObj_PFEATrax::UpdateAmbience 0x4DF6D0 (Ambience field 0), SoundAI::SyncPlayers 0x71D080 (Speech field 1). The four
    /// Screen Tint layers and FA9F8DAA have no reader (their accessors are only constructed).
    /// (Speech), SFXObj_Pathfinder (Ambience). See NFSMW_RE/specs/sky_lighting.md.
    /// </summary>
    public sealed class ParameterMapLayer
    {
        public uint NameHash;
        public float QuadLeft, QuadTop, QuadRight, QuadBottom;
        public int NumberOfQuadNodes, NumberOfParameterSets, SizeOfParameterSet, NumberOfFields;
        public int[] FieldTypes = Array.Empty<int>();
        public int[] FieldOffsets = Array.Empty<int>();
        public byte[] ParameterData;
        public byte[] QuadData8;      // 4 bytes per node
        public ushort[] QuadData16;   // 4 shorts per node
        public bool HasHeader;

        /// <summary>ParameterMapLayer::Load (ParameterMaps.cpp:18): the size checks reject malformed sub-chunks.</summary>
        public static ParameterMapLayer Load(byte[] d, Chunk layer)
        {
            var l = new ParameterMapLayer();
            foreach (var c in Chunks.Children(d, layer))
            {
                int o = c.DataStart;
                switch (c.Id)
                {
                    case 0x3B602:
                        l.NameHash = Chunks.U32(d, o);
                        l.QuadLeft = Chunks.F32(d, o + 4);
                        l.QuadTop = Chunks.F32(d, o + 8);
                        l.QuadRight = Chunks.F32(d, o + 12);
                        l.QuadBottom = Chunks.F32(d, o + 16);
                        l.NumberOfQuadNodes = Chunks.I32(d, o + 20);
                        l.NumberOfParameterSets = Chunks.I32(d, o + 24);
                        l.SizeOfParameterSet = Chunks.I32(d, o + 28);
                        l.NumberOfFields = Chunks.I32(d, o + 32);
                        l.HasHeader = true;
                        l.FieldTypes = Array.Empty<int>();
                        l.FieldOffsets = Array.Empty<int>();
                        l.ParameterData = null;
                        l.QuadData8 = null;
                        l.QuadData16 = null;
                        break;
                    case 0x3B603:
                        if (l.HasHeader && c.Size == l.NumberOfFields * 4)
                        {
                            l.FieldTypes = new int[l.NumberOfFields];
                            for (int i = 0; i < l.NumberOfFields; i++) l.FieldTypes[i] = Chunks.I32(d, o + i * 4);
                        }
                        break;
                    case 0x3B604:
                        if (l.HasHeader && c.Size == l.NumberOfFields * 4)
                        {
                            l.FieldOffsets = new int[l.NumberOfFields];
                            for (int i = 0; i < l.NumberOfFields; i++) l.FieldOffsets[i] = Chunks.I32(d, o + i * 4);
                        }
                        break;
                    case 0x3B605:
                        if (l.HasHeader && l.FieldTypes.Length > 0 && l.FieldOffsets.Length > 0
                            && c.Size == l.NumberOfParameterSets * l.SizeOfParameterSet)
                        {
                            l.ParameterData = new byte[c.Size];
                            Buffer.BlockCopy(d, o, l.ParameterData, 0, c.Size);
                        }
                        break;
                    case 0x3B607:
                        if (l.HasHeader && c.Size == l.NumberOfQuadNodes * 4)
                        {
                            l.QuadData8 = new byte[c.Size];
                            Buffer.BlockCopy(d, o, l.QuadData8, 0, c.Size);
                        }
                        break;
                    case 0x3B608:
                        if (l.HasHeader && c.Size == l.NumberOfQuadNodes * 8)
                        {
                            l.QuadData16 = new ushort[l.NumberOfQuadNodes * 4];
                            for (int i = 0; i < l.QuadData16.Length; i++) l.QuadData16[i] = Chunks.U16(d, o + i * 2);
                        }
                        break;
                }
            }
            return l;
        }

        /// <summary>
        /// GetParameterSetIndexFromQuadData8/16 (ParameterMaps.cpp:197/240). The out-of-bounds test is the exe's
        /// (x &lt; left AND x &gt; right ...), which never fires, so positions outside the map walk the tree anyway.
        /// x, y are game-space world X and Y.
        /// </summary>
        public int GetParameterSetIndex(float x, float y)
        {
            if (QuadData8 == null && QuadData16 == null) return 0;
            float left = QuadLeft, top = QuadTop, right = QuadRight, bottom = QuadBottom;
            int current = 0;
            if (x < left && x > right && y < top && y > bottom) return 0;
            for (int guard = 0; guard < 64; guard++)   // [ours] guard against a cyclic tree; the exe loops forever
            {
                int c0, c1, c2, c3;
                if (QuadData8 != null)
                {
                    if (current * 4 + 3 >= QuadData8.Length) return 0;
                    c0 = QuadData8[current * 4]; c1 = QuadData8[current * 4 + 1];
                    c2 = QuadData8[current * 4 + 2]; c3 = QuadData8[current * 4 + 3];
                }
                else
                {
                    if (current * 4 + 3 >= QuadData16.Length) return 0;
                    c0 = QuadData16[current * 4]; c1 = QuadData16[current * 4 + 1];
                    c2 = QuadData16[current * 4 + 2]; c3 = QuadData16[current * 4 + 3];
                }
                if (c0 == 0) return c1;   // IsLeave(): Child0 == 0, GetData() = Child1
                float cx = (left + right) * 0.5f;
                float cy = (top + bottom) * 0.5f;
                if (x < cx)
                {
                    if (y < cy) { right = cx; bottom = cy; current = c0; }
                    else { right = cx; top = cy; current = c1; }
                }
                else
                {
                    if (y < cy) { left = cx; bottom = cy; current = c2; }
                    else { left = cx; top = cy; current = c3; }
                }
            }
            return 0;
        }

        /// <summary>Byte offset of the set picked at (x, y) into ParameterData (ParameterMapLayer::GetParameterData).</summary>
        public int GetParameterData(float x, float y) => SizeOfParameterSet * GetParameterSetIndex(x, y);

        public bool Valid => HasHeader && ParameterData != null && FieldOffsets.Length == NumberOfFields;

        public float GetFloat(int field, int dataOffset)
        {
            if (!Valid || field < 0 || field >= NumberOfFields) return 0f;
            int o = dataOffset + FieldOffsets[field];
            return o >= 0 && o + 4 <= ParameterData.Length ? BitConverter.ToSingle(ParameterData, o) : 0f;
        }

        public int GetInt(int field, int dataOffset)
        {
            if (!Valid || field < 0 || field >= NumberOfFields) return 0;
            int o = dataOffset + FieldOffsets[field];
            return o >= 0 && o + 4 <= ParameterData.Length ? BitConverter.ToInt32(ParameterData, o) : 0;
        }

        public float GetFloatAt(int field, float x, float y) => GetFloat(field, GetParameterData(x, y));
        public int GetIntAt(int field, float x, float y) => GetInt(field, GetParameterData(x, y));
    }

    /// <summary>ParameterMapsManager + LoaderParameterMaps (ParameterMaps.cpp:470/497): every layer of a bundle.</summary>
    public sealed class ParameterMaps
    {
        public const uint ChunkId = 0x8003B600, LayerId = 0x8003B601;
        public readonly List<ParameterMapLayer> Layers = new List<ParameterMapLayer>();

        public static ParameterMaps Load(byte[] d)
        {
            var maps = new ParameterMaps();
            if (d == null) return maps;
            foreach (var c in Chunks.FindAll(d, 0, d.Length, ChunkId))
                foreach (var l in Chunks.Children(d, c))
                    if (l.Id == LayerId) maps.Layers.Add(ParameterMapLayer.Load(d, l));
            return maps;
        }

        /// <summary>GetDataForLayer: the first layer whose NameHash matches bStringHash(name).</summary>
        public ParameterMapLayer Layer(string name) => Layer(EaHash.Hash(name));

        public ParameterMapLayer Layer(uint nameHash)
        {
            foreach (var l in Layers)
                if (l.NameHash == nameHash) return l;
            return null;
        }
    }

    /// <summary>ParameterAccessor (ParameterMaps.cpp:330): binds to a layer by name and captures the set at a position.</summary>
    public class ParameterAccessor
    {
        public ParameterMapLayer Layer { get; protected set; }
        protected int CurrentData = -1;          // byte offset into Layer.ParameterData, -1 = none
        protected byte[] CurrentBuffer;          // where CurrentData points (layer data or the blend buffer)
        public readonly string LayerName;

        public ParameterAccessor(string layerName) => LayerName = layerName;

        public virtual void SetLayer(ParameterMapLayer layer)
        {
            ClearData();
            Layer = layer != null && layer.Valid ? layer : null;
            if (Layer != null) SetUpForNewLayer();
        }

        /// <summary>Re-attaches by name (the auto-accessor list of LoaderParameterMaps).</summary>
        public void Attach(ParameterMaps maps) => SetLayer(maps?.Layer(LayerName));

        public virtual void CaptureData(float x, float y)
        {
            if (Layer == null) { CurrentData = -1; CurrentBuffer = null; return; }
            CurrentData = Layer.GetParameterData(x, y);
            CurrentBuffer = Layer.ParameterData;
        }

        public virtual void ClearData() { CurrentData = -1; CurrentBuffer = null; }

        public float GetDataFloat(int field)
        {
            if (Layer == null || CurrentBuffer == null || field < 0 || field >= Layer.NumberOfFields) return 0f;
            int o = CurrentData + Layer.FieldOffsets[field];
            return o >= 0 && o + 4 <= CurrentBuffer.Length ? BitConverter.ToSingle(CurrentBuffer, o) : 0f;
        }

        public int GetDataInt(int field)
        {
            if (Layer == null || CurrentBuffer == null || field < 0 || field >= Layer.NumberOfFields) return 0;
            int o = CurrentData + Layer.FieldOffsets[field];
            return o >= 0 && o + 4 <= CurrentBuffer.Length ? BitConverter.ToInt32(CurrentBuffer, o) : 0;
        }

        protected virtual void SetUpForNewLayer() { }
    }

    /// <summary>ParameterAccessorBlend (ParameterMaps.cpp:385): blends each field from the last captured set by ratio.</summary>
    public class ParameterAccessorBlend : ParameterAccessor
    {
        byte[] _last;
        bool _haveLast;

        public ParameterAccessorBlend(string layerName) : base(layerName) { }

        public void CaptureData(float x, float y, float ratio)
        {
            if (Layer == null || _last == null) { CurrentData = -1; CurrentBuffer = null; return; }
            int cur = Layer.GetParameterData(x, y);
            var src = Layer.ParameterData;
            if (!_haveLast)
            {
                Buffer.BlockCopy(src, cur, _last, 0, Math.Min(Layer.SizeOfParameterSet, src.Length - cur));
                _haveLast = true;
            }
            else
            {
                for (int i = 0; i < Layer.NumberOfFields; i++)
                {
                    int fo = Layer.FieldOffsets[i];
                    if (fo < 0 || fo + 4 > _last.Length || cur + fo + 4 > src.Length) continue;
                    if (Layer.FieldTypes[i] == 0)
                    {
                        float c = BitConverter.ToSingle(src, cur + fo), l = BitConverter.ToSingle(_last, fo);
                        WriteF(_last, fo, c * ratio + l * (1f - ratio));
                    }
                    else if (Layer.FieldTypes[i] == 1)
                    {
                        float c = BitConverter.ToInt32(src, cur + fo), l = BitConverter.ToInt32(_last, fo);
                        WriteI(_last, fo, (int)(c * ratio + l * (1f - ratio)));
                    }
                }
            }
            CurrentData = 0;
            CurrentBuffer = _last;
        }

        public override void CaptureData(float x, float y) { }   // the exe's two-argument overload is empty

        public override void ClearData()
        {
            _last = null;
            _haveLast = false;
            base.ClearData();
        }

        protected override void SetUpForNewLayer()
        {
            if (Layer != null && Layer.SizeOfParameterSet > 0) _last = new byte[Layer.SizeOfParameterSet];
        }

        static void WriteF(byte[] b, int o, float v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, o, 4);
        static void WriteI(byte[] b, int o, int v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, o, 4);
    }

    /// <summary>ParameterAccessorBlendByDistance (ParameterMaps.cpp:443): ratio = distance moved / full blend distance.</summary>
    public sealed class ParameterAccessorBlendByDistance : ParameterAccessorBlend
    {
        float _lastX, _lastY;
        bool _haveLastPosition;

        public ParameterAccessorBlendByDistance(string layerName) : base(layerName) { }

        /// <summary>The exe's CaptureData(x, y, full_blend_distance) overload (renamed: same C# signature as the ratio one).</summary>
        public void CaptureByDistance(float x, float y, float fullBlendDistance)
        {
            float ratio = 1f;
            if (_haveLastPosition && fullBlendDistance != 0f)
            {
                float dx = x - _lastX, dy = y - _lastY;
                float travelled = (float)Math.Sqrt(dx * dx + dy * dy);
                if (travelled < fullBlendDistance) ratio = travelled / fullBlendDistance;
            }
            CaptureData(x, y, ratio);
            _lastX = x;
            _lastY = y;
            _haveLastPosition = true;
        }

        protected override void SetUpForNewLayer()
        {
            _lastX = _lastY = 0f;
            _haveLastPosition = false;
            base.SetUpForNewLayer();
        }
    }
}
