using System;
using System.Runtime.CompilerServices;

namespace NfsMw.Core
{
    /// <summary>
    /// The boolean (range) decoder of VP5/VP6: 24-bit window, 16-bit refills, high in [128, 255] after renormalising.
    /// Reads past its end return zero bytes, the way a padded packet does.
    /// </summary>
    sealed class Vp6RangeDecoder
    {
        static readonly byte[] NormShift = BuildNormShift();
        byte[] _b;
        int _p, _end, _high, _bits;
        uint _code;

        static byte[] BuildNormShift()
        {
            var t = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int s = 0;
                while (s < 8 && (i << s) < 128) s++;
                t[i] = (byte)s;
            }
            return t;
        }

        public void Init(byte[] b, int start, int end)
        {
            _b = b;
            _p = start;
            _end = end;
            _high = 255;
            _bits = -16;
            _code = (uint)(Byte() << 16 | Byte() << 8 | Byte());
        }

        int Byte() => _p < _end ? _b[_p++] : Pad();
        int Pad() { _p++; return 0; }

        /// <summary>True once the decoder has consumed bytes beyond its buffer (corrupt or truncated data).</summary>
        public bool Overrun => _p > _end + 2;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        uint Renorm()
        {
            int shift = NormShift[_high];
            _high <<= shift;
            uint code = _code << shift;
            _bits += shift;
            if (_bits >= 0 && _p < _end)
            {
                int b0 = _b[_p], b1 = _p + 1 < _end ? _b[_p + 1] : 0;
                code |= (uint)(b0 << 8 | b1) << _bits;
                _p += 2;
                _bits -= 16;
            }
            return code;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetProb(int prob)
        {
            uint code = Renorm();
            uint low = 1 + (uint)(((_high - 1) * prob) >> 8);
            uint lowShift = low << 16;
            if (code >= lowShift)
            {
                _high -= (int)low;
                _code = code - lowShift;
                return 1;
            }
            _high = (int)low;
            _code = code;
            return 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Get()
        {
            uint code = Renorm();
            uint low = (uint)((_high + 1) >> 1);
            uint lowShift = low << 16;
            if (code >= lowShift)
            {
                _high -= (int)low;
                _code = code - lowShift;
                return 1;
            }
            _high = (int)low;
            _code = code;
            return 0;
        }

        public int Gets(int bits)
        {
            int v = 0;
            while (bits-- > 0) v = v << 1 | Get();
            return v;
        }

        /// <summary>A 7-bit probability that is never zero.</summary>
        public int GetsNn7()
        {
            int v = Gets(7) << 1;
            return v + (v == 0 ? 1 : 0);
        }

        /// <summary>Walks a {jump, prob index} tree (see Vp6Tables) with probabilities probs[off + index].</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int GetTree(sbyte[] tree, byte[] probs, int off)
        {
            int i = 0;
            while (tree[i] > 0)
            {
                if (GetProb(probs[off + tree[i + 1]]) != 0) i += tree[i] * 2;
                else i += 2;
            }
            return -tree[i];
        }
    }

    /// <summary>One decoded picture: Y, U, V planes (4:2:0), rows in coded order (row 0 = bottom of the picture for EA's VP6).</summary>
    public sealed class Vp6Picture
    {
        public readonly int Width, Height, ChromaWidth, ChromaHeight;
        public readonly byte[] Y, U, V;

        public Vp6Picture(int w, int h)
        {
            Width = w;
            Height = h;
            ChromaWidth = w / 2;
            ChromaHeight = h / 2;
            Y = new byte[w * h];
            U = new byte[ChromaWidth * ChromaHeight];
            V = new byte[ChromaWidth * ChromaHeight];
        }

        public void CopyTo(Vp6Picture o)
        {
            Buffer.BlockCopy(Y, 0, o.Y, 0, Y.Length);
            Buffer.BlockCopy(U, 0, o.U, 0, U.Length);
            Buffer.BlockCopy(V, 0, o.V, 0, V.Length);
        }
    }

    /// <summary>
    /// VP6 video decoder for the "VP60" streams in EA movies (MV0K/MV0F chunks; speed.exe links EA's RCMP
    /// VP6_CODEC_INTERNAL, strings at file 0x4C27E8). Own C# implementation of the On2 VP6 bitstream as documented by the
    /// open VP6 spec and the FFmpeg vp56/vp6 algorithms (boolean decoder, model updates, DC prediction, VP3 IDCT,
    /// quarter-pel bilinear motion compensation). The codec stores pictures bottom-up; everything here works in that
    /// coded order, so row 0 of the output planes is the bottom of the picture (exactly what a Unity texture wants).
    /// Supported: key/inter/golden frames, 4MV, separated coefficient partitions, sub-versions 6-8 without the
    /// "filter header" options. [?] Not implemented (no MW movie uses them, see movies.md): Huffman coefficients,
    /// interlaced pictures, the 4-tap / deblocking filters of the advanced profile.
    /// </summary>
    public sealed class Vp6Decoder
    {
        const int RefNone = -1, RefCurrent = 0, RefPrevious = 1, RefGolden = 2;
        const int MbInterNovecPf = 0, MbIntra = 1, MbInterDeltaPf = 2, MbInterV1Pf = 3, MbInterV2Pf = 4,
            MbInterNovecGf = 5, MbInterDeltaGf = 6, MbInter4V = 7, MbInterV1Gf = 8, MbInterV2Gf = 9;
        static readonly int[] RefFrameOf = { RefPrevious, RefCurrent, RefPrevious, RefPrevious, RefPrevious, RefGolden, RefGolden, RefPrevious, RefGolden, RefGolden };
        static readonly int[] B2P = { 0, 0, 0, 0, 1, 2 };
        static readonly int[] B6To4 = { 0, 0, 1, 1, 2, 3 };

        // offsets into _m, the coefficient model block
        const int Dccv = 0, Dcct = 22, Ract = 52, Runv = 448, ModelSize = 476;

        readonly Vp6RangeDecoder _c = new Vp6RangeDecoder(), _cc = new Vp6RangeDecoder();
        Vp6RangeDecoder _ccp;

        public int Width { get; private set; }
        public int Height { get; private set; }
        /// <summary>Displayed size from the key frame header (macroblocks x 16).</summary>
        public int DisplayWidth { get; private set; }
        public int DisplayHeight { get; private set; }
        public bool KeyFrame { get; private set; }
        public int SubVersion => _subVersion;
        /// <summary>Last frame: coefficients were Huffman coded / it refreshed the golden frame / its quantiser.</summary>
        public bool UsesHuffman => _useHuffman;
        public bool Golden => _goldenFlag;
        public int Quantizer => _quant;
        /// <summary>Debug: macroblock type counts of the last frame.</summary>
        public readonly int[] TypeCounts = new int[10];
        public string DebugMb(int i) => $"type {_mbType[i]} mv ({_mbMvX[i]},{_mbMvY[i]})";
        /// <summary>Count of frames that used a feature this decoder approximates (advanced-profile filters).</summary>
        public int ApproximatedFrames { get; private set; }

        int _mbW, _mbH;
        readonly Vp6Picture[] _pool = new Vp6Picture[3];
        Vp6Picture _cur, _prev, _golden;

        int _subVersion, _quant = -1, _dequantDc, _dequantAc;
        bool _filterHeader, _goldenFlag, _useHuffman, _deblock;
        int _filterMode;

        // models
        readonly byte[] _m = new byte[ModelSize];
        readonly byte[] _coeffReorder = new byte[64];
        readonly byte[] _indexToPos = new byte[64];
        readonly byte[] _idctSelectorOf = new byte[64];
        readonly byte[] _vectorDct = new byte[2], _vectorSig = new byte[2];
        readonly byte[] _vectorPdv = new byte[14], _vectorFdv = new byte[16];
        readonly byte[] _mbTypesStats = new byte[60];
        readonly byte[] _mbTypeProb = new byte[300]; // [ctx 3][prev type 10][10]

        // per macroblock
        byte[] _mbType;
        short[] _mbMvX, _mbMvY;
        int _prevMbType;
        int _vc0X, _vc0Y, _vc1X, _vc1Y, _vecCandPos;
        readonly int[] _mvX = new int[6], _mvY = new int[6];
        readonly short[] _block = new short[6 * 64];
        readonly int[] _idctSel = new int[6];
        readonly short[] _tmp = new short[64];
        readonly byte[] _mcTmp = new byte[16 * 16];

        // DC prediction
        int[] _aboveRef, _aboveDc;
        byte[] _aboveNotNull;
        readonly int[] _leftRef = new int[4], _leftDc = new int[4];
        readonly byte[] _leftNotNull = new byte[4];
        readonly int[] _aboveIdx = new int[6];
        readonly int[] _prevDc = new int[12]; // [plane 3][ref 4]

        /// <summary>The last decoded picture (the reference the next inter frame predicts from).</summary>
        public Vp6Picture Picture => _prev;

        /// <summary>Decodes one frame (an MV0K/MV0F chunk payload). Returns false on corrupt data.</summary>
        public bool Decode(byte[] d, int off, int len)
        {
            if (len < 3) return false;
            int end = off + len;
            int b0 = d[off];
            bool key = (b0 & 0x80) == 0;
            int quant = (b0 >> 1) & 0x3F;
            bool separated = (b0 & 1) != 0;
            int p = off, coeffOffset = 0;
            bool parseFilterInfo = false;
            int vrtShift = 0;
            if (key)
            {
                if (len < 8) return false;
                int sub = d[p + 1] >> 3;
                if (sub > 8) return false;
                if ((d[p + 1] & 1) != 0) throw new NotSupportedException("interlaced VP6");
                _filterHeader = (d[p + 1] & 6) != 0;
                if (separated || !_filterHeader)
                {
                    coeffOffset = (d[p + 2] << 8 | d[p + 3]) - 2;
                    p += 2;
                }
                int rows = d[p + 2], cols = d[p + 3];
                if (rows == 0 || cols == 0) return false;
                DisplayHeight = d[p + 4] * 16;
                DisplayWidth = d[p + 5] * 16;
                if (cols * 16 != Width || rows * 16 != Height) Resize(cols * 16, rows * 16);
                _c.Init(d, p + 6, end);
                _c.Gets(2);
                parseFilterInfo = _filterHeader;
                if (sub < 8) vrtShift = 5;
                _subVersion = sub;
                _goldenFlag = false;
            }
            else
            {
                if (_subVersion == 0 || Width == 0 || _prev == null) return false;
                if (separated || !_filterHeader)
                {
                    coeffOffset = (d[p + 1] << 8 | d[p + 2]) - 2;
                    p += 2;
                }
                _c.Init(d, p + 1, end);
                _goldenFlag = _c.Get() != 0;
                if (_filterHeader)
                {
                    _deblock = _c.Get() != 0;
                    if (_deblock) _c.Get();
                    if (_subVersion > 7) parseFilterInfo = _c.Get() != 0;
                }
            }
            KeyFrame = key;
            if (quant != _quant)
            {
                _quant = quant;
                _dequantDc = Vp6Tables.DcDequant[quant] << 2;
                _dequantAc = Vp6Tables.AcDequant[quant] << 2;
            }
            if (parseFilterInfo)
            {
                if (_c.Get() != 0)
                {
                    _filterMode = 2;
                    _c.Gets(5); // sample variance threshold << vrtShift
                    _c.Gets(3); // max vector length
                }
                else if (_c.Get() != 0) _filterMode = 1;
                else _filterMode = 0;
                if (_subVersion > 7) _c.Gets(4); // filter selection
                _ = vrtShift;
            }
            // the coefficient partition is Huffman coded or a second boolean decoder; Huffman needs a separate partition
            _useHuffman = _c.Get() != 0;
            if (coeffOffset != 0)
            {
                int cs = p + coeffOffset;
                if (cs >= end || cs < off) return false;
                if (_useHuffman) _gb.Init(d, cs, end);
                else _cc.Init(d, cs, end);
                _ccp = _cc;
            }
            else
            {
                _useHuffman = false;
                _ccp = _c;
            }
            _huffError = false;
            if ((_filterHeader && _filterMode != 0) || _deblock) ApproximatedFrames++;

            // a fresh buffer that is neither the previous nor the golden reference
            _cur = null;
            foreach (var pic in _pool)
                if (pic != _prev && pic != _golden) { _cur = pic; break; }

            DecodeMacroblocks();
            bool ok = !(_c.Overrun || _ccp.Overrun || _huffError);

            // a damaged frame is still kept as the reference (the picture degrades instead of stopping)
            if (key || _goldenFlag) _golden = _cur;
            _prev = _cur;
            return ok;
        }

        // ---- Huffman coefficient partition ----

        /// <summary>MSB-first bit reader; reads past the end return zeros.</summary>
        sealed class BitReader
        {
            byte[] _b;
            int _start, _end;
            long _bit, _bitEnd;

            public void Init(byte[] b, int start, int end)
            {
                _b = b;
                _start = start;
                _end = end;
                _bit = 0;
                _bitEnd = (long)(end - start) * 8;
            }

            public long Left => _bitEnd - _bit;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public int Bit()
            {
                long i = _bit++;
                int p = _start + (int)(i >> 3);
                return p < _end ? (_b[p] >> (7 - (int)(i & 7))) & 1 : 0;
            }

            /// <summary>The next 16 bits without consuming them.</summary>
            public int Peek16()
            {
                int p = _start + (int)(_bit >> 3), sh = (int)(_bit & 7);
                int v = (p < _end ? _b[p] : 0) << 16 | (p + 1 < _end ? _b[p + 1] : 0) << 8 | (p + 2 < _end ? _b[p + 2] : 0);
                return (v >> (8 - sh)) & 0xFFFF;
            }

            public void Skip(int n) => _bit += n;

            public int Bits(int n)
            {
                int v = 0;
                while (n-- > 0) v = v << 1 | Bit();
                return v;
            }
        }

        /// <summary>
        /// A Huffman code built from a boolean-tree model the way the VP6 spec does it: leaf counts from the node
        /// probabilities (map gives each node's two children: below size a symbol, otherwise an internal node), sorted by
        /// (count, then higher symbol first), merged pairwise with new nodes placed before equal counts; bit 0 = first
        /// child. Decoding uses a 12-bit lookup with a tree walk for longer codes.
        /// </summary>
        sealed class Huffman
        {
            const int LookupBits = 12;
            const int HNode = -1;
            readonly int[] _sym = new int[64], _n0 = new int[64], _count = new int[64];
            readonly int[] _tmpCount = new int[32];
            readonly int[] _lookup = new int[1 << LookupBits]; // (len << 16 | sym) or (-1 - node) to continue walking
            int _root;

            public void Build(byte[] model, int mOff, byte[] map, int size)
            {
                // counts
                _tmpCount[0] = 256;
                for (int i = 0; i < size - 1; i++)
                {
                    int a = _tmpCount[i] * model[mOff + i] >> 8;
                    int b = _tmpCount[i] * (255 - model[mOff + i]) >> 8;
                    Set(map[2 * i], a + (a == 0 ? 1 : 0), size);
                    Set(map[2 * i + 1], b + (b == 0 ? 1 : 0), size);
                }
                // sort the leaves: ascending count, ties higher symbol first
                var order = new int[size];
                var cnt = new int[size];
                for (int i = 0; i < size; i++) { order[i] = i; cnt[i] = _leafCount[i]; }
                Array.Sort(order, (x, y) => cnt[x] != cnt[y] ? cnt[x].CompareTo(cnt[y]) : y.CompareTo(x));
                for (int i = 0; i < size; i++)
                {
                    _sym[i] = order[i];
                    _count[i] = cnt[order[i]];
                    _n0[i] = -2;
                }
                int curNode = size;
                _count[size * 2 - 1] = 0;
                for (int i = 0; i < size * 2 - 1; i += 2)
                {
                    int cur = _count[i] + _count[i + 1];
                    int j;
                    for (j = curNode; j > i + 2; j--)
                    {
                        if (cur > _count[j - 1]) break;
                        _sym[j] = _sym[j - 1];
                        _count[j] = _count[j - 1];
                        _n0[j] = _n0[j - 1];
                    }
                    _sym[j] = HNode;
                    _count[j] = cur;
                    _n0[j] = i;
                    curNode++;
                }
                _root = size * 2 - 2;
                // lookup table
                for (int code = 0; code < 1 << LookupBits; code++)
                {
                    int node = _root, len = 0;
                    while (_sym[node] == HNode && len < LookupBits)
                    {
                        node = _n0[node] + ((code >> (LookupBits - 1 - len)) & 1);
                        len++;
                    }
                    _lookup[code] = _sym[node] != HNode ? len << 16 | _sym[node] : -1 - node;
                }
            }

            readonly int[] _leafCount = new int[32];

            void Set(int index, int count, int size)
            {
                if (index < size) _leafCount[index] = count;
                else _tmpCount[index - size] = count;
            }

            public int Read(BitReader gb)
            {
                int e = _lookup[gb.Peek16() >> (16 - LookupBits)];
                if (e >= 0)
                {
                    gb.Skip(e >> 16);
                    return e & 0xFFFF;
                }
                gb.Skip(LookupBits);
                int node = -1 - e;
                while (_sym[node] == HNode) node = _n0[node] + gb.Bit();
                return _sym[node];
            }
        }

        static readonly byte[] HuffCoeffMap = { 13, 14, 11, 0, 1, 15, 16, 18, 2, 17, 3, 4, 19, 20, 5, 6, 21, 22, 7, 8, 9, 10 };
        static readonly byte[] HuffRunMap = { 10, 13, 11, 12, 0, 1, 2, 3, 14, 8, 15, 16, 4, 5, 6, 7 };
        static readonly int[] HuffCoeffBias = { 0, 1, 2, 3, 4, 5, 7, 11, 19, 35, 67 };

        readonly BitReader _gb = new BitReader();
        readonly Huffman[] _dccvVlc = { new Huffman(), new Huffman() };
        readonly Huffman[] _runvVlc = { new Huffman(), new Huffman() };
        readonly Huffman[] _ractVlc = NewHuffmans(2 * 3 * 6);
        readonly int[] _nbNull = new int[4]; // [coeff index 0/1][plane type]
        bool _huffError;

        static Huffman[] NewHuffmans(int n)
        {
            var a = new Huffman[n];
            for (int i = 0; i < n; i++) a[i] = new Huffman();
            return a;
        }

        void BuildHuffmanTables()
        {
            for (int pt = 0; pt < 2; pt++)
            {
                _dccvVlc[pt].Build(_m, Dccv + pt * 11, HuffCoeffMap, 12);
                _runvVlc[pt].Build(_m, Runv + pt * 14, HuffRunMap, 9);
                for (int ct = 0; ct < 3; ct++)
                    for (int cg = 0; cg < 6; cg++)
                        _ractVlc[(pt * 3 + ct) * 6 + cg].Build(_m, Ract + ((pt * 3 + ct) * 6 + cg) * 11, HuffCoeffMap, 12);
            }
            Array.Clear(_nbNull, 0, 4);
        }

        int GetNbNull()
        {
            int val = _gb.Bits(2);
            if (val == 2) val += _gb.Bits(2);
            else if (val == 3)
            {
                val = _gb.Bit() << 2;
                val = 6 + val + _gb.Bits(2 + val);
            }
            return val;
        }

        void ParseCoeffHuffman()
        {
            var gb = _gb;
            var block = _block;
            for (int b = 0; b < 6; b++)
            {
                int ct = 0, pt = b > 3 ? 1 : 0, bo = b * 64;
                var vlc = _dccvVlc[pt];
                int ci = 0;
                for (;;)
                {
                    int run = 1;
                    if (ci < 2 && _nbNull[ci * 2 + pt] != 0)
                    {
                        _nbNull[ci * 2 + pt]--;
                        if (ci != 0) break;
                    }
                    else
                    {
                        if (gb.Left <= 0) { _huffError = true; return; }
                        int coeff = vlc.Read(gb);
                        if (coeff == 0)
                        {
                            if (ci != 0)
                            {
                                run += _runvVlc[ci >= 6 ? 1 : 0].Read(gb);
                                if (run >= 9) run += gb.Bits(6);
                            }
                            else _nbNull[pt] = GetNbNull();
                            ct = 0;
                        }
                        else if (coeff == 11)
                        {
                            if (ci == 1) _nbNull[2 + pt] = GetNbNull();
                            break;
                        }
                        else
                        {
                            int c2 = HuffCoeffBias[coeff];
                            if (coeff > 4) c2 += gb.Bits(coeff <= 9 ? coeff - 4 : 11);
                            ct = 1 + (c2 > 1 ? 1 : 0);
                            int sign = gb.Bit();
                            c2 = (c2 ^ -sign) + sign;
                            if (ci != 0) c2 *= _dequantAc;
                            block[bo + Vp6Tables.ZigZag[_indexToPos[ci]]] = (short)c2;
                        }
                    }
                    ci += run;
                    if (ci >= 64) break;
                    int cg = Vp6Tables.CoeffGroups[ci];
                    vlc = _ractVlc[(pt * 3 + ct) * 6 + (cg > 3 ? 3 : cg)];
                }
                _idctSel[b] = _idctSelectorOf[ci > 63 ? 63 : ci];
            }
        }

        void Resize(int w, int h)
        {
            Width = w;
            Height = h;
            _mbW = w / 16;
            _mbH = h / 16;
            for (int i = 0; i < _pool.Length; i++) _pool[i] = new Vp6Picture(w, h);
            _prev = _golden = null;
            _mbType = new byte[_mbW * _mbH];
            _mbMvX = new short[_mbW * _mbH];
            _mbMvY = new short[_mbW * _mbH];
            int n = 4 * _mbW + 6;
            _aboveRef = new int[n];
            _aboveDc = new int[n];
            _aboveNotNull = new byte[n];
        }

        // ---- models ----

        void DefaultModelsInit()
        {
            _vectorDct[0] = 0xA2;
            _vectorDct[1] = 0xA4;
            _vectorSig[0] = 0x80;
            _vectorSig[1] = 0x80;
            Buffer.BlockCopy(Vp6Tables.DefMbTypesStats, 0, _mbTypesStats, 0, 60);
            Buffer.BlockCopy(Vp6Tables.DefFdvVectorModel, 0, _vectorFdv, 0, 16);
            Buffer.BlockCopy(Vp6Tables.DefPdvVectorModel, 0, _vectorPdv, 0, 14);
            Buffer.BlockCopy(Vp6Tables.DefRunvCoeffModel, 0, _m, Runv, 28);
            Buffer.BlockCopy(Vp6Tables.DefCoeffReorder, 0, _coeffReorder, 0, 64);
            CoeffOrderTableInit();
        }

        void CoeffOrderTableInit()
        {
            int idx = 1;
            _indexToPos[0] = 0;
            for (int i = 0; i < 16; i++)
                for (int pos = 1; pos < 64; pos++)
                    if (_coeffReorder[pos] == i) _indexToPos[idx++] = (byte)pos;
            int max = 0;
            for (idx = 0; idx < 64; idx++)
            {
                if (_indexToPos[idx] > max) max = _indexToPos[idx];
                _idctSelectorOf[idx] = (byte)(_subVersion > 6 ? max + 1 : max);
            }
        }

        void ParseMbTypeModels()
        {
            var c = _c;
            for (int ctx = 0; ctx < 3; ctx++)
            {
                if (c.GetProb(174) != 0)
                {
                    int idx = c.Gets(4);
                    // speed.exe 0x808CD4: the table is [ctx 3][16][type 10]{stat0, stat1}
                    Buffer.BlockCopy(Vp6Tables.PreDefMbTypeStats, (ctx * 16 + idx) * 20, _mbTypesStats, ctx * 20, 20);
                }
                if (c.GetProb(254) != 0)
                {
                    for (int type = 0; type < 10; type++)
                        for (int i = 0; i < 2; i++)
                            if (c.GetProb(205) != 0)
                            {
                                int sign = c.Get();
                                int delta = c.GetTree(Vp6Tables.PmbtmTree, Vp6Tables.MbTypeModelModel, 0);
                                if (delta == 0) delta = 4 * c.Gets(7);
                                // speed.exe 0x808D47 clamps the statistic to 0..255 (FFmpeg lets the byte wrap)
                                int k = ctx * 20 + type * 2 + i;
                                int v = _mbTypesStats[k] + (delta ^ -sign) + sign;
                                _mbTypesStats[k] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                            }
                }
            }

            // macroblock-type probabilities, conditioned on the previous macroblock's type
            var p = new int[10];
            for (int ctx = 0; ctx < 3; ctx++)
            {
                for (int type = 0; type < 10; type++) p[type] = 100 * _mbTypesStats[ctx * 20 + type * 2 + 1];
                for (int type = 0; type < 10; type++)
                {
                    int s0 = _mbTypesStats[ctx * 20 + type * 2], s1 = _mbTypesStats[ctx * 20 + type * 2 + 1];
                    int o = (ctx * 10 + type) * 10;
                    _mbTypeProb[o] = (byte)(255 - (255 * s0) / (1 + s0 + s1));
                    p[type] = 0;
                    int p02 = p[0] + p[2], p34 = p[3] + p[4], p0234 = p02 + p34, p17 = p[1] + p[7], p56 = p[5] + p[6], p89 = p[8] + p[9];
                    int p5689 = p56 + p89, p156789 = p17 + p5689;
                    _mbTypeProb[o + 1] = (byte)(1 + 255 * p0234 / (1 + p0234 + p156789));
                    _mbTypeProb[o + 2] = (byte)(1 + 255 * p02 / (1 + p0234));
                    _mbTypeProb[o + 3] = (byte)(1 + 255 * p17 / (1 + p156789));
                    _mbTypeProb[o + 4] = (byte)(1 + 255 * p[0] / (1 + p02));
                    _mbTypeProb[o + 5] = (byte)(1 + 255 * p[3] / (1 + p34));
                    _mbTypeProb[o + 6] = (byte)(1 + 255 * p[1] / (1 + p17));
                    _mbTypeProb[o + 7] = (byte)(1 + 255 * p56 / (1 + p5689));
                    _mbTypeProb[o + 8] = (byte)(1 + 255 * p[5] / (1 + p56));
                    _mbTypeProb[o + 9] = (byte)(1 + 255 * p[8] / (1 + p89));
                    p[type] = 100 * s1;
                }
            }
        }

        void ParseVectorModels()
        {
            var c = _c;
            for (int comp = 0; comp < 2; comp++)
            {
                if (c.GetProb(Vp6Tables.SigDctPct[comp * 2]) != 0) _vectorDct[comp] = (byte)c.GetsNn7();
                if (c.GetProb(Vp6Tables.SigDctPct[comp * 2 + 1]) != 0) _vectorSig[comp] = (byte)c.GetsNn7();
            }
            for (int comp = 0; comp < 2; comp++)
                for (int node = 0; node < 7; node++)
                    if (c.GetProb(Vp6Tables.PdvPct[comp * 7 + node]) != 0) _vectorPdv[comp * 7 + node] = (byte)c.GetsNn7();
            for (int comp = 0; comp < 2; comp++)
                for (int node = 0; node < 8; node++)
                    if (c.GetProb(Vp6Tables.FdvPct[comp * 8 + node]) != 0) _vectorFdv[comp * 8 + node] = (byte)c.GetsNn7();
        }

        void ParseCoeffModels()
        {
            var c = _c;
            var defProb = new int[11];
            for (int i = 0; i < 11; i++) defProb[i] = 0x80;
            for (int pt = 0; pt < 2; pt++)
                for (int node = 0; node < 11; node++)
                    if (c.GetProb(Vp6Tables.DccvPct[pt * 11 + node]) != 0)
                    {
                        defProb[node] = c.GetsNn7();
                        _m[Dccv + pt * 11 + node] = (byte)defProb[node];
                    }
                    else if (KeyFrame) _m[Dccv + pt * 11 + node] = (byte)defProb[node];

            if (c.Get() != 0)
            {
                for (int pos = 1; pos < 64; pos++)
                    if (c.GetProb(Vp6Tables.CoeffReorderPct[pos]) != 0) _coeffReorder[pos] = (byte)c.Gets(4);
                CoeffOrderTableInit();
            }

            for (int cg = 0; cg < 2; cg++)
                for (int node = 0; node < 14; node++)
                    if (c.GetProb(Vp6Tables.RunvPct[cg * 14 + node]) != 0) _m[Runv + cg * 14 + node] = (byte)c.GetsNn7();

            for (int ct = 0; ct < 3; ct++)
                for (int pt = 0; pt < 2; pt++)
                    for (int cg = 0; cg < 6; cg++)
                        for (int node = 0; node < 11; node++)
                        {
                            int dst = Ract + ((pt * 3 + ct) * 6 + cg) * 11 + node;
                            if (c.GetProb(Vp6Tables.RactPct[((ct * 2 + pt) * 6 + cg) * 11 + node]) != 0)
                            {
                                defProb[node] = c.GetsNn7();
                                _m[dst] = (byte)defProb[node];
                            }
                            else if (KeyFrame) _m[dst] = (byte)defProb[node];
                        }

            if (_useHuffman)
            {
                BuildHuffmanTables();
                return;
            }
            // DC context models are a linear combination of the DC models
            for (int pt = 0; pt < 2; pt++)
                for (int ctx = 0; ctx < 3; ctx++)
                    for (int node = 0; node < 5; node++)
                    {
                        int lc = (ctx * 5 + node) * 2;
                        int v = ((_m[Dccv + pt * 11 + node] * Vp6Tables.DccvLc[lc] + 128) >> 8) + Vp6Tables.DccvLc[lc + 1];
                        _m[Dcct + (pt * 3 + ctx) * 5 + node] = (byte)(v < 1 ? 1 : v > 255 ? 255 : v);
                    }
        }

        // ---- macroblocks ----

        void DecodeMacroblocks()
        {
            int mbCount = _mbW * _mbH;
            Array.Clear(TypeCounts, 0, 10);
            if (KeyFrame)
            {
                DefaultModelsInit();
                for (int i = 0; i < mbCount; i++) _mbType[i] = MbIntra;
            }
            else
            {
                ParseMbTypeModels();
                ParseVectorModels();
                _prevMbType = MbInterNovecPf;
            }
            ParseCoeffModels();

            Array.Clear(_prevDc, 0, _prevDc.Length);
            _prevDc[1 * 4 + RefCurrent] = 128;
            _prevDc[2 * 4 + RefCurrent] = 128;
            for (int i = 0; i < _aboveRef.Length; i++)
            {
                _aboveRef[i] = RefNone;
                _aboveDc[i] = 0;
                _aboveNotNull[i] = 0;
            }
            _aboveRef[2 * _mbW + 2] = RefCurrent;
            _aboveRef[3 * _mbW + 4] = RefCurrent;

            for (int row = 0; row < _mbH; row++)
            {
                for (int i = 0; i < 4; i++)
                {
                    _leftRef[i] = RefNone;
                    _leftDc[i] = 0;
                    _leftNotNull[i] = 0;
                }
                _aboveIdx[0] = 1;
                _aboveIdx[1] = 2;
                _aboveIdx[2] = 1;
                _aboveIdx[3] = 2;
                _aboveIdx[4] = 2 * _mbW + 2 + 1;
                _aboveIdx[5] = 3 * _mbW + 4 + 1;
                for (int col = 0; col < _mbW; col++)
                {
                    DecodeMb(row, col);
                    for (int i = 0; i < 4; i++) _aboveIdx[i] += 2;
                    _aboveIdx[4]++;
                    _aboveIdx[5]++;
                }
            }
        }

        void DecodeMb(int row, int col)
        {
            int type = KeyFrame ? MbIntra : DecodeMv(row, col);
            if (_useHuffman) ParseCoeffHuffman();
            else ParseCoeff();
            int refFrame = RefFrameOf[type];
            AddPredictorsDc(refFrame);
            Render(row, col, type, refFrame);
        }

        int ParseMbType(int prev, int ctx)
        {
            int o = (ctx * 10 + prev) * 10;
            if (_c.GetProb(_mbTypeProb[o]) != 0) return prev;
            return _c.GetTree(Vp6Tables.PmbtTree, _mbTypeProb, o);
        }

        int GetVectorsPredictors(int row, int col, int refFrame)
        {
            int nb = 0, v0x = 0, v0y = 0, v1x = 0, v1y = 0;
            var cand = Vp6Tables.CandidatePos;
            for (int pos = 0; pos < 12; pos++)
            {
                int x = col + cand[pos * 2], y = row + cand[pos * 2 + 1];
                if (x < 0 || x >= _mbW || y < 0 || y >= _mbH) continue;
                int o = x + _mbW * y;
                if (RefFrameOf[_mbType[o]] != refFrame) continue;
                int mx = _mbMvX[o], my = _mbMvY[o];
                if ((mx == v0x && my == v0y) || (mx == 0 && my == 0)) continue;
                if (nb == 0) { v0x = mx; v0y = my; }
                else { v1x = mx; v1y = my; }
                nb++;
                if (nb > 1) { nb = -1; break; }
                _vecCandPos = pos;
            }
            _vc0X = v0x;
            _vc0Y = v0y;
            _vc1X = v1x;
            _vc1Y = v1y;
            return nb + 1;
        }

        void ParseVectorAdjustment(out int vx, out int vy)
        {
            vx = vy = 0;
            if (_vecCandPos < 2)
            {
                vx = _vc0X;
                vy = _vc0Y;
            }
            var c = _c;
            for (int comp = 0; comp < 2; comp++)
            {
                int delta = 0;
                if (c.GetProb(_vectorDct[comp]) != 0)
                {
                    int f = comp * 8;
                    delta |= c.GetProb(_vectorFdv[f + 0]);
                    delta |= c.GetProb(_vectorFdv[f + 1]) << 1;
                    delta |= c.GetProb(_vectorFdv[f + 2]) << 2;
                    delta |= c.GetProb(_vectorFdv[f + 7]) << 7;
                    delta |= c.GetProb(_vectorFdv[f + 6]) << 6;
                    delta |= c.GetProb(_vectorFdv[f + 5]) << 5;
                    delta |= c.GetProb(_vectorFdv[f + 4]) << 4;
                    if ((delta & 0xF0) != 0) delta |= c.GetProb(_vectorFdv[f + 3]) << 3;
                    else delta |= 8;
                }
                else delta = c.GetTree(Vp6Tables.PvaTree, _vectorPdv, comp * 7);
                if (delta != 0 && c.GetProb(_vectorSig[comp]) != 0) delta = -delta;
                if (comp == 0) vx += delta;
                else vy += delta;
            }
        }

        int DecodeMv(int row, int col)
        {
            int ctx = GetVectorsPredictors(row, col, RefPrevious);
            _prevMbType = ParseMbType(_prevMbType, ctx);
            int mbi = row * _mbW + col;
            _mbType[mbi] = (byte)_prevMbType;
            TypeCounts[_prevMbType]++;
            int mx = 0, my = 0;
            switch (_prevMbType)
            {
                case MbInterV1Pf: mx = _vc0X; my = _vc0Y; break;
                case MbInterV2Pf: mx = _vc1X; my = _vc1Y; break;
                case MbInterV1Gf:
                    GetVectorsPredictors(row, col, RefGolden);
                    mx = _vc0X; my = _vc0Y;
                    break;
                case MbInterV2Gf:
                    GetVectorsPredictors(row, col, RefGolden);
                    mx = _vc1X; my = _vc1Y;
                    break;
                case MbInterDeltaPf:
                    ParseVectorAdjustment(out mx, out my);
                    break;
                case MbInterDeltaGf:
                    GetVectorsPredictors(row, col, RefGolden);
                    ParseVectorAdjustment(out mx, out my);
                    break;
                case MbInter4V:
                    Decode4Mv(mbi);
                    return _prevMbType;
            }
            _mbMvX[mbi] = (short)mx;
            _mbMvY[mbi] = (short)my;
            for (int b = 0; b < 6; b++)
            {
                _mvX[b] = mx;
                _mvY[b] = my;
            }
            return _prevMbType;
        }

        void Decode4Mv(int mbi)
        {
            var type = _type4;
            for (int b = 0; b < 4; b++)
            {
                int t = _c.Gets(2);
                type[b] = t != 0 ? t + 1 : 0; // 0, 2, 3 or 4: all from the previous frame
            }
            int sx = 0, sy = 0;
            for (int b = 0; b < 4; b++)
            {
                switch (type[b])
                {
                    case MbInterNovecPf: _mvX[b] = 0; _mvY[b] = 0; break;
                    case MbInterDeltaPf: ParseVectorAdjustment(out _mvX[b], out _mvY[b]); break;
                    case MbInterV1Pf: _mvX[b] = _vc0X; _mvY[b] = _vc0Y; break;
                    case MbInterV2Pf: _mvX[b] = _vc1X; _mvY[b] = _vc1Y; break;
                }
                sx += _mvX[b];
                sy += _mvY[b];
            }
            _mbMvX[mbi] = (short)_mvX[3];
            _mbMvY[mbi] = (short)_mvY[3];
            _mvX[4] = _mvX[5] = RShift2(sx);
            _mvY[4] = _mvY[5] = RShift2(sy);
        }

        /// <summary>Average of four vectors, rounding half away from zero.</summary>
        static int RShift2(int a) => a > 0 ? (a + 2) >> 2 : (a + 1) >> 2;

        void ParseCoeff()
        {
            var c = _ccp;
            var m = _m;
            var block = _block;
            var parse = Vp6Tables.CoeffParseTable;
            for (int b = 0; b < 6; b++)
            {
                int ct = 1, run = 1;
                int pt = b > 3 ? 1 : 0;
                int ai = _aboveIdx[b], li = B6To4[b];
                int ctx = _leftNotNull[li] + _aboveNotNull[ai];
                int m1 = Dccv + pt * 11, m2 = Dcct + (pt * 3 + ctx) * 5;
                int ci = 0, bo = b * 64;
                for (;;)
                {
                    if ((ci > 1 && ct == 0) || c.GetProb(m[m2]) != 0)
                    {
                        int coeff;
                        if (c.GetProb(m[m2 + 2]) != 0)
                        {
                            if (c.GetProb(m[m2 + 3]) != 0)
                            {
                                int idx = c.GetTree(Vp6Tables.PcTree, m, m1);
                                coeff = Vp6Tables.CoeffBias[idx];
                                var pp = parse[idx];
                                for (int i = Vp6Tables.CoeffBitLength[idx]; i >= 0; i--) coeff += c.GetProb(pp[i]) << i;
                            }
                            else if (c.GetProb(m[m2 + 4]) != 0) coeff = 3 + c.GetProb(m[m1 + 5]);
                            else coeff = 2;
                            ct = 2;
                        }
                        else
                        {
                            ct = 1;
                            coeff = 1;
                        }
                        int sign = c.Get();
                        coeff = (coeff ^ -sign) + sign;
                        if (ci != 0) coeff *= _dequantAc;
                        block[bo + Vp6Tables.ZigZag[_indexToPos[ci]]] = (short)coeff;
                        run = 1;
                    }
                    else
                    {
                        ct = 0;
                        if (ci > 0)
                        {
                            if (c.GetProb(m[m2 + 1]) == 0) break;
                            int m3 = Runv + (ci >= 6 ? 14 : 0);
                            run = c.GetTree(Vp6Tables.PcrTree, m, m3);
                            if (run == 0)
                            {
                                run = 9;
                                for (int i = 0; i < 6; i++) run += c.GetProb(m[m3 + i + 8]) << i;
                            }
                        }
                    }
                    ci += run;
                    if (ci >= 64) break;
                    m1 = m2 = Ract + ((pt * 3 + ct) * 6 + Vp6Tables.CoeffGroups[ci]) * 11;
                }
                byte nn = (byte)(block[bo] != 0 ? 1 : 0);
                _leftNotNull[li] = nn;
                _aboveNotNull[ai] = nn;
                _idctSel[b] = _idctSelectorOf[ci > 63 ? 63 : ci];
            }
        }

        void AddPredictorsDc(int refFrame)
        {
            for (int b = 0; b < 6; b++)
            {
                int ai = _aboveIdx[b], li = B6To4[b];
                int count = 0, dc = 0;
                if (refFrame == _leftRef[li]) { dc += _leftDc[li]; count++; }
                if (refFrame == _aboveRef[ai]) { dc += _aboveDc[ai]; count++; }
                int pi = B2P[b] * 4 + refFrame;
                if (count == 0) dc = _prevDc[pi];
                else if (count == 2) dc /= 2;
                int bo = b * 64;
                short v = (short)(_block[bo] + dc);
                _prevDc[pi] = v;
                _aboveDc[ai] = v;
                _aboveRef[ai] = refFrame;
                _leftDc[li] = v;
                _leftRef[li] = refFrame;
                _block[bo] = (short)(v * _dequantDc);
            }
        }

        // ---- reconstruction ----

        void Render(int row, int col, int type, int refFrame)
        {
            var cur = _cur;
            Vp6Picture rf = refFrame == RefPrevious ? _prev : refFrame == RefGolden ? _golden : null;
            if (type != MbIntra && rf == null)
            {
                for (int b = 0; b < 6; b++) Array.Clear(_block, b * 64, 64);
                return;
            }
            for (int b = 0; b < 6; b++)
            {
                byte[] dst, src;
                int stride, x, y, pw, ph;
                if (b < 4)
                {
                    dst = cur.Y;
                    src = rf?.Y;
                    stride = pw = cur.Width;
                    ph = cur.Height;
                    x = col * 16 + ((b & 1) << 3);
                    y = row * 16 + ((b & 2) << 2);
                }
                else
                {
                    dst = b == 4 ? cur.U : cur.V;
                    src = rf == null ? null : b == 4 ? rf.U : rf.V;
                    stride = pw = cur.ChromaWidth;
                    ph = cur.ChromaHeight;
                    x = col * 8;
                    y = row * 8;
                }
                int dOff = y * stride + x;
                switch (type)
                {
                    case MbIntra:
                        Idct(b, dst, dOff, stride, false);
                        break;
                    case MbInterNovecPf:
                    case MbInterNovecGf:
                        for (int r = 0; r < 8; r++) Buffer.BlockCopy(src, dOff + r * stride, dst, dOff + r * stride, 8);
                        IdctAdd(b, dst, dOff, stride);
                        break;
                    default:
                        Mc(src, dst, dOff, stride, pw, ph, x, y, _mvX[b], _mvY[b], b < 4);
                        IdctAdd(b, dst, dOff, stride);
                        break;
                }
            }
        }

        /// <summary>Motion compensation of one 8x8 block: quarter-pel luma / eighth-pel chroma, bilinear, edges clamped.</summary>
        void Mc(byte[] src, byte[] dst, int dOff, int stride, int pw, int ph, int bx, int by, int mvx, int mvy, bool luma)
        {
            int shift = luma ? 2 : 3, mask = luma ? 3 : 7;
            int fx = mvx & mask, fy = mvy & mask;
            int ix = bx + (mvx >> shift), iy = by + (mvy >> shift);
            byte[] s = src;
            int sOff, sStride = stride;
            if (ix < 0 || iy < 0 || ix + 9 > pw || iy + 9 > ph)
            {
                // edge emulation: a clamped 9x9 copy
                var t = _mcTmp;
                for (int r = 0; r < 9; r++)
                {
                    int yy = iy + r;
                    yy = yy < 0 ? 0 : yy >= ph ? ph - 1 : yy;
                    int rowOff = yy * stride;
                    for (int k = 0; k < 9; k++)
                    {
                        int xx = ix + k;
                        xx = xx < 0 ? 0 : xx >= pw ? pw - 1 : xx;
                        t[r * 16 + k] = s[rowOff + xx];
                    }
                }
                s = t;
                sOff = 0;
                sStride = 16;
            }
            else sOff = iy * stride + ix;

            if (fx == 0 && fy == 0)
            {
                for (int r = 0; r < 8; r++) Buffer.BlockCopy(s, sOff + r * sStride, dst, dOff + r * stride, 8);
                return;
            }
            if (luma)
            {
                fx <<= 1;
                fy <<= 1;
            }
            if (fx == 0 || fy == 0)
            {
                // single-direction bilinear: (a*(8-f) + b*f) * 8 + 32 >> 6
                int a = (8 - fx) * (8 - fy), e = fx * (8 - fy) + (8 - fx) * fy;
                int step = fy != 0 ? sStride : 1;
                for (int r = 0; r < 8; r++)
                {
                    int so = sOff + r * sStride, d0 = dOff + r * stride;
                    for (int k = 0; k < 8; k++) dst[d0 + k] = (byte)((a * s[so + k] + e * s[so + k + step] + 32) >> 6);
                }
                return;
            }
            // diagonal: horizontal pass over 9 rows, then vertical, both rounded (vp6_filter_diag2)
            int ha = (8 - fx) * 8, hb = fx * 8, va = (8 - fy) * 8, vb = fy * 8;
            var tmp = _diag;
            for (int r = 0; r < 9; r++)
            {
                int so = sOff + r * sStride;
                for (int k = 0; k < 8; k++) tmp[r * 8 + k] = (byte)((ha * s[so + k] + hb * s[so + k + 1] + 32) >> 6);
            }
            for (int r = 0; r < 8; r++)
            {
                int d0 = dOff + r * stride;
                for (int k = 0; k < 8; k++) dst[d0 + k] = (byte)((va * tmp[r * 8 + k] + vb * tmp[r * 8 + 8 + k] + 32) >> 6);
            }
        }

        readonly byte[] _diag = new byte[72];
        readonly int[] _type4 = new int[4];

        const int C1 = 64277, C2 = 60547, C3 = 54491, C4 = 46341, C5 = 36410, C6 = 25080, C7 = 12785;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static int M(int a, int b) => (a * b) >> 16;

        void IdctAdd(int b, byte[] dst, int dOff, int stride)
        {
            if (_idctSel[b] <= 1)
            {
                // DC only (vp3 idct_dc_add)
                int bo = b * 64;
                int dc = (_block[bo] + 15) >> 5;
                _block[bo] = 0;
                for (int r = 0; r < 8; r++)
                {
                    int o = dOff + r * stride;
                    for (int k = 0; k < 8; k++)
                    {
                        int v = dst[o + k] + dc;
                        dst[o + k] = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                    }
                }
                return;
            }
            Idct(b, dst, dOff, stride, true);
        }

        /// <summary>
        /// VP3/VP6 integer inverse DCT of block b (natural order): rows, then columns with +8 >> 4; put adds 128, add adds to
        /// dst. Clears the block afterwards.
        /// </summary>
        void Idct(int b, byte[] dst, int dOff, int stride, bool add)
        {
            var blk = _block;
            int bo = b * 64;
            var t = _tmp;
            // rows (horizontal 1-D transforms), results kept as int16 like the reference
            for (int i = 0; i < 8; i++)
            {
                int o = bo + i * 8;
                int i0 = blk[o], i1 = blk[o + 1], i2 = blk[o + 2], i3 = blk[o + 3], i4 = blk[o + 4], i5 = blk[o + 5], i6 = blk[o + 6], i7 = blk[o + 7];
                int to = i * 8;
                if ((i0 | i1 | i2 | i3 | i4 | i5 | i6 | i7) == 0)
                {
                    t[to] = t[to + 1] = t[to + 2] = t[to + 3] = t[to + 4] = t[to + 5] = t[to + 6] = t[to + 7] = 0;
                    continue;
                }
                int A = M(C1, i1) + M(C7, i7);
                int B = M(C7, i1) - M(C1, i7);
                int C = M(C3, i3) + M(C5, i5);
                int D = M(C3, i5) - M(C5, i3);
                int Ad = M(C4, A - C), Bd = M(C4, B - D);
                int Cd = A + C, Dd = B + D;
                int E = M(C4, i0 + i4), F = M(C4, i0 - i4);
                int G = M(C2, i2) + M(C6, i6), H = M(C6, i2) - M(C2, i6);
                int Ed = E - G, Gd = E + G, Add = F + Ad, Bdd = Bd - H, Fd = F - Ad, Hd = Bd + H;
                t[to] = (short)(Gd + Cd);
                t[to + 7] = (short)(Gd - Cd);
                t[to + 1] = (short)(Add + Hd);
                t[to + 2] = (short)(Add - Hd);
                t[to + 3] = (short)(Ed + Dd);
                t[to + 4] = (short)(Ed - Dd);
                t[to + 5] = (short)(Fd + Bdd);
                t[to + 6] = (short)(Fd - Bdd);
            }
            Array.Clear(blk, bo, 64);
            // columns
            for (int i = 0; i < 8; i++)
            {
                int i0 = t[i], i1 = t[8 + i], i2 = t[16 + i], i3 = t[24 + i], i4 = t[32 + i], i5 = t[40 + i], i6 = t[48 + i], i7 = t[56 + i];
                int d = dOff + i;
                if ((i1 | i2 | i3 | i4 | i5 | i6 | i7) == 0)
                {
                    int v = (C4 * i0 + (8 << 16)) >> 20;
                    if (!add)
                    {
                        v += 128;
                        byte bv = (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
                        for (int r = 0; r < 8; r++) dst[d + r * stride] = bv;
                    }
                    else if (i0 != 0)
                        for (int r = 0; r < 8; r++)
                        {
                            int o = d + r * stride, x = dst[o] + v;
                            dst[o] = (byte)(x < 0 ? 0 : x > 255 ? 255 : x);
                        }
                    continue;
                }
                int A = M(C1, i1) + M(C7, i7);
                int B = M(C7, i1) - M(C1, i7);
                int C = M(C3, i3) + M(C5, i5);
                int D = M(C3, i5) - M(C5, i3);
                int Ad = M(C4, A - C), Bd = M(C4, B - D);
                int Cd = A + C, Dd = B + D;
                int E = M(C4, i0 + i4) + 8, F = M(C4, i0 - i4) + 8;
                if (!add)
                {
                    E += 16 * 128;
                    F += 16 * 128;
                }
                int G = M(C2, i2) + M(C6, i6), H = M(C6, i2) - M(C2, i6);
                int Ed = E - G, Gd = E + G, Add = F + Ad, Bdd = Bd - H, Fd = F - Ad, Hd = Bd + H;
                if (!add)
                {
                    dst[d] = Clip((Gd + Cd) >> 4);
                    dst[d + 7 * stride] = Clip((Gd - Cd) >> 4);
                    dst[d + stride] = Clip((Add + Hd) >> 4);
                    dst[d + 2 * stride] = Clip((Add - Hd) >> 4);
                    dst[d + 3 * stride] = Clip((Ed + Dd) >> 4);
                    dst[d + 4 * stride] = Clip((Ed - Dd) >> 4);
                    dst[d + 5 * stride] = Clip((Fd + Bdd) >> 4);
                    dst[d + 6 * stride] = Clip((Fd - Bdd) >> 4);
                }
                else
                {
                    dst[d] = Clip(dst[d] + ((Gd + Cd) >> 4));
                    dst[d + 7 * stride] = Clip(dst[d + 7 * stride] + ((Gd - Cd) >> 4));
                    dst[d + stride] = Clip(dst[d + stride] + ((Add + Hd) >> 4));
                    dst[d + 2 * stride] = Clip(dst[d + 2 * stride] + ((Add - Hd) >> 4));
                    dst[d + 3 * stride] = Clip(dst[d + 3 * stride] + ((Ed + Dd) >> 4));
                    dst[d + 4 * stride] = Clip(dst[d + 4 * stride] + ((Ed - Dd) >> 4));
                    dst[d + 5 * stride] = Clip(dst[d + 5 * stride] + ((Fd + Bdd) >> 4));
                    dst[d + 6 * stride] = Clip(dst[d + 6 * stride] + ((Fd - Bdd) >> 4));
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static byte Clip(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
    }
}
