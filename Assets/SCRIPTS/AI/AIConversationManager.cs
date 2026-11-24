using System;
using UnityEngine;
using UnityEngine.InputSystem;
using System.Text.RegularExpressions;

// ============================================================================
//  AIConversationManager
// ----------------------------------------------------------------------------
//  Zentrale Steuerklasse für das KI-Gespräch. Verbindet vier Bausteine:
//    1. MicrophoneRecorder -> nimmt die Sprache des Beraters auf
//    2. AzureSpeechClient  -> Sprache zu Text (STT) und Text zu Sprache (TTS)
//    3. ClaudeClient       -> erzeugt die Antwort der ratsuchenden Person
//    4. AudioSource        -> spielt die Antwort räumlich aus dem Avatar ab
//
//  Ablauf einer Gesprächsrunde:
//    Trigger drücken -> Mikro nimmt auf -> Trigger erneut -> Aufnahme stoppt ->
//    STT macht Text daraus -> Claude antwortet -> Regieanweisungen werden
//    entfernt -> TTS macht Sprache daraus -> AudioSource spielt sie ab und der
//    Avatar wechselt in die Sprech-Animation.
//
//  Woher kommen Stimme und Szenario?
//    Beide werden im Hauptmenü gewählt und über PlayerPrefs übergeben (siehe
//    Awake). Im normalen Studienbetrieb gibt es keinen fest einprogrammierten
//    Prompt - die Szenarien kommen aus der Cloud.
//
//  Status-Anzeige:
//    Die Pipeline braucht mehrere Sekunden. Damit die Versuchsperson sieht,
//    was gerade passiert, meldet diese Klasse ihren Zustand über das Ereignis
//    OnStateChanged. Das Skript AIStatusDisplay hört darauf und zeigt es an.
//
//  Protokollierung:
//    Wenn ein StudyLogger zugewiesen ist, wird nach jeder Runde eine Zeile
//    geschrieben - auch bei Fehlern. So lassen sich Latenzen, Token-Verbrauch
//    und Kosten hinterher auswerten.
//
//  WICHTIG - Token-Erfassung pro Runde:
//    Die Token-Zahlen werden direkt nach dem Claude-Aufruf in lokale Variablen
//    gesichert und von dort ins Protokoll geschrieben. Sie werden NICHT erst
//    am Ende der Runde aus dem ClaudeClient gelesen. Grund: Bricht eine Runde
//    vorher ab (z.B. weil das Mikrofon nichts verstanden hat), hält der Client
//    noch die Werte der vorherigen Runde - die landeten dann samt Kosten ein
//    zweites Mal im Protokoll und verfälschten die Kostenauswertung.
// ============================================================================
[RequireComponent(typeof(AudioSource))]
public class AIConversationManager : MonoBehaviour
{
    [Header("Konfiguration")]
    [Tooltip("Das ApiConfig-Asset mit den API-Schlüsseln und Stimmen.")]
    [SerializeField] private ApiConfig config;

    [Header("Referenzen")]
    [Tooltip("Der MicrophoneRecorder - hängt normalerweise am selben GameObject.")]
    [SerializeField] private MicrophoneRecorder recorder;

    [Tooltip("Optional: Protokolliert jede Gesprächsrunde in eine CSV-Datei. " +
             "Darf leer bleiben, dann wird nichts aufgezeichnet.")]
    [SerializeField] private StudyLogger studyLogger;

    [Header("Animation")]
    [Tooltip("Der Animator des Avatars. Wenn leer, wird beim Start automatisch " +
             "im Elternobjekt gesucht. Bei zur Laufzeit erzeugten Avataren " +
             "sollte das Feld über SetAnimator() gesetzt werden.")]
    [SerializeField] private Animator npcAnimator;

    [Tooltip("Name des Bool-Parameters im Animator Controller, der zwischen " +
             "Idle und Sprechen umschaltet.")]
    [SerializeField] private string talkingParameterName = "isTalking";

    [Header("Eingabe (VR)")]
    [Tooltip("Startet/stoppt die Aufnahme, z.B. XRI Right Interaction/Activate")]
    [SerializeField] private InputActionReference toggleAction;
    
    [Tooltip("Wie lange eine Fehlermeldung angezeigt wird, bevor wieder " +
             "'Bereit' erscheint (in Sekunden).")]
    [SerializeField] private float fehlerAnzeigeDauer = 3f;

    [Header("Kosten (USD pro 1 Million Tokens)")]
    [Tooltip("Preis für Eingabe-Tokens. Bei Claude Sonnet 4.6: 3.00")]
    [SerializeField] private double preisInputProMillion = 3.0;

    [Tooltip("Preis für Ausgabe-Tokens. Bei Claude Sonnet 4.6: 15.00")]
    [SerializeField] private double preisOutputProMillion = 15.0;

    // --- Interne Felder ---
    private AzureSpeechClient azureSpeech;
    private ClaudeClient claude;
    private AudioSource audioSource;
    private bool isProcessing;              // läuft die Pipeline gerade?
    private string currentVoiceName;
    private string currentSystemPrompt;
    private string currentSzenarioName;     // nur zur Protokollierung
    private float fehlerTimer = 0f;

    public bool IsSpeaking => audioSource != null && audioSource.isPlaying;
    public AudioSource AudioSource => audioSource;
    public ApiConfig Config => config;

    private NpcGaze gaze;
    private NpcLipSync lipSync;
    private NpcBlink blink;

    // ========================================================================
    //  STATUS-EVENTS
    // ========================================================================
    public enum AIState
    {
        Bereit,        // wartet auf Tastendruck
        NimmtAuf,      // Mikrofon läuft
        Verarbeitet,   // STT + Claude + TTS laufen
        Spricht,       // Antwort wird abgespielt
        Fehler         // etwas ist schiefgegangen
    }

    public event Action<AIState, string> OnStateChanged;

    private AIState currentState = AIState.Bereit;
    public AIState CurrentState => currentState;

    private void SetState(AIState state, string info = "")
    {
        currentState = state;
        OnStateChanged?.Invoke(state, info);
    }

    // ------------------------------------------------------------------------
    //  Awake(): läuft einmal beim Erzeugen des Objekts.
    // ------------------------------------------------------------------------
    private void Awake()
    {
        audioSource = GetComponent<AudioSource>();

        if (config == null)
        {
            Debug.LogError("[AIConvo] ApiConfig nicht zugewiesen! " +
                           "Bitte im Inspector das ApiConfig-Asset in das Feld 'Config' ziehen.");
            return;
        }

        // === Animator suchen, falls im Inspector nichts zugewiesen wurde ===
        // Der Animator sitzt normalerweise nicht an diesem GameObject selbst,
        // sondern am Avatar-Modell daneben. Deshalb suchen wir im Elternobjekt.
        if (npcAnimator == null && transform.parent != null)
            npcAnimator = transform.parent.GetComponentInChildren<Animator>();

        if (npcAnimator == null)
            Debug.LogWarning("[AIConvo] Kein Animator gefunden. Die Sprech-Animation " +
                             "bleibt aus. Bei Bedarf im Inspector zuweisen.");

        // === Stimme aus PlayerPrefs (im Hauptmenü gewählt) ===
        currentVoiceName = PlayerPrefs.GetString(ConfigUIManager.PREF_VOICE, config.voiceName);

        // === Szenario aus PlayerPrefs (aus der Cloud gewählt) ===
        currentSystemPrompt = PlayerPrefs.GetString(ConfigUIManager.PREF_PROMPT, config.systemPrompt);

        // Den Namen des Szenarios merken - der landet später im Protokoll,
        // damit man sieht, welcher Fall gespielt wurde.
        currentSzenarioName = PlayerPrefs.GetString(ConfigUIManager.PREF_PROMPT_NAME, "unbekannt");

        if (string.IsNullOrWhiteSpace(currentSystemPrompt))
            Debug.LogWarning("[AIConvo] Kein System-Prompt gesetzt! " +
                             "Bitte im Hauptmenü unter Einstellungen ein Szenario wählen.");

        Debug.Log($"[AIConvo] Verwendete Stimme: {currentVoiceName}");
        Debug.Log($"[AIConvo] Szenario: {currentSzenarioName}");

        SetupAvatarComponents();

        InitializeClients();

        if (recorder != null)
            recorder.OnRecordingFinished += HandleAudioReady;
    }

    // ------------------------------------------------------------------------
    //  Baut die Verbindungs-Objekte zu Azure und Claude auf.
    // ------------------------------------------------------------------------
    private void InitializeClients()
    {
        azureSpeech = new AzureSpeechClient(
            config.azureSpeechKey, config.azureRegion, currentVoiceName);

        // Die tatsächlich eingestellte Abtastrate vom Recorder übernehmen,
        // damit die Angabe im HTTP-Header zu den gesendeten Daten passt.
        if (recorder != null)
            azureSpeech.SampleRate = recorder.SampleRate;

        claude = new ClaudeClient(
            config.anthropicApiKey, config.claudeModel,
            config.maxTokens, currentSystemPrompt);
    }

    // ========================================================================
    //  ÖFFENTLICHE API (für Änderungen zur Laufzeit)
    // ========================================================================

    public void SetVoice(string azureVoiceId)
    {
        currentVoiceName = azureVoiceId;
        azureSpeech = new AzureSpeechClient(
            config.azureSpeechKey, config.azureRegion, currentVoiceName);

        if (recorder != null)
            azureSpeech.SampleRate = recorder.SampleRate;

        Debug.Log($"[AIConvo] Stimme gewechselt: {azureVoiceId}");
    }

    public void SetSystemPrompt(string newPrompt)
    {
        currentSystemPrompt = newPrompt;
        claude = new ClaudeClient(
            config.anthropicApiKey, config.claudeModel,
            config.maxTokens, currentSystemPrompt);
        Debug.Log("[AIConvo] System-Prompt aktualisiert + Gesprächsverlauf zurückgesetzt");
    }

    // ------------------------------------------------------------------------
    //  Setzt den Animator zur Laufzeit. Wird gebraucht, wenn der Avatar erst
    //  während des Spiels erzeugt wird (Avatar-Auswahl im Einstellungsmenü).
    // ------------------------------------------------------------------------
    public void SetAnimator(Animator animator)
    {
        npcAnimator = animator;
        Debug.Log("[AIConvo] Animator zur Laufzeit zugewiesen.");
        SetupAvatarComponents();
    }

    public void ResetSession()
    {
        claude?.ResetHistory();
        SetTalkingAnimation(false);
        SetState(AIState.Bereit);
        Debug.Log("[AIConvo] Session zurückgesetzt");
    }

    // ========================================================================
    //  EINGABE-HANDLING
    // ========================================================================
    private void OnEnable()
    {
        if (toggleAction != null && toggleAction.action != null)
        {
            toggleAction.action.Enable();
            toggleAction.action.performed += OnToggle;
        }
    }

    private void OnDisable()
    {
        if (toggleAction != null && toggleAction.action != null)
        {
            toggleAction.action.performed -= OnToggle;
            toggleAction.action.Disable();
        }
    }

    public void Update()
    {        
        if (Keyboard.current != null && Keyboard.current.spaceKey.wasPressedThisFrame)
            ToggleRecording();
        

        // Wenn die KI fertig gesprochen hat: Animation beenden und zurück
        // auf "Bereit". Genau hier gehört das Ausschalten der Sprech-Animation
        // hin - direkt nach dem Play()-Aufruf wäre sie sofort wieder aus,
        // weil Play() nicht wartet, bis der Ton zu Ende ist.
        if (currentState == AIState.Spricht && !audioSource.isPlaying)
        {
            SetTalkingAnimation(false);
            SetState(AIState.Bereit);
        }

        // Eine Fehlermeldung nach der eingestellten Dauer wieder ausblenden.
        if (currentState == AIState.Fehler)
        {
            fehlerTimer += Time.deltaTime;
            if (fehlerTimer > fehlerAnzeigeDauer)
            {
                fehlerTimer = 0f;
                SetState(AIState.Bereit);
            }
        }
        else
        {
            fehlerTimer = 0f;
        }
    }

    public void OnToggle(InputAction.CallbackContext _) => ToggleRecording();

    public void ToggleRecording()
    {
        if (isProcessing) return;

        if (recorder.IsRecording)
            SetState(AIState.Verarbeitet);
        else
            SetState(AIState.NimmtAuf);

        recorder.ToggleRecording();
    }

    // ========================================================================
    //  HAUPT-PIPELINE
    // ========================================================================
    private async void HandleAudioReady(byte[] wavBytes)
    {
        isProcessing = true;
        SetState(AIState.Verarbeitet);

        var totalStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // Diese Werte werden am Ende protokolliert. Sie stehen außerhalb des
        // try-Blocks, damit sie auch im Fehlerfall verfügbar sind.
        long sttMs = 0, claudeMs = 0, ttsMs = 0;

        // Token-Zähler dieser Runde. Starten bei 0 und werden nur dann
        // gefüllt, wenn Claude tatsächlich aufgerufen wurde. Damit kann eine
        // abgebrochene Runde keine fremden Werte mehr ins Protokoll tragen.
        int inputTokens = 0, outputTokens = 0;

        string transcript = "";
        string reply = "";
        string status = "OK";

        try
        {
            // --- Schritt 1: Sprache -> Text ---
            var sttStopwatch = System.Diagnostics.Stopwatch.StartNew();
            transcript = await azureSpeech.TranscribeAsync(wavBytes, "de-DE");
            sttStopwatch.Stop();
            sttMs = sttStopwatch.ElapsedMilliseconds;

            if (string.IsNullOrWhiteSpace(transcript))
            {
                status = "STT_leer";
                SetState(AIState.Fehler, "Nichts verstanden");
                return;   // Tokens bleiben 0 - Claude wurde nie aufgerufen
            }
            Debug.Log($"[AIConvo] STT-Latenz: {sttMs} ms");

            // --- Schritt 2: Text -> KI-Antwort ---
            var claudeStopwatch = System.Diagnostics.Stopwatch.StartNew();
            reply = await claude.SendMessageAsync(transcript);
            claudeStopwatch.Stop();
            claudeMs = claudeStopwatch.ElapsedMilliseconds;

            // Token-Zahlen SOFORT sichern - noch bevor wir bei einer leeren
            // Antwort aussteigen. Eine leere Antwort kann trotzdem Tokens
            // verbraucht und damit Kosten verursacht haben; die gehören in
            // die Auswertung.
            inputTokens  = claude != null ? claude.LastInputTokens  : 0;
            outputTokens = claude != null ? claude.LastOutputTokens : 0;

            if (string.IsNullOrWhiteSpace(reply))
            {
                status = "Claude_Fehler";
                SetState(AIState.Fehler, "Keine Antwort erhalten");
                return;
            }
            Debug.Log($"[AIConvo] Claude-Latenz: {claudeMs} ms " +
                      $"({inputTokens} Input- / {outputTokens} Output-Tokens)");

            // --- Schritt 2b: Regieanweisungen entfernen ---
            // Sonst liest die Sprachausgabe "*seufzt*" wörtlich vor.
            string cleanReply = StripStageDirections(reply);
            if (string.IsNullOrWhiteSpace(cleanReply))
            {
                status = "Nur_Regieanweisung";
                Debug.LogWarning("[AIConvo] Antwort bestand nur aus Regieanweisungen.");
                SetState(AIState.Fehler, "Leere Antwort");
                return;
            }
            if (cleanReply != reply)
                Debug.Log($"[AIConvo] Für die Sprachausgabe bereinigt: \"{cleanReply}\"");

            // --- Schritt 3: Text -> Sprache ---
            var ttsStopwatch = System.Diagnostics.Stopwatch.StartNew();
            AudioClip clip = await azureSpeech.SynthesizeAsync(cleanReply);
            ttsStopwatch.Stop();
            ttsMs = ttsStopwatch.ElapsedMilliseconds;

            if (clip == null)
            {
                status = "TTS_Fehler";
                SetState(AIState.Fehler, "Sprachausgabe fehlgeschlagen");
                return;
            }
            Debug.Log($"[AIConvo] TTS-Latenz: {ttsMs} ms");

            // --- Schritt 4: Abspielen + Sprech-Animation starten ---
            audioSource.clip = clip;
            SetTalkingAnimation(true);   // Avatar wechselt in die Sprech-Animation
            audioSource.Play();

            Debug.Log($"[AIConvo] GESAMT-Latenz: {totalStopwatch.ElapsedMilliseconds} ms");

            // Der Wechsel zurück auf "Bereit" (und das Beenden der Animation)
            // passiert in Update(), sobald die AudioSource fertig ist.
            SetState(AIState.Spricht);
        }
        catch (Exception ex)
        {
            status = "Exception";
            Debug.LogException(ex);
            SetState(AIState.Fehler, "Fehler in der Pipeline");
        }
        finally
        {
            totalStopwatch.Stop();
            isProcessing = false;

            // Immer protokollieren - auch bei Fehlern. Sonst weiß man
            // hinterher nicht, wie viele Versuche tatsächlich stattfanden.
            SchreibeProtokoll(sttMs, claudeMs, ttsMs,
                              totalStopwatch.ElapsedMilliseconds,
                              status, transcript, reply,
                              inputTokens, outputTokens);
        }
    }

    // ------------------------------------------------------------------------
    //  Schaltet die Sprech-Animation des Avatars an oder aus.
    //  Prüft vorher, ob überhaupt ein Animator da ist - sonst gäbe es einen
    //  Fehler, wenn kein Avatar mit Animator in der Szene steht.
    // ------------------------------------------------------------------------
    private void SetTalkingAnimation(bool redet)
    {
        if (npcAnimator == null) return;
        if (string.IsNullOrEmpty(talkingParameterName)) return;

        npcAnimator.SetBool(talkingParameterName, redet);
    }

    private void SetupAvatarComponents()
    {
        if (npcAnimator == null) return;

        gaze = npcAnimator.GetComponent<NpcGaze>();
        if (gaze == null) gaze = npcAnimator.gameObject.AddComponent<NpcGaze>();

        lipSync = npcAnimator.GetComponentInChildren<NpcLipSync>();
        if (lipSync == null) lipSync = npcAnimator.gameObject.AddComponent<NpcLipSync>();
        lipSync.Init(audioSource);

        blink = npcAnimator.GetComponentInChildren<NpcBlink>();
        if (blink == null) blink = npcAnimator.gameObject.AddComponent<NpcBlink>();
    }

    // ------------------------------------------------------------------------
    //  Übergibt die Daten einer Runde an den StudyLogger und rechnet dabei
    //  die Kosten aus.
    //
    //  Die Token-Zahlen kommen als Parameter herein und werden NICHT mehr
    //  hier aus dem ClaudeClient gelesen. Sonst würden bei einer vorzeitig
    //  abgebrochenen Runde die Werte der vorherigen Runde ins Protokoll
    //  wandern - samt der bereits abgerechneten Kosten, die dann doppelt
    //  in der Auswertung stünden.
    // ------------------------------------------------------------------------
    private void SchreibeProtokoll(long sttMs, long claudeMs, long ttsMs,
                                   long gesamtMs, string status,
                                   string transcript, string reply,
                                   int inputTokens, int outputTokens)
    {
        if (studyLogger == null) return;

        // Kosten dieser Runde in US-Dollar.
        // Die Preise sind pro 1 Million Tokens angegeben, deshalb geteilt
        // durch 1.000.000.
        double kosten = (inputTokens  * preisInputProMillion  / 1_000_000.0)
                      + (outputTokens * preisOutputProMillion / 1_000_000.0);

        studyLogger.LogRunde(
            szenario: currentSzenarioName,
            stimme: currentVoiceName,
            modell: claude != null ? claude.ModelName : config.claudeModel,
            sttMs: sttMs,
            claudeMs: claudeMs,
            ttsMs: ttsMs,
            gesamtMs: gesamtMs,
            inputTokens: inputTokens,
            outputTokens: outputTokens,
            kostenUsd: kosten,
            status: status,
            gesagt: transcript,
            antwort: reply
        );
    }

    // ------------------------------------------------------------------------
    //  Entfernt Regieanweisungen aus dem Antworttext:
    //    *seufzt*, (nachdenklich), [Pause], einzelne Sternchen
    //  Damit die Sprachausgabe sie nicht wörtlich vorliest.
    // ------------------------------------------------------------------------
    private static string StripStageDirections(string input)
    {
        if (string.IsNullOrEmpty(input)) return input;

        string result = Regex.Replace(input, @"\*[^*]*\*", " ");  // *...*
        result = Regex.Replace(result, @"\([^)]*\)", " ");          // (...)
        result = Regex.Replace(result, @"\[[^\]]*\]", " ");         // [...]
        result = result.Replace("*", " ");                           // übrige Sternchen
        result = Regex.Replace(result, @"\s{2,}", " ").Trim();       // doppelte Leerzeichen
        result = result.TrimStart(',', '.', ';', ':', ' ');          // hängende Satzzeichen
        return result;
    }
}