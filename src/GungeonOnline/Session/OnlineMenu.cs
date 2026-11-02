using System;
using System.Collections.Generic;
using GungeonOnline.Net;
using InControl;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>
    /// The Gungeon Online menu. Controller first: d-pad or stick to move, A to choose, B to go back,
    /// left/right to change a setting. Mouse and keyboard work too. Opened from the pause menu ("Play Online"),
    /// by holding Back + Start on any controller, or with F8.
    /// </summary>
    internal sealed class OnlineMenu : MonoBehaviour
    {
        public static OnlineMenu Instance { get; private set; }
        public static bool IsOpen => Instance != null && Instance.open;

        enum Screen { Home, Host, Join, Session, Settings, ConfirmLeave, SetGame, SetFriend, SetDisplay, SetHelp, SaveSlots, ResumeSlots }

        sealed class Entry
        {
            public string Label;
            public Action Do;
            public Action<int> Adjust;   // left/right
            public bool Enabled = true;
        }

        bool open;
        Screen screen = Screen.Home;
        int sel;
        readonly List<Entry> entries = new List<Entry>();
        string typedCode = "";
        bool typing;
        float holdTime;
        float nextRepeat;
        int lastDir;
        bool overrideSet;
        string info;   // one line under the title

        void Awake() { Instance = this; }

        public void Open()
        {
            if (open) return;
            open = true;
            sel = 0;
            screen = NetSession.Active ? Screen.Session : Screen.Home;
            if (GameManager.HasInstance && GameManager.Instance.IsPaused) GameManager.Instance.Unpause();
            SetInputLock(true);
        }

        /// <summary>Harness: open a given screen with a given row selected (UI tour screenshots).</summary>
        public void ShowForHarness(string name, int row)
        {
            Open();
            Go((Screen)Enum.Parse(typeof(Screen), name));
            pendingRow = row;
        }
        int pendingRow = -1;

        /// <summary>Harness (GO_TYPE_AT): open the Join screen's code box, as if the player picked Type the code.</summary>
        public void StartTypingForTest() { Open(); Go(Screen.Join); typing = true; typedCode = ""; }

        public void OpenLeave()
        {
            Open();
            screen = NetSession.Active ? Screen.ConfirmLeave : Screen.Home;
            sel = 0;
        }

        public void Close()
        {
            open = false;
            typing = false;
            SetInputLock(false);
        }

        void SetInputLock(bool on)
        {
            var p = Game.PlayerSlots.Local;
            if (!p) { overrideSet = false; return; }
            if (on && !overrideSet) { p.SetInputOverride("gungeon online menu"); overrideSet = true; }
            if (!on && overrideSet) { p.ClearInputOverride("gungeon online menu"); overrideSet = false; }
        }

        // ---------------- Input ----------------

        static IEnumerable<InputDevice> RealDevices()
        {
            // During the first frames InControl is not set up yet.
            var list = new List<InputDevice>();
            try { var all = InputManager.Devices; if (all != null) foreach (var d in all) if (d != null && !d.Passive) list.Add(d); }
            catch { }
            return list;
        }

        static bool AnyPressed(InputControlType t, bool wasPressed)
        {
            foreach (var d in RealDevices())
            {
                var c = d.GetControl(t);
                if (c == null) continue;
                if (wasPressed ? c.WasPressed : c.IsPressed) return true;
            }
            return false;
        }

        void Update()
        {
            // Open: F8, or hold Back + Start on any controller for half a second.
            if (UnityEngine.Input.GetKeyDown(NetConfig.OverlayKey.Value)) { if (open) Close(); else Open(); return; }
            bool back = AnyPressed(InputControlType.Back, false) || AnyPressed(InputControlType.Select, false) || AnyPressed(InputControlType.Share, false) || AnyPressed(InputControlType.View, false);
            bool start = AnyPressed(InputControlType.Start, false) || AnyPressed(InputControlType.Options, false) || AnyPressed(InputControlType.Menu, false);
            if (back && start)
            {
                holdTime += Time.unscaledDeltaTime;
                if (holdTime > 0.5f) { holdTime = -10f; if (open) Close(); else Open(); }
            }
            else holdTime = 0f;

            if (!open) return;
            SetInputLock(true);
            Rebuild();
            if (typing) return; // keyboard text field has focus

            int dir = 0;
            foreach (var d in RealDevices())
            {
                float y = d.Direction.Y;
                if (y > 0.5f || d.DPadUp.IsPressed) dir = -1;
                else if (y < -0.5f || d.DPadDown.IsPressed) dir = 1;
            }
            if (UnityEngine.Input.GetKey(KeyCode.UpArrow) || UnityEngine.Input.GetKey(KeyCode.W)) dir = -1;
            if (UnityEngine.Input.GetKey(KeyCode.DownArrow) || UnityEngine.Input.GetKey(KeyCode.S)) dir = 1;
            if (dir != 0 && (dir != lastDir || Time.unscaledTime >= nextRepeat))
            {
                Move(dir);
                nextRepeat = Time.unscaledTime + (dir != lastDir ? 0.35f : 0.12f);
            }
            lastDir = dir;

            int side = 0;
            foreach (var d in RealDevices())
            {
                if (d.DPadLeft.WasPressed || d.LeftStickLeft.WasPressed) side = -1;
                if (d.DPadRight.WasPressed || d.LeftStickRight.WasPressed) side = 1;
            }
            if (UnityEngine.Input.GetKeyDown(KeyCode.LeftArrow) || UnityEngine.Input.GetKeyDown(KeyCode.A)) side = -1;
            if (UnityEngine.Input.GetKeyDown(KeyCode.RightArrow) || UnityEngine.Input.GetKeyDown(KeyCode.D)) side = 1;
            if (side != 0 && sel < entries.Count && entries[sel].Adjust != null) entries[sel].Adjust(side);

            bool accept = AnyPressed(InputControlType.Action1, true) || UnityEngine.Input.GetKeyDown(KeyCode.Return) || UnityEngine.Input.GetKeyDown(KeyCode.Space);
            bool cancel = AnyPressed(InputControlType.Action2, true) || UnityEngine.Input.GetKeyDown(KeyCode.Escape) || UnityEngine.Input.GetKeyDown(KeyCode.Backspace);
            if (accept && sel < entries.Count && entries[sel].Enabled && entries[sel].Do != null) entries[sel].Do();
            else if (cancel) Back();
        }

        void Move(int dir)
        {
            if (entries.Count == 0) return;
            for (int i = 0; i < entries.Count; i++)
            {
                sel = (sel + dir + entries.Count) % entries.Count;
                if (entries[sel].Enabled) break;
            }
        }

        void Go(Screen s) { screen = s; sel = 0; typing = false; }

        void Back()
        {
            switch (screen)
            {
                case Screen.Home: case Screen.Session: Close(); break;
                case Screen.ConfirmLeave: case Screen.SaveSlots: Go(Screen.Session); break;
                case Screen.ResumeSlots: Go(Screen.Home); break;
                case Screen.SetGame: case Screen.SetFriend: case Screen.SetDisplay: case Screen.SetHelp: Go(Screen.Settings); break;
                default: Go(NetSession.Active ? Screen.Session : Screen.Home); break;
            }
        }

        // ---------------- Content ----------------

        static string ClipboardCode()
        {
            string c = null;
            try { c = GUIUtility.systemCopyBuffer; } catch { }
            return c != null && InviteCode.TryParse(c.Trim(), out _) ? c.Trim() : null;
        }

        static bool InRun => GameManager.HasInstance && !Game.SceneObjects.InBreach && GameManager.Instance.Dungeon != null;

        void Add(string label, Action a, bool enabled = true, Action<int> adjust = null) =>
            entries.Add(new Entry { Label = label, Do = a, Enabled = enabled, Adjust = adjust });

        void Rebuild()
        {
            entries.Clear();
            var s = NetSession.Instance;
            info = null;
            if (NetSession.Active && (screen == Screen.Home || screen == Screen.Join)) screen = Screen.Session;
            if (!NetSession.Active && (screen == Screen.Session || screen == Screen.ConfirmLeave)) screen = Screen.Home;
            switch (screen)
            {
                case Screen.Home:
                    if (RunBackup.AnySlotUsable) Add("Resume saved run", () => Go(Screen.ResumeSlots));
                    if (!string.IsNullOrEmpty(NetConfig.LastJoin.Value)) Add("Rejoin last game", () => { s.RejoinLast(); Go(Screen.Session); });
                    Add("Host", () => { s.Host(); Go(Screen.Host); });
                    Add("Join", () => Go(Screen.Join));
                    Add("Settings", () => Go(Screen.Settings));
                    Add("Close", Close);
                    break;
                case Screen.Host:
                    info = s.InviteCodeText == null ? "Getting your invite code..." : null;
                    if (s.SteamReady) Add("Invite Steam friend", () => s.InviteSteamFriend());
                    AddStyleRow();
                    AddCountRow();
                    Add("Copy code again", () => { if (s.InviteCodeText != null) { GUIUtility.systemCopyBuffer = s.InviteCodeText; Toasts.Show("Code copied"); } }, s.InviteCodeText != null);
                    var reply = ClipboardCode();
                    Add("Let in friend",
                        () => { var rc = ClipboardCode(); if (rc != null) { s.LetIn(rc); Toasts.Show("Opening a path to your friend..."); } },
                        reply != null && reply != s.InviteCodeText);
                    Add("Done", () => Go(Screen.Session));
                    break;
                case Screen.Join:
                    var code = ClipboardCode();
                    Add(code != null ? "Join " + code : "Join (copy your friend's code first)", () => { var c = ClipboardCode(); if (c != null) s.Join(c); }, code != null);
                    Add("Type the code", () => { typing = true; typedCode = ""; });
                    if (s.SteamReady) info = "On Steam, accept your friend's invite from Steam and you join by yourself.";
                    Add("Back", () => Go(Screen.Home));
                    break;
                case Screen.Session:
                    if (NetSession.IsHost && Game.AwayGuard.Instance != null && Game.AwayGuard.Instance.AnyoneAway)
                        Add(Game.AwayGuard.Instance.ContinueAlone ? "Wait for them (pause the run)" : "Continue without them for now",
                            () => Game.AwayGuard.Instance.ContinueAlone = !Game.AwayGuard.Instance.ContinueAlone);
                    if (!NetSession.IsHost && s.Node != null && s.Node.Connecting && s.InviteCodeText != null)
                        Add("Copy my reply code", () => { GUIUtility.systemCopyBuffer = s.InviteCodeText; Toasts.Show("Reply code copied. Send it to the host, and they pick Let in friend."); });
                    Add("Settings", () => Go(Screen.Settings));
                    if (NetSession.IsHost && s.InviteCodeText != null) Add("Show invite code", () => Go(Screen.Host));
                    if (InRun) Add("I'm stuck", () => { Game.Unstuck.Now(); Close(); });
                    if (NetSession.IsHost && InRun) Add("Save run to a slot", () => Go(Screen.SaveSlots));
                    // Stop for the night: the run is saved and can be resumed any time later from this menu.
                    if (NetSession.IsHost && InRun) Add("Save run and quit", () =>
                    {
                        RunBackup.SaveNow("saved for later");
                        Toasts.Show("Run saved. Pick Resume saved run next time.", 5f);
                        Close();
                        s.Leave("the host saved the run for later");
                        GameManager.Instance.DelayedReturnToFoyer(1f);
                    });
                    Add("Leave", () => { if (InRun) Go(Screen.ConfirmLeave); else { s.Leave("left"); Go(Screen.Home); } });
                    Add("Close", Close);
                    break;
                case Screen.SaveSlots:
                    info = "Keep this run in a slot. Resume it any time from the Breach (Play Online, Resume saved run).";
                    for (int i = 1; i <= RunBackup.Slots; i++)
                    {
                        int slot = i;
                        Add($"Slot {slot}: {RunBackup.SlotSummary(slot)}", () =>
                        {
                            Toasts.Show(RunBackup.SaveToSlot(slot) ? $"Run saved to slot {slot}" : "Could not save the run", 4f, false);
                            Go(Screen.Session);
                        });
                    }
                    Add("Back", () => Go(Screen.Session));
                    break;
                case Screen.ResumeSlots:
                    for (int i = 0; i <= RunBackup.Slots; i++)
                    {
                        int slot = i;
                        Add((slot == 0 ? "Last autosave: " : $"Slot {slot}: ") + RunBackup.SlotSummary(slot), () => { RunBackup.ResumeSlot(slot); Go(Screen.Host); }, RunBackup.SlotUsable(slot));
                    }
                    Add("Back", () => Go(Screen.Home));
                    break;
                case Screen.ConfirmLeave:
                    info = !InRun ? "Leave this online game?"
                        : NetSession.IsHost ? "Leave the run? It is saved, and you can resume it later from this menu."
                        : "Leave the run? The others can keep playing and you can rejoin.";
                    Add("Stay", () => Go(Screen.Session));
                    Add("Leave", () => { RunBackup.SaveNow("leaving"); s.Leave("left"); Close(); });
                    break;
                case Screen.Settings:
                    Add("Gameplay", () => Go(Screen.SetGame));
                    Add("Your friends", () => Go(Screen.SetFriend));
                    Add("Display", () => Go(Screen.SetDisplay));
                    Add("Help and advanced", () => Go(Screen.SetHelp));
                    Add("Back", Back);
                    break;
                case Screen.SetGame:
                    AddStyleRow();
                    Add("Pausing stops everyone: " + (NetConfig.PauseFreezesEveryone.Value ? "On" : "Off"), () => NetConfig.PauseFreezesEveryone.Value = !NetConfig.PauseFreezesEveryone.Value, true,
                        d => NetConfig.PauseFreezesEveryone.Value = !NetConfig.PauseFreezesEveryone.Value);
                    Add("Shared shells and keys: " + (NetConfig.SharedMoney.Value ? "On" : "Off"), () => NetConfig.SharedMoney.Value = !NetConfig.SharedMoney.Value, true,
                        d => NetConfig.SharedMoney.Value = !NetConfig.SharedMoney.Value);
                    Add("Doors wait for teammates: " + (NetConfig.DoorGraceSeconds.Value > 0f ? NetConfig.DoorGraceSeconds.Value.ToString("0") + " s" : "Off"),
                        () => NetConfig.DoorGraceSeconds.Value = NetConfig.DoorGraceSeconds.Value >= 5f ? 0f : NetConfig.DoorGraceSeconds.Value + 1f, true,
                        d => NetConfig.DoorGraceSeconds.Value = Mathf.Clamp(NetConfig.DoorGraceSeconds.Value + d, 0f, 5f));
                    Add("Back", Back);
                    break;
                case Screen.SetFriend:
                    Add("Locator: " + Game.PartnerUI.LocatorNames[NetConfig.LocatorStyle.Value], () => NetConfig.LocatorStyle.Value = NextLocator(NetConfig.LocatorStyle.Value, 1), true,
                        d => NetConfig.LocatorStyle.Value = NextLocator(NetConfig.LocatorStyle.Value, d));
                    if (NetConfig.LocatorStyle.Value > 0)
                        Add("Locator text: " + Game.PartnerUI.LabelNames[NetConfig.LocatorLabel.Value], () => NetConfig.LocatorLabel.Value = (NetConfig.LocatorLabel.Value + 1) % 3, true,
                            d => NetConfig.LocatorLabel.Value = (NetConfig.LocatorLabel.Value + d + 3) % 3);
                    Add("Your color: " + (NetConfig.MyColor.Value < 0 ? "Auto" : Game.PartnerUI.ColorNames[NetConfig.MyColor.Value]), () => NetConfig.MyColor.Value = (NetConfig.MyColor.Value + 2) % 9 - 1, true,
                        d => NetConfig.MyColor.Value = (NetConfig.MyColor.Value + 1 + d + 9) % 9 - 1);
                    Add("HUD faces: " + (NetConfig.HudPortraits.Value ? "On" : "Off"), () => NetConfig.HudPortraits.Value = !NetConfig.HudPortraits.Value, true,
                        d => NetConfig.HudPortraits.Value = !NetConfig.HudPortraits.Value);
                    Add("Outlines: " + OutlineNames[NetConfig.Outlines.Value], () => NetConfig.Outlines.Value = (NetConfig.Outlines.Value + 1) % 3, true,
                        d => NetConfig.Outlines.Value = (NetConfig.Outlines.Value + d + 3) % 3);
                    Add("Teammate HUD: " + Game.NPlayers.HudPatches.TeammateHudNames[NetConfig.TeammateHud.Value], () => NetConfig.TeammateHud.Value = (NetConfig.TeammateHud.Value + 1) % 4, true,
                        d => NetConfig.TeammateHud.Value = (NetConfig.TeammateHud.Value + d + 4) % 4);
                    Add("Reload text: " + Game.PartnerUI.ReloadNames[NetConfig.ReloadText.Value], () => NetConfig.ReloadText.Value = (NetConfig.ReloadText.Value + 1) % 3, true,
                        d => NetConfig.ReloadText.Value = (NetConfig.ReloadText.Value + d + 3) % 3);
                    Add("Name tags: " + Game.PartnerUI.TagNames[NetConfig.NameTagStyle.Value], () => NetConfig.NameTagStyle.Value = Cycle(TagOrder, NetConfig.NameTagStyle.Value, 1), true,
                        d => NetConfig.NameTagStyle.Value = Cycle(TagOrder, NetConfig.NameTagStyle.Value, d));
                    if (NetConfig.NameTagStyle.Value > 0)
                    {
                        Add("Name color: " + Game.PartnerUI.ColorNames[NetConfig.NameColor.Value], () => NetConfig.NameColor.Value = (NetConfig.NameColor.Value + 1) % Game.PartnerUI.ColorNames.Length, true,
                            d => NetConfig.NameColor.Value = (NetConfig.NameColor.Value + d + Game.PartnerUI.ColorNames.Length) % Game.PartnerUI.ColorNames.Length);
                        Add("Name opacity: " + OpacityNames[NetConfig.NameOpacity.Value], () => NetConfig.NameOpacity.Value = (NetConfig.NameOpacity.Value + 1) % 3, true,
                            d => NetConfig.NameOpacity.Value = (NetConfig.NameOpacity.Value + d + 3) % 3);
                        Add("Name height: " + HeightNames[NetConfig.NameHeight.Value], () => NetConfig.NameHeight.Value = (NetConfig.NameHeight.Value + 1) % 3, true,
                            d => NetConfig.NameHeight.Value = (NetConfig.NameHeight.Value + d + 3) % 3);
                    }
                    Add("Crosshair: " + (NetConfig.FriendCrosshair.Value ? "On" : "Off"), () => NetConfig.FriendCrosshair.Value = !NetConfig.FriendCrosshair.Value, true,
                        d => NetConfig.FriendCrosshair.Value = !NetConfig.FriendCrosshair.Value);
                    Add("Back", Back);
                    break;
                case Screen.SetDisplay:
                    Add("Connection warning: " + WarnNames[NetConfig.ConnWarning.Value], () => NetConfig.ConnWarning.Value = (NetConfig.ConnWarning.Value + 1) % 5, true,
                        d => NetConfig.ConnWarning.Value = (NetConfig.ConnWarning.Value + d + 5) % 5);
                    Add("Overlay size: " + SizeNames[NetConfig.OverlaySize.Value], () => NetConfig.OverlaySize.Value = (NetConfig.OverlaySize.Value + 1) % 3, true,
                        d => NetConfig.OverlaySize.Value = (NetConfig.OverlaySize.Value + d + 3) % 3);
                    Add("Back", Back);
                    break;
                case Screen.SetHelp:
                    Add("Send report", () => Reports.Save());
                    Add("Check for updates: " + (NetConfig.CheckUpdates.Value ? "On" : "Off"), () => NetConfig.CheckUpdates.Value = !NetConfig.CheckUpdates.Value);
                    int sm = NetConfig.SmoothingMs.Value;
                    Add("Friend smoothing: " + (sm < 0 ? "Auto" : sm + " ms"), null, true,
                        d => NetConfig.SmoothingMs.Value = Mathf.Clamp(sm < 0 && d > 0 ? 0 : sm + d * 5, -1, 100));
                    Add("Correction speed: " + NetConfig.CorrectionSpeed.Value.ToString("F2"), null, true,
                        d => NetConfig.CorrectionSpeed.Value = Mathf.Clamp(NetConfig.CorrectionSpeed.Value + d * 0.05f, 0.05f, 1f));
                    Add("Reset to defaults", () =>
                    {
                        NetConfig.SmoothingMs.Value = (int)NetConfig.SmoothingMs.DefaultValue;
                        NetConfig.CorrectionSpeed.Value = (float)NetConfig.CorrectionSpeed.DefaultValue;
                        NetConfig.CoopStyle.Value = (int)NetConfig.CoopStyle.DefaultValue;
                        NetConfig.PauseFreezesEveryone.Value = (bool)NetConfig.PauseFreezesEveryone.DefaultValue;
                        NetConfig.LocatorStyle.Value = (int)NetConfig.LocatorStyle.DefaultValue;
                        NetConfig.NameTagStyle.Value = (int)NetConfig.NameTagStyle.DefaultValue;
                        NetConfig.NameColor.Value = (int)NetConfig.NameColor.DefaultValue;
                        NetConfig.FriendCrosshair.Value = (bool)NetConfig.FriendCrosshair.DefaultValue;
                        NetConfig.NameOpacity.Value = (int)NetConfig.NameOpacity.DefaultValue;
                        NetConfig.NameHeight.Value = (int)NetConfig.NameHeight.DefaultValue;
                        NetConfig.ConnWarning.Value = (int)NetConfig.ConnWarning.DefaultValue;
                        NetConfig.OverlaySize.Value = (int)NetConfig.OverlaySize.DefaultValue;
                        Toasts.Show("Settings reset");
                    });
                    Add("Back", Back);
                    break;
            }
            if (sel >= entries.Count) sel = Math.Max(0, entries.Count - 1);
            if (entries.Count > 0 && !entries[sel].Enabled) Move(1);
        }

        string Title()
        {
            switch (screen)
            {
                case Screen.Host: return "Hosting";
                case Screen.Join: return "Join a friend";
                case Screen.Session: return "Playing online";
                case Screen.Settings: return "Settings";
                case Screen.SetGame: return "Gameplay";
                case Screen.SetFriend: return "Your friends";
                case Screen.SetDisplay: return "Display";
                case Screen.SetHelp: return "Help";
                case Screen.ConfirmLeave: return "Leave?";
                case Screen.SaveSlots: return "Save run";
                case Screen.ResumeSlots: return "Resume a run";
                default: return "Online Co-Op";
            }
        }

        // ---------------- Drawing ----------------

        void OnGUI()
        {
            if (pendingRow >= 0 && entries.Count > 0) { sel = Mathf.Min(pendingRow, entries.Count - 1); pendingRow = -1; }
            if (GungeonUI.Ready) { if (!open) DrawQualityHintGame(); else { DrawGame(); DrawPointer(); } return; }
            if (!open) return; // the game's art is still loading: nothing in a system font meanwhile
            if (!open) { DrawQualityHint(); return; }
            var s = NetSession.Instance;
            GUI.DrawTexture(new Rect(0, 0, UnityEngine.Screen.width, UnityEngine.Screen.height), MenuStyle.DimBg);
            float w = Mathf.Min(UnityEngine.Screen.width * 0.6f, MenuStyle.Item.fontSize * 34f);
            float x = (UnityEngine.Screen.width - w) / 2f;
            float y = UnityEngine.Screen.height * 0.16f;
            float lineH = MenuStyle.Item.fontSize * 2.1f;
            float h = UnityEngine.Screen.height * 0.68f;
            GUI.DrawTexture(new Rect(x - 20, y - 20, w + 40, h), MenuStyle.PanelBg);
            GUI.Label(new Rect(x, y, w, lineH), Title(), MenuStyle.Title);
            y += lineH * 1.2f;

            if (screen == Screen.Host)
            {
                string code = s.InviteCodeText ?? "...";
                GUI.Label(new Rect(x, y, w, lineH * 1.4f), code, MenuStyle.Big);
                y += lineH * 1.5f;
                GUI.Label(new Rect(x, y, w, lineH), s.InviteCodeText != null ? "Copied. Paste it to your friend." : "Getting your code...", MenuStyle.Small);
                y += lineH * 0.8f;
                GUI.Label(new Rect(x, y, w, lineH), RouterLine(s), MenuStyle.Small);
                y += lineH;
            }
            if (screen == Screen.Session) y = DrawPlayers(s, x, y, w, lineH);
            if (!string.IsNullOrEmpty(info))
            {
                GUI.Label(new Rect(x, y, w, lineH * 1.6f), info, MenuStyle.Small);
                y += lineH * 1.3f;
            }
            if (!string.IsNullOrEmpty(s.LastError) && (screen == Screen.Home || screen == Screen.Join))
            {
                GUI.Label(new Rect(x, y, w, lineH * 1.6f), s.LastError, MenuStyle.Small);
                y += lineH * 1.3f;
            }

            for (int i = 0; i < entries.Count; i++)
            {
                var r = new Rect(x, y, w, lineH * 0.9f);
                bool selected = i == sel;
                if (selected) GUI.DrawTexture(r, MenuStyle.SelBg);
                var style = selected ? MenuStyle.ItemSel : MenuStyle.Item;
                var old = GUI.color;
                if (!entries[i].Enabled) GUI.color = new Color(1, 1, 1, 0.4f);
                string label = entries[i].Adjust != null ? "<  " + entries[i].Label + "  >" : entries[i].Label;
                if (GUI.Button(r, label, style) && entries[i].Enabled)
                {
                    sel = i;
                    entries[i].Do?.Invoke();
                    entries[i].Adjust?.Invoke(1);
                }
                GUI.color = old;
                y += lineH;
            }

            if (typing)
            {
                // Enter and Esc first: the text field below uses them up, so a check after it never sees them.
                if (CodeKeys(s)) return;
                GUI.SetNextControlName("go_code");
                typedCode = GUI.TextField(new Rect(x, y, w, lineH * 0.9f), typedCode, 40, MenuStyle.Item);
                GUI.FocusControl("go_code");
                y += lineH;
                GUI.Label(new Rect(x, y, w, lineH), "Type the code and press Enter. Esc cancels.", MenuStyle.Small);
            }

            GUI.Label(new Rect(x, UnityEngine.Screen.height * 0.16f + h - lineH * 1.6f, w, lineH),
                "A  choose     B  back     ← →  change", MenuStyle.Small);
        }

        // ---------------- Drawing with the game's own art ----------------

        static readonly Color Dim = new Color(0.62f, 0.6f, 0.58f);

        static GameCursorController cursorCtl;
        static System.Func<bool> gameShowsPointer;

        /// <summary>The game draws its mouse pointer underneath this menu. Draw the same pointer again on top
        /// while the menu is open (only when the game itself would show one).</summary>
        static void DrawPointer()
        {
            if (Event.current.type != EventType.Repaint) return;
            try
            {
                if (gameShowsPointer == null)
                {
                    var prop = HarmonyLib.AccessTools.Property(typeof(GameCursorController), "showMouseCursor");
                    gameShowsPointer = prop != null ? (System.Func<bool>)(() => (bool)prop.GetValue(null, null)) : () => true;
                }
                if (!gameShowsPointer() && !(Dev.DevEnv.IsHarness && System.Environment.GetEnvironmentVariable("GO_TEST_POINTER") != null)) return;
                if (!cursorCtl) cursorCtl = UnityEngine.Object.FindObjectOfType<GameCursorController>();
                if (!cursorCtl) return;
                var tex = cursorCtl.normalCursor;
                int idx = GameManager.Options != null ? GameManager.Options.CurrentCursorIndex : -1;
                if (cursorCtl.cursors != null && idx >= 0 && idx < cursorCtl.cursors.Length && cursorCtl.cursors[idx]) tex = cursorCtl.cursors[idx];
                if (!tex) return;
                float scale = Pixelator.Instance != null ? (int)Pixelator.Instance.ScaleTileScale : 3f;
                var size = new Vector2(tex.width, tex.height) * scale;
                var m = new Vector2(UnityEngine.Input.mousePosition.x, UnityEngine.Screen.height - UnityEngine.Input.mousePosition.y);
                // UI test (hidden window, no real mouse over it): GO_TEST_POINTER=x,y as fractions of the screen.
                var tp = Dev.DevEnv.IsHarness ? System.Environment.GetEnvironmentVariable("GO_TEST_POINTER") : null;
                if (!string.IsNullOrEmpty(tp))
                {
                    var parts = tp.Split(',');
                    m = new Vector2(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture) * UnityEngine.Screen.width,
                                    float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture) * UnityEngine.Screen.height);
                }
                var oldMatrix = GUI.matrix;
                GUI.matrix = Matrix4x4.identity;
                GUI.DrawTexture(new Rect(m.x + 0.5f - size.x / 2f, m.y + 0.5f - size.y / 2f, size.x, size.y), tex);
                GUI.matrix = oldMatrix;
            }
            catch (System.Exception e) { if (!pointerErrorLogged) { pointerErrorLogged = true; Plugin.Log.LogWarning("Menu pointer: " + e.Message); } }
        }
        static bool pointerErrorLogged;

        /// <summary>
        /// The menu as the game would draw it: the HUD's dark gun-box frame, Alagard title, Daikon items, gold for
        /// the selected row with the options menu's little arrows, 04b03 for small print.
        /// </summary>
        void DrawGame()
        {
            var s = NetSession.Instance;
            int px = GungeonUI.Px;
            float sw = UnityEngine.Screen.width, sh = UnityEngine.Screen.height;
            var old = GUI.color;
            GUI.color = new Color(0, 0, 0, 0.6f);
            GUI.DrawTexture(new Rect(0, 0, sw, sh), Texture2D.whiteTexture);
            GUI.color = old;

            float row = 17 * px, small = 11 * px;
            // Width first (it only depends on the rows), then every text block is wrapped to fit inside it.
            string router = screen == Screen.Host ? RouterLine(s) : "";
            float widest = GungeonUI.Measure(Title(), GungeonUI.Font.Title).x;
            foreach (var e0 in entries) widest = Mathf.Max(widest, GungeonUI.Measure(e0.Label, GungeonUI.Font.Menu).x + 30 * px);
            if (screen == Screen.Host) widest = Mathf.Max(widest, GungeonUI.Measure(s.InviteCodeText ?? "", GungeonUI.Font.Menu, 1.5f).x);
            float w = Mathf.Min(sw - 20 * px, Mathf.Max(200 * px, widest + 36 * px)), wrapW = w - 24 * px;
            float ContentH(float r)
            {
                float c = 26 * px;
                if (screen == Screen.Host) c += 22 * px + 3 * px + small + (router.Length > 0 ? small * Wrap(router, wrapW).Count : 0) + 8 * px;
                if (screen == Screen.Session) c += r * (1 + Mathf.Max(1, NetSession.Active ? PlayerCount(s) - 1 : 1)) + 8 * px;
                if (!string.IsNullOrEmpty(info)) c += small * Wrap(info, wrapW).Count + 4 * px;
                if (!string.IsNullOrEmpty(s.LastError) && (screen == Screen.Home || screen == Screen.Join)) c += small * Wrap(s.LastError, wrapW).Count + 4 * px;
                return c + r * entries.Count + (typing ? r + small : 0) + small + 10 * px;
            }
            float contentH = ContentH(row);
            // A long page (Your friends has 14 rows) must still fit on the screen: tighten the rows until it does.
            while (contentH + 20 * px > sh - 8 * px && row > 12 * px) { row -= px; contentH = ContentH(row); }
            float h = contentH + 20 * px;
            float x = Mathf.Round((sw - w) / 2f), y = Mathf.Round((sh - h) / 2f);
            GungeonUI.Fill(new Rect(x + 2 * px, y + 2 * px, w - 4 * px, h - 4 * px), 1f);
            switch (NetConfig.LookFrame.Value)
            {
                case 1: GungeonUI.Frame("pause_menu_9slice", new Rect(x - 4 * px, y - 14 * px, w + 8 * px, h + 16 * px), Color.white); break;
                case 2: GungeonUI.Frame("DeathScreenFrame", new Rect(x - 6 * px, y - 6 * px, w + 12 * px, h + 12 * px), Color.white); break;
                default: GungeonUI.Frame("weapon_box_01", new Rect(x, y, w, h), Color.white); break;
            }
            float cx = x + w / 2f, inner = w - 24 * px;
            float cy = y + 9 * px;

            switch (NetConfig.LookTitle.Value)
            {
                case 1: GungeonUI.TextCentered(Title(), cx, cy, GungeonUI.Font.Menu, Color.white, 1.25f); break;
                case 2: GungeonUI.TextCentered(Title(), cx, cy, GungeonUI.Font.Title, Color.white); break;
                default: GungeonUI.TextCentered(Title(), cx, cy, GungeonUI.Font.Title, GungeonUI.Gold); break;
            }
            cy += 19 * px;
            GungeonUI.Frame("crosshair-hyphen", new Rect(cx - 50 * px, cy - 4 * px, 100 * px, 18 * px), new Color(1, 1, 1, 0.8f));
            cy += 12 * px;

            if (screen == Screen.Host)
            {
                // One block: the code, and right under it what to do with it. Then a clear gap before the buttons.
                string code = s.InviteCodeText ?? "...";
                var cs = GungeonUI.TextCentered(code, cx, cy, GungeonUI.Font.Menu, GungeonUI.Gold, 1.5f);
                cy += Mathf.Max(cs.y, 22 * px) + 3 * px;
                GungeonUI.TextCentered(s.InviteCodeText != null ? "Copied. Paste it to your friend." : "Getting your code...", cx, cy, GungeonUI.Font.Small, GungeonUI.Cream);
                cy += small;
                if (router.Length > 0) cy = Wrapped(router, cx, cy, inner, Dim);
                cy += 8 * px;
            }
            if (screen == Screen.Session) cy = DrawPlayersGame(s, cx, cy, row) + 4 * px;
            if (!string.IsNullOrEmpty(info)) cy = Wrapped(info, cx, cy, inner, GungeonUI.Cream) + 4 * px;
            if (!string.IsNullOrEmpty(s.LastError) && (screen == Screen.Home || screen == Screen.Join))
                cy = Wrapped(s.LastError, cx, cy, inner, new Color(1f, 0.55f, 0.45f)) + 4 * px;
            cy += 2 * px;

            for (int i = 0; i < entries.Count; i++)
            {
                bool selected = i == sel, enabled = entries[i].Enabled;
                int look = NetConfig.LookSelect.Value;
                var color = !enabled ? new Color(0.45f, 0.43f, 0.42f) : selected ? (look == 2 ? Color.white : GungeonUI.Gold) : GungeonUI.Cream;
                if (selected && enabled && look == 2)
                {
                    var ms = GungeonUI.Measure(entries[i].Label, GungeonUI.Font.Menu);
                    GungeonUI.Frame("button_generic_raised_001", new Rect(cx - ms.x / 2f - 10 * px, cy - 4 * px, ms.x + 20 * px, row), Color.white, new RectOffset(6, 6, 6, 6));
                }
                var size = GungeonUI.TextCentered(entries[i].Label, cx, cy, GungeonUI.Font.Menu, color);
                if (selected && enabled && look == 1)
                {
                    float spin = Time.unscaledTime * 90f;
                    var mid = new Vector2(cx - size.x / 2f - 11 * px, cy + size.y / 2f);
                    var m0 = GUI.matrix;
                    GUIUtility.RotateAroundPivot(spin, mid);
                    GungeonUI.SpriteAt("spinning_white_crosshair_001", mid, GungeonUI.Gold, 0.75f);
                    GUI.matrix = m0;
                }
                if (selected && enabled && look == 0)
                {
                    float gap = 7 * px, mid = cy + size.y / 2f;
                    if (entries[i].Adjust != null)
                    {
                        GungeonUI.SpriteAt("ui_options_arrow_left", new Vector2(cx - size.x / 2f - gap, mid), GungeonUI.Gold);
                        GungeonUI.SpriteAt("ui_options_arrow_right", new Vector2(cx + size.x / 2f + gap, mid), GungeonUI.Gold);
                    }
                    else
                    {
                        GungeonUI.SpriteAt("ui_options_arrow_right", new Vector2(cx - size.x / 2f - gap, mid), GungeonUI.Gold);
                        GungeonUI.SpriteAt("ui_options_arrow_left", new Vector2(cx + size.x / 2f + gap, mid), GungeonUI.Gold);
                    }
                }
                // Mouse: an invisible button over the row.
                if (GUI.Button(new Rect(x + 8 * px, cy - 2 * px, w - 16 * px, row), GUIContent.none, GUIStyle.none) && enabled)
                {
                    sel = i;
                    entries[i].Do?.Invoke();
                    entries[i].Adjust?.Invoke(1);
                }
                cy += row;
            }

            if (typing)
            {
                // Enter and Esc first: the text field below uses them up, so a check after it never sees them.
                if (CodeKeys(s)) return;
                GUI.SetNextControlName("go_code");
                var field = new GUIStyle(GUIStyle.none);
                field.normal.textColor = Color.clear;
                var cursor = GUI.skin.settings.cursorColor;
                GUI.skin.settings.cursorColor = Color.clear;
                typedCode = GUI.TextField(new Rect(x + 12 * px, cy, w - 24 * px, row), typedCode, 40, field);
                GUI.skin.settings.cursorColor = cursor;
                GUI.FocusControl("go_code");
                GungeonUI.Frame("weapon_box_01", new Rect(x + 16 * px, cy - 3 * px, w - 32 * px, row + 2 * px), new Color(1, 1, 1, 0.9f));
                bool blink = Mathf.Repeat(Time.unscaledTime, 1f) < 0.55f;
                GungeonUI.TextCentered((typedCode ?? "") + (blink ? "_" : " "), cx, cy, GungeonUI.Font.Menu, GungeonUI.Gold);
                cy += row + 2 * px;
                GungeonUI.TextCentered("Type the code and press Enter. Esc cancels.", cx, cy, GungeonUI.Font.Small, Dim);
            }

            GungeonUI.TextCentered("A choose    B back    < > change", cx, y + h - 14 * px, GungeonUI.Font.Small, Dim);
        }

        /// <summary>Typing a code: Enter joins (or says the code is not valid), Esc cancels. True when typing ended.</summary>
        bool CodeKeys(NetSession s)
        {
            var e = Event.current;
            if (e.type != EventType.KeyDown) return false;
            if (e.keyCode == KeyCode.Return || e.keyCode == KeyCode.KeypadEnter)
            {
                e.Use();
                typing = false;
                if (Dev.DevEnv.IsHarness) Plugin.Log.LogInfo($"CODE entered \"{typedCode}\" valid={InviteCode.TryParse(typedCode, out _)}");
                if (InviteCode.TryParse(typedCode, out _)) s.Join(typedCode);
                else Toasts.Show("That code is not valid. Codes look like ABCD-EFGH-JKLM.", 4f, true);
                return true;
            }
            if (e.keyCode == KeyCode.Escape) { e.Use(); typing = false; return true; }
            return false;
        }

        static readonly string[] StyleNames = { "Classic", "Explore" };

        /// <summary>Co-op style: the host chooses (it runs the rooms); a guest sees the host's choice, dimmed.</summary>
        void AddStyleRow()
        {
            if (NetSession.Active && !NetSession.IsHost)
            {
                int hs = Lobby.HostCoopStyle;
                Add("Co-op style: " + (hs >= 0 && hs < StyleNames.Length ? StyleNames[hs] : "...") + " (host's choice)", null, false);
                return;
            }
            Add("Co-op style: " + StyleNames[NetConfig.CoopStyle.Value], () => NetConfig.CoopStyle.Value = (NetConfig.CoopStyle.Value + 1) % 2, true,
                d => NetConfig.CoopStyle.Value = (NetConfig.CoopStyle.Value + d + 2) % 2);
        }
        /// <summary>How many can play: the host picks 2, 3 or 4 (more only by editing the config, experimental).</summary>
        void AddCountRow()
        {
            var s = NetSession.Instance;
            if (NetSession.Active && !NetSession.IsHost)
            {
                int hm = Lobby.HostMaxPlayers > 0 ? Lobby.HostMaxPlayers : (s != null && s.Node != null ? s.Node.HostMaxPlayers : -1);
                Add("Players: " + (hm > 0 ? hm.ToString() : "...") + " (host's choice)", null, false);
                return;
            }
            int cur = NetConfig.MaxPlayers.Value;
            Add("Players: " + cur, () => SetCount(cur + 1), true, d => SetCount(cur + d));
        }

        static void SetCount(int n)
        {
            n = n < 2 ? 4 : n > 4 ? 2 : n; // 2, 3, 4, round and round
            var node = NetSession.Instance != null ? NetSession.Instance.Node : null;
            int already = node != null ? node.Connections.Count + 1 : 1;
            if (n < already) { Toasts.Show(already + " players are already in", 3f, true); n = already; }
            NetConfig.MaxPlayers.Value = n;
            if (node != null && NetSession.IsHost) node.SetMaxPlayers(n);
        }

        static readonly string[] WarnNames = { "Off", "Top", "Top, faint", "Corner", "Corner, faint" };
        static readonly string[] SizeNames = { "Small", "Normal", "Large" };

        static readonly int[] LocatorOrder = { 5, 1, 0 };   // pickup tag (default), speech bubble, off
        static readonly int[] TagOrder = { 9, 1, 6, 0 };     // above faint (default), above, below feet, off
        static readonly string[] OpacityNames = { "100%", "75%", "50%" };
        static readonly string[] OutlineNames = { "Off", "Teammates", "Everyone" };
        static readonly string[] HeightNames = { "Close", "Normal", "High" };

        static int NextLocator(int cur, int d) => Cycle(LocatorOrder, cur, d);

        static int Cycle(int[] order, int cur, int d)
        {
            int i = System.Array.IndexOf(order, cur);
            if (i < 0) i = 0;
            return order[(i + d + order.Length) % order.Length];
        }

        int WrapCount(string text) => Wrap(text, Mathf.Min(UnityEngine.Screen.width - 40 * GungeonUI.Px, 250 * GungeonUI.Px) - 24 * GungeonUI.Px).Count;

        static List<string> Wrap(string text, float width)
        {
            var lines = new List<string>();
            string line = "";
            foreach (var word in text.Split(' '))
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && GungeonUI.Measure(next, GungeonUI.Font.Small).x > width) { lines.Add(line); line = word; }
                else line = next;
            }
            if (line.Length > 0) lines.Add(line);
            return lines;
        }

        static float Wrapped(string text, float cx, float y, float width, Color color)
        {
            foreach (var l in Wrap(text, width)) { GungeonUI.TextCentered(l, cx, y, GungeonUI.Font.Small, color); y += 11 * GungeonUI.Px; }
            return y;
        }

        float DrawPlayersGame(NetSession s, float cx, float y, float row)
        {
            int px = GungeonUI.Px;
            string me = "You" + (NetConfig.PlayerName.Value == "Gungeoneer" ? "" : " (" + NetConfig.PlayerName.Value + ")") + (NetSession.IsHost ? " - host" : "");
            DrawPlayerRow(Game.PlayerSlots.Local, me, null, Color.white, cx, y);
            y += row;
            if (s.Node == null || s.Node.Connections.Count == 0)
            {
                GungeonUI.TextCentered(NetSession.IsHost ? (NetConfig.MaxPlayers.Value > 2 ? "Waiting for your friends..." : "Waiting for your friend...") : "Connecting...", cx, y + 3 * px, GungeonUI.Font.Small, Dim);
                return y + row;
            }
            // Every spot in the game: the host and each friend (with their link quality where we hold the link, or how
            // their input reaches us when we don't), and the spots still open.
            var lobby = UnityEngine.Object.FindObjectOfType<Lobby>();
            int max = PlayerCount(s);
            for (int slot = 0; slot < max; slot++)
            {
                if (slot == NetSession.LocalSlot) continue;
                Net.Connection c = null;
                foreach (var k in s.Node.Connections) if (k.Slot == slot) c = k;
                var p = Game.PlayerSlots.ForSlot(slot);
                Lobby.Entry entry = null;
                if (lobby != null) lobby.Roster.TryGetValue(slot, out entry);
                if (c == null && p == null && entry == null)
                {
                    GungeonUI.TextCentered("Open spot", cx, y + 3 * px, GungeonUI.Font.Small, Dim);
                    y += row;
                    continue;
                }
                string who = c != null ? c.Tag as string : (entry != null ? entry.Name : null);
                if (string.IsNullOrEmpty(who) || who == "Gungeoneer") who = GungeonUI.CharacterName(p);
                if (slot == 0) who += " - host";
                string right; Color col;
                if (c != null) { string q = Quality(c, out col); right = q + "  " + Mathf.RoundToInt(Shown(c)) + " ms"; }
                else { col = Dim; right = (PeerLanes.IsDirect(slot) ? "direct" : "via host") + "  "; }
                DrawPlayerRow(p, who, right, col, cx, y);
                y += row;
            }
            return y + 4 * px;
        }

        /// <summary>How many can play in this session: the host's cap, as the host knows it or as it told us.</summary>
        static int PlayerCount(NetSession s)
        {
            if (NetSession.IsHost) return Mathf.Max(2, NetConfig.MaxPlayers.Value);
            if (Lobby.HostMaxPlayers > 0) return Lobby.HostMaxPlayers;
            return s.Node != null && s.Node.HostMaxPlayers > 0 ? s.Node.HostMaxPlayers : 2;
        }

        static void DrawPlayerRow(PlayerController p, string name, string right, Color rightColor, float cx, float y)
        {
            int px = GungeonUI.Px;
            var head = GungeonUI.HeadFor(p);
            if (head == null || GungeonUI.Item(head) == null) head = "Player_Coop_001";
            // Same font and baseline for the whole row; only the quality word is colored.
            string quality = null, ms = null;
            if (right != null) { int sp = right.IndexOf("  "); quality = sp > 0 ? right.Substring(0, sp) : right; ms = sp > 0 ? right.Substring(sp + 2) : ""; }
            var ns = GungeonUI.Measure(name, GungeonUI.Font.Menu);
            var qs = quality != null ? GungeonUI.Measure(quality, GungeonUI.Font.Menu) : Vector2.zero;
            var mss = ms != null ? GungeonUI.Measure(ms, GungeonUI.Font.Menu) : Vector2.zero;
            var hs = GungeonUI.SpriteSize(head);
            float gap = 7 * px;
            float total = hs.x + 5 * px + ns.x + (quality != null ? gap + qs.x + 4 * px + mss.x : 0), x = cx - total / 2f;
            GungeonUI.Sprite(head, new Rect(x, y + (ns.y - hs.y) / 2f - px, hs.x, hs.y), Color.white);
            x += hs.x + 5 * px;
            GungeonUI.Text(name, new Vector2(x, y), GungeonUI.Font.Menu, GungeonUI.Cream);
            if (quality != null)
            {
                x += ns.x + gap;
                GungeonUI.Text(quality, new Vector2(x, y), GungeonUI.Font.Menu, rightColor);
                GungeonUI.Text(ms, new Vector2(x + qs.x + 4 * px, y), GungeonUI.Font.Menu, Dim);
            }
        }

        /// <summary>Connection note at the top center, only when the connection is not great.</summary>
        void DrawQualityHintGame()
        {
            var s = NetSession.Instance;
            if (s == null || !NetSession.Active || s.Node == null) return;
            if (GameManager.HasInstance && (GameManager.Instance.IsLoadingLevel || GameManager.Instance.IsPaused)) return;
            int mode = NetConfig.ConnWarning.Value;
            if (mode == 0) return;
            float scale = GungeonUI.OverlayScale;
            float alpha = mode == 2 || mode == 4 ? 0.45f : 1f;
            bool corner = mode >= 3;
            float y = corner ? UnityEngine.Screen.height - 72 * GungeonUI.Px : 6 * GungeonUI.Px;
            // One short line, however many friends: the worst connection sets the colour.
            var bad = new List<string>();
            Color col = Color.white; float worst = -1f; string single = null, worstName = null;
            foreach (var c in s.Node.Connections)
            {
                string q = Quality(c, out var cc);
                if (q == "Great") continue;
                bad.Add($"{GungeonUI.WhoName(c)} {Shown(c):F0} ms");
                single = $"Connection {q}: {Shown(c):F0} ms, {c.LossPercent:F0}% loss";
                if (Shown(c) > worst) { worst = Shown(c); col = cc; worstName = $"{GungeonUI.WhoName(c)} {Shown(c):F0} ms"; }
            }
            if (bad.Count == 0) return;
            // Several friends: only the slowest, so the line stays short between the hearts.
            string text = s.Node.Connections.Count > 1 ? "Slow: " + worstName + (bad.Count > 1 ? $" (+{bad.Count - 1})" : "") : single;
            col.a = alpha;
            if (corner) GungeonUI.Text(text, new Vector2(8 * GungeonUI.Px, y), GungeonUI.Font.Small, col, scale);
            else GungeonUI.TextCentered(text, UnityEngine.Screen.width / 2f, y, GungeonUI.Font.Small, col, scale);
        }

        static string RouterLine(NetSession s)
        {
            if (Dev.DevEnv.IsHarness && System.Environment.GetEnvironmentVariable("GO_FAKE_UPNP") == "failed")
                return "If your friend can't get in, ask for their reply code and pick Let in friend."; // UI test: a router that did not open
            if (s.Upnp == null) return "";
            switch (s.Upnp.Status)
            {
                case UpnpMapper.State.Mapped: return "Your router was opened automatically.";
                case UpnpMapper.State.Failed: return "If your friend can't get in, ask for their reply code and pick Let in friend.";
                default: return "Opening your router...";
            }
        }

        static float Shown(Connection c) => c.DisplayRttMs >= 0 ? c.DisplayRttMs : c.RttMs;

        public static string Quality(Connection c, out Color color)
        {
            float rtt = c.DisplayRttMs >= 0 ? c.DisplayRttMs : c.RttMs, jit = c.DisplayRttMs >= 0 ? c.DisplayJitterMs : c.RttVarMs;
            float score = rtt + jit * 2f + c.LossPercent * 20f;
            if (score < 120) { color = new Color(0.45f, 0.9f, 0.45f); return "Great"; }
            if (score < 220) { color = new Color(0.95f, 0.85f, 0.35f); return "OK"; }
            color = new Color(0.95f, 0.4f, 0.35f); return "Poor";
        }

        float DrawPlayers(NetSession s, float x, float y, float w, float lineH)
        {
            GUI.Label(new Rect(x, y, w, lineH), "You" + (NetConfig.PlayerName.Value == "Gungeoneer" ? "" : ": " + NetConfig.PlayerName.Value) + (NetSession.IsHost ? " (host)" : ""), MenuStyle.Item);
            y += lineH * 0.9f;
            if (s.Node == null || s.Node.Connections.Count == 0)
            {
                GUI.Label(new Rect(x, y, w, lineH), NetSession.IsHost ? "Waiting for your friend..." : "Connecting...", MenuStyle.Small);
                return y + lineH;
            }
            foreach (var c in s.Node.Connections)
            {
                string q = Quality(c, out var col);
                var style = new GUIStyle(MenuStyle.Item);
                style.normal.textColor = col;
                string who = c.Tag as string;
                if (string.IsNullOrEmpty(who) || who == "Gungeoneer") who = NetSession.IsHost ? "A player" : "Host";
                GUI.Label(new Rect(x, y, w, lineH), $"{who}: connection {q} ({Shown(c):F0} ms)", style);
                y += lineH * 0.9f;
            }
            return y + lineH * 0.3f;
        }

        /// <summary>Small connection note in the corner, only when the connection is not great.</summary>
        void DrawQualityHint()
        {
            var s = NetSession.Instance;
            if (s == null || !NetSession.Active || s.Node == null) return;
            foreach (var c in s.Node.Connections)
            {
                string q = Quality(c, out var col);
                if (q == "Great") continue;
                var style = new GUIStyle(MenuStyle.Small) { alignment = TextAnchor.LowerLeft };
                style.normal.textColor = col;
                GUI.Label(new Rect(10, UnityEngine.Screen.height - 40, 600, 30), $"Connection {q}: {Shown(c):F0} ms, {c.LossPercent:F0}% loss", style);
            }
        }
    }
}
