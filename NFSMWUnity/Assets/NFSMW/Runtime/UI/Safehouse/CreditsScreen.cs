using System.Collections.Generic;
using System.IO;
using System.Text;
using NfsMw.Core;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>
    /// Credits.fng — uiCredits (Safehouse/options/uiCredits.cpp; PC range 0x532E90-0x533337). The credits are
    /// CREDITS\&lt;NA_|UK_&gt;&lt;LANGUAGE&gt;.TXT, a UTF-16 file read by UnicodeFile (Frontend/UnicodeFile.cpp): CR / LF end a line,
    /// runs of line ends collapse (Next skips them), LineWrap(45) breaks a line at its last space once it reaches 45
    /// characters. Flow: the package's INIT_COMPLETE loads the file; each CREDIT_NEXT (0xE6E946B8, from the handler at 320
    /// ms and from every rolling line at 500 ms) clones the CreditsArea prototype string with the next line (looping to the
    /// first) and sets its RollCredit script (RollCredit_ENDGAME after the game is won); CREDIT_AT_TOP (0x29161540, the
    /// script's end) deletes that object on the next tick. Back → 0x587C018B → the package's leave → EXIT_COMPLETE →
    /// MainMenu_Sub (the options menu), or the game-won screen chain (GameWonScreen). Not in beat-game mode: black 0xEB4CF244 hidden and
    /// 0x8CB81F09 posted; beat-game: 0x0BF41045 hidden, 0x3111B806 posted.
    /// </summary>
    public sealed class CreditsScreen : FEMenuScreen
    {
        public override string Package => "Credits";
        const uint CreditNext = 0xE6E946B8, CreditAtTop = 0x29161540, LeaveScreen = 0x587C018B;
        readonly bool _beatGame;
        readonly List<string> _lines = new List<string>();
        int _next = -1;
        FEView.Node _prototype, _pendingDelete;
        bool _loaded;

        public CreditsScreen(bool beatGame = false) { _beatGame = beatGame || FEDb.Is(FEGameMode.BeatGame); }

        protected override void Setup()
        {
            if (!_beatGame) { SetInvisible(0xEB4CF244); View.PackageMessage(0x8CB81F09); }
            else { SetInvisible(0x0BF41045); View.PackageMessage(0x3111B806); }
        }

        /// <summary>CREDITS\NA_ENGLISH.TXT (America) [?: region fixed to NA; other languages use &lt;LANGUAGE&gt;.TXT].</summary>
        static string FileName()
        {
            string lang = FrontEndData.LanguageName;
            string prefix = lang == "ENGLISH" ? "NA_" : "";
            return Path.Combine(GameData.Root ?? "", "CREDITS", prefix + lang + ".TXT");
        }

        /// <summary>UnicodeFile::Load + LineWrap(0x2D).</summary>
        void Load()
        {
            _lines.Clear();
            string path = FileName();
            if (!File.Exists(path)) path = Path.Combine(GameData.Root ?? "", "CREDITS", "NA_ENGLISH.TXT");
            if (!File.Exists(path)) return;
            var d = File.ReadAllBytes(path);
            string text;
            if (d.Length >= 2 && d[0] == 0xFE && d[1] == 0xFF) text = Encoding.BigEndianUnicode.GetString(d, 2, d.Length - 2);
            else if (d.Length >= 2 && d[0] == 0xFF && d[1] == 0xFE) text = Encoding.Unicode.GetString(d, 2, d.Length - 2);
            else text = Encoding.Unicode.GetString(d);
            foreach (var raw in text.Split('\r', '\n', '\0'))
            {
                if (raw.Length == 0) continue;
                // LineWrap: at the 45th character break at the last space after a non-space; the spaces become line ends
                var line = raw;
                int count = 0, lastSpace = -1, start = 0;
                for (int i = 0; i < line.Length; i++)
                {
                    count++;
                    if (count > 1 && line[i] == ' ' && line[i - 1] != ' ') lastSpace = i;
                    if (count >= 45 && lastSpace >= 0)
                    {
                        int e = lastSpace;
                        _lines.Add(line.Substring(start, e - start));
                        while (e < line.Length && line[e] == ' ') e++;
                        count = i - lastSpace;
                        start = e;
                        lastSpace = -1;
                    }
                }
                if (start < line.Length) _lines.Add(line.Substring(start));
            }
        }

        protected override void NotificationMessage(uint msg, FEView.Node obj)
        {
            switch (msg)
            {
                case FEHash.InitComplete:
                    Load();
                    _prototype = Obj("CreditsArea");
                    _loaded = true;
                    break;
                case FEHash.ExitComplete:
                    _loaded = false;
                    // 0x533288: IsBeatGameMode (FEGameMode bit 0x40000) → FEGameWonScreen::QueuePackageSwitchForNextScreen
                    // (0x5332BB, GameWonScreen.cs), else MainMenu_Sub (the options menu)
                    if (_beatGame) GameWonScreen.QueuePackageSwitchForNextScreen(FE, this);
                    else FE.Defer(() => { if (FE.Top == this) FE.Replace(new OptionsMainScreen(false)); });
                    break;
                case FEHash.ScreenTick:
                    if (_pendingDelete != null) { View.Remove(_pendingDelete); _pendingDelete = null; }
                    break;
                case CreditNext:
                    if (!_loaded || _lines.Count == 0 || _prototype == null) break;
                    _next = (_next + 1) % _lines.Count;
                    var ns = View.Clone(_prototype);
                    if (ns == null) break;
                    View.SetText(ns, _lines[_next]);
                    View.SetVisibleTree(ns);
                    View.SetScript(ns, FrontEndData.Hash(_beatGame ? "RollCredit_ENDGAME" : "RollCredit"), false);
                    break;
                case CreditAtTop:
                    if (obj != null && obj != _prototype) _pendingDelete = obj;
                    break;
                case FEHash.PadBack:
                    View.PackageMessage(LeaveScreen);
                    break;
                case FEHash.PadAccept:
                    if (_beatGame) View.PackageMessage(LeaveScreen);
                    break;
            }
        }
    }
}
