using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

// ============================================================================
//  ConfigUIManager
// ----------------------------------------------------------------------------
//  Steuert das Einstellungs-Panel (Panel_Config) im VR-Hauptmenü.
//
//  Einzustellen sind:
//    1. STIMME (Voice): kommt LOKAL aus dem ApiConfig-Asset.
//    2. SZENARIO (System-Prompt): kommt aus der CLOUD (Sciebo, prompts.json).
//
//  Der vollständige Text des gewählten Szenarios wird angezeigt, damit die
//  Versuchsleitung direkt in VR lesen kann, welche Anweisung die KI bekommt.
//
//  Bei "Anwenden" werden Stimme + Szenario in PlayerPrefs gespeichert. Diese
//  überleben den Szenenwechsel (Menü -> Beratungsraum) und werden dort vom
//  AIConversationManager beim Start ausgelesen.
//
//  WICHTIG - warum bei jedem Öffnen neu geladen wird:
//  Früher wurde die Cloud-Liste nur EINMAL geladen und dann gemerkt. Dadurch
//  kamen Änderungen aus Sciebo erst nach einem Neustart der App an.
//  Jetzt wird bei jedem Öffnen des Panels frisch geladen.
// ============================================================================
public class ConfigUIManager : MonoBehaviour
{
    // PlayerPrefs-Schlüssel (Konstanten -> keine Tippfehler; der
    // AIConversationManager benutzt exakt dieselben Namen).
    public const string PREF_VOICE       = "ConfigVoice";       // Azure-Stimmen-ID
    public const string PREF_PROMPT      = "ConfigPrompt";      // System-Prompt (Volltext)
    public const string PREF_PROMPT_NAME = "ConfigPromptName";  // Szenario-Name

    [Header("UI-Referenzen")]
    [SerializeField] private TMP_Dropdown voiceDropdown;      // Auswahlmenü Stimme
    [SerializeField] private TMP_Dropdown promptDropdown;     // Auswahlmenü Szenario (Cloud)
    [SerializeField] private Button applyButton;              // "Anwenden"
    [Tooltip("Zeigt den vollständigen Text des gewählten Szenarios zum Lesen.")]
    [SerializeField] private TMP_Text scenarioDisplayText;
    [Tooltip("Optional: Knopf zum manuellen Neuladen der Szenarien aus der Cloud.")]
    [SerializeField] private Button reloadButton;             // darf leer bleiben

    [Header("Konfiguration")]
    [SerializeField] private ApiConfig config;                // liefert die Stimmen-Liste
    [Tooltip("Sciebo Direkt-Download-Link zur prompts.json (muss auf /download enden!)")]
    [SerializeField] private string cloudPromptUrl = "";

    // Die aktuell geladenen Cloud-Szenarien. Wird gebraucht, um vom
    // Dropdown-Index auf den vollständigen Text zu schließen.
    private List<PromptCloudLoader.PromptEntry> loadedPrompts = new List<PromptCloudLoader.PromptEntry>();

    private bool initialized = false;  // wurde Awake() erfolgreich durchlaufen?
    private bool isLoading   = false;  // läuft gerade ein Ladevorgang?

    // ------------------------------------------------------------------------
    //  Awake(): läuft einmal beim Erzeugen des Objekts.
    // ------------------------------------------------------------------------
    private void Awake()
    {
        if (config == null)
        {
            Debug.LogError("[ConfigUI] ApiConfig nicht zugewiesen! " +
                           "Bitte im Inspector das ApiConfig-Asset in das Feld 'Config' ziehen.");
            return;
        }

        SetupVoiceDropdown();

        // "Anwenden"-Knopf verbinden.
        if (applyButton != null)
        {
            applyButton.onClick.RemoveAllListeners();   // doppelte Verbindungen vermeiden
            applyButton.onClick.AddListener(ApplyChanges);
        }

        // Optionaler "Neu laden"-Knopf.
        if (reloadButton != null)
        {
            reloadButton.onClick.RemoveAllListeners();
            reloadButton.onClick.AddListener(ReloadPrompts);
        }

        // Wenn im Szenario-Dropdown etwas gewählt wird, Text-Anzeige aktualisieren.
        if (promptDropdown != null)
        {
            promptDropdown.onValueChanged.RemoveAllListeners();
            promptDropdown.onValueChanged.AddListener(OnPromptSelectionChanged);
        }

        initialized = true;
    }

    // ------------------------------------------------------------------------
    //  OnEnable(): läuft jedes Mal, wenn das Panel sichtbar geschaltet wird.
    //  Lädt die Szenarien IMMER neu, damit Änderungen aus Sciebo sofort
    //  ankommen (kein Neustart der App nötig).
    // ------------------------------------------------------------------------
    private async void OnEnable()
    {
        if (!initialized) return;
        await LoadPromptsFromCloud();
    }

    // Öffentliche Methode für den "Neu laden"-Knopf.
    public async void ReloadPrompts()
    {
        await LoadPromptsFromCloud();
    }

    // ========================================================================
    //  STIMMEN-DROPDOWN (lokal aus ApiConfig)
    // ========================================================================
    private void SetupVoiceDropdown()
    {
        if (voiceDropdown == null) return;

        // Namen aller verfügbaren Stimmen sammeln.
        List<string> namen = new List<string>();
        foreach (var voice in config.availableVoices)
            namen.Add(voice.displayName);

        voiceDropdown.ClearOptions();
        voiceDropdown.AddOptions(namen);

        // Zuletzt gewählte Stimme vorauswählen (sonst Standard aus ApiConfig).
        string savedVoice = PlayerPrefs.GetString(PREF_VOICE, config.voiceName);
        int index = config.availableVoices.FindIndex(v => v.azureVoiceId == savedVoice);
        if (index < 0) index = 0;

        // SetValueWithoutNotify setzt den Wert, ohne das Änderungs-Ereignis
        // auszulösen. Sonst würde beim Aufbau unnötig Logik mitlaufen.
        voiceDropdown.SetValueWithoutNotify(index);
        voiceDropdown.RefreshShownValue();
    }

    // ========================================================================
    //  SZENARIO-DROPDOWN (aus der Cloud)
    // ========================================================================
    private async Task LoadPromptsFromCloud()
    {
        // Doppelte Ladevorgänge verhindern (z.B. schnelles Auf/Zu des Panels).
        if (isLoading) return;
        isLoading = true;

        try
        {
            SetDisplayText("Lade Szenarien aus der Cloud...");

            // Merken, welches Szenario vorher gewählt war, um es nach dem
            // Neuladen wieder auszuwählen (falls es noch existiert).
            string zuletztGewaehlt = PlayerPrefs.GetString(PREF_PROMPT_NAME, "");

            var prompts = await PromptCloudLoader.LoadAsync(cloudPromptUrl);

            // Objekt könnte inzwischen zerstört worden sein (Szenenwechsel).
            if (this == null || promptDropdown == null) return;

            // --- Fehlerfall ---
            if (prompts == null || prompts.Count == 0)
            {
                loadedPrompts = new List<PromptCloudLoader.PromptEntry>();
                promptDropdown.ClearOptions();
                promptDropdown.AddOptions(new List<string> { "(keine Szenarien)" });
                promptDropdown.SetValueWithoutNotify(0);
                promptDropdown.RefreshShownValue();
                SetDisplayText("FEHLER: Szenarien konnten nicht geladen werden.\n" +
                               "Bitte Internetverbindung und Sciebo-Link prüfen. " +
                               "Details stehen in der Konsole.");
                return;
            }

            // --- Erfolgsfall ---
            loadedPrompts = prompts;

            // Dropdown komplett neu aufbauen. AddOptions mit einer Namensliste
            // ist zuverlässiger als options.Add einzeln.
            List<string> namen = new List<string>();
            foreach (var p in loadedPrompts)
                namen.Add(p.name);

            promptDropdown.ClearOptions();
            promptDropdown.AddOptions(namen);

            // Vorher gewähltes Szenario wiederfinden. Wenn es nicht mehr
            // existiert (umbenannt/gelöscht), nehmen wir das erste.
            int index = loadedPrompts.FindIndex(p => p.name == zuletztGewaehlt);
            if (index < 0) index = 0;

            // Sicherheitsnetz: Index darf niemals außerhalb der Liste liegen.
            index = Mathf.Clamp(index, 0, loadedPrompts.Count - 1);

            promptDropdown.SetValueWithoutNotify(index);
            promptDropdown.RefreshShownValue();

            // Anzeige-Text passend zur Auswahl setzen.
            OnPromptSelectionChanged(index);

            Debug.Log($"[ConfigUI] {loadedPrompts.Count} Szenarien im Dropdown, " +
                      $"ausgewählt: {loadedPrompts[index].name}");
        }
        finally
        {
            isLoading = false;
        }
    }

    // ------------------------------------------------------------------------
    //  Wird aufgerufen, wenn im Szenario-Dropdown etwas gewählt wird.
    //  Zeigt den vollständigen Prompt-Text im Anzeige-Feld.
    // ------------------------------------------------------------------------
    private void OnPromptSelectionChanged(int index)
    {
        // Index prüfen, bevor wir zugreifen - verhindert Fehler, wenn sich die
        // Liste zwischenzeitlich geändert hat.
        if (index >= 0 && index < loadedPrompts.Count)
            SetDisplayText(loadedPrompts[index].text);
        else
            SetDisplayText("(kein Szenario ausgewählt)");
    }

    // ========================================================================
    //  ANWENDEN: speichert Stimme + Szenario in PlayerPrefs.
    // ========================================================================
    public void ApplyChanges()
    {
        // --- Stimme speichern ---
        int voiceIndex = Mathf.Clamp(voiceDropdown.value, 0, config.availableVoices.Count - 1);
        string voiceId = config.availableVoices[voiceIndex].azureVoiceId;
        PlayerPrefs.SetString(PREF_VOICE, voiceId);

        // --- Szenario speichern ---
        if (loadedPrompts.Count > 0 &&
            promptDropdown.value >= 0 &&
            promptDropdown.value < loadedPrompts.Count)
        {
            var selected = loadedPrompts[promptDropdown.value];
            PlayerPrefs.SetString(PREF_PROMPT, selected.text);
            PlayerPrefs.SetString(PREF_PROMPT_NAME, selected.name);
            Debug.Log($"[ConfigUI] Gespeichert - Stimme: {voiceId}, Szenario: {selected.name}");
        }
        else
        {
            Debug.LogWarning("[ConfigUI] Kein gültiges Szenario gewählt - " +
                             "es wurde nur die Stimme gespeichert.");
        }

        // Dauerhaft auf die Festplatte schreiben.
        PlayerPrefs.Save();
        Debug.Log("[ConfigUI] Einstellungen übernommen.");
    }

    // ------------------------------------------------------------------------
    //  Hilfsmethode: schreibt Text ins Anzeige-Feld (falls zugewiesen).
    // ------------------------------------------------------------------------
    private void SetDisplayText(string message)
    {
        if (scenarioDisplayText != null)
            scenarioDisplayText.text = message;
    }
}