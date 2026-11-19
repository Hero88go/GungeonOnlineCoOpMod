using GungeonOnline.Session;
using HarmonyLib;
using HutongGames.PlayMaker.Actions;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Couch co-op (one PC, two controllers) with any character. Player two joins at the Cultist as usual, then talks
    /// to any Gungeoneer in the Breach to become them, the same way player one switches characters. Vanilla hides
    /// the other Gungeoneers while two people play; here they stay out (except the ones already being played).
    /// Online play is untouched: there each player picks in their own Breach.
    /// </summary>
    internal static class CouchCoop
    {
        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(FoyerCharacterSelectFlag), nameof(FoyerCharacterSelectFlag.OnCoopChangedCallback)),
                prefix: new HarmonyMethod(typeof(CouchCoop), nameof(CoopChangedPrefix)));
            h.Patch(AccessTools.Method(typeof(ChangeToNewCharacter), nameof(ChangeToNewCharacter.OnEnter)),
                prefix: new HarmonyMethod(typeof(CouchCoop), nameof(ChangeCharacterPrefix)));
            h.Patch(AccessTools.Method(typeof(TalkDoerLite), nameof(TalkDoerLite.Interact)),
                prefix: new HarmonyMethod(typeof(CouchCoop), nameof(InteractPrefix)));
            h.Patch(AccessTools.Method(typeof(Dungeonator.RoomHandler), nameof(Dungeonator.RoomHandler.GetNearestInteractable)),
                prefix: new HarmonyMethod(typeof(CouchCoop), nameof(NearestPrefix)));
        }

        static readonly AccessTools.FieldRef<Dungeonator.RoomHandler, System.Collections.Generic.List<IPlayerInteractable>> Interactables =
            AccessTools.FieldRefAccess<Dungeonator.RoomHandler, System.Collections.Generic.List<IPlayerInteractable>>("interactableObjects");

        /// <summary>
        /// Vanilla never even offers player two a Gungeoneer to talk to: the room's nearest-interactable search skips
        /// every talker marked PreventCoopInteraction for player two. Lift that mark on the character statues in the
        /// Breach before the search, so player two sees the prompt and can press interact.
        /// </summary>
        static void NearestPrefix(Dungeonator.RoomHandler __instance, PlayerController player)
        {
            if (!Couch || TalkFixOff || !player || player.IsPrimaryPlayer || !GameManager.Instance.IsFoyer) return;
            var list = Interactables(__instance);
            if (list == null) return;
            for (int i = 0; i < list.Count; i++)
                if (list[i] is TalkDoerLite t && t && t.PreventCoopInteraction && (t.GetComponent<FoyerCharacterSelectFlag>() || t.GetComponentInParent<FoyerCharacterSelectFlag>()))
                    t.PreventCoopInteraction = false;
        }

        static readonly bool TalkFixOff = Dev.DevEnv.IsHarness && System.Environment.GetEnvironmentVariable("GO_NO_COUCHTALK_FIX") == "1";

        /// <summary>The Gungeoneers only talk to player one in vanilla; on one PC player two may talk to them too.</summary>
        static void InteractPrefix(TalkDoerLite __instance, PlayerController interactor)
        {
            if (!Couch || TalkFixOff || !__instance || !interactor || interactor.IsPrimaryPlayer || !__instance.PreventCoopInteraction) return;
            if (!__instance.GetComponent<FoyerCharacterSelectFlag>() && !__instance.GetComponentInParent<FoyerCharacterSelectFlag>()) return;
            __instance.PreventCoopInteraction = false;
        }

        static bool Couch => !NetSession.Active && GameManager.HasInstance && GameManager.Instance.CurrentGameType == GameManager.GameType.COOP_2_PLAYER;

        /// <summary>Two players on one PC: keep the Gungeoneers out instead of hiding them all.</summary>
        static bool CoopChangedPrefix(FoyerCharacterSelectFlag __instance)
        {
            if (!Couch) return true;
            Show(__instance);
            return false;
        }

        static void Show(FoyerCharacterSelectFlag f)
        {
            if (f.IsCoopCharacter) return; // the Cultist keeps its own vanilla behaviour
            bool taken = Plays(GameManager.Instance.PrimaryPlayer, f) || Plays(GameManager.Instance.SecondaryPlayer, f);
            f.gameObject.SetActive(!taken);
            if (taken || !f.specRigidbody) return;
            SpriteOutlineManager.RemoveOutlineFromSprite(f.sprite, deparent: true);
            f.specRigidbody.enabled = true;
            PhysicsEngine.Instance.RegisterOverlappingGhostCollisionExceptions(f.specRigidbody);
            var idle = f.GetComponent<CharacterSelectIdleDoer>();
            if (idle) idle.enabled = true;
        }

        static bool Plays(PlayerController p, FoyerCharacterSelectFlag f) =>
            p && !string.IsNullOrEmpty(f.CharacterPrefabPath) && p.gameObject.name.StartsWith(f.CharacterPrefabPath.Substring(f.CharacterPrefabPath.LastIndexOf('/') + 1));

        /// <summary>Player two talked to a Gungeoneer: player two becomes them (vanilla would replace player one).</summary>
        static bool ChangeCharacterPrefix(ChangeToNewCharacter __instance)
        {
            if (!Couch) return true;
            var talk = __instance.Owner ? __instance.Owner.GetComponent<TalkDoerLite>() : null;
            var p2 = GameManager.Instance.SecondaryPlayer;
            // By the time the switch runs the conversation has usually ended: the game then keeps the talker as
            // CompletedTalkingPlayer.
            var talker = talk ? (talk.TalkingPlayer ? talk.TalkingPlayer : talk.CompletedTalkingPlayer) : null;
            if (!p2 || talker != p2) return true;
            var prefab = (GameObject)BraveResources.Load(__instance.PlayerPrefabPath);
            if (prefab == null) return true;
            var flag = __instance.Owner.GetComponent<FoyerCharacterSelectFlag>();
            // Same price as at the statue for player one.
            if (flag && flag.IsEevee) GameStatsManager.Instance.RegisterStatChange(TrackedStats.META_CURRENCY, -5f);
            if (flag && flag.IsGunslinger) GameStatsManager.Instance.RegisterStatChange(TrackedStats.META_CURRENCY, -7f);
            Vector3 at = p2.transform.position;
            p2.SetInputOverride("couch swap");
            Object.Destroy(p2.gameObject);
            var p = PlayerSlots.CreateSecondary(prefab, at);
            if (flag && flag.IsAlternateCostume) p.SwapToAlternateCostume();
            Plugin.Instance.StartCoroutine(FinishSwap(p));
            Plugin.Log.LogInfo("CouchCoop: player two is now " + __instance.PlayerPrefabPath);
            __instance.Finish();
            return false;
        }

        static System.Collections.IEnumerator FinishSwap(PlayerController p)
        {
            yield return null;
            if (!p) yield break;
            var gm = GameManager.Instance;
            gm.RefreshAllPlayers(); // the replaced player is gone now
            if (GameUIRoot.Instance) GameUIRoot.Instance.ConvertCoreUIToCoopMode();
            if (PhysicsEngine.Instance && p.specRigidbody) PhysicsEngine.Instance.RegisterOverlappingGhostCollisionExceptions(p.specRigidbody);
            if (gm.MainCameraController) gm.MainCameraController.ClearPlayerCache();
            if (SceneObjects.Breach) SceneObjects.Breach.ProcessPlayerEnteredFoyer(p);
            p.CurrentInputState = PlayerInputState.AllInput;
            foreach (var f in Object.FindObjectsOfType<FoyerCharacterSelectFlag>()) Show(f);
            // Statues hidden right now (player two's old character) are inactive, so look at those too.
            foreach (var f in Resources.FindObjectsOfTypeAll<FoyerCharacterSelectFlag>())
                if (f && f.gameObject.scene.IsValid() && !f.gameObject.activeSelf && f.PrerequisitesFulfilled()) Show(f);
        }
    }
}
