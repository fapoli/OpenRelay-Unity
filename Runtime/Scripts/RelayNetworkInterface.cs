using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Networking.Transport;

namespace OpenRelay {

/// <summary>
/// INetworkInterface that routes all UTP traffic through an openrelay UDP relay.
/// Send: prepends [4 bytes sessionToken BE][4 bytes peerToken][1 byte targetPeerIndex] to every packet.
/// Receive: strips the [4 bytes senderIndex] the relay prepends, maps it to synthetic endpoint 10.0.0.{senderIndex}.
/// </summary>
public struct RelayNetworkInterface : INetworkInterface {
    private int _stateId;
    private const ushort SyntheticPort = 7777;

    public const string HostAddress = "10.0.0.0";
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
    /// <param name="peerToken">4-byte token identifying this peer within the session.</param>
    public void Configure(string relayHost, int relayPort, uint sessionToken, byte[] peerToken, byte peerIndex) {
        var s = new State();
        var addresses = Dns.GetHostAddresses(relayHost);
        s.RelayEndpoint = new IPEndPoint(addresses[0], relayPort);
        s.RecvBuffer = new byte[4096];

        s.SendHeader = new byte[9];
        s.SendHeader[0] = (byte)(sessionToken >> 24);
        s.SendHeader[1] = (byte)(sessionToken >> 16);
        s.SendHeader[2] = (byte)(sessionToken >> 8);
        s.SendHeader[3] = (byte)(sessionToken);
        System.Buffer.BlockCopy(peerToken, 0, s.SendHeader, 4, 4);
        s.SendHeader[8] = peerIndex;

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

            var sendBuf = new byte[9 + packet.Length];
            System.Buffer.BlockCopy(s.SendHeader, 0, sendBuf, 0, 9);

            // Parse peerIndex from synthetic endpoint IP (10.0.0.{peerIndex})
            var epStr = packet.EndpointRef.ToString(); // "10.0.0.X:port"
            var lastDot = epStr.LastIndexOf('.');
            var lastColon = epStr.LastIndexOf(':');
            if (lastDot >= 0 && lastColon > lastDot &&
                byte.TryParse(epStr.Substring(lastDot + 1, lastColon - lastDot - 1), out var peerIdx))
                sendBuf[8] = peerIdx;
            // else byte 8 remains our own peerIndex (copied from SendHeader) — relay skips sender, nothing forwarded

            fixed (byte* dst = sendBuf)
                UnsafeUtility.MemCpy(dst + 9,
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

            var endpoint = NetworkEndpoint.Parse($"10.0.0.{senderIndex}", SyntheticPort);

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
