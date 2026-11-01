using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace GungeonOnline.Dev
{
    /// <summary>
    /// Harness GO_FREEZE_PROBE=1: finds where the game hangs. Every method of the floor builder (and of this mod)
    /// notes itself in a ring when called; a watcher thread writes the ring to freeze.txt once the main thread has
    /// not finished a frame for 12 s, and again every 10 s while it stays stuck.
    /// </summary>
    internal sealed class FreezeProbe : MonoBehaviour
    {
        static readonly MethodBase[] ring = new MethodBase[4096];
        static int pos;
        static long frames;
        static string dir;

        public static void Install(Harmony h)
        {
            if (System.Environment.GetEnvironmentVariable("GO_FREEZE_PROBE") != "1") return;
            dir = System.Environment.GetEnvironmentVariable("GO_RUN_DIR") ?? ".";
            mainThreadId = GetCurrentThreadId();
            var enter = new HarmonyMethod(typeof(FreezeProbe), nameof(Enter));
            int n = 0, failed = 0;
            var asms = new List<Assembly> { typeof(Dungeonator.RoomHandler).Assembly, typeof(FreezeProbe).Assembly };
            foreach (var asm in asms)
                foreach (var t in asm.GetTypes())
                {
                    if (t == typeof(FreezeProbe) || t.IsGenericTypeDefinition || t.IsInterface) continue;
                    bool want = asm == typeof(FreezeProbe).Assembly
                        ? t.Namespace != null && (t.Namespace.EndsWith(".Game") || t.Namespace.EndsWith(".Replication") || t.Namespace.EndsWith(".Session"))
                        : t.Namespace == "Dungeonator" || t.Name.Contains("Dungeon") || t.Name.Contains("Flow") || t.Name.Contains("Room") || t.Name.Contains("Generat") || t.Name.Contains("Loop");
                    if (!want) continue;
                    foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                    {
                        if (m.IsAbstract || m.IsGenericMethodDefinition || m.Name.EndsWith("Prefix") || m.Name.EndsWith("Postfix") || m.Name.EndsWith("Finalizer") || m.Name == "Leave" || m.ContainsGenericParameters || m.GetMethodBody() == null || m.Name.StartsWith("get_") && m.GetMethodBody().GetILAsByteArray().Length < 16) continue;
                        try { h.Patch(m, prefix: enter); n++; } catch { failed++; }
                    }
                    // Coroutine bodies (floor building runs in steps).
                    foreach (var nt in t.GetNestedTypes(BindingFlags.NonPublic | BindingFlags.Public))
                    {
                        if (nt.IsGenericTypeDefinition) continue;
                        var mn = nt.GetMethod("MoveNext", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                        if (mn == null || mn.GetMethodBody() == null) continue;
                        try { h.Patch(mn, prefix: enter); n++; } catch { failed++; }
                    }
                }
            Plugin.Log.LogInfo($"FreezeProbe: watching {n} methods ({failed} could not be)");
            var go = new GameObject("FreezeProbe");
            DontDestroyOnLoad(go);
            go.AddComponent<FreezeProbe>();
            new Thread(Watch) { IsBackground = true, Name = "FreezeProbe" }.Start();
        }

        static uint mainThreadId;

        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern System.IntPtr OpenThread(uint access, bool inherit, uint id);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint SuspendThread(System.IntPtr h);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern uint ResumeThread(System.IntPtr h);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool GetThreadContext(System.IntPtr h, System.IntPtr ctx);
        [System.Runtime.InteropServices.DllImport("kernel32.dll")] static extern bool CloseHandle(System.IntPtr h);
        [System.Runtime.InteropServices.DllImport("mono.dll")] static extern System.IntPtr mono_pmip(System.IntPtr ip);

        /// <summary>The main thread's managed methods: stop it, read its stack, and ask the runtime which method each
        /// return address belongs to.</summary>
        static string MainStack()
        {
            var h = OpenThread(0x0002 | 0x0008 | 0x0040, false, mainThreadId); // suspend/resume, get context, query
            if (h == System.IntPtr.Zero) return "no thread handle";
            var raw = System.Runtime.InteropServices.Marshal.AllocHGlobal(1232 + 16);
            var sb = new System.Text.StringBuilder();
            try
            {
                var ctx = new System.IntPtr((raw.ToInt64() + 15) & ~15L);
                for (int i = 0; i < 1232; i += 8) System.Runtime.InteropServices.Marshal.WriteInt64(ctx, i, 0);
                System.Runtime.InteropServices.Marshal.WriteInt32(ctx, 0x30, 0x00100001); // CONTEXT_CONTROL
                SuspendThread(h);
                var names = new List<string>();
                try
                {
                    if (!GetThreadContext(h, ctx)) return "no context";
                    long rip = System.Runtime.InteropServices.Marshal.ReadInt64(ctx, 0xF8);
                    long rsp = System.Runtime.InteropServices.Marshal.ReadInt64(ctx, 0x98);
                    var top = mono_pmip(new System.IntPtr(rip));
                    names.Add("rip " + (top != System.IntPtr.Zero ? System.Runtime.InteropServices.Marshal.PtrToStringAnsi(top) : rip.ToString("X")));
                    for (long a = rsp; a < rsp + 256 * 1024 && names.Count < 60; a += 8)
                    {
                        long v = System.Runtime.InteropServices.Marshal.ReadInt64(new System.IntPtr(a));
                        if (v < 0x10000) continue;
                        var n = mono_pmip(new System.IntPtr(v));
                        if (n != System.IntPtr.Zero) names.Add(System.Runtime.InteropServices.Marshal.PtrToStringAnsi(n));
                    }
                }
                finally { ResumeThread(h); }
                foreach (var n in names) sb.Append("  ").Append(n).Append("\n");
            }
            catch (System.Exception e) { sb.Append("stack error " + e.Message); }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(raw); CloseHandle(h); }
            return sb.ToString();
        }

        static void Enter(MethodBase __originalMethod)
        {
            ring[pos & (ring.Length - 1)] = __originalMethod;
            pos++;
        }

        void Update() { Interlocked.Increment(ref frames); }

        static void Watch()
        {
            long last = -1;
            int still = 0, dumps = 0;
            while (true)
            {
                Thread.Sleep(2000);
                long f = Interlocked.Read(ref frames);
                if (f != last) { last = f; still = 0; continue; }
                still += 2;
                if (still < 12 || (still - 12) % 10 != 0 || dumps >= 6) continue;
                dumps++;
                try
                {
                    int p = pos;
                    var counts = new Dictionary<string, int>();
                    var tail = new System.Text.StringBuilder();
                    for (int i = 0; i < ring.Length; i++)
                    {
                        var m = ring[(p - 1 - i) & (ring.Length - 1)];
                        if (m == null) continue;
                        string k = (m.DeclaringType != null ? m.DeclaringType.FullName : "?") + "." + m.Name;
                        counts[k] = counts.TryGetValue(k, out var c) ? c + 1 : 1;
                        if (i < 120) tail.Append("  ").Append(k).Append('\n');
                    }
                    var top = new List<KeyValuePair<string, int>>(counts);
                    top.Sort((a, b) => b.Value.CompareTo(a.Value));
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"FREEZE stuck {still}s calls={p}\nmost called in the last {ring.Length}:\n");
                    for (int i = 0; i < top.Count && i < 40; i++) sb.Append($"  {top[i].Value} {top[i].Key}\n");
                    sb.Append("main thread stack (managed, newest first):\n");
                    for (int k = 0; k < 3; k++) { sb.Append(MainStack()).Append("--\n"); Thread.Sleep(300); }
                    sb.Append("newest first:\n").Append(tail);
                    // A loop that calls nothing watched shows as no new calls between dumps.
                    System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "freeze.txt"), sb.ToString() + "\n");
                }
                catch (System.Exception e) { System.IO.File.AppendAllText(System.IO.Path.Combine(dir, "freeze.txt"), "probe error " + e + "\n"); }
            }
        }
    }
}
