using System.Collections.Generic;
using Dungeonator;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Host-owned world: enemies and room combat state.
    /// Host: watches every AIActor and room, sends spawn/death/damage/seal events (reliable) and
    /// state snapshots (unreliable). Client: removes its own generated enemies after each floor load and
    /// shows replicas whose brains are off; rooms seal, unseal and spawn waves only on host events.
    /// </summary>
    [DefaultExecutionOrder(31500)]
    internal sealed class WorldSync : MonoBehaviour
    {
        public static WorldSync Instance { get; private set; }

        const byte EvFloorReady = 1, EvSpawn = 2, EvDespawn = 3, EvDeath = 4, EvDamage = 5, EvRoomSeal = 6, EvJammed = 7, EvReaper = 8, EvSpawnPart = 9, EvSpawnGroup = 10;

        /// <summary>True while applying a host event on the client (lets the blocked game paths run).</summary>
        public static bool ApplyingHost;

        static bool IsClient => NetSession.Active && !NetSession.IsHost;
        static bool IsHostSession => NetSession.Active && NetSession.IsHost;

        // Host state
        readonly HashSet<Connection> ready = new HashSet<Connection>();
        readonly HashSet<uint> deathSent = new HashSet<uint>();
        sealed class Sent { public Vector2 Pos; public int Clip; public byte Flags, Angle; public byte Hp; public float At; }
        readonly Dictionary<uint, Sent> lastSent = new Dictionary<uint, Sent>();
        float nextSnapshot;
        uint snapSeq;

        // Client state
        public bool FloorReady;
        sealed class Replica
        {
            public AIActor Actor;
            public Vector2 Target, Velocity, OldTarget, OldVelocity;
            public float OldTime;
            public float TargetTime, MovedFor = -1f; // MovedFor: the snapshot the last move used
            public int Clip;
            public bool Jammed;
            // Host state that the enemy's own AI would show here: gun aim and the sniper's laser line.
            public bool HasGun, Laser;
            public float GunAngle;
        }
        readonly Dictionary<uint, Replica> replicas = new Dictionary<uint, Replica>();
        uint lastSnapSeq;
        readonly List<byte[]> earlyEvents = new List<byte[]>();

        public int ReplicaCount => replicas.Count;
        public int TrackedCount => lastSent.Count;
        public int ReadyPeers => ready.Count;
        public bool IsReady(Connection c) => ready.Contains(c);

        public bool IsSlotReady(int slot)
        {
            foreach (var c in ready) if (c.Slot == slot) return true;
            return false;
        }
        public bool DeathAnnounced(uint id) => deathSent.Contains(id);
        public bool IsReplica(uint id) => replicas.ContainsKey(id);
        public int SpawnsSent, DeathsSent, SpawnsApplied, DeathsApplied, SnapshotsApplied, LocalActorsSuppressed, SpawnFailures;

        readonly WireWriter w = new WireWriter(2048);

        public static void Install(Harmony h)
        {
            var t = typeof(WorldSync);
            h.Patch(AccessTools.Method(typeof(RoomHandler), nameof(RoomHandler.SealRoom)), prefix: new HarmonyMethod(t, nameof(SealPrefix)), postfix: new HarmonyMethod(t, nameof(SealPostfix)));
            h.Patch(AccessTools.Method(typeof(RoomHandler), nameof(RoomHandler.UnsealRoom)), prefix: new HarmonyMethod(t, nameof(SealPrefix)), postfix: new HarmonyMethod(t, nameof(UnsealPostfix)));
            h.Patch(AccessTools.Method(typeof(RoomHandler), nameof(RoomHandler.TriggerReinforcementLayer)), prefix: new HarmonyMethod(t, nameof(ClientBlockBool)));
            h.Patch(AccessTools.Method(typeof(RoomHandler), nameof(RoomHandler.HandleRoomClearReward)), prefix: new HarmonyMethod(t, nameof(ClientBlock)));
            h.Patch(AccessTools.Method(typeof(AIActor), "HandleLootPinata"), prefix: new HarmonyMethod(t, nameof(ClientBlock)));
            h.Patch(AccessTools.Method(typeof(AIActor), nameof(AIActor.ForceDeath)), prefix: new HarmonyMethod(t, nameof(DeathPrefix)));
            h.Patch(AccessTools.Method(typeof(HealthHaver), nameof(HealthHaver.ApplyDamage)), prefix: new HarmonyMethod(t, nameof(DamagePrefix)), postfix: new HarmonyMethod(t, nameof(DamagePostfix)));
            h.Patch(AccessTools.Method(typeof(HealthHaver), nameof(HealthHaver.Die)), prefix: new HarmonyMethod(t, nameof(DiePrefix)));
            // Client: enemies that appear on their own (split-on-death, summons) are the host's to spawn.
            h.Patch(AccessTools.Method(typeof(AIActor), "Start"), prefix: new HarmonyMethod(t, nameof(ActorStartPrefix)));
            // A copy of a host's wall mimic is placed where the host's one is; marking the wall cells there can fail.
            // The copy's own AI never runs, so it needs none of that: the copy is kept instead of failing the spawn.
            h.Patch(AccessTools.Method(typeof(WallMimicController), nameof(WallMimicController.ConfigureOnPlacement)), finalizer: new HarmonyMethod(t, nameof(CopyPlacementFinalizer)));
            // The Door Lord's death code reaches for the boss door it replaced, which a guest's copy does not have:
            // without this the copy never dies on the guest's screen.
            var doorDeath = AccessTools.Method(typeof(BossDoorMimicDeathController), "OnBossDeath");
            if (doorDeath != null) h.Patch(doorDeath, finalizer: new HarmonyMethod(t, nameof(CopyDeathFinalizer)));
            h.Patch(AccessTools.Method(typeof(Dungeon), nameof(Dungeon.SpawnCurseReaper)), prefix: new HarmonyMethod(t, nameof(SpawnReaperPrefix)));
            h.Patch(AccessTools.Method(typeof(SuperReaperController), "HandleMotion"), prefix: new HarmonyMethod(t, nameof(ReaperMotionPrefix)));
            // Boss death: the host kills the remaining enemies and their deaths replicate; the client must not do it itself.
            h.Patch(AccessTools.Method(typeof(BossKillCam), "KillAllEnemies"), prefix: new HarmonyMethod(t, nameof(ClientBlock)));
            // Client: erasing its own copy of the floor's boss must not start a boss kill cam (it froze the player).
            h.Patch(AccessTools.Method(typeof(GameUIRoot), nameof(GameUIRoot.TriggerBossKillCam)), prefix: new HarmonyMethod(t, nameof(KillCamPrefix)));
            // Client: a copy's animation ending must not start the AI's next one; the host says what plays next.
            h.Patch(AccessTools.Method(typeof(AIAnimator), "AnimationCompleted"), prefix: new HarmonyMethod(t, nameof(CopyAnimationDonePrefix)));
        }

        static bool CopyAnimationDonePrefix(AIAnimator __instance)
        {
            if (!IsClient || Instance == null || __instance == null) return true;
            var a = __instance.aiActor;
            return a == null || !Instance.IsReplica(NetIds.Of(a));
        }

        static System.Exception CopyPlacementFinalizer(System.Exception __exception)
        {
            if (__exception != null && IsClient && ApplyingHost) { Plugin.Log.LogInfo("WorldSync: wall mimic copy placed without its wall cells"); return null; }
            return __exception;
        }

        static System.Exception CopyDeathFinalizer(System.Exception __exception)
        {
            if (__exception != null && IsClient) { Plugin.Log.LogInfo("WorldSync: boss copy died without its door"); return null; }
            return __exception;
        }

        static bool erasing;
        static bool KillCamPrefix() => !erasing;

        void Awake() { Instance = this; }

        void Start()
        {
            NetSession.Instance.On(Channel.Event, OnEvent);
            NetSession.Instance.On(Channel.World, OnWorld);
            NetSession.Instance.PeerLeft += (c, why) => ready.Remove(c);
            NetSession.Instance.SessionEnded += Reset;
        }

        void Reset()
        {
            ready.Clear(); deathSent.Clear(); lastSent.Clear(); replicas.Clear(); earlyEvents.Clear(); jammedSent.Clear(); reaperSent = reaperWanted = false;
            FloorReady = false; lastSnapSeq = 0;
            NetIds.Clear();
            modEnemyPrefabs.Clear();
            if (modTemplateRoot) Destroy(modTemplateRoot);
        }

        // ---------------- Patches ----------------

        static bool ClientBlock() => !IsClient || ApplyingHost;

        static bool ClientBlockBool(ref bool __result)
        {
            if (!IsClient || ApplyingHost) return true;
            __result = false;
            return false;
        }

        /// <summary>Client: rooms seal and open only on the host's word. Not on side effects of other host events: an
        /// enemy erased or killed while applying the host's message made the game open its room here ("room cleared")
        /// while the host still had it shut (one of SecondFightLock's held rooms), until the checksum repaired it.</summary>
        static bool SealPrefix() => !IsClient || applyingSeal;
        static bool applyingSeal;

        static bool ActorStartPrefix(AIActor __instance)
        {
            if (!IsClient || ApplyingHost || Instance == null || !Instance.FloorReady) return true;
            if (__instance.GetComponent<PlayerController>() != null || __instance.CompanionOwner != null) return true;
            if (NetIds.Has(__instance)) return true;
            if (IsTemplate(__instance)) return true;
            // Part of a host enemy's copy (a boss's statues): it comes with that copy and the host names it.
            if (NamedAncestor(__instance) != null) { MakeReplica(__instance); return true; }
            RememberModEnemy(__instance);
            Instance.LocalActorsSuppressed++;
            __instance.EraseFromExistence(true);
            return false;
        }

        static void SealPostfix(RoomHandler __instance) { if (IsHostSession) Instance.SendRoomSeal(__instance, true); }
        static void UnsealPostfix(RoomHandler __instance) { if (IsHostSession) Instance.SendRoomSeal(__instance, false); }

        static bool IsReplica(HealthHaver hh)
        {
            if (!IsClient || hh == null) return false;
            var a = hh.aiActor;
            return a != null && NetIds.Has(a);
        }

        /// <summary>Client: replicas take damage only from host events (the host decides every hit on enemies).</summary>
        static bool DamagePrefix(HealthHaver __instance) => ApplyingHost || !IsReplica(__instance);

        static void DamagePostfix(HealthHaver __instance, float damage, Vector2 direction)
        {
            if (!IsHostSession || Instance == null) return;
            var a = __instance.aiActor;
            if (a == null) return;
            uint id = NetIds.Of(a);
            if (id == 0) return;
            var w = Instance.w;
            w.Reset(); w.U8(EvDamage); w.U32(id); w.F32(damage); w.F32(direction.x); w.F32(direction.y);
            Instance.SendEvent(w.ToArray());
        }

        static bool DiePrefix(HealthHaver __instance) => ApplyingHost || !IsReplica(__instance);

        static void DeathPrefix(AIActor __instance, Vector2 finalDamageDirection)
        {
            if (!IsHostSession || Instance == null) return;
            uint id = NetIds.Of(__instance);
            if (Dev.DevEnv.IsHarness) Plugin.Log.LogInfo($"DEATHSEND {id} {__instance.EnemyGuid} t={Time.realtimeSinceStartup:F2}");
            if (id == 0 || !Instance.deathSent.Add(id)) return;
            var w = Instance.w;
            w.Reset(); w.U8(EvDeath); w.U32(id); w.F32(finalDamageDirection.x); w.F32(finalDamageDirection.y);
            Instance.SendEvent(w.ToArray());
            Instance.DeathsSent++;
        }

        // ---------------- Host ----------------

        void SendEvent(byte[] data)
        {
            foreach (var c in ready) c.Send((byte)Channel.Event, data, Delivery.ReliableOrdered);
        }

        internal static int RoomIndex(RoomHandler r)
        {
            var rooms = GameManager.Instance.Dungeon != null ? GameManager.Instance.Dungeon.data.rooms : null;
            return rooms == null || r == null ? -1 : rooms.IndexOf(r);
        }

        internal static RoomHandler RoomAt(int i)
        {
            var rooms = GameManager.Instance.Dungeon != null ? GameManager.Instance.Dungeon.data.rooms : null;
            return rooms != null && i >= 0 && i < rooms.Count ? rooms[i] : null;
        }

        void SendRoomSeal(RoomHandler r, bool sealedNow)
        {
            int i = RoomIndex(r);
            if (i < 0) return;
            w.Reset(); w.U8(EvRoomSeal); w.I16((short)i); w.Bool(sealedNow);
            SendEvent(w.ToArray());
        }

        static bool Replicated(AIActor a) => a != null && !(a is null) && a.gameObject.activeInHierarchy && a.GetComponent<PlayerController>() == null && !IsTemplate(a);

        /// <summary>The enemy this one is a part of (Kill Pillars' statues are enemies inside the boss), or null.</summary>
        static AIActor OwnerOf(AIActor a)
        {
            var t = a.transform.parent;
            return t != null ? t.GetComponentInParent<AIActor>() : null;
        }

        /// <summary>The nearest object above this enemy that has a network id: an owner enemy, or a group such as
        /// Kill Pillars (a boss object that is not an enemy itself, holding four statue enemies).</summary>
        static Transform NamedAncestor(AIActor a)
        {
            for (var t = a.transform.parent; t != null; t = t.parent)
            {
                var o = t.GetComponent<AIActor>();
                if (o != null && NetIds.Has(o)) return t;
                if (NetIds.Has(t.gameObject)) return t;
            }
            return null;
        }

        static uint IdOf(Transform t)
        {
            var o = t.GetComponent<AIActor>();
            return o != null && NetIds.Has(o) ? NetIds.Of(o) : NetIds.Of(t.gameObject);
        }

        /// <summary>Host: the group object this enemy belongs to (a boss object that is not an enemy itself, such as
        /// Kill Pillars), with the catalogue entry that spawns the whole group. Its statues have no id of their own.</summary>
        static GameObject GroupOf(AIActor a, out string groupGuid)
        {
            groupGuid = null;
            for (var t = a.transform.parent; t != null; t = t.parent)
            {
                if (t.GetComponent<AIActor>() != null) return null;
                groupGuid = GroupGuid(t.name.Replace("(Clone)", "").Trim());
                if (groupGuid != null) return t.gameObject;
            }
            return null;
        }

        static readonly Dictionary<string, string> groupGuids = new Dictionary<string, string>();

        /// <summary>The catalogue entry (a stand-in such as "BossStatuesDummy") whose real object is named this.</summary>
        static string GroupGuid(string name)
        {
            if (groupGuids.TryGetValue(name, out var have)) return have;
            string found = null;
            foreach (var e in EnemyDatabase.Instance.Entries)
            {
                if (e == null || string.IsNullOrEmpty(e.path) || !e.name.StartsWith(name) || e.name == name) continue;
                var d = EnemyDatabase.GetOrLoadByGuid(e.myGuid) as AIActorDummy;
                if (d != null && d.realPrefab != null && d.realPrefab.name == name) { found = e.myGuid; break; }
            }
            groupGuids[name] = found;
            return found;
        }

        byte[] GroupMessage(uint id, GameObject g, AIActor part)
        {
            var gw = new WireWriter(64);
            GroupOf(part, out var guid);
            gw.U8(EvSpawnGroup); gw.U32(id); gw.Str(guid ?? "");
            Vector3 p = g.transform.position;
            gw.F32(p.x); gw.F32(p.y); gw.F32(p.z);
            gw.I16((short)RoomIndex(part.ParentRoom));
            return gw.ToArray();
        }

        byte[] SpawnMessage(uint id, AIActor a)
        {
            var ownerT = NamedAncestor(a);
            uint ownerId = ownerT != null ? IdOf(ownerT) : 0;
            if (ownerId != 0)
            {
                // A part comes with its owner's copy: name it by its place inside the owner.
                var path = new List<byte>();
                for (var t = a.transform; t != null && t != ownerT; t = t.parent) path.Insert(0, (byte)t.GetSiblingIndex());
                var pw = new WireWriter(64);
                pw.U8(EvSpawnPart); pw.U32(id); pw.U32(ownerId); pw.Str(a.EnemyGuid ?? "");
                pw.U8((byte)path.Count); foreach (var b in path) pw.U8(b);
                return pw.ToArray();
            }
            var ww = new WireWriter(96);
            ww.U8(EvSpawn); ww.U32(id); ww.Str(a.EnemyGuid ?? "");
            Vector2 p = a.transform.position;
            ww.F32(p.x); ww.F32(p.y);
            ww.I16((short)RoomIndex(a.ParentRoom));
            ww.Bool(a.IsBlackPhantom);
            return ww.ToArray();
        }

        void HostLateUpdate()
        {
            if (GameManager.Instance.Dungeon == null || Dungeon.IsGenerating) return;
            // New actors
            foreach (var a in StaticReferenceManager.AllEnemies)
            {
                // Companions come from their player's items and exist for every copy of that player on every machine.
                if (!Replicated(a) || NetIds.Has(a) || a.CompanionOwner != null) continue;
                var owner = OwnerOf(a);
                if (string.IsNullOrEmpty(a.EnemyGuid) && (owner != null || !GroupOf(a, out _))) continue;
                if (owner != null && Replicated(owner) && !string.IsNullOrEmpty(owner.EnemyGuid) && !NetIds.Has(owner)) continue; // owner first
                if (owner == null)
                {
                    var group = GroupOf(a, out _);
                    if (group != null && !NetIds.Has(group))
                    {
                        uint gid = NetIds.Assign(group);
                        var gmsg = GroupMessage(gid, group, a);
                        foreach (var c in ready) c.Send((byte)Channel.Event, gmsg, Delivery.ReliableOrdered);
                        if (Dev.DevEnv.IsHarness) Plugin.Log.LogInfo($"SPAWNGROUP {gid} {group.name}");
                    }
                }
                uint id = NetIds.Assign(a);
                var msg = SpawnMessage(id, a);
                if (Dev.DevEnv.IsHarness) Plugin.Log.LogInfo($"SPAWNSEND {id} {a.EnemyGuid} ready={ready.Count} t={Time.realtimeSinceStartup:F2}");
                foreach (var c in ready) c.Send((byte)Channel.Event, msg, Delivery.ReliableOrdered);
                SpawnsSent++;
            }
            // Destroyed actors
            foreach (var id in NetIds.Dead())
            {
                NetIds.Forget(id);
                lastSent.Remove(id);
                if (deathSent.Contains(id)) continue;
                w.Reset(); w.U8(EvDespawn); w.U32(id);
                SendEvent(w.ToArray());
            }
            if (ready.Count > 0 && Time.realtimeSinceStartup >= nextSnapshot)
            {
                nextSnapshot = Time.realtimeSinceStartup + 1f / Mathf.Max(10, NetConfig.WorldSnapshotHz.Value);
                SendJammed();
                SendReaper();
                SendSnapshot();
            }
        }

        // Jammed enemies: the roll uses each PC's own curse total, and items can make an enemy jammed later, so the
        // host's state is the one shown everywhere.
        readonly Dictionary<uint, bool> jammedSent = new Dictionary<uint, bool>();

        void SendJammed()
        {
            foreach (var kv in NetIds.All)
            {
                var a = kv.Value as AIActor;
                if (a == null) continue;
                bool j = a.IsBlackPhantom;
                if (jammedSent.TryGetValue(kv.Key, out var was) && was == j) continue;
                bool first = !jammedSent.ContainsKey(kv.Key);
                jammedSent[kv.Key] = j;
                if (first && !j) continue; // the spawn message already said "not jammed"
                w.Reset(); w.U8(EvJammed); w.U32(kv.Key); w.Bool(j);
                SendEvent(w.ToArray());
            }
        }

        // Lord of the Jammed is not an enemy actor: the host spawns it from its curse total and sends where it is.
        bool reaperSent;
        Vector2 reaperTarget;
        bool reaperWanted;
        GameObject reaperCopy; // the guest's copy; SuperReaperController.Instance is only set once it starts

        void SendReaper()
        {
            var sr = SuperReaperController.Instance;
            bool present = sr != null && sr.gameObject.activeInHierarchy;
            if (!present && !reaperSent) return;
            reaperSent = present;
            w.Reset(); w.U8(EvReaper); w.Bool(present);
            Vector2 p = present ? (Vector2)sr.transform.position : Vector2.zero;
            w.F32(p.x); w.F32(p.y);
            SendEvent(w.ToArray());
        }

        void ApplyReaper(bool present, Vector2 at)
        {
            reaperWanted = present; reaperTarget = at;
            var sr = SuperReaperController.Instance;
            var d = GameManager.Instance ? GameManager.Instance.Dungeon : null;
            if (present && sr == null && !reaperCopy && d != null && PrefabDatabase.Instance && PrefabDatabase.Instance.SuperReaper)
            {
                d.CurseReaperActive = true;
                reaperCopy = Instantiate(PrefabDatabase.Instance.SuperReaper, at.ToVector3ZUp(), Quaternion.identity);
                Plugin.Log.LogInfo("WorldSync: Lord of the Jammed appeared (host)");
            }
            else if (!present && (sr != null || reaperCopy))
            {
                if (sr != null) Destroy(sr.gameObject);
                if (reaperCopy) Destroy(reaperCopy);
                if (d != null) d.CurseReaperActive = false;
            }
        }

        void ClientMoveReaper()
        {
            var sr = SuperReaperController.Instance;
            if (!reaperWanted || sr == null) return;
            Vector2 cur = sr.transform.position;
            Vector2 next = Vector2.Lerp(cur, reaperTarget, Mathf.Min(1f, Time.deltaTime * 12f));
            sr.transform.position = new Vector3(next.x, next.y, sr.transform.position.z);
            if (sr.specRigidbody) sr.specRigidbody.Reinitialize();
        }

        static bool SpawnReaperPrefix() => !IsClient;

        static bool ReaperMotionPrefix(SuperReaperController __instance)
        {
            if (!IsClient) return true;
            if (__instance.specRigidbody) __instance.specRigidbody.Velocity = Vector2.zero;
            return false;
        }

        void SendSnapshot()
        {
            float now = Time.realtimeSinceStartup;
            snapSeq++;
            var batch = new List<KeyValuePair<uint, AIActor>>();
            foreach (var kv in NetIds.All)
            {
                var a = kv.Value as AIActor;
                if (a == null || a.healthHaver == null || a.healthHaver.IsDead) continue;
                Vector2 pos = a.transform.position;
                int clip = a.spriteAnimator != null && a.spriteAnimator.CurrentClip != null ? ClipHash(a.spriteAnimator.CurrentClip.name) : 0;
                byte flags = StateFlags(a, out byte angle);
                byte hp = (byte)Mathf.Clamp(Mathf.RoundToInt(a.healthHaver.GetCurrentHealthPercentage() * 255f), 0, 255);
                if (!lastSent.TryGetValue(kv.Key, out var s)) lastSent[kv.Key] = s = new Sent { At = -10 };
                bool changed = (pos - s.Pos).sqrMagnitude > 1e-4f || clip != s.Clip || flags != s.Flags || angle != s.Angle || hp != s.Hp;
                if (!changed && now - s.At < 0.5f) continue; // idle actors refresh twice a second
                s.Pos = pos; s.Clip = clip; s.Flags = flags; s.Angle = angle; s.Hp = hp; s.At = now;
                batch.Add(new KeyValuePair<uint, AIActor>(kv.Key, a));
            }
            const int perPacket = 48; // 20 to 21 bytes each, stays under one datagram
            for (int start = 0; start < batch.Count; start += perPacket)
            {
                int n = Mathf.Min(perPacket, batch.Count - start);
                w.Reset();
                w.U32(snapSeq);
                w.U8((byte)n);
                for (int i = start; i < start + n; i++)
                {
                    var a = batch[i].Value;
                    var s = lastSent[batch[i].Key];
                    var v = a.specRigidbody != null ? a.specRigidbody.Velocity : Vector2.zero;
                    w.U32(batch[i].Key);
                    w.F32(s.Pos.x); w.F32(s.Pos.y);
                    w.I16((short)Mathf.Clamp(Mathf.RoundToInt(v.x * 64f), short.MinValue, short.MaxValue));
                    w.I16((short)Mathf.Clamp(Mathf.RoundToInt(v.y * 64f), short.MinValue, short.MaxValue));
                    w.U16((ushort)s.Clip);
                    w.U8(s.Flags);
                    w.U8(s.Hp);
                    if ((s.Flags & FlagGun) != 0) w.U8(s.Angle);
                }
                var msg = w.ToArray();
                foreach (var c in ready) c.Send((byte)Channel.World, msg, Delivery.Unreliable);
            }
        }

        const byte FlagFlip = 1, FlagEngaged = 2, FlagHidden = 4, FlagGun = 8, FlagLaser = 16, FlagGreen = 32;

        /// <summary>Host: what the enemy's own AI decides to show, which its copies (AI off) cannot work out.
        /// Enemies that appear mid-fight (split slimes, ghosts, summons) stay hidden until engaged; snipers show a
        /// laser line before they fire.</summary>
        static byte StateFlags(AIActor a, out byte angle)
        {
            angle = 0;
            byte f = 0;
            if (a.sprite != null && a.sprite.FlipX) f |= FlagFlip;
            if (a.HasBeenEngaged) f |= FlagEngaged;
            if (a.sprite != null && a.sprite.renderer != null && !a.sprite.renderer.enabled) f |= FlagHidden;
            var gun = a.aiShooter != null ? a.aiShooter.CurrentGun : null;
            if (gun != null)
            {
                f |= FlagGun;
                angle = (byte)(Mathf.RoundToInt(Mathf.Repeat(a.aiShooter.GunAngle, 360f) / 360f * 256f) & 0xFF);
                var laser = gun.LaserSight;
                if (laser != null && laser.renderer != null && laser.renderer.enabled)
                {
                    f |= FlagLaser;
                    if (gun.LaserSightIsGreen) f |= FlagGreen;
                }
            }
            return f;
        }

        /// <summary>Client: show a copy the way the host's enemy is shown.</summary>
        static readonly bool flagsOff = Dev.DevEnv.IsHarness && System.Environment.GetEnvironmentVariable("GO_REPLICA_FLAGS") == "off"; // tests: the old behaviour

        static void ApplyFlags(Replica rep, byte flags, byte angle)
        {
            var a = rep.Actor;
            if (a.sprite != null) a.sprite.FlipX = (flags & FlagFlip) != 0;
            if (flagsOff) return;
            try
            {
                // Engaging runs the enemy's appear step (invisible-until-awake enemies switch their sprites on).
                // A boss engages here only for someone in its room: engaging also shows its health bar.
                if ((flags & FlagEngaged) != 0 && !a.HasBeenEngaged &&
                    (a.healthHaver == null || !a.healthHaver.IsBoss || (Game.PlayerSlots.Local && Game.PlayerSlots.Local.CurrentRoom == a.ParentRoom)))
                    a.HasBeenEngaged = true;
                bool hidden = (flags & FlagHidden) != 0;
                if (a.sprite != null && a.sprite.renderer != null && a.sprite.renderer.enabled == hidden) a.ToggleRenderers(!hidden);
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"WorldSync: could not show {a.name}: {e.Message}"); }
            rep.HasGun = (flags & FlagGun) != 0;
            rep.Laser = (flags & FlagLaser) != 0;
            rep.GunAngle = angle * (360f / 256f);
            if (rep.Laser && a.aiShooter != null && a.aiShooter.CurrentGun != null && !a.aiShooter.CurrentGun.ForceLaserSight)
            {
                a.aiShooter.CurrentGun.LaserSightIsGreen = (flags & FlagGreen) != 0;
                a.aiShooter.CurrentGun.ForceLaserSight = true;
            }
        }

        /// <summary>Client: point a copy's gun where the host's points, and draw the laser line while the host's is lit.</summary>
        static void AimReplica(Replica rep)
        {
            var a = rep.Actor;
            var sh = a.aiShooter;
            if (!rep.HasGun || sh == null || sh.CurrentGun == null) return;
            var gun = sh.CurrentGun;
            if (gun.LaserSight != null && gun.LaserSight.renderer != null) gun.LaserSight.renderer.enabled = rep.Laser;
            sh.AimAtPoint(a.CenterPosition + BraveMathCollege.DegreesToVector(rep.GunAngle, 6f));
        }

        static int ClipHash(string name)
        {
            uint h = 2166136261;
            for (int i = 0; i < name.Length; i++) { h ^= name[i]; h *= 16777619; }
            return (int)(h & 0xFFFF);
        }

        /// <summary>Host: send every live actor and sealed room again (repair after a detected desync).</summary>
        public void ResendWorld(Connection c)
        {
            foreach (var kv in NetIds.All)
                if (kv.Value is GameObject g && g)
                    foreach (var part in g.GetComponentsInChildren<AIActor>())
                        if (part && NetIds.Has(part)) { c.Send((byte)Channel.Event, GroupMessage(kv.Key, g, part), Delivery.ReliableOrdered); break; }
            foreach (var kv in NetIds.All)
                if (kv.Value is AIActor a && a && a.healthHaver && !a.healthHaver.IsDead)
                    c.Send((byte)Channel.Event, SpawnMessage(kv.Key, a), Delivery.ReliableOrdered);
            foreach (var room in GameManager.Instance.Dungeon.data.rooms)
            {
                w.Reset(); w.U8(EvRoomSeal); w.I16((short)RoomIndex(room)); w.Bool(room.IsSealed);
                c.Send((byte)Channel.Event, w.ToArray(), Delivery.ReliableOrdered);
            }
        }

        /// <summary>Client: remove replicas the host no longer has.</summary>
        public void KeepOnly(List<uint> alive)
        {
            var keep = new HashSet<uint>(alive);
            foreach (var id in new List<uint>(replicas.Keys))
            {
                if (keep.Contains(id)) continue;
                var rep = replicas[id];
                ApplyingHost = true;
                try { if (rep.Actor) rep.Actor.EraseFromExistence(true); }
                finally { ApplyingHost = false; }
                replicas.Remove(id);
                NetIds.Forget(id);
            }
        }

        // ---------------- Client ----------------

        /// <summary>Client: called by RunSync once a floor finished generating.</summary>
        /// <summary>Client: after a reconnect on the same floor, ask the host for everything again.</summary>
        public void ResendReady()
        {
            if (!IsClient || !FloorReady) return;
            lastSnapSeq = 0; // the host may have restarted its numbering
            w.Reset(); w.U8(EvFloorReady); w.I32(GameManager.Instance.CurrentRunSeed);
            NetSession.Instance.Node.Broadcast((byte)Channel.Event, w.ToArray(), Delivery.ReliableOrdered);
        }

        public void OnFloorLoaded()
        {
            if (!IsClient) return;
            replicas.Clear(); reaperWanted = false;
            NetIds.Clear();
            int removed = 0;
            foreach (var a in new List<AIActor>(StaticReferenceManager.AllEnemies))
            {
                if (a == null || a.GetComponent<PlayerController>() != null) continue;
                if (a.CompanionOwner != null) continue; // owned companions are per machine; unowned critters replicate
                if (IsTemplate(a)) continue;
                RememberModEnemy(a);
                // Our own copy of a grouped boss (Kill Pillars): the host sends its group; drop ours whole.
                var ownGroup = GroupOf(a, out _);
                if (ownGroup != null) Destroy(ownGroup);
                erasing = true;
                // One enemy's own death code failing (a boss door mimic did) must not stop the rest: this floor
                // would never be reported ready, and the host would send it again.
                try { a.EraseFromExistence(true); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"WorldSync: could not remove {a.name}: {e.Message}"); }
                finally { erasing = false; }
                removed++;
            }
            // A kill cam left over from an earlier floor would keep the player frozen ("bossKillCam" input lock).
            foreach (var cam in Object.FindObjectsOfType<BossKillCam>()) if (cam) cam.ForceCancelSequence();
            FloorReady = true;
            w.Reset(); w.U8(EvFloorReady); w.I32(GameManager.Instance.CurrentRunSeed);
            NetSession.Instance.Node.Broadcast((byte)Channel.Event, w.ToArray(), Delivery.ReliableOrdered);
            Plugin.Log.LogInfo($"WorldSync: client removed {removed} local enemies, ready");
            foreach (var e in earlyEvents) ApplyEvent(e);
            earlyEvents.Clear();
        }

        public void OnFloorUnloading()
        {
            FloorReady = false;
            PlayerSync.Instance?.ResetTracks();
            ProjectileSync.Instance?.OnFloorChanged();
            LookSync.Instance?.OnFloorChanged();
            Game.SecondFightLock.Reset();
            PickupSync.Instance?.OnFloorChanged();
            ShopSync.Instance?.OnFloorChanged();
            PropSync.Instance?.OnFloorChanged();
            replicas.Clear();
            if (IsHostSession) { ready.Clear(); lastSent.Clear(); deathSent.Clear(); NetIds.Clear(); jammedSent.Clear(); reaperSent = false; }
        }

        void OnEvent(Connection c, byte[] data, Delivery d)
        {
            if (data.Length == 0) return;
            if (data[0] >= 80 && data[0] < 90)
            {
                if (!NetSession.IsHost && !FloorReady) { earlyEvents.Add(data); return; }
                var qr = new WireReader(data, 1, data.Length - 1);
                PropSync.Instance?.Apply(data[0], ref qr);
                return;
            }
            if (data[0] >= 70 && data[0] < 80)
            {
                if (!NetSession.IsHost && !FloorReady) return; // summaries before the floor is ready are meaningless
                var cr = new WireReader(data, 1, data.Length - 1);
                try { Checksum.Instance?.Apply(c, data[0], ref cr); }
                catch (System.Exception e) { Plugin.Log.LogError("Checksum: " + e); }
                return;
            }
            if (data[0] >= 60 && data[0] < 70)
            {
                if (!NetSession.IsHost && !FloorReady) { earlyEvents.Add(data); return; }
                var sr = new WireReader(data, 1, data.Length - 1);
                ShopSync.Instance?.Apply(c, data[0], ref sr);
                return;
            }
            if ((data[0] >= 50 && data[0] < 60) || (data[0] >= 90 && data[0] < 100))
            {
                if (!NetSession.IsHost && !FloorReady) { earlyEvents.Add(data); return; }
                var pr = new WireReader(data, 1, data.Length - 1);
                PickupSync.Instance?.Apply(c, data[0], ref pr);
                return;
            }
            if (data[0] >= 40 && data[0] < 50)
            {
                var vr = new WireReader(data, 1, data.Length - 1);
                try
                {
                    if (data[0] == 45) { if (!NetSession.IsHost && !FloorReady) { earlyEvents.Add(data); return; } InventorySync.Instance?.Apply(ref vr); }
                    else if (data[0] == Game.MapPing.EvPing) Game.MapPing.Apply(c, ref vr);
                    else if (data[0] == Game.MapPing.EvMapOpen) Game.MapPing.ApplyMapOpen(c, ref vr);
                    else VitalsSync.Instance?.Apply(c, data[0], ref vr);
                }
                catch (System.Exception e) { Plugin.Log.LogError("Vitals: " + e); }
                return;
            }
            if (NetSession.IsHost)
            {
                if (data[0] == EvFloorReady)
                {
                    ready.Add(c);
                    // Send the whole current world to the newcomer. Groups first (Kill Pillars' statues): their
                    // parts are named by their place inside the group and are lost if the group is not there yet.
                    foreach (var kv in NetIds.All)
                        if (kv.Value is GameObject g && g)
                            foreach (var part in g.GetComponentsInChildren<AIActor>(true))
                                if (part && NetIds.Has(part)) { c.Send((byte)Channel.Event, GroupMessage(kv.Key, g, part), Delivery.ReliableOrdered); break; }
                    foreach (var kv in NetIds.All)
                        if (kv.Value is AIActor a && a && !a.healthHaver.IsDead)
                            c.Send((byte)Channel.Event, SpawnMessage(kv.Key, a), Delivery.ReliableOrdered);
                    var dungeon = GameManager.Instance.Dungeon; // none yet on the title screen
                    if (dungeon != null && dungeon.data != null) foreach (var room in dungeon.data.rooms)
                        if (room.IsSealed) { w.Reset(); w.U8(EvRoomSeal); w.I16((short)RoomIndex(room)); w.Bool(true); c.Send((byte)Channel.Event, w.ToArray(), Delivery.ReliableOrdered); }
                    PickupSync.Instance?.SendAllTo(c);
                    ShopSync.Instance?.SendAllTo(c);
                    ProjectileSync.Instance?.SendAllTo(c);
                    InventorySync.Instance?.ForceResend();
                    VitalsSync.Instance?.ForceResend();
                    VitalsSync.Instance?.SendDeathsTo(c);
                    Game.RunSync.Instance?.SendLayout(c);
                    Plugin.Log.LogInfo($"WorldSync: slot {c.Slot} ready");
                }
                return;
            }
            if (!FloorReady) { earlyEvents.Add(data); return; }
            ApplyEvent(data);
        }

        void ApplyEvent(byte[] data)
        {
            var r = new WireReader(data, 0, data.Length);
            byte type = r.U8();
            ApplyingHost = true;
            try
            {
                if (type >= 20 && type < 30) { ProjectileSync.Instance?.Apply(type, ref r); return; }
                if (type >= 30 && type < 40) { ApplyingHost = false; LookSync.Instance?.Apply(type, ref r); return; }
                if (type >= 60 && type < 70) { ApplyingHost = false; var sr = new WireReader(data, 1, data.Length - 1); ShopSync.Instance?.Apply(null, type, ref sr); return; }
                if (type >= 80 && type < 90) { ApplyingHost = false; var qr = new WireReader(data, 1, data.Length - 1); PropSync.Instance?.Apply(type, ref qr); return; }
                if (type == 45) { var ir = new WireReader(data, 1, data.Length - 1); InventorySync.Instance?.Apply(ref ir); return; }
                if ((type >= 50 && type < 60) || (type >= 90 && type < 100)) { ApplyingHost = false; var pr = new WireReader(data, 1, data.Length - 1); PickupSync.Instance?.Apply(null, type, ref pr); return; }
                switch (type)
                {
                    case EvSpawn: ApplySpawn(ref r); break;
                    case EvSpawnPart: ApplySpawnPart(ref r); break;
                    case EvSpawnGroup: ApplySpawnGroup(ref r); break;
                    case EvDespawn:
                        {
                            uint id = r.U32();
                            if (replicas.TryGetValue(id, out var rep) && rep.Actor) rep.Actor.EraseFromExistence(true);
                            else { var gone = NetIds.Get<GameObject>(id); if (gone) Destroy(gone); }
                            replicas.Remove(id); NetIds.Forget(id);
                            break;
                        }
                    case EvDeath:
                        {
                            uint id = r.U32();
                            var dir = new Vector2(r.F32(), r.F32());
                            if (replicas.TryGetValue(id, out var rep) && rep.Actor && rep.Actor.healthHaver && !rep.Actor.healthHaver.IsDead)
                            {
                                rep.Actor.healthHaver.ForceSetCurrentHealth(0f);
                                rep.Actor.healthHaver.Die(dir);
                                DeathsApplied++;
                            }
                            else
                                Plugin.Log.LogWarning($"WorldSync: death for {id} not applied (known={replicas.ContainsKey(id)} alive={(replicas.TryGetValue(id, out var rr) && rr.Actor && rr.Actor.healthHaver && !rr.Actor.healthHaver.IsDead)})");
                            replicas.Remove(id);
                            break;
                        }
                    case EvDamage:
                        {
                            uint id = r.U32();
                            float dmg = r.F32();
                            var dir = new Vector2(r.F32(), r.F32());
                            if (replicas.TryGetValue(id, out var rep) && rep.Actor && rep.Actor.healthHaver && !rep.Actor.healthHaver.IsDead)
                            {
                                // Visual hit (flash, knockback); health itself follows the host's snapshots, and death only by event.
                                float before = rep.Actor.healthHaver.GetCurrentHealth();
                                rep.Actor.healthHaver.ApplyDamage(Mathf.Min(dmg, Mathf.Max(0f, before - 0.01f)), dir, "host", CoreDamageTypes.None, DamageCategory.Normal, true);
                            }
                            break;
                        }
                    case EvJammed:
                        {
                            uint id = r.U32();
                            bool j = r.Bool();
                            if (replicas.TryGetValue(id, out var rep)) { rep.Jammed = j; SetJammed(rep.Actor, j); }
                            break;
                        }
                    case EvReaper:
                        {
                            bool present = r.Bool();
                            ApplyReaper(present, new Vector2(r.F32(), r.F32()));
                            break;
                        }
                    case EvRoomSeal:
                        {
                            var room = RoomAt(r.I16());
                            bool s = r.Bool();
                            if (room != null)
                            {
                                applyingSeal = true;
                                try { if (s) room.SealRoom(); else room.UnsealRoom(); }
                                finally { applyingSeal = false; }
                            }
                            break;
                        }
                }
            }
            catch (System.Exception e) { Plugin.Log.LogError("WorldSync event " + type + ": " + e); }
            finally { ApplyingHost = false; }
        }

        void ApplySpawn(ref WireReader r)
        {
            uint id = r.U32();
            string guid = r.Str();
            var pos = new Vector2(r.F32(), r.F32());
            var room = RoomAt(r.I16()) ?? pos.GetAbsoluteRoom();
            bool jammed = r.Remaining > 0 && r.Bool();
            if (replicas.ContainsKey(id)) return;
            if (Dev.DevEnv.IsHarness) Plugin.Log.LogInfo($"SPAWNRECV {id} {guid} t={Time.realtimeSinceStartup:F2}");
            var prefab = EnemyPrefab(guid);
            if (prefab == null) { SpawnFailures++; Plugin.Log.LogWarning("WorldSync: unknown enemy " + guid); return; }
            ProjectileSync.LearnBank(prefab);
            var a = AIActor.Spawn(prefab, pos, room, false, AIActor.AwakenAnimationType.Default, false);
            if (a == null) { SpawnFailures++; Plugin.Log.LogWarning($"WorldSync: spawn of {id} ({guid}) failed"); return; }
            MakeReplica(a);
            NetIds.Bind(id, a);
            if (Dev.DevEnv.IsHarness) Plugin.Log.LogInfo($"SPAWNAPPLY {id} t={Time.realtimeSinceStartup:F2}");
            replicas[id] = new Replica { Actor = a, Target = pos, TargetTime = Time.realtimeSinceStartup, Jammed = jammed };
            SetJammed(a, jammed);
            SpawnsApplied++;
            if (Dev.DevEnv.IsHarness && modEnemyPrefabs.ContainsKey(guid))
                Plugin.Log.LogInfo($"MODREPLICA {guid} active={a.gameObject.activeInHierarchy} drawn={(a.sprite && a.sprite.renderer && a.sprite.renderer.enabled)}");
        }

        void ApplySpawnPart(ref WireReader r)
        {
            uint id = r.U32(), ownerId = r.U32();
            string guid = r.Str();
            int n = r.U8();
            Transform t = null;
            if (replicas.TryGetValue(ownerId, out var o) && o.Actor) t = o.Actor.transform;
            else { var g = NetIds.Get<GameObject>(ownerId); if (g) t = g.transform; }
            for (int i = 0; i < n; i++)
            {
                int k = r.U8();
                t = t != null && k < t.childCount ? t.GetChild(k) : null;
            }
            if (replicas.ContainsKey(id)) return;
            var a = t != null ? t.GetComponent<AIActor>() : null;
            if (a == null || a.EnemyGuid != guid)
            {
                SpawnFailures++;
                Plugin.Log.LogWarning($"WorldSync: part {id} ({guid}) of {ownerId} not found");
                return;
            }
            MakeReplica(a);
            NetIds.Bind(id, a);
            replicas[id] = new Replica { Actor = a, Target = a.transform.position, TargetTime = Time.realtimeSinceStartup };
            SpawnsApplied++;
            if (Dev.DevEnv.IsHarness) Plugin.Log.LogInfo($"SPAWNPART {id} {a.name} of {ownerId}");
        }

        void ApplySpawnGroup(ref WireReader r)
        {
            uint id = r.U32();
            string guid = r.Str();
            var pos = new Vector3(r.F32(), r.F32(), r.F32());
            var room = RoomAt(r.I16()) ?? ((Vector2)pos).GetAbsoluteRoom();
            if (NetIds.Get<GameObject>(id)) return;
            var entry = EnemyDatabase.GetOrLoadByGuid(guid) as AIActorDummy;
            if (entry == null || entry.realPrefab == null || room == null) { SpawnFailures++; Plugin.Log.LogWarning("WorldSync: group " + guid + " unknown"); return; }
            var cell = ((Vector2)pos).ToIntVector2(VectorConversions.Floor) - room.area.basePosition;
            var go = DungeonPlaceableUtility.InstantiateDungeonPlaceable(entry.gameObject, room, cell, false);
            if (go == null) { SpawnFailures++; Plugin.Log.LogWarning("WorldSync: group " + guid + " failed"); return; }
            go.transform.position = pos;
            NetIds.Bind(id, go);
            // Its enemies start next frame and stay (their group has a network id); the host names each one.
            foreach (var part in go.GetComponentsInChildren<AIActor>(true)) MakeReplica(part);
            if (Dev.DevEnv.IsHarness) Plugin.Log.LogInfo($"SPAWNGROUP {id} {go.name} applied");
        }

        static readonly Dictionary<string, AIActor> modEnemyPrefabs = new Dictionary<string, AIActor>();
        static GameObject modTemplateRoot;

        /// <summary>Content mods keep their enemy and boss templates alive outside the level, in the scene kept across
        /// loads. Those are not enemies on this floor: erasing them breaks the mod's enemy lookups
        /// and every later floor fails to generate.</summary>
        static bool IsTemplate(AIActor a)
        {
            var go = a.gameObject;
            if (!go.scene.IsValid() || go.scene.name == "DontDestroyOnLoad") return true;
            if (modTemplateRoot && go.transform.IsChildOf(modTemplateRoot.transform)) return true;
            return false;
        }

        static void RememberModEnemy(AIActor actor)
        {
            string guid = actor.EnemyGuid;
            if (string.IsNullOrEmpty(guid) || modEnemyPrefabs.TryGetValue(guid, out var have) && have || EnemyDatabase.GetOrLoadByGuid(guid)) return;
            // Runtime-built enemies may have no asset prefab at all. Keep a dormant template before removing
            // the guest's generated actors, so the host can ask for that exact enemy later on.
            if (!modTemplateRoot)
            {
                modTemplateRoot = new GameObject("Gungeon Online enemy templates");
                modTemplateRoot.SetActive(false);
                DontDestroyOnLoad(modTemplateRoot);
            }
            var go = Instantiate(actor.gameObject, modTemplateRoot.transform, false);
            go.name = actor.name + " template";
            go.SetActive(true); // the parent stays inactive; spawning a copy later activates it normally
            var prefab = go.GetComponent<AIActor>();
            StaticReferenceManager.AllEnemies.Remove(prefab);
            modEnemyPrefabs[guid] = prefab;
            Plugin.Log.LogInfo("WorldSync: preserved mod enemy " + guid + " (" + actor.name + ")");
        }

        static AIActor EnemyPrefab(string guid)
        {
            var native = EnemyDatabase.GetOrLoadByGuid(guid);
            if (native) return native;
            if (modEnemyPrefabs.TryGetValue(guid, out var cached) && cached) return cached;
            // Some content mods retain inactive prefabs without registering them in the native catalogue.
            // Their runtime actors still have stable GUIDs, and the same loaded prefab exists on each PC.
            AIActor fallback = null;
            foreach (var a in Resources.FindObjectsOfTypeAll<AIActor>())
            {
                if (!a || a.EnemyGuid != guid) continue;
                if (!a.gameObject.scene.IsValid()) { fallback = a; break; }
                if (!a.gameObject.activeSelf && a.ParentRoom == null) fallback = a;
            }
            if (fallback)
            {
                modEnemyPrefabs[guid] = fallback;
                Plugin.Log.LogInfo("WorldSync: loaded mod enemy " + guid + " (" + fallback.name + ")");
            }
            return fallback;
        }

        static void SetJammed(AIActor a, bool jammed)
        {
            if (!a || a.IsBlackPhantom == jammed) return;
            if (jammed) a.BecomeBlackPhantom(); else a.UnbecomeBlackPhantom();
        }

        static void MakeReplica(AIActor a)
        {
            a.PreventBlackPhantom = true; // this PC's own curse roll never decides; the host's state does
            var companion = a.GetComponent<CompanionController>();
            if (companion) companion.enabled = false;
            if (a.behaviorSpeculator) a.behaviorSpeculator.enabled = false;
            if (a.aiAnimator) a.aiAnimator.enabled = false;
            if (a.aiShooter) a.aiShooter.enabled = false;
            // Goop comes from the host (LookSync); the copy's own goopers follow other rules here (the Gorgun's is
            // switched on and off by her AI).
            foreach (var g in a.GetComponents<GoopDoer>()) if (g) g.enabled = false;
            a.CanDropCurrency = false;
            a.CanDropItems = false;
        }

        void OnWorld(Connection c, byte[] data, Delivery d)
        {
            if (NetSession.IsHost || !FloorReady) return;
            var r = new WireReader(data, 0, data.Length);
            uint seq = r.U32();
            if (seq + 1000 < lastSnapSeq) lastSnapSeq = 0; // new floor restarted numbering
            bool stale = seq < lastSnapSeq;
            if (seq > lastSnapSeq) lastSnapSeq = seq;
            int n = r.U8();
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < n; i++)
            {
                uint id = r.U32();
                var pos = new Vector2(r.F32(), r.F32());
                var vel = new Vector2(r.I16() / 64f, r.I16() / 64f);
                int clip = r.U16();
                byte flags = r.U8();
                byte hp = r.U8();
                byte angle = (flags & FlagGun) != 0 ? r.U8() : (byte)0;
                if (stale || !replicas.TryGetValue(id, out var rep) || !rep.Actor) continue;
                rep.OldTarget = rep.Target; rep.OldVelocity = rep.Velocity; rep.OldTime = rep.TargetTime;
                rep.Target = pos; rep.Velocity = vel; rep.TargetTime = now;
                var a = rep.Actor;
                // Play the host's clip when it changes, and again whenever something here played another one over it
                // (a hit flash, the end of an intro).
                var cur = a.spriteAnimator != null ? a.spriteAnimator.CurrentClip : null;
                if (clip != 0 && (clip != rep.Clip || cur == null || ClipHash(cur.name) != clip) && a.spriteAnimator != null && a.spriteAnimator.Library != null)
                {
                    foreach (var cl in a.spriteAnimator.Library.clips)
                        if (cl != null && cl.name != null && ClipHash(cl.name) == clip) { a.spriteAnimator.Play(cl); break; }
                    rep.Clip = clip;
                }
                ApplyFlags(rep, flags, angle);
                if (a.healthHaver != null && !a.healthHaver.IsDead)
                    a.healthHaver.ForceSetCurrentHealth(Mathf.Max(0.01f, a.healthHaver.GetMaxHealth() * hp / 255f));
            }
            SnapshotsApplied++;
        }

        void ClientMoveReplicas()
        {
            float now = Time.realtimeSinceStartup;
            foreach (var kv in replicas)
            {
                var rep = kv.Value;
                var a = rep.Actor;
                if (!a || a.healthHaver == null || a.healthHaver.IsDead) continue;
                if (a.IsBlackPhantom != rep.Jammed) SetJammed(a, rep.Jammed);
                // Boss intros, freezing and other effects switch these back on when they end; a copy's AI stays off
                // (its animator running left bosses frozen in one pose: the Dragun, the Gatling Gull).
                if (a.behaviorSpeculator && a.behaviorSpeculator.enabled) a.behaviorSpeculator.enabled = false;
                if (a.aiAnimator && a.aiAnimator.enabled) a.aiAnimator.enabled = false;
                if (a.aiShooter && a.aiShooter.enabled) a.aiShooter.enabled = false;
                try { AimReplica(rep); } catch (System.Exception) { rep.HasGun = false; }
                float age = Mathf.Min(0.1f, now - rep.TargetTime); // extrapolate at most 100 ms
                Vector2 want = rep.Target + rep.Velocity * age;
                Vector2 cur = a.transform.position;
                if ((want - cur).sqrMagnitude < 1e-6f) continue;
                // A new update: where the last one's guess would have drawn the enemy now, versus where it really is.
                if (rep.MovedFor >= 0f && rep.MovedFor != rep.TargetTime)
                    DrawSmoothing.Corrected(a, rep.OldTarget + rep.OldVelocity * Mathf.Min(0.1f, now - rep.OldTime), want);
                rep.MovedFor = rep.TargetTime;
                a.transform.position = new Vector3(want.x, want.y, a.transform.position.z);
                if (a.specRigidbody) a.specRigidbody.Reinitialize();
            }
        }

        void LateUpdate()
        {
            long t0 = Dev.NetProfiler.Begin();
            try { LateUpdateImpl(); }
            finally { Dev.NetProfiler.End("world", t0); }
        }

        void LateUpdateImpl()
        {
            if (!NetSession.Active) return;
            if (NetSession.IsHost) HostLateUpdate();
            else if (FloorReady) { ClientMoveReplicas(); ClientMoveReaper(); }
        }
    }
}
