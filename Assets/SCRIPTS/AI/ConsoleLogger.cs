using System;
using System.IO;
using System.Text;
using UnityEngine;

// ============================================================================
//  ConsoleLogger
// ----------------------------------------------------------------------------
//  Schreibt alle Konsolenmeldungen zusätzlich in eine Textdatei.
//
//  Wozu das gut ist:
//  Auf der Meta Quest gibt es keine Unity-Konsole. Wenn dort etwas schiefgeht,
//  sieht man nichts. Diese Klasse fängt jede Meldung ab und legt sie in eine
//  Datei neben dem Studienprotokoll - so lässt sich hinterher nachvollziehen,
//  was passiert ist.
//
//  Wo landet die Datei?
//    Windows-Editor: C:\Users\<Name>\AppData\LocalLow\<Firma>\<Projekt>\
//    Meta Quest:     /sdcard/Android/data/<package-name>/files/
//
//  Einbau:
//    Ein leeres GameObject in der Szene anlegen, dieses Skript dranhängen.
//    Mit aktiviertem "Nicht Zerstoeren" reicht es, das Objekt einmal ins
//    Hauptmenü zu legen - es überlebt dann den Szenenwechsel.
// ============================================================================
public class ConsoleLogger : MonoBehaviour
{
    [Header("Aufzeichnung")]
    [Tooltip("Wenn aus, wird nichts geschrieben.")]
    [SerializeField] private bool loggingAktiv = true;

    [Tooltip("Auch normale Meldungen (Debug.Log) mitschreiben. " +
             "Wenn aus, landen nur Warnungen und Fehler in der Datei.")]
    [SerializeField] private bool auchNormaleMeldungen = true;

    [Tooltip("Bei Fehlern zusätzlich den Aufrufverlauf (Stack Trace) " +
             "mitschreiben. Hilft beim Suchen, macht die Datei aber größer.")]
    [SerializeField] private bool stackTraceBeiFehlern = true;

    [Header("Verhalten")]
    [Tooltip("Dieses Objekt beim Szenenwechsel behalten. Dann läuft die " +
             "Aufzeichnung über Hauptmenü und Beratungsraum hinweg weiter.")]
    [SerializeField] private bool nichtZerstoeren = true;

    [Tooltip("Beim Start den Dateipfad in die Konsole schreiben.")]
    [SerializeField] private bool pfadAusgeben = true;

    private string dateiPfad;
    private int meldungsZaehler = 0;

    // Damit nicht zweimal derselbe Logger läuft, wenn die Szene neu geladen wird.
    private static ConsoleLogger instanz;

    // ------------------------------------------------------------------------
    //  Awake(): Datei anlegen und beim Log-Ereignis von Unity anmelden.
    // ------------------------------------------------------------------------
    private void Awake()
    {
        // Wenn es schon einen Logger gibt, diesen hier entfernen.
        if (nichtZerstoeren)
        {
            if (instanz != null && instanz != this)
            {
                Destroy(gameObject);
                return;
            }
            instanz = this;
            DontDestroyOnLoad(gameObject);
        }

        if (!loggingAktiv) return;

        string zeitstempel = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        dateiPfad = Path.Combine(Application.persistentDataPath,
                                 $"Konsole_{zeitstempel}.txt");

        // Kopfzeile mit ein paar Eckdaten - hilft beim Zuordnen, falls
        // mehrere Geräte oder Versionen im Spiel sind.
        var kopf = new StringBuilder();
        kopf.AppendLine("=====================================================");
        kopf.AppendLine("Konsolenprotokoll");
        kopf.AppendLine($"Gestartet:   {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        kopf.AppendLine($"Gerät:       {SystemInfo.deviceModel}");
        kopf.AppendLine($"Plattform:   {Application.platform}");
        kopf.AppendLine($"Unity:       {Application.unityVersion}");
        kopf.AppendLine($"App-Version: {Application.version}");
        kopf.AppendLine("=====================================================");
        kopf.AppendLine();

        SchreibeRoh(kopf.ToString());

        // Bei Unity anmelden: ab jetzt bekommen wir jede Konsolenmeldung.
        Application.logMessageReceived += EmpfangeMeldung;

        if (pfadAusgeben)
            Debug.Log($"[ConsoleLogger] Protokoll läuft. Datei:\n{dateiPfad}");
    }

    // ------------------------------------------------------------------------
    //  Beim Zerstören abmelden, damit keine Meldungen ins Leere laufen.
    // ------------------------------------------------------------------------
    private void OnDestroy()
    {
        if (!loggingAktiv) return;
        Application.logMessageReceived -= EmpfangeMeldung;
    }

    // ------------------------------------------------------------------------
    //  Wird beim Beenden der Anwendung aufgerufen - schreibt eine Abschlusszeile.
    //  Auf der Quest greift das nicht immer; deshalb wird ohnehin jede Zeile
    //  sofort geschrieben (siehe SchreibeRoh).
    // ------------------------------------------------------------------------
    private void OnApplicationQuit()
    {
        if (!loggingAktiv) return;
        SchreibeRoh($"\n--- Beendet um {DateTime.Now:HH:mm:ss} " +
                    $"nach {meldungsZaehler} Meldungen ---\n");
    }

    // ------------------------------------------------------------------------
    //  Wird von Unity für JEDE Konsolenmeldung aufgerufen.
    //
    //  Parameter:
    //    meldung    -> der Text
    //    stackTrace -> woher die Meldung kam
    //    typ        -> Log, Warning, Error, Assert oder Exception
    // ------------------------------------------------------------------------
    private void EmpfangeMeldung(string meldung, string stackTrace, LogType typ)
    {
        if (!loggingAktiv) return;

        // Normale Meldungen überspringen, wenn nur Fehler gewünscht sind.
        if (!auchNormaleMeldungen && typ == LogType.Log) return;

        meldungsZaehler++;

        // Kürzel für den Meldungstyp, damit die Datei übersichtlich bleibt.
        string kuerzel;
        switch (typ)
        {
            case LogType.Error:     kuerzel = "FEHLER  "; break;
            case LogType.Exception: kuerzel = "AUSNAHME"; break;
            case LogType.Warning:   kuerzel = "WARNUNG "; break;
            case LogType.Assert:    kuerzel = "ASSERT  "; break;
            default:                kuerzel = "INFO    "; break;
        }

        var zeile = new StringBuilder();
        zeile.AppendLine($"[{DateTime.Now:HH:mm:ss}] {kuerzel} | {meldung}");

        // Bei echten Fehlern zusätzlich den Aufrufverlauf anhängen.
        bool istFehler = typ == LogType.Error
                      || typ == LogType.Exception
                      || typ == LogType.Assert;

        if (istFehler && stackTraceBeiFehlern && !string.IsNullOrEmpty(stackTrace))
        {
            // Einrücken, damit man den Stack Trace vom eigentlichen Text
            // unterscheiden kann.
            foreach (string z in stackTrace.Split('\n'))
            {
                if (!string.IsNullOrWhiteSpace(z))
                    zeile.AppendLine($"                     {z.TrimEnd()}");
            }
        }

        SchreibeRoh(zeile.ToString());
    }

    // ------------------------------------------------------------------------
    //  Hängt Text an die Datei an und schließt sie sofort wieder.
    //
    //  Warum nicht gepuffert?
    //  Auf der Quest wird OnApplicationQuit nicht zuverlässig aufgerufen, wenn
    //  jemand die App über den Home-Button verlässt. Gepufferte Zeilen wären
    //  dann verloren - ausgerechnet die letzten, die bei einem Absturz am
    //  interessantesten sind.
    // ------------------------------------------------------------------------
    private void SchreibeRoh(string text)
    {
        if (string.IsNullOrEmpty(dateiPfad)) return;

        try
        {
            File.AppendAllText(dateiPfad, text, new UTF8Encoding(true));
        }
        catch
        {
            // Bewusst ohne Debug.LogError: Das würde erneut EmpfangeMeldung
            // auslösen und eine Endlosschleife erzeugen.
        }
    }

    // Für andere Skripte lesbar, z.B. um den Pfad im UI anzuzeigen.
    public string DateiPfad => dateiPfad;
    public int AnzahlMeldungen => meldungsZaehler;
}