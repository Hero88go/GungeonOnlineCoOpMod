using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Blanks are the owner's. A friend's blank used to reach the host only as a replayed button press; when that
    /// press did not land on the host's copy (mid roll, a merged input), the host kept counting the blank, every repair
    /// gave it back to the friend (free blanks), and the host's enemy bullets were not cleared. Now a player's copy
    /// never blanks by itself: its owner says "I used a blank" and every machine runs it on that player's copy.
    /// Sent on the Props channel (the event ids are all taken), the host passes it on to everyone else.
    /// </summary>
    internal static class BlankSync
    {
        public const byte EvBlank = 3;
        static bool scope;
        public static int Sent, Applied;
        static readonly System.Reflection.MethodInfo DoBlank = AccessTools.Method(typeof(PlayerController), "DoConsumableBlank");

        public static void Install(Harmony h)
        {
            h.Patch(DoBlank, prefix: new HarmonyMethod(typeof(BlankSync), nameof(Prefix)), postfix: new HarmonyMethod(typeof(BlankSync), nameof(Postfix)));
        }

        static bool Prefix(PlayerController __instance, out int __state)
        {
            __state = __instance ? __instance.Blanks : 0;
            if (!NetSession.Active || scope || !__instance) return true;
            return !PlayerSlots.IsRemote(__instance); // a copy blanks only when its owner says so
        }

        static void Postfix(PlayerController __instance, int __state)
        {
            if (!NetSession.Active || scope || !__instance || __instance != PlayerSlots.Local) return;
            if (__instance.Blanks >= __state) return; // nothing was used
            var w = new WireWriter();
            w.U8(EvBlank); w.U8((byte)NetSession.LocalSlot);
            NetSession.Instance.Node.Broadcast((byte)Channel.Props, w.ToArray(), Delivery.ReliableOrdered);
            Sent++;
        }

        /// <summary>Someone used a blank: run it on their copy here (the host also passes it on).</summary>
        public static void Apply(Connection from, byte[] data)
        {
            if (data.Length < 2) return;
            int slot = data[1];
            if (NetSession.IsHost)
                foreach (var c in NetSession.Instance.Node.Connections)
                    if (c != from) c.Send((byte)Channel.Props, data, Delivery.ReliableOrdered);
            var p = PlayerSlots.ForSlot(slot);
            if (!p || !PlayerSlots.IsRemote(p)) return;
            scope = true;
            try
            {
                if (p.Blanks <= 0) p.Blanks = 1; // the owner had one: the blank happens even if the count here was behind
                DoBlank.Invoke(p, null);
                Applied++;
                if (Dev.DevEnv.IsHarness) Plugin.Log.LogInfo($"BLANKTEST slot {slot} blanked here, copy has {p.Blanks} left");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("BlankSync: " + e.Message); }
            finally { scope = false; }
        }
    }
}
