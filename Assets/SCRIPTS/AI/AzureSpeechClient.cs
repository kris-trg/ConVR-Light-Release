using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

// ============================================================================
//  AzureSpeechClient
// ----------------------------------------------------------------------------
//  Verbindung zum Azure-Speech-Dienst. Übernimmt beide Richtungen:
//
//    TranscribeAsync -> Sprache in Text  (STT, Speech-to-Text)
//    SynthesizeAsync -> Text in Sprache  (TTS, Text-to-Speech)
//
//  Warum beides in einer Klasse?
//  Azure bietet beide Dienste unter demselben Schlüssel und derselben Region
//  an. Das spart einen zweiten Zugang und hält die Datenverarbeitung in der
//  EU (wichtig für den Datenschutz in der Studie).
//
//  Warum die REST-Schnittstelle statt des Azure-SDK?
//  Das SDK bringt zusätzliche Abhängigkeiten mit und macht den Build für die
//  Quest größer und fehleranfälliger. Die direkten HTTP-Anfragen hier
//  funktionieren mit Unity-Bordmitteln.
//
//  Diese Klasse erbt NICHT von MonoBehaviour - sie wird vom
//  AIConversationManager mit "new AzureSpeechClient(...)" erzeugt.
// ============================================================================
public class AzureSpeechClient
{
    private readonly string apiKey;     // Azure-Schlüssel
    private readonly string region;     // z.B. germanywestcentral
    private readonly string voiceName;  // z.B. de-DE-KatjaNeural

    // ------------------------------------------------------------------------
    //  Abtastrate der gesendeten Aufnahme.
    //  Muss zu dem passen, was der MicrophoneRecorder tatsächlich aufnimmt.
    //  Azure liest den echten Wert zwar aus dem WAV-Kopf, erwartet aber im
    //  Content-Type-Header dieselbe Angabe. Der AIConversationManager setzt
    //  diesen Wert automatisch, siehe dort InitializeClients().
    // ------------------------------------------------------------------------
    public int SampleRate { get; set; } = 16000;

    // Konstruktor: wird beim Erzeugen aufgerufen und merkt sich die Zugangsdaten.
    public AzureSpeechClient(string apiKey, string region, string voiceName)
    {
        this.apiKey = apiKey;
        this.region = region;
        this.voiceName = voiceName;
    }

    // ========================================================================
    //  SPRACHE -> TEXT (Speech-to-Text)
    // ========================================================================
    //  Schickt die WAV-Aufnahme an Azure und bekommt den erkannten Text zurück.
    //
    //  Parameter:
    //    wavBytes -> die Audiodaten vom MicrophoneRecorder
    //    language -> Sprachkennung, "de-DE" für Deutsch
    //
    //  Rückgabe: erkannter Text, oder null bei Fehler / nichts erkannt.
    // ------------------------------------------------------------------------
    public async Task<string> TranscribeAsync(byte[] wavBytes, string language = "de-DE")
    {
        // Die Adresse setzt sich aus der Region und dem gewünschten Dienst zusammen.
        string url = $"https://{region}.stt.speech.microsoft.com/speech/recognition/" +
                     $"conversation/cognitiveservices/v1?language={language}&format=detailed";

        using var req = new UnityWebRequest(url, "POST");

        // Die Audiodaten als Inhalt der Anfrage anhängen.
        req.uploadHandler = new UploadHandlerRaw(wavBytes);
        req.downloadHandler = new DownloadHandlerBuffer();

        // Kopfzeilen: Schlüssel zur Anmeldung, Format der gesendeten Daten.
        // Die Abtastrate kommt aus der Eigenschaft oben, damit sie immer zu
        // den tatsächlich aufgenommenen Daten passt.
        req.SetRequestHeader("Ocp-Apim-Subscription-Key", apiKey);
        req.SetRequestHeader("Content-Type",
            $"audio/wav; codecs=audio/pcm; samplerate={SampleRate}");
        req.SetRequestHeader("Accept", "application/json");
        req.timeout = 30;

        // Abschicken und warten, ohne das Bild einfrieren zu lassen.
        var op = req.SendWebRequest();
        while (!op.isDone) await Task.Yield();

        if (req.result != UnityWebRequest.Result.Success)
        {
            // Häufige Ursachen: falscher Schlüssel (401), falsche Region,
            // kein Internet, oder das Kontingent ist aufgebraucht (429).
            Debug.LogError($"[Azure STT] Fehler: {req.error} (HTTP {req.responseCode})\n" +
                           $"{req.downloadHandler.text}");
            return null;
        }

        // Antwort ist JSON - wir lesen nur die beiden Felder, die uns interessieren.
        var resp = JsonUtility.FromJson<SttResponse>(req.downloadHandler.text);

        if (resp == null || resp.RecognitionStatus != "Success")
        {
            // Kommt z.B. wenn nur Stille aufgenommen wurde, zu leise gesprochen
            // wurde, oder die Abtastrate nicht zu den Daten passt.
            Debug.LogWarning($"[Azure STT] Erkennung fehlgeschlagen " +
                             $"(Status: {resp?.RecognitionStatus ?? "unbekannt"}, " +
                             $"gesendete Rate: {SampleRate} Hz)");
            return null;
        }

        Debug.Log($"[Azure STT] Transkript: {resp.DisplayText}");
        return resp.DisplayText;
    }

    // Hilfsklasse zum Auslesen der JSON-Antwort von Azure.
    [System.Serializable]
    private class SttResponse
    {
        public string RecognitionStatus;  // "Success", "NoMatch", ...
        public string DisplayText;        // der erkannte Text
    }

    // ========================================================================
    //  TEXT -> SPRACHE (Text-to-Speech)
    // ========================================================================
    //  Schickt Text an Azure und bekommt gesprochene Sprache als AudioClip zurück.
    //
    //  Parameter:
    //    text -> was gesprochen werden soll (bereits von Regieanweisungen befreit)
    //
    //  Rückgabe: fertiger AudioClip zum Abspielen, oder null bei Fehler.
    // ------------------------------------------------------------------------
    public async Task<AudioClip> SynthesizeAsync(string text)
    {
        string url = $"https://{region}.tts.speech.microsoft.com/cognitiveservices/v1";

        // SSML ist ein XML-Format, mit dem man Azure sagt, WAS in WELCHER Stimme
        // gesprochen werden soll. Man könnte hier auch Sprechtempo oder Tonhöhe
        // steuern - für uns reichen Sprache und Stimme.
        //
        // SecurityElement.Escape wandelt Sonderzeichen wie & oder < in ihre
        // XML-Schreibweise um. Ohne das würde ein "&" im Text das XML zerstören.
        string ssml = $@"<speak version='1.0' xml:lang='de-DE'>
            <voice xml:lang='de-DE' name='{voiceName}'>
                {System.Security.SecurityElement.Escape(text)}
            </voice>
        </speak>";

        byte[] body = System.Text.Encoding.UTF8.GetBytes(ssml);

        using var req = new UnityWebRequest(url, "POST");
        req.uploadHandler = new UploadHandlerRaw(body);

        // DownloadHandlerAudioClip wandelt die Antwort direkt in einen AudioClip um.
        req.downloadHandler = new DownloadHandlerAudioClip(url, AudioType.MPEG);

        req.SetRequestHeader("Ocp-Apim-Subscription-Key", apiKey);
        req.SetRequestHeader("Content-Type", "application/ssml+xml");
        // Das Ausgabeformat: MP3, 24 kHz, Mono - guter Kompromiss aus Qualität
        // und Dateigröße (kleinere Datei = schnellerer Download = weniger Wartezeit).
        req.SetRequestHeader("X-Microsoft-OutputFormat", "audio-24khz-48kbitrate-mono-mp3");
        req.SetRequestHeader("User-Agent", "BeratungszimmerVR");
        req.timeout = 30;

        var op = req.SendWebRequest();
        while (!op.isDone) await Task.Yield();

        if (req.result != UnityWebRequest.Result.Success)
        {
            // Häufigste Ursache: der Stimmenname ist falsch geschrieben.
            Debug.LogError($"[Azure TTS] Fehler: {req.error} (HTTP {req.responseCode})\n" +
                           $"Verwendete Stimme: {voiceName}");
            return null;
        }

        AudioClip clip = DownloadHandlerAudioClip.GetContent(req);
        Debug.Log($"[Azure TTS] AudioClip erhalten: {clip.length:F2} Sekunden");
        return clip;
    }
}