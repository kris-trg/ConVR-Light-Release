using UnityEngine;
using System.Collections.Generic;

[CreateAssetMenu(fileName = "ApiConfig", menuName = "AI/API Config")]
public class ApiConfig : ScriptableObject
{
    [Header("API Keys - NICHT in Git committen!")]
    public string anthropicApiKey;
    public string azureSpeechKey;

    [Header("Azure Speech Settings")]
    [Tooltip("z.B. germanywestcentral oder westeurope")]
    public string azureRegion = "germanywestcentral";

    [Tooltip("Default-Stimme. Im UI ueberschreibbar.")]
    public string voiceName = "de-DE-FlorianMultilingualNeural";

    [Header("Verfuegbare deutsche Stimmen (fuer UI-Dropdown)")]
    public List<VoiceOption> availableVoices = new List<VoiceOption>
    {
        new VoiceOption("Florian (m)",   "de-DE-FlorianMultilingualNeural"),
        new VoiceOption("Seraphina (w)", "de-DE-SeraphinaMultilingualNeural"),
        new VoiceOption("Katja (w)",     "de-DE-KatjaNeural"),
        new VoiceOption("Conrad (m)",    "de-DE-ConradNeural"),
    };

    // ------------------------------------------------------------------------
    //  Auswaehlbare Figuren fuer das Beratungszimmer.
    //
    //  Anders als bei den Stimmen gibt es hier keine Vorbelegung im Code:
    //  Prefabs sind Assets und lassen sich nicht im Skript referenzieren.
    //  Die vier Eintraege muessen deshalb im Inspector am ApiConfig-Asset
    //  von Hand eingetragen werden.
    //
    //  Die Reihenfolge dieser Liste bestimmt die Reihenfolge im Dropdown des
    //  Einstellungsmenues. Gespeichert wird die Auswahl als Index in diese
    //  Liste - wird spaeter ein Eintrag entfernt, faengt der AvatarSpawner
    //  das ab und faellt auf die erste Figur zurueck.
    // ------------------------------------------------------------------------
    [Header("Verfuegbare Avatare (fuer UI-Dropdown)")]
    [Tooltip("Im Inspector befuellen: Anzeigename + zugehoeriges Prefab.")]
    public AvatarOption[] availableAvatars;

    [Header("Claude Settings")]
    public string claudeModel = "claude-sonnet-5";
    public int maxTokens = 50000;

    [Header("System-Prompt (Default - im UI ueberschreibbar)")]
    [TextArea(8, 20)]
    public string systemPrompt =
        "";
}

[System.Serializable]
public class VoiceOption
{
    public string displayName;  // Was im Dropdown angezeigt wird
    public string azureVoiceId; // Was an Azure geschickt wird

    public VoiceOption(string display, string azureId)
    {
        displayName = display;
        azureVoiceId = azureId;
    }
}

// ============================================================================
//  AvatarOption
//  Eine auswaehlbare Figur: Anzeigename fuers Menue + zugehoeriges Prefab.
//
//  Kein Konstruktor wie bei VoiceOption - die Eintraege werden ausschliesslich
//  im Inspector gepflegt, weil ein Prefab-Verweis nur dort gesetzt werden kann.
// ============================================================================
[System.Serializable]
public class AvatarOption
{
    [Tooltip("Name, der im Einstellungsmenue erscheint, z.B. 'Mona'.")]
    public string anzeigeName;

    [Tooltip("Das Avatar-Prefab, das im Beratungszimmer erzeugt wird " +
             "(nur Modell und Animator, keine KI-Komponenten).")]
    public GameObject prefab;
}