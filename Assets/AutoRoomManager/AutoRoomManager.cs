using System.Collections;
using UnityEngine;
using Unity.Netcode;
using XRMultiplayer;

/// <summary>
/// Zet dit script op een leeg GameObject in je scene (bv. naast je Network Manager).
/// Bij het opstarten van de app:
/// 1. Wacht een korte, willekeurige tijd (vermindert kans op gelijktijdige host-conflicten
///    als meerdere headsets tegelijk worden aangezet).
/// 2. Zoekt naar een bestaande host op het LAN.
/// 3. Gevonden? -> Join automatisch.
/// 4. Niet gevonden binnen de timeout? -> Word zelf host.
///
/// LET OP: dit elimineert het risico op twee gelijktijdige hosts niet volledig,
/// alleen de KANS erop. Bij perfect gelijktijdig opstarten van meerdere headsets
/// blijft een dubbele-host-situatie theoretisch mogelijk. Zie toelichting in chat.
/// </summary>
public class AutoRoomManager : MonoBehaviour
{
    [Header("Timing")]
    [Tooltip("Willekeurige opstartvertraging (min-max in seconden) voordat gezocht wordt. " +
             "Verkleint kans dat meerdere headsets exact gelijktijdig besluiten host te worden.")]
    [SerializeField] Vector2 m_StartupDelayRange = new Vector2(0.5f, 3f);

    [Tooltip("Hoe lang gezocht wordt naar een bestaande host voordat wordt besloten zelf host te worden.")]
    [SerializeField] float m_DiscoveryTimeout = 5f;

    [Header("Reconnect")]
    [Tooltip("Hoe vaak (seconden) gecheckt wordt of de verbinding nog actief is.")]
    [SerializeField] float m_ConnectionCheckInterval = 2f;

    [Tooltip("Wachttijd voordat een reconnect-poging start nadat verbindingsverlies is gedetecteerd. " +
             "Voorkomt te snel/agressief herverbinden bij een kortstondige hapering.")]
    [SerializeField] float m_ReconnectDelay = 2f;

    bool m_HasResolvedRoomState = false;
    bool m_IsMonitoring = false;

    void Start()
    {
        StartCoroutine(AutoResolveRoomState());
    }

    IEnumerator AutoResolveRoomState()
    {
        // Stap 1: willekeurige startvertraging.
        float delay = Random.Range(m_StartupDelayRange.x, m_StartupDelayRange.y);
        Debug.Log($"[AutoRoomManager] Wacht {delay:F1}s voor het zoeken naar een host...");
        yield return new WaitForSeconds(delay);

        // Stap 2: begin met zoeken naar een bestaande host.
        PlayerHudNotification.Instance.ShowText("Zoeken naar bestaande room...");

        bool hostFound = false;
        XRINetworkGameManager.Instance.OnLocalHostFound += OnHostFoundHandler;
        void OnHostFoundHandler(string ip, ushort port, string roomName)
        {
            hostFound = true;
            PlayerHudNotification.Instance.ShowText("Room gevonden, verbinden...");
        }

        XRINetworkGameManager.Instance.FindAndJoinLocalGame();

        float elapsed = 0f;
        while (elapsed < m_DiscoveryTimeout && !NetworkManager.Singleton.IsConnectedClient)
        {
            elapsed += Time.deltaTime;
            yield return null;
        }

        XRINetworkGameManager.Instance.OnLocalHostFound -= OnHostFoundHandler;

        // Stap 3/4: check resultaat.
        if (NetworkManager.Singleton.IsConnectedClient)
        {
            PlayerHudNotification.Instance.ShowText("<b>Status:</b> Verbonden als speler (client)");
            Debug.Log("[AutoRoomManager] Bestaande host gevonden en verbonden als client.");
        }
        else
        {
            PlayerHudNotification.Instance.ShowText("Geen room gevonden, room wordt gestart...");
            Debug.Log("[AutoRoomManager] Geen host gevonden binnen timeout - word zelf host.");
            XRINetworkGameManager.Instance.StopSearchingForLocalHost();
            bool started = XRINetworkGameManager.Instance.HostLocalConnection();

            if (started)
                PlayerHudNotification.Instance.ShowText("<b>Status:</b> Room gestart (jij bent host)");
            else
                PlayerHudNotification.Instance.ShowText("<b>Fout:</b> Kon geen room starten");
        }

        m_HasResolvedRoomState = true;

        // Start continue monitoring, zodat we een onverwacht verbindingsverlies
        // later automatisch kunnen detecteren en herstellen.
        if (!m_IsMonitoring)
        {
            m_IsMonitoring = true;
            StartCoroutine(MonitorConnection());
        }
    }

    /// <summary>
    /// Controleert doorlopend of de netwerkverbinding nog actief is (als host óf als client).
    /// Bij onverwacht verbindingsverlies wordt automatisch de volledige
    /// zoek-of-host-flow opnieuw gestart, zodat de headset zichzelf herstelt
    /// zonder handmatig ingrijpen.
    /// </summary>
    IEnumerator MonitorConnection()
    {
        while (true)
        {
            yield return new WaitForSeconds(m_ConnectionCheckInterval);

            // NetworkManager.IsListening is true zolang we host, server of client zijn.
            // Wordt dit onverwacht false nadat we eerder succesvol verbonden waren,
            // dan is de verbinding weggevallen (bv. host crashte, wifi-hapering, etc.).
            bool stillConnected = NetworkManager.Singleton.IsListening &&
                                   (NetworkManager.Singleton.IsHost ||
                                    NetworkManager.Singleton.IsServer ||
                                    NetworkManager.Singleton.IsConnectedClient);

            if (!stillConnected)
            {
                Debug.LogWarning("[AutoRoomManager] Verbinding onverwacht verloren. Reconnect-poging start...");
                PlayerHudNotification.Instance.ShowText("<b>Verbinding verloren.</b> Opnieuw verbinden...");

                yield return new WaitForSeconds(m_ReconnectDelay);

                // Zorg voor een schone staat voordat we opnieuw proberen.
                if (NetworkManager.Singleton.IsListening)
                {
                    NetworkManager.Singleton.Shutdown();
                    yield return new WaitUntil(() => !NetworkManager.Singleton.IsListening);
                }

                // Reset de monitoring-vlag zodat AutoResolveRoomState() na het herstellen
                // van de verbinding een NIEUWE MonitorConnection-loop start (deze huidige
                // loop stopt hieronder via yield break, om duplicatie te voorkomen).
                m_IsMonitoring = false;
                yield return StartCoroutine(AutoResolveRoomState());
                yield break;
            }
        }
    }
}