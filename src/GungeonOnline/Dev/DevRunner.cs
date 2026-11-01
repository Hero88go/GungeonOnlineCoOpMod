using System.IO;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>Harness heartbeat and timed quit. Inert for players.</summary>
    internal sealed class DevRunner : MonoBehaviour
    {
        float nextStatus;
        bool suspended;
        float bothSince = -1f;

        float nextSuspend;

        /// <summary>
        /// Test instances are always silent: Wwise volumes forced to 0 every frame, the engine suspended once it is up
        /// and again every second (the game wakes it on focus and scene changes; DevEnv also blocks that wake-up).
        /// </summary>
        void Mute()
        {
            AudioListener.volume = 0f;
            try
            {
                AkSoundEngine.SetRTPCValue("VOL_MUS", 0f);
                AkSoundEngine.SetRTPCValue("VOL_SFX", 0f);
                AkSoundEngine.SetRTPCValue("VOL_UI", 0f);
                if (AkSoundEngine.IsInitialized() && (!suspended || Time.realtimeSinceStartup >= nextSuspend))
                {
                    nextSuspend = Time.realtimeSinceStartup + 1f;
                    bool ok = AkSoundEngine.Suspend() == AKRESULT.AK_Success;
                    if (ok && !suspended) Plugin.Log.LogInfo("Harness: audio engine suspended (muted)");
                    suspended = ok;
                }
            }
            catch { }
        }

        System.Collections.Generic.List<float> shots;

        /// <summary>GO_SHOTS="30,45": screenshots at those seconds since boot, saved as shot-30.png etc.</summary>
        void TimedShots(float t)
        {
            if (shots == null)
            {
                shots = new System.Collections.Generic.List<float>();
                var v = System.Environment.GetEnvironmentVariable("GO_SHOTS");
                if (v != null) foreach (var x in v.Split(',')) if (float.TryParse(x, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) shots.Add(f);
            }
            for (int i = shots.Count - 1; i >= 0; i--)
            {
                if (t < shots[i]) continue;
                var dir = System.Environment.GetEnvironmentVariable("GO_RUN_DIR") ?? ".";
                ScreenCapture.CaptureScreenshot(Path.Combine(dir, "shot-" + Mathf.RoundToInt(shots[i]) + ".png"));
                Plugin.Log.LogInfo("Harness: screenshot at " + shots[i]);
                shots.RemoveAt(i);
            }
        }

        float doorSince = -1f;
        int doorStep;

        /// <summary>
        /// GO_DOOR_TEST=1: the real Breach door path. 6 s after both players exist the friend walks into the Gungeon
        /// door (must be refused), 6 s later the host walks in (must start the run for both).
        /// </summary>
        void DoorTest(float t)
        {
            if (doorStep >= 2 || System.Environment.GetEnvironmentVariable("GO_DOOR_TEST") != "1") return;
            if (!Game.SceneObjects.InBreach || GameManager.Instance.AllPlayers.Length < 2 || Game.PlayerSlots.Local == null) return;
            if (doorSince < 0) doorSince = t;
            bool host = Session.NetSession.IsHost;
            float due = host ? 12f : 6f;
            if (t - doorSince < due) return;
            doorStep = 2;
            FoyerGungeonDoor door = null;
            foreach (var d in Object.FindObjectsOfType<FoyerGungeonDoor>())
                if (d && !d.LoadsCharacterSelect && !d.ReturnToFoyerFromTutorial && !d.LoadsCustomLevel) { door = d; break; }
            if (door == null) { Plugin.Log.LogWarning("Harness: no Gungeon door found"); return; }
            Plugin.Log.LogInfo("Harness: walking into the Gungeon door as " + (host ? "host" : "client"));
            HarmonyLib.AccessTools.Method(typeof(FoyerGungeonDoor), "OnTriggered").Invoke(door, new object[] { Game.PlayerSlots.Local.specRigidbody, door.specRigidbody, null });
        }

        float tourT0 = -1f;
        int tourStep;

        /// <summary>GO_UI_TOUR=1: in the Breach, step through the online menu screens and the pause menu, a screenshot each.</summary>
        void UiTour(float t)
        {
            if (tourStep >= 99 || System.Environment.GetEnvironmentVariable("GO_UI_TOUR") != "1") return;
            if (!Game.SceneObjects.InBreach || GameManager.Instance.AllPlayers.Length < 2 || Game.PlayerSlots.Local == null) return;
            if (tourT0 < 0) tourT0 = t + 3f;
            float g = t - tourT0;
            var menu = Session.OnlineMenu.Instance;
            bool host = Session.NetSession.IsHost;
            string[] plan = host
                ? new[] { "Home:0", "Host:0", "Session:0", "Settings:3", "Settings:0", "pause", "close" }
                : new[] { "Join:0", "Session:0", "Settings:4", "close", "close", "close", "close" };
            // GO_UI_TOUR_PLAN=Screen:row,...: a different list of screens (host only), e.g. the settings pages.
            var custom = System.Environment.GetEnvironmentVariable("GO_UI_TOUR_PLAN");
            if (host && !string.IsNullOrEmpty(custom)) plan = (custom + ",close").Split(',');
            int step = (int)(g / 2.5f);
            if (g < 0 || step >= plan.Length) { if (step >= plan.Length) { tourStep = 99; menu?.Close(); GameManager.Instance.Unpause(); } return; }
            float within = g - step * 2.5f;
            string what = plan[step];
            if (within < 0.1f || tourStep != step + 1)
            {
                if (tourStep != step + 1)
                {
                    tourStep = step + 1;
                    if (what == "pause") { menu?.Close(); GameManager.Instance.Pause(); }
                    else if (what == "close") { menu?.Close(); GameManager.Instance.Unpause(); }
                    else
                    {
                        var p = what.Split(':'); menu?.ShowForHarness(p[0], int.Parse(p[1]));
                        // GO_UI_TOAST=1: a message pops up while the menu is open (it must not draw over the menu).
                        if (System.Environment.GetEnvironmentVariable("GO_UI_TOAST") == "1") Session.Toasts.Show("Invite code copied: TEST-CODE-1234. Paste it to your friend.", 4f);
                    }
                    shotTaken = false;
                }
            }
            if (!shotTaken && within > 1.2f && what != "close")
            {
                shotTaken = true;
                var dir = System.Environment.GetEnvironmentVariable("GO_RUN_DIR") ?? ".";
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "ui-" + step + "-" + what.Replace(":", "") + ".png"));
            }
        }
        bool shotTaken;

        float galleryT0 = -1f;
        int galleryShot = -1;
        int galleryWarp;

        /// <summary>
        /// GO_STYLE_GALLERY=1: once the floor is ready, players stand close (name tags), then far apart (locator),
        /// cycling the five styles of each and saving a screenshot of every one on both machines.
        /// </summary>
        void StyleGallery(float t)
        {
            if (System.Environment.GetEnvironmentVariable("GO_STYLE_GALLERY") != "1") return;
            var ws = Replication.WorldSync.Instance;
            var me = Game.PlayerSlots.Local;
            if (ws == null || me == null || Game.SceneObjects.InBreach || GameManager.Instance.IsLoadingLevel) return;
            if (Session.NetSession.IsHost ? ws.ReadyPeers == 0 : !ws.FloorReady) return;
            if (galleryT0 < 0) galleryT0 = t;
            float g = t - galleryT0;
            var gm = GameManager.Instance;
            if (Session.NetSession.IsHost && galleryWarp == 0 && g > 3f)
            {
                galleryWarp = 1;
                Vector2 c = me.CenterPosition;
                if (System.Environment.GetEnvironmentVariable("GO_GALLERY_FIGHT") == "1")
                {
                    // Front-page shot: both players side by side in the middle of a room full of enemies.
                    foreach (var room in gm.Dungeon.data.rooms)
                    {
                        var list = room.GetActiveEnemies(Dungeonator.RoomHandler.ActiveEnemyType.RoomClear);
                        if (list != null && list.Count >= 4 && room != gm.Dungeon.data.Entrance) { c = room.GetCenteredVisibleClearSpot(3, 3).ToVector2(); me.WarpToPoint(c); break; }
                    }
                }
                foreach (var p in gm.AllPlayers) if (p) p.healthHaver.IsVulnerable = false;
                foreach (var kv in Game.PlayerSlots.Remotes) if (kv.Value) kv.Value.WarpToPoint(c + new Vector2(3.5f, -0.5f));
                Plugin.Log.LogInfo("Harness: gallery, players side by side");
            }
            if (Session.NetSession.IsHost && galleryWarp == 1 && g > 38f)
            {
                galleryWarp = 2;
                Vector2 c = me.CenterPosition;
                Dungeonator.RoomHandler far = null; float best = 0;
                foreach (var room in gm.Dungeon.data.rooms)
                {
                    float d = (room.GetCenterCell().ToVector2() - c).magnitude;
                    if (d > best && d < 45f) { best = d; far = room; }
                }
                if (far != null)
                    foreach (var kv in Game.PlayerSlots.Remotes) if (kv.Value) kv.Value.WarpToPoint(far.GetCenterCell().ToVector2());
                Plugin.Log.LogInfo("Harness: gallery, players far apart");
            }
            // Styles: name tags 6..21 s (players close), locators 30..45 s (players apart). 3 s each, shot at +2 s.
            int phase = g >= 6f && g < 36f ? 0 : g >= 42f && g < 57f ? 1 : -1;
            if (phase < 0) { Session.NetConfig.LocatorStyle.Value = 0; Session.NetConfig.NameTagStyle.Value = 0; return; }
            float local = g - (phase == 0 ? 6f : 42f);
            int style = 1 + Mathf.Min(phase == 0 ? 9 : 4, (int)(local / 3f));
            Session.NetConfig.NameTagStyle.Value = phase == 0 ? style : 0;
            Session.NetConfig.LocatorStyle.Value = phase == 1 ? style : 0;
            int shotId = phase * 10 + style;
            if (local - (style - 1) * 3f >= 2f && galleryShot != shotId)
            {
                galleryShot = shotId;
                var dir = System.Environment.GetEnvironmentVariable("GO_RUN_DIR") ?? ".";
                string file = (phase == 0 ? "tag" : "loc") + style + ".png";
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, file));
                Plugin.Log.LogInfo("Harness: gallery shot " + file);
            }
        }

        int elevStep;
        float elevAt = -1f;
        ElevatorDepartureController elev;

        /// <summary>
        /// GO_ELEVATOR_TEST=1 (host): once the guest is ready on the floor, bring both players to the exit elevator
        /// (it arrives when someone is near), then into it; vanilla decides the departure and the guest must follow.
        /// </summary>
        void ElevatorTest(float t)
        {
            if (elevStep >= 3 || !Session.NetSession.IsHost || System.Environment.GetEnvironmentVariable("GO_ELEVATOR_TEST") != "1") return;
            var ws = Replication.WorldSync.Instance;
            if (ws == null || ws.ReadyPeers == 0 || Game.SceneObjects.InBreach || GameManager.Instance.IsLoadingLevel) return;
            if (elevAt < 0) { elevAt = t + 5f; return; }
            if (t < elevAt) return;
            var gm = GameManager.Instance;
            if (elevStep == 0)
            {
                foreach (var e in Game.SceneObjects.All<ElevatorDepartureController>()) { elev = e; break; }
                if (elev == null) { Plugin.Log.LogWarning("Harness: no exit elevator"); elevStep = 3; return; }
                int k = 0;
                foreach (var p in gm.AllPlayers) if (p) { p.healthHaver.IsVulnerable = false; p.WarpToPoint((Vector2)elev.spawnTransform.position + new Vector2(k * 1.5f - 0.75f, -4f)); k++; }
                Plugin.Log.LogInfo("Harness: players at the exit elevator");
                elevStep = 1; elevAt = t + 8f;
                return;
            }
            if (elevStep == 1)
            {
                var floor = elev.elevatorFloor.GetComponent<SpeculativeRigidbody>();
                Vector2 c = floor ? floor.UnitCenter : (Vector2)elev.spawnTransform.position;
                int k = 0;
                foreach (var p in gm.AllPlayers) if (p) { p.WarpToPoint(c + new Vector2(k * 0.6f - 0.6f, -0.6f)); k++; }
                Plugin.Log.LogInfo("Harness: players stepped into the elevator");
                elevStep = 2; elevAt = t + 10f;
                return;
            }
            Plugin.Log.LogWarning("Harness: elevator did not leave within 10 s");
            elevStep = 3;
        }

        // GO_BOSS_KILL_AT=seconds (host): the boss dies then, as if shot down, so boss reward tests do not depend on the bot.
        bool bossKilled;
        float bossLogAt, bossRetryAt;
        void BossKill()
        {
            if (bossKilled || !Session.NetSession.IsHost) return;
            if (!float.TryParse(System.Environment.GetEnvironmentVariable("GO_BOSS_KILL_AT"), out var at) || Time.realtimeSinceStartup < at) return;
            var me = GameManager.Instance.PrimaryPlayer;
            var room = me ? me.CurrentRoom : null;
            if (Time.realtimeSinceStartup > bossLogAt)
            {
                bossLogAt = Time.realtimeSinceStartup + 5f;
                int n = 0, inRoom = 0;
                foreach (var e in StaticReferenceManager.AllEnemies) if (e) { n++; if (e.ParentRoom == room) inRoom++; }
                Plugin.Log.LogInfo($"Harness: boss kill check room={(room != null ? room.GetRoomName() : "-")} cat={(room != null && room.area != null ? room.area.PrototypeRoomCategory.ToString() : "-")} enemies={n} inRoom={inRoom}");
            }
            if (room == null || room.area == null || room.area.PrototypeRoomCategory != PrototypeDungeonRoom.RoomCategory.BOSS) return;
            if (Time.realtimeSinceStartup < bossRetryAt) return;
            // Players cannot hurt a boss during its intro; killing it then starts the kill cam over the intro and both stall.
            if (GameManager.IsBossIntro) return;
            bossRetryAt = Time.realtimeSinceStartup + 2f;
            // Retried until every enemy in the room is dead: a boss can still be untouchable (intro, walk-in) the first time.
            bool any = false;
            foreach (var e in new System.Collections.Generic.List<AIActor>(StaticReferenceManager.AllEnemies))
                if (e && e.healthHaver && !e.healthHaver.IsDead && e.ParentRoom == room && e.CompanionOwner == null)
                {
                    any = true;
                    if (e.healthHaver.IsBoss && !e.HasBeenEngaged) continue;
                    Plugin.Log.LogInfo($"Harness: killing {e.name} (boss={e.healthHaver.IsBoss} vulnerable={e.healthHaver.IsVulnerable} preventAll={e.healthHaver.PreventAllDamage} engaged={e.HasBeenEngaged} introRunning={GameManager.IsBossIntro})");
                    e.healthHaver.ApplyDamage(1e6f, Vector2.zero, "harness", CoreDamageTypes.None, DamageCategory.Unstoppable, true);
                }
            if (!any) bossKilled = true;
        }

        // GO_DEATH_BUTTON=menu|quick (host): 8 s after the death screen shows, press its Main menu or Quick restart.
        float deathSeen = -1f;
        bool deathPressed;
        void DeathButton()
        {
            var which = System.Environment.GetEnvironmentVariable("GO_DEATH_BUTTON");
            if (deathPressed || string.IsNullOrEmpty(which) || !Session.NetSession.IsHost) return;
            var page = Object.FindObjectOfType<AmmonomiconDeathPageController>();
            if (!page) { deathSeen = -1f; return; }
            if (deathSeen < 0) { deathSeen = Time.realtimeSinceStartup; Plugin.Log.LogInfo("Harness: death screen up"); }
            if (Time.realtimeSinceStartup - deathSeen < 8f) return;
            deathPressed = true;
            string m = which == "quick" ? "DoQuickRestart" : "DoMainMenu";
            Plugin.Log.LogInfo("Harness: host presses " + m);
            var mi = HarmonyLib.AccessTools.Method(typeof(AmmonomiconDeathPageController), m);
            var args = new object[mi.GetParameters().Length];
            for (int i = 0; i < args.Length; i++) args[i] = mi.GetParameters()[i].ParameterType.IsValueType ? System.Activator.CreateInstance(mi.GetParameters()[i].ParameterType) : null;
            mi.Invoke(page, args);
        }

        // GO_PEDESTAL_TAKE=seconds: each player takes the nearest boss pedestal then (the guest's goes through the host).
        bool pedestalTaken;
        void PedestalTake()
        {
            if (pedestalTaken || !float.TryParse(System.Environment.GetEnvironmentVariable("GO_PEDESTAL_TAKE"), out var at) || Time.realtimeSinceStartup < at) return;
            var me = GameManager.Instance.PrimaryPlayer;
            if (!me) return;
            RewardPedestal best = null; float bd = 1e9f;
            foreach (var r in Object.FindObjectsOfType<RewardPedestal>())
            {
                if (!r || r.pickedUp) continue;
                // Host takes the left one, guest the right one, so both get one.
                float d = Session.NetSession.IsHost ? r.transform.position.x : -r.transform.position.x;
                if (d < bd) { bd = d; best = r; }
            }
            if (!best) return;
            pedestalTaken = true;
            Plugin.Log.LogInfo($"Harness: taking pedestal at {(Vector2)best.transform.position} item={(best.contents ? best.contents.DisplayName : "-")}");
            me.transform.position = best.transform.position + new Vector3(0.5f, -1f, 0f);
            me.specRigidbody.Reinitialize();
            best.Interact(me);
        }

        bool shopLooked;
        void ShopLook()
        {
            if (shopLooked || System.Environment.GetEnvironmentVariable("GO_SHOP_LOOK") != "1" || Session.NetSession.IsHost) return;
            var at = Replication.ShopSync.LookAt; var me = GameManager.Instance.PrimaryPlayer;
            if (at == null || !me) return;
            shopLooked = true;
            me.transform.position = at.Value + new Vector2(0f, -2f);
            me.specRigidbody.Reinitialize();
            Plugin.Log.LogInfo("Harness: guest looks at the restocked item at " + at.Value);
        }

        float nextPedestalLog;
        void PedestalLog()
        {
            if (Time.realtimeSinceStartup < nextPedestalLog) return;
            nextPedestalLog = Time.realtimeSinceStartup + 10f;
            var sb = new System.Text.StringBuilder();
            foreach (var r in Object.FindObjectsOfType<RewardPedestal>())
            {
                if (!r) continue;
                bool drawn = r.sprite && r.sprite.renderer && r.sprite.renderer.enabled;
                sb.Append($" [{(Vector2)r.transform.position} drawn={drawn} active={r.gameObject.activeInHierarchy} item={(r.contents ? r.contents.DisplayName : "-")} picked={r.pickedUp}]");
            }
            if (sb.Length > 0) Plugin.Log.LogInfo("Harness: pedestals" + sb);
        }

        // GO_BREACH_AT=seconds (host): quit the run back to the Breach, as the pause menu's Quit does (a reported bug:
        // the guest then sat on an endless loading screen).
        bool breachDone;
        void BreachReturn()
        {
            if (breachDone || !Session.NetSession.IsHost || Game.SceneObjects.InBreach) return;
            if (!float.TryParse(System.Environment.GetEnvironmentVariable("GO_BREACH_AT"), out var at) || Time.realtimeSinceStartup < at) return;
            if (GameManager.Instance.IsLoadingLevel || GameManager.Instance.PrimaryPlayer == null) return;
            breachDone = true;
            Plugin.Log.LogInfo("Harness: host quits to the Breach");
            GameManager.Instance.DelayedReturnToFoyer(0.5f);
        }

        // GO_STATUE_SWAP=PlayerBullet (client): 8 s after both players stand in the Breach, swap to that character at its
        // statue, as a player walking up to it would (a reported bug: the host then teleported the guest into a wall).
        float statueSince = -1f;
        bool statueDone;
        void StatueSwap()
        {
            var want = System.Environment.GetEnvironmentVariable("GO_STATUE_SWAP");
            if (statueDone || string.IsNullOrEmpty(want) || Session.NetSession.IsHost || !Game.SceneObjects.InBreach) return;
            if (GameManager.Instance.PrimaryPlayer == null || Game.PlayerSlots.ForSlot(0) == null) { statueSince = -1f; return; }
            if (statueSince < 0) statueSince = Time.realtimeSinceStartup;
            if (Time.realtimeSinceStartup - statueSince < 8f) return;
            statueDone = true;
            foreach (var f in Object.FindObjectsOfType<FoyerCharacterSelectFlag>())
                if (!f.IsCoopCharacter && !f.IsAlternateCostume && f.CharacterPrefabPath != null && f.CharacterPrefabPath.EndsWith(want))
                {
                    Plugin.Log.LogInfo($"Harness: statue swap to {want} at {(Vector2)GameManager.Instance.PrimaryPlayer.transform.position}");
                    Game.SceneObjects.Breach.StartCoroutine(Game.SceneObjects.Breach.OnSelectedCharacter(0.05f, f));
                    return;
                }
            Plugin.Log.LogInfo("Harness: statue swap found no statue for " + want);
        }

        int swapStep;
        float swapSince = -1f, titleSeen = -1f;

        /// <summary>
        /// GO_SWAP_TEST=1 (client): 6 s after both players exist, take the Breach door back to character select,
        /// pick the Convict, and check that the host sees the new character.
        /// </summary>
        float swapLog;
        void SwapTest(float t)
        {
            if (swapStep >= 2 || Session.NetSession.IsHost || System.Environment.GetEnvironmentVariable("GO_SWAP_TEST") != "1") return;
            if (swapStep == 1)
            {
                // Real time: game time stands still on the title screen.
                if (Time.realtimeSinceStartup - swapLog > 5f)
                {
                    swapLog = Time.realtimeSinceStartup;
                    // The character select door goes through the title screen: press New Game like a player would.
                    var title = Object.FindObjectOfType<MainMenuFoyerController>();
                    if (!title || !Foyer.DoMainMenu || GameManager.Instance.IsLoadingLevel) titleSeen = -1f;
                    else if (titleSeen < 0) titleSeen = Time.realtimeSinceStartup;
                    // Pressing New Game the moment the title scene appears can freeze the game: wait like a player would.
                    if (titleSeen >= 0 && Time.realtimeSinceStartup - titleSeen > 4f)
                    {
                        Plugin.Log.LogInfo("Harness: title screen, pressing New Game");
                        HarmonyLib.AccessTools.Method(typeof(MainMenuFoyerController), "OnNewGameSelected").Invoke(title, new object[] { null, null });
                    }
                    Plugin.Log.LogInfo($"Harness: swap waiting breach={Game.SceneObjects.InBreach} player={(GameManager.Instance.PrimaryPlayer != null)} loading={GameManager.Instance.IsLoadingLevel} selecting={GameManager.Instance.IsSelectingCharacter}");
                }
                if (Game.SceneObjects.InBreach && GameManager.Instance.PrimaryPlayer == null && !GameManager.Instance.IsLoadingLevel)
                {
                    swapStep = 2;
                    System.Environment.SetEnvironmentVariable("GO_CHAR", "PlayerConvict");
                    Plugin.Instance.StartCoroutine(QuickStart.Run());
                    Plugin.Log.LogInfo("Harness: back at character select, picking the Convict");
                }
                return;
            }
            if (!Game.SceneObjects.InBreach || GameManager.Instance.AllPlayers.Length < 2 || Game.PlayerSlots.Local == null) return;
            if (swapSince < 0) swapSince = t;
            if (t - swapSince < 6f) return;
            FoyerGungeonDoor door = null;
            foreach (var d in Object.FindObjectsOfType<FoyerGungeonDoor>()) if (d && d.LoadsCharacterSelect) { door = d; break; }
            if (door == null) { Plugin.Log.LogWarning("Harness: no character select door"); swapStep = 2; return; }
            swapStep = 1;
            Plugin.Log.LogInfo("Harness: walking into the character select door");
            HarmonyLib.AccessTools.Method(typeof(FoyerGungeonDoor), "OnTriggered").Invoke(door, new object[] { Game.PlayerSlots.Local.specRigidbody, door.specRigidbody, null });
        }

        bool typeDone;
        bool menuDone, pauseDone, pauseClosed;

        void UiHooks(float t)
        {
            // Real keys (the key test sends Windows key messages): what Unity and the game's input see.
            if (DevEnv.IsHarness) foreach (var k in new[] { KeyCode.D, KeyCode.A, KeyCode.Escape })
                if (UnityEngine.Input.GetKeyDown(k))
                {
                    var p0 = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
                    var bi = p0 ? BraveInput.GetInstanceForPlayer(p0.PlayerIDX) : null;
                    Plugin.Log.LogInfo($"KEY {k} down; game move={(bi != null && bi.ActiveActions != null ? bi.ActiveActions.Move.Vector.ToString() : "-")} device={(bi != null && bi.ActiveActions != null && bi.ActiveActions.Device != null ? bi.ActiveActions.Device.Name : "-")} last={InControl.InputManager.ActiveDevice?.Name}");
                }
            float m = EnvSec("GO_MENU_AT"), p = EnvSec("GO_PAUSE_AT");
            float ty = EnvSec("GO_TYPE_AT");
            if (!typeDone && ty > 0 && t >= ty) { typeDone = true; Session.OnlineMenu.Instance?.StartTypingForTest(); Plugin.Log.LogInfo("Harness: typing a code"); }
            if (!menuDone && m > 0 && t >= m) { menuDone = true; Session.OnlineMenu.Instance?.Open(); Plugin.Log.LogInfo("Harness: online menu opened"); }
            if (menuDone && m > 0 && t >= m + 4 && Session.OnlineMenu.IsOpen) Session.OnlineMenu.Instance.Close();
            if (!pauseDone && p > 0 && t >= p && GameManager.Instance.PrimaryPlayer != null) { pauseDone = true; GameManager.Instance.Pause(); Plugin.Log.LogInfo("Harness: pause menu opened"); }
            if (pauseDone && !pauseClosed && t >= p + 4) { pauseClosed = true; GameManager.Instance.Unpause(); }
        }

        static float EnvSec(string k)
        {
            var v = System.Environment.GetEnvironmentVariable(k);
            return v != null && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : -1f;
        }

        float floorSince = -1f;
        string floorScene;

        /// <summary>Harness host (GO_FLOOR_EVERY=seconds): move on to the next floor on a timer, re-run the fight setup on each.</summary>
        bool holdFloor;
        Dungeonator.RoomHandler classicRoom;
        PlayerController classicOutside; // a test found what it needs on this floor: stop cycling

        void HostFloorCycle(float t)
        {
            var v = System.Environment.GetEnvironmentVariable("GO_FLOOR_EVERY");
            if (v == null || holdFloor || !Session.NetSession.IsHost || Game.SceneObjects.InBreach || GameManager.Instance.IsLoadingLevel) return;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + GameManager.Instance.CurrentFloor;
            if (scene != floorScene) { floorScene = scene; floorSince = t; fightDone = false; fightAt = -1f; return; }
            if (!float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var every)) return;
            if (t - floorSince < every) return;
            floorSince = t + 1000f;
            Plugin.Log.LogInfo($"Harness: next floor (was floor {GameManager.Instance.CurrentFloor})");
            GameManager.Instance.DelayedLoadNextLevel(0.5f);
        }

        bool lowHpDone;
        float pitAt = -1f;
        bool pitDone, pitNone;
        int pitFloor;

        /// <summary>
        /// GO_PIT_TEST=1: each machine walks its own player onto the middle of a real pit once the floor is ready
        /// (client after 8 s, host after 20 s). Vanilla pit detection decides; the other machine must follow.
        /// </summary>
        void PitTest(float t)
        {
            if (System.Environment.GetEnvironmentVariable("GO_PIT_TEST") != "1") return;
            // No pit on a floor: try again on the next one (run with GO_FLOOR_EVERY).
            if (pitNone && GameManager.Instance.CurrentFloor != pitFloor) { pitNone = false; pitDone = false; pitAt = -1f; }
            if (pitDone) return;
            var ws = Replication.WorldSync.Instance;
            var me = Game.PlayerSlots.Local;
            if (!Session.NetSession.Active || Game.SceneObjects.InBreach || ws == null || me == null) return;
            if (Session.NetSession.IsHost ? ws.ReadyPeers == 0 : !ws.FloorReady) return;
            if (pitAt < 0) { pitAt = t + (Session.NetSession.IsHost ? 20f : 8f); return; }
            if (t < pitAt) return;
            pitDone = true;
            var d = GameManager.Instance.Dungeon.data;
            Vector2 mine = me.transform.position;
            IntVector2 best = IntVector2.Zero; float bestD = float.MaxValue;
            for (int x = 2; x < d.Width - 2; x++)
                for (int y = 2; y < d.Height - 2; y++)
                {
                    bool all = true;
                    for (int dx = -1; dx <= 1 && all; dx++)
                        for (int dy = -1; dy <= 1 && all; dy++)
                        {
                            var c = d.cellData[x + dx][y + dy];
                            all = c != null && c.type == Dungeonator.CellType.PIT && !c.fallingPrevented;
                        }
                    if (!all) continue;
                    float dist = (new Vector2(x, y) - mine).sqrMagnitude;
                    if (dist < bestD) { bestD = dist; best = new IntVector2(x, y); }
                }
            if (bestD == float.MaxValue) { Plugin.Log.LogWarning("Harness: no pit on this floor"); pitNone = true; pitFloor = GameManager.Instance.CurrentFloor; return; }
            if (Session.NetSession.IsHost) holdFloor = true;
            Plugin.Log.LogInfo($"Harness: stepping onto the pit at {best} as " + (Session.NetSession.IsHost ? "host" : "client"));
            me.WarpToPoint(best.ToVector2() + new Vector2(0.5f, 0.2f));
        }

        /// <summary>Harness client (GO_LOW_HP=1): once on a floor, drop to half a heart so the next hit kills.</summary>
        void ClientLowHp()
        {
            if (lowHpDone || System.Environment.GetEnvironmentVariable("GO_LOW_HP") != "1") return;
            if (!Session.NetSession.Active || Session.NetSession.IsHost || Game.SceneObjects.InBreach) return;
            var ws = Replication.WorldSync.Instance;
            var me = GameManager.Instance.PrimaryPlayer;
            if (ws == null || !ws.FloorReady || me == null) return;
            lowHpDone = true;
            // Through a real hit, so it is reported to the host like any other.
            me.healthHaver.Armor = 0f;
            // Player hits are capped at half a heart: set one heart, then take one reported half-heart hit.
            me.healthHaver.ForceSetCurrentHealth(1f);
            me.healthHaver.ApplyDamage(0.5f, Vector2.zero, "harness", CoreDamageTypes.None, DamageCategory.Normal, true);
            Plugin.Log.LogInfo("Harness: client at half a heart");
        }

        bool forcedBlack, reportSent, hudTested;
        int couchStep; float couchAt;
        /// <summary>Harness (GO_COUCH_TEST=prefab, no session): couch co-op player two joins at the Cultist, then talks
        /// to that Gungeoneer's statue and must become them (player one stays), then the run starts.</summary>
        void CouchTest()
        {
            var want = System.Environment.GetEnvironmentVariable("GO_COUCH_TEST");
            if (want == null || couchStep >= 4) return;
            var gm = GameManager.Instance;
            float now = Time.realtimeSinceStartup;
            if (couchStep == 0)
            {
                if (!Game.SceneObjects.InBreach || gm.PrimaryPlayer == null) return;
                if (couchAt == 0) { couchAt = now + 5f; return; }
                if (now < couchAt) return;
                couchStep = 4;
                foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>())
                    foreach (var st in fsm.FsmStates)
                        foreach (var act in st.Actions)
                            if (act is HutongGames.PlayMaker.Actions.ChangeCoopMode cm && cm.TargetCoopMode && !cm.IsTestCoopValid && couchStep == 4)
                            {
                                fsm.Fsm.SetState(st.Name);
                                couchStep = 1; couchAt = now + 6f;
                            }
                if (couchStep == 4) Plugin.Log.LogWarning("Harness: no co-op join conversation found");
                return;
            }
            if (now < couchAt) return;
            string p1 = gm.PrimaryPlayer ? gm.PrimaryPlayer.gameObject.name : "none";
            string p2 = gm.SecondaryPlayer ? gm.SecondaryPlayer.gameObject.name : "none";
            if (couchStep == 1)
            {
                int shown = 0; foreach (var f in Object.FindObjectsOfType<FoyerCharacterSelectFlag>()) if (!f.IsCoopCharacter) shown++;
                Plugin.Log.LogInfo($"Harness: couch joined, p1={p1} p2={p2} statues out={shown}");
                couchStep = 4;
                foreach (var fsm in Object.FindObjectsOfType<PlayMakerFSM>())
                    foreach (var st in fsm.FsmStates)
                        foreach (var act in st.Actions)
                            if (act is HutongGames.PlayMaker.Actions.ChangeToNewCharacter ch && ch.PlayerPrefabPath != null && ch.PlayerPrefabPath.EndsWith(want) && couchStep == 4)
                            {
                                var talk = fsm.GetComponent<TalkDoerLite>();
                                if (talk) talk.CompletedTalkingPlayer = System.Environment.GetEnvironmentVariable("GO_COUCH_TALKER") == "1" ? gm.PrimaryPlayer : gm.SecondaryPlayer; // what the end of a real conversation leaves
                                Plugin.Log.LogInfo("Harness: player two talks to " + fsm.gameObject.name);
                                fsm.Fsm.SetState(st.Name);
                                couchStep = 2; couchAt = now + 6f;
                            }
                if (couchStep == 4) Plugin.Log.LogWarning("Harness: no statue for " + want);
            }
            else if (couchStep == 2)
            {
                int shown = 0; foreach (var f in Object.FindObjectsOfType<FoyerCharacterSelectFlag>()) if (!f.IsCoopCharacter) shown++;
                Plugin.Log.LogInfo($"Harness: after the statue, p1={p1} p2={p2} statues out={shown} players={gm.AllPlayers.Length}");
                Pixelator.Instance.FadeToBlack(0.5f);
                gm.DelayedLoadNextLevel(0.75f);
                if (Game.SceneObjects.Breach) Game.SceneObjects.Breach.OnDepartedFoyer();
                couchStep = 3; couchAt = now + 25f;
            }
            else if (couchStep == 3)
            {
                Plugin.Log.LogInfo($"Harness: couch on the floor p1={p1} p2={p2} type={gm.CurrentGameType} players={gm.AllPlayers.Length}");
                couchStep = 4;
            }
        }

        /// <summary>Harness (GO_FORCE_BLACK=second): leave this screen faded to black like a lost fade-in.</summary>
        void ForceBlack(float t)
        {
            var v = System.Environment.GetEnvironmentVariable("GO_FORCE_BLACK");
            if (forcedBlack || v == null || Pixelator.Instance == null || t < float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)) return;
            forcedBlack = true;
            Pixelator.Instance.fade = 0f;
            Plugin.Log.LogInfo("Harness: screen forced to black");
        }

        bool hostLowHpDone;
        float hostHitAt;
        int hostHits;

        /// <summary>Harness host (GO_HOST_DIES=1): once the guest is a ghost, the host drops to half a heart and takes a
        /// hit, so both players are down. Both machines must go to game over.</summary>
        void HostDiesAfterGuest()
        {
            if (hostLowHpDone || System.Environment.GetEnvironmentVariable("GO_HOST_DIES") != "1") return;
            if (!Session.NetSession.Active || !Session.NetSession.IsHost || Game.SceneObjects.InBreach) return;
            var me = GameManager.Instance.PrimaryPlayer;
            bool guestDown = false;
            foreach (var kv in Game.PlayerSlots.Remotes) if (kv.Value && kv.Value.IsGhost) guestDown = true;
            if (me == null || !guestDown || Time.realtimeSinceStartup < hostHitAt) return;
            if (me.healthHaver.IsDead || hostHits >= 20) { hostLowHpDone = true; return; }
            hostHitAt = Time.realtimeSinceStartup + 1f; hostHits++; // retried: a hit during i-frames does nothing
            me.healthHaver.IsVulnerable = true; // the fight setup made both players invulnerable
            me.healthHaver.Armor = 0f;
            me.healthHaver.ForceSetCurrentHealth(0.5f);
            me.healthHaver.ApplyDamage(0.5f, Vector2.zero, "harness", CoreDamageTypes.None, DamageCategory.Normal, true);
            Plugin.Log.LogInfo($"Harness: host takes the last hit after the guest went down hp={me.healthHaver.GetCurrentHealth()} armor={me.healthHaver.Armor} vulnerable={me.healthHaver.IsVulnerable} dodge={me.IsDodgeRolling} invuln={me.healthHaver.PreventAllDamage}");
        }

        float buyAt = -1f;
        bool bought;

        /// <summary>Harness client (GO_TEST_BUY=1): buy the nearest shop item once, through the normal request path.</summary>
        void ClientTestBuy(float t)
        {
            if (bought || System.Environment.GetEnvironmentVariable("GO_TEST_BUY") != "1") return;
            if (!Session.NetSession.Active || Session.NetSession.IsHost) return;
            var me = GameManager.Instance.PrimaryPlayer;
            if (me == null || Game.SceneObjects.InBreach) return;
            ShopItemController best = null;
            float bestD = 36f;
            foreach (var si in FindObjectsOfType<ShopItemController>())
            {
                if (!si || !si.item || si.Acquired) continue;
                float d = ((Vector2)si.transform.position - me.CenterPosition).sqrMagnitude;
                if (d < bestD) { bestD = d; best = si; }
            }
            if (best == null) return;
            if (buyAt < 0) { buyAt = t + 5f; return; }
            if (t < buyAt) return;
            bought = true;
            Plugin.Log.LogInfo($"Harness: client buying item {best.item.PickupObjectId} for {best.CurrentPrice}");
            best.Interact(me);
        }

        float fightAt = -1f;
        bool fightDone;

        static readonly bool keepAlive = System.Environment.GetEnvironmentVariable("GO_KEEP_ALIVE") == "1";
        float nextKeepAlive;

        /// <summary>
        /// Harness, long soaks (GO_KEEP_ALIVE=1): every machine keeps its own player invulnerable on every floor, so a
        /// 30-minute run is not cut short by a game over (the fight setup's invulnerability ends with the floor).
        /// </summary>
        void KeepAlive(float t)
        {
            if (!keepAlive || t < nextKeepAlive) return;
            nextKeepAlive = t + 1f;
            var me = GameManager.HasInstance ? GameManager.Instance.PrimaryPlayer : null;
            if (me && me.healthHaver && me.healthHaver.IsVulnerable) me.healthHaver.IsVulnerable = false;
        }

        /// <summary>
        /// Harness host only (GO_FIGHT=together|split): once the client is ready on floor 1, make players
        /// invulnerable and warp them into rooms that still have enemies.
        /// </summary>
        void HostFightSetup(float t)
        {
            string mode = System.Environment.GetEnvironmentVariable("GO_FIGHT");
            if (fightDone || mode == null || !Session.NetSession.IsHost) return;
            var ws = Replication.WorldSync.Instance;
            var gm = GameManager.Instance;
            if (ws == null || ws.ReadyPeers < DevEnv.ExpectPlayers - 1 || gm.Dungeon == null || Game.SceneObjects.InBreach) return;
            if (fightAt < 0) { fightAt = t + 3f; return; }
            if (t < fightAt) return;
            fightDone = true;
            if (mode == "shop")
            {
                ShopItemController any = null;
                foreach (var si in Game.SceneObjects.All<ShopItemController>()) if (si && si.item && !si.Acquired) { any = si; Plugin.Log.LogInfo($"Harness: shop item {si.item.PickupObjectId} acquired={si.Acquired} price={si.CurrentPrice}"); break; }
                if (any == null)
                {
                    // Shop stock can appear a little after the floor; keep looking for 20 s.
                    if (t - fightAt < 20f) { fightDone = false; return; }
                    Plugin.Log.LogWarning("Harness: no shop on this floor");
                    return;
                }
                int k = 0;
                foreach (var p in gm.AllPlayers)
                {
                    if (!p) continue;
                    p.healthHaver.IsVulnerable = false;
                    p.carriedConsumables.Currency = 300;
                    p.WarpToPoint((Vector2)any.transform.position + new Vector2(k * 1.5f - 0.75f, -2.5f));
                    k++;
                }
                Plugin.Log.LogInfo("Harness: warped players into the shop with 300 casings");
                return;
            }
            if (mode == "classic")
            {
                // GO_PULL_WHO=host|guest: who walks into the fight. The other one is left outside.
                bool hostFights = System.Environment.GetEnvironmentVariable("GO_PULL_WHO") != "guest";
                Dungeonator.RoomHandler fightRoom = null;
                foreach (var room in gm.Dungeon.data.rooms)
                {
                    var list = room.GetActiveEnemies(Dungeonator.RoomHandler.ActiveEnemyType.RoomClear);
                    if (list != null && list.Count >= 2 && room != gm.Dungeon.data.Entrance && !room.GetRoomName().Contains("Cart")) { fightRoom = room; break; }
                }
                if (fightRoom == null) { Plugin.Log.LogWarning("Harness: no fight room"); return; }
                PlayerController fighter = null, outside = null;
                foreach (var p in gm.AllPlayers)
                {
                    if (!p) continue;
                    p.healthHaver.IsVulnerable = false;
                    bool isHost = p == Game.PlayerSlots.Local;
                    if (isHost == hostFights) fighter = p; else outside = p;
                }
                if (fighter == null || outside == null) return;
                fighter.WarpToPoint(fightRoom.GetCenteredVisibleClearSpot(2, 2).ToVector2());
                Plugin.Log.LogInfo($"Harness: CLASSIC {(hostFights ? "host" : "guest")} fights in {fightRoom.GetRoomName()}, the other waits outside");
                classicRoom = fightRoom;
                classicOutside = outside;
                return;
            }
            if (mode == "cart")
            {
                var list = new System.Collections.Generic.List<MineCartController>();
                foreach (var c in Game.SceneObjects.All<MineCartController>()) if (c && c.occupation != MineCartController.CartOccupationState.ENEMY) list.Add(c);
                if (list.Count == 0) { Plugin.Log.LogWarning("Harness: no minecart on this floor"); return; }
                int ci = 0;
                foreach (var p in gm.AllPlayers)
                {
                    if (!p) continue;
                    p.healthHaver.IsVulnerable = false;
                    var cart = list[ci % list.Count];
                    int n = gm.AllPlayers.Length;
                    float x = n <= 2 ? (ci == 0 ? -1.5f : 1.5f) : (ci - (n - 1) / 2f) * 1.5f; // more players: spread along the track
                    p.WarpToPoint(cart.specRigidbody.UnitCenter + new Vector2(x, -1.5f));
                    ci++;
                }
                holdFloor = true;
                Plugin.Log.LogInfo($"Harness: warped players next to minecarts ({list.Count} on this floor)");
                return;
            }
            if (mode == "boss")
            {
                Dungeonator.RoomHandler bossRoom = null;
                foreach (var room in gm.Dungeon.data.rooms)
                    if (room.area != null && room.area.PrototypeRoomCategory == PrototypeDungeonRoom.RoomCategory.BOSS) { bossRoom = room; break; }
                if (bossRoom == null) { Plugin.Log.LogWarning("Harness: no boss room"); return; }
                int bi = 0;
                foreach (var p in gm.AllPlayers)
                {
                    if (!p) continue;
                    p.healthHaver.IsVulnerable = false;
                    var spot = bossRoom.GetCenteredVisibleClearSpot(2, 2);
                    p.WarpToPoint(spot.ToVector2() + new Vector2(0.5f + bi, -3f));
                    bi++;
                }
                Plugin.Log.LogInfo("Harness: warped players into the boss room " + bossRoom.GetRoomName());
                return;
            }
            var rooms = new System.Collections.Generic.List<Dungeonator.RoomHandler>();
            foreach (var room in gm.Dungeon.data.rooms)
            {
                var list = room.GetActiveEnemies(Dungeonator.RoomHandler.ActiveEnemyType.RoomClear);
                // Minecart rooms are not replicated yet (carts drive player positions); keep the harness out of them.
                if (list != null && list.Count >= 2 && room != gm.Dungeon.data.Entrance && !room.GetRoomName().Contains("Cart")) rooms.Add(room);
            }
            rooms.Sort((x, y) => y.GetActiveEnemies(Dungeonator.RoomHandler.ActiveEnemyType.RoomClear).Count.CompareTo(x.GetActiveEnemies(Dungeonator.RoomHandler.ActiveEnemyType.RoomClear).Count));
            if (rooms.Count == 0) { Plugin.Log.LogWarning("Harness: no fight room"); return; }
            int i = 0;
            foreach (var p in gm.AllPlayers)
            {
                if (!p) continue;
                p.healthHaver.IsVulnerable = false;
                var room = rooms[Mathf.Min(mode == "split" ? i : 0, rooms.Count - 1)];
                var spot = room.GetCenteredVisibleClearSpot(2, 2);
                p.WarpToPoint(spot.ToVector2() + new Vector2(0.5f + i, 0.5f));
                Plugin.Log.LogInfo($"Harness: warped player {p.PlayerIDX} into {room.GetRoomName()} ({room.GetActiveEnemies(Dungeonator.RoomHandler.ActiveEnemyType.RoomClear).Count} enemies)");
                i++;
            }
        }

        void Awake()
        {
            if (!DevEnv.IsHarness) return;
            Mute();
            // Run at a chosen display rate (GO_FPS, default 60) instead of whatever the hidden desktop allows.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = DevEnv.Fps;
        }

        bool updateErrorLogged;

        /// <summary>slot:player id for everyone on this machine, local first.</summary>
        static string IdList()
        {
            var sb = new System.Text.StringBuilder();
            var me = Game.PlayerSlots.Local;
            if (me) sb.Append(Game.PlayerSlots.LocalSlot).Append(':').Append(me.PlayerIDX);
            foreach (var kv in Game.PlayerSlots.Remotes)
                if (kv.Value) sb.Append(';').Append(kv.Key).Append(':').Append(kv.Value.PlayerIDX);
            return sb.ToString();
        }

        void Update()
        {
            if (!DevEnv.IsHarness) return;
            // The swap test crosses the title screen, where the rest of the harness update can throw: keep it apart.
            try { SwapTest(Time.realtimeSinceStartup); } catch (System.Exception e) { Plugin.Log.LogInfo("Harness: swap step error " + e.Message); }
            try { StatueSwap(); } catch (System.Exception e) { Plugin.Log.LogInfo("Harness: statue swap error " + e.Message); }
            try { BreachReturn(); } catch (System.Exception e) { Plugin.Log.LogInfo("Harness: breach return error " + e.Message); }
            try { LookCheck.Tick(); } catch (System.Exception e) { Plugin.Log.LogInfo("LOOKCHECK error " + e.Message); }
            try { BossTour.Tick(); } catch (System.Exception e) { Plugin.Log.LogInfo("BOSSTOUR error " + e); }
            try { BossKill(); } catch (System.Exception e) { Plugin.Log.LogInfo("Harness: boss kill error " + e.Message); }
            try { DeathButton(); } catch (System.Exception e) { Plugin.Log.LogInfo("Harness: death button error " + e); }
            try { PedestalTake(); } catch (System.Exception e) { Plugin.Log.LogInfo("Harness: pedestal take error " + e.Message); }
            try { ShopLook(); } catch (System.Exception e) { Plugin.Log.LogInfo("Harness: shop look error " + e.Message); }
            try { PedestalLog(); } catch (System.Exception e) { Plugin.Log.LogInfo("Harness: pedestal log error " + e.Message); }
            try { HarnessUpdate(); }
            catch (System.Exception e)
            {
                // Early boot frames: the game is not up yet. Log once, keep going.
                if (!updateErrorLogged) { updateErrorLogged = true; Plugin.Log.LogInfo("Harness: status not ready yet (" + e.GetType().Name.Replace("Exception", "") + ")"); }
            }
        }

        void HarnessUpdate()
        {
            NetProfiler.Frame();
            float t = Time.realtimeSinceStartup;
            Mute();
            if (Application.targetFrameRate != DevEnv.Fps || QualitySettings.vSyncCount != 0) { QualitySettings.vSyncCount = 0; Application.targetFrameRate = DevEnv.Fps; }
            if (DevEnv.StatusFile != null && t >= nextStatus)
            {
                nextStatus = t + 0.5f;
                var gm = GameManager.HasInstance ? GameManager.Instance : null;
                string scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                int players = gm != null && gm.AllPlayers != null ? gm.AllPlayers.Length : 0;
                string pos = gm != null && gm.PrimaryPlayer != null ? gm.PrimaryPlayer.transform.position.ToString("F2") : "-";
                string net = "off";
                var s = Session.NetSession.Instance;
                if (Session.NetSession.Active)
                {
                    net = $"{(Session.NetSession.IsHost ? "host" : "client")} slot={Session.NetSession.LocalSlot} peers={s.Node.Connections.Count}";
                    foreach (var c in s.Node.Connections) net += $" [P{c.Slot + 1} rtt={c.RttMs:F1} loss={c.LossPercent:F1} sent={c.PacketsSent} recv={c.PacketsReceived} {c.RelStats}]";
                    if (s.InviteCodeText != null) net += " code=" + s.InviteCodeText;
                    foreach (var kv in Game.PlayerSlots.Remotes)
                    {
                        string rp = kv.Value ? kv.Value.transform.position.ToString("F2") : "dead";
                        net += $" remote{kv.Key}={rp}";
                        if (Replication.InputSync.Instance.Streams.TryGetValue(kv.Key, out var st))
                            net += $" in{kv.Key}[applied={st.Applied} starved={st.Starved} held={st.Held} merged={st.Merged} buf={st.BufferMs:F1}ms jit={st.JitterMs:F1}ms late={st.LastLateMs:F1}ms lag={st.PollLagMs:F1}ms{Replication.InputSync.E2eText(st)} lane={(Session.PeerLanes.IsDirect(kv.Key) ? "direct" : "relay")} fresh={st.FreshDirect}/{st.FreshRelay} last={st.Last}]";
                        if (kv.Value)
                        {
                            var bi = BraveInput.GetInstanceForPlayer(kv.Value.PlayerIDX);
                            var act = bi != null ? bi.ActiveActions : null;
                            var ob = HarmonyLib.Traverse.Create(kv.Value).Field("m_disableInput").Field("m_overrides").GetValue<System.Collections.IList>();
                            string keys = "";
                            if (ob != null) foreach (var o in ob) keys += HarmonyLib.Traverse.Create(o).Field("key").GetValue<string>() + ";";
                            net += $" p{kv.Key}overrides=[{keys}]";
                            net += $" p{kv.Key}state={kv.Value.CurrentInputState} acts={(act != null ? act.Move.Vector.ToString() + " dev=" + (act.Device != null ? act.Device.Name : "null") + " off=" + act.ForceDisable + " en=" + act.Enabled : "null")}";
                        }
                        net += $" sync{kv.Key}[{Replication.PlayerSync.Instance.Stats(kv.Key)}]";
                    }
                    var lb = BraveInput.GetInstanceForPlayer(0);
                    if (lb != null && lb.ActiveActions != null) net += $" localMove={lb.ActiveActions.Move.Vector} localSeq={Replication.InputSync.Instance.LocalSeq}";
                    var ws = Replication.WorldSync.Instance;
                    if (ws != null) net += $" world[ready={ws.ReadyPeers} tracked={ws.TrackedCount} replicas={ws.ReplicaCount} spawnsSent={ws.SpawnsSent} spawnsApplied={ws.SpawnsApplied} deathsSent={ws.DeathsSent} deathsApplied={ws.DeathsApplied} snaps={ws.SnapshotsApplied} localSuppressed={ws.LocalActorsSuppressed} spawnFail={ws.SpawnFailures} enemies={StaticReferenceManager.AllEnemies.Count}]";
                    var ps = Replication.ProjectileSync.Instance;
                    net += " draw[" + Replication.DrawSmoothing.Stats() + "]";
                    if (ps != null) net += $" proj[announced={ps.Announced} sent={ps.Sent} updates={ps.Updates} copies={ps.Copies} resent={ps.Resent} early={ps.EarlyDeaths} hitMe={ps.EarlyNearMe} suppressed={ps.LocalSuppressed} unknown={ps.UnknownPrefabs}]";
                    var pk = Replication.PickupSync.Instance;
                    if (pk != null) net += $" pick[ann={pk.PickupsAnnounced} copies={pk.PickupsCopied} purged={pk.LocalPurged} req={pk.Requests} gives={pk.Gives} givesApplied={pk.GivesApplied} chests={pk.ChestsMapped} chestOpened={pk.ChestsOpened}]";
                    var ck = Replication.Checksum.Instance;
                    if (ck != null) net += $" check[summaries={ck.Summaries} checks={ck.Checks} mismatches={ck.Mismatches} resyncs={ck.Resyncs} clean={ck.ConsecutiveClean}]";
                    var sh = Replication.ShopSync.Instance;
                    if (sh != null) net += $" shop[ann={sh.Announced} mapped={sh.Mapped} restocked={sh.Restocked} req={sh.Requests}]";
                    var inv = Replication.InventorySync.Instance;
                    if (inv != null) net += $" inv[sent={inv.Sent} applied={inv.Applied} added={inv.Added} removed={inv.Removed}]";
                    var vs = Replication.VitalsSync.Instance;
                    if (vs != null) net += $" vitals[hurtSent={vs.HurtsSent} hurtApplied={vs.HurtsApplied} sent={vs.VitalsSent} applied={vs.VitalsApplied} pits={vs.PitfallsApplied} cartRides={ScenarioRunner.CartRides} elevators={(Replication.PropSync.Instance != null ? Replication.PropSync.Instance.Elevators : 0)} watchdog={Game.InputWatchdog.Restores} stats={Replication.StatSync.Sent}/{Replication.StatSync.Applied} credits={(GameStatsManager.HasInstance ? GameStatsManager.Instance.GetPlayerStatValue(TrackedStats.META_CURRENCY) : -1)} kills={(GameStatsManager.HasInstance ? GameStatsManager.Instance.GetPlayerStatValue(TrackedStats.ENEMIES_KILLED) : -1)}]";
                    foreach (var kv in Game.PlayerSlots.Remotes) if (kv.Value) net += $" hpR{kv.Key}={kv.Value.healthHaver.GetCurrentHealth()}";
                    if (gm.PrimaryPlayer != null) net += $" money={gm.PrimaryPlayer.carriedConsumables.Currency} guns={gm.PrimaryPlayer.inventory.AllGuns.Count} passives={gm.PrimaryPlayer.passiveItems.Count}";
                    foreach (var kv in Game.PlayerSlots.Remotes) if (kv.Value) net += $" R{kv.Key}guns={kv.Value.inventory.AllGuns.Count} R{kv.Key}passives={kv.Value.passiveItems.Count}";
                    if (gm.PrimaryPlayer != null) net += $" ghost0={gm.PrimaryPlayer.IsGhost} dead0={gm.PrimaryPlayer.healthHaver.IsDead}";
                    foreach (var kv in Game.PlayerSlots.Remotes) if (kv.Value) net += $" ghostR{kv.Key}={kv.Value.IsGhost} deadR{kv.Key}={kv.Value.healthHaver.IsDead}";
                    net += $" hp0={(gm.PrimaryPlayer != null ? gm.PrimaryPlayer.healthHaver.GetCurrentHealth() : -1)}";
                    string rooms = "";
                    foreach (var pl in gm.AllPlayers) if (pl) rooms += (pl == gm.PrimaryPlayer ? "me" : "them") + ":" + (pl.CurrentRoom != null ? pl.CurrentRoom.GetRoomName().Replace(" ", "_") : "-") + (pl.CurrentRoom != null && pl.CurrentRoom.IsSealed ? "(sealed)" : "") + ";";
                    net += $" rooms[{rooms}] pulls={Camera.IndependentCamera.Pulls}";
                    net += $" syncLocal[{Replication.PlayerSync.Instance.Stats(Session.NetSession.LocalSlot)}] snaps={Replication.PlayerSync.Instance.SnapshotsApplied}";
                }
                string fp = Game.RunSync.Instance != null ? Game.RunSync.Instance.LastFingerprint : "-";
                string line = $"t={t:F1} frame={Time.frameCount} fps={1f / Mathf.Max(0.0001f, Time.smoothDeltaTime):F0} {NetProfiler.Summary()} scene={scene} floor=[{fp}] gm={gm != null} players={players} expect={DevEnv.ExpectPlayers} ids[{IdList()}] p0={pos} net={net}";
                try { File.WriteAllText(DevEnv.StatusFile, line); } catch { }
                if (Session.NetSession.Active && gm != null && gm.PrimaryPlayer != null)
                {
                    string tr = $"TRACE t={t:F1} local={gm.PrimaryPlayer.transform.position.x:F2},{gm.PrimaryPlayer.transform.position.y:F2} st={gm.PrimaryPlayer.CurrentInputState}";
                    if (gm.PrimaryPlayer.CurrentInputState != PlayerInputState.AllInput)
                    {
                        var lob = HarmonyLib.Traverse.Create(gm.PrimaryPlayer).Field("m_disableInput").Field("m_overrides").GetValue<System.Collections.IList>();
                        string lk = "";
                        if (lob != null) foreach (var o in lob) lk += HarmonyLib.Traverse.Create(o).Field("key").GetValue<string>() + ";";
                        var pp = gm.PrimaryPlayer;
                        tr += $" ov=[{lk}] raw={HarmonyLib.Traverse.Create(pp).Field("m_inputState").GetValue()}" +
                              $" fall={pp.IsFalling} cart={(bool)pp.currentMineCart} ghost={pp.IsGhost} dead={pp.healthHaver.IsDead} killcam={BossKillCam.BossDeathCamRunning}" +
                              $" noPause={gm.PreventPausing} paused={gm.IsPaused} loading={gm.IsLoadingLevel} menu={Session.OnlineMenu.IsOpen} wd={Game.InputWatchdog.Restores}";
                    }
                    foreach (var kv in Game.PlayerSlots.Remotes)
                        if (kv.Value) tr += $" r{kv.Key}={kv.Value.transform.position.x:F2},{kv.Value.transform.position.y:F2} st={kv.Value.CurrentInputState} mv={BraveInput.GetInstanceForPlayer(kv.Value.PlayerIDX).ActiveActions.Move.Vector}";
                    Plugin.Log.LogInfo(tr);
                }
            }
            if (DevEnv.StartRunAt >= 0 && Session.NetSession.IsHost && Game.SceneObjects.InBreach && (GameManager.Instance.AllPlayers.Length >= DevEnv.ExpectPlayers || (System.Environment.GetEnvironmentVariable("GO_SOLO_START") == "1" && GameManager.Instance.PrimaryPlayer != null)))
            {
                if (bothSince < 0) bothSince = t;
                if (t - bothSince >= DevEnv.StartRunAt)
                {
                    DevEnv.StartRunAt = -1f;
                    Plugin.Log.LogInfo("Harness: host leaving the Breach");
                    Pixelator.Instance.FadeToBlack(0.5f);
                    GameManager.Instance.DelayedLoadNextLevel(0.75f);
                    if (Game.SceneObjects.Breach) Game.SceneObjects.Breach.OnDepartedFoyer();
                }
            }
            UiDump.Tick();
            StyleGallery(t);
            UiTour(t);
            DoorTest(t);
            if (elevStep == 2 && GameManager.Instance.IsLoadingLevel) { elevStep = 3; Plugin.Log.LogInfo("Harness: elevator left, next floor loading"); }
            ElevatorTest(t);
            PitTest(t);
            HostFloorCycle(t);
            UiHooks(t);
            HostFightSetup(t);
            KeepAlive(t);
            ClientLowHp();
            HostDiesAfterGuest();
            ForceBlack(Time.realtimeSinceStartup);
            CouchTest();
            if (Game.NPlayers.HudPatches.LayoutTest && !hudTested && Game.SceneObjects.OnDungeonFloor && Time.realtimeSinceStartup > 30f) { hudTested = true; Game.NPlayers.HudPatches.TestExtend(); }
            var rep = System.Environment.GetEnvironmentVariable("GO_SEND_REPORT_AT");
            if (!reportSent && rep != null && Time.realtimeSinceStartup > float.Parse(rep, System.Globalization.CultureInfo.InvariantCulture)) { reportSent = true; Session.Reports.Save(); }
            ClientTestBuy(t);
            TimedShots(t);
            if (DevEnv.QuitAfter > 0 && t >= DevEnv.QuitAfter)
            {
                Plugin.Log.LogInfo("GO_QUIT_AFTER reached, quitting.");
                DevEnv.QuitAfter = -1;
                Application.Quit();
            }
        }
    }
}
