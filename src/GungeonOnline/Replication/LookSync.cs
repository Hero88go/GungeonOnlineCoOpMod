using System.Collections;
using System.Collections.Generic;
using Dungeonator;
using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Session;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// What a guest's enemy copies cannot work out themselves, because their AI is off.
    /// Parts: every sprite inside a boss (the Dragun's head and neck, the Treadnaught's gun, the Gatling Gull's shadow,
    /// statues and rings) is placed, shown and drawn as on the host, twenty times a second.
    /// Beams: enemy lasers start, turn and stop with the host's (each guest's own game judges hits on its own player).
    /// Room effects the AI starts: darkness, goop (poison, fire, water), the Gorgun's stone gaze, sky rockets with their
    /// landing marks, and the spawn reticle that warns where an enemy is about to appear.
    /// </summary>
    [DefaultExecutionOrder(31700)] // after WorldSync moved the copies this frame
    internal sealed class LookSync : MonoBehaviour
    {
        public static LookSync Instance { get; private set; }
        const byte EvDark = 30, EvGoop = 31, EvStone = 32, EvRocket = 33, EvRocketLand = 34, EvReticle = 35, EvVfx = 36, EvVfxStop = 37;
        const float PartRate = 0.05f, FullEvery = 1f;

        static bool IsClient => NetSession.Active && !NetSession.IsHost;
        static bool IsHostSession => NetSession.Active && NetSession.IsHost;
        static bool applying;

        readonly WireWriter w = new WireWriter(1400);
        readonly WireWriter gw = new WireWriter(1400);
        public int PartsSent, PartsApplied, BeamsStarted, GoopsSent, GoopsApplied, RocketsSent, RocketsMade;

        public static void Install(Harmony h)
        {
            var room = typeof(RoomHandler);
            h.Patch(AccessTools.Method(room, nameof(RoomHandler.BecomeTerrifyingDarkRoom)), postfix: new HarmonyMethod(typeof(LookSync), nameof(DarkPostfix)));
            h.Patch(AccessTools.Method(room, nameof(RoomHandler.EndTerrifyingDarkRoom)), postfix: new HarmonyMethod(typeof(LookSync), nameof(LightPostfix)));
            var goop = typeof(DeadlyDeadlyGoopManager);
            foreach (var name in new[] { "AddGoopCircle", "AddGoopRing", "AddGoopLine", "AddGoopRect", "AddGoopPoints", "RemoveGoopCircle",
                                         "IgniteGoopCircle", "IgniteGoopLine", "FreezeGoopCircle", "FreezeGoopLine", "ElectrifyGoopCircle", "ElectrifyGoopLine" })
            {
                var m = AccessTools.Method(goop, name);
                if (m == null) { Plugin.Log.LogWarning("LookSync: no goop method " + name); continue; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(LookSync), nameof(GoopPrefix)), finalizer: new HarmonyMethod(typeof(LookSync), nameof(GoopFinalizer)));
            }
            h.Patch(AccessTools.Method(typeof(MeduziStoneEyesBehavior), "AnimationEventTriggered"), postfix: new HarmonyMethod(typeof(LookSync), nameof(StonePostfix)));
            h.Patch(AccessTools.Method(typeof(SkyRocket), nameof(SkyRocket.Start)), postfix: new HarmonyMethod(typeof(LookSync), nameof(RocketStartPostfix)));
            h.Patch(AccessTools.Method(typeof(SkyRocket), nameof(SkyRocket.Update)), prefix: new HarmonyMethod(typeof(LookSync), nameof(RocketUpdatePrefix)), postfix: new HarmonyMethod(typeof(LookSync), nameof(RocketUpdatePostfix)));
            h.Patch(AccessTools.Method(typeof(AIActor), nameof(AIActor.HandleReinforcementFallIntoRoom)), postfix: new HarmonyMethod(typeof(LookSync), nameof(ReinforcePostfix)));
            h.Patch(AccessTools.Method(typeof(AIAnimator), nameof(AIAnimator.PlayVfx)), prefix: new HarmonyMethod(typeof(LookSync), nameof(PlayVfxPrefix)));
            h.Patch(AccessTools.Method(typeof(AIAnimator), nameof(AIAnimator.StopVfx)), prefix: new HarmonyMethod(typeof(LookSync), nameof(StopVfxPrefix)));
        }

        void Awake() { Instance = this; }

        void Start()
        {
            NetSession.Instance.On(Channel.Looks, OnLooks);
            NetSession.Instance.SessionEnded += Clear;
        }

        public void OnFloorChanged() => Clear();

        void Clear()
        {
            hostLooks.Clear(); guestLooks.Clear(); rocketIds.Clear(); rocketCopies.Clear(); rocketLand.Clear(); landSent.Clear();
        }

        int Broadcast(byte[] data, Channel ch, Delivery d)
        {
            var ws = WorldSync.Instance;
            int n = 0;
            foreach (var c in NetSession.Instance.Node.Connections)
                if (ws != null && ws.IsReady(c)) { c.Send((byte)ch, data, d); n++; }
            return n;
        }

        // ---------------- Parts and beams ----------------

        /// <summary>One sprite inside an enemy, named by its object name and which of the same name it is.</summary>
        sealed class Part
        {
            public tk2dBaseSprite Sprite;
            public ushort Key;
            public bool On, Flip;
            public short X, Y;
            public byte Rot;
            public ushort Coll, Id;
            public bool Have;          // guest: a state arrived for it
            public bool Muted;         // guest: its own animators are off
            public SpeculativeRigidbody Body;
        }

        sealed class Look
        {
            public AIActor Actor;
            public List<Part> Parts = new List<Part>();
            public Dictionary<ushort, Part> ByKey = new Dictionary<ushort, Part>();
            public List<AIBeamShooter> Beams = new List<AIBeamShooter>();
            public float NextScan, NextFull;
            // guest beam state from the host
            public bool[] BeamOn = new bool[0];
            public float[] BeamAngle = new float[0];
        }

        readonly Dictionary<uint, Look> hostLooks = new Dictionary<uint, Look>();
        readonly Dictionary<uint, Look> guestLooks = new Dictionary<uint, Look>();
        float nextParts;

        static ushort Hash16(string s)
        {
            uint h = 2166136261;
            for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 16777619; }
            return (ushort)((h >> 16) ^ (h & 0xFFFF));
        }

        /// <summary>Enemies whose looks are sent: bosses, mini bosses and anything that fires beams.</summary>
        static bool Wanted(AIActor a) =>
            a && a.healthHaver && !a.healthHaver.IsDead && (a.healthHaver.IsBoss || a.healthHaver.IsSubboss || a.GetComponentInChildren<AIBeamShooter>() != null);

        /// <summary>The sprites that belong to this enemy and not to another copied enemy inside it.</summary>
        static void Scan(Look l)
        {
            var a = l.Actor;
            var old = l.ByKey;
            l.Parts = new List<Part>();
            l.ByKey = new Dictionary<ushort, Part>();
            var seen = new Dictionary<string, int>();
            foreach (var s in a.GetComponentsInChildren<tk2dBaseSprite>(true))
            {
                if (!s || s == a.sprite) continue;
                var owner = s.GetComponentInParent<AIActor>();
                if (owner != a && owner != null && NetIds.Has(owner)) continue; // a copied enemy of its own (Kill Pillars' statues)
                string n = s.name;
                seen.TryGetValue(n, out int k);
                seen[n] = k + 1;
                ushort key = Hash16(n + "#" + k);
                if (l.ByKey.ContainsKey(key)) continue;
                Part p = old.TryGetValue(key, out var was) && was.Sprite == s ? was : new Part { Sprite = s, Key = key, Body = s.GetComponent<SpeculativeRigidbody>() };
                l.Parts.Add(p);
                l.ByKey[key] = p;
            }
            l.Beams = new List<AIBeamShooter>(a.GetComponentsInChildren<AIBeamShooter>(true));
        }

        void HostParts()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextParts) return;
            nextParts = now + PartRate;
            foreach (var kv in NetIds.All)
            {
                var a = kv.Value as AIActor;
                if (!Wanted(a)) continue;
                if (!hostLooks.TryGetValue(kv.Key, out var l)) hostLooks[kv.Key] = l = new Look { Actor = a };
                if (now >= l.NextScan) { Scan(l); l.NextScan = now + 1f; }
                bool full = now >= l.NextFull;
                if (full) l.NextFull = now + FullEvery;
                SendLook(kv.Key, l, full);
            }
            List<uint> gone = null;
            foreach (var kv in hostLooks) if (!kv.Value.Actor || kv.Value.Actor.healthHaver.IsDead) (gone ??= new List<uint>()).Add(kv.Key);
            if (gone != null) foreach (var id in gone) hostLooks.Remove(id);
        }

        void SendLook(uint id, Look l, bool full)
        {
            Vector3 root = l.Actor.transform.position;
            w.Reset();
            w.U32(id);
            // Beams: count, then per beam on/off and angle.
            w.U8((byte)Mathf.Min(l.Beams.Count, 255));
            for (int i = 0; i < l.Beams.Count && i < 255; i++)
            {
                var b = l.Beams[i];
                bool on = b && b.IsFiringLaser;
                w.Bool(on);
                if (on) w.U16((ushort)(Mathf.Repeat(b.LaserAngle, 360f) / 360f * 65535f));
            }
            int countAt = w.Length;
            w.U8(0);
            int n = 0;
            foreach (var p in l.Parts)
            {
                var s = p.Sprite;
                if (!s) continue;
                bool on = s.renderer && s.renderer.enabled && s.gameObject.activeInHierarchy;
                Vector2 d = (Vector2)(s.transform.position - root);
                short x = (short)Mathf.Clamp(Mathf.RoundToInt(d.x * 32f), short.MinValue, short.MaxValue);
                short y = (short)Mathf.Clamp(Mathf.RoundToInt(d.y * 32f), short.MinValue, short.MaxValue);
                byte rot = (byte)(Mathf.RoundToInt(Mathf.Repeat(s.transform.eulerAngles.z, 360f) / 360f * 256f) & 0xFF);
                ushort coll = s.Collection ? Hash16(s.Collection.name) : (ushort)0;
                ushort sid = (ushort)Mathf.Max(0, s.spriteId);
                bool flip = s.FlipX;
                if (!full && p.Have && on == p.On && x == p.X && y == p.Y && rot == p.Rot && coll == p.Coll && sid == p.Id && flip == p.Flip) continue;
                if (n == 255 || w.Length > 1200) break; // rest next time (they still differ)
                p.Have = true; p.On = on; p.X = x; p.Y = y; p.Rot = rot; p.Coll = coll; p.Id = sid; p.Flip = flip;
                w.U16(p.Key);
                w.U8((byte)((on ? 1 : 0) | (flip ? 2 : 0)));
                w.I16(x); w.I16(y); w.U8(rot); w.U16(coll); w.U16(sid);
                n++;
            }
            if (n == 0 && l.Beams.Count == 0) return;
            w.Buffer[countAt] = (byte)n;
            if (Broadcast(w.ToArray(), Channel.Looks, Delivery.Unreliable) > 0) PartsSent++;
        }

        void OnLooks(Connection c, byte[] data, Delivery d)
        {
            if (NetSession.IsHost) return;
            var ws = WorldSync.Instance;
            if (ws == null || !ws.FloorReady) return;
            var r = new WireReader(data, 0, data.Length);
            uint id = r.U32();
            var a = NetIds.Get<AIActor>(id);
            if (!a || !ws.IsReplica(id)) return;
            if (!guestLooks.TryGetValue(id, out var l) || l.Actor != a)
            {
                guestLooks[id] = l = new Look { Actor = a };
                Scan(l);
                l.NextScan = Time.realtimeSinceStartup + 1f;
            }
            int nb = r.U8();
            if (l.BeamOn.Length != nb) { l.BeamOn = new bool[nb]; l.BeamAngle = new float[nb]; }
            for (int i = 0; i < nb; i++)
            {
                l.BeamOn[i] = r.Bool();
                if (l.BeamOn[i]) l.BeamAngle[i] = r.U16() / 65535f * 360f;
            }
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                ushort key = r.U16();
                byte f = r.U8();
                short x = r.I16(), y = r.I16();
                byte rot = r.U8();
                ushort coll = r.U16(), sid = r.U16();
                if (!l.ByKey.TryGetValue(key, out var p))
                {
                    if (Time.realtimeSinceStartup < l.NextScan) continue;
                    Scan(l); // children made since (a spawned ring, a dropped hatch): look again once a second at most
                    l.NextScan = Time.realtimeSinceStartup + 1f;
                    if (!l.ByKey.TryGetValue(key, out p)) continue;
                }
                p.Have = true; p.On = (f & 1) != 0; p.Flip = (f & 2) != 0; p.X = x; p.Y = y; p.Rot = rot; p.Coll = coll; p.Id = sid;
            }
            PartsApplied++;
        }

        /// <summary>Guest: show every boss copy's parts as the host last sent them (runs after the game moved them).</summary>
        void GuestApply()
        {
            List<uint> gone = null;
            foreach (var kv in guestLooks)
            {
                var l = kv.Value;
                var a = l.Actor;
                if (!a || a.healthHaver == null || a.healthHaver.IsDead) { (gone ??= new List<uint>()).Add(kv.Key); continue; }
                Vector3 root = a.transform.position;
                foreach (var p in l.Parts)
                {
                    var s = p.Sprite;
                    if (!p.Have || !s) continue;
                    try { ApplyPart(p, s, root); }
                    catch (System.Exception e) { p.Have = false; Plugin.Log.LogWarning($"LookSync: part {s.name} of {a.name}: {e.Message}"); }
                }
                try { ApplyBeams(l); }
                catch (System.Exception e) { Plugin.Log.LogWarning($"LookSync: beams of {a.name}: {e.Message}"); l.BeamOn = new bool[0]; }
            }
            if (gone != null) foreach (var id in gone) guestLooks.Remove(id);
        }

        static void ApplyPart(Part p, tk2dBaseSprite s, Vector3 root)
        {
            if (!p.Muted)
            {
                // The part's own animation would fight the host's frames.
                p.Muted = true;
                var anim = s.GetComponent<tk2dSpriteAnimator>();
                if (anim) anim.enabled = false;
                var ai = s.GetComponent<AIAnimator>();
                if (ai) ai.enabled = false;
            }
            if (s.renderer && s.renderer.enabled != p.On) s.renderer.enabled = p.On;
            var want = new Vector3(root.x + p.X / 32f, root.y + p.Y / 32f, s.transform.position.z);
            if ((s.transform.position - want).sqrMagnitude > 1e-6f)
            {
                s.transform.position = want;
                s.UpdateZDepth();
                if (p.Body && p.Body.enabled) p.Body.Reinitialize();
            }
            float rot = p.Rot * (360f / 256f);
            if (Mathf.Abs(Mathf.DeltaAngle(s.transform.eulerAngles.z, rot)) > 1f)
                s.transform.rotation = Quaternion.Euler(0f, 0f, rot);
            if (s.FlipX != p.Flip) s.FlipX = p.Flip;
            if (s.spriteId != p.Id || (s.Collection && Hash16(s.Collection.name) != p.Coll))
            {
                var coll = FindCollection(s, p.Coll);
                if (coll != null && p.Id < coll.spriteDefinitions.Length) s.SetSprite(coll, p.Id);
            }
        }

        static readonly Dictionary<ushort, tk2dSpriteCollectionData> collections = new Dictionary<ushort, tk2dSpriteCollectionData>();

        /// <summary>The sprite collection the host's part draws from: its own, or one its animations use.</summary>
        static tk2dSpriteCollectionData FindCollection(tk2dBaseSprite s, ushort hash)
        {
            if (s.Collection && Hash16(s.Collection.name) == hash) return s.Collection;
            if (collections.TryGetValue(hash, out var c) && c) return c;
            var anim = s.GetComponent<tk2dSpriteAnimator>();
            if (anim && anim.Library != null && anim.Library.clips != null)
                foreach (var clip in anim.Library.clips)
                    if (clip != null && clip.frames != null)
                        foreach (var f in clip.frames)
                            if (f != null && f.spriteCollection && Hash16(f.spriteCollection.name) == hash) { collections[hash] = f.spriteCollection; return f.spriteCollection; }
            return null;
        }

        void ApplyBeams(Look l)
        {
            for (int i = 0; i < l.BeamOn.Length && i < l.Beams.Count; i++)
            {
                var b = l.Beams[i];
                if (!b) continue;
                if (l.BeamOn[i])
                {
                    if (!b.IsFiringLaser) { b.StartFiringLaser(l.BeamAngle[i]); BeamsStarted++; }
                    else b.LaserAngle = Mathf.LerpAngle(b.LaserAngle, l.BeamAngle[i], Mathf.Clamp01(Time.deltaTime * 30f));
                }
                else if (b.IsFiringLaser) b.StopFiringLaser();
            }
        }

        // ---------------- Darkness ----------------

        static void DarkPostfix(RoomHandler __instance, float duration, float goalIntensity, float lightIntensity) => SendDark(__instance, true, duration, goalIntensity, lightIntensity);
        static void LightPostfix(RoomHandler __instance, float duration, float goalIntensity, float lightIntensity) => SendDark(__instance, false, duration, goalIntensity, lightIntensity);

        static void SendDark(RoomHandler room, bool dark, float duration, float goal, float light)
        {
            if (!IsHostSession || Instance == null) return;
            int i = WorldSync.RoomIndex(room);
            if (i < 0) return;
            var w = Instance.w;
            w.Reset(); w.U8(EvDark); w.I16((short)i); w.Bool(dark); w.F32(duration); w.F32(goal); w.F32(light);
            Instance.Broadcast(w.ToArray(), Channel.Event, Delivery.ReliableOrdered);
        }

        // ---------------- Goop ----------------

        static int goopDepth;

        /// <summary>Only the outermost goop call is sent (a circle grown over time calls rings).</summary>
        static void GoopPrefix() => goopDepth++;

        static void GoopFinalizer(DeadlyDeadlyGoopManager __instance, System.Reflection.MethodBase __originalMethod, object[] __args)
        {
            goopDepth--;
            if (goopDepth > 0 || applying || !IsHostSession || Instance == null || __instance == null || __instance.goopDefinition == null) return;
            try { Instance.QueueGoop(__instance.goopDefinition.name, __originalMethod.Name, __args); }
            catch (System.Exception e) { Plugin.Log.LogWarning("LookSync: goop " + e.Message); }
        }

        static readonly string[] goopOps = { "AddGoopCircle", "AddGoopRing", "AddGoopLine", "AddGoopRect", "AddGoopPoints", "RemoveGoopCircle",
                                             "IgniteGoopCircle", "IgniteGoopLine", "FreezeGoopCircle", "FreezeGoopLine", "ElectrifyGoopCircle", "ElectrifyGoopLine" };
        int goopCount;

        /// <summary>Host: goop calls are gathered for one message per frame.</summary>
        void QueueGoop(string def, string op, object[] args)
        {
            int k = System.Array.IndexOf(goopOps, op);
            if (k < 0) return;
            if (goopCount == 0) { gw.Reset(); gw.U8(EvGoop); gw.U8(0); }
            if (gw.Length > 1100) FlushGoop();
            if (goopCount == 0) { gw.Reset(); gw.U8(EvGoop); gw.U8(0); }
            gw.U8((byte)k);
            gw.Str(def);
            switch (op)
            {
                case "AddGoopCircle": case "RemoveGoopCircle": case "IgniteGoopCircle": case "FreezeGoopCircle": case "ElectrifyGoopCircle":
                    V2(gw, (Vector2)args[0]); gw.F32((float)args[1]);
                    if (op == "AddGoopCircle") gw.Bool((bool)args[3]);
                    break;
                case "AddGoopRing":
                    V2(gw, (Vector2)args[0]); gw.F32((float)args[1]); gw.F32((float)args[2]); gw.Bool((bool)args[4]);
                    break;
                case "AddGoopLine": case "IgniteGoopLine": case "FreezeGoopLine": case "ElectrifyGoopLine":
                    V2(gw, (Vector2)args[0]); V2(gw, (Vector2)args[1]); gw.F32((float)args[2]);
                    break;
                case "AddGoopRect":
                    V2(gw, (Vector2)args[0]); V2(gw, (Vector2)args[1]);
                    break;
                case "AddGoopPoints":
                    {
                        var pts = (List<Vector2>)args[0];
                        int cnt = Mathf.Min(pts != null ? pts.Count : 0, 60);
                        gw.U8((byte)cnt);
                        for (int i = 0; i < cnt; i++) V2(gw, pts[i]);
                        gw.F32((float)args[1]); V2(gw, (Vector2)args[2]); gw.F32((float)args[3]);
                        break;
                    }
            }
            goopCount++;
            if (goopCount == 255) FlushGoop();
        }

        void FlushGoop()
        {
            if (goopCount == 0) return;
            gw.Buffer[1] = (byte)goopCount;
            Broadcast(gw.ToArray(), Channel.Event, Delivery.ReliableOrdered);
            GoopsSent += goopCount;
            goopCount = 0;
        }

        static void V2(WireWriter w, Vector2 v) { w.F32(v.x); w.F32(v.y); }
        static Vector2 V2(ref WireReader r) => new Vector2(r.F32(), r.F32());

        static readonly Dictionary<string, GoopDefinition> goopDefs = new Dictionary<string, GoopDefinition>();

        static GoopDefinition GoopDef(string name)
        {
            if (goopDefs.TryGetValue(name, out var d) && d) return d;
            foreach (var m in StaticReferenceManager.AllGoops)
                if (m && m.goopDefinition && m.goopDefinition.name == name) return goopDefs[name] = m.goopDefinition;
            foreach (var g in Resources.FindObjectsOfTypeAll<GoopDefinition>())
                if (g && g.name == name) return goopDefs[name] = g;
            return null;
        }

        void ApplyGoop(ref WireReader r)
        {
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                int k = r.U8();
                var def = GoopDef(r.Str());
                var m = def ? DeadlyDeadlyGoopManager.GetGoopManagerForGoopType(def) : null;
                string op = k < goopOps.Length ? goopOps[k] : "";
                switch (op)
                {
                    case "AddGoopCircle": { var c = V2(ref r); float rad = r.F32(); bool quiet = r.Bool(); if (m) m.AddGoopCircle(c, rad, -1, quiet); break; }
                    case "RemoveGoopCircle": { var c = V2(ref r); float rad = r.F32(); if (m) m.RemoveGoopCircle(c, rad); break; }
                    case "IgniteGoopCircle": { var c = V2(ref r); float rad = r.F32(); if (m) m.IgniteGoopCircle(c, rad); break; }
                    case "FreezeGoopCircle": { var c = V2(ref r); float rad = r.F32(); if (m) m.FreezeGoopCircle(c, rad); break; }
                    case "ElectrifyGoopCircle": { var c = V2(ref r); float rad = r.F32(); if (m) m.ElectrifyGoopCircle(c, rad); break; }
                    case "AddGoopRing": { var c = V2(ref r); float a = r.F32(), b = r.F32(); bool quiet = r.Bool(); if (m) m.AddGoopRing(c, a, b, -1, quiet); break; }
                    case "AddGoopLine": { var a = V2(ref r); var b = V2(ref r); float rad = r.F32(); if (m) m.AddGoopLine(a, b, rad); break; }
                    case "IgniteGoopLine": { var a = V2(ref r); var b = V2(ref r); float rad = r.F32(); if (m) m.IgniteGoopLine(a, b, rad); break; }
                    case "FreezeGoopLine": { var a = V2(ref r); var b = V2(ref r); float rad = r.F32(); if (m) m.FreezeGoopLine(a, b, rad); break; }
                    case "ElectrifyGoopLine": { var a = V2(ref r); var b = V2(ref r); float rad = r.F32(); if (m) m.ElectrifyGoopLine(a, b, rad); break; }
                    case "AddGoopRect": { var a = V2(ref r); var b = V2(ref r); if (m) m.AddGoopRect(a, b); break; }
                    case "AddGoopPoints":
                        {
                            int cnt = r.U8();
                            var pts = new List<Vector2>(cnt);
                            for (int j = 0; j < cnt; j++) pts.Add(V2(ref r));
                            float rad = r.F32(); var ex = V2(ref r); float exr = r.F32();
                            if (m) m.AddGoopPoints(pts, rad, ex, exr);
                            break;
                        }
                    default: return; // unknown op: the rest of the message cannot be read
                }
                GoopsApplied++;
            }
        }

        // ---------------- Stone gaze ----------------

        static readonly AccessTools.FieldRef<MeduziStoneEyesBehavior, Vector2> stoneCenter = AccessTools.FieldRefAccess<MeduziStoneEyesBehavior, Vector2>("m_distortionCenter");
        static readonly AccessTools.FieldRef<MeduziStoneEyesBehavior, float> stoneTimer = AccessTools.FieldRefAccess<MeduziStoneEyesBehavior, float>("m_timer");
                static readonly Dictionary<MeduziStoneEyesBehavior, float> stoneLastTimer = new Dictionary<MeduziStoneEyesBehavior, float>();

        /// <summary>Host: the Gorgun's gaze wave left her eyes. Each guest runs the wave against its own player.</summary>
        static void StonePostfix(MeduziStoneEyesBehavior __instance)
        {
            if (!IsHostSession || Instance == null || __instance == null) return;
            float timer = stoneTimer(__instance);
            // The handler runs for every animation event; a wave starts when it set the timer back up to the full duration.
            bool had = stoneLastTimer.TryGetValue(__instance, out var last);
            stoneLastTimer[__instance] = timer;
            if ((had && timer <= last + 0.05f) || timer < __instance.distortionDuration * 0.5f) return;
            var w = Instance.w;
            var c = stoneCenter(__instance);
            w.Reset(); w.U8(EvStone);
            V2(w, c);
            w.F32(__instance.distortionIntensity); w.F32(__instance.distortionThickness);
            w.F32(__instance.distortionMaxRadius); w.F32(__instance.distortionDuration); w.F32(__instance.stoneDuration);
            Instance.Broadcast(w.ToArray(), Channel.Event, Delivery.ReliableOrdered);
            if (Dev.DevEnv.IsHarness) Plugin.Log.LogInfo($"LOOKSYNC stone wave sent from {c}");
        }

        /// <summary>Harness: send a stone wave from this spot as if the Gorgun had looked.</summary>
        public void TestStone(Vector2 c)
        {
            w.Reset(); w.U8(EvStone); V2(w, c);
            w.F32(0.5f); w.F32(0.04f); w.F32(20f); w.F32(1.5f); w.F32(3f);
            Broadcast(w.ToArray(), Channel.Event, Delivery.ReliableOrdered);
        }

        IEnumerator StoneWave(Vector2 center, float intensity, float thickness, float maxRadius, float duration, float stone)
        {
            Exploder.DoDistortionWave(center, intensity, thickness, maxRadius, duration);
            float timer = duration, prev = 0f;
            while (timer > 0f)
            {
                yield return null;
                timer -= BraveTime.DeltaTime;
                float wave = BraveMathCollege.LinearToSmoothStepInterpolate(0f, maxRadius, 1f - timer / duration);
                var p = PlayerSlots.Local;
                if (p && !p.healthHaver.IsDead && !p.spriteAnimator.QueryInvulnerabilityFrame() && p.healthHaver.IsVulnerable)
                {
                    Vector2 at = p.specRigidbody.GetUnitCenter(ColliderType.HitBox);
                    float dist = Vector2.Distance(at, center);
                    // Same rule as the Gorgun's own: caught by the wave front unless looking away from her.
                    if (dist >= prev - 0.25f && dist <= wave + 0.25f && BraveMathCollege.AbsAngleBetween(p.FacingDirection, (at - center).ToAngle()) >= 45f)
                    {
                        if (p.CurrentStoneGunTimer <= 0f && Dev.DevEnv.IsHarness) Plugin.Log.LogInfo("LOOKSYNC stone wave caught me");
                        p.CurrentStoneGunTimer = stone;
                    }
                }
                prev = wave;
            }
            if (Dev.DevEnv.IsHarness)
            {
                var me = PlayerSlots.Local;
                Plugin.Log.LogInfo($"LOOKSYNC stone wave done dist={(me ? Vector2.Distance(me.CenterPosition, center) : -1f):F1} max={maxRadius} vuln={(me ? me.healthHaver.IsVulnerable : false)} iframe={(me ? me.spriteAnimator.QueryInvulnerabilityFrame() : false)} facing={(me ? me.FacingDirection : 0f):F0} stone={(me ? me.CurrentStoneGunTimer : 0f):F1}");
            }
        }

        // ---------------- Sky rockets ----------------

        static readonly AccessTools.FieldRef<SkyRocket, SkyRocket.SkyRocketState> rocketState = AccessTools.FieldRefAccess<SkyRocket, SkyRocket.SkyRocketState>("m_state");
        static readonly AccessTools.FieldRef<SkyRocket, float> rocketTimer = AccessTools.FieldRefAccess<SkyRocket, float>("m_timer");
        static readonly AccessTools.FieldRef<SkyRocket, Vector3> rocketLandAt = AccessTools.FieldRefAccess<SkyRocket, Vector3>("m_targetLandPosition");
        readonly Dictionary<int, uint> rocketIds = new Dictionary<int, uint>();      // host: instance id to network id
        readonly HashSet<uint> landSent = new HashSet<uint>();
        readonly Dictionary<uint, SkyRocket> rocketCopies = new Dictionary<uint, SkyRocket>(); // guest
        readonly Dictionary<int, Vector2> rocketLand = new Dictionary<int, Vector2>();          // guest: copy instance id to landing spot
        static readonly HashSet<int> copyRockets = new HashSet<int>();
        uint nextRocket = 1;

        public static bool IsRocketCopy(Object o) => o && copyRockets.Contains(o.GetInstanceID());

        static string PrefabName(GameObject g)
        {
            var n = g.name;
            int i = n.IndexOf("(Clone)");
            return (i >= 0 ? n.Substring(0, i) : n).Trim();
        }

        static void RocketStartPostfix(SkyRocket __instance)
        {
            if (!IsHostSession || Instance == null || !__instance) return;
            uint id = Instance.nextRocket++;
            Instance.rocketIds[__instance.GetInstanceID()] = id;
            var w = Instance.w;
            Vector2 at = __instance.transform.position;
            w.Reset(); w.U8(EvRocket); w.U32(id); w.Str(PrefabName(__instance.gameObject)); V2(w, at);
            if (Instance.Broadcast(w.ToArray(), Channel.Event, Delivery.ReliableOrdered) > 0) Instance.RocketsSent++;
        }

        /// <summary>Guest: a copy waits at the top until the host says where its rocket comes down.</summary>
        static bool RocketUpdatePrefix(SkyRocket __instance)
        {
            if (!IsClient || Instance == null || !copyRockets.Contains(__instance.GetInstanceID())) return true;
            if (Instance.rocketLand.TryGetValue(__instance.GetInstanceID(), out var land)) { __instance.TargetVector2 = land; return true; }
            return !(rocketState(__instance) == SkyRocket.SkyRocketState.Ascend && rocketTimer(__instance) - BraveTime.DeltaTime <= 0f);
        }

        /// <summary>Host: say where each rocket comes down once it picked the spot.</summary>
        static void RocketUpdatePostfix(SkyRocket __instance)
        {
            if (!IsHostSession || Instance == null || !__instance || rocketState(__instance) == SkyRocket.SkyRocketState.Ascend) return;
            if (!Instance.rocketIds.TryGetValue(__instance.GetInstanceID(), out var id) || !Instance.landSent.Add(id)) return;
            var w = Instance.w;
            w.Reset(); w.U8(EvRocketLand); w.U32(id); V2(w, rocketLandAt(__instance));
            Instance.Broadcast(w.ToArray(), Channel.Event, Delivery.ReliableOrdered);
        }

        static readonly Dictionary<string, GameObject> rocketPrefabs = new Dictionary<string, GameObject>();

        static GameObject RocketPrefab(string name)
        {
            if (rocketPrefabs.TryGetValue(name, out var g) && g) return g;
            foreach (var r in Resources.FindObjectsOfTypeAll<SkyRocket>())
                if (r && r.gameObject.name == name && !r.gameObject.scene.IsValid()) return rocketPrefabs[name] = r.gameObject;
            return null;
        }

        void ApplyRocket(ref WireReader r)
        {
            uint id = r.U32();
            string name = r.Str();
            var at = V2(ref r);
            if (rocketCopies.ContainsKey(id)) return;
            var prefab = RocketPrefab(name);
            if (!prefab) { Plugin.Log.LogInfo("LookSync: no rocket prefab " + name); return; }
            var go = SpawnManager.SpawnProjectile(prefab, at, Quaternion.identity);
            var rocket = go ? go.GetComponent<SkyRocket>() : null;
            if (!rocket) return;
            copyRockets.Add(rocket.GetInstanceID());
            var proj = go.GetComponent<Projectile>();
            if (proj) copyRockets.Add(proj.GetInstanceID());
            rocketCopies[id] = rocket;
            RocketsMade++;
        }

        void ApplyRocketLand(ref WireReader r)
        {
            uint id = r.U32();
            var at = V2(ref r);
            if (rocketCopies.TryGetValue(id, out var rocket) && rocket)
            {
                rocketLand[rocket.GetInstanceID()] = at;
                rocket.TargetVector2 = at;
            }
        }

        // ---------------- Spawn reticle ----------------

        /// <summary>Host: an enemy is about to appear with the warning reticle; guests show the reticle there too.</summary>
        static void ReinforcePostfix(AIActor __instance, float delay)
        {
            if (!IsHostSession || Instance == null || !__instance || !__instance.sprite || __instance.reinforceType != AIActor.ReinforceType.FullVfx || __instance.CompanionOwner != null) return;
            Vector3 at = __instance.transform.position + __instance.sprite.GetRelativePositionFromAnchor(tk2dBaseSprite.Anchor.LowerCenter).ToVector3ZUp();
            var w = Instance.w;
            w.Reset(); w.U8(EvReticle); V2(w, at); w.F32(delay);
            if (Dev.DevEnv.IsHarness) Plugin.Log.LogInfo($"LOOKSYNC reticle sent {(Vector2)at}");
            Instance.Broadcast(w.ToArray(), Channel.Event, Delivery.ReliableOrdered);
        }

        IEnumerator Reticle(Vector2 at, float delay)
        {
            if (delay > 0f) yield return new WaitForSeconds(delay);
            var go = (GameObject)Object.Instantiate(ResourceCache.Acquire("Global VFX/VFX_SpawnEnemy_Reticle"));
            var s = go.GetComponent<tk2dBaseSprite>();
            s.transform.position = at;
            s.HeightOffGround = -1.5f;
            s.UpdateZDepth();
            AkSoundEngine.PostEvent("Play_ENM_spawn_reticle_01", go);
            if (Dev.DevEnv.IsHarness) Plugin.Log.LogInfo($"LOOKSYNC reticle at {at}");
        }

        // ---------------- Enemy effects (charge glows, telegraphs) ----------------

        /// <summary>The enemy with a network id this animator belongs to, and which of its animators it is.</summary>
        static bool AnimatorAddress(AIAnimator an, out uint id, out int index)
        {
            id = 0; index = -1;
            var a = an.GetComponentInParent<AIActor>();
            if (!a) return false;
            id = NetIds.Of(a);
            if (id == 0) return false;
            index = System.Array.IndexOf(a.GetComponentsInChildren<AIAnimator>(true), an);
            return index >= 0;
        }

        /// <summary>Host: an enemy shows a named effect (a beam's charge glow, a boss's warning); guests show it on
        /// the copy. Guest: copies show effects only on the host's word (their own animations would add others).</summary>
        static bool PlayVfxPrefix(AIAnimator __instance, string name, Vector2? position)
        {
            if (!NetSession.Active || __instance == null) return true;
            if (IsClient) return applying || !Instance || !WorldSync.Instance || !WorldSync.Instance.IsReplica(NetIds.Of(__instance.GetComponentInParent<AIActor>()));
            if (Instance == null || !AnimatorAddress(__instance, out var id, out var index)) return true;
            var w = Instance.w;
            w.Reset(); w.U8(EvVfx); w.U32(id); w.U8((byte)index); w.Str(name ?? "");
            w.Bool(position.HasValue);
            if (position.HasValue) V2(w, position.Value);
            Instance.Broadcast(w.ToArray(), Channel.Event, Delivery.ReliableOrdered);
            return true;
        }

        static bool StopVfxPrefix(AIAnimator __instance, string name)
        {
            if (!NetSession.Active || __instance == null) return true;
            if (IsClient) return applying || !Instance || !WorldSync.Instance || !WorldSync.Instance.IsReplica(NetIds.Of(__instance.GetComponentInParent<AIActor>()));
            if (Instance == null || !AnimatorAddress(__instance, out var id, out var index)) return true;
            var w = Instance.w;
            w.Reset(); w.U8(EvVfxStop); w.U32(id); w.U8((byte)index); w.Str(name ?? "");
            Instance.Broadcast(w.ToArray(), Channel.Event, Delivery.ReliableOrdered);
            return true;
        }

        static AIAnimator ReplicaAnimator(uint id, int index)
        {
            var a = NetIds.Get<AIActor>(id);
            if (!a) return null;
            var all = a.GetComponentsInChildren<AIAnimator>(true);
            return index >= 0 && index < all.Length ? all[index] : null;
        }

        // ---------------- Events in (guest) ----------------

        /// <summary>Called by WorldSync for event types 30 to 39 (reader is just past the type byte).</summary>
        public void Apply(byte type, ref WireReader r)
        {
            applying = true;
            try
            {
                switch (type)
                {
                    case EvDark:
                        {
                            var room = WorldSync.RoomAt(r.I16());
                            bool dark = r.Bool();
                            float dur = r.F32(), goal = r.F32(), light = r.F32();
                            if (room == null) break;
                            if (dark) room.BecomeTerrifyingDarkRoom(dur, goal, light);
                            else room.EndTerrifyingDarkRoom(dur, goal, light);
                            break;
                        }
                    case EvGoop: ApplyGoop(ref r); break;
                    case EvStone:
                        {
                            var c = V2(ref r);
                            float inten = r.F32(), thick = r.F32(), maxR = r.F32(), dur = r.F32(), stone = r.F32();
                            StartCoroutine(StoneWave(c, inten, thick, maxR, dur, stone));
                            break;
                        }
                    case EvVfx:
                        {
                            uint id = r.U32(); int index = r.U8(); string name = r.Str();
                            Vector2? at = r.Bool() ? V2(ref r) : (Vector2?)null;
                            var an = ReplicaAnimator(id, index);
                            if (an) an.PlayVfx(name, null, null, at);
                            break;
                        }
                    case EvVfxStop:
                        {
                            uint id = r.U32(); int index = r.U8(); string name = r.Str();
                            var an = ReplicaAnimator(id, index);
                            if (an) an.StopVfx(name);
                            break;
                        }
                    case EvRocket: ApplyRocket(ref r); break;
                    case EvRocketLand: ApplyRocketLand(ref r); break;
                    case EvReticle:
                        {
                            var at = V2(ref r);
                            float delay = r.F32();
                            StartCoroutine(Reticle(at, delay));
                            break;
                        }
                }
            }
            finally { applying = false; }
        }

        void LateUpdate()
        {
            long t0 = Dev.NetProfiler.Begin();
            try
            {
                if (!NetSession.Active) return;
                if (NetSession.IsHost) { HostParts(); FlushGoop(); }
                else GuestApply();
            }
            catch (System.Exception e) { Plugin.Log.LogWarning("LookSync: " + e.Message); }
            finally { Dev.NetProfiler.End("looks", t0); }
        }
    }
}
