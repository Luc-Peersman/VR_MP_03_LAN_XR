using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.XR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.OpenXR.Features.Meta;
using XRMultiplayer;

/// <summary>
/// [v3] Losstaande validatietool (NIET gekoppeld aan de ArUco-colocatie in
/// AR_MarkerDetection/) voor de vraag: kan een gedeelde ruimtelijke anchor
/// (Meta Colocation via com.unity.xr.meta-openxr) EENMALIG met internet
/// aangemaakt/gedeeld/gedownload worden, en daarna volledig OFFLINE (op de
/// "IX"-LAN, zonder internet) opnieuw geladen en gelokaliseerd worden?
///
/// v3: knoppen verplaatst - v2 gebruikte per ongeluk nog linker X
/// (primaryButton) voor CreateAndShare, exact dezelfde knop als
/// MarkerColocationAligner voor het triggeren van de ArUco-kalibratie. Bij
/// gelijktijdig actieve scripts zou 1 druk op X dus BEIDE routines tegelijk
/// starten. Dit script raakt linker X nu helemaal niet meer aan.
///
/// v2: toetsenbord 1/2/3 werkt alleen in de Unity Editor - op een standalone
/// Quest-build is er geen toetsenbord. Toegevoegd: dezelfde legacy XR
/// InputDevice-polling als MarkerColocationAligner.
///
/// Testprocedure (met minstens 2 headsets, tijdelijk verbonden met een
/// netwerk MET internet, bv. een mobiele hotspot):
/// 1. Host-headset: druk rechter controller A-knop in (CreateAndShare). Maakt
///    een anchor aan op de positie van m_AnchorPoseSource, deelt 'm (vereist
///    internet) en bewaart 'm meteen ook lokaal op dit toestel.
/// 2. Client-headset(s): druk rechter controller B-knop in (DownloadShared).
///    Haalt de gedeelde anchor op (vereist internet) en bewaart 'm daarna
///    ook lokaal op dat toestel.
/// 3. Zet ALLE headsets over op de "IX"-LAN (geen internet meer).
/// 4. Op elke headset (host en clients): druk linker controller Y-knop in
///    (LoadPersisted). Laadt de lokaal bewaarde anchor-guid opnieuw - dit
///    gaat NIET via internet. Vergelijk de getoonde positie met de fysieke
///    plek van de anchor op elk toestel. Klopt dit overal, dan is bevestigd
///    dat colocatie via omgevingsdata volledig offline werkt zodra er
///    eenmalig (met internet) gedeeld is.
///
/// BELANGRIJK vóór stap 1 en 2: loop even (10-20 sec) rond in de buurt van de
/// beoogde ankerplek voordat je de knop indrukt. Er is geen aparte "start
/// scan"-actie in deze API - de headset bouwt zijn ruimtelijke puntenwolk
/// automatisch/doorlopend op terwijl je rondkijkt (Insight tracking), en
/// zowel host als client moeten dit ELK ZELF gedaan hebben voor hun kant van
/// de omgeving rond de anchor, anders mislukt de matching.
///
/// Vereist vooraf, handmatig in de Editor:
/// - Project Settings > XR Plug-in Management > OpenXR > Android-tab >
///   OpenXR Feature Groups > vink "Meta Quest: Anchors" aan. Zonder deze
///   feature actief gooien de Try*Anchor-calls een InvalidOperationException.
/// - Een ARAnchorManager-component op het XR Origin GameObject in de scene
///   (zelfde Origin als waar ARCameraManager al op zit, zie
///   PassthroughCameraToTexture in AR_MarkerDetection/).
/// </summary>
public class AnchorColocationTester : MonoBehaviour
{
    [SerializeField] ARAnchorManager m_AnchorManager;
    [SerializeField] Transform m_AnchorPoseSource;

    [Tooltip("Moet exact identiek zijn op ALLE testtoestellen (host en clients) - dit bepaalt de groep waarbinnen anchors gedeeld worden.")]
    [SerializeField] string m_SharedGroupIdString = "6f9c2e1a-6b3d-4c2a-9e2f-1a2b3c4d5e6f";

    [Tooltip("Optioneel: prefab dat op de anchor-positie getoond wordt zodra deze aangemaakt/geladen is (zichtbaar in passthrough, om fysiek te kunnen vergelijken tussen headsets). Leeg = een simpele bol van 15cm.")]
    [SerializeField] GameObject m_MarkerPrefab;

    [Header("Input (optioneel - toetsen 1/2/3 (Editor) en controllerknoppen werken altijd als fallback)")]
    [SerializeField] InputActionReference m_CreateAndShareAction;
    [SerializeField] InputActionReference m_DownloadSharedAction;
    [SerializeField] InputActionReference m_LoadPersistedAction;

    const string k_PersistedAnchorGuidsKey = "AnchorColocationTester_SavedGuids";

    UnityEngine.XR.InputDevice m_LeftController;
    UnityEngine.XR.InputDevice m_RightController;
    bool m_PrevRightA, m_PrevRightB, m_PrevLeftY;

    MetaOpenXRAnchorSubsystem MetaSubsystem => m_AnchorManager != null
        ? m_AnchorManager.subsystem as MetaOpenXRAnchorSubsystem
        : null;

    void OnEnable()
    {
        Bind(m_CreateAndShareAction, OnCreateAndSharePressed);
        Bind(m_DownloadSharedAction, OnDownloadSharedPressed);
        Bind(m_LoadPersistedAction, OnLoadPersistedPressed);

        m_LeftController = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
        m_RightController = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
    }

    void OnDisable()
    {
        Unbind(m_CreateAndShareAction, OnCreateAndSharePressed);
        Unbind(m_DownloadSharedAction, OnDownloadSharedPressed);
        Unbind(m_LoadPersistedAction, OnLoadPersistedPressed);
    }

    void Update()
    {
        if (Keyboard.current != null)
        {
            if (Keyboard.current.digit1Key.wasPressedThisFrame) CreateAndShareAsync();
            if (Keyboard.current.digit2Key.wasPressedThisFrame) DownloadSharedAsync();
            if (Keyboard.current.digit3Key.wasPressedThisFrame) LoadPersistedAsync();
        }

        // Rechter controller A-knop (primaryButton) = CreateAndShare (host).
        if (m_RightController.isValid &&
            m_RightController.TryGetFeatureValue(UnityEngine.XR.CommonUsages.primaryButton, out bool aPressed))
        {
            if (aPressed && !m_PrevRightA) CreateAndShareAsync();
            m_PrevRightA = aPressed;
        }

        // Rechter controller B-knop (secondaryButton) = DownloadShared (client).
        if (m_RightController.isValid &&
            m_RightController.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out bool bPressed))
        {
            if (bPressed && !m_PrevRightB) DownloadSharedAsync();
            m_PrevRightB = bPressed;
        }

        // Linker controller Y-knop (secondaryButton) = LoadPersisted (host + client).
        // Bewust NIET linker X (primaryButton) - dat is de bestaande
        // ArUco-kalibratieknop in MarkerColocationAligner.
        if (m_LeftController.isValid &&
            m_LeftController.TryGetFeatureValue(UnityEngine.XR.CommonUsages.secondaryButton, out bool yPressed))
        {
            if (yPressed && !m_PrevLeftY) LoadPersistedAsync();
            m_PrevLeftY = yPressed;
        }
    }

    static void Bind(InputActionReference actionRef, Action<InputAction.CallbackContext> handler)
    {
        if (actionRef == null || actionRef.action == null) return;
        actionRef.action.performed += handler;
        actionRef.action.Enable();
    }

    static void Unbind(InputActionReference actionRef, Action<InputAction.CallbackContext> handler)
    {
        if (actionRef == null || actionRef.action == null) return;
        actionRef.action.performed -= handler;
    }

    void OnCreateAndSharePressed(InputAction.CallbackContext ctx) => CreateAndShareAsync();
    void OnDownloadSharedPressed(InputAction.CallbackContext ctx) => DownloadSharedAsync();
    void OnLoadPersistedPressed(InputAction.CallbackContext ctx) => LoadPersistedAsync();

    void ShowHud(string message)
    {
        Debug.Log($"[AnchorColocationTester] {message}");
        if (PlayerHudNotification.Instance != null)
            PlayerHudNotification.Instance.ShowText(message);
    }

    GameObject m_MarkerInstance;

    /// <summary>
    /// Zet een zichtbaar object (in passthrough) op de anchor-pose, zodat je
    /// fysiek kunt lopen kijken of dit op dezelfde plek staat op elke headset -
    /// in plaats van alleen op cijfers in de HUD te vertrouwen.
    /// </summary>
    void ShowMarkerAt(Pose pose)
    {
        if (m_MarkerInstance == null)
        {
            m_MarkerInstance = m_MarkerPrefab != null
                ? Instantiate(m_MarkerPrefab)
                : GameObject.CreatePrimitive(PrimitiveType.Sphere);

            if (m_MarkerPrefab == null)
                m_MarkerInstance.transform.localScale = Vector3.one * 0.15f;
        }

        m_MarkerInstance.transform.SetPositionAndRotation(pose.position, pose.rotation);
        m_MarkerInstance.SetActive(true);
    }

    bool TryGetSubsystemOrShowError(out MetaOpenXRAnchorSubsystem subsystem)
    {
        subsystem = MetaSubsystem;
        if (subsystem != null)
            return true;

        ShowHud("Fout: geen MetaOpenXRAnchorSubsystem.\nStaat 'Meta Quest: Anchors' aan in OpenXR Feature Groups?");
        return false;
    }

    async void CreateAndShareAsync()
    {
        if (!TryGetSubsystemOrShowError(out var subsystem))
            return;

        var pose = new Pose(m_AnchorPoseSource.position, m_AnchorPoseSource.rotation);
        var addResult = await m_AnchorManager.TryAddAnchorAsync(pose);
        if (addResult.status.IsError())
        {
            ShowHud($"Aanmaken anchor mislukt: {addResult.status}");
            return;
        }

        var anchorId = addResult.value.trackableId;
        subsystem.sharedAnchorsGroupId = new SerializableGuid(Guid.Parse(m_SharedGroupIdString));

        var shareStatus = await subsystem.TryShareAnchorAsync(anchorId);
        if (shareStatus.IsError())
        {
            ShowHud($"Delen anchor mislukt (internet nodig!): {shareStatus}");
            return;
        }

        await SaveLocallyAsync(subsystem, anchorId, "host");
        ShowMarkerAt(pose);
        ShowHud("Anchor aangemaakt, gedeeld EN lokaal bewaard.\nZet nu de client-headsets aan om te downloaden.");
    }

    async void DownloadSharedAsync()
    {
        if (!TryGetSubsystemOrShowError(out var subsystem))
            return;

        subsystem.sharedAnchorsGroupId = new SerializableGuid(Guid.Parse(m_SharedGroupIdString));

        var loaded = new List<XRAnchor>();
        var status = await m_AnchorManager.TryLoadAllSharedAnchorsAsync(loaded, null);
        if (status.IsError())
        {
            ShowHud($"Downloaden gedeelde anchors mislukt (internet nodig!): {status}");
            return;
        }

        if (loaded.Count == 0)
        {
            ShowHud("Geen gedeelde anchors gevonden voor deze groep-id.\nIs CreateAndShare al gelukt op de host?");
            return;
        }

        foreach (var xrAnchor in loaded)
            await SaveLocallyAsync(subsystem, xrAnchor.trackableId, "client");

        ShowHud($"{loaded.Count} gedeelde anchor(s) gedownload EN lokaal bewaard.\nJe kunt nu offline (IX-LAN) testen met LoadPersisted.");
    }

    async Awaitable SaveLocallyAsync(MetaOpenXRAnchorSubsystem subsystem, TrackableId anchorId, string label)
    {
        var saveResult = await subsystem.TrySaveAnchorAsync(anchorId);
        if (saveResult.status.IsError())
        {
            ShowHud($"Lokaal bewaren ({label}) mislukt: {saveResult.status}");
            return;
        }

        var guids = LoadStoredGuids();
        string guidString = saveResult.value.ToString();
        if (!guids.Contains(guidString))
            guids.Add(guidString);
        PlayerPrefs.SetString(k_PersistedAnchorGuidsKey, string.Join(",", guids));
        PlayerPrefs.Save();
    }

    async void LoadPersistedAsync()
    {
        if (!TryGetSubsystemOrShowError(out var subsystem))
            return;

        var guids = LoadStoredGuids();
        if (guids.Count == 0)
        {
            ShowHud("Geen lokaal bewaarde anchor-guid op dit toestel.\nEerst CreateAndShare (host) of DownloadShared (client) draaien.");
            return;
        }

        foreach (var guidString in guids)
        {
            var guid = new SerializableGuid(Guid.Parse(guidString));
            var loadResult = await subsystem.TryLoadAnchorAsync(guid);
            if (loadResult.status.IsError())
            {
                ShowHud($"Offline laden mislukt voor {guidString}: {loadResult.status}");
                continue;
            }

            var pose = loadResult.value.pose;
            ShowMarkerAt(pose);
            ShowHud(
                "<b>Offline geladen (geen internet gebruikt)</b>\n" +
                $"pos: ({pose.position.x:F2}, {pose.position.y:F2}, {pose.position.z:F2})\n" +
                "Vergelijk deze positie t.o.v. de fysieke plek op alle headsets.");
        }
    }

    static List<string> LoadStoredGuids()
    {
        string stored = PlayerPrefs.GetString(k_PersistedAnchorGuidsKey, "");
        return string.IsNullOrEmpty(stored) ? new List<string>() : new List<string>(stored.Split(','));
    }
}
