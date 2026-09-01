using XRMultiplayer;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using TryAR.MarkerTracking;

/// <summary>
/// [v10] ZUIVERE versie: leest de rauwe, camera-relatieve marker-pose rechtstreeks
/// uit ArUcoMarkerTracking (via LastRawPoseInCameraSpace), zonder omweg via
/// Unity-wereldcoördinaten.
///
/// v10: X-teken omgedraaid (gemeten x was consequent tegenovergesteld aan de
/// bedoelde conventie: rechts = positief, vastgesteld via de kalibratiemetingen).
///
/// - x: links(-)/rechts(+) t.o.v. de headset (die stilstaat op (0,0,0)), in cm
/// - y: hoogte (afstand marker t.o.v. camera-hoogte), in cm
/// - z: diepte, rechtvooruit in kijkrichting (altijd positief), in cm
/// - hoek_y: rotatie van de marker rond de Y-as (yaw), in hele graden
///
/// LET OP - aanname die nog te verifiëren is: dit gaat ervan uit dat
/// rawPose.pos exact Unity's standaard camera-lokale asconventie volgt
/// (+x=rechts, +y=omhoog, +z=voorwaarts).
/// </summary>
public class MarkerDebugger : MonoBehaviour
{
    [Tooltip("Referentie naar het ArUcoMarkerTracking-component.")]
    [SerializeField] ArUcoMarkerTracking m_ArucoMarkerTracking;

    [Tooltip("Welke marker-ID je wilt volgen.")]
    [SerializeField] int m_MarkerId = 1;

    [SerializeField] float m_UpdateInterval = 0.5f;

    [Header("Logging (voor kalibratie-meetreeks)")]
    [Tooltip("Optioneel: koppel hier een controllerknop-actie om metingen te loggen. " +
             "Laat leeg om alleen de spatiebalk-fallback te gebruiken.")]
    [SerializeField] InputActionReference m_LogButtonAction;

    [Tooltip("Naam van het CSV-logbestand, opgeslagen in Application.persistentDataPath.")]
    [SerializeField] string m_LogFileName = "marker_calibration_log.csv";

    [Header("Geluid")]
    [Tooltip("Volume van de geluidjes (0-1). Zet hoger als je 'm eerder niet goed hoorde.")]
    [SerializeField, Range(0f, 1f)] float m_SoundVolume = 1f;

    float m_UpdateTimer = 0f;
    int m_MeasurementCount = 0;
    string m_LogFilePath;

    AudioSource m_AudioSource;
    AudioClip m_ShutterClip;
    AudioClip m_ErrorClip;

    void Awake()
    {
        // Eigen, NIET-ruimtelijke (2D) AudioSource: spatialBlend = 0 zorgt dat het
        // geluid altijd op volle sterkte hoorbaar is, ongeacht de positie van dit
        // GameObject t.o.v. de headset/luisteraar. Dit was vermoedelijk de reden
        // dat het vorige geluidje nauwelijks te horen was (3D-verzwakking).
        m_AudioSource = gameObject.AddComponent<AudioSource>();
        m_AudioSource.spatialBlend = 0f;
        m_AudioSource.playOnAwake = false;

        m_ShutterClip = GenerateShutterClip();
        m_ErrorClip = GenerateErrorClip();
    }

    void OnEnable()
    {
        m_LogFilePath = Path.Combine(Application.persistentDataPath, m_LogFileName);

        if (!File.Exists(m_LogFilePath))
        {
            File.WriteAllText(m_LogFilePath, "meting_nr;tijdstempel;marker_id;x_cm;y_cm;z_cm;hoek_y_graden\n");
        }
        Debug.Log($"[MarkerDebugger] Logbestand: {m_LogFilePath}");

        if (m_LogButtonAction != null && m_LogButtonAction.action != null)
        {
            m_LogButtonAction.action.performed += OnLogButtonPressed;
            m_LogButtonAction.action.Enable();
        }
    }

    void OnDisable()
    {
        if (m_LogButtonAction != null && m_LogButtonAction.action != null)
            m_LogButtonAction.action.performed -= OnLogButtonPressed;
    }

    void OnLogButtonPressed(InputAction.CallbackContext ctx)
    {
        LogMeasurement();
    }

    void Update()
    {
        if (m_ArucoMarkerTracking == null)
            return;

        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            LogMeasurement();

        if (!m_ArucoMarkerTracking.LastRawPoseInCameraSpace.TryGetValue(m_MarkerId, out var rawPose))
            return;

        m_UpdateTimer += Time.deltaTime;
        if (m_UpdateTimer < m_UpdateInterval)
            return;
        m_UpdateTimer = 0f;

        (int xCm, int yCm, int zCm, int hoekGraden) = BerekenAfgerondeWaarden(rawPose.pos, rawPose.rot);

        string message =
            $"<b>Marker {m_MarkerId}</b>\n" +
            $"x: {xCm} cm  y: {yCm} cm  z: {zCm} cm\n" +
            $"hoek_y: {hoekGraden}°";

        if (PlayerHudNotification.Instance != null)
            PlayerHudNotification.Instance.ShowText(message);
    }

    /// <summary>
    /// Slaat de huidige meting op (Console + CSV-bestand). Als de marker niet
    /// gedetecteerd is, klinkt een ANDER (fout-)geluidje i.p.v. het sluiter-geluidje,
    /// en wordt er niets gelogd.
    /// </summary>
    public void LogMeasurement()
    {
        if (m_ArucoMarkerTracking == null ||
            !m_ArucoMarkerTracking.LastRawPoseInCameraSpace.TryGetValue(m_MarkerId, out var rawPose))
        {
            Debug.LogWarning("[MarkerDebugger] Kan niet loggen - marker nog niet gedetecteerd.");
            PlayErrorSound();
            if (PlayerHudNotification.Instance != null)
                PlayerHudNotification.Instance.ShowText("<b>Geen marker in beeld</b> - niets gelogd");
            return;
        }

        m_MeasurementCount++;
        (int xCm, int yCm, int zCm, int hoekGraden) = BerekenAfgerondeWaarden(rawPose.pos, rawPose.rot);
        string timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        string line = $"{m_MeasurementCount};{timestamp};{m_MarkerId};{xCm};{yCm};{zCm};{hoekGraden}";
        Debug.Log($"[MarkerDebugger] METING #{m_MeasurementCount} gelogd: " +
                  $"x={xCm}cm y={yCm}cm z={zCm}cm hoek_y={hoekGraden}°");

        try
        {
            File.AppendAllText(m_LogFilePath, line + "\n");
        }
        catch (System.Exception e)
        {
            Debug.LogError($"[MarkerDebugger] Kon niet naar logbestand schrijven: {e.Message}");
        }

        PlayShutterSound();

        if (PlayerHudNotification.Instance != null)
            PlayerHudNotification.Instance.ShowText(
                $"<b>Meting #{m_MeasurementCount} gelogd</b>\n" +
                $"x: {xCm} cm  y: {yCm} cm  z: {zCm} cm\n" +
                $"hoek_y: {hoekGraden}°");
    }

    /// <summary>
    /// Zet positie/rotatie om naar afgeronde waarden: x/y/z in HELE centimeters,
    /// hoek_y in HELE graden (met 180°-conventiecorrectie).
    /// </summary>
    (int, int, int, int) BerekenAfgerondeWaarden(Vector3 pos, Quaternion rot)
    {
        // X-teken omgedraaid: gemeten was consequent tegenovergesteld aan de
        // gewenste conventie (rechts = positief).
        int xCm = Mathf.RoundToInt(-pos.x * 100f);
        int yCm = Mathf.RoundToInt(pos.y * 100f);
        int zCm = Mathf.RoundToInt(pos.z * 100f);
        int hoekGraden = Mathf.RoundToInt(CorrigeerHoek(rot.eulerAngles.y));
        return (xCm, yCm, zCm, hoekGraden);
    }

    /// <summary>
    /// Corrigeert de gemeten Y-rotatie voor de vastgestelde, constante 180°-offset.
    /// </summary>
    float CorrigeerHoek(float rawHoek)
    {
        float corrected = rawHoek - 180f;
        if (corrected < -180f) corrected += 360f;
        if (corrected > 180f) corrected -= 360f;
        return corrected;
    }

    void PlayShutterSound()
    {
        m_AudioSource.PlayOneShot(m_ShutterClip, m_SoundVolume);
    }

    void PlayErrorSound()
    {
        m_AudioSource.PlayOneShot(m_ErrorClip, m_SoundVolume);
    }

    /// <summary>
    /// Kort, scherp 'klik'-geluid (camera-sluiter-achtig) voor een succesvolle meting.
    /// </summary>
    static AudioClip GenerateShutterClip()
    {
        int sampleRate = 44100;
        float duration = 0.08f;
        int sampleCount = Mathf.CeilToInt(sampleRate * duration);
        float[] samples = new float[sampleCount];
        var rnd = new System.Random();

        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleCount;
            float envelope = Mathf.Exp(-t * 30f);
            float noise = (float)(rnd.NextDouble() * 2.0 - 1.0);
            samples[i] = noise * envelope;
        }

        AudioClip clip = AudioClip.Create("ShutterClick", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }

    /// <summary>
    /// Ander, duidelijk herkenbaar 'fout'-geluid (lage, korte dubbele zoemtoon)
    /// voor als er geen marker gedetecteerd is bij een druk op de knop.
    /// </summary>
    static AudioClip GenerateErrorClip()
    {
        int sampleRate = 44100;
        float duration = 0.30f;
        int sampleCount = Mathf.CeilToInt(sampleRate * duration);
        float[] samples = new float[sampleCount];

        float freq1 = 180f; // lage toon
        float freq2 = 140f; // net iets lager, tweede 'buzz'

        for (int i = 0; i < sampleCount; i++)
        {
            float t = (float)i / sampleRate;
            // Twee korte tonen na elkaar, met korte stilte ertussen ("foutmelding"-gevoel).
            float freq = t < duration * 0.45f ? freq1 : freq2;
            float envelopeGate = (t < duration * 0.4f || t > duration * 0.5f) ? 1f : 0f;
            float wave = Mathf.Sign(Mathf.Sin(2f * Mathf.PI * freq * t)); // vierkante golf, 'zoemer'-geluid
            float fadeOut = Mathf.Clamp01((duration - t) / 0.05f);
            samples[i] = wave * envelopeGate * fadeOut * 0.5f;
        }

        AudioClip clip = AudioClip.Create("ErrorBuzz", sampleCount, 1, sampleRate, false);
        clip.SetData(samples, 0);
        return clip;
    }
}