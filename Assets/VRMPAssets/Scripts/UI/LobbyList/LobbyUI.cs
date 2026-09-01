using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;
using Unity.Services.Vivox;
using Unity.Services.Multiplayer;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;

namespace XRMultiplayer
{
    public class LobbyUI : MonoBehaviour
    {
        /// <summary>
        ///  De timeout voor direct join in seconden.
        /// </summary>
        const float k_DirectJoinTimeout = 4.5f;

        /// <summary>
        /// Timeout voor het zoeken naar een host via LAN discovery, in seconden.
        /// Iets ruimer dan k_DirectJoinTimeout omdat er ook zoektijd bij komt
        /// (broadcast-interval + tijd voordat de host antwoordt).
        /// </summary>
        const float k_LocalDiscoveryTimeout = 8.0f;

        enum ConnectionSubPanel
        {
            LobbyPanel = 0,
            CreationPanel = 1,
            ConnectionPanel = 2,
            ConnectionSuccessPanel = 3,
            ConnectionFailurePanel = 4,
            NoConnectionPanel = 5
        }

        [Header("Lobby List")]
        [SerializeField]
        Transform m_LobbyListParent;

        [SerializeField]
        GameObject m_LobbyListPrefab;

        [SerializeField]
        GameObject m_SessionPanelObject;

        [SerializeField]
        GameObject m_LocalPanelObject;

        [SerializeField]
        Button m_RefreshButton;

        [SerializeField]
        Image m_CooldownImage;

        [SerializeField]
        float m_AutoRefreshTime = 5.0f;

        [SerializeField]
        float m_RefreshCooldownTime = .5f;

        [Header("Connection Texts")]
        [SerializeField]
        TMP_Text m_ConnectionUpdatedText;

        [SerializeField]
        TMP_Text m_ConnectionSuccessText;

        [SerializeField]
        TMP_Text m_ConnectionFailedText;

        [Header("Room Creation")]
        [SerializeField]
        TMP_InputField m_RoomNameText;

        [SerializeField]
        Toggle m_PrivacyToggle;

        [SerializeField]
        GameObject[] m_ConnectionSubPanels;

        VoiceChatManager m_VoiceChatManager;

        Coroutine m_UpdateLobbiesRoutine;
        Coroutine m_CooldownFillRoutine;

        bool m_Private = false;
        int m_PlayerCount;

        private void Awake()
        {
            m_VoiceChatManager = FindFirstObjectByType<VoiceChatManager>();
            SessionManager.status.Subscribe(ConnectedUpdated);
            m_CooldownImage.enabled = false;
        }

        private void Start()
        {
            m_PrivacyToggle.onValueChanged.AddListener(TogglePrivacy);

            bool isLocal = XRINetworkGameManager.CurrentSessionType != SessionType.DistributedAuthority;
            m_SessionPanelObject.SetActive(!isLocal);
            m_LocalPanelObject.SetActive(isLocal);
            m_PlayerCount = XRINetworkGameManager.maxPlayers / 2;
            XRINetworkGameManager.Instance.OnConnectionFailedAction += FailedToConnect;
            XRINetworkGameManager.Instance.OnConnectionUpdated += ConnectedUpdated;

            foreach (Transform t in m_LobbyListParent)
            {
                Destroy(t.gameObject);
            }
        }

        void OnEnable()
        {
            ToggleConnectionSubPanel(ConnectionSubPanel.LobbyPanel);
        }

        private void OnDisable()
        {
            HideLobbies();
        }

        private void OnDestroy()
        {
            XRINetworkGameManager.Instance.OnConnectionFailedAction -= FailedToConnect;
            XRINetworkGameManager.Instance.OnConnectionUpdated -= ConnectedUpdated;

            SessionManager.status.Unsubscribe(ConnectedUpdated);
        }

        public void CreateLobby()
        {
            XRINetworkGameManager.Connected.Subscribe(OnConnected);
            if (string.IsNullOrEmpty(m_RoomNameText.text) || m_RoomNameText.text == "<Room Name>")
            {
                m_RoomNameText.text = $"{XRINetworkGameManager.LocalPlayerName.Value}'s Room";
            }
            XRINetworkGameManager.Instance.CreateNewLobby(m_RoomNameText.text, m_Private, m_PlayerCount);
            m_ConnectionSuccessText.text = $"Joining {m_RoomNameText.text}";
        }

        public void CancelConnection()
        {
            XRINetworkGameManager.Instance.CancelMatchmaking();
        }

        public void SetRoomName(string roomName)
        {
            if (!string.IsNullOrEmpty(roomName))
            {
                m_RoomNameText.text = roomName;
            }
        }

        public void EnterRoomCode(string roomCode)
        {
            ToggleConnectionSubPanel(ConnectionSubPanel.ConnectionPanel);
            XRINetworkGameManager.Connected.Subscribe(OnConnected);
            XRINetworkGameManager.Instance.JoinLobbyByCode(roomCode.ToUpper());
            m_ConnectionSuccessText.text = $"Joining Room: {roomCode.ToUpper()}";
        }

        public void JoinLobby(ISessionInfo Session)
        {
            ToggleConnectionSubPanel(ConnectionSubPanel.ConnectionPanel);
            XRINetworkGameManager.Connected.Subscribe(OnConnected);
            XRINetworkGameManager.Instance.JoinLobbySpecific(Session);
            m_ConnectionSuccessText.text = $"Joining {Session.Name}";
        }

        public void QuickJoinLobby()
        {
            XRINetworkGameManager.Connected.Subscribe(OnConnected);
            XRINetworkGameManager.Instance.QuickJoinLobby();
            m_ConnectionSuccessText.text = "Joining Random";
        }

        public void SetVoiceChatAudidibleDistance(int audibleDistance)
        {
            if (audibleDistance <= m_VoiceChatManager.ConversationalDistance)
            {
                audibleDistance = m_VoiceChatManager.ConversationalDistance + 1;
            }
            m_VoiceChatManager.AudibleDistance = audibleDistance;
        }

        public void SetVoiceChatConversationalDistance(int conversationalDistance)
        {
            m_VoiceChatManager.ConversationalDistance = conversationalDistance;
        }

        public void SetVoiceChatAudioFadeIntensity(float fadeIntensity)
        {
            m_VoiceChatManager.AudioFadeIntensity = fadeIntensity;
        }

        public void SetVoiceChatAudioFadeModel(int fadeModel)
        {
            m_VoiceChatManager.AudioFadeModel = (AudioFadeModel)fadeModel;
        }

        public void TogglePrivacy(bool toggle)
        {
            m_Private = toggle;
        }

        void ToggleConnectionSubPanel(ConnectionSubPanel panel)
        {
            ToggleConnectionSubPanel((int)panel);
        }

        public void ToggleConnectionSubPanel(int panelId)
        {
            for (int i = 0; i < m_ConnectionSubPanels.Length; i++)
            {
                m_ConnectionSubPanels[i].SetActive(i == panelId);
            }

            if (panelId == 0)
            {
                ShowLobbies();
            }
            else
            {
                HideLobbies();
            }
        }

        void OnConnected(bool connected)
        {
            if (connected)
            {
                ToggleConnectionSubPanel(ConnectionSubPanel.ConnectionSuccessPanel);
                XRINetworkGameManager.Connected.Unsubscribe(OnConnected);
            }
        }

        void ConnectedUpdated(string update)
        {
            m_ConnectionUpdatedText.text = $"<b>Status:</b> {update}";
        }

        public void FailedToConnect(string reason)
        {
            ToggleConnectionSubPanel(ConnectionSubPanel.ConnectionFailurePanel);
            m_ConnectionFailedText.text = $"<b>Error:</b> {reason}";
        }

        public void HideLobbies()
        {
            EnableRefresh();
            if (m_UpdateLobbiesRoutine != null) StopCoroutine(m_UpdateLobbiesRoutine);
        }

        public void ShowLobbies()
        {
            UpdateLobbyDisplay();
            if (m_UpdateLobbiesRoutine != null) StopCoroutine(m_UpdateLobbiesRoutine);
            m_UpdateLobbiesRoutine = StartCoroutine(UpdateAvailableLobbies());
        }

        IEnumerator UpdateAvailableLobbies()
        {
            while (true)
            {
                yield return new WaitForSeconds(m_AutoRefreshTime);
                UpdateLobbyDisplay();
            }
        }

        void EnableRefresh()
        {
            m_CooldownImage.enabled = false;
            m_RefreshButton.interactable = true;
        }

        IEnumerator UpdateButtonCooldown()
        {
            m_RefreshButton.interactable = false;

            m_CooldownImage.enabled = true;
            for (float i = 0; i < m_RefreshCooldownTime; i += Time.deltaTime)
            {
                m_CooldownImage.fillAmount = Mathf.Clamp01(i / m_RefreshCooldownTime);
                yield return null;
            }
            EnableRefresh();
        }

        async void UpdateLobbyDisplay()
        {
            if (m_CooldownImage.enabled || (int)XRINetworkGameManager.CurrentConnectionState.Value < 2) return;
            if (m_CooldownFillRoutine != null) StopCoroutine(m_CooldownFillRoutine);
            m_CooldownFillRoutine = StartCoroutine(UpdateButtonCooldown());

            await System.Threading.Tasks.Task.Yield();
            if (XRINetworkGameManager.CurrentSessionType == SessionType.LocalOnly)
                return;

            QuerySessionsResults results = await MultiplayerService.Instance.QuerySessionsAsync(SessionManager.GetQuickJoinFilterOptions());

            foreach (Transform t in m_LobbyListParent)
            {
                Destroy(t.gameObject);
            }

            if (results != null && results.Sessions.Count > 0)
            {
                foreach (var session in results.Sessions)
                {
                    if (SessionManager.CheckForSessionFilter(session))
                        continue;

                    if (SessionManager.CheckForIncompatibilityFilter(session))
                    {
                        LobbyListSlotUI newLobbyUI = Instantiate(m_LobbyListPrefab, m_LobbyListParent).GetComponent<LobbyListSlotUI>();
                        newLobbyUI.CreateNonJoinableLobbyUI(session, this, "Version Conflict");
                        continue;
                    }

                    if (SessionManager.CanJoinLobby(session))
                    {
                        LobbyListSlotUI newLobbyUI = Instantiate(m_LobbyListPrefab, m_LobbyListParent).GetComponent<LobbyListSlotUI>();
                        newLobbyUI.CreateSessionUI(session, this);
                    }
                }
            }
        }

        public void HostLocalRoom()
        {
            if (XRINetworkGameManager.Instance.HostLocalConnection())
            {
                ToggleConnectionSubPanel(ConnectionSubPanel.ConnectionSuccessPanel);
            }
            else
            {
                Utils.LogError($"Failed to host local room:");
                m_ConnectionFailedText.text = $"<b>Error:</b> Room already exists or could not be created";
                ToggleConnectionSubPanel(ConnectionSubPanel.ConnectionFailurePanel);
            }
        }

        // -----------------------------------------------------------
        // AANGEPAST: JoinLocalRoom gebruikt nu LAN discovery i.p.v.
        // een direct/synchroon join op een handmatig ingevoerd IP.
        // Koppel je "Join"-knop in de Inspector nog steeds gewoon aan
        // deze methode (LobbyUI.JoinLocalRoom) - de knop-koppeling zelf
        // hoeft niet te veranderen, alleen wat er ín de methode gebeurt.
        // -----------------------------------------------------------
        public void JoinLocalRoom()
        {
            ToggleConnectionSubPanel(ConnectionSubPanel.ConnectionPanel);
            m_ConnectionSuccessText.text = "Zoeken naar host op LAN...";

            XRINetworkGameManager.Instance.FindAndJoinLocalGame();
            StartCoroutine(CheckForFailedLocalDiscovery());
        }

        /// <summary>
        /// Wacht tot de discovery + connectie is gelukt, of geeft na k_LocalDiscoveryTimeout
        /// seconden een foutmelding als er geen host gevonden/verbonden is.
        /// </summary>
        IEnumerator CheckForFailedLocalDiscovery()
        {
            yield return new WaitForSeconds(k_LocalDiscoveryTimeout);

            if (!NetworkManager.Singleton.IsConnectedClient)
            {
                // Stop het zoeken en eventuele hangende client-poging netjes.
                XRINetworkGameManager.Instance.StopSearchingForLocalHost();
                if (NetworkManager.Singleton.IsClient)
                {
                    NetworkManager.Singleton.Shutdown();
                }
                FailedToJoinLocal();
            }
            else
            {
                ToggleConnectionSubPanel(ConnectionSubPanel.ConnectionSuccessPanel);
            }
        }

        void FailedToJoinLocal()
        {
            Utils.LogError($"Failed to join local room:");
            m_ConnectionFailedText.text = $"<b>Error:</b> Geen host gevonden op het LAN";
            ToggleConnectionSubPanel(ConnectionSubPanel.ConnectionFailurePanel);
        }

        /// <summary>
        /// Called from the UI to set the IP address for joining a direct connection.
        /// Blijft beschikbaar als handmatige FALLBACK, mocht LAN discovery
        /// om wat voor reden niet werken. Koppel dit desgewenst aan een apart
        /// "Handmatig verbinden"-knopje/paneel i.p.v. het hoofd-join-scherm.
        /// </summary>
        /// <param name="address">IP address or DNS</param>
        public void SetIP(string address)
        {
            SetIPAsync(address);
        }

        public virtual async void SetIPAsync(string address)
        {
            var hostEntry = await System.Net.Dns.GetHostEntryAsync(address);
            if (hostEntry == null || hostEntry.AddressList.Length == 0)
            {
                Utils.LogError($"Failed to resolve IP address: {address}");
                m_ConnectionFailedText.text = $"<b>Error:</b> Invalid IP address";
                ToggleConnectionSubPanel(ConnectionSubPanel.ConnectionFailurePanel);
                return;
            }

            if (hostEntry.AddressList.Length > 1)
                Utils.LogWarning($"Multiple IP addresses found for {address}. Using the first one: {hostEntry.AddressList[0]}");

            var ipAddress = hostEntry.AddressList[0].ToString();
            var transport = (UnityTransport)NetworkManager.Singleton.NetworkConfig.NetworkTransport;
            transport.SetConnectionData(ipAddress, transport.ConnectionData.Port);
        }

        /// <summary>
        /// Handmatige join-knop (fallback), gebruikt het IP dat via SetIP is ingesteld.
        /// Dit is de OUDE flow - laat staan als noodoplossing, koppel 'm los aan een
        /// apart knopje als je die wilt behouden.
        /// </summary>
        public void JoinLocalRoomManual()
        {
            if (XRINetworkGameManager.Instance.JoinLocalConnection())
            {
                StartCoroutine(CheckForFailedConnection());
                ToggleConnectionSubPanel(ConnectionSubPanel.ConnectionPanel);
            }
            else
            {
                FailedToJoinLocal();
            }
        }

        IEnumerator CheckForFailedConnection()
        {
            yield return new WaitForSeconds(k_DirectJoinTimeout);
            if (!NetworkManager.Singleton.IsConnectedClient)
            {
                NetworkManager.Singleton.Shutdown();
                FailedToJoinLocal();
            }
            else
            {
                ToggleConnectionSubPanel(ConnectionSubPanel.ConnectionSuccessPanel);
            }
        }

        /// <summary>
        /// Called from the UI buttons to update the max players allowed per room.
        /// </summary>
        /// <param name="count">Amount of players to allow.</param>
        public void UpdatePlayerCount(int count)
        {
            m_PlayerCount = Mathf.Clamp(count, 1, XRINetworkGameManager.maxPlayers);
        }
    }
}