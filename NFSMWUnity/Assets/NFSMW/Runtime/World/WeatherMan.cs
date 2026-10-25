using System.Collections.Generic;
using System.IO;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.World
{
    /// <summary>
    /// World/WeatherMan.cpp on PC: LoaderWeatherMan 0x72CAB0 keeps the GenericRegions of TRACKS\L2RA.BUN (chunk 0x34250;
    /// 61 regions, all REGION_BLOOM "BLOOM_CP/CT/TC_nn" points at tunnel exits). Their only live reader is
    /// GetClosestRegionInView 0x72A760 for the tunnel bloom (<see cref="ScreenEffects"/>). The fog-region query
    /// (RegionQuery::CalculateRegionInfo 0x72A170) is reached only from 0x6BFFD0, which nothing calls, and
    /// getHorizonColour 0x72A500 has no caller, so neither is ported. Reader: Core/Weather.cs (WeatherRegions).
    /// </summary>
    public static class WeatherMan
    {
        static List<GenericRegion> _regions;
        static byte[] _from;

        /// <summary>The region lists, loaded from the streamer's L2RA bundle (or the file when no world is up).</summary>
        public static List<GenericRegion> Regions
        {
            get
            {
                var w = GameBootstrap.Instance ? GameBootstrap.Instance.World : null;
                var bundle = w != null && w.Track != null ? w.Track.Bundle : null;
                if (_regions != null && (bundle == null || bundle == _from)) return _regions;
                try
                {
                    if (bundle == null)
                    {
                        GameData.Mount();
                        bundle = GameFiles.ReadBundle(Path.Combine(GameData.Root, "TRACKS", "L2RA.BUN"));
                    }
                    _regions = WeatherRegions.Read(bundle);
                    _from = bundle;
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning("NFSMW weather: generic regions unavailable: " + e.Message);
                    _regions = new List<GenericRegion>();
                }
                return _regions;
            }
        }
    }
}
