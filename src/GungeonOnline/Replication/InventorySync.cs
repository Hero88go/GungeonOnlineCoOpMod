using System.Collections.Generic;
using System.Text;
using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Session;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Full inventory reconciliation. Whenever a player's guns, passive items or active items change on the host,
    /// the host sends the complete list; every machine adds what its copy is missing and removes what it has extra.
    /// Pickup events give instant feedback; this makes sure nothing (starting items, a missed event) can drift.
    /// Ammo of your own guns is only corrected on large gaps so it never jumps back while you shoot.
    /// </summary>
    internal sealed class InventorySync : MonoBehaviour
    {
        public static InventorySync Instance { get; private set; }
        const byte EvInventory = 45;

        readonly Dictionary<int, string> lastSent = new Dictionary<int, string>();
        float nextCheck;
        public int Sent, Applied, Added, Removed;
        readonly WireWriter w = new WireWriter(512);

        void Awake() { Instance = this; }
        void Start() { NetSession.Instance.SessionEnded += () => lastSent.Clear(); }

        public void ForceResend() => lastSent.Clear();

        static string Key(PlayerController p)
        {
            var sb = new StringBuilder();
            foreach (var g in p.inventory.AllGuns) if (g) sb.Append('g').Append(g.PickupObjectId).Append(':').Append(g.CurrentAmmo).Append(',');
            sb.Append(p.CurrentGun ? p.CurrentGun.PickupObjectId : -1).Append('|');
            foreach (var i in p.passiveItems) if (i) sb.Append('p').Append(i.PickupObjectId).Append(',');
            foreach (var i in p.activeItems) if (i) sb.Append('a').Append(i.PickupObjectId).Append(',');
            return sb.ToString();
        }

        void LateUpdate()
        {
            long t0 = Dev.NetProfiler.Begin();
            try { LateUpdateImpl(); }
            finally { Dev.NetProfiler.End("inventory", t0); }
        }

        void LateUpdateImpl()
        {
            if (!NetSession.Active || !NetSession.IsHost || GameManager.Instance.IsLoadingLevel) return;
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 0.25f;
            var players = new List<KeyValuePair<int, PlayerController>>();
            if (PlayerSlots.Local) players.Add(new KeyValuePair<int, PlayerController>(NetSession.LocalSlot, PlayerSlots.Local));
            foreach (var kv in PlayerSlots.Remotes) if (kv.Value) players.Add(kv);
            foreach (var kv in players)
            {
                var p = kv.Value;
                if (p.inventory == null) continue;
                string key = Key(p);
                if (lastSent.TryGetValue(kv.Key, out var prev) && prev == key) continue;
                lastSent[kv.Key] = key;
                w.Reset();
                w.U8(EvInventory); w.U8((byte)kv.Key);
                w.U8((byte)p.inventory.AllGuns.Count);
                foreach (var g in p.inventory.AllGuns) { w.I32(g ? g.PickupObjectId : -1); w.I32(g ? g.CurrentAmmo : 0); }
                w.I32(p.CurrentGun ? p.CurrentGun.PickupObjectId : -1);
                w.U8((byte)p.passiveItems.Count);
                foreach (var i in p.passiveItems) w.I32(i ? i.PickupObjectId : -1);
                w.U8((byte)p.activeItems.Count);
                foreach (var i in p.activeItems) w.I32(i ? i.PickupObjectId : -1);
                var msg = w.ToArray();
                var ws = WorldSync.Instance;
                foreach (var c in NetSession.Instance.Node.Connections)
                    if (ws != null && ws.IsReady(c)) c.Send((byte)Channel.Event, msg, Delivery.ReliableOrdered);
                Sent++;
            }
        }

        public void Apply(ref WireReader r)
        {
            if (NetSession.IsHost) return;
            int slot = r.U8();
            var guns = new List<KeyValuePair<int, int>>();
            int ng = r.U8();
            for (int i = 0; i < ng; i++) guns.Add(new KeyValuePair<int, int>(r.I32(), r.I32()));
            int current = r.I32();
            var passives = new List<int>();
            int np = r.U8();
            for (int i = 0; i < np; i++) passives.Add(r.I32());
            var actives = new List<int>();
            int na = r.U8();
            for (int i = 0; i < na; i++) actives.Add(r.I32());
            var p = PlayerSlots.ForSlot(slot);
            if (!p || p.inventory == null) return;
            bool own = slot == NetSession.LocalSlot;
            PickupSync.WithScope(() => Reconcile(p, own, guns, current, passives, actives));
            Applied++;
        }

        /// <summary>Host: put a saved inventory back on a player (run backups).</summary>
        public void Restore(PlayerController p, List<KeyValuePair<int, int>> guns, int current, List<int> passives, List<int> actives)
        {
            PickupSync.WithScope(() => Reconcile(p, false, guns, current, passives, actives));
            lastSent.Clear();
        }

        void Reconcile(PlayerController p, bool own, List<KeyValuePair<int, int>> guns, int current, List<int> passives, List<int> actives)
        {
            // Guns
            var want = new List<int>();
            foreach (var g in guns) if (g.Key >= 0) want.Add(g.Key);
            foreach (var g in new List<Gun>(p.inventory.AllGuns))
            {
                if (!g) continue;
                if (want.Remove(g.PickupObjectId)) continue;
                p.inventory.RemoveGunFromInventory(g);
                Removed++;
            }
            foreach (var id in want) Give(p, id);
            // Ammo
            foreach (var kv in guns)
                foreach (var g in p.inventory.AllGuns)
                    if (g && g.PickupObjectId == kv.Key)
                    {
                        int diff = Mathf.Abs(g.CurrentAmmo - kv.Value);
                        if (!own || diff > Mathf.Max(5, g.AdjustedMaxAmmo / 4)) g.CurrentAmmo = kv.Value;
                        break;
                    }
            // Current gun for the other players' copies (your own follows your input; a copy whose owner already
            // said which gun it holds follows the owner, which is newer than the host's view)
            if (!own && current >= 0 && p.CurrentGun && p.CurrentGun.PickupObjectId != current && VitalsSync.WantedGun(PlayerSlots.SlotOf(p)) == int.MinValue)
            {
                var all = p.inventory.AllGuns;
                for (int i = 0; i < all.Count; i++)
                    if (all[i] && all[i].PickupObjectId == current) { p.ChangeToGunSlot(i, true); break; }
            }
            // Passives
            var wantP = new List<int>(passives);
            foreach (var i in new List<PassiveItem>(p.passiveItems))
            {
                if (!i) continue;
                if (wantP.Remove(i.PickupObjectId)) continue;
                p.RemovePassiveItem(i.PickupObjectId);
                Removed++;
            }
            foreach (var id in wantP) Give(p, id);
            // Actives
            var wantA = new List<int>(actives);
            foreach (var i in new List<PlayerItem>(p.activeItems))
            {
                if (!i) continue;
                if (wantA.Remove(i.PickupObjectId)) continue;
                p.RemoveActiveItem(i.PickupObjectId);
                Removed++;
            }
            foreach (var id in wantA) Give(p, id);
        }

        void Give(PlayerController p, int id)
        {
            var prefab = PickupObjectDatabase.GetById(id);
            if (prefab == null) return;
            int before = p.inventory.AllGuns.Count + p.passiveItems.Count + p.activeItems.Count;
            LootEngine.GivePrefabToPlayer(prefab.gameObject, p);
            Added++;
            if (p.inventory.AllGuns.Count + p.passiveItems.Count + p.activeItems.Count == before)
                Plugin.Log.LogWarning($"InventorySync: giving item {id} ({prefab.name}) to {p.name} had no effect");
        }
    }
}
