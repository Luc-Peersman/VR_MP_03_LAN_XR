using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Unity.Netcode;
using Unity.XR.CoreUtils.Bindings.Variables;
using UnityEngine;
using UnityEditor;
using Unity.Services.Multiplayer;
using Unity.Netcode.Transports.UTP;
using System.Net.Sockets;
using System.Net;
using System.Text;

#if UNITY_EDITOR && HAS_MPPM

#endif

namespace XRMultiplayer
{
    /// <summary>
    /// Manages the high level connection for a networked game session.
    /// </summary>
    [RequireComponent(typeof(SessionManager)), RequireComponent(typeof(AuthenticationManager))]
    public class XRINetworkGameManager : MonoBehaviour
    {
        public enum ConnectionState
        {
            None,
            Authenticating,
            Authenticated,
            Connecting,
            Connected
        }

        public const int maxPlayers = 20;

        public static XRINetworkGameManager Instance => s_Instance;
        static XRINetworkGameManager s_Instance;

        public static ulong LocalId;
        public static string AuthenicationId;
        public static string ConnectedRoomCode;
        public static string ConnectedRoomRegion;
        public static BindableVariable<string> ConnectedRoomName = new("");
        public static BindableVariable<string> LocalPlayerName = new("Player");
        public static BindableVariable<Color> LocalPlayerColor = new(Color.white);

        public static IReadOnlyBindableVariable<bool> Connected
        {
            get => m_Connected;
        }
        static BindableVariable<bool> m_Connected = new BindableVariable<bool>(false);

        public static IReadOnlyBindableVariable<ConnectionState> CurrentConnectionState
        {
            get => m_ConnectionState;
        }
        static BindableEnum<ConnectionState> m_ConnectionState = new BindableEnum<ConnectionState>(ConnectionState.None);

        public static SessionType CurrentSessionType
        {
            get
            {
                return SessionType.LocalOnly;
            }
        }

        public bool autoConnectOnLobbyJoin { get => m_AutoConnectOnLobbyJoin; }
        [SerializeField] bool m_AutoConnectOnLobbyJoin = true;

        public bool positionalVoiceChat = false;

        public Action<ulong, bool> OnPlayerStateChanged;
        public Action<string> OnConnectionUpdated;
        public Action<string> OnConnectionFailedAction;
        public Action<ulong> OnSessionOwnerPromoted;

        // ---------------------------------------------------------------
        // LAN DISCOVERY - toegevoegd voor gesloten LAN-only opzet (geen Relay/Lobby)
        // ---------------------------------------------------------------
        [Header("LAN Discovery")]
        [Tooltip("UDP poort voor discovery-broadcast. Moet afwijken van je UTP game-poort (meestal 7777).")]
        [SerializeField] ushort m_DiscoveryPort = 47777;

        [Tooltip("Hoe vaak (seconden) een zoekende client een broadcast-ping verstuurt.")]
        [SerializeField] float m_DiscoveryBroadcastInterval = 1.0f;

        UdpClient m_DiscoveryServerSocket;   // host: luistert naar client-pings
        UdpClient m_DiscoveryClientSocket;   // client: zoekt naar host
        bool m_IsBroadcastingAsHost = false;
        bool m_IsSearchingForHost = false;

        const string k_DiscoveryRequestTag = "XRVR_DISCOVERY_REQUEST";
        const string k_DiscoveryResponseTag = "XRVR_DISCOVERY_RESPONSE";

        /// <summary>
        /// Wordt aangeroepen zodra de client tijdens het zoeken een host heeft gevonden op het LAN.
        /// Params: (ip, port, roomName)
        /// </summary>
        public Action<string, ushort, string> OnLocalHostFound;

        public SessionManager sessionManager => m_SessionManager;
        SessionManager m_SessionManager;

        public AuthenticationManager authenticationManager => m_AuthenticationManager;
        AuthenticationManager m_AuthenticationManager;

        readonly List<ulong> m_CurrentPlayerIDs = new();

        bool m_IsShuttingDown = false;

        const string k_DebugPrepend = "<color=#FAC00C>[Network Game Manager]</color> ";

        protected virtual async void Awake()
        {
            if (s_Instance != null)
            {
                Utils.Log($"{k_DebugPrepend}Duplicate XRINetworkGameManager found, destroying.", 2);
                Destroy(gameObject);
                return;
            }
            s_Instance = this;

            if (TryGetComponent(out m_SessionManager) && TryGetComponent(out m_AuthenticationManager))
            {
                m_SessionManager.OnSessionFailed += ConnectionFailed;
            }
            else
            {
                Utils.Log($"{k_DebugPrepend}Missing Managers, Disabling Component", 2);
                enabled = false;
                return;
            }

#if UNITY_EDITOR
            bool skipCloudCheck = false;
# if HAS_MPPM
            if (!Unity.Multiplayer.PlayMode.CurrentPlayer.IsMainEditor)
            {
                skipCloudCheck = true;
            }
# endif
            if (!CloudProjectSettings.projectBound && !skipCloudCheck)
            {
                Utils.Log($"{k_DebugPrepend}Project has not been linked to Unity Cloud." +
                               "\nThe VR Multiplayer Template utilizes Unity Gaming Services and must be linked to Unity Cloud." +
                               "\nGo to <b>Settings -> Project Settings -> Services</b> and link your project.", 2);
            }
#endif

            m_Connected.Value = false;
            m_ConnectionState.Value = ConnectionState.Authenticating;

            if (CurrentSessionType == SessionType.DistributedAuthority)
            {
                bool signedIn = await m_AuthenticationManager.Authenticate();
                if (!signedIn)
                {
                    Utils.Log($"{k_DebugPrepend}Failed to Authenticate.", 1);
                    ConnectionFailed("Failed to Authenticate.");
                    PlayerHudNotification.Instance.ShowText($"Failed to Authenticate.");
                    return;
                }
            }

            m_ConnectionState.Value = ConnectionState.Authenticated;
        }

        protected virtual void Start()
        {
            NetworkManager.Singleton.OnClientStopped += LocalClientStopped;
            NetworkManager.Singleton.OnSessionOwnerPromoted += SessionOwnerPromoted;
        }

        void SessionOwnerPromoted(ulong sessionOwnerId)
        {
            OnSessionOwnerPromoted?.Invoke(sessionOwnerId);
            if (TryGetPlayerByID(sessionOwnerId, out XRINetworkPlayer player))
            {
                PlayerHudNotification.Instance.ShowText($"<b>Status:</b> {player.playerName} now the Host.");
            }
        }

        public void OnDestroy()
        {
            ShutDown();
            StopLanDiscoveryBroadcast();
            StopSearchingForLocalHost();
        }

        private void OnApplicationQuit()
        {
            ShutDown();
            StopLanDiscoveryBroadcast();
            StopSearchingForLocalHost();
        }

        async void ShutDown()
        {
            if (m_IsShuttingDown) return;
            m_IsShuttingDown = true;

            if (NetworkManager.Singleton != null)
            {
                NetworkManager.Singleton.OnClientStopped -= LocalClientStopped;
            }

            await m_SessionManager.LeaveSession();
        }

        public bool IsAuthenticated()
        {
            return m_SessionManager.sessionType != SessionType.DistributedAuthority || AuthenticationManager.IsAuthenticated();
        }

        public virtual void OnLocalClientStarted(ulong localPlayerId)
        {
            LocalId = localPlayerId;
            m_Connected.Value = true;
            m_ConnectionState.Value = ConnectionState.Connected;
            PlayerHudNotification.Instance.ShowText($"<b>Status:</b> Connected");
            Utils.Log($"{k_DebugPrepend}Local Player Started with ID: {localPlayerId}", 0);
        }

        protected virtual void LocalClientStopped(bool id)
        {
            m_Connected.Value = false;
            m_CurrentPlayerIDs.Clear();
            PlayerHudNotification.Instance.ShowText($"<b>Status:</b> Disconnected");
            if (IsAuthenticated())
            {
                m_ConnectionState.Value = ConnectionState.Authenticated;
            }
            else
            {
                m_ConnectionState.Value = ConnectionState.None;
            }

            // Stop discovery-processen zodra we niet meer gehost/verbonden zijn.
            StopLanDiscoveryBroadcast();
            StopSearchingForLocalHost();
        }

        public virtual bool TryGetPlayerByID(ulong id, out XRINetworkPlayer player)
        {
            XRINetworkPlayer[] allPlayers = FindObjectsByType<XRINetworkPlayer>(FindObjectsSortMode.None);
            foreach (XRINetworkPlayer p in allPlayers)
            {
                if (p.NetworkObject.OwnerClientId == id)
                {
                    player = p;
                    return true;
                }
            }
            player = null;
            return false;
        }

        [ContextMenu("Show All NetworkClients")]
        void ShowAllNetworkClients()
        {
            foreach (var client in NetworkManager.Singleton.ConnectedClients)
            {
                Debug.Log($"Client: {client.Key}, {client.Value.PlayerObject.name}");
            }
        }

        [Header("Spawn In Line")]
        [Tooltip("Sleep hier een (leeg) GameObject in de scene - de positie/rotatie van DAT " +
                 "object bepaalt waar/hoe de EERSTE speler (host) spawnt. Positioneer het " +
                 "visueel in de Scene-view op de plek waar de eerste speler fysiek moet staan " +
                 "voordat de productie gestart wordt.")]
        [SerializeField] Transform m_FirstSpawnPointObject;

        [Tooltip("Richting waarin volgende spelers achter elkaar spawnen (bv. (0,0,-1) = " +
                 "1 meter naar achteren t.o.v. de wereld-Z-as). Moet overeenkomen met de " +
                 "richting waarin studenten zich fysiek opstellen.")]
        [SerializeField] Vector3 m_SpawnLineDirection = new Vector3(0f, 0f, -1f);

        [Tooltip("Afstand in meters tussen elke speler in de rij.")]
        [SerializeField] float m_SpawnSpacing = 1f;

        /// <summary>
        /// Berekent de spawn-positie in de rij op basis van de volgorde van binnenkomst
        /// (0 = host/eerste speler, 1 = tweede speler, etc.).
        /// </summary>
        Vector3 GetSpawnPositionForIndex(int index)
        {
            if (m_FirstSpawnPointObject == null)
            {
                Utils.Log($"{k_DebugPrepend}First Spawn Point Object is niet ingesteld - val terug op wereldorigin.", 1);
                return m_SpawnLineDirection.normalized * (m_SpawnSpacing * index);
            }

            return m_FirstSpawnPointObject.position + m_SpawnLineDirection.normalized * (m_SpawnSpacing * index);
        }

        public virtual void PlayerJoined(ulong playerID)
        {
            if (!m_CurrentPlayerIDs.Contains(playerID))
            {
                // Index in de rij = huidig aantal spelers VOORDAT deze speler is toegevoegd
                // (dus 0 voor de host/eerste speler, 1 voor de tweede, etc.)
                int spawnIndex = m_CurrentPlayerIDs.Count;

                m_CurrentPlayerIDs.Add(playerID);
                OnPlayerStateChanged?.Invoke(playerID, true);

                // Alleen de server/host mag de autoritatieve positie zetten.
                if (NetworkManager.Singleton.IsServer && TryGetPlayerByID(playerID, out XRINetworkPlayer player))
                {
                    Vector3 spawnPos = GetSpawnPositionForIndex(spawnIndex);
                    Quaternion spawnRot = m_FirstSpawnPointObject != null
                        ? m_FirstSpawnPointObject.rotation
                        : Quaternion.identity;

                    player.transform.position = spawnPos;
                    player.transform.rotation = spawnRot;

                    Utils.Log($"{k_DebugPrepend}Speler {playerID} gepositioneerd op rij-index {spawnIndex}: {spawnPos}", 0);
                }
            }
            else
            {
                Utils.Log($"{k_DebugPrepend}Trying to Add a player ID [{playerID}] that already exists", 1);
            }
        }

        public virtual void PlayerLeft(ulong playerID)
        {
            if (m_CurrentPlayerIDs.Contains(playerID))
            {
                m_CurrentPlayerIDs.Remove(playerID);
                OnPlayerStateChanged?.Invoke(playerID, false);
            }
            else
            {
                Utils.Log($"{k_DebugPrepend}Trying to remove a player ID [{playerID}] that doesn't exist", 1);
            }
        }

        public virtual void ConnectionFailed(string reason)
        {
            OnConnectionFailedAction?.Invoke(reason);
            m_ConnectionState.Value = IsAuthenticated() ? ConnectionState.Authenticated : ConnectionState.None;
        }

        public virtual void ConnectionUpdated(string update)
        {
            OnConnectionUpdated?.Invoke(update);
        }

        public virtual async void QuickJoinLobby()
        {
            Utils.Log($"{k_DebugPrepend}Joining Lobby by Quick Join.");
            if (await AbleToConnect())
            {
                ConnectToSession(await m_SessionManager.QuickJoinLobby());
            }
        }

        public virtual async void JoinLobbyByCode(string code)
        {
            Utils.Log($"{k_DebugPrepend}Joining Lobby by room code: {code}.");
            if (await AbleToConnect())
            {
                ConnectToSession(await m_SessionManager.JoinLobby(roomCode: code));
            }
        }

        public virtual async void JoinLobbySpecific(ISessionInfo session)
        {
            Utils.Log($"{k_DebugPrepend}Joining specific Lobby: {session.Name}.");
            if (await AbleToConnect())
            {
                ConnectToSession(await m_SessionManager.JoinLobby(sessionInfo: session));
            }
        }

        public virtual async void CreateNewLobby(string roomName = null, bool isPrivate = false, int playerCount = maxPlayers)
        {
            Utils.Log($"{k_DebugPrepend}Creating New Lobby: {roomName}.");
            if (await AbleToConnect())
            {
                ConnectToSession(await m_SessionManager.CreateSession(roomName, isPrivate, playerCount));
            }
        }

        protected virtual async Task<bool> AbleToConnect()
        {
            if (m_ConnectionState.Value == ConnectionState.Connecting)
            {
                string failureMessage = "Connection attempt still in progress.";
                Utils.Log($"{k_DebugPrepend}{failureMessage}", 1);
                ConnectionFailed(failureMessage);
                return false;
            }

            if (Connected.Value || m_ConnectionState.Value == ConnectionState.Connected)
            {
                Utils.Log($"{k_DebugPrepend}Already Connected to a Lobby. Disconnecting.", 0);
                await DisconnectAsync();
                await Task.Delay(100);
            }

            m_ConnectionState.Value = ConnectionState.Connecting;
            return true;
        }

        protected virtual void ConnectToSession(ISession session)
        {
            if (session == null)
            {
                FailedToConnect();
            }
            else
            {
                ConnectedRoomCode = session.Code;
                ConnectedRoomName.Value = session.Name;
                m_Connected.Value = true;
            }
        }

        protected virtual void FailedToConnect(string reason = null)
        {
            string failureMessage = "Failed to connect to lobby.";
            if (reason != null)
            {
                failureMessage = $"{reason}";
            }
            Utils.Log($"{k_DebugPrepend}{failureMessage}", 1);
        }

        public virtual async void CancelMatchmaking()
        {
            if (IsAuthenticated())
            {
                m_ConnectionState.Value = ConnectionState.Authenticated;
            }

            await m_SessionManager.LeaveSession();
        }

        public virtual async void Disconnect()
        {
            if (CurrentSessionType == SessionType.DistributedAuthority)
                await DisconnectAsync();
            else
                LeaveLocalConnection();
        }

        public virtual async Task DisconnectAsync()
        {
            await m_SessionManager.LeaveSession();
            m_Connected.Value = false;
            if (IsAuthenticated())
            {
                m_ConnectionState.Value = ConnectionState.Authenticated;
            }
            else
            {
                m_ConnectionState.Value = ConnectionState.None;
            }
            Utils.Log($"{k_DebugPrepend}Disconnected from Game.");
        }

        /// <summary>
        /// Start een 'headless' server-only sessie: geen speler-avatar, geen XR-vereiste.
        /// Bedoeld voor de facilitator-/host-laptop (VR_MP_01_HOST), die zelf niet
        /// deelneemt als speler maar puur de netwerksessie host en overziet.
        /// Start ook de LAN-discovery-broadcast, net als HostLocalConnection().
        /// </summary>
        public virtual bool StartAsHeadlessServer()
        {
            string localIP = GetLocalIPAddress();

            var transport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;
            transport.ConnectionData.Address = localIP;
            ConnectedRoomName.Value = "Facilitator Room";
            ConnectedRoomCode = localIP;

            // Belangrijk: StartServer() in plaats van StartHost() - dit maakt GEEN lokale
            // speler/avatar aan voor deze machine, in tegenstelling tot StartHost().
            bool started = NetworkManager.Singleton.StartServer();

            if (started)
            {
                StartLanDiscoveryBroadcast(transport.ConnectionData.Port, ConnectedRoomName.Value);
                Utils.Log($"{k_DebugPrepend}Headless server gestart op {localIP}, discovery actief.", 0);
            }
            else
            {
                Utils.Log($"{k_DebugPrepend}Headless server starten is mislukt.", 2);
            }

            return started;
        }

        /// <summary>
        /// Hosts a local connection.
        /// This will use the local IP address of the device to connect.
        /// Start ook direct de LAN discovery-broadcast zodat clients deze host automatisch kunnen vinden.
        /// </summary>
        public virtual bool HostLocalConnection()
        {
            string localIP = GetLocalIPAddress();

            var transport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;

            transport.ConnectionData.Address = localIP;
            ConnectedRoomName.Value = "Local Room";
            ConnectedRoomCode = localIP;

            bool started = NetworkManager.Singleton.StartHost();

            if (started)
            {
                StartLanDiscoveryBroadcast(transport.ConnectionData.Port, ConnectedRoomName.Value);
            }

            return started;
        }

        /// <summary>
        /// Joins a local connection as a client.
        /// This will use the the IP address the user manually sets in the UnityTransport.
        /// </summary>
        public virtual bool JoinLocalConnection()
        {
            var transport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;
            ConnectedRoomName.Value = "Local Room";
            ConnectedRoomCode = transport.ConnectionData.Address;
            return NetworkManager.Singleton.StartClient();
        }

        /// <summary>
        /// Leaves the local connection, either as a host or client.
        /// </summary>
        public virtual void LeaveLocalConnection()
        {
            NetworkManager.Singleton.Shutdown();
            StopLanDiscoveryBroadcast();
            StopSearchingForLocalHost();
        }

        /// <summary>
        /// Gets the local IP address.
        /// </summary>
        public virtual string GetLocalIPAddress()
        {
            string localIP = "127.0.0.1";
            try
            {
                string host = "8.8.8.8";
                int port = 65530;

                using (Socket socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect(host, port);
                    IPEndPoint endPoint = socket.LocalEndPoint as IPEndPoint;
                    localIP = endPoint.Address.ToString();
                }
            }
            catch (Exception e)
            {
                Utils.Log($"{k_DebugPrepend}Failed to get local IP: {e.Message}", 1);
            }
            return localIP;
        }

        // =================================================================
        // LAN DISCOVERY - nieuwe functionaliteit
        // =================================================================

        /// <summary>
        /// Start het zoeken naar een host op het lokale netwerk.
        /// Zodra een host gevonden is, wordt automatisch <see cref="JoinLocalConnection"/> aangeroepen.
        /// Roep dit aan vanaf je "Join"-knop in de UI, in plaats van het handmatige IP-invoerveld.
        /// </summary>
        public virtual void FindAndJoinLocalGame()
        {
            if (m_IsSearchingForHost)
            {
                Utils.Log($"{k_DebugPrepend}Al aan het zoeken naar een host.", 1);
                return;
            }

            try
            {
                // Bind expliciet aan het eigen lokale IP (i.p.v. IPAddress.Any) - op sommige
                // Android/Quest wifi-stacks is dit nodig om broadcast egress betrouwbaar te laten werken.
                string localIp = GetLocalIPAddress();
                m_DiscoveryClientSocket = new UdpClient(new IPEndPoint(IPAddress.Parse(localIp), 0))
                {
                    EnableBroadcast = true
                };
                m_IsSearchingForHost = true;
                StartCoroutine(BroadcastDiscoveryRequests());
                StartCoroutine(ListenForDiscoveryResponses());
                Utils.Log($"{k_DebugPrepend}Zoeken naar host op LAN gestart vanaf {localIp}...", 0);
            }
            catch (Exception e)
            {
                Utils.Log($"{k_DebugPrepend}Kon niet starten met zoeken naar host: {e.Message}", 2);
            }
        }

        /// <summary>
        /// Berekent het subnet-gerichte broadcast-adres (bv. 192.168.0.255) op basis van het
        /// eigen lokale IP, ervan uitgaande dat het een /24-netwerk is (standaard bij de meeste
        /// consumentenrouters, waaronder de TP-Link AX55 default-instelling).
        /// </summary>
        string GetSubnetBroadcastAddress(string localIp)
        {
            var parts = localIp.Split('.');
            if (parts.Length != 4) return "255.255.255.255";
            return $"{parts[0]}.{parts[1]}.{parts[2]}.255";
        }

        public virtual void StopSearchingForLocalHost()
        {
            m_IsSearchingForHost = false;
            m_DiscoveryClientSocket?.Close();
            m_DiscoveryClientSocket = null;
        }

        IEnumerator BroadcastDiscoveryRequests()
        {
            byte[] requestData = Encoding.UTF8.GetBytes(k_DiscoveryRequestTag);

            string localIp = GetLocalIPAddress();
            string subnetBroadcast = GetSubnetBroadcastAddress(localIp);

            IPEndPoint globalBroadcastEndPoint = new IPEndPoint(IPAddress.Broadcast, m_DiscoveryPort);
            IPEndPoint subnetBroadcastEndPoint = new IPEndPoint(IPAddress.Parse(subnetBroadcast), m_DiscoveryPort);

            while (m_IsSearchingForHost)
            {
                // Stuur naar beide adressen - subnet-gericht (bv. 192.168.0.255) werkt op
                // Android/Quest vaak betrouwbaarder dan het globale 255.255.255.255.
                try
                {
                    m_DiscoveryClientSocket.Send(requestData, requestData.Length, subnetBroadcastEndPoint);
                }
                catch (Exception e)
                {
                    Utils.Log($"{k_DebugPrepend}Subnet broadcast mislukt: {e.Message}", 1);
                }

                try
                {
                    m_DiscoveryClientSocket.Send(requestData, requestData.Length, globalBroadcastEndPoint);
                }
                catch (Exception e)
                {
                    Utils.Log($"{k_DebugPrepend}Globale broadcast mislukt: {e.Message}", 1);
                }

                yield return new WaitForSeconds(m_DiscoveryBroadcastInterval);
            }
        }

        IEnumerator ListenForDiscoveryResponses()
        {
            while (m_IsSearchingForHost)
            {
                if (m_DiscoveryClientSocket != null && m_DiscoveryClientSocket.Available > 0)
                {
                    IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = m_DiscoveryClientSocket.Receive(ref remoteEndPoint);
                    string message = Encoding.UTF8.GetString(data);

                    if (message.StartsWith(k_DiscoveryResponseTag))
                    {
                        string[] parts = message.Split('|');
                        if (parts.Length >= 3)
                        {
                            string hostIp = remoteEndPoint.Address.ToString();
                            ushort hostPort = ushort.Parse(parts[1]);
                            string foundRoomName = parts[2];

                            Utils.Log($"{k_DebugPrepend}Host gevonden: {hostIp}:{hostPort} ('{foundRoomName}')", 0);
                            OnLocalHostFound?.Invoke(hostIp, hostPort, foundRoomName);

                            // Vul de transport-data in en join direct, zoals JoinLocalConnection() dat al deed
                            // wanneer het IP handmatig werd ingevoerd.
                            var transport = NetworkManager.Singleton.NetworkConfig.NetworkTransport as UnityTransport;
                            transport.ConnectionData.Address = hostIp;
                            transport.ConnectionData.Port = hostPort;

                            StopSearchingForLocalHost();
                            JoinLocalConnection();
                            yield break;
                        }
                    }
                }
                yield return null;
            }
        }

        /// <summary>
        /// Start als host de UDP-listener die antwoordt op discovery-verzoeken van clients.
        /// Wordt automatisch aangeroepen vanuit <see cref="HostLocalConnection"/>.
        /// </summary>
        protected virtual void StartLanDiscoveryBroadcast(ushort gamePort, string roomName)
        {
            if (m_IsBroadcastingAsHost) return;

            try
            {
                m_DiscoveryServerSocket = new UdpClient(m_DiscoveryPort)
                {
                    EnableBroadcast = true
                };
                m_IsBroadcastingAsHost = true;
                StartCoroutine(ListenForDiscoveryRequests(gamePort, roomName));
                Utils.Log($"{k_DebugPrepend}LAN discovery-listener gestart op poort {m_DiscoveryPort}.", 0);
            }
            catch (Exception e)
            {
                Utils.Log($"{k_DebugPrepend}Kon discovery-listener niet starten: {e.Message}", 2);
            }
        }

        public virtual void StopLanDiscoveryBroadcast()
        {
            m_IsBroadcastingAsHost = false;
            m_DiscoveryServerSocket?.Close();
            m_DiscoveryServerSocket = null;
        }

        IEnumerator ListenForDiscoveryRequests(ushort gamePort, string roomName)
        {
            while (m_IsBroadcastingAsHost)
            {
                if (m_DiscoveryServerSocket != null && m_DiscoveryServerSocket.Available > 0)
                {
                    IPEndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
                    byte[] data = m_DiscoveryServerSocket.Receive(ref remoteEndPoint);
                    string message = Encoding.UTF8.GetString(data);

                    if (message == k_DiscoveryRequestTag)
                    {
                        string responseMessage = $"{k_DiscoveryResponseTag}|{gamePort}|{roomName}";
                        byte[] responseData = Encoding.UTF8.GetBytes(responseMessage);
                        m_DiscoveryServerSocket.Send(responseData, responseData.Length, remoteEndPoint);
                    }
                }
                yield return null;
            }
        }
    }
}