using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Dungeonator;
using GungeonOnline.Game;
using GungeonOnline.Replication;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>
    /// Progress on the current floor, so "Save run and quit" picks up where everyone stood (like Slay the Spire 2),
    /// not at the floor's start: rooms visited and cleared, chests (opened, gone, or still closed, including reward
    /// chests that appeared), items lying on the ground, shop items still for sale, and every player's position.
    ///
    /// The floor itself is rebuilt from the same seed, so rooms, chests and shop items are matched by their room index
    /// and position. The host applies this right after the floor loads; the friends' games take enemies, items, chests
    /// and positions from the host as always, so they see the same floor. Old saves without these lines still load
    /// (at the floor's start, as before).
    /// </summary>
    internal static class FloorProgress
    {
        static readonly CultureInfo C = CultureInfo.InvariantCulture;

        public sealed class Data
        {
            public readonly HashSet<int> Visited = new HashSet<int>(), Cleared = new HashSet<int>();
            public readonly List<KeyValuePair<string, Vector3>> Chests = new List<KeyValuePair<string, Vector3>>(); // name, (x, y, state)
            public readonly List<KeyValuePair<int, Vector2>> Items = new List<KeyValuePair<int, Vector2>>();
            public readonly List<Vector2> ShopLeft = new List<Vector2>();
            public readonly Dictionary<int, Vector2> Positions = new Dictionary<int, Vector2>();
            public bool HasShopList;
            public bool Any => Visited.Count > 0 || Positions.Count > 0;
        }

        const float ChestClosed = 0f, ChestOpen = 1f;

        // ---------------- Saving ----------------

        public static void Write(StringBuilder sb)
        {
            var dungeon = GameManager.Instance.Dungeon;
            if (dungeon == null || dungeon.data == null) return;
            var rooms = dungeon.data.rooms;
            for (int i = 0; i < rooms.Count; i++)
            {
                var r = rooms[i];
                // Visited: entered, or shown on screen (warps and teleporters can skip the entered flag).
                if (r == null || (!r.hasEverBeenVisited && r.visibility == RoomHandler.VisibilityStatus.OBSCURED)) continue;
                bool cleared = !r.HasActiveEnemies(RoomHandler.ActiveEnemyType.All) && !IsBossRoom(r);
                sb.Append("room ").Append(i).Append(' ').Append(cleared ? 1 : 0).Append('\n');
            }
            foreach (var ch in StaticReferenceManager.AllChests)
            {
                if (!ch || !ch.gameObject.activeInHierarchy) continue;
                Vector2 p = ch.transform.position;
                sb.Append("chest ").Append(PickupSync.ChestPrefabName(ch.name)).Append(' ').Append(F(p.x)).Append(' ').Append(F(p.y))
                  .Append(' ').Append(ch.IsOpen ? "open" : "closed").Append('\n');
            }
            foreach (var p in PickupSync.GroundItems())
            {
                Vector2 at = p.transform.position;
                sb.Append("item ").Append(p.PickupObjectId).Append(' ').Append(F(at.x)).Append(' ').Append(F(at.y)).Append('\n');
            }
            sb.Append("shop");
            foreach (var s in UnityEngine.Object.FindObjectsOfType<ShopItemController>())
                if (s && s.item) { Vector2 at = s.transform.position; sb.Append(' ').Append(F(at.x)).Append(':').Append(F(at.y)); }
            sb.Append('\n');
            var local = PlayerSlots.Local;
            if (local) Pos(sb, NetSession.LocalSlot, local);
            foreach (var kv in PlayerSlots.Remotes) if (kv.Value) Pos(sb, kv.Key, kv.Value);
        }

        static void Pos(StringBuilder sb, int slot, PlayerController p)
        {
            Vector2 at = p.transform.position;
            sb.Append("pos ").Append(slot).Append(' ').Append(F(at.x)).Append(' ').Append(F(at.y)).Append('\n');
        }

        static string F(float v) => v.ToString("0.###", C);

        /// <summary>Reads one save line; false when the line is not floor progress.</summary>
        public static bool Read(Data d, string[] parts)
        {
            switch (parts[0])
            {
                case "room":
                    int i = int.Parse(parts[1], C);
                    d.Visited.Add(i);
                    if (parts[2] == "1") d.Cleared.Add(i);
                    return true;
                case "chest":
                    d.Chests.Add(new KeyValuePair<string, Vector3>(parts[1], new Vector3(float.Parse(parts[2], C), float.Parse(parts[3], C), parts[4] == "open" ? ChestOpen : ChestClosed)));
                    return true;
                case "item":
                    d.Items.Add(new KeyValuePair<int, Vector2>(int.Parse(parts[1], C), new Vector2(float.Parse(parts[2], C), float.Parse(parts[3], C))));
                    return true;
                case "shop":
                    d.HasShopList = true;
                    for (int k = 1; k < parts.Length; k++) { var xy = parts[k].Split(':'); d.ShopLeft.Add(new Vector2(float.Parse(xy[0], C), float.Parse(xy[1], C))); }
                    return true;
                case "pos":
                    d.Positions[int.Parse(parts[1], C)] = new Vector2(float.Parse(parts[2], C), float.Parse(parts[3], C));
                    return true;
            }
            return false;
        }

        // ---------------- Restoring (host, right after the saved floor loads) ----------------

        public static void Apply(Data d)
        {
            Reset();
            if (d == null || !d.Any) return;
            var dungeon = GameManager.Instance.Dungeon;
            var rooms = dungeon.data.rooms;
            int erased = 0, chests = 0, items = 0, shop = 0;

            // Rooms: seen on the map; cleared rooms get no enemies, no more waves and no second reward.
            foreach (int i in d.Visited)
            {
                if (i < 0 || i >= rooms.Count || rooms[i] == null) continue;
                var r = rooms[i];
                r.hasEverBeenVisited = true;
                if (r.visibility == RoomHandler.VisibilityStatus.OBSCURED) r.visibility = RoomHandler.VisibilityStatus.VISITED;
                try { Minimap.Instance?.RevealMinimapRoom(r, true, false, false); } catch { }
                if (!d.Cleared.Contains(i)) continue;
                r.ClearReinforcementLayers();
                givenReward?.SetValue(r, true);
                clearedRooms.Add(r);
            }
            // The floor's enemies are placed a moment after it reports ready: Tick keeps cleared rooms empty for a while.
            clearUntil = Time.realtimeSinceStartup + 60f;
            erased = EraseInCleared();

            // Chests: the floor's own chests are matched by position. Gone or already opened: removed. A reward chest
            // that had appeared and was still closed is placed again.
            var saved = new List<KeyValuePair<string, Vector3>>(d.Chests);
            foreach (var ch in new List<Chest>(StaticReferenceManager.AllChests))
            {
                if (!ch) continue;
                int m = saved.FindIndex(s => ((Vector2)ch.transform.position - new Vector2(s.Value.x, s.Value.y)).sqrMagnitude < 2.25f);
                // Gone, or opened before the save (already looted): removed. Opening it again would roll new loot.
                if (m < 0 || saved[m].Value.z == ChestOpen) { ch.DeregisterChestOnMinimap(); UnityEngine.Object.Destroy(ch.gameObject); chests++; if (m >= 0) saved.RemoveAt(m); continue; }
                saved.RemoveAt(m);
            }
            foreach (var s in saved)
            {
                if (s.Value.z == ChestOpen) continue;
                var ch = PickupSync.SpawnChestAt(s.Key, new Vector2(s.Value.x, s.Value.y));
                if (ch) chests++;
            }

            // Items on the ground: what was lying there at the save, no more and no less.
            foreach (var p in new List<PickupObject>(PickupSync.GroundItems())) { PickupSync.RemoveGroundItem(p); items++; }
            foreach (var kv in d.Items)
            {
                var prefab = PickupObjectDatabase.GetById(kv.Key);
                if (prefab == null) continue;
                try { LootEngine.SpawnItem(prefab.gameObject, kv.Value, Vector2.zero, 0f, false, false, false); items++; } catch { }
            }

            // Shops: anything bought before the save is gone.
            if (d.HasShopList)
                foreach (var s in UnityEngine.Object.FindObjectsOfType<ShopItemController>())
                {
                    if (!s || !s.item) continue;
                    Vector2 at = s.transform.position;
                    // Only shops someone had visited; a shop nobody saw had nothing bought (its items may not even
                    // have existed when the save was made).
                    var room = at.GetAbsoluteRoom();
                    if (room == null || !d.Visited.Contains(rooms.IndexOf(room))) continue;
                    if (d.ShopLeft.Exists(v => (v - at).sqrMagnitude < 0.25f)) continue;
                    UnityEngine.Object.Destroy(s.gameObject);
                    shop++;
                }

            // The host goes back to where it stood; friends are placed when they arrive (see PlaceArrivals).
            var local = PlayerSlots.Local;
            if (local && d.Positions.TryGetValue(NetSession.LocalSlot, out var me)) Warp(local, me);
            pendingPositions = d.Positions;
            Plugin.Log.LogInfo($"FloorProgress: restored {d.Visited.Count} visited rooms ({d.Cleared.Count} cleared, {erased} enemies removed), {chests} chest changes, {items} item changes, {shop} sold shop items");
        }

        static Dictionary<int, Vector2> pendingPositions;
        static readonly HashSet<RoomHandler> clearedRooms = new HashSet<RoomHandler>();
        static float clearUntil;
        static int erasedTotal;

        /// <summary>Host, every frame: for a minute after a resume, enemies appearing in rooms cleared before the save are
        /// removed (the friends' games get the removal through the usual enemy sync).</summary>
        public static void Tick()
        {
            if (clearedRooms.Count == 0) return;
            if (Time.realtimeSinceStartup > clearUntil)
            {
                Plugin.Log.LogInfo($"FloorProgress: cleared rooms kept empty ({erasedTotal} enemies removed)");
                clearedRooms.Clear();
                return;
            }
            if (Time.frameCount % 10 == 0) EraseInCleared();
        }

        static int EraseInCleared()
        {
            int n = 0;
            foreach (var a in new List<AIActor>(StaticReferenceManager.AllEnemies))
            {
                if (!a || a.healthHaver == null || a.healthHaver.IsBoss || a.healthHaver.IsDead) continue;
                var r = a.ParentRoom ?? ((Vector2)a.transform.position).GetAbsoluteRoom();
                if (r == null || !clearedRooms.Contains(r)) continue;
                try { a.EraseFromExistence(true); n++; } catch { }
            }
            erasedTotal += n;
            return n;
        }

        /// <summary>Host: a friend's character arrived on the resumed floor; put it where it stood at the save.</summary>
        public static void PlaceArrival(int slot, PlayerController pc)
        {
            if (pendingPositions == null || !pc || !pendingPositions.TryGetValue(slot, out var at)) return;
            pendingPositions.Remove(slot);
            Warp(pc, at);
        }

        public static void Clear() => pendingPositions = null;

        /// <summary>A new floor or session: nothing from the resumed floor applies any more.</summary>
        public static void Reset() { pendingPositions = null; clearedRooms.Clear(); erasedTotal = 0; }

        static void Warp(PlayerController p, Vector2 at)
        {
            try
            {
                p.WarpToPoint(at, false, false);
                Plugin.Log.LogInfo($"FloorProgress: slot {PlayerSlots.SlotOf(p)} back at {at}");
            }
            catch (Exception e) { Plugin.Log.LogWarning("FloorProgress warp: " + e.Message); }
        }

        static readonly System.Reflection.FieldInfo givenReward = HarmonyLib.AccessTools.Field(typeof(RoomHandler), "m_hasGivenReward");

        static bool IsBossRoom(RoomHandler r) => r.area != null && r.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS;
    }
}
