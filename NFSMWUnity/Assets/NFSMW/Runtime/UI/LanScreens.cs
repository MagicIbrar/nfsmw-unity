using System.Linq;
using NfsMw.Game;
using UnityEngine;

namespace NfsMw.UI
{
    /// <summary>LAN Race: host a race or join one found on the local network.</summary>
    public sealed class LanMenuScreen : CarouselScreen
    {
        public override string Package => "MainMenu_Sub";
        protected override string Title => "lan race";

        protected override void Build()
        {
            string name = GameSession.Profile?.Name ?? System.Environment.UserName;
            Options.Add(new Option
            {
                Label = "Host", Icon = "MAIN_ICON_GO_ONLINE",
                Run = () => FE.Push(new QuickCarScreen(car =>
                {
                    GameSession.EndLan();
                    GameSession.QuickCar = new OwnedCar { Car = car };
                    GameSession.Lan = LanSession.Host(name, car);
                    FE.Push(new RaceListScreen(RaceListScreen.Kind.Lan));
                }, "Host: pick your car")),
            });
            Options.Add(new Option
            {
                Label = "Join", Icon = "MAIN_ICON_LAN",
                Run = () => FE.Push(new QuickCarScreen(car =>
                {
                    GameSession.EndLan();
                    GameSession.QuickCar = new OwnedCar { Car = car };
                    GameSession.Lan = LanSession.Client(name, car);
                    FE.Push(new LanLobbyScreen());
                }, "Join: pick your car")),
            });
            Options.Add(new Option { Label = "Back", Icon = "MAIN_ICON_PROFILE_DELET", Run = () => FE.Pop() });
        }
    }

    /// <summary>
    /// The lobby (GenericDialog layout): a client lists the hosts it finds and joins with Enter; everyone sees the
    /// roster; the host starts with Enter. When the host starts, every machine loads the race.
    /// </summary>
    public sealed class LanLobbyScreen : FEScreen
    {
        public override string Package => "GenericDialog_ThreeButton";
        readonly Core.RaceEvent _event;
        int _hostIndex;
        float _refresh;
        bool _started;

        public LanLobbyScreen(Core.RaceEvent ev = null) { _event = ev; }

        public override void Open() => Refresh();

        void Refresh()
        {
            var lan = GameSession.Lan;
            if (lan == null) return;
            string title, body;
            if (lan.IsHost)
            {
                title = $"Hosting: {_event?.TypeName} {_event?.EventId}";
                body = string.Join("   ", lan.Peers.Select(p => $"{p.Name} ({CarNames.Display(p.Car)})")) + "      Enter: start   Esc: cancel";
            }
            else if (lan.LocalId < 0)
            {
                title = "Looking for LAN races";
                var hosts = lan.Hosts.Where(h => Time.unscaledTime - h.Seen < 4f).ToList();
                body = hosts.Count == 0 ? "No hosts found yet" :
                    string.Join("   ", hosts.Select((h, i) => $"{(i == _hostIndex % hosts.Count ? ">" : "")}{h.Name} ({h.Players} players, {h.Event})")) + "      Enter: join";
            }
            else
            {
                title = "Joined - waiting for the host";
                body = string.Join("   ", lan.Peers.Select(p => $"{p.Name} ({CarNames.Display(p.Car)})"));
            }
            var texts = View.Nodes.Where(n => n.Label != null).ToList();
            for (int i = 0; i < texts.Count; i++) View.SetText(texts[i], i == 0 ? title : i == 1 ? body : i == 2 ? (lan.IsHost ? "Start" : "OK") : "");
        }

        public override void Tick(MenuInput input)
        {
            var lan = GameSession.Lan;
            if (lan == null) { FE.Pop(); return; }
            lan.Poll();
            if (!lan.IsHost && lan.LocalId < 0) lan.Discover();
            if (Time.unscaledTime > _refresh) { _refresh = Time.unscaledTime + 0.5f; Refresh(); }
            if (lan.Started && !_started)
            {
                _started = true;
                var ev = _event ?? GameSession.Career.EventFor(lan.EventKey);
                if (ev != null)
                {
                    GameSession.Mode = GameMode.QuickRace;
                    GameFlow.StartRace(ev);
                }
                return;
            }
            if (input.Left || input.Right) _hostIndex++;
            if (input.Accept)
            {
                if (lan.IsHost && _event != null && lan.Peers.Count >= 1) lan.StartRace(_event.Key);
                else if (!lan.IsHost && lan.LocalId < 0)
                {
                    var hosts = lan.Hosts.Where(h => Time.unscaledTime - h.Seen < 4f).ToList();
                    if (hosts.Count > 0) lan.Join(hosts[_hostIndex % hosts.Count].EndPoint);
                }
            }
            if (input.Back)
            {
                GameSession.EndLan();
                FE.Pop();
            }
        }
    }
}
