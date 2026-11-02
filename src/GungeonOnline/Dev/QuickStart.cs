using System.Collections;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>
    /// Harness only: skips the intro and main menu and picks a character in the Breach automatically.
    /// GO_CHAR selects the character prefab (default PlayerRogue = the Pilot).
    /// </summary>
    internal static class QuickStart
    {
        public static void Prepare()
        {
            if (!DevEnv.IsHarness) return;
            Foyer.DoIntroSequence = false;
            Foyer.DoMainMenu = false;
        }

        public static IEnumerator Run()
        {
            if (!DevEnv.IsHarness) yield break;
            string want = System.Environment.GetEnvironmentVariable("GO_CHAR");
            if (string.IsNullOrEmpty(want)) want = "PlayerRogue";
            float giveUp = Time.realtimeSinceStartup + 300f; // big modpacks take minutes to load the Breach
            FoyerCharacterSelectFlag pick = null;
            while (Time.realtimeSinceStartup < giveUp)
            {
                // Do not wait on IsSelectingCharacter: at high frame rates it can flip before this loop sees it.
                if (Game.SceneObjects.InBreach && GameManager.HasInstance && GameManager.Instance.PrimaryPlayer == null)
                {
                    FoyerCharacterSelectFlag carrier = null;
                    foreach (var f in Object.FindObjectsOfType<FoyerCharacterSelectFlag>())
                        if (!f.IsCoopCharacter && !f.IsAlternateCostume && f.CharacterPrefabPath != null)
                        {
                            bool unlocked = f.PrerequisitesFulfilled();
                            bool wanted = f.CharacterPrefabPath.EndsWith(want);
                            if (wanted && (unlocked || Time.realtimeSinceStartup > giveUp - 280f)) pick = f; // mods can lock every statue on a blank test save
                            else if (!wanted && unlocked && carrier == null) carrier = f;
                        }
                    // A blank test save has not unlocked the Robot, Bullet, Paradox or Gunslinger and the game removes
                    // their statues, but the tests play every character: an unlocked statue carries the locked one's path.
                    if (pick == null && carrier != null)
                    {
                        carrier.CharacterPrefabPath = want;
                        pick = carrier;
                        Plugin.Log.LogInfo("QuickStart: " + want + " is locked on this save, picking it through another statue");
                    }
                    if (pick != null) break;
                }
                yield return null;
            }
            if (pick == null)
            {
                var seen = new System.Text.StringBuilder();
                foreach (var f in Object.FindObjectsOfType<FoyerCharacterSelectFlag>()) seen.Append(" [" + f.CharacterPrefabPath + " coop=" + f.IsCoopCharacter + " alt=" + f.IsAlternateCostume + "]");
                Plugin.Log.LogWarning("QuickStart: character flag not found: " + want + " breach=" + Game.SceneObjects.InBreach + " player=" + (GameManager.HasInstance && GameManager.Instance.PrimaryPlayer != null) + " isFoyer=" + (GameManager.HasInstance && GameManager.Instance.IsFoyer) + " foyers=" + Resources.FindObjectsOfTypeAll<Foyer>().Length + " scene=" + UnityEngine.SceneManagement.SceneManager.GetActiveScene().name + " dungeon=" + (GameManager.HasInstance && GameManager.Instance.Dungeon ? GameManager.Instance.Dungeon.name + "/" + GameManager.Instance.Dungeon.tileIndices.tilesetId : "none") + " flags:" + seen);
                yield break;
            }
            Plugin.Log.LogInfo("QuickStart: found flag, selecting shortly");
            yield return new WaitForSeconds(1f);
            if (GameManager.Instance.PrimaryPlayer != null) { Plugin.Log.LogInfo("QuickStart: player already chosen"); ScenarioRunner.Begin(); yield break; }
            // The statue borrowed for a locked character can be one the game removes a moment later: borrow a live one.
            if (!pick || !pick.gameObject.activeInHierarchy)
            {
                string path = want;
                pick = null;
                foreach (var f in Object.FindObjectsOfType<FoyerCharacterSelectFlag>())
                    if (f && f.gameObject.activeInHierarchy && !f.IsCoopCharacter && !f.IsAlternateCostume) { pick = f; break; }
                if (pick == null) { Plugin.Log.LogWarning("QuickStart: no statue left to pick " + path); yield break; }
                pick.CharacterPrefabPath = path;
                Plugin.Log.LogInfo("QuickStart: statue went away, picking " + path + " through another");
            }
            Plugin.Log.LogInfo("QuickStart: selecting " + pick.CharacterPrefabPath);
            Game.SceneObjects.Breach.StartCoroutine(Game.SceneObjects.Breach.OnSelectedCharacter(0.05f, pick));
            while (GameManager.Instance.PrimaryPlayer == null) yield return null;
            Plugin.Log.LogInfo("QuickStart: player spawned at " + GameManager.Instance.PrimaryPlayer.CenterPosition);
            ScenarioRunner.Begin();
        }
    }
}
