using System;
using System.Collections;
using System.Collections.Generic;
using Dungeonator;
using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Replication;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>Opt-in integration checks in isolated game copies. Never active in ordinary play.</summary>
    internal sealed class ReleaseRegression : MonoBehaviour
    {
        static IEnumerator Wait(float seconds)
        {
            float end = Time.realtimeSinceStartup + seconds;
            while (Time.realtimeSinceStartup < end) yield return null;
        }

        static void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException("REGRESSION FAIL: " + message);
            Plugin.Log.LogInfo("REGRESSION PASS: " + message);
        }

        static void Kill(PlayerController p)
        {
            p.healthHaver.IsVulnerable = true;
            p.healthHaver.PreventAllDamage = false;
            p.healthHaver.Armor = 0f;
            p.healthHaver.ForceSetCurrentHealth(0.5f);
            p.healthHaver.ApplyDamage(1f, Vector2.zero, "regression", CoreDamageTypes.None, DamageCategory.Unstoppable, true);
        }

        IEnumerator Start()
        {
            string mode = Environment.GetEnvironmentVariable("GO_REGRESSION");
            if (!DevEnv.IsHarness || string.IsNullOrEmpty(mode)) yield break;
            if (mode == "talk") { yield return TalkTest(); yield break; }
            if (mode == "ask") { yield return AskTest(); yield break; }
            if (mode == "mapblock") { yield return MapBlockTest(); yield break; }
            while (!SceneObjects.OnDungeonFloor || PlayerSlots.Local == null ||
                (NetSession.IsHost ? WorldSync.Instance.ReadyPeers < DevEnv.ExpectPlayers - 1 : !WorldSync.Instance.FloorReady)) yield return null;
            yield return Wait(5f);
            var gm = GameManager.Instance;
            foreach (var p in Game.NPlayers.Players.All) p.healthHaver.IsVulnerable = false;
            if (mode == "rainbow")
                foreach (var item in PickupObjectDatabase.Instance.Objects) if (item) item.IgnoredByRat = true;
            if (mode == "enemyvis") { yield return EnemyVisibility(); yield break; }
            if (mode == "shop")
            {
                if (NetSession.IsHost) yield break;
                var items = Traverse.Create(ShopSync.Instance).Field("clientItems").GetValue<Dictionary<uint, ShopItemController>>();
                Check(items.Count > 0, "shop stock mapped");
                uint id = 0; ShopItemController mine = null;
                foreach (var kv in items) if (kv.Value && !kv.Value.Acquired) { id = kv.Key; mine = kv.Value; break; }
                Check(mine, "unsold shop item found");
                int instance = mine.GetInstanceID();
                for (int i = 0; i < 3; i++)
                {
                    int item = mine.item.PickupObjectId == 15 ? 38 : 15;
                    var w = new WireWriter(32);
                    w.U32(id); w.I32(item); w.I32(10);
                    Vector2 at = mine.transform.position;
                    w.F32(at.x); w.F32(at.y); w.Bool(false);
                    byte[] data = w.ToArray(); var r = new WireReader(data, 0, data.Length);
                    ShopSync.Instance.Apply(null, 60, ref r);
                    yield return null; yield return null;
                    Check(mine && mine.GetInstanceID() == instance && mine.item.PickupObjectId == item &&
                        mine.GetComponents<tk2dSprite>().Length == 1 && mine.sprite.renderer.enabled,
                        "restock " + i + " keeps one visible sprite and the live controller");
                }
                PlayerSlots.Local.WarpToPoint((Vector2)mine.transform.position + new Vector2(0f, -2f));
                yield break;
            }
            if (!NetSession.IsHost)
            {
                if (mode == "rainbow")
                {
                    yield return Wait(18f);
                    LogLoot();
                    yield break;
                }
                if (mode == "jammed")
                {
                    yield return Wait(12f);
                    int jammed = 0, total = 0;
                    foreach (var e in StaticReferenceManager.AllEnemies)
                        if (e && e.GetComponent<PlayerController>() == null) { total++; if (e.IsBlackPhantom) jammed++; }
                    Plugin.Log.LogInfo("REGRESSION jammed guest " + jammed + "/" + total);
                    Check(total > 0 && jammed == total, "guest shows every host-jammed enemy as jammed");
                    Check(SuperReaperController.Instance, "guest sees Lord of the Jammed");
                    yield return Wait(4f);
                    Vector2 at = SuperReaperController.Instance.transform.position;
                    Plugin.Log.LogInfo("REGRESSION reaper guest at " + at);
                    yield break;
                }
                if (mode == "boss-revive")
                {
                    Kill(PlayerSlots.Local);
                    // Revived as soon as the boss dies and its pedestal appears; checked while the session is still up.
                    float until = Time.realtimeSinceStartup + 70f;
                    while (Time.realtimeSinceStartup < until && (PlayerSlots.Local.IsGhost || PlayerSlots.Local.healthHaver.IsDead)) yield return null;
                    Check(!PlayerSlots.Local.IsGhost && !PlayerSlots.Local.healthHaver.IsDead, "boss pedestal revives guest");
                    yield break;
                }
                if (mode.StartsWith("reset"))
                {
                    Kill(PlayerSlots.Local);
                    if (mode == "reset-guest-menu")
                    {
                        var end = Time.realtimeSinceStartup + 25f;
                        while (!UnityEngine.Object.FindObjectOfType<AmmonomiconDeathPageController>() && Time.realtimeSinceStartup < end) yield return null;
                        var page = UnityEngine.Object.FindObjectOfType<AmmonomiconDeathPageController>();
                        Check(page, "guest death screen appears");
                        AccessTools.Method(typeof(AmmonomiconDeathPageController), "DoMainMenu").Invoke(page, new object[] { null, null });
                        yield return Wait(1f);
                        Check(!gm.IsLoadingLevel && AmmonomiconController.Instance.IsOpen, "guest reset keeps the death screen open while waiting for host");
                    }
                    yield break;
                }
                if (mode == "chest")
                {
                    Kill(PlayerSlots.Local);
                    yield return Wait(5f);
                    Check((bool)AccessTools.Field(typeof(Chest), "m_IsCoopMode").GetValue(null), "guest sees revive chests");
                    yield return Wait(8f);
                    Check(!PlayerSlots.Local.IsGhost && !PlayerSlots.Local.healthHaver.IsDead, "guest revived from rainbow chest");
                }
                else if (mode.StartsWith("clone"))
                {
                    var floor = gm.Dungeon;
                    yield return Wait(mode == "clone-guest" ? 5f : 10f);
                    Kill(PlayerSlots.Local);
                    yield return Wait(30f);
                    Check(gm.Dungeon != floor && RunSync.CurrentIndex == 1, "Clone brings guest to chamber one");
                    foreach (var p in Game.NPlayers.Players.All)
                        Check(!p.IsGhost && !p.healthHaver.IsDead, "Clone revives slot " + PlayerSlots.SlotOf(p) + " on guest");
                }
                yield break;
            }
            PlayerController guest = null;
            foreach (var kv in PlayerSlots.Remotes) if (kv.Value) { guest = kv.Value; break; }
            Check(guest, "guest exists");
            if (mode == "pulldoor")
            {
                // The host stands in a doorway of a room, like walking into a fight; the guest is pulled to the host.
                var d = gm.Dungeon.data;
                IntVector2? door = null;
                foreach (var room in d.rooms)
                {
                    if (room.area == null || room == gm.Dungeon.data.Entrance) continue;
                    var b0 = room.area.basePosition; var dim = room.area.dimensions;
                    for (int x = b0.x - 1; x <= b0.x + dim.x && door == null; x++)
                        for (int y = b0.y - 1; y <= b0.y + dim.y && door == null; y++)
                            if (d.CheckInBounds(x, y) && d.cellData[x][y] != null && d.cellData[x][y].isExitCell && d.cellData[x][y].type == CellType.FLOOR && d.cellData[x][y].parentRoom == room) door = new IntVector2(x, y);
                    if (door != null) break;
                }
                Check(door != null, "found a doorway");
                PlayerSlots.Local.WarpToPoint(door.Value.ToVector2());
                yield return Wait(1.5f);
                Plugin.Log.LogInfo("REGRESSION host in doorway at " + door.Value + " room=" + (PlayerSlots.Local.CurrentRoom != null ? PlayerSlots.Local.CurrentRoom.GetRoomName() : "none"));
                guest.ReuniteWithOtherPlayer(PlayerSlots.Local);
                yield return Wait(4f);
                yield break;
            }
            if (mode == "jammed")
            {
                int n = 0;
                foreach (var e in new List<AIActor>(StaticReferenceManager.AllEnemies))
                    if (e && e.GetComponent<PlayerController>() == null) { e.BecomeBlackPhantom(); n++; }
                Plugin.Log.LogInfo("REGRESSION jammed host " + n);
                gm.Dungeon.SpawnCurseReaper();
                yield return Wait(1f);
                Check(SuperReaperController.Instance, "host spawned Lord of the Jammed");
                yield return Wait(15f);
                if (SuperReaperController.Instance) Plugin.Log.LogInfo("REGRESSION reaper host at " + (Vector2)SuperReaperController.Instance.transform.position);
                yield break;
            }
            if (mode == "rainbow")
            {
                var room = gm.Dungeon.data.rooms[gm.Dungeon.data.rooms.Count - 1];
                Vector2 at = room.GetCenteredVisibleClearSpot(2, 2).ToVector2();
                var chest = Chest.Spawn(gm.RewardManager.Rainbow_Chest, new Vector3(at.x, at.y, 0f), room, true);
                yield return Wait(3f);
                chest.ForceOpen(PlayerSlots.Local);
                yield return Wait(10f);
                LogLoot();
                yield break;
            }
            if (mode.StartsWith("reset"))
            {
                yield return Wait(6f); Kill(PlayerSlots.Local);
                var end = Time.realtimeSinceStartup + 25f;
                while (!UnityEngine.Object.FindObjectOfType<AmmonomiconDeathPageController>() && Time.realtimeSinceStartup < end) yield return null;
                var page = UnityEngine.Object.FindObjectOfType<AmmonomiconDeathPageController>();
                Check(page, "death screen appears");
                yield return Wait(5f);
                AccessTools.Method(typeof(AmmonomiconDeathPageController), mode == "reset-quick" ? "DoQuickRestart" : "DoMainMenu")
                    .Invoke(page, new object[] { null, null });
                Plugin.Log.LogInfo("REGRESSION: pressed death screen " + mode);
                yield break;
            }
            if (mode.StartsWith("clone"))
            {
                var owner = mode == "clone-host" ? PlayerSlots.Local : guest;
                ExtraLifeItem clone = null;
                foreach (var item in PickupObjectDatabase.Instance.Objects)
                    if (item is ExtraLifeItem extra && extra.extraLifeMode == ExtraLifeItem.ExtraLifeMode.CLONE) { clone = extra; break; }
                Check(clone, "Clone prefab exists");
                LootEngine.GivePrefabToPlayer(clone.gameObject, owner);
                yield return Wait(mode == "clone-host" ? 3f : 10f);
                Kill(PlayerSlots.Local);
                yield return Wait(30f);
                Check(!gm.IsPaused && !gm.IsLoadingLevel && RunSync.CurrentIndex == 1, "Clone finishes its shared load");
                foreach (var p in Game.NPlayers.Players.All)
                    Check(!p.IsGhost && !p.healthHaver.IsDead, "Clone revives slot " + PlayerSlots.SlotOf(p) + " on host");
                yield break;
            }
            if (mode == "chest")
            {
                RoomHandler room = null;
                foreach (var r in gm.Dungeon.data.rooms)
                    if (r != PlayerSlots.Local.CurrentRoom && r.area != null && r.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.REWARD) { room = r; break; }
                if (room == null) room = gm.Dungeon.data.rooms[gm.Dungeon.data.rooms.Count - 1];
                Vector2 at = room.GetCenteredVisibleClearSpot(2, 2).ToVector2();
                var chest = Chest.Spawn(gm.RewardManager.Rainbow_Chest, new Vector3(at.x, at.y, 0f), room, true);
                yield return Wait(8f);
                Check(guest.IsGhost, "guest is a ghost before chest opens");
                chest.Interact(PlayerSlots.Local);
                yield return Wait(4f);
                Check(!guest.IsGhost && !guest.healthHaver.IsDead, "host revives guest from rainbow chest");
                yield break;
            }
            if (mode.StartsWith("door"))
            {
                NetConfig.CoopStyle.Value = 1;
                RoomHandler fight = null; DungeonDoorController door = null; RoomHandler outside = null;
                foreach (var room in gm.Dungeon.data.rooms)
                {
                    var enemies = room.GetActiveEnemies(RoomHandler.ActiveEnemyType.RoomClear);
                    if (room.area == null || room.area.PrototypeRoomCategory != PrototypeDungeonRoom.RoomCategory.NORMAL ||
                        enemies == null || enemies.Count == 0 || room.connectedDoors == null) continue;
                    foreach (var d in room.connectedDoors)
                        if (d && !d.isLocked && (d.Mode == DungeonDoorController.DungeonDoorMode.COMPLEX || d.Mode == DungeonDoorController.DungeonDoorMode.SINGLE_DOOR))
                        {
                            var other = d.upstreamRoom == room ? d.downstreamRoom : d.upstreamRoom;
                            if (other != null) { fight = room; door = d; outside = other; break; }
                        }
                    if (fight != null) break;
                }
                Check(door, "normal combat door found");
                var late = mode == "door-host" ? PlayerSlots.Local : guest;
                var fighter = late == guest ? PlayerSlots.Local : guest;
                late.WarpToPoint(outside.GetCenteredVisibleClearSpot(2, 2).ToVector2());
                fighter.WarpToPoint(fight.GetCenteredVisibleClearSpot(2, 2).ToVector2());
                yield return Wait(3f);
                if (!fight.IsSealed) fight.SealRoom();
                Check(late.CurrentRoom != fight, "teammate starts outside sealed fight");
                AccessTools.Method(typeof(DungeonDoorController), "CheckForPlayerCollision").Invoke(door, new object[] { late.specRigidbody, Vector2.up });
                yield return Wait(2f);
                Check(late.CurrentRoom == fight && fight.IsSealed, "late teammate enters while fight stays sealed");
            }
        }

        /// <summary>The guest talks to a Breach NPC that asks a question, then the host talks to another one.
        /// The host's own conversation must still end (a friend's conversation used to block it).</summary>
        IEnumerator TalkTest()
        {
            while (!SceneObjects.InBreach || PlayerSlots.Local == null || !PlayerSlots.Local.healthHaver || !AnyRemote()) yield return null;
            yield return Wait(8f);
            var npcs = new List<TalkDoerLite>();
            foreach (var td in UnityEngine.Object.FindObjectsOfType<TalkDoerLite>())
            {
                if (!td || !td.gameObject.activeInHierarchy) continue;
                int asks = 0;
                foreach (var fsm in td.GetComponents<PlayMakerFSM>())
                    foreach (var st in fsm.FsmStates)
                        foreach (var act in st.Actions)
                            if (act is HutongGames.PlayMaker.Actions.DialogueBox db && db.responses != null && db.responses.Length > 0) asks++;
                Plugin.Log.LogInfo($"TALK npc {td.name} at {td.transform.position} questions={asks}");
                if (asks > 0) npcs.Add(td);
            }
            npcs.Sort((x, y) => string.CompareOrdinal(x.name, y.name));
            if (NetSession.IsHost) npcs.Reverse();
            Check(npcs.Count >= 2, "two Breach NPCs that ask questions");
            if (NetSession.IsHost)
            {
                // The guest goes first. While their conversation runs here, our map must stay usable.
                float stop = Time.realtimeSinceStartup + 70f;
                int seen = 0, blocked = 0;
                while (Time.realtimeSinceStartup < stop)
                {
                    bool friendTalking = false;
                    foreach (var td in UnityEngine.Object.FindObjectsOfType<TalkDoerLite>())
                        if (td && td.State == TalkDoerLite.TalkingState.Conversation && PlayerSlots.IsRemote(td.TalkingPlayer)) { friendTalking = true; break; }
                    if (friendTalking)
                    {
                        seen++;
                        if (Minimap.HasInstance && Minimap.Instance && Minimap.Instance.TemporarilyPreventMinimap) blocked++;
                    }
                    yield return Wait(0.25f);
                }
                Plugin.Log.LogInfo($"TALK map while friend talks: samples={seen} blocked={blocked}");
                Check(seen > 0 && blocked == 0, "our map stays usable while the friend talks here");
            }
            var me = PlayerSlots.Local;
            int sentBefore = ConversationSync.AnswersSent;
            bool asked = false;
            foreach (var npc in npcs)
            {
                me.WarpToPoint((Vector2)npc.transform.position + new Vector2(0f, -1.5f));
                yield return Wait(0.5f);
                Plugin.Log.LogInfo("TALK " + (NetSession.IsHost ? "host" : "guest") + " talks to " + npc.name);
                npc.Interact(me);
                yield return Wait(1f);
                float end = Time.realtimeSinceStartup + 25f;
                while (Time.realtimeSinceStartup < end && (me.IsInputOverridden || npc.State == TalkDoerLite.TalkingState.Conversation)) yield return null;
                Check(!me.IsInputOverridden, "conversation with " + npc.name + " ended and the player can move");
                if (ConversationSync.AnswersSent > sentBefore || ConversationSync.LocalAnswered) { asked = true; break; }
                yield return Wait(1.5f);
            }
            Check(asked, "a conversation with a question was answered");
            Plugin.Log.LogInfo($"TALK done sent={ConversationSync.AnswersSent} used={ConversationSync.AnswersUsed}");
        }

        /// <summary>A map block left on with nothing on this screen holding it (a friend's conversation that never ended
        /// here) is lifted by the watchdog, and the map opens again.</summary>
        IEnumerator MapBlockTest()
        {
            while (!SceneObjects.OnDungeonFloor || PlayerSlots.Local == null || !AnyRemote() || !Minimap.HasInstance) yield return null;
            yield return Wait(15f);
            Minimap.Instance.TemporarilyPreventMinimap = true;
            Plugin.Log.LogInfo("MAPBLOCK left the map blocked");
            float end = Time.realtimeSinceStartup + 20f;
            while (Time.realtimeSinceStartup < end && Minimap.Instance.TemporarilyPreventMinimap) yield return null;
            Check(!Minimap.Instance.TemporarilyPreventMinimap, "stuck map block lifted after " + (20f - (end - Time.realtimeSinceStartup)).ToString("F1") + " s");
            Minimap.Instance.ToggleMinimap(true);
            yield return Wait(1f);
            Check(Minimap.Instance.IsFullscreen, "map opens again");
            Minimap.Instance.ToggleMinimap(false);
        }

        /// <summary>A friend's question open on this PC (their NPC or shrine runs here too) must not block our own
        /// question, and their answer must arrive from their PC.</summary>
        IEnumerator AskTest()
        {
            while (!SceneObjects.InBreach || PlayerSlots.Local == null || !AnyRemote()) yield return null;
            yield return Wait(6f);
            var ui = GameUIRoot.Instance;
            if (!NetSession.IsHost)
            {
                yield return Wait(4f);
                Check(ui.DisplayPlayerConversationOptions(PlayerSlots.Local, new[] { "Yes", "No" }), "guest's question opens");
                float end = Time.realtimeSinceStartup + 10f;
                int idx = -1;
                while (Time.realtimeSinceStartup < end && !ui.GetPlayerConversationResponse(out idx)) yield return null;
                Check(idx >= 0 && Time.realtimeSinceStartup < end, "guest answered " + idx);
                yield break;
            }
            PlayerController friend = null;
            foreach (var kv in PlayerSlots.Remotes) if (kv.Value) { friend = kv.Value; break; }
            ui.DisplayPlayerConversationOptions(friend, new[] { "Yes", "No" }); // the copy of the friend's question
            yield return Wait(2f);
            Check(ui.DisplayPlayerConversationOptions(PlayerSlots.Local, new[] { "A", "B" }), "host's own question opens while a friend's question is open");
            float until = Time.realtimeSinceStartup + 10f;
            int mine = -1;
            while (Time.realtimeSinceStartup < until && !ui.GetPlayerConversationResponse(out mine)) yield return null;
            Check(Time.realtimeSinceStartup < until, "host's own question answered (" + mine + ")");
            yield return Wait(1f);
            until = Time.realtimeSinceStartup + 15f;
            int theirs = -1;
            while (Time.realtimeSinceStartup < until && !ui.GetPlayerConversationResponse(out theirs)) yield return null;
            Check(Time.realtimeSinceStartup < until && ConversationSync.AnswersUsed > 0, "friend's answer arrived from their PC (" + theirs + ")");
        }

        static bool AnyRemote()
        {
            foreach (var kv in PlayerSlots.Remotes) if (kv.Value) return true;
            return false;
        }

        /// <summary>Enemies whose own AI decides what is shown: split slimes, ghosts, sniper lasers, Kill Pillars.
        /// The host spawns them next to the players; both sides log what each enemy shows, for comparison.</summary>
        IEnumerator EnemyVisibility()
        {
            var spawned = new List<AIActor>();
            if (NetSession.IsHost)
            {
                var names = new List<string>();
                foreach (var e in EnemyDatabase.Instance.Entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.path)) continue;
                    string n = e.name.ToLowerInvariant();
                    if (n.Contains("blob") || n.Contains("hollowpoint") || n.Contains("sniper") || n.Contains("statue") || n.Contains("ghost") || n.Contains("spectre"))
                        names.Add(e.name + "=" + e.myGuid);
                }
                Plugin.Log.LogInfo("ENEMYVIS candidates " + string.Join(", ", names.ToArray()));
                var all = new List<string>();
                foreach (var e in EnemyDatabase.Instance.Entries) if (e != null && !string.IsNullOrEmpty(e.path)) all.Add(e.name);
                Plugin.Log.LogInfo("ENEMYVIS all " + string.Join(",", all.ToArray()));
                var want = (Environment.GetEnvironmentVariable("GO_VIS_ENEMIES") ?? "blobulon,ghost,spectre,bulletrifleman,bossstatuesdummy").Split(',');
                var room = PlayerSlots.Local.CurrentRoom;
                Vector2 at = PlayerSlots.Local.CenterPosition;
                int k = 0;
                foreach (var w in want)
                {
                    AIActor prefab = null;
                    foreach (var e in EnemyDatabase.Instance.Entries)
                        if (e != null && !string.IsNullOrEmpty(e.path) && e.name.ToLowerInvariant() == w) { prefab = EnemyDatabase.GetOrLoadByGuid(e.myGuid); break; }
                    if (prefab == null) { Plugin.Log.LogWarning("ENEMYVIS no enemy named " + w); continue; }
                    var spot = at + new Vector2(-6f + 4f * k++, 4f);
                    if (prefab is AIActorDummy dummy && dummy.realPrefab && dummy.realPrefab.GetComponent<AIActor>() == null)
                    {
                        // A group such as Kill Pillars: placed the way the game places it, not spawned as one enemy.
                        var go = DungeonPlaceableUtility.InstantiateDungeonPlaceable(prefab.gameObject, room, spot.ToIntVector2(VectorConversions.Floor) - room.area.basePosition, false, AIActor.AwakenAnimationType.Default, true);
                        foreach (var part in go.GetComponentsInChildren<AIActor>(true)) { spawned.Add(part); part.HasBeenEngaged = true; }
                        Plugin.Log.LogInfo("ENEMYVIS placed group " + w + " " + go.name + " parts=" + go.GetComponentsInChildren<AIActor>(true).Length);
                        continue;
                    }
                    var a = AIActor.Spawn(prefab, spot, room, true, AIActor.AwakenAnimationType.Spawn, true);
                    if (a) { spawned.Add(a); Plugin.Log.LogInfo("ENEMYVIS spawned " + w + " " + a.EnemyGuid); }
                    else Plugin.Log.LogWarning("ENEMYVIS spawn failed " + w);
                }
            }
            float end = Time.realtimeSinceStartup + 36f, splitAt = Time.realtimeSinceStartup + 6f, nextLog = 0f;
            bool split = false;
            while (Time.realtimeSinceStartup < end)
            {
                if (NetSession.IsHost && !split && Time.realtimeSinceStartup >= splitAt)
                {
                    split = true;
                    foreach (var a in spawned)
                        if (a && a.name.ToLowerInvariant().Contains("blob") && a.healthHaver && !a.healthHaver.IsDead)
                        {
                            a.healthHaver.ApplyDamage(10000f, Vector2.up, "regression", CoreDamageTypes.None, DamageCategory.Unstoppable, true);
                            Plugin.Log.LogInfo("ENEMYVIS killed " + a.name + " to split it");
                        }
                }
                if (Time.realtimeSinceStartup >= nextLog)
                {
                    nextLog = Time.realtimeSinceStartup + 0.5f;
                    var sb = new System.Text.StringBuilder("ENEMYVIS t=" + Time.realtimeSinceStartup.ToString("F1"));
                    foreach (var kv in NetIds.All)
                    {
                        var a = kv.Value as AIActor;
                        if (!a || a.healthHaver == null || a.healthHaver.IsDead) continue;
                        bool drawn = a.sprite && a.sprite.renderer && a.sprite.renderer.enabled;
                        var gun = a.aiShooter ? a.aiShooter.CurrentGun : null;
                        bool laser = gun && gun.LaserSight && gun.LaserSight.renderer && gun.LaserSight.renderer.enabled;
                        sb.Append(" | ").Append(kv.Key).Append(' ').Append(a.name.Replace(" ", "_").Replace("(Clone)", ""))
                          .Append(drawn ? " D" : " h").Append(a.HasBeenEngaged ? "E" : "e").Append(laser ? "L" : "-")
                          .Append(gun ? " a" + Mathf.RoundToInt(a.aiShooter.GunAngle) : "");
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                }
                yield return null;
            }
        }

        static void LogLoot()
        {
            int count = 0;
            foreach (var d in StaticReferenceManager.AllDebris)
            {
                if (!d) continue;
                var p = d.GetComponentInChildren<PickupObject>();
                if (!p || p.IsBeingSold || p.PickupObjectId < 0 || (p is Gun gun && gun.CurrentOwner != null)) continue;
                count++;
                Vector2 at = p.transform.position;
                Plugin.Log.LogInfo(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "REGRESSION LOOT: {0} {1:F2} {2:F2}", p.PickupObjectId, at.x, at.y));
            }
            Check(count >= 8, "rainbow chest has at least eight ground items");
        }
    }
}
