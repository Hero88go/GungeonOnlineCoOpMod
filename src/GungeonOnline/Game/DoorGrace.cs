using System.Collections;
using System.Collections.Generic;
using Dungeonator;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// When one player starts a fight, the room's doors stay open for a few seconds while a teammate is still
    /// outside, so they can walk in themselves instead of being pulled across the floor. The doors close as soon as
    /// everyone is inside, or after the grace time (then Classic pulls the rest as before). Host only; the seal
    /// replicates as usual. Config: Gameplay.DoorGraceSeconds (0 = off).
    /// </summary>
    internal static class DoorGrace
    {
        static readonly HashSet<RoomHandler> waiting = new HashSet<RoomHandler>();
        static bool sealing;
        public static int Graced, WalkedIn;

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(RoomHandler), nameof(RoomHandler.SealRoom)), prefix: new HarmonyMethod(typeof(DoorGrace), nameof(SealPrefix)) { priority = Priority.First });
        }

        static bool SomeoneInside(RoomHandler r, out bool someoneOutside)
        {
            bool inside = false;
            someoneOutside = false;
            foreach (var p in GameManager.Instance.AllPlayers)
            {
                if (!p || p.IsGhost || p.healthHaver.IsDead) continue;
                if (p.CurrentRoom == r) inside = true; else someoneOutside = true;
            }
            return inside;
        }

        static bool SealPrefix(RoomHandler __instance)
        {
            if (sealing || !Session.NetSession.Active || !Session.NetSession.IsHost) return true;
            float grace = Session.NetConfig.DoorGraceSeconds.Value;
            if (grace <= 0f || waiting.Contains(__instance)) return waiting.Contains(__instance) ? false : true;
            if (__instance.area == null || __instance.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS) return true;
            if (!SomeoneInside(__instance, out bool outside) || !outside) return true;
            waiting.Add(__instance);
            Graced++;
            GameManager.Instance.StartCoroutine(SealLater(__instance, grace));
            return false;
        }

        static IEnumerator SealLater(RoomHandler r, float grace)
        {
            float end = Time.realtimeSinceStartup + grace;
            bool walked = false;
            while (Time.realtimeSinceStartup < end && Session.NetSession.Active && !GameManager.Instance.IsLoadingLevel)
            {
                SomeoneInside(r, out bool outside);
                if (!outside) { walked = true; break; }
                yield return null;
            }
            waiting.Remove(r);
            if (GameManager.Instance.IsLoadingLevel || r == null) yield break;
            if (walked) WalkedIn++;
            // The fight may already be over (everything died while the doors were open).
            var enemies = r.GetActiveEnemies(RoomHandler.ActiveEnemyType.RoomClear);
            if (enemies == null || enemies.Count == 0) yield break;
            sealing = true;
            try { r.SealRoom(); }
            finally { sealing = false; }
        }

        public static void Reset() => waiting.Clear();
    }
}
