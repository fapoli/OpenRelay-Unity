using System;
using System.Text;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using UnityEngine;
using UnityEngine.Networking;

namespace OpenRelay {

public class NetworkRelay : MonoBehaviour {
    public struct SessionInfo {
        public string code;
        public string password;
        public uint sessionToken;
        public byte[] peerSecret;
        public int peerIndex;
    }

    public static NetworkRelay instance { get; private set; }

    [SerializeField] private NetworkManager networkManager;
    [SerializeField] private string relayUrl;
    [SerializeField] private string apiKey;
    [SerializeField] private int relayPort = 7777;

    public static string playerName { get; set; } = "Player";

    public void Awake() {
        if (instance != null) { Destroy(gameObject); return; }
        instance = this;
        DontDestroyOnLoad(gameObject);
    }

    /// <summary>
    /// Creates a new session on the relay server and starts as host.
    /// </summary>
    /// <param name="code">Optional session code. A random one is assigned if omitted.</param>
    /// <param name="password">Optional session password.</param>
    public async Task<SessionInfo> host(string code = null, string password = null) {
        var session = await createSession(code, password);
        startNgo(session, isHost: true);
        return session;
    }

    /// <summary>
    /// Joins an existing session on the relay server and starts as client.
    /// </summary>
    /// <param name="code">The session code to join.</param>
    /// <param name="password">The session password.</param>
    public async Task<SessionInfo> join(string code, string password) {
        var session = await joinSession(code, password);
        startNgo(session, isHost: false);
        return session;
    }

    private void startNgo(SessionInfo session, bool isHost) {
        var relay = new RelayNetworkInterface();
        relay.Configure(new Uri(relayUrl).Host, relayPort, session.sessionToken, session.peerSecret);

        var transport = networkManager.GetComponent<UnityTransport>();
        transport.SetConnectionData(RelayNetworkInterface.HostAddress, RelayNetworkInterface.HostPort);
        UnityTransport.s_DriverConstructor = new RelayDriverConstructor(relay);

        if (isHost)
            networkManager.StartHost();
        else
            networkManager.StartClient();
    }

    private async Task<SessionInfo> createSession(string code, string password) {
        var json = await post($"{relayUrl}/sessions", new CreateSessionRequest { code = code, password = password });
        return parseSession(json, includePassword: true);
    }

    private async Task<SessionInfo> joinSession(string code, string password) {
        var json = await post($"{relayUrl}/sessions/{code}/join", new JoinSessionRequest { password = password });
        var session = parseSession(json, includePassword: false);
        session.code = code;
        session.password = password;
        return session;
    }

    private async Task<string> post<T>(string url, T body) {
        using var req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonUtility.ToJson(body)));
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("Authorization", $"Bearer {apiKey}");

        await req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
            throw new Exception($"Request to {url} failed: {req.responseCode} {req.downloadHandler.text}");

        return req.downloadHandler.text;
    }

    private static SessionInfo parseSession(string json, bool includePassword) {
        var parsed = JsonUtility.FromJson<SessionResponse>(json);
        return new SessionInfo {
            code = parsed.code,
            password = includePassword ? parsed.password : null,
            sessionToken = (uint)parsed.sessionToken,
            peerSecret = hexToBytes(parsed.peerSecret),
            peerIndex = parsed.peerIndex,
        };
    }

    private static byte[] hexToBytes(string hex) {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    [Serializable]
    private class CreateSessionRequest {
        public string code;
        public string password;
    }

    [Serializable]
    private class JoinSessionRequest {
        public string password;
    }

    [Serializable]
    private class SessionResponse {
        public string code;
        public string password;
        public long sessionToken;
        public string peerSecret;
        public int peerIndex;
    }
}

}
