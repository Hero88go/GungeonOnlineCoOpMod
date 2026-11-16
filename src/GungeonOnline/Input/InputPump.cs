using System;
using System.Collections.Generic;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace GungeonOnline.Input
{
    /// <summary>
    /// Routes player slots to virtual devices and hooks InControl's device poll:
    /// BeforePoll sets virtual device frames, AfterPoll samples local players and sends their input at once.
    /// Each fires once per Unity frame, on the first poll of that frame.
    /// </summary>
    internal static class InputPump
    {
        static readonly Dictionary<int, NetInputDevice> devices = new Dictionary<int, NetInputDevice>();
        static readonly Dictionary<int, GungeonActions> bound = new Dictionary<int, GungeonActions>();
        static int beforeFrame = -1, afterFrame = -1;

        public static event Action BeforePoll;
        public static event Action AfterPoll;

        /// <summary>Unity frame number of the last poll, for pipeline delay measurement.</summary>
        public static int LastPollFrame;

        public static void Install(Harmony h)
        {
            var m = AccessTools.Method(typeof(InputManager), "UpdateInternal");
            h.Patch(m, prefix: new HarmonyMethod(typeof(InputPump), nameof(Prefix)), postfix: new HarmonyMethod(typeof(InputPump), nameof(Postfix)));
            // The game hands every player a pad from its couch co-op table each frame (BraveInput.Update) and whenever
            // controllers change. That table also counts the friends' virtual pads, so with a real gamepad plugged in
            // the friend's copy could be given your pad (your shots fired their gun too, your Start opened and closed
            // the pause menu at once) and you could be given theirs or none (you could not move).
            var assign = AccessTools.Method(typeof(BraveInput), "AssignActionsDevice");
            if (assign != null) h.Patch(assign, postfix: new HarmonyMethod(typeof(InputPump), nameof(AfterAssign)));
            else Plugin.Log.LogWarning("InputPump: BraveInput.AssignActionsDevice not found");
            // The options menu rebuilds its controller list when any device attaches; a virtual pad is not one of yours.
            foreach (var name in new[] { "InputManager_OnDeviceAttached", "InputManager_OnDeviceDetached" })
            {
                var om = AccessTools.Method(typeof(BraveOptionsMenuItem), name);
                if (om != null) h.Patch(om, prefix: new HarmonyMethod(typeof(InputPump), nameof(SkipVirtual)));
            }
        }

        static bool SkipVirtual(InputDevice obj) => !(obj is NetInputDevice);

        static readonly AccessTools.FieldRef<BraveInput, int> playerIdOf = AccessTools.FieldRefAccess<BraveInput, int>("m_playerID");

        static void AfterAssign(BraveInput __instance)
        {
            try
            {
                if (!Session.NetSession.Active || __instance == null) return;
                var a = __instance.ActiveActions;
                if (a == null) return;
                int id = playerIdOf(__instance);
                if (devices.TryGetValue(id, out var d))
                {
                    if (a.Device != d) a.Device = d;
                    if (a.ForceDisable) a.ForceDisable = false;
                }
                else if (id == 0)
                {
                    // Your own player: whatever you last touched, like single player.
                    var active = InputManager.ActiveDevice;
                    if (a.Device != active && !(active is NetInputDevice)) a.Device = active;
                    if (a.ForceDisable) a.ForceDisable = false;
                }
            }
            catch (Exception e) { Plugin.Log.LogError("InputPump assign: " + e.Message); }
        }

        static void Prefix()
        {
            // Every poll, not once a frame: a level load polls again later in the same frame, after the game has
            // handed out pads again.
            try { EnforceRouting(); } catch (Exception e) { Plugin.Log.LogError("InputPump routing: " + e); }
            if (beforeFrame == Time.frameCount) return;
            beforeFrame = Time.frameCount;
            try { BeforePoll?.Invoke(); EnforceRouting(); }
            catch (Exception e) { Plugin.Log.LogError("InputPump before: " + e); }
        }

        static void Postfix()
        {
            if (afterFrame == Time.frameCount) return;
            afterFrame = Time.frameCount;
            LastPollFrame = Time.frameCount;
            try { AfterPoll?.Invoke(); }
            catch (Exception e) { Plugin.Log.LogError("InputPump after: " + e); }
        }

        /// <summary>Give player <paramref name="playerId"/> a virtual device. Returns it.</summary>
        public static NetInputDevice Route(int playerId)
        {
            if (!devices.TryGetValue(playerId, out var d))
            {
                d = new NetInputDevice(playerId);
                InputManager.AttachDevice(d);
                devices[playerId] = d;
                Plugin.Log.LogInfo($"InputPump: player {playerId} -> virtual device");
            }
            return d;
        }

        public static void Unroute(int playerId)
        {
            if (devices.TryGetValue(playerId, out var d))
            {
                InputManager.DetachDevice(d);
                devices.Remove(playerId);
                bound.Remove(playerId);
            }
        }

        public static bool IsRouted(int playerId) => devices.ContainsKey(playerId);
        public static NetInputDevice DeviceFor(int playerId) => devices.TryGetValue(playerId, out var d) ? d : null;

        /// <summary>
        /// Keeps each routed player's action set pointed at its virtual device with the fixed bindings.
        /// The game rebuilds action sets on controller changes, so this is checked every frame (cheap).
        /// </summary>
        public static void EnforceRouting()
        {
            // An unrouted player (the local one) must never read a virtual device.
            var players = GameManager.HasInstance ? GameManager.Instance.AllPlayers : null;
            for (int i = 0; players != null && i < players.Length; i++)
            {
                if (!players[i]) continue;
                int id = players[i].PlayerIDX;
                if (devices.ContainsKey(id)) continue;
                var lb = BraveInput.GetInstanceForPlayer(id);
                var la = lb != null ? lb.ActiveActions : null;
                if (la != null && (la.Device is NetInputDevice || (la.Device == null && Session.NetSession.Active)) && !(InputManager.ActiveDevice is NetInputDevice)) la.Device = InputManager.ActiveDevice;
                if (la != null && Session.NetSession.Active) KeepKeyboard(la);
            }
            foreach (var kv in devices)
            {
                var bi = BraveInput.GetInstanceForPlayer(kv.Key);
                var actions = bi != null ? bi.ActiveActions : null;
                if (actions == null) continue;
                if (!bound.TryGetValue(kv.Key, out var prev) || !ReferenceEquals(prev, actions))
                {
                    NetBindings.Apply(actions);
                    bound[kv.Key] = actions;
                    Plugin.Log.LogInfo($"InputPump: bound player {kv.Key} action set");
                }
                if (actions.Device != kv.Value) actions.Device = kv.Value;
                if (actions.ForceDisable) actions.ForceDisable = false;
            }
        }

        static readonly AccessTools.FieldRef<PlayerAction, List<BindingSourceType>> ignored =
            AccessTools.FieldRefAccess<PlayerAction, List<BindingSourceType>>("m_ignoredBindingSources");

        /// <summary>
        /// With two players the game turns off keyboard and mouse for player one whenever player two has no controller
        /// yet (BraveInput.ReassignAllControllers, couch co-op rules). Online, player two is the friend's copy, so
        /// keyboard players could not move or pause at all. Your own player always keeps keyboard and mouse.
        /// </summary>
        static void KeepKeyboard(GungeonActions a)
        {
            var list = a.Actions;
            for (int i = 0; i < list.Count; i++)
            {
                var ig = ignored(list[i]);
                if (ig == null || ig.Count == 0) continue;
                ig.RemoveAll(t => t == BindingSourceType.KeyBindingSource || t == BindingSourceType.MouseBindingSource);
            }
        }

        /// <summary>Read a player's current action states into a wire frame.</summary>
        public static InputFrame Sample(GungeonActions a)
        {
            var f = new InputFrame();
            if (a == null) return f;
            f.Move = a.Move.Vector;
            f.Aim = a.Aim.Vector;
            foreach (var kv in NetBindings.Map)
            {
                var act = NetBindings.Action(a, kv.Key);
                if (act != null && act.IsPressed) f.Buttons |= (uint)kv.Value.Key;
            }
            var down = NetBindings.Action(a, "SelectDown");
            if (down != null && down.IsPressed) f.Flags |= NetFlags.SelectDown;
            return f;
        }
    }
}
