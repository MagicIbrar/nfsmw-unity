using System;
using System.Collections.Generic;
using NfsMw.Core;
using UnityEngine;
using UnityEngine.Rendering;

namespace NfsMw.Game
{
    /// <summary>
    /// A NIS character (CBasicCharacterAnimEntity, decomp Animation/AnimEntity_BasicCharacter.cpp; Init 0x44C5E0,
    /// RenderEffects 0x449CF0, FindWorldBonePosition 0x4467A0): an arrest cop, perp or the handcuffs. Its skinned solid
    /// is posed by its skeletal animation and skinned on the CPU every frame [ours: the exe skins on the GPU in
    /// worldbone.efx with 16-bone palettes; the result is the same]. Normals stay in the bind pose, as worldbone.efx
    /// lights the unskinned normal. The entity sits on the ground under its pelvis bone (mKeepOnGround, +0.05 m) and draws
    /// the CHARACTERSHADOW quad between its feet.
    /// </summary>
    public sealed class NisCharacter : MonoBehaviour
    {
        public NisCharacterInfo Info;
        public EaglSkeleton Skeleton;
        /// <summary>dof 0: the skeletal animation (mSkelAnimNameHash replaces mTransAnimNameHash when skinned).</summary>
        public EaglAnim Anim;
        /// <summary>dof 1 / dof 2: mQuatsAnimNameHash / mScaleAnimNameHash (evaluated before dof 0; none in the retail bundles).</summary>
        public EaglAnim QuatsAnim, ScaleAnim;
        public SkinnedSolid Solid;
        /// <summary>The CAnimCtrl (frames; flags, time scale, master delay, loop range from the entity info).</summary>
        public readonly AnimCtrl Ctrl = new AnimCtrl();
        public string ModelName => Solid != null ? Solid.Name : "";
        /// <summary>CBasicCharacterAnimEntity::mBoneMapType: 2 for a 48-bone biped, 3 for "Bip23" (the perp: face pixelation), -1 otherwise.</summary>
        public int BoneMapType = -1;
        public bool Hidden;

        // BoneMap (AnimEntity_BasicCharacter.cpp:25): left foot, right foot, left hand, right hand, head
        static readonly int[,] BoneMap = { { 0x05, 0x09, 0x12, 0x1C, 0x1E }, { 0x05, 0x08, 0x0F, 0x15, 0x1E }, { 0x04, 0x08, 0x0F, 0x15, 0x1C }, { 0x04, 0x08, 0x0F, 0x15, 0x1C } };

        Matrix4x4 _scene;            // scene frame (Unity): columns right, up, forward, origin
        Mesh _mesh;
        Vector3[] _verts;
        readonly List<(SkinnedPart part, int offset)> _parts = new List<(SkinnedPart, int)>();
        float[] _sqt, _still;
        float[][] _global, _skin;
        float _groundY;
        bool _haveGround;
        Transform _shadow;
        Mesh _shadowMesh;
        static Shader _shader, _shadowShader;
        static Texture2D _shadowTex;
        static readonly Dictionary<NisContent, TextureLibrary> _libs = new Dictionary<NisContent, TextureLibrary>();

        /// <summary>
        /// Builds the character for one entity of the scene. <paramref name="scene"/> maps the scene frame (game axes:
        /// x right, y forward, z up, the frame of the car tracks) into Unity world space.
        /// </summary>
        public static NisCharacter Spawn(NisContent content, NisCharacterInfo info, Matrix4x4 scene, Transform parent)
        {
            var skel = content.Skeleton(info.SkelName);
            var solid = content.Solid(info);
            if (skel == null || solid == null || solid.Parts.Count == 0) return null;   // Init returns false without a skeleton
            // Init (AnimEntity_BasicCharacter.cpp:161-177): trans/quats/scale anims into dofs 0/1/2, then the skeletal anim
            // into dof 0 (time scale × 0.5); nothing allocated → Init false and the entity is dropped (CreateAnimEntities)
            bool skeletal = info.SkelName != 0 && info.SkelAnim != 0;
            var anim = skeletal ? content.Anim(info.SkelAnim) ?? content.Anim(info.TransAnim) : content.Anim(info.TransAnim);
            var quats = content.Anim(info.QuatsAnim);
            var scale = content.Anim(info.ScaleAnim);
            if (anim == null && quats == null && scale == null) return null;
            var go = new GameObject("NIS " + solid.Name);
            if (parent) go.transform.SetParent(parent, false);
            var c = go.AddComponent<NisCharacter>();
            c.Info = info;
            c.Skeleton = skel;
            c.Solid = solid;
            c.Anim = anim;
            c.QuatsAnim = quats;
            c.ScaleAnim = scale;
            c._scene = scene;
            // CAnimCtrl setup: flags, time scale, master delay (SetMasterDelayTime sets 0x80), loop range (0x40, scaled by the
            // halved time scale); m_animLength is the last-created dof's length (order t, q, s, then the skeletal anim)
            c.Ctrl.Flags = info.CtrlFlags;
            c.Ctrl.TimeScale = info.TimeScale;
            c.Ctrl.AnimLength = (anim ?? scale ?? quats).Length;
            c.Ctrl.SetMasterDelayTime(info.PlayDelay);
            if ((info.PlayFlags & 0x40) != 0) c.Ctrl.SetLoopRange(info.LoopStart, info.LoopEnd);
            if (skeletal && skel.Bones.Length == 0x30) c.BoneMapType = skel.NameHash == EaHash.Hash("Bip23") ? 3 : 2;
            c.Build(LibraryFor(content));
            c.Pose(true);
            return c;
        }

        /// <summary>CBasicCharacterAnimEntity::UpdateTimeStep (:257-311): AdvanceAnimTime(dt) then the pose.</summary>
        public void Advance(float dt)
        {
            Ctrl.Advance(dt);
            Pose(false);
        }

        /// <summary>CBasicCharacterAnimEntity::SetTime (:250): SetEvalTime(0), then one UpdateTimeStep(t).</summary>
        public void SetTime(float t)
        {
            Ctrl.SetEvalTime(0f);
            Advance(t);
        }

        static TextureLibrary LibraryFor(NisContent content)
        {
            if (_libs.TryGetValue(content, out var lib)) return lib;
            lib = new TextureLibrary();
            lib.Add(content.Textures);
            lib.Resolver = h =>
            {
                var t = GameData.Textures?.Source(h);
                if (t == null) return false;
                lib.Add(new[] { t });
                return true;
            };
            _libs[content] = lib;
            return lib;
        }

        void Build(TextureLibrary lib)
        {
            if (!_shader) _shader = Shader.Find("NFSMW/Character") ?? Shader.Find("Unlit/Texture");
            int n = 0;
            foreach (var p in Solid.Parts) { _parts.Add((p, n)); n += p.VertexCount; }
            _verts = new Vector3[n];
            var normals = new Vector3[n];
            var uvs = new Vector2[n];
            var mats = new Material[_parts.Count];
            _mesh = new Mesh { name = Solid.Name, indexFormat = n > 65000 ? IndexFormat.UInt32 : IndexFormat.UInt16 };
            _mesh.MarkDynamic();
            foreach (var (p, o) in _parts)
                for (int i = 0; i < p.VertexCount; i++)
                {
                    _verts[o + i] = new Vector3(p.Positions[i * 3], p.Positions[i * 3 + 2], p.Positions[i * 3 + 1]);
                    normals[o + i] = new Vector3(p.Normals[i * 3], p.Normals[i * 3 + 2], p.Normals[i * 3 + 1]);
                    uvs[o + i] = new Vector2(p.Uvs[i * 2], p.Uvs[i * 2 + 1]);
                }
            _mesh.vertices = _verts;
            _mesh.normals = normals;
            _mesh.uv = uvs;
            _mesh.subMeshCount = _parts.Count;
            for (int s = 0; s < _parts.Count; s++)
            {
                var (p, o) = _parts[s];
                // the Y/Z swap mirrors the mesh: keep the winding that faces along the stored normals (as MeshFactory)
                double facing = 0;
                for (int t = 0; t + 2 < p.Indices.Length; t += 3)
                {
                    int a = o + p.Indices[t], b = o + p.Indices[t + 1], c = o + p.Indices[t + 2];
                    facing += Vector3.Dot(Vector3.Cross(_verts[b] - _verts[a], _verts[c] - _verts[a]), normals[a] + normals[b] + normals[c]);
                }
                var idx = new int[p.Indices.Length];
                for (int t = 0; t + 2 < idx.Length; t += 3)
                {
                    idx[t] = o + p.Indices[t];
                    idx[t + 1] = o + p.Indices[facing < 0 ? t + 2 : t + 1];
                    idx[t + 2] = o + p.Indices[facing < 0 ? t + 1 : t + 2];
                }
                _mesh.SetTriangles(idx, s, false);
                var m = new Material(_shader) { name = Solid.Name + "/" + p.MaterialName };
                var tex = lib.Get(p.TextureHash);
                if (tex) m.mainTexture = tex;
                // the material's light material (sub_6C76E0 constants): DiffuseMin / DiffuseRange of worldbone.efx
                var lms = NfsMw.CarRender.CarSurfaces.LightMaterials;
                if (lms != null && (lms.TryGetValue(p.LightMaterialHash, out var lmat) || lms.TryGetValue(LightMaterials.Default, out lmat)))
                {
                    var k = lmat.Shader();
                    m.SetVector("_DiffuseMin", new Vector4(k.DiffuseMin[0], k.DiffuseMin[1], k.DiffuseMin[2], k.DiffuseMin[3]));
                    m.SetVector("_DiffuseRange", new Vector4(k.DiffuseRange[0], k.DiffuseRange[1], k.DiffuseRange[2], k.DiffuseRange[3]));
                }
                mats[s] = m;
            }
            _mesh.RecalculateBounds();
            gameObject.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var mr = gameObject.AddComponent<MeshRenderer>();
            mr.sharedMaterials = mats;
            mr.shadowCastingMode = ShadowCastingMode.Off;   // the exe draws the CHARACTERSHADOW quad instead
            _still = Skeleton.StillPose();
            _sqt = (float[])_still.Clone();
            _global = new float[Skeleton.Bones.Length][];
            _skin = new float[Skeleton.Bones.Length][];
            BuildShadow(lib);
        }

        void BuildShadow(TextureLibrary lib)
        {
            if (BoneMapType < 0) return;
            if (!_shadowTex)
            {
                uint h = EaHash.Hash("CHARACTERSHADOW");   // InitCharacterEffects: GetTextureInfo(bStringHash("CHARACTERSHADOW"))
                _shadowTex = lib.Get(h) ?? GameData.Textures?.Get(h);
            }
            if (!_shadowTex) return;   // RenderEffects skips the shadow without the texture
            if (!_shadowShader) _shadowShader = Shader.Find("NFSMW/CharacterShadow");
            if (!_shadowShader) return;
            var go = new GameObject("CHARACTERSHADOW");
            go.transform.SetParent(transform.parent, false);
            _shadow = go.transform;
            _shadowMesh = new Mesh { name = "CHARACTERSHADOW" };
            _shadowMesh.MarkDynamic();
            _shadowMesh.vertices = new Vector3[4];
            _shadowMesh.uv = new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), new Vector2(0, 1) };
            _shadowMesh.colors32 = new[] { new Color32(0x80, 0x80, 0x80, 0x80), new Color32(0x80, 0x80, 0x80, 0x80), new Color32(0x80, 0x80, 0x80, 0x80), new Color32(0x80, 0x80, 0x80, 0x80) };
            _shadowMesh.triangles = new[] { 0, 1, 2, 0, 2, 3, 0, 2, 1, 0, 3, 2 };
            go.AddComponent<MeshFilter>().sharedMesh = _shadowMesh;
            var mr = go.AddComponent<MeshRenderer>();
            mr.sharedMaterial = new Material(_shadowShader) { mainTexture = _shadowTex };
            mr.shadowCastingMode = ShadowCastingMode.Off;
        }

        /// <summary>The CAnimCtrl's eval time in frames (CAnimCtrl::AdvanceAnimTime 0x4494D0, see <see cref="AnimCtrl"/>).</summary>
        public float Frame => Ctrl.Eval;

        /// <summary>
        /// CAnimCtrl::UpdateAnimPose (AnimCtrl.cpp:238-276: dof 1 quats, dof 2 scale, then dof 0 into the SQT buffer,
        /// PoseSQTToGlobal, skin) and the entity update: keep on the ground (mKeepOnGround), place the shadow.
        /// </summary>
        void Pose(bool init)
        {
            if (Skeleton == null || _mesh == null) return;
            Array.Copy(_still, _sqt, _sqt.Length);
            float frame = Ctrl.Eval;
            QuatsAnim?.Eval(frame, _sqt);
            ScaleAnim?.Eval(frame, _sqt);
            Anim?.Eval(frame, _sqt);
            Skeleton.PoseGlobal(_sqt, _global);
            Skeleton.Skin(_global, _skin);
            foreach (var (p, o) in _parts)
                for (int i = 0; i < p.VertexCount; i++)
                {
                    float x = p.Positions[i * 3], y = p.Positions[i * 3 + 1], z = p.Positions[i * 3 + 2];
                    float rx = 0, ry = 0, rz = 0;
                    for (int k = 0; k < 3; k++)
                    {
                        float w = p.Weights[i * 3 + k];
                        if (w == 0) continue;
                        var m = _skin[p.Bones[i * 3 + k]];
                        rx += (x * m[0] + y * m[4] + z * m[8] + m[12]) * w;
                        ry += (x * m[1] + y * m[5] + z * m[9] + m[13]) * w;
                        rz += (x * m[2] + y * m[6] + z * m[10] + m[14]) * w;
                    }
                    _verts[o + i] = new Vector3(rx, rz, ry);
                }
            _mesh.vertices = _verts;
            _mesh.RecalculateBounds();

            // entity placement: scene frame * local matrix, then the ground offset (mKeepOnGround)
            var local = ToUnity(Info.LocalMatrix);
            var world = _scene * local;
            Vector3 pos = world.GetColumn(3);
            var rot = Quaternion.LookRotation(world.GetColumn(2), world.GetColumn(1));
            var pelvis = world.MultiplyPoint3x4(BonePosition(1));
            if (Skeleton.Bones.Length > 1)
            {
                // GetWorldHeightAtPointRigorous under the pelvis: Init (:199) probes at the pelvis itself and takes the height
                // as is; UpdateTimeStep (:278) raises the probe 2 m and averages 50/50 with the previous height; the entity is
                // lifted so it stands 0.05 m above it (local.z += (height − nonAdjusted) + 0.05, converging to ground + 0.05)
                if (GroundAt(init ? pelvis : pelvis + Vector3.up * 2f, out float ground))
                {
                    _groundY = init || !_haveGround ? ground : ground * 0.5f + _groundY * 0.5f;
                    _haveGround = true;
                }
                if (_haveGround) pos.y = _groundY + 0.05f;
            }
            // SetLocalVelocity((local.v3 − last)/dt) feeds the renderer's motion blur [not consumed here]
            transform.SetPositionAndRotation(pos, rot);
            var r = GetComponent<MeshRenderer>();
            if (r) r.enabled = !Hidden;
            UpdateShadow();
        }

        /// <summary>Bone position in the entity's local space (Unity axes): the translation of its global matrix.</summary>
        public Vector3 BonePosition(int bone)
        {
            if (_global == null || bone < 0 || bone >= _global.Length || _global[bone] == null) return Vector3.zero;
            var m = _global[bone];
            return new Vector3(m[12], m[14], m[13]);
        }

        public Vector3 BoneWorld(int bone) => transform.TransformPoint(BonePosition(bone));

        /// <summary>The head bone (BoneMap Head) in world space, for FacePixelation::SetLocation.</summary>
        public Vector3 HeadWorld => BoneMapType >= 0 ? BoneWorld(BoneMap[BoneMapType, 4]) : transform.position + Vector3.up * 1.6f;

        void UpdateShadow()
        {
            if (!_shadow) return;
            _shadow.gameObject.SetActive(!Hidden);
            if (Hidden) return;
            var lf = BoneWorld(BoneMap[BoneMapType, 0]);
            var rf = BoneWorld(BoneMap[BoneMapType, 1]);
            var par = new Vector2(1, 0);
            if (lf.x != rf.x || lf.z != rf.z) par = new Vector2(lf.x - rf.x, lf.z - rf.z).normalized;
            var perp = new Vector2(-par.y, par.x) * 0.35f;
            par *= 0.25f;
            float y = transform.position.y + 0.01f;
            Vector3 P(Vector3 f, Vector2 a, Vector2 b) => new Vector3(f.x + a.x + b.x, y, f.z + a.y + b.y);
            var v = new[]
            {
                P(lf, par, -perp), P(lf, par, perp), P(rf, -par, perp), P(rf, -par, -perp),
            };
            _shadowMesh.vertices = v;
            _shadowMesh.RecalculateBounds();
        }

        static bool GroundAt(Vector3 from, out float y)
        {
            y = 0;
            float best = float.MaxValue;
            bool hit = false;
            foreach (var h in Physics.RaycastAll(from, Vector3.down, 8f, ~0, QueryTriggerInteraction.Ignore))
                if (!h.rigidbody && h.distance < best) { best = h.distance; y = h.point.y; hit = true; }
            return hit;
        }

        /// <summary>A game-space bMatrix4 (row vectors, Z up) as a Unity matrix (column vectors, Y up): P * M^T * P.</summary>
        public static Matrix4x4 ToUnity(float[] m)
        {
            int[] p = { 0, 2, 1, 3 };
            var u = new Matrix4x4();
            for (int i = 0; i < 4; i++)
                for (int j = 0; j < 4; j++)
                    u[i, j] = m[p[j] * 4 + p[i]];
            return u;
        }

        void OnDestroy()
        {
            if (_mesh) Destroy(_mesh);
            if (_shadow) Destroy(_shadow.gameObject);
            if (_shadowMesh) Destroy(_shadowMesh);
            var mr = GetComponent<MeshRenderer>();
            if (mr) foreach (var m in mr.sharedMaterials) if (m) Destroy(m);
        }
    }
}
