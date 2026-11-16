using System.Collections.Generic;
using HarmonyLib;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Each floor has its own physics. A player made while a floor was loading (a friend who reconnected then) could
    /// still be listed in the previous floor's physics; placing them on the new floor then failed inside the game's
    /// floor build, and the floor never finished loading. Before players are placed, any player the current
    /// physics does not know is registered with it fresh.
    /// </summary>
    internal static class PlayerPhysicsGuard
    {
        static readonly AccessTools.FieldRef<PhysicsEngine, List<SpeculativeRigidbody>> bodies =
            AccessTools.FieldRefAccess<PhysicsEngine, List<SpeculativeRigidbody>>("m_rigidbodies");

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Dungeonator.Dungeon), "PlacePlayerInRoom"),
                prefix: new HarmonyMethod(typeof(PlayerPhysicsGuard), nameof(Prefix)));
            // A friend who reconnects while a floor loads is made before their guns exist. The gun HUD and the shop's
            // item roll read those guns; their errors stopped the end of the floor load, so the floor never opened.
            // One player's level-loaded step failing (a friend who left or reconnected during the load) stopped the end
            // of the load for everyone on this PC, so the floor never opened. Online, it is logged and the load goes on.
            h.Patch(AccessTools.Method(typeof(PlayerController), "BraveOnLevelWasLoaded"),
                prefix: new HarmonyMethod(typeof(PlayerPhysicsGuard), nameof(LevelLoadedPrefix)),
                finalizer: new HarmonyMethod(typeof(PlayerPhysicsGuard), nameof(LevelLoadedFinalizer)));
            h.Patch(AccessTools.Method(typeof(GenericIntroDoer), nameof(GenericIntroDoer.PlayerEntered)),
                prefix: new HarmonyMethod(typeof(PlayerPhysicsGuard), nameof(IntroPrefix)));
            h.Patch(AccessTools.Method(typeof(GameUIRoot), nameof(GameUIRoot.UpdateGunData)),
                prefix: new HarmonyMethod(typeof(PlayerPhysicsGuard), nameof(HudPrefix)));
            h.Patch(AccessTools.Method(typeof(RewardManager), nameof(RewardManager.PlayerHasItemInSynergyContainingOtherItem),
                new[] { typeof(PlayerController), typeof(PickupObject), typeof(bool).MakeByRefType() }),
                prefix: new HarmonyMethod(typeof(PlayerPhysicsGuard), nameof(SynergyPrefix)));
        }

        // Right after a floor arrives, a friend's copy can still sit where they stood on the previous floor (or on an
        // older copy of this one); on this floor that spot may be inside the boss room. Such a copy must not start the
        // boss intro, which froze this player. A real entry a moment later still starts it.
        static float arrivedAt = -100f;

        static bool IntroPrefix(PlayerController player)
        {
            if (!Session.NetSession.Active || player == null || !PlayerSlots.IsRemote(player)) return true;
            if (UnityEngine.Time.realtimeSinceStartup - arrivedAt < 4f) { Plugin.Log.LogInfo("Boss intro: ignored a friend's copy right after the floor arrived"); return false; }
            if (Session.NetSession.IsHost && Replication.WorldSync.Instance != null && !Replication.WorldSync.Instance.IsSlotReady(PlayerSlots.SlotOf(player)))
            { Plugin.Log.LogInfo("Boss intro: ignored a friend who has not loaded this floor yet"); return false; }
            return true;
        }

        static void LevelLoadedPrefix() { PlayerSlots.EnsureSecondary(); if (Plugin.Instance != null) Plugin.Instance.StartCoroutine(PlayerSlots.GunsBackAfterLoad()); }

        static System.Exception LevelLoadedFinalizer(PlayerController __instance, System.Exception __exception)
        {
            if (__exception == null || !Session.NetSession.Active) return __exception;
            Plugin.Log.LogWarning($"Level load: {(__instance ? __instance.name : "a player")} could not finish its arrival; the floor opens anyway. {__exception}");
            return null;
        }

        static bool Unarmed(PlayerController p) => p == null || p.inventory == null || p.inventory.AllGuns == null;

        static bool HudPrefix(GunInventory inventory, PlayerController sourcePlayer) =>
            !(Session.NetSession.Active && (inventory == null || Unarmed(sourcePlayer) || sourcePlayer.healthHaver == null));

        static bool SynergyPrefix(PlayerController player, ref bool __result)
        {
            if (!Unarmed(player) && player.passiveItems != null && player.activeItems != null) return true;
            __result = false;
            return false;
        }

        static void Prefix()
        {
            arrivedAt = UnityEngine.Time.realtimeSinceStartup;
            if (!PhysicsEngine.HasInstance || !GameManager.HasInstance) return;
            var engine = PhysicsEngine.Instance;
            var list = bodies(engine);
            foreach (var p in GameManager.Instance.AllPlayers)
            {
                var rb = p ? p.specRigidbody : null;
                if (rb == null || list.Contains(rb)) continue;
                rb.proxyId = -1;
                rb.PhysicsRegistration = SpeculativeRigidbody.RegistrationState.Deregistered;
                engine.Register(rb);
                Plugin.Log.LogInfo("PlayerPhysicsGuard: registered " + p.name + " with this floor's physics");
            }
        }
    }
}
