using System.Collections.Generic;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>Short messages at the top of the screen that fade out on their own.</summary>
    internal sealed class Toasts : MonoBehaviour
    {
        static Toasts instance;
        sealed class Toast { public string Text; public float Until; public float Seconds; public bool Warn; }
        readonly List<Toast> list = new List<Toast>();
        /// <summary>A banner that stays until cleared (e.g. "Waiting for your friend to reconnect").</summary>
        public static string Banner;

        void Awake() { instance = this; }

        float shotAt = -1f;
        int shots;

        void Update()
        {
            // Harness UI tour: a screenshot of each new toast once it has slid in.
            if (shotAt > 0 && Time.realtimeSinceStartup >= shotAt && shots < 8)
            {
                shotAt = -1f;
                var dir = System.Environment.GetEnvironmentVariable("GO_RUN_DIR") ?? ".";
                ScreenCapture.CaptureScreenshot(System.IO.Path.Combine(dir, "toast-" + (++shots) + ".png"));
            }
        }

        public static void Show(string text, float seconds = 4f, bool warn = false)
        {
            Plugin.Log.LogInfo("TOAST " + text);
            if (instance == null) return;
            // Same text again just extends the old one.
            foreach (var t in instance.list) if (t.Text == text) { t.Until = Time.realtimeSinceStartup + seconds; t.Seconds = seconds + 10f; return; }
            instance.list.Add(new Toast { Text = text, Until = Time.realtimeSinceStartup + seconds, Seconds = seconds, Warn = warn });
            if (System.Environment.GetEnvironmentVariable("GO_UI_TOUR") == "1") instance.shotAt = Time.realtimeSinceStartup + 0.5f;
            if (instance.list.Count > 4) instance.list.RemoveAt(0);
        }

        void OnGUI()
        {
            float now = Time.realtimeSinceStartup;
            if (!GungeonUI.Ready)
            {
                // Hold toasts until the game's art is loaded, so none show in a system font at boot.
                foreach (var t in list) t.Until = Mathf.Max(t.Until, now + t.Seconds);
                return;
            }
            list.RemoveAll(t => t.Until < now);
            // With the online menu open, messages go along the bottom of the screen, clear of its title and code
            // (hiding them there left "Send report" with no answer at all).
            DrawGame(now, OnlineMenu.IsOpen);
            return;
            var style = MenuStyle.Toast;
            float y = Screen.height * 0.08f;
            if (!string.IsNullOrEmpty(Banner))
            {
                DrawLine(Banner, y, 1f, true, MenuStyle.Banner);
                y += MenuStyle.Banner.fontSize * 2.2f;
            }
            foreach (var t in list)
            {
                float alpha = Mathf.Clamp01((t.Until - now) / 0.6f);
                DrawLine(t.Text, y, alpha, t.Warn, style);
                y += style.fontSize * 2f;
            }
        }

        /// <summary>Toasts in the item-pickup box, sliding in from above; the banner uses the gold box.</summary>
        void DrawGame(float now, bool atBottom = false)
        {
            int px = GungeonUI.Px;
            float y = 30 * px;
            if (atBottom)
            {
                y = Screen.height - 34 * px * list.Count - 10 * px;
                foreach (var t in list)
                {
                    float a = Mathf.Min(1f, GungeonUI.Ease((t.Until - now) / 0.4f));
                    DrawStyled(t.Text, y, a, false, t.Warn);
                    y += 29 * px;
                }
                return;
            }
            if (!string.IsNullOrEmpty(Banner))
            {
                DrawStyled(Banner, y, 1f, true, false);
                y += 29 * px;
            }
            foreach (var t in list)
            {
                float age = now - (t.Until - t.Seconds);
                float a = Mathf.Min(GungeonUI.Ease(age / 0.2f), GungeonUI.Ease((t.Until - now) / 0.4f));
                float slide = (1f - GungeonUI.Ease(age / 0.2f)) * -8 * px;
                DrawStyled(t.Text, y + slide, a, false, t.Warn);
                y += 29 * px;
            }
        }

        static void DrawStyled(string text, float y, float a, bool banner, bool warn)
        {
            int px = GungeonUI.Px;
            switch (NetConfig.LookToast.Value)
            {
                case 1: // The game's speech bubble, dark text.
                    {
                        var size = GungeonUI.Measure(text, GungeonUI.Font.Small);
                        var r = new Rect(Mathf.Round((Screen.width - size.x) / 2f - 8 * px), Mathf.Round(y), size.x + 16 * px, size.y + 8 * px);
                        GungeonUI.Frame("speech_bubble_border", r, new Color(1, 1, 1, a));
                        var c = banner ? new Color(0.45f, 0.25f, 0.02f, a) : warn ? new Color(0.55f, 0.1f, 0.08f, a) : new Color(0.12f, 0.08f, 0.1f, a);
                        GungeonUI.TextCentered(text, Screen.width / 2f, r.y + 4 * px, GungeonUI.Font.Small, c, 1f, false);
                        break;
                    }
                case 2: // Text on the soft shadow strip the floor title uses.
                    {
                        var size = GungeonUI.Measure(text, GungeonUI.Font.Small);
                        var r = new Rect(Mathf.Round((Screen.width - size.x) / 2f - 24 * px), Mathf.Round(y), size.x + 48 * px, size.y + 6 * px);
                        GungeonUI.Frame("text-shadow-sprite", r, new Color(1, 1, 1, a));
                        var c = banner ? GungeonUI.Gold : warn ? new Color(1f, 0.6f, 0.5f) : Color.white;
                        GungeonUI.TextCentered(text, Screen.width / 2f, r.y + 3 * px, GungeonUI.Font.Small, new Color(c.r, c.g, c.b, a));
                        break;
                    }
                default:
                    DrawBox(text, y, a, banner ? "notification_box_gold_001" : warn ? "notification_box_purp_001" : "notification_box",
                        banner ? GungeonUI.Gold : warn ? new Color(1f, 0.7f, 0.6f) : Color.white);
                    break;
            }
        }

        static void DrawBox(string text, float y, float a, string frame, Color textColor)
        {
            int px = GungeonUI.Px;
            var size = GungeonUI.Measure(text, GungeonUI.Font.Small);
            float w = Mathf.Max(size.x + 40 * px, 120 * px), h = 25 * px;
            var r = new Rect(Mathf.Round((Screen.width - w) / 2f), Mathf.Round(y), w, h);
            GungeonUI.Frame(frame, r, new Color(1, 1, 1, a));
            GungeonUI.TextCentered(text, Screen.width / 2f, r.y + (h - size.y) / 2f + px, GungeonUI.Font.Small, new Color(textColor.r, textColor.g, textColor.b, a));
        }

        static void DrawLine(string text, float y, float alpha, bool warn, GUIStyle style)
        {
            var size = style.CalcSize(new GUIContent(text));
            var r = new Rect((Screen.width - size.x) / 2f - 16f, y, size.x + 32f, size.y + 10f);
            var old = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(r, warn ? MenuStyle.WarnBg : MenuStyle.PanelBg);
            GUI.Label(new Rect(r.x, r.y + 5f, r.width, size.y), text, style);
            GUI.color = old;
        }
    }

    /// <summary>Shared look: dark panels, warm highlight, text sized to the screen.</summary>
    internal static class MenuStyle
    {
        static int builtFor = -1;
        static GUIStyle toast, banner, title, item, itemSel, small, big;
        public static Texture2D PanelBg, SelBg, WarnBg, DimBg;

        static Texture2D Tex(Color c)
        {
            var t = new Texture2D(1, 1);
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        static void Build()
        {
            if (builtFor == Screen.height && toast != null) return;
            builtFor = Screen.height;
            PanelBg = PanelBg ?? Tex(new Color(0.06f, 0.05f, 0.07f, 0.92f));
            SelBg = SelBg ?? Tex(new Color(0.93f, 0.66f, 0.18f, 0.95f));
            WarnBg = WarnBg ?? Tex(new Color(0.45f, 0.08f, 0.06f, 0.92f));
            DimBg = DimBg ?? Tex(new Color(0f, 0f, 0f, 0.55f));
            int u = Mathf.Max(12, Screen.height / 40);
            toast = new GUIStyle(GUI.skin.label) { fontSize = u, alignment = TextAnchor.UpperCenter, wordWrap = false };
            toast.normal.textColor = Color.white;
            banner = new GUIStyle(toast) { fontSize = Mathf.RoundToInt(u * 1.2f), fontStyle = FontStyle.Bold };
            title = new GUIStyle(toast) { fontSize = Mathf.RoundToInt(u * 1.6f), fontStyle = FontStyle.Bold };
            title.normal.textColor = new Color(0.98f, 0.8f, 0.35f);
            item = new GUIStyle(toast) { fontSize = Mathf.RoundToInt(u * 1.15f), alignment = TextAnchor.MiddleCenter };
            itemSel = new GUIStyle(item) { fontStyle = FontStyle.Bold };
            itemSel.normal.textColor = new Color(0.08f, 0.05f, 0.02f);
            small = new GUIStyle(toast) { fontSize = Mathf.RoundToInt(u * 0.85f), wordWrap = true };
            small.normal.textColor = new Color(0.8f, 0.8f, 0.85f);
            big = new GUIStyle(toast) { fontSize = Mathf.RoundToInt(u * 2.2f), fontStyle = FontStyle.Bold };
            big.normal.textColor = new Color(0.98f, 0.8f, 0.35f);
        }

        public static GUIStyle Toast { get { Build(); return toast; } }
        public static GUIStyle Banner { get { Build(); return banner; } }
        public static GUIStyle Title { get { Build(); return title; } }
        public static GUIStyle Item { get { Build(); return item; } }
        public static GUIStyle ItemSel { get { Build(); return itemSel; } }
        public static GUIStyle Small { get { Build(); return small; } }
        public static GUIStyle Big { get { Build(); return big; } }
    }
}
