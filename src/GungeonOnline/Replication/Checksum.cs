using System.Collections.Generic;
using System.IO;
using System.Text;
using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Session;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Desync detection and repair. Once a second the host sends a summary of everything that must match:
    /// which enemies are alive, which rooms are sealed, every player's health, wallet and inventory, chest states,
    /// and rough player positions. The client compares with its own view; any difference is logged to
    /// desync.log, counted in the overlay, and repaired by a full resync from the host.
    /// </summary>
    internal sealed class Checksum : MonoBehaviour
    {
        public static Checksum Instance { get; private set; }
        const byte EvSummary = 70, EvResyncReq = 71, EvAliveSet = 72;

        float nextSummary;
        float lastResyncReq;
        public int Summaries, Checks, Mismatches, Resyncs, ConsecutiveClean;
        public string LastMismatch = "";
        readonly WireWriter w = new WireWriter(1024);

        void Awake() { Instance = this; }
        void Start() { NetSession.Instance.SessionEnded += () => { Mismatches = 0; Checks = 0; ConsecutiveClean = 0; LastMismatch = ""; }; }

        // ---------- What both sides summarize ----------

        static uint Fnv(string s)
        {
            uint h = 2166136261;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return h;
        }

        static List<uint> AliveEnemyIds()
        {
            var ids = new List<uint>();
            // "Alive" means no death event yet: some enemies sit at zero health through a death animation first.
            var ws = WorldSync.Instance;
            foreach (var kv in NetIds.All)
                if (kv.Value is AIActor a && a && ws != null && !ws.DeathAnnounced(kv.Key) && (NetSession.IsHost || ws.IsReplica(kv.Key))) ids.Add(kv.Key);
            ids.Sort();
            return ids;
        }

        static string SealedRooms()
        {
            var sb = new StringBuilder();
            var rooms = GameManager.Instance.Dungeon.data.rooms;
            for (int i = 0; i < rooms.Count; i++) if (rooms[i].IsSealed) sb.Append(i).Append(',');
            return sb.ToString();
        }

        static string PlayerKey(PlayerController p)
        {
            var sb = new StringBuilder();
            sb.Append(p.healthHaver.GetCurrentHealth().ToString("F1")).Append('/').Append(p.healthHaver.Armor.ToString("F0")).Append('/');
            sb.Append(p.carriedConsumables != null ? p.carriedConsumables.Currency : 0).Append('/');
            sb.Append(p.carriedConsumables != null ? p.carriedConsumables.KeyBullets : 0).Append('/').Append(p.Blanks).Append('|');
            var ids = new List<int>();
            foreach (var g in p.inventory.AllGuns) if (g) ids.Add(g.PickupObjectId);
            ids.Sort();
            foreach (var id in ids) sb.Append('g').Append(id);
            ids.Clear();
            foreach (var i in p.passiveItems) if (i) ids.Add(i.PickupObjectId);
            ids.Sort();
            foreach (var id in ids) sb.Append('p').Append(id);
            ids.Clear();
            foreach (var i in p.activeItems) if (i) ids.Add(i.PickupObjectId);
            ids.Sort();
            foreach (var id in ids) sb.Append('a').Append(id);
            return sb.ToString();
        }

        static int OpenChests()
        {
            int n = 0;
            foreach (var c in StaticReferenceManager.AllChests) if (c && c.IsOpen) n++;
            return n;
        }

        // ---------- Host ----------

        void LateUpdate()
        {
            long t0 = Dev.NetProfiler.Begin();
            try { LateUpdateImpl(); }
            finally { Dev.NetProfiler.End("checksum", t0); }
        }

        void LateUpdateImpl()
        {
            if (!NetSession.Active || !NetSession.IsHost || !SceneObjects.OnDungeonFloor) return;
            if (Time.realtimeSinceStartup < nextSummary) return;
            nextSummary = Time.realtimeSinceStartup + 1f;
            var ws = WorldSync.Instance;
            if (ws == null || ws.ReadyPeers == 0) return;
            w.Reset();
            w.U8(EvSummary);
            var alive = AliveEnemyIds();
            w.U16((ushort)alive.Count);
            w.U32(Fnv(string.Join(",", alive.ConvertAll(x => x.ToString()).ToArray())));
            w.Str(SealedRooms());
            w.U8((byte)OpenChests());
            var players = new List<KeyValuePair<int, PlayerController>>();
            if (PlayerSlots.Local) players.Add(new KeyValuePair<int, PlayerController>(NetSession.LocalSlot, PlayerSlots.Local));
            foreach (var kv in PlayerSlots.Remotes) if (kv.Value) players.Add(kv);
            w.U8((byte)players.Count);
            foreach (var kv in players)
            {
                w.U8((byte)kv.Key);
                w.Str(PlayerKey(kv.Value));
                Vector2 pos = kv.Value.transform.position;
                w.F32(pos.x); w.F32(pos.y);
            }
            var msg = w.ToArray();
            foreach (var c in NetSession.Instance.Node.Connections)
                if (ws.IsReady(c)) c.Send((byte)Channel.Event, msg, Delivery.ReliableOrdered);
            Summaries++;
        }

        // ---------- Both: events 70-79 ----------

        public void Apply(Connection c, byte type, ref WireReader r)
        {
            if (type == EvResyncReq && NetSession.IsHost)
            {
                string why = r.Remaining > 0 ? r.Str() : "";
                if (why.Length > 0) Log("slot " + c.Slot + " saw: " + why);
                HostResync(c);
                return;
            }
            if (type == EvAliveSet && !NetSession.IsHost) { WorldSync.Instance?.KeepOnly(ReadIds(ref r)); return; }
            if (type != EvSummary || NetSession.IsHost || !SceneObjects.OnDungeonFloor) return;

            // After a gap (a drop, a long load) summaries that were held up arrive together and describe the past:
            // skip comparisons for two seconds, and only ever compare the newest summary of a frame.
            float now = Time.realtimeSinceStartup;
            if (now - lastSummaryAt > 2.5f) graceUntil = now + 2f;
            lastSummaryAt = now;
            if (now < graceUntil || Time.frameCount == lastCheckFrame) return;
            lastCheckFrame = Time.frameCount;
            Checks++;
            var problems = new List<string>();
            int hostAlive = r.U16();
            uint hostAliveHash = r.U32();
            var mine = AliveEnemyIds();
            if (mine.Count != hostAlive || Fnv(string.Join(",", mine.ConvertAll(x => x.ToString()).ToArray())) != hostAliveHash)
                problems.Add($"enemies host={hostAlive} mine={mine.Count}");
            string sealedHost = r.Str();
            string sealedMine = SealedRooms();
            if (sealedHost != sealedMine) problems.Add($"sealed host=[{sealedHost}] mine=[{sealedMine}]");
            int chestsHost = r.U8();
            int chestsMine = OpenChests();
            if (chestsHost != chestsMine) problems.Add($"open chests host={chestsHost} mine={chestsMine}");
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                int slot = r.U8();
                string key = r.Str();
                var hostPos = new Vector2(r.F32(), r.F32());
                var p = PlayerSlots.ForSlot(slot);
                if (!p) { problems.Add($"player {slot} missing"); continue; }
                string myKey = PlayerKey(p);
                bool ownHitsPending = slot == NetSession.LocalSlot && VitalsSync.Instance != null && VitalsSync.Instance.HurtsInFlight;
                if (myKey != key && !ownHitsPending) problems.Add($"player {slot} state host=[{key}] mine=[{myKey}]");
                // Positions are a frame or two apart by nature; only gross divergence counts.
                float d = (hostPos - (Vector2)p.transform.position).magnitude;
                // Ghosts fly fast and pass through walls; their position is not part of the game state that matters.
                // Tolerance grows with ping (a fast dodge covers more ground before the summary arrives).
                float rtt = 0f;
                foreach (var cn in NetSession.Instance.Node.Connections) rtt = Mathf.Max(rtt, cn.RttMs);
                float tol = 4f + 10f * rtt / 1000f;
                if (d > tol && !p.IsGhost && !p.currentMineCart && !PlayerSync.FellRecently(slot, p)) problems.Add($"player {slot} position off by {d:F1}");
            }
            if (problems.Count == 0) { ConsecutiveClean++; pendingProblems = 0; return; }
            // A single check can catch a change that is still in flight; act on two in a row.
            ConsecutiveClean = 0;
            pendingProblems++;
            if (pendingProblems < 2) return;
            pendingProblems = 0;
            Mismatches++;
            LastMismatch = string.Join("; ", problems.ToArray());
            Log(LastMismatch);
            RequestResync();
        }

        int pendingProblems;
        float lastSummaryAt = -10f, graceUntil;
        int lastCheckFrame = -1;

        static List<uint> ReadIds(ref WireReader r)
        {
            int n = r.U16();
            var ids = new List<uint>(n);
            for (int i = 0; i < n; i++) ids.Add(r.U32());
            return ids;
        }

        void Log(string what)
        {
            Plugin.Log.LogWarning("DESYNC " + what);
            try
            {
                var dir = Path.Combine(BepInEx.Paths.BepInExRootPath, "GungeonOnline");
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "desync.log"), System.DateTime.Now.ToString("s") + " " + what + "\n");
                var run = System.Environment.GetEnvironmentVariable("GO_RUN_DIR");
                if (run != null) File.AppendAllText(Path.Combine(run, "desync.log"), Time.realtimeSinceStartup.ToString("F1") + " " + what + "\n");
            }
            catch { }
        }

        void RequestResync()
        {
            if (Time.realtimeSinceStartup - lastResyncReq < 3f) return;
            lastResyncReq = Time.realtimeSinceStartup;
            w.Reset(); w.U8(EvResyncReq);
            // Why: the host's log (and its reports) then say what did not match, not only the guest's.
            w.Str(LastMismatch.Length > 300 ? LastMismatch.Substring(0, 300) : LastMismatch);
            NetSession.Instance.Node.Broadcast((byte)Channel.Event, w.ToArray(), Delivery.ReliableOrdered);
            Resyncs++;
        }

        void HostResync(Connection c)
        {
            Plugin.Log.LogInfo($"Checksum: full resync for slot {c.Slot}");
            WorldSync.Instance?.ResendWorld(c);
            var alive = AliveEnemyIds();
            w.Reset(); w.U8(EvAliveSet); w.U16((ushort)alive.Count);
            foreach (var id in alive) w.U32(id);
            c.Send((byte)Channel.Event, w.ToArray(), Delivery.ReliableOrdered);
            PickupSync.Instance?.SendAllTo(c);
            ShopSync.Instance?.SendAllTo(c);
            InventorySync.Instance?.ForceResend();
            VitalsSync.Instance?.ForceResend();
            Resyncs++;
        }
    }
}
