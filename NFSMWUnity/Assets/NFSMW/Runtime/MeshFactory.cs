using System;
using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace NfsMw
{
    /// <summary>Game space is right-handed Z-up (X east, Y north); Unity is left-handed Y-up. Swapping Y and Z converts.</summary>
    public static class Coords
    {
        public static Vector3 ToUnity(float x, float y, float z) => new Vector3(x, z, y);
        public static Vector3 ToUnity(Vec3 v) => new Vector3(v.X, v.Z, v.Y);
        public static Vector3 ToGame(Vector3 u) => new Vector3(u.x, u.z, u.y);
        public static Vec3 ToGame3(Vector3 u) => new Vec3(u.x, u.z, u.y);

        /// <summary>Scenery rotation (rows = local axes in game space, row-vector convention) + position -> Unity matrix.</summary>
        public static Matrix4x4 InstanceMatrix(float[] r, Vec3 p)
        {
            // M_unity = S * R^T * S with S swapping Y/Z; R^T columns are the game rows.
            int[] s = { 0, 2, 1 };
            var m = Matrix4x4.identity;
            for (int i = 0; i < 3; i++)
                for (int j = 0; j < 3; j++)
                    m[i, j] = r[s[j] * 3 + s[i]];
            m.SetColumn(3, new Vector4(p.X, p.Z, p.Y, 1));
            return m;
        }
    }

    public enum PartKind { World, Car }

    /// <summary>Builds one Unity mesh per solid (a submesh per material) and shares materials by texture.</summary>
    public sealed class MeshFactory
    {
        readonly TextureLibrary _textures;
        readonly Dictionary<(uint, int), Material> _materials = new Dictionary<(uint, int), Material>();
        static Shader _world, _worldTransparent, _worldDecal, _car;

        public MeshFactory(TextureLibrary textures)
        {
            _textures = textures;
            if (_world == null)
            {
                _world = Shader.Find("NFSMW/World");
                _worldTransparent = Shader.Find("NFSMW/WorldTransparent");
                _worldDecal = Shader.Find("NFSMW/WorldDecal");
                _car = Shader.Find("NFSMW/Car");
            }
        }

        public sealed class Built
        {
            public Mesh Mesh;
            public Material[] Materials;
            public List<SolidPart> Parts;
        }

        public Built Build(Solid solid, PartKind kind, Func<SolidPart, int> materialVariant = null)
        {
            var parts = solid.Parts;
            int vcount = 0;
            foreach (var p in parts) vcount += p.VertexCount;
            if (vcount == 0) return null;

            var verts = new Vector3[vcount];
            var normals = new Vector3[vcount];
            var uvs = new Vector2[vcount];
            var colors = new Color32[vcount];
            var subs = new List<int[]>();
            var mats = new List<Material>();
            var used = new List<SolidPart>();
            int vo = 0;
            foreach (var p in parts)
            {
                int n = p.VertexCount;
                for (int i = 0; i < n; i++)
                {
                    verts[vo + i] = Coords.ToUnity(p.Positions[i * 3], p.Positions[i * 3 + 1], p.Positions[i * 3 + 2]);
                    normals[vo + i] = Coords.ToUnity(p.Normals[i * 3], p.Normals[i * 3 + 1], p.Normals[i * 3 + 2]);
                    uvs[vo + i] = new Vector2(p.Uvs[i * 2], p.Uvs[i * 2 + 1]);
                    uint c = p.Colors[i];
                    colors[vo + i] = new Color32((byte)(c >> 16), (byte)(c >> 8), (byte)c, (byte)(c >> 24));
                }
                var idx = new int[p.Indices.Length];
                // the Y/Z swap mirrors the mesh; pick the winding that faces along the stored normals
                double facing = 0;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = p.Indices[t], b = p.Indices[t + 1], c2 = p.Indices[t + 2];
                    var cr = Vector3.Cross(verts[vo + b] - verts[vo + a], verts[vo + c2] - verts[vo + a]);
                    facing += Vector3.Dot(cr, normals[vo + a] + normals[vo + b] + normals[vo + c2]);
                }
                bool flip = facing < 0;
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    int a = p.Indices[t], b = p.Indices[t + 1], c2 = p.Indices[t + 2];
                    bool f = flip;
                    idx[t] = vo + a;
                    idx[t + 1] = vo + (f ? c2 : b);
                    idx[t + 2] = vo + (f ? b : c2);
                }
                subs.Add(idx);
                int variant = materialVariant?.Invoke(p) ?? 0;
                mats.Add(MaterialFor(p, kind, variant));
                used.Add(p);
                vo += n;
            }

            var mesh = new Mesh { name = solid.Name, indexFormat = vcount > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            mesh.vertices = verts;
            mesh.normals = normals;
            mesh.uv = uvs;
            mesh.colors32 = colors;
            mesh.subMeshCount = subs.Count;
            for (int i = 0; i < subs.Count; i++) mesh.SetTriangles(subs[i], i, false);
            mesh.RecalculateBounds();
            return new Built { Mesh = mesh, Materials = mats.ToArray(), Parts = used };
        }

        /// <summary>variant: 0 by texture alpha usage, 1 transparent (glass), 2 car paint, 3 decal (baked shadows), 4 opaque.</summary>
        public Material MaterialFor(SolidPart part, PartKind kind, int variant)
        {
            uint hash = part.TextureHash;
            int key = (int)kind * 16 + variant;
            if (_materials.TryGetValue((hash, key), out var m)) return m;
            var tex = _textures.Get(hash);
            if (kind == PartKind.Car)
            {
                if (variant == 1)
                {
                    m = new Material(_worldTransparent);
                    m.SetFloat("_VertexScale", 1f);
                    m.SetColor("_Color", new Color(0.55f, 0.6f, 0.65f, 0.75f));
                }
                else
                {
                    m = new Material(_car);
                    m.SetFloat("_Paint", variant == 2 ? 1f : 0f);
                    m.SetFloat("_Gloss", variant == 2 ? 0.85f : 0.25f);
                }
            }
            else
            {
                // Ecstasy Texture.hpp: AlphaBlendType decides blending (1 blend, 2 additive, 3 subtractive); otherwise
                // usage 1 is alpha test and everything else is opaque. A "modulated" texture with SRCCOPY blending keeps a
                // gloss/reflection mask in alpha (window facades, brick, pavement) and is drawn solid.
                var src = _textures.Source(hash);
                int usage = src?.AlphaUsage ?? 0, blend = src?.AlphaBlend ?? 0;
                if (variant == 3 && blend == 0) blend = 1;          // baked shadow solids always blend
                if (variant != 1) variant = blend != 0 ? 3 : 0;
                m = new Material(variant == 1 ? _worldTransparent : variant == 3 ? _worldDecal : _world);
                if (variant == 0) m.SetFloat("_Cutoff", usage == 1 ? 0.5f : -1f);
                if (variant == 3)
                {
                    if (src != null) m.renderQueue = 2470 + Mathf.Clamp(src.RenderOrder, 0, 20);
                    bool add = blend == 2 || blend == 4 || blend == 3;
                    m.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
                    m.SetFloat("_DstBlend", (float)(add ? UnityEngine.Rendering.BlendMode.One : UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha));
                    m.SetFloat("_BlendOp", (float)(blend == 3 ? UnityEngine.Rendering.BlendOp.ReverseSubtract : UnityEngine.Rendering.BlendOp.Add));
                }
                m.enableInstancing = true;
            }
            m.name = tex ? tex.name : $"tex_{hash:X8}";
            if (tex) m.mainTexture = tex;
            TextureScroll.Register(m, _textures.Source(hash));
            _materials[(hash, key)] = m;
            return m;
        }
    }
}
