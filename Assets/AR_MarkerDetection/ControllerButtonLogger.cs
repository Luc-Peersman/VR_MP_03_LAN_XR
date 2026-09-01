using UnityEngine;
using UnityEngine.XR;

/// <summary>
/// [v1] Zelfstandig script dat een knop op een VR-controller uitleest (via de
/// legacy XR Input-API, geen Input Actions asset-configuratie nodig) en bij een
/// druk MarkerDebugger.LogMeasurement() aanroept.
///
/// Simpeler alternatief voor de InputActionReference-route in MarkerDebugger:
/// dit script werkt direct, zonder dat je eerst een nieuwe Action in een asset
/// hoeft aan te maken.
/// </summary>
public class ControllerButtonLogger : MonoBehaviour
{
    [Tooltip("Welke controller je wilt gebruiken.")]
    [SerializeField] XRNode m_ControllerNode = XRNode.RightHand;

    [Tooltip("Welke knop de meting moet loggen. 'PrimaryButton' is meestal de " +
             "A/X-knop, 'TriggerButton' is de index-trigger, 'GripButton' is de " +
             "greep-knop aan de zijkant.")]
    [SerializeField] ButtonType m_ButtonType = ButtonType.PrimaryButton;

    [Tooltip("Het MarkerDebugger-component waarop LogMeasurement() aangeroepen wordt.")]
    [SerializeField] MarkerDebugger m_MarkerDebugger;

    public enum ButtonType
    {
        PrimaryButton,
        SecondaryButton,
        TriggerButton,
        GripButton
    }

    InputDevice m_ControllerDevice;
    bool m_WasPressedLastFrame = false;

    void Update()
    {
        if (m_MarkerDebugger == null)
            return;

        // Haal het controller-device op (kan pas na een paar frames beschikbaar
        // zijn, dus elke frame opnieuw checken totdat het lukt).
        if (!m_ControllerDevice.isValid)
        {
            m_ControllerDevice = InputDevices.GetDeviceAtXRNode(m_ControllerNode);
            if (!m_ControllerDevice.isValid)
                return;
        }

        bool isPressed = false;
        InputFeatureUsage<bool> feature = m_ButtonType switch
        {
            ButtonType.PrimaryButton => CommonUsages.primaryButton,
            ButtonType.SecondaryButton => CommonUsages.secondaryButton,
            ButtonType.TriggerButton => CommonUsages.triggerButton,
            ButtonType.GripButton => CommonUsages.gripButton,
            _ => CommonUsages.primaryButton
        };

        m_ControllerDevice.TryGetFeatureValue(feature, out isPressed);

        // Alleen loggen bij het MOMENT van indrukken (niet continu terwijl
        // ingedrukt gehouden wordt).
        if (isPressed && !m_WasPressedLastFrame)
        {
            m_MarkerDebugger.LogMeasurement();
        }

        m_WasPressedLastFrame = isPressed;
    }
}