using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>
    /// Tests only. GO_PAST_AUTO=1: in a Past, dialogue advances by itself every second and conversations take their
    /// first answer. GO_PAST_KILL_AT=N: N seconds after the Past opens, the owner's screen defeats any boss that is up
    /// (the boss fight itself is not what a Past test checks). Logs PASTSTATE every 2 s and PASTEND when the
    /// time-tube credits start on this screen.
    /// </summary>
    internal sealed class PastDriver : MonoBehaviour
    {
        static readonly bool Auto = System.Environment.GetEnvironmentVariable("GO_PAST_AUTO") == "1";
        static float killAt = float.TryParse(System.Environment.GetEnvironmentVariable("GO_PAST_KILL_AT"), out var k) ? k : -1f;
        static float pastSince = -1f, nextPulse, nextLog, nextWalk;
        static bool pulse, killed, sawBoss;

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(BraveInput), nameof(BraveInput.WasAdvanceDialoguePressed), new System.Type[0]),
                postfix: new HarmonyMethod(typeof(PastDriver), nameof(AdvancePostfix)));
            h.Patch(AccessTools.Method(typeof(BraveInput), nameof(BraveInput.WasAdvanceDialoguePressed), new[] { typeof(bool).MakeByRefType() }),
                postfix: new HarmonyMethod(typeof(PastDriver), nameof(AdvancePostfix)));
            h.Patch(AccessTools.Method(typeof(TimeTubeCreditsController), nameof(TimeTubeCreditsController.HandleTimeTubeCredits)),
                prefix: new HarmonyMethod(typeof(PastDriver), nameof(CreditsPrefix)));
        }

        static void AdvancePostfix(ref bool __result) { if (Auto && pulse && InPast) __result = true; }

        static void CreditsPrefix() => Plugin.Log.LogInfo($"PASTEND credits start t={Time.realtimeSinceStartup:F1} past={(pastSince >= 0 ? Time.realtimeSinceStartup - pastSince : -1):F1}s");

        static bool InPast => GameManager.HasInstance && GameManager.Instance.CurrentLevelOverrideState == GameManager.LevelOverrideState.CHARACTER_PAST && !GameManager.Instance.IsLoadingLevel;

        void Update()
        {
            if (!InPast) { pastSince = -1f; killed = false; sawBoss = false; pulse = false; return; }
            float now = Time.realtimeSinceStartup;
            if (pastSince < 0f) { pastSince = now; Plugin.Log.LogInfo("PASTSTART " + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + " owner=" + Game.PastMode.OwnerSlot); }
            pulse = Auto && now >= nextPulse;
            if (pulse) nextPulse = now + 1f;
            if (Auto && pulse && GameUIRoot.HasInstance) { try { GameUIRoot.Instance.SelectConversationResponse(); } catch { } }
            var me = Game.PlayerSlots.Local;
            bool owner = me && Game.PlayerSlots.SlotOf(me) == Game.PastMode.OwnerSlot;
            if (Auto && me && now >= nextWalk && me.CurrentInputState == PlayerInputState.AllInput && !GameManager.IsBossIntro)
            {
                // The story moves on when players walk; the bots do not. Stand where the story waits:
                // the hero by a boss that has not woken, the others beside the hero, everyone by the getaway car.
                nextWalk = now + 6f;
                Vector2? to = null;
                var car = Object.FindObjectOfType<ConvictPastController>();
                bool bossUp = false;
                foreach (var hh in StaticReferenceManager.AllHealthHavers) if (hh && hh.IsBoss && hh.IsAlive) bossUp = true;
                if (bossUp) sawBoss = true;
                if (car && car.Car && sawBoss && !bossUp) to = car.Car.Sprite.WorldCenter + new Vector2(0f, -0.5f);
                else if (owner)
                {
                    foreach (var hh in StaticReferenceManager.AllHealthHavers)
                        if (hh && hh.IsBoss && hh.IsAlive && (!hh.gameObject.activeInHierarchy || !hh.aiActor || !hh.aiActor.HasBeenEngaged))
                        { to = (Vector2)hh.transform.position + new Vector2(0f, -4f); break; }
                }
                else { var o = Game.PastMode.Owner; if (o && Vector2.Distance(o.CenterPosition, me.CenterPosition) > 4f) to = o.transform.position; }
                if (to.HasValue && Vector2.Distance(to.Value, me.transform.position) > 1.5f)
                {
                    Plugin.Log.LogInfo($"PASTWALK to {to.Value}");
                    me.WarpToPoint(to.Value, false, false);
                }
            }
            if (killAt >= 0 && !killed && owner && now - pastSince >= killAt && !GameManager.IsBossIntro)
            {
                foreach (var hh in new List<HealthHaver>(StaticReferenceManager.AllHealthHavers))
                    if (hh && hh.IsBoss && hh.IsAlive && hh.IsVulnerable && hh.gameObject.activeInHierarchy && hh.aiActor && hh.aiActor.HasBeenEngaged)
                    {
                        killed = true;
                        Plugin.Log.LogInfo("PASTKILL " + hh.name);
                        hh.ApplyDamage(1000000f, Vector2.zero, "test", CoreDamageTypes.None, DamageCategory.Unstoppable, true, null, true);
                    }
            }
            if (now >= nextLog)
            {
                nextLog = now + 2f;
                var sb = new System.Text.StringBuilder("PASTSTATE t=" + (now - pastSince).ToString("F0"));
                foreach (var p in GameManager.Instance.AllPlayers)
                    if (p) sb.Append($" [{(p == me ? "me" : "copy")} slot={Game.PlayerSlots.SlotOf(p)} {p.characterIdentity} pos={(Vector2)p.transform.position} st={p.CurrentInputState} hp={p.healthHaver.GetCurrentHealth()}]");
                int bosses = 0, enemies = StaticReferenceManager.AllEnemies.Count;
                foreach (var hh in StaticReferenceManager.AllHealthHavers) if (hh && hh.IsBoss && hh.IsAlive) bosses++;
                sb.Append($" bosses={bosses} enemies={enemies} intro={GameManager.IsBossIntro}");
                Plugin.Log.LogInfo(sb.ToString());
            }
        }
    }
}
