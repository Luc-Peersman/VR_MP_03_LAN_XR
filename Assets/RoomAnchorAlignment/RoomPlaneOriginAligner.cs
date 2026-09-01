using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using Unity.Netcode.Components;
using XRMultiplayer;

/// <summary>
/// [v3] Lijnt de XR Origin EENMALIG uit op een vooraf gescande, fysiek herkende
/// ruimte (Meta Quest "Ruimteconfiguratie"/Space Setup - hier: de ruimte met de
/// naam "Studio"), in plaats van op een ArUco-marker of een los aangemaakte
/// spatial anchor.
///
/// v2: eerste on-device test gaf "vloer te hoog" (oorzaak bleek de Y-positie van
/// Calibration Point Object, niet dit script) - bij die gelegenheid ook de
/// rotatie-toepassing beperkt tot YAW-only (was per ongeluk nog volledige 3D-rotatie),
/// consistent met de al bestaande fix in MarkerColocationAligner.cs.
///
/// v3: tweede on-device test wees een berekende correctie van ~40m af als
/// "waarschijnlijke foutdetectie" (default drempel was 20m, gekopieerd van
/// MarkerColocationAligner zonder te checken of dat hier klopt) - bleek GEEN fout:
/// het kamer-model in deze scene staat zelf ~40m van wereld-origin verwijderd, dus
/// een correctie van die orde is hier normaal. Drempel verhoogd naar 60m. Ook een
/// herhaalde-afwijzing-spam gefixt: bij afwijzing werd voorheen ELK FRAME opnieuw
/// geprobeerd en gelogd zolang hetzelfde vlak stabiel bleef - nu maar 1x per
/// kandidaat-vlak, pas opnieuw na Recalibrate() of een ander gedetecteerd vlak.
///
/// Werking: zodra de headset zich fysiek in een eerder gescande ruimte bevindt,
/// levert Meta's Scene API automatisch gelabelde vlakken (vloer, muren, ...) via
/// ARPlaneManager - GEEN los aan te maken/te delen anchor nodig, GEEN PlayerPrefs-
/// afhankelijkheid (in tegenstelling tot AnchorColocationTest/), en volledig
/// offline (de ruimte-scan staat al lokaal op het toestel).
///
/// LET OP - single-headset only: deze ruimte-scan wordt niet gedeeld tussen
/// toestellen. Voor colocation tussen meerdere headsets is nog steeds Shared
/// Spatial Anchors nodig (zie AnchorColocationTest/) - dit script lost alleen
/// "kom ik elke sessie op dezelfde plek in de virtuele scene terecht" op, op
/// EEN specifiek toestel in EEN specifieke fysieke ruimte.
///
/// Hergebruikt bewust dezelfde eenmalige-kalibratie-wiskunde en veiligheidslaag
/// (CharacterController aan/uit, NetworkTransform.Teleport, plausibiliteitscheck,
/// vloeiende Lerp/Slerp-overgang) als MarkerColocationAligner.cs (AR_MarkerDetection/),
/// omdat die aanpak daar de "wegvliegen"-bug al oploste.
///
/// Vereist vooraf, handmatig in de Editor:
/// - Project Settings > XR Plug-in Management > OpenXR > Android-tab > "Meta
///   Quest: Planes" staat aan onder OpenXR Feature Groups (al gedaan).
/// - Een ARPlaneManager-component ergens in de scene (bv. op hetzelfde
///   GameObject als de bestaande ARAnchorManager/PassthroughCameraToTexture,
///   "PassthroughCameraToTextureObject" in SampleScene - of een nieuw
///   GameObject, dat maakt niet uit).
/// - Dit script als component (zelfde of nieuw GameObject), met:
///   - Xr Origin: hetzelfde Transform als bij MarkerColocationAligner's
///     "Xr Origin"-veld.
///   - Plane Manager: de zojuist toegevoegde ARPlaneManager.
///   - Calibration Point Object: een (nieuw) leeg GameObject in de scene, dat
///     je precies zo positioneert/roteert als waar het HERKENDE VLOERVLAK in
///     de virtuele scene moet uitkomen (zelfde sleep-workflow als het ArUco-
///     marker kalibratiepunt).
/// - Eerste testronde: laat "Log All Detected Planes" aan (default) en
///   controleer via de HUD-tekst welke classificatie(s) daadwerkelijk
///   verschijnen voor "Studio", voordat je op de default "Floor" vertrouwt.
/// </summary>
public class RoomPlaneOriginAligner : MonoBehaviour
{
    [Header("Referenties")]
    [Tooltip("De XR Origin van de speler-rig (het object dat we gaan verplaatsen) - " +
             "hetzelfde Transform als bij MarkerColocationAligner's 'Xr Origin'-veld.")]
    [SerializeField] Transform m_XROrigin;

    [Tooltip("De ARPlaneManager die de eerder gescande ruimte ('Studio') als " +
             "gelabelde vlakken oplevert.")]
    [SerializeField] ARPlaneManager m_PlaneManager;

    [Tooltip("Sleep hier een (nieuw) leeg GameObject in dat je precies positioneert/" +
             "roteert op de plek waar het HERKENDE VLOERVLAK in de virtuele scene moet " +
             "uitkomen - zelfde sleep-workflow als het ArUco-marker kalibratiepunt.")]
    [SerializeField] Transform m_CalibrationPointObject;

    [Header("Vlak-selectie")]
    [Tooltip("Classificatie(s) waarnaar gezocht wordt. Standaard Floor - dit is " +
             "normaliter uniek per ruimte-scan. Zet 'Log All Detected Planes' hieronder " +
             "aan om te controleren welke classificatie de vloer van 'Studio' echt krijgt.")]
    [SerializeField] PlaneClassifications m_TargetClassification = PlaneClassifications.Floor;

    [Tooltip("Hoeveel seconden een kandidaat-vlak stabiel (nagenoeg ongewijzigde pose) " +
             "moet zijn voordat we 'm gebruiken - voorkomt uitlijnen op een vlak dat nog " +
             "aan het verfijnen is.")]
    [SerializeField] float m_StabilizationTime = 1.5f;

    [Tooltip("Maximale pose-verandering tussen twee updates die nog als 'stabiel' geldt " +
             "(meters).")]
    [SerializeField] float m_StablePositionTolerance = 0.02f;

    [Tooltip("Maximale pose-verandering tussen twee updates die nog als 'stabiel' geldt " +
             "(graden).")]
    [SerializeField] float m_StableRotationTolerance = 1f;

    [Tooltip("Na hoeveel seconden zonder passend vlak we EENMALIG een HUD-waarschuwing " +
             "tonen (we blijven daarna wel luisteren, voor als de scan alsnog laat komt).")]
    [SerializeField] float m_DetectionWarningTimeout = 10f;

    [Header("Overgang")]
    [Tooltip("Hoe lang (seconden) de eenmalige correctie duurt om vloeiend in te lopen, " +
             "in plaats van instant te snappen.")]
    [SerializeField] float m_CalibrationTransitionDuration = 0.5f;

    [Header("Betrouwbaarheid")]
    [Tooltip("Maximale toegestane correctie-afstand (meters). Groter wordt verworpen als " +
             "waarschijnlijke foutdetectie. LET OP: dit hangt af van hoe ver het kamer-model " +
             "in deze scene van wereld-origin (0,0,0) af staat geplaatst - controleer de " +
             "wereldpositie van je Calibration Point Object en zet dit ruim daarboven, niet " +
             "blind op een kleine waarde.")]
    [SerializeField] float m_MaxPlausibleCorrectionDistance = 60f;

    [Tooltip("Maximale toegestane rotatiecorrectie (graden).")]
    [SerializeField] float m_MaxPlausibleRotationDegrees = 180f;

    [Header("Debug")]
    [Tooltip("Toont/logt elk NIEUW gedetecteerd vlak (classificatie + afmeting) via de " +
             "HUD - zet dit tijdens de eerste testronde aan om te verifieren welke " +
             "labels daadwerkelijk verschijnen voor de ruimte 'Studio'.")]
    [SerializeField] bool m_LogAllDetectedPlanes = true;

    bool m_HasCalibrated = false;
    bool m_WarningShown = false;
    float m_ElapsedSinceStart = 0f;

    ARPlane m_CandidatePlane;
    TrackableId m_CandidateTrackableId;
    bool m_HasCandidateSample = false;
    Vector3 m_CandidateLastPosition;
    Quaternion m_CandidateLastRotation;
    float m_CandidateStableElapsed = 0f;

    // Voorkomt dat een geweigerde kalibratie (te grote sprong) ELK FRAME opnieuw wordt
    // geprobeerd en gelogd zolang hetzelfde kandidaat-vlak stabiel blijft staan - pas
    // opnieuw proberen zodra het kandidaat-vlak wijzigt of Recalibrate() wordt aangeroepen.
    bool m_RejectedCurrentCandidate = false;

    bool m_IsTransitioning = false;
    float m_TransitionElapsed = 0f;
    Vector3 m_TransitionStartPos, m_TransitionTargetPos;
    Quaternion m_TransitionStartRot, m_TransitionTargetRot;

    CharacterController[] m_CachedCharacterControllers;
    NetworkTransform[] m_CachedNetworkTransforms;

    void OnEnable()
    {
        if (m_PlaneManager != null)
            m_PlaneManager.trackablesChanged.AddListener(OnTrackablesChanged);
    }

    void OnDisable()
    {
        if (m_PlaneManager != null)
            m_PlaneManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
    }

    /// <summary>
    /// Reset de kalibratiestatus zodat het eerstvolgende passende, stabiele vlak
    /// opnieuw gebruikt wordt om uit te lijnen. Handig als de eerste kalibratie op
    /// een verkeerd/ruizig vlak is gebeurd. Nog niet aan een controllerknop
    /// gekoppeld (alle voor de hand liggende knoppen zijn al in gebruik door
    /// MarkerColocationAligner/AnchorColocationTester) - roep desgewenst zelf aan
    /// vanuit een nieuwe input-binding.
    /// </summary>
    public void Recalibrate()
    {
        m_HasCalibrated = false;
        m_WarningShown = false;
        m_ElapsedSinceStart = 0f;
        m_CandidatePlane = null;
        m_HasCandidateSample = false;
        m_CandidateStableElapsed = 0f;
        m_RejectedCurrentCandidate = false;
        m_IsTransitioning = false;

        Debug.Log("[RoomPlaneOriginAligner] Recalibrate() aangeroepen - wacht op eerstvolgend stabiel vlak.");
    }

    void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
    {
        if (m_LogAllDetectedPlanes)
        {
            foreach (var plane in args.added)
            {
                string msg = $"[RoomPlaneOriginAligner] Nieuw vlak: {plane.classifications} " +
                             $"({plane.size.x:F2} x {plane.size.y:F2}m)";
                Debug.Log(msg);
                if (PlayerHudNotification.Instance != null)
                    PlayerHudNotification.Instance.ShowText(msg);
            }
        }

        // Als het huidige kandidaat-vlak verwijderd is, opnieuw op zoek.
        foreach (var removed in args.removed)
        {
            if (m_CandidatePlane != null && removed.Key == m_CandidateTrackableId)
            {
                m_CandidatePlane = null;
                m_HasCandidateSample = false;
                m_CandidateStableElapsed = 0f;
                m_RejectedCurrentCandidate = false;
            }
        }

        if (m_HasCalibrated)
            return;

        ARPlane best = null;
        float bestArea = 0f;

        void Consider(ARPlane plane)
        {
            if ((plane.classifications & m_TargetClassification) == 0)
                return;

            float area = plane.size.x * plane.size.y;
            if (best == null || area > bestArea)
            {
                best = plane;
                bestArea = area;
            }
        }

        foreach (var plane in args.added) Consider(plane);
        foreach (var plane in args.updated) Consider(plane);

        if (best == null)
            return;

        if (m_CandidatePlane == null || best.trackableId != m_CandidateTrackableId)
        {
            // Nieuw of ander kandidaat-vlak - stabiliteitsteller opnieuw beginnen.
            m_CandidatePlane = best;
            m_CandidateTrackableId = best.trackableId;
            m_HasCandidateSample = false;
            m_CandidateStableElapsed = 0f;
            m_RejectedCurrentCandidate = false;
        }
    }

    void Update()
    {
        m_ElapsedSinceStart += Time.deltaTime;

        HandleCalibrationTransition();

        if (m_HasCalibrated || m_IsTransitioning || m_RejectedCurrentCandidate)
            return;

        if (m_CandidatePlane == null)
        {
            if (!m_WarningShown && m_ElapsedSinceStart > m_DetectionWarningTimeout)
            {
                m_WarningShown = true;
                Debug.LogWarning("[RoomPlaneOriginAligner] Nog geen passend vlak gevonden na " +
                                  $"{m_DetectionWarningTimeout}s. Sta je fysiek in de gescande " +
                                  "ruimte 'Studio'? Staat 'Meta Quest: Planes' aan?");
                if (PlayerHudNotification.Instance != null)
                    PlayerHudNotification.Instance.ShowText(
                        "<b>Ruimte niet herkend</b>\nSta je in 'Studio'? Blijft wachten...");
            }
            return;
        }

        CheckCandidateStability();
    }

    void CheckCandidateStability()
    {
        Transform planeTransform = m_CandidatePlane.transform;

        if (!m_HasCandidateSample)
        {
            m_CandidateLastPosition = planeTransform.position;
            m_CandidateLastRotation = planeTransform.rotation;
            m_CandidateStableElapsed = 0f;
            m_HasCandidateSample = true;
            return;
        }

        float posDelta = Vector3.Distance(planeTransform.position, m_CandidateLastPosition);
        float rotDelta = Quaternion.Angle(planeTransform.rotation, m_CandidateLastRotation);

        m_CandidateLastPosition = planeTransform.position;
        m_CandidateLastRotation = planeTransform.rotation;

        if (posDelta > m_StablePositionTolerance || rotDelta > m_StableRotationTolerance)
        {
            m_CandidateStableElapsed = 0f;
            return;
        }

        m_CandidateStableElapsed += Time.deltaTime;

        if (m_CandidateStableElapsed >= m_StabilizationTime)
            TryCalibrateNow(planeTransform.position, planeTransform.rotation);
    }

    void TryCalibrateNow(Vector3 planePosition, Quaternion planeRotation)
    {
        if (m_XROrigin == null || m_CalibrationPointObject == null)
        {
            Debug.LogWarning("[RoomPlaneOriginAligner] Xr Origin of Calibration Point Object niet ingesteld.");
            return;
        }

        // Alleen de YAW (rotatie om de verticale as) van het gedetecteerde vlak gebruiken,
        // niet de volledige 3D-rotatie - kleine pitch/roll-ruis in de detectie (vloer niet
        // perfect 0� gemeten) zou anders de hele speelruimte laten kantelen. Zelfde fix als
        // MarkerColocationAligner.cs, die dit al eerder oploste voor de ArUco-marker.
        Quaternion planeYawOnlyRotation = Quaternion.Euler(0f, planeRotation.eulerAngles.y, 0f);

        Matrix4x4 currentPlaneMatrix = Matrix4x4.TRS(planePosition, planeYawOnlyRotation, Vector3.one);
        Matrix4x4 targetMatrix = Matrix4x4.TRS(
            m_CalibrationPointObject.position, m_CalibrationPointObject.rotation, Vector3.one);
        Matrix4x4 currentOriginMatrix = Matrix4x4.TRS(m_XROrigin.position, m_XROrigin.rotation, Vector3.one);

        Matrix4x4 newOriginMatrix = targetMatrix * currentPlaneMatrix.inverse * currentOriginMatrix;

        Vector3 newPosition = ExtractPosition(newOriginMatrix);
        Quaternion newRotation = ExtractRotation(newOriginMatrix);

        float jumpDistance = Vector3.Distance(m_XROrigin.position, newPosition);
        float jumpRotation = Quaternion.Angle(m_XROrigin.rotation, newRotation);

        if (jumpDistance > m_MaxPlausibleCorrectionDistance)
        {
            m_RejectedCurrentCandidate = true;
            Debug.LogWarning($"[RoomPlaneOriginAligner] Kalibratie VERWORPEN (positie): " +
                              $"{jumpDistance:F2}m > {m_MaxPlausibleCorrectionDistance}m. " +
                              "Roep Recalibrate() aan om het opnieuw te proberen.");
            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText(
                    $"<b>Uitlijning verworpen</b>\nSprong van {jumpDistance:F1}m is te groot.");
            return;
        }

        if (jumpRotation > m_MaxPlausibleRotationDegrees)
        {
            m_RejectedCurrentCandidate = true;
            Debug.LogWarning($"[RoomPlaneOriginAligner] Kalibratie VERWORPEN (rotatie): " +
                              $"{jumpRotation:F1} graden > {m_MaxPlausibleRotationDegrees} graden. " +
                              "Roep Recalibrate() aan om het opnieuw te proberen.");
            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText(
                    $"<b>Uitlijning verworpen</b>\nRotatie-sprong van {jumpRotation:F0}° is te groot.");
            return;
        }

        m_TransitionStartPos = m_XROrigin.position;
        m_TransitionStartRot = m_XROrigin.rotation;
        m_TransitionTargetPos = newPosition;
        m_TransitionTargetRot = newRotation;
        m_TransitionElapsed = 0f;
        m_IsTransitioning = true;
        m_HasCalibrated = true;

        Debug.Log($"[RoomPlaneOriginAligner] Kalibratie gestart o.b.v. vlak " +
                  $"'{m_CandidatePlane.classifications}': sprong van {jumpDistance:F2}m, {jumpRotation:F1} graden.");

        if (PlayerHudNotification.Instance != null)
            PlayerHudNotification.Instance.ShowText("<b>Ruimte herkend</b> - eenmalig uitlijnen...");
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
            Debug.Log("[RoomPlaneOriginAligner] Kalibratie VOLTOOID.");
            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText("<b>Uitlijning voltooid</b>");
        }
    }

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
        // componenten in de rig, anders probeert netwerk-interpolatie de sprong
        // vloeiend "in te halen" (zelfde fix als MarkerColocationAligner).
        foreach (var nt in m_CachedNetworkTransforms)
        {
            if (nt != null)
                nt.Teleport(nt.transform.position, nt.transform.rotation, nt.transform.localScale);
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
