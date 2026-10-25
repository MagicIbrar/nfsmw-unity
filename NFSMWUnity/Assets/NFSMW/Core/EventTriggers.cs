using System.Collections.Generic;

namespace NfsMw.Core
{
    /// <summary>
    /// World event triggers (World/EventManager.cpp LoaderEventManager): nested chunk 0x80036000 in each streamed section
    /// with 0x36001 the EventTriggerPack header {bTNode 8, i32 Version (2), i32 ScenerySectionNumber, i32 NumEventTriggers,
    /// i32 EndianSwapped, ptr tree, ptr array} (16-aligned data), 0x36002 its vAABBTree and 0x36003 the EventTrigger
    /// records (0x20 each: u32 NameHash, u32 EventID, u32 Parameter, i32 TrackDirectionMask, float x, y, z (game space),
    /// float Radius). emTriggerEventsInSection fires the triggers whose sphere holds the query point. EventID 0x10007 =
    /// TRIGGER_EVENT_SOUND: the drive-by wooshes (NameHash LAMPPOST/PILLAR/TRAFFIC_LIGHT/FREEWAY_SIGN → lamppost, TREE →
    /// tree; STATEMGR_DriveBy.cpp:172).
    /// </summary>
    public sealed class EventTrigger
    {
        public const uint SoundEvent = 0x10007;
        public const uint HashLamppost = 0xF40A48EF, HashTree = 0x001D5D4F;
        public uint NameHash, EventId, Parameter;
        public int TrackDirectionMask, Section;
        public float X, Y, Z, Radius;

        static int Align16(int o) => (o + 15) & ~15;

        public static List<EventTrigger> ReadAll(byte[] d, int start, int end, int depth = 0)
        {
            var list = new List<EventTrigger>();
            if (depth > 4) return list;
            foreach (var c in Chunks.Read(d, start, end))
            {
                if (c.Id == 0x80036000)
                {
                    int section = -1, count = 0, version = 0;
                    foreach (var s in Chunks.Read(d, c.DataStart, c.End))
                    {
                        if (s.Id == 0x36001)
                        {
                            int h = Align16(s.DataStart);
                            if (h + 0x20 > s.End) continue;
                            version = Chunks.I32(d, h + 8);
                            section = Chunks.I32(d, h + 12);
                            count = Chunks.I32(d, h + 16);
                        }
                        else if (s.Id == 0x36003 && version == 2)
                        {
                            int a = Align16(s.DataStart);
                            for (int o = a, n = 0; o + 0x20 <= s.End && n < count; o += 0x20, n++)
                                list.Add(new EventTrigger
                                {
                                    NameHash = Chunks.U32(d, o), EventId = Chunks.U32(d, o + 4), Parameter = Chunks.U32(d, o + 8),
                                    TrackDirectionMask = Chunks.I32(d, o + 12), X = Chunks.F32(d, o + 16), Y = Chunks.F32(d, o + 20),
                                    Z = Chunks.F32(d, o + 24), Radius = Chunks.F32(d, o + 28), Section = section,
                                });
                        }
                    }
                }
                else if ((c.Id & 0x80000000) != 0) list.AddRange(ReadAll(d, c.DataStart, c.End, depth + 1));
            }
            return list;
        }
    }
}
