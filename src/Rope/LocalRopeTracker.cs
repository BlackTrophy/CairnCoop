using System;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using CairnCoop.Network;
using CairnCoop.Protocol;
using Il2CppInterop.Runtime.Attributes;
using Obi;
using UnityEngine;

namespace CairnCoop.Rope
{
    /// <summary>
    /// Added to the LOCAL player pawn GameObject.
    ///
    /// Samples the active ObiRope each tick (10 Hz) and sends a RopeSnapshotPacket
    /// to the host.  The snapshot encodes 3 Catenary control points, the attach
    /// positions, rest length, and tension — enough for remote clients to render
    /// a plausible rope without running a full Obi simulation.
    ///
    /// Obi access notes:
    ///   - ObiSolver.positions  → NativeList<float4>  (Obi 6.x)
    ///   - Access ONLY after ObiSolver.simulateStep event (Jobs complete)
    ///   - We subscribe to ObiSolver.OnSolverStepEnd
    /// </summary>
    public sealed class LocalRopeTracker : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.LocalRopeTracker");

        private NetworkManager _net  = null!;
        private byte           _slot;

        private ObiRope?   _rope;
        private ObiSolver? _solver;
        private bool       _ropeReady;

        private bool _lastActive;

        [HideFromIl2Cpp]
        public void Initialize(NetworkManager net, int slot)
        {
            _net  = net;
            _slot = (byte)slot;
        }

        private void Start()
        {
            // Obi components live on the rope GameObject, not necessarily the pawn.
            // We find them by searching for ObiSolverManager via GameBridge.
            _solver = GameBridge.GetLocalObiSolver();
            if (_solver != null)
            {
                // ObiSolver.OnSolverStepEnd is not available in the generated interop.
                // Instead we sample rope state each LateUpdate (after Obi simulation step).
                Log.LogDebug("LocalRopeTracker: ObiSolver found, will sample in LateUpdate.");
            }
            else
            {
                Log.LogWarning("ObiSolver not found — rope sync disabled.");
            }
        }

        private void OnDestroy()
        {
            // No event subscription to clean up
        }

        // ----------------------------------------------------------------
        // Sample rope state each LateUpdate (after physics/Obi step)
        // Pending state stored as primitives — IL2CPP ClassInjector crashes on
        // custom struct VALUE TYPE fields on registered MonoBehaviours.
        // ----------------------------------------------------------------
        private bool    _hasPending;
        private Vector3 _pendingAttachA, _pendingAttachB;
        private Vector3 _pendingCtrl1,   _pendingCtrl2,  _pendingCtrl3;
        private float   _pendingRestLen,  _pendingTension;

        private void LateUpdate()
        {
            if (_solver == null) return;

            _rope = GameBridge.GetPlayerRope();
            if (_rope == null || !_rope.isActiveAndEnabled)
            {
                _hasPending = false;
                return;
            }

            try
            {
                var snap        = SampleRopeState();
                _pendingAttachA = snap.AttachA.ToUnity();
                _pendingAttachB = snap.AttachB.ToUnity();
                _pendingCtrl1   = snap.CtrlPoint1.ToUnity();
                _pendingCtrl2   = snap.CtrlPoint2.ToUnity();
                _pendingCtrl3   = snap.CtrlPoint3.ToUnity();
                _pendingRestLen = snap.RestLength;
                _pendingTension = snap.Tension;
                _hasPending     = true;
            }
            catch (Exception ex)
            {
                Log.LogError($"Rope sample failed: {ex.Message}");
                _hasPending = false;
            }
        }

        // ----------------------------------------------------------------
        // Called from NetworkManager.TickRope (10 Hz)
        // ----------------------------------------------------------------
        public void SendSnapshot()
        {
            if (_net.Transport == null) return;

            if (!_hasPending)
            {
                // Rope is inactive
                if (_lastActive)
                {
                    // Send "rope deactivated" packet
                    SendInactiveRope();
                    _lastActive = false;
                }
                return;
            }

            _lastActive = true;
            // Rebuild packet from primitive fields (struct fields not allowed on registered MonoBehaviours)
            var snap = new RopeSnapshotPacket
            {
                Header        = new PacketHeader(PacketType.RopeUpdate, _slot),
                Tick          = _net.Ticks.CurrentTick,
                OwnerPlayerID = _slot,
                IsActive      = 1,
                AttachA       = _pendingAttachA,
                AttachB       = _pendingAttachB,
                CtrlPoint1    = _pendingCtrl1,
                CtrlPoint2    = _pendingCtrl2,
                CtrlPoint3    = _pendingCtrl3,
                RestLength    = _pendingRestLen,
                Tension       = _pendingTension,
            };
            _hasPending = false;

            Span<byte> buf = stackalloc byte[
                Unsafe.SizeOf<PacketHeader>() + Unsafe.SizeOf<RopeSnapshotPacket>()];

            int offset = PacketSerializer.WriteHeader(buf, PacketType.RopeUpdate, _slot);
            PacketSerializer.Write(buf.Slice(offset), snap);

            _net.Transport.Send(_net.Session!.HostPeerID, buf,
                Channel.Rope, SendMode.Unreliable);
        }

        private void SendInactiveRope()
        {
            var snap = new RopeSnapshotPacket
            {
                Header        = new PacketHeader(PacketType.RopeUpdate, _slot),
                Tick          = _net.Ticks.CurrentTick,
                OwnerPlayerID = _slot,
                IsActive      = 0,
            };

            Span<byte> buf = stackalloc byte[
                Unsafe.SizeOf<PacketHeader>() + Unsafe.SizeOf<RopeSnapshotPacket>()];
            int offset = PacketSerializer.WriteHeader(buf, PacketType.RopeUpdate, _slot);
            PacketSerializer.Write(buf.Slice(offset), snap);
            _net.Transport!.Send(_net.Session!.HostPeerID, buf,
                Channel.Rope, SendMode.Unreliable);
        }

        // ----------------------------------------------------------------
        // Sample current rope state from Obi
        // ----------------------------------------------------------------
        [HideFromIl2Cpp]
        private RopeSnapshotPacket SampleRopeState()
        {
            // Obi 6.x: solver.positions is a NativeList<float4>
            // Index maps: rope.solverIndices maps rope particles to solver indices
            // In IL2CPP interop, solverIndices is Il2CppStructArray<int> (use .Length not .count)
            var positions = _solver!.positions;
            var solverIdx = _rope!.solverIndices;
            int count     = solverIdx.Length;

            if (count < 4) throw new Exception($"Too few rope particles: {count}");

            // Endpoints
            Vector3 pA = GetParticlePos(positions, solverIdx[0]);
            Vector3 pB = GetParticlePos(positions, solverIdx[count - 1]);

            // 3 evenly distributed control points
            Vector3 cp1 = GetParticlePos(positions, solverIdx[count / 4]);
            Vector3 cp2 = GetParticlePos(positions, solverIdx[count / 2]);
            Vector3 cp3 = GetParticlePos(positions, solverIdx[3 * count / 4]);

            float tension = CalculateTension(positions, solverIdx, count);
            float restLen = _rope.restLength;

            return new RopeSnapshotPacket
            {
                Header        = new PacketHeader(PacketType.RopeUpdate, _slot),
                Tick          = _net.Ticks.CurrentTick,
                OwnerPlayerID = _slot,
                IsActive      = 1,
                AttachA       = pA,
                AttachB       = pB,
                RestLength    = restLen,
                Tension       = tension,
                CtrlPoint1    = cp1,
                CtrlPoint2    = cp2,
                CtrlPoint3    = cp3,
            };
        }

        private static Vector3 GetParticlePos(
            ObiNativeVector4List positions, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> indices, int particleIdx)
        {
            int solverIdx = indices[particleIdx];
            var p4 = positions[solverIdx];
            return new Vector3(p4.x, p4.y, p4.z);
        }

        // Overload for direct solver index
        private static Vector3 GetParticlePos(ObiNativeVector4List positions, int solverIdx)
        {
            var p4 = positions[solverIdx];
            return new Vector3(p4.x, p4.y, p4.z);
        }

        private static float CalculateTension(
            ObiNativeVector4List positions, Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<int> indices, int count)
        {
            float actualLen = 0f;
            for (int i = 1; i < count; i++)
            {
                Vector3 a = GetParticlePos(positions, indices[i - 1]);
                Vector3 b = GetParticlePos(positions, indices[i]);
                actualLen += Vector3.Distance(a, b);
            }

            // rope is attached, reference by restLength
            // We'd need restLength here — pass via field in future refactor
            float stretch = actualLen > 0 ? actualLen : 1f;
            return Mathf.Clamp01(stretch - 1.0f); // normalised stretch
        }
    }
}
