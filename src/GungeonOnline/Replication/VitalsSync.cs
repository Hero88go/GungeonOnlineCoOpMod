using System.Collections.Generic;
using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Player health, armor, blanks and the shared wallet.
    /// Hits on a player are decided by the machine that owns that player (what you see dodge is a dodge):
    /// the client reports each hit on its own character; the host applies it to its copy. Everything else that
    /// changes vitals (pickups, shops, healing) happens on the host, which sends every player's vitals when they
    /// change. A client accepts the host's numbers for its own player only once the host has caught up with all
    /// of its hit reports, so health never flickers back up.
    /// </summary>
    [DefaultExecutionOrder(31700)]
    internal sealed class VitalsSync : MonoBehaviour
    {
        public static VitalsSync Instance { get; private set; }
        const byte EvHurt = 40, EvVitals = 41, EvPlayerDied = 42, EvPlayerRevived = 43, EvPitfall = 44, EvFire = 46, EvGun = 48; // 45 is InventorySync, 47 is MapPing

        static bool scope;
        static bool IsClient => NetSession.Active && !NetSession.IsHost;
        static bool IsHostSession => NetSession.Active && NetSession.IsHost;

        uint hurtSeq;                                        // client: reports sent
        readonly Dictionary<int, uint> hurtApplied = new Dictionary<int, uint>(); // host: per slot, last report applied
        readonly Dictionary<int, string> lastVitals = new Dictionary<int, string>();
        readonly HashSet<int> dyingSent = new HashSet<int>();
        /// <summary>Guest: players whose death the host announced and we already ran (health alone can read 0 first).</summary>
        readonly HashSet<int> deathRun = new HashSet<int>();
        public int HurtsSent, HurtsApplied, VitalsSent, VitalsApplied;
        readonly WireWriter w = new WireWriter(128);

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(PlayerController), "Die"), postfix: new HarmonyMethod(typeof(VitalsSync), nameof(DiedPostfix)));
            h.Patch(AccessTools.PropertyGetter(typeof(PlayerController), nameof(PlayerController.IsInMinecart)), prefix: new HarmonyMethod(typeof(VitalsSync), nameof(InMinecartPrefix)));
            h.Patch(AccessTools.Method(typeof(PlayerController), nameof(PlayerController.ResurrectFromChest)), postfix: new HarmonyMethod(typeof(VitalsSync), nameof(RevivedPostfix)));
            h.Patch(AccessTools.Method(typeof(PlayerController), nameof(PlayerController.ResurrectFromBossKill)), postfix: new HarmonyMethod(typeof(VitalsSync), nameof(BossRevivedPostfix)));
            h.Patch(AccessTools.Method(typeof(PlayerController), "Fall"),
                prefix: new HarmonyMethod(typeof(VitalsSync), nameof(FallPrefix)),
                postfix: new HarmonyMethod(typeof(VitalsSync), nameof(FallPostfix)));
            // On fire: the owner of a player decides (it walked into the flames); every other screen shows it burning.
            h.Patch(AccessTools.PropertySetter(typeof(PlayerController), nameof(PlayerController.IsOnFire)),
                prefix: new HarmonyMethod(typeof(VitalsSync), nameof(FirePrefix)),
                postfix: new HarmonyMethod(typeof(VitalsSync), nameof(FirePostfix)));
            h.Patch(AccessTools.Method(typeof(HealthHaver), nameof(HealthHaver.ApplyDamage)),
                prefix: new HarmonyMethod(typeof(VitalsSync), nameof(DamagePrefix)),
                postfix: new HarmonyMethod(typeof(VitalsSync), nameof(DamagePostfix)));
        }

        void Awake() { Instance = this; }

        void Start()
        {
            NetSession.Instance.SessionEnded += () => { hurtSeq = 0; hurtApplied.Clear(); lastVitals.Clear(); lastFireSent = false; gunWanted.Clear(); lastGunSent = int.MinValue; };
            // Hit report numbering restarts with every connection.
            NetSession.Instance.PeerJoined += c =>
            {
                if (NetSession.IsHost) { hurtApplied.Remove(c.Slot); lastVitals.Clear(); }
                else hurtSeq = 0;
            };
        }

        public void ForceResend() { lastVitals.Clear(); lastSig.Clear(); }
        readonly Dictionary<int, float> lastSig = new Dictionary<int, float>();
        readonly List<KeyValuePair<int, PlayerController>> vitalsList = new List<KeyValuePair<int, PlayerController>>();

        /// <summary>A copy of another machine's player never takes damage from local hit checks.</summary>
        static bool DamagePrefix(HealthHaver __instance)
        {
            if (!NetSession.Active || scope || __instance == null) return true;
            var p = __instance.GetComponent<PlayerController>();
            return p == null || !PlayerSlots.IsRemote(p);
        }

        static bool fireScope;
        static bool lastFireSent;

        /// <summary>Another machine's player burns only when its owner says so (local flames never light the copy,
        /// and the copy's own "not burning" check never puts it out early).</summary>
        static bool FirePrefix(PlayerController __instance)
        {
            if (!NetSession.Active || fireScope || __instance == null) return true;
            return !PlayerSlots.IsRemote(__instance);
        }

        /// <summary>Your own player caught fire or went out: tell everyone (the host forwards it to the others).</summary>
        static void FirePostfix(PlayerController __instance)
        {
            if (!NetSession.Active || fireScope || Instance == null || __instance == null || __instance != PlayerSlots.Local) return;
            bool on = __instance.IsOnFire;
            if (on == lastFireSent) return;
            lastFireSent = on;
            Instance.SendFire(NetSession.LocalSlot, on, null);
        }

        void SendFire(int slot, bool on, Connection except)
        {
            w.Reset(); w.U8(EvFire); w.U8((byte)slot); w.Bool(on);
            var msg = w.ToArray();
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (c != except && (ws == null || ws.IsReady(c) || !NetSession.IsHost)) c.Send((byte)Channel.Event, msg, Delivery.ReliableOrdered);
        }

        void ApplyFire(Connection from, ref WireReader r)
        {
            int slot = r.U8();
            bool on = r.Bool();
            if (NetSession.IsHost) SendFire(slot, on, from); // pass it on to everyone else
            var p = PlayerSlots.ForSlot(slot);
            if (!p || !PlayerSlots.IsRemote(p)) return;
            fireScope = true;
            try { p.IsOnFire = on; if (!on) p.CurrentFireMeterValue = 0f; }
            finally { fireScope = false; }
        }

        /// <summary>Client: report every hit on its own player to the host.</summary>
        static void DamagePostfix(HealthHaver __instance, float damage, Vector2 direction)
        {
            if (!IsClient || scope || Instance == null || __instance == null) return;
            var p = __instance.GetComponent<PlayerController>();
            if (p == null || p != PlayerSlots.Local) return;
            Instance.hurtSeq++;
            var w = Instance.w;
            w.Reset(); w.U8(EvHurt); w.U32(Instance.hurtSeq);
            w.F32(damage); w.F32(direction.x); w.F32(direction.y);
            w.F32(__instance.GetCurrentHealth()); w.F32(__instance.Armor);
            NetSession.Instance.Node.Broadcast((byte)Channel.Event, w.ToArray(), Delivery.ReliableOrdered);
            NetSession.Instance.FlushNow();
            Instance.HurtsSent++;
        }

        /// <summary>Host: a player died here (ghost or game over follows); every machine runs the same death on its copy.</summary>
        // ---------- Pit falls: the owner of a player decides, like hits ----------

        static bool itemErrorLogged;

        static bool fallScope;

        /// <summary>Another machine's player only falls when its owner says it fell, so nobody sees a fall that did not happen.</summary>
        static bool FallPrefix(PlayerController __instance, out bool __state)
        {
            __state = __instance != null && __instance.IsFalling;
            if (!NetSession.Active || fallScope || __instance == null) return true;
            return !PlayerSlots.IsRemote(__instance);
        }

        /// <summary>Your own player just started falling: tell everyone (the host forwards it to the others).</summary>
        static void FallPostfix(PlayerController __instance, bool __state)
        {
            if (!NetSession.Active || fallScope || Instance == null || __instance == null || __state || !__instance.IsFalling) return;
            if (__instance != PlayerSlots.Local) return;
            Instance.SendPitfall(NetSession.LocalSlot, __instance.transform.position, null);
        }

        void SendPitfall(int slot, Vector2 at, Connection except)
        {
            w.Reset(); w.U8(EvPitfall); w.U8((byte)slot); w.F32(at.x); w.F32(at.y);
            var msg = w.ToArray();
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                // The host only sends to players whose floor is ready; a guest always tells the host.
                if (c != except && (!NetSession.IsHost || (ws != null && ws.IsReady(c)))) c.Send((byte)Channel.Event, msg, Delivery.ReliableOrdered);
        }

        void ApplyPitfall(Connection from, ref WireReader r)
        {
            int slot = r.U8();
            var at = new Vector2(r.F32(), r.F32());
            if (NetSession.IsHost) slot = from.Slot; // a client only speaks for its own player
            var p = PlayerSlots.ForSlot(slot);
            if (!p || !PlayerSlots.IsRemote(p) || p.IsFalling || p.IsGhost) return;
            fallScope = true;
            try { p.WarpToPoint(at); p.ForceFall(); }
            finally { fallScope = false; }
            PitfallsApplied++;
            if (NetSession.IsHost) SendPitfall(slot, at, from);
        }

        public int PitfallsApplied;
        uint LastAck;
        /// <summary>Guest: hits on our own player the host has not confirmed yet.</summary>
        public bool HurtsInFlight => hurtSeq > LastAck;

        /// <summary>
        /// A co-op pit respawn waits until the other player is out of their minecart (on a shared screen that is a
        /// moment). Online the friend may ride a long track somewhere else, and you would hang mid-fall the whole
        /// time. While your own player is falling, the friend's copy reads as not in a cart: you come back at the edge.
        /// </summary>
        static bool InMinecartPrefix(PlayerController __instance, ref bool __result)
        {
            if (!NetSession.Active || !PlayerSlots.IsRemote(__instance)) return true;
            var me = PlayerSlots.Local;
            if (me == null || !me.IsFalling) return true;
            __result = false;
            return false;
        }

        /// <summary>Every Die that ran on this machine (a postfix runs even when a prefix replaced the game's own).</summary>
        static int diesRun;

        static void DiedPostfix(PlayerController __instance)
        {
            diesRun++;
            // Also while applying a guest's own fatal hit (scope): with three or four players the other guests
            // learn that death only from the host. The guest who died is never told about itself.
            if (!IsHostSession || Instance == null) return;
            int slot = PlayerSlots.SlotOf(__instance);
            if (slot < 0) return;
            var w = Instance.w;
            w.Reset(); w.U8(EvPlayerDied); w.U8((byte)slot);
            Instance.SendReady(w.ToArray(), slot);
        }

        static void RevivedPostfix(PlayerController __instance, Vector2 chestBottomCenter)
        {
            SendRevive(__instance, chestBottomCenter, false);
        }

        static void BossRevivedPostfix(PlayerController __instance)
        {
            SendRevive(__instance, __instance.transform.position, true);
        }

        static void SendRevive(PlayerController __instance, Vector2 at, bool boss)
        {
            if (!IsHostSession || scope || Instance == null) return;
            int slot = PlayerSlots.SlotOf(__instance);
            if (slot < 0) return;
            var w = Instance.w;
            w.Reset(); w.U8(EvPlayerRevived); w.U8((byte)slot); w.F32(at.x); w.F32(at.y); w.Bool(boss);
            Instance.SendReady(w.ToArray());
        }

        /// <summary>
        /// Host: a guest just (re)joined the floor. Tell it who is down, its own player included: a guest that crashed
        /// as the last player fell comes back from its backup alive, in a run that is already over everywhere else.
        /// Its own death goes last, so when nobody else is left it ends the run there too.
        /// </summary>
        public void SendDeathsTo(Connection c)
        {
            byte[] own = null;
            foreach (var p in Game.NPlayers.Players.All)
            {
                if (!p || p.healthHaver == null || !(p.IsGhost || p.healthHaver.IsDead)) continue;
                int slot = PlayerSlots.SlotOf(p);
                if (slot < 0) continue;
                w.Reset(); w.U8(EvPlayerDied); w.U8((byte)slot);
                if (slot == c.Slot) own = w.ToArray();
                else c.Send((byte)Channel.Event, w.ToArray(), Delivery.ReliableOrdered);
            }
            if (own != null) c.Send((byte)Channel.Event, own, Delivery.ReliableOrdered);
        }

        void SendReady(byte[] msg, int exceptSlot = -1)
        {
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (c.Slot != exceptSlot && ws != null && ws.IsReady(c)) c.Send((byte)Channel.Event, msg, Delivery.ReliableOrdered);
        }

        /// <summary>Events 40-49, called by WorldSync's event router on both sides.</summary>
        public void Apply(Connection c, byte type, ref WireReader r)
        {
            if (type == EvFire) { ApplyFire(c, ref r); return; }
            if (type == EvGun) { ApplyGun(c, ref r); return; }
            if (type == EvPitfall) { ApplyPitfall(c, ref r); return; }
            if (type == EvHurt && NetSession.IsHost)
            {
                var self = PlayerSlots.ForSlot(c.Slot);
                if (self && !self.healthHaver.IsDead) dyingSent.Remove(c.Slot);
                uint seq = r.U32();
                float dmg = r.F32();
                var dir = new Vector2(r.F32(), r.F32());
                float hpAfter = r.F32(), armorAfter = r.F32();
                var p = PlayerSlots.ForSlot(c.Slot);
                hurtApplied[c.Slot] = seq;
                if (!p || p.healthHaver == null) return;
                int diesBefore = diesRun;
                scope = true;
                try
                {
                    // Apply as a real hit (damage reactions, item triggers), then pin to what the owner saw.
                    // An item's own reaction to the hit can fail on a copy (it was written for a local player); the
                    // hit still counts, and health and death below must still follow what the owner saw.
                    try { p.healthHaver.ApplyDamage(dmg, dir, "remote", CoreDamageTypes.None, DamageCategory.Normal, true); }
                    catch (System.Exception e) { if (!itemErrorLogged) { itemErrorLogged = true; Plugin.Log.LogWarning("Vitals: an item's hit reaction failed on a copy: " + e.Message); } }
                    if (Game.RunSync.CloneInProgress) return;
                    p.healthHaver.ForceSetCurrentHealth(hpAfter);
                    p.healthHaver.Armor = armorAfter;
                    // The owner saw this hit kill; if this copy's own checks (i-frames, invulnerability) skipped the
                    // death, run it now so ghost mode, the co-op item drop and game over follow as on the owner's side.
                    // Not when the hit itself just ran the death: the ghost only appears a moment later.
                    if (hpAfter <= 0f && armorAfter <= 0f && !p.IsGhost && diesRun == diesBefore && !dyingSent.Contains(c.Slot))
                    {
                        dyingSent.Add(c.Slot);
                        p.healthHaver.Die(dir);
                    }
                }
                finally { scope = false; }
                HurtsApplied++;
                lastVitals.Remove(c.Slot); // force a vitals echo with the new hurt sequence
            }
            else if (type == EvPlayerDied && !NetSession.IsHost)
            {
                int slot = r.U8();
                // Our own death normally ran here first and is never echoed; the host names us only after we rejoined.
                var p = slot == NetSession.LocalSlot ? PlayerSlots.Local : PlayerSlots.ForSlot(slot);
                if (slot == NetSession.LocalSlot) Plugin.Log.LogInfo("Vitals: the host says we are down (rejoined after our death)");
                // Not IsDead: a health update can set this copy to 0 before this event lands, and the death (ghost,
                // or game over when nobody is left) must still run here exactly once.
                if (!p || p.healthHaver == null || p.IsGhost || deathRun.Contains(slot)) return;
                deathRun.Add(slot);
                scope = true;
                try { p.healthHaver.ForceSetCurrentHealth(0f); p.healthHaver.Armor = 0f; p.healthHaver.Die(Vector2.zero); }
                finally { scope = false; }
            }
            else if (type == EvPlayerRevived && !NetSession.IsHost)
            {
                var p = PlayerSlots.ForSlot(r.U8());
                var at = new Vector2(r.F32(), r.F32());
                bool boss = r.Bool();
                if (!p || !p.IsGhost) return;
                deathRun.Remove(PlayerSlots.SlotOf(p));
                scope = true;
                try
                {
                    p.gameObject.SetActive(true);
                    p.specRigidbody.enabled = true;
                    if (boss) { p.transform.position = at; p.ResurrectFromBossKill(); }
                    else p.ResurrectFromChest(at);
                }
                finally { scope = false; }
            }
            else if (type == EvVitals && !NetSession.IsHost)
            {
                int n = r.U8();
                for (int i = 0; i < n; i++)
                {
                    int slot = r.U8();
                    uint ack = r.U32();
                    float hp = r.F32(), max = r.F32(), armor = r.F32();
                    int blanks = r.I16();
                    int money = r.I32(), keys = r.I16();
                    var p = PlayerSlots.ForSlot(slot);
                    if (!p || p.healthHaver == null) continue;
                    bool own = slot == NetSession.LocalSlot;
                    if (own) LastAck = ack;
                    if (own && ack < hurtSeq) continue; // the host has not seen all our hits yet
                    scope = true;
                    try
                    {
                        if (Mathf.Abs(p.healthHaver.GetMaxHealth() - max) > 0.01f) p.healthHaver.SetHealthMaximum(max);
                        if (Mathf.Abs(p.healthHaver.GetCurrentHealth() - hp) > 0.01f) p.healthHaver.ForceSetCurrentHealth(hp);
                        if (hp > 0f && !p.IsGhost) deathRun.Remove(slot);
                        if (Mathf.Abs(p.healthHaver.Armor - armor) > 0.01f) p.healthHaver.Armor = armor;
                        if (p.Blanks != blanks) p.Blanks = blanks;
                        if (p.carriedConsumables != null)
                        {
                            if (p.carriedConsumables.Currency != money) p.carriedConsumables.Currency = money;
                            if (p.carriedConsumables.KeyBullets != keys) p.carriedConsumables.KeyBullets = keys;
                        }
                    }
                    finally { scope = false; }
                    VitalsApplied++;
                }
            }
        }

        /// <summary>Host: send vitals for every player whenever any of them changed.</summary>
        void LateUpdate()
        {
            long t0 = Dev.NetProfiler.Begin();
            try { LateUpdateImpl(); }
            finally { Dev.NetProfiler.End("vitals", t0); }
        }

        // ---------------- Held gun ----------------
        // The owner of a player decides which gun it holds. Other machines never switch a copy's gun from replayed
        // button presses (a mouse wheel or number key is not even a button there, and one lost press left a copy
        // firing an empty chest gun that did no damage); they follow this message.

        int lastGunSent = int.MinValue;
        float nextGunResend;
        readonly Dictionary<int, int> gunWanted = new Dictionary<int, int>();
        public int GunsSent, GunsApplied;

        void OwnGunTick()
        {
            var me = PlayerSlots.Local;
            if (!me || GameManager.Instance.IsLoadingLevel) return;
            int id = me.CurrentGun ? me.CurrentGun.PickupObjectId : -1;
            float now = Time.realtimeSinceStartup;
            if (id == lastGunSent && now < nextGunResend) return;
            lastGunSent = id;
            nextGunResend = now + 2f; // repeated now and then: a copy made later (reconnect, new floor) catches up
            SendGun(NetSession.LocalSlot, id, null);
            GunsSent++;
        }

        void SendGun(int slot, int gunId, Connection except)
        {
            w.Reset(); w.U8(EvGun); w.U8((byte)slot); w.I32(gunId);
            var msg = w.ToArray();
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (c != except && (ws == null || ws.IsReady(c) || !NetSession.IsHost)) c.Send((byte)Channel.Event, msg, Delivery.ReliableOrdered);
        }

        void ApplyGun(Connection from, ref WireReader r)
        {
            int slot = r.U8();
            int id = r.I32();
            if (NetSession.IsHost) { if (slot != from.Slot) return; SendGun(slot, id, from); } // a guest speaks only for itself
            gunWanted[slot] = id;
            FollowGuns();
        }

        /// <summary>Hold the gun each copy's owner holds (a gun picked up a moment ago may not be in the copy's
        /// inventory yet: keep trying).</summary>
        void FollowGuns()
        {
            foreach (var kv in gunWanted)
            {
                var p = PlayerSlots.ForSlot(kv.Key);
                // A copy made a moment ago (a friend rejoining) holds no gun yet: switching it then fails inside the game.
                if (!p || !PlayerSlots.IsRemote(p) || p.inventory == null || kv.Value < 0 || !p.CurrentGun) continue;
                if (p.CurrentGun.PickupObjectId == kv.Value) continue;
                var all = p.inventory.AllGuns;
                for (int i = 0; i < all.Count; i++)
                    if (all[i] && all[i].PickupObjectId == kv.Value)
                    {
                        try { p.ChangeToGunSlot(i, true); GunsApplied++; }
                        catch (System.Exception e) { Plugin.Log.LogWarning($"Vitals: gun switch on slot {kv.Key} copy waits ({e.GetType().Name})"); }
                        break;
                    }
            }
        }

        public static int WantedGun(int slot) => Instance != null && Instance.gunWanted.TryGetValue(slot, out var id) ? id : int.MinValue;

        void LateUpdateImpl()
        {
            if (NetSession.Active)
            {
                try { OwnGunTick(); if (gunWanted.Count > 0 && !GameManager.Instance.IsLoadingLevel) FollowGuns(); }
                catch (System.Exception e) { Plugin.Log.LogWarning("Vitals: gun " + e.Message); }
            }
            if (!IsHostSession || GameManager.Instance.IsLoadingLevel) return;
            var list = vitalsList;
            list.Clear();
            if (PlayerSlots.Local) list.Add(new KeyValuePair<int, PlayerController>(NetSession.LocalSlot, PlayerSlots.Local));
            foreach (var kv in PlayerSlots.Remotes) if (kv.Value) list.Add(kv);
            bool changed = false;
            foreach (var kv in list)
            {
                // Cheap change test (numbers, no strings); the text key is only built when something moved.
                var hh = kv.Value.healthHaver;
                var cc = kv.Value.carriedConsumables;
                float sig = hh.GetCurrentHealth() * 7f + hh.GetMaxHealth() * 131f + hh.Armor * 1031f + kv.Value.Blanks * 9973f
                            + (cc != null ? cc.Currency * 3.7f + cc.KeyBullets * 101.3f : 0f);
                if (lastSig.TryGetValue(kv.Key, out var ps) && ps == sig && lastVitals.ContainsKey(kv.Key)) continue;
                lastSig[kv.Key] = sig;
                string key = Key(kv.Key, kv.Value);
                if (!lastVitals.TryGetValue(kv.Key, out var prev) || prev != key) { lastVitals[kv.Key] = key; changed = true; }
            }
            if (!changed) return;
            w.Reset(); w.U8(EvVitals); w.U8((byte)list.Count);
            foreach (var kv in list)
            {
                var p = kv.Value;
                w.U8((byte)kv.Key);
                w.U32(hurtApplied.TryGetValue(kv.Key, out var a) ? a : 0);
                w.F32(p.healthHaver.GetCurrentHealth()); w.F32(p.healthHaver.GetMaxHealth()); w.F32(p.healthHaver.Armor);
                w.I16((short)p.Blanks);
                w.I32(p.carriedConsumables != null ? p.carriedConsumables.Currency : 0);
                w.I16((short)(p.carriedConsumables != null ? p.carriedConsumables.KeyBullets : 0));
            }
            var msg = w.ToArray();
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (ws != null && ws.IsReady(c)) c.Send((byte)Channel.Event, msg, Delivery.ReliableOrdered);
            VitalsSent++;
        }

        static string Key(int slot, PlayerController p)
        {
            var hh = p.healthHaver;
            int money = p.carriedConsumables != null ? p.carriedConsumables.Currency : 0;
            int keys = p.carriedConsumables != null ? p.carriedConsumables.KeyBullets : 0;
            return $"{hh.GetCurrentHealth():F2}/{hh.GetMaxHealth():F2}/{hh.Armor:F2}/{p.Blanks}/{money}/{keys}";
        }
    }
}
