using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using UnityEngine;

namespace NfsMw.Game
{
    /// <summary>
    /// LAN races over UDP (the game's own online/LAN service is long gone). One machine hosts, the others find it
    /// by broadcast on <see cref="Port"/> and join with their car. Every machine drives its own car and sends its
    /// state 20 times a second through the host, which relays it; the host starts the race once everyone has loaded
    /// the world. Messages are short text lines: HELLO, HOST, JOIN, WELCOME, ROSTER, START, READY, GO, STATE, FINISH,
    /// LEAVE.
    /// </summary>
    public sealed class LanSession : IDisposable
    {
        public const int Port = 47805;

        public sealed class Peer
        {
            public int Id;
            public string Name, Car;
            public IPEndPoint EndPoint;
            public bool Ready;
            public float FinishTime = -1;
            // last received state (Unity space)
            public Vector3 Position, Velocity;
            public Quaternion Rotation = Quaternion.identity;
            public float StateTime;
            public float Progress;
        }

        public sealed class HostInfo
        {
            public IPEndPoint EndPoint;
            public string Name, Event;
            public int Players;
            public float Seen;
        }

        public bool IsHost { get; private set; }
        public int LocalId { get; private set; } = -1;
        public string LocalName, LocalCar;
        public readonly List<Peer> Peers = new List<Peer>();          // everyone, local player included
        public readonly List<HostInfo> Hosts = new List<HostInfo>();  // found by a client
        public uint EventKey { get; private set; }
        public bool Started { get; private set; }
        public bool Go { get; private set; }
        public event Action<uint> OnStart;

        readonly UdpClient _udp;
        IPEndPoint _host;
        float _nextHello;
        int _nextId = 1;

        LanSession(int port)
        {
            _udp = new UdpClient(AddressFamily.InterNetwork);
            _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
            _udp.EnableBroadcast = true;
        }

        /// <summary>Hosts on the LAN port (or another port, for tests on one machine).</summary>
        public static LanSession Host(string name, string car, int port = Port)
        {
            var s = new LanSession(port) { IsHost = true, LocalId = 0, LocalName = name, LocalCar = car };
            s.Peers.Add(new Peer { Id = 0, Name = name, Car = car });
            return s;
        }

        /// <summary>A client on any free port; call <see cref="Discover"/> or <see cref="Join"/>.</summary>
        public static LanSession Client(string name, string car) => new LanSession(0) { LocalName = name, LocalCar = car };

        public Peer Local => Peers.Find(p => p.Id == LocalId);

        void Send(IPEndPoint to, string msg)
        {
            var b = Encoding.UTF8.GetBytes(msg);
            try { _udp.Send(b, b.Length, to); }
            catch (SocketException) { }
        }

        void SendAll(string msg, int exceptId = -1)
        {
            foreach (var p in Peers)
                if (p.Id != LocalId && p.Id != exceptId && p.EndPoint != null) Send(p.EndPoint, msg);
        }

        /// <summary>Client: broadcasts HELLO once a second and lists the hosts that answer.</summary>
        public void Discover(int port = Port)
        {
            if (Time.unscaledTime < _nextHello) return;
            _nextHello = Time.unscaledTime + 1f;
            Send(new IPEndPoint(IPAddress.Broadcast, port), "HELLO");
            Send(new IPEndPoint(IPAddress.Loopback, port), "HELLO");
        }

        public void Join(IPEndPoint host)
        {
            _host = host;
            Send(host, $"JOIN|{Clean(LocalName)}|{Clean(LocalCar)}");
        }

        /// <summary>Host: everyone loads this event.</summary>
        public void StartRace(uint eventKey)
        {
            if (!IsHost) return;
            EventKey = eventKey;
            Started = true;
            foreach (var p in Peers) p.Ready = p.Id == LocalId ? false : p.Ready;
            SendAll($"START|{eventKey}");
            OnStart?.Invoke(eventKey);
        }

        /// <summary>This machine has loaded the world; the host sends GO when every player has.</summary>
        public void SetReady()
        {
            if (IsHost) { var me = Local; if (me != null) me.Ready = true; CheckGo(); }
            else if (_host != null) Send(_host, $"READY|{LocalId}");
        }

        void CheckGo()
        {
            if (!IsHost || Go || !Started) return;
            foreach (var p in Peers) if (!p.Ready) return;
            Go = true;
            SendAll("GO");
        }

        public void SendState(Vector3 pos, Quaternion rot, Vector3 vel, float progress)
        {
            var me = Local;
            if (me == null) return;
            me.Position = pos; me.Rotation = rot; me.Velocity = vel; me.Progress = progress; me.StateTime = Time.time;
            string msg = string.Format(CultureInfo.InvariantCulture, "STATE|{0}|{1:0.###}|{2:0.###}|{3:0.###}|{4:0.####}|{5:0.####}|{6:0.####}|{7:0.####}|{8:0.##}|{9:0.##}|{10:0.##}|{11:0.#}",
                LocalId, pos.x, pos.y, pos.z, rot.x, rot.y, rot.z, rot.w, vel.x, vel.y, vel.z, progress);
            if (IsHost) SendAll(msg);
            else if (_host != null) Send(_host, msg);
        }

        public void SendFinish(float time)
        {
            var me = Local;
            if (me != null) me.FinishTime = time;
            string msg = string.Format(CultureInfo.InvariantCulture, "FINISH|{0}|{1:0.###}", LocalId, time);
            if (IsHost) SendAll(msg);
            else if (_host != null) Send(_host, msg);
        }

        /// <summary>Handles everything received since the last call (call once a frame).</summary>
        public void Poll()
        {
            while (_udp.Available > 0)
            {
                IPEndPoint from = null;
                byte[] b;
                try { b = _udp.Receive(ref from); }
                catch (SocketException) { break; }
                Handle(Encoding.UTF8.GetString(b).Split('|'), from);
            }
        }

        void Handle(string[] m, IPEndPoint from)
        {
            switch (m[0])
            {
                case "HELLO" when IsHost:
                    Send(from, $"HOST|{Clean(LocalName)}|{(Started ? "racing" : "lobby")}|{Peers.Count}");
                    break;
                case "HOST" when !IsHost && m.Length >= 4:
                {
                    var h = Hosts.Find(x => x.EndPoint.Equals(from));
                    if (h == null) Hosts.Add(h = new HostInfo { EndPoint = from });
                    h.Name = m[1];
                    h.Event = m[2];
                    h.Players = int.Parse(m[3]);
                    h.Seen = Time.unscaledTime;
                    break;
                }
                case "JOIN" when IsHost && m.Length >= 3:
                {
                    var p = Peers.Find(x => from.Equals(x.EndPoint));
                    if (p == null) Peers.Add(p = new Peer { Id = _nextId++, EndPoint = from });
                    p.Name = m[1];
                    p.Car = m[2];
                    Send(from, $"WELCOME|{p.Id}");
                    BroadcastRoster();
                    break;
                }
                case "WELCOME" when !IsHost:
                    LocalId = int.Parse(m[1]);
                    _host = from;
                    break;
                case "ROSTER" when !IsHost:
                {
                    // id:name:car;...
                    var keep = new List<Peer>();
                    foreach (var e in m[1].Split(';'))
                    {
                        var f = e.Split(':');
                        if (f.Length < 3) continue;
                        int id = int.Parse(f[0]);
                        var p = Peers.Find(x => x.Id == id) ?? new Peer { Id = id };
                        p.Name = f[1];
                        p.Car = f[2];
                        keep.Add(p);
                    }
                    Peers.Clear();
                    Peers.AddRange(keep);
                    break;
                }
                case "START" when !IsHost:
                    EventKey = uint.Parse(m[1]);
                    Started = true;
                    OnStart?.Invoke(EventKey);
                    break;
                case "READY" when IsHost:
                {
                    var p = Peers.Find(x => x.Id == int.Parse(m[1]));
                    if (p != null) p.Ready = true;
                    CheckGo();
                    break;
                }
                case "GO" when !IsHost:
                    Go = true;
                    break;
                case "STATE" when m.Length >= 13:
                {
                    int id = int.Parse(m[1]);
                    if (id == LocalId) break;
                    var p = Peers.Find(x => x.Id == id);
                    if (p == null) break;
                    float F(int i) => float.Parse(m[i], CultureInfo.InvariantCulture);
                    p.Position = new Vector3(F(2), F(3), F(4));
                    p.Rotation = new Quaternion(F(5), F(6), F(7), F(8));
                    p.Velocity = new Vector3(F(9), F(10), F(11));
                    p.Progress = F(12);
                    p.StateTime = Time.time;
                    if (IsHost) SendAll(string.Join("|", m), id);
                    break;
                }
                case "FINISH":
                {
                    var p = Peers.Find(x => x.Id == int.Parse(m[1]));
                    if (p != null) p.FinishTime = float.Parse(m[2], CultureInfo.InvariantCulture);
                    if (IsHost) SendAll(string.Join("|", m), p?.Id ?? -1);
                    break;
                }
                case "LEAVE":
                {
                    Peers.RemoveAll(x => x.Id == int.Parse(m[1]));
                    if (IsHost) BroadcastRoster();
                    break;
                }
            }
        }

        void BroadcastRoster()
        {
            var sb = new StringBuilder("ROSTER|");
            foreach (var p in Peers) sb.Append(p.Id).Append(':').Append(Clean(p.Name)).Append(':').Append(Clean(p.Car)).Append(';');
            SendAll(sb.ToString());
        }

        static string Clean(string s) => (s ?? "").Replace("|", "").Replace(";", "").Replace(":", "");

        public void Dispose()
        {
            try
            {
                if (IsHost) SendAll($"LEAVE|{LocalId}");
                else if (_host != null) Send(_host, $"LEAVE|{LocalId}");
            }
            catch (Exception) { }
            _udp.Close();
        }
    }

    /// <summary>A remote player's car: kinematic, following the received states with extrapolation and smoothing.</summary>
    public sealed class NetCar : MonoBehaviour
    {
        public LanSession.Peer Peer;
        Rigidbody _rb;

        void Awake()
        {
            _rb = GetComponent<Rigidbody>();
            if (_rb) _rb.isKinematic = true;
        }

        void FixedUpdate()
        {
            if (Peer == null || Peer.StateTime <= 0) return;
            float age = Mathf.Min(Time.time - Peer.StateTime, 0.25f);
            var target = Peer.Position + Peer.Velocity * age;
            var pos = Vector3.Lerp(transform.position, target, 0.35f);
            var rot = Quaternion.Slerp(transform.rotation, Peer.Rotation, 0.35f);
            if ((transform.position - target).sqrMagnitude > 25f * 25f) pos = target; // snap after a teleport
            if (_rb) { _rb.MovePosition(pos); _rb.MoveRotation(rot); }
            else transform.SetPositionAndRotation(pos, rot);
        }
    }
}
