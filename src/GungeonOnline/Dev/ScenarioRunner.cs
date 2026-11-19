using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using GungeonOnline.Input;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>
    /// Harness only: plays a scripted input file into player 0 through a virtual device, the same path
    /// network input will take. Line format (times in seconds from player spawn):
    ///   at duration moveX moveY aimX aimY [Button,Button...]
    ///   shot at name          (screenshot)
    ///   mark at name          (logs player position)
    /// </summary>
    internal static class ScenarioRunner
    {
        struct Step { public float At, Dur; public InputFrame Frame; }
        struct Marker { public float At; public string Kind, Name; }

        static readonly List<Step> steps = new List<Step>();
        static readonly List<Marker> markers = new List<Marker>();
        static float t0 = -1f;
        static NetInputDevice device;

        static bool bot, passive;
        static float botPhase;

        public static void Begin()
        {
            passive = System.Environment.GetEnvironmentVariable("GO_BOT") == "passive";
            cartBot = System.Environment.GetEnvironmentVariable("GO_BOT") == "cart";
            danceBot = System.Environment.GetEnvironmentVariable("GO_BOT") == "dance";
            if (System.Environment.GetEnvironmentVariable("GO_BOT") == "fight" || passive || cartBot || danceBot)
            {
                bot = true;
                device = InputPump.Route(0);
                InputPump.BeforePoll += Tick;
                t0 = Time.realtimeSinceStartup;
                Plugin.Log.LogInfo("Scenario: fight bot");
                return;
            }
            if (DevEnv.Script == null) return;
            if (!File.Exists(DevEnv.Script)) { Plugin.Log.LogWarning("Scenario missing: " + DevEnv.Script); return; }
            foreach (var raw in File.ReadAllLines(DevEnv.Script))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var p = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (p[0] == "shot" || p[0] == "mark") { markers.Add(new Marker { Kind = p[0], At = F(p[1]), Name = p[2] }); continue; }
                var f = new InputFrame { Move = new Vector2(F(p[2]), F(p[3])), Aim = new Vector2(F(p[4]), F(p[5])) };
                if (p.Length > 6)
                    foreach (var b in p[6].Split(','))
                        f.Set((NetButton)Enum.Parse(typeof(NetButton), b), true);
                steps.Add(new Step { At = F(p[0]), Dur = F(p[1]), Frame = f });
            }
            device = InputPump.Route(0);
            InputPump.BeforePoll += Tick;
            t0 = Time.realtimeSinceStartup;
            Plugin.Log.LogInfo($"Scenario: {steps.Count} steps, {markers.Count} markers");
        }

        static bool cartBot;
        static float cartSince = -1f;
        public static int CartRides;
        static System.Collections.Generic.List<MineCartController> cartList;
        static float cartListAt;

        /// <summary>Cart bot: walk to the nearest cart, press interact to get in, ride for 6 s, dodge roll out, repeat.</summary>
        static InputFrame CartFrame(float t)
        {
            var f = new InputFrame();
            var me = GameManager.Instance.PrimaryPlayer;
            if (me == null || Game.SceneObjects.InBreach) return f;
            if (me.currentMineCart)
            {
                if (cartSince < 0) { cartSince = t; CartRides++; Plugin.Log.LogInfo("Harness: riding a minecart"); }
                if (t - cartSince > 6f) { f.Move = Vector2.left; f.Set(NetButton.DodgeRoll, Mathf.Repeat(t, 0.5f) < 0.1f); }
                return f;
            }
            cartSince = -1f;
            if (cartList == null || t > cartListAt + 2f) { cartList = Game.SceneObjects.All<MineCartController>(); cartListAt = t; }
            MineCartController best = null; float bestD = 1e9f;
            foreach (var c in cartList)
            {
                if (!c || c.occupation == MineCartController.CartOccupationState.ENEMY) continue;
                float d = ((Vector2)c.transform.position - me.CenterPosition).sqrMagnitude;
                if (d < bestD) { bestD = d; best = c; }
            }
            if (best == null || bestD > 100f) return f;
            var delta = best.specRigidbody.UnitCenter - me.CenterPosition;
            if (delta.magnitude > 1.2f) f.Move = delta.normalized;
            f.Set(NetButton.Interact, Mathf.Repeat(t, 0.5f) < 0.1f);
            return f;
        }

        /// <summary>Test bot: aim at the nearest living enemy in the same room, fire in bursts, strafe around it.</summary>
        static InputFrame BotFrame(float t)
        {
            if (cartBot) return CartFrame(t);
            if (danceBot) return DanceFrame(t);
            var f = new InputFrame();
            if (DevEnv.MapTestShoot) { botPhase += Time.deltaTime; f.Set(NetButton.Shoot, (botPhase % 0.3f) < 0.2f); return f; }
            var me = GameManager.Instance.PrimaryPlayer;
            // In the Breach, interacting would swap characters at the statues; the bot only plays dungeon floors.
            if (me == null || Game.SceneObjects.InBreach) return f;
            AIActor best = null;
            float bestD = 1e9f;
            foreach (var e in StaticReferenceManager.AllEnemies)
            {
                if (!e || e.healthHaver == null || e.healthHaver.IsDead || e.CompanionOwner != null) continue;
                if (e.ParentRoom != me.CurrentRoom) continue;
                float d = (e.CenterPosition - me.CenterPosition).sqrMagnitude;
                if (d < bestD) { bestD = d; best = e; }
            }
            if (best == null) return Loot(me, t);
            var to = (best.CenterPosition - me.CenterPosition).normalized;
            f.Aim = to;
            botPhase += Time.deltaTime;
            if (!passive) f.Set(NetButton.Shoot, (botPhase % 0.3f) < 0.2f);
            var strafe = new Vector2(-to.y, to.x) * (Mathf.Sin(t * 0.7f) > 0 ? 1f : -1f);
            float dist = Mathf.Sqrt(bestD);
            f.Move = (strafe + (dist > 6f ? to : dist < 3f ? -to : Vector2.zero)).normalized;
            if (Mathf.Repeat(t, 4f) < 0.02f) f.Set(NetButton.DodgeRoll, true);
            return f;
        }

        static bool danceBot;

        /// <summary>
        /// Latency tests: walk one of 8 directions, then straight back the same time (so the player stays put), with
        /// stops and dodge rolls in between: the starts, stops, turns and rolls where a friend's copy goes wrong.
        /// </summary>
        static InputFrame DanceFrame(float t)
        {
            var f = new InputFrame();
            if (GameManager.Instance.PrimaryPlayer == null || Game.SceneObjects.InBreach) return f;
            int leg = (int)(t / 0.6f);
            float inLeg = t - leg * 0.6f;
            int pair = leg / 2;
            int h = (pair * 7919 + 13) % 97;
            if (h % 6 == 0) return f; // a stop
            float ang = (h % 8) * 45f * Mathf.Deg2Rad;
            var dir = new Vector2(Mathf.Cos(ang), Mathf.Sin(ang));
            f.Move = leg % 2 == 0 ? dir : -dir;
            f.Aim = f.Move;
            if (h % 5 == 0 && inLeg < 0.03f) f.Set(NetButton.DodgeRoll, true);
            return f;
        }

        /// <summary>Room clear: walk to the nearest item or closed chest in this room and interact.</summary>
        static InputFrame Loot(PlayerController me, float t)
        {
            var f = new InputFrame();
            if (!GameManager.HasInstance || GameManager.Instance.Dungeon == null || GameManager.Instance.Dungeon.data == null) return f; // floor still loading
            Vector2? target = null;
            float bestD = 1e9f;
            foreach (var p in UnityEngine.Object.FindObjectsOfType<PickupObject>())
            {
                if (!p || p.transform.parent != null || !p.gameObject.activeInHierarchy || p.IsBeingSold) continue;
                Vector2 pos = p.transform.position;
                if (pos.GetAbsoluteRoom() != me.CurrentRoom) continue;
                float d = (pos - me.CenterPosition).sqrMagnitude;
                if (d < bestD) { bestD = d; target = pos + new Vector2(0.5f, 0.5f); }
            }
            foreach (var ch in StaticReferenceManager.AllChests)
            {
                if (!ch || ch.IsOpen || ch.IsBroken) continue;
                Vector2 pos = ch.transform.position;
                if (pos.GetAbsoluteRoom() != me.CurrentRoom) continue;
                float d = (pos - me.CenterPosition).sqrMagnitude;
                if (d < bestD) { bestD = d; target = pos + new Vector2(1f, 0.5f); }
            }
            foreach (var si in UnityEngine.Object.FindObjectsOfType<ShopItemController>())
            {
                if (!si || !si.item || !si.gameObject.activeInHierarchy) continue;
                Vector2 pos = si.transform.position;
                if (pos.GetAbsoluteRoom() != me.CurrentRoom) continue;
                float d = (pos - me.CenterPosition).sqrMagnitude;
                if (d < bestD) { bestD = d; target = pos + new Vector2(0.5f, 0.5f); }
            }
            if (target == null) return f;
            var delta = target.Value - me.CenterPosition;
            if (delta.magnitude > 1.0f) f.Move = delta.normalized;
            f.Set(NetButton.Interact, Mathf.Repeat(t, 0.5f) < 0.1f);
            return f;
        }

        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);

        static void Tick()
        {
            if (t0 < 0) return;
            InputPump.EnforceRouting();
            float t = Time.realtimeSinceStartup - t0;
            var frame = default(InputFrame);
            if (bot) frame = BotFrame(t);
            else
                foreach (var s in steps)
                    if (t >= s.At && t < s.At + s.Dur) frame = s.Frame;
            device.Frame = frame;

            for (int i = markers.Count - 1; i >= 0; i--)
            {
                if (t < markers[i].At) continue;
                var m = markers[i];
                markers.RemoveAt(i);
                var p = GameManager.Instance.PrimaryPlayer;
                string pos = p != null ? p.CenterPosition.ToString("F2") : "none";
                Plugin.Log.LogInfo($"MARK {m.Name} t={t:F2} pos={pos}");
                if (m.Kind == "shot")
                {
                    var dir = Environment.GetEnvironmentVariable("GO_RUN_DIR") ?? ".";
                    ScreenCapture.CaptureScreenshot(Path.Combine(dir, m.Name + ".png"));
                }
            }
        }
    }
}
