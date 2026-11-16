using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

namespace GungeonOnline.Session
{
    /// <summary>
    /// Once per launch, asks GitHub for the latest release. If it is newer, the player is told once and the online menu
    /// offers "Update to x.y.z". Nothing is ever downloaded or changed unless the player picks Update now: they can keep
    /// playing this version, pick Not now, or Skip this version. Updating never happens during an online session.
    ///
    /// Update now reads the release's GungeonOnline-update.json (version, files, SHA-256, size), downloads each file,
    /// checks the hash, then swaps it in beside the running copy (the running DLL is renamed to .old, which Windows
    /// allows; it is deleted next launch). The new version starts the next time the game opens; Restart game does that.
    /// Installs from a mod manager (r2modman) only get the notice: the manager owns those files.
    /// Off entirely with Settings, Help and advanced, Check for updates.
    /// </summary>
    internal static class UpdateCheck
    {
        const string Api = "https://api.github.com/repos/Hero88go/GungeonOnlineCoOpMod/releases/latest";
        public const string Page = "https://github.com/Hero88go/GungeonOnlineCoOpMod/releases/latest";
        const string PluginFolder = "BepInEx/plugins/GungeonOnline/";

        public enum State { None, Available, Downloading, Ready, Failed }
        public static State Status = State.None;
        public static string Latest = "";
        public static string Message = "";
        public static float Progress;
        static string manifestUrl;

        class Manifest { public string version; public FileEntry[] files; public bool needsInstaller; }
        class FileEntry { public string path; public string url; public string sha256; public long size; }

        /// <summary>Reads GungeonOnline-update.json (written by package.py): {"version", "needsInstaller", "files": [{path, url, sha256, size}]}.</summary>
        static Manifest ParseManifest(string json)
        {
            string Str(string src, string key) { var m = System.Text.RegularExpressions.Regex.Match(src, "\"" + key + @"""\s*:\s*""([^""]*)"""); return m.Success ? m.Groups[1].Value : null; }
            var man = new Manifest { version = Str(json, "version"), needsInstaller = System.Text.RegularExpressions.Regex.IsMatch(json, @"""needsInstaller""\s*:\s*true") };
            var list = new List<FileEntry>();
            int at = json.IndexOf("\"files\"", StringComparison.Ordinal);
            if (at >= 0)
                foreach (System.Text.RegularExpressions.Match o in System.Text.RegularExpressions.Regex.Matches(json.Substring(at), @"\{[^{}]*\}"))
                {
                    var size = System.Text.RegularExpressions.Regex.Match(o.Value, @"""size""\s*:\s*([0-9]+)");
                    list.Add(new FileEntry { path = Str(o.Value, "path"), url = Str(o.Value, "url"), sha256 = Str(o.Value, "sha256"), size = size.Success ? long.Parse(size.Groups[1].Value) : -1 });
                }
            man.files = list.ToArray();
            return man;
        }

        /// <summary>The running mod DLL.</summary>
        static string DllPath => typeof(Plugin).Assembly.Location;

        /// <summary>True when a mod manager (r2modman) installed the mod: its files live outside the game folder.</summary>
        public static bool Managed
        {
            get
            {
                try
                {
                    string expected = Path.GetFullPath(Path.Combine(BepInEx.Paths.GameRootPath, PluginFolder.Replace('/', Path.DirectorySeparatorChar)));
                    return !Path.GetFullPath(DllPath).StartsWith(expected, StringComparison.OrdinalIgnoreCase);
                }
                catch { return true; }
            }
        }

        /// <summary>Offered in the menu: a newer version is out and this install can update itself.</summary>
        public static bool Offer => Status == State.Available && !Managed && manifestUrl != null;

        public static IEnumerator Run()
        {
            CleanOld();
            // Tests never reach GitHub unless they point the check somewhere (GO_UPDATE_URL, may be a file:// URL).
            string url = Environment.GetEnvironmentVariable("GO_UPDATE_URL");
            if (string.IsNullOrEmpty(url)) { if (Dev.DevEnv.IsHarness) yield break; url = Api; }
            if (!NetConfig.CheckUpdates.Value) yield break;
            yield return new WaitForSeconds(Dev.DevEnv.IsHarness ? 3f : 16f); // after the welcome toast
            using (var req = UnityWebRequest.Get(url))
            {
                req.SetRequestHeader("User-Agent", "GungeonOnlineCoOpMod");
                req.SetRequestHeader("Accept", "application/vnd.github+json");
                req.timeout = 10;
                yield return req.SendWebRequest();
                if (req.isNetworkError || req.isHttpError) { Plugin.Log.LogInfo("Update check: no answer (" + req.error + ")"); yield break; }
                string body = req.downloadHandler.text;
                var m = System.Text.RegularExpressions.Regex.Match(body, @"""tag_name""\s*:\s*""v?([0-9][0-9.]*)");
                if (!m.Success) yield break;
                Latest = m.Groups[1].Value;
                var a = System.Text.RegularExpressions.Regex.Match(body, @"""browser_download_url""\s*:\s*""([^""]*GungeonOnline-update\.json)""");
                manifestUrl = a.Success ? a.Groups[1].Value : null;
                Plugin.Log.LogInfo("Update check: latest " + Latest + ", this is " + Plugin.Version + (manifestUrl != null ? ", in-game update available" : "") + (Managed ? ", mod manager install" : ""));
                if (!Newer(Latest, Plugin.Version)) yield break;
                Status = State.Available;
                if (NetConfig.SkipVersion.Value == Latest) { Plugin.Log.LogInfo("Update check: " + Latest + " skipped by the player"); yield break; }
                if (Managed) Toasts.Show("Gungeon Online " + Latest + " is out. Update it in your mod manager.", 10f);
                else if (manifestUrl != null) Toasts.Show("Gungeon Online " + Latest + " is out. Pause, Play Online to update (optional).", 10f);
                else Toasts.Show("Update " + Latest + " available: github.com/Hero88go/GungeonOnlineCoOpMod", 12f);
                if (Environment.GetEnvironmentVariable("GO_UPDATE_AUTO") == "1" && Offer) Plugin.Instance.StartCoroutine(Install());
            }
        }

        public static void Skip()
        {
            NetConfig.SkipVersion.Value = Latest;
            Plugin.Log.LogInfo("Update " + Latest + " skipped");
        }

        /// <summary>Downloads, checks and swaps in the new files. Every failure leaves the current install untouched.</summary>
        public static IEnumerator Install()
        {
            if (Status == State.Downloading || NetSession.Active) yield break;
            // A release without GungeonOnline-update.json (every one before 1.2.9) can only be installed with Setup.exe.
            if (string.IsNullOrEmpty(manifestUrl)) { Fail("This version can't be installed from the game. Pick Open download page."); yield break; }
            Status = State.Downloading; Progress = 0f; Message = "Downloading " + Latest + "...";
            Manifest man = null;
            using (var req = Get(manifestUrl))
            {
                yield return req.SendWebRequest();
                if (req.isNetworkError || req.isHttpError) { Fail("Could not download the update (" + req.error + ")."); yield break; }
                try { man = ParseManifest(req.downloadHandler.text); } catch { }
            }
            if (man == null || man.files == null || man.files.Length == 0 || man.version != Latest) { Fail("The update list did not look right."); yield break; }
            if (man.needsInstaller) { Fail("This update needs the installer. Pick Open download page."); yield break; }

            string root = Path.GetFullPath(BepInEx.Paths.GameRootPath);
            var staged = new List<KeyValuePair<string, string>>(); // target, staged copy
            for (int i = 0; i < man.files.Length; i++)
            {
                var f = man.files[i];
                // Only files inside the mod's own folder can be replaced in game; anything else needs the installer.
                if (f == null || string.IsNullOrEmpty(f.path) || f.path.Contains("..") || !f.path.StartsWith(PluginFolder, StringComparison.Ordinal))
                { Fail("This update needs the installer. Pick Open download page."); yield break; }
                byte[] data;
                using (var req = Get(f.url))
                {
                    var op = req.SendWebRequest();
                    while (!op.isDone) { Progress = (i + req.downloadProgress) / man.files.Length; yield return null; }
                    if (req.isNetworkError || req.isHttpError) { Fail("Could not download the update (" + req.error + ")."); yield break; }
                    data = req.downloadHandler.data;
                }
                if (data == null || data.LongLength != f.size || !string.Equals(Sha256(data), f.sha256, StringComparison.OrdinalIgnoreCase))
                { Fail("The download did not match the release (damaged or changed), so nothing was installed."); yield break; }
                string target = Path.Combine(root, f.path.Replace('/', Path.DirectorySeparatorChar));
                string temp = Path.Combine(Path.GetTempPath(), "GungeonOnline-" + Latest + "-" + Path.GetFileName(target));
                try { File.WriteAllBytes(temp, data); }
                catch (Exception e) { Fail("Could not save the update: " + e.Message); yield break; }
                staged.Add(new KeyValuePair<string, string>(target, temp));
            }
            Progress = 1f;
            string error = Swap(staged);
            if (error == null)
            {
                Status = State.Ready;
                Message = "Updated to " + Latest + ". It starts the next time the game opens.";
                Plugin.Log.LogInfo("Update " + Latest + " installed (" + staged.Count + " file(s))");
            }
            else Fail(error);
        }

        /// <summary>Moves the checked files in place. The running DLL is renamed to .old first (Windows allows renaming a
        /// file in use, not overwriting it). A protected game folder asks Windows for permission once.</summary>
        static string Swap(List<KeyValuePair<string, string>> staged)
        {
            try
            {
                foreach (var kv in staged)
                {
                    string old = kv.Key + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    if (File.Exists(kv.Key)) File.Move(kv.Key, old);
                    File.Copy(kv.Value, kv.Key);
                    File.Delete(kv.Value);
                }
                return null;
            }
            catch (UnauthorizedAccessException) { return Elevated(staged); }
            catch (Exception e)
            {
                // Put back whatever was moved, so the current version still starts.
                foreach (var kv in staged)
                    try { if (!File.Exists(kv.Key) && File.Exists(kv.Key + ".old")) File.Move(kv.Key + ".old", kv.Key); } catch { }
                Plugin.Log.LogWarning("Update swap failed: " + e);
                return "Could not replace the mod files: " + e.Message;
            }
        }

        static string Elevated(List<KeyValuePair<string, string>> staged)
        {
            if (Dev.DevEnv.IsHarness) return "The game folder is protected (test: no permission prompt).";
            var ps = new System.Text.StringBuilder("$ErrorActionPreference='Stop';");
            foreach (var kv in staged)
                ps.AppendFormat("$t='{0}';$o=$t+'.old';if(Test-Path -LiteralPath $o){{Remove-Item -LiteralPath $o -Force}};if(Test-Path -LiteralPath $t){{Move-Item -LiteralPath $t $o}};Copy-Item -LiteralPath '{1}' $t;",
                    kv.Key.Replace("'", "''"), kv.Value.Replace("'", "''"));
            try
            {
                var psi = new ProcessStartInfo("powershell.exe", "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -Command \"" + ps.ToString().Replace("\"", "\\\"") + "\"")
                { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                var p = Process.Start(psi);
                p.WaitForExit(30000);
                foreach (var kv in staged) if (!File.Exists(kv.Key) || Sha256(File.ReadAllBytes(kv.Key)) != Sha256(File.ReadAllBytes(kv.Value))) return "Windows did not allow the update. Pick Open download page and run the installer.";
                foreach (var kv in staged) try { File.Delete(kv.Value); } catch { }
                return null;
            }
            catch (Exception e) { return "Windows did not allow the update (" + e.Message + "). Pick Open download page and run the installer."; }
        }

        /// <summary>Starts the game again a moment after this copy closes, so the new version loads.</summary>
        public static void Restart()
        {
            string exe = Path.Combine(BepInEx.Paths.GameRootPath, "EtG.exe");
            Plugin.Log.LogInfo("Update: restarting the game");
            if (Dev.DevEnv.IsHarness) return; // tests never open windows
            try
            {
                var psi = new ProcessStartInfo("cmd.exe", "/c ping -n 3 127.0.0.1 >nul & start \"\" \"" + exe + "\"")
                { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = BepInEx.Paths.GameRootPath };
                Process.Start(psi);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Restart failed: " + e.Message); }
            Application.Quit();
        }

        public static void OpenPage()
        {
            Plugin.Log.LogInfo("Update: opening the download page");
            if (!Dev.DevEnv.IsHarness) Application.OpenURL(Page); // tests never open a browser
        }

        static void Fail(string why)
        {
            Status = State.Failed;
            Message = why;
            Plugin.Log.LogWarning("Update " + Latest + " failed: " + why);
        }

        /// <summary>The previous version's DLL (renamed by an update) is no longer in use once the game restarts.</summary>
        static void CleanOld()
        {
            try { var old = DllPath + ".old"; if (File.Exists(old)) { File.Delete(old); Plugin.Log.LogInfo("Update: removed the previous version's file"); } }
            catch { }
        }

        static UnityWebRequest Get(string url)
        {
            var req = UnityWebRequest.Get(url);
            req.SetRequestHeader("User-Agent", "GungeonOnlineCoOpMod");
            req.timeout = 60;
            return req;
        }

        static string Sha256(byte[] data)
        {
            using (var h = new System.Security.Cryptography.SHA256Managed())
                return BitConverter.ToString(h.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        public static bool Newer(string a, string b)
        {
            var x = a.Split('.'); var y = b.Split('.');
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int p = i < x.Length && int.TryParse(x[i], out var t) ? t : 0;
                int q = i < y.Length && int.TryParse(y[i], out var u) ? u : 0;
                if (p != q) return p > q;
            }
            return false;
        }
    }
}
