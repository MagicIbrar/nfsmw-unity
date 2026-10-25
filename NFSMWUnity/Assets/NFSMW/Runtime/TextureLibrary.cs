using System;
using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw
{
    /// <summary>Texture hash -> GameTexture source, converted to a Unity texture the first time it is used.</summary>
    public sealed class TextureLibrary
    {
        readonly Dictionary<uint, GameTexture> _src = new Dictionary<uint, GameTexture>();
        readonly Dictionary<uint, Texture2D> _tex = new Dictionary<uint, Texture2D>();
        public readonly HashSet<uint> Missing = new HashSet<uint>();

        /// <summary>Called when a hash is not known; may Add() the texture (e.g. by loading the section that owns it).</summary>
        public Func<uint, bool> Resolver;

        public int Count => _src.Count;

        public void Add(IEnumerable<GameTexture> textures, bool replace = false)
        {
            foreach (var t in textures)
                if (t.Data != null && (replace || !_src.ContainsKey(t.Hash)))
                    _src[t.Hash] = t;
        }

        public bool Contains(uint hash) => _src.ContainsKey(hash);
        public IEnumerable<GameTexture> All => _src.Values;
        public GameTexture Source(uint hash) => _src.TryGetValue(hash, out var t) ? t : null;

        public Texture2D Get(uint hash)
        {
            if (hash == 0) return null;
            if (_tex.TryGetValue(hash, out var tex) && tex) return tex; // (edit-mode scene changes can unload it)
            if (!_src.TryGetValue(hash, out var src) && !(Resolver != null && Resolver(hash) && _src.TryGetValue(hash, out src)))
            {
                Missing.Add(hash);
                return null;
            }
            tex = ToUnity(src);
            _tex[hash] = tex;
            return tex;
        }

        /// <summary>DXT1/DXT5 upload as-is, DXT3 is transcoded to DXT5 (Unity has no BC2), BGRA/P8 become RGBA32.</summary>
        public static Texture2D ToUnity(GameTexture t)
        {
            int mips = Math.Max(1, TexturePack.UsableMips(t));
            Texture2D tex;
            switch (t.Format)
            {
                case TexFormat.Dxt1:
                case TexFormat.Dxt5:
                case TexFormat.Dxt3:
                {
                    var fmt = t.Format == TexFormat.Dxt1 ? TextureFormat.DXT1 : TextureFormat.DXT5;
                    int size = 0;
                    for (int i = 0; i < mips; i++) size += TexturePack.LevelSize(t.Format, t.Width >> i, t.Height >> i);
                    var raw = new byte[size];
                    Buffer.BlockCopy(t.Data, 0, raw, 0, size);
                    if (t.Format == TexFormat.Dxt3) Dxt3ToDxt5(raw);
                    tex = new Texture2D(t.Width, t.Height, fmt, mips, false);
                    tex.LoadRawTextureData(raw);
                    tex.Apply(false, true);
                    break;
                }
                case TexFormat.Bgra32:
                {
                    tex = new Texture2D(t.Width, t.Height, TextureFormat.BGRA32, mips, false);
                    int size = 0;
                    for (int i = 0; i < mips; i++) size += TexturePack.LevelSize(t.Format, t.Width >> i, t.Height >> i);
                    var raw = new byte[size];
                    Buffer.BlockCopy(t.Data, 0, raw, 0, size);
                    tex.LoadRawTextureData(raw);
                    tex.Apply(false, true);
                    break;
                }
                case TexFormat.P8 when t.Palette != null:
                {
                    tex = new Texture2D(t.Width, t.Height, TextureFormat.RGBA32, true, false);
                    var px = new Color32[t.Width * t.Height];
                    for (int i = 0; i < px.Length && i < t.Data.Length; i++)
                    {
                        int p = t.Data[i] * 4;
                        if (p + 3 >= t.Palette.Length) continue;
                        px[i] = new Color32(t.Palette[p + 2], t.Palette[p + 1], t.Palette[p], t.Palette[p + 3]);
                    }
                    tex.SetPixels32(px);
                    tex.Apply(true, true);
                    break;
                }
                default:
                    tex = Texture2D.grayTexture;
                    return tex;
            }
            tex.name = t.Name;
            tex.wrapModeU = (t.TileUV & 1) != 0 ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            tex.wrapModeV = (t.TileUV & 2) != 0 ? TextureWrapMode.Repeat : TextureWrapMode.Clamp;
            tex.filterMode = FilterMode.Trilinear;
            tex.anisoLevel = 4;
            return tex;
        }

        // DXT3 explicit 4-bit alpha -> DXT5 interpolated alpha with endpoints 255/0 (8 evenly spaced levels)
        static readonly byte[] AlphaToIndex = BuildAlphaTable();

        static byte[] BuildAlphaTable()
        {
            // DXT5 8-alpha mode with a0=255, a1=0: index 0 = 255, 1 = 0, 2..7 = (8-i)/7*255
            var levels = new float[8];
            levels[0] = 255;
            levels[1] = 0;
            for (int i = 2; i < 8; i++) levels[i] = ((8 - i) * 255f + (i - 1) * 0f) / 7f;
            var table = new byte[16];
            for (int a = 0; a < 16; a++)
            {
                float v = a * 17f;
                int best = 0;
                for (int i = 1; i < 8; i++)
                    if (Math.Abs(levels[i] - v) < Math.Abs(levels[best] - v)) best = i;
                table[a] = (byte)best;
            }
            return table;
        }

        static void Dxt3ToDxt5(byte[] blocks)
        {
            for (int b = 0; b + 16 <= blocks.Length; b += 16)
            {
                ulong bits = 0;
                for (int p = 0; p < 16; p++)
                {
                    int a4 = (blocks[b + p / 2] >> ((p & 1) * 4)) & 0xF;
                    bits |= (ulong)AlphaToIndex[a4] << (p * 3);
                }
                blocks[b] = 255;
                blocks[b + 1] = 0;
                for (int i = 0; i < 6; i++) blocks[b + 2 + i] = (byte)(bits >> (i * 8));
            }
        }
    }
}
