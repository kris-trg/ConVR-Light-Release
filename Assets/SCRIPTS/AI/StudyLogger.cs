using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

// ============================================================================
//  StudyLogger
// ----------------------------------------------------------------------------
//  Schreibt für jede Gesprächsrunde eine Zeile in eine CSV-Datei. Damit lassen
//  sich hinterher Latenzen, Token-Verbrauch und Kosten auswerten - und bei
//  Bedarf auch die Gesprächsverläufe selbst.
//
//  Wo landet die Datei?
//    Unity legt sie unter Application.persistentDataPath ab. Das ist:
//      Windows-Editor: C:\Users\<Name>\AppData\LocalLow\<Firma>\<Projekt>\
//      Meta Quest:     /sdcard/Android/data/<package-name>/files/
//
//  Wie holt man sie von der Quest?
//    1. Quest per USB anstecken, im Headset "Dateien zulassen" bestätigen
//    2. Im Windows-Explorer: Quest > Interner Speicher > Android > data >
//       <package-name> > files
//    3. CSV auf den PC KOPIEREN - Excel kann Dateien nicht direkt vom Gerät
//       öffnen, dort erscheint sonst die Meldung "Software nicht gefunden".
//
//  AUFBAU DER DATEI:
//    Zeile 1 - 27  Legende (feste Länge, erklärt jede Spalte)
//    Zeile 28      leer
//    Zeile 29      Kopfzeile mit den Spaltennamen
//    Zeile 30 ff.  die Gesprächsrunden
//
//    Die feste Länge ist Absicht: So stehen die Daten in jeder Datei an
//    derselben Stelle und Excel-Formeln lassen sich zwischen Sitzungen
//    kopieren. Auch wenn "Log Transcripts" ausgeschaltet ist, bleibt die
//    Legende gleich lang - dann sind die letzten beiden Spalten eben nicht
//    vorhanden.
//
//    Beispiele für die Auswertung (Bereiche ab Zeile 30):
//      Kosten gesamt:    =SUMME($N$30:$N$1000)
//      Nur gültige:      =SUMMEWENN($O$30:$O$1000;"OK";$N$30:$N$1000)
//      Gültige Runden:   =ZÄHLENWENN($O$30:$O$1000;"OK")
//      Ø Wartezeit:      =MITTELWERTWENN($O$30:$O$1000;"OK";$K$30:$K$1000)
//
//  Warum eine Datei pro Sitzung?
//    So geht nichts verloren, wenn die App abstürzt, und die Zuordnung zu
//    einer Versuchsperson bleibt eindeutig.
//
//  Warum wird nach jeder Zeile geschlossen?
//    Auf der Quest wird OnApplicationQuit nicht zuverlässig aufgerufen, wenn
//    jemand die App über den Home-Button verlässt. Bei gepufferten Schreib-
//    vorgängen wären die letzten Zeilen dann weg.
//
//  DATENSCHUTZ:
//    Über das Häkchen "Log Transcripts" lässt sich steuern, ob die
//    gesprochenen Inhalte mitgeschrieben werden. Für die reine Kosten- und
//    Latenzauswertung wird das nicht gebraucht. Für eine inhaltliche Analyse
//    schon - dann muss es aber in der Einverständniserklärung stehen.
// ============================================================================
public class StudyLogger : MonoBehaviour
{
    [Header("Aufzeichnung")]
    [Tooltip("Wenn aus, wird gar nichts geschrieben.")]
    [SerializeField] private bool loggingAktiv = true;

    [Tooltip("Sollen das Gesagte und die KI-Antwort mitgeschrieben werden? " +
             "Für Kosten- und Latenzauswertung nicht nötig. Für inhaltliche " +
             "Analysen ja - dann muss es in der Einverständniserklärung stehen.")]
    [SerializeField] private bool logTranscripts = true;

    [Tooltip("Die Legende oben in die Datei schreiben. Wenn aus, beginnt die " +
             "Kopfzeile in Zeile 1 - praktisch für automatisierte Auswertung " +
             "z.B. mit Python.")]
    [SerializeField] private bool legendeSchreiben = true;

    [Tooltip("Kosten mit Komma als Dezimaltrennzeichen schreiben. Deutsches " +
             "Excel erkennt sie dann als Zahl. Für Auswertung mit Python oder " +
             "englischem Excel ausschalten - dann wird der Punkt verwendet.")]
    [SerializeField] private bool deutschesZahlenformat = true;

    [Header("Kennzeichnung")]
    [Tooltip("Optionale Kennung der Versuchsperson, z.B. P001. Darf leer " +
             "bleiben - dann wird nur der Zeitstempel verwendet.")]
    [SerializeField] private string probandenId = "";

    [Header("Debug")]
    [Tooltip("Beim Start den vollständigen Dateipfad in die Konsole schreiben.")]
    [SerializeField] private bool pfadInKonsoleAusgeben = true;

    // --- Interne Felder ---
    private string dateiPfad;
    private string sitzungsId;
    private int rundenZaehler = 0;

    // Trennzeichen für die CSV. Semikolon, weil Excel im deutschen
    // Sprachraum damit besser umgehen kann als mit Komma - und weil das
    // Komma bei deutschem Zahlenformat als Dezimaltrenner gebraucht wird.
    private const char TRENNER = ';';

    // ------------------------------------------------------------------------
    //  Awake(): legt die Datei an, schreibt Legende und Kopfzeile.
    // ------------------------------------------------------------------------
    private void Awake()
    {
        if (!loggingAktiv) return;

        sitzungsId = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        string dateiName = string.IsNullOrWhiteSpace(probandenId)
            ? $"Sitzung_{sitzungsId}.csv"
            : $"Sitzung_{probandenId}_{sitzungsId}.csv";

        dateiPfad = Path.Combine(Application.persistentDataPath, dateiName);

        if (legendeSchreiben)
            SchreibeLegende();

        SchreibeKopfzeile();

        if (pfadInKonsoleAusgeben)
            Debug.Log($"[StudyLogger] Aufzeichnung läuft. Datei:\n{dateiPfad}");
    }

    // ------------------------------------------------------------------------
    //  SchreibeLegende
    //  Erklärt jede Spalte und jeden Status-Wert, damit die Datei auch für
    //  Kolleginnen und Kollegen ohne Kenntnis des Codes lesbar ist.
    //
    //  WICHTIG: Der Block hat eine feste Länge von 27 Zeilen. Wird hier etwas
    //  ergänzt, verschiebt sich die Kopfzeile - dann müssen die Zeilenangaben
    //  im Kommentar oben und in den Excel-Formeln angepasst werden.
    // ------------------------------------------------------------------------
    private void SchreibeLegende()
    {
        var z = new List<string>();

        //  1
        z.Add(Zeile("LEGENDE", "Beratungszimmer VR – Studienprotokoll. "
                  + "Die Daten beginnen in Zeile 30."));
        //  2
        z.Add(Zeile("", ""));
        //  3
        z.Add(Zeile("Spalte", "Bedeutung"));

        //  4 - 20  (17 Spalten)
        z.Add(Zeile("Zeitstempel",
              "Uhrzeit, zu der die Runde abgeschlossen wurde. Das Datum steht "
            + "in der SitzungsID."));

        z.Add(Zeile("SitzungsID",
              "Startzeitpunkt der Sitzung (JJJJMMTT_HHMMSS). Alle Zeilen einer "
            + "Datei teilen sich diese Kennung."));

        z.Add(Zeile("ProbandenID",
              "Kennung der Versuchsperson. Ein Bindestrich bedeutet: nicht gesetzt."));

        z.Add(Zeile("Runde",
              "Fortlaufende Nummer der Gesprächsrunde innerhalb der Sitzung, "
            + "beginnend bei 1."));

        z.Add(Zeile("Szenario",
              "Name des Beratungsfalls, der im Hauptmenü gewählt wurde."));

        z.Add(Zeile("Stimme",
              "Azure-Stimme, mit der die ratsuchende Person spricht."));

        z.Add(Zeile("Modell",
              "Das verwendete Claude-Modell. Wichtig beim Vergleich verschiedener "
            + "Modelle."));

        z.Add(Zeile("STT_ms",
              "SPEECH TO TEXT: Dauer in Millisekunden, die Azure braucht, um die "
            + "Mikrofonaufnahme des Beraters in deutschen Text umzuwandeln. Gemessen "
            + "vom Absenden der Audiodaten bis zum Eintreffen des Textes. Das ist "
            + "RECHENZEIT, nicht die Dauer des Gesprochenen."));

        z.Add(Zeile("Claude_ms",
              "ANTWORTGENERIERUNG: Dauer in Millisekunden, die Claude braucht, um "
            + "aus dem erkannten Text, dem Szenario-Prompt und dem bisherigen "
            + "Gesprächsverlauf die Antwort der ratsuchenden Person zu erzeugen. "
            + "Einzige Stufe, in der Tokens und damit Kosten anfallen."));

        z.Add(Zeile("TTS_ms",
              "TEXT TO SPEECH: Dauer in Millisekunden, die Azure braucht, um die "
            + "Antwort in eine Audiodatei umzuwandeln. Hängt von der Länge der "
            + "Antwort ab. Das ist RECHENZEIT, nicht die Abspieldauer."));

        z.Add(Zeile("Gesamt_ms",
              "Gesamte Wartezeit der Versuchsperson vom Ende der Aufnahme bis zum "
            + "Beginn der Sprachausgabe. Etwas größer als die Summe der drei Stufen "
            + "– die Differenz ist die Verarbeitung in Unity (Audiodaten aufbereiten, "
            + "Regieanweisungen filtern, AudioClip erzeugen)."));

        z.Add(Zeile("InputTokens",
              "An Claude gesendete Tokens dieser Runde: System-Prompt, gesamter "
            + "bisheriger Gesprächsverlauf und die neue Äußerung. Steigt innerhalb "
            + "einer Sitzung an, weil der Verlauf jedes Mal mitgeschickt wird."));

        z.Add(Zeile("OutputTokens",
              "Von Claude erzeugte Tokens dieser Runde, also die Länge der Antwort."));

        z.Add(Zeile("Kosten_USD",
              "Kosten dieser einen Runde in US-Dollar, berechnet aus Input- und "
            + "Output-Tokens mit den im Inspector hinterlegten Preisen pro Million "
            + "Tokens."));

        z.Add(Zeile("Status",
              "Ergebnis der Runde. Mögliche Werte siehe unten."));

        z.Add(Zeile("Gesagt",
              "Der vom Berater gesprochene Text, wie ihn die Spracherkennung "
            + "verstanden hat. Nur vorhanden, wenn 'Log Transcripts' aktiv ist."));

        z.Add(Zeile("Antwort",
              "Die Antwort von Claude im Originaltext, also VOR dem Entfernen der "
            + "Regieanweisungen. Vorgelesen wurde die bereinigte Fassung."));

        // 21
        z.Add(Zeile("", ""));
        // 22
        z.Add(Zeile("Status-Wert", "Bedeutung"));

        // 23 - 27  (5 Status-Werte)
        z.Add(Zeile("OK",
              "Runde vollständig durchlaufen, Antwort wurde abgespielt."));

        z.Add(Zeile("STT_leer",
              "Die Spracherkennung hat nichts verstanden – meist zu leise oder gar "
            + "nicht gesprochen. Claude wurde nicht aufgerufen, es entstanden keine "
            + "Kosten. Diese Zeilen bei der Latenzauswertung ausschließen."));

        z.Add(Zeile("Claude_Fehler",
              "Keine Antwort von Claude erhalten. Tokens können trotzdem angefallen "
            + "sein – dann gehört die Zeile in die Kostensumme, aber nicht in die "
            + "Latenzauswertung, weil TTS nie lief."));

        z.Add(Zeile("Nur_Regieanweisung",
              "Claude hat geantwortet, der Text bestand aber nur aus Regieanweisungen "
            + "wie *seufzt*. Nach dem Filtern blieb nichts zum Vorlesen übrig."));

        z.Add(Zeile("TTS_Fehler",
              "Die Sprachausgabe hat keine Audiodatei geliefert. Antwort und Kosten "
            + "sind vorhanden, abgespielt wurde nichts."));

        // 28: Leerzeile als Trennung zur Kopfzeile
        z.Add(Zeile("", ""));

        foreach (string zeile in z)
            SchreibeZeile(zeile);
    }

    // Kleine Hilfe, damit die Legende oben lesbar bleibt.
    private static string Zeile(string spalte, string bedeutung)
    {
        return CsvSicher(spalte) + TRENNER + CsvSicher(bedeutung);
    }

    // ------------------------------------------------------------------------
    //  Schreibt die Zeile mit den Spaltenüberschriften (Zeile 29).
    // ------------------------------------------------------------------------
    private void SchreibeKopfzeile()
    {
        var spalten = new List<string>
        {
            "Zeitstempel",
            "SitzungsID",
            "ProbandenID",
            "Runde",
            "Szenario",
            "Stimme",
            "Modell",
            "STT_ms",
            "Claude_ms",
            "TTS_ms",
            "Gesamt_ms",
            "InputTokens",
            "OutputTokens",
            "Kosten_USD",
            "Status"
        };

        if (logTranscripts)
        {
            spalten.Add("Gesagt");
            spalten.Add("Antwort");
        }

        SchreibeZeile(string.Join(TRENNER.ToString(), spalten));
    }

    // ------------------------------------------------------------------------
    //  LogRunde
    //  Schreibt eine abgeschlossene Gesprächsrunde in die Datei.
    //
    //  Wird vom AIConversationManager nach jeder Runde aufgerufen - auch bei
    //  Fehlern, damit man hinterher sieht, wie viele Versuche stattfanden.
    // ------------------------------------------------------------------------
    public void LogRunde(
        string szenario,
        string stimme,
        string modell,
        long sttMs,
        long claudeMs,
        long ttsMs,
        long gesamtMs,
        int inputTokens,
        int outputTokens,
        double kostenUsd,
        string status,
        string gesagt,
        string antwort)
    {
        if (!loggingAktiv) return;

        rundenZaehler++;

        var werte = new List<string>
        {
            // Nur die Uhrzeit - das Datum steckt in der SitzungsID. Sonst
            // erkennt Excel den Wert als Datum und zeigt ###### an, solange
            // die Spalte nicht breit genug ist.
            DateTime.Now.ToString("HH:mm:ss"),
            sitzungsId,
            string.IsNullOrWhiteSpace(probandenId) ? "-" : probandenId,
            rundenZaehler.ToString(),
            CsvSicher(szenario),
            CsvSicher(stimme),
            CsvSicher(modell),
            sttMs.ToString(),
            claudeMs.ToString(),
            ttsMs.ToString(),
            gesamtMs.ToString(),
            inputTokens.ToString(),
            outputTokens.ToString(),
            FormatiereKosten(kostenUsd),
            CsvSicher(status)
        };

        if (logTranscripts)
        {
            werte.Add(CsvSicher(gesagt));
            werte.Add(CsvSicher(antwort));
        }

        SchreibeZeile(string.Join(TRENNER.ToString(), werte));
    }

    // ------------------------------------------------------------------------
    //  Formatiert den Kostenbetrag mit sechs Nachkommastellen.
    //
    //  Warum sechs? Die Beträge pro Runde liegen im Bereich von Tausendstel-
    //  Cent. Mit weniger Stellen würde alles zu 0,00 gerundet.
    //
    //  Warum umschaltbar? Deutsches Excel erkennt "0,002532" als Zahl und
    //  kann damit rechnen. Bei "0.002532" landet der Wert als Text in der
    //  Zelle - SUMME() liefert dann 0. Python und englisches Excel brauchen
    //  umgekehrt den Punkt.
    // ------------------------------------------------------------------------
    private string FormatiereKosten(double kostenUsd)
    {
        string text = kostenUsd.ToString("F6", CultureInfo.InvariantCulture);

        if (deutschesZahlenformat)
            text = text.Replace('.', ',');

        return text;
    }

    // ------------------------------------------------------------------------
    //  Hängt eine Zeile an die Datei an und schließt sie sofort wieder.
    //  Siehe Erklärung oben: auf der Quest ist gepuffertes Schreiben riskant.
    // ------------------------------------------------------------------------
    private void SchreibeZeile(string zeile)
    {
        try
        {
            // UTF8 mit BOM, damit Excel die Umlaute richtig darstellt.
            File.AppendAllText(dateiPfad, zeile + "\n", new UTF8Encoding(true));
        }
        catch (Exception e)
        {
            // Wenn das Schreiben scheitert, soll die Anwendung trotzdem
            // weiterlaufen - die Studie ist wichtiger als das Protokoll.
            Debug.LogError($"[StudyLogger] Konnte nicht schreiben: {e.Message}");
        }
    }

    // ------------------------------------------------------------------------
    //  Macht einen Text CSV-tauglich.
    //
    //  Problem: Wenn im Text ein Semikolon, ein Zeilenumbruch oder ein
    //  Anführungszeichen vorkommt, zerschießt das die Spaltenstruktur.
    //  Lösung: Text in Anführungszeichen setzen und enthaltene
    //  Anführungszeichen verdoppeln - so schreibt es der CSV-Standard vor.
    // ------------------------------------------------------------------------
    private static string CsvSicher(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        text = text.Replace("\r", " ").Replace("\n", " ");
        text = text.Replace("\"", "\"\"");
        return $"\"{text}\"";
    }

    // ------------------------------------------------------------------------
    //  Setzt die Probanden-Kennung zur Laufzeit. Kann später vom Hauptmenü
    //  aufgerufen werden, wenn dort ein Eingabefeld ergänzt wird.
    // ------------------------------------------------------------------------
    public void SetProbandenId(string id)
    {
        probandenId = id;
        Debug.Log($"[StudyLogger] Probanden-Kennung gesetzt: {id}");
    }

    // Für andere Skripte lesbar, z.B. um den Pfad im UI anzuzeigen.
    public string DateiPfad => dateiPfad;
    public int AnzahlRunden => rundenZaehler;
}