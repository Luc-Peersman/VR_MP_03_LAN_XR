using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// [v8] Herbouwd vanaf de bevestigd werkende basis van PassthroughCameraTest2,
/// met minimale wijzigingen, om het eerdere, onverklaarde acquisitie-probleem
/// van v1-v4 te vermijden. Structuur blijft zo dicht mogelijk bij het origineel.
///
/// v8: IsCapturingEnabled-vlag toegevoegd (standaard UIT) - camera-frame-verwerking
/// (YUV->RGBA-conversie) draait nu alleen als hier expliciet om gevraagd wordt,
/// i.p.v. continu. Gekoppeld aan ArUcoTrackingCoordinator.IsSearchingEnabled.
///
/// v7: Transformation gewijzigd naar MirrorY - test tegen mogelijk spiegelbeeld-
/// probleem dat ArUco-detectie zou kunnen blokkeren (patroon niet spiegel-symmetrisch).
/// v6: DisablePassthroughCompositionLayer() herstelt - was per ongeluk verloren
/// gegaan bij de v5-herbouw, waardoor passthrough weer zichtbaar werd.
/// </summary>
public class PassthroughCameraToTexture : MonoBehaviour
{
    [SerializeField] ARCameraManager m_CameraManager;
    [SerializeField] ARCameraBackground m_CameraBackground;

    [SerializeField] bool m_EnableVisualBackground = false;

    public Texture2D CameraTexture => m_CameraTexture;
    public event System.Action<Texture2D> OnTextureUpdated;

    Texture2D m_CameraTexture;
    bool m_IntrinsicsLogged = false;
    int m_FrameCount = 0;

    void OnEnable()
    {
        if (m_CameraManager == null)
        {
            Debug.LogError("[PassthroughCameraToTexture] Geen ARCameraManager toegewezen!");
            return;
        }

        m_CameraManager.frameReceived += OnFrameReceived;

        if (m_CameraBackground != null)
        {
            m_CameraBackground.enabled = m_EnableVisualBackground;
            Debug.Log($"[PassthroughCameraToTexture] AR Camera Background component enabled = {m_EnableVisualBackground}");
        }

        DisablePassthroughCompositionLayer();
    }

    void DisablePassthroughCompositionLayer()
    {
        var scene = gameObject.scene;
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.name == "Passthrough")
            {
                bool disabledAComponent = false;
                foreach (var component in root.GetComponents<Behaviour>())
                {
                    if (component.GetType().Name.Contains("CompositionLayer"))
                    {
                        component.enabled = false;
                        disabledAComponent = true;
                        Debug.Log($"[PassthroughCameraToTexture] Component '{component.GetType().FullName}' op 'Passthrough' UITGESCHAKELD.");
                    }
                }

                if (!disabledAComponent)
                {
                    root.SetActive(false);
                    Debug.Log("[PassthroughCameraToTexture] Geen uitschakelbaar CompositionLayer-component gevonden; " +
                              "'Passthrough' GameObject volledig uitgeschakeld als fallback.");
                }
                return;
            }
        }
        Debug.LogWarning("[PassthroughCameraToTexture] Geen 'Passthrough' root-object gevonden om uit te schakelen.");
    }

    void OnDisable()
    {
        if (m_CameraManager != null)
            m_CameraManager.frameReceived -= OnFrameReceived;
    }

    /// <summary>
    /// Bepaalt of camera-frames daadwerkelijk verwerkt worden (YUV->RGBA-conversie
    /// + texture-upload, relatief kostbaar per frame). Standaard UIT, zodat dit
    /// géén doorlopende rekenlast geeft zolang niemand om de textuur vraagt (bv.
    /// via ArUcoTrackingCoordinator, gekoppeld aan de X-knop-kalibratie).
    /// </summary>
    public bool IsCapturingEnabled { get; set; } = false;

    void OnFrameReceived(ARCameraFrameEventArgs eventArgs)
    {
        if (!IsCapturingEnabled)
            return;
        m_FrameCount++;

        // EXACT dezelfde structuur als de bevestigd werkende PassthroughCameraTest2:
        // if (Try...) { succes-pad } else { faal-pad } - GEEN vroege 'return'.
        if (m_CameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
        {
            if (m_FrameCount % 60 == 0)
            {
                Debug.Log($"[PassthroughCameraToTexture] CPU image succesvol opgehaald: " +
                          $"{image.width}x{image.height}, format: {image.format}");
            }

            if (m_CameraTexture == null || m_CameraTexture.width != image.width || m_CameraTexture.height != image.height)
            {
                m_CameraTexture = new Texture2D(image.width, image.height, TextureFormat.RGBA32, false);
                Debug.Log($"[PassthroughCameraToTexture] Texture aangemaakt: {image.width}x{image.height}");
            }

            var conversionParams = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, image.width, image.height),
                outputDimensions = new Vector2Int(image.width, image.height),
                outputFormat = TextureFormat.RGBA32,
                transformation = XRCpuImage.Transformation.MirrorY // TEST: was None
            };

            var rawData = m_CameraTexture.GetRawTextureData<byte>();
            image.Convert(conversionParams, rawData);
            m_CameraTexture.Apply();

            if (!m_IntrinsicsLogged && m_CameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics))
            {
                m_IntrinsicsLogged = true;
                Debug.Log($"[PassthroughCameraToTexture] Camera intrinsics: " +
                          $"focalLength={intrinsics.focalLength}, " +
                          $"principalPoint={intrinsics.principalPoint}, " +
                          $"resolution={intrinsics.resolution}");
            }

            OnTextureUpdated?.Invoke(m_CameraTexture);

            image.Dispose();
        }
        else
        {
            if (m_FrameCount % 60 == 0)
                Debug.LogWarning("[PassthroughCameraToTexture] Kon geen CPU image ophalen (nog).");
        }
    }

    public bool TryGetIntrinsics(out XRCameraIntrinsics intrinsics)
    {
        if (m_CameraManager != null)
            return m_CameraManager.TryGetIntrinsics(out intrinsics);

        intrinsics = default;
        return false;
    }
}