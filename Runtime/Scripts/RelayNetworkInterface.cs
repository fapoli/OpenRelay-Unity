using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Networking.Transport;

namespace OpenRelay {

/// <summary>
/// INetworkInterface that routes all UTP traffic through an openrelay UDP relay.
/// Send: prepends [4 bytes sessionToken BE][16 bytes peerSecret] to every packet.
/// Receive: strips the [4 bytes senderIndex] the relay prepends, maps it to a synthetic NetworkEndpoint.
/// </summary>
public struct RelayNetworkInterface : INetworkInterface {
    private int _stateId;
    private const ushort SyntheticPort = 7777;

    public const string HostAddress = "10.0.0.1";
    public const ushort HostPort = SyntheticPort;

    public static NetworkEndpoint HostEndpoint => NetworkEndpoint.Parse(HostAddress, HostPort);

    private State S => State.Registry[_stateId];

    public NetworkEndpoint LocalEndpoint => NetworkEndpoint.AnyIpv4;

    /// <summary>
    /// Configures the relay endpoint and builds the packet header for this peer.
    /// Must be called before the interface is bound.
    /// </summary>
    /// <param name="relayHost">Hostname or IP of the relay server.</param>
    /// <param name="relayPort">UDP port of the relay server.</param>
    /// <param name="sessionToken">Token identifying the session on the relay.</param>
    /// <param name="peerSecret">16-byte secret identifying this peer within the session.</param>
    public void Configure(string relayHost, int relayPort, uint sessionToken, byte[] peerSecret) {
        var s = new State();
        var addresses = Dns.GetHostAddresses(relayHost);
        s.RelayEndpoint = new IPEndPoint(addresses[0], relayPort);
        s.RecvBuffer = new byte[4096];

        s.SendHeader = new byte[20];
        s.SendHeader[0] = (byte)(sessionToken >> 24);
        s.SendHeader[1] = (byte)(sessionToken >> 16);
        s.SendHeader[2] = (byte)(sessionToken >> 8);
        s.SendHeader[3] = (byte)(sessionToken);
        System.Buffer.BlockCopy(peerSecret, 0, s.SendHeader, 4, 16);

        _stateId = State.Register(s);
    }

    public int Initialize(ref NetworkSettings settings, ref int packetPadding) => 0;

    public int Bind(NetworkEndpoint endpoint) {
        var s = S;
        s.Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        s.Socket.Blocking = false;
        s.Socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        return 0;
    }

    public int Listen() => 0;

    public unsafe JobHandle ScheduleSend(ref SendJobArguments arguments, JobHandle dep) {
        dep.Complete();
        var s = S;

        var now = System.Environment.TickCount;
        if (!s.Registered || now - s.LastKeepaliveTick > 3000) {
            var firstPing = !s.Registered;
            s.Registered = true;
            s.LastKeepaliveTick = now;
            try {
                s.Socket.SendTo(s.SendHeader, s.RelayEndpoint);
                if (firstPing) UnityEngine.Debug.Log($"[Relay] Registration ping sent to {s.RelayEndpoint}");
            } catch (SocketException ex) {
                UnityEngine.Debug.LogWarning($"[Relay] Keepalive failed: {ex.SocketErrorCode}");
            }
        }

        var queue = arguments.SendQueue;
        var count = queue.Count;

        for (int i = 0; i < count; i++) {
            var packet = queue[i];
            if (packet.Length == 0) continue;

            var sendBuf = new byte[20 + packet.Length];
            System.Buffer.BlockCopy(s.SendHeader, 0, sendBuf, 0, 20);

            fixed (byte* dst = sendBuf)
                UnsafeUtility.MemCpy(dst + 20,
                    (byte*)packet.GetUnsafePayloadPtr() + packet.Offset,
                    packet.Length);

            try { s.Socket.SendTo(sendBuf, s.RelayEndpoint); }
            catch (SocketException) { }
        }

        return default;
    }

    public unsafe JobHandle ScheduleReceive(ref ReceiveJobArguments arguments, JobHandle dep) {
        dep.Complete();
        var s = S;

        while (true) {
            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
            int received;
            try { received = s.Socket.ReceiveFrom(s.RecvBuffer, ref from); }
            catch (SocketException ex) {
                if (ex.SocketErrorCode != SocketError.WouldBlock && ex.SocketErrorCode != SocketError.TryAgain)
                    UnityEngine.Debug.LogWarning($"[Relay] ReceiveFrom error: {ex.SocketErrorCode}");
                break;
            }

            if (received < 4) continue;

            var senderIndex = (uint)(
                (s.RecvBuffer[0] << 24) |
                (s.RecvBuffer[1] << 16) |
                (s.RecvBuffer[2] << 8)  |
                 s.RecvBuffer[3]);

            if (received == 4) continue;

            if (!s.IndexToEndpoint.TryGetValue(senderIndex, out var endpoint)) {
                endpoint = NetworkEndpoint.Parse(
                    $"10.{(senderIndex >> 8) & 0xFF}.{senderIndex & 0xFF}.1",
                    SyntheticPort);
                s.IndexToEndpoint[senderIndex] = endpoint;
            }

            if (!arguments.ReceiveQueue.EnqueuePacket(out var packetProcessor)) continue;

            fixed (byte* src = s.RecvBuffer)
                packetProcessor.AppendToPayload(src + 4, received - 4);

            packetProcessor.EndpointRef = endpoint;
        }

        return default;
    }

    public void Dispose() {
        if (State.Registry.TryGetValue(_stateId, out var s))
            s.Socket?.Close();
        State.Unregister(_stateId);
    }

    private class State {
        public static readonly Dictionary<int, State> Registry = new();
        private static int _nextId;

        public Socket Socket;
        public IPEndPoint RelayEndpoint;
        public byte[] SendHeader;
        public byte[] RecvBuffer;
        public Dictionary<uint, NetworkEndpoint> IndexToEndpoint = new();
        public bool Registered;
        public int LastKeepaliveTick;

        public static int Register(State state) {
            var id = _nextId++;
            Registry[id] = state;
            return id;
        }

        public static void Unregister(int id) => Registry.Remove(id);
    }
}

}
