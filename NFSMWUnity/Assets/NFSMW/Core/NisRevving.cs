using System;
using System.IO;

namespace NfsMw.Core
{
    /// <summary>
    /// SFXCTL_NISReving (EAXSound/sfxctl/SFXCTL_NISReving.cpp; coverage range 0x4B4390-0x4C8733): engine revving for the
    /// cars of a NIS and for the AI racers during the 3-2-1.
    /// <list type="bullet">
    /// <item>NIS_RevManager::OpenNISRevData: SOUND\NISREVDATA\&lt;anim&gt;.bin (anim = GetCsisEventIndex's scene name) holds
    /// 16 data sets, one per NIS car channel (car1..car8 = 0..7, cop1..cop8 = 8..15): {s32 NumPoints, NumPoints x {f32 time
    /// s, s32 rpm, s32 torque}}, little-endian on PC (bPlatEndianSwap is a no-op; GenericStart.bin is exactly 16 sets).</item>
    /// <item>SFXCTL_Physics::UpdateNIS(TotalTime, dt): a car with a data set follows it (TimeIntoRev = scene time, linear
    /// interpolation between points); AI racers told to rev ("QRev", MsgRevEngine: pattern 5 + bRandom(7)) play one of the
    /// built-in RevPat5..12 tables (dt-driven); with under 1 s left of the countdown it merges into the physics (torque 100
    /// while below it, rpm smoothed toward it by 500); after a pattern rpm falls 500 and torque 15 per update. Results are
    /// clamped to 1000..10000 rpm, 0..100 torque, accelerating = torque &gt; 30, throttle = torque.</item>
    /// </list>
    /// </summary>
    public sealed class NisRevData
    {
        public readonly (float time, int rpm, int trq)[][] Sets = new (float, int, int)[16][];

        public static NisRevData Read(byte[] d)
        {
            var r = new NisRevData();
            int p = 0;
            for (int n = 0; n < 16; n++)
            {
                if (p + 4 > d.Length) { r.Sets[n] = Array.Empty<(float, int, int)>(); continue; }
                int c = BitConverter.ToInt32(d, p);
                p += 4;
                if (c < 0 || p + c * 12 > d.Length) c = 0;
                var s = new (float, int, int)[c];
                for (int k = 0; k < c; k++) s[k] = (BitConverter.ToSingle(d, p + k * 12), BitConverter.ToInt32(d, p + k * 12 + 4), BitConverter.ToInt32(d, p + k * 12 + 8));
                r.Sets[n] = s;
                p += c * 12;
            }
            return r;
        }

        public static NisRevData Load(string path)
        {
            try { return File.Exists(path) ? Read(File.ReadAllBytes(path)) : null; }
            catch (Exception) { return null; }
        }

        static (float, int, int) P(float t, int r, int q) => (t, r, q);

        /// <summary>RevPat5..RevPat12 (SFXCTL_NISReving.cpp:14-136).</summary>
        public static readonly (float time, int rpm, int trq)[][] Patterns =
        {
            new[] { P(0.0f, 1000, 0), P(0.3f, 1631, 83), P(0.6f, 2833, 100), P(0.90000004f, 4250, 100), P(1.2f, 5856, 100), P(1.5f, 6746, 70),
                P(1.8000001f, 6098, 0), P(2.1f, 5970, 30), P(2.4f, 7556, 100), P(2.6999998f, 9337, 100), P(3.0f, 9939, 100), P(3.3000002f, 9946, 100),
                P(3.6000001f, 9964, 100), P(3.8999999f, 9906, 100), P(4.2f, 9348, 16), P(4.5f, 8678, 0), P(4.8f, 7979, 0), P(5.1000004f, 7267, 0),
                P(5.3999996f, 6551, 0), P(5.7f, 5830, 0), P(6.0f, 5111, 0), P(6.2999997f, 4395, 0), P(6.6000004f, 3676, 0), P(6.8999996f, 2959, 0),
                P(7.2000003f, 2377, 0), P(7.5f, 2137, 0), P(7.7999997f, 1897, 0), P(8.1f, 1657, 0) },
            new[] { P(0.0f, 1000, 0), P(0.3f, 1052, 10), P(0.6f, 2010, 96), P(0.90000004f, 3285, 100), P(1.2f, 4770, 100), P(1.5f, 6427, 100),
                P(1.8000001f, 7965, 90), P(2.1f, 7571, 3), P(2.4f, 6864, 0), P(2.6999998f, 6219, 10), P(3.0f, 7544, 96), P(3.3000002f, 9154, 90),
                P(3.6000001f, 8831, 3), P(3.8999999f, 8154, 0), P(4.2f, 7443, 0), P(4.5f, 6727, 0), P(4.8f, 6008, 0), P(5.1000004f, 5291, 0),
                P(5.3999996f, 4572, 0), P(5.7f, 3853, 0), P(6.0f, 3137, 0), P(6.2999997f, 2433, 0), P(6.6000004f, 2193, 0) },
            new[] { P(0.0f, 1037, 47), P(0.3f, 1192, 54), P(0.6f, 1674, 72), P(0.90000004f, 2496, 85), P(1.2f, 3506, 87), P(1.5f, 4960, 100),
                P(1.8000001f, 6634, 100), P(2.1f, 8375, 97), P(2.4f, 8146, 12), P(2.6999998f, 7455, 0), P(3.0f, 6741, 0), P(3.3000002f, 6022, 0),
                P(3.6000001f, 5304, 0), P(3.8999999f, 4591, 0), P(4.2f, 3872, 0), P(4.5f, 3272, 20), P(4.8f, 2823, 6), P(5.1000004f, 2302, 0),
                P(5.3999996f, 2062, 0), P(5.7f, 1822, 0) },
            new[] { P(0.0f, 2476, 100), P(0.3f, 2714, 100), P(0.6f, 2763, 100), P(0.90000004f, 2747, 100), P(1.2f, 2944, 100), P(1.5f, 3093, 100),
                P(1.8000001f, 3240, 100), P(2.1f, 3393, 100), P(2.4f, 3550, 100), P(2.6999998f, 3707, 100), P(3.0f, 3868, 100), P(3.3000002f, 4031, 100),
                P(3.6000001f, 4197, 100), P(3.8999999f, 4365, 100), P(4.2f, 4535, 100), P(4.5f, 4707, 100), P(4.8f, 4882, 100), P(5.1000004f, 5058, 100),
                P(5.3999996f, 5230, 100), P(5.7f, 5409, 100), P(6.0f, 5591, 100), P(6.2999997f, 5773, 100), P(6.6000004f, 5958, 100), P(6.8999996f, 6144, 100),
                P(7.2000003f, 6330, 100), P(7.5f, 6518, 100), P(7.7999997f, 6542, 16), P(8.1f, 6430, 0), P(8.4f, 6328, 14), P(8.700001f, 6359, 76),
                P(9.0f, 6538, 100), P(9.299999f, 6727, 100), P(9.6f, 6916, 100), P(9.9f, 7105, 100), P(10.200001f, 7295, 100), P(10.5f, 7485, 100),
                P(10.799999f, 7675, 100), P(11.1f, 7866, 100), P(11.4f, 8058, 100), P(11.700001f, 8234, 90), P(12.0f, 8048, 3), P(12.299999f, 7614, 0),
                P(12.599999f, 7136, 0), P(12.900001f, 6689, 0), P(13.200001f, 6246, 0), P(13.5f, 5909, 0), P(13.799999f, 5722, 0), P(14.099999f, 5582, 0),
                P(14.400001f, 8915, 62), P(14.700001f, 9083, 68), P(15.0f, 8884, 6), P(15.299999f, 8618, 0), P(15.599999f, 8182, 0), P(15.900001f, 7646, 0),
                P(16.2f, 7014, 0), P(16.5f, 6275, 0), P(16.8f, 5443, 0), P(17.099998f, 4696, 0), P(17.400002f, 4412, 0), P(17.7f, 4134, 0),
                P(18.0f, 3862, 0), P(18.3f, 3595, 0), P(18.599998f, 3335, 0), P(18.900002f, 3079, 0), P(19.2f, 2829, 0), P(19.5f, 2582, 0),
                P(19.8f, 2342, 0), P(20.099998f, 2106, 0) },
            new[] { P(0.0f, 1588, 0), P(0.3f, 1546, 49), P(0.6f, 1580, 50), P(0.90000004f, 1701, 76), P(1.2f, 1983, 94), P(1.5f, 2410, 100),
                P(1.8000001f, 2856, 100), P(2.1f, 3318, 100), P(2.4f, 3785, 100), P(2.6999998f, 4266, 100), P(3.0f, 4759, 100), P(3.3000002f, 5266, 100),
                P(3.6000001f, 5597, 70), P(3.8999999f, 5379, 0), P(4.2f, 5193, 28), P(4.5f, 5612, 100), P(4.8f, 6160, 100), P(5.1000004f, 6729, 100),
                P(5.3999996f, 7335, 100), P(5.7f, 7977, 94), P(6.0f, 7922, 7), P(6.2999997f, 7648, 0), P(6.6000004f, 7363, 0), P(6.8999996f, 7294, 30),
                P(7.2000003f, 7890, 100), P(7.5f, 8600, 100), P(7.7999997f, 9347, 100), P(8.1f, 9884, 90), P(8.4f, 9648, 3), P(8.700001f, 9380, 0),
                P(9.0f, 8849, 0), P(9.299999f, 8384, 0), P(9.6f, 7958, 0), P(9.9f, 7550, 0), P(10.200001f, 7170, 0), P(10.5f, 6808, 0),
                P(10.799999f, 6460, 0), P(11.1f, 6132, 0), P(11.4f, 5827, 0), P(11.700001f, 5105, 0), P(12.0f, 4390, 0), P(12.299999f, 3671, 0),
                P(12.599999f, 3038, 0), P(12.900001f, 3339, 0), P(13.200001f, 3644, 0), P(13.5f, 3558, 0), P(13.799999f, 3305, 0), P(14.099999f, 3049, 0),
                P(14.400001f, 2799, 0), P(14.700001f, 2553, 0), P(15.0f, 2313, 0), P(15.299999f, 2077, 0), P(15.599999f, 1847, 0), P(15.900001f, 1612, 0),
                P(16.2f, 1372, 0), P(16.5f, 1132, 0), P(16.8f, 1004, 0) },
            new[] { P(0.0f, 1864, 96), P(0.3f, 3111, 100), P(0.6f, 4570, 100), P(0.90000004f, 6211, 100), P(1.2f, 7707, 81), P(1.5f, 8127, 47),
                P(1.8000001f, 8455, 49), P(2.1f, 8571, 38), P(2.4f, 8611, 38), P(2.6999998f, 8650, 38), P(3.0f, 8689, 38), P(3.3000002f, 8731, 38),
                P(3.6000001f, 8629, 24), P(3.8999999f, 8023, 0), P(4.2f, 7315, 0), P(4.5f, 6590, 0), P(4.8f, 5879, 0), P(5.1000004f, 5290, 21),
                P(5.3999996f, 6618, 100), P(5.7f, 6547, 16), P(6.0f, 5839, 0), P(6.2999997f, 7064, 83), P(6.6000004f, 7489, 37), P(6.8999996f, 6814, 0),
                P(7.2000003f, 6098, 0), P(7.5f, 5376, 0), P(7.7999997f, 4663, 0), P(8.1f, 3947, 0), P(8.4f, 3227, 0), P(8.700001f, 2525, 0),
                P(9.0f, 2187, 0), P(9.299999f, 1947, 0), P(9.6f, 1707, 0), P(9.9f, 1467, 0), P(10.200001f, 1227, 0) },
            new[] { P(0.0f, 1148, 30), P(0.3f, 2207, 100), P(0.6f, 3517, 100), P(0.90000004f, 5035, 100), P(1.2f, 6716, 100), P(1.5f, 8489, 100),
                P(1.8000001f, 9788, 100), P(2.1f, 9508, 16), P(2.4f, 8852, 0), P(2.6999998f, 8161, 0), P(3.0f, 7450, 0), P(3.3000002f, 6733, 0),
                P(3.6000001f, 6013, 0), P(3.8999999f, 6603, 60), P(4.2f, 8317, 100), P(4.5f, 8107, 16), P(4.8f, 7414, 0), P(5.1000004f, 7371, 30),
                P(5.3999996f, 9029, 100), P(5.7f, 9892, 100), P(6.0f, 9783, 70), P(6.2999997f, 9170, 0), P(6.6000004f, 8492, 0), P(6.8999996f, 7793, 0),
                P(7.2000003f, 7077, 0), P(7.5f, 6360, 0), P(7.7999997f, 5646, 0), P(8.1f, 4927, 0), P(8.4f, 4209, 0), P(8.700001f, 3496, 0),
                P(9.0f, 2776, 0) },
            new[] { P(0.0f, 1220, 30), P(0.3f, 2300, 100), P(0.6f, 3634, 100), P(0.90000004f, 5163, 100), P(1.2f, 6849, 100), P(1.5f, 7537, 42),
                P(1.8000001f, 7288, 21), P(2.1f, 6711, 0), P(2.4f, 5991, 0), P(2.6999998f, 6775, 83), P(3.0f, 8390, 90), P(3.3000002f, 8085, 3),
                P(3.6000001f, 8402, 60), P(3.8999999f, 9433, 70), P(4.2f, 9375, 30), P(4.5f, 9824, 70), P(4.8f, 9844, 30), P(5.1000004f, 9837, 70),
                P(5.3999996f, 9919, 70), P(5.7f, 9644, 40), P(6.0f, 9840, 60), P(6.2999997f, 9784, 70), P(6.6000004f, 9172, 0), P(6.8999996f, 8492, 0),
                P(7.2000003f, 7795, 0), P(7.5f, 7079, 0), P(7.7999997f, 6360, 0), P(8.1f, 5649, 0), P(8.4f, 4927, 0), P(8.700001f, 4210, 0) },
        };

        /// <summary>NumDataPoints the decomp sets per pattern: 0x1B, 0x16, 0x13, 0x43, 0x38, 0x22, 0x1E, 0x1D (each table's
        /// length - 1, the transitions walked).</summary>
        public static readonly int[] PatternCounts = { 0x1B, 0x16, 0x13, 0x43, 0x38, 0x22, 0x1E, 0x1D };
    }

    /// <summary>The per-car SFXCTL_Physics NIS state (eCurNisRevingState, pRevData, TimeIntoRev, NISRPM/NISTRQ).</summary>
    public sealed class NisRevState
    {
        public const int Off = 0, PatternOn = 1, MergeWithPhysics = 2;
        public int State { get; private set; }
        bool _patternPlay;
        int _pattern = 5, _num, _idx;
        (float time, int rpm, int trq)[] _data;
        float _timeInto;
        public float NisRpm, NisTrq;

        /// <summary>MsgRevEngine ("QRev"): AI racers pick pattern 5 + bRandom(7).</summary>
        public void RevEngine(bool aiRacer, Random rng)
        {
            State = Off;
            if (aiRacer) { _patternPlay = true; _pattern = rng.Next(7) + 5; }
        }

        /// <summary>MsgRevOff ("RevOFF").</summary>
        public void RevOff() { State = Off; _data = null; }

        static float Slope(float y0, float y1, float x0, float x1, float x)
        {
            // Slope(y0, y1, x0, x1).GetValue(x): clamped linear map (OldSoundTemplates.hpp)
            if (x1 == x0) return x <= x0 ? y0 : y1;
            float t = (x - x0) / (x1 - x0);
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            return y0 + (y1 - y0) * t;
        }

        /// <summary>
        /// SFXCTL_Physics::UpdateNIS. <paramref name="physicsRpm"/> in: this update's audio rpm from the physics
        /// (PhysicsRPM), out: the NIS rpm; <paramref name="prevSetRpm"/> = m_pEAXCar-&gt;GetPhysRPM() (the last value
        /// set). Returns false where the exe returns early (no data for the car): the car's audio values stay as they were.
        /// </summary>
        public bool Update(float totalTime, float dt, int nisCarId, NisRevData data, float countdownLeft, float prevSetRpm, ref float physicsRpm, out float trq)
        {
            trq = NisTrq;
            if (State == Off)
            {
                if (_patternPlay)
                {
                    _patternPlay = false;
                    int p = _pattern < 5 || _pattern > 12 ? 0 : _pattern - 5;
                    _data = NisRevData.Patterns[p];
                    _num = NisRevData.PatternCounts[p];
                    _idx = 0;
                    State = PatternOn;
                    NisRpm = Slope(_data[0].rpm, _data[1].rpm, _data[0].time, _data[1].time, _timeInto);
                    NisTrq = Slope(_data[0].trq, _data[1].trq, _data[0].time, _data[1].time, _timeInto);
                }
                else
                {
                    State = Off;
                    _timeInto = totalTime;
                    if (nisCarId < 0 || data == null || nisCarId >= 16) return false;
                    var set = data.Sets[nisCarId];
                    if (set == null || set.Length < 2) return false;
                    State = PatternOn;
                    _num = set.Length - 1;
                    _data = set;
                    _idx = 0;
                }
            }
            switch (State)
            {
                case PatternOn:
                    if (totalTime < 0.001f) _timeInto += dt; else _timeInto = totalTime;
                    while (State != Off && _idx + 1 < _data.Length && _timeInto > _data[_idx + 1].time)
                    {
                        _num--;
                        if (_num == 0) State = Off;
                        else _idx++;
                    }
                    if (_idx + 1 >= _data.Length) State = Off;   // [ours] the walk past the table's end the exe allows
                    if (State != Off)
                    {
                        var a = _data[_idx]; var b = _data[_idx + 1];
                        NisRpm = Slope(a.rpm, b.rpm, a.time, b.time, _timeInto);
                        NisTrq = Slope(a.trq, b.trq, a.time, b.time, _timeInto);
                    }
                    if (countdownLeft < 1f && countdownLeft > 0f) State = MergeWithPhysics;
                    break;
                case MergeWithPhysics:
                    NisTrq = prevSetRpm < physicsRpm ? 100f : 0f;
                    NisRpm = SndCurves.Smooth(prevSetRpm, physicsRpm, 500f);
                    break;
                default:
                    NisRpm -= 500f;
                    NisTrq -= 15f;
                    break;
            }
            physicsRpm = Math.Max(1000f, Math.Min(10000f, NisRpm));
            NisTrq = Math.Max(0f, Math.Min(100f, NisTrq));
            NisRpm = physicsRpm;
            trq = NisTrq;
            return true;
        }
    }
}
