using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;

namespace GungeonOnline.Net
{
    /// <summary>
    /// The host's own addresses a friend can type as "ip:port" instead of the invite code: VPNs (Radmin VPN, Hamachi,
    /// Tailscale, ZeroTier) first, then the home network. The invite code only carries the public address, so
    /// players on a VPN had no way to see which address to give their friend.
    /// </summary>
    public static class LocalAddresses
    {
        public struct Entry
        {
            public string Ip;
            public string Kind; // "Radmin VPN", "Tailscale", "same network", ...
            public bool Vpn;
        }

        static volatile Entry[] cached;
        static int started;

        /// <summary>Found addresses, best first; empty until the background lookup finishes (well under a second).</summary>
        public static Entry[] Current => cached ?? new Entry[0];

        /// <summary>Looks up the addresses once per Host on a worker thread (adapter queries can take a moment).</summary>
        public static void Refresh()
        {
            if (Interlocked.Exchange(ref started, 1) == 1) return;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { cached = Find(); }
                catch (Exception e) { Plugin.Log.LogWarning("Local addresses: " + e.Message); cached = new Entry[0]; }
                finally { started = 0; }
                Plugin.Log.LogInfo("Local addresses: " + cached.Length + " (" + Describe(cached) + ")" + (Dev.DevEnv.IsHarness ? " " + HostLine(0).Replace('\n', '|') : ""));
            });
        }

        /// <summary>Kinds only, never the addresses, so logs and reports stay anonymous.</summary>
        static string Describe(Entry[] list)
        {
            var parts = new List<string>();
            foreach (var e in list) parts.Add(e.Kind.Replace(' ', '-'));
            return parts.Count == 0 ? "none" : string.Join(",", parts.ToArray());
        }

        /// <summary>For reports: e.g. "Radmin-VPN,same-network", or "-" if this game never hosted.</summary>
        public static string Kinds() => cached == null ? "-" : Describe(cached);

        static Entry[] Find()
        {
            // Harness: GO_FAKE_LAN="26.1.2.3=Radmin VPN;192.168.1.20=Ethernet" shows the menu for a setup the test PC lacks.
            var fake = Dev.DevEnv.IsHarness ? Environment.GetEnvironmentVariable("GO_FAKE_LAN") : null;
            var found = new List<Entry>();
            var seen = new HashSet<string>();
            if (!string.IsNullOrEmpty(fake))
            {
                foreach (var item in fake.Split(';'))
                {
                    var kv = item.Split('=');
                    if (kv.Length == 2 && IPAddress.TryParse(kv[0], out var ip)) Consider(found, seen, ip, kv[1]);
                }
            }
            else
            {
                try
                {
                    foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                        foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                            Consider(found, seen, ua.Address, nic.Name + " " + nic.Description);
                    }
                }
                catch (Exception e) { Plugin.Log.LogInfo("Adapter list unavailable (" + e.GetType().Name + "), using host name lookup"); }
                if (found.Count == 0)
                    foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName())) Consider(found, seen, ip, "");
            }
            // VPNs first, otherwise adapter order (List.Sort is not stable, so build the order by hand).
            var ordered = new List<Entry>();
            foreach (var e in found) if (e.Vpn) ordered.Add(e);
            foreach (var e in found) if (!e.Vpn) ordered.Add(e);
            if (ordered.Count > 3) ordered.RemoveRange(3, ordered.Count - 3);
            return ordered.ToArray();
        }

        static void Consider(List<Entry> found, HashSet<string> seen, IPAddress ip, string adapter)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(ip)) return;
            var b = ip.GetAddressBytes();
            if (b[0] == 169 && b[1] == 254) return; // no network (link-local)
            string name = (adapter ?? "").ToLowerInvariant();
            // Virtual adapters for virtual machines and WSL: a friend can never reach these.
            if (name.Contains("vethernet") || name.Contains("hyper-v") || name.Contains("virtualbox") || name.Contains("vmware") || name.Contains("wsl")) return;
            string kind = Kind(b, name, out bool vpn);
            if (kind == null || !seen.Add(ip.ToString())) return;
            found.Add(new Entry { Ip = ip.ToString(), Kind = kind, Vpn = vpn });
        }

        static string Kind(byte[] b, string name, out bool vpn)
        {
            vpn = true;
            if (name.Contains("radmin")) return "Radmin VPN";
            if (name.Contains("hamachi")) return "Hamachi";
            if (name.Contains("tailscale")) return "Tailscale";
            if (name.Contains("zerotier")) return "ZeroTier";
            if (name.Contains("wireguard") || name.Contains("openvpn") || name.Contains("tap-windows")) return "VPN";
            if (b[0] == 26) return "Radmin VPN";
            if (b[0] == 25) return "Hamachi";
            if (b[0] == 100 && b[1] >= 64 && b[1] < 128) return "Tailscale";
            vpn = false;
            bool lan = b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168);
            return lan ? "same network" : null; // a public address is what the invite code already carries
        }

        /// <summary>Host screen text: "Or join by IP (VPN or same network):" then one "26.1.2.3:47624 (Radmin VPN)" line each.</summary>
        public static string HostLine(int port)
        {
            var list = Current;
            if (list.Length == 0) return "";
            var parts = new List<string> { "Or join by IP (VPN or same network):" };
            foreach (var e in list) parts.Add(e.Ip + ":" + port + " (" + e.Kind + ")");
            return string.Join("\n", parts.ToArray());
        }
    }
}
