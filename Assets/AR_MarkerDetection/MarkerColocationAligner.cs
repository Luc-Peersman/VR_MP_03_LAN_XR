using XRMultiplayer;
using UnityEngine;
using UnityEngine.XR;
using Unity.Netcode.Components;

/// <summary>
/// [v16] Lijnt de XR Origin uit op basis van een gedetecteerde ArUco-marker, zodat
/// de fysieke marker-positie overeenkomt met een vaste, door jou gedefinieerde
/// positie in de virtuele scene.
///
/// v16: 'Target World Position/Euler Angles' vervangen door een sleepbaar
/// 'Calibration Point Object' (Transform) - visueel verplaatsen/draaien in de
/// Scene-view i.p.v. losse getallen intypen. Bedoeld als development-hulpmiddel
/// (met marker-afbeelding erop), uit te zetten voor de definitieve show.
///
/// v7: FUNDAMENTEEL ANDERE AANPAK. In plaats van de correctie CONTINU, elke
/// detectie-cyclus opnieuw toe te passen (wat ondanks meerdere fixes bleef
/// leiden tot een niet-convergerend "wegvliegen" zodra een marker vaak/
/// betrouwbaar gedetecteerd werd), doet dit script de correctie nu EENMALIG:
/// zodra de marker voor het eerst plausibel gedetecteerd wordt na het opstarten
/// (of na een expliciete Recalibrate()-aanroep), wordt de Origin één keer
/// gecorrigeerd en stopt de aligner daarna met verder ingrijpen - totdat je
/// opnieuw kalibreert (bv. via een controllerknop, later toe te voegen).
///
/// Dit elimineert het risico op een opstapelende/niet-convergerende fout
/// volledig, omdat er domweg geen herhaling meer is om op te stapelen.
/// </summary>
public class MarkerColocationAligner : MonoBehaviour
{
    [Header("Referenties")]
    [Tooltip("De XR Origin van de speler-rig (het object dat we gaan verplaatsen).")]
    [SerializeField] Transform m_XROrigin;

    [Tooltip("Het GameObject dat door ArUcoTrackingCoordinator al op de gedetecteerde " +
             "marker-positie wordt gezet.")]
    [SerializeField] Transform m_MarkerAnchorObject;

    [Tooltip("Referentie naar ArUcoTrackingCoordinator, om detectie alleen te " +
             "laten draaien terwijl er daadwerkelijk op een kalibratie gewacht wordt.")]
    [SerializeField] ArUcoTrackingCoordinator m_ArucoTrackingCoordinator;

    [Header("Doelpositie in de virtuele scene")]
    [Tooltip("Sleep hier het sleepbare 'MarkerCalibrationPoint'-object (met de " +
             "marker-afbeelding erop) in. De positie/rotatie van DAT object wordt " +
             "nu gebruikt als doelwaarde, in plaats van losse, handmatig ingetypte " +
             "getallen - visueel verplaatsen/draaien in de Scene-view volstaat.")]
    [SerializeField] Transform m_CalibrationPointObject;

    [Header("Kalibratie-modus")]
    [Tooltip("Zodra AAN: wacht op de eerste plausibele marker-detectie, kalibreer " +
             "EENMALIG, en stop daarna automatisch. Zet UIT en weer AAN (of roep " +
             "Recalibrate() aan) om een nieuwe kalibratie-poging te starten.")]
    [SerializeField] bool m_WaitingForCalibration = false;

    [Tooltip("Hoe lang (seconden) de eenmalige correctie duurt om vloeiend in te " +
             "lopen, in plaats van instant te snappen (voorkomt een onprettige, " +
             "harde teleport-schok).")]
    [SerializeField] float m_CalibrationTransitionDuration = 0.5f;

    [Header("Betrouwbaarheid")]
    [Tooltip("Maximale toegestane correctie-afstand (meters). Groter wordt " +
             "verworpen als waarschijnlijke valse-positieve detectie.")]
    [SerializeField] float m_MaxPlausibleCorrectionDistance = 20f;

    [Tooltip("Maximale toegestane rotatiecorrectie (graden).")]
    [SerializeField] float m_MaxPlausibleRotationDegrees = 180f;

    [Header("HUD / Vloerval-detectie")]
    [SerializeField] float m_FloorFallWarningY = -1f;

    Vector3 m_LastAnchorPosition;
    Quaternion m_LastAnchorRotation;
    bool m_HasPreviousAnchorPose = false;

    bool m_IsTransitioning = false;
    float m_TransitionElapsed = 0f;
    Vector3 m_TransitionStartPos, m_TransitionTargetPos;
    Quaternion m_TransitionStartRot, m_TransitionTargetRot;

    CharacterController[] m_CachedCharacterControllers;
    float m_FloorCheckTimer = 0f;

    void Start()
    {
        // Forceer dit expliciet op 'false', ONGEACHT wat er in de scene is
        // opgeslagen (Unity behoudt oude, eerder-opgeslagen waarden van een
        // [SerializeField] ook als de code-default verandert - dit voorkomt dat
        // een verouderde 'true'-waarde in de scene een ongewenste, automatische
        // timeout bij opstarten veroorzaakt, zonder dat Recalibrate() ooit is
        // aangeroepen).
        m_WaitingForCalibration = false;
        m_IsTransitioning = false;

        if (m_ArucoTrackingCoordinator != null)
            m_ArucoTrackingCoordinator.IsSearchingEnabled = false;

        if (m_MarkerAnchorObject != null)
        {
            m_LastAnchorPosition = m_MarkerAnchorObject.position;
            m_LastAnchorRotation = m_MarkerAnchorObject.rotation;
            m_HasPreviousAnchorPose = true;
        }
    }

    /// <summary>
    /// Start een nieuwe, eenmalige kalibratiepoging. Roep dit aan (bv. vanaf een
    /// controllerknop-actie) als je op een later moment opnieuw wilt uitlijnen.
    /// </summary>
    [Tooltip("Hoe lang (seconden) na een X-druk gewacht wordt op een marker-detectie " +
             "voordat 'Marker niet zichtbaar' getoond wordt.")]
    [SerializeField] float m_CalibrationTimeout = 1f;

    float m_CalibrationWaitElapsed = 0f;

    public void Recalibrate()
    {
        m_WaitingForCalibration = true;
        m_IsTransitioning = false;
        m_CalibrationWaitElapsed = 0f;

        if (m_ArucoTrackingCoordinator != null)
            m_ArucoTrackingCoordinator.IsSearchingEnabled = true;

        Debug.Log("[MarkerColocationAligner] Recalibrate() aangeroepen - wachten op eerstvolgende marker-detectie.");
        if (PlayerHudNotification.Instance != null)
            PlayerHudNotification.Instance.ShowText("<b>Kalibratie gestart</b> - kijk naar de marker...");
    }

    InputDevice m_LeftController;
    bool m_XWasPressedLastFrame = false;

    void CheckCalibrationButtons()
    {
        // Linker controller: X-knop (primaryButton).
        if (!m_LeftController.isValid)
            m_LeftController = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);

        if (m_LeftController.isValid &&
            m_LeftController.TryGetFeatureValue(CommonUsages.primaryButton, out bool xPressed))
        {
            if (xPressed && !m_XWasPressedLastFrame)
                Recalibrate();
            m_XWasPressedLastFrame = xPressed;
        }
    }

    void LateUpdate()
    {
        HandleCalibrationTransition();

        if (!m_WaitingForCalibration || m_IsTransitioning)
            return;

        // Timeout-check: als er te lang geen (verse) marker-detectie is geweest
        // sinds Recalibrate(), geef dat expliciet terug in de HUD i.p.v. stil
        // te blijven wachten.
        m_CalibrationWaitElapsed += Time.deltaTime;
        if (m_CalibrationWaitElapsed > m_CalibrationTimeout)
        {
            m_WaitingForCalibration = false;

            if (m_ArucoTrackingCoordinator != null)
                m_ArucoTrackingCoordinator.IsSearchingEnabled = false;

            Debug.Log("[MarkerColocationAligner] Kalibratie timeout - geen marker gevonden binnen " +
                      $"{m_CalibrationTimeout}s.");
            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText("<b>Marker niet zichtbaar</b> — probeer het opnieuw");
            return;
        }

        if (m_XROrigin == null || m_MarkerAnchorObject == null || m_CalibrationPointObject == null)
            return;

        bool poseChangedThisFrame =
            !m_HasPreviousAnchorPose ||
            Vector3.Distance(m_MarkerAnchorObject.position, m_LastAnchorPosition) > 0.0001f ||
            Quaternion.Angle(m_MarkerAnchorObject.rotation, m_LastAnchorRotation) > 0.01f;

        m_LastAnchorPosition = m_MarkerAnchorObject.position;
        m_LastAnchorRotation = m_MarkerAnchorObject.rotation;
        m_HasPreviousAnchorPose = true;

        if (!poseChangedThisFrame)
            return;

        // BELANGRIJKE CORRECTIE (v8): de externe library's marker-lokale as-conventie
        // wijst "naar binnen" i.p.v. naar de camera toe - dit gaf in de
        // kalibratiemetingen een constante ~180° afwijking (marker recht voor je
        // gaf ~180° i.p.v. 0°). We corrigeren dat hier, zodat "marker recht/normaal
        // neergelegd" overeenkomt met de door jou bedoelde Target World Euler Angles.
        // BELANGRIJK: gebruik alleen de YAW (rotatie om de Y-as, kijkrichting) van
        // de marker, niet de volledige 3D-rotatie. Kleine pitch/roll-ruis in de
        // meting (bv. marker niet perfect vlak, of meetonzekerheid) zou anders de
        // hele speelruimte laten kantelen, wat samen met zwaartekracht/
        // CharacterController tot een verwarrend "vallen/wegvliegen"-gevoel leidt.
        float rawYaw = m_MarkerAnchorObject.rotation.eulerAngles.y;
        float correctedYaw = rawYaw + 180f; // zelfde 180°-conventiecorrectie, nu direct op de yaw-waarde
        Quaternion correctedAnchorRotation = Quaternion.Euler(0f, correctedYaw, 0f);

        Matrix4x4 currentMarkerMatrix = Matrix4x4.TRS(
            m_MarkerAnchorObject.position, correctedAnchorRotation, Vector3.one);
        Matrix4x4 targetMarkerMatrix = Matrix4x4.TRS(
            m_CalibrationPointObject.position, m_CalibrationPointObject.rotation, Vector3.one);
        Matrix4x4 currentOriginMatrix = Matrix4x4.TRS(
            m_XROrigin.position, m_XROrigin.rotation, Vector3.one);

        Matrix4x4 newOriginMatrix = targetMarkerMatrix * currentMarkerMatrix.inverse * currentOriginMatrix;

        Vector3 newPosition = ExtractPosition(newOriginMatrix);
        Quaternion newRotation = ExtractRotation(newOriginMatrix);

        float jumpDistance = Vector3.Distance(m_XROrigin.position, newPosition);
        float jumpRotation = Quaternion.Angle(m_XROrigin.rotation, newRotation);

        if (jumpDistance > m_MaxPlausibleCorrectionDistance)
        {
            Debug.LogWarning($"[MarkerColocationAligner] Kalibratie VERWORPEN (positie): " +
                              $"{jumpDistance:F2}m > {m_MaxPlausibleCorrectionDistance}m.");
            return;
        }

        if (jumpRotation > m_MaxPlausibleRotationDegrees)
        {
            Debug.LogWarning($"[MarkerColocationAligner] Kalibratie VERWORPEN (rotatie): " +
                              $"{jumpRotation:F1} graden > {m_MaxPlausibleRotationDegrees} graden.");
            return;
        }

        // Start de EENMALIGE overgang naar de nieuwe positie. Zodra deze klaar is,
        // stopt de aligner met ingrijpen totdat Recalibrate() opnieuw wordt aangeroepen.
        m_TransitionStartPos = m_XROrigin.position;
        m_TransitionStartRot = m_XROrigin.rotation;
        m_TransitionTargetPos = newPosition;
        m_TransitionTargetRot = newRotation;
        m_TransitionElapsed = 0f;
        m_IsTransitioning = true;
        m_WaitingForCalibration = false; // klaar met wachten - deze kalibratie wordt nu uitgevoerd

        Debug.Log($"[MarkerColocationAligner] Kalibratie gestart: sprong van {jumpDistance:F2}m, {jumpRotation:F1} graden.");

        if (PlayerHudNotification.Instance != null)
            PlayerHudNotification.Instance.ShowText("<b>Marker gedetecteerd</b> - eenmalig kalibreren...");
    }

    void HandleCalibrationTransition()
    {
        if (!m_IsTransitioning) return;

        m_TransitionElapsed += Time.deltaTime;
        float t = m_CalibrationTransitionDuration <= 0f
            ? 1f
            : Mathf.Clamp01(m_TransitionElapsed / m_CalibrationTransitionDuration);

        Vector3 pos = Vector3.Lerp(m_TransitionStartPos, m_TransitionTargetPos, t);
        Quaternion rot = Quaternion.Slerp(m_TransitionStartRot, m_TransitionTargetRot, t);

        ApplyPositionSafely(pos, rot);

        if (t >= 1f)
        {
            m_IsTransitioning = false;

            if (m_ArucoTrackingCoordinator != null)
                m_ArucoTrackingCoordinator.IsSearchingEnabled = false;

            Debug.Log("[MarkerColocationAligner] Kalibratie VOLTOOID. Aligner is nu inactief tot Recalibrate().");
            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText("<b>Kalibratie voltooid</b>");
        }
    }

    NetworkTransform[] m_CachedNetworkTransforms;

    void ApplyPositionSafely(Vector3 newPosition, Quaternion newRotation)
    {
        if (m_CachedCharacterControllers == null)
            m_CachedCharacterControllers = m_XROrigin.GetComponentsInChildren<CharacterController>(true);

        if (m_CachedNetworkTransforms == null)
            m_CachedNetworkTransforms = m_XROrigin.GetComponentsInChildren<NetworkTransform>(true);

        foreach (var cc in m_CachedCharacterControllers)
            if (cc != null) cc.enabled = false;

        m_XROrigin.position = newPosition;
        m_XROrigin.rotation = newRotation;

        foreach (var cc in m_CachedCharacterControllers)
            if (cc != null) cc.enabled = true;

        // BELANGRIJK: forceer een "harde teleport" op eventuele NetworkTransform-
        // componenten in de rig. Zonder dit probeert het netwerk-synchronisatie-
        // systeem een grote, plotselinge sprong soms vloeiend "in te halen" met een
        // vaste interpolatiesnelheid - wat aanvoelt als wegvliegen met constante
        // snelheid, in plaats van de sprong direct te accepteren.
        foreach (var nt in m_CachedNetworkTransforms)
        {
            if (nt != null)
                nt.Teleport(nt.transform.position, nt.transform.rotation, nt.transform.localScale);
        }
    }

    void Update()
    {
        CheckCalibrationButtons();

        m_FloorCheckTimer += Time.deltaTime;
        if (m_FloorCheckTimer >= 1f)
        {
            m_FloorCheckTimer = 0f;
            if (m_XROrigin != null && m_XROrigin.position.y < m_FloorFallWarningY)
            {
                if (PlayerHudNotification.Instance != null)
                    PlayerHudNotification.Instance.ShowText(
                        $"<b>Waarschuwing:</b> door de vloer gezakt (Y={m_XROrigin.position.y:F1}m)");
            }
        }
    }

    static Vector3 ExtractPosition(Matrix4x4 m) => new Vector3(m.m03, m.m13, m.m23);

    static Quaternion ExtractRotation(Matrix4x4 m)
    {
        Vector3 forward = m.GetColumn(2);
        Vector3 up = m.GetColumn(1);
        return Quaternion.LookRotation(forward, up);
    }
}