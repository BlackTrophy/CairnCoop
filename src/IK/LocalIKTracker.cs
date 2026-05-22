using System.Runtime.CompilerServices;
using BepInEx.Logging;
using CairnCoop.Network;
using CairnCoop.Protocol;
using Il2CppInterop.Runtime.Attributes;
using RootMotion.FinalIK;
using UnityEngine;

namespace CairnCoop.IK
{
    /// <summary>
    /// Added to the LOCAL player pawn GameObject.
    /// Samples Final IK effector targets each frame and sends IKSnapshotPackets
    /// to the host at 30 Hz (driven by NetworkManager.TickIK).
    ///
    /// Hook strategy:
    ///   We READ from FullBodyBipedIK.solver after ClimbingV2RAHManager has set
    ///   the effectors. No patching needed — just read in LateUpdate.
    /// </summary>
    public sealed class LocalIKTracker : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.LocalIKTracker");

        private NetworkManager _net   = null!;
        private byte           _slot;
        private FullBodyBipedIK? _fbik;

        // Last sent values expanded to primitives — IL2CPP ClassInjector crashes on
        // custom struct VALUE TYPE fields, so we cannot store IKSnapshotPacket directly.
        private Vector3 _lastLH, _lastRH, _lastLF, _lastRF;
        private byte    _lastLHW, _lastRHW;
        private const float SEND_THRESHOLD = 0.005f; // 5 mm change required to resend

        [HideFromIl2Cpp]
        public void Initialize(NetworkManager net, int slot)
        {
            _net  = net;
            _slot = (byte)slot;
        }

        private void Start()
        {
            _fbik = GetComponent<FullBodyBipedIK>()
                 ?? GetComponentInChildren<FullBodyBipedIK>()
                 ?? GetComponentInParent<FullBodyBipedIK>();

            if (_fbik == null)
                Log.LogWarning("FullBodyBipedIK not found on local pawn. IK sync disabled.");
        }

        // ----------------------------------------------------------------
        // Called from NetworkManager.TickIK (30 Hz)
        // ----------------------------------------------------------------
        public void SendSnapshot()
        {
            if (_fbik == null || _net.Transport == null) return;

            var snap = BuildSnapshot();

            // Delta suppression — only send if targets moved significantly
            if (!HasSignificantChange(snap)) return;
            _lastLH  = snap.LeftHandTarget.ToUnity();
            _lastRH  = snap.RightHandTarget.ToUnity();
            _lastLF  = snap.LeftFootTarget.ToUnity();
            _lastRF  = snap.RightFootTarget.ToUnity();
            _lastLHW = snap.LHWeight;
            _lastRHW = snap.RHWeight;

            System.Span<byte> buf = stackalloc byte[
                Unsafe.SizeOf<PacketHeader>() + Unsafe.SizeOf<IKSnapshotPacket>()];

            int offset = PacketSerializer.WriteHeader(buf, PacketType.IKUpdate, _slot);
            PacketSerializer.Write(buf.Slice(offset), snap);

            _net.Transport.Send(_net.Session!.HostPeerID, buf,
                Channel.IK, SendMode.Unreliable);
        }

        // ----------------------------------------------------------------
        // Build snapshot from current Final IK state
        // ----------------------------------------------------------------
        [HideFromIl2Cpp]
        private IKSnapshotPacket BuildSnapshot()
        {
            var solver = _fbik!.solver;
            return new IKSnapshotPacket
            {
                Header           = new PacketHeader(PacketType.IKUpdate, _slot),
                Tick             = _net.Ticks.CurrentTick,
                LeftHandTarget   = solver.leftHandEffector.position,
                RightHandTarget  = solver.rightHandEffector.position,
                LeftFootTarget   = solver.leftFootEffector.position,
                RightFootTarget  = solver.rightFootEffector.position,
                LHWeight = (byte)(solver.leftHandEffector.positionWeight  * 255f),
                RHWeight = (byte)(solver.rightHandEffector.positionWeight * 255f),
                LFWeight = (byte)(solver.leftFootEffector.positionWeight  * 255f),
                RFWeight = (byte)(solver.rightFootEffector.positionWeight * 255f),
            };
        }

        [HideFromIl2Cpp]
        private bool HasSignificantChange(in IKSnapshotPacket snap)
        {
            return
                Vector3.Distance(snap.LeftHandTarget.ToUnity(),  _lastLH) > SEND_THRESHOLD ||
                Vector3.Distance(snap.RightHandTarget.ToUnity(), _lastRH) > SEND_THRESHOLD ||
                Vector3.Distance(snap.LeftFootTarget.ToUnity(),  _lastLF) > SEND_THRESHOLD ||
                Vector3.Distance(snap.RightFootTarget.ToUnity(), _lastRF) > SEND_THRESHOLD ||
                Mathf.Abs(snap.LHWeight - _lastLHW) > 5 ||
                Mathf.Abs(snap.RHWeight - _lastRHW) > 5;
        }
    }
}
