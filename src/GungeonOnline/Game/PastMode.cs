using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Game
{
    /// <summary>
    /// A character's Past online. The Past is that character's story: the game's Past scripts call that character
    /// "player 1" and treat anyone else as the co-op partner, and they hold on to the Past's own soldiers and bosses.
    /// Online, "player 1" is a different person on each screen and a guest's own Past enemies used to be swapped for
    /// the host's copies, so each screen told a different story and the guest's script lost its characters.
    ///
    /// In a Past: every screen runs the Past itself (no enemy, bullet or boss-effect copies from the host), the Past's
    /// scripts see its owner (the player the clock was aimed at) as player 1 on every screen and the others as the
    /// partner, and when the boss falls on the owner's screen it falls on every screen, so everyone reaches the ending.
    /// </summary>
    internal static class PastMode
    {
        /// <summary>The player whose Past this is (network slot), or -1.</summary>
        public static int OwnerSlot = -1;
        public static int BossDownsSent, BossDownsApplied;
        static PlayerController lastShot; // the player the Ark's clock was last aimed at, on this screen

        /// <summary>A character's Past is loading or loaded (set from the Past flags as the load starts). The Past's
        /// scripts pick their hero and its enemies appear while the level is still loading, so Past mode must already
        /// be on then: before, a guest also received the host's copies of the Past's enemies during the load (two of
        /// each boss, the copy's intro failing every frame), and a guest-owned Past picked the wrong hero.</summary>
        public static bool PastLevel;

        public static bool Active =>
            Session.NetSession.Active && GameManager.HasInstance && !SceneObjects.InBreach
            && (PastLevel || GameManager.Instance.CurrentLevelOverrideState == GameManager.LevelOverrideState.CHARACTER_PAST);

        public static PlayerController Owner => OwnerSlot >= 0 ? PlayerSlots.ForSlot(OwnerSlot) : null;

        static readonly string[] ScriptTypes =
        {
            "ConvictPastController", "PilotPastController", "GuidePastController", "PastLabMarineController",
            "RobotPastController", "BulletPastRoomController", "PastCameraUtility",
        };

        [ThreadStatic] static int depth;
        /// <summary>Above zero: the real players, never the Past's stand-ins (PlayerSlots.Local uses it).</summary>
        [ThreadStatic] public static int Bypass;

        public static void Install(Harmony h)
        {
            var enter = new HarmonyMethod(typeof(PastMode), nameof(Enter));
            var leave = new HarmonyMethod(typeof(PastMode), nameof(Leave));
            int n = 0;
            foreach (var name in ScriptTypes)
            {
                var t = AccessTools.TypeByName(name);
                if (t == null) { Plugin.Log.LogWarning("PastMode: no " + name); continue; }
                foreach (var m in t.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (m.IsAbstract || m.ContainsGenericParameters || m.GetMethodBody() == null) continue;
                    h.Patch(m, prefix: enter, finalizer: leave); n++;
                }
                // Coroutines: their bodies run in compiler-made classes nested in the script.
                foreach (var nt in t.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                {
                    var mn = AccessTools.Method(nt, "MoveNext");
                    if (mn == null || mn.DeclaringType != nt || mn.GetMethodBody() == null) continue;
                    h.Patch(mn, prefix: enter, finalizer: leave); n++;
                }
            }
            h.Patch(AccessTools.PropertyGetter(typeof(GameManager), nameof(GameManager.PrimaryPlayer)), postfix: new HarmonyMethod(typeof(PastMode), nameof(PrimaryPostfix)));
            h.Patch(AccessTools.PropertyGetter(typeof(GameManager), nameof(GameManager.SecondaryPlayer)), postfix: new HarmonyMethod(typeof(PastMode), nameof(SecondaryPostfix)));
            h.Patch(AccessTools.Method(typeof(HealthHaver), nameof(HealthHaver.Die)), postfix: new HarmonyMethod(typeof(PastMode), nameof(DiePostfix)));
            var follow = AccessTools.Method(typeof(CompanionFollowPlayerBehavior), "Update");
            if (follow != null) h.Patch(follow, finalizer: new HarmonyMethod(typeof(PastMode), nameof(FollowFinalizer)));
            h.Patch(AccessTools.Method(typeof(Dungeonator.Dungeon), "GeneratePlayerIfNecessary"),
                prefix: new HarmonyMethod(typeof(PastMode), nameof(GeneratePrefix)), postfix: new HarmonyMethod(typeof(PastMode), nameof(GeneratePostfix)));
            h.Patch(AccessTools.Method(typeof(TimeTubeCreditsController), nameof(TimeTubeCreditsController.HandleTimeTubeCredits)),
                prefix: new HarmonyMethod(typeof(PastMode), nameof(CreditsPrefix)));
            var check = AccessTools.Method(typeof(ArkController), "CheckPlayerTarget");
            if (check != null) h.Patch(check, postfix: new HarmonyMethod(typeof(PastMode), nameof(ShotPostfix)));
            Plugin.Log.LogInfo($"PastMode: {n} Past script methods scoped");
        }

        static void Enter() { if (Session.NetSession.Active) depth++; }
        static Exception Leave(Exception __exception) { if (depth > 0) depth--; return __exception; }

        static bool Scoped => depth > 0 && Bypass == 0 && OwnerSlot >= 0 && Active;

        static void PrimaryPostfix(ref PlayerController __result)
        {
            if (!Scoped) return;
            Bypass++;
            try { var o = Owner; if (o) __result = o; }
            finally { Bypass--; }
        }

        static void SecondaryPostfix(ref PlayerController __result)
        {
            if (!Scoped) return;
            Bypass++;
            try
            {
                var o = Owner;
                if (!o) return;
                var me = PlayerSlots.Local;
                if (me && me != o) { __result = me; return; }
                foreach (var p in GameManager.Instance.AllPlayers)
                    if (p && p != o) { __result = p; return; }
            }
            finally { Bypass--; }
        }

        static void ShotPostfix(PlayerController target, bool __result) { if (__result && target) lastShot = target; }

        /// <summary>Host, as a Past loads: whose Past it is. The player the clock was aimed at here, else the first
        /// player who is that Past's character (a guest who used the Ark asks the host by the Past's name).</summary>
        public static int PickOwner(string custom)
        {
            if (lastShot && PlayerSlots.SlotOf(lastShot) >= 0) { int s = PlayerSlots.SlotOf(lastShot); lastShot = null; return s; }
            PlayableCharacters want;
            switch (custom)
            {
                case "fs_convict": want = PlayableCharacters.Convict; break;
                case "fs_pilot": want = PlayableCharacters.Pilot; break;
                case "fs_guide": want = PlayableCharacters.Guide; break;
                case "fs_soldier": want = PlayableCharacters.Soldier; break;
                case "fs_robot": want = PlayableCharacters.Robot; break;
                case "fs_bullet": want = PlayableCharacters.Bullet; break;
                case "tt_bullethell": want = PlayableCharacters.Gunslinger; break;
                default: return -1;
            }
            var me = PlayerSlots.Local;
            if (me && me.characterIdentity == want) return PlayerSlots.SlotOf(me);
            foreach (var p in GameManager.Instance.AllPlayers)
                if (p && p.characterIdentity == want) return PlayerSlots.SlotOf(p);
            return me ? PlayerSlots.SlotOf(me) : -1;
        }

        /// <summary>The boss of the Past fell on the owner's screen: tell everyone.</summary>
        static void DiePostfix(HealthHaver __instance)
        {
            if (!Active || __instance == null || !__instance.IsBoss) return;
            if (PlayerSlots.Local == null || PlayerSlots.SlotOf(PlayerSlots.Local) != OwnerSlot) return;
            BossDownsSent++;
            Plugin.Log.LogInfo("PAST boss down on the owner's screen: " + __instance.name);
            RunSync.SendPastBossDown(__instance.name);
        }

        /// <summary>Another screen: the owner beat the Past's boss. Ours falls too, so this screen reaches the ending.</summary>
        public static void ApplyBossDown(string boss)
        {
            if (!Active) return;
            BossDownsApplied++;
            int n = 0;
            // Only that boss: a Past can have more than one (the Bullet's second boss comes after the first one falls).
            bool named = false;
            foreach (var hh in StaticReferenceManager.AllHealthHavers) if (hh && hh.IsBoss && hh.IsAlive && hh.name == boss) named = true;
            foreach (var hh in new List<HealthHaver>(StaticReferenceManager.AllHealthHavers))
                if (hh && hh.IsBoss && hh.IsAlive && (!named || hh.name == boss))
                {
                    hh.minimumHealth = 0f;
                    hh.ApplyDamage(1000000f, Vector2.zero, "past", CoreDamageTypes.SpecialBossDamage, DamageCategory.Unstoppable, true, null, true);
                    n++;
                }
            Plugin.Log.LogInfo($"PAST the owner beat the boss: {n} boss(es) here fall too");
        }

        static bool creditsHere;   // this screen's credits have started in this Past
        static bool creditsForced; // ...because the owner's had (this screen's own script must not start them again)
        public static int EndingsSent, EndingsApplied;

        /// <summary>The owner's credits start: tell everyone. On another screen whose credits we already started, the
        /// Past script's own call is skipped (its story may have lagged behind or stalled; the owner's screen decides).</summary>
        static bool CreditsPrefix(ref System.Collections.IEnumerator __result)
        {
            if (!Active) return true;
            if (creditsForced && !forcing) { __result = Nothing(); Plugin.Log.LogInfo("PAST the script's own credits skipped (already playing the owner's)"); return false; }
            creditsHere = true;
            var me = PlayerSlots.Local;
            if (me && PlayerSlots.SlotOf(me) == OwnerSlot && !forcing) { EndingsSent++; Plugin.Log.LogInfo("PAST the owner's ending starts"); RunSync.SendPastEnding(); }
            return true;
        }

        static System.Collections.IEnumerator Nothing() { yield break; }
        static bool forcing;

        /// <summary>Another screen: the owner's Past ended. If this screen's story has not got there itself, play the
        /// same ending here: the time-tube credits, then the victory page.</summary>
        public static void ApplyEnding()
        {
            if (!Active || creditsHere) return;
            EndingsApplied++;
            creditsForced = true;
            Plugin.Log.LogInfo("PAST the owner's Past ended: ending here too");
            GameManager.Instance.StartCoroutine(EndingHere());
        }

        static System.Collections.IEnumerator EndingHere()
        {
            // Real time: a boss intro still running on this screen stops game time, and a game-time wait never ended.
            yield return new WaitForSecondsRealtime(1.5f); // this screen's own story may be a moment behind and get there itself
            var me = PlayerSlots.Local;
            if (!Active || !me || creditsHere) yield break;
            foreach (var cam in UnityEngine.Object.FindObjectsOfType<BossKillCam>()) if (cam) cam.ForceCancelSequence();
            // A boss intro left running here (this screen's story was behind the owner's) would hold time and input.
            foreach (var intro in UnityEngine.Object.FindObjectsOfType<GenericIntroDoer>()) if (intro) intro.StopAllCoroutines();
            GameManager.IsBossIntro = false;
            GameManager.Instance.PreventPausing = false;
            BraveTime.ClearAllMultipliers();
            foreach (var p in GameManager.Instance.AllPlayers) if (p) p.ClearInputOverride("BossIntro");
            foreach (var p in GameManager.Instance.AllPlayers) if (p) p.CurrentInputState = PlayerInputState.NoInput;
            var ttcc = new TimeTubeCreditsController();
            Pixelator.Instance.FadeToColor(0.15f, Color.white, true, 0.15f);
            ttcc.ClearDebris();
            forcing = true;
            System.Collections.IEnumerator credits;
            try { credits = ttcc.HandleTimeTubeCredits(me.sprite.WorldCenter, false, null, -1); }
            finally { forcing = false; }
            creditsHere = true;
            yield return GameManager.Instance.StartCoroutine(credits);
            AmmonomiconController.Instance.OpenAmmonomicon(true, true);
        }

        // ---------------- The Pilot's Past: everyone flies ----------------
        // That level replaces the players with ships as it loads: player 1 becomes the Pilot's ship, player 2 a co-op
        // ship. Online, "player 1" on each screen is that screen's own player, so a friend's game made them the Pilot's
        // ship, the game added a second player nobody steered (driven by a friend's input on top of their copy), and
        // friends' copies came back on foot. Now the owner flies the Pilot's ship and everyone else a co-op ship, on
        // every screen, and the game's extra player is removed.

        /// <summary>Set while the Pilot's Past is loaded or loading (from the Past flags).</summary>
        public static bool ShipPast;
        const string OwnerShip = "PlayerRogueShip", FriendShip = "PlayerCoopShip";

        /// <summary>The prefab a friend's copy uses: in the Pilot's Past, a ship.</summary>
        public static string PrefabFor(int slot, string prefab) => InShipPast ? (slot == OwnerSlot ? OwnerShip : FriendShip) : prefab;

        /// <summary>In (or loading into) the Pilot's Past; never once back in the Breach.</summary>
        public static bool InShipPast => ShipPast && GameManager.HasInstance && !SceneObjects.InBreach
            && (GameManager.Instance.IsLoadingLevel || GameManager.Instance.CurrentLevelOverrideState == GameManager.LevelOverrideState.CHARACTER_PAST);

        static void GeneratePrefix(Dungeonator.Dungeon __instance)
        {
            if (!Session.NetSession.Active || !ShipPast || !__instance.ForceRegenerationOfCharacters) return;
            if (Session.NetSession.LocalSlot == OwnerSlot) return; // the level's own player is the Pilot's ship
            var ship = BraveResources.Load(FriendShip) as GameObject;
            if (ship) { GameManager.PlayerPrefabForNewGame = ship; Plugin.Log.LogInfo("PAST Pilot: this player flies a co-op ship"); }
        }

        static void GeneratePostfix(Dungeonator.Dungeon __instance)
        {
            if (!Session.NetSession.Active || !ShipPast || !__instance.ForceRegenerationOfCharacters) return;
            GameManager.Instance.StartCoroutine(RemoveExtraPlayers());
        }

        /// <summary>After the load: any player that is neither this machine's own nor a friend's copy was made by the
        /// game as its own "player 2"; online the friends are player 2 and up.</summary>
        static System.Collections.IEnumerator RemoveExtraPlayers()
        {
            while (GameManager.HasInstance && GameManager.Instance.IsLoadingLevel) yield return null;
            yield return null;
            var gm = GameManager.Instance;
            var me = PlayerSlots.Local;
            foreach (var p in gm.AllPlayers)
            {
                if (!p || p == me || PlayerSlots.SlotOf(p) >= 0) continue;
                Plugin.Log.LogInfo("PAST Pilot: removed the game's own extra player " + p.name);
                p.SetInputOverride("gungeon online extra");
                UnityEngine.Object.Destroy(p.gameObject);
                if (gm.SecondaryPlayer == p) gm.SecondaryPlayer = null;
            }
            yield return null;
            PlayerSlots.EnsureSecondary();
            gm.RefreshAllPlayers();
        }

        static readonly AccessTools.FieldRef<BehaviorBase, AIActor> behaviorActor = AccessTools.FieldRefAccess<BehaviorBase, AIActor>("m_aiActor");
        static float lastFollowFix;

        /// <summary>A companion that ended up outside the map (a friend's pet as a Past places its owner) made the
        /// game's follow code fail every frame. Online, it is put back beside its owner instead.</summary>
        static Exception FollowFinalizer(CompanionFollowPlayerBehavior __instance, Exception __exception)
        {
            if (__exception == null || !Session.NetSession.Active) return __exception;
            try
            {
                var a = behaviorActor(__instance);
                var owner = a ? a.CompanionOwner : null;
                if (a && owner && a.specRigidbody)
                {
                    a.transform.position = owner.transform.position;
                    a.specRigidbody.Reinitialize();
                    a.ClearPath();
                }
                if (UnityEngine.Time.realtimeSinceStartup - lastFollowFix > 10f) { lastFollowFix = UnityEngine.Time.realtimeSinceStartup; Plugin.Log.LogInfo("Companion was off the map: back beside its owner (" + __exception.GetType().Name + ")"); }
            }
            catch { }
            return null;
        }

        public static void Reset() { PastLevel = false; OwnerSlot = -1; creditsHere = false; creditsForced = false; ShipPast = false; }
    }
}
