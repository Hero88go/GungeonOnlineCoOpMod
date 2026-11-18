using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Online, the friends are player 2 and up. A floor that starts in co-op mode while the game sees only one player
    /// (a resumed run, a rejoin, a friend whose copy is not back yet) made the game's own player two: the Cultist, or
    /// the last co-op character. The friend then drove both, and the game's shared co-op camera took over from the
    /// independent ones. The game no longer makes its own player two online, and one made anyway is removed.
    /// </summary>
    internal static class NoGamePlayerTwo
    {
        public static int Removed;
        static readonly bool Off = Dev.DevEnv.IsHarness && System.Environment.GetEnvironmentVariable("GO_NO_P2_FIX") == "1"; // tests: before the fix

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Dungeonator.Dungeon), "GeneratePlayerIfNecessary"),
                prefix: new HarmonyMethod(typeof(NoGamePlayerTwo), nameof(Prefix)),
                finalizer: new HarmonyMethod(typeof(NoGamePlayerTwo), nameof(Finalizer)));
        }

        static void Prefix(Dungeonator.Dungeon __instance, out bool __state)
        {
            __state = false;
            if (Dev.DevEnv.IsHarness && GameManager.HasInstance) Plugin.Log.LogInfo($"NoGamePlayerTwo: floor start type={GameManager.Instance.CurrentGameType} players={GameManager.Instance.AllPlayers.Length} regen={__instance.ForceRegenerationOfCharacters}");
            if (!NetSession.Active || !GameManager.HasInstance || PastMode.InShipPast || Off) return;
            var gm = GameManager.Instance;
            if (gm.CurrentGameType != GameManager.GameType.COOP_2_PLAYER || __instance.ForceRegenerationOfCharacters) return;
            if (gm.AllPlayers.Length >= 2) return;
            gm.CurrentGameType = GameManager.GameType.SINGLE_PLAYER;
            __state = true;
        }

        static System.Exception Finalizer(System.Exception __exception, bool __state)
        {
            if (__state && GameManager.HasInstance)
            {
                GameManager.Instance.CurrentGameType = GameManager.GameType.COOP_2_PLAYER;
                Plugin.Log.LogInfo("NoGamePlayerTwo: floor started with no friend copy yet; the game's own player two was not made");
            }
            if (NetSession.Active && Plugin.Instance != null && !PastMode.InShipPast && !Off) Plugin.Instance.StartCoroutine(RemoveExtra());
            return __exception;
        }

        /// <summary>After the load: a player that is neither this machine's own nor a friend's copy is the game's.</summary>
        static System.Collections.IEnumerator RemoveExtra()
        {
            while (GameManager.HasInstance && GameManager.Instance.IsLoadingLevel) yield return null;
            yield return null;
            if (!NetSession.Active || !GameManager.HasInstance || PastMode.InShipPast) yield break;
            var gm = GameManager.Instance;
            var me = PlayerSlots.Local;
            if (!me) yield break;
            bool any = false;
            foreach (var p in gm.AllPlayers)
            {
                if (!p || p == me || PlayerSlots.SlotOf(p) >= 0) continue;
                Plugin.Log.LogWarning("NoGamePlayerTwo: removed the game's own extra player " + p.name);
                p.SetInputOverride("gungeon online extra");
                if (gm.SecondaryPlayer == p) gm.SecondaryPlayer = null;
                Object.Destroy(p.gameObject);
                Removed++;
                any = true;
            }
            if (!any) yield break;
            yield return null;
            PlayerSlots.EnsureSecondary();
            gm.RefreshAllPlayers();
        }
    }
}
