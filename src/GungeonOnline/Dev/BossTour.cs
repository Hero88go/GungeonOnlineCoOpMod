using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>
    /// Harness GO_BOSS_TOUR=1: the boss of every floor from 1 to GO_BOSS_TOUR_LAST (default 5), with both players.
    /// Host: warp everyone into the boss room, let the boss fight for a few seconds after its intro, kill it,
    /// wait for the rewards, go to the next floor. Guest: watch the boss copy and report what it saw.
    /// Lines: "BOSSTOUR host floor=N ..." and "BOSSTOUR guest floor=N ...".
    /// </summary>
    internal static class BossTour
    {
        static string floorKey;
        static int phase;
        static float arrivedAt, engagedAt, doneAt, nextKill, nextLog;
        static readonly HashSet<string> hostNames = new HashSet<string>();

        // guest
        static readonly Dictionary<int, AIActor> seen = new Dictionary<int, AIActor>();
        static readonly HashSet<string> seenNames = new HashSet<string>();
        static bool everVisible, everEngagedIntro;
        static float minHp = 1f;

        /// <summary>GO_BOSS_FIGHT_FOR=seconds: how long the boss fights before the harness kills it (default 8).</summary>
        static float FightFor => float.TryParse(System.Environment.GetEnvironmentVariable("GO_BOSS_FIGHT_FOR"), out var s) ? s : 8f;
        static float nextLook, nextShot;
        static int shotN;

        /// <summary>Both sides, twice a second while in the boss room: what each boss looks like on this screen
        /// ("BOSSLOOK"), so tests can compare the host's boss with the guest's copy part by part.</summary>
        static void LookLog(float t, Dungeonator.RoomHandler room)
        {
            if (t < nextLook || room == null) return;
            nextLook = t + 0.5f;
            var sb = new System.Text.StringBuilder();
            foreach (var e in StaticReferenceManager.AllEnemies)
            {
                if (!e || !e.healthHaver || !e.healthHaver.IsBoss || e.healthHaver.IsDead || !InRoom(e, room)) continue;
                Vector2 root = e.transform.position;
                sb.Append($" [{e.name.Replace("(Clone)", "")} at={root.x:F1},{root.y:F1} hp={e.healthHaver.GetCurrentHealthPercentage():F2} clip={(e.spriteAnimator && e.spriteAnimator.CurrentClip != null ? e.spriteAnimator.CurrentClip.name : "-")} on={(e.sprite && e.sprite.renderer ? e.sprite.renderer.enabled : false)}");
                foreach (var s in e.GetComponentsInChildren<tk2dBaseSprite>(true))
                {
                    if (!s || s == e.sprite) continue;
                    Vector2 d = (Vector2)s.transform.position - root;
                    sb.Append($" {s.name}:{(s.renderer && s.renderer.enabled && s.gameObject.activeInHierarchy ? 1 : 0)}:{d.x:F1},{d.y:F1}:{s.transform.eulerAngles.z:F0}:{s.spriteId}");
                }
                foreach (var b in e.GetComponentsInChildren<AIBeamShooter>(true)) if (b) sb.Append($" beam:{(b.IsFiringLaser ? 1 : 0)}:{b.LaserAngle:F0}");
                sb.Append("]");
            }
            int beams = 0, rockets = 0;
            foreach (var b in Object.FindObjectsOfType<BasicBeamController>()) if (b) beams++;
            foreach (var r in Object.FindObjectsOfType<SkyRocket>()) if (r) rockets++;
            if (System.Environment.GetEnvironmentVariable("GO_BOSS_SHOTS") == "1" && t >= nextShot)
            {
                nextShot = t + 3f;
                var dir = System.Environment.GetEnvironmentVariable("GO_RUN_DIR") ?? ".";
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, $"look-{(Session.NetSession.IsHost ? "host" : "guest")}-f{GameManager.Instance.CurrentFloor}-{shotN++:D3}.png"));
            }
            if (System.Environment.GetEnvironmentVariable("GO_BOSS_HEAL") == "1" && !Session.NetSession.IsHost && GameManager.Instance.PrimaryPlayer)
            {
                var hh = GameManager.Instance.PrimaryPlayer.healthHaver;
                if (hh.GetCurrentHealth() < hh.GetMaxHealth()) hh.ForceSetCurrentHealth(hh.GetMaxHealth());
                if (hh.Armor < 2f) hh.Armor = 2f;
            }
            Plugin.Log.LogInfo($"BOSSLOOK {(Session.NetSession.IsHost ? "host" : "guest")} floor={GameManager.Instance.CurrentFloor} wall={System.DateTime.Now:HH:mm:ss.f} dark={room.IsDarkAndTerrifying} goop={DeadlyDeadlyGoopManager.allGoopPositionMap.Count} beams={beams} rockets={rockets} stone={(GameManager.Instance.PrimaryPlayer ? GameManager.Instance.PrimaryPlayer.CurrentStoneGunTimer : 0f):F1}{sb}");
        }

        static bool On => System.Environment.GetEnvironmentVariable("GO_BOSS_TOUR") == "1";
        static int Last => int.TryParse(System.Environment.GetEnvironmentVariable("GO_BOSS_TOUR_LAST"), out var n) ? n : 5;

        static bool patched;
        static string pickedRooms = "";

        /// <summary>GO_FORCE_BOSS=name,name,...: when a floor's boss is picked, take the one whose room table or rooms
        /// contain one of the names (both games run this, so the floors still match). Every pick logs the whole pool.</summary>
        static void Patch()
        {
            patched = true;
            var h = new HarmonyLib.Harmony("gungeononline.dev.bosstour");
            h.Patch(HarmonyLib.AccessTools.Method(typeof(BossFloorEntry), "SelectBoss"), postfix: new HarmonyLib.HarmonyMethod(typeof(BossTour), nameof(SelectBossPostfix)));
            if (System.Environment.GetEnvironmentVariable("GO_PLAY_PROBE") == "1")
                h.Patch(HarmonyLib.AccessTools.Method(typeof(tk2dSpriteAnimator), "Play", new[] { typeof(tk2dSpriteAnimationClip), typeof(float), typeof(float), typeof(bool) }), prefix: new HarmonyLib.HarmonyMethod(typeof(BossTour), nameof(PlayProbe)));
        }

        static int playLogs;

        /// <summary>GO_PLAY_PROBE=1 (guest): who plays which animation on a boss copy.</summary>
        static void PlayProbe(tk2dSpriteAnimator __instance, tk2dSpriteAnimationClip clip)
        {
            if (playLogs > 300 || Session.NetSession.IsHost || clip == null || __instance == null) return;
            var a = __instance.GetComponent<AIActor>();
            if (!a || !a.healthHaver || !a.healthHaver.IsBoss) return;
            playLogs++;
            var st = new System.Diagnostics.StackTrace(1, false);
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < st.FrameCount && i < 7; i++) { var m = st.GetFrame(i).GetMethod(); if (m != null) sb.Append(m.DeclaringType != null ? m.DeclaringType.Name : "?").Append('.').Append(m.Name).Append(" < "); }
            Plugin.Log.LogInfo($"PLAYPROBE {a.name} {clip.name} from {sb}");
        }

        static string TableText(IndividualBossFloorEntry e)
        {
            var t = e.TargetRoomTable;
            if (t == null) return "-";
            var sb = new System.Text.StringBuilder(t.name);
            if (t.includedRooms != null && t.includedRooms.elements != null)
                foreach (var w in t.includedRooms.elements) if (w != null && w.room) sb.Append("/").Append(w.room.name);
            return sb.ToString();
        }

        static void SelectBossPostfix(BossFloorEntry __instance, ref IndividualBossFloorEntry __result)
        {
            try
            {
                var pool = new List<string>();
                foreach (var e in __instance.Bosses) if (e != null) pool.Add(TableText(e));
                string force = System.Environment.GetEnvironmentVariable("GO_FORCE_BOSS");
                IndividualBossFloorEntry pick = null;
                if (!string.IsNullOrEmpty(force))
                    foreach (var name in force.Split(','))
                    {
                        foreach (var e in __instance.Bosses)
                            if (e != null && name.Length > 0 && TableText(e).IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0) { pick = e; break; }
                        if (pick != null) break;
                    }
                if (pick != null) __result = pick;
                if (__result != null) pickedRooms = TableText(__result);
                Plugin.Log.LogInfo($"BOSSPOOL {__instance.Annotation} picked={(__result != null ? TableText(__result) : "-")} forced={pick != null} pool=[{string.Join(" | ", pool.ToArray())}]");
            }
            catch (System.Exception ex) { Plugin.Log.LogInfo("BOSSPOOL error " + ex.Message); }
        }

        static float nextProbe;

        /// <summary>GO_INTRO_PROBE=1: while a boss intro runs, log every 2 s what it is waiting for.</summary>
        static void IntroProbe()
        {
            if (System.Environment.GetEnvironmentVariable("GO_INTRO_PROBE") != "1" || !GameManager.IsBossIntro) return;
            if (Time.realtimeSinceStartup < nextProbe) return;
            nextProbe = Time.realtimeSinceStartup + 2f;
            foreach (var d in Object.FindObjectsOfType<GenericIntroDoer>())
            {
                if (!d) continue;
                var tr = HarmonyLib.Traverse.Create(d);
                if (!tr.Field("m_isRunning").GetValue<bool>()) continue;
                var spec = tr.Field("m_specificIntroDoer").GetValue<SpecificIntroDoer>();
                var motions = tr.Field("activeMotions").GetValue() as System.Collections.IList;
                var sb = new System.Text.StringBuilder();
                if (motions != null)
                    foreach (var m in motions)
                    {
                        var mt = HarmonyLib.Traverse.Create(m);
                        sb.Append($" [prog={mt.Field("lerpProgress").GetValue()} start={mt.Field("lerpStart").GetValue()} end={mt.Field("lerpEnd").GetValue()} speed={mt.Field("speed").GetValue()}]");
                    }
                Vector2 ideal = Vector2.zero;
                try { ideal = GameManager.Instance.MainCameraController.GetIdealCameraPosition(); } catch { }
                var players = new System.Text.StringBuilder();
                foreach (var p in GameManager.Instance.AllPlayers) if (p) players.Append($" {p.name}@{p.CenterPosition} room={(p.CurrentRoom != null ? p.CurrentRoom.GetRoomName() : "-")} st={p.CurrentInputState}");
                Plugin.Log.LogInfo($"INTROPROBE {d.name} phase={tr.Field("m_currentPhase").GetValue()} complete={tr.Field("m_phaseComplete").GetValue()} countdown={tr.Field("m_phaseCountdown").GetValue()} card={tr.Field("m_waitingForBossCard").GetValue()} waitSpecific={tr.Field("m_waitingForSpecificIntroCompletion").GetValue()} spec={(spec ? spec.GetType().Name + " finished=" + spec.IsIntroFinished : "-")} coopTp={tr.Field("m_hasCoopTeleported").GetValue()} ideal={ideal} motions={(motions != null ? motions.Count : -1)}{sb} players:{players}");
            }
        }

        public static void Tick()
        {
            try { IntroProbe(); } catch (System.Exception e) { Plugin.Log.LogInfo("INTROPROBE error " + e.Message); }
            if (!patched && On) Patch();
            if (!On || !Session.NetSession.Active || !GameManager.HasInstance) return;
            var gm = GameManager.Instance;
            if (Game.SceneObjects.InBreach || gm.IsLoadingLevel || gm.Dungeon == null || gm.PrimaryPlayer == null) return;
            float t = Time.realtimeSinceStartup;
            string key = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + gm.CurrentFloor;
            if (key != floorKey)
            {
                if (floorKey != null && !Session.NetSession.IsHost) GuestReport("left");
                floorKey = key; phase = 0; arrivedAt = t; engagedAt = -1f; doneAt = -1f;
                hostNames.Clear(); seen.Clear(); seenNames.Clear(); everVisible = false; everEngagedIntro = false; minHp = 1f;
            }
            if (Session.NetSession.IsHost) HostTick(t, gm); else GuestTick(t, gm);
        }

        static Dungeonator.RoomHandler BossRoom(GameManager gm)
        {
            // The floor's own boss (the one the game picked), not a secret boss room on the same floor.
            foreach (var room in gm.Dungeon.data.rooms)
                if (room.area != null && room.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS && pickedRooms.Contains("/" + room.GetRoomName())) return room;
            foreach (var room in gm.Dungeon.data.rooms)
                if (room.area != null && room.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS) return room;
            return null;
        }

        static bool InRoom(AIActor e, Dungeonator.RoomHandler room)
        {
            if (e.ParentRoom == room) return true;
            try { return e.GetAbsoluteParentRoom() == room; } catch { return false; }
        }

        static void HostTick(float t, GameManager gm)
        {
            var ws = Replication.WorldSync.Instance;
            int floor = gm.CurrentFloor;
            if (phase == 0)
            {
                if (ws == null || ws.ReadyPeers < DevEnv.ExpectPlayers - 1 || t - arrivedAt < 4f) return;
                var bossRoom = BossRoom(gm);
                if (bossRoom == null) { Plugin.Log.LogInfo($"BOSSTOUR host floor={floor} FAIL no boss room"); phase = 3; doneAt = t - 100f; return; }
                int bi = 0;
                bool alone = System.Environment.GetEnvironmentVariable("GO_BOSS_ALONE") == "1"; // the host walks in, the friend stays behind
                foreach (var p in gm.AllPlayers)
                {
                    if (!p) continue;
                    p.healthHaver.IsVulnerable = false;
                    if (alone && p != gm.PrimaryPlayer) continue;
                    var spot = bossRoom.GetCenteredVisibleClearSpot(2, 2);
                    p.WarpToPoint(spot.ToVector2() + new Vector2(0.5f + bi, -3f));
                    bi++;
                }
                Plugin.Log.LogInfo($"BOSSTOUR host floor={floor} room={bossRoom.GetRoomName()} warped");
                phase = 1;
                return;
            }
            var room = gm.PrimaryPlayer.CurrentRoom;
            if (phase == 1 && engagedAt >= 0 && room != null) LookLog(t, room);
            if (phase == 1)
            {
                if (t - arrivedAt > 150f) { Plugin.Log.LogInfo($"BOSSTOUR host floor={floor} FAIL no kill in 150 s names={string.Join(",", new List<string>(hostNames).ToArray())} intro={GameManager.IsBossIntro}"); phase = 2; doneAt = t; return; }
                if (room == null || room.area == null || room.area.PrototypeRoomCategory != PrototypeDungeonRoom.RoomCategory.BOSS) return;
                bool anyBoss = false, allEngaged = true;
                foreach (var e in StaticReferenceManager.AllEnemies)
                    if (e && e.healthHaver && e.healthHaver.IsBoss && !e.healthHaver.IsDead && InRoom(e, room)) { anyBoss = true; hostNames.Add(e.name.Replace("(Clone)", "")); if (!e.HasBeenEngaged) allEngaged = false; }
                if (engagedAt < 0)
                {
                    if (anyBoss && allEngaged && !GameManager.IsBossIntro) { engagedAt = t; Plugin.Log.LogInfo($"BOSSTOUR host floor={floor} engaged names={string.Join(",", new List<string>(hostNames).ToArray())}"); }
                    return;
                }
                if (t - engagedAt < FightFor || t < nextKill || GameManager.IsBossIntro) return; // let it fight a little first
                nextKill = t + 2f;
                bool any = false;
                foreach (var e in new List<AIActor>(StaticReferenceManager.AllEnemies))
                    if (e && e.healthHaver && !e.healthHaver.IsDead && InRoom(e, room) && e.CompanionOwner == null)
                    {
                        if (e.healthHaver.IsBoss) any = true; // done once every boss is down; leftovers just get cleared
                        if (e.healthHaver.IsBoss && !e.HasBeenEngaged) continue;
                        e.healthHaver.ApplyDamage(1e6f, Vector2.zero, "harness", CoreDamageTypes.None, DamageCategory.Unstoppable, true);
                    }
                if (!any)
                {
                    phase = 2; doneAt = t;
                    Plugin.Log.LogInfo($"BOSSTOUR host floor={floor} killed names={string.Join(",", new List<string>(hostNames).ToArray())} fought={t - engagedAt:F0}s");
                }
                return;
            }
            if (phase == 2 && t - doneAt > 20f)
            {
                int ped = 0;
                foreach (var r in Object.FindObjectsOfType<RewardPedestal>()) if (r && r.gameObject.activeInHierarchy) ped++;
                Plugin.Log.LogInfo($"BOSSTOUR host floor={floor} rewards pedestals={ped} st={gm.PrimaryPlayer.CurrentInputState}");
                phase = 3;
                if (floor >= Last) { Plugin.Log.LogInfo("BOSSTOUR host done"); phase = 4; return; }
                gm.DelayedLoadNextLevel(0.5f);
            }
        }

        static void GuestTick(float t, GameManager gm)
        {
            var room = gm.PrimaryPlayer.CurrentRoom;
            if (room == null || room.area == null || room.area.PrototypeRoomCategory != PrototypeDungeonRoom.RoomCategory.BOSS) return;
            LookLog(t, room);
            foreach (var e in StaticReferenceManager.AllEnemies)
            {
                if (!e || !e.healthHaver || !e.healthHaver.IsBoss || !InRoom(e, room)) continue;
                int id = e.GetInstanceID();
                if (!seen.ContainsKey(id)) { seen[id] = e; seenNames.Add(e.name.Replace("(Clone)", "")); }
                bool drawn = e.gameObject.activeInHierarchy && e.sprite && e.sprite.renderer && e.sprite.renderer.enabled && e.sprite.color.a > 0.1f;
                if (drawn) everVisible = true;
                if (GameManager.IsBossIntro) everEngagedIntro = true;
                float hp = e.healthHaver.GetCurrentHealthPercentage();
                if (hp < minHp) minHp = hp;
            }
            if (t >= nextLog && seen.Count > 0) { nextLog = t + 5f; GuestReport("in room"); }
        }

        static void GuestReport(string when)
        {
            if (seen.Count == 0) { Plugin.Log.LogInfo($"BOSSTOUR guest floor={GameManager.Instance.CurrentFloor} {when} seen=none"); return; }
            bool allDead = true;
            foreach (var kv in seen)
            {
                var e = kv.Value;
                if (e && e.healthHaver && !e.healthHaver.IsDead && e.healthHaver.GetCurrentHealthPercentage() > 0f && e.gameObject.activeInHierarchy) allDead = false;
            }
            int ped = 0;
            foreach (var r in Object.FindObjectsOfType<RewardPedestal>()) if (r && r.gameObject.activeInHierarchy && r.sprite && r.sprite.renderer && r.sprite.renderer.enabled) ped++;
            var p = GameManager.Instance.PrimaryPlayer;
            Plugin.Log.LogInfo($"BOSSTOUR guest floor={GameManager.Instance.CurrentFloor} {when} seen={string.Join(",", new List<string>(seenNames).ToArray())} visible={everVisible} intro={everEngagedIntro} minHp={minHp:F2} died={allDead} pedestals={ped} st={(p ? p.CurrentInputState.ToString() : "-")}");
        }
    }
}
