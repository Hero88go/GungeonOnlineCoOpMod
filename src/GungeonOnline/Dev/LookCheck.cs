using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>
    /// Harness checks for how a player looks on the other screens.
    /// GO_ALT_COSTUME=host|guest: that player wears the alternate costume (picked in the Breach).
    /// GO_FIRE_AT=seconds (with GO_FIRE_WHO=host|guest): that player catches fire then, and goes out 6 s later.
    /// Every 2 s each game logs "LOOKCHECK" with every player's costume and fire state as it sees them.
    /// </summary>
    internal static class LookCheck
    {
        static bool costumeDone, fireOn, fireOff;
        static float nextLog;

        static bool reticleDone, stoneDone, customDone;
        static int propStep;
        static Vector2 barrelStart, tableStart;
        static float propNext;

        /// <summary>GO_PROP_AT=seconds (guest): kick the nearest barrel, then flip the nearest table, the way the
        /// interact button does. Both games log where that barrel and table are ("PROPTEST") every second after.</summary>
        static void PropTest(float t, PlayerController me)
        {
            if (!float.TryParse(System.Environment.GetEnvironmentVariable("GO_PROP_AT"), out var at) || t < at || t < propNext) return;
            if (Game.SceneObjects.InBreach || GameManager.Instance.IsLoadingLevel) return;
            propNext = t + 1f;
            if (!Session.NetSession.IsHost && propStep == 0)
            {
                var k = Nearest(Game.SceneObjects.All<KickableObject>(), me.CenterPosition, x => x && x.specRigidbody && x.specRigidbody.enabled);
                if (!k) { Plugin.Log.LogInfo("PROPTEST no barrel on this floor"); propStep = 9; return; }
                barrelStart = k.transform.position;
                me.WarpToPoint(barrelStart + new Vector2(-1.2f, 0.2f));
                propStep = 1;
                return;
            }
            if (!Session.NetSession.IsHost && propStep == 1)
            {
                var k = Nearest(Game.SceneObjects.All<KickableObject>(), barrelStart, x => x);
                k.Interact(me);
                Plugin.Log.LogInfo($"PROPTEST kicked barrel from {barrelStart}");
                var f = Nearest(Game.SceneObjects.All<FlippableCover>(), me.CenterPosition, x => x && !x.IsFlipped);
                if (f) { tableStart = f.transform.position; me.WarpToPoint(tableStart + new Vector2(0f, -1.5f)); }
                propStep = 2;
                return;
            }
            if (!Session.NetSession.IsHost && propStep == 2)
            {
                var f = Nearest(Game.SceneObjects.All<FlippableCover>(), tableStart, x => x);
                if (f && !f.IsFlipped) { f.Interact(me); Plugin.Log.LogInfo($"PROPTEST flipped table at {tableStart}"); }
                propStep = 3;
                return;
            }
            // Both: report every kicked barrel and flipped table there is (the test compares host and guest).
            var sb = new System.Text.StringBuilder($"PROPTEST wall={System.DateTime.Now:HH:mm:ss.f}");
            foreach (var k in Game.SceneObjects.All<KickableObject>())
                if (k && k.specRigidbody && k.specRigidbody.enabled)
                    sb.Append($" barrel@{(Vector2)k.transform.position:F1}");
            foreach (var f in Game.SceneObjects.All<FlippableCover>()) if (f && f.IsFlipped) sb.Append($" table@{(Vector2)f.transform.position:F1}:flipped");
            Plugin.Log.LogInfo(sb.ToString());
        }

        static T Nearest<T>(System.Collections.Generic.List<T> items, Vector2 p, System.Func<T, bool> ok) where T : Component
        {
            T best = null; float bd = float.MaxValue;
            foreach (var x in items) { if (!x || !ok(x)) continue; float d = Vector2.Distance(x.transform.position, p); if (d < bd) { bd = d; best = x; } }
            return best;
        }

        /// <summary>GO_RETICLE_AT=seconds (host): a bullet kin drops into the host's room the way reinforcements do,
        /// with the warning reticle first.</summary>
        static void ReticleTest(float t, PlayerController me)
        {
            if (reticleDone || !Session.NetSession.IsHost || !float.TryParse(System.Environment.GetEnvironmentVariable("GO_RETICLE_AT"), out var at) || t < at) return;
            if (Game.SceneObjects.InBreach || GameManager.Instance.IsLoadingLevel || me.CurrentRoom == null) return;
            reticleDone = true;
            var prefab = EnemyDatabase.GetOrLoadByGuid("01972dee89fc4404a5c408d50007dad5"); // bullet kin
            var a = AIActor.Spawn(prefab, me.CenterPosition + new Vector2(3f, 2f), me.CurrentRoom, true, AIActor.AwakenAnimationType.Spawn, false);
            a.reinforceType = AIActor.ReinforceType.FullVfx;
            a.HandleReinforcementFallIntoRoom(0.5f);
            Plugin.Log.LogInfo("RETICLETEST dropped an enemy in");
        }

        static int gunStep;
        static float gunNext, gunLog;

        /// <summary>GO_GUN_AT=seconds: the host gives the guest two more guns; the guest then switches guns on its own
        /// (the way a mouse wheel or number key does, which sends no button) every 3 s. Both games log "GUNTEST" with
        /// the gun the guest's player holds on their screen once a second.</summary>
        static void GunTest(float t, PlayerController me)
        {
            if (!float.TryParse(System.Environment.GetEnvironmentVariable("GO_GUN_AT"), out var at) || t < at) return;
            if (Game.SceneObjects.InBreach || GameManager.Instance.IsLoadingLevel) return;
            var guest = Session.NetSession.IsHost ? Game.PlayerSlots.ForSlot(1) : me;
            if (!guest || guest.inventory == null) return;
            if (Session.NetSession.IsHost && gunStep == 0)
            {
                gunStep = 1;
                int given = 0;
                foreach (var o in PickupObjectDatabase.Instance.Objects)
                {
                    var g = o as Gun;
                    if (!g || g.InfiniteAmmo || g.PickupObjectId <= 0 || g.quality == PickupObject.ItemQuality.EXCLUDED || g.quality == PickupObject.ItemQuality.SPECIAL) continue;
                    if (guest.inventory.AllGuns.Exists(x => x && x.PickupObjectId == g.PickupObjectId)) continue;
                    LootEngine.GivePrefabToPlayer(g.gameObject, guest);
                    Plugin.Log.LogInfo($"GUNTEST gave {g.PickupObjectId} {g.name}");
                    if (++given == 2) break;
                }
            }
            if (!Session.NetSession.IsHost && t >= at + 6f && gunStep < 5 && t >= gunNext && guest.inventory.AllGuns.Count >= 3)
            {
                gunNext = t + 3f;
                gunStep++;
                var all = guest.inventory.AllGuns;
                int i = (all.IndexOf(guest.CurrentGun) + 1) % all.Count;
                guest.ChangeToGunSlot(i, true);
                Plugin.Log.LogInfo($"GUNTEST guest switched to {(guest.CurrentGun ? guest.CurrentGun.PickupObjectId : -1)}");
            }
            if (t >= gunLog)
            {
                gunLog = t + 1f;
                Plugin.Log.LogInfo($"GUNTEST wall={System.DateTime.Now:HH:mm:ss.f} guest holds {(guest.CurrentGun ? guest.CurrentGun.PickupObjectId : -1)} of {guest.inventory.AllGuns.Count}");
            }
        }

        static bool Me(string who) => who == (Session.NetSession.IsHost ? "host" : "guest");

        public static void Tick()
        {
            if (!DevEnv.IsHarness || !Session.NetSession.Active || !GameManager.HasInstance) return;
            var me = Game.PlayerSlots.Local;
            if (!me) return;
            float t = Time.realtimeSinceStartup;
            if (!costumeDone && Game.SceneObjects.InBreach && Me(System.Environment.GetEnvironmentVariable("GO_ALT_COSTUME") ?? "") && t > 12f)
            {
                costumeDone = true;
                if (!me.IsUsingAlternateCostume) me.SwapToAlternateCostume();
                Plugin.Log.LogInfo("LOOKCHECK put on the alternate costume alt=" + me.IsUsingAlternateCostume);
            }
            if (float.TryParse(System.Environment.GetEnvironmentVariable("GO_FIRE_AT"), out var at) && Me(System.Environment.GetEnvironmentVariable("GO_FIRE_WHO") ?? "guest")
                && !Game.SceneObjects.InBreach && !GameManager.Instance.IsLoadingLevel)
            {
                if (!fireOn && t >= at) { fireOn = true; me.healthHaver.IsVulnerable = false; me.IsOnFire = true; Plugin.Log.LogInfo("LOOKCHECK set myself on fire"); }
                if (fireOn && !fireOff && t >= at + 6f) { fireOff = true; me.IsOnFire = false; Plugin.Log.LogInfo("LOOKCHECK put my fire out"); }
                // Keep it burning for the 6 s (the game puts out a fire that is not fed by flames on the floor).
                if (fireOn && !fireOff && !me.IsOnFire) me.IsOnFire = true;
            }
            if (Session.NetSession.IsHost && !stoneDone && float.TryParse(System.Environment.GetEnvironmentVariable("GO_STONE_AT"), out var sat) && t >= sat
                && !Game.SceneObjects.InBreach && !GameManager.Instance.IsLoadingLevel && Game.PlayerSlots.ForSlot(1))
            {
                stoneDone = true;
                Replication.LookSync.Instance.TestStone(Game.PlayerSlots.ForSlot(1).CenterPosition + new Vector2(0f, 5f));
                Plugin.Log.LogInfo("STONETEST sent");
            }
            if (!customDone && float.TryParse(System.Environment.GetEnvironmentVariable("GO_CUSTOM_AT"), out var cat) && t >= cat
                && Me(System.Environment.GetEnvironmentVariable("GO_CUSTOM_WHO") ?? "host") && !Game.SceneObjects.InBreach && !GameManager.Instance.IsLoadingLevel)
            {
                customDone = true;
                string name = System.Environment.GetEnvironmentVariable("GO_CUSTOM_NAME") ?? "tt_sewer";
                Plugin.Log.LogInfo("CUSTOMTEST going to " + name);
                GameManager.Instance.LoadCustomLevel(name);
            }
            try { PropTest(t, me); } catch (System.Exception e) { Plugin.Log.LogInfo("PROPTEST error " + e.Message); }
            try { ReticleTest(t, me); } catch (System.Exception e) { Plugin.Log.LogInfo("RETICLETEST error " + e.Message); }
            try { GunTest(t, me); } catch (System.Exception e) { Plugin.Log.LogInfo("GUNTEST error " + e.Message); }
            if (t < nextLog) return;
            nextLog = t + 2f;
            var sb = new System.Text.StringBuilder("LOOKCHECK");
            foreach (var p in GameManager.Instance.AllPlayers)
                if (p) sb.Append($" [{(p == me ? "me" : "copy")} slot={Game.PlayerSlots.SlotOf(p)} {p.name.Replace("(Clone)", "")} alt={p.IsUsingAlternateCostume} fire={p.IsOnFire}]");
            Plugin.Log.LogInfo(sb.ToString());
        }
    }
}
