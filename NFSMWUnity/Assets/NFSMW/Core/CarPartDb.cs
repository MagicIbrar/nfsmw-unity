using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NfsMw.Core
{
    /// <summary>
    /// The car part database (GlobalB.lzc nested chunk 0x80034602 BCHUNK_SPEED_CARPART_PACK_HEADER, LoaderCarInfo,
    /// World/CarInfo.cpp:300-440). Children in order: pack header (CarPartPack: Version @0x8 … NumParts @0x38, the
    /// bTNode Next/Prev in the first 8 bytes), string table, attribute-table table (int16 count + int16 indices, the
    /// part's AttributeTableOffset counts int16s), attributes (8 bytes {u32 NameHash, u32/i32/f32 param}), model tables
    /// (0x18 bytes), car-type name hashes (u32), parts (0xE bytes, CarInfo.hpp:212-288):
    /// {u16 PartNameHashBot, u16 PartNameHashTop, i8 PartID (CAR_PART_ID), u8 GroupNumber_UpgradeLevel (group = low 5
    /// bits, upgrade level = bits 5..7), i8 BaseModelNameHashSelector, u8 CarTypeNameHashIndex, u16 NameOffset (×4 into
    /// the string table), u16 AttributeTableOffset, u16 ModelNameHashTableOffset}.
    /// Also reads the slot → car-type lookup (chunk 0x34607, 139×2 u32 then 0x10-byte overrides; 0xFFFFFFFF = the car's
    /// own name hash, GetTypesFromSlot CarInfo.cpp:789) and CarTypeInfo (chunk 0x34600, 0xD0 bytes per type, aligned 16)
    /// for the rim-size range. Lookups follow NewGetCarPart (CarInfo.cpp:661): both slot types in order, parts with
    /// the slot's CAR_PART_ID (CarPartSlotMap) and that car-type hash.
    /// </summary>
    public sealed class CarPartDb
    {
        public sealed class Part
        {
            public int Index;
            public uint NameHash;
            public int PartId;
            public int Level, Group;
            public int BaseModelSelector;
            public uint CarTypeHash;
            public string Name;
            /// <summary>Applied attributes in table order (name hash, raw 32-bit param).</summary>
            public (uint hash, uint param)[] Attributes = Array.Empty<(uint, uint)>();
            /// <summary>ModelNameHashTableOffset (0xFFFF = none).</summary>
            public int ModelTable = 0xFFFF;
            internal CarPartDb Db;

            /// <summary>
            /// CarPart::GetModelNameHash (CarInfo.cpp:587): base = -1 / the car type hash / the brand hash by
            /// BaseModelNameHashSelector 0/1/2, then the templated middle string, model name and "_A".."_E" chained
            /// with bStringHash; a non-templated table holds the hash itself. 0 = no model at that LOD.
            /// </summary>
            public uint ModelHash(int lod = 0, uint? carTypeHash = null)
            {
                if (ModelTable == 0xFFFF || ModelTable >= Db._models.Count) return 0;
                var m = Db._models[ModelTable];
                uint name = m.names[lod];
                if (name == 0xFFFFFFFF) return 0;
                if (!m.templated) return name;
                uint h = BaseModelSelector == 0 ? 0xFFFFFFFF : BaseModelSelector == 1 ? (carTypeHash ?? CarTypeHash) : Brand;
                if (m.middle != 0xFFFF) h = EaHash.Hash(Db.String(m.middle), h);
                h = EaHash.Hash(Db.String((int)name) ?? "", h);
                return EaHash.Hash("_" + (char)('A' + lod), h);
            }

            /// <summary>The templated model name without its base (e.g. "_STYLE06_HOOD"), or null.</summary>
            public string ModelSuffix(int lod = 0)
            {
                if (ModelTable == 0xFFFF || ModelTable >= Db._models.Count) return null;
                var m = Db._models[ModelTable];
                if (!m.templated || m.names[lod] == 0xFFFFFFFF) return null;
                return (m.middle != 0xFFFF ? Db.String(m.middle) : "") + Db.String((int)m.names[lod]) + "_" + (char)('A' + lod);
            }

            public bool Has(uint hash) => Attributes.Any(a => a.hash == hash);
            public uint U(uint hash, uint fallback = 0) { foreach (var a in Attributes) if (a.hash == hash) return a.param; return fallback; }
            public int I(uint hash, int fallback = 0) => Has(hash) ? (int)U(hash) : fallback;
            public float F(uint hash, float fallback = 0) => Has(hash) ? BitConverter.ToSingle(BitConverter.GetBytes(U(hash)), 0) : fallback;
            /// <summary>A string attribute: its param is a ×4 offset into the string table (GetAppliedAttributeString).</summary>
            public string S(uint hash) => Has(hash) ? Db.String((int)U(hash)) : null;

            public uint Brand => U(Attr.BRAND_NAME);
            public int InnerRadius => (sbyte)(byte)I(Attr.INNER_RADIUS);
            public int OuterRadius => (sbyte)(byte)I(Attr.OUTER_RADIUS);
            public bool Carbon => I(Attr.CARBONFIBRE) != 0;
            public uint TextureHash => U(Attr.TEXTURE_NAME);
            public (int r, int g, int b) Rgb => (I(Attr.RED), I(Attr.GREEN), I(Attr.BLUE));
            public override string ToString() => $"{Name} id {PartId} lvl {Level} grp {Group}";
        }

        /// <summary>bStringHash of the attribute names used by the shop (Generated/Hash.hpp STRINGHASH_*).</summary>
        public static class Attr
        {
            public static readonly uint BRAND_NAME = EaHash.Hash("BRAND_NAME"), INNER_RADIUS = EaHash.Hash("INNER_RADIUS"),
                OUTER_RADIUS = EaHash.Hash("OUTER_RADIUS"), CARBONFIBRE = EaHash.Hash("CARBONFIBRE"), TEXTURE_NAME = EaHash.Hash("TEXTURE_NAME"),
                RED = EaHash.Hash("RED"), GREEN = EaHash.Hash("GREEN"), BLUE = EaHash.Hash("BLUE"), NAME_OFFSET = EaHash.Hash("NAME_OFFSET"),
                LANGUAGEHASH = EaHash.Hash("LANGUAGEHASH"), SPOKE_COUNT = EaHash.Hash("SPOKE_COUNT"), KIT_NUMBER = EaHash.Hash("KITNUMBER"),
                PART_NAME_OFFSETS = EaHash.Hash("PART_NAME_OFFSETS"), STOCK = EaHash.Hash("STOCK"), GLOSS = EaHash.Hash("GLOSS"),
                METAL = EaHash.Hash("METAL"), PEARL = EaHash.Hash("PEARL"), SPECIAL = EaHash.Hash("SPECIAL");
        }

        /// <summary>CarPartSlotMap (CarInfo.cpp:46): CAR_SLOT_ID → CAR_PART_ID for the slots the shop uses.</summary>
        public static int PartIdForSlot(int slot)
        {
            if (slot <= 65) return slot;            // 0..65 map one to one (model slots)
            if (slot == 66 || slot == 67) return 67; // FRONT/REAR_WHEEL → WHEEL
            if (slot >= 68 && slot <= 75) return slot;
            switch (slot)
            {
                case 76: return 76;                  // BASE_PAINT → PAINT
                case 77: return 79;                  // VINYL_LAYER0 → VINYL
                case 78: return 78;                  // PAINT_RIM → RIM_PAINT
                case 131: return 81;                 // WINDOW_TINT
                case 132: return 82;                 // CUSTOM_HUD
                case 133: case 134: case 135: return 83; // HUD colours → CUSTOM_HUD_PAINT
                case 136: return 84;
                case 137: return 85;
                case 138: return 86;
            }
            if (slot >= 79 && slot <= 82) return 77; // VINYL_COLOURx → VINYL_PAINT
            if (slot >= 83 && slot <= 130) return 80; // decal textures
            return -1;
        }

        public sealed class CarType
        {
            public string Name, BaseModel, Manufacturer;
            public uint NameHash;
            public int WheelOuterRadius, WheelInnerMin, WheelInnerMax;
            public uint DefaultBasePaint;
        }

        public readonly List<Part> Parts = new List<Part>();
        public uint[] TypeNames = Array.Empty<uint>();
        public readonly Dictionary<string, CarType> CarTypes = new Dictionary<string, CarType>(StringComparer.OrdinalIgnoreCase);
        uint[] _slotTypes = Array.Empty<uint>();
        readonly List<(uint car, uint slot, uint t0, uint t1)> _overrides = new List<(uint, uint, uint, uint)>();
        byte[] _strings = Array.Empty<byte>();
        readonly List<(bool templated, int middle, uint[] names)> _models = new List<(bool, int, uint[])>();
        public int Version;

        public string String(int offset4)
        {
            int o = offset4 * 4;
            return o >= 0 && o < _strings.Length ? Chunks.CString(_strings, o, _strings.Length - o) : null;
        }

        public static CarPartDb Load(string gameRoot)
        {
            var path = Path.Combine(gameRoot, "GLOBAL", "GlobalB.lzc");
            return File.Exists(path) ? Read(GameFiles.ReadBundle(path)) : new CarPartDb();
        }

        public static CarPartDb Read(byte[] d)
        {
            var db = new CarPartDb();
            foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x00034607)) db.ReadSlotTypes(d, c);
            foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x00034600)) db.ReadCarTypes(d, c);
            foreach (var c in Chunks.FindAll(d, 0, d.Length, 0x80034602)) db.ReadPack(d, c);
            return db;
        }

        void ReadSlotTypes(byte[] d, Chunk c)
        {
            int n = Math.Min(278, c.Size / 4);
            _slotTypes = new uint[n];
            for (int i = 0; i < n; i++) _slotTypes[i] = Chunks.U32(d, c.DataStart + i * 4);
            for (int o = c.DataStart + 278 * 4; o + 16 <= c.End; o += 16)
                _overrides.Add((Chunks.U32(d, o), Chunks.U32(d, o + 4), Chunks.U32(d, o + 8), Chunks.U32(d, o + 12)));
        }

        void ReadCarTypes(byte[] d, Chunk c)
        {
            int start = c.DataStart;
            while (start < c.End && Chunks.U32(d, start) == 0x11111111) start += 4;   // alignment padding
            start = c.DataStart + ((start - c.DataStart) & ~3);
            if ((start & 15) != 0 && Chunks.U32(d, start) == 0) start = Chunks.Align(start, 16);
            for (int o = start; o + 0xD0 <= c.End; o += 0xD0)
            {
                var t = new CarType
                {
                    Name = Chunks.CString(d, o, 16), BaseModel = Chunks.CString(d, o + 0x10, 16), Manufacturer = Chunks.CString(d, o + 0x40, 16),
                    NameHash = Chunks.U32(d, o + 0x50), WheelOuterRadius = (sbyte)d[o + 0x5C], WheelInnerMin = (sbyte)d[o + 0x5D],
                    WheelInnerMax = (sbyte)d[o + 0x5E], DefaultBasePaint = Chunks.U32(d, o + 0xCC),
                };
                if (t.Name.Length > 0 && !CarTypes.ContainsKey(t.Name)) CarTypes[t.Name] = t;
            }
        }

        void ReadPack(byte[] d, Chunk header)
        {
            var kids = Chunks.Children(d, header).ToList();
            if (kids.Count < 7) return;
            int P(int i) => kids[i].DataStart;
            int pack = P(0);
            Version = Chunks.I32(d, pack + 0x8);
            int numAttrTables = Chunks.I32(d, pack + 0x18), numAttrs = Chunks.I32(d, pack + 0x20), numTypes = Chunks.I32(d, pack + 0x28);
            int numParts = Chunks.I32(d, pack + 0x38);
            _strings = new byte[kids[1].Size];
            Array.Copy(d, P(1), _strings, 0, _strings.Length);
            int attrTables = P(2), attrs = P(3), types = P(5), parts = P(6);
            for (int o = P(4); o + 0x18 <= kids[4].End; o += 0x18)
            {
                var names = new uint[5];
                for (int k = 0; k < 5; k++) names[k] = Chunks.U32(d, o + 4 + k * 4);
                _models.Add((d[o] != 0, Chunks.U16(d, o + 2), names));
            }
            TypeNames = new uint[numTypes];
            for (int i = 0; i < numTypes; i++) TypeNames[i] = Chunks.U32(d, types + i * 4);
            int baseIndex = Parts.Count;
            for (int i = 0; i < numParts && parts + i * 0xE + 0xE <= kids[6].End; i++)
            {
                int o = parts + i * 0xE;
                byte gl = d[o + 5];
                var p = new Part
                {
                    Db = this, Index = baseIndex + i,
                    NameHash = (uint)(Chunks.U16(d, o + 2) << 16 | Chunks.U16(d, o)),
                    PartId = (sbyte)d[o + 4], Group = gl & 0x1F, Level = (gl >> 5) & 7, BaseModelSelector = (sbyte)d[o + 6],
                };
                int ti = d[o + 7];
                p.CarTypeHash = ti < TypeNames.Length ? TypeNames[ti] : 0;
                p.Name = String(Chunks.U16(d, o + 8));
                p.ModelTable = Chunks.U16(d, o + 12);
                int at = Chunks.U16(d, o + 10);
                if (at != 0xFFFF)
                {
                    int t = attrTables + at * 2;
                    int n = Chunks.I16(d, t);
                    var list = new (uint, uint)[Math.Max(0, n)];
                    for (int k = 0; k < n; k++)
                    {
                        int ai = Chunks.I16(d, t + 2 + k * 2);
                        int ao = attrs + ai * 8;
                        list[k] = ao + 8 <= kids[3].End ? (Chunks.U32(d, ao), Chunks.U32(d, ao + 4)) : (0u, 0u);
                    }
                    p.Attributes = list;
                }
                Parts.Add(p);
            }
        }

        /// <summary>GetTypesFromSlot (CarInfo.cpp:789): the two car-type name hashes a slot's parts are filed under.</summary>
        public (uint, uint) TypesForSlot(int slot, string carTypeName)
        {
            uint car = EaHash.Hash(carTypeName.ToUpperInvariant());
            foreach (var o in _overrides)
                if (o.car == car && o.slot == slot) return (o.t0, o.t1);
            if (slot * 2 + 1 >= _slotTypes.Length) return (car, 0);
            uint a = _slotTypes[slot * 2], b = _slotTypes[slot * 2 + 1];
            return (a == 0xFFFFFFFF ? car : a, b == 0xFFFFFFFF ? car : b);
        }

        /// <summary>
        /// NewGetCarPart over every part (CarInfo.cpp:661): parts of a car type for a slot, first slot type first,
        /// optionally only one upgrade level (-1 = any).
        /// </summary>
        public IEnumerable<Part> PartsFor(string carTypeName, int slot, int level = -1)
        {
            var (t0, t1) = TypesForSlot(slot, carTypeName);
            int id = PartIdForSlot(slot);
            foreach (uint t in new[] { t0, t1 })
            {
                if (t == 0) continue;
                foreach (var p in Parts)
                    if (p.PartId == id && p.CarTypeHash == t && (level < 0 || p.Level == level)) yield return p;
            }
        }

        public Part ByName(string name) => Parts.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        public Part ByIndex(int i) => i >= 0 && i < Parts.Count ? Parts[i] : null;
    }
}
