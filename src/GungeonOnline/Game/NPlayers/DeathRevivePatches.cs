using System.Collections;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game.NPlayers
{
    /// <summary>
    /// With three or four players: a death is a knock-out while anyone else is still standing (the game only asks
    /// about "the other player"), a revive chest or boss pedestal brings every ghost back, and chests stay revive
    /// chests while any ghost remains. Two-player sessions keep the game's own rules.
    /// </summary>
    internal static class DeathRevivePatches
    {
        static bool allowUntoggle;
        public static int CoopDeaths, Revives;

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(PlayerController), "Die"), prefix: new HarmonyMethod(typeof(DeathRevivePatches), nameof(DiePrefix)));
            h.Patch(AccessTools.Method(typeof(Chest), "GiveCoopPartnerBack"), prefix: new HarmonyMethod(typeof(DeathRevivePatches), nameof(ChestRevivePrefix)));
            h.Patch(AccessTools.Method(typeof(RewardPedestal), nameof(RewardPedestal.GiveCoopPartnerBack)), prefix: new HarmonyMethod(typeof(DeathRevivePatches), nameof(PedestalRevivePrefix)));
            h.Patch(AccessTools.Method(typeof(RewardPedestal), "Start"), postfix: new HarmonyMethod(typeof(DeathRevivePatches), nameof(PedestalStartPostfix)));
            h.Patch(AccessTools.Method(typeof(Chest), nameof(Chest.ToggleCoopChests)), prefix: new HarmonyMethod(typeof(DeathRevivePatches), nameof(TogglePrefix)));
            h.Patch(AccessTools.Method(typeof(Chest), "Open"), postfix: new HarmonyMethod(typeof(DeathRevivePatches), nameof(ChestUsedPostfix)));
            h.Patch(AccessTools.Method(typeof(Chest), "OnBroken"), postfix: new HarmonyMethod(typeof(DeathRevivePatches), nameof(ChestUsedPostfix)));
        }

        static int LivingCount()
        {
            int n = 0;
            foreach (var p in Players.All) if (Players.IsLiving(p)) n++;
            return n;
        }

        /// <summary>The game ends the run when "the other player" is down. With more players: only when nobody is left.</summary>
        static bool DiePrefix(PlayerController __instance)
        {
            if (Session.NetSession.Active && !Players.AnyOtherAlive(__instance))
            {
                foreach (var p in Players.All)
                    if (Traverse.Create(p).Field("m_cloneWaitingForCoopDeath").GetValue<bool>())
                    {
                        AccessTools.Method(typeof(PlayerController), "DoCloneEffect").Invoke(p, null);
                        return false;
                    }
                if (RunSync.CloneInProgress) return false;
            }
            // Online with any number of players: a teammate still standing means a knock-out, decided from the mod's own
            // player list. The game asks "the other player", which after a friend crashed and rejoined could be
            // missing, and their next death then ended the whole run on their screen alone.
            if (!Session.NetSession.Active || !Players.AnyOtherAlive(__instance)) return true;
            // What the game's own Die does before its two-player decision.
            __instance.DeathsThisRun++;
            if (GameUIRoot.Instance)
            {
                var bar = GameUIRoot.Instance.GetReloadBarForPlayer(__instance);
                if (bar) bar.UpdateStatusBars(__instance);
            }
            CoopDeaths++;
            Plugin.Log.LogInfo($"DEATH slot={PlayerSlots.SlotOf(__instance)} id={__instance.PlayerIDX} living={LivingCount()} gameover=False");
            __instance.StartCoroutine(__instance.HandleCoopDeath(__instance.IsFalling));
            return false;
        }

        static IEnumerator ReviveAllFromChest(Chest chest, bool doDelay)
        {
            if (Session.NetSession.Active && !Session.NetSession.IsHost) yield break;
            if (doDelay) yield return new WaitForSeconds(0.7f);
            AkSoundEngine.PostEvent("play_obj_chest_open_01", chest.gameObject);
            AkSoundEngine.PostEvent("stop_obj_fuse_loop_01", chest.gameObject);
            var ghosts = Players.Ghosts();
            Vector2 at = chest.sprite.WorldBottomCenter;
            for (int i = 0; i < ghosts.Count; i++)
            {
                var g = ghosts[i];
                g.specRigidbody.enabled = true;
                g.gameObject.SetActive(true);
                g.sprite.renderer.enabled = true;
                g.ResurrectFromChest(at + new Vector2(0.75f * i, 0f));
                Revives++;
            }
            Plugin.Log.LogInfo("REVIVE all ghosts=" + ghosts.Count);
            Plugin.Instance.StartCoroutine(UntoggleWhenClear());
        }

        static readonly System.Reflection.FieldInfo coopChestsField = AccessTools.Field(typeof(Chest), "m_IsCoopMode");
        static bool coopChests() => coopChestsField != null && (bool)coopChestsField.GetValue(null);

        /// <summary>
        /// The game revives from a chest only when exactly one player is alive. With more players a revive chest that is
        /// opened or broken while two or three are still up brings the ghosts back too (the loot is kept as well).
        /// </summary>
        static void ChestUsedPostfix(Chest __instance)
        {
            if (Session.NetSession.Active && !Session.NetSession.IsHost) return;
            if (!Players.NPlus || !coopChests() || GameManager.Instance.NumberOfLivingPlayers <= 1) return;
            if (Players.Ghosts().Count == 0) return;
            __instance.StartCoroutine(ReviveAllFromChest(__instance, false));
        }

        static bool ChestRevivePrefix(Chest __instance, bool doDelay, ref IEnumerator __result)
        {
            if (Session.NetSession.Active && !Session.NetSession.IsHost)
            {
                __result = ReviveAllFromChest(__instance, doDelay);
                return false;
            }
            if (!Players.NPlus) return true;
            __result = ReviveAllFromChest(__instance, doDelay);
            return false;
        }

        static bool PedestalRevivePrefix()
        {
            if (Session.NetSession.Active && !Session.NetSession.IsHost) return false;
            if (!Players.NPlus) return true;
            var ghosts = Players.Ghosts();
            foreach (var g in ghosts)
            {
                g.specRigidbody.enabled = true;
                g.gameObject.SetActive(true);
                g.ResurrectFromBossKill();
                Revives++;
            }
            Plugin.Log.LogInfo("REVIVE all ghosts=" + ghosts.Count + " (boss)");
            Plugin.Instance.StartCoroutine(UntoggleWhenClear());
            return false;
        }

        /// <summary>The game only arms the boss pedestal's revive when exactly one player is alive; arm it for any ghost.</summary>
        static void PedestalStartPostfix(RewardPedestal __instance)
        {
            if (Players.NPlus && Session.NetSession.IsHost && __instance.IsBossRewardPedestal && Players.Ghosts().Count > 0) __instance.ReturnCoopPlayerOnLand = true;
        }

        /// <summary>
        /// A revive turns the chests back to normal before the revived player stops being a ghost, so "any ghost left"
        /// has to allow that one. Our revive-all turns them back itself once nobody is a ghost.
        /// </summary>
        static bool TogglePrefix(bool coopDead)
        {
            if (coopDead || allowUntoggle || !Players.NPlus) return true;
            return Players.Ghosts().Count <= 1;
        }

        static IEnumerator UntoggleWhenClear()
        {
            float giveUp = Time.realtimeSinceStartup + 10f;
            while (Players.Ghosts().Count > 0 && Time.realtimeSinceStartup < giveUp) yield return null;
            allowUntoggle = true;
            try { Chest.ToggleCoopChests(false); }
            finally { allowUntoggle = false; }
        }
    }
}
