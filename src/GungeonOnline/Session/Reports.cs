using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>
    /// "Send report": one text file with everything needed to look into a problem afterwards (mod log, desync log,
    /// settings, connection numbers), saved in the game folder (Gungeon Online Reports) and sent to the developer. Anything that points at a
    /// person (invite codes, IP addresses, the Windows user name, Steam ids) is removed first.
    /// </summary>
    internal static class Reports
    {
        const string RelayUrl = "https://gungeon-report-relay.firescribe-share-worker.workers.dev/report";

        public static string Scrub(string text)
        {
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\b[0-9A-Z]{4}-[0-9A-Z]{4}-[0-9A-Z]{4}\b", "XXXX-XXXX-XXXX");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\b(?:\d{1,3}\.){3}\d{1,3}(?::\d+)?\b", "<ip>");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"([A-Za-z]:[\\/]+Users[\\/]+)[^\\/\r\n]+", "$1<user>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\b7656119\d{10}\b", "<steamid>");
            return text;
        }

        /// <summary>Where reports are kept: a folder of their own in the game folder (the one with EtG.exe).</summary>
        public static string Folder => Path.Combine(BepInEx.Paths.GameRootPath, "Gungeon Online Reports");

        /// <summary>Reports used to be saved to the Desktop: move any found there into the reports folder.</summary>
        public static void MoveOldDesktopReports()
        {
            if (Environment.GetEnvironmentVariable("GO_RUN_DIR") != null) return;
            try
            {
                string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                if (string.IsNullOrEmpty(desktop) || !Directory.Exists(desktop)) return;
                var old = Directory.GetFiles(desktop, "GungeonOnline-report-*.txt");
                if (old.Length == 0) return;
                Directory.CreateDirectory(Folder);
                int moved = 0;
                foreach (var f in old)
                {
                    string to = Path.Combine(Folder, Path.GetFileName(f));
                    if (File.Exists(to)) continue;
                    File.Move(f, to);
                    moved++;
                }
                Plugin.Log.LogInfo($"Reports: moved {moved} old reports from the Desktop to {Folder}");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Reports: could not move old Desktop reports: " + e.Message); }
        }

        /// <summary>Open the reports folder in Explorer.</summary>
        public static void OpenFolder()
        {
            try { Directory.CreateDirectory(Folder); System.Diagnostics.Process.Start("explorer.exe", "\"" + Folder + "\""); }
            catch (Exception e) { Plugin.Log.LogWarning("Reports: could not open the folder: " + e.Message); }
        }

        public static void Save()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Enter The Gungeon Online Co-Op Mod report " + DateTime.Now.ToString("s"));
                sb.AppendLine("Mod " + Plugin.Version + "  game " + NetSession.GameHash);
                sb.AppendLine("Mod version: " + Plugin.Version);
                sb.AppendLine("Role: " + (NetSession.Active ? (NetSession.IsHost ? "host" : "guest") : "offline"));
                var s = NetSession.Instance;
                sb.AppendLine("Session: " + (s != null ? s.Status : "none") + (NetSession.Active ? (NetSession.IsHost ? " (host)" : " (guest)") : ""));
                if (s != null && s.Node != null)
                    foreach (var c in s.Node.Connections)
                        sb.AppendLine($"  peer {c.Slot}: ping {c.RttMs:F0} ms, jitter {c.RttVarMs:F0} ms, loss {c.LossPercent:F1}%, resends {c.Resends}");
                if (s != null)
                    sb.AppendLine("Network: steam=" + (s.SteamReady ? "yes" : "no") + " upnp=" + (s.Upnp != null ? s.Upnp.Status.ToString() : "-")
                        + " public address=" + (s.Stun == null ? "-" : s.Stun.Result != null ? "known" : s.Stun.Failed ? "failed" : "pending")
                        + " join attempts seen=" + s.JoinAttemptsSeen
                        + " knock posted=" + Knock.Posted + " polls=" + Knock.Polls + " heard=" + Knock.Heard + " relay failures=" + Knock.Failures);
                var ck = Replication.Checksum.Instance;
                if (ck != null) sb.AppendLine($"Sync checks {ck.Checks}, desyncs repaired {ck.Mismatches}, last: {ck.LastMismatch}");
                sb.AppendLine();
                Append(sb, "Settings", Path.Combine(BepInEx.Paths.ConfigPath, "gungeononline.cfg"), 200);
                Append(sb, "Desync log", Path.Combine(Path.Combine(BepInEx.Paths.BepInExRootPath, "GungeonOnline"), "desync.log"), 300);
                Append(sb, "Metrics (last 10 minutes)", Metrics.FilePath, 120);
                Append(sb, "Mod log (last lines)", Path.Combine(BepInEx.Paths.BepInExRootPath, "LogOutput.log"), 1500);
                // Tests keep their copy in the run folder.
                string dir = Environment.GetEnvironmentVariable("GO_RUN_DIR") ?? Folder;
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "GungeonOnline-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                string text = Scrub(sb.ToString());
                File.WriteAllText(file, text);
                Toasts.Show("Sending report...");
                Plugin.Instance.StartCoroutine(Send(text));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("Report: " + e.Message);
                Toasts.Show("Could not save the report", 4f, true);
            }
        }

        static System.Collections.IEnumerator Send(string text)
        {
            // Tests point this somewhere else or turn it off (GO_REPORT_URL=off).
            string url = Environment.GetEnvironmentVariable("GO_REPORT_URL") ?? RelayUrl;
            if (url == "off") { Toasts.Show("Report saved in the game folder, Gungeon Online Reports"); yield break; }
            var body = Encoding.UTF8.GetBytes(text);
            using (var req = new UnityEngine.Networking.UnityWebRequest(url, "POST"))
            {
                req.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(body) { contentType = "text/plain; charset=utf-8" };
                req.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                req.SetRequestHeader("Content-Type", "text/plain; charset=utf-8");
                req.timeout = 20;
                yield return req.SendWebRequest();
                string answer = req.downloadHandler != null ? req.downloadHandler.text ?? "" : "";
                if (!req.isNetworkError && !req.isHttpError && answer.StartsWith("sent "))
                {
                    string id = answer.Substring(5).Trim();
                    Plugin.Log.LogInfo("Report sent: " + id);
                    Toasts.Show("Report sent (" + id + "). A copy is in the game folder, Gungeon Online Reports.", 8f);
                }
                else if (req.responseCode == 429)
                    Toasts.Show("Report saved in the game folder (one send every 2 minutes)", 6f, true);
                else
                {
                    Plugin.Log.LogInfo("Report send failed: " + req.responseCode + " " + req.error);
                    Toasts.Show("Could not send the report. It is saved in the game folder, Gungeon Online Reports.", 6f, true);
                }
            }
        }

        static void Append(StringBuilder sb, string title, string path, int lastLines)
        {
            sb.AppendLine("==== " + title);
            try
            {
                if (!File.Exists(path)) { sb.AppendLine("(none)"); return; }
                string[] lines;
                // The log is open for writing by BepInEx; read it shared.
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var r = new StreamReader(fs))
                    lines = r.ReadToEnd().Split('\n');
                int from = Math.Max(0, lines.Length - lastLines);
                for (int i = from; i < lines.Length; i++) sb.AppendLine(lines[i].TrimEnd('\r'));
            }
            catch (Exception e) { sb.AppendLine("(could not read: " + e.Message + ")"); }
            sb.AppendLine();
        }
    }
}
