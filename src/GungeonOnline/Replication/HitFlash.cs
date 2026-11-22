using System.Collections.Generic;
using GungeonOnline.Game;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Enemies belong to the host, so on a friend's screen an enemy only flashed white once the host's hit came back
    /// (a round trip later), and not at all when the host's copy of the shot missed. Your own shot hitting an enemy
    /// now makes it flash at once on your screen, a look only: the host still decides the damage. The host's hit for
    /// that enemy right after does not flash it a second time.
    /// </summary>
    internal static class HitFlash
    {
        static readonly bool Off = Dev.DevEnv.IsHarness && System.Environment.GetEnvironmentVariable("GO_NO_HITFLASH_FIX") == "1"; // tests: before the fix
        [System.ThreadStatic] static bool ownShot;
        static readonly Dictionary<HealthHaver, float> flashedAt = new Dictionary<HealthHaver, float>();
        public static int Flashed, HostFlashSkipped;

        static readonly AccessTools.FieldRef<HealthHaver, bool> isFlashing = AccessTools.FieldRefAccess<HealthHaver, bool>("m_isFlashing");
        static readonly AccessTools.FieldRef<HealthHaver, Coroutine> flashCo = AccessTools.FieldRefAccess<HealthHaver, Coroutine>("m_flashOnHitCoroutine");
        static readonly AccessTools.FieldRef<HealthHaver, List<Material>> mats = AccessTools.FieldRefAccess<HealthHaver, List<Material>>("materialsToFlash");
        static readonly AccessTools.FieldRef<HealthHaver, List<Material>> outlineMats = AccessTools.FieldRefAccess<HealthHaver, List<Material>>("outlineMaterialsToFlash");
        static readonly AccessTools.FieldRef<HealthHaver, List<Color>> srcColors = AccessTools.FieldRefAccess<HealthHaver, List<Color>>("sourceColors");
        static readonly System.Reflection.MethodInfo flashOnHit = AccessTools.Method(typeof(HealthHaver), "FlashOnHit");

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Projectile), "HandleDamage"),
                prefix: new HarmonyMethod(typeof(HitFlash), nameof(ShotPrefix)), finalizer: new HarmonyMethod(typeof(HitFlash), nameof(ShotFinalizer)));
        }

        static void ShotPrefix(Projectile __instance) { ownShot = !Off && NetSession.Active && !NetSession.IsHost && __instance && __instance.Owner && __instance.Owner == PlayerSlots.Local; }
        static System.Exception ShotFinalizer(System.Exception __exception) { ownShot = false; return __exception; }

        /// <summary>Called where a friend's screen refuses damage to an enemy copy.</summary>
        public static void OnRefusedHit(HealthHaver hh, DamageCategory cat, PixelCollider col)
        {
            if (!ownShot || !hh || !hh.flashesOnDamage || hh.spriteAnimator == null || isFlashing(hh) || flashOnHit == null) return;
            if (hh.GetCurrentHealth() <= 0f) return;
            try
            {
                // What the game's own damage code does right before its flash.
                var co = flashCo(hh);
                if (co != null) hh.StopCoroutine(co);
                if (mats(hh) == null) { mats(hh) = new List<Material>(); outlineMats(hh) = new List<Material>(); srcColors(hh) = new List<Color>(); }
                if (hh.gameActor) foreach (var m in mats(hh)) m.SetColor("_OverrideColor", hh.gameActor.CurrentOverrideColor);
                var om = outlineMats(hh); var sc = srcColors(hh);
                if (om != null && sc != null) for (int i = 0; i < om.Count && i < sc.Count; i++) om[i].SetColor("_OverrideColor", sc[i]);
                flashCo(hh) = hh.StartCoroutine((System.Collections.IEnumerator)flashOnHit.Invoke(hh, new object[] { cat, col }));
                flashedAt[hh] = Time.realtimeSinceStartup;
                Flashed++;
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("HitFlash: " + e.Message); }
        }

        /// <summary>The host's hit for an enemy we just flashed ourselves: no second flash.</summary>
        public static bool SkipHostFlash(HealthHaver hh)
        {
            if (!hh || !flashedAt.TryGetValue(hh, out var t)) return false;
            if (Time.realtimeSinceStartup - t > 0.4f) { flashedAt.Remove(hh); return false; }
            flashedAt.Remove(hh);
            HostFlashSkipped++;
            return true;
        }
    }
}
