// Gungeon Online one-click installer. Built by tools/package.py with the .NET Framework compiler that ships with
// Windows (C# 5), so players need nothing extra. The mod files (BepInEx + the plugin) are embedded as payload.zip.
// Finds Enter the Gungeon on Steam (every library), GOG and Epic; installs or removes with one button; asks Windows
// for permission only when the game sits in a protected folder. Same logic as Install.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("Gungeon Online Setup")]
[assembly: System.Reflection.AssemblyProduct("Enter The Gungeon Online Co-Op Mod")]

static class Setup
{
    const string PluginRel = @"BepInEx\plugins\GungeonOnline";
    static string Version = "?";

    [STAThread]
    static int Main(string[] args)
    {
        using (var s = typeof(Setup).Assembly.GetManifestResourceStream("version.txt"))
            if (s != null) Version = new StreamReader(s).ReadToEnd().Trim();
        // Elevated re-run (protected folder): do the work straight away, report by exit code.
        if (args.Length >= 2 && (args[0] == "--install" || args[0] == "--uninstall"))
        {
            try { foreach (var g in args.Skip(1)) { if (args[0] == "--install") Install(g); else Uninstall(g); } return 0; }
            catch { return 1; }
        }
        if (args.Length == 1 && args[0] == "--list") { foreach (var g in Find()) Console.WriteLine(g); return 0; }
        Application.EnableVisualStyles();
        Application.Run(new SetupForm());
        return 0;
    }

    // ---------------- Finding the game ----------------

    public static List<string> Find()
    {
        var found = new List<string>();
        // Tests: GO_SETUP_DIRS="dir;dir" lists only those folders, so a test never touches a real install.
        var only = Environment.GetEnvironmentVariable("GO_SETUP_DIRS");
        if (!string.IsNullOrEmpty(only)) { foreach (var d in only.Split(';')) AddIfGame(found, d); return found; }
        var roots = new List<string>();
        foreach (var k in new[] { @"HKEY_CURRENT_USER\Software\Valve\Steam", @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam" })
            foreach (var v in new[] { "SteamPath", "InstallPath" })
            {
                var p = Registry.GetValue(k, v, null) as string;
                if (!string.IsNullOrEmpty(p)) roots.Add(p.Replace('/', '\\'));
            }
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var libs = new List<string> { root };
            try
            {
                var vdf = Path.Combine(root, @"steamapps\libraryfolders.vdf");
                if (File.Exists(vdf))
                    foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                        libs.Add(m.Groups[1].Value.Replace(@"\\", @"\"));
            }
            catch { }
            foreach (var l in libs) AddIfGame(found, Path.Combine(l, @"steamapps\common\Enter the Gungeon"));
        }
        foreach (var k in new[] { @"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\GOG.com\Games\1456912569", @"HKEY_LOCAL_MACHINE\SOFTWARE\GOG.com\Games\1456912569" })
        {
            var p = Registry.GetValue(k, "path", null) as string;
            if (!string.IsNullOrEmpty(p)) AddIfGame(found, p);
        }
        try
        {
            var man = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (Directory.Exists(man))
                foreach (var f in Directory.GetFiles(man, "*.item"))
                {
                    var t = File.ReadAllText(f);
                    if (!t.Contains("Gungeon")) continue;
                    var m = Regex.Match(t, "\"InstallLocation\"\\s*:\\s*\"([^\"]+)\"");
                    if (m.Success) AddIfGame(found, m.Groups[1].Value.Replace(@"\\", @"\"));
                }
        }
        catch { }
        return found;
    }

    static void AddIfGame(List<string> found, string dir)
    {
        try
        {
            if (!File.Exists(Path.Combine(dir, "EtG.exe"))) return;
            var full = Path.GetFullPath(dir).TrimEnd('\\');
            if (!found.Any(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase))) found.Add(full);
        }
        catch { } // a library on a drive that is not connected
    }

    public static string InstalledVersion(string game)
    {
        var dll = Path.Combine(Path.Combine(game, PluginRel), "GungeonOnline.dll");
        if (!File.Exists(dll)) return null;
        try { var v = System.Reflection.AssemblyName.GetAssemblyName(dll).Version; return v.Major == 1 && v.Minor == 0 && v.Build == 0 ? "older version" : v.ToString(3); } catch { return "installed"; }
    }

    // ---------------- Installing ----------------

    public static bool Writable(string dir)
    {
        try { var t = Path.Combine(dir, ".go-write-test"); File.WriteAllText(t, "x"); File.Delete(t); return true; }
        catch { return false; }
    }

    public static void Install(string game)
    {
        using (var s = typeof(Setup).Assembly.GetManifestResourceStream("payload.zip"))
        using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
        {
            foreach (var e in zip.Entries)
            {
                if (string.IsNullOrEmpty(e.Name)) continue; // folder entry
                var target = Path.GetFullPath(Path.Combine(game, e.FullName.Replace('/', '\\')));
                if (!target.StartsWith(Path.GetFullPath(game), StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                // A DLL left from an in-game update may still be named .old; the plugin itself is replaced in place.
                if (File.Exists(target)) File.SetAttributes(target, FileAttributes.Normal);
                using (var src = e.Open())
                using (var dst = File.Create(target)) src.CopyTo(dst);
            }
        }
    }

    public static void Uninstall(string game)
    {
        var dir = Path.Combine(game, PluginRel);
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    public static bool GameRunning()
    {
        return Process.GetProcessesByName("EtG").Length > 0;
    }

    /// <summary>Runs this exe again as administrator for the given folders. True when it worked.</summary>
    public static bool Elevated(string verb, IEnumerable<string> games)
    {
        var args = verb + " " + string.Join(" ", games.Select(g => "\"" + g + "\"").ToArray());
        try
        {
            var p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, args) { UseShellExecute = true, Verb = "runas" });
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch { return false; } // the player said no
    }
}

sealed class SetupForm : Form
{
    readonly CheckedListBox list = new CheckedListBox();
    readonly Label status = new Label();
    readonly Button install = new Button();
    readonly Button remove = new Button();

    public SetupForm()
    {
        Text = "Gungeon Online Setup";
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(560, 330);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

        var title = new Label { Text = "Enter The Gungeon Online Co-Op Mod " + SetupVersion(), Font = new Font("Segoe UI", 13f, FontStyle.Bold), AutoSize = true, Location = new Point(16, 14) };
        var hint = new Label { Text = "Your game folders (Steam, GOG and Epic are found by themselves):", AutoSize = true, Location = new Point(16, 50) };
        list.Location = new Point(16, 76); list.Size = new Size(528, 110); list.CheckOnClick = true; list.IntegralHeight = false; list.HorizontalScrollbar = true;
        var browse = new LinkLabel { Text = "Game not listed? Pick its folder...", AutoSize = true, Location = new Point(16, 192) };
        browse.LinkClicked += (s, e) => Browse();
        status.Location = new Point(16, 218); status.Size = new Size(528, 60);
        install.Text = "Install"; install.Font = new Font("Segoe UI", 11f, FontStyle.Bold); install.Size = new Size(160, 38); install.Location = new Point(384, 280);
        install.Click += (s, e) => Run(false);
        remove.Text = "Uninstall"; remove.Size = new Size(110, 38); remove.Location = new Point(16, 280);
        remove.Click += (s, e) => Run(true);
        Controls.AddRange(new Control[] { title, hint, list, browse, status, install, remove });
        AcceptButton = install;

        foreach (var g in Setup.Find()) list.Items.Add(new Game(g), true);
        if (list.Items.Count == 0) status.Text = "Enter the Gungeon was not found automatically. Pick its folder (the one with EtG.exe).";
        else status.Text = "Close the game first, then press Install. Your saves and settings are kept.";
    }

    static string SetupVersion()
    {
        using (var s = typeof(Setup).Assembly.GetManifestResourceStream("version.txt"))
            return s == null ? "" : new StreamReader(s).ReadToEnd().Trim();
    }

    sealed class Game
    {
        public readonly string Dir;
        public Game(string dir) { Dir = dir; }
        public override string ToString()
        {
            var v = Setup.InstalledVersion(Dir);
            return Dir + (v == null ? "" : "   (mod " + v + " installed)");
        }
    }

    void Browse()
    {
        using (var d = new FolderBrowserDialog { Description = "Select your Enter the Gungeon folder (the one with EtG.exe)" })
        {
            if (d.ShowDialog(this) != DialogResult.OK) return;
            if (!File.Exists(Path.Combine(d.SelectedPath, "EtG.exe"))) { status.Text = "That folder has no EtG.exe. Pick the folder Enter the Gungeon is installed in."; return; }
            list.Items.Add(new Game(Path.GetFullPath(d.SelectedPath).TrimEnd('\\')), true);
            status.Text = "Added. Press Install.";
        }
    }

    void Run(bool uninstall)
    {
        var games = list.CheckedItems.Cast<Game>().Select(g => g.Dir).ToList();
        if (games.Count == 0) { status.Text = "Tick at least one game folder (or pick one with the link above)."; return; }
        if (Setup.GameRunning()) { status.Text = "Enter the Gungeon is running. Close it, then press " + (uninstall ? "Uninstall" : "Install") + " again."; return; }
        UseWaitCursor = true; install.Enabled = remove.Enabled = false;
        try
        {
            var open = games.Where(Setup.Writable).ToList();
            var locked = games.Where(g => !open.Contains(g)).ToList();
            foreach (var g in open) { if (uninstall) Setup.Uninstall(g); else Setup.Install(g); }
            if (locked.Count > 0 && !Setup.Elevated(uninstall ? "--uninstall" : "--install", locked))
            {
                status.Text = "Windows did not allow changes in " + string.Join(", ", locked.ToArray()) + ". Nothing was changed there.";
                return;
            }
            for (int i = 0; i < list.Items.Count; i++) list.Items[i] = list.Items[i]; // refresh "(mod x installed)"
            status.Text = uninstall
                ? "Removed Gungeon Online. The game is back to normal."
                : "Done! Start the game, pause, and pick PLAY ONLINE (or hold Back + Start, or press F8). Updates are offered in game.";
            install.Text = uninstall ? "Install" : "Reinstall";
        }
        catch (Exception e) { status.Text = "Something went wrong: " + e.Message; }
        finally { UseWaitCursor = false; install.Enabled = remove.Enabled = true; }
    }
}
