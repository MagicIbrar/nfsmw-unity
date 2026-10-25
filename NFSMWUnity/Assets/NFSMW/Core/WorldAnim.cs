using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>WorldAnimInstance (0xA0, chunk 0x37090; decomp Animation/AnimWorldTypes.hpp): one placed animation tree.</summary>
    public sealed class WorldAnimInstance
    {
        public uint UniqueId, TreeHash, PlayFlags, StartTrigger, StopTrigger, LodB, LodZ, NamedRange, EventTrack, TrackNum;
        public int Section;
        public float Speed, MasterDelay, LoopDelay;
        public ushort BeginRange, EndRange;
        public short TrackDir;
        public float[] Matrix = Mat.Identity();   // world placement (game axes, row vectors)
        public Vec3 BBoxMin, BBoxMax;

        internal static WorldAnimInstance Read(byte[] d, int o) => new WorldAnimInstance
        {
            UniqueId = Chunks.U32(d, o), TreeHash = Chunks.U32(d, o + 4), Section = Chunks.I32(d, o + 8), PlayFlags = Chunks.U32(d, o + 0xC),
            Speed = Chunks.F32(d, o + 0x10), MasterDelay = Chunks.F32(d, o + 0x14), LoopDelay = Chunks.F32(d, o + 0x18),
            BeginRange = Chunks.U16(d, o + 0x1C), EndRange = Chunks.U16(d, o + 0x1E),
            StartTrigger = Chunks.U32(d, o + 0x20), StopTrigger = Chunks.U32(d, o + 0x24), LodB = Chunks.U32(d, o + 0x28), LodZ = Chunks.U32(d, o + 0x2C),
            NamedRange = Chunks.U32(d, o + 0x30), EventTrack = Chunks.U32(d, o + 0x34), TrackNum = Chunks.U32(d, o + 0x38), TrackDir = Chunks.I16(d, o + 0x3C),
            Matrix = Mat.Read(d, o + 0x40), BBoxMin = Vec3.Read(d, o + 0x80), BBoxMax = Vec3.Read(d, o + 0x90),
        };
    }

    /// <summary>WorldAnimEntityInfo (0x70, chunk 0x37080; CWorldAnimEntity::Init 0x44F340).</summary>
    public sealed class WorldAnimEntity
    {
        public uint TypeId, InstanceHash, ParentHash, ModelHash, LodB, LodZ, TreeHash, AnimHash;
        public byte ContentFlags;   // 1: "_t", 2: "_q", 4: "_s" channels (GetAnimChannelHash = bStringHash(suffix, anim hash))
        public int ParentIndex;
        public float[] LocalMatrix = Mat.Identity();

        /// <summary>The tree root (mAnimTreeHash == mThisInstanceNameHash) has no WorldModel.</summary>
        public bool IsRoot => TreeHash == InstanceHash;

        internal static WorldAnimEntity Read(byte[] d, int o) => new WorldAnimEntity
        {
            TypeId = Chunks.U32(d, o), InstanceHash = Chunks.U32(d, o + 4), ParentHash = Chunks.U32(d, o + 8), ModelHash = Chunks.U32(d, o + 0xC),
            LodB = Chunks.U32(d, o + 0x10), LodZ = Chunks.U32(d, o + 0x14), TreeHash = Chunks.U32(d, o + 0x20), AnimHash = Chunks.U32(d, o + 0x24),
            ContentFlags = d[o + 0x28], ParentIndex = (sbyte)d[o + 0x29], LocalMatrix = Mat.Read(d, o + 0x30),
        };

        public uint ChannelHash(int dof) => dof == 0 ? EaHash.Hash("_t", AnimHash) : dof == 1 ? EaHash.Hash("_q", AnimHash) : EaHash.Hash("_s", AnimHash);
    }

    public sealed class WorldAnimTree
    {
        public uint Hash;
        public WorldAnimInstance Instance;
        public readonly List<WorldAnimEntity> Entities = new List<WorldAnimEntity>();
    }

    /// <summary>
    /// World animations of a track (TRACKS\L2RA.BUN; LoaderWorldAnimDirectoryData 0x44F9D0 / LoaderWorldAnimEntityData
    /// 0x44CB70 / LoaderWorldAnimTreeMarker 0x44F730 / LoaderWorldAnimInstanceEntry 0x44F080): 0x37090 instances,
    /// 0x37150 the instance control list, then per tree an anim bank 0xE34010, its entities 0x37080 and a tree marker
    /// 0x37110 (named ranges). The retail track has 11 trees: tower crane arms, a sailboat, a cargo ship, a blimp and
    /// an airliner. Each animated entity's local matrix becomes the "ROOT" skeleton's bone 1 pose from its t/q/s
    /// channels (CWorldAnimEntity::UpdateTimeStep); the root entity sits at the instance matrix.
    /// </summary>
    public sealed class WorldAnimSet
    {
        public readonly List<WorldAnimTree> Trees = new List<WorldAnimTree>();
        public readonly Dictionary<uint, EaglAnim> Anims = new Dictionary<uint, EaglAnim>();

        public static WorldAnimSet Load(byte[] d)
        {
            var set = new WorldAnimSet();
            var instances = new List<WorldAnimInstance>();
            var entities = new List<WorldAnimEntity>();
            foreach (var c in Chunks.Read(d, 0, d.Length))
            {
                switch (c.Id)
                {
                    case 0x37090:
                    {
                        int o = c.DataStart;
                        while (o + 4 <= c.End && Chunks.U32(d, o) == 0x11111111) o += 4;
                        for (; o + 0xA0 <= c.End; o += 0xA0) instances.Add(WorldAnimInstance.Read(d, o));
                        break;
                    }
                    case 0x37080:
                    {
                        int o = c.DataStart;
                        while (o + 4 <= c.End && Chunks.U32(d, o) == 0x11111111) o += 4;
                        for (; o + 0x70 <= c.End; o += 0x70) entities.Add(WorldAnimEntity.Read(d, o));
                        break;
                    }
                    case 0xE34010:
                        foreach (var a in EaglAnimBankReader.Read(d, c.DataStart, c.End)) set.Anims[a.NameHash] = a;
                        break;
                }
            }
            foreach (var inst in instances)
            {
                var tree = new WorldAnimTree { Hash = inst.TreeHash, Instance = inst };
                foreach (var e in entities) if (e.TreeHash == inst.TreeHash) tree.Entities.Add(e);
                tree.Entities.Sort((a, b) => a.ParentIndex.CompareTo(b.ParentIndex));   // CompareParentIndex
                set.Trees.Add(tree);
            }
            return set;
        }

        public EaglAnim Anim(uint hash) => Anims.TryGetValue(hash, out var a) ? a : null;

        /// <summary>
        /// The entity's local matrix at a time in frames: the t/q/s channels evaluated into bone 1 of the identity
        /// "ROOT" skeleton (GLOBAL\InGameB.bun; both bones are identity), so global[1] is the bone 1 SQT itself.
        /// False when the entity has no channel in the bank.
        /// </summary>
        public bool Pose(WorldAnimEntity e, float frame, float[] sqt, float[] outLocal)
        {
            bool any = false;
            // GetStillPose (0x49FF90) of ROOT: S 1, length scale (slot 3) 1, Q identity, T 0, slot 11 1
            for (int i = 0; i < 24; i++) sqt[i] = 0;
            sqt[0] = sqt[1] = sqt[2] = sqt[3] = sqt[7] = sqt[11] = 1; sqt[12] = sqt[13] = sqt[14] = sqt[15] = sqt[19] = sqt[23] = 1;
            for (int dof = 0; dof < 3; dof++)
            {
                if ((e.ContentFlags & (1 << dof)) == 0) continue;
                var a = Anim(e.ChannelHash(dof));
                if (a == null) continue;
                a.Eval(frame, sqt);
                any = true;
            }
            if (!any) return false;
            // PoseSQTToGlobal (0x49FCF0) of bone 1 (bone 0 is identity): the quaternion as evaluated (0x493FF0 does not
            // renormalise), column 0 × the length scale of slot 15
            float qx = sqt[16], qy = sqt[17], qz = sqt[18], qw = sqt[19];
            float sx = sqt[12], sy = sqt[13], sz = sqt[14], len = sqt[15];
            var m = outLocal;
            m[0] = (1 - 2 * (qy * qy + qz * qz)) * sx * len; m[1] = 2 * (qx * qy + qz * qw) * sx; m[2] = 2 * (qx * qz - qy * qw) * sx; m[3] = 0;
            m[4] = 2 * (qx * qy - qz * qw) * sy * len; m[5] = (1 - 2 * (qx * qx + qz * qz)) * sy; m[6] = 2 * (qy * qz + qx * qw) * sy; m[7] = 0;
            m[8] = 2 * (qx * qz + qy * qw) * sz * len; m[9] = 2 * (qy * qz - qx * qw) * sz; m[10] = (1 - 2 * (qx * qx + qy * qy)) * sz; m[11] = 0;
            m[12] = sqt[20]; m[13] = sqt[21]; m[14] = sqt[22]; m[15] = 1;
            return true;
        }

        /// <summary>
        /// CWorldAnimCtrl::CreateFnAnimFromBank (WorldAnimCtrl.cpp:119-128): m_animLength = FnAnim::GetLength of each dof
        /// created, in order t, q, s — the last one created wins (the frame count, EaglAnim.Length).
        /// </summary>
        public float Length(WorldAnimEntity e)
        {
            float len = 0;
            for (int dof = 0; dof < 3; dof++)
                if ((e.ContentFlags & (1 << dof)) != 0 && Anim(e.ChannelHash(dof)) is EaglAnim a) len = a.Length;
            return len;
        }

        /// <summary>bRandom(r, &amp;seed) (bWare/Src/bMath.cpp:83-96): r · 4.656613e-10 · (seed % 0x7FFFFFFF), then the seed steps.</summary>
        public static float BRandom(float r, ref uint seed)
        {
            float v = r * 4.656613e-10f * (seed % 0x7FFFFFFF);
            seed = seed * 1103515245u + 12345u;   // [?] the LCG step; every caller here reseeds with 0x69BABE69 first
            return v;
        }
    }
}
