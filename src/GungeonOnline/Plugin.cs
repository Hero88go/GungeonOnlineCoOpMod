using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace GungeonOnline
{
    [BepInPlugin(Guid, Name, Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "gungeononline";
        public const string Name = "Enter The Gungeon Online Co-Op Mod";
        public const string Version = "1.2.2";

        internal static ManualLogSource Log;
        internal static Plugin Instance;
        Harmony harmony;

        void Awake()
        {
            Instance = this;
            Log = Logger;
            var clockStart = Net.Clock.Now; // start the network clock on the game thread
            Session.NetConfig.Bind(Config);
            Dev.DevEnv.Load();
            harmony = new Harmony(Guid);
            Dev.SaveIsolation.Apply(harmony);
            Game.BackgroundRun.Install(harmony);
            Input.InputPump.Install(harmony);
            Camera.IndependentCamera.Install(harmony);
            Game.PartnerUI.Install(harmony);
            Game.RunSync.Install(harmony);
            TryInstall("GenContext", () => Game.GenContext.Install(harmony));
            TryInstall("PlayerPhysicsGuard", () => Game.PlayerPhysicsGuard.Install(harmony));
            Game.PauseScope.Install(harmony);
            Game.PresentationGate.Install(harmony);
            TryInstall("ConversationSync", () => Game.ConversationSync.Install(harmony));
            Game.SecondFightLock.Install(harmony);
            TryInstall("DoorGrace", () => Game.DoorGrace.Install(harmony));
            Game.CombatDoorEntry.Install(harmony);
            Replication.WorldSync.Install(harmony);
            Replication.ProjectileSync.Install(harmony);
            TryInstall("LookSync", () => Replication.LookSync.Install(harmony));
            Game.CouchCoop.Install(harmony);
            TryInstall("PlayerListPatches", () => Game.NPlayers.PlayerListPatches.Install(harmony));
            TryInstall("DeathRevivePatches", () => Game.NPlayers.DeathRevivePatches.Install(harmony));
            TryInstall("RoomPresencePatches", () => Game.NPlayers.RoomPresencePatches.Install(harmony));
            TryInstall("GatherPatches", () => Game.NPlayers.GatherPatches.Install(harmony));
            TryInstall("StatsPatches", () => Game.NPlayers.StatsPatches.Install(harmony));
            TryInstall("HudPatches", () => Game.NPlayers.HudPatches.Install(harmony));
            if (Dev.DevEnv.IsHarness)
            {
                // Tests never make a sound: silent from the first frame, and the game's audio wake-up does nothing.
                UnityEngine.AudioListener.volume = 0f;
                var wake = AccessTools.Method(typeof(AkSoundEngine), "WakeupFromSuspend");
                if (wake != null) harmony.Patch(wake, prefix: new HarmonyMethod(typeof(Plugin), nameof(NoWake)));
            }
            Replication.VitalsSync.Install(harmony);
            Replication.PickupSync.Install(harmony);
            Replication.ShopSync.Install(harmony);
            Replication.PropSync.Install(harmony);
            Session.PauseMenuEntry.Install(harmony);
            Game.BreachDoorGate.Install(harmony);
            Game.RemoteAim.Install(harmony);
            Replication.StatSync.Install(harmony);
            harmony.Patch(AccessTools.Method(typeof(GameManager), nameof(GameManager.DoGameOver)),
                prefix: new HarmonyMethod(typeof(Plugin), nameof(OnGameOver)));
            Dev.QuickStart.Prepare();
            Log.LogInfo($"{Name} {Version} loaded. game={Session.NetSession.GameHash} dev={Dev.DevEnv.Summary()}");
            if (Dev.DevEnv.IsHarness) TryInstall("NoMouseGrab", () => Dev.NoMouseGrab.Install(harmony));
            if (Dev.DevEnv.IsHarness) TryInstall("FreezeProbe", () => Dev.FreezeProbe.Install(harmony));
            gameObject.AddComponent<Session.NetSession>();
            gameObject.AddComponent<Session.Toasts>();
            gameObject.AddComponent<Session.Metrics>();
            gameObject.AddComponent<Game.PartnerUI>();
            gameObject.AddComponent<Game.MapPing>();
            gameObject.AddComponent<Game.InputWatchdog>();
            gameObject.AddComponent<Session.OnlineMenu>();
            gameObject.AddComponent<Session.RunBackup>();
            gameObject.AddComponent<Game.AwayGuard>();
            gameObject.AddComponent<Session.Lobby>();
            gameObject.AddComponent<Replication.InputSync>();
            gameObject.AddComponent<Replication.PlayerSync>();
            gameObject.AddComponent<Game.RunSync>();
            gameObject.AddComponent<Game.ConversationSync>();
            gameObject.AddComponent<Replication.WorldSync>();
            gameObject.AddComponent<Replication.ProjectileSync>();
            gameObject.AddComponent<Replication.LookSync>();
            gameObject.AddComponent<Replication.VitalsSync>();
            gameObject.AddComponent<Replication.PickupSync>();
            gameObject.AddComponent<Replication.InventorySync>();
            gameObject.AddComponent<Replication.ShopSync>();
            gameObject.AddComponent<Replication.Checksum>();
            gameObject.AddComponent<Replication.PropSync>();
            gameObject.AddComponent<Session.PeerLanes>();
            gameObject.AddComponent<Dev.DevRunner>();
            if (Dev.DevEnv.IsHarness) gameObject.AddComponent<Dev.ReleaseRegression>();
            StartCoroutine(Dev.QuickStart.Run());
            Dev.AutoSession.Begin();
            StartCoroutine(Welcome());
            StartCoroutine(Session.UpdateCheck.Run());
        }

        /// <summary>Run over (everyone dead): the saved run is finished and is not offered for resuming.</summary>
        static bool NoWake() => false;

        /// <summary>One bad patch must never take the whole mod down: log it and keep the rest.</summary>
        static void TryInstall(string what, System.Action install)
        {
            try { install(); }
            catch (System.Exception e) { Log.LogError(what + " patches failed to install: " + e.Message); }
        }

        static void OnGameOver()
        {
            Plugin.Log.LogInfo("Game over (everyone is down)");
            if (Session.NetSession.Active && Session.NetSession.IsHost) Session.RunBackup.MarkEnded();
        }

        System.Collections.IEnumerator Welcome()
        {
            // The new-user test (GO_FIRST_RUN=1) checks the welcome like a real first launch.
            bool firstRunTest = System.Environment.GetEnvironmentVariable("GO_FIRST_RUN") == "1";
            if ((Dev.DevEnv.IsHarness && !firstRunTest) || Session.NetConfig.WelcomeShown.Value) yield break;
            yield return new UnityEngine.WaitForSeconds(6f);
            Session.Toasts.Show("Enter The Gungeon Online Co-Op Mod is ready. Pause and pick Play Online, or hold Back + Start.", 9f);
            Session.NetConfig.WelcomeShown.Value = true;
        }
    }
}
