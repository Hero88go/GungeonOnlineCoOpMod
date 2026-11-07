using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Text;
using GungeonOnline.Net;
using UnityEngine;
using UnityEngine.Networking;

namespace GungeonOnline.Session
{
    /// <summary>
    /// Gets a friend past the host's router or firewall. Many routers drop the first packet from someone the host
    /// has never talked to, so the join never arrives. The joining game tells the relay "I am at A, trying to reach
    /// host H". The host's game asks the relay every couple of seconds who is knocking and sends a packet to each,
    /// which opens its router toward that friend, and the friend's next try gets through. Only the two public
    /// addresses go to the relay, and it forgets them after 30 seconds.
    /// </summary>
    internal static class Knock
    {
        const string RelayUrl = "https://gungeon-report-relay.firescribe-share-worker.workers.dev/knock";
        public static int Posted, Polls, Heard, Failures;

        /// <summary>The relay, or null when off. Tests only use it when GO_KNOCK_URL names one.</summary>
        static string Url
        {
            get
            {
                var u = Environment.GetEnvironmentVariable("GO_KNOCK_URL");
                if (u == "off") return null;
                if (!string.IsNullOrEmpty(u)) return u;
                return Dev.DevEnv.IsHarness ? null : RelayUrl;
            }
        }

        static string token;
        /// <summary>Same on every launch (a restarted host keeps reading its own knocks), and not the id other players see.</summary>
        static string Token
        {
            get
            {
                if (token != null) return token;
                using (var md5 = System.Security.Cryptography.MD5.Create())
                    token = BitConverter.ToString(md5.ComputeHash(Encoding.UTF8.GetBytes("knock:" + NetConfig.PlayerId.Value))).Replace("-", "").ToLowerInvariant();
                return token;
            }
        }

        static float Clock => Time.realtimeSinceStartup;

        static NetNode knocking; // the join that already has a knock loop running

        /// <summary>Joining: knock until we are in, the join gives up, or the session ends.</summary>
        public static IEnumerator Joiner(NetSession s, NetNode node, IPEndPoint host)
        {
            string url = Url;
            if (url == null || knocking == node) yield break;
            knocking = node;
            try
            {
                foreach (var step in JoinerLoop(s, node, host, url)) yield return step;
            }
            finally { if (knocking == node) knocking = null; }
        }

        static IEnumerable JoinerLoop(NetSession s, NetNode node, IPEndPoint host, string url)
        {
            bool told = false, failed = false;
            float giveUp = Clock + 10f; // no public address after this long: nothing to tell the relay
            while (s.Node == node && node.Connecting)
            {
                var me = s.PublicEndPoint;
                if (me == null)
                {
                    if (Clock > giveUp || (s.Stun != null && s.Stun.Failed)) { Plugin.Log.LogInfo("Knock: our public address is unknown, cannot knock"); yield break; }
                    yield return new WaitForSecondsRealtime(0.25f);
                    continue;
                }
                using (var req = new UnityWebRequest(url, "POST"))
                {
                    req.uploadHandler = new UploadHandlerRaw(Encoding.ASCII.GetBytes(host + " " + me)) { contentType = "text/plain" };
                    req.downloadHandler = new DownloadHandlerBuffer();
                    req.SetRequestHeader("Content-Type", "text/plain");
                    req.timeout = 5;
                    yield return req.SendWebRequest();
                    if (!req.isNetworkError && !req.isHttpError)
                    {
                        Posted++;
                        if (!told) { told = true; Plugin.Log.LogInfo("Knock: asked the host's game to open a path to us"); }
                        // The host's game tells the relay its Steam id when it has one: if the direct path never opens,
                        // a joiner on Steam connects through Steam instead (see NetSession.Rejected).
                        foreach (var line in (req.downloadHandler.text ?? "").Split('\n'))
                            if (line.StartsWith("steam:") && ulong.TryParse(line.Substring(6).Trim(), out var id) && id != 0 && s.KnockSteamId != id)
                            {
                                s.KnockSteamId = id;
                                Plugin.Log.LogInfo("Knock: the host is on Steam too" + (s.SteamReady ? "; Steam is the way in if the direct path stays shut" : ""));
                            }
                    }
                    else if (!failed)
                    {
                        failed = true; Failures++;
                        Plugin.Log.LogInfo("Knock: relay not reachable (" + req.responseCode + " " + req.error + ")");
                    }
                }
                yield return new WaitForSecondsRealtime(3f);
            }
        }

        /// <summary>Hosting: while there is room, ask who is knocking and open a path to each of them.</summary>
        public static IEnumerator Host(NetSession s, NetNode node)
        {
            string url = Url;
            if (url == null) yield break;
            var opened = new Dictionary<string, float>();
            float fastUntil = Clock + 300f;
            int peers = 0;
            bool failed = false;
            while (s.Node == node)
            {
                if (node.Connections.Count != peers) { peers = node.Connections.Count; fastUntil = Clock + 300f; }
                var me = s.PublicEndPoint;
                if (me == null || node.Connections.Count + 1 >= node.MaxPlayers) { yield return new WaitForSecondsRealtime(1f); continue; }
                string steam = "";
                if (s.SteamReady) try { steam = "&steam=" + Steamworks.SteamUser.GetSteamID().m_SteamID; } catch { }
                using (var req = UnityWebRequest.Get(url + "?host=" + me + "&token=" + Token + steam))
                {
                    req.timeout = 5;
                    yield return req.SendWebRequest();
                    if (s.Node != node) yield break;
                    if (!req.isNetworkError && !req.isHttpError)
                    {
                        Polls++;
                        foreach (var line in (req.downloadHandler.text ?? "").Split('\n'))
                        {
                            if (!InviteCode.TryParse(line.Trim(), out var ep)) continue;
                            string key = ep.ToString();
                            if (opened.TryGetValue(key, out var at) && Clock - at < 6f) continue;
                            bool inAlready = false;
                            foreach (var c in node.Connections) if (c.Remote != null && c.Remote.ToString() == key) inAlready = true;
                            if (inAlready) continue; // their last knocks linger at the relay for a few seconds
                            opened[key] = Clock;
                            Heard++;
                            s.OpenPathTo(ep);
                            Plugin.Log.LogInfo("Knock: a friend at " + ep + " is trying to join, opening a path");
                        }
                    }
                    else if (!failed)
                    {
                        failed = true; Failures++;
                        Plugin.Log.LogInfo("Knock: relay not reachable (" + req.responseCode + " " + req.error + ")");
                    }
                }
                yield return new WaitForSecondsRealtime(Clock < fastUntil ? 2f : 4f);
            }
        }
    }
}
