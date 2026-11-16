using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Safety net against a player left without control. Some rare chains (a pit fall interrupted by a floor change,
    /// for example) can leave the game's own input state at "no input" with nothing left to restore it. If your own
    /// character has had no control for 8 seconds while nothing legitimately holds it (no input lock of any kind,
    /// not falling, not in a cart, not a ghost, no boss kill cam, no loading, no pause, no menu), control is given back.
    /// </summary>
    internal sealed class InputWatchdog : MonoBehaviour
    {
        float since = -1f, fallSince = -1f;
        bool fallRescued;
        public static int Restores;

        static readonly System.Reflection.FieldInfo InputStateField = AccessTools.Field(typeof(PlayerController), "m_inputState");
        static readonly System.Reflection.FieldInfo DisableField = AccessTools.Field(typeof(PlayerController), "m_disableInput");
        float nextCheck;
        float blackSince = -1f, arrivalSince = -1f;
        bool stuckDone;

        static void HardFallRescue(PlayerController p)
        {
            try
            {
                p.StopAllCoroutines(); // the stuck respawn
                AccessTools.Field(typeof(PlayerController), "m_isFalling").SetValue(p, false);
                Vector2 at = p.transform.position;
                var room = p.CurrentRoom;
                if (room != null)
                {
                    var cell = room.GetNearestAvailableCell(at, new IntVector2(2, 3), Dungeonator.CellTypes.FLOOR);
                    if (cell.HasValue) at = cell.Value.ToVector2() + new Vector2(0f, 1f);
                }
                p.WarpToPoint(at);
                var r = AccessTools.Field(typeof(PlayerController), "m_renderer").GetValue(p) as Renderer;
                if (r) r.enabled = true;
                p.ToggleShadowVisiblity(true);
                SpriteOutlineManager.ToggleOutlineRenderers(p.sprite, true);
                AccessTools.Method(typeof(PlayerController), "TogglePitClipping").Invoke(p, new object[] { false });
                AccessTools.Property(typeof(PlayerController), "IgnoredByCamera").SetValue(p, false, null);
                p.specRigidbody.CollideWithTileMap = true;
                p.specRigidbody.CollideWithOthers = true;
                p.ToggleGunRenderers(true, string.Empty);
                p.ToggleHandRenderers(true, string.Empty);
                p.CurrentInputState = PlayerInputState.AllInput;
                Camera.IndependentCamera.Apply();
                Restores++;
                Plugin.Log.LogWarning("InputWatchdog: the pit respawn got stuck, put you back on the floor");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("InputWatchdog: hard fall rescue failed: " + e.Message); }
        }
        public static int ScreenRestores;

        /// <summary>
        /// A screen left fully faded to black while you have full control of your character (a fade whose fade-in
        /// never came) is faded back in after 3 seconds. Fades that belong to something (loading, pause, menus, kill
        /// cams, cutscenes, elevator rides) never get here because they take your control or pause the game.
        /// </summary>
        void CheckBlackScreen(PlayerController p, GameManager gm)
        {
            var px = Pixelator.Instance;
            bool black = px != null && px.fade < 0.05f;
            bool free = p.CurrentInputState == PlayerInputState.AllInput && !p.IsFalling && !p.healthHaver.IsDead
                        && !BossKillCam.BossDeathCamRunning && !gm.PreventPausing && !gm.IsSelectingCharacter;
            if (!black || !free) { blackSince = -1f; return; }
            if (blackSince < 0) { blackSince = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - blackSince < 3f) return;
            blackSince = -1f;
            ScreenRestores++;
            px.saturation = 1f;
            px.FadeToBlack(0.5f, true);
            Plugin.Log.LogWarning("InputWatchdog: the screen stayed black while you could move, faded it back in");
        }

        void Update()
        {
            // Four checks a second is plenty for an 8 s timeout, and keeps the cost near zero.
            if (Time.realtimeSinceStartup < nextCheck) return;
            nextCheck = Time.realtimeSinceStartup + 0.25f;
            if (!NetSession.Active || !GameManager.HasInstance) { since = -1f; return; }
            var gm = GameManager.Instance;
            var p = PlayerSlots.Local;
            if (p == null || gm.IsLoadingLevel || gm.IsPaused || OnlineMenu.IsOpen) { since = -1f; blackSince = -1f; arrivalSince = -1f; return; }
            CheckBlackScreen(p, gm); // the Breach too
            // A fall that never finishes (its coroutine was cut short) gets the game's own pit respawn after 10 s (5 s in
            // the Breach, where nothing falls to a next floor). The Breach too: a host who fell into a Breach pit stayed there.
            if (p.IsFalling && !p.healthHaver.IsDead)
            {
                if (fallSince < 0) fallSince = Time.realtimeSinceStartup;
                else if (!fallRescued && Time.realtimeSinceStartup - fallSince > (SceneObjects.InBreach ? 5f : 10f))
                {
                    fallRescued = true;
                    try
                    {
                        var cr = (System.Collections.IEnumerator)AccessTools.Method(typeof(PlayerController), "PitRespawn").Invoke(p, new object[] { Vector2.zero });
                        p.StartCoroutine(cr);
                        Restores++;
                        Plugin.Log.LogWarning("InputWatchdog: a fall never finished, respawned at the edge");
                    }
                    catch (System.Exception e) { Plugin.Log.LogWarning("InputWatchdog: pit respawn failed: " + e.Message); }
                }
                else if (fallRescued && Time.realtimeSinceStartup - fallSince > (SceneObjects.InBreach ? 9f : 16f))
                {
                    // The respawn itself got stuck waiting on something: end the fall here and stand on the floor.
                    fallSince = Time.realtimeSinceStartup;
                    HardFallRescue(p);
                }
            }
            else { fallSince = -1f; fallRescued = false; }
            if (SceneObjects.InBreach) { since = -1f; return; }
            // Harness: GO_STUCK_ARRIVAL_AT=seconds leaves an arrival elevator lock on, as a cut-short arrival does.
            if (Dev.DevEnv.IsHarness && !stuckDone && float.TryParse(System.Environment.GetEnvironmentVariable("GO_STUCK_ARRIVAL_AT"), out var stuckAt) && Time.realtimeSinceStartup >= stuckAt)
            {
                stuckDone = true;
                p.SetInputOverride("elevator arrival");
                Plugin.Log.LogInfo("InputWatchdog harness: left the arrival elevator lock on");
            }
            var raw = (PlayerInputState)InputStateField.GetValue(p);
            var dis = DisableField.GetValue(p) as OverridableBool;
            // The arrival elevator locks input and unlocks it from its animation; a dropout or reconnect during that
            // animation can cut it short and leave the lock on for the rest of the run. The animation takes about
            // 2 s, so 12 s after the floor finished loading the lock is stale: lift that one lock, nothing else.
            if (dis != null && dis.HasOverride("elevator arrival"))
            {
                if (arrivalSince < 0) arrivalSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - arrivalSince > 12f)
                {
                    arrivalSince = -1f;
                    p.ClearInputOverride("elevator arrival");
                    p.ToggleGunRenderers(true, string.Empty);
                    p.ToggleHandRenderers(true, string.Empty);
                    p.ToggleShadowVisiblity(true);
                    p.ToggleFollowerRenderers(true);
                    Restores++;
                    Plugin.Log.LogWarning("InputWatchdog: the arrival elevator never let you out, gave control back");
                }
            }
            else arrivalSince = -1f;
            bool locked = dis != null && dis.Value;
            bool held = locked || p.IsFalling || p.currentMineCart || p.IsGhost || p.healthHaver.IsDead
                        || BossKillCam.BossDeathCamRunning || gm.PreventPausing || p.IsInMinecart;
            if (raw == PlayerInputState.AllInput || held) { since = -1f; return; }
            if (since < 0) { since = Time.realtimeSinceStartup; return; }
            if (Time.realtimeSinceStartup - since < 8f) return;
            p.CurrentInputState = PlayerInputState.AllInput;
            Restores++;
            since = -1f;
            Plugin.Log.LogWarning($"InputWatchdog: gave control back (state was {raw} for 8 s with nothing holding it)");
        }
    }
}
