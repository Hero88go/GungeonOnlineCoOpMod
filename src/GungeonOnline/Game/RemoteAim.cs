using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// Aim assist is a per-player setting, but the game reads it from the local options for every player. While
    /// the game works out where another machine's player is aiming, use that player's own settings, so their
    /// shots go where they went on their screen.
    /// </summary>
    internal static class RemoteAim
    {
        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(PlayerController), "DetermineAimPointInWorld"),
                prefix: new HarmonyMethod(typeof(RemoteAim), nameof(Prefix)),
                postfix: new HarmonyMethod(typeof(RemoteAim), nameof(OwnAimPostfix)),
                finalizer: new HarmonyMethod(typeof(RemoteAim), nameof(Finalizer)));
        }

        // ---------- The owner's final aim ----------

        public static bool HasOwnAim;
        public static Vector2 OwnAimDirection;
        static readonly System.Collections.Generic.HashSet<int> forced = new System.Collections.Generic.HashSet<int>();

        /// <summary>Your own player: remember where its gun points this frame (sent with your next input).</summary>
        static void OwnAimPostfix(PlayerController __instance, Vector3 __result)
        {
            if (!NetSession.Active || __instance != PlayerSlots.Local || !__instance.gunAttachPoint) return;
            Vector2 d = (Vector2)__result - (Vector2)__instance.gunAttachPoint.position;
            if (d.sqrMagnitude < 0.0001f) return;
            OwnAimDirection = d.normalized;
            HasOwnAim = true;
        }

        /// <summary>Another machine's player: point exactly where its owner pointed (no aim assist worked out here).</summary>
        public static void Follow(PlayerController p, Input.InputFrame f)
        {
            if (!p || !p.gunAttachPoint) return;
            int id = p.GetInstanceID();
            if ((f.Flags & Input.NetFlags.FinalAim) != 0)
            {
                var aim = f.Aim;
                if (aim.sqrMagnitude > 0.0001f)
                {
                    p.forceAimPoint = (Vector2)p.gunAttachPoint.position + aim.normalized * 150f;
                    forced.Add(id);
                }
            }
            else if (forced.Remove(id)) p.forceAimPoint = null;
        }

        struct Saved { public bool Active; public float Assist; public bool Beam; public BraveInput.AutoAim Mode; }

        static void Prefix(PlayerController __instance, out Saved __state)
        {
            __state = default(Saved);
            if (!NetSession.Active || !PlayerSlots.IsRemote(__instance) || GameManager.Options == null) return;
            if (!Lobby.Aim.TryGetValue(PlayerSlots.SlotOf(__instance), out var a)) return;
            var o = GameManager.Options;
            __state = new Saved { Active = true, Assist = o.controllerAimAssistMultiplier, Beam = o.controllerBeamAimAssist, Mode = BraveInput.AutoAimMode };
            o.controllerAimAssistMultiplier = a.Assist;
            o.controllerBeamAimAssist = a.Beam;
            BraveInput.AutoAimMode = a.Super ? BraveInput.AutoAim.SuperAutoAim : BraveInput.AutoAim.AutoAim;
        }

        static System.Exception Finalizer(System.Exception __exception, Saved __state)
        {
            if (__state.Active && GameManager.Options != null)
            {
                GameManager.Options.controllerAimAssistMultiplier = __state.Assist;
                GameManager.Options.controllerBeamAimAssist = __state.Beam;
                BraveInput.AutoAimMode = __state.Mode;
            }
            return __exception;
        }
    }
}
