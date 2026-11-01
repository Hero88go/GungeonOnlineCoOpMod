using System.Collections.Generic;
using GungeonOnline.Game;
using GungeonOnline.Net;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>
    /// Who is in the session and which character each player picked. Each player picks in their own Breach;
    /// the host keeps the roster and sends it to everyone; every machine spawns the others' characters.
    /// </summary>
    internal sealed class Lobby : MonoBehaviour
    {
        const byte MsgChoice = 1, MsgRoster = 2, MsgAim = 3, MsgStyle = 4, MsgColor = 6; // 5 is PeerLanes
        /// <summary>Each player's own color pick (-1 auto), keyed by slot.</summary>
        public static readonly Dictionary<int, int> Colors = new Dictionary<int, int>();
        int sentColor = -2;

        public sealed class Entry { public string Prefab = ""; public string Name = ""; public bool Alt; } // Alt: wearing the alternate costume
        public readonly Dictionary<int, Entry> Roster = new Dictionary<int, Entry>();

        /// <summary>Each player's own aim assist settings, so every machine aims their copy the way they aim it.</summary>
        public struct AimPrefs { public float Assist; public bool Beam; public bool Super; }
        public static readonly Dictionary<int, AimPrefs> Aim = new Dictionary<int, AimPrefs>();
        string sentAim;

        /// <summary>Guest: the co-op style the host chose (-1 until it arrives).</summary>
        public static int HostCoopStyle = -1;
        public static int HostMaxPlayers = -1;
        int sentMax = -1;
        int sentBreach = -1;
        /// <summary>Guest: the host said it is in the Breach (-1 = not told yet).</summary>
        public static int HostInBreach = -1;
        float nextBreachTrip;
        float ownRunSince = -1f;
        static readonly bool oldJoin = Dev.DevEnv.IsHarness && System.Environment.GetEnvironmentVariable("GO_OLD_JOIN") == "1";
        int sentStyle = -1;
        float nextAimCheck;
        float nextChoiceCheck;
        string sentChoice;
        PlayerController lastLocal;

        void Start()
        {
            var s = NetSession.Instance;
            s.On(Channel.Lobby, OnMessage);
            s.PeerJoined += c => { if (NetSession.IsHost) { SendRoster(); SendAllAim(c); SendAllColors(c); sentStyle = -1; } else sentChoice = null; sentAim = null; sentColor = -2; };
            s.PeerLeft += (c, why) =>
            {
                if (!NetSession.IsHost) return;
                // During a run a player who drops stays in the game (their character waits for them).
                if (!Game.SceneObjects.InBreach && GameManager.HasInstance && GameManager.Instance.Dungeon != null) return;
                // Also while a floor is loading (no Dungeon yet): removing a character mid-load breaks the load.
                if (GameManager.HasInstance && GameManager.Instance.IsLoadingLevel) return;
                Roster.Remove(c.Slot);
                PlayerSlots.Despawn(c.Slot);
                SendRoster();
            };
            s.SessionEnded += () => { Roster.Clear(); sentChoice = null; sentAim = null; Aim.Clear(); Colors.Clear(); sentColor = -2; HostCoopStyle = -1; sentStyle = -1; HostMaxPlayers = -1; sentMax = -1; sentBreach = -1; HostInBreach = -1; PlayerSlots.Clear(); };
        }

        static string PrefabOf(PlayerController p)
        {
            var n = p.gameObject.name;
            int i = n.IndexOf("(Clone)");
            return i >= 0 ? n.Substring(0, i).Trim() : n;
        }

        void Update()
        {
            if (!NetSession.Active) return;
            var local = PlayerSlots.Local;
            if (local != lastLocal)
            {
                string desc = "";
                if (GameManager.HasInstance && GameManager.Instance.AllPlayers != null)
                    foreach (var p in GameManager.Instance.AllPlayers) if (p) desc += $" [{p.name} idx={p.PlayerIDX} id={p.GetInstanceID()}]";
                Plugin.Log.LogInfo($"LOCALCHANGE scene={UnityEngine.SceneManagement.SceneManager.GetActiveScene().name} now={(local ? local.name + " id=" + local.GetInstanceID() : "none")} all:{desc}");
                lastLocal = local;
                // A character swap at a Breach statue replaces the player object; drop the stale one from the list.
                if (GameManager.HasInstance) GameManager.Instance.RefreshAllPlayers();
            }
            // Characters are chosen in the Breach only; never re-announce mid-run.
            if (local != null && Game.SceneObjects.InBreach)
            {
                string mine = PrefabOf(local);
                bool myAlt = local.IsUsingAlternateCostume;
                string myKey = mine + (myAlt ? "|alt" : "");
                // Guest: if the host's player list still doesn't show our pick (it was sent before the connection was
                // up, or lost), send it again every two seconds until it does.
                if (!NetSession.IsHost && NetSession.Instance.Node != null && NetSession.Instance.Node.Connections.Count > 0
                    && Time.realtimeSinceStartup >= nextChoiceCheck)
                {
                    nextChoiceCheck = Time.realtimeSinceStartup + 2f;
                    if (!Roster.TryGetValue(NetSession.LocalSlot, out var me) || me.Prefab != mine || me.Alt != myAlt) sentChoice = null;
                }
                if (myKey != sentChoice)
                {
                    sentChoice = myKey;
                    if (NetSession.IsHost)
                    {
                        Roster[0] = new Entry { Prefab = mine, Name = NetConfig.PlayerName.Value, Alt = myAlt };
                        SendRoster();
                    }
                    else
                    {
                        var w = new WireWriter(64);
                        w.U8(MsgChoice); w.Str(mine); w.Str(NetConfig.PlayerName.Value); w.Bool(myAlt);
                        Plugin.Log.LogInfo($"Lobby: sending my character {myKey} to {NetSession.Instance.Node.Connections.Count} peer(s)");
                        NetSession.Instance.Node.Broadcast((byte)Channel.Lobby, w.ToArray(), Delivery.ReliableOrdered);
                    }
                }
            }
            if (NetSession.IsHost && (NetConfig.CoopStyle.Value != sentStyle || NetConfig.MaxPlayers.Value != sentMax || (Game.SceneObjects.InBreach ? 1 : 0) != sentBreach)) SendStyle();
            // Guest still inside an old run of their own (the host saved and quit, then hosted again from the Breach):
            // characters are only picked in the Breach, so the host would wait forever. Go there.
            bool inOwnRun = !NetSession.IsHost && HostInBreach == 1 && !oldJoin && !Game.SceneObjects.InBreach && GameManager.HasInstance &&
                !GameManager.Instance.IsLoadingLevel && !GameManager.Instance.IsFoyer && GameManager.Instance.Dungeon != null &&
                GameManager.Instance.PrimaryPlayer != null && Game.SceneObjects.OnDungeonFloor;
            // Only when that holds steadily: while the game boots or changes level the Breach can briefly look like a run.
            if (!inOwnRun) ownRunSince = -1f;
            else if (ownRunSince < 0f) ownRunSince = Time.realtimeSinceStartup;
            if (inOwnRun && Time.realtimeSinceStartup - ownRunSince >= 5f && Time.realtimeSinceStartup >= nextBreachTrip)
            {
                nextBreachTrip = Time.realtimeSinceStartup + 10f;
                Plugin.Log.LogInfo("Lobby: host is in the Breach and we are in a run; going to the Breach to pick a character");
                Toasts.Show("The host is in the Breach. Taking you there to pick a character.", 4f);
                Game.RunSync.GuestToBreach();
            }
            if (Time.realtimeSinceStartup >= nextAimCheck && local != null) { nextAimCheck = Time.realtimeSinceStartup + 1f; CheckAim(); CheckColor(); }
            // Spawn anyone we know about but have not spawned (our own player may have appeared late).
            if (local != null)
                foreach (var kv in Roster)
                    if (kv.Key != NetSession.LocalSlot && kv.Value.Prefab.Length > 0 && PlayerSlots.ForSlot(kv.Key) == null)
                        PlayerSlots.SpawnRemote(kv.Key, kv.Value.Prefab, kv.Value.Alt);
        }

        static AimPrefs MyAim()
        {
            var o = GameManager.Options;
            bool mouse = false;
            var me = PlayerSlots.Local;
            try { if (me != null) mouse = BraveInput.GetInstanceForPlayer(me.PlayerIDX).IsKeyboardAndMouse(); } catch { }
            // Mouse aim is already exact (it is sent as a direction); no assist on the other side either.
            if (o == null || mouse) return new AimPrefs { Assist = 0f, Beam = false, Super = false };
            bool super = false;
            try { super = BraveInput.AutoAimMode == BraveInput.AutoAim.SuperAutoAim; } catch { } // no input instance yet at boot
            return new AimPrefs { Assist = o.controllerAimAssistMultiplier, Beam = o.controllerBeamAimAssist, Super = super };
        }

        void CheckColor()
        {
            int mine = NetConfig.MyColor.Value;
            if (mine == sentColor) return;
            sentColor = mine;
            Colors[NetSession.LocalSlot] = mine;
            NetSession.Instance.Node.Broadcast((byte)Channel.Lobby, ColorMsg(NetSession.LocalSlot, mine), Delivery.ReliableOrdered);
        }

        static byte[] ColorMsg(int slot, int color)
        {
            var w = new WireWriter(8);
            w.U8(MsgColor); w.U8((byte)slot); w.U8((byte)(color + 1));
            return w.ToArray();
        }

        void SendAllColors(Connection to)
        {
            foreach (var kv in Colors) to.Send((byte)Channel.Lobby, ColorMsg(kv.Key, kv.Value), Delivery.ReliableOrdered);
        }

        void CheckAim()
        {
            var a = MyAim();
            string key = a.Assist.ToString("F2") + a.Beam + a.Super;
            if (key == sentAim) return;
            sentAim = key;
            Aim[NetSession.LocalSlot] = a;
            NetSession.Instance.Node.Broadcast((byte)Channel.Lobby, AimMsg(NetSession.LocalSlot, a), Delivery.ReliableOrdered);
        }

        static byte[] AimMsg(int slot, AimPrefs a)
        {
            var w = new WireWriter(16);
            w.U8(MsgAim); w.U8((byte)slot); w.F32(a.Assist); w.Bool(a.Beam); w.Bool(a.Super);
            return w.ToArray();
        }

        void SendAllAim(Connection to)
        {
            foreach (var kv in Aim) if (kv.Key != to.Slot) to.Send((byte)Channel.Lobby, AimMsg(kv.Key, kv.Value), Delivery.ReliableOrdered);
        }

        void SendStyle()
        {
            sentStyle = NetConfig.CoopStyle.Value;
            sentMax = NetConfig.MaxPlayers.Value;
            sentBreach = Game.SceneObjects.InBreach ? 1 : 0;
            var w = new WireWriter(4);
            w.U8(MsgStyle); w.U8((byte)sentStyle); w.U8((byte)sentMax); w.U8((byte)sentBreach);
            NetSession.Instance.Node.Broadcast((byte)Channel.Lobby, w.ToArray(), Delivery.ReliableOrdered);
        }

        public void SendRoster()
        {
            var w = new WireWriter(128);
            w.U8(MsgRoster);
            w.U8((byte)Roster.Count);
            foreach (var kv in Roster) { w.U8((byte)kv.Key); w.Str(kv.Value.Prefab); w.Str(kv.Value.Name); w.Bool(kv.Value.Alt); }
            NetSession.Instance.Node.Broadcast((byte)Channel.Lobby, w.ToArray(), Delivery.ReliableOrdered);
        }

        void OnMessage(Connection c, byte[] data, Delivery d)
        {
            var r = new WireReader(data, 0, data.Length);
            byte type = r.U8();
            if (type == MsgStyle && !NetSession.IsHost)
            {
                int v = r.U8();
                if (HostCoopStyle >= 0 && v != HostCoopStyle) Toasts.Show("The host switched to " + (v == 0 ? "Classic" : "Explore") + " co-op");
                HostCoopStyle = v;
                if (r.Remaining > 0) HostMaxPlayers = r.U8();
                if (r.Remaining > 0) HostInBreach = r.U8();
                return;
            }
            if (type == MsgColor)
            {
                int slot = r.U8(), color = r.U8() - 1;
                if (NetSession.IsHost) slot = c.Slot;
                if (slot == NetSession.LocalSlot) return;
                Colors[slot] = color;
                if (NetSession.IsHost)
                    foreach (var o in NetSession.Instance.Node.Connections)
                        if (o != c) o.Send((byte)Channel.Lobby, ColorMsg(slot, color), Delivery.ReliableOrdered);
                return;
            }
            if (type == MsgAim)
            {
                int slot = r.U8();
                var a = new AimPrefs { Assist = r.F32(), Beam = r.Bool(), Super = r.Bool() };
                if (NetSession.IsHost) slot = c.Slot;
                if (slot == NetSession.LocalSlot) return;
                Aim[slot] = a;
                if (NetSession.IsHost)
                    foreach (var o in NetSession.Instance.Node.Connections)
                        if (o != c) o.Send((byte)Channel.Lobby, AimMsg(slot, a), Delivery.ReliableOrdered);
                return;
            }
            if (type == MsgChoice && NetSession.IsHost)
            {
                Roster[c.Slot] = new Entry { Prefab = r.Str(), Name = r.Str(), Alt = r.Bool() };
                Plugin.Log.LogInfo($"Lobby: slot {c.Slot} picked {Roster[c.Slot].Prefab}{(Roster[c.Slot].Alt ? " (alternate costume)" : "")}");
                SendRoster();
            }
            else if (type == MsgRoster && !NetSession.IsHost)
            {
                int n = r.U8();
                var seen = new HashSet<int>();
                for (int i = 0; i < n; i++)
                {
                    int slot = r.U8();
                    Roster[slot] = new Entry { Prefab = r.Str(), Name = r.Str(), Alt = r.Bool() };
                    seen.Add(slot);
                }
                foreach (var slot in new List<int>(Roster.Keys))
                    if (!seen.Contains(slot)) { Roster.Remove(slot); PlayerSlots.Despawn(slot); }
            }
            // Re-spawn with a new character if someone changed theirs.
            // A copy: spawning can change the roster (a player who crashed and rejoined mid-loop).
            foreach (var kv in new List<KeyValuePair<int, Entry>>(Roster))
                if (kv.Key != NetSession.LocalSlot && kv.Value.Prefab.Length > 0)
                    PlayerSlots.SpawnRemote(kv.Key, kv.Value.Prefab, kv.Value.Alt);
        }
    }
}
