using System;
using System.IO;
using BepInEx.Logging;

namespace GungeonOnline.Session
{
    /// <summary>
    /// Keeps a log of each game launch in BepInEx/GungeonOnline/logs (the last 5), so a report sent after restarting
    /// the game still carries the session that went wrong. BepInEx rewrites its own LogOutput.log at every start,
    /// before any mod loads, so copying that file would always be too late. Everything BepInEx logs from here on
    /// (this mod, other mods, the game's errors) is written here as it happens.
    /// </summary>
    internal sealed class SessionLogs : ILogListener
    {
        const int Keep = 5;
        static SessionLogs instance;
        readonly object gate = new object();
        StreamWriter writer;

        public static string Dir => Path.Combine(Path.Combine(BepInEx.Paths.BepInExRootPath, "GungeonOnline"), "logs");
        public static string CurrentFile { get; private set; }

        public static void Start()
        {
            if (instance != null) return;
            try
            {
                Directory.CreateDirectory(Dir);
                var old = Directory.GetFiles(Dir, "launch-*.log");
                Array.Sort(old, StringComparer.Ordinal); // names sort by time
                for (int i = 0; i < old.Length - (Keep - 1); i++) try { File.Delete(old[i]); } catch { }
                CurrentFile = Path.Combine(Dir, "launch-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log");
                instance = new SessionLogs();
                instance.writer = new StreamWriter(new FileStream(CurrentFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                instance.writer.WriteLine("Gungeon Online " + Plugin.Version + " launch " + DateTime.Now.ToString("s"));
                Logger.Listeners.Add(instance);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Launch log unavailable: " + e.Message); instance = null; }
        }

        /// <summary>The newest launch log before this one, or null.</summary>
        public static string PreviousFile
        {
            get
            {
                try
                {
                    var files = Directory.GetFiles(Dir, "launch-*.log");
                    Array.Sort(files, StringComparer.Ordinal);
                    for (int i = files.Length - 1; i >= 0; i--)
                        if (!string.Equals(files[i], CurrentFile, StringComparison.OrdinalIgnoreCase)) return files[i];
                }
                catch { }
                return null;
            }
        }

        public void LogEvent(object sender, LogEventArgs e)
        {
            lock (gate)
            {
                try { writer?.WriteLine(DateTime.Now.ToString("HH:mm:ss") + " " + e.ToString()); }
                catch { }
            }
        }

        public void Dispose()
        {
            lock (gate) { try { writer?.Dispose(); } catch { } writer = null; }
        }
    }
}
