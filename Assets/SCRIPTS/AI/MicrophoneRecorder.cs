using UnityEngine;
using System;
using System.IO;

// ============================================================================
//  MicrophoneRecorder
// ----------------------------------------------------------------------------
//  Nimmt Sprache über das Mikrofon auf und liefert sie als WAV-Daten.
//
//  Bedienprinzip "Toggle": Ein Tastendruck startet die Aufnahme, der nächste
//  beendet sie. Das ist in VR angenehmer als Gedrückthalten.
//
//  Ablauf:
//    1. ToggleRecording() startet die Aufnahme
//    2. ToggleRecording() erneut -> Aufnahme stoppt
//    3. Die Audiodaten werden ins WAV-Format umgewandelt
//    4. Das Ereignis OnRecordingFinished meldet die fertigen Daten
//       (der AIConversationManager hört darauf und schickt sie an Azure)
//
//  Zur Abtastrate (Sample Rate):
//  Azure Speech-to-Text arbeitet am effizientesten mit 16000 Hz. Manche
//  Mikrofone am PC unterstützen aber nur 48000 Hz - dann muss man hier
//  umstellen, sonst wird nichts erkannt. Auf der Quest funktioniert 16000
//  in der Regel direkt, was auch besser ist: kleinere Datei = schnellerer
//  Upload = weniger Wartezeit im Gespräch.
//
//  Wichtig: Der eingestellte Wert wird auch an den AzureSpeechClient
//  weitergegeben (siehe Eigenschaft SampleRate weiter unten), damit die
//  Angabe im HTTP-Header zu den tatsächlich gesendeten Daten passt.
// ============================================================================
public class MicrophoneRecorder : MonoBehaviour
{
    [Header("Aufnahme-Einstellungen")]
    [Tooltip("Abtastrate in Hz. 16000 ist ideal für Azure. Falls das Mikrofon " +
             "das nicht unterstützt (Warnung in der Konsole und leeres Transkript), " +
             "auf 48000 umstellen.")]
    [SerializeField] private int sampleRate = 16000;

    [Tooltip("Maximale Länge einer Aufnahme in Sekunden. Danach stoppt sie automatisch.")]
    [SerializeField] private int maxRecordingSeconds = 40;

    [Header("Debug")]
    [Tooltip("Zusätzliche Meldungen in der Konsole ausgeben.")]
    [SerializeField] private bool verboseLogging = true;

    private AudioClip recordingClip;   // der laufende Aufnahme-Puffer
    private string deviceName;         // Name des verwendeten Mikrofons
    private bool isRecording;          // läuft gerade eine Aufnahme?

    // Von außen lesbar (z.B. für die Status-Anzeige).
    public bool IsRecording => isRecording;

    // ------------------------------------------------------------------------
    //  Die eingestellte Abtastrate nach außen lesbar machen.
    //  Der AIConversationManager gibt diesen Wert an den AzureSpeechClient
    //  weiter, damit dort der richtige Wert im Content-Type-Header steht.
    //  Ohne das würde man Azure 48-kHz-Daten schicken und dabei behaupten,
    //  es seien 16 kHz.
    // ------------------------------------------------------------------------
    public int SampleRate => sampleRate;

    // ------------------------------------------------------------------------
    //  Start(): Berechtigungen holen und Mikrofon suchen.
    // ------------------------------------------------------------------------
    private void Start()
    {
        // Auf Android (also auf der Quest) muss die Mikrofon-Berechtigung zur
        // Laufzeit angefragt werden. Beim ersten Start erscheint ein Dialog.
        #if PLATFORM_ANDROID
        if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                UnityEngine.Android.Permission.Microphone))
        {
            UnityEngine.Android.Permission.RequestUserPermission(
                UnityEngine.Android.Permission.Microphone);
        }
        #endif

        // Gibt es überhaupt ein Mikrofon?
        if (Microphone.devices.Length == 0)
        {
            Debug.LogError("[MicRecorder] Kein Mikrofon gefunden! " +
                           "Auf der Quest: Berechtigungen prüfen. " +
                           "Am PC: Windows-Datenschutzeinstellungen prüfen.");
            return;
        }

        // Das erste verfügbare Mikrofon verwenden.
        // Hinweis: Bei mehreren Mikrofonen am PC kann sich die Reihenfolge
        // ändern - dann nimmt Unity plötzlich ein anderes Gerät.
        deviceName = Microphone.devices[0];

        if (verboseLogging)
        {
            Debug.Log($"[MicRecorder] Verwendetes Mikrofon: {deviceName}");
            Debug.Log($"[MicRecorder] Eingestellte Abtastrate: {sampleRate} Hz");
        }
    }

    // ------------------------------------------------------------------------
    //  Umschalten: startet oder beendet die Aufnahme.
    // ------------------------------------------------------------------------
    public void ToggleRecording()
    {
        if (isRecording)
            StopRecording();
        else
            StartRecording();
    }

    private void StartRecording()
    {
        if (string.IsNullOrEmpty(deviceName))
        {
            Debug.LogError("[MicRecorder] Kein Mikrofon verfügbar - Aufnahme nicht möglich.");
            return;
        }

        if (verboseLogging)
            Debug.Log("[MicRecorder] Aufnahme gestartet");

        // Microphone.Start(gerät, wiederholen, längeInSekunden, abtastrate)
        // 'false' = nicht in Schleife aufnehmen, sondern nach maxRecordingSeconds stoppen.
        recordingClip = Microphone.Start(deviceName, false, maxRecordingSeconds, sampleRate);
        isRecording = true;
    }

    private void StopRecording()
    {
        if (!isRecording) return;

        // GetPosition liefert, wie viele Abtastwerte tatsächlich aufgenommen
        // wurden. Wichtig: erst auslesen, DANN das Mikrofon beenden.
        int recordedSamples = Microphone.GetPosition(deviceName);
        Microphone.End(deviceName);
        isRecording = false;

        // Leere Aufnahme (z.B. sofort wieder losgelassen) verwerfen.
        if (recordedSamples <= 0 || recordingClip == null)
        {
            Debug.LogWarning("[MicRecorder] Leere Aufnahme - wird verworfen.");
            return;
        }

        if (verboseLogging)
            Debug.Log($"[MicRecorder] gestoppt: {recordedSamples} samples");

        // Die rohen Audiodaten aus dem AudioClip holen.
        // Ein "sample" ist ein einzelner Messwert der Schallwelle.
        float[] samples = new float[recordedSamples * recordingClip.channels];
        recordingClip.GetData(samples, 0);

        // In das WAV-Format umwandeln und per Ereignis weitergeben.
        // Wichtig: Wir verwenden die tatsächliche Rate des Clips, nicht die
        // eingestellte - falls Unity intern etwas anderes gewählt hat.
        byte[] wavBytes = EncodeWav(samples, recordingClip.channels, recordingClip.frequency);
        OnRecordingFinished?.Invoke(wavBytes);
    }

    // ------------------------------------------------------------------------
    //  Ereignis: wird ausgelöst, sobald eine Aufnahme fertig verarbeitet ist.
    //  Der AIConversationManager meldet sich hier an und bekommt die WAV-Daten.
    // ------------------------------------------------------------------------
    public event Action<byte[]> OnRecordingFinished;

    // ------------------------------------------------------------------------
    //  EncodeWav
    //  Wandelt die Audiodaten in eine gültige WAV-Datei um.
    //
    //  Eine WAV-Datei besteht aus einem 44 Byte langen Kopf ("Header") mit
    //  technischen Angaben, gefolgt von den eigentlichen Audiodaten. Azure
    //  braucht diesen Kopf, um die Datei lesen zu können.
    //
    //  Die einzelnen Schreibbefehle unten folgen exakt dem WAV-Standard -
    //  Reihenfolge und Datentypen dürfen nicht verändert werden.
    // ------------------------------------------------------------------------
    private byte[] EncodeWav(float[] samples, int channels, int sr)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);

        // --- RIFF-Kopf ---
        w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        w.Write(36 + samples.Length * 2);                    // Dateigröße ab hier
        w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        // --- Format-Abschnitt ---
        w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        w.Write(16);                        // Länge dieses Abschnitts
        w.Write((short)1);                  // Format 1 = PCM (unkomprimiert)
        w.Write((short)channels);           // Anzahl Kanäle (1 = Mono)
        w.Write(sr);                        // Abtastrate
        w.Write(sr * channels * 2);         // Bytes pro Sekunde
        w.Write((short)(channels * 2));     // Bytes pro Abtastblock
        w.Write((short)16);                 // Bits pro Abtastwert

        // --- Daten-Abschnitt ---
        w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        w.Write(samples.Length * 2);

        // Unity liefert Werte zwischen -1.0 und +1.0 (Fließkomma).
        // WAV erwartet Ganzzahlen zwischen -32768 und +32767.
        // Clamp verhindert Übersteuerung bei zu lauten Eingaben.
        foreach (float s in samples)
            w.Write((short)(Mathf.Clamp(s, -1f, 1f) * short.MaxValue));

        return ms.ToArray();
    }
}