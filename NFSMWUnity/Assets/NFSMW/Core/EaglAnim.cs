using System;
using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// The relocatable MIPS ELF that wraps every EAGL4 object (skeletons 0xE34009, animation banks 0xE34010): only
    /// .data and the symbol table matter. .rel.data only turns section offsets into pointers, so offsets inside the
    /// copied .data are the pointers (EAGL4::DynamicLoader, eagl4supportdlopen.cpp; speed.exe 0x4914C0..).
    /// </summary>
    public sealed class EaglElf
    {
        public byte[] Data = Array.Empty<byte>();
        public readonly List<(string name, int value, int size)> Symbols = new List<(string, int, int)>();

        public static EaglElf Parse(byte[] d, int start, int end)
        {
            int elf = start;
            while (elf + 4 < end && !(d[elf] == 0x7F && d[elf + 1] == 0x45 && d[elf + 2] == 0x4C && d[elf + 3] == 0x46)) elf++;
            if (elf + 52 > end) return null;
            int shoff = Chunks.I32(d, elf + 32), shnum = Chunks.U16(d, elf + 48);
            int dataOff = -1, dataSize = 0, symOff = -1, symSize = 0, strOff = -1;
            for (int i = 0; i < shnum; i++)
            {
                int sh = elf + shoff + i * 40;
                if (sh + 40 > d.Length) return null;
                int type = Chunks.I32(d, sh + 4), off = Chunks.I32(d, sh + 16), size = Chunks.I32(d, sh + 20), link = Chunks.I32(d, sh + 24);
                if (type == 1 && dataOff < 0) { dataOff = elf + off; dataSize = size; }
                else if (type == 2) { symOff = elf + off; symSize = size; strOff = elf + Chunks.I32(d, elf + shoff + link * 40 + 16); }
            }
            if (dataOff < 0 || dataOff + dataSize > d.Length) return null;
            var e = new EaglElf { Data = new byte[dataSize] };
            Buffer.BlockCopy(d, dataOff, e.Data, 0, dataSize);
            if (symOff >= 0)
                for (int s = symOff; s + 16 <= symOff + symSize && s + 16 <= d.Length; s += 16)
                {
                    string name = Chunks.CString(d, strOff + Chunks.I32(d, s), 256);
                    if (name.Length > 0) e.Symbols.Add((name, Chunks.I32(d, s + 4), Chunks.I32(d, s + 8)));
                }
            return e;
        }
    }

    /// <summary>EAGL4Anim::BoneData (0x70): S, parent, Q, T, mirror bone, inverse bind matrix (row-major, row vectors).</summary>
    public sealed class EaglBone
    {
        public string Name = "";
        public int Parent, Mirror;
        public readonly float[] S = new float[3], Q = new float[4], T = new float[3];
        public readonly float[] InvBase = new float[16];
    }

    /// <summary>
    /// EAGL4Anim::Skeleton from chunk 0xE34009 (LoaderEAGLSkeletons 0x44B2C0, CAnimSkeleton::DynamicLoadResolve
    /// 0x447BD0): symbol "__Skeleton:::Name" points at SkeletonData {u16 core checksum, u16 flags, u32 ?, i32 bone count,
    /// float* inv bone scales} followed by BoneData[count]; each "__Bone:::Skel.Bone" symbol points at that bone's index.
    /// GetSkeletonFromList (0x44B280) matches bStringHash(name). The bind pose rebuilds InvBase exactly with
    /// local = diag(S) * R(Q) + T, global = local * parent global (checked on all 48 bones of Bip20).
    /// </summary>
    public sealed class EaglSkeleton
    {
        public string Name = "";
        public uint NameHash;
        public ushort CheckSum;
        public EaglBone[] Bones = Array.Empty<EaglBone>();
        /// <summary>SkeletonData +0xC: the per-bone length scales (null when the pointer is 0: every retail skeleton).</summary>
        public float[] BoneLengthScales;

        public int Find(string boneName)
        {
            for (int i = 0; i < Bones.Length; i++)
                if (string.Equals(Bones[i].Name, boneName, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        public static List<EaglSkeleton> Read(byte[] d, int start, int end)
        {
            var list = new List<EaglSkeleton>();
            var elf = EaglElf.Parse(d, start, end);
            if (elf == null) return list;
            var data = elf.Data;
            foreach (var (name, value, _) in elf.Symbols)
            {
                if (!name.StartsWith("__Skeleton:::", StringComparison.Ordinal) || value < 0 || value + 16 > data.Length) continue;
                var sk = new EaglSkeleton { Name = name.Substring(13), CheckSum = Chunks.U16(data, value) };
                sk.NameHash = EaHash.Hash(sk.Name);
                int n = Chunks.I32(data, value + 8);
                if (n <= 0 || n > 256 || value + 16 + n * 0x70 > data.Length) continue;
                sk.Bones = new EaglBone[n];
                int scales = Chunks.I32(data, value + 12);   // a .data offset (.rel.data makes it a pointer); 0 = none
                if (scales > 0 && scales + n * 4 <= data.Length)
                {
                    sk.BoneLengthScales = new float[n];
                    for (int i = 0; i < n; i++) sk.BoneLengthScales[i] = Chunks.F32(data, scales + i * 4);
                }
                for (int i = 0; i < n; i++)
                {
                    int b = value + 16 + i * 0x70;
                    var bone = new EaglBone { Parent = Chunks.I32(data, b + 12), Mirror = Chunks.I32(data, b + 44) };
                    for (int k = 0; k < 3; k++) { bone.S[k] = Chunks.F32(data, b + k * 4); bone.T[k] = Chunks.F32(data, b + 32 + k * 4); }
                    for (int k = 0; k < 4; k++) bone.Q[k] = Chunks.F32(data, b + 16 + k * 4);
                    for (int k = 0; k < 16; k++) bone.InvBase[k] = Chunks.F32(data, b + 48 + k * 4);
                    sk.Bones[i] = bone;
                }
                string prefix = "__Bone:::" + sk.Name + ".";
                foreach (var (bn, bv, _) in elf.Symbols)
                    if (bn.StartsWith(prefix, StringComparison.Ordinal) && bv >= 0 && bv + 4 <= data.Length)
                    {
                        int idx = Chunks.I32(data, bv);
                        if (idx >= 0 && idx < n) sk.Bones[idx].Name = bn.Substring(prefix.Length);
                    }
                list.Add(sk);
            }
            return list;
        }

        /// <summary>
        /// The still pose (Skeleton::GetStillPose 0x49FF90, no bone mask) into an SQT buffer: 12 floats per bone, S at 0, Q at 4,
        /// T at 8. Slot 3 is the bone-length scale: SkeletonData +0xC[i] when that array exists, else 1.0; slot 11 is 1.0.
        /// (No retail skeleton has the array and no retail channel writes slot 3 / 11: `Harness anim9`.)
        /// </summary>
        public float[] StillPose()
        {
            var sqt = new float[Bones.Length * 12];
            for (int i = 0; i < Bones.Length; i++)
            {
                var b = Bones[i];
                Array.Copy(b.S, 0, sqt, i * 12, 3);
                sqt[i * 12 + 3] = BoneLengthScales != null && i < BoneLengthScales.Length ? BoneLengthScales[i] : 1f;
                Array.Copy(b.Q, 0, sqt, i * 12 + 4, 4);
                Array.Copy(b.T, 0, sqt, i * 12 + 8, 3);
                sqt[i * 12 + 11] = 1f;
            }
            return sqt;
        }

        /// <summary>
        /// Skeleton::PoseSQTToGlobal (0x49FCF0, no bone mask): per bone the SQT matrix of 0x493FF0 (row i = row i of R(Q)
        /// × S[i], T in the last row; the quaternion is used as evaluated, not renormalised: DecompressValuesIndexed's lerped
        /// keys go straight in), then column 0 (m[0], m[4], m[8]) × the bone-length scale of slot 3, then global = local *
        /// parent through the 0x8EE51C pointer (0x4A05A0 = D3DXMatrixMultiply(out, local, parent)).
        /// Matrices are 16 floats, row-major, row vectors (bMatrix4).
        /// </summary>
        public void PoseGlobal(float[] sqt, float[][] global)
        {
            for (int i = 0; i < Bones.Length; i++)
            {
                int o = i * 12;
                float qx = sqt[o + 4], qy = sqt[o + 5], qz = sqt[o + 6], qw = sqt[o + 7];
                var m = global[i] ?? (global[i] = new float[16]);
                float sx = sqt[o], sy = sqt[o + 1], sz = sqt[o + 2], len = sqt[o + 3];
                m[0] = (1 - 2 * (qy * qy + qz * qz)) * sx * len; m[1] = 2 * (qx * qy + qz * qw) * sx; m[2] = 2 * (qx * qz - qy * qw) * sx; m[3] = 0;
                m[4] = 2 * (qx * qy - qz * qw) * sy * len; m[5] = (1 - 2 * (qx * qx + qz * qz)) * sy; m[6] = 2 * (qy * qz + qx * qw) * sy; m[7] = 0;
                m[8] = 2 * (qx * qz + qy * qw) * sz * len; m[9] = 2 * (qy * qz - qx * qw) * sz; m[10] = (1 - 2 * (qx * qx + qy * qy)) * sz; m[11] = 0;
                m[12] = sqt[o + 8]; m[13] = sqt[o + 9]; m[14] = sqt[o + 10]; m[15] = 1;
                int p = Bones[i].Parent;
                if (p >= 0 && p < i) Mat.Mul(m, global[p], m);
            }
        }

        /// <summary>Skeleton::PoseGlobalToSkin: skin[i] = InvBase[i] * global[i] (what worldbone.efx gets as BlendMatrices).</summary>
        public void Skin(float[][] global, float[][] skin)
        {
            for (int i = 0; i < Bones.Length; i++)
                Mat.Mul(Bones[i].InvBase, global[i], skin[i] ?? (skin[i] = new float[16]));
        }
    }

    /// <summary>Row-major 4x4 helpers with the bMatrix4 convention (row vectors, translation in the last row).</summary>
    public static class Mat
    {
        public static float[] Identity() => new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1 };

        /// <summary>r = a * b (bMulMatrix order: apply a, then b). r may alias a or b.</summary>
        public static void Mul(float[] a, float[] b, float[] r)
        {
            float a0 = a[0], a1 = a[1], a2 = a[2], a3 = a[3], a4 = a[4], a5 = a[5], a6 = a[6], a7 = a[7];
            float a8 = a[8], a9 = a[9], a10 = a[10], a11 = a[11], a12 = a[12], a13 = a[13], a14 = a[14], a15 = a[15];
            float b0 = b[0], b1 = b[1], b2 = b[2], b3 = b[3], b4 = b[4], b5 = b[5], b6 = b[6], b7 = b[7];
            float b8 = b[8], b9 = b[9], b10 = b[10], b11 = b[11], b12 = b[12], b13 = b[13], b14 = b[14], b15 = b[15];
            r[0] = a0 * b0 + a1 * b4 + a2 * b8 + a3 * b12; r[1] = a0 * b1 + a1 * b5 + a2 * b9 + a3 * b13;
            r[2] = a0 * b2 + a1 * b6 + a2 * b10 + a3 * b14; r[3] = a0 * b3 + a1 * b7 + a2 * b11 + a3 * b15;
            r[4] = a4 * b0 + a5 * b4 + a6 * b8 + a7 * b12; r[5] = a4 * b1 + a5 * b5 + a6 * b9 + a7 * b13;
            r[6] = a4 * b2 + a5 * b6 + a6 * b10 + a7 * b14; r[7] = a4 * b3 + a5 * b7 + a6 * b11 + a7 * b15;
            r[8] = a8 * b0 + a9 * b4 + a10 * b8 + a11 * b12; r[9] = a8 * b1 + a9 * b5 + a10 * b9 + a11 * b13;
            r[10] = a8 * b2 + a9 * b6 + a10 * b10 + a11 * b14; r[11] = a8 * b3 + a9 * b7 + a10 * b11 + a11 * b15;
            r[12] = a12 * b0 + a13 * b4 + a14 * b8 + a15 * b12; r[13] = a12 * b1 + a13 * b5 + a14 * b9 + a15 * b13;
            r[14] = a12 * b2 + a13 * b6 + a14 * b10 + a15 * b14; r[15] = a12 * b3 + a13 * b7 + a14 * b11 + a15 * b15;
        }

        public static Vec3 Point(float[] m, float x, float y, float z) =>
            new Vec3(x * m[0] + y * m[4] + z * m[8] + m[12], x * m[1] + y * m[5] + z * m[9] + m[13], x * m[2] + y * m[6] + z * m[10] + m[14]);

        public static float[] Read(byte[] d, int o)
        {
            var m = new float[16];
            for (int i = 0; i < 16; i++) m[i] = Chunks.F32(d, o + i * 4);
            return m;
        }
    }

    /// <summary>One animation channel; Eval writes its DOFs into an SQT pose buffer at a time in frames.</summary>
    public abstract class EaglChannel
    {
        public int Type;
        public int NumFrames;
        public abstract void Eval(float frame, float[] sqt);

        protected static void Split(float frame, int keys, out int k0, out int k1, out float f)
        {
            if (frame <= 0 || keys < 2) { k0 = k1 = 0; f = 0; return; }
            k0 = (int)frame;
            if (k0 >= keys - 1) { k0 = k1 = keys - 1; f = 0; return; }
            k1 = k0 + 1;
            f = frame - k0;
        }
    }

    /// <summary>
    /// DeltaChan types 10 (DeltaLerp, one DOF index per value) and 11 (DeltaQuat, one index per quaternion):
    /// {u16 type, u16 checksum, DeltaCompressedData*, u16 frames, u16 dof indices...}. DeltaCompressedData
    /// (DecompressValues, decoder 0x48FF00) is {u16 dofs, u8 bits 4/8/16, u8, DofInfo {min, range, start}[dofs]} then one
    /// row of quantised deltas per frame step: v[f] = v[f-1] + q * range + min (4-bit: low nibble first).
    /// FnDeltaLerpChan/FnDeltaQuatChan::Eval lerp between the floor and ceil frames.
    /// </summary>
    public sealed class EaglDeltaChannel : EaglChannel
    {
        public ushort[] DofIndices = Array.Empty<ushort>();
        public float[][] Keys = Array.Empty<float[]>();   // [frame][dof]
        public int ValuesPerIndex => Type == 11 ? 4 : 1;

        internal static EaglDeltaChannel Read(byte[] d, int o)
        {
            int block = Chunks.I32(d, o + 4), frames = Chunks.U16(d, o + 8);
            if (block <= 0 || block + 4 > d.Length || frames <= 0) return null;
            int n = Chunks.U16(d, block), bits = d[block + 2];
            if (n <= 0 || (bits != 4 && bits != 8 && bits != 16) || block + 4 + 12 * n > d.Length) return null;
            int type = Chunks.U16(d, o);
            var ch = new EaglDeltaChannel { Type = type, NumFrames = frames };
            int nIdx = type == 11 ? n / 4 : n;
            if (o + 10 + nIdx * 2 > d.Length) return null;
            ch.DofIndices = new ushort[nIdx];
            for (int i = 0; i < nIdx; i++) ch.DofIndices[i] = Chunks.U16(d, o + 10 + i * 2);
            int rec = block + 4 + 12 * n;
            int size = bits == 4 ? (n + 1) / 2 : bits == 8 ? n : 2 * n;
            var cur = new float[n];
            for (int i = 0; i < n; i++) cur[i] = Chunks.F32(d, block + 4 + 12 * i + 8);
            ch.Keys = new float[frames][];
            ch.Keys[0] = (float[])cur.Clone();
            for (int k = 1; k < frames; k++)
            {
                int r = rec + (k - 1) * size;
                if (r + size <= d.Length)
                    for (int i = 0; i < n; i++)
                    {
                        int q = bits == 4 ? (d[r + i / 2] >> (4 * (i & 1))) & 15 : bits == 8 ? d[r + i] : Chunks.U16(d, r + 2 * i);
                        cur[i] += Chunks.F32(d, block + 4 + 12 * i) + q * Chunks.F32(d, block + 4 + 12 * i + 4);
                    }
                ch.Keys[k] = (float[])cur.Clone();
            }
            return ch;
        }

        public override void Eval(float frame, float[] sqt)
        {
            Split(frame, Keys.Length, out int k0, out int k1, out float f);
            var a = Keys[k0];
            var b = Keys[k1];
            int vpi = ValuesPerIndex;
            for (int s = 0; s < DofIndices.Length; s++)
                for (int j = 0; j < vpi; j++)
                {
                    int v = s * vpi + j, dst = DofIndices[s] + j;
                    if (v < a.Length && dst < sqt.Length) sqt[dst] = a[v] + (b[v] - a[v]) * f;
                }
        }
    }

    /// <summary>
    /// DeltaF3 (type 20, FnDeltaF3::EvalSQT 0x495DB0..) and DeltaF1 (type 21, FnDeltaF1 0x493F50..):
    /// {u16 type, u16 checksum, u16* dof indices, u16* key times (null = one key per frame), u16 keys, u16 animated
    /// count, u8 bin length power, u8 constant count, u16 pad} then DofInfo {f32 phys min[c], f32 phys range[c],
    /// u16 quant min[c], u16 quant range[c]} per animated DOF group (c = 3 or 1), then bins of 2^power keys: the first
    /// key of a bin as u16 physical values, the others as u8 deltas. phys = q * range / 65535 + min;
    /// delta = q * (2 * qrange / 65535 * range / 255) + (2 * qmin / 65535 * range - range) (InitBuffersAsRequired).
    /// After the bins (2-aligned): u16 constant DOF indices, then (4-aligned) their floats.
    /// </summary>
    public sealed class EaglDeltaFChannel : EaglChannel
    {
        public int Components;                      // 3 (F3) or 1 (F1)
        public ushort[] DofIndices = Array.Empty<ushort>();
        public ushort[] KeyTimes;                   // null: key k at frame k
        public float[][] Keys = Array.Empty<float[]>();   // [key][group * c + i]
        public ushort[] ConstIndices = Array.Empty<ushort>();
        public float[] ConstValues = Array.Empty<float>();

        internal static EaglDeltaFChannel Read(byte[] d, int o)
        {
            if (o + 0x14 > d.Length) return null;
            int type = Chunks.U16(d, o);
            int c = type == 20 ? 3 : 1;
            int dofPtr = Chunks.I32(d, o + 4), timesPtr = Chunks.I32(d, o + 8);
            int keys = Chunks.U16(d, o + 12), nb = Chunks.U16(d, o + 14), pow = d[o + 16], nc = d[o + 17];
            if (keys <= 0 || pow > 15) return null;
            var ch = new EaglDeltaFChannel { Type = type, Components = c };
            ch.DofIndices = new ushort[nb];
            for (int i = 0; i < nb; i++) ch.DofIndices[i] = dofPtr + i * 2 + 2 <= d.Length ? Chunks.U16(d, dofPtr + i * 2) : (ushort)0;
            if (timesPtr > 0 && keys >= 2)
            {
                ch.KeyTimes = new ushort[keys - 1];
                for (int i = 0; i < keys - 1; i++) ch.KeyTimes[i] = Chunks.U16(d, timesPtr + i * 2);
                ch.NumFrames = ch.KeyTimes[keys - 2] + 1;
            }
            else ch.NumFrames = keys;

            int infoSize = c == 3 ? 36 : 12;
            int info = o + 0x14;
            var physMin = new float[nb * c];
            var physRange = new float[nb * c];
            var deltaMin = new float[nb * c];
            var deltaRange = new float[nb * c];
            for (int b = 0; b < nb; b++)
                for (int k = 0; k < c; k++)
                {
                    int i = info + b * infoSize;
                    float pmin = Chunks.F32(d, i + k * 4), prange = Chunks.F32(d, i + c * 4 + k * 4);
                    int qmin = Chunks.U16(d, i + c * 8 + k * 2), qrange = Chunks.U16(d, i + c * 8 + c * 2 + k * 2);
                    physMin[b * c + k] = pmin;
                    physRange[b * c + k] = prange * 1.5259022e-5f;
                    deltaMin[b * c + k] = 2 * (qmin * 1.5259022e-5f) * prange - prange;
                    deltaRange[b * c + k] = 2 * (qrange * 1.5259022e-5f) * prange * 0.003921569f;
                }
            int binLen = 1 << pow, frameDelta = nb * c, physSize = nb * c * 2;
            int binSize = (physSize + (binLen - 1) * frameDelta + 1) & ~1;
            int bins = info + nb * infoSize;
            ch.Keys = new float[keys][];
            for (int k = 0; k < keys; k++)
            {
                int bin = bins + (k >> pow) * binSize;
                int j = k & (binLen - 1);
                var v = new float[nb * c];
                if (bin + physSize <= d.Length)
                    for (int i = 0; i < nb * c; i++) v[i] = Chunks.U16(d, bin + i * 2) * physRange[i] + physMin[i];
                for (int f = 0; f < j; f++)
                {
                    int dr = bin + physSize + f * frameDelta;
                    if (dr + frameDelta > d.Length) break;
                    for (int i = 0; i < nb * c; i++) v[i] += d[dr + i] * deltaRange[i] + deltaMin[i];
                }
                ch.Keys[k] = v;
            }
            // GetConstBoneIdx / GetConstPhysical
            int s = bins + binSize * (keys >> pow);
            int r = keys & (binLen - 1);
            if (r > 0) s = (s + physSize + (r - 1) * frameDelta + 1) & ~1;
            if (nb == 0) s = (s + 1) & ~1;
            if (nc > 0 && s + nc * 2 <= d.Length)
            {
                ch.ConstIndices = new ushort[nc];
                for (int i = 0; i < nc; i++) ch.ConstIndices[i] = Chunks.U16(d, s + i * 2);
                int p = (s + nc * 2 + 3) & ~3;
                ch.ConstValues = new float[nc * c];
                for (int i = 0; i < nc * c && p + i * 4 + 4 <= d.Length; i++) ch.ConstValues[i] = Chunks.F32(d, p + i * 4);
            }
            return ch;
        }

        public override void Eval(float frame, float[] sqt)
        {
            int keys = Keys.Length;
            int k0, k1;
            float f;
            if (KeyTimes == null) Split(frame, keys, out k0, out k1, out f);
            else
            {
                // key 0 at frame 0, key k (k >= 1) at KeyTimes[k - 1]: the last key at or before the frame, lerp to the next
                k0 = 0;
                while (k0 + 1 < keys && KeyTimes[k0] <= frame) k0++;
                k1 = Math.Min(k0 + 1, keys - 1);
                float t0 = k0 == 0 ? 0 : KeyTimes[k0 - 1];
                float t1 = k1 == 0 ? 0 : KeyTimes[k1 - 1];
                f = k1 != k0 && t1 > t0 ? Math.Max(0, Math.Min(1, (frame - t0) / (t1 - t0))) : 0;
            }
            if (keys > 0)
            {
                var a = Keys[k0];
                var b = Keys[k1];
                for (int g = 0; g < DofIndices.Length; g++)
                    for (int i = 0; i < Components; i++)
                    {
                        int v = g * Components + i, dst = DofIndices[g] + i;
                        if (dst < sqt.Length) sqt[dst] = a[v] + (b[v] - a[v]) * f;
                    }
            }
            for (int g = 0; g < ConstIndices.Length; g++)
                for (int i = 0; i < Components; i++)
                {
                    int dst = ConstIndices[g] + i;
                    if (dst < sqt.Length) sqt[dst] = ConstValues[g * Components + i];
                }
        }
    }

    /// <summary>
    /// A bank animation. Type 15 (CompoundChannel, FnCompoundChannel 0x895DF0 vtable) is {u16 type, u16 target
    /// checksum, AttributeBlock*, u16 channel count, u16 frames, AnimMemoryMap* channels[]}; Eval runs the channels
    /// last to first (FnCompoundChannel::Eval) with the time in frames.
    /// </summary>
    public sealed class EaglAnim
    {
        public string Name = "";
        public uint NameHash;       // bStringHash(name): CAnimCtrl::CreateFnAnimFromNamehash / GetAnimFromBankByNamehash
        public int Type;
        public int NumFrames;
        public ushort CheckSum;
        public readonly List<EaglChannel> Channels = new List<EaglChannel>();
        public readonly List<int> UnknownChannelTypes = new List<int>();

        /// <summary>
        /// FnAnim::GetLength (EAGL4Anim/CompoundChannel.h:141, DeltaChan.h:90): the frame count (a key-timed channel
        /// returns KeyTimes[n − 2] + 1). CAnimCtrl / CWorldAnimCtrl take it as m_animLength; the clip wraps or holds at it
        /// (the decoders clamp past the last key).
        /// </summary>
        public float Length => Math.Max(0, NumFrames);

        public void Eval(float frame, float[] sqt)
        {
            for (int i = Channels.Count - 1; i >= 0; i--) Channels[i].Eval(frame, sqt);
        }

        internal static EaglChannel ReadChannel(byte[] d, int o, List<int> unknown)
        {
            if (o <= 0 || o + 8 > d.Length) return null;
            int type = Chunks.U16(d, o);
            switch (type)
            {
                case 10:
                case 11: return EaglDeltaChannel.Read(d, o);
                case 20:
                case 21: return EaglDeltaFChannel.Read(d, o);
                default: unknown?.Add(type); return null;
            }
        }
    }

    /// <summary>
    /// CAnimCtrl::AdvanceAnimTime (0x4494D0; decomp Animation/AnimCtrl.cpp:140-220) and CWorldAnimCtrl::AdvanceAnimTime
    /// (decomp WorldAnimCtrl.cpp:183-296), the animation clock in frames (30 per scene second × time scale). Flags: 1
    /// skinned, 8 linear, 0x10 ping-pong, 0x20 loop, 0x40 loop range, 0x80 master delay, 0x100 local (per loop) delay,
    /// 0x800 triggered (world: pauses at each end), 0x1000 reverse (world). The master and local delays are given in
    /// seconds and compared in frames: elapsed·30·scale against delay·30, so a delay lasts delay/scale seconds; the overshoot
    /// of the step that ends a delay carries into the clip (fmod). The PC exe divides the elapsed time by 30 (0x8934E0 =
    /// 1/30 at 0x449648; the GameCube decomp's "/ 3" is not the PC's). A world controller only advances while playing,
    /// and its loop range is scaled by the effective time scale at use; a character controller's range was scaled when set.
    /// </summary>
    public sealed class AnimCtrl
    {
        public const int Stopped = 0, Playing = 1, PausedState = 2;

        public uint Flags;
        public bool World;
        public float TimeScale = 1f, SpeedModifier = 1f;
        /// <summary>m_animLength in frames (the last-created dof's FnAnim::GetLength).</summary>
        public float AnimLength;
        public float MasterDelay, LocalDelay, MasterElapsed, LocalElapsed;
        /// <summary>Loop range in frames: a character's is pre-scaled by the time scale (SetLoopRange), a world entity's is raw.</summary>
        public float LoopStart, LoopEnd;
        public float Eval;
        public bool Reverse;
        public int PlayState = Playing;

        public float EffectiveTimeScale => World ? TimeScale * SpeedModifier : TimeScale;
        public bool IsStopped => PlayState == Stopped;
        public bool IsPlaying => PlayState == Playing;

        public void SetMasterDelayTime(float s) { MasterDelay = s; Flags |= 0x80; }
        public void SetLocalDelayTime(float s) { LocalDelay = s; if (World) Flags |= 0x100; }

        /// <summary>CAnimCtrl::SetLoopRange (frames × time scale at the call) / CWorldAnimCtrl::SetLoopRange (raw).</summary>
        public void SetLoopRange(float start, float end)
        {
            Flags |= 0x40;
            LoopStart = World ? start : start * TimeScale;
            LoopEnd = World ? end : end * TimeScale;
        }

        void Range(float eff, out float begin, out float end)
        {
            if ((Flags & 0x40) != 0)
            {
                begin = World ? LoopStart * eff : LoopStart;
                end = World ? LoopEnd * eff : LoopEnd;
            }
            else { begin = 0f; end = AnimLength; }
        }

        /// <summary>CWorldAnimCtrl::SetEvalTime (:74-90): forward, local delay cleared; a ping-pong clip past its end reflects.</summary>
        public void SetEvalTime(float t)
        {
            Eval = t;
            Reverse = false;
            LocalElapsed = 0f;
            if (World && (Flags & 0x10) != 0)
            {
                Range(EffectiveTimeScale, out _, out float end);
                if (t > end) { Reverse = true; Eval = end - (t - end); }
            }
        }

        public void Play() => PlayState = Playing;
        public void Pause() => PlayState = PausedState;
        public void Stop() { PlayState = Stopped; Eval = 0f; }
        public void JumpToEndForTrigger() { Eval = AnimLength; PlayState = PausedState; Reverse = true; }
        public void JumpToBeginForTrigger() { Eval = 0f; PlayState = PausedState; Reverse = false; }

        /// <summary>Advances the clock by dt seconds; true when a non-looping linear clip reached its end this step.</summary>
        public bool Advance(float dt)
        {
            float eff = EffectiveTimeScale;
            if (eff <= 0f) return false;
            float step = dt * 30f * eff;
            float mde = MasterElapsed * 30f * eff, mdl = MasterDelay * 30f;
            float lde = LocalElapsed * 30f * eff, ldl = LocalDelay * 30f;
            Range(eff, out float begin, out float end);
            float range = end - begin;
            bool triggered = World && (Flags & 0x800) != 0;
            bool linear = (Flags & 8) != 0, loop = (Flags & 0x20) != 0 && !triggered, pingpong = (Flags & 0x10) != 0;
            if (linear) { pingpong = false; Flags &= ~0x10u; }
            else if (!pingpong) { linear = true; Flags |= 8; }
            if (World && PlayState != Playing) return false;
            float ev = Eval;
            bool done = false;
            if ((Flags & 0x80) != 0 && mde < mdl && Eval < end)
            {
                mde += step;
                if (mde > mdl) ev += mde % mdl;   // bFMod
                MasterElapsed = mde / eff / 30f;
            }
            else if ((Flags & 0x100) != 0 && lde < ldl && Eval < end)
            {
                lde += step;
                if (lde > ldl) ev += lde % ldl;
                LocalElapsed = lde / eff / 30f;
            }
            else if (linear)
            {
                if (World && (Flags & 0x1000) != 0)
                {
                    ev -= step;
                    if (ev < begin) { if (loop) { ev += range; LocalElapsed = 0f; } else { ev = begin; done = true; } }
                }
                else
                {
                    ev += step;
                    if (ev > end) { if (loop) { ev -= range; LocalElapsed = 0f; } else { ev = end; done = true; } }
                }
            }
            else if (pingpong)
            {
                if (!Reverse)
                {
                    ev += step;
                    if (ev > end) { if (triggered) PlayState = PausedState; Reverse = true; ev = end - (ev - end); }
                }
                else
                {
                    ev -= step;
                    if (ev < begin) { if (triggered) PlayState = PausedState; Reverse = false; ev = begin; LocalElapsed = 0f; }
                }
            }
            Eval = ev;
            return done;
        }
    }

    /// <summary>
    /// EAGL4 animation bank (chunk 0xE34010; CAnimBank::Initialize 0x448DB0, GetAnimFromBankByNamehash 0x448F20):
    /// symbol "__AnimationBank" marks {?, i32 count, ?, ?, anims*, names*}. All of NIS (cars, cops, characters) and the
    /// world animations (TRACKS\L2RA.BUN) use this one container.
    /// </summary>
    public static class EaglAnimBankReader
    {
        public static List<EaglAnim> Read(byte[] d, int start, int end)
        {
            var list = new List<EaglAnim>();
            var elf = EaglElf.Parse(d, start, end);
            if (elf == null) return list;
            var data = elf.Data;
            int hdr = -1;
            foreach (var (name, value, _) in elf.Symbols)
                if (name.StartsWith("__AnimationBank", StringComparison.Ordinal)) hdr = value;
            if (hdr < 0 || hdr + 0x18 > data.Length) return list;
            int count = Chunks.I32(data, hdr + 4), anims = Chunks.I32(data, hdr + 0x10), names = Chunks.I32(data, hdr + 0x14);
            for (int i = 0; i < count && anims + i * 4 + 4 <= data.Length && names + i * 4 + 4 <= data.Length; i++)
            {
                int a = Chunks.I32(data, anims + i * 4);
                if (a <= 0 || a + 0xC > data.Length) continue;
                var anim = new EaglAnim
                {
                    Name = Chunks.CString(data, Chunks.I32(data, names + i * 4), 256),
                    Type = Chunks.U16(data, a),
                    CheckSum = Chunks.U16(data, a + 2),
                };
                anim.NameHash = EaHash.Hash(anim.Name);
                if (anim.Type == 15)
                {
                    int n = Chunks.U16(data, a + 8);
                    anim.NumFrames = Chunks.U16(data, a + 10);
                    for (int c = 0; c < n && a + 12 + c * 4 + 4 <= data.Length; c++)
                    {
                        var ch = EaglAnim.ReadChannel(data, Chunks.I32(data, a + 12 + c * 4), anim.UnknownChannelTypes);
                        if (ch != null) anim.Channels.Add(ch);
                    }
                }
                else
                {
                    var ch = EaglAnim.ReadChannel(data, a, anim.UnknownChannelTypes);
                    if (ch != null) { anim.Channels.Add(ch); anim.NumFrames = ch.NumFrames; }
                }
                list.Add(anim);
            }
            return list;
        }
    }
}
