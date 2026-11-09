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
        static float pastSince = -1f, nextPulse, nextLog, nextWalk, nextInteract, nextKill, nextClear;
        static bool pulse, killed, sawBoss, guideAsked;

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

        static bool AgunimAlive()
        {
            foreach (var hh in StaticReferenceManager.AllHealthHavers) if (hh && hh.IsBoss && hh.IsAlive && hh.name.Contains("Agunim")) return true;
            return false;
        }

        /// <summary>The Bullet's Past: where the floor broke after its first boss, or null.</summary>
        static Vector2? FindHole()
        {
            foreach (var b in Object.FindObjectsOfType<BulletPastRoomController>())
                if (b.RoomIdentifier == BulletPastRoomController.BulletRoomCategory.ROOM_C && b.AgunimFlightCollider && b.AgunimFlightCollider.enabled
                    && b.AgunimPostDeathTalker == null)
                    return b.AgunimFlightCollider.UnitCenter;
            return null;
        }

        void Update()
        {
            if (!InPast) { pastSince = -1f; killed = false; sawBoss = false; guideAsked = false; pulse = false; return; }
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
                else if (owner && sawBoss && FindHole() is Vector2 hole && Vector2.Distance(me.CenterPosition, hole) > 1f)
                    to = hole; // the Bullet's Past: down through the broken floor to the last room
                else if (owner && Object.FindObjectOfType<RobotPastController>() is RobotPastController rpc && rpc.EmperorBot
                         && me.CenterPosition.y < rpc.EmperorBot.transform.position.y - 12f)
                    to = new Vector2(me.CenterPosition.x, rpc.EmperorBot.transform.position.y - 11f); // walk up to the Emperor
                else if (owner && Object.FindObjectOfType<PastLabMarineController>() is PastLabMarineController plmc && plmc.LeftRedMarine
                         && me.CenterPosition.y < plmc.LeftRedMarine.transform.position.y)
                    to = new Vector2(me.CenterPosition.x, plmc.LeftRedMarine.transform.position.y + 1f); // walk past the red marines
                else if (owner && !Object.FindObjectOfType<RobotPastController>() && !Object.FindObjectOfType<PastLabMarineController>() && !Object.FindObjectOfType<GuidePastController>())
                {
                    foreach (var hh in StaticReferenceManager.AllHealthHavers)
                        if (hh && hh.IsBoss && hh.IsAlive && (!hh.gameObject.activeInHierarchy || !hh.aiActor || !hh.aiActor.HasBeenEngaged)
                            && !(hh.name.Contains("Gunon") && AgunimAlive()))
                        { to = (Vector2)hh.transform.position + new Vector2(0f, -4f); break; }
                }
                else { var o = Game.PastMode.Owner; if (o && Vector2.Distance(o.CenterPosition, me.CenterPosition) > 4f) to = o.transform.position; }
                if (to.HasValue && Vector2.Distance(to.Value, me.transform.position) > 1.5f)
                {
                    Plugin.Log.LogInfo($"PASTWALK to {to.Value}");
                    me.WarpToPoint(to.Value, false, false);
                }
            }
            // Test players cannot die in a Past (the bots stand in the fire); the story is what is tested.
            if (Auto && me && me.healthHaver) me.healthHaver.PreventAllDamage = true;
            // The Guide's boss starts when the hero examines the room's object: the hero's button, replayed everywhere.
            if (Auto && now >= nextInteract)
            {
                nextInteract = now + 3f;
                var gpc = Object.FindObjectOfType<GuidePastController>();
                var hero = Game.PastMode.Owner;
                if (gpc && gpc.TargetInteractable && hero && !guideAsked)
                {
                    if (owner && Vector2.Distance(me.CenterPosition, gpc.TargetInteractable.transform.position) > 2.5f)
                        me.WarpToPoint((Vector2)gpc.TargetInteractable.transform.position + new Vector2(0f, -1.5f), false, false);
                    else if (Vector2.Distance(hero.CenterPosition, gpc.TargetInteractable.transform.position) < 3f)
                    {
                        guideAsked = true;
                        Plugin.Log.LogInfo("PASTINTERACT the hero examines the Guide's object");
                        gpc.TargetInteractable.Interact(hero);
                    }
                }
            }
            // The players' shooting, which the bots do not do here: ordinary enemies (waves) fall on every screen.
            if (Auto && now - pastSince > 20f && now >= nextClear && !GameManager.IsBossIntro)
            {
                nextClear = now + 4f;
                int n = 0;
                foreach (var a in new List<AIActor>(StaticReferenceManager.AllEnemies))
                    if (a && a.healthHaver && !a.healthHaver.IsBoss && a.healthHaver.IsAlive && a.gameObject.activeInHierarchy && a.HasBeenEngaged && a.CompanionOwner == null && !a.GetComponent<PlayerController>())
                    { a.healthHaver.ApplyDamage(100000f, Vector2.zero, "test", CoreDamageTypes.None, DamageCategory.Unstoppable, true, null, true); n++; }
                if (n > 0) Plugin.Log.LogInfo("PASTCLEAR " + n + " enemies");
            }
            if (killAt >= 0 && owner && now - pastSince >= killAt && now >= nextKill && !GameManager.IsBossIntro)
            {
                nextKill = now + 3f;
                foreach (var hh in new List<HealthHaver>(StaticReferenceManager.AllHealthHavers))
                    if (hh && hh.IsBoss && hh.IsAlive && hh.IsVulnerable && hh.gameObject.activeInHierarchy && hh.aiActor && hh.aiActor.HasBeenEngaged
                        && !(hh.name.Contains("Gunon") && AgunimAlive())) // the Bullet's Past: its bosses in story order
                    {
                        killed = true;
                        hh.minimumHealth = 0f;
                        Plugin.Log.LogInfo("PASTKILL " + hh.name + " hp=" + hh.GetCurrentHealth());
                        hh.ApplyDamage(1000000f, Vector2.zero, "test", CoreDamageTypes.SpecialBossDamage, DamageCategory.Unstoppable, true, null, true);
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
