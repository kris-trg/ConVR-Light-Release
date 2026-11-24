using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;
using Newtonsoft.Json;

// ============================================================================
//  PromptCloudLoader
// ----------------------------------------------------------------------------
//  Lädt die Beratungs-Szenarien (System-Prompts) aus einer JSON-Datei, die in
//  der Cloud (Sciebo) liegt.
//
//  Warum aus der Cloud?
//  So kann die Versuchsleitung die Szenarien im Browser anpassen, ohne dass die
//  Unity-App neu gebaut werden muss.
//
//  WICHTIG - Cache-Problem:
//  Sowohl Unity als auch der Server/Browser speichern heruntergeladene Dateien
//  zwischen ("Cache"). Ohne Gegenmaßnahme bekommt die App beim zweiten Laden
//  die ALTE Fassung, obwohl in Sciebo längst etwas geändert wurde.
//  Deshalb hängen wir an die URL einen Zeitstempel an und setzen zusätzlich
//  "no-cache"-Kopfzeilen. Damit ist jede Anfrage für den Cache "neu".
//
//  Erwartete Struktur der JSON-Datei (prompts.json):
//  {
//    "prompts": [
//      { "name": "Berufliche Überlastung", "text": "Du bist eine ..." },
//      { "name": "Beziehungskonflikt",     "text": "Du bist eine ..." }
//    ]
//  }
//  ACHTUNG: Zwischen den Einträgen muss ein Komma stehen, nach dem LETZTEN
//  Eintrag aber KEINES. Das ist der häufigste Fehler beim Hinzufügen.
// ============================================================================
public class PromptCloudLoader
{
    // ------------------------------------------------------------------------
    //  Ein einzelner Szenario-Eintrag (= ein Objekt im "prompts"-Array).
    // ------------------------------------------------------------------------
    [Serializable]
    public class PromptEntry
    {
        public string name;  // Anzeigename -> erscheint im Dropdown-Menü
        public string text;  // Vollständiger System-Prompt für die KI
    }

    // ------------------------------------------------------------------------
    //  Wurzel-Objekt der JSON-Datei (bildet das Feld "prompts" ab).
    // ------------------------------------------------------------------------
    [Serializable]
    private class PromptCollection
    {
        public List<PromptEntry> prompts;
    }

    // ------------------------------------------------------------------------
    //  LoadAsync
    //  Lädt die Prompt-Liste asynchron (ohne das Bild einzufrieren).
    //
    //  Parameter:
    //    url -> Sciebo-Direkt-Download-Link (muss auf /download enden!)
    //
    //  Rückgabe:
    //    Liste bei Erfolg, null bei Fehler.
    // ------------------------------------------------------------------------
    public static async Task<List<PromptEntry>> LoadAsync(string url)
    {
        // Sicherheitscheck: Wurde überhaupt eine URL gesetzt?
        if (string.IsNullOrWhiteSpace(url))
        {
            Debug.LogError("[PromptLoader] Keine Cloud-URL angegeben! " +
                           "Bitte im Inspector des ConfigUIManager den Sciebo-Link eintragen.");
            return null;
        }

        // --- Cache-Buster anhängen ---
        // Wir hängen einen Zeitstempel als Parameter an, z.B. ".../download?t=638..."
        // Für den Server ist das eine "neue" Adresse, also wird nichts aus dem
        // Zwischenspeicher geliefert. Der Zeitstempel selbst wird ignoriert.
        string separator = url.Contains("?") ? "&" : "?";
        string urlWithCacheBuster = url + separator + "t=" + DateTime.UtcNow.Ticks;

        Debug.Log($"[PromptLoader] Lade Prompts von: {urlWithCacheBuster}");

        using UnityWebRequest req = UnityWebRequest.Get(urlWithCacheBuster);
        req.timeout = 20;  // nach 20 Sekunden abbrechen

        // Zusätzlich dem Server/Proxy ausdrücklich sagen: bitte nichts Zwischengespeichertes.
        req.SetRequestHeader("Cache-Control", "no-cache, no-store, must-revalidate");
        req.SetRequestHeader("Pragma", "no-cache");

        // Anfrage abschicken und warten, ohne Unity zu blockieren.
        var operation = req.SendWebRequest();
        while (!operation.isDone)
            await Task.Yield();

        // Netzwerk-/HTTP-Fehler (404, Timeout, kein Internet ...)
        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[PromptLoader] Fehler beim Laden: {req.error} " +
                           $"(HTTP-Code {req.responseCode})");
            return null;
        }

        string json = req.downloadHandler.text;

        // --- Plausibilitätsprüfung ---
        // Wenn Sciebo statt der Datei eine Webseite liefert (z.B. Login-Seite oder
        // Fehlerseite), fängt der Text mit "<" an. Das ist HTML, kein JSON.
        string trimmed = json == null ? "" : json.TrimStart();
        if (trimmed.StartsWith("<"))
        {
            Debug.LogError("[PromptLoader] Der Server hat HTML statt JSON geliefert. " +
                           "Endet der Link wirklich auf /download? Ist die Freigabe ohne Passwort?");
            return null;
        }

        // --- JSON in C#-Objekte umwandeln ---
        try
        {
            PromptCollection collection = JsonConvert.DeserializeObject<PromptCollection>(json);

            if (collection == null || collection.prompts == null || collection.prompts.Count == 0)
            {
                Debug.LogWarning("[PromptLoader] JSON geladen, aber keine Prompts darin gefunden. " +
                                 "Stimmt die Struktur (Feld \"prompts\" mit einer Liste)?");
                return null;
            }

            // Einträge prüfen: leere Namen oder Texte würden später Probleme machen.
            for (int i = collection.prompts.Count - 1; i >= 0; i--)
            {
                var p = collection.prompts[i];
                if (p == null || string.IsNullOrWhiteSpace(p.name) || string.IsNullOrWhiteSpace(p.text))
                {
                    Debug.LogWarning($"[PromptLoader] Eintrag Nr. {i + 1} ist unvollständig " +
                                     "(name oder text fehlt) und wird übersprungen.");
                    collection.prompts.RemoveAt(i);
                }
            }

            if (collection.prompts.Count == 0)
            {
                Debug.LogError("[PromptLoader] Nach der Prüfung ist kein gültiger Eintrag übrig.");
                return null;
            }

            Debug.Log($"[PromptLoader] Erfolgreich {collection.prompts.Count} Szenarien geladen.");
            return collection.prompts;
        }
        catch (Exception e)
        {
            // Die JSON-Datei ist fehlerhaft formatiert (z.B. fehlendes Komma).
            // Wir geben den Anfang der Datei mit aus, damit man den Fehler findet.
            string vorschau = json.Length > 300 ? json.Substring(0, 300) + " ..." : json;
            Debug.LogError($"[PromptLoader] JSON konnte nicht gelesen werden: {e.Message}\n" +
                           $"Anfang der geladenen Datei:\n{vorschau}\n" +
                           "Tipp: Die Datei auf https://jsonlint.com prüfen. " +
                           "Häufigster Fehler: fehlendes Komma zwischen zwei Einträgen " +
                           "oder ein Komma nach dem letzten Eintrag.");
            return null;
        }
    }
}