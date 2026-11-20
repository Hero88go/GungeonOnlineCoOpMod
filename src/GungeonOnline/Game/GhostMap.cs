using GungeonOnline.Session;
using HarmonyLib;

namespace GungeonOnline.Game
{
    /// <summary>
    /// A dead co-op player is a ghost: the game only lets a ghost blank, so it could not even open the map. On one couch
    /// that is fine (you share the screen), online it left a dead player stuck wherever they fell (an elevator, a pit
    /// edge) until a revive. Online, your own ghost can open the full map and teleport from it. Picking things up,
    /// chests and shops stay closed to ghosts, as in the game.
    /// </summary>
    internal static class GhostMap
    {
        static readonly bool Off = Dev.DevEnv.IsHarness && System.Environment.GetEnvironmentVariable("GO_NO_GHOSTMAP_FIX") == "1"; // tests: before the fix
        public static int Opened;

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(PlayerController), "HandlePlayerInput"), postfix: new HarmonyMethod(typeof(GhostMap), nameof(Postfix)));
        }

        static void Postfix(PlayerController __instance)
        {
            if (Off || !NetSession.Active || !__instance || !__instance.IsGhost || __instance != PlayerSlots.Local) return;
            if (!Minimap.HasInstance || !Minimap.Instance || Minimap.Instance.IsFullscreen || GameUIRoot.Instance == null || GameUIRoot.Instance.MetalGearActive) return;
            if (GameManager.Instance.IsPaused || GameManager.Instance.IsLoadingLevel || __instance.CurrentInputState == PlayerInputState.NoInput) return;
            var a = BraveInput.GetInstanceForPlayer(__instance.PlayerIDX);
            bool test = Dev.DevEnv.GhostMapPress;
            Dev.DevEnv.GhostMapPress = false;
            if (!test && (a == null || a.ActiveActions == null || !a.ActiveActions.MapAction.WasPressed)) return;
            Minimap.Instance.ToggleMinimap(true, test); // like the game: open while the button is held (a test has no held button)
            Opened++;
            Plugin.Log.LogInfo("GhostMap: your ghost opened the map");
        }
    }
}
