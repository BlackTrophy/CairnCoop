using System;
using System.Collections;
using BepInEx.Logging;
using CairnCoop.Protocol;
using CairnCoop.Session;
using CairnCoop.Host;
using CairnCoop.Client;
using CairnCoop.Players;
using CairnCoop.LateJoin;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace CairnCoop.Network
{
    /// <summary>
    /// Central multiplayer coordinator — lives as a DontDestroyOnLoad MonoBehaviour.
    ///
    /// Responsibilities:
    ///   - Transport lifecycle
    ///   - Tick scheduling (20 Hz / 30 Hz / 10 Hz timers)
    ///   - Packet routing
    ///   - Session state
    /// </summary>
    public sealed class NetworkManager : MonoBehaviour
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("CairnCoop.NetworkManager");

        // ----------------------------------------------------------------
        // Singleton
        // ----------------------------------------------------------------
        public static NetworkManager? Instance { get; private set; }

        public static void EnsureExists()
        {
            if (Instance != null) return;
            var go = new GameObject("[CairnCoop.NetworkManager]");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<NetworkManager>();
        }

        // ----------------------------------------------------------------
        // Sub-systems (assigned after Initialize)
        // ----------------------------------------------------------------
        // [HideFromIl2Cpp] MUST be on the accessor methods directly.
        // ClassInjector uses BindingFlags.NonPublic when iterating methods,
        // so neither 'internal' nor an attribute on the PropertyInfo is
        // enough — both approaches still expose get_X()/set_X() to the
        // trampoline builder, which crashes on managed return/parameter types.
        // Solution: explicit backing fields + [HideFromIl2Cpp] on each
        // get/set accessor so the attribute lands on the MethodInfo itself.
        private ITransport?            _transport;
        private SessionState?          _session;
        private PacketRouter?          _router;
        private HostSimulator?         _hostSim;
        private LocalPlayerController? _localPlayer;
        private RemotePlayerManager?   _remotePlayers;
        private LateJoinHandler?       _lateJoin;
        private TickSystem             _ticks = new();

        public ITransport?            Transport    { [HideFromIl2Cpp] get => _transport;      [HideFromIl2Cpp] private set => _transport    = value; }
        public SessionState?          Session      { [HideFromIl2Cpp] get => _session;        [HideFromIl2Cpp] private set => _session      = value; }
        public PacketRouter?          Router       { [HideFromIl2Cpp] get => _router;         [HideFromIl2Cpp] private set => _router       = value; }
        public HostSimulator?         HostSim      { [HideFromIl2Cpp] get => _hostSim;        [HideFromIl2Cpp] private set => _hostSim      = value; }
        public LocalPlayerController? LocalPlayer  { [HideFromIl2Cpp] get => _localPlayer;    [HideFromIl2Cpp] private set => _localPlayer  = value; }
        public RemotePlayerManager?   RemotePlayers{ [HideFromIl2Cpp] get => _remotePlayers;  [HideFromIl2Cpp] private set => _remotePlayers = value; }
        public LateJoinHandler?       LateJoin     { [HideFromIl2Cpp] get => _lateJoin;       [HideFromIl2Cpp] private set => _lateJoin     = value; }
        public TickSystem             Ticks        { [HideFromIl2Cpp] get => _ticks;          [HideFromIl2Cpp] private set => _ticks        = value; }

        public bool IsHost    => Session?.IsHost ?? false;
        public bool IsInGame  => Session?.Phase == SessionPhase.InGame;

        // ----------------------------------------------------------------
        // Timing
        // ----------------------------------------------------------------
        private float _snapshotTimer;
        private float _ikTimer;
        private float _ropeTimer;
        private float _heartbeatTimer;

        private const float SNAPSHOT_INTERVAL  = 1f / 20f;  // 20 Hz
        private const float IK_INTERVAL        = 1f / 30f;  // 30 Hz
        private const float ROPE_INTERVAL      = 1f / 10f;  // 10 Hz
        private const float HEARTBEAT_INTERVAL = 2f;

        // ----------------------------------------------------------------
        // Unity lifecycle
        // ----------------------------------------------------------------
        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        // Start runs after the first scene's Awake() methods — Steam is initialized by then.
        private void Start()
        {
            try
            {
                Initialize();
            }
            catch (Exception ex)
            {
                Log.LogError($"NetworkManager.Start Initialize failed: {ex.Message}");
            }
        }

        private void OnDestroy()
        {
            Transport?.Dispose();
            if (Instance == this) Instance = null;
        }

        // ----------------------------------------------------------------
        // Public API
        // ----------------------------------------------------------------

        /// <summary>
        /// Initialize sub-systems and transport.
        /// Called from Plugin.Load().
        /// </summary>
        public void Initialize()
        {
            Log.LogInfo("NetworkManager initializing...");

            // Build transport (Steam first, EOS fallback)
            try
            {
                Transport = new SteamTransport();
            }
            catch (Exception ex)
            {
                Log.LogWarning($"SteamTransport failed: {ex.Message}");
                // Transport = new EOSTransport();  // Phase 2 — EOS fallback
                // Without transport the plugin is loaded but network ops won't work.
                return;
            }

            Session       = new SessionState(Transport.LocalPeerID);
            Router        = new PacketRouter(this);
            RemotePlayers = new RemotePlayerManager(this);
            LateJoin      = new LateJoinHandler(this);

            // Wire transport events
            Transport.OnPeerConnected    += OnPeerConnected;
            Transport.OnPeerDisconnected += OnPeerDisconnected;
            Transport.OnReceive          += Router.Route;

            Log.LogInfo("NetworkManager ready.");
        }

        /// <summary>Host a session. Returns lobby code (own SteamID64).</summary>
        public string HostSession(int maxPlayers = 8)
        {
            if (Session == null) throw new InvalidOperationException("Not initialized");

            Session.BeginHost(Transport!.LocalPeerID, maxPlayers);

            HostSim   = new HostSimulator(this);
            LocalPlayer = gameObject.AddComponent<LocalPlayerController>();
            LocalPlayer.Initialize(this, 0); // Host is always slot 0

            Log.LogInfo($"Hosting session. Code: {Transport.LocalPeerID.Value}");
            return Transport.LocalPeerID.Value.ToString();
        }

        /// <summary>Join a session as client.</summary>
        [HideFromIl2Cpp]
        public async void JoinSession(string lobbyCode)
        {
            if (Session == null) throw new InvalidOperationException("Not initialized");

            bool ok = await Transport!.ConnectAsync(lobbyCode);
            if (!ok) { Log.LogError("ConnectAsync failed"); return; }

            // Send handshake — slot assignment comes back in HandshakeAck
            SendHandshake(new PeerID(ulong.Parse(lobbyCode)));
        }

        // ----------------------------------------------------------------
        // Update — pump all timed systems
        // ----------------------------------------------------------------
        private void Update()
        {
            if (Transport == null) return;
            Transport.Tick();
            Ticks.Advance(Time.deltaTime);

            if (!IsInGame) return;

            _snapshotTimer  += Time.deltaTime;
            _ikTimer        += Time.deltaTime;
            _ropeTimer      += Time.deltaTime;
            _heartbeatTimer += Time.deltaTime;

            if (_snapshotTimer >= SNAPSHOT_INTERVAL)
            {
                _snapshotTimer -= SNAPSHOT_INTERVAL;
                TickSnapshot();
            }
            if (_ikTimer >= IK_INTERVAL)
            {
                _ikTimer -= IK_INTERVAL;
                TickIK();
            }
            if (_ropeTimer >= ROPE_INTERVAL)
            {
                _ropeTimer -= ROPE_INTERVAL;
                TickRope();
            }
            if (_heartbeatTimer >= HEARTBEAT_INTERVAL)
            {
                _heartbeatTimer -= HEARTBEAT_INTERVAL;
                TickHeartbeat();
            }
        }

        // ----------------------------------------------------------------
        // Timed ticks
        // ----------------------------------------------------------------
        private void TickSnapshot()
        {
            if (IsHost)
                HostSim?.BroadcastWorldSnapshot();
            else
                LocalPlayer?.SendInput();
        }

        private void TickIK()
        {
            LocalPlayer?.SendIKUpdate();
        }

        private void TickRope()
        {
            LocalPlayer?.SendRopeUpdate();
        }

        private void TickHeartbeat()
        {
            if (Transport == null || Session == null) return;
            Span<byte> buf = stackalloc byte[8];
            int offset = 0;
            offset += PacketSerializer.WriteHeader(buf, PacketType.Ping, (byte)Session.LocalSlot);
            var ping = new PingPacket
            {
                Header      = new PacketHeader(PacketType.Ping, (byte)Session.LocalSlot),
                SendTimeMs  = (uint)(Time.time * 1000),
            };
            PacketSerializer.Write(buf, ping);
            Transport.Broadcast(buf.Slice(0, System.Runtime.CompilerServices.Unsafe.SizeOf<PingPacket>()),
                Channel.Connection, SendMode.Reliable);
        }

        // ----------------------------------------------------------------
        // Connection events
        // ----------------------------------------------------------------
        private void OnPeerConnected(PeerID peer)
        {
            Log.LogInfo($"Peer connected: {peer}");
            if (IsHost)
            {
                // Check if this is a rejoin
                if (Session!.TryGetRejoinToken(peer, out var token))
                    LateJoin!.SendInitialState(peer, isRejoin: true);
                else
                    LateJoin!.SendInitialState(peer, isRejoin: false);
            }
        }

        private void OnPeerDisconnected(PeerID peer, DisconnectReason reason)
        {
            Log.LogWarning($"Peer disconnected: {peer} ({reason})");

            if (IsHost)
            {
                Session!.MarkDisconnected(peer, reason);
                RemotePlayers!.DespawnPlayer(peer);
                BroadcastPlayerLeft(peer);
            }
            else if (peer == Session?.HostPeerID)
            {
                Log.LogWarning("Host disconnected — attempting host migration");
                TryMigrateHost();
            }
        }

        // ----------------------------------------------------------------
        // Helper: handshake
        // ----------------------------------------------------------------
        private void SendHandshake(PeerID host)
        {
            var pkt = new HandshakePacket
            {
                Header            = new PacketHeader(PacketType.Handshake, 0xFF),
                ProtocolVersion   = CairnCoop.Protocol.Protocol.Version,
                SteamID           = Transport!.LocalPeerID.Value,
                RequestedPlayerSlot = 0xFF, // any
            };
            Span<byte> buf = stackalloc byte[System.Runtime.CompilerServices.Unsafe.SizeOf<HandshakePacket>()];
            PacketSerializer.Write(buf, pkt);
            Transport.Send(host, buf, Channel.Connection, SendMode.Reliable);
        }

        private void BroadcastPlayerLeft(PeerID peer)
        {
            // Simple reliable event to all remaining clients
            // (PacketRouter handles the detail)
            Router?.BroadcastPlayerLeftEvent(peer);
        }

        private void TryMigrateHost()
        {
            if (Session == null) return;
            var newHost = Session.ElectNewHost();
            if (newHost == Transport?.LocalPeerID)
            {
                Log.LogInfo("We are the new host.");
                Session.BeginHostMigration(newHost);
                HostSim = new HostSimulator(this);
                Router?.BroadcastHostMigration(newHost);
            }
        }

        // ----------------------------------------------------------------
        // Accessors for sub-systems
        // ----------------------------------------------------------------
        public float ServerTime => Ticks.ServerTime;

        /// <summary>Called by PacketRouter after HandshakeAck to register the local player controller.</summary>
        [HideFromIl2Cpp]
        public void SetLocalPlayer(LocalPlayerController lpc) => LocalPlayer = lpc;
    }
}
