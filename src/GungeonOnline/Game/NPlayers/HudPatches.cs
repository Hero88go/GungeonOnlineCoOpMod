using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game.NPlayers
{
    /// <summary>
    /// HUD panels for players 3 and 4, made from the game's own two. The game keeps lists of heart, blank, ammo and
    /// item panels but only ever uses index 0 (player 1) and index 1 (everyone else). Extra players get clones of the
    /// matching panel placed under it, and while the game updates "player 2's" panel for one of them, the clone
    /// takes index 1's place for that call (a swap in, a swap back out). The floating "RELOAD" text stays with
    /// players 1 and 2 only.
    /// </summary>
    internal static class HudPatches
    {
        static readonly System.Reflection.FieldInfo barsField = AccessTools.Field(typeof(GameUIRoot), "m_extantReloadBars");
        static readonly System.Reflection.FieldInfo timersField = AccessTools.Field(typeof(GameUIRoot), "m_itemNameVisibilityTimers");
        static readonly System.Reflection.FieldInfo markersField = AccessTools.Field(typeof(Minimap), "m_playerMarkers");
        static readonly List<dfControl> clones = new List<dfControl>();

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GameUIRoot), nameof(GameUIRoot.ConvertCoreUIToCoopMode)), postfix: new HarmonyMethod(typeof(HudPatches), nameof(ConvertPostfix)));
            foreach (var name in new[] { nameof(GameUIRoot.UpdatePlayerHealthUI), nameof(GameUIRoot.UpdatePlayerBlankUI), nameof(GameUIRoot.UpdateGunData),
                                         nameof(GameUIRoot.UpdateItemData), nameof(GameUIRoot.SetAmmoCountColor), nameof(GameUIRoot.DisableCoopPlayerUI),
                                         nameof(GameUIRoot.ReenableCoopPlayerUI), nameof(GameUIRoot.GetReloadBarForPlayer) })
                h.Patch(AccessTools.Method(typeof(GameUIRoot), name),
                    prefix: new HarmonyMethod(typeof(HudPatches), nameof(SwapInPrefix)), postfix: new HarmonyMethod(typeof(HudPatches), nameof(SwapOutPostfix)));
            h.Patch(AccessTools.Method(typeof(GameUIRoot), nameof(GameUIRoot.InformNeedsReload)), prefix: new HarmonyMethod(typeof(HudPatches), nameof(ReloadLabelPrefix)));
            h.Patch(AccessTools.Method(typeof(PlayerController), "BraveOnLevelWasLoaded"), postfix: new HarmonyMethod(typeof(HudPatches), nameof(LevelLoadedPostfix)));
            h.Patch(AccessTools.Method(typeof(Minimap), nameof(Minimap.UpdatePlayerPositionExact)),
                prefix: new HarmonyMethod(typeof(HudPatches), nameof(MarkerSwapPrefix)), postfix: new HarmonyMethod(typeof(HudPatches), nameof(MarkerSwapPostfix)));
        }

        static int ExtraId(PlayerController p) => p && p.PlayerIDX >= 2 ? p.PlayerIDX : -1;

        // ---------- Making the panels ----------

        static dfControl Clone(dfControl template, Vector3 shift, List<dfControl> made)
        {
            if (template == null) throw new System.Exception("no template panel");
            var go = Object.Instantiate(template.gameObject);
            go.name = template.gameObject.name + " (player " + (clones.Count / 7 + 3) + ")";
            var c = go.GetComponent<dfControl>();
            made.Add(c);
            go.transform.parent = template.transform.parent;
            // HUD panels sit straight under the UI manager (no parent control) or inside a panel: add to whichever.
            if (template.Parent != null) template.Parent.AddControl(c);
            else { var mgr = template.GetManager(); if (mgr != null) mgr.AddControl(c); }
            c.RelativePosition = template.RelativePosition + shift;
            // Anchors were copied from the template: capture them again at the new spot, or the next layout pass
            // puts the clone back on top of the original.
            c.ResetLayout(true, true);
            c.IsVisible = template.IsVisible;
            Plugin.Log.LogInfo($"HUD: {go.name} at {c.RelativePosition} (template {template.RelativePosition}, anchor {template.Anchor}, parent {(template.Parent ? template.Parent.name : "manager")})");
            return c;
        }

        /// <summary>After the game lays out two players' HUD: make sure every player id has panels, then refresh.</summary>
        static void ConvertPostfix(GameUIRoot __instance)
        {
            try { Extend(__instance); }
            catch (System.Exception e) { Plugin.Log.LogWarning("HUD for players 3 and 4: " + e); }
            Sync();
        }

        /// <summary>Harness (GO_HUD_TEST=1): make and show the extra panels in a solo game, to check their placement.</summary>
        public static bool LayoutTest => LayoutTestCount > 0;
        /// <summary>GO_HUD_TEST=N (2 to 8; 1 means 4): HUD panels for that many players in a solo game.</summary>
        public static int LayoutTestCount
        {
            get
            {
                var v = System.Environment.GetEnvironmentVariable("GO_HUD_TEST");
                if (string.IsNullOrEmpty(v) || !int.TryParse(v, out int n) || n <= 0) return 0;
                return n == 1 ? 4 : Mathf.Clamp(n, 2, 8);
            }
        }

        public static void TestExtend()
        {
            if (!GameUIRoot.Instance) return;
            try { Extend(GameUIRoot.Instance); } catch (System.Exception e) { Plugin.Log.LogWarning("HUD test: " + e); }
            Sync();
        }

        static void Extend(GameUIRoot ui)
        {
            int want = LayoutTest ? LayoutTestCount : Players.Count;
            var bars = barsField.GetValue(ui) as List<GameUIReloadBarController>;
            while (ui.heartControllers.Count < want && ui.heartControllers.Count >= 2)
            {
                int k = ui.heartControllers.Count;    // the new id
                int side = k % 2;                     // 0 = under player 1 (left), 1 = under player 2 (right)
                var made = new List<dfControl>();
                try
                {
                    var hearts = ui.heartControllers[side].GetComponent<dfPanel>();
                    var blanks = ui.blankControllers[side].GetComponent<dfPanel>();
                    float top = Mathf.Min(hearts.RelativePosition.y, blanks.RelativePosition.y);
                    float bottom = Mathf.Max(hearts.RelativePosition.y + hearts.Height, blanks.RelativePosition.y + blanks.Height);
                    // Hearts and blanks go on the same rows as player 1's (or 2's), beside them: Sync places them.
                    var down = Vector3.zero;
                    var heartClone = Clone(hearts, down, made).GetComponent<GameUIHeartController>();
                    var blankClone = Clone(blanks, down, made).GetComponent<GameUIBlankController>();
                    // In co-op the game draws player 1's gun box with ammo panel 1 and player 2's with panel 0.
                    // The weapon and item panels are tall, bottom-anchored strips: stack by the gun box's own height.
                    var ammoCtl = ui.ammoControllers[side == 0 ? 1 : 0];
                    var ammo = ammoCtl.GetComponent<dfPanel>();
                    float boxH = ammoCtl.GunBoxSprite ? ammoCtl.GunBoxSprite.Height : 64f;
                    var up = new Vector3(0f, -(boxH + 8f), 0f);
                    var ammoClone = Clone(ammo, up, made).GetComponent<GameUIAmmoController>();
                    var gunLabel = (dfLabel)Clone(ui.gunNameLabels[side == 0 ? 1 : 0], up, made);
                    var item = ui.itemControllers[side].GetComponent<dfPanel>();
                    var itemClone = Clone(item, up, made).GetComponent<GameUIItemController>();
                    var itemLabel = (dfLabel)Clone(ui.itemNameLabels[side], up, made);
                    GameUIReloadBarController bar = null;
                    if (bars != null && ui.p_secondaryPlayerReloadBar)
                        bar = Clone(ui.p_secondaryPlayerReloadBar.GetComponent<dfControl>(), Vector3.zero, made).GetComponent<GameUIReloadBarController>();
                    if (!heartClone || !blankClone || !ammoClone || !itemClone) throw new System.Exception("a clone lost its controller");
                    // Everything was made: now it becomes player k's HUD.
                    ui.heartControllers.Add(heartClone); ui.blankControllers.Add(blankClone);
                    ui.ammoControllers.Add(ammoClone); ui.gunNameLabels.Add(gunLabel);
                    ui.itemControllers.Add(itemClone); ui.itemNameLabels.Add(itemLabel);
                    var timers = timersField.GetValue(ui);
                    if (timers is float[] arr && arr.Length <= k) { var bigger = new float[k + 1]; arr.CopyTo(bigger, 0); timersField.SetValue(ui, bigger); }
                    else if (timers is List<float> list) while (list.Count <= k) list.Add(0f);
                    if (bar != null) { if (bars.Count > k) bars[k] = bar; else bars.Add(bar); }
                    clones.AddRange(made);
                    Plugin.Log.LogInfo("HUD: panels made for player id " + k);
                }
                catch
                {
                    foreach (var c in made) if (c) Object.Destroy(c.gameObject);
                    throw;
                }
            }
        }

        public static readonly string[] TeammateHudNames = { "Auto", "Everything", "Hearts only", "Off" };
        static int lastHudMode = -1;

        /// <summary>0 everything, 1 hearts and blanks only, 2 nothing: the setting, with Auto resolved by player count.</summary>
        static int TeammateMode()
        {
            int v = Session.NetConfig.TeammateHud.Value;
            if (v == 0) return Players.Count > 2 ? 1 : 0;
            return v - 1;
        }

        /// <summary>
        /// Every frame: hide the parts of teammates' HUDs the setting leaves out (the game turns its own panels back
        /// on at times, so this keeps them off). Player 2's panels sit in the game's second slot (ammo and gun name
        /// in the first), players 3 and 4 in their own.
        /// </summary>
        public static void ApplyTeammateHud()
        {
            var ui = GameUIRoot.Instance;
            if (ui && ui.heartControllers != null) ApplyPortraitRoom(ui);
            // With five or more, the blocks line up with each other: keep placing them (a player who joins or gains
            // hearts later moves the ones beside them).
            if (ui && ui.heartControllers != null && ui.heartControllers.Count > 4 && Time.frameCount % 10 == 0) Sync();
            if (!ui || ui.heartControllers == null || !Session.NetSession.Active) { lastHudMode = -1; return; }
            int mode = TeammateMode();
            if (mode != lastHudMode)
            {
                bool wasHidden = lastHudMode > 0;
                lastHudMode = mode;
                if (wasHidden) { Restore(ui); Sync(); }
            }
            hiddenArt.Clear();
            if (mode == 0) return;
            HookArt();
            for (int id = 1; id < ui.heartControllers.Count; id++)
            {
                bool present = false;
                foreach (var p in Players.All) if (p.PlayerIDX == id) present = true;
                if (!present) continue;
                int gun = id == 1 ? 0 : id;
                Hide(At(ui.ammoControllers, gun)); Hide(At(ui.itemControllers, id));
                hiddenArt.Add(new KeyValuePair<GameUIAmmoController, GameUIItemController>(At(ui.ammoControllers, gun), At(ui.itemControllers, id)));
                HideLabel(At(ui.gunNameLabels, gun)); HideLabel(At(ui.itemNameLabels, id));
                if (mode == 2)
                {
                    Hide(At(ui.heartControllers, id)); Hide(At(ui.blankControllers, id));
                    // The heart and blank icons sit beside their panels, not in them.
                    foreach (var c in Icons(At(ui.heartControllers, id), At(ui.blankControllers, id))) HideLabel(c);
                }
            }
        }

        // ---------- Room for each player's face beside their hearts ----------

        /// <summary>HUD units the face takes (0 when portraits are off).</summary>
        public static float PortraitRoom;
        static readonly Dictionary<int, float> shiftApplied = new Dictionary<int, float>();
        static readonly Dictionary<int, Vector3> lastSet = new Dictionary<int, Vector3>();

        public static bool PortraitsOn => Session.NetSession.Active && Session.NetConfig.HudPortraits.Value;

        /// <summary>Face height compared with a heart.</summary>
        public const float PortraitScale = 1.1f;

        /// <summary>
        /// Players 1 and 2 sit at the screen edges: slide their hearts and blanks inward by one face's width, so the
        /// face fits on the outer side. Players 3 and 4 get the room through <see cref="PlaceBeside"/>.
        /// </summary>
        static void ApplyPortraitRoom(GameUIRoot ui)
        {
            float room = 0f;
            if (PortraitsOn)
            {
                foreach (var c in Icons(ui.heartControllers[0], null))
                {
                    if (!c.IsVisible) continue;
                    var sr = c.GetScreenRect();
                    if (sr.width < 1f) continue;
                    float unitsPerPx = c.Width / sr.width;
                    room = (sr.height * PortraitScale + 3 * Session.GungeonUI.Px) * unitsPerPx;
                    break;
                }
                if (room <= 0f) room = PortraitRoom; // no heart drawn this frame: keep the last room
            }
            PortraitRoom = room;
            // Only player 1's panels move (their face goes on the screen-edge side). Player 2's panels are tied to
            // the right edge and the game kept pulling them back, so their face sat half off the screen at times:
            // faces on the right half now go on the inner side of the hearts instead, where there is always room.
            for (int id = 0; id < 2 && id < ui.heartControllers.Count; id++)
            {
                float dx = id == 0 ? room : 0f;
                Shift(ui.heartControllers[id] ? ui.heartControllers[id].GetComponent<dfControl>() : null, Icons(ui.heartControllers[id], null), dx);
                Shift(ui.blankControllers[id] ? ui.blankControllers[id].GetComponent<dfControl>() : null, Icons(null, ui.blankControllers[id]), dx);
            }
        }

        static void Shift(dfControl panel, List<dfControl> icons, float dx)
        {
            if (!panel) return;
            int key = panel.GetInstanceID();
            shiftApplied.TryGetValue(key, out float applied);
            // The game put the panel back where it wants it: start counting from there.
            if (lastSet.TryGetValue(key, out var was) && (panel.RelativePosition - was).sqrMagnitude > 0.01f) applied = 0f;
            float delta = dx - applied;
            if (Mathf.Abs(delta) > 0.01f)
            {
                var by = new Vector3(delta, 0f, 0f);
                panel.RelativePosition += by;
                foreach (var c in icons) c.RelativePosition += by;
            }
            shiftApplied[key] = dx;
            lastSet[key] = panel.RelativePosition;
        }

        /// <summary>Screen rectangle (GUI coordinates) of a player's heart row, if it is showing.</summary>
        public static bool HeartRow(GameUIRoot ui, int id, out Rect row) => HeartRow(ui, id, out row, out _);

        /// <summary>Also the height of one heart: with many hearts the row wraps onto a second line, and a face sized
        /// to both lines no longer fit its place (it ran off the screen edge).</summary>
        public static bool HeartRow(GameUIRoot ui, int id, out Rect row, out float iconH)
        {
            row = default(Rect);
            iconH = 0f;
            if (!ui || ui.heartControllers == null || id < 0 || id >= ui.heartControllers.Count || !ui.heartControllers[id]) return false;
            var panel = ui.heartControllers[id].GetComponent<dfControl>();
            if (!panel || !panel.IsVisible) return false;
            float x0 = float.MaxValue, x1 = float.MinValue, y0 = float.MaxValue, y1 = float.MinValue;
            foreach (var c in Icons(ui.heartControllers[id], null))
            {
                if (!c.IsVisible) continue;
                var sr = c.GetScreenRect();
                x0 = Mathf.Min(x0, sr.xMin); x1 = Mathf.Max(x1, sr.xMax); y0 = Mathf.Min(y0, sr.yMin); y1 = Mathf.Max(y1, sr.yMax);
                if (iconH <= 0f) iconH = sr.height;
            }
            if (x1 <= x0) return false;
            row = Rect.MinMaxRect(x0, y0, x1, y1);
            return true;
        }

        static T At<T>(List<T> list, int i) where T : class => list != null && i < list.Count ? list[i] : null;

        static void Hide(Component c)
        {
            if (!c) return;
            var panel = c.GetComponent<dfControl>();
            if (panel && panel.IsVisible) panel.IsVisible = false;
        }

        static void HideLabel(dfControl c) { if (c && c.IsVisible) c.IsVisible = false; }

        /// <summary>
        /// The gun and item pictures are game sprites drawn over the boxes, not part of the panels: hiding a panel
        /// leaves them floating, so they are switched off (and back on) with it.
        /// </summary>
        static void Art(GameUIAmmoController ammo, GameUIItemController item, bool on)
        {
            if (ammo)
            {
                if (ammo.gunSprites != null) foreach (var sp in ammo.gunSprites) Show(sp, on);
                if (ammoOutlines(ammo) != null) foreach (var row in ammoOutlines(ammo)) if (row != null) foreach (var sp in row) Show(sp, on);
            }
            if (item)
            {
                Show(item.itemSprite, on);
                if (itemOutlines(item) != null) foreach (var sp in itemOutlines(item)) Show(sp, on);
            }
        }

        static readonly HarmonyLib.AccessTools.FieldRef<GameUIAmmoController, tk2dSprite[][]> ammoOutlines = HarmonyLib.AccessTools.FieldRefAccess<GameUIAmmoController, tk2dSprite[][]>("outlineSprites");
        static readonly HarmonyLib.AccessTools.FieldRef<GameUIItemController, tk2dSprite[]> itemOutlines = HarmonyLib.AccessTools.FieldRefAccess<GameUIItemController, tk2dSprite[]>("outlineSprites");

        // The game switches the gun and item pictures back on during its own frame, after ours: switch them off
        // again just before any camera draws.
        static readonly List<KeyValuePair<GameUIAmmoController, GameUIItemController>> hiddenArt = new List<KeyValuePair<GameUIAmmoController, GameUIItemController>>();
        static bool artHooked;

        static void HookArt()
        {
            if (artHooked) return;
            artHooked = true;
            UnityEngine.Camera.onPreCull += cam => { foreach (var kv in hiddenArt) Art(kv.Key, kv.Value, false); };
        }

        static void Show(tk2dBaseSprite sp, bool on)
        {
            if (sp && sp.renderer && sp.renderer.enabled != on) sp.renderer.enabled = on;
        }

        /// <summary>Back to everything: show player 2's panels again (players 3 and 4 follow <see cref="Sync"/>).</summary>
        static void Restore(GameUIRoot ui)
        {
            bool onFloor = GameManager.HasInstance && GameManager.Instance.CurrentLevelOverrideState != GameManager.LevelOverrideState.FOYER;
            bool two = false;
            foreach (var p in Players.All) if (p.PlayerIDX == 1) two = true;
            if (!onFloor || !two) return;
            foreach (var c in new Component[] { At(ui.ammoControllers, 0), At(ui.itemControllers, 1), At(ui.heartControllers, 1), At(ui.blankControllers, 1) })
            {
                var panel = c ? c.GetComponent<dfControl>() : null;
                if (panel) panel.IsVisible = true;
            }
            for (int id = 1; id < ui.heartControllers.Count; id++) foreach (var c in Icons(At(ui.heartControllers, id), At(ui.blankControllers, id))) c.IsVisible = true;
            for (int id = 1; id < ui.itemControllers.Count; id++) Art(At(ui.ammoControllers, id == 1 ? 0 : id), At(ui.itemControllers, id), true);
        }

        /// <summary>Extra panels show only while that player is in and off the Breach.</summary>
        public static void Sync()
        {
            var ui = GameUIRoot.Instance;
            if (!ui || ui.heartControllers == null) return;
            bool onFloor = GameManager.HasInstance && GameManager.Instance.CurrentLevelOverrideState != GameManager.LevelOverrideState.FOYER;
            for (int k = 2; k < ui.heartControllers.Count; k++)
            {
                bool present = LayoutTest;
                foreach (var p in Players.All) if (p.PlayerIDX == k) present = true;
                bool show = present && onFloor;
                ui.heartControllers[k].GetComponent<dfPanel>().IsVisible = show;
                ui.blankControllers[k].GetComponent<dfPanel>().IsVisible = show;
                if (k < ui.ammoControllers.Count) { var a = ui.ammoControllers[k].GetComponent<dfPanel>(); a.IsVisible = show; a.IsEnabled = show; }
                if (k < ui.itemControllers.Count) ui.itemControllers[k].GetComponent<dfPanel>().IsVisible = show;
                if (k < ui.gunNameLabels.Count && !show) ui.gunNameLabels[k].IsVisible = false;
                if (k < ui.itemNameLabels.Count && !show) ui.itemNameLabels[k].IsVisible = false;
                if (show) PlaceBeside(ui, k);
            }
        }

        /// <summary>
        /// Players 3 and 4 sit on player 1's and player 2's rows, beside them (toward the middle). With more than four,
        /// players 5 and 6 start a second row under players 3 and 4 (clear of your shell and key counters and of the
        /// minimap in the corners), and players 7 and 8 continue that row toward the middle. The HUD follows as hearts
        /// are gained or lost.
        /// </summary>
        static void PlaceBeside(GameUIRoot ui, int k)
        {
            int side = k % 2;
            bool underneath = k == 4 || k == 5;          // first in the second row: under player 3 or 4
            int before = k - 2;                           // the player this one lines up with
            float gap = 18f + PortraitRoom; // and room for this player's face, when portraits are on
            var rows = new[] { Icons(ui.heartControllers[before], ui.blankControllers[before]), Icons(ui.heartControllers[k], ui.blankControllers[k]) };
            // One shared edge for both rows (hearts and blanks), so each player's block reads as a column.
            if (!Edges(rows[0], out float l0, out float r0)) return;
            Edges(rows[1], out float l1, out float r1);
            var hp = ui.heartControllers[k].GetComponent<dfPanel>();
            var bp = ui.blankControllers[k].GetComponent<dfPanel>();
            if (r1 <= l1) { l1 = hp.GetAbsolutePosition().x; r1 = l1; } // no icons yet: place the empty panel
            float want = underneath ? (side == 0 ? l0 : r0 - (r1 - l1)) : side == 0 ? r0 + gap : l0 - gap - (r1 - l1);
            float dx = want - l1;
            float dy = 0f;
            var ap = ui.heartControllers[before].GetComponent<dfPanel>();
            // Heights from the panels' own positions: a panel moved this frame reports its new spot only there.
            if (underneath)
            {
                // Under player 3 or 4: their block's height (hearts and blanks) plus a little room.
                var abp = ui.blankControllers[before].GetComponent<dfPanel>();
                dy = abp.RelativePosition.y + abp.Height + 10f - hp.RelativePosition.y;
            }
            else if (k >= 6) dy = ap.RelativePosition.y - hp.RelativePosition.y; // same row as player 5 or 6
            if (k == 7 && Players.Count > 7 && Edges(Icons(ui.heartControllers[6], ui.blankControllers[6]), out float l6, out float r6) && want < r6 + gap)
            {
                // Players 7 and 8 would run into each other in the middle (lots of hearts or shields): player 8 goes
                // on a third row, under player 6.
                want = r0 - (r1 - l1);
                dx = want - l1;
                var b5 = ui.blankControllers[5].GetComponent<dfPanel>();
                dy = b5.RelativePosition.y + b5.Height + 10f - hp.RelativePosition.y;
            }
            if (k >= 4) KeepOnRow(ui, k, hp, bp);
            if (Mathf.Abs(dx) < 0.5f && Mathf.Abs(dy) < 0.5f) return;
            // The game places each icon once, from the panel's position: move the panels and the icons together.
            var shift = new Vector3(dx, dy, 0f);
            hp.RelativePosition += shift; bp.RelativePosition += shift;
            foreach (var c in rows[1]) c.RelativePosition += shift;
        }

        /// <summary>
        /// Second-row players: the game puts each heart, shield and blank in a little holder of its own and sets the
        /// shield's height inside it on every health change, which pulled shields back up to the first row. Any icon
        /// that is not on its panel's row is moved there through its holder.
        /// </summary>
        static void KeepOnRow(GameUIRoot ui, int k, dfPanel hp, dfPanel bp)
        {
            float tile = Pixelator.Instance ? Pixelator.Instance.CurrentTileScale : 3f;
            Fix(ui, Icons(ui.heartControllers[k], null), hp.GetAbsolutePosition().y, tile);
            Fix(ui, Icons(null, ui.blankControllers[k]), bp.GetAbsolutePosition().y, tile);
        }

        static void Fix(GameUIRoot ui, List<dfControl> icons, float rowY, float tile)
        {
            foreach (var c in icons)
            {
                float d = rowY - c.GetAbsolutePosition().y;
                if (Mathf.Abs(d) <= tile * 2f + 1f) continue; // on the row (a crest sits a little lower)
                var holder = ui.GetMotionGroupParent(c);
                if (holder) holder.RelativePosition += new Vector3(0f, d, 0f);
                else c.RelativePosition += new Vector3(0f, d, 0f);
            }
        }

        static List<dfControl> Icons(GameUIHeartController h, GameUIBlankController b)
        {
            var list = new List<dfControl>();
            if (h != null) { if (h.extantHearts != null) list.AddRange(h.extantHearts.ConvertAll(x => (dfControl)x)); if (h.extantArmors != null) list.AddRange(h.extantArmors.ConvertAll(x => (dfControl)x)); }
            if (b != null && b.extantBlanks != null) list.AddRange(b.extantBlanks.ConvertAll(x => (dfControl)x));
            list.RemoveAll(c => !c);
            return list;
        }

        /// <summary>Screen-space left and right edge of a set of HUD icons.</summary>
        static bool Edges(List<dfControl> icons, out float left, out float right)
        {
            left = float.MaxValue; right = float.MinValue;
            foreach (var c in icons)
            {
                if (!c.IsVisible) continue;
                float x = c.GetAbsolutePosition().x;
                left = Mathf.Min(left, x); right = Mathf.Max(right, x + c.Width);
            }
            return right > left;
        }

        // ---------- Routing the game's updates to the right panel ----------

        static void SwapLists(GameUIRoot ui, int k)
        {
            if (k >= ui.heartControllers.Count) return;
            Swap(ui.heartControllers, 1, k); Swap(ui.blankControllers, 1, k);
            Swap(ui.ammoControllers, 0, k); Swap(ui.gunNameLabels, 0, k);
            Swap(ui.itemControllers, 1, k); Swap(ui.itemNameLabels, 1, k);
            var bars = barsField.GetValue(ui) as List<GameUIReloadBarController>;
            if (bars != null) Swap(bars, 1, k);
            var timers = timersField.GetValue(ui);
            if (timers is float[] arr && arr.Length > k) { var t = arr[1]; arr[1] = arr[k]; arr[k] = t; }
            else if (timers is List<float> list) Swap(list, 1, k);
        }

        static void Swap<T>(List<T> list, int a, int b)
        {
            if (list == null || a >= list.Count || b >= list.Count) return;
            var t = list[a]; list[a] = list[b]; list[b] = t;
        }

        /// <summary>The player argument, wherever the patched method keeps it.</summary>
        static PlayerController PlayerArg(object[] args)
        {
            if (args != null) foreach (var a in args) if (a is PlayerController pc) return pc;
            return null;
        }

        static void SwapInPrefix(GameUIRoot __instance, object[] __args, out int __state)
        {
            __state = -1;
            if (!Players.NPlus) return;
            int k = ExtraId(PlayerArg(__args));
            if (k < 2 || k >= __instance.heartControllers.Count) return;
            SwapLists(__instance, k);
            __state = k;
        }

        static void SwapOutPostfix(GameUIRoot __instance, int __state)
        {
            if (__state >= 2) SwapLists(__instance, __state);
        }

        /// <summary>On a new floor the game refreshes player 2's HUD from player 1; refresh players 3 and 4 too.</summary>
        static void LevelLoadedPostfix(PlayerController __instance)
        {
            if (!Players.NPlus || !__instance.IsPrimaryPlayer || !GameUIRoot.Instance) return;
            var ui = GameUIRoot.Instance;
            foreach (var p in Players.All)
            {
                if (p.PlayerIDX < 2 || p.inventory == null) continue;
                try
                {
                    ui.UpdateGunData(p.inventory, 0, p);
                    ui.UpdateItemData(p, p.CurrentItem, p.activeItems);
                    if (p.healthHaver) ui.UpdatePlayerHealthUI(p, p.healthHaver);
                    ui.UpdatePlayerBlankUI(p);
                }
                catch (System.Exception e) { Plugin.Log.LogWarning("HUD refresh for player " + p.PlayerIDX + ": " + e.Message); }
            }
            Sync();
        }

        /// <summary>The floating "RELOAD" text is tied to the game's two label slots: skip it for players 3 and 4.</summary>
        static bool ReloadLabelPrefix(PlayerController attachPlayer) => !(Players.NPlus && ExtraId(attachPlayer) >= 2);

        static void MarkerSwapPrefix(Minimap __instance, PlayerController player, out int __state)
        {
            __state = -1;
            if (!Players.NPlus) return;
            int k = ExtraId(player);
            var markers = markersField.GetValue(__instance) as System.Collections.IList;
            if (k < 2 || markers == null || k >= markers.Count) return;
            var t = markers[1]; markers[1] = markers[k]; markers[k] = t;
            __state = k;
        }

        static void MarkerSwapPostfix(Minimap __instance, int __state)
        {
            if (__state < 2) return;
            var markers = markersField.GetValue(__instance) as System.Collections.IList;
            if (markers == null || __state >= markers.Count) return;
            var t = markers[1]; markers[1] = markers[__state]; markers[__state] = t;
        }
    }
}
