using System.Collections.Generic;
using Dungeonator;
using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Props the floor generator places identically on every machine: pots and crates, barrels, tables and minecarts.
    /// The host decides when they break or flip and where carts are; the client follows. Objects are matched by
    /// position because both machines build the same floor from the shared seed.
    /// </summary>
    [DefaultExecutionOrder(31900)]
    internal sealed class PropSync : MonoBehaviour
    {
        public static PropSync Instance { get; private set; }
        const byte EvMinor = 80, EvMajor = 81, EvFlip = 82, EvCarts = 83, EvElevator = 84, EvMyCart = 85, EvKick = 86, EvKickPos = 87, EvMovers = 88, EvKickReq = 89, EvFlipReq = 1; // EvFlipReq goes on the Props channel (the event ids are all taken)

        static bool scope;
        static bool IsClient => NetSession.Active && !NetSession.IsHost;
        static bool IsHostSession => NetSession.Active && NetSession.IsHost;

        List<MineCartController> carts;
        float nextCarts;
        public int Breaks, Flips, CartUpdates, Elevators;
        readonly WireWriter w = new WireWriter(512);

        public static void Install(Harmony h)
        {
            var t = typeof(PropSync);
            foreach (var m in typeof(MinorBreakable).GetMethods())
                if (m.Name == nameof(MinorBreakable.Break) && m.DeclaringType == typeof(MinorBreakable))
                    h.Patch(m, prefix: new HarmonyMethod(t, nameof(ClientBlock)), postfix: new HarmonyMethod(t, nameof(MinorPost)));
            // Kicked barrels roll with each game's own physics and ended up in different places: the host's roll is
            // the real one; guests start the same roll and follow the host's position until it stops.
            h.Patch(AccessTools.Method(typeof(KickableObject), "Kick"), prefix: new HarmonyMethod(t, nameof(KickPrefix)), postfix: new HarmonyMethod(t, nameof(KickPost)));
            h.Patch(AccessTools.Method(typeof(MajorBreakable), nameof(MajorBreakable.Break)), prefix: new HarmonyMethod(t, nameof(ClientBlock)), postfix: new HarmonyMethod(t, nameof(MajorPost)));
            h.Patch(AccessTools.Method(typeof(FlippableCover), nameof(FlippableCover.Flip), new[] { typeof(DungeonData.Direction) }),
                prefix: new HarmonyMethod(t, nameof(FlipPrefix)), postfix: new HarmonyMethod(t, nameof(FlipPost)));
            // A flip by a player starts by switching on the flipped table's wall, before the flip itself: on a guest
            // that left an upright table blocking like a flipped one. The whole flip waits for the host.
            h.Patch(AccessTools.Method(typeof(FlippableCover), nameof(FlippableCover.Flip), new[] { typeof(SpeculativeRigidbody) }),
                prefix: new HarmonyMethod(t, nameof(FlipByPlayerPrefix)));
            // The exit elevator leaves when the host's game sees everyone inside; the guest's elevator follows it,
            // so a guest whose copy of the players lines up a little differently is never left standing in it.
            h.Patch(AccessTools.Method(typeof(ElevatorDepartureController), nameof(ElevatorDepartureController.DoDeparture)),
                prefix: new HarmonyMethod(t, nameof(ClientBlock)), postfix: new HarmonyMethod(t, nameof(ElevatorPost)));
            h.Patch(AccessTools.Method(typeof(ElevatorDepartureController), nameof(ElevatorDepartureController.DoCryoDeparture)),
                prefix: new HarmonyMethod(t, nameof(ClientBlock)), postfix: new HarmonyMethod(t, nameof(CryoPost)));
        }

        static void ElevatorPost(ElevatorDepartureController __instance)
        {
            if (IsHostSession && !scope && Instance) { Instance.SendAt(EvElevator, __instance.transform.position, 0); Instance.Elevators++; }
        }

        static void CryoPost(ElevatorDepartureController __instance, bool playerless)
        {
            if (IsHostSession && !scope && Instance && !playerless) { Instance.SendAt(EvElevator, __instance.transform.position, 1); Instance.Elevators++; }
        }

        void Awake() { Instance = this; }

        void Start()
        {
            NetSession.Instance.On(Channel.Props, (c, data, d) =>
            {
                if (data.Length > 0 && data[0] == EvMyCart && NetSession.IsHost) { var mr = new WireReader(data, 1, data.Length - 1); ApplyGuestCart(c, ref mr); return; }
                if (data.Length > 0 && data[0] == EvFlipReq && NetSession.IsHost)
                {
                    var fr = new WireReader(data, 1, data.Length - 1);
                    var fp = new Vector2(fr.F32(), fr.F32());
                    int dir = fr.I32();
                    try
                    {
                        // Flip as that player's copy here, so the table's own rules (some only tip sideways) apply.
                        var who = PlayerSlots.ForSlot(c.Slot);
                        var f = Nearest(SceneObjects.All<FlippableCover>(), fp, x => !x.IsFlipped);
                        if (f && who && who.specRigidbody) f.Flip(who.specRigidbody);
                        else HostRequest(EvFlipReq, fp, dir);
                    }
                    catch (System.Exception e) { Plugin.Log.LogWarning("PropSync: flip request " + e.Message); }
                    return;
                }
                if (data.Length > 0 && data[0] == EvMovers && !NetSession.IsHost && WorldSync.Instance != null && WorldSync.Instance.FloorReady)
                { var vr = new WireReader(data, 1, data.Length - 1); ApplyMovers(ref vr); return; }
                if (data.Length == 0 || data[0] != EvCarts || WorldSync.Instance == null || !WorldSync.Instance.FloorReady) return;
                var r = new WireReader(data, 1, data.Length - 1);
                ApplyCarts(ref r);
            });
        }
        public void OnFloorChanged() { carts = null; movers = null; rolling.Clear(); guestKicked.Clear(); }

        static bool ClientBlock() => !IsClient || scope;

        void Send(byte[] data)
        {
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (ws != null && ws.IsReady(c)) c.Send((byte)Channel.Event, data, Delivery.ReliableOrdered);
        }

        void SendAt(byte type, Vector2 p, int extra = 0)
        {
            w.Reset(); w.U8(type); w.F32(p.x); w.F32(p.y); w.I32(extra);
            Send(w.ToArray());
        }

        readonly Dictionary<int, KickableObject> rolling = new Dictionary<int, KickableObject>(); // host: kick id
        readonly Dictionary<int, KickableObject> guestKicked = new Dictionary<int, KickableObject>();
        int nextKick = 1;
        float nextKickSend;

        /// <summary>Guest: its own kick starts the roll here at once and asks the host to kick the real barrel (the
        /// host's copy of this player does not always get to it). The host's roll is the one that counts.</summary>
        static bool KickPrefix(KickableObject __instance, SpeculativeRigidbody kickerRigidbody, out Vector2 __state)
        {
            __state = __instance ? (Vector2)__instance.transform.position : Vector2.zero;
            if (!IsClient || scope) return true;
            var me = PlayerSlots.Local;
            if (!me || !kickerRigidbody || kickerRigidbody != me.specRigidbody || !Instance) return false;
            Instance.SendAt(EvKickReq, __state, NetSession.LocalSlot);
            return true;
        }

        /// <summary>Guest: flipping a table asks the host, which flips the real one (and with it everyone's).</summary>
        static bool FlipPrefix(FlippableCover __instance, DungeonData.Direction flipDirection)
        {
            if (!IsClient || scope) return true;
            if (Instance && __instance && !__instance.IsFlipped)
            {
                var w = Instance.w;
                Vector2 at = __instance.transform.position;
                w.Reset(); w.U8(EvFlipReq); w.F32(at.x); w.F32(at.y); w.I32((int)flipDirection);
                NetSession.Instance.Node.Broadcast((byte)Channel.Props, w.ToArray(), Delivery.ReliableOrdered);
            }
            return false;
        }

        static bool FlipByPlayerPrefix(FlippableCover __instance, SpeculativeRigidbody flipperRigidbody)
        {
            if (!IsClient || scope) return true;
            if (__instance && !__instance.IsFlipped && flipperRigidbody) FlipPrefix(__instance, __instance.GetFlipDirection(flipperRigidbody));
            return false;
        }

        readonly Dictionary<int, float> kickedAt = new Dictionary<int, float>();
        const int StoppedFlag = 1 << 24;
        const int FollowOnly = 14; // EvKick "slot": no kick here, just follow the host's barrel

        /// <summary>Host: the guest's barrel must end up where ours is, kicked or not (the guest already rolled its own).</summary>
        void FollowAnyway(KickableObject k)
        {
            int id = nextKick++;
            rolling[id] = k;
            SendAt(EvKick, k.transform.position, id * 16 + FollowOnly);
        }

        void HostRequest(byte type, Vector2 p, int extra)
        {
            if (type == EvKickReq)
            {
                var k = Nearest(SceneObjects.All<KickableObject>(), p, x => x && x.specRigidbody && x.specRigidbody.enabled);
                var who = PlayerSlots.ForSlot(extra);
                if (!k) return;
                // Already rolling, or kicked a moment ago by this player's copy here: once is enough (but the guest
                // still follows our barrel to wherever it stops).
                bool recent = kickedAt.TryGetValue(k.GetInstanceID(), out var t0) && Time.realtimeSinceStartup - t0 < 0.6f;
                if (!who || !who.specRigidbody || k.specRigidbody.Velocity.sqrMagnitude > 0.01f || recent)
                {
                    if (Dev.DevEnv.IsHarness) Plugin.Log.LogInfo($"PROPSYNC kick request from slot {extra} not kicked (rolling or just kicked)");
                    if (!recent) FollowAnyway(k);
                    return;
                }
                AccessTools.Method(typeof(KickableObject), "Kick").Invoke(k, new object[] { who.specRigidbody });
                if (Dev.DevEnv.IsHarness) Plugin.Log.LogInfo($"PROPSYNC kicked for slot {extra} at {p}");
            }
            else if (type == EvFlipReq)
            {
                var f = Nearest(SceneObjects.All<FlippableCover>(), p, x => !x.IsFlipped);
                if (f) f.Flip((DungeonData.Direction)extra);
            }
        }

        static void KickPost(KickableObject __instance, SpeculativeRigidbody kickerRigidbody, Vector2 __state)
        {
            if (!IsHostSession || scope || !Instance || !__instance) return;
            Instance.kickedAt[__instance.GetInstanceID()] = Time.realtimeSinceStartup;
            int id = Instance.nextKick++;
            Instance.rolling[id] = __instance;
            var kicker = kickerRigidbody ? kickerRigidbody.GetComponent<PlayerController>() : null;
            int slot = kicker ? PlayerSlots.SlotOf(kicker) : -1;
            Instance.SendAt(EvKick, __state, id * 16 + (slot < 0 ? 15 : slot));
        }

        void SendRolling()
        {
            if (rolling.Count == 0 || Time.realtimeSinceStartup < nextKickSend) return;
            nextKickSend = Time.realtimeSinceStartup + 0.1f;
            List<int> done = null;
            foreach (var kv in rolling)
            {
                var k = kv.Value;
                if (!k) { (done ??= new List<int>()).Add(kv.Key); continue; }
                bool stopped = !k.specRigidbody || k.specRigidbody.Velocity.sqrMagnitude < 0.0001f;
                // The last word says "stopped": the guest's own roll must end exactly here, not run on.
                SendAt(EvKickPos, k.transform.position, kv.Key | (stopped ? StoppedFlag : 0));
                if (stopped) (done ??= new List<int>()).Add(kv.Key);
            }
            if (done != null) foreach (var id in done) rolling.Remove(id);
        }

        static void MinorPost(MinorBreakable __instance)
        {
            if (IsHostSession && !scope && Instance) { Instance.SendAt(EvMinor, __instance.transform.position); Instance.Breaks++; }
        }

        static void MajorPost(MajorBreakable __instance)
        {
            if (IsHostSession && !scope && Instance) { Instance.SendAt(EvMajor, __instance.transform.position); Instance.Breaks++; }
        }

        static void FlipPost(FlippableCover __instance, DungeonData.Direction flipDirection)
        {
            if (IsHostSession && !scope && Instance) { Instance.SendAt(EvFlip, __instance.transform.position, (int)flipDirection); Instance.Flips++; }
        }

        static T Nearest<T>(IEnumerable<T> items, Vector2 p, System.Func<T, bool> ok) where T : Component
        {
            T best = null;
            float bestD = 0.75f * 0.75f;
            foreach (var it in items)
            {
                if (!it || !ok(it)) continue;
                float d = ((Vector2)it.transform.position - p).sqrMagnitude;
                if (d < bestD) { bestD = d; best = it; }
            }
            return best;
        }

        List<MineCartController> Carts()
        {
            if (carts == null)
            {
                carts = SceneObjects.All<MineCartController>();
                // Same floor on both machines: order by starting position so index i is the same cart everywhere.
                carts.Sort((a, b) =>
                {
                    Vector2 pa = a.transform.position, pb = b.transform.position;
                    int c = pa.x.CompareTo(pb.x);
                    return c != 0 ? c : pa.y.CompareTo(pb.y);
                });
            }
            return carts;
        }

        float nextMyCart;
        public static readonly Dictionary<int, float> GuestCartUntil = new Dictionary<int, float>();

        /// <summary>Guest: while riding, tell the host where our cart is (30 times a second).</summary>
        void SendMyCart()
        {
            var me = PlayerSlots.Local;
            if (!IsClient || me == null || !me.currentMineCart || Time.realtimeSinceStartup < nextMyCart) return;
            // Only the driver steers: when both ride one cart, the host's driver decides where it goes.
            if (me.currentMineCart.CurrentInhabitant != me) return;
            nextMyCart = Time.realtimeSinceStartup + 1f / 30f;
            int idx = Carts().IndexOf(me.currentMineCart);
            if (idx < 0) return;
            Vector2 p = me.currentMineCart.transform.position;
            w.Reset(); w.U8(EvMyCart); w.U8((byte)idx); w.F32(p.x); w.F32(p.y);
            foreach (var c in NetSession.Instance.Node.Connections) c.Send((byte)Channel.Props, w.ToArray(), Delivery.Unreliable);
        }

        /// <summary>Host: the guest's cart goes where the guest's game says; the guest's character rides along.</summary>
        void ApplyGuestCart(Connection from, ref WireReader r)
        {
            if (!SceneObjects.OnDungeonFloor) return;
            int idx = r.U8();
            var p = new Vector2(r.F32(), r.F32());
            var list = Carts();
            if (idx >= list.Count || !list[idx]) return;
            var cart = list[idx];
            cart.transform.position = new Vector3(p.x, p.y, cart.transform.position.z);
            if (cart.specRigidbody) cart.specRigidbody.Reinitialize();
            GuestCartUntil[from.Slot] = Time.realtimeSinceStartup + 0.5f;
        }

        void LateUpdate()
        {
            SendMyCart();
            if (IsHostSession) SendRolling();
            if (IsHostSession && SceneObjects.OnDungeonFloor && WorldSync.Instance != null && WorldSync.Instance.ReadyPeers > 0) SendMovers();
            if (!IsHostSession || !SceneObjects.OnDungeonFloor || Time.realtimeSinceStartup < nextCarts) return;
            nextCarts = Time.realtimeSinceStartup + 1f / 30f;
            var list = Carts();
            if (list.Count == 0) return;
            w.Reset(); w.U8(EvCarts); w.U8((byte)Mathf.Min(255, list.Count));
            for (int i = 0; i < list.Count && i < 255; i++)
            {
                Vector2 p = list[i] ? (Vector2)list[i].transform.position : Vector2.zero;
                w.F32(p.x); w.F32(p.y);
            }
            var msg = w.ToArray();
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (ws != null && ws.IsReady(c)) c.Send((byte)Channel.Props, msg, Delivery.Unreliable);
        }

        // ---------------- Moving platforms ----------------
        // Platforms and other things on a path (not enemies, not carts) start moving when each game gets to them,
        // so they ran at different spots: the host's position is sent and the guests' copies follow it.

        List<PathMover> movers;
        float nextMovers;

        List<PathMover> Movers()
        {
            if (movers == null)
            {
                movers = new List<PathMover>();
                foreach (var m in SceneObjects.All<PathMover>())
                    if (m && m.Path != null && m.Path.nodes != null && m.Path.nodes.Count > 0 && m.RoomHandler != null && !m.GetComponent<AIActor>() && !m.GetComponent<MineCartController>())
                        movers.Add(m);
                // Ordered by where each path starts: the same on every machine.
                movers.Sort((a, b) =>
                {
                    Vector2 pa = a.GetPositionOfNode(a.PathStartNode), pb = b.GetPositionOfNode(b.PathStartNode);
                    int c = pa.x.CompareTo(pb.x);
                    if (c == 0) c = pa.y.CompareTo(pb.y);
                    return c != 0 ? c : a.name.CompareTo(b.name);
                });
            }
            return movers;
        }

        void SendMovers()
        {
            if (Time.realtimeSinceStartup < nextMovers) return;
            nextMovers = Time.realtimeSinceStartup + 1f / 15f;
            var list = Movers();
            if (list.Count == 0) return;
            w.Reset(); w.U8(EvMovers); w.U8((byte)Mathf.Min(255, list.Count));
            for (int i = 0; i < list.Count && i < 255; i++)
            {
                Vector2 p = list[i] ? (Vector2)list[i].transform.position : Vector2.zero;
                w.F32(p.x); w.F32(p.y);
            }
            var msg = w.ToArray();
            var ws = WorldSync.Instance;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (ws != null && ws.IsReady(c)) c.Send((byte)Channel.Props, msg, Delivery.Unreliable);
        }

        public int MoverUpdates;

        void ApplyMovers(ref WireReader r)
        {
            if (!SceneObjects.OnDungeonFloor) return;
            int n = r.U8();
            var list = Movers();
            for (int i = 0; i < n; i++)
            {
                var p = new Vector2(r.F32(), r.F32());
                if (i >= list.Count || !list[i]) continue;
                var m = list[i];
                if (((Vector2)m.transform.position - p).sqrMagnitude < 0.01f) continue;
                // Small differences are left to the platform's own movement (it keeps moving between updates).
                if (((Vector2)m.transform.position - p).sqrMagnitude < 0.25f && m.enabled) continue;
                m.transform.position = new Vector3(p.x, p.y, m.transform.position.z);
                if (m.specRigidbody) m.specRigidbody.Reinitialize();
                MoverUpdates++;
            }
        }

        public void ApplyCarts(ref WireReader r)
        {
            if (!IsClient || !SceneObjects.OnDungeonFloor) return;
            int n = r.U8();
            var list = Carts();
            for (int i = 0; i < n; i++)
            {
                var p = new Vector2(r.F32(), r.F32());
                if (i >= list.Count || !list[i]) continue;
                var cart = list[i];
                // The cart you ride is yours to drive, like your own player; the host's copy is corrected through you.
                var me = PlayerSlots.Local;
                if (me && me.currentMineCart == cart) continue;
                if (((Vector2)cart.transform.position - p).sqrMagnitude < 0.0025f) continue;
                bool ridden = false;
                foreach (var pl in GameManager.Instance.AllPlayers) if (pl && pl.currentMineCart == cart) ridden = true;
                if (ridden) DrawSmoothing.Clear(cart); else DrawSmoothing.Corrected(cart, cart.transform.position, p);
                cart.transform.position = new Vector3(p.x, p.y, cart.transform.position.z);
                if (cart.specRigidbody) cart.specRigidbody.Reinitialize();
                CartUpdates++;
            }
        }

        /// <summary>Events 80-89 from the host.</summary>
        public void Apply(byte type, ref WireReader r)
        {
            if (NetSession.IsHost)
            {
                if (type != EvKickReq) return;
                var hp = new Vector2(r.F32(), r.F32());
                int he = r.I32();
                try { HostRequest(type, hp, he); } catch (System.Exception e) { Plugin.Log.LogWarning("PropSync: request " + e.Message); }
                return;
            }
            var p = new Vector2(r.F32(), r.F32());
            int extra = r.I32();
            scope = true;
            try
            {
                switch (type)
                {
                    case EvMinor:
                        {
                            var b = Nearest(StaticReferenceManager.AllMinorBreakables, p, x => !x.IsBroken);
                            if (b) { b.Break(); Breaks++; }
                            break;
                        }
                    case EvMajor:
                        {
                            var b = Nearest(StaticReferenceManager.AllMajorBreakables, p, x => !x.IsDestroyed);
                            if (b) { b.Break(Vector2.zero); Breaks++; }
                            break;
                        }
                    case EvElevator:
                        {
                            var e = Nearest(SceneObjects.All<ElevatorDepartureController>(), p, x => true);
                            if (e) { if (extra == 1) e.DoCryoDeparture(false); else e.DoDeparture(); Elevators++; }
                            else Plugin.Log.LogWarning("PropSync: no elevator at " + p);
                            break;
                        }
                    case EvKick:
                        {
                            var k = Nearest(SceneObjects.All<KickableObject>(), p, x => true);
                            if (!k) break;
                            int id = extra / 16, slot = extra % 16;
                            guestKicked[id] = k;
                            var who = slot < FollowOnly ? PlayerSlots.ForSlot(slot) : null;
                            var m = AccessTools.Method(typeof(KickableObject), "Kick");
                            // Our own kick already started this roll here.
                            bool rolling = k.specRigidbody && k.specRigidbody.Velocity.sqrMagnitude > 0.01f;
                            if (!rolling && who && who.specRigidbody) m.Invoke(k, new object[] { who.specRigidbody });
                            break;
                        }
                    case EvKickPos:
                        {
                            bool stopped = (extra & StoppedFlag) != 0;
                            if (!guestKicked.TryGetValue(extra & ~StoppedFlag, out var k) || !k) break;
                            k.transform.position = new Vector3(p.x, p.y, k.transform.position.z);
                            if (k.specRigidbody)
                            {
                                // Our own roll ends here too (the game's own stop: no more rolling, bouncing or goop trail).
                                if (stopped && k.specRigidbody.Velocity.sqrMagnitude > 0f) AccessTools.Method(typeof(KickableObject), "StopRolling").Invoke(k, new object[] { false });
                                k.specRigidbody.Reinitialize();
                            }
                            if (stopped) guestKicked.Remove(extra & ~StoppedFlag);
                            break;
                        }
                    case EvFlip:
                        {
                            var f = Nearest(SceneObjects.All<FlippableCover>(), p, x => !x.IsFlipped);
                            if (f) { f.Flip((DungeonData.Direction)extra); Flips++; }
                            break;
                        }
                }
            }
            catch (System.Exception e) { Plugin.Log.LogError("PropSync: " + e.Message); }
            finally { scope = false; }
        }
    }
}
