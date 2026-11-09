using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using GungeonOnline.Net;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>Message channels. Values are on the wire; never reorder.</summary>
    public enum Channel : byte
    {
        Control = 0,
        Input = 1,
        Snapshot = 2,
        Event = 3,
        Lobby = 4,
        World = 5,
        Props = 6,
        Backup = 7,
        Move = 8,    // host to a player's own game: move your player here (see PlayerSync)
        Looks = 9,   // host to guests: boss parts and enemy beams (see LookSync)
    }

    /// <summary>
    /// The live online session. Receives at the very start of each frame (before input is polled and before
    /// any gameplay update) and sends at the very end (after physics), so a packet never waits an extra frame.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    internal sealed class NetSession : MonoBehaviour
    {
        public static NetSession Instance { get; private set; }
        public static bool Active => Instance != null && Instance.node != null;
        public static bool IsHost => Active && Instance.node.IsHost;
        public static int LocalSlot => Active ? Instance.node.LocalSlot : 0;

        NetNode node;
        StunClient stun;
        SteamLobby steam;
        bool steamInitTried;
        System.Net.EndPoint steamHost;
        UpnpMapper upnp;
        public string Status = "Offline";
        public string InviteCodeText;
        public string LastError;
        readonly List<KeyValuePair<IPEndPoint, double>> punchTargets = new List<KeyValuePair<IPEndPoint, double>>();
        double nextStats;
        bool joinExtended, stunFailLogged;
        UpnpMapper.State upnpLogged = UpnpMapper.State.Idle;
        double nextRefusedToast;
        public int JoinAttemptsSeen;

        public NetNode Node => node;
        public StunClient Stun => stun;
        public UpnpMapper Upnp => upnp;

        /// <summary>Where other players reach this game from the internet (from STUN). In tests: the loopback port.</summary>
        public IPEndPoint PublicEndPoint
        {
            get
            {
                if (Dev.DevEnv.IsHarness && udp != null && udp.LocalEndPoint is IPEndPoint lp) return new IPEndPoint(IPAddress.Loopback, lp.Port);
                return stun?.Result;
            }
        }

        /// <summary>Send a few packets toward a friend so this side's router lets their join in (see <see cref="Knock"/>).</summary>
        public void OpenPathTo(IPEndPoint ep) { if (Active) AddPunch(ep); }
        public bool SteamReady => steam != null;
        /// <summary>Joining by code: the host's Steam id, learned through the relay (0 when unknown or the host has no Steam).</summary>
        public ulong KnockSteamId;
        ulong steamFallback; // a Steam id to connect to on the next frame, after a code join found no way through
        public void InviteSteamFriend() => steam?.Invite();

        readonly Dictionary<Channel, Action<Connection, byte[], Delivery>> handlers = new Dictionary<Channel, Action<Connection, byte[], Delivery>>();
        public event Action<Connection> PeerJoined;
        public event Action<Connection, string> PeerLeft;
        public event Action SessionEnded;

        static string gameHash;
        public static string GameHash
        {
            get
            {
                if (gameHash != null) return gameHash;
                try
                {
                    var path = Path.Combine(Path.Combine(Application.dataPath, "Managed"), "Assembly-CSharp.dll");
                    using (var md5 = MD5.Create())
                    using (var f = File.OpenRead(path))
                        gameHash = BitConverter.ToString(md5.ComputeHash(f)).Replace("-", "");
                }
                catch (Exception e) { gameHash = "unknown"; Plugin.Log.LogWarning("Game hash: " + e.Message); }
                return gameHash;
            }
        }

        public static double Now => Net.Clock.Now;

        void Awake()
        {
            Instance = this;
            Input.InputPump.BeforePoll += EarlyPoll;
            gameObject.AddComponent<NetFlush>();
        }

        /// <summary>Listen on a channel. Several listeners may share one (the lobby and the peer lanes both use Lobby).</summary>
        public void On(Channel ch, Action<Connection, byte[], Delivery> h) => handlers[ch] = handlers.TryGetValue(ch, out var prev) ? prev + h : h;

        Handshake MakeHandshake() => new Handshake { ModVersion = Plugin.Version, GameHash = GameHash, PlayerName = NetConfig.PlayerName.Value, PlayerId = NetConfig.PlayerId.Value };

        /// <summary>Host: player id -> slot, so a returning player (drop, crash) gets their own spot and character back.</summary>
        public readonly Dictionary<string, int> KnownPlayers = new Dictionary<string, int>();
        /// <summary>Client: trying to get back to the host after the connection dropped.</summary>
        public bool Reconnecting { get; private set; }
        double nextReconnect;
        bool leaving;

        // ---------- Keep-alive while loading ----------
        // A floor load or the trip back to character select can block the game thread for longer than the
        // timeout. A background thread then tells every direct UDP peer "still here", so nobody is dropped.
        UdpDatagramSocket udp;
        volatile EndPoint[] keepAlivePeers = new EndPoint[0];
        static long TickMs => System.Diagnostics.Stopwatch.GetTimestamp() * 1000 / System.Diagnostics.Stopwatch.Frequency;
        long lastMainTickMs;
        System.Threading.Thread keepAliveThread;
        static readonly byte[] KeepAlivePacket = { (byte)(Packet.Magic & 0xFF), (byte)(Packet.Magic >> 8), (byte)PacketType.KeepAlive };

        void KeepAliveLoop()
        {
            while (true)
            {
                System.Threading.Thread.Sleep(500);
                try
                {
                    var sock = udp;
                    if (sock == null || TickMs - System.Threading.Interlocked.Read(ref lastMainTickMs) < 1500) continue;
                    foreach (var ep in keepAlivePeers)
                        if (ep is System.Net.IPEndPoint) sock.SendTo(ep, KeepAlivePacket, KeepAlivePacket.Length);
                }
                catch { }
            }
        }

        void NoteMainTick()
        {
            System.Threading.Interlocked.Exchange(ref lastMainTickMs, TickMs);
            if (node == null) { if (keepAlivePeers.Length > 0) keepAlivePeers = new EndPoint[0]; return; }
            bool same = keepAlivePeers.Length == node.Connections.Count;
            for (int i = 0; same && i < keepAlivePeers.Length; i++) same = ReferenceEquals(keepAlivePeers[i], node.Connections[i].Remote);
            if (!same)
            {
                var list = new EndPoint[node.Connections.Count];
                for (int i = 0; i < list.Length; i++) list[i] = node.Connections[i].Remote;
                keepAlivePeers = list;
            }
            if (keepAliveThread == null)
            {
                keepAliveThread = new System.Threading.Thread(KeepAliveLoop) { IsBackground = true, Name = "GungeonOnline keep-alive" };
                keepAliveThread.Start();
            }
        }

        IDatagramSocket MakeSocket(int port, bool withSteam)
        {
            IDatagramSocket s = udp = new UdpDatagramSocket(port);
            if (withSteam && steam != null)
            {
                var steamSock = new SteamDatagramSocket(id => node != null && (node.IsHost || (steamHost is SteamEndPoint h && h.Id == id) || PeerLanes.KnownSteamIds.Contains(id.m_SteamID)));
                s = new CompositeSocket(ep => ep is SteamEndPoint ? 1 : 0, s, steamSock);
            }
            float dropAt = EnvF("GO_DROP_AT"), dropFor = EnvF("GO_DROP_FOR");
            if (dropFor > 0)
            {
                Plugin.Log.LogInfo($"Simulated outage at {dropAt}s for {dropFor}s");
                s = new OutageSocket(s, () => Now >= dropAt && Now < dropAt + dropFor);
            }
            float lat = EnvF("GO_SIM_LAT"), jit = EnvF("GO_SIM_JIT"), loss = EnvF("GO_SIM_LOSS");
            if (lat > 0 || jit > 0 || loss > 0)
            {
                Plugin.Log.LogInfo($"Simulated link: rtt={lat}ms jitter={jit}ms loss={loss}%");
                s = new SimulatedLinkSocket(s, () => Now, lat, jit, loss, Environment.TickCount);
            }
            if (Environment.GetEnvironmentVariable("GO_SIM_STRICT_ROUTER") == "1")
            {
                Plugin.Log.LogInfo("Simulated strict router: packets from unknown addresses are dropped");
                s = new StrictRouterSocket(s);
            }
            return s;
        }

        static float EnvF(string k)
        {
            var v = Environment.GetEnvironmentVariable(k);
            float f;
            return v != null && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out f) ? f : 0f;
        }

        public void Host()
        {
            if (Active) return;
            // A run started offline cannot be shared: its floors were not built from a seed the friend's game can
            // repeat, so the friend would never reach this floor. Online play starts in the Breach.
            if (Game.RunSync.CurrentIndex < 0 && GameManager.HasInstance && GameManager.Instance.Dungeon != null && !Game.SceneObjects.InBreach)
            {
                Toasts.Show("Online play starts in the Breach. Finish or quit this run, then pick Host there.", 7f, true);
                Plugin.Log.LogInfo("Host refused: in a run that was started offline");
                return;
            }
            try
            {
                int port = Dev.DevEnv.PortOverride ?? NetConfig.Port.Value;
                node = new NetNode(MakeSocket(port, withSteam: true), true, NetConfig.MaxPlayers.Value, MakeHandshake());
                node.SlotForPlayer = pid => !string.IsNullOrEmpty(pid) && KnownPlayers.TryGetValue(pid, out var known) ? known : -1;
                steam?.Host(NetConfig.MaxPlayers.Value);
                Wire();
                StartStun();
                StartCoroutine(Knock.Host(this, node));
                if (NetConfig.UseUpnp.Value && !Dev.DevEnv.IsHarness)
                {
                    upnp = new UpnpMapper();
                    upnp.MapAsync(port, "Enter The Gungeon Online Co-Op Mod");
                }
                Status = "Hosting";
                Toasts.Show("Hosting. Getting your invite code...");
                Game.BackgroundRun.SessionActive = true;
                Game.BackgroundRun.Refresh();
                Plugin.Log.LogInfo($"Hosting on UDP {port}, max {NetConfig.MaxPlayers.Value} players");
            }
            catch (Exception e) { Fail("Could not host: " + e.Message); }
        }

        public void Join(string code)
        {
            if (Active) return;
            if (!InviteCode.TryParse(code, out var ep)) { LastError = "That code is not valid."; return; }
            try
            {
                node = new NetNode(MakeSocket(Dev.DevEnv.PortOverride ?? 0, withSteam: false), false, NetConfig.MaxPlayers.Value, MakeHandshake());
                Wire();
                StartStun();
                joinExtended = false;
                node.Connect(ep, Now);
                StartCoroutine(Knock.Joiner(this, node, ep));
                NetConfig.LastJoin.Value = code.Trim();
                Toasts.Show("Joining...");
                // Opening our NAT toward the host lets a host behind a strict NAT reach us once it punches back.
                AddPunch(ep);
                Status = "Joining " + ep;
                Game.BackgroundRun.SessionActive = true;
                Game.BackgroundRun.Refresh();
                Plugin.Log.LogInfo("Joining " + ep);
            }
            catch (Exception e) { Fail("Could not join: " + e.Message); }
        }

        /// <summary>Host side: punch toward a joiner's reply code when routers block the first contact.</summary>
        public void LetIn(string code)
        {
            if (!Active || !node.IsHost) return;
            if (!InviteCode.TryParse(code, out var ep)) { LastError = "That code is not valid."; return; }
            AddPunch(ep);
            Plugin.Log.LogInfo("Punching toward " + ep);
        }

        /// <summary>Accepted a Steam invite: connect to the lobby owner over Steam P2P.</summary>
        internal void JoinSteam(Steamworks.CSteamID owner)
        {
            if (Active) Leave("joining another session");
            try
            {
                steamHost = new SteamEndPoint(owner);
                NetConfig.LastJoin.Value = "steam:" + owner.m_SteamID;
                Toasts.Show("Joining your Steam friend...");
                node = new NetNode(MakeSocket(Dev.DevEnv.PortOverride ?? 0, withSteam: true), false, NetConfig.MaxPlayers.Value, MakeHandshake());
                Wire();
                node.Connect(steamHost, Now);
                Status = "Joining Steam friend";
                Game.BackgroundRun.SessionActive = true;
                Game.BackgroundRun.Refresh();
            }
            catch (Exception e) { Fail("Could not join over Steam: " + e.Message); }
        }

        void AddPunch(IPEndPoint ep) => punchTargets.Add(new KeyValuePair<IPEndPoint, double>(ep, Now + 8.0));

        void StartStun()
        {
            var parts = NetConfig.StunServer.Value.Split(':');
            int port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 19302;
            stun = new StunClient(node.Socket, parts[0], port);
        }

        void Wire()
        {
            node.RawHandler = (from, data) => stun != null && stun.TryHandle(data);
            node.Connected += c =>
            {
                Plugin.Log.LogInfo($"Peer connected: slot {c.Slot} {c.Remote} name={c.Tag}");
                string name = c.Tag as string;
                if (name == "Gungeoneer") name = null;
                if (node.IsHost)
                {
                    bool returning = !string.IsNullOrEmpty(c.PlayerId) && KnownPlayers.ContainsKey(c.PlayerId);
                    if (!string.IsNullOrEmpty(c.PlayerId)) KnownPlayers[c.PlayerId] = c.Slot;
                    Toasts.Show((string.IsNullOrEmpty(name) || name == "Gungeoneer" ? "A player" : name) + (returning ? " is back" : " joined"));
                }
                else
                {
                    Status = "Connected";
                    Toasts.Show(Reconnecting ? "Reconnected" : "Connected");
                    Reconnecting = false;
                    Toasts.Banner = null;
                }
                PeerJoined?.Invoke(c);
            };
            node.Disconnected += (c, why) =>
            {
                Plugin.Log.LogInfo($"Peer left: slot {c.Slot} ({why})");
                PeerLeft?.Invoke(c, why);
                if (node == null) return;
                string who = string.IsNullOrEmpty(c.Tag as string) || (string)c.Tag == "Gungeoneer" ? (node.IsHost ? "A player" : "The host") : (string)c.Tag;
                if (node.IsHost)
                {
                    bool quit = why == "left" || why == "session closed" || why == "game closed";
                    Toasts.Show(who + (quit ? " left" : " lost connection"), 5f, !quit);
                    return;
                }
                // Client: never give up on its own. Keep everything and keep trying to get back.
                if (!node.IsHost && why != null && why.Contains("saved the run"))
                {
                    // The host stopped for now: back to the Breach. The run continues when they pick Resume saved run.
                    Toasts.Show("The host saved the run for later. You'll rejoin it when they resume.", 7f);
                    Leave("left");
                    if (GameManager.HasInstance && !Game.SceneObjects.InBreach) GameManager.Instance.DelayedReturnToFoyer(1f);
                    return;
                }
                bool hostQuit = why == "session closed" || why == "game closed";
                if (!leaving && node.LastHost != null && why != "left")
                {
                    Reconnecting = true;
                    Status = "Reconnecting";
                    Toasts.Banner = hostQuit ? "The host closed the game. Waiting for them to come back..." : "Connection lost. Reconnecting...";
                    nextReconnect = Now + 1.0;
                }
                else Leave("Disconnected");
            };
            node.Rejected += why =>
            {
                Plugin.Log.LogWarning("Join rejected: " + why);
                if (Reconnecting && why == "no answer from host") { nextReconnect = Now + 2.0; return; } // keep trying
                if (why == "no answer from host" && steamHost == null && KnockSteamId != 0 && steam != null)
                {
                    // Both games are on Steam and the direct path stayed shut (a router or carrier that drops everything
                    // unasked for, even after the punch): go through Steam, which relays when it has to.
                    steamFallback = KnockSteamId;
                    Toasts.Show("The direct connection is blocked. Connecting through Steam instead...", 7f);
                    Plugin.Log.LogInfo("Join: no direct path to the host after " + (joinExtended ? "80" : "20") + " s; connecting through Steam");
                    return;
                }
                if (why == "no answer from host" && !joinExtended && steamHost != null)
                {
                    // A Steam join: Steam can take a while to find a route (or the host's game is still loading a big
                    // modpack). Keep trying for another minute before giving up.
                    joinExtended = true;
                    node.Connect(steamHost, Now, 60);
                    Toasts.Show("Still trying to reach your Steam friend...", 6f, true);
                    Plugin.Log.LogInfo("Join: no answer over Steam yet, trying for another minute");
                    return;
                }
                if (why == "no answer from host" && !joinExtended && steamHost == null && node.LastHost is IPEndPoint hostEp)
                {
                    // Keep this same connection trying for another minute: the host's game can still open a path to us
                    // (by itself through the relay, or with our reply code), and the code only works while we keep trying.
                    joinExtended = true;
                    node.Connect(hostEp, Now, 60);
                    StartCoroutine(Knock.Joiner(this, node, hostEp));
                    if (InviteCodeText != null)
                    {
                        try { GUIUtility.systemCopyBuffer = InviteCodeText; } catch { }
                        Toasts.Show("Still trying to reach the host. Your reply code is copied: send it to the host, and they pick Let in friend.", 10f, true);
                    }
                    else Toasts.Show("Still trying to reach the host...", 6f, true);
                    Plugin.Log.LogInfo("Join: no answer yet, trying for another minute (reply code " + (InviteCodeText != null ? "copied" : "not ready") + ")");
                    return;
                }
                Toasts.Show(why != "no answer from host" ? why
                    : steamHost != null ? "Could not reach your Steam friend. Check that they are hosting with the same mod version, or try an invite code."
                    : "Could not reach the host. Check the invite code, or have your friend host instead.", 7f, true);
                Leave(why);
            };
            node.JoinAttempt += (from, theirMod, refused) =>
            {
                JoinAttemptsSeen++;
                Plugin.Log.LogInfo("Join attempt from " + from + " (mod " + theirMod + ")" + (refused != null ? ", turned away: " + refused : ""));
                if (refused == null || Now < nextRefusedToast) return;
                nextRefusedToast = Now + 10.0;
                Toasts.Show(theirMod != Plugin.Version ? "A friend tried to join with mod " + theirMod + ". You have " + Plugin.Version + ". You both need the same version."
                    : refused == "The game is full." ? "A friend tried to join, but the game is full."
                    : "A friend tried to join, but their game version is different. Update the game on both PCs.", 9f, true);
            };
            node.Message += (c, ch, data, d) =>
            {
                if (handlers.TryGetValue((Channel)ch, out var h))
                {
                    try { h(c, data, d); }
                    catch (Exception e) { Plugin.Log.LogError($"Handler {(Channel)ch}: {e}"); }
                }
            };
        }

        void EarlyPoll() => PollNow();

        /// <summary>Drain the network now. Safe to call several times per frame.</summary>
        public void PollNow()
        {
            if (node != null) node.Poll(Now);
        }

        void Update()
        {
            if (!steamInitTried && Time.frameCount > 30)
            {
                steamInitTried = true;
                if (SteamLobby.Available)
                {
                    steam = new SteamLobby();
                    steam.Init();
                    steam.JoinHost += JoinSteam;
                    if (NetConfig.PlayerName.Value == "Gungeoneer")
                        try { NetConfig.PlayerName.Value = Steamworks.SteamFriends.GetPersonaName(); } catch { }
                    Plugin.Log.LogInfo("Steam available: invites enabled");
                }
            }
            NoteMainTick();
            if (steamFallback != 0)
            {
                var id = steamFallback;
                steamFallback = 0;
                Leave(); // the code join, quietly (no error to show): a new frame, outside its callbacks
                LastError = null;
                JoinSteam(new Steamworks.CSteamID(id));
            }
            if (node == null) return;
            double now = Now;
            node.Poll(now);
            if (node == null) return;
            if (Reconnecting && !node.Connecting && node.Connections.Count == 0 && now >= nextReconnect)
            {
                node.Connect(node.LastHost, now, 6);
                nextReconnect = now + 7.0;
            }
            stun?.Update(now);
            if (stun != null && stun.Failed && !stunFailLogged) { stunFailLogged = true; Plugin.Log.LogInfo("STUN: could not learn our public address"); }
            if (upnp != null && upnp.Status != upnpLogged && (upnp.Status == UpnpMapper.State.Mapped || upnp.Status == UpnpMapper.State.Failed))
            {
                upnpLogged = upnp.Status;
                Plugin.Log.LogInfo("UPnP: " + (upnp.Status == UpnpMapper.State.Mapped ? "port forwarded by the router" : "router did not forward the port (" + upnp.Error + ")"));
            }
            if (stun != null && stun.Result != null && InviteCodeText == null)
            {
                // Hosts behind UPnP keep the same port outside; otherwise the STUN-mapped port is the one to share.
                InviteCodeText = InviteCode.Encode(stun.Result);
                Plugin.Log.LogInfo("Invite code ready: " + InviteCodeText);
                if (node.IsHost)
                {
                    try { GUIUtility.systemCopyBuffer = InviteCodeText; } catch { }
                    Toasts.Show("Invite code copied: " + InviteCodeText + ". Paste it to your friend.", 7f);
                }
            }
            for (int i = punchTargets.Count - 1; i >= 0; i--)
            {
                if (now > punchTargets[i].Value) { punchTargets.RemoveAt(i); continue; }
                var b = new byte[] { (byte)(Packet.Magic & 0xFF), (byte)(Packet.Magic >> 8), (byte)PacketType.Punch };
                node.Socket.SendTo(punchTargets[i].Key, b, b.Length);
            }
            if (now >= nextStats) { node.UpdateStats(now); nextStats = now + 1.0; }
        }

        /// <summary>Called by <see cref="NetFlush"/> at the very end of the frame.</summary>
        internal void FlushNow()
        {
            if (node != null) node.Flush(Now);
        }

        /// <summary>Rejoin the last game (after a crash or restart).</summary>
        public void RejoinLast()
        {
            var last = NetConfig.LastJoin.Value;
            if (string.IsNullOrEmpty(last)) return;
            if (last.StartsWith("steam:") && ulong.TryParse(last.Substring(6), out var id)) JoinSteam(new Steamworks.CSteamID(id));
            else Join(last);
        }

        public void Leave(string reason = null)
        {
            if (node == null) return;
            leaving = true;
            Reconnecting = false;
            Toasts.Banner = null;
            var n = node;
            node = null;
            try { n.Dispose(); } catch { }
            upnp?.Unmap();
            steam?.Leave();
            steamHost = null;
            upnp = null;
            stun = null;
            InviteCodeText = null;
            punchTargets.Clear();
            joinExtended = false;
            KnockSteamId = 0;
            stunFailLogged = false;
            upnpLogged = UpnpMapper.State.Idle;
            Status = "Offline";
            if (reason != null) LastError = reason;
            Game.BackgroundRun.SessionActive = false;
            Plugin.Log.LogInfo("Session ended" + (reason != null ? ": " + reason : ""));
            KnownPlayers.Clear();
            SessionEnded?.Invoke();
            leaving = false;
        }

        void Fail(string msg)
        {
            Plugin.Log.LogError(msg);
            LastError = msg;
            Leave();
        }

        void OnApplicationQuit() { SteamDatagramSocket.Quitting = true; Leave("game closed"); }
    }

    /// <summary>Sends everything queued this frame after all gameplay and physics have run.</summary>
    [DefaultExecutionOrder(32000)]
    internal sealed class NetFlush : MonoBehaviour
    {
        void LateUpdate() => NetSession.Instance?.FlushNow();
    }
}
