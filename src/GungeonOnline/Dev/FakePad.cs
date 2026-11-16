using InControl;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>
    /// Tests only (GO_FAKE_PAD=early|late): a stand-in for a real gamepad on this machine. "early" plugs it in at boot,
    /// "late" once the friend's copy exists (a pad that wakes up mid-session sits after the friend's virtual pad).
    /// It holds Shoot and stick-right for 1.5 s of every 4 s. PADCHECK lines count frames where the friend's copy read
    /// this pad (leak) and frames where your own player did (ok).
    /// </summary>
    internal sealed class FakePad : InputDevice
    {
        public bool Press;
        public FakePad() : base("Harness Pad")
        {
            DeviceClass = InputDeviceClass.Controller;
            DeviceStyle = InputDeviceStyle.XboxOne;
            foreach (var t in new[] { InputControlType.LeftStickLeft, InputControlType.LeftStickRight, InputControlType.LeftStickUp, InputControlType.LeftStickDown,
                InputControlType.RightStickLeft, InputControlType.RightStickRight, InputControlType.RightStickUp, InputControlType.RightStickDown,
                InputControlType.RightBumper, InputControlType.Start, InputControlType.Action1 })
                AddControl(t, t.ToString(), 0f, 1f);
        }

        public override void Update(ulong updateTick, float deltaTime)
        {
            UpdateWithValue(InputControlType.LeftStickRight, Press ? 1f : 0f, updateTick, deltaTime);
            UpdateWithState(InputControlType.RightBumper, Press, updateTick, deltaTime);
        }

        static readonly string mode = System.Environment.GetEnvironmentVariable("GO_FAKE_PAD");
        static FakePad pad, twin;
        static int leak, ok, frames, localWrong;
        static float lastLog;

        public static void Tick()
        {
            if (string.IsNullOrEmpty(mode)) return;
            if (pad == null)
            {
                bool late = mode == "late";
                if (late && !(Session.NetSession.Active && Game.PlayerSlots.Local && System.Linq.Enumerable.Any(Game.PlayerSlots.Remotes, kv => kv.Value))) return;
                if (!late && !InputManager.IsSetup) return;
                pad = new FakePad();
                InputManager.AttachDevice(pad);
                // "double": one physical pad seen twice (Steam Input's virtual pad next to the real one).
                if (mode == "double") { twin = new FakePad(); InputManager.AttachDevice(twin); }
                Plugin.Log.LogInfo($"PADCHECK attached ({mode}) devices={InputManager.Devices.Count}");
                return;
            }
            pad.Press = Time.realtimeSinceStartup % 4f < 1.5f;
            if (twin != null) twin.Press = pad.Press;
        }

        public static void Check()
        {
            if (pad == null || !Session.NetSession.Active) return;
            var local = Game.PlayerSlots.Local;
            if (!local) return;
            frames++;
            var lb = BraveInput.GetInstanceForPlayer(local.PlayerIDX);
            if (lb != null && lb.ActiveActions != null)
            {
                if (pad.Press && lb.ActiveActions.ShootAction.IsPressed) ok++;
                if (lb.ActiveActions.Device is Input.NetInputDevice) localWrong++;
            }
            foreach (var kv in Game.PlayerSlots.Remotes)
            {
                if (!kv.Value) continue;
                var bi = BraveInput.GetInstanceForPlayer(kv.Value.PlayerIDX);
                var dev = Input.InputPump.DeviceFor(kv.Value.PlayerIDX);
                if (bi == null || bi.ActiveActions == null) continue;
                bool sent = dev != null && dev.Frame.Has(Input.NetButton.Shoot);
                if (pad.Press && bi.ActiveActions.ShootAction.IsPressed && !sent) leak++;
            }
            if (Time.realtimeSinceStartup - lastLog > 10f)
            {
                lastLog = Time.realtimeSinceStartup;
                string devs = "";
                foreach (var kv in Game.PlayerSlots.Remotes)
                    if (kv.Value) { var a = BraveInput.GetInstanceForPlayer(kv.Value.PlayerIDX)?.ActiveActions; devs += $" r{kv.Value.PlayerIDX}={(a != null && a.Device != null ? a.Device.Name : "-")}"; }
                Plugin.Log.LogInfo($"PADCHECK frames={frames} ok={ok} leak={leak} localWrong={localWrong} local={(lb?.ActiveActions?.Device != null ? lb.ActiveActions.Device.Name : "-")}{devs} active={InputManager.ActiveDevice?.Name}");
            }
        }
    }
}
