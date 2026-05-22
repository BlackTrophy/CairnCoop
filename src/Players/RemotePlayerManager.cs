using System;
using System.Collections.Generic;
using BepInEx.Logging;
using CairnCoop.Network;
using CairnCoop.Protocol;
using UnityEngine;
// using UnityEngine.AddressableAssets; -- not in interop yet
// using UnityEngine.ResourceManagement.AsyncOperations;

namespace CairnCoop.Players
{
    /// <summary>
    /// Manages the lifecycle of all remote player pawns.
    /// Spawns from the Aava addressable bundle, applies snapshots,
    /// and handles respawn/despawn.
    /// </summary>
    public sealed partial class RemotePlayerManager
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.RemotePlayerManager");

        // Addressable key for the player character prefab
        // Discovered from aava_assets_all.bundle — key to confirm via AssetBundle extraction
        private const string AAVA_PREFAB_KEY = "Assets/Characters/Aava/Aava_Network.prefab";
        // Fallback: try the marco prefab if Aava network variant doesn't exist
        private const string MARCO_PREFAB_KEY = "Assets/Characters/Marco/Marco_Network.prefab";

        private readonly NetworkManager             _net;
        private readonly Dictionary<PeerID, RemotePlayer> _players = new();

        // Colors to differentiate players (applied via MaterialPropertyBlock)
        private static readonly Color[] PlayerColors =
        {
            new Color(0.2f, 0.6f, 1.0f),   // Slot 1: blue
            new Color(1.0f, 0.4f, 0.2f),   // Slot 2: orange
            new Color(0.2f, 1.0f, 0.4f),   // Slot 3: green
            new Color(1.0f, 0.8f, 0.2f),   // Slot 4: yellow
            new Color(0.8f, 0.2f, 1.0f),   // Slot 5: purple
            new Color(1.0f, 0.2f, 0.4f),   // Slot 6: red
            new Color(0.2f, 0.9f, 0.9f),   // Slot 7: cyan
        };

        public RemotePlayerManager(NetworkManager net) { _net = net; }

        // ----------------------------------------------------------------
        // Spawn
        // ----------------------------------------------------------------
        public void SpawnPlayer(PeerID peer, int slot)
        {
            if (_players.ContainsKey(peer))
            {
                Log.LogWarning($"SpawnPlayer called but {peer} already exists.");
                return;
            }

            Log.LogInfo($"Spawning remote player: slot={slot} peer={peer}");

            // Phase 1: Use capsule fallback while Aava Addressable key is unconfirmed.
            // TODO: Replace with Addressables.InstantiateAsync(AAVA_PREFAB_KEY, ...) once
            //       the exact key is extracted from aava_assets_all.bundle via AssetStudio.
            var pawn = SpawnFallbackPawn(slot);
            RegisterPlayer(peer, slot, pawn);
        }

        private void RegisterPlayer(PeerID peer, int slot, GameObject pawn)
        {
            var remote = new RemotePlayer(peer, slot, pawn, _net);
            _players[peer] = remote;
            Log.LogInfo($"Remote player ready: slot={slot}");
        }

        // ----------------------------------------------------------------
        // Despawn
        // ----------------------------------------------------------------
        public void DespawnPlayer(PeerID peer)
        {
            if (!_players.TryGetValue(peer, out var remote)) return;
            remote.Despawn();
            _players.Remove(peer);
        }

        // ----------------------------------------------------------------
        // Snapshot application (called from PacketRouter)
        // ----------------------------------------------------------------
        public void ApplyWorldSnapshot(uint tick, byte activeMask, byte cpMask,
            ReadOnlySpan<PlayerStatePacket> players)
        {
            int localSlot = _net.Session?.LocalSlot ?? -1;
            float srvTime = _net.Ticks.ServerTime;

            for (int i = 0; i < CairnCoop.Protocol.Protocol.MaxPlayers; i++)
            {
                if ((activeMask & (1 << i)) == 0) continue;
                if (i == localSlot) continue; // skip own slot

                var snap = players[i];
                var peer = _net.Session!.GetPeerBySlot(i);
                if (!peer.IsValid) continue;

                if (_players.TryGetValue(peer, out var remote))
                    remote.ApplyPositionSnapshot(snap, srvTime);
            }

            // Update checkpoint mask
            if (_net.Session != null)
                ApplyCheckpointMask(cpMask);
        }

        public void ApplyIKSnapshot(PeerID from, in IKSnapshotPacket snap)
        {
            if (_players.TryGetValue(from, out var remote))
                remote.ApplyIKSnapshot(snap);
        }

        public void ApplyRopeSnapshot(PeerID from, in RopeSnapshotPacket snap)
        {
            if (_players.TryGetValue(from, out var remote))
                remote.ApplyRopeSnapshot(snap);
        }

        public void ApplyAnchorEvent(in AnchorEventPacket pkt)
        {
            if (pkt.IsValid == 0) return;
            GameBridge.SpawnAnchorVisual(pkt.AnchorID, pkt.Position.ToUnity(), pkt.Normal.ToUnity());
        }

        public void UpdateGrip(PeerID peer, byte leftID, byte rightID)
        {
            if (_players.TryGetValue(peer, out var r)) r.UpdateGrip(leftID, rightID);
        }

        public void HandleRopeEvent(PeerID peer, PacketType type)
        {
            if (_players.TryGetValue(peer, out var r)) r.HandleRopeEvent(type);
        }

        public void RespawnPlayer(PeerID peer, int checkpointID)
        {
            if (_players.TryGetValue(peer, out var r)) r.Respawn(checkpointID);
        }

        // ----------------------------------------------------------------
        // LateUpdate — drives interpolation for all remote players
        // ----------------------------------------------------------------
        public void LateUpdate()
        {
            foreach (var remote in _players.Values)
                remote.LateUpdate();
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------
        private void ConfigurePawn(GameObject pawn, int slot)
        {
            // Remove or disable components that shouldn't run on remote pawns
            // Input, local camera, physics controller, etc.
            DestroyIfExists<AudioListener>(pawn);

            // Apply player color via MaterialPropertyBlock
            var renderers = pawn.GetComponentsInChildren<SkinnedMeshRenderer>();
            var mpb = new MaterialPropertyBlock();
            Color color = slot > 0 && slot <= PlayerColors.Length
                ? PlayerColors[slot - 1]
                : Color.white;

            foreach (var r in renderers)
            {
                r.GetPropertyBlock(mpb);
                mpb.SetColor("_PlayerColor", color);
                r.SetPropertyBlock(mpb);
            }

            // Add nameplate
            AddNameplate(pawn, slot);

            // Disable any local-player-specific MonoBehaviours
            // (identified by Harmony pre-scan or known names)
            DisableLocalComponents(pawn);
        }

        private static void AddNameplate(GameObject pawn, int slot)
        {
            // Simple world-space TextMeshPro tag above head
            var nameplateGO = new GameObject("Nameplate");
            nameplateGO.transform.SetParent(pawn.transform, false);
            nameplateGO.transform.localPosition = new Vector3(0, 2.2f, 0);

            // We'll resolve TMPro at runtime to avoid hard reference
            // NOTE: In IL2CPP, AddComponent(System.Type) is not available; we skip nameplate for now.
            // TODO: Use ClassInjector or a known TMPro type once confirmed via interop inspection.
            // var comp = nameplateGO.AddComponent(...);
        }

        private static void DisableLocalComponents(GameObject pawn)
        {
            // Known single-player-only component names to disable
            // NOTE: IL2CPP GetComponent(string) requires Il2CppSystem.Type — iterate and filter by name instead.
            string[] toDisable =
            {
                "CameraController",
                "InputReceiver",
                "HapticsController",
                "LocalPlayerMarker",
            };

            var allComps = pawn.GetComponents<Behaviour>();
            foreach (var b in allComps)
            {
                if (b == null) continue;
                string typeName = b.GetType().Name;
                foreach (string name in toDisable)
                    if (typeName == name) { b.enabled = false; break; }
            }
        }

        private static void DestroyIfExists<T>(GameObject go) where T : Component
        {
            var c = go.GetComponentInChildren<T>();
            if (c != null) UnityEngine.Object.Destroy(c);
        }

        private static GameObject SpawnFallbackPawn(int slot)
        {
            // Capsule proxy for Phase 1 testing
            var go = GameObject.CreatePrimitive(PrimitiveType.Capsule);
            go.name = $"[RemotePlayer_Slot{slot}_Fallback]";
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.material.color = slot < PlayerColors.Length
                ? PlayerColors[slot - 1] : Color.gray;
            return go;
        }

        private static void ApplyCheckpointMask(byte mask)
        {
            for (int i = 0; i < 8; i++)
                if ((mask & (1 << i)) != 0)
                    GameBridge.ActivateCheckpoint(i);
        }
    }
}
