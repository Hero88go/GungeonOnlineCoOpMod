using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>"I'm stuck" in the online menu: your own player is moved to the nearest clear floor (or next to a
    /// friend when there is none nearby). Your machine owns your position, so the others see the move too.</summary>
    internal static class Unstuck
    {
        public static int Used;

        public static void Now()
        {
            var me = PlayerSlots.Local;
            if (!me || GameManager.Instance.IsLoadingLevel) return;
            var to = Camera.IndependentCamera.SafeSpotNear(me);
            if (to == null)
                foreach (var kv in PlayerSlots.Remotes)
                    if (kv.Value && !kv.Value.IsGhost) { to = Camera.IndependentCamera.SafeSpotNear(kv.Value); if (to != null) break; }
            if (to == null) { Session.Toasts.Show("No clear floor found nearby", 3f, true); return; }
            if (!GameManager.IsBossIntro) me.ClearAllInputOverrides(); // stuck can also mean frozen in place by a lock that never lifted
            me.WarpToPoint(to.Value);
            if (Pixelator.Instance) Pixelator.Instance.FadeToBlack(0.3f, true);
            Used++;
            Plugin.Log.LogInfo($"Unstuck: moved to {to.Value}");
            Session.Toasts.Show("Moved you to clear floor", 3f);
        }
    }
}
