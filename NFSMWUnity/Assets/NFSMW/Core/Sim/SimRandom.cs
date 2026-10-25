namespace NfsMw.Core
{
    /// <summary>
    /// SimRandom (Math/Common/SimRandom.cpp, decomp Math/SimRandom.h; PC 1.3): the simulation's deterministic random, a
    /// 16-bit multiplicative generator. ctor 0x6272F0 (randSeed 0x075BCD15 = 123456789), Reset 0x633E70 (fastRandom
    /// 0xF874AF01, randtemp / numRandCalls 0, then randSeed % 50 = 39 dry generations), SimRandom_Generate 0x627320
    /// (randtemp = fastRandom · randSeed; fastRandom = randtemp &amp; 0xFFFF; result (randtemp &gt;&gt; 8) &amp; 0xFFFF), the inlined
    /// helpers _SimRandom_Float (0x402850: r / 65536), _SimRandom_FloatRange (0x402870: r · range / 65536 on the x87) and
    /// _SimRandom_IntRange (0x402890: (range · r) &gt;&gt; 16, signed). One instance, Sim::GetRandom (0x6ED200, the static at
    /// 0x988F0C), Reset by Sim::Init (0x6FC417) and Sim::Shutdown (0x6FABD2) — GameFlow.BeginLoad / EndGameplay.
    /// Consumers in speed.exe (call xrefs of 0x627320 / 0x402870 / 0x6ED200): AICopManager rand_point_in_circle (0x406340),
    /// SpawnPursuitIVehicle (0x430E60: the ±60° side and the cooldown spawn), CreateRoadBlock (0x43DD30: the spike chance),
    /// GetHeavySupportVehicles (0x41F130: big SUV chance); AIPursuit RequestRoadBlock (0x4194B0: the 8-12 s roadblock timer
    /// and the request chance), RequestGroundSupport (0x419680), RemoveUnwantedVehicles (0x42B820: the type to flee),
    /// CopRequest (0x42BA50: the weighted cop type), OnTask (0x443BA0: the formation pick and its 0-3 s extra time);
    /// GetRandomValidCopCar (0x419AE0); AISpawnManager GetSpawnPointOnSegment (0x419F40: the point along the segment) and
    /// GetSpawnLocation (0x42FA40: the segment); AIActionHeliPursuit::SetNextPerpSearchDest (0x412F30: the search radius);
    /// Speech::Manager::CanPlayback (0x704490: the event's play probability). Nothing outside the sim (FE, audio
    /// effects, world) draws from it — those use bRandom (Core/Lua GameplayState Random) or their own generators.
    /// </summary>
    public sealed class SimRandom
    {
        uint _fastRandom, _randTemp, _randSeed = 0x075BCD15u, _numRandCalls;

        static SimRandom _instance;
        static readonly object Lock = new object();

        /// <summary>Sim::GetRandom (0x6ED200): the function-static instance (constructed on first use, not Reset).</summary>
        public static SimRandom Get() { lock (Lock) return _instance ??= new SimRandom(); }

        public uint Calls => _numRandCalls;

        /// <summary>Reset (0x633E70).</summary>
        public void Reset()
        {
            lock (Lock)
            {
                _randTemp = 0;
                _numRandCalls = 0;
                _fastRandom = 0xF874AF01u;
                int n = (int)(_randSeed % 50u);
                for (int i = 0; i < n; i++) GenerateUnlocked();
            }
        }

        int GenerateUnlocked()
        {
            unchecked
            {
                _numRandCalls++;
                _randTemp = _fastRandom * _randSeed;
                _fastRandom = _randTemp & 0xFFFFu;
                return (int)((_randTemp >> 8) & 0xFFFFu);
            }
        }

        /// <summary>SimRandom_Generate (0x627320): 0 .. 0xFFFF.</summary>
        public int Generate() { lock (Lock) return GenerateUnlocked(); }

        /// <summary>_SimRandom_Float (0x402850): [0, 1).</summary>
        public float Float() => Generate() * (1f / 65536f);

        /// <summary>_SimRandom_FloatRange (0x402870): [0, range).</summary>
        public float FloatRange(float range) => Generate() * range * (1f / 65536f);

        /// <summary>_SimRandom_IntRange (0x402890): 0 .. range − 1 (imul, sar 16).</summary>
        public int IntRange(int range) => unchecked(range * Generate()) >> 16;

        /// <summary>The helpers on the one instance (Sim::GetRandom().SimRandom_*).</summary>
        public static float RandFloat() => Get().Float();
        public static float RandFloatRange(float range) => Get().FloatRange(range);
        public static int RandIntRange(int range) => Get().IntRange(range);
    }
}
