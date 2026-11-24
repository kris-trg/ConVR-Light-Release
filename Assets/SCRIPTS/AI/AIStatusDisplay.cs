using UnityEngine;
using TMPro;

// ============================================================================
//  AIStatusDisplay
// ----------------------------------------------------------------------------
//  Zeigt der Versuchsperson im VR-Raum an, was die KI gerade macht.
//
//  Warum das nötig ist:
//  Ohne diese Anzeige weiß niemand, ob die Aufnahme läuft oder ob die KI
//  gerade nachdenkt. Man drückt den Trigger und es passiert scheinbar nichts,
//  weil die Pipeline mehrere Sekunden braucht.
//
//  Verwendung:
//    1. Ein TextMeshPro-Textfeld auf einem World-Space-Canvas anlegen,
//       z.B. über dem Kopf des Avatars.
//    2. Dieses Skript an den Canvas hängen.
//    3. Im Inspector den AIConversationManager und das Textfeld zuweisen.
// ============================================================================
public class AIStatusDisplay : MonoBehaviour
{
    [Header("Referenzen")]
    [Tooltip("Der AIConversationManager - hängt am Avatar (z.B. Ch21_nonPBR).")]
    [SerializeField] private AIConversationManager manager;

    [Tooltip("Das Textfeld, in dem der Status angezeigt wird.")]
    [SerializeField] private TMP_Text statusText;

    [Header("Optional: farbige Anzeige")]
    [Tooltip("Wenn zugewiesen, wird dieses Bild passend zum Zustand eingefärbt.")]
    [SerializeField] private UnityEngine.UI.Image statusIndicator;

    [Header("Texte (frei anpassbar)")]
    [SerializeField] private string textBereit      = "Trigger drücken zum Sprechen";
    [SerializeField] private string textNimmtAuf    = "Ich höre zu ...   (Trigger = fertig)";
    [SerializeField] private string textVerarbeitet = "Einen Moment ...";
    [SerializeField] private string textSpricht     = "Antwortet ...";

    [Header("Farben")]
    [SerializeField] private Color farbeBereit      = new Color(0.75f, 0.75f, 0.75f);  // grau
    [SerializeField] private Color farbeNimmtAuf    = new Color(0.90f, 0.30f, 0.30f);  // rot
    [SerializeField] private Color farbeVerarbeitet = new Color(0.95f, 0.75f, 0.20f);  // gelb
    [SerializeField] private Color farbeSpricht     = new Color(0.35f, 0.80f, 0.45f);  // grün
    [SerializeField] private Color farbeFehler      = new Color(0.95f, 0.45f, 0.15f);  // orange

    // ------------------------------------------------------------------------
    //  OnEnable / OnDisable: beim Manager an- bzw. abmelden.
    //  Das Abmelden ist wichtig, damit es keine Fehler gibt, wenn dieses
    //  Objekt zerstört wird, der Manager aber weiterläuft.
    // ------------------------------------------------------------------------
    private void OnEnable()
    {
        if (manager == null)
        {
            Debug.LogError("[AIStatus] Kein AIConversationManager zugewiesen! " +
                           "Bitte im Inspector den Avatar in das Feld 'Manager' ziehen.");
            return;
        }

        // Beim Zustands-Ereignis anmelden.
        manager.OnStateChanged += HandleStateChanged;

        // Direkt den aktuellen Zustand anzeigen.
        HandleStateChanged(manager.CurrentState, "");
    }

    private void OnDisable()
    {
        if (manager != null)
            manager.OnStateChanged -= HandleStateChanged;
    }

    // ------------------------------------------------------------------------
    //  Wird vom Manager aufgerufen, sobald sich der Zustand ändert.
    //    state -> der neue Zustand
    //    info  -> zusätzliche Information (z.B. Fehlertext)
    // ------------------------------------------------------------------------
    private void HandleStateChanged(AIConversationManager.AIState state, string info)
    {
        string anzeige;
        Color farbe;

        switch (state)
        {
            case AIConversationManager.AIState.NimmtAuf:
                anzeige = textNimmtAuf;
                farbe = farbeNimmtAuf;
                break;

            case AIConversationManager.AIState.Verarbeitet:
                anzeige = textVerarbeitet;
                farbe = farbeVerarbeitet;
                break;

            case AIConversationManager.AIState.Spricht:
                anzeige = textSpricht;
                farbe = farbeSpricht;
                break;

            case AIConversationManager.AIState.Fehler:
                // Wenn der Manager einen Grund mitgeliefert hat, diesen anzeigen.
                anzeige = string.IsNullOrEmpty(info)
                          ? "Etwas hat nicht geklappt - bitte nochmal"
                          : info + " - bitte nochmal";
                farbe = farbeFehler;
                break;

            default:  // Bereit
                anzeige = textBereit;
                farbe = farbeBereit;
                break;
        }

        if (statusText != null)
        {
            statusText.text = anzeige;
            statusText.color = farbe;
        }

        if (statusIndicator != null)
            statusIndicator.color = farbe;
    }
}