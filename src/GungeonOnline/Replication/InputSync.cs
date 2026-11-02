using System.Collections.Generic;
using GungeonOnline.Game;
using GungeonOnline.Input;
using GungeonOnline.Net;
using GungeonOnline.Session;
using UnityEngine;

namespace GungeonOnline.Replication
{
    /// <summary>
    /// Input streams. Every machine samples its own player right after the device poll and sends at once
    /// (each packet repeats the last 8 frames, so a lost packet costs nothing). Each frame carries the sender's
    /// clock, so remote input is replayed on the sender's timeline: a player at 200 fps and one at 240 fps hold
    /// every input for the same real time on both machines, and network jitter does not shift dodge rolls.
    /// A small playout buffer absorbs jitter; Auto sizes it from measured jitter. Your own player is never delayed.
    /// </summary>
    internal sealed class InputSync : MonoBehaviour
    {
        public const int Redundancy = 8;

        public static InputSync Instance { get; private set; }

        public uint LocalSeq { get; private set; }
        readonly InputFrame[] localHistory = new InputFrame[256];
        readonly uint[] localTimeMs = new uint[256];

        public sealed class Stream
        {
            public int Slot;
            public uint NewestSeq, AppliedSeq;
            public InputFrame Last;
            public readonly SortedDictionary<uint, Frame> Pending = new SortedDictionary<uint, Frame>();
            // The applied frame's owner position, for PlayerSync to put the copy there this frame.
            public bool PlaceNow;
            public Vector2 PlacePos;
            public byte PlaceEpoch;
            public byte PlaceState;
            // How far the owner has most likely moved since the applied frame began, by the time it is applied here:
            // a frame that lands late is placed where its owner is now, not where they were (see Ahead).
            public Vector2 PlaceAhead;
            public Vector2 PrevPos;
            public uint PrevTime;
            public bool PrevValid;
            public uint LastFreshMs;
            // Clock mapping: sample = localArrivalMs - remoteMs. Min over a window = fastest path; spread = jitter.
            public readonly Queue<KeyValuePair<double, double>> Samples = new Queue<KeyValuePair<double, double>>();
            public double BaseOffsetMs = double.NaN;
            public double JitterMs;
            public double BufferMs;
            public int Starved, Merged, Applied;
            public int Held;              // frames with nothing new (also when the sender just runs at a lower frame rate)
            public double SendGapMs = 16; // average time between the sender's frames
            public uint LastFreshStamp;
            public int FreshDirect, FreshRelay; // which route delivered a new frame first
            public double LastLateMs; // how long the applied frame waited after it was due
            public double PollLagMs;  // average time a packet sat waiting for the frame to start (no longer counted as jitter)
            // Tests only: the sender's high-resolution clock for each frame, and the measured press-to-use delays.
            public readonly long[] SentQpc = new long[256];
            public readonly uint[] SentQpcSeq = new uint[256];
            public readonly List<double> E2e = new List<double>();
        }

        // Tests only. GO_MEASURE_E2E=1: each input packet also carries the sender's high-resolution clock for its newest
        // frame; both test games run on one PC and share that clock, so the receiver measures the true delay from a
        // press on the sender to the frame that uses it here. GO_INPUT_CLOCK=poll times packets by the frame that
        // drains them (the 1.0 method), so the two methods can be compared in one build. GO_BUF_PCT: jitter percentile.
        static readonly bool measureE2E = System.Environment.GetEnvironmentVariable("GO_MEASURE_E2E") == "1";
        static readonly bool pollClock = System.Environment.GetEnvironmentVariable("GO_INPUT_CLOCK") == "poll";
        static readonly double bufPercentile = ParsePct(System.Environment.GetEnvironmentVariable("GO_BUF_PCT"));
        // GO_BUF_CAP (tests): most smoothing Auto may use, in ms.
        static readonly float bufCap = float.TryParse(System.Environment.GetEnvironmentVariable("GO_BUF_CAP"), out var cap) && cap >= 4f ? cap : 40f;
        // GO_BUF_SCALE (tests): smoothing per ms of measured jitter. GO_AHEAD=0 (tests): place late frames where they were (the 1.1 way).
        static readonly float bufScale = float.TryParse(System.Environment.GetEnvironmentVariable("GO_BUF_SCALE"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sc) && sc >= 0f ? sc : 0f;
        static readonly bool aheadOn = System.Environment.GetEnvironmentVariable("GO_AHEAD") != "0";
        /// <summary>Longest lateness a frame is carried forward over, and the furthest it is moved (units).</summary>
        const double AheadMaxMs = 80;
        const float AheadMaxDist = 1.2f;
        static double ParsePct(string v) =>
            double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) && d > 0 && d < 1 ? d : 0.9;
        readonly long[] localQpc = new long[256];

        /// <summary>Tests: mean and 95th percentile of the measured press-to-use delay for a stream (ms), or null.</summary>
        public static string E2eText(Stream s)
        {
            if (s.E2e.Count == 0) return "";
            var copy = new List<double>(s.E2e);
            copy.Sort();
            double sum = 0;
            foreach (var x in copy) sum += x;
            return $" e2e={sum / copy.Count:F1}ms e2e95={copy[(int)(copy.Count * 0.95)]:F1}ms";
        }

        public struct Frame
        {
            public uint Time;     // sender clock
            public InputFrame Input;
            public Vector2 Pos;   // where the sender's player was when this frame began
            public byte Epoch;    // how many host-ordered moves the sender had made by then
            public byte State;    // StateFalling | StateCart, as the sender's player was then
        }

        /// <summary>Host-ordered moves our own player has made (PlayerSync); every frame we send carries it.</summary>
        public byte MoveEpoch;
        readonly Vector2[] localPos = new Vector2[256];
        readonly byte[] localEpoch = new byte[256];
        readonly byte[] localState = new byte[256];
        public const byte StateFalling = 1, StateCart = 2;

        /// <summary>Owner silent this long: the copy stands still instead of running on with the last input.</summary>
        const uint StillAfterMs = 250;

        public readonly Dictionary<int, Stream> Streams = new Dictionary<int, Stream>();
        readonly WireWriter w = new WireWriter(512);

        static uint NowMs => (uint)Clock.NowMs;

        void Awake()
        {
            Instance = this;
            InputPump.BeforePoll += BeforePoll;
            InputPump.AfterPoll += AfterPoll;
        }

        void Start()
        {
            NetSession.Instance.On(Channel.Input, OnInput);
            NetSession.Instance.SessionEnded += () => { Streams.Clear(); LocalSeq = 0; MoveEpoch = 0; };
            NetSession.Instance.PeerLeft += (c, why) => Streams.Remove(c.Slot);
            // A (re)connected peer may have restarted and count from zero again: start their streams fresh.
            NetSession.Instance.PeerJoined += c =>
            {
                if (NetSession.IsHost) Streams.Remove(c.Slot);
                else { Streams.Clear(); MoveEpoch = 0; }
            };
        }

        Stream Get(int slot)
        {
            if (!Streams.TryGetValue(slot, out var s)) Streams[slot] = s = new Stream { Slot = slot };
            return s;
        }

        /// <summary>Playout buffer for other players: the slider value, or Auto (-1) from measured jitter.</summary>
        static double TargetBufferMs(Stream s)
        {
            int setting = NetConfig.SmoothingMs.Value;
            if (setting >= 0) return setting;
            // Auto: one 240 Hz frame so bursts still play smoothly, no more. Late input is predicted instead of waited
            // for: the friend keeps moving with their last input, and a late frame is placed where they are by then
            // (Ahead). Measured against covering the jitter (1.2x, up to 40 ms): friends drawn 14 to 21 ms sooner and
            // closer to where they really are, with no more jerk. GO_BUF_SCALE / GO_BUF_CAP bring the old way back for tests.
            return Mathf.Clamp((float)(s.JitterMs * bufScale + 2.0), 4f, bufCap);
        }

        void BeforePoll()
        {
            if (!NetSession.Active) return;
            NetSession.Instance.PollNow();
            foreach (var kv in PlayerSlots.Remotes)
            {
                var p = kv.Value;
                if (!p) continue;
                var dev = InputPump.Route(p.PlayerIDX);
                var st = Get(kv.Key);
                dev.Frame = Next(st);
                Game.RemoteAim.Follow(p, dev.Frame);
                PlayerSync.Instance?.Place(kv.Key, p, st); // exactly where its owner was when this input began
            }
        }

        /// <summary>
        /// Apply the newest frame whose sender time is due (local now - base offset - buffer). Any button pressed
        /// in a frame that is passed over is still pressed in the applied frame, so taps are never lost.
        /// No frame due: hold the last one (that is what the sender's player is doing too).
        /// </summary>
        static InputFrame Next(Stream s)
        {
            if (double.IsNaN(s.BaseOffsetMs)) return s.Last;
            s.BufferMs = TargetBufferMs(s);
            double dueRemote = NowMs - s.BaseOffsetMs - s.BufferMs;
            uint prevButtons = s.Last.Buttons, edges = 0;
            InputFrame chosen = s.Last;
            uint chosenSeq = s.AppliedSeq, chosenTime = 0;
            Vector2 chosenPos = default(Vector2);
            byte chosenEpoch = 0, chosenState = 0;
            int n = 0;
            var used = new List<uint>();
            foreach (var kv in s.Pending)
            {
                if (kv.Value.Time > dueRemote) break;
                edges |= kv.Value.Input.Buttons & ~prevButtons;
                prevButtons = kv.Value.Input.Buttons;
                chosen = kv.Value.Input;
                chosenSeq = kv.Key;
                chosenTime = kv.Value.Time;
                chosenPos = kv.Value.Pos;
                chosenEpoch = kv.Value.Epoch;
                chosenState = kv.Value.State;
                used.Add(kv.Key);
                n++;
            }
            if (n == 0)
            {
                if (s.Applied > 0)
                {
                    s.Held++;
                    // Late for real: nothing new for longer than the sender's own frame gap allows.
                    if (NowMs - s.LastFreshMs > System.Math.Max(8.0, s.SendGapMs * 1.6)) s.Starved++;
                }
                // The owner has gone quiet (a stall, a loading screen): stand still instead of running on.
                if (s.Applied > 0 && NowMs - s.LastFreshMs > StillAfterMs) { var still = s.Last; still.MoveX = 0; still.MoveY = 0; still.Buttons = 0; return still; }
                return s.Last;
            }
            s.PlaceNow = true;
            s.PlacePos = chosenPos;
            s.PlaceEpoch = chosenEpoch;
            s.PlaceState = chosenState;
            if (measureE2E && s.SentQpcSeq[chosenSeq & 255] == chosenSeq && s.E2e.Count < 50000)
                s.E2e.Add((System.Diagnostics.Stopwatch.GetTimestamp() - s.SentQpc[chosenSeq & 255]) * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
            foreach (var k in used) s.Pending.Remove(k);
            if (n > 1) s.Merged += n - 1;
            s.LastLateMs = dueRemote - chosenTime;
            s.PlaceAhead = Ahead(s, chosen, chosenPos, chosenTime, chosenState, s.LastLateMs);
            s.PrevPos = chosenPos; s.PrevTime = chosenTime; s.PrevValid = true;
            chosen.Buttons |= edges;
            s.Last = chosen;
            s.AppliedSeq = chosenSeq;
            s.Applied++;
            return chosen;
        }

        /// <summary>
        /// The applied frame is this late: carry its start position forward at the owner's own speed (from their last
        /// two frames), the way rollback games re-run late input. Never past a stop: no stick (or a fall, a cart) means
        /// no carry, so a stop, a turn or the start of a roll is shown where it really happened.
        /// </summary>
        static Vector2 Ahead(Stream s, InputFrame f, Vector2 pos, uint time, byte state, double lateMs)
        {
            if (!aheadOn || !s.PrevValid || lateMs <= 1 || state != 0) return Vector2.zero;
            if (f.MoveX * f.MoveX + f.MoveY * f.MoveY < 0.04f) return Vector2.zero;
            double dt = (double)(time - s.PrevTime);
            if (dt <= 0 || dt > 100) return Vector2.zero;
            var v = (pos - s.PrevPos) / (float)dt;
            var step = v * (float)System.Math.Min(lateMs, AheadMaxMs);
            return step.magnitude > AheadMaxDist ? Vector2.zero : step;
        }

        void AfterPoll()
        {
            if (!NetSession.Active) return;
            var local = PlayerSlots.Local;
            var bi = BraveInput.GetInstanceForPlayer(0);
            if (local == null || bi == null) return;
            // While your pause menu is open the stick drives the menu, not your character.
            var f = Game.PauseScope.LocalMenuOpen ? default(InputFrame) : InputPump.Sample(bi.ActiveActions);
            // Mouse players have no aim stick: send the direction they are actually aiming as stick aim.
            if (!Game.PauseScope.LocalMenuOpen && bi.IsKeyboardAndMouse())
            {
                Vector2 dir = (Vector2)local.unadjustedAimPoint - local.CenterPosition;
                if (dir.sqrMagnitude > 0.0001f) f.Aim = dir.normalized;
                f.Flags |= NetFlags.MouseAim;
            }
            // Where the gun really pointed last frame, after this player's own aim assist. Other games used to work
            // the aim out again with their own assist settings (one that auto-aims for player 2 swung the friend's
            // gun at enemies they were not aiming at, and their shots went there).
            if (!Game.PauseScope.LocalMenuOpen && Game.RemoteAim.HasOwnAim)
            {
                Vector2 dir = Game.RemoteAim.OwnAimDirection;
                if (dir.sqrMagnitude > 0.0001f) { f.Aim = dir.normalized; f.Flags |= NetFlags.FinalAim; }
            }
            LocalSeq++;
            localHistory[LocalSeq & 255] = f;
            localTimeMs[LocalSeq & 255] = NowMs;
            localPos[LocalSeq & 255] = local.transform.position; // where this frame begins: the end of the last one
            localEpoch[LocalSeq & 255] = MoveEpoch;
            localState[LocalSeq & 255] = (byte)((local.IsFalling ? StateFalling : 0) | (local.currentMineCart ? StateCart : 0));
            localQpc[LocalSeq & 255] = System.Diagnostics.Stopwatch.GetTimestamp();
            WriteStream(NetSession.LocalSlot, LocalSeq);
            var payload = w.ToArray();
            NetSession.Instance.Node.Broadcast((byte)Channel.Input, payload, Delivery.Unreliable);
            NetSession.Instance.FlushNow(); // out the door this frame
            Session.PeerLanes.Instance?.SendInput(payload); // and straight to the other guests
        }

        void WriteStream(int slot, uint newest)
        {
            w.Reset();
            w.U8((byte)slot);
            w.U32(newest);
            uint newestTime = localTimeMs[newest & 255];
            w.U32(newestTime);
            int count = (int)System.Math.Min(newest, Redundancy);
            w.U8((byte)count);
            using (var ms = new System.IO.MemoryStream(count * (InputFrame.WireSize + 12)))
            using (var bw = new System.IO.BinaryWriter(ms))
            {
                for (int i = 0; i < count; i++)
                {
                    uint seq = newest - (uint)i;
                    bw.Write((ushort)System.Math.Min(65535u, newestTime - localTimeMs[seq & 255]));
                    localHistory[seq & 255].Write(bw);
                    bw.Write(localPos[seq & 255].x);
                    bw.Write(localPos[seq & 255].y);
                    bw.Write(localEpoch[seq & 255]);
                    bw.Write(localState[seq & 255]);
                }
                bw.Flush();
                var b = ms.ToArray();
                w.Bytes(b, 0, b.Length);
            }
            if (measureE2E) w.U64((ulong)localQpc[newest & 255]);
        }

        void OnInput(Connection c, byte[] data, Delivery d)
        {
            int slot = data.Length > 0 ? data[0] : -1;
            if (NetSession.IsHost && slot != c.Slot) return; // a client may only speak for itself
            Ingest(slot, data, 0, false, c);
        }

        /// <summary>A guest's input that came straight from them (PeerLanes checked the sender).</summary>
        public void IngestLane(int slot, byte[] data, int pos)
        {
            if (pos >= data.Length || data[pos] != slot) return; // the lane's slot and the payload's must agree
            Ingest(slot, data, pos, true, null);
        }

        void Ingest(int slot, byte[] data, int pos, bool direct, Connection c)
        {
            var r = new WireReader(data, pos, data.Length - pos);
            r.U8(); // slot, checked by the caller
            uint newest = r.U32();
            uint newestTime = r.U32();
            int count = r.U8();
            if (slot == NetSession.LocalSlot) return;
            var s = Get(slot);
            // The packet's real arrival, not the start of the frame that drained it: the wait for the frame is
            // ours, not the network's, and must not widen the playout buffer.
            double now = NowMs;
            double arrival = NetSession.Instance.Node != null && NetSession.Instance.Node.CurrentArrival > 0 ? NetSession.Instance.Node.CurrentArrival * 1000.0 : now;
            s.PollLagMs = s.PollLagMs * 0.95 + (now - arrival) * 0.05;
            if (!pollClock) now = arrival;
            bool fresh = newest > s.NewestSeq;
            if (fresh)
            {
                s.LastFreshMs = NowMs;
                if (s.LastFreshStamp != 0 && newestTime > s.LastFreshStamp && newest > s.NewestSeq)
                    s.SendGapMs = s.SendGapMs * 0.95 + System.Math.Min(100.0, (double)(newestTime - s.LastFreshStamp) / System.Math.Max(1u, newest - s.NewestSeq)) * 0.05;
                s.LastFreshStamp = newestTime;
            }
            if (fresh) { if (direct) s.FreshDirect++; else s.FreshRelay++; }
            Observe(s, now, newestTime, fresh);
            using (var ms = new System.IO.MemoryStream(data, r.Pos, data.Length - r.Pos))
            using (var br = new System.IO.BinaryReader(ms))
            {
                for (int i = 0; i < count; i++)
                {
                    uint seq = newest - (uint)i;
                    uint t = newestTime - br.ReadUInt16();
                    var f = InputFrame.Read(br);
                    var at = new Vector2(br.ReadSingle(), br.ReadSingle());
                    byte epoch = br.ReadByte();
                    byte state = br.ReadByte();
                    if (seq <= s.AppliedSeq || s.Pending.ContainsKey(seq)) continue;
                    s.Pending[seq] = new Frame { Time = t, Input = f, Pos = at, Epoch = epoch, State = state };
                }
                if (measureE2E && ms.Length - ms.Position >= 8)
                {
                    s.SentQpc[newest & 255] = (long)br.ReadUInt64();
                    s.SentQpcSeq[newest & 255] = newest;
                }
            }
            if (newest > s.NewestSeq) s.NewestSeq = newest;
            if (c != null && NetSession.IsHost && NetSession.Instance.Node.Connections.Count > 1)
            {
                // Relay to the other guests now, not at the end of the host's frame: the relay must add no frame wait.
                foreach (var other in NetSession.Instance.Node.Connections)
                    if (other != c) other.Send((byte)Channel.Input, data, Delivery.Unreliable);
                NetSession.Instance.FlushNow();
            }
        }

        /// <summary>Track the clock offset (fastest arrival over 3 s) and jitter (90th percentile above it).</summary>
        static void Observe(Stream s, double nowMs, uint remoteMs, bool fresh)
        {
            if (!fresh) return;
            double sample = nowMs - remoteMs;
            s.Samples.Enqueue(new KeyValuePair<double, double>(nowMs, sample));
            while (s.Samples.Count > 0 && nowMs - s.Samples.Peek().Key > 3000) s.Samples.Dequeue();
            double min = double.MaxValue;
            var list = new List<double>(s.Samples.Count);
            foreach (var kv in s.Samples) { if (kv.Value < min) min = kv.Value; list.Add(kv.Value); }
            list.Sort();
            double p90 = list[System.Math.Min(list.Count - 1, (int)(list.Count * bufPercentile))];
            s.BaseOffsetMs = min;
            s.JitterMs = p90 - min;
        }
    }
}
