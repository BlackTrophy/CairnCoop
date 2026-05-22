using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace CairnCoop.Protocol
{
    // ============================================================
    //  PACKET TYPE ENUM
    // ============================================================
    public enum PacketType : ushort
    {
        // --- Channel 0: Connection (Reliable) ---
        Handshake           = 0x0001,
        HandshakeAck        = 0x0002,
        LobbyInfo           = 0x0003,
        PlayerJoined        = 0x0004,
        PlayerLeft          = 0x0005,
        Ping                = 0x0006,
        Pong                = 0x0007,
        Kick                = 0x0008,
        HostMigration       = 0x0009,

        // --- Channel 1: Events (Reliable) ---
        AnchorPlaced        = 0x0101,
        AnchorRemoved       = 0x0102,
        CheckpointActivated = 0x0103,
        PlayerRespawned     = 0x0104,
        GripChanged         = 0x0105,
        RopeAttached        = 0x0106,
        RopeDetached        = 0x0107,
        ServerCorrection    = 0x0108,

        // --- Channel 2: World Snapshot (Unreliable, 20 Hz) ---
        WorldSnapshot       = 0x0201,

        // --- Channel 3: IK Update (Unreliable, 30 Hz) ---
        IKUpdate            = 0x0301,

        // --- Channel 4: Rope Update (Unreliable, 10 Hz) ---
        RopeUpdate          = 0x0401,

        // --- Channel 5: Late Join (Reliable, fragmented) ---
        LateJoinRequest     = 0x0501,
        LateJoinData        = 0x0502,
        LateJoinComplete    = 0x0503,

        // --- Channel 6: Input (Unreliable, per frame) ---
        InputFrame          = 0x0601,
    }

    public enum Channel : byte
    {
        Connection  = 0,
        Events      = 1,
        Snapshot    = 2,
        IK          = 3,
        Rope        = 4,
        LateJoin    = 5,
        Input       = 6,
    }

    public enum SendMode { Reliable, Unreliable }

    // ============================================================
    //  GAME-STATE ENUMS
    // ============================================================
    public enum ClimberState : byte
    {
        Idle        = 0,
        Climbing    = 1,
        Traversing  = 2,
        Jumping     = 3,
        Falling     = 4,
        Rappelling  = 5,
        Resting     = 6,
        Dead        = 7,
        Spectating  = 8,
    }

    public enum GripState : byte
    {
        None      = 0,
        Holding   = 1,
        Reaching  = 2,
        Releasing = 3,
        Catching  = 4,
    }

    // ============================================================
    //  WIRE STRUCTS  (Pack=1, no padding)
    // ============================================================

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PacketHeader   // 4 bytes
    {
        public PacketType Type;      // 2 B
        public byte       PlayerID;  // 0-7
        public byte       Flags;     // bit0=fragmented, bit1=lastFrag

        public PacketHeader(PacketType t, byte pid, byte flags = 0)
        { Type = t; PlayerID = pid; Flags = flags; }
    }

    // ------------------------------------------------------------------
    //  Player State  (48 B)
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PlayerStatePacket
    {
        public uint         Tick;          // 4 B
        public NetVector3   Position;      // 12 B
        public float        Yaw;           // 4 B
        public NetVector3   Velocity;      // 12 B
        public float        Stamina;       // 4 B  0-1
        public ClimberState ClimbState;    // 1 B
        public byte         GripLeftID;    // Hold-ID (0=none)
        public byte         GripRightID;
        public byte         Flags;         // bit0=falling,bit1=swinging,...
        public uint         Pad;           // align to 48 B total
    }

    // ------------------------------------------------------------------
    //  World Snapshot  (4 header + 4 tick + 4 serverTime + 1 mask + 1 cpMask + 2 pad + 8*48 = 398 B max)
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct WorldSnapshotPacket
    {
        public PacketHeader    Header;
        public uint            Tick;
        public uint            ServerTimeMs;
        public byte            ActivePlayerMask;  // bitmask, bit i = player i connected
        public byte            CheckpointMask;
        public ushort          Pad;
        public fixed byte      PlayerData[8 * 48]; // up to 8 PlayerStatePackets inline

        // Helper: read player i from inline array
        public PlayerStatePacket GetPlayer(int i)
        {
            fixed (byte* p = PlayerData)
                return *(PlayerStatePacket*)(p + i * 48);
        }
        public void SetPlayer(int i, in PlayerStatePacket s)
        {
            fixed (byte* p = PlayerData)
                *(PlayerStatePacket*)(p + i * 48) = s;
        }
    }

    // ------------------------------------------------------------------
    //  IK Snapshot  (52 B)
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct IKSnapshotPacket
    {
        public PacketHeader Header;
        public uint         Tick;
        public NetVector3   LeftHandTarget;   // 12 B
        public NetVector3   RightHandTarget;  // 12 B
        public NetVector3   LeftFootTarget;   // 12 B
        public NetVector3   RightFootTarget;  // 12 B
        public byte         LHWeight;         // 0-255
        public byte         RHWeight;
        public byte         LFWeight;
        public byte         RFWeight;
    }

    // ------------------------------------------------------------------
    //  Rope Snapshot  (~80 B)
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct RopeSnapshotPacket
    {
        public PacketHeader Header;
        public uint         Tick;
        public byte         OwnerPlayerID;
        public byte         IsActive;        // 0 = no rope
        public ushort       Pad;
        public NetVector3   AttachA;         // 12 B (hand)
        public NetVector3   AttachB;         // 12 B (anchor)
        public float        RestLength;
        public float        Tension;         // 0-1
        public NetVector3   CtrlPoint1;      // 12 B
        public NetVector3   CtrlPoint2;      // 12 B
        public NetVector3   CtrlPoint3;      // 12 B
    }

    // ------------------------------------------------------------------
    //  Input Frame  (20 B)
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct InputFramePacket
    {
        public PacketHeader Header;
        public uint         Tick;
        public float        MoveX;
        public float        MoveY;
        public byte         Buttons;     // bit0=jump,bit1=lGrip,bit2=rGrip,bit3=rope
        public byte         LeftTrigger; // 0-255
        public byte         RightTrigger;
        public byte         Pad;
    }

    // ------------------------------------------------------------------
    //  Anchor Event  (36 B)
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct AnchorEventPacket
    {
        public PacketHeader Header;
        public uint         EventID;
        public ushort       AnchorID;
        public byte         EventType;   // 0=placed, 1=removed
        public byte         IsValid;     // 1=host validated
        public NetVector3   Position;    // 12 B
        public NetVector3   Normal;      // 12 B
    }

    // ------------------------------------------------------------------
    //  Server Correction  (32 B)
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ServerCorrectionPacket
    {
        public PacketHeader    Header;
        public uint            CorrectedTick;
        public PlayerStatePacket State;
    }

    // ------------------------------------------------------------------
    //  Handshake  (variable, encoded manually)
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HandshakePacket
    {
        public PacketHeader Header;
        public uint         ProtocolVersion;  // must match
        public ulong        SteamID;
        public byte         RequestedPlayerSlot;
        public byte         Pad0, Pad1, Pad2;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HandshakeAckPacket
    {
        public PacketHeader Header;
        public uint         ProtocolVersion;
        public byte         AssignedPlayerID;  // 0-7 or 0xFF=rejected
        public byte         Pad0, Pad1, Pad2;
    }

    // ------------------------------------------------------------------
    //  Ping / Pong  (12 B)
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct PingPacket
    {
        public PacketHeader Header;
        public uint         SendTimeMs;
    }

    // ------------------------------------------------------------------
    //  Late Join Fragments
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct LateJoinFragmentHeader
    {
        public PacketHeader Header;
        public ushort       FragmentIndex;
        public ushort       TotalFragments;
        public uint         TotalBytes;
        // followed by payload bytes
    }

    // ------------------------------------------------------------------
    //  Vector3 that survives P/Invoke across IL2CPP boundary
    // ------------------------------------------------------------------
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct NetVector3
    {
        public float X, Y, Z;

        public NetVector3(Vector3 v) { X = v.x; Y = v.y; Z = v.z; }
        public Vector3 ToUnity() => new Vector3(X, Y, Z);
        public static implicit operator NetVector3(Vector3 v) => new(v);
        public static implicit operator Vector3(NetVector3 v) => v.ToUnity();
        public static NetVector3 Lerp(NetVector3 a, NetVector3 b, float t)
            => new NetVector3(Vector3.Lerp(a.ToUnity(), b.ToUnity(), t));
    }

    // ============================================================
    //  Const protocol version — bump on breaking wire changes
    // ============================================================
    public static class Protocol
    {
        public const uint Version = 1u;
        public const int  MaxPlayers = 8;
        public const int  MaxFragmentPayload = 1024;
    }
}
