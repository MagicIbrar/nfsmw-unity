using System.Collections.Generic;
using System.Linq;
using NfsMw.Core;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>Small shared helpers of the safehouse screens.</summary>
    public static class SafehouseUi
    {
        static readonly Dictionary<uint, string> _iconNames = new Dictionary<uint, string>();

        /// <summary>
        /// IconOption textures are given by hash (IconOption(tex_hash, name_hash, desc_hash)); CarouselScreen wants a name.
        /// The name as stored in the texture packs (24 characters at most), or the hash in hex when it is not loaded.
        /// </summary>
        public static string IconName(uint hash)
        {
            if (_iconNames.TryGetValue(hash, out var n)) return n;
            FrontEndData.Load();
            n = FrontEndData.Textures.All.FirstOrDefault(t => t.Hash == hash)?.Name ?? hash.ToString("X8");
            _iconNames[hash] = n;
            return n;
        }

        /// <summary>FEPrintf "%$d": a number with thousands separators.</summary>
        public static string Dollars(int v) => v.Grouped();

        /// <summary>Sets every string object of a hash (text + drop shadow copies share it).</summary>
        public static void SetAll(FEView view, uint hash, string text)
        {
            foreach (var n in view.Nodes) if (n.Obj.Hash == hash && n.Label != null) view.SetText(n, text);
        }

        /// <summary>FEngSetLanguageHash on an object or a group: the object's own string or every string under it.</summary>
        public static void SetLang(FEView view, uint obj, string text)
        {
            foreach (var n in view.Nodes)
            {
                if (n.Obj.Hash != obj) continue;
                if (n.Label != null) view.SetText(n, text);
                foreach (var d in view.Descendants(n)) if (d.Label != null) view.SetText(d, text);
            }
        }

        /// <summary>FEngSetVisible / FEngSetInvisible by hash (every object of that name).</summary>
        public static void Visible(FEView view, uint obj, bool on)
        {
            foreach (var n in view.Nodes) if (n.Obj.Hash == obj) view.SetVisible(n, on);
        }

        /// <summary>QueuePackageMessage(msg, pkg): the game runs the script of that name on every object that has one.</summary>
        public static void Message(FEView view, uint script)
        {
            foreach (var n in view.Nodes) view.Play(n, script);
        }

        public static void Message(FEView view, string script) => Message(view, FrontEndData.Hash(script));

        /// <summary>blacklist_rival_NN_aka: the rival's nickname.</summary>
        public static string RivalAka(int bin) => FrontEndData.Text(FrontEndData.Hash($"blacklist_rival_{bin:00}_aka"), $"Blacklist #{bin}");

        /// <summary>"Unlock this car by beating %s, Blacklist member %d." (0x4EF2A115).</summary>
        public static string UnlockByRival(int bin)
        {
            string fmt = FEDb.L(0x4EF2A115, "Unlock this car by beating %s, Blacklist member %d.");
            int s = fmt.IndexOf("%s");
            if (s >= 0) fmt = fmt.Substring(0, s) + RivalAka(bin) + fmt.Substring(s + 2);
            return fmt.Replace("%d", bin.ToString());
        }
    }
}
