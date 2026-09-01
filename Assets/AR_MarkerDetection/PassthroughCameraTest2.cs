using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// Minimaal testscript om te verifiëren:
/// 1. Of de Passthrough Camera API data levert via CPU image capture (frameReceived).
/// 2. Of het AR Camera Background-component visueel passthrough toont aan de gebruiker
///    (dit willen we juist NIET, want de speler moet volledig in VR blijven).
///
/// Zet dit script op een leeg GameObject in je testscene, en sleep de AR Camera Manager
/// erin via de Inspector.
/// </summary>
public class PassthroughCameraTest2 : MonoBehaviour
{
    [SerializeField] ARCameraManager m_CameraManager;
    [SerializeField] ARCameraBackground m_CameraBackground;

    [Tooltip("Zet dit UIT om te testen of CPU image capture werkt ZONDER dat de gebruiker" +
             "de passthrough-achtergrond ziet. Dit is de kernvraag van deze test.")]
    [SerializeField] bool m_EnableVisualBackground = false;

    int m_FrameCount = 0;

    void OnEnable()
    {
        if (m_CameraManager == null)
        {
            Debug.LogError("[PassthroughTest2] Geen ARCameraManager toegewezen!");
            return;
        }

        m_CameraManager.frameReceived += OnFrameReceived;

        if (m_CameraBackground != null)
        {
            m_CameraBackground.enabled = m_EnableVisualBackground;
            Debug.Log($"[PassthroughTest2] AR Camera Background component enabled = {m_EnableVisualBackground}");
        }

        // Log alle root-objecten in de scene, om te ontdekken hoe het automatisch
        // aangemaakte Passthrough-compositielaag-object heet (dit bestaat alleen
        // op de headset zelf, niet in de Editor/Desktop).
        LogAllRootObjects();

        // Probeer het automatisch aangemaakte "Passthrough" GameObject te vinden
        // en de visuele compositielaag uit te schakelen, zonder de onderliggende
        // camera-databeschikbaarheid (AR Camera Manager) te raken.
        DisablePassthroughCompositionLayer();
    }

    /// <summary>
    /// Zoekt het automatisch door OpenXR/AR Foundation aangemaakte "Passthrough"
    /// GameObject (met een CompositionLayer-component) en schakelt dat uit, zodat
    /// de gebruiker geen visuele passthrough ziet, terwijl de camera-databeschikbaarheid
    /// via ARCameraManager gewoon actief blijft.
    /// </summary>
    void DisablePassthroughCompositionLayer()
    {
        var scene = gameObject.scene;
        foreach (var root in scene.GetRootGameObjects())
        {
            if (root.name == "Passthrough")
            {
                bool disabledAComponent = false;

                // Doorloop alle componenten op dit object en schakel elk component uit
                // waarvan de klassenaam "CompositionLayer" bevat. Dit vermijdt afhankelijkheid
                // van de exacte namespace, die kan verschillen per package-versie.
                foreach (var component in root.GetComponents<Behaviour>())
                {
                    if (component.GetType().Name.Contains("CompositionLayer"))
                    {
                        component.enabled = false;
                        disabledAComponent = true;
                        Debug.Log($"[PassthroughTest2] Component '{component.GetType().FullName}' op 'Passthrough' UITGESCHAKELD.");
                    }
                }

                if (!disabledAComponent)
                {
                    // Fallback: geen los uitschakelbaar component gevonden (bv. omdat het
                    // geen Behaviour is), schakel dan het hele GameObject uit.
                    root.SetActive(false);
                    Debug.Log("[PassthroughTest2] Geen uitschakelbaar CompositionLayer-component gevonden; " +
                              "'Passthrough' GameObject volledig uitgeschakeld als fallback.");
                }

                return;
            }
        }
        Debug.LogWarning("[PassthroughTest2] Geen 'Passthrough' root-object gevonden om uit te schakelen.");
    }

    void LogAllRootObjects()
    {
        var scene = gameObject.scene;
        var rootObjects = scene.GetRootGameObjects();
        Debug.Log($"[PassthroughTest2] Aantal root-objecten in scene '{scene.name}': {rootObjects.Length}");
        foreach (var root in rootObjects)
        {
            Debug.Log($"[PassthroughTest2] Root-object: '{root.name}', actief={root.activeSelf}, componenten: " +
                      string.Join(", ", System.Array.ConvertAll(root.GetComponents<Component>(), c => c.GetType().Name)));

            // Log ook direct child-objecten, voor het geval het Passthrough-object
            // een niveau dieper zit i.p.v. als root.
            foreach (Transform child in root.transform)
            {
                Debug.Log($"[PassthroughTest2]   Child: '{child.name}', componenten: " +
                          string.Join(", ", System.Array.ConvertAll(child.GetComponents<Component>(), c => c.GetType().Name)));
            }
        }
    }

    void OnDisable()
    {
        if (m_CameraManager != null)
            m_CameraManager.frameReceived -= OnFrameReceived;
    }

    void OnFrameReceived(ARCameraFrameEventArgs eventArgs)
    {
        m_FrameCount++;

        // Log slechts elke 60 frames (~1x per seconde bij 60Hz) om de log niet te overspoelen.
        if (m_FrameCount % 60 == 0)
        {
            Debug.Log($"[PassthroughTest2] Frame ontvangen (#{m_FrameCount}). " +
                      $"CPU image capture werkt dus, ongeacht of de visuele achtergrond aanstaat.");
        }

        // Probeer daadwerkelijk een CPU-image op te halen, om te bevestigen dat de
        // onderliggende data ook echt beschikbaar is (niet alleen het event zelf).
        if (m_CameraManager.TryAcquireLatestCpuImage(out XRCpuImage image))
        {
            if (m_FrameCount % 60 == 0)
            {
                Debug.Log($"[PassthroughTest2] CPU image succesvol opgehaald: " +
                          $"{image.width}x{image.height}, format: {image.format}");
            }
            image.Dispose(); // Belangrijk: altijd disposen om geheugenlekken te voorkomen.
        }
        else
        {
            if (m_FrameCount % 60 == 0)
                Debug.LogWarning("[PassthroughTest2] Kon geen CPU image ophalen (nog).");
        }
    }
}