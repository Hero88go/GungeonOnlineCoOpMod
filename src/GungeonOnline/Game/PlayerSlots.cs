using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// The one place where network slots meet the game's player numbers.
    /// On every machine the local player is the game's player 0 (PrimaryPlayer), so each screen gets its own
    /// HUD on the left, its own audio listener and its own camera. Remote players are added the way vanilla
    /// co-op adds player 2, with any character. The game supports one extra player today; the slot map is N-ready.
    /// </summary>
    internal static class PlayerSlots
    {
        /// <summary>slot -> spawned remote player (local slot is not in here).</summary>
        static readonly Dictionary<int, PlayerController> remotes = new Dictionary<int, PlayerController>();
        /// <summary>slot -> prefab path the remote player chose.</summary>
        static readonly Dictionary<int, string> remoteChoice = new Dictionary<int, string>();

        public static int LocalSlot => Session.NetSession.Active ? Session.NetSession.LocalSlot : 0;

        /// <summary>This machine's own player. With none (menus, character select), the game searches every object each
        /// time it is asked; a burst of network messages that each asked froze the game. One search a frame at most.</summary>
        public static PlayerController Local
        {
            get
            {
                if (!GameManager.HasInstance) return null;
                if (noPlayerFrame == Time.frameCount && !gmPlayer(GameManager.Instance)) return null;
                var p = GameManager.Instance.PrimaryPlayer;
                if (!p) noPlayerFrame = Time.frameCount;
                return p;
            }
        }
        static int noPlayerFrame = -1;
        static readonly HarmonyLib.AccessTools.FieldRef<GameManager, PlayerController> gmPlayer = HarmonyLib.AccessTools.FieldRefAccess<GameManager, PlayerController>("m_player");

        /// <summary>
        /// The game's player id for a slot on this machine. The local player is always id 0 (own HUD, own camera),
        /// so the host's slot and the local slot trade places; every other slot keeps its number. Ids stay unique
        /// and contiguous, which the game's input code needs.
        /// </summary>
        public static int IdForSlot(int slot) => slot == LocalSlot ? 0 : (slot == 0 ? LocalSlot : slot);

        public static int SlotForId(int id) => id == 0 ? LocalSlot : (id == LocalSlot ? 0 : id);

        public static IEnumerable<KeyValuePair<int, PlayerController>> Remotes => remotes;

        /// <summary>Player for a network slot on this machine, or null.</summary>
        public static PlayerController ForSlot(int slot)
        {
            if (slot == LocalSlot) return Local;
            return remotes.TryGetValue(slot, out var p) && p ? p : null;
        }

        /// <summary>The game's player id for a slot here (0 for local, 1.. for remotes).</summary>
        public static int PlayerIdFor(int slot)
        {
            var p = ForSlot(slot);
            return p ? p.PlayerIDX : -1;
        }

        public static int SlotOf(PlayerController p)
        {
            if (p == Local) return LocalSlot;
            foreach (var kv in remotes) if (kv.Value == p) return kv.Key;
            return -1;
        }

        public static bool IsRemote(PlayerController p) => p != null && p != Local && SlotOf(p) >= 0;

        static System.Collections.IEnumerator DespawnAfterLoad(int slot, PlayerController p)
        {
            while (GameManager.HasInstance && GameManager.Instance.IsLoadingLevel) yield return null;
            yield return null;
            // Still the same character in that slot (nobody took it back in the meantime): remove it now.
            if (remotes.TryGetValue(slot, out var now) && now == p) Despawn(slot);
        }

        /// <summary>Player two as <paramref name="prefab"/> at <paramref name="at"/>, set up the way the game's own co-op join does it.
        /// Used by online play and by couch co-op.</summary>
        public static PlayerController CreateSecondary(GameObject prefab, Vector3 at)
        {
            var gm = GameManager.Instance;
            gm.ClearSecondaryPlayer();
            GameManager.LastUsedCoopPlayerPrefab = prefab;
            var go = Object.Instantiate(prefab, at, Quaternion.identity);
            go.SetActive(true);
            var p = go.GetComponent<PlayerController>();
            p.PlayerIDX = 1;
            gm.SecondaryPlayer = p;
            return p;
        }

        /// <summary>
        /// A remote player as <paramref name="prefab"/> with the game's player id <paramref name="id"/> (1 and up).
        /// Player 1 is the game's "second player"; with more players the lowest live id holds that role, so the
        /// game's "other player" lookups always find someone.
        /// </summary>
        static PlayerController CreateRemote(GameObject prefab, Vector3 at, int id)
        {
            var gm = GameManager.Instance;
            // The game walks its input instances 0..Count-1: every lower id must exist before this one.
            for (int k = 1; k <= id; k++) BraveInput.GetInstanceForPlayer(k);
            GameManager.LastUsedCoopPlayerPrefab = prefab;
            var go = Object.Instantiate(prefab, at, Quaternion.identity);
            go.SetActive(true);
            var p = go.GetComponent<PlayerController>();
            p.PlayerIDX = id;
            p.ActorName = "Player ID " + id;
            if (id == 1 || gm.SecondaryPlayer == null) gm.SecondaryPlayer = p;
            return p;
        }

        /// <summary>Before a player's floor arrival: the game's "other player" must be a live copy. After a character
        /// swap it could still point at the old, destroyed one, and the arrival then stopped half way (no new floor
        /// blanks, no reload, no item floor effects) on every later floor.</summary>
        public static void EnsureSecondary()
        {
            if (!Session.NetSession.Active || !GameManager.HasInstance || remotes.Count == 0) return;
            var gm = GameManager.Instance;
            bool anyLive = false;
            foreach (var kv in remotes) if (kv.Value) { anyLive = true; break; }
            if (!anyLive) return;
            var second = gm.SecondaryPlayer;
            if (gm.CurrentGameType == GameManager.GameType.COOP_2_PLAYER && second) return;
            if (gm.CurrentGameType != GameManager.GameType.COOP_2_PLAYER) gm.CurrentGameType = GameManager.GameType.COOP_2_PLAYER;
            RepointSecondary();
            Plugin.Log.LogInfo("PlayerSlots: second player pointed back at the live copy before the floor arrival");
        }

        /// <summary>The game's second player is the remote with the lowest id that is still here.</summary>
        static void RepointSecondary()
        {
            var gm = GameManager.Instance;
            PlayerController best = null;
            foreach (var kv in remotes) if (kv.Value && (best == null || kv.Value.PlayerIDX < best.PlayerIDX)) best = kv.Value;
            if (best != null && gm.SecondaryPlayer != best) gm.SecondaryPlayer = best;
            gm.RefreshAllPlayers();
        }

        /// <summary>Enemy health follows the player count (the game's own co-op uses one value for two).</summary>
        public static void ApplyEnemyHealth()
        {
            if (!GameManager.HasInstance) return;
            GameManager.Instance.COOP_ENEMY_HEALTH_MULTIPLIER = Session.NetConfig.EnemyHealthFor(1 + remotes.Count);
        }

        /// <summary>Spawn (or re-spawn with a new character) the remote player for <paramref name="slot"/>.</summary>
        static readonly bool oldSwap = System.Environment.GetEnvironmentVariable("GO_OLD_SWAP") == "1"; // tests: the 1.1.1 swap, before/after

        public static PlayerController SpawnRemote(int slot, string prefabPath, bool alt = false)
        {
            string choice = prefabPath + (alt ? "|alt" : ""); // a costume change is a new look, like a new character
            var gm = GameManager.Instance;
            var local = Local;
            if (local == null) return null; // spawn once the local player exists
            Vector3? keepAt = null;
            if (remotes.TryGetValue(slot, out var existing) && existing)
            {
                if (remoteChoice.TryGetValue(slot, out var prev) && prev == choice) return existing;
                // A character swap that arrives while a floor loads: the old copy can only go once the load is over,
                // and making the new one now would leave the old one behind for good. Swap when the load is done.
                if (!oldSwap && gm.IsLoadingLevel && Plugin.Instance != null)
                {
                    bool waiting = swapAfterLoad.ContainsKey(slot);
                    swapAfterLoad[slot] = choice;
                    if (!waiting) Plugin.Instance.StartCoroutine(SwapAfterLoad(slot));
                    return existing;
                }
                if (!oldSwap) keepAt = existing.transform.position; // a character swap: the new one stands where they are
                Despawn(slot);
            }
            var prefab = (GameObject)BraveResources.Load(prefabPath);
            if (prefab == null) { Plugin.Log.LogError("Unknown character prefab " + prefabPath); return null; }

            gm.CurrentGameType = GameManager.GameType.COOP_2_PLAYER;
            local.ReinitializeMovementRestrictors();
            int id = IdForSlot(slot);
            var p = CreateRemote(prefab, keepAt ?? local.transform.position + new Vector3(1.5f * id, 0f, 0f), id);
            if (!oldSwap) Replication.PlayerSync.Instance?.Forget(slot);
            remotes[slot] = p;
            remoteChoice[slot] = choice;
            // The alternate costume (picked at the character's second Breach statue) looks different: wear it here too.
            if (alt && !p.IsUsingAlternateCostume) p.SwapToAlternateCostume();
            RepointSecondary();
            ApplyEnemyHealth();
            Plugin.Instance.StartCoroutine(FinishSpawn(p));
            Plugin.Log.LogInfo($"PlayerSlots: slot {slot} spawned as {prefabPath}{(alt ? " (alternate costume)" : "")} (player id {p.PlayerIDX})");
            return p;
        }

        static readonly Dictionary<int, string> swapAfterLoad = new Dictionary<int, string>();

        static System.Collections.IEnumerator SwapAfterLoad(int slot)
        {
            while (GameManager.HasInstance && GameManager.Instance.IsLoadingLevel) yield return null;
            yield return null;
            if (!swapAfterLoad.TryGetValue(slot, out var path)) yield break;
            swapAfterLoad.Remove(slot);
            // Still that player (they did not leave meanwhile): now the old copy can go and the new one comes in.
            if (remotes.ContainsKey(slot)) { int bar = path.IndexOf('|'); SpawnRemote(slot, bar < 0 ? path : path.Substring(0, bar), bar >= 0); }
        }

        static System.Collections.IEnumerator FinishSpawn(PlayerController p)
        {
            yield return null;
            if (!p) yield break;
            // A scene change can land between the spawn and this frame; skip whatever is gone.
            GameManager.Instance.RefreshAllPlayers();
            if (GameUIRoot.Instance) GameUIRoot.Instance.ConvertCoreUIToCoopMode();
            if (PhysicsEngine.Instance && p.specRigidbody) PhysicsEngine.Instance.RegisterOverlappingGhostCollisionExceptions(p.specRigidbody);
            if (GameManager.Instance.MainCameraController) GameManager.Instance.MainCameraController.ClearPlayerCache();
            if (Game.SceneObjects.InBreach && Game.SceneObjects.Breach) Game.SceneObjects.Breach.ProcessPlayerEnteredFoyer(p);
            // Vanilla co-op restores input when the join conversation ends; there is no conversation here.
            p.CurrentInputState = PlayerInputState.AllInput;
            RefreshFoyerForCoopChange();
            Camera.IndependentCamera.Apply();
        }

        public static void Despawn(int slot)
        {
            if (!remotes.TryGetValue(slot, out var p)) return;
            // Never in the middle of a floor load: the game's load code still uses every player.
            if (GameManager.HasInstance && GameManager.Instance.IsLoadingLevel && Plugin.Instance != null)
            {
                Plugin.Instance.StartCoroutine(DespawnAfterLoad(slot, p));
                return;
            }
            remotes.Remove(slot);
            remoteChoice.Remove(slot);
            Input.InputPump.Unroute(p ? p.PlayerIDX : IdForSlot(slot));
            if (p)
            {
                p.SetInputOverride("gungeon online leaving");
                Object.Destroy(p.gameObject);
            }
            if (remotes.Count == 0 && GameManager.HasInstance)
            {
                GameManager.Instance.SecondaryPlayer = null;
                GameManager.Instance.CurrentGameType = GameManager.GameType.SINGLE_PLAYER;
                if (Local) Local.ReinitializeMovementRestrictors();
            }
            else if (GameManager.HasInstance) Plugin.Instance.StartCoroutine(RepointNextFrame());
            ApplyEnemyHealth();
            NPlayers.HudPatches.Sync();
            RefreshFoyerForCoopChange();
            Plugin.Log.LogInfo($"PlayerSlots: slot {slot} removed");
        }

        /// <summary>The destroyed player is gone from the scene one frame later; only then is the list rebuilt.</summary>
        static System.Collections.IEnumerator RepointNextFrame()
        {
            yield return null;
            if (GameManager.HasInstance) RepointSecondary();
        }

        /// <summary>
        /// Breach objects that depend on co-op mode (the tutorial blocker) are only updated by vanilla when a
        /// character changes. Re-run that rule so host and client Breaches have the same walls.
        /// </summary>
        static void RefreshFoyerForCoopChange()
        {
            if (!Game.SceneObjects.InBreach) return;
            try { AccessTools.Method(typeof(Foyer), "ToggleTutorialBlocker").Invoke(Foyer.Instance, new object[] { Local }); }
            catch (System.Exception e) { Plugin.Log.LogWarning("Foyer refresh: " + e.Message); }
        }

        public static void Clear()
        {
            swapAfterLoad.Clear();
            foreach (var s in new List<int>(remotes.Keys)) Despawn(s);
        }

        static readonly AccessTools.FieldRef<PlayerController, bool> ignoredByCamera =
            AccessTools.FieldRefAccess<PlayerController, bool>("<IgnoredByCamera>k__BackingField");

        public static void SetIgnoredByCamera(PlayerController p, bool v) => ignoredByCamera(p) = v;
    }
}
