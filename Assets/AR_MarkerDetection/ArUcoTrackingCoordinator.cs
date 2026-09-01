using XRMultiplayer;
using System;
using System.Collections.Generic;
using TryAR.MarkerTracking;
using UnityEngine;
using UnityEngine.XR.ARFoundation;

/// <summary>
/// [v7] Coordineert PassthroughCameraToTexture (onze eigen AR Foundation-gebaseerde
/// camera-toegang) met ArUcoMarkerTracking (uit de QuestArUcoMarkerTracking-repo).
///
/// v7: IsSearchingEnabled zet nu ook automatisch PassthroughCameraToTexture.
/// IsCapturingEnabled mee aan/uit - camera-frame-conversie (YUV->RGBA) draait nu
/// ook alleen wanneer nodig, niet meer continu.
///
/// v6: Detectie draait nu ALLEEN als IsSearchingEnabled = true (standaard UIT).
/// Voorheen liep de volledige, zware OpenCV-detectiepijplijn continu, ook als er
/// niemand naar de uitkomst vroeg - nu pas actief zodra bv. MarkerColocationAligner
/// erom vraagt (na een X-knop-druk).
///
/// v5: UpdateMarkerDetectionHud() tijdelijk uitgeschakeld - stond in de weg tijdens
/// MarkerDebugger-tests (dubbele/conflicterende HUD-meldingen). Detectie zelf
/// blijft ongewijzigd werken.
///
/// v4: HUD-meldingen toegevoegd die tonen welke marker(s) momenteel gedetecteerd
/// worden (bv. "Marker 0 Detectie"). Verdwijnt automatisch (via het eigen gedrag
/// van PlayerHudNotification) zodra geen marker meer in beeld is.
/// v3: Frame-throttling toegevoegd om CPU-belasting/oplopende vertraging te verminderen.
/// v2: Debug-logging toegevoegd rond DetectMarker/marker-GameObject-posities.
///
/// Dit is een aangepaste versie van de originele ArUcoTrackingAppCoordinator,
/// die in plaats van Meta's PassthroughCameraAccess-component nu onze eigen
/// PassthroughCameraToTexture + ARCameraManager gebruikt.
/// </summary>
public class ArUcoTrackingCoordinator : MonoBehaviour
{
    [Serializable]
    public class MarkerGameObjectPair
    {
        public int markerId;
        public GameObject gameObject;
    }

    [Header("Camera")]
    [SerializeField] PassthroughCameraToTexture m_CameraTextureProvider;
    [SerializeField] ARCameraManager m_CameraManager;

    [Header("Marker Tracking")]
    [SerializeField] ArUcoMarkerTracking m_ArucoMarkerTracking;
    [SerializeField, Tooltip("Lijst van marker-ID's gekoppeld aan hun GameObjects")]
    List<MarkerGameObjectPair> m_MarkerGameObjectPairs = new List<MarkerGameObjectPair>();

    [Header("Debug (optioneel)")]
    [SerializeField] MeshRenderer m_DebugRenderer;

    Dictionary<int, GameObject> m_MarkerGameObjectDictionary = new Dictionary<int, GameObject>();
    Texture2D m_ResultTexture;
    bool m_IsInitialized = false;
    int m_DebugFrameCount = 0;

    void OnEnable()
    {
        if (m_CameraTextureProvider == null || m_CameraManager == null || m_ArucoMarkerTracking == null)
        {
            Debug.LogError("[ArUcoTrackingCoordinator] Niet alle referenties zijn ingesteld in de Inspector!");
            enabled = false;
            return;
        }

        m_CameraTextureProvider.OnTextureUpdated += OnCameraTextureUpdated;
    }

    void OnDisable()
    {
        if (m_CameraTextureProvider != null)
            m_CameraTextureProvider.OnTextureUpdated -= OnCameraTextureUpdated;
    }

    /// <summary>
    /// Wordt aangeroepen telkens als er een nieuwe camera-texture beschikbaar is
    /// (vanuit PassthroughCameraToTexture, dus effectief elke camera-frame).
    /// </summary>
    [Header("Performance")]
    [Tooltip("Voer marker-detectie niet elke frame uit, maar 1x per zoveel frames, " +
             "om CPU-belasting te beperken (OpenCV-detectie is relatief zwaar).")]
    [SerializeField] int m_DetectionFrameInterval = 3;

    int m_FramesSinceLastDetection = 0;

    /// <summary>
    /// Bepaalt of er daadwerkelijk detectie/verwerking moet gebeuren. Standaard UIT,
    /// zodat er GEEN OpenCV-rekenwerk plaatsvindt zolang niemand erom vraagt (bv.
    /// via een X-knop-druk in MarkerColocationAligner). Zet dit AAN vanuit een ander
    /// script (bv. MarkerColocationAligner.Recalibrate()) wanneer detectie nodig is,
    /// en weer UIT zodra je klaar bent (kalibratie gelukt, of getimed-out).
    /// </summary>
    bool m_IsSearchingEnabled = false;

    /// <summary>
    /// Bepaalt of er daadwerkelijk detectie/verwerking moet gebeuren. Standaard UIT,
    /// zodat er GEEN OpenCV-rekenwerk plaatsvindt zolang niemand erom vraagt (bv.
    /// via een X-knop-druk in MarkerColocationAligner). Zet dit AAN vanuit een ander
    /// script (bv. MarkerColocationAligner.Recalibrate()) wanneer detectie nodig is,
    /// en weer UIT zodra je klaar bent (kalibratie gelukt, of getimed-out).
    ///
    /// Deze zet ook automatisch PassthroughCameraToTexture.IsCapturingEnabled mee -
    /// zonder detectie hoeft ook de (relatief kostbare) camera-frame-conversie niet
    /// te draaien.
    /// </summary>
    public bool IsSearchingEnabled
    {
        get => m_IsSearchingEnabled;
        set
        {
            m_IsSearchingEnabled = value;
            if (m_CameraTextureProvider != null)
                m_CameraTextureProvider.IsCapturingEnabled = value;
        }
    }

    void OnCameraTextureUpdated(Texture2D texture)
    {
        if (!IsSearchingEnabled)
            return; // GEEN detectie-werk uitvoeren als er niemand om vraagt
        // Eenmalige initialisatie zodra de eerste texture (en dus de camera-
        // intrinsieken) beschikbaar zijn.
        if (!m_IsInitialized)
        {
            TryInitializeMarkerTracking(texture.width, texture.height);
            if (!m_IsInitialized) return; // intrinsics nog niet klaar, probeer volgende frame opnieuw
        }

        // Throttle: voer de (zware) OpenCV-detectie niet elke frame uit.
        m_FramesSinceLastDetection++;
        if (m_FramesSinceLastDetection < m_DetectionFrameInterval)
            return;
        m_FramesSinceLastDetection = 0;

        // Stap 1: detecteer markers in de huidige camera-frame.
        m_ArucoMarkerTracking.DetectMarker(texture, m_ResultTexture);

        // Stap 2: bereken de pose van elke gedetecteerde marker en positioneer
        // de bijbehorende GameObjects. We gebruiken de transform van de camera
        // zelf (Main Camera binnen je XR Origin) als wereld-referentiepunt.
        //
        // LET OP: dit is de gerenderde oog-camera-transform, niet per se de exacte
        // fysieke positie van de passthrough-camera-sensor. Voor colocatie/spawn-
        // doeleinden is dit vermoedelijk nauwkeurig genoeg; test dit empirisch.
        m_ArucoMarkerTracking.EstimatePoseCanonicalMarker(m_MarkerGameObjectDictionary, m_CameraManager.transform);

        // UpdateMarkerDetectionHud(); // TIJDELIJK UITGESCHAKELD - staat in de weg tijdens MarkerDebugger-tests

        m_DebugFrameCount++;
        if (m_DebugFrameCount % 60 == 0)
        {
            Debug.Log($"[ArUcoTrackingCoordinator] DetectMarker aangeroepen (frame #{m_DebugFrameCount}). " +
                      $"Aantal gekoppelde markers in dictionary: {m_MarkerGameObjectDictionary.Count}");

            foreach (var kvp in m_MarkerGameObjectDictionary)
            {
                if (kvp.Value != null)
                {
                    Debug.Log($"[ArUcoTrackingCoordinator] Marker ID {kvp.Key} -> GameObject '{kvp.Value.name}' " +
                              $"positie: {kvp.Value.transform.position}, actief: {kvp.Value.activeSelf}");
                }
            }
        }
    }

    void TryInitializeMarkerTracking(int width, int height)
    {
        if (!m_CameraTextureProvider.TryGetIntrinsics(out var intrinsics))
        {
            // Intrinsics nog niet beschikbaar - probeer het gewoon een volgende
            // frame opnieuw (OnCameraTextureUpdated wordt continu aangeroepen).
            return;
        }

        float fx = intrinsics.focalLength.x;
        float fy = intrinsics.focalLength.y;
        float cx = intrinsics.principalPoint.x;
        float cy = intrinsics.principalPoint.y;

        Debug.Log($"[ArUcoTrackingCoordinator] Camera Intrinsics - fx: {fx}, fy: {fy}, cx: {cx}, cy: {cy}, " +
                  $"resolutie: {intrinsics.resolution.x}x{intrinsics.resolution.y}, " +
                  $"texture: {width}x{height}");

        // Schaal de intrinsieken als de texture-resolutie afwijkt van de
        // resolutie waarvoor de intrinsieken oorspronkelijk gerapporteerd zijn
        // (zelfde aanpak als in de originele coordinator).
        if (intrinsics.resolution.x != width || intrinsics.resolution.y != height)
        {
            float scaleX = (float)width / intrinsics.resolution.x;
            float scaleY = (float)height / intrinsics.resolution.y;
            fx *= scaleX;
            fy *= scaleY;
            cx *= scaleX;
            cy *= scaleY;
        }

        m_ArucoMarkerTracking.Initialize(width, height, cx, cy, fx, fy);

        BuildMarkerDictionary();
        ConfigureResultTexture(width, height);

        m_IsInitialized = true;
    }

    void BuildMarkerDictionary()
    {
        m_MarkerGameObjectDictionary.Clear();
        foreach (var pair in m_MarkerGameObjectPairs)
        {
            if (pair.gameObject != null)
                m_MarkerGameObjectDictionary[pair.markerId] = pair.gameObject;
        }
    }

    void ConfigureResultTexture(int width, int height)
    {
        int divideNumber = m_ArucoMarkerTracking.DivideNumber;
        m_ResultTexture = new Texture2D(width / divideNumber, height / divideNumber, TextureFormat.RGB24, false);

        if (m_DebugRenderer != null)
            m_DebugRenderer.material.mainTexture = m_ResultTexture;
    }

    // =================================================================
    // MARKER-DETECTIE HUD - toont welke marker(s) momenteel in beeld zijn
    // =================================================================

    [Header("HUD")]
    [Tooltip("Hoe lang (seconden) een marker als 'nog in beeld' wordt beschouwd na " +
             "zijn laatste, daadwerkelijk veranderde positie. Moet ruim boven de " +
             "tijd tussen twee detectie-cycli liggen (afhankelijk van Detection " +
             "Frame Interval), anders knippert de melding onterecht aan/uit.")]
    [SerializeField] float m_MarkerVisibleTimeout = 1.0f;

    Dictionary<int, Vector3> m_LastMarkerPosition = new Dictionary<int, Vector3>();
    Dictionary<int, Quaternion> m_LastMarkerRotation = new Dictionary<int, Quaternion>();
    Dictionary<int, float> m_LastMarkerSeenTime = new Dictionary<int, float>();
    HashSet<int> m_PreviouslyVisibleMarkerIds = new HashSet<int>();

    void UpdateMarkerDetectionHud()
    {
        var currentlyVisibleIds = new HashSet<int>();

        foreach (var kvp in m_MarkerGameObjectDictionary)
        {
            int markerId = kvp.Key;
            GameObject obj = kvp.Value;
            if (obj == null) continue;

            bool changedThisCycle = false;
            if (m_LastMarkerPosition.TryGetValue(markerId, out Vector3 lastPos) &&
                m_LastMarkerRotation.TryGetValue(markerId, out Quaternion lastRot))
            {
                changedThisCycle =
                    Vector3.Distance(obj.transform.position, lastPos) > 0.0001f ||
                    Quaternion.Angle(obj.transform.rotation, lastRot) > 0.01f;
            }
            else
            {
                // Nog geen eerdere waarde bekend - beschouw dit niet als een
                // "verse detectie" (voorkomt een valse eerste-frame-melding,
                // zelfde reden als bij MarkerColocationAligner).
                changedThisCycle = false;
            }

            m_LastMarkerPosition[markerId] = obj.transform.position;
            m_LastMarkerRotation[markerId] = obj.transform.rotation;

            if (changedThisCycle)
                m_LastMarkerSeenTime[markerId] = Time.time;

            bool stillWithinTimeout =
                m_LastMarkerSeenTime.TryGetValue(markerId, out float lastSeen) &&
                (Time.time - lastSeen) < m_MarkerVisibleTimeout;

            if (stillWithinTimeout)
                currentlyVisibleIds.Add(markerId);
        }

        // Alleen de HUD bijwerken als de set van zichtbare markers daadwerkelijk
        // is veranderd (voorkomt onnodig herhaald aanroepen van ShowText, wat de
        // melding elke keer opnieuw zou kunnen laten 'flashen').
        bool setChanged = !currentlyVisibleIds.SetEquals(m_PreviouslyVisibleMarkerIds);
        if (setChanged)
        {
            m_PreviouslyVisibleMarkerIds = new HashSet<int>(currentlyVisibleIds);

            if (currentlyVisibleIds.Count > 0 && PlayerHudNotification.Instance != null)
            {
                var sortedIds = new List<int>(currentlyVisibleIds);
                sortedIds.Sort();
                string message = string.Join("\n", sortedIds.ConvertAll(id => $"Marker {id} Detectie"));
                PlayerHudNotification.Instance.ShowText(message);
            }
            // LET OP: als currentlyVisibleIds leeg is (geen marker meer in beeld),
            // wordt hier bewust GEEN nieuwe melding getoond - de vorige melding
            // verdwijnt dan vanzelf via het eigen aflooptijd-gedrag van
            // PlayerHudNotification. Als die niet automatisch verdwijnt, heb je
            // mogelijk een aparte 'Clear'-achtige methode nodig op dat systeem.
        }
    }
}