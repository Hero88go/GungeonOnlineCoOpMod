using System;
using System.Reflection;
using HarmonyLib;

namespace GungeonOnline.Game
{
    /// <summary>
    /// A boss intro that fails on its first frames (seen on a guest walking into the Bullet King: an error inside the
    /// game's own intro routine) used to leave that player's game outside the intro while the host's stood still for
    /// the cutscene. Their replayed button presses then meant different things on each screen (a blank spent on one
    /// screen only). Online, such a failure is logged in full and whatever the intro had already switched on (time
    /// stop, input lock, hidden HUD, manual camera) is switched off again, so play goes on cleanly on both screens.
    /// </summary>
    internal static class BossIntroGuard
    {
        public static int Failures;

        public static void Install(Harmony h)
        {
            var routine = AccessTools.Method(typeof(GenericIntroDoer), "FrameDelayedTriggerSequence");
            var moveNext = routine != null ? AccessTools.EnumeratorMoveNext(routine) : null;
            if (moveNext == null) { Plugin.Log.LogWarning("BossIntroGuard: the intro routine was not found"); return; }
            h.Patch(moveNext, finalizer: new HarmonyMethod(typeof(BossIntroGuard), nameof(Finalizer)));
        }

        static Exception Finalizer(object __instance, Exception __exception, ref bool __result)
        {
            if (__exception == null || !Session.NetSession.Active) return __exception;
            Failures++;
            Plugin.Log.LogError("Boss intro failed; play goes on without the cutscene. " + __exception);
            try
            {
                GenericIntroDoer doer = null;
                foreach (var f in __instance.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    if (f.FieldType == typeof(GenericIntroDoer)) { doer = f.GetValue(__instance) as GenericIntroDoer; break; }
                Undo(doer);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Boss intro cleanup: " + e.Message); }
            __result = false; // the routine ends here
            return null;
        }

        /// <summary>Switch off everything the intro's first frames may have switched on before the error.</summary>
        static void Undo(GenericIntroDoer doer)
        {
            var gm = GameManager.Instance;
            if (doer != null)
            {
                BraveTime.ClearMultiplier(doer.gameObject);
                if (doer.aiAnimator) doer.aiAnimator.enabled = true; // copies are kept still by WorldSync anyway
                if (doer.aiShooter) doer.aiShooter.ToggleGunAndHandRenderers(true, "genericIntro");
            }
            GameManager.IsBossIntro = false;
            gm.PreventPausing = false;
            foreach (var p in gm.AllPlayers) if (p) p.ClearInputOverride("BossIntro");
            var ui = GameUIRoot.Instance;
            if (ui != null) { ui.ShowCoreUI(string.Empty); ui.ToggleLowerPanels(true, false, string.Empty); }
            var cam = gm.MainCameraController;
            if (cam != null) { cam.SetManualControl(false, false); cam.StartTrackingPlayer(); }
            if (Minimap.HasInstance) Minimap.Instance.TemporarilyPreventMinimap = false;
        }
    }
}
