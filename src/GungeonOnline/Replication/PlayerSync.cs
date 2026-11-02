using System.Collections.Generic;
using GungeonOnline.Game;
using GungeonOnline.Net;
using GungeonOnline.Session;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Player positions. Each player's own game is the authority for where that player is. Every input frame it sends
    /// also carries where the player was when that frame began, and every other machine puts its copy exactly there
    /// when it applies the frame; in between, the game moves the copy with that input as usual. The copy cannot drift,
    /// so nothing is ever blended or corrected, and the network never moves your own player.
    ///
    /// When the host's game moves a player on purpose (a warp, the Classic pull into a fight, a boss intro), the host
    /// sends that player's game the same move. Until frames from after the move arrive, the host keeps its copy where
    /// it put it. The host's per-frame snapshot of every position is now only measured, and used as a last resort for a
    /// copy whose owner has sent nothing for a second.
    /// </summary>
    [DefaultExecutionOrder(31000)]
    internal sealed class PlayerSync : MonoBehaviour
    {
        public static PlayerSync Instance { get; private set; }

        const float StaleSnapDistance = 4f; // a copy with no owner frames for a second, this far from the host's copy: move it
        const float StaleSeconds = 1f;
        const float TeleportDistance = 2f;  // larger placements are the owner's own warps and respawns, not drift

        sealed class Track
        {
            public float LastError, MaxError;        // distance to the host's snapshot (measured only)
            public int Snaps, Placed, Moves, Teleports;
            public double JumpSum; public float MaxJump; // how far the game's own movement of the copy ended from the owner's position
            public byte ExpectedEpoch;               // host: frames from before our last move order are not used
            public Vector2 FrameStart; public bool FrameStartValid;
            public Vector2 FrameEnd; public bool FrameEndValid;
            public bool WasCart;                     // host: in a cart last frame (getting off can move a player far)
            public float FellAt = -100f;              // host: last frame the copy was falling // host: where the copy was at the end of the last frame
            public float LastPlaced = -100f;
            public float LastJumpLog, LastMoveAt = -100f;
        }

        readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();
        uint hostFrame;
        uint lastSnapshotFrame;
        float detectAfter;                  // host: no move orders right after a floor load
        bool hasPendingMove;                // guest: a move the host ordered, done as soon as our player can move
        Vector2 pendingMove;
        byte pendingEpoch;
        public int SnapshotsApplied, MovesApplied;
        readonly WireWriter w = new WireWriter(512);

        void Awake() { Instance = this; }

        void Start()
        {
            NetSession.Instance.On(Channel.Snapshot, OnSnapshot);
            NetSession.Instance.On(Channel.Move, OnMove);
            NetSession.Instance.SessionEnded += () => { tracks.Clear(); lastSnapshotFrame = 0; hostFrame = 0; hasPendingMove = false; };
            // After a (re)connect both sides count moves from zero again.
            NetSession.Instance.PeerJoined += c =>
            {
                if (NetSession.IsHost) tracks.Remove(c.Slot);
                else { tracks.Clear(); hasPendingMove = false; }
                lastSnapshotFrame = 0;
            };
        }

        /// <summary>Floor change: everyone starts at the new entrance, so nothing measured before the load counts.</summary>
        public void ResetTracks()
        {
            foreach (var t in tracks.Values) { t.FrameStartValid = false; t.FrameEndValid = false; }
            detectAfter = Time.realtimeSinceStartup + 2f;
        }

        /// <summary>
        /// A new copy replaced this player (they swapped character, or joined): forget where the old one was, or the
        /// host takes the swap for something in its game moving them and sends them to the new copy's spot.
        /// </summary>
        public void Forget(int slot)
        {
            if (!tracks.TryGetValue(slot, out var t)) return;
            t.FrameStartValid = false;
            t.FrameEndValid = false;
        }

        Track T(int slot)
        {
            if (!tracks.TryGetValue(slot, out var t)) tracks[slot] = t = new Track();
            return t;
        }

        public string Stats(int slot)
        {
            if (!tracks.TryGetValue(slot, out var t)) return "-";
            int n = t.Placed - t.Teleports;
            return $"err={t.LastError:F3} max={t.MaxError:F3} corr=0 snaps={t.Snaps} placed={t.Placed} jump={(n > 0 ? t.JumpSum / n : 0):F3}/{t.MaxJump:F3} moves={t.Moves}";
        }

        static bool AtLeast(byte a, byte b) => (sbyte)(a - b) >= 0;

        uint SeqFor(int slot)
        {
            if (slot == NetSession.LocalSlot) return InputSync.Instance.LocalSeq;
            return InputSync.Instance.Streams.TryGetValue(slot, out var s) ? s.AppliedSeq : 0;
        }

        /// <summary>
        /// Start of a frame, right after a remote player's input for it was chosen: put the copy exactly where its owner
        /// was when that input began. Also remembers where the frame started, so the host can tell its own game's moves.
        /// </summary>
        static readonly Dictionary<int, float> fallSeen = new Dictionary<int, float>();

        /// <summary>A player falling on either machine, or just put back on solid ground: the screens differ by nature.</summary>
        public static bool FellRecently(int slot, PlayerController p)
        {
            if (p != null && p.IsFalling) fallSeen[slot] = Time.realtimeSinceStartup;
            return fallSeen.TryGetValue(slot, out var at) && Time.realtimeSinceStartup - at < 3f;
        }

        public void Place(int slot, PlayerController p, InputSync.Stream s)
        {
            var t = T(slot);
            if (p.IsFalling || p.currentMineCart) DrawSmoothing.Clear(p);
            if (p.IsFalling || (s.PlaceState & InputSync.StateFalling) != 0) fallSeen[slot] = Time.realtimeSinceStartup;
            // Host: something in our game moved the copy between the end of the last frame and now (a warp that ran
            // before the input poll this frame). That is a deliberate move too: order it, and do not put the copy back.
            if (NetSession.IsHost && t.FrameEndValid && !p.IsFalling && !p.currentMineCart && Time.realtimeSinceStartup - t.FellAt >= 1.5f && (s.PlaceState & (InputSync.StateFalling | InputSync.StateCart)) == 0 && !GameManager.Instance.IsLoadingLevel
                && Time.realtimeSinceStartup >= detectAfter && ((Vector2)p.transform.position - t.FrameEnd).magnitude >= MoveLimit())
                OrderMove(slot, t, p.transform.position);
            t.FrameEndValid = false;
            if (s.PlaceNow)
            {
                s.PlaceNow = false;
                bool current = !NetSession.IsHost || AtLeast(s.PlaceEpoch, t.ExpectedEpoch);
                // Both falling: the fall plays out here as it does there. A copy that rides a cart here is carried by it; one
                // that missed the cart, or has not reached the pit its owner fell into, follows its owner.
                bool ownerFree = (s.PlaceState & InputSync.StateFalling) == 0 || !p.IsFalling;
                // Host: a player still on an older copy of the floor (we restarted and resumed the run, or changed floors)
                // reports positions from that floor. Placed on ours they can land in a boss room and start its intro.
                bool sameFloor = !NetSession.IsHost || Game.SceneObjects.InBreach || WorldSync.Instance == null || WorldSync.Instance.IsSlotReady(slot);
                if (current && ownerFree && sameFloor && p.specRigidbody != null && !p.currentMineCart && !GameManager.Instance.IsLoadingLevel)
                {
                    Vector2 at = p.transform.position;
                    var to = s.PlacePos + s.PlaceAhead; // where the owner is now, when the frame landed late
                    float jump = (to - at).magnitude;
                    t.Placed++;
                    if (jump >= TeleportDistance) t.Teleports++;
                    else { t.JumpSum += jump; if (jump > t.MaxJump) t.MaxJump = jump; }
                    if (Dev.DevEnv.IsHarness && jump > 0.3f && jump < TeleportDistance && Time.realtimeSinceStartup - t.LastJumpLog > 0.25f)
                    {
                        t.LastJumpLog = Time.realtimeSinceStartup;
                        Plugin.Log.LogInfo($"PLACE slot={slot} jump={jump:F2} roll={p.IsDodgeRolling} starved={s.Starved} merged={s.Merged} late={s.LastLateMs:F0}ms epoch={s.PlaceEpoch}/{t.ExpectedEpoch} sinceMove={Time.realtimeSinceStartup - t.LastMoveAt:F2}s");
                    }
                    if (jump > 0.0001f) DrawSmoothing.Corrected(p, at, to);
                    if (jump > 0.0001f)
                    {
                        p.transform.position = new Vector3(to.x, to.y, p.transform.position.z);
                        p.specRigidbody.Reinitialize();
                    }
                    t.LastPlaced = Time.realtimeSinceStartup;
                }
            }
            t.FrameStart = p.transform.position;
            t.FrameStartValid = true;
        }

        /// <summary>End of frame: host move orders and snapshot; guests carry out a move the host ordered.</summary>
        void LateUpdate()
        {
            long t0 = Dev.NetProfiler.Begin();
            try { LateUpdateImpl(); }
            finally { Dev.NetProfiler.End("players", t0); }
        }

        void LateUpdateImpl()
        {
            if (!NetSession.Active) return;
            Camera.IndependentCamera.Apply();
            DrawSmoothing.Tick();
            foreach (var kv in PlayerSlots.Remotes) if (kv.Value && !kv.Value.IsFalling && !kv.Value.currentMineCart) DrawSmoothing.Measure(kv.Value);
            if (PlayerSlots.Local) DrawSmoothing.Trace(PlayerSlots.Local, PlayerSlots.Local.transform.position);
            if (NetSession.IsHost)
            {
                DetectHostMoves();
                SendSnapshot();
            }
            else TryPendingMove();
        }

        /// <summary>
        /// Host: a remote player's copy moved much further this frame than walking or rolling can (a warp, the Classic
        /// pull into a fight, a boss intro). Our game moved it on purpose, so that player's own game makes the same move.
        /// </summary>
        void DetectHostMoves()
        {
            if (GameManager.Instance.IsLoadingLevel) detectAfter = Mathf.Max(detectAfter, Time.realtimeSinceStartup + 1f); // and a second after the load ends
            if (GameManager.Instance.IsLoadingLevel || Time.realtimeSinceStartup < detectAfter)
            {
                foreach (var t in tracks.Values) { t.FrameStartValid = false; t.FrameEndValid = false; }
                return;
            }
            float limit = MoveLimit();
            foreach (var kv in PlayerSlots.Remotes)
            {
                var p = kv.Value;
                if (!p) continue;
                var t = T(kv.Key);
                t.FrameEnd = p.transform.position;
                t.FrameEndValid = true;
                if (!t.FrameStartValid) continue;
                t.FrameStartValid = false;
                if (p.IsFalling) t.FellAt = Time.realtimeSinceStartup;
                // A fall ends with the game putting the player back on solid ground: not an order either.
                if (p.IsFalling || p.currentMineCart || t.WasCart || Time.realtimeSinceStartup - t.FellAt < 1.5f) { t.WasCart = p.currentMineCart; continue; }
                Vector2 now = p.transform.position;
                if ((now - t.FrameStart).magnitude < limit) continue;
                OrderMove(kv.Key, t, now);
            }
        }

        static float MoveLimit() => Mathf.Max(3f, 30f * Time.unscaledDeltaTime);

        void OrderMove(int slot, Track t, Vector2 to)
        {
            t.ExpectedEpoch = (byte)(t.ExpectedEpoch + 1);
            t.Moves++;
            t.LastMoveAt = Time.realtimeSinceStartup;
            SendMove(slot, t.ExpectedEpoch, to);
        }

        void SendMove(int slot, byte epoch, Vector2 to)
        {
            w.Reset(); w.U8(epoch); w.F32(to.x); w.F32(to.y);
            var msg = w.ToArray();
            foreach (var c in NetSession.Instance.Node.Connections)
                if (c.Slot == slot) c.Send((byte)Channel.Move, msg, Delivery.ReliableOrdered);
            Plugin.Log.LogInfo($"MOVE slot={slot} epoch={epoch} to={to.x:F2},{to.y:F2}");
        }

        /// <summary>Guest: the host's game moved our player on purpose; make the same move here.</summary>
        void OnMove(Connection c, byte[] data, Delivery d)
        {
            if (NetSession.IsHost) return;
            var r = new WireReader(data, 0, data.Length);
            pendingEpoch = r.U8();
            pendingMove = new Vector2(r.F32(), r.F32());
            hasPendingMove = true;
            TryPendingMove();
        }

        void TryPendingMove()
        {
            if (!hasPendingMove) return;
            var me = PlayerSlots.Local;
            if (!me || me.specRigidbody == null || GameManager.Instance.IsLoadingLevel || me.IsFalling) return;
            hasPendingMove = false;
            bool far = Vector2.Distance(me.CenterPosition, pendingMove) > 8f;
            me.WarpToPoint(pendingMove);
            if (far) Camera.IndependentCamera.SoftenJump(null);
            InputSync.Instance.MoveEpoch = pendingEpoch; // frames from now on show the move
            MovesApplied++;
            Plugin.Log.LogInfo($"MOVED by the host to {pendingMove.x:F2},{pendingMove.y:F2} (epoch {pendingEpoch})");
        }

        void SendSnapshot()
        {
            hostFrame++;
            w.Reset();
            w.U32(hostFrame);
            var list = new List<KeyValuePair<int, PlayerController>>();
            if (PlayerSlots.Local) list.Add(new KeyValuePair<int, PlayerController>(NetSession.LocalSlot, PlayerSlots.Local));
            foreach (var kv in PlayerSlots.Remotes) if (kv.Value) list.Add(kv);
            w.U8((byte)list.Count);
            foreach (var kv in list)
            {
                var p = kv.Value;
                w.U8((byte)kv.Key);
                w.U32(SeqFor(kv.Key));
                Vector2 pos = p.transform.position;
                w.F32(pos.x); w.F32(pos.y);
            }
            NetSession.Instance.Node.Broadcast((byte)Channel.Snapshot, w.ToArray(), Delivery.Unreliable);
        }

        /// <summary>Guests: measure how far each copy is from the host's; move only a copy whose owner has gone quiet.</summary>
        void OnSnapshot(Connection c, byte[] data, Delivery d)
        {
            if (NetSession.IsHost) return;
            var r = new WireReader(data, 0, data.Length);
            uint frame = r.U32();
            if (frame <= lastSnapshotFrame) return; // older than what we have: drop
            lastSnapshotFrame = frame;
            SnapshotsApplied++;
            int n = r.U8();
            for (int i = 0; i < n; i++)
            {
                int slot = r.U8();
                r.U32(); // the host's input sequence for that player (no longer needed)
                var pos = new Vector2(r.F32(), r.F32());
                var p = PlayerSlots.ForSlot(slot);
                if (!p || GameManager.Instance.IsLoadingLevel) continue;
                var t = T(slot);
                t.LastError = (pos - (Vector2)p.transform.position).magnitude;
                if (t.LastError > t.MaxError) t.MaxError = t.LastError;
                if (slot == NetSession.LocalSlot || p.specRigidbody == null || p.IsFalling) continue;
                if (t.LastError > StaleSnapDistance && Time.realtimeSinceStartup - t.LastPlaced > StaleSeconds)
                {
                    t.Snaps++;
                    p.transform.position = new Vector3(pos.x, pos.y, p.transform.position.z);
                    try { p.specRigidbody.Reinitialize(); } catch { } // player still being set up after a load
                    Plugin.Log.LogInfo($"SYNC slot={slot} quiet owner, moved to the host's copy ({t.LastError:F1} off)");
                }
            }
        }
    }
}
