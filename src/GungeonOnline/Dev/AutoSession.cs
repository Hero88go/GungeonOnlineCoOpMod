using GungeonOnline.Session;

namespace GungeonOnline.Dev
{
    /// <summary>Harness only: GO_AUTO=host or GO_AUTO=join:CODE starts a session at boot.</summary>
    internal static class AutoSession
    {
        static System.Collections.IEnumerator Later(System.Action a)
        {
            yield return new UnityEngine.WaitForSeconds(3f);
            a();
        }

        /// <summary>Harness: after a host crash, resume the saved run and leave the Breach once the friend is back.</summary>
        static System.Collections.IEnumerator Resume()
        {
            while (!Game.SceneObjects.InBreach || GameManager.Instance.PrimaryPlayer == null) yield return null;
            yield return new UnityEngine.WaitForSeconds(2f);
            // GO_AUTO=lostrun: resume the newest lost run, as the menu's "Lost run" row does.
            if (Dev.DevEnv.Auto == "lostrun")
            {
                var lost = RunBackup.EndedRuns(1);
                UnityEngine.Debug.Log("Harness: lost runs offered=" + lost.Count + " menu=" + RunBackup.AnySlotUsable);
                if (lost.Count > 0) RunBackup.ResumeFile(lost[0]);
            }
            else RunBackup.ResumeAsHost();
            float giveUp = UnityEngine.Time.realtimeSinceStartup + 60f;
            while (Game.PlayerSlots.Remotes.GetEnumerator().MoveNext() == false && UnityEngine.Time.realtimeSinceStartup < giveUp) yield return null;
            yield return new UnityEngine.WaitForSeconds(3f);
            UnityEngine.Debug.Log("Harness: resuming run, leaving the Breach");
            GameManager.Instance.DelayedLoadNextLevel(0.5f);
            if (Game.SceneObjects.Breach) Game.SceneObjects.Breach.OnDepartedFoyer();
        }

        /// <summary>Harness: wait for the host instance to write its Steam id, then join it over Steam.</summary>
        static System.Collections.IEnumerator SteamJoin(string file)
        {
            float giveUp = UnityEngine.Time.realtimeSinceStartup + 90f;
            while (UnityEngine.Time.realtimeSinceStartup < giveUp && !System.IO.File.Exists(file)) yield return null;
            yield return new UnityEngine.WaitForSeconds(2f);
            ulong id;
            if (!System.IO.File.Exists(file) || !ulong.TryParse(System.IO.File.ReadAllText(file).Trim(), out id)) { UnityEngine.Debug.Log("Harness: no Steam host id"); yield break; }
            UnityEngine.Debug.Log("Harness: joining over Steam " + id);
            NetSession.Instance.JoinSteam(new Steamworks.CSteamID(id));
        }

        /// <summary>Harness (GO_JOIN_LATE=seconds): like a real player, pick a character in the Breach first, then host or
        /// join this many seconds later.</summary>
        static System.Collections.IEnumerator Late(float seconds)
        {
            while (!Game.SceneObjects.InBreach || GameManager.Instance.PrimaryPlayer == null) yield return null;
            yield return new UnityEngine.WaitForSeconds(seconds);
            UnityEngine.Debug.Log("Harness: joining late, after picking a character");
            Start(DevEnv.Auto);
        }

        /// <summary>Harness (GO_HOST_IN_RUN=1): the host starts a run alone, offline, and hosts from floor 1; the guest
        /// joins once the host is up there. What a player does who decides to play online mid-run.</summary>
        static System.Collections.IEnumerator HostInRun()
        {
            while (!Game.SceneObjects.InBreach || GameManager.Instance.PrimaryPlayer == null) yield return null;
            yield return new UnityEngine.WaitForSeconds(6f);
            UnityEngine.Debug.Log("Harness: leaving the Breach alone, offline");
            Pixelator.Instance.FadeToBlack(0.5f);
            GameManager.Instance.DelayedLoadNextLevel(0.75f);
            if (Game.SceneObjects.Breach) Game.SceneObjects.Breach.OnDepartedFoyer();
            yield return new UnityEngine.WaitForSeconds(3f);
            while (GameManager.Instance.IsLoadingLevel || !Game.SceneObjects.OnDungeonFloor || GameManager.Instance.PrimaryPlayer == null) yield return null;
            yield return new UnityEngine.WaitForSeconds(8f);
            UnityEngine.Debug.Log("Harness: hosting from inside the run");
            NetSession.Instance.Host();
        }

        /// <summary>Harness (GO_GUEST_IN_RUN=1): the guest is still inside a run of its own (the host saved and quit,
        /// then hosted again from the Breach) and joins from there.</summary>
        static System.Collections.IEnumerator GuestInRun(string code)
        {
            while (!Game.SceneObjects.InBreach || GameManager.Instance.PrimaryPlayer == null) yield return null;
            yield return new UnityEngine.WaitForSeconds(4f);
            UnityEngine.Debug.Log("Harness: guest leaving the Breach alone, offline");
            Pixelator.Instance.FadeToBlack(0.5f);
            GameManager.Instance.DelayedLoadNextLevel(0.75f);
            if (Game.SceneObjects.Breach) Game.SceneObjects.Breach.OnDepartedFoyer();
            yield return new UnityEngine.WaitForSeconds(3f);
            while (GameManager.Instance.IsLoadingLevel || !Game.SceneObjects.OnDungeonFloor || GameManager.Instance.PrimaryPlayer == null) yield return null;
            yield return new UnityEngine.WaitForSeconds(5f);
            UnityEngine.Debug.Log("Harness: guest joining from inside its own run");
            NetSession.Instance.Join(code);
        }

        public static void Begin()
        {
            var a = DevEnv.Auto;
            if (a == null || NetSession.Instance == null) return;
            if (System.Environment.GetEnvironmentVariable("GO_HOST_IN_RUN") == "1")
            {
                if (a == "host") { NetSession.Instance.StartCoroutine(HostInRun()); return; }
                if (a.StartsWith("join:")) { NetSession.Instance.StartCoroutine(Late(45f)); return; }
            }
            if (System.Environment.GetEnvironmentVariable("GO_GUEST_IN_RUN") == "1" && a.StartsWith("join:"))
            {
                NetSession.Instance.StartCoroutine(GuestInRun(a.Substring(5)));
                return;
            }
            var late = System.Environment.GetEnvironmentVariable("GO_JOIN_LATE");
            if (!string.IsNullOrEmpty(late) && (a == "host" || a.StartsWith("join:")))
            {
                float sec = float.Parse(late, System.Globalization.CultureInfo.InvariantCulture);
                // The guest joins a few seconds after the host is up.
                float guestExtra = float.Parse(System.Environment.GetEnvironmentVariable("GO_JOIN_LATE_GUEST") ?? "4", System.Globalization.CultureInfo.InvariantCulture);
                NetSession.Instance.StartCoroutine(Late(a == "host" ? sec : sec + guestExtra));
                return;
            }
            Start(a);
        }

        static void Start(string a)
        {
            if (a == "host") NetSession.Instance.Host();
            else if (a == "rejoin") NetSession.Instance.StartCoroutine(Later(() => NetSession.Instance.RejoinLast()));
            else if (a == "resume" || a == "lostrun") NetSession.Instance.StartCoroutine(Resume());
            else if (a.StartsWith("join:")) NetSession.Instance.Join(a.Substring(5));
            else if (a.StartsWith("steamjoinfile:")) NetSession.Instance.StartCoroutine(SteamJoin(a.Substring(14)));
        }
    }
}
