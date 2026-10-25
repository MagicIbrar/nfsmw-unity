using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;
using UnityEngine.Rendering;

namespace NfsMw.CarRender
{
    /// <summary>
    /// The car materials of speed.exe (specs/car_render.md §2-§4): every solid part gets the NFSMW/Car shader with the
    /// constants of its eLightMaterial (sub_6C76E0), after CarRenderInfo's replacement tables (CarRender.cpp:1191-1290)
    /// have swapped its texture (skin → composite skin, light states, window damage, decals) and its light material
    /// (CARSKIN → the paint's, WINDSHIELD → the tint's, MAG* → the rim paint's, CARSKIN on a carbon hood → CARBONFIBER).
    /// </summary>
    public static class CarSurfaces
    {
        static Dictionary<uint, LightMaterial> _lightMaterials;
        static Dictionary<string, CarTypeRenderInfo> _types;
        static Shader _shader;
        static readonly Dictionary<(int tex, uint lm, int blend, float cut), Material> Shared = new Dictionary<(int, uint, int, float), Material>();

        public static readonly uint DefaultAlpha = EaHash.Hash("DEFAULTALPHA"), WindowFront = EaHash.Hash("WINDOW_FRONT"),
            RearDefroster = EaHash.Hash("REAR_DEFROSTER"), WindowDamage0 = EaHash.Hash("WINDOW_DAMAGE0"), CarbonFibre = EaHash.Hash("CARBONFIBRE"),
            GlobalSkin1 = EaHash.Hash("GLOBAL_SKIN1"), SpoilerSkin1 = EaHash.Hash("SPOILER_SKIN1"), RoofSkin = EaHash.Hash("ROOF_SKIN"),
            CarbonSkin = 0xA7366AE6, GlobalCarbonSkin = 0x3C84D757, TrafficTire = EaHash.Hash("TRAFFIC_TIRE");

        /// <summary>GLOBAL\GLOBALB light materials (156 on PC), with the PC glass alpha overrides.</summary>
        public static Dictionary<uint, LightMaterial> LightMaterials
        {
            get
            {
                if (_lightMaterials != null) return _lightMaterials;
                _lightMaterials = new Dictionary<uint, LightMaterial>();
                try
                {
                    GameData.Mount();
                    foreach (var f in new[] { "GLOBAL/GLOBALB.lzc", "GLOBAL/GLOBALB.BUN" })
                    {
                        var p = Path.Combine(GameData.Root, f);
                        if (!File.Exists(p)) continue;
                        _lightMaterials = Core.LightMaterials.Read(GameFiles.ReadBundle(p));
                        if (_lightMaterials.Count > 0) break;
                    }
                }
                catch (Exception e) { Debug.LogWarning("NFSMW car render: light materials: " + e.Message); }
                return _lightMaterials;
            }
        }

        /// <summary>CarTypeInfo (Skinnable, usage, default base paint) by car type name.</summary>
        public static CarTypeRenderInfo Type(string car)
        {
            if (_types == null)
            {
                _types = new Dictionary<string, CarTypeRenderInfo>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    GameData.Mount();
                    var p = Path.Combine(GameData.Root, "GLOBAL", "GlobalB.lzc");
                    if (File.Exists(p)) _types = CarTypeRenderInfo.Read(GameFiles.ReadBundle(p));
                }
                catch (Exception e) { Debug.LogWarning("NFSMW car render: car types: " + e.Message); }
            }
            return car != null && _types.TryGetValue(car, out var t) ? t : null;
        }

        /// <summary>elGetLightMaterial (0x502820): unknown names give the DEFAULT material.</summary>
        public static LightMaterial Get(uint hash)
        {
            var map = LightMaterials;
            if (map.TryGetValue(hash, out var m)) return m;
            return map.TryGetValue(Core.LightMaterials.Default, out var d) ? d : null;
        }

        public enum BlendKind { Opaque = 0, Alpha = 1, Additive = 2, Subtractive = 3 }

        /// <summary>TextureInfo AlphaBlendType → blend (1 blend, 2/4 additive, 3 subtractive); AlphaUsage 1 = punch-through.</summary>
        public static (BlendKind blend, float cutoff) BlendOf(GameTexture src)
        {
            if (src == null) return (BlendKind.Opaque, -1f);
            var b = src.AlphaBlend switch { 1 => BlendKind.Alpha, 2 => BlendKind.Additive, 4 => BlendKind.Additive, 3 => BlendKind.Subtractive, _ => BlendKind.Opaque };
            return (b, b == BlendKind.Opaque && src.AlphaUsage == 1 ? 0.5f : -1f);
        }

        /// <summary>A new car material: texture, light material constants and blend state.</summary>
        public static Material Create(Texture tex, LightMaterial lm, BlendKind blend, float cutoff, string name)
        {
            if (_shader == null) _shader = Shader.Find("NFSMW/Car");
            var m = new Material(_shader) { name = name };
            if (tex) m.mainTexture = tex;
            m.SetFloat("_Cutoff", cutoff);
            Apply(m, lm);
            SetBlend(m, blend);
            return m;
        }

        /// <summary>The light material's effect constants (LightMaterial.Shader, sub_6C76E0) on a material.</summary>
        public static void Apply(Material m, LightMaterial lm)
        {
            if (lm == null) return;
            var c = lm.Shader();
            Vector4 V(float[] f) => new Vector4(f[0], f[1], f[2], f[3]);
            m.SetVector("_DiffuseMin", V(c.DiffuseMin));
            m.SetVector("_DiffuseRange", V(c.DiffuseRange));
            m.SetVector("_SpecularMin", V(c.SpecularMin));
            m.SetVector("_SpecularRange", V(c.SpecularRange));
            m.SetVector("_EnvmapMin", V(c.EnvmapMin));
            m.SetVector("_EnvmapRange", V(c.EnvmapRange));
            m.SetFloat("_SpecularPower", c.SpecularPower);
            m.SetFloat("_EnvmapPower", c.EnvmapPower);
        }

        public static void SetBlend(Material m, BlendKind blend)
        {
            switch (blend)
            {
                case BlendKind.Opaque:
                    m.SetFloat("_SrcBlend", (float)BlendMode.One); m.SetFloat("_DstBlend", (float)BlendMode.Zero); m.SetFloat("_ZWrite", 1);
                    m.renderQueue = m.GetFloat("_Cutoff") >= 0 ? 2450 : 2000;
                    m.SetShaderPassEnabled("ShadowCaster", true);
                    break;
                default:
                    m.SetFloat("_SrcBlend", (float)BlendMode.SrcAlpha);
                    m.SetFloat("_DstBlend", (float)(blend == BlendKind.Alpha ? BlendMode.OneMinusSrcAlpha : BlendMode.One));
                    m.SetFloat("_ZWrite", 0);
                    m.renderQueue = 3000;
                    m.SetShaderPassEnabled("ShadowCaster", false);
                    break;
            }
        }

        /// <summary>A shared (not per-car) material for a game texture and a light material.</summary>
        public static Material SharedFor(uint texHash, uint lmHash)
        {
            var lib = GameData.Textures;
            var tex = lib.Get(texHash);
            var (blend, cut) = BlendOf(lib.Source(texHash));
            var key = (tex ? tex.GetInstanceID() : (int)texHash, lmHash, (int)blend, cut);
            if (Shared.TryGetValue(key, out var m) && m) return m;
            m = Create(tex, Get(lmHash), blend, cut, (tex ? tex.name : $"tex_{texHash:X8}") + "/" + (Get(lmHash)?.Name ?? lmHash.ToString("X8")));
            Shared[key] = m;
            return m;
        }

        // ---- composite skins (CarSkin.cpp) ---------------------------------------------------------------------------

        static readonly Dictionary<string, List<GameTexture>> VinylPacks = new Dictionary<string, List<GameTexture>>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, Texture2D> Composites = new Dictionary<string, Texture2D>();
        static readonly List<string> CompositeOrder = new List<string>();

        /// <summary>CARS\&lt;car&gt;\VINYLS.BIN, kept raw (only two textures of it are ever used at once).</summary>
        public static List<GameTexture> VinylPack(string car)
        {
            if (VinylPacks.TryGetValue(car, out var l)) return l;
            l = new List<GameTexture>();
            var path = Path.Combine(GameData.Root, "CARS", car, "VINYLS.BIN");
            if (File.Exists(path))
            {
                var d = File.ReadAllBytes(path);
                l = TexturePack.ReadAll(d, 0, d.Length);
            }
            VinylPacks[car] = l;
            while (VinylPacks.Count > 6) VinylPacks.Remove(VinylPacks.Keys.First(k => k != car));
            return l;
        }

        static readonly Dictionary<Texture2D, int> Refs = new Dictionary<Texture2D, int>();

        static Texture2D Remember(string key, Texture2D t)
        {
            Composites[key] = t;
            CompositeOrder.Add(key);
            while (CompositeOrder.Count > 16)
            {
                string old = CompositeOrder[0];
                CompositeOrder.RemoveAt(0);
                if (CompositeOrder.Contains(old) || !Composites.TryGetValue(old, out var o)) continue;
                Composites.Remove(old);
                if (o && (!Refs.TryGetValue(o, out int n) || n <= 0)) Kill(o);
            }
            return t;
        }

        /// <summary>A car holds its composites while it uses them; an evicted composite is destroyed after its last user.</summary>
        public static void Acquire(Texture2D t)
        {
            if (!t) return;
            Refs[t] = Refs.TryGetValue(t, out int n) ? n + 1 : 1;
        }

        static void Kill(UnityEngine.Object o)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);
        }

        public static void Release(Texture2D t)
        {
            if (!t || !Refs.TryGetValue(t, out int n)) return;
            if (--n > 0) { Refs[t] = n; return; }
            Refs.Remove(t);
            if (!Composites.ContainsValue(t)) Kill(t);
        }

        public static Texture2D ToTexture(uint[] bgra, int w, int h, string name, bool mips = true)
        {
            var px = new Color32[w * h];
            // rows in file order, as TextureLibrary.ToUnity uploads the game textures (the mesh UVs expect that)
            for (int i = 0; i < px.Length; i++)
            {
                uint c = bgra[i];
                px[i] = new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24));
            }
            var t = new Texture2D(w, h, TextureFormat.RGBA32, mips) { name = name, wrapMode = TextureWrapMode.Repeat };
            t.SetPixels32(px);
            t.Apply(mips, true);
            return t;
        }

        /// <summary>
        /// CompositeSkin (0x748AD0): DUMMY_SKIN = BASE_PAINT colour (alpha GLOSS) with the VINYL_LAYER0 part's
        /// &lt;CAR&gt;_&lt;TEXTURE&gt; and &lt;CAR&gt;_&lt;TEXTURE&gt;_MASK from VINYLS.BIN, REMAP vinyls recoloured by VINYL_COLOUR0_0..2
        /// (an unset colour j is 0xFF &lt;&lt; 8j). A vinyl with NUMCOLOURS 0 aborts the composite.
        /// </summary>
        public static Texture2D Skin(string car, uint baseColour, string vinylTexture, bool remap, uint[] remapColours)
        {
            string key = $"{car}|{baseColour:X8}|{vinylTexture}|{remap}|{(remapColours == null ? "" : string.Join(",", remapColours))}";
            if (Composites.TryGetValue(key, out var t) && t) return t;
            uint[] layer = null, mask = null;
            int w = 512, h = 512;   // DUMMY_SKIN1..4 are 512x512 A8R8G8B8
            if (!string.IsNullOrEmpty(vinylTexture))
            {
                var pack = VinylPack(car);
                uint lh = EaHash.Hash(car + "_" + vinylTexture), mh = EaHash.Hash(car + "_" + vinylTexture + "_MASK");
                var lt = pack.FirstOrDefault(x => x.Hash == lh);
                var mt = pack.FirstOrDefault(x => x.Hash == mh);
                if (lt != null && mt != null && lt.Width == w && lt.Height == h && mt.Width == w && mt.Height == h)
                {
                    layer = SkinComposite.Pixels(lt);
                    mask = SkinComposite.Pixels(mt);
                }
            }
            var px = SkinComposite.Skin(w, h, baseColour, layer, mask, remap, remapColours);
            return Remember(key, ToTexture(px, w, h, $"DUMMY_SKIN {car}"));
        }

        /// <summary>CompositeRim (CarSkin.cpp:1088): DUMMY_WHEEL = &lt;rim&gt;_WHEEL coloured by PAINT_RIM through &lt;rim&gt;_WHEEL_INNER_MASK.</summary>
        public static Texture2D Wheel(uint textureName, uint colour)
        {
            if (textureName == 0) return null;
            string key = $"wheel|{textureName:X8}|{colour:X8}";
            if (Composites.TryGetValue(key, out var t) && t) return t;
            var lib = GameData.Textures;
            var src = lib.Source(EaHash.Hash("_WHEEL", textureName));
            var mask = lib.Source(EaHash.Hash("_WHEEL_INNER_MASK", textureName));
            if (src == null || mask == null || src.Width != mask.Width || src.Height != mask.Height) return null;
            var a = SkinComposite.Pixels(src);
            var b = SkinComposite.Pixels(mask);
            if (a == null || b == null) return null;
            return Remember(key, ToTexture(SkinComposite.Wheel(a, b, colour), src.Width, src.Height, $"DUMMY_WHEEL {textureName:X8}"));
        }

        /// <summary>A part's RED/GREEN/BLUE/GLOSS as a BGRA word (0 when no part).</summary>
        public static uint PartColour(CarPartDb.Part p) =>
            p == null ? 0u : SkinComposite.Colour(p.I(CarPartDb.Attr.RED), p.I(CarPartDb.Attr.GREEN), p.I(CarPartDb.Attr.BLUE), p.I(CarPartDb.Attr.GLOSS));

        public static readonly uint LightMaterialName = EaHash.Hash("LIGHT_MATERIAL_NAME"), Texture = EaHash.Hash("TEXTURE"),
            NumColours = EaHash.Hash("NUMCOLOURS"), Remap = EaHash.Hash("REMAP"), Name = EaHash.Hash("NAME"), Size = EaHash.Hash("SIZE"), Shape = EaHash.Hash("SHAPE");
    }

    /// <summary>
    /// The ride's visual parts as RideInfo holds them (CarInfo.cpp SetStockParts / SetPart): the installed part of a
    /// slot or the slot's stock part, plus what CarRenderInfo derives from them.
    /// </summary>
    public sealed class CarLook
    {
        public string Car;
        public CarTypeRenderInfo Type;
        public bool Skinnable;
        public OwnedCar Owned;
        public Color Paint = Color.white;
        public CarPartDb.Part BasePaint, Vinyl, Rim, RimPaint, Tint, Hood, Body, Headlight, Brakelight;
        public readonly CarPartDb.Part[] VinylColours = new CarPartDb.Part[4];
        /// <summary>Decal texture parts by slot (83..130).</summary>
        public readonly Dictionary<int, CarPartDb.Part> Decals = new Dictionary<int, CarPartDb.Part>();

        public const int SlotBody = 23, SlotHeadlight = 64, SlotBrakelight = 65, SlotHood = 63, SlotFrontWheel = 66, SlotBasePaint = 76,
            SlotVinyl = 77, SlotPaintRim = 78, SlotVinylColour0 = 79, SlotDecalTex0 = 83, SlotWindowTint = 131;

        static readonly string[] StockVinylColours = { "VINYL_L1_COLOR01", "VINYL_L1_COLOR03", "VINYL_L2_COLOR11", "VINYL_L1_COLOR01" };
        static readonly Dictionary<(string, int), CarPartDb.Part> StockCache = new Dictionary<(string, int), CarPartDb.Part>();

        static CarPartDb.Part Cached(string car, int key, Func<CarPartDb.Part> find)
        {
            if (StockCache.TryGetValue((car, key), out var p)) return p;
            p = find();
            StockCache[(car, key)] = p;
            return p;
        }

        /// <summary>RideInfo::SetStockParts' part of a slot (cached per car type; the database scan is long).</summary>
        public static CarPartDb.Part Stock(string car, int slot) => Cached(car, slot, () => CarParts.Stock(car, slot));

        public static CarLook For(string car, OwnedCar owned, Color paint)
        {
            var l = new CarLook { Car = car.ToUpperInvariant(), Owned = owned, Paint = paint };
            l.Type = CarSurfaces.Type(l.Car);
            l.Skinnable = l.Type?.Skinnable ?? true;
            CarPartDb db;
            try { db = CarParts.Db; } catch { db = null; }
            if (db == null) return l;
            CarPartDb.Part Get(int slot)
            {
                var p = owned != null ? CarParts.Installed(owned, slot) : null;
                return p ?? Stock(l.Car, slot);
            }
            l.BasePaint = owned != null ? CarParts.Installed(owned, SlotBasePaint) : null;
            l.Vinyl = owned != null ? CarParts.Installed(owned, SlotVinyl) : null;
            l.Rim = Get(SlotFrontWheel);
            l.RimPaint = owned != null ? CarParts.Installed(owned, SlotPaintRim) : null;
            l.Tint = Get(SlotWindowTint);
            l.Hood = Get(SlotHood);
            l.Body = Get(SlotBody);
            l.Headlight = owned != null ? CarParts.Installed(owned, SlotHeadlight) : null;
            l.Brakelight = owned != null ? CarParts.Installed(owned, SlotBrakelight) : null;
            for (int j = 0; j < 4; j++)
            {
                var p = owned != null ? CarParts.Installed(owned, SlotVinylColour0 + j) : null;
                l.VinylColours[j] = p ?? Cached(l.Car, -1 - j, () => db.PartsFor(l.Car, SlotVinylColour0 + j).FirstOrDefault(x => x.Name == StockVinylColours[j]));
            }
            if (owned != null)
                for (int i = 0; i < owned.PartSlots.Count; i++)
                {
                    int s = owned.PartSlots[i];
                    if (s >= SlotDecalTex0 && s <= 130 && CarParts.Installed(owned, s) is CarPartDb.Part dp) l.Decals[s] = dp;
                }
            return l;
        }

        /// <summary>The part whose light material paints CARSKIN: the installed BASE_PAINT, else the car type's default paint.</summary>
        public CarPartDb.Part PaintMaterialPart
        {
            get
            {
                if (BasePaint != null) return BasePaint;
                if (Type == null) return null;
                try
                {
                    var t = Type;
                    return Cached(Car, -100, () => CarParts.Db.PartsFor(Car, SlotBasePaint).FirstOrDefault(p => EaHash.Hash(p.Name ?? "") == t.DefaultBasePaint || p.NameHash == t.DefaultBasePaint));
                }
                catch { return null; }
            }
        }

        public uint LightMaterialOf(CarPartDb.Part p) => p == null ? 0u : p.U(CarSurfaces.LightMaterialName);

        /// <summary>The BASE_PAINT colour word: the part's RGB/GLOSS, else the given paint colour with gloss 0x80 [?: the
        /// stock paints all have GLOSS 0x80].</summary>
        public uint BaseColour => BasePaint != null ? CarSurfaces.PartColour(BasePaint)
            : SkinComposite.Colour(Mathf.RoundToInt(Paint.r * 255), Mathf.RoundToInt(Paint.g * 255), Mathf.RoundToInt(Paint.b * 255), 0x80);

        /// <summary>The vinyl texture name: the part's TEXTURE string, else the legacy OwnedCar.Vinyl texture name.</summary>
        public string VinylTexture
        {
            get
            {
                if (Vinyl != null) return Vinyl.S(CarSurfaces.Texture);
                string legacy = Owned?.Vinyl;
                return !string.IsNullOrEmpty(legacy) && legacy.StartsWith(Car + "_") ? legacy.Substring(Car.Length + 1) : null;
            }
        }

        public bool VinylRemap => Vinyl != null && Vinyl.I(CarSurfaces.Remap) != 0;

        /// <summary>m_RemapColours: VINYL_COLOUR0_j's colour, or 0xFF &lt;&lt; 8j when the slot is empty.</summary>
        public uint[] RemapColours
        {
            get
            {
                var c = new uint[4];
                for (int j = 0; j < 4; j++) c[j] = VinylColours[j] != null ? CarSurfaces.PartColour(VinylColours[j]) : 0xFFu << (j * 8);
                return c;
            }
        }

        public bool CarbonHood => Hood != null && Hood.I(CarPartDb.Attr.CARBONFIBRE) != 0;

        /// <summary>A key of everything the materials depend on (Restyle rebuilds only when it changes).</summary>
        public string Key
        {
            get
            {
                var sb = new System.Text.StringBuilder(Car);
                sb.Append('|').Append(BaseColour.ToString("X8")).Append('|').Append(VinylTexture).Append(VinylRemap);
                foreach (var c in RemapColours) sb.Append(',').Append(c.ToString("X8"));
                sb.Append('|').Append(Rim?.Name).Append('|').Append(RimPaint?.Name).Append('|').Append(Tint?.Name).Append('|').Append(CarbonHood);
                foreach (var kv in Decals.OrderBy(k => k.Key)) sb.Append('|').Append(kv.Key).Append('=').Append(kv.Value.Name).Append(kv.Value.U(CarSurfaces.Name).ToString("X"));
                return sb.ToString();
            }
        }
    }
}
