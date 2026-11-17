using HarmonyLib;
using HutongGames.PlayMaker;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Conversations, shrines and NPC dialogue lock only the talking player, but they also take over the screen
    /// of whatever machine runs them: camera lock on the NPC, letterbox, hidden HUD, the response bar.
    /// When the talking player is another machine's player, that presentation is skipped here; the conversation
    /// logic still runs (driven by that player's own input) so its outcome is the same everywhere.
    /// </summary>
    internal static class PresentationGate
    {
        static int depth;
        public static bool Suppressing => depth > 0;

        public static void Install(Harmony h)
        {
            var t = typeof(PresentationGate);
            foreach (var type in new[] { typeof(HutongGames.PlayMaker.Actions.BeginConversation), typeof(HutongGames.PlayMaker.Actions.EndConversation) })
            {
                var m = AccessTools.Method(type, "OnEnter");
                if (m != null) h.Patch(m, prefix: new HarmonyMethod(t, nameof(EnterPrefix)), finalizer: new HarmonyMethod(t, nameof(Exit)));
            }
            foreach (var m in typeof(GameUIRoot).GetMethods())
                if (m.Name == nameof(GameUIRoot.DisplayPlayerConversationOptions))
                    h.Patch(m, prefix: new HarmonyMethod(t, nameof(OptionsPrefix)), finalizer: new HarmonyMethod(t, nameof(Exit)));

            var skip = new HarmonyMethod(t, nameof(SkipWhenSuppressing));
            h.Patch(AccessTools.Method(typeof(Pixelator), nameof(Pixelator.LerpToLetterbox)), prefix: skip);
            h.Patch(AccessTools.Method(typeof(GameUIRoot), nameof(GameUIRoot.ToggleLowerPanels)), prefix: skip);
            h.Patch(AccessTools.Method(typeof(GameUIRoot), nameof(GameUIRoot.HideCoreUI)), prefix: skip);
            h.Patch(AccessTools.Method(typeof(GameUIRoot), nameof(GameUIRoot.ShowCoreUI)), prefix: skip);
            h.Patch(AccessTools.Method(typeof(CameraController), nameof(CameraController.SetManualControl)), prefix: skip);
            // The response bar opens from a coroutine a frame later, so it is gated by its own talker argument.
            h.Patch(AccessTools.Method(typeof(ConversationBarController), nameof(ConversationBarController.ShowBar)),
                prefix: new HarmonyMethod(t, nameof(ShowBarPrefix)));
        }

        static bool IsRemoteTalker(PlayerController p) => Session.NetSession.Active && p != null && PlayerSlots.IsRemote(p);

        static bool mapBlockBefore;
        static readonly bool MapGateOff = Dev.DevEnv.IsHarness && System.Environment.GetEnvironmentVariable("GO_MAPGATE") == "off"; // tests: the old behaviour

        static void EnterPrefix(FsmStateAction __instance, ref bool __state)
        {
            __state = false;
            mapBlockBefore = Minimap.HasInstance && Minimap.Instance && Minimap.Instance.TemporarilyPreventMinimap;
            var go = __instance != null && __instance.Fsm != null ? __instance.Fsm.GameObject : null;
            var td = go != null ? go.GetComponent<TalkDoerLite>() : null;
            if (td != null && IsRemoteTalker(td.TalkingPlayer)) { depth++; __state = true; }
        }

        static void OptionsPrefix(PlayerController interactingPlayer, ref bool __state)
        {
            __state = false;
            if (IsRemoteTalker(interactingPlayer)) { depth++; __state = true; }
        }

        static System.Exception Exit(System.Exception __exception, bool __state)
        {
            if (__state)
            {
                depth--;
                // The game blocks the map for the whole screen while anyone talks: a friend's conversation leaves ours alone.
                if (depth == 0 && !MapGateOff && Minimap.HasInstance && Minimap.Instance) Minimap.Instance.TemporarilyPreventMinimap = mapBlockBefore;
            }
            return __exception;
        }

        static bool SkipWhenSuppressing() => !Suppressing;

        static bool ShowBarPrefix(PlayerController interactingPlayer) => !IsRemoteTalker(interactingPlayer);
    }
}
