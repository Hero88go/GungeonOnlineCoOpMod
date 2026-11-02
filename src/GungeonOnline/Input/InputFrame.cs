using System;
using System.IO;
using UnityEngine;

namespace GungeonOnline.Input
{
    /// <summary>
    /// Semantic buttons, one per Gungeon action. Sent over the wire instead of raw device buttons so each
    /// player's own bindings (keyboard, mouse, remapped pad) never matter to the other side.
    /// </summary>
    [Flags]
    public enum NetButton : uint
    {
        None = 0,
        Shoot = 1u << 0, DodgeRoll = 1u << 1, Interact = 1u << 2, Reload = 1u << 3,
        UseItem = 1u << 4, Map = 1u << 5, GunUp = 1u << 6, GunDown = 1u << 7,
        ItemUp = 1u << 8, ItemDown = 1u << 9, Keybullet = 1u << 10, Pause = 1u << 11,
        Cancel = 1u << 12, MenuSelect = 1u << 13, EquipmentMenu = 1u << 14, Blank = 1u << 15,
        DropGun = 1u << 16, DropItem = 1u << 17, GunQuickEquip = 1u << 18, MinimapZoomIn = 1u << 19,
        MinimapZoomOut = 1u << 20, SwapDualGuns = 1u << 21, PunchoutDodgeLeft = 1u << 22, PunchoutDodgeRight = 1u << 23,
        PunchoutBlock = 1u << 24, PunchoutDuck = 1u << 25, PunchoutPunchLeft = 1u << 26, PunchoutPunchRight = 1u << 27,
        PunchoutSuper = 1u << 28, SelectLeft = 1u << 29, SelectRight = 1u << 30, SelectUp = 1u << 31,
    }

    [Flags]
    public enum NetFlags : byte
    {
        None = 0,
        SelectDown = 1 << 0,
        /// <summary>Aim comes from a mouse cursor: AimPoint is a world position, no controller aim assist.</summary>
        MouseAim = 1 << 1,
        /// <summary>Aim is where the owner's gun really pointed after its own aim assist: copies point exactly there.</summary>
        FinalAim = 1 << 2,
    }

    /// <summary>One frame of one player's input. 17 bytes on the wire.</summary>
    public struct InputFrame : IEquatable<InputFrame>
    {
        public const int WireSize = 17;

        public short MoveX, MoveY, AimX, AimY;
        public uint Buttons;
        public NetFlags Flags;
        /// <summary>Mouse aim target relative to the player, 1/16 unit steps. Only meaningful with MouseAim.</summary>
        public short AimPointX, AimPointY;

        public Vector2 Move
        {
            get => new Vector2(MoveX / 32767f, MoveY / 32767f);
            set { MoveX = Q(value.x); MoveY = Q(value.y); }
        }

        public Vector2 Aim
        {
            get => new Vector2(AimX / 32767f, AimY / 32767f);
            set { AimX = Q(value.x); AimY = Q(value.y); }
        }

        public bool Has(NetButton b) => (Buttons & (uint)b) != 0;
        public void Set(NetButton b, bool on) { if (on) Buttons |= (uint)b; else Buttons &= ~(uint)b; }

        static short Q(float v) => (short)Mathf.RoundToInt(Mathf.Clamp(v, -1f, 1f) * 32767f);

        public void Write(BinaryWriter w)
        {
            w.Write(MoveX); w.Write(MoveY); w.Write(AimX); w.Write(AimY);
            w.Write(Buttons); w.Write((byte)Flags); w.Write(AimPointX); w.Write(AimPointY);
        }

        public static InputFrame Read(BinaryReader r) => new InputFrame
        {
            MoveX = r.ReadInt16(), MoveY = r.ReadInt16(), AimX = r.ReadInt16(), AimY = r.ReadInt16(),
            Buttons = r.ReadUInt32(), Flags = (NetFlags)r.ReadByte(), AimPointX = r.ReadInt16(), AimPointY = r.ReadInt16(),
        };

        public bool Equals(InputFrame o) =>
            MoveX == o.MoveX && MoveY == o.MoveY && AimX == o.AimX && AimY == o.AimY &&
            Buttons == o.Buttons && Flags == o.Flags && AimPointX == o.AimPointX && AimPointY == o.AimPointY;

        public override bool Equals(object obj) => obj is InputFrame f && Equals(f);
        public override int GetHashCode() => (int)Buttons ^ (MoveX << 16) ^ MoveY ^ (AimX << 8) ^ AimY;
        public override string ToString() => $"move={Move} aim={Aim} buttons={(NetButton)Buttons} flags={Flags}";
    }
}
