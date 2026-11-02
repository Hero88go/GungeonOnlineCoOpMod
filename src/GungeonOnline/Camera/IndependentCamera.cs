using System.Collections.Generic;
using System.Reflection.Emit;
using HarmonyLib;
using GungeonOnline.Game;

namespace GungeonOnline.Camera
{
    /// <summary>
    /// Each machine's camera follows only its own player. Vanilla co-op averages both players and pulls
    /// a player who strays into the partner's fight; both are off during an online session.
    /// </summary>
    internal static class IndependentCamera
    {
        public static void Install(Harmony h)
        {
            // Classic co-op style: the vanilla pull into your partner's fight, decided on the host (a guest's
            // character is moved there by the host and follows). Explore and Free for all: no pull.
            h.Patch(AccessTools.Method(typeof(PlayerController), "HandleCoopSpecificTimers"),
                prefix: new HarmonyMethod(typeof(IndependentCamera), nameof(PullPrefix)));
            h.Patch(AccessTools.Method(typeof(PlayerController), nameof(PlayerController.ReuniteWithOtherPlayer)),
                prefix: new HarmonyMethod(typeof(IndependentCamera), nameof(PullSafePrefix)),
                postfix: new HarmonyMethod(typeof(IndependentCamera), nameof(PulledPostfix)));
            // Vanilla co-op stops a player at the edge of the shared camera. With a camera per machine that
            // would pin the other player to the edge of your screen, so it is off online.
            h.Patch(AccessTools.Method(typeof(PlayerController), "CameraBoundsMovementRestrictor"),
                prefix: new HarmonyMethod(typeof(IndependentCamera), nameof(SkipInSession)));
            // The shared camera ignores ghosts; your own camera must keep following you as a ghost.
            h.Patch(AccessTools.Method(typeof(CameraController), nameof(CameraController.GetCoreCurrentBasePosition)),
                transpiler: new HarmonyMethod(typeof(IndependentCamera), nameof(GhostFieldTranspiler)));
            // Vanilla warps a ghost that leaves the shared camera back to its partner; online each has a camera.
            h.Patch(AccessTools.Method(typeof(PlayerController), "Update"),
                transpiler: new HarmonyMethod(typeof(IndependentCamera), nameof(GhostVisibilityTranspiler)));
        }

        static IEnumerable<CodeInstruction> GhostFieldTranspiler(IEnumerable<CodeInstruction> code)
        {
            var field = AccessTools.Field(typeof(PlayerController), nameof(PlayerController.IsGhost));
            var repl = AccessTools.Method(typeof(IndependentCamera), nameof(GhostForCamera));
            foreach (var ci in code)
            {
                if (ci.opcode == OpCodes.Ldfld && ci.operand as System.Reflection.FieldInfo == field)
                    yield return new CodeInstruction(OpCodes.Call, repl) { labels = ci.labels };
                else yield return ci;
            }
        }

        public static bool GhostForCamera(PlayerController p) => p.IsGhost && !(Session.NetSession.Active && p == PlayerSlots.Local);

        static IEnumerable<CodeInstruction> GhostVisibilityTranspiler(IEnumerable<CodeInstruction> code)
        {
            var target = AccessTools.Method(typeof(CameraController), nameof(CameraController.PointIsVisible), new[] { typeof(UnityEngine.Vector2), typeof(float) });
            var repl = AccessTools.Method(typeof(IndependentCamera), nameof(GhostPointVisible));
            foreach (var ci in code)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand as System.Reflection.MethodInfo == target)
                    yield return new CodeInstruction(OpCodes.Call, repl) { labels = ci.labels };
                else yield return ci;
            }
        }

        public static bool GhostPointVisible(CameraController cc, UnityEngine.Vector2 point, float buffer) =>
            Session.NetSession.Active || cc.PointIsVisible(point, buffer);

        static bool SkipInSession() => !Session.NetSession.Active;

        static bool PullPrefix(PlayerController __instance)
        {
            if (!Session.NetSession.Active) return true;
            if (!Session.NetSession.IsHost || Session.NetConfig.CoopStyle.Value != 0) return false;
            if (!Game.NPlayers.Players.NPlus) return true;
            // Three or four players: the game's timer only follows one partner.
            Game.NPlayers.GatherPatches.PullForN(__instance);
            return false;
        }

        static System.Collections.IEnumerator CheckLanding(PlayerController p)
        {
            float end = UnityEngine.Time.realtimeSinceStartup + 2f;
            while (UnityEngine.Time.realtimeSinceStartup < end) yield return null;
            if (!p || GameManager.Instance.Dungeon == null) yield break;
            var d = GameManager.Instance.Dungeon.data;
            var at = p.specRigidbody ? p.specRigidbody.UnitCenter.ToIntVector2(VectorConversions.Floor) : p.transform.position.IntXY(VectorConversions.Floor);
            var c = d.CheckInBounds(at.x, at.y) ? d.cellData[at.x][at.y] : null;
            bool bad = c == null || c.type != Dungeonator.CellType.FLOOR || c.isDoorFrameCell || c.isExitCell;
            Plugin.Log.LogInfo($"PULLCHECK {(bad ? "BAD" : "ok")} cell={(c != null ? c.type.ToString() : "none")} door={(c != null && (c.isDoorFrameCell || c.isExitCell))} at={at}");
        }

        static readonly bool oldPull = Dev.DevEnv.IsHarness && System.Environment.GetEnvironmentVariable("GO_OLD_PULL") == "1";

        /// <summary>Vanilla puts the pulled player on the exact spot of the one who walked in, which is often the
        /// doorway: the door then seals on top of them and they end up in the wall. Land on the nearest clear floor
        /// inside the room, away from doors, instead.</summary>
        static bool PullSafePrefix(PlayerController __instance, PlayerController other, bool useDefaultVFX)
        {
            if (!Session.NetSession.Active || oldPull || other == null) return true;
            var to = SafeSpotNear(other);
            Plugin.Log.LogInfo($"PULLSAFE {(to != null ? "to " + to.Value : "none found")} from {(UnityEngine.Vector2)other.transform.position} room={(other.CurrentRoom != null ? other.CurrentRoom.GetRoomName() : "none")}");
            if (to == null) return true;
            __instance.WarpToPoint(to.Value, useDefaultVFX);
            return false;
        }

        static bool Clear(Dungeonator.DungeonData d, int x, int y, Dungeonator.RoomHandler room)
        {
            if (!d.CheckInBounds(x, y)) return false;
            var c = d.cellData[x][y];
            return c != null && c.type == Dungeonator.CellType.FLOOR && (room == null || c.parentRoom == room) && !c.isExitCell && !c.isDoorFrameCell &&
                !c.doesDamage && !c.IsPlayerInaccessible && !c.containsTrap;
        }

        public static UnityEngine.Vector2? SafeSpotNear(PlayerController other)
        {
            var gm = GameManager.Instance;
            if (gm == null || gm.Dungeon == null) return null;
            var d = gm.Dungeon.data;
            var at = other.transform.position.IntXY(VectorConversions.Floor);
            // The room the player is walking into first; any floor if that has no clear spot close by.
            return Search(d, at, other.CurrentRoom) ?? Search(d, at, null);
        }

        static UnityEngine.Vector2? Search(Dungeonator.DungeonData d, IntVector2 at, Dungeonator.RoomHandler room)
        {
            for (int r = 0; r <= 12; r++)
                for (int dx = -r; dx <= r; dx++)
                    for (int dy = -r; dy <= r; dy++)
                    {
                        if (System.Math.Max(System.Math.Abs(dx), System.Math.Abs(dy)) != r) continue;
                        int x = at.x + dx, y = at.y + dy;
                        bool ok = true;
                        // A 2 by 2 footprint, and nothing within two cells that is a door.
                        for (int i = -2; i <= 3 && ok; i++)
                            for (int j = -2; j <= 3 && ok; j++)
                            {
                                bool foot = i >= 0 && i <= 1 && j >= 0 && j <= 1;
                                if (foot) ok = Clear(d, x + i, y + j, room);
                                else if (d.CheckInBounds(x + i, y + j))
                                {
                                    var c = d.cellData[x + i][y + j];
                                    if (c != null && (c.isExitCell || c.isDoorFrameCell)) ok = false;
                                }
                            }
                        if (ok) return new UnityEngine.Vector2(x + 0.5f, y + 0.25f);
                    }
            return null;
        }

        public static int Pulls;

        /// <summary>Count and log every Classic pull (host side), for the tests and for reports.</summary>
        static void PulledPostfix(PlayerController __instance, PlayerController other)
        {
            if (!Session.NetSession.Active) return;
            Pulls++;
            Plugin.Log.LogInfo($"PULL {(Game.PlayerSlots.IsRemote(__instance) ? "guest" : "host")} into {(other != null && other.CurrentRoom != null ? other.CurrentRoom.GetRoomName() : "?")} ghost={__instance.IsGhost}");
            if (Dev.DevEnv.IsHarness) __instance.StartCoroutine(CheckLanding(__instance));
            if (__instance == Game.PlayerSlots.Local) SoftenJump(other);
        }

        /// <summary>Your own player was just pulled across the floor (Classic: a friend started a fight). Instead of the
        /// view cutting straight to somewhere new, it comes in from black, and a line says where you are.</summary>
        public static void SoftenJump(PlayerController towards)
        {
            if (Pixelator.Instance) Pixelator.Instance.FadeToBlack(0.35f, true);
            string who = "your friend";
            int slot = towards != null ? Game.PlayerSlots.SlotOf(towards) : -1;
            if (slot >= 0 && Session.NetSession.Instance != null && Session.NetSession.Instance.Node != null)
                foreach (var c in Session.NetSession.Instance.Node.Connections) if (c.Slot == slot) who = Session.GungeonUI.WhoName(c);
            Session.Toasts.Show("Pulled into " + who + "'s fight", 3f);
        }

        /// <summary>Mark every remote player as ignored by this machine's camera.</summary>
        public static void Apply()
        {
            foreach (var kv in PlayerSlots.Remotes)
                if (kv.Value) PlayerSlots.SetIgnoredByCamera(kv.Value, true);
            if (GameManager.HasInstance && GameManager.Instance.MainCameraController != null)
                GameManager.Instance.MainCameraController.ClearPlayerCache();
        }
    }
}
