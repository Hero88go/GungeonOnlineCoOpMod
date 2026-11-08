using System.Text;
using Dungeonator;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Floor transitions. The host decides when the next floor loads and with which run seed; the client
    /// loads only on the host's word. After every load both machines log a fingerprint of the generated floor
    /// so any difference in generation is visible (and later reconciled by the host's floor manifest).
    /// </summary>
    internal sealed class RunSync : MonoBehaviour
    {
        const byte MsgLoadLevel = 1, MsgRejoin = 2, MsgLayout = 3, MsgClone = 4, MsgMods = 5, MsgCustomRequest = 7, MsgPastBoss = 8;
        /// <summary>Secret floors (the Oubliette, the Abbey, ...) load by name, not by floor number: sent as "custom:NAME".</summary>
        const string CustomTag = "custom:";
        /// <summary>Host: the secret floor we are on, or null on a regular floor.</summary>
        static string currentCustom;
        /// <summary>A secret floor's number: the next regular floor's plus this, so the two never share a number (the
        /// floor check once compared a friend's next floor with the host's secret one and sent them to the Breach).</summary>
        public const int SecretFloor = 1000;
        /// <summary>The regular floor number to save and resume at (a secret floor resumes at the floor after it).</summary>
        public static int RegularIndex => CurrentIndex >= SecretFloor ? CurrentIndex - SecretFloor : CurrentIndex;
        /// <summary>Index of the floor currently loaded (both machines).</summary>
        public static int CurrentIndex = -1;
        public static RunSync Instance { get; private set; }

        static bool clientAllowed;
        static byte resetKind;
        static bool applyingReset, applyingClone;
        static bool resetUnload, resetMenu;
        static QuickRestartOptions resetOptions;
        public static bool CloneInProgress { get; private set; }
        public string LastFingerprint = "-";
        string lastScene;

        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.LoadNextLevel)),
                prefix: new HarmonyMethod(typeof(RunSync), nameof(LoadNextLevelPrefix)));
            h.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.DelayedLoadNextLevel)),
                prefix: new HarmonyMethod(typeof(RunSync), nameof(DelayedPrefix)));
            foreach (var name in new[] { "ReturnToFoyer", "LoadCharacterSelect", "QuickRestart" })
                h.Patch(AccessTools.Method(typeof(GameManager), name),
                    prefix: new HarmonyMethod(typeof(RunSync), nameof(ResetPrefix)),
                    finalizer: new HarmonyMethod(typeof(RunSync), nameof(ResetFinished)));
            // Reject a guest's reset before its UI closes or a delayed coroutine sets the loading flag.
            foreach (var name in new[] { "DelayedReturnToFoyer", "DelayedLoadCharacterSelect", "DelayedQuickRestart" })
                h.Patch(AccessTools.Method(typeof(GameManager), name), prefix: new HarmonyMethod(typeof(RunSync), nameof(GuestResetAllowed)));
            foreach (var name in new[] { "DoMainMenu", "DoQuickRestart" })
                h.Patch(AccessTools.Method(typeof(AmmonomiconDeathPageController), name), prefix: new HarmonyMethod(typeof(RunSync), nameof(GuestResetAllowed)));
            // Secret floors: the host takes everyone; a guest who finds the way in asks the host to go.
            h.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.LoadCustomLevel)), prefix: new HarmonyMethod(typeof(RunSync), nameof(LoadCustomPrefix)));
            h.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.DelayedLoadCustomLevel)), prefix: new HarmonyMethod(typeof(RunSync), nameof(DelayedCustomPrefix)));
            h.Patch(AccessTools.Method(typeof(PlayerController), "DoCloneEffect"),
                prefix: new HarmonyMethod(typeof(RunSync), nameof(ClonePrefix)));
            h.Patch(AccessTools.Method(typeof(PlayerController), nameof(PlayerController.HandleCloneItem)),
                prefix: new HarmonyMethod(typeof(RunSync), nameof(CloneItemPrefix)));
        }

        static bool ResetPrefix(System.Reflection.MethodBase __originalMethod, object[] __args, out byte __state)
        {
            __state = resetKind;
            if (!NetSession.Active) return true;
            if (!GuestResetAllowed()) return false;
            resetKind = __originalMethod.Name == "ReturnToFoyer" ? (byte)1 : __originalMethod.Name == "LoadCharacterSelect" ? (byte)2 : (byte)3;
            if (resetKind == 2) { resetUnload = (bool)__args[0]; resetMenu = (bool)__args[1]; }
            if (resetKind == 3) resetOptions = (QuickRestartOptions)__args[0];
            return true;
        }

        /// <summary>Guest: leave a run of our own for the Breach, where the host is waiting for our pick.</summary>
        public static void GuestToBreach()
        {
            var gm = GameManager.Instance;
            rejoining = hasPending = false;
            CurrentIndex = -1;
            Replication.WorldSync.Instance?.OnFloorUnloading();
            clientAllowed = applyingReset = true;
            try { gm.ReturnToFoyer(); }
            finally { applyingReset = false; clientAllowed = false; }
        }

        static bool GuestResetAllowed()
        {
            if (!NetSession.Active || NetSession.IsHost || applyingReset || SceneObjects.InBreach) return true;
            Toasts.Show("The host chooses when to restart or return to the Breach.", 4f);
            return false;
        }

        static System.Exception ResetFinished(System.Exception __exception, byte __state)
        {
            resetKind = __state;
            return __exception;
        }

        static bool CloneItemPrefix(PlayerController __instance)
        {
            if (!NetSession.Active) return true;
            if (NPlayers.Players.AnyOtherAlive(__instance))
                Traverse.Create(__instance).Field("m_cloneWaitingForCoopDeath").SetValue(true);
            else AccessTools.Method(typeof(PlayerController), "DoCloneEffect").Invoke(__instance, null);
            return false;
        }

        static bool ClonePrefix(PlayerController __instance)
        {
            if (!NetSession.Active || applyingClone) return true;
            // Every fatal guest hit reaches the host through VitalsSync. Only the host starts the shared Clone.
            if (!NetSession.IsHost || CloneInProgress) return false;
            CloneInProgress = true;
            var w = new WireWriter(2);
            w.U8(MsgClone); w.U8((byte)PlayerSlots.SlotOf(__instance));
            NetSession.Instance.Node.Broadcast((byte)Channel.Control, w.ToArray(), Delivery.ReliableOrdered);
            NetSession.Instance.FlushNow();
            Plugin.Log.LogInfo("RunSync: Clone restarts everyone, owner slot " + PlayerSlots.SlotOf(__instance));
            return true;
        }

        void Awake() { Instance = this; }

        void Start()
        {
            NetSession.Instance.On(Channel.Control, OnControl);
            // Everyone says which other mods they run as soon as they connect, so a mismatch shows before the run.
            NetSession.Instance.PeerJoined += c =>
            {
                var w = new WireWriter(256);
                w.U8(MsgMods); w.Str(ModList());
                c.Send((byte)Channel.Control, w.ToArray(), Delivery.ReliableOrdered);
            };
            NetSession.Instance.SessionEnded += () =>
            {
                rejoining = hasPending = CloneInProgress = clientAllowed = false;
                rejoinWhenReady.Clear(); lastRejoin.Clear();
                hostLayout = null; hostLayoutIndex = warnedIndex = -2;
            };
        }

        static Traverse GM => Traverse.Create(GameManager.Instance);

        /// <summary>Client: floor changes only come from the host.</summary>
        static bool DelayedPrefix()
        {
            if (!NetSession.Active || NetSession.IsHost) return true;
            return clientAllowed;
        }

        static bool applyingCustom;

        /// <summary>Host: a secret floor loads by name; tell everyone to load the same one.
        /// Guest: only the host's word loads one (asking the host if this guest found the way in).</summary>
        static bool LoadCustomPrefix(string custom)
        {
            if (!NetSession.Active) return true;
            var gm = GameManager.Instance;
            if (NetSession.IsHost)
            {
                if (custom == "tt_foyer" || custom == "MainMenu" || custom == "Foyer" || custom == "tt_tutorial" || SceneObjects.InBreach) return true;
                currentCustom = custom + PastFlags(custom);
                int index = GM.Field("nextLevelIndex").GetValue<int>() + SecretFloor;
                CurrentIndex = index;
                ReseedForFloor(gm.CurrentRunSeed, index);
                var w = new WireWriter(96);
                w.U8(MsgLoadLevel); w.I32(gm.CurrentRunSeed); w.I32(index); w.Str(CustomTag + currentCustom); w.U8(0);
                Replication.WorldSync.Instance?.OnFloorUnloading();
                GenContext.CaptureAndSend(gm.CurrentRunSeed, index);
                NetSession.Instance.Node.Broadcast((byte)Channel.Control, w.ToArray(), Delivery.ReliableOrdered);
                NetSession.Instance.FlushNow();
                Plugin.Log.LogInfo($"RunSync: host loading secret floor {custom}");
                return true;
            }
            if (clientAllowed) { clientAllowed = false; return true; }
            AskHostFor(custom);
            GM.Field("m_loadingLevel").SetValue(false);
            return false;
        }

        /// <summary>A character's Past carries state the Ark sets on the host's screen only: which Past (the Cultist's
        /// co-op Past and the Gunslinger's both load by flag), and the reset to final-fight guns and full health that
        /// every player gets. Sent after the level name as ";coop", ";gunslinger", ";reset".</summary>
        static string PastFlags(string custom)
        {
            string f = "";
            if (GameManager.IsCoopPast) f += ";coop";
            if (GameManager.IsGunslingerPast) f += ";gunslinger";
            if (custom.StartsWith("fs_")) f += ";reset";
            PastMode.OwnerSlot = custom.StartsWith("fs_") || GameManager.IsGunslingerPast ? PastMode.PickOwner(custom) : -1;
            PastMode.ShipPast = custom == "fs_pilot";
            if (PastMode.OwnerSlot >= 0) { f += ";owner=" + PastMode.OwnerSlot; Plugin.Log.LogInfo($"RunSync: Past {custom} belongs to slot {PastMode.OwnerSlot}"); }
            return f;
        }

        /// <summary>Guest: apply the Past flags the host sent, before the Past loads. Returns the level name alone.</summary>
        static string ApplyPastFlags(string sent)
        {
            var parts = sent.Split(';');
            bool coop = false, gunslinger = false, reset = false; int owner = -1;
            for (int i = 1; i < parts.Length; i++)
            {
                if (parts[i] == "coop") coop = true;
                else if (parts[i] == "gunslinger") gunslinger = true;
                else if (parts[i] == "reset") reset = true;
                else if (parts[i].StartsWith("owner=") && int.TryParse(parts[i].Substring(6), out var o)) owner = o;
            }
            PastMode.OwnerSlot = owner;
            PastMode.ShipPast = parts[0] == "fs_pilot";
            GameManager.IsCoopPast = coop;
            GameManager.IsGunslingerPast = gunslinger;
            var me = PlayerSlots.Local;
            if (reset && me && me.healthHaver && me.healthHaver.IsAlive)
            {
                // The same reset the Ark gives every player on the host's screen; this player's own things are its own.
                me.ResetToFactorySettings(true, true);
                me.CharacterUsesRandomGuns = false;
                me.IsVisible = true;
                me.ClearAllInputOverrides();
                Plugin.Log.LogInfo("RunSync: Past reset (final-fight guns, full health)");
            }
            if (coop || gunslinger || reset) Plugin.Log.LogInfo($"RunSync: Past {parts[0]} coop={coop} gunslinger={gunslinger} reset={reset} owner={owner}");
            return parts[0];
        }

        /// <summary>This screen's Past owner beat the boss: tell every other screen.</summary>
        public static void SendPastBossDown()
        {
            if (!NetSession.Active) return;
            var w = new WireWriter(8);
            w.U8(MsgPastBoss);
            NetSession.Instance.Node.Broadcast((byte)Channel.Control, w.ToArray(), Delivery.ReliableOrdered);
        }

        /// <summary>This screen's Past owner reached the ending: every other screen plays it too.</summary>
        public static void SendPastEnding()
        {
            if (!NetSession.Active) return;
            var w = new WireWriter(8);
            w.U8(MsgPastBoss); w.U8(1);
            NetSession.Instance.Node.Broadcast((byte)Channel.Control, w.ToArray(), Delivery.ReliableOrdered);
        }

        static bool DelayedCustomPrefix(string customLevel)
        {
            if (!NetSession.Active || NetSession.IsHost || clientAllowed) return true;
            AskHostFor(customLevel);
            return false;
        }

        static float lastAsk;

        static void AskHostFor(string custom)
        {
            if (Time.realtimeSinceStartup - lastAsk < 2f) return;
            lastAsk = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo("RunSync: found the way to secret floor " + custom + ", asking the host");
            var w = new WireWriter(64);
            w.U8(MsgCustomRequest); w.Str(custom);
            NetSession.Instance.Node.Broadcast((byte)Channel.Control, w.ToArray(), Delivery.ReliableOrdered);
        }

        static bool LoadNextLevelPrefix()
        {
            if (!NetSession.Active) return true;
            var gm = GameManager.Instance;
            if (NetSession.IsHost)
            {
                // Everyone must have picked a character before the run starts.
                if (SceneObjects.InBreach)
                {
                    var lobby = FindObjectOfType<Lobby>();
                    foreach (var c in NetSession.Instance.Node.Connections)
                        if (lobby == null || !lobby.Roster.ContainsKey(c.Slot) || string.IsNullOrEmpty(lobby.Roster[c.Slot].Prefab))
                        {
                            Toasts.Show("Waiting for " + GungeonUI.WhoName(c) + " to pick a character", 4f, true);
                            GM.Field("m_loadingLevel").SetValue(false);
                            if (Pixelator.Instance) Pixelator.Instance.FadeToBlack(0.2f, true);
                            return false;
                        }
                }
                var resume = RunBackup.PendingResume;
                if (resume != null && SceneObjects.InBreach)
                {
                    // Resuming a saved run: jump straight to the saved floor with the saved seed.
                    if (gm.CurrentRunSeed != resume.Seed) gm.CurrentRunSeed = resume.Seed;
                    GM.Field("nextLevelIndex").SetValue(resume.FloorIndex);
                    Plugin.Log.LogInfo($"RunSync: resuming run at floor index {resume.FloorIndex}");
                }
                else if (!gm.IsSeeded)
                {
                    int seed = Random.Range(1, int.MaxValue);
                    if (Dev.DevEnv.IsHarness && int.TryParse(System.Environment.GetEnvironmentVariable("GO_RUN_SEED"), out var testSeed) && testSeed > 0) seed = testSeed;
                    gm.CurrentRunSeed = seed;
                    Plugin.Log.LogInfo("RunSync: run seed " + seed);
                }
                int index = GM.Field("nextLevelIndex").GetValue<int>();
                currentCustom = null;
                PastMode.Reset();
                ReseedForFloor(gm.CurrentRunSeed, index);
                CurrentIndex = index;
                var w = new WireWriter(64);
                w.U8(MsgLoadLevel);
                w.I32(gm.CurrentRunSeed);
                w.I32(index);
                w.Str(gm.InjectedLevelName ?? "");
                // Returning to the Breach and quick restart clear players, pause state and run data before loading.
                // A bare floor load on the guest leaves its old dead players and game-over pause behind.
                w.U8(resetKind);
                if (resetKind == 2) { w.Bool(resetUnload); w.Bool(resetMenu); }
                if (resetKind == 3)
                {
                    w.Bool(resetOptions.GunGame); w.I32((int)resetOptions.ChallengeMode);
                    w.Bool(resetOptions.BossRush); w.I32(resetOptions.NumMetas);
                }
                Replication.WorldSync.Instance?.OnFloorUnloading();
                // Ahead of the load order on the same ordered channel: the save state this floor is built from.
                GenContext.CaptureAndSend(gm.CurrentRunSeed, index);
                NetSession.Instance.Node.Broadcast((byte)Channel.Control, w.ToArray(), Delivery.ReliableOrdered);
                NetSession.Instance.FlushNow();
                Plugin.Log.LogInfo($"RunSync: host loading level index {GM.Field("nextLevelIndex").GetValue<int>()}");
                return true;
            }
            if (clientAllowed)
            {
                clientAllowed = false;
                ReseedForFloor(gm.CurrentRunSeed, GM.Field("nextLevelIndex").GetValue<int>());
                return true;
            }
            Plugin.Log.LogInfo("RunSync: client blocked its own level load (waiting for host)");
            return false;
        }

        /// <summary>
        /// Floor generation reseeded per floor, so floor N depends only on (run seed, N). A player who restarts the
        /// game mid-run and rejoins builds exactly the floor the host is on.
        /// </summary>
        static void ReseedForFloor(int seed, int index)
        {
            unchecked
            {
                int floorSeed = seed * 31 + index * 7919 + 17;
                BraveRandom.InitializeWithSeed(floorSeed);
                Random.InitState(floorSeed);
            }
        }

        /// <summary>Host: a player came back mid-run. Bring them to this floor (or just re-sync if they never left it).</summary>
        public void SendRejoin(Connection c)
        {
            if (!NetSession.IsHost || CurrentIndex < 0 || SceneObjects.InBreach) return;
            var gm = GameManager.Instance;
            var w = new WireWriter(96);
            w.U8(MsgRejoin);
            w.I32(gm.CurrentRunSeed);
            w.I32(CurrentIndex);
            w.Str(currentCustom != null ? CustomTag + currentCustom : gm.InjectedLevelName ?? "");
            var lobby = FindObjectOfType<Lobby>();
            w.Str(lobby != null && lobby.Roster.TryGetValue(c.Slot, out var e) ? e.Prefab : "");
            GenContext.SendTo(c);
            c.Send((byte)Channel.Control, w.ToArray(), Delivery.ReliableOrdered);
            Plugin.Log.LogInfo($"RunSync: sent rejoin (floor index {CurrentIndex}) to slot {c.Slot}");
        }

        void OnControl(Connection c, byte[] data, Delivery d)
        {
            var r = new WireReader(data, 0, data.Length);
            byte type = r.U8();
            if (type == MsgMods) { CompareMods(c, r.Str()); return; }
            if (type == MsgPastBoss)
            {
                // The Past's owner beat its boss. The host passes it on to everyone else.
                if (NetSession.IsHost)
                    foreach (var other in NetSession.Instance.Node.Connections)
                        if (other != c) other.Send((byte)Channel.Control, data, Delivery.ReliableOrdered);
                if (r.Remaining > 0 && r.U8() == 1) PastMode.ApplyEnding(); else PastMode.ApplyBossDown();
                return;
            }
            if (type == MsgCustomRequest)
            {
                string want = r.Str();
                if (NetSession.IsHost && !GameManager.Instance.IsLoadingLevel && !SceneObjects.InBreach)
                {
                    Plugin.Log.LogInfo($"RunSync: {GungeonUI.WhoName(c)} found the way to {want}, taking everyone");
                    Toasts.Show(GungeonUI.WhoName(c) + " found a secret floor", 4f);
                    GameManager.Instance.DelayedLoadCustomLevel(0.5f, want);
                }
                return;
            }
            if (NetSession.IsHost) return;
            if (type == GenContext.Msg) { GenContext.Receive(ref r); return; }
            if (type == MsgLoadLevel || type == MsgRejoin) Session.Lobby.HostInBreach = 0; // the host is in a run now
            if (type == MsgClone)
            {
                var owner = PlayerSlots.ForSlot(r.U8());
                if (owner && !CloneInProgress)
                {
                    CloneInProgress = true;
                    applyingClone = true;
                    try { AccessTools.Method(typeof(PlayerController), "DoCloneEffect").Invoke(owner, null); }
                    finally { applyingClone = false; }
                }
                return;
            }
            if (type == MsgRejoin) { OnRejoin(ref r); return; }
            if (type == MsgLayout) { hostLayoutIndex = r.I32(); hostLayoutSeed = r.I32(); hostLayout = r.Str(); CheckLayout(); return; }
            if (type != MsgLoadLevel) return;
            int seed = r.I32();
            int index = r.I32();
            string injected = r.Str();
            byte reset = r.U8();
            if (reset != 0)
            {
                resetUnload = reset == 2 && r.Bool();
                resetMenu = reset == 2 && r.Bool();
                if (reset == 3)
                    resetOptions = new QuickRestartOptions { GunGame = r.Bool(), ChallengeMode = (ChallengeModeType)r.I32(), BossRush = r.Bool(), NumMetas = r.I32() };
                var gm = GameManager.Instance;
                rejoining = hasPending = false;
                gm.CurrentRunSeed = seed;
                CurrentIndex = index;
                Replication.WorldSync.Instance?.OnFloorUnloading();
                clientAllowed = applyingReset = true;
                try
                {
                    Plugin.Log.LogInfo("RunSync: following host run reset " + reset);
                    if (reset == 1) gm.ReturnToFoyer();
                    else if (reset == 2) gm.LoadCharacterSelect(resetUnload, resetMenu);
                    else gm.QuickRestart(resetOptions);
                }
                finally { applyingReset = false; clientAllowed = false; }
                return;
            }
            // Still finding our way back after a restart (the game may not even be ready to load yet): go to this
            // floor when the rejoin gets there, not now. The host moved on while we were away.
            if (rejoining)
            {
                pendingSeed = seed; pendingIndex = index; pendingInjected = injected; hasPending = true;
                Plugin.Log.LogInfo($"RunSync: host moved to floor index {index} while we rejoin, going there instead");
                return;
            }
            // Busy with a load of our own (walking through the character select door as the host starts the run,
            // say): loading the floor now breaks both loads. Go there as soon as this game has a character again.
            if (Busy())
            {
                pendingSeed = seed; pendingIndex = index; pendingInjected = injected; hasPending = true;
                rejoining = true;
                Plugin.Log.LogInfo($"RunSync: host started floor index {index} while we were loading, following when ready");
                StartCoroutine(LoadWhenFree());
                return;
            }
            LoadFloor(seed, index, injected);
        }

        static bool Busy()
        {
            var gm = GameManager.Instance;
            return gm.IsLoadingLevel || Dungeon.IsGenerating || gm.PrimaryPlayer == null || gm.IsSelectingCharacter;
        }

        System.Collections.IEnumerator LoadWhenFree()
        {
            float freeSince = -1f;
            while (NetSession.Active)
            {
                // Free for half a second in a row: the Breach has settled after the character change.
                if (Busy()) freeSince = -1f;
                else if (freeSince < 0f) freeSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - freeSince > 0.5f) break;
                yield return null;
            }
            if (!NetSession.Active || !hasPending) { rejoining = hasPending = false; yield break; }
            hasPending = false;
            LoadFloor(pendingSeed, pendingIndex, pendingInjected);
        }

        static bool hasPending;
        static int pendingSeed, pendingIndex;
        static string pendingInjected;

        void OnRejoin(ref WireReader r)
        {
            int seed = r.I32();
            int index = r.I32();
            string injected = r.Str();
            string prefab = r.Str();
            var gm = GameManager.Instance;
            var ws = Replication.WorldSync.Instance;
            // Already on our way there (a load can take longer than the host's resend interval).
            if (rejoining)
            {
                pendingSeed = seed; pendingIndex = index; pendingInjected = injected; hasPending = true;
                return;
            }
            if (gm.IsLoadingLevel || Dungeon.IsGenerating) return;
            if (!SceneObjects.InBreach && gm.CurrentRunSeed == seed && CurrentIndex == index && ws != null && gm.Dungeon != null)
            {
                // Already on this floor (a short drop, or we loaded it but never got to say so): never build it a
                // second time, that makes a different floor. Just tell the host we are here.
                Plugin.Log.LogInfo("RunSync: rejoined on the same floor, refreshing");
                if (ws.FloorReady) ws.ResendReady(); else ws.OnFloorLoaded();
                return;
            }
            Plugin.Log.LogInfo($"RunSync: rejoining the run on floor index {index}");
            Toasts.Show("Rejoining your run...", 5f);
            rejoining = true;
            hasPending = false;
            StartCoroutine(RejoinFlow(seed, index, injected, prefab));
        }

        static bool rejoining;

        System.Collections.IEnumerator RejoinFlow(int seed, int index, string injected, string prefab)
        {
            // Back in the Breach after a restart: become the same character, then go straight to the host's floor.
            if (GameManager.Instance.PrimaryPlayer == null && !string.IsNullOrEmpty(prefab))
                yield return StartCoroutine(CharacterPicker.Pick(prefab));
            float giveUp = Time.realtimeSinceStartup + 20f;
            while (GameManager.Instance.PrimaryPlayer == null && Time.realtimeSinceStartup < giveUp) yield return null;
            if (hasPending) { seed = pendingSeed; index = pendingIndex; injected = pendingInjected; hasPending = false; }
            LoadFloor(seed, index, injected);
        }

        void LoadFloor(int seed, int index, string injected)
        {
            var gm = GameManager.Instance;
            if (injected != null && injected.StartsWith(CustomTag))
            {
                string custom = ApplyPastFlags(injected.Substring(CustomTag.Length));
                if (gm.CurrentRunSeed != seed) gm.CurrentRunSeed = seed;
                CurrentIndex = index;
                ReseedForFloor(seed, index);
                GM.Field("m_loadingLevel").SetValue(false);
                Plugin.Log.LogInfo($"RunSync: host says load secret floor {custom}");
                clientAllowed = true;
                Replication.WorldSync.Instance?.OnFloorUnloading();
                if (Pixelator.Instance) Pixelator.Instance.FadeToBlack(0.2f);
                gm.LoadCustomLevel(custom);
                return;
            }
            if (gm.CurrentRunSeed != seed) gm.CurrentRunSeed = seed;
            PastMode.Reset();
            ReseedForFloor(seed, index);
            CurrentIndex = index;
            GM.Field("nextLevelIndex").SetValue(index);
            gm.InjectedLevelName = injected;
            GM.Field("m_loadingLevel").SetValue(false);
            Plugin.Log.LogInfo($"RunSync: host says load index {index} seed {seed}");
            clientAllowed = true;
            Replication.WorldSync.Instance?.OnFloorUnloading();
            bool fromFoyer = Game.SceneObjects.InBreach;
            if (Pixelator.Instance) Pixelator.Instance.FadeToBlack(0.2f);
            gm.LoadNextLevel();
            if (fromFoyer && Game.SceneObjects.Breach) Game.SceneObjects.Breach.OnDepartedFoyer();
        }

        readonly System.Collections.Generic.Dictionary<Connection, float> lastRejoin = new System.Collections.Generic.Dictionary<Connection, float>();

        /// <summary>
        /// Host: anyone connected but not ready on this floor (joined while we were loading, came back after a
        /// crash, missed the load message) is sent the rejoin again until they are on the floor with us.
        /// </summary>
        Dungeon stragglerFloor;
        float stragglerFloorAt;

        readonly System.Collections.Generic.HashSet<Connection> rejoinWhenReady = new System.Collections.Generic.HashSet<Connection>();

        /// <summary>Host: someone joined while we were changing floors; send them the rejoin once the floor is ready.</summary>
        public void RejoinWhenReady(Connection c) => rejoinWhenReady.Add(c);

        void BringStragglers()
        {
            if (!NetSession.Active || !NetSession.IsHost || CurrentIndex < 0 || SceneObjects.InBreach) return;
            if (GameManager.Instance.IsLoadingLevel || GameManager.Instance.Dungeon == null || Dungeon.IsGenerating) return;
            var ws = Replication.WorldSync.Instance;
            if (ws == null) return;
            float now = Time.realtimeSinceStartup;
            if (rejoinWhenReady.Count > 0)
            {
                foreach (var c in rejoinWhenReady)
                    if (NetSession.Instance.Node.Connections.Contains(c) && !ws.IsReady(c)) { lastRejoin[c] = now; SendRejoin(c); }
                rejoinWhenReady.Clear();
            }
            // A friend on a slow PC can take a while to load the same floor: give them 40 s before re-sending it.
            if (GameManager.Instance.Dungeon != stragglerFloor) { stragglerFloor = GameManager.Instance.Dungeon; stragglerFloorAt = now; }
            if (now - stragglerFloorAt < 40f) return;
            foreach (var c in NetSession.Instance.Node.Connections)
            {
                if (ws.IsReady(c)) continue;
                if (lastRejoin.TryGetValue(c, out var t) && now - t < 40f) continue;
                lastRejoin[c] = now;
                SendRejoin(c);
            }
        }

        void Update()
        {
            BringStragglers();
            // Every floor is a new Dungeon object, but all floors load into the same scene, so key on the dungeon.
            if (!GameManager.HasInstance || GameManager.Instance.Dungeon == null || Dungeon.IsGenerating || GameManager.Instance.IsLoadingLevel) return;
            if (GameManager.Instance.Dungeon.data == null || GameManager.Instance.Dungeon.data.rooms == null) return;
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + "#" + GameManager.Instance.Dungeon.GetInstanceID();
            if (scene == lastScene) return;
            lastScene = scene;
            if (CloneInProgress) StartCoroutine(FinishClone());
            LastFingerprint = Fingerprint();
            fingerprintIndex = SceneObjects.InBreach ? -1 : CurrentIndex; // which floor that fingerprint is of
            // Not ready in the Breach while on our way back into a run: the host would send us its floor's world.
            bool following = rejoining && !SceneObjects.InBreach && hasPending && (pendingIndex != CurrentIndex || pendingSeed != GameManager.Instance.CurrentRunSeed);
            if (NetSession.Active && !NetSession.IsHost && !(rejoining && SceneObjects.InBreach) && !following) Replication.WorldSync.Instance?.OnFloorLoaded();
            if (!SceneObjects.InBreach && rejoining)
            {
                rejoining = false;
                // The host changed floors while our rejoin load was already under way: follow it now.
                if (hasPending && (pendingIndex != CurrentIndex || pendingSeed != GameManager.Instance.CurrentRunSeed))
                {
                    // Not from inside this floor's own arrival: a load started then never finished arriving (no
                    // world, input locked). Wait until this floor has settled, then go.
                    rejoining = true;
                    Plugin.Log.LogInfo($"RunSync: rejoin landed on floor index {CurrentIndex}, the host is on {pendingIndex}: following");
                    StartCoroutine(LoadWhenFree());
                    return;
                }
                hasPending = false;
            }
            if (NetSession.Active && NetSession.IsHost && !SceneObjects.InBreach) RunBackup.OnHostFloorReady();
            if (SceneObjects.InBreach) CurrentIndex = -1;
            Plugin.Log.LogInfo("FLOOR " + scene + " " + LastFingerprint);
            if (NetSession.Active && NetSession.IsHost && !SceneObjects.InBreach) SendLayout(null);
            if (NetSession.Active && !NetSession.IsHost) CheckLayout();
            var dir = System.Environment.GetEnvironmentVariable("GO_RUN_DIR");
            if (dir != null)
            {
                var lines = new System.Collections.Generic.List<string>();
                foreach (var e in StaticReferenceManager.AllEnemies)
                    if (e) lines.Add($"{e.EnemyGuid} {e.name} {e.transform.position.x:F2},{e.transform.position.y:F2} room={(e.ParentRoom != null ? e.ParentRoom.GetRoomName() : "-")}");
                lines.Sort();
                System.IO.File.WriteAllLines(System.IO.Path.Combine(dir, "enemies-" + scene + ".txt"), lines.ToArray());
            }
        }

        // ---------- Same floor on every machine? ----------

        System.Collections.IEnumerator FinishClone()
        {
            var floor = GameManager.Instance.Dungeon;
            // The last death and the Clone load overlap. Its co-op death coroutine may turn that player into
            // a ghost after Clone's first revive pass, especially on the last of three or four machines.
            float end = Time.realtimeSinceStartup + 2f;
            while (Time.realtimeSinceStartup < end) yield return null;
            if (!NetSession.Active || GameManager.Instance.Dungeon != floor) yield break;
            foreach (var p in NPlayers.Players.All)
            {
                if (p.IsGhost)
                {
                    p.gameObject.SetActive(true);
                    p.specRigidbody.enabled = true;
                    p.ResurrectFromChest(p.CenterPosition);
                }
                p.healthHaver.FullHeal();
                if (p.characterIdentity == PlayableCharacters.Robot) p.healthHaver.Armor = 6f;
            }
            Replication.PickupSync.WithScope(() => Chest.ToggleCoopChests(false));
            CloneInProgress = false;
            Plugin.Log.LogInfo("RunSync: Clone completed, everyone revived");
        }
        // Every machine builds the floor itself from the host's seed. Another mod that changes floors (new rooms,
        // new enemies) makes each machine build a different one: rooms, walls and enemies stop matching. The host
        // sends its layout; a guest whose own differs is told why, once per floor.

        static string hostLayout;
        static int hostLayoutIndex = -2, hostLayoutSeed, warnedIndex = -2, fingerprintIndex = -1;

        static string LayoutPart(string fp)
        {
            int i = fp.IndexOf("rooms="), j = fp.IndexOf(" enemies=");
            return i >= 0 && j > i ? fp.Substring(i, j - i) : fp;
        }

        /// <summary>Host: our layout to everyone (or one newcomer).</summary>
        public void SendLayout(Connection to)
        {
            // Only a fingerprint of the floor we are on now (mid-load, the last one is still the previous floor's).
            if (!NetSession.Active || !NetSession.IsHost || fingerprintIndex < 0 || fingerprintIndex != CurrentIndex || LastFingerprint == "-") return;
            if (GameManager.Instance.IsLoadingLevel || Dungeon.IsGenerating) return;
            var w = new WireWriter(96);
            w.U8(MsgLayout); w.I32(fingerprintIndex); w.I32(GameManager.Instance.CurrentRunSeed); w.Str(LayoutPart(LastFingerprint));
            if (to != null) to.Send((byte)Channel.Control, w.ToArray(), Delivery.ReliableOrdered);
            else NetSession.Instance.Node.Broadcast((byte)Channel.Control, w.ToArray(), Delivery.ReliableOrdered);
        }

        void CheckLayout()
        {
            if (hostLayout == null || fingerprintIndex < 0 || hostLayoutIndex != fingerprintIndex || fingerprintIndex != CurrentIndex || SceneObjects.InBreach || LastFingerprint == "-") return;
            if (hostLayoutSeed != GameManager.Instance.CurrentRunSeed) return;
            if (GameManager.Instance.IsLoadingLevel || Dungeon.IsGenerating) return;
            var mine = LayoutPart(LastFingerprint);
            if (System.Environment.GetEnvironmentVariable("GO_LAYOUT_TEST") == "1") mine += "-test"; // tests: a floor that differs
            if (mine == hostLayout || warnedIndex == CurrentIndex) return;
            warnedIndex = CurrentIndex;
            var others = OtherMods();
            Plugin.Log.LogWarning($"FLOOR MISMATCH: host {hostLayout}, mine {mine}; other mods: {(others.Length > 0 ? others : "none")}");
            string message = others.Length > 0
                ? "This floor is different from the host's, so rooms and enemies won't match. Other mods can change floors: " + others + ". Everyone needs the same mods, or play without them."
                : "This floor is different from the host's, so rooms and enemies won't match. Rejoin the host to get back in sync.";
            // Continuing puts the guest in walls and lets them take damage from enemies they cannot see.
            // Stop the incompatible run and return safely rather than trying to replicate onto another layout.
            NetSession.Instance.Leave("incompatible floor layout");
            GameManager.Instance.ForceUnpause();
            GameManager.Instance.DelayedLoadCharacterSelect(0.5f, true, false);
            Toasts.Show(message + " Returned to the Breach safely.", 18f, true);
        }

        /// <summary>Other BepInEx plugins loaded next to this one, as "name version" entries.</summary>
        public static string ModList()
        {
            var names = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                    if (kv.Key != Plugin.Guid && kv.Value != null && kv.Value.Metadata != null)
                        names.Add(kv.Value.Metadata.Name + " " + kv.Value.Metadata.Version);
            }
            catch (System.Exception) { }
            names.Sort(System.StringComparer.Ordinal);
            return string.Join(";", names.ToArray());
        }

        public static string LastModWarning = "";

        static void CompareMods(Connection c, string theirs)
        {
            var mine = new System.Collections.Generic.HashSet<string>(ModList().Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries));
            var other = new System.Collections.Generic.HashSet<string>(theirs.Split(new[] { ';' }, System.StringSplitOptions.RemoveEmptyEntries));
            var onlyMine = new System.Collections.Generic.List<string>(); var onlyTheirs = new System.Collections.Generic.List<string>();
            foreach (var m in mine) if (!other.Contains(m)) onlyMine.Add(m);
            foreach (var m in other) if (!mine.Contains(m)) onlyTheirs.Add(m);
            if (onlyMine.Count == 0 && onlyTheirs.Count == 0) { Plugin.Log.LogInfo("Mods match " + GungeonUI.WhoName(c) + ": " + (mine.Count > 0 ? string.Join(", ", new System.Collections.Generic.List<string>(mine).ToArray()) : "none")); return; }
            string who = GungeonUI.WhoName(c);
            string msg = "Your mods are different from " + who + "'s, so floors may not match.";
            if (onlyMine.Count > 0) msg += " Only you have: " + string.Join(", ", onlyMine.ToArray()) + ".";
            if (onlyTheirs.Count > 0) msg += " Only " + who + " has: " + string.Join(", ", onlyTheirs.ToArray()) + ".";
            msg += " Everyone needs the same mods and versions.";
            LastModWarning = msg;
            Plugin.Log.LogWarning("MODS DIFFER: " + msg);
            Toasts.Show(msg, 15f, true);
        }

        /// <summary>Other BepInEx plugins loaded next to this one, by name.</summary>
        public static string OtherMods()
        {
            var names = new System.Collections.Generic.List<string>();
            try
            {
                foreach (var kv in BepInEx.Bootstrap.Chainloader.PluginInfos)
                    if (kv.Key != Plugin.Guid && kv.Value != null && kv.Value.Metadata != null) names.Add(kv.Value.Metadata.Name);
            }
            catch (System.Exception) { }
            return string.Join(", ", names.ToArray());
        }

        /// <summary>Hashes of the floor layout, enemies, chests and pickups, with counts.</summary>
        public static string Fingerprint()
        {
            var d = GameManager.Instance.Dungeon;
            var layout = new StringBuilder();
            foreach (var room in d.data.rooms)
                layout.Append(room.area.basePosition).Append(room.area.dimensions).Append(room.GetRoomName()).Append('|');
            var enemies = new StringBuilder();
            int ne = 0;
            foreach (var e in StaticReferenceManager.AllEnemies)
            {
                if (!e) continue;
                ne++;
                var p = e.transform.position;
                enemies.Append(e.EnemyGuid).Append(Mathf.RoundToInt(p.x * 4)).Append(',').Append(Mathf.RoundToInt(p.y * 4)).Append('|');
            }
            var chests = new StringBuilder();
            int nc = 0;
            foreach (var ch in StaticReferenceManager.AllChests)
            {
                if (!ch) continue;
                nc++;
                chests.Append(ch.name).Append(ch.transform.position).Append('|');
            }
            int np = 0;
            var picks = new StringBuilder();
            foreach (var po in Object.FindObjectsOfType<PickupObject>())
            {
                if (!po || po.transform.parent != null) continue;
                np++;
                picks.Append(po.PickupObjectId).Append(po.transform.position).Append('|');
            }
            return $"seed={GameManager.Instance.CurrentRunSeed} rooms={d.data.rooms.Count} layout={H(layout)} enemies={ne}:{H(enemies)} chests={nc}:{H(chests)} pickups={np}:{H(picks)}";
        }

        static string H(StringBuilder sb)
        {
            uint h = 2166136261;
            var s = sb.ToString();
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return h.ToString("X8");
        }
    }
}
