using System.Collections.Generic;
using GungeonOnline.Session;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Where is my friend? With independent cameras the other player is often off your screen. Two optional
    /// pieces, both drawn with the game's own sprites and fonts:
    /// - Locator: when your friend is off screen, a marker at the screen edge points the way.
    /// - Name tag: your friend's name over their character while they are on screen.
    /// Each has five styles (Settings), 0 = off.
    /// </summary>
    internal sealed class PartnerUI : MonoBehaviour
    {
        public static readonly string[] LocatorNames = { "Off", "Speech bubble", "Gun sight", "Minimap head", "Orbit arrow", "Pickup tag" };
        public static readonly string[] ColorNames = { "Gold", "White", "Sky", "Rose", "Mint", "Orange", "Violet", "Red" };
        static readonly Color[] NameColors = { new Color(1f, 0.82f, 0.33f), new Color(0.96f, 0.95f, 0.92f), new Color(0.48f, 0.78f, 1f), new Color(1f, 0.55f, 0.66f), new Color(0.5f, 0.95f, 0.66f), new Color(1f, 0.6f, 0.25f), new Color(0.76f, 0.58f, 1f), new Color(1f, 0.36f, 0.32f) };
        public static readonly string[] LabelNames = { "Icon only", "Name", "Name and distance" };
        public static readonly string[] ReloadNames = { "Everyone", "Only mine", "Off" };

        public static void Install(HarmonyLib.Harmony h)
        {
            var m = HarmonyLib.AccessTools.Method(typeof(GameUIRoot), "InformNeedsReload");
            if (m != null) h.Patch(m, prefix: new HarmonyLib.HarmonyMethod(typeof(PartnerUI), nameof(ReloadPrefix)));
        }

        /// <summary>Setting: hide the game's Reload text over players (everyone's, or just your friends').</summary>
        static bool ReloadPrefix(PlayerController attachPlayer)
        {
            int mode = NetConfig.ReloadText.Value;
            if (mode == 2) return false;
            if (mode == 1 && attachPlayer != null && attachPlayer != PlayerSlots.Local) return false;
            return true;
        }

        /// <summary>The camera's picture on screen, in GUI coordinates (the game letterboxes at some window sizes).</summary>
        static Rect view;
        static Vector2 ToGui(Vector3 vp) => new Vector2(view.x + vp.x * view.width, view.y + (1f - vp.y) * view.height);

        public static readonly string[] TagNames = { "Off", "Above", "Above, outline", "Above, large", "Above, pointer", "Above, ornament", "Below feet", "Beside", "Above, strip", "Above, plain", "Above, with head" };

        Lobby lobby;
        readonly Dictionary<int, float> onScreenFade = new Dictionary<int, float>();
        readonly Dictionary<int, float> offFade = new Dictionary<int, float>();
        readonly Dictionary<int, Vector2> edgeSmooth = new Dictionary<int, Vector2>();

        string NameFor(int slot)
        {
            if (lobby == null) lobby = FindObjectOfType<Lobby>();
            if (lobby != null && lobby.Roster.TryGetValue(slot, out var e) && !string.IsNullOrEmpty(e.Name) && e.Name != "Gungeoneer") return e.Name;
            return "Player " + (slot + 1);
        }

        static string Head(PlayerController p)
        {
            var h = GungeonUI.HeadFor(p);
            return GungeonUI.Item(h) != null ? h : "Player_Coop_001";
        }

        void Start()
        {
            // Screenshots: show both helpers.
            if (System.Environment.GetEnvironmentVariable("GO_SHOT_UI") == "1") { NetConfig.LocatorStyle.Value = 5; NetConfig.FriendCrosshair.Value = true; }
            if (int.TryParse(System.Environment.GetEnvironmentVariable("GO_OUTLINES"), out var ol)) NetConfig.Outlines.Value = ol;
            if (int.TryParse(System.Environment.GetEnvironmentVariable("GO_MY_COLOR"), out var mc)) NetConfig.MyColor.Value = mc;
            if (int.TryParse(System.Environment.GetEnvironmentVariable("GO_TEAM_HUD"), out var th)) NetConfig.TeammateHud.Value = th;
            // Harness: GO_SHOT_UI="locator,tag,opacity,crosshair" for front-page screenshots, e.g. "5,1,1,0".
            var su = System.Environment.GetEnvironmentVariable("GO_SHOT_UI");
            if (!string.IsNullOrEmpty(su))
            {
                var p = su.Split(',');
                int v;
                if (p.Length > 0 && int.TryParse(p[0], out v)) NetConfig.LocatorStyle.Value = v;
                if (p.Length > 1 && int.TryParse(p[1], out v)) NetConfig.NameTagStyle.Value = v;
                if (p.Length > 2 && int.TryParse(p[2], out v)) NetConfig.NameOpacity.Value = v;
                if (p.Length > 3) NetConfig.FriendCrosshair.Value = p[3] == "1";
            }
        }

        void LateUpdate()
        {
            NPlayers.HudPatches.ApplyTeammateHud();
            ApplyOutlines();
        }

        // ---------- Outlines in each player's color ----------

        static readonly HashSet<int> recolored = new HashSet<int>();

        static void ApplyOutlines()
        {
            if (!GameManager.HasInstance || GameManager.Instance.AllPlayers == null) return;
            int mode = NetSession.Active ? NetConfig.Outlines.Value : 0;
            if (mode == 0 && recolored.Count == 0) return;
            foreach (var p in GameManager.Instance.AllPlayers)
            {
                if (!p || !p.sprite) continue;
                int id = p.GetInstanceID();
                bool mine = mode == 2 || (mode == 1 && p != PlayerSlots.Local);
                if (!mine && !recolored.Contains(id)) continue;
                var mat = SpriteOutlineManager.GetOutlineMaterial(p.sprite);
                if (!mat || !mat.HasProperty("_OverrideColor")) continue;
                Color want = mine ? ColorFor(p) : p.outlineColor;
                if (mine) want.a = 1f;
                if (mat.GetColor("_OverrideColor") != want) mat.SetColor("_OverrideColor", want);
                if (mine) recolored.Add(id); else recolored.Remove(id);
            }
        }

        // ---------- Faces beside the hearts ----------

        static float nextFaceLog;

        static void DrawPortraits()
        {
            if (!NPlayers.HudPatches.PortraitsOn || !GameManager.HasInstance || GameManager.Instance.AllPlayers == null) return;
            var ui = GameUIRoot.Instance;
            if (!ui) return;
            int px = GungeonUI.Px;
            bool log = Dev.DevEnv.IsHarness && Time.realtimeSinceStartup >= nextFaceLog;
            if (log) nextFaceLog = Time.realtimeSinceStartup + 10f;
            foreach (var p in GameManager.Instance.AllPlayers)
            {
                if (!p) continue;
                bool hasRow = NPlayers.HudPatches.HeartRow(ui, p.PlayerIDX, out var row, out float iconH);
                if (log) Plugin.Log.LogInfo($"FACE idx={p.PlayerIDX} {p.characterIdentity} row={(hasRow ? row.ToString() : "none")} hearts={(p.PlayerIDX < ui.heartControllers.Count ? ui.heartControllers[p.PlayerIDX].name : "-")}");
                if (!hasRow) continue;
                string head = Head(p);
                var hs = GungeonUI.SpriteSize(head);
                if (hs.y <= 0) continue;
                if (iconH <= 0f) iconH = row.height;
                float h = iconH * NPlayers.HudPatches.PortraitScale, w = hs.x * h / hs.y;
                // Always on the left of the hearts: on the left half that is the screen edge (room is made there), on the
                // right half it is the gap toward the middle (the gap beside each block leaves room for one face).
                float x = row.xMin - w - 1.5f * px;
                float y = row.yMin + iconH / 2f - h / 2f; // beside the first line of hearts
                x = Mathf.Clamp(x, 0f, Screen.width - w);
                var col = ColorFor(p);
                // A strip in their color under the face, so the face, the name tag and the outline all match.
                var old = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.6f);
                GUI.DrawTexture(new Rect(x - px, y + h - px, w + 2 * px, 3 * px), Texture2D.whiteTexture);
                GUI.color = new Color(col.r, col.g, col.b, 1f);
                GUI.DrawTexture(new Rect(x, y + h, w, px), Texture2D.whiteTexture);
                GUI.color = old;
                GungeonUI.Sprite(head, new Rect(x, y, w, h), Color.white);
            }
        }

        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (NetSession.Active && GungeonUI.Ready && !OnlineMenu.IsOpen && GameManager.HasInstance && !GameManager.Instance.IsLoadingLevel && !GameManager.Instance.IsPaused) DrawPortraits();
            int loc = NetConfig.LocatorStyle.Value, tag = NetConfig.NameTagStyle.Value;
            bool cross = NetConfig.FriendCrosshair.Value;
            if (loc <= 0 && tag <= 0 && !cross) return;
            if (!NetSession.Active || OnlineMenu.IsOpen || !GameManager.HasInstance || !GungeonUI.Ready) return;
            var gm = GameManager.Instance;
            if (gm.IsLoadingLevel || gm.IsPaused || gm.MainCameraController == null) return;
            var cam = gm.MainCameraController.GetComponent<UnityEngine.Camera>();
            var me = PlayerSlots.Local;
            if (cam == null || me == null) return;
            var pr = cam.pixelRect;
            view = new Rect(pr.x, Screen.height - pr.yMax, pr.width, pr.height);
            float dt = Mathf.Min(0.05f, Time.unscaledDeltaTime);
            foreach (var kv in PlayerSlots.Remotes)
            {
                var p = kv.Value;
                if (!p || !p.sprite) continue;
                Vector3 vp = cam.WorldToViewportPoint(p.sprite.WorldTopCenter);
                Vector3 vf = cam.WorldToViewportPoint(p.sprite.WorldBottomCenter);
                bool on = vp.z > 0 && vf.x > 0.03f && vf.x < 0.97f && vf.y > 0.05f && vp.y < 0.97f;
                onScreenFade.TryGetValue(kv.Key, out var f);
                f = Mathf.MoveTowards(f, on ? 1f : 0f, dt / 0.18f);
                onScreenFade[kv.Key] = f;
                // The locator only shows once your friend is fully off your screen, so tag and locator never overlap.
                bool fullyOff = !(vp.z > 0 && vf.x > -0.02f && vf.x < 1.02f && vf.y > -0.04f && vp.y < 1.04f);
                offFade.TryGetValue(kv.Key, out var g);
                g = Mathf.MoveTowards(g, fullyOff ? 1f : 0f, dt / 0.18f);
                offFade[kv.Key] = g;
                float onA = GungeonUI.Ease(f), offA = GungeonUI.Ease(g);
                string name = NameFor(kv.Key);
                if (tag > 0 && onA > 0.01f)
                {
                    // Anchor on the feet: sprite boxes include empty space above the head. Characters are about 22 px tall.
                    // Name height: close sits just over the hair, normal and high leave more room.
                    up = NetConfig.NameHeight.Value == 2 ? 30 : NetConfig.NameHeight.Value == 1 ? 24 : 19;
                    var head = ToGui(vf) - new Vector2(0, up * GungeonUI.Px);
                    DrawTag(tag, p, name, head, onA);
                }
                if (cross && onA > 0.01f && !p.IsGhost && p.CurrentGun)
                {
                    // The game's crosshair where your friend is aiming, in their name color.
                    Vector3 av = cam.WorldToViewportPoint(p.unadjustedAimPoint);
                    if (av.z > 0 && av.x > 0 && av.x < 1 && av.y > 0 && av.y < 1)
                    {
                        var c = ColorFor(p);
                        GungeonUI.SpriteAt("crosshair", ToGui(av), new Color(c.r, c.g, c.b, 0.85f * onA));
                    }
                }
                if (loc > 0 && offA > 0.01f)
                    DrawLocator(loc, kv.Key, p, me, cam, name, offA);
            }
        }

        /// <summary>Your chosen colour for the first friend; each further friend gets the next colour, so tags tell them apart.</summary>
        public static Color ColorFor(PlayerController p)
        {
            int slot = PlayerSlots.SlotOf(p), before = 0;
            // Everyone's own pick, as they chose it in their game.
            if (p == PlayerSlots.Local && NetConfig.MyColor.Value >= 0) return NameColors[NetConfig.MyColor.Value];
            if (Session.Lobby.Colors.TryGetValue(slot, out var picked) && picked >= 0 && picked < NameColors.Length) return NameColors[picked];
            if (p == PlayerSlots.Local) return NameColors[0];
            foreach (var kv in PlayerSlots.Remotes) if (kv.Key < slot) before++;
            return NameColors[(Mathf.Clamp(NetConfig.NameColor.Value, 0, NameColors.Length - 1) + before) % NameColors.Length];
        }

        // ---------- Name tags (on screen) ----------

        static int up = 19; // game pixels from the feet to the tag's anchor

        void DrawTag(int style, PlayerController p, string name, Vector2 head, float a)
        {
            int px = GungeonUI.Px;
            float lift = (2 + (1f - a) * 3) * px; // rises into place as it fades in
            var pick = ColorFor(p);
            a *= NetConfig.NameOpacity.Value == 1 ? 0.75f : NetConfig.NameOpacity.Value == 2 ? 0.5f : 1f;
            var gold = new Color(pick.r, pick.g, pick.b, a);
            var font = style == 3 ? GungeonUI.Font.Menu : GungeonUI.Font.Small;
            var size = GungeonUI.Measure(name, font);
            Vector2 feet = head + new Vector2(0, up * px);
            switch (style)
            {
                case 2: Outlined(name, new Vector2(head.x - size.x / 2f, head.y - lift - size.y), font, gold); break;
                case 3: GungeonUI.Text(name, new Vector2(head.x - size.x / 2f, head.y - lift - size.y), font, gold); break;
                case 4:
                    {
                        GungeonUI.Text(name, new Vector2(head.x - size.x / 2f, head.y - lift - size.y - 5 * px), font, gold);
                        var m = GUI.matrix;
                        var at = new Vector2(head.x, head.y - lift - 2 * px);
                        GUIUtility.RotateAroundPivot(180f, at);
                        GungeonUI.SpriteAt("ui_options_arrow_left", at, gold);
                        GUI.matrix = m;
                        GUIUtility.RotateAroundPivot(-90f, at);
                        GungeonUI.SpriteAt("ui_options_arrow_right", at, gold);
                        GUI.matrix = m;
                        break;
                    }
                case 5:
                    {
                        float y = head.y - lift - size.y - 4 * px;
                        GungeonUI.Text(name, new Vector2(head.x - size.x / 2f, y), font, gold);
                        GungeonUI.Frame("crosshair-hyphen", new Rect(head.x - size.x / 2f - 10 * px, y + size.y - 6 * px, size.x + 20 * px, 18 * px), new Color(1, 1, 1, 0.85f * a));
                        break;
                    }
                case 6: Outlined(name, new Vector2(feet.x - size.x / 2f, feet.y + 3 * px), font, gold); break;
                case 7: Outlined(name, new Vector2(head.x + 10 * px, head.y + 6 * px), font, gold); break;
                case 8:
                    {
                        var r = new Rect(head.x - size.x / 2f - 10 * px, head.y - lift - size.y - 3 * px, size.x + 20 * px, size.y + 3 * px);
                        GungeonUI.Frame("text-shadow-sprite", r, new Color(1, 1, 1, 0.9f * a));
                        GungeonUI.Text(name, new Vector2(head.x - size.x / 2f, r.y + px), font, gold);
                        break;
                    }
                case 9: GungeonUI.Text(name, new Vector2(head.x - size.x / 2f, head.y - lift - size.y), font, gold, 1f, false); break;
                case 10:
                    {
                        var hs = GungeonUI.SpriteSize(Head(p));
                        float w = hs.x + 3 * px + size.x, x = head.x - w / 2f, y = head.y - lift - Mathf.Max(hs.y, size.y);
                        GungeonUI.Sprite(Head(p), new Rect(x, y, hs.x, hs.y), new Color(1, 1, 1, a));
                        GungeonUI.Text(name, new Vector2(x + hs.x + 3 * px, y + (hs.y - size.y) / 2f + px), font, gold);
                        break;
                    }
                default: GungeonUI.Text(name, new Vector2(head.x - size.x / 2f, head.y - lift - size.y), font, gold); break;
            }
        }

        /// <summary>Text with a one-game-pixel dark outline all around, readable on any floor.</summary>
        static void Outlined(string text, Vector2 at, GungeonUI.Font font, Color c)
        {
            int px = GungeonUI.Px;
            var dark = new Color(0.04f, 0.02f, 0.05f, 0.9f * c.a);
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0) GungeonUI.Text(text, at + new Vector2(dx * px, dy * px), font, dark, 1f, false);
            GungeonUI.Text(text, at, font, c, 1f, false);
        }

        // ---------- Locator (off screen) ----------

        void DrawLocator(int style, int slot, PlayerController p, PlayerController me, UnityEngine.Camera cam, string name, float a)
        {
            int px = GungeonUI.Px;
            Vector2 d = (Vector2)p.CenterPosition - me.CenterPosition;
            float dist = d.magnitude;
            Vector2 dir = dist > 0.01f ? d / dist : Vector2.up;
            Vector2 screenDir = new Vector2(dir.x, -dir.y);
            float bob = Mathf.Round(Mathf.Sin(Time.realtimeSinceStartup * 3f) * 1.0f) * px;
            var white = new Color(1, 1, 1, a);
            string meters = Mathf.RoundToInt(dist) + "m";
            string head = Head(p);
            var hs = GungeonUI.SpriteSize(head);

            if (style == 4)
            {
                // Orbit arrow: a bullet-shaped arrow circling your own character, pointing at your friend.
                Vector3 mv = cam.WorldToViewportPoint(me.CenterPosition);
                var c = ToGui(mv);
                float radius = 22 * px;
                var tip = c + screenDir * radius;
                float ang = Mathf.Atan2(screenDir.y, screenDir.x) * Mathf.Rad2Deg + 90f;
                var m = GUI.matrix;
                GUIUtility.RotateAroundPivot(ang, tip);
                GungeonUI.SpriteAt("chamber_full_arrow_001", tip, new Color(GungeonUI.Gold.r, GungeonUI.Gold.g, GungeonUI.Gold.b, a));
                GUI.matrix = m;
                var hc = c + screenDir * (radius + 14 * px);
                GungeonUI.Sprite(head, new Rect(hc.x - hs.x / 2f + px, hc.y - hs.y / 2f + px, hs.x, hs.y), new Color(0, 0, 0, 0.6f * a));
                GungeonUI.Sprite(head, new Rect(hc.x - hs.x / 2f, hc.y - hs.y / 2f, hs.x, hs.y), white);
                return;
            }

            // Edge styles: a marker of this size, kept inside the screen along the friend's direction.
            Vector2 box;
            switch (style)
            {
                case 1: box = new Vector2(hs.x + 9 * px, hs.y + 9 * px); break;
                case 2: box = GungeonUI.SpriteSize("crosshair_gold") + new Vector2(8 * px, 8 * px); break;
                case 3: box = hs + new Vector2(10 * px, 10 * px); break;
                default: box = hs + new Vector2(10 * px, 8 * px); break;
            }
            // Keep clear of the HUD: hearts and blanks along the top, guns and ammo along the bottom.
            float margin = 6 * px;
            var safe = Rect.MinMaxRect(view.xMin + margin + box.x / 2f, view.yMin + 48 * px + box.y / 2f, view.xMax - margin - box.x / 2f, view.yMax - 62 * px - box.y / 2f);
            var center = new Vector2(view.center.x, Mathf.Clamp(view.center.y, safe.yMin, safe.yMax));
            float s = float.MaxValue;
            if (screenDir.x > 0.0001f) s = Mathf.Min(s, (safe.xMax - center.x) / screenDir.x);
            if (screenDir.x < -0.0001f) s = Mathf.Min(s, (safe.xMin - center.x) / screenDir.x);
            if (screenDir.y > 0.0001f) s = Mathf.Min(s, (safe.yMax - center.y) / screenDir.y);
            if (screenDir.y < -0.0001f) s = Mathf.Min(s, (safe.yMin - center.y) / screenDir.y);
            var target = center + screenDir * Mathf.Max(0f, s);
            // Glide along the edge instead of jumping when the direction changes.
            if (!edgeSmooth.TryGetValue(slot, out var at) || (at - target).sqrMagnitude > Screen.width * Screen.width) at = target;
            at = Vector2.Lerp(at, target, 1f - Mathf.Exp(-14f * Mathf.Min(0.05f, Time.unscaledDeltaTime)));
            edgeSmooth[slot] = at;
            at.y += bob;
            float angle = Mathf.Atan2(screenDir.y, screenDir.x) * Mathf.Rad2Deg;

            switch (style)
            {
                case 1: // Speech bubble with the friend's head, nub pointing toward them.
                    {
                        var r = new Rect(at.x - box.x / 2f, at.y - box.y / 2f, box.x, box.y);
                        // Tight and a little see-through, so it is a marker rather than a white block.
                        GungeonUI.Frame("speech_bubble_border", r, new Color(1, 1, 1, 0.82f * a), new RectOffset(5, 5, 5, 5));
                        var nubAt = at + screenDir * (Mathf.Min(box.x, box.y) / 2f + px);
                        var m = GUI.matrix;
                        GUIUtility.RotateAroundPivot(Mathf.Round((angle - 90f) / 90f) * 90f, nubAt);
                        GungeonUI.SpriteAt("speech_bubble_nub", nubAt, new Color(1, 1, 1, 0.82f * a));
                        GUI.matrix = m;
                        GungeonUI.Sprite(head, new Rect(at.x - hs.x / 2f, at.y - hs.y / 2f, hs.x, hs.y), white);
                        GungeonUI.TextCentered(meters, at.x, r.yMax + 2 * px, GungeonUI.Font.Small, new Color(GungeonUI.Cream.r, GungeonUI.Cream.g, GungeonUI.Cream.b, a));
                        break;
                    }
                case 2: // Gun sight: the gold crosshair around the head, with an arrow outside it.
                    {
                        float spin = Mathf.Sin(Time.realtimeSinceStartup * 1.6f) * 8f;
                        var m = GUI.matrix;
                        GUIUtility.RotateAroundPivot(spin, at);
                        GungeonUI.SpriteAt("crosshair_gold", at, white);
                        GUI.matrix = m;
                        GungeonUI.Sprite(head, new Rect(at.x - hs.x / 2f, at.y - hs.y / 2f, hs.x, hs.y), white);
                        // The bubble nub, gold, sitting on the sight's rim toward your friend.
                        var tip = at + screenDir * (box.x / 2f - px);
                        GUIUtility.RotateAroundPivot(Mathf.Round((angle - 90f) / 45f) * 45f, tip);
                        GungeonUI.SpriteAt("speech_bubble_nub", tip, new Color(GungeonUI.Gold.r, GungeonUI.Gold.g, GungeonUI.Gold.b, a));
                        GUI.matrix = m;
                        GungeonUI.TextCentered(meters, at.x, at.y + box.y / 2f + px, GungeonUI.Font.Small, new Color(GungeonUI.Cream.r, GungeonUI.Cream.g, GungeonUI.Cream.b, a));
                        break;
                    }
                case 3: // Minimap head: just the head and a three-dot trail toward your friend.
                    {
                        GungeonUI.Sprite(head, new Rect(at.x - hs.x / 2f + px, at.y - hs.y / 2f + px, hs.x, hs.y), new Color(0, 0, 0, 0.6f * a));
                        GungeonUI.Sprite(head, new Rect(at.x - hs.x / 2f, at.y - hs.y / 2f, hs.x, hs.y), white);
                        for (int i = 0; i < 3; i++)
                        {
                            float phase = Mathf.Repeat(Time.realtimeSinceStartup * 1.5f - i * 0.25f, 1f);
                            var dot = at + screenDir * (hs.x / 2f + (4 + i * 4) * px);
                            GungeonUI.SpriteAt("quick_switch_icon_dot_001", dot, new Color(GungeonUI.Gold.r, GungeonUI.Gold.g, GungeonUI.Gold.b, a * (0.35f + 0.65f * (1f - phase))));
                        }
                        break;
                    }
                default: // Pickup tag: the item-pickup box, compact, with head, gold distance and a gold pointer.
                    {
                        var r = new Rect(at.x - box.x / 2f, at.y - box.y / 2f, box.x, box.y);
                        // Point first so the box sits on top of the pointer's base.
                        var nubAt = at + screenDir * (Mathf.Min(box.x, box.y) / 2f + px);
                        if (Mathf.Abs(screenDir.x) > Mathf.Abs(screenDir.y)) nubAt = new Vector2(at.x + Mathf.Sign(screenDir.x) * (box.x / 2f + px), Mathf.Clamp(nubAt.y, r.yMin + 5 * px, r.yMax - 5 * px));
                        else nubAt = new Vector2(Mathf.Clamp(nubAt.x, r.xMin + 5 * px, r.xMax - 5 * px), at.y + Mathf.Sign(screenDir.y) * (box.y / 2f + px));
                        var m = GUI.matrix;
                        GUIUtility.RotateAroundPivot(Mathf.Round((angle - 90f) / 90f) * 90f, nubAt);
                        GungeonUI.SpriteAt("speech_bubble_nub", nubAt, new Color(GungeonUI.Gold.r, GungeonUI.Gold.g, GungeonUI.Gold.b, a));
                        GUI.matrix = m;
                        GungeonUI.Frame("notification_box", r, white, new RectOffset(6, 6, 6, 6));
                        GungeonUI.Sprite(head, new Rect(at.x - hs.x / 2f, at.y - hs.y / 2f, hs.x, hs.y), white);
                        break;
                    }
            }
            DrawLocatorLabel(p, name, meters, at, box, screenDir, a);
        }

        /// <summary>
        /// Setting (Locator text): the friend's name, and the distance if asked, in a small outlined line on the inner
        /// side of the marker, so it never covers the marker or the edge of the screen.
        /// </summary>
        void DrawLocatorLabel(PlayerController p, string name, string meters, Vector2 at, Vector2 box, Vector2 screenDir, float a)
        {
            int mode = NetConfig.LocatorLabel.Value;
            if (mode == 0) return;
            int px = GungeonUI.Px;
            string text = mode == 2 ? name + "  " + meters : name;
            var size = GungeonUI.Measure(text, GungeonUI.Font.Small);
            var pick = ColorFor(p);
            var col = new Color(pick.r, pick.g, pick.b, a);
            Vector2 pos;
            if (Mathf.Abs(screenDir.x) >= Mathf.Abs(screenDir.y))
            {
                // Marker on a side edge: text goes inward, level with it.
                float x = screenDir.x > 0 ? at.x - box.x / 2f - 4 * px - size.x : at.x + box.x / 2f + 4 * px;
                pos = new Vector2(x, at.y - size.y / 2f + px);
            }
            else
            {
                // Marker on the top or bottom edge: text goes inward, centred under or over it.
                float y = screenDir.y > 0 ? at.y - box.y / 2f - 3 * px - size.y : at.y + box.y / 2f + 3 * px;
                pos = new Vector2(at.x - size.x / 2f, y);
            }
            pos.x = Mathf.Clamp(pos.x, view.xMin + 2 * px, view.xMax - size.x - 2 * px);
            Outlined(text, pos, GungeonUI.Font.Small, col);
        }
    }
}
