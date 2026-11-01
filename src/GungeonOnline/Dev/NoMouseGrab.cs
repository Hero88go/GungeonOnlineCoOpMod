using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>
    /// Tests only: hidden test games never grab the mouse. The game clips the Windows cursor to its window and
    /// confines it while playing; from a hidden test copy that trapped the real mouse of whoever uses the PC.
    /// </summary>
    internal static class NoMouseGrab
    {
        public static void Install(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GameCursorController), nameof(GameCursorController.ToggleClip)), prefix: new HarmonyMethod(typeof(NoMouseGrab), nameof(Skip)));
            h.Patch(AccessTools.Method(typeof(GameCursorController), "Start"), postfix: new HarmonyMethod(typeof(NoMouseGrab), nameof(Free)));
            h.Patch(AccessTools.Method(typeof(GameCursorController), nameof(GameCursorController.DrawCursor)), postfix: new HarmonyMethod(typeof(NoMouseGrab), nameof(Free)));
        }

        static bool Skip() => false;

        static void Free()
        {
            if (Cursor.lockState != CursorLockMode.None) Cursor.lockState = CursorLockMode.None;
        }
    }
}
