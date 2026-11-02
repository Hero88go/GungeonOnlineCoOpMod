using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using GungeonOnline.Game;
using GungeonOnline.Net;
using UnityEngine;

namespace GungeonOnline.Session
{
    /// <summary>
    /// Run backups, so a crash or a closed game never ends a run. The host saves at every floor start, every
    /// 2 minutes, and whenever someone drops or leaves: run seed, floor, and for every player their character,
    /// guns with ammo, items, health, armor and blanks, plus the shared wallet and keys. The last 10 are kept in
    /// BepInEx/GungeonOnline/backups (the guest gets a copy too). After a restart the host picks "Resume saved run"
    /// and walks into the Gungeon: the saved floor loads and everyone gets their things back.
    /// </summary>
    internal sealed class RunBackup : MonoBehaviour
    {
        public sealed class PlayerState
        {
            public int Slot; public string Id = "", Name = "", Prefab = "";
            public float Hp, MaxHp, Armor; public int Blanks, Current = -1;
            public List<KeyValuePair<int, int>> Guns = new List<KeyValuePair<int, int>>();
            public List<int> Passives = new List<int>(), Actives = new List<int>();
        }

        public sealed class Backup
        {
            public int Seed, FloorIndex, Money, Keys;
            public bool Ended;
            public string Saved = "";
            public List<PlayerState> Players = new List<PlayerState>();
        }

        const byte MsgBackupCopy = 1;
        public static RunBackup Instance { get; private set; }
        public static Backup PendingResume { get; private set; }
        static readonly HashSet<int> restoredSlots = new HashSet<int>();
        float nextPeriodic;
        float saveAt = -1f;

        static string Dir
        {
            get
            {
                var d = Path.Combine(Path.Combine(BepInEx.Paths.BepInExRootPath, "GungeonOnline"), "backups");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static string Latest => Path.Combine(Dir, "latest.txt");

        void Awake() { Instance = this; }

        void Start()
        {
            NetSession.Instance.On(Channel.Backup, (c, data, d) =>
            {
                if (NetSession.IsHost || data.Length < 1 || data[0] != MsgBackupCopy) return;
                var r = new WireReader(data, 1, data.Length - 1);
                try { File.WriteAllText(Path.Combine(Dir, "from-host-latest.txt"), r.Str()); } catch { }
            });
        }

        // ---------------- Saving ----------------

        public static void OnHostFloorReady()
        {
            if (Instance == null) return;
            // Starting items and restores land a moment after the floor appears; save just after.
            Instance.saveAt = Time.realtimeSinceStartup + 3f;
            if (PendingResume != null) Instance.RestoreHost();
        }

        public static void SaveNow(string why)
        {
            if (Instance == null || !NetSession.Active || !NetSession.IsHost || RunSync.CurrentIndex < 0 || SceneObjects.InBreach) return;
            try
            {
                var text = Serialize(Capture());
                var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.WriteAllText(Path.Combine(Dir, "run-" + stamp + ".txt"), text);
                File.WriteAllText(Latest, text);
                var all = new List<string>(Directory.GetFiles(Dir, "run-*.txt"));
                all.Sort();
                for (int i = 0; i < all.Count - 40; i++) File.Delete(all[i]);
                var w = new WireWriter(text.Length + 16);
                w.U8(MsgBackupCopy); w.Str(text);
                NetSession.Instance.Node.Broadcast((byte)Channel.Backup, w.ToArray(), Delivery.ReliableOrdered);
                Plugin.Log.LogInfo("RunBackup: saved (" + why + ")");
            }
            catch (Exception e) { Plugin.Log.LogError("RunBackup save: " + e.Message); }
        }

        static Backup Capture()
        {
            var gm = GameManager.Instance;
            var b = new Backup { Seed = gm.CurrentRunSeed, FloorIndex = RunSync.CurrentIndex, Saved = DateTime.Now.ToString("s") };
            var local = PlayerSlots.Local;
            if (local && local.carriedConsumables != null) { b.Money = local.carriedConsumables.Currency; b.Keys = local.carriedConsumables.KeyBullets; }
            var lobby = FindObjectOfType<Lobby>();
            if (local) b.Players.Add(Snap(NetSession.LocalSlot, local, NetConfig.PlayerId.Value, NetConfig.PlayerName.Value));
            foreach (var kv in PlayerSlots.Remotes)
            {
                if (!kv.Value) continue;
                string id = "", name = "";
                foreach (var kp in NetSession.Instance.KnownPlayers) if (kp.Value == kv.Key) id = kp.Key;
                if (lobby != null && lobby.Roster.TryGetValue(kv.Key, out var e)) name = e.Name;
                b.Players.Add(Snap(kv.Key, kv.Value, id, name));
            }
            return b;
        }

        static PlayerState Snap(int slot, PlayerController p, string id, string name)
        {
            var s = new PlayerState { Slot = slot, Id = id, Name = name, Prefab = PrefabOf(p) };
            s.Hp = p.healthHaver.GetCurrentHealth(); s.MaxHp = p.healthHaver.GetMaxHealth(); s.Armor = p.healthHaver.Armor;
            s.Blanks = p.Blanks;
            s.Current = p.CurrentGun ? p.CurrentGun.PickupObjectId : -1;
            foreach (var g in p.inventory.AllGuns) if (g) s.Guns.Add(new KeyValuePair<int, int>(g.PickupObjectId, g.CurrentAmmo));
            foreach (var i in p.passiveItems) if (i) s.Passives.Add(i.PickupObjectId);
            foreach (var i in p.activeItems) if (i) s.Actives.Add(i.PickupObjectId);
            return s;
        }

        static string PrefabOf(PlayerController p)
        {
            var n = p.gameObject.name;
            int i = n.IndexOf("(Clone)");
            return i >= 0 ? n.Substring(0, i).Trim() : n;
        }

        static string Serialize(Backup b)
        {
            var c = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append("gungeon-online-backup 1\n");
            sb.Append("saved ").Append(b.Saved).Append('\n');
            sb.Append("seed ").Append(b.Seed).Append('\n');
            sb.Append("floor ").Append(b.FloorIndex).Append('\n');
            sb.Append("money ").Append(b.Money).Append('\n');
            sb.Append("keys ").Append(b.Keys).Append('\n');
            sb.Append("ended ").Append(b.Ended ? 1 : 0).Append('\n');
            foreach (var p in b.Players)
            {
                sb.Append("player ").Append(p.Slot).Append(' ').Append(Clean(p.Id)).Append(' ').Append(Clean(p.Prefab)).Append(' ')
                  .Append(p.Hp.ToString(c)).Append(' ').Append(p.MaxHp.ToString(c)).Append(' ').Append(p.Armor.ToString(c)).Append(' ')
                  .Append(p.Blanks).Append(' ').Append(p.Current).Append('\n');
                sb.Append("  name ").Append(p.Name ?? "").Append('\n');
                sb.Append("  guns");
                foreach (var g in p.Guns) sb.Append(' ').Append(g.Key).Append(':').Append(g.Value);
                sb.Append("\n  passives");
                foreach (var i in p.Passives) sb.Append(' ').Append(i);
                sb.Append("\n  actives");
                foreach (var i in p.Actives) sb.Append(' ').Append(i);
                sb.Append('\n');
            }
            return sb.ToString();
        }

        static string Clean(string s) => string.IsNullOrEmpty(s) ? "-" : s.Replace(' ', '_');

        static Backup Parse(string text)
        {
            var c = CultureInfo.InvariantCulture;
            var b = new Backup();
            PlayerState cur = null;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var parts = line.Trim().Split(' ');
                if (parts.Length == 0 || parts[0].Length == 0) continue;
                switch (parts[0])
                {
                    case "saved": b.Saved = line.Substring(6); break;
                    case "seed": b.Seed = int.Parse(parts[1], c); break;
                    case "floor": b.FloorIndex = int.Parse(parts[1], c); break;
                    case "money": b.Money = int.Parse(parts[1], c); break;
                    case "keys": b.Keys = int.Parse(parts[1], c); break;
                    case "ended": b.Ended = parts[1] == "1"; break;
                    case "player":
                        cur = new PlayerState
                        {
                            Slot = int.Parse(parts[1], c), Id = parts[2] == "-" ? "" : parts[2], Prefab = parts[3],
                            Hp = float.Parse(parts[4], c), MaxHp = float.Parse(parts[5], c), Armor = float.Parse(parts[6], c),
                            Blanks = int.Parse(parts[7], c), Current = int.Parse(parts[8], c),
                        };
                        b.Players.Add(cur);
                        break;
                    case "name": if (cur != null) cur.Name = line.Trim().Length > 5 ? line.Trim().Substring(5) : ""; break;
                    case "guns":
                        if (cur != null) for (int i = 1; i < parts.Length; i++) { var gp = parts[i].Split(':'); cur.Guns.Add(new KeyValuePair<int, int>(int.Parse(gp[0], c), int.Parse(gp[1], c))); }
                        break;
                    case "passives": if (cur != null) for (int i = 1; i < parts.Length; i++) cur.Passives.Add(int.Parse(parts[i], c)); break;
                    case "actives": if (cur != null) for (int i = 1; i < parts.Length; i++) cur.Actives.Add(int.Parse(parts[i], c)); break;
                }
            }
            return b;
        }

        static Backup LoadLatest() => LoadFile(Latest);

        static Backup LoadFile(string path)
        {
            try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : null; }
            catch (Exception e) { Plugin.Log.LogWarning("RunBackup: unreadable backup: " + e.Message); return null; }
        }

        // ---------------- Save slots ----------------
        // Besides the automatic save (always the newest), the host can keep runs in three slots and resume any of them.

        public const int Slots = 3;
        static string SlotPath(int slot) => Path.Combine(Dir, "slot-" + slot + ".txt");

        /// <summary>One line for a slot: what is in it, or "Empty". Slot 0 is the automatic save.</summary>
        public static string SlotSummary(int slot) => Summary(LoadFile(slot == 0 ? Latest : SlotPath(slot)));

        /// <summary>The newest autosave of each of the last few runs (a crash or a drop can end any of them).</summary>
        static string EndedFile => Path.Combine(Dir, "ended-runs.txt");

        public static List<string> RecentRuns(int max)
        {
            var list = new List<string>();
            var seeds = new HashSet<int>();
            try { if (File.Exists(EndedFile)) foreach (var l in File.ReadAllLines(EndedFile)) if (int.TryParse(l.Trim(), out var es)) seeds.Add(es); } catch { }
            var all = new List<string>(Directory.GetFiles(Dir, "run-*.txt"));
            all.Sort();
            all.Reverse();
            foreach (var f in all)
            {
                var b = LoadFile(f);
                if (b == null || b.FloorIndex <= 0 || !seeds.Add(b.Seed)) continue;
                if (!b.Ended) list.Add(f);
                if (list.Count >= max) break;
            }
            return list;
        }

        public static string FileSummary(string path) => Summary(LoadFile(path));

        static string Summary(Backup b)
        {
            if (b == null || b.FloorIndex <= 0) return "Empty";
            var names = new List<string>();
            foreach (var p in b.Players) names.Add(string.IsNullOrEmpty(p.Prefab) ? "?" : p.Prefab.Replace("Player", ""));
            string when = DateTime.TryParse(b.Saved, out var t) ? t.ToString("MMM d HH:mm") : b.Saved;
            return $"Floor {b.FloorIndex}, {string.Join(" + ", names.ToArray())}, {when}" + (b.Ended ? " (run over)" : "");
        }

        public static bool SlotUsable(int slot)
        {
            var b = LoadFile(slot == 0 ? Latest : SlotPath(slot));
            return b != null && !b.Ended && b.FloorIndex > 0;
        }

        public static bool SaveToSlot(int slot)
        {
            if (Instance == null || !NetSession.Active || !NetSession.IsHost || RunSync.CurrentIndex < 0 || SceneObjects.InBreach) return false;
            try
            {
                File.WriteAllText(SlotPath(slot), Serialize(Capture()));
                Plugin.Log.LogInfo("RunBackup: saved to slot " + slot);
                return true;
            }
            catch (Exception e) { Plugin.Log.LogError("RunBackup slot save: " + e.Message); return false; }
        }

        public static bool AnySlotUsable
        {
            get
            {
                if (!SceneObjects.InBreach || (NetSession.Active && !NetSession.IsHost) || PendingResume != null) return false;
                for (int i = 0; i <= Slots; i++) if (SlotUsable(i)) return true;
                return RecentRuns(1).Count > 0;
            }
        }

        // ---------------- Resuming ----------------

        /// <summary>The menu offers "Resume saved run" in the Breach when an unfinished run was saved.</summary>
        public static bool CanResume
        {
            get
            {
                if (!SceneObjects.InBreach || (NetSession.Active && !NetSession.IsHost) || PendingResume != null) return false;
                if (!File.Exists(Latest)) return false;
                var b = LoadLatest();
                return b != null && !b.Ended && b.FloorIndex > 0;
            }
        }

        public static void ResumeAsHost() => ResumeSlot(0);

        public static void ResumeSlot(int slot) => ResumeFile(slot == 0 ? Latest : SlotPath(slot));

        public static void ResumeFile(string path)
        {
            var b = LoadFile(path);
            if (b == null) return;
            PendingResume = b;
            restoredSlots.Clear();
            var s = NetSession.Instance;
            foreach (var p in b.Players)
                if (!string.IsNullOrEmpty(p.Id) && p.Id != NetConfig.PlayerId.Value) s.KnownPlayers[p.Id] = p.Slot;
            if (!NetSession.Active) s.Host();
            // The friend comes back straight onto the dungeon floor, not through the Breach, so the host needs
            // their character from the backup to bring their copy back.
            var lobby = FindObjectOfType<Lobby>();
            if (lobby != null)
            {
                foreach (var p in b.Players)
                    if (p.Id != NetConfig.PlayerId.Value)
                        lobby.Roster[p.Slot] = new Lobby.Entry { Prefab = p.Prefab, Name = p.Name };
                lobby.SendRoster();
            }
            foreach (var p in b.Players)
                if (p.Id == NetConfig.PlayerId.Value && GameManager.Instance.PrimaryPlayer == null && Instance != null)
                    Instance.StartCoroutine(CharacterPicker.Pick(p.Prefab));
            Toasts.Show($"Saved run ready (floor {b.FloorIndex}). Once everyone has joined, walk into the Gungeon.", 8f);
        }

        void RestoreHost()
        {
            var b = PendingResume;
            var local = PlayerSlots.Local;
            foreach (var p in b.Players)
                if (p.Id == NetConfig.PlayerId.Value && local) { Apply(local, p); restoredSlots.Add(NetSession.LocalSlot); }
            if (local && local.carriedConsumables != null) { local.carriedConsumables.Currency = b.Money; local.carriedConsumables.KeyBullets = b.Keys; }
            Toasts.Show("Run restored", 4f);
        }

        static void Apply(PlayerController pc, PlayerState s)
        {
            Replication.InventorySync.Instance?.Restore(pc, s.Guns, s.Current, s.Passives, s.Actives);
            if (Mathf.Abs(pc.healthHaver.GetMaxHealth() - s.MaxHp) > 0.01f) pc.healthHaver.SetHealthMaximum(s.MaxHp);
            pc.healthHaver.ForceSetCurrentHealth(s.Hp);
            pc.healthHaver.Armor = s.Armor;
            pc.Blanks = s.Blanks;
            Plugin.Log.LogInfo($"RunBackup: restored {s.Prefab} ({s.Guns.Count} guns, {s.Passives.Count} items)");
        }

        void Update()
        {
            if (!NetSession.Active || !NetSession.IsHost) return;
            float now = Time.realtimeSinceStartup;
            if (saveAt > 0 && now >= saveAt) { saveAt = -1f; SaveNow("floor start"); }
            if (RunSync.CurrentIndex >= 0 && !SceneObjects.InBreach && now >= nextPeriodic)
            {
                if (nextPeriodic > 0) SaveNow("every minute");
                nextPeriodic = now + 60f;
            }
            // Resuming: give each returning friend their saved things once their character is back on this floor.
            if (PendingResume != null && !SceneObjects.InBreach && RunSync.CurrentIndex >= 0)
            {
                bool all = true;
                foreach (var p in PendingResume.Players)
                {
                    if (restoredSlots.Contains(p.Slot)) continue;
                    all = false;
                    var pc = PlayerSlots.ForSlot(p.Slot);
                    var ws = Replication.WorldSync.Instance;
                    if (pc && p.Slot != NetSession.LocalSlot && ws != null && ws.IsSlotReady(p.Slot))
                    {
                        Apply(pc, p);
                        restoredSlots.Add(p.Slot);
                    }
                }
                if (all) { PendingResume = null; SaveNow("resumed"); }
            }
        }

        /// <summary>The run ended normally (back in the Breach): mark the backup so it is not offered again.</summary>
        public static void MarkEnded()
        {
            var b = LoadLatest();
            if (b == null || b.Ended) return;
            b.Ended = true;
            try { File.WriteAllText(Latest, Serialize(b)); } catch { }
            // Its other autosaves are over too: never offer this run again.
            try { File.AppendAllText(EndedFile, b.Seed + "\n"); } catch { }
        }
    }
}
