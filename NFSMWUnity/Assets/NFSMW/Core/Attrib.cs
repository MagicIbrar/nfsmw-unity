using System;
using System.Collections.Generic;
using System.IO;

namespace NfsMw.Core
{
    /// <summary>
    /// The attribute database (GLOBAL\attributes.bin, "VPAK"), EA's VLT format as loaded by speed.exe @0x664010:
    ///   header: magic, count, ..., [5] bin size, [6] vlt size, [7] bin offset, [8] vlt offset
    ///   db.bin: StrE string table and default data; db.vlt: DepN, StrN, DatN, ExpN, PtrN chunks.
    ///   PtrN (12 bytes: offset, u16 type, u16 index, target): type 2 selects the base (0 = vlt, 1 = bin),
    ///   type 3 makes a pointer to base[index] + target, type 0 ends the list (fix-ups @0x457670).
    ///   ExpN: count, then {key, type, extra, size, offset} records for the Database, Classes and Collections.
    /// Keys are Jenkins lookup2 hashes (seed 0xABCDEF00) of the names.
    /// </summary>
    public sealed class AttribDatabase
    {
        const uint TypeClass = 0x5E970CBC, TypeCollection = 0x8E112EB7;

        public readonly Dictionary<uint, AttribClass> Classes = new Dictionary<uint, AttribClass>();
        public readonly Dictionary<(uint cls, uint key), AttribCollection> Collections = new Dictionary<(uint, uint), AttribCollection>();
        public readonly Dictionary<uint, string> Names = new Dictionary<uint, string>();

        public static AttribDatabase Load(string path) => new AttribDatabase(File.ReadAllBytes(path));

        /// <summary>Several VPAK files into one database (gameplay.bin uses classes defined in attributes.bin).</summary>
        public static AttribDatabase LoadAll(params string[] paths)
        {
            var db = new AttribDatabase();
            foreach (var p in paths)
                if (File.Exists(p)) db.AddPack(File.ReadAllBytes(p));
            db.Link();
            return db;
        }

        AttribDatabase() { }

        public AttribDatabase(byte[] d)
        {
            AddPack(d);
            Link();
        }

        // packs read so far (pointer targets are absolute offsets into the pack's own bytes)
        readonly List<byte[]> _packs = new List<byte[]>();
        public byte[] D => _packs.Count > 0 ? _packs[0] : null;
        readonly Dictionary<(int pack, int at), int> _ptr = new Dictionary<(int, int), int>();
        readonly List<(int pack, int at)> _pendingCollections = new List<(int, int)>();
        public readonly List<string> EntryNames = new List<string>();

        /// <summary>
        /// VPAK: magic, entry count, names offset, names size, then {name offset, bin size, vlt size, bin offset,
        /// vlt offset} per entry; every entry is one db.vlt + db.bin pair.
        /// </summary>
        void AddPack(byte[] d)
        {
            if (Chunks.U32(d, 0) != 0x4B415056) throw new InvalidDataException("not a VPAK");
            int pack = _packs.Count;
            _packs.Add(d);
            int count = Chunks.I32(d, 4), names = Chunks.I32(d, 8);
            for (int i = 0; i < count; i++)
            {
                int e = 16 + i * 20;
                EntryNames.Add(Chunks.CString(d, names + Chunks.I32(d, e), 64));
                AddDatabase(pack, Chunks.I32(d, e + 12), Chunks.I32(d, e + 16), Chunks.I32(d, e + 8));
            }
        }

        void AddDatabase(int pack, int binOff, int vltOff, int vltSize)
        {
            var d = _packs[pack];
            int[] bases = { vltOff, binOff };

            // StrE: every string in db.bin, so collection keys can be named
            if (Chunks.U32(d, binOff) == 0x53747245)
            {
                int strSize = Chunks.I32(d, binOff + 4);
                for (int o = binOff + 8; o < binOff + strSize;)
                {
                    int e = o;
                    while (e < d.Length && d[e] != 0) e++;
                    if (e > o)
                    {
                        string s = System.Text.Encoding.ASCII.GetString(d, o, e - o);
                        Names[Hash(s)] = s;
                    }
                    o = e + 1;
                }
            }

            int ptrOff = -1, ptrSize = 0, expOff = -1;
            for (int o = vltOff; o + 8 <= vltOff + vltSize;)
            {
                uint tag = Chunks.U32(d, o);
                int size = Chunks.I32(d, o + 4);
                if (size < 8) break;
                if (tag == 0x5074724E) { ptrOff = o; ptrSize = size; }      // PtrN
                else if (tag == 0x4578704E) expOff = o;                      // ExpN
                o += size;
            }
            if (ptrOff < 0 || expOff < 0) return;

            int cur = bases[0];
            for (int o = ptrOff + 8; o + 12 <= ptrOff + ptrSize; o += 12)
            {
                int fix = Chunks.I32(d, o);
                int type = Chunks.U16(d, o + 4), idx = Chunks.U16(d, o + 6);
                int target = Chunks.I32(d, o + 8);
                if (type == 0) break;
                if (type == 2) cur = bases[idx];
                else if (type == 3) _ptr[(pack, cur + fix)] = bases[idx] + target;
            }

            int count = Chunks.I32(d, expOff + 8);
            for (int i = 0; i < count; i++)
            {
                int e = expOff + 12 + i * 20;
                uint type = Chunks.U32(d, e + 4);
                int at = vltOff + Chunks.I32(d, e + 16);
                if (type == TypeClass) ReadClass(pack, at);
                else if (type == TypeCollection) _pendingCollections.Add((pack, at));
            }
        }

        void Link()
        {
            foreach (var (pack, at) in _pendingCollections) ReadCollection(pack, at);
            _pendingCollections.Clear();
            foreach (var c in Collections.Values)
            {
                if (c.Class == null) Classes.TryGetValue(c.ClassKey, out c.Class);
                if (c.ParentKey != 0 && Collections.TryGetValue((c.ClassKey, c.ParentKey), out var p)) c.Parent = p;
            }
        }

        public static uint Hash(string s) => JenkinsHash(System.Text.Encoding.ASCII.GetBytes(s));

        public static uint JenkinsHash(byte[] k, uint init = 0xABCDEF00)
        {
            uint a = 0x9E3779B9, b = 0x9E3779B9, c = init;
            int i = 0, len = k.Length;
            while (len - i >= 12)
            {
                a += BitConverter.ToUInt32(k, i);
                b += BitConverter.ToUInt32(k, i + 4);
                c += BitConverter.ToUInt32(k, i + 8);
                Mix(ref a, ref b, ref c);
                i += 12;
            }
            c += (uint)len;
            int r = len - i;
            if (r > 10) c += (uint)k[i + 10] << 24;
            if (r > 9) c += (uint)k[i + 9] << 16;
            if (r > 8) c += (uint)k[i + 8] << 8;
            if (r > 7) b += (uint)k[i + 7] << 24;
            if (r > 6) b += (uint)k[i + 6] << 16;
            if (r > 5) b += (uint)k[i + 5] << 8;
            if (r > 4) b += k[i + 4];
            if (r > 3) a += (uint)k[i + 3] << 24;
            if (r > 2) a += (uint)k[i + 2] << 16;
            if (r > 1) a += (uint)k[i + 1] << 8;
            if (r > 0) a += k[i];
            Mix(ref a, ref b, ref c);
            return c;
        }

        static void Mix(ref uint a, ref uint b, ref uint c)
        {
            a -= b; a -= c; a ^= c >> 13;
            b -= c; b -= a; b ^= a << 8;
            c -= a; c -= b; c ^= b >> 13;
            a -= b; a -= c; a ^= c >> 12;
            b -= c; b -= a; b ^= a << 16;
            c -= a; c -= b; c ^= b >> 5;
            a -= b; a -= c; a ^= c >> 3;
            b -= c; b -= a; b ^= a << 10;
            c -= a; c -= b; c ^= b >> 15;
        }

        public int Ptr(int pack, int at) => _ptr.TryGetValue((pack, at), out int t) ? t : -1;
        public byte[] Pack(int pack) => _packs[pack];

        // ClassLoadData: key, ?, field count, fields*, layout size, ?, field count; fields are 16 bytes
        void ReadClass(int pack, int at)
        {
            var D = _packs[pack];
            var cls = new AttribClass
            {
                Key = Chunks.U32(D, at),
                LayoutSize = Chunks.I32(D, at + 16),
            };
            int n = Chunks.I32(D, at + 8);
            int fp = Ptr(pack, at + 12);
            for (int i = 0; i < n && fp >= 0; i++)
            {
                int f = fp + i * 16;
                var field = new AttribField
                {
                    Key = Chunks.U32(D, f),
                    Type = Chunks.U32(D, f + 4),
                    Offset = Chunks.U16(D, f + 8),
                    Size = Chunks.U16(D, f + 10),
                    Count = Chunks.U16(D, f + 12),
                    Flags = D[f + 14],
                };
                cls.Fields[field.Key] = field;
            }
            Classes[cls.Key] = cls;
        }

        // CollectionLoadData: key, class, parent, reserve, ?, entry count, type count, layout*,
        // type hashes[type count], then entries {key, value or pointer, u16 type index, u16 flags}
        void ReadCollection(int pack, int at)
        {
            var D = _packs[pack];
            var c = new AttribCollection
            {
                Db = this,
                Pack = pack,
                Key = Chunks.U32(D, at),
                ClassKey = Chunks.U32(D, at + 4),
                ParentKey = Chunks.U32(D, at + 8),
                Layout = Ptr(pack, at + 28),
            };
            Classes.TryGetValue(c.ClassKey, out c.Class);
            int entries = Chunks.I32(D, at + 20), types = Chunks.I32(D, at + 24);
            int e0 = at + 32 + types * 4;
            for (int i = 0; i < entries; i++)
            {
                int e = e0 + i * 12;
                uint key = Chunks.U32(D, e);
                int p = Ptr(pack, e + 4);
                c.Entries[key] = p >= 0 ? p : e + 4; // no pointer: the value is stored inline
            }
            if (Names.TryGetValue(c.Key, out var name)) c.Name = name;
            Collections[(c.ClassKey, c.Key)] = c;
        }

        /// <summary>The first collection with this name in any class (for packs whose class names are unknown).</summary>
        public AttribCollection Named(string collection)
        {
            uint k = Hash(collection);
            foreach (var kv in Collections) if (kv.Key.key == k) return kv.Value;
            return null;
        }

        public AttribCollection Get(string cls, string collection) =>
            Collections.TryGetValue((Hash(cls), Hash(collection)), out var c) ? c : null;

        public IEnumerable<AttribCollection> OfClass(string cls)
        {
            uint k = Hash(cls);
            foreach (var c in Collections.Values)
                if (c.ClassKey == k) yield return c;
        }
    }

    public sealed class AttribField
    {
        public uint Key, Type;
        public int Offset, Size, Count;
        public byte Flags;
        public bool IsArray => (Flags & 1) != 0;
        public bool InLayout => (Flags & 2) != 0;
    }

    public sealed class AttribClass
    {
        public uint Key;
        public int LayoutSize;
        public readonly Dictionary<uint, AttribField> Fields = new Dictionary<uint, AttribField>();
    }

    public sealed class AttribCollection
    {
        public AttribDatabase Db;
        public int Pack;
        public string Name;
        public uint Key, ClassKey, ParentKey;
        public AttribCollection Parent;
        public AttribClass Class;
        public int Layout = -1;
        public readonly Dictionary<uint, int> Entries = new Dictionary<uint, int>();

        /// <summary>Absolute offset of the field's first element and its element count (arrays carry an 8-byte header).</summary>
        /// <summary>Collection that supplied the last TryGet result (may be a parent, possibly in another pack).</summary>
        public AttribCollection Source { get; private set; }
        public byte[] Data => Db.Pack(Pack);

        public bool TryGet(string field, out int at, out int count, out AttribField def) =>
            TryGetKey(AttribDatabase.Hash(field), out at, out count, out def);

        /// <summary>TryGet by field hash (for fields whose names are not known).</summary>
        public bool TryGetKey(uint key, out int at, out int count, out AttribField def)
        {
            at = -1;
            count = 0;
            def = null;
            if (Class == null || !Class.Fields.TryGetValue(key, out def)) return false;
            for (var c = this; c != null; c = c.Parent)
            {
                int raw;
                if (def.InLayout)
                {
                    if (c.Layout < 0) continue;
                    raw = c.Layout + def.Offset;
                }
                else if (!c.Entries.TryGetValue(key, out raw)) continue;

                if (def.IsArray)
                {
                    // header: u16 capacity, u16 count, elem size (GEAR_RATIO of a 6-speed is "9, 8")
                    count = Chunks.U16(c.Data, raw + 2);
                    if (count > Chunks.U16(c.Data, raw)) count = Chunks.U16(c.Data, raw);
                    at = raw + 8;
                }
                else
                {
                    count = 1;
                    at = raw;
                }
                Source = c;
                return true;
            }
            return false;
        }

        /// <summary>A float field by its hash (fields whose names are not in any string table).</summary>
        public float FloatKey(uint key, float fallback = 0f) =>
            TryGetKey(key, out int at, out _, out _) ? Chunks.F32(Source.Data, at) : fallback;

        public float Float(string field, int index = 0, float fallback = 0f)
        {
            if (!TryGet(field, out int at, out int count, out var def) || index >= Math.Max(count, def.IsArray ? count : 2)) return fallback;
            return Chunks.F32(Source.Data, at + index * 4);
        }

        public float[] Floats(string field)
        {
            if (!TryGet(field, out int at, out int count, out _)) return Array.Empty<float>();
            var r = new float[count];
            for (int i = 0; i < count; i++) r[i] = Chunks.F32(Source.Data, at + i * 4);
            return r;
        }

        public int Int(string field, int index = 0, int fallback = 0)
        {
            if (!TryGet(field, out int at, out int count, out _) || index >= count) return fallback;
            return Chunks.I32(Source.Data, at + index * 4);
        }

        public bool Bool(string field, bool fallback = false)
        {
            if (!TryGet(field, out int at, out _, out _)) return fallback;
            return Source.Data[at] != 0;
        }

        /// <summary>EA::Reflection::Text: a char* (fixed up into db.bin).</summary>
        public string Text(string field, int index = 0)
        {
            if (!TryGet(field, out int at, out int count, out var def) || index >= count) return null;
            var src = Source;
            // an optional single Text entry already holds the fixed-up char*; layout fields and arrays hold pointers
            int p = !def.InLayout && !def.IsArray ? at : Db.Ptr(src.Pack, at + index * 4);
            return p >= 0 ? Chunks.CString(src.Data, p, 256) : null;
        }

        public string[] Texts(string field)
        {
            if (!TryGet(field, out _, out int count, out _)) return Array.Empty<string>();
            var r = new string[count];
            for (int i = 0; i < count; i++) r[i] = Text(field, i);
            return r;
        }

        /// <summary>GCollectionKey / RefSpec keys (u32 collection keys) as a list.</summary>
        public uint[] Keys(string field)
        {
            if (!TryGet(field, out int at, out int count, out var def)) return Array.Empty<uint>();
            var r = new uint[count];
            int stride = def.Size >= 12 ? def.Size : 4; // RefSpec = {class, collection, ptr}
            int keyOff = def.Size >= 12 ? 4 : 0;
            for (int i = 0; i < count; i++) r[i] = Chunks.U32(Source.Data, at + i * stride + keyOff);
            return r;
        }

        public Vec3 Vector3(string field)
        {
            if (!TryGet(field, out int at, out _, out _)) return default;
            return Vec3.Read(Source.Data, at);
        }

        /// <summary>Attrib::StringKey (16 bytes with a fixed-up char* somewhere inside).</summary>
        public string StringKey(string field, int index = 0)
        {
            if (!TryGet(field, out int at, out int count, out _) || index >= count) return null;
            var src = Source;
            int b = at + index * 16;
            for (int k = 0; k < 16; k += 4)
            {
                int p = Db.Ptr(src.Pack, b + k);
                if (p >= 0) return Chunks.CString(src.Data, p, 256);
            }
            return null;
        }

        /// <summary>Attrib::RefSpec targets (class key, collection key) of a field.</summary>
        public (uint cls, uint key)[] Refs(string field)
        {
            if (!TryGet(field, out int at, out int count, out _)) return Array.Empty<(uint, uint)>();
            var r = new (uint, uint)[count];
            for (int i = 0; i < count; i++) r[i] = (Chunks.U32(Source.Data, at + i * 12), Chunks.U32(Source.Data, at + i * 12 + 4));
            return r;
        }

        /// <summary>Is the field set on this collection or an ancestor?</summary>
        public bool Has(string field) => TryGet(field, out _, out _, out _);

        /// <summary>Front/rear pair (UMath::Vector2): x = front, y = rear.</summary>
        public (float front, float rear) Pair(string field, (float, float) fallback = default)
        {
            if (!TryGet(field, out int at, out _, out _)) return fallback;
            return (Chunks.F32(Source.Data, at), Chunks.F32(Source.Data, at + 4));
        }

        public override string ToString() => $"{Name ?? Key.ToString("X8")} ({Class?.Key:X8})";
    }
}
