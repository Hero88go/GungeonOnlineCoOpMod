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
        const byte EvMinor = 80, EvMajor = 81, EvFlip = 82, EvCarts = 83, EvElevator = 84, EvMyCart = 85, EvKick = 86, EvKickPos = 87;

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
                prefix: new HarmonyMethod(t, nameof(ClientBlock)), postfix: new HarmonyMethod(t, nameof(FlipPost)));
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
                if (data.Length == 0 || data[0] != EvCarts || WorldSync.Instance == null || !WorldSync.Instance.FloorReady) return;
                var r = new WireReader(data, 1, data.Length - 1);
                ApplyCarts(ref r);
            });
        }
        public void OnFloorChanged() { carts = null; }

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

        static bool KickPrefix(KickableObject __instance, out Vector2 __state)
        {
            __state = __instance ? (Vector2)__instance.transform.position : Vector2.zero;
            return ClientBlock();
        }

        static void KickPost(KickableObject __instance, SpeculativeRigidbody kickerRigidbody, Vector2 __state)
        {
            if (!IsHostSession || scope || !Instance || !__instance) return;
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
                SendAt(EvKickPos, k.transform.position, kv.Key);
                if (!k.specRigidbody || k.specRigidbody.Velocity.sqrMagnitude < 0.0001f) (done ??= new List<int>()).Add(kv.Key);
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
            if (NetSession.IsHost) return;
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
                            var who = slot < 15 ? PlayerSlots.ForSlot(slot) : null;
                            var m = AccessTools.Method(typeof(KickableObject), "Kick");
                            if (who && who.specRigidbody) m.Invoke(k, new object[] { who.specRigidbody });
                            break;
                        }
                    case EvKickPos:
                        {
                            if (!guestKicked.TryGetValue(extra, out var k) || !k) break;
                            k.transform.position = new Vector3(p.x, p.y, k.transform.position.z);
                            if (k.specRigidbody) k.specRigidbody.Reinitialize();
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
