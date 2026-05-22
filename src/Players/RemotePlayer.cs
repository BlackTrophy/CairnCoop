using BepInEx.Logging;
using CairnCoop.IK;
using CairnCoop.Interpolation;
using CairnCoop.Network;
using CairnCoop.Protocol;
using CairnCoop.Rope;
using UnityEngine;

namespace CairnCoop.Players
{
    /// <summary>
    /// Represents a single remote player:
    ///   - Owns their pawn GameObject
    ///   - Drives position/rotation interpolation
    ///   - Delegates IK to RemoteIKController
    ///   - Delegates rope to RemoteRopeController
    /// </summary>
    public sealed partial class RemotePlayer
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.RemotePlayer");

        public PeerID PeerID  { get; }
        public int    Slot    { get; }
        public bool   IsAlive { get; private set; } = true;

        private readonly GameObject       _pawn;
        private readonly Animator         _animator;
        private readonly RemoteIKController _ik;
        private readonly RemoteRopeController _rope;

        // Position interpolator
        private readonly SnapshotInterpolator<PosSnapshot> _posInterp;

        // Animator parameter IDs (cached)
        private static readonly int Anim_ClimbState = Animator.StringToHash("ClimbState");
        private static readonly int Anim_VelX       = Animator.StringToHash("VelocityX");
        private static readonly int Anim_VelY       = Animator.StringToHash("VelocityY");
        private static readonly int Anim_Stamina    = Animator.StringToHash("Stamina");
        private static readonly int Anim_IsFalling  = Animator.StringToHash("IsFalling");

        private readonly NetworkManager _net;

        public RemotePlayer(PeerID peer, int slot, GameObject pawn, NetworkManager net)
        {
            PeerID = peer;
            Slot   = slot;
            _pawn  = pawn;
            _net   = net;

            _animator = pawn.GetComponent<Animator>()
                     ?? pawn.GetComponentInChildren<Animator>()!;

            _ik   = pawn.AddComponent<RemoteIKController>();
            _rope = pawn.AddComponent<RemoteRopeController>();

            _posInterp = new SnapshotInterpolator<PosSnapshot>(LerpPosSnapshot);

            Log.LogInfo($"RemotePlayer spawned: slot={slot} peer={peer}");
        }

        // ----------------------------------------------------------------
        // Called from RemotePlayerManager every frame (in LateUpdate)
        // ----------------------------------------------------------------
        public void LateUpdate()
        {
            float renderTime = _net.Ticks.RenderTime;

            if (_posInterp.TryGetInterpolated(renderTime, out var pos))
            {
                _pawn.transform.position = pos.Position;
                _pawn.transform.rotation = Quaternion.Euler(0, pos.Yaw, 0);
                UpdateAnimator(pos);
            }
        }

        // ----------------------------------------------------------------
        // Snapshot application
        // ----------------------------------------------------------------
        public void ApplyPositionSnapshot(PlayerStatePacket snap, float serverTime)
        {
            _posInterp.AddSnapshot(new PosSnapshot(snap), serverTime);
        }

        public void ApplyIKSnapshot(in IKSnapshotPacket snap)
        {
            _ik.ReceiveSnapshot(snap, _net.Ticks.RenderTime);
        }

        public void ApplyRopeSnapshot(in RopeSnapshotPacket snap)
        {
            _rope.ReceiveSnapshot(snap);
        }

        public void UpdateGrip(byte leftID, byte rightID)
        {
            // Could drive additional grip-hold-highlight visuals here
        }

        public void HandleRopeEvent(PacketType type)
        {
            _rope.HandleRopeEvent(type);
        }

        public void Respawn(int checkpointID)
        {
            var spawnPos = GameBridge.GetCheckpointPosition(checkpointID);
            _pawn.transform.position = spawnPos;
            IsAlive = true;
            _pawn.SetActive(true);
        }

        public void Despawn()
        {
            IsAlive = false;
            if (_pawn != null) Object.Destroy(_pawn);
        }

        // ----------------------------------------------------------------
        // Animator
        // ----------------------------------------------------------------
        private void UpdateAnimator(PosSnapshot pos)
        {
            if (_animator == null) return;
            _animator.SetInteger(Anim_ClimbState, pos.ClimbState);
            _animator.SetFloat(Anim_VelX,  pos.VelX);
            _animator.SetFloat(Anim_VelY,  pos.VelY);
            _animator.SetFloat(Anim_Stamina, pos.Stamina);
            _animator.SetBool(Anim_IsFalling, (pos.Flags & 0x01) != 0);
        }

        // ----------------------------------------------------------------
        // Lerp delegate for SnapshotInterpolator
        // ----------------------------------------------------------------
        private static PosSnapshot LerpPosSnapshot(PosSnapshot a, PosSnapshot b, float t) => new()
        {
            Position   = Vector3.Lerp(a.Position, b.Position, t),
            Yaw        = Mathf.LerpAngle(a.Yaw, b.Yaw, t),
            VelX       = Mathf.Lerp(a.VelX, b.VelX, t),
            VelY       = Mathf.Lerp(a.VelY, b.VelY, t),
            Stamina    = Mathf.Lerp(a.Stamina, b.Stamina, t),
            ClimbState = t < 0.5f ? a.ClimbState : b.ClimbState,
            Flags      = t < 0.5f ? a.Flags : b.Flags,
        };

        // ----------------------------------------------------------------
        // Internal snapshot value-type
        // ----------------------------------------------------------------
        private struct PosSnapshot
        {
            public Vector3 Position;
            public float   Yaw;
            public float   VelX, VelY;
            public float   Stamina;
            public int     ClimbState;
            public byte    Flags;

            public PosSnapshot(PlayerStatePacket p)
            {
                Position   = p.Position.ToUnity();
                Yaw        = p.Yaw;
                VelX       = p.Velocity.X;
                VelY       = p.Velocity.Y;
                Stamina    = p.Stamina;
                ClimbState = (int)p.ClimbState;
                Flags      = p.Flags;
            }
        }
    }
}
