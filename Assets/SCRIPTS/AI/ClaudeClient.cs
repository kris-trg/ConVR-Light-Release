using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine;
using UnityEngine.Networking;

// ============================================================================
//  ClaudeClient
// ----------------------------------------------------------------------------
//  Verbindung zur Anthropic-Schnittstelle (Claude). Erzeugt die Antworten der
//  ratsuchenden Person.
//
//  Gesprächs-History:
//  Ein Sprachmodell hat kein Gedächtnis. Bei jeder Anfrage wird der komplette
//  bisherige Verlauf mitgeschickt (Liste 'history'). Mit ResetHistory()
//  beginnt ein frisches Gespräch.
//
//  Änderungen gegenüber der alten Version:
//    1. Die Antwort wird NICHT mehr über content[0] gelesen. Claude kann vor
//       dem eigentlichen Text weitere Blöcke liefern (z.B. "thinking"). Dieser
//       Block hat kein Textfeld, deshalb kam früher null zurück und die
//       Anfrage galt als fehlgeschlagen. Jetzt werden alle Blöcke vom Typ
//       "text" gesammelt.
//    2. Der Format-Fallback (Array/String) ist entfernt. Gesendet wird immer
//       das dokumentierte Array-Format.
//    3. Wiederholt wird nur noch bei vorübergehenden Fehlern (HTTP 429, 529,
//       5xx, Verbindungsabbruch), mit kurzer Pause. Timeouts und fachliche
//       Fehler (z.B. HTTP 400) werden nicht wiederholt.
//    4. Der Grund eines Fehlschlags steht in LastError und wird einmal als
//       LogError ausgegeben. Der Client wirft keine Exceptions mehr nach außen.
//    5. Leere Nutzernachrichten werden gar nicht erst gesendet.
//
//  Token-Zählung:
//  Anthropic liefert in jeder Antwort den Verbrauch mit. Er steht in
//  LastInputTokens/LastOutputTokens (Denk-Tokens zählen zu den Output-Tokens).
// ============================================================================
public class ClaudeClient
{
    private const string ENDPOINT = "https://api.anthropic.com/v1/messages";
    private const string API_VERSION = "2023-06-01";
    private const int TIMEOUT_SECONDS = 30;
    private const int MAX_RETRIES = 2;   // zusätzliche Versuche bei vorübergehenden Fehlern

    private readonly string apiKey;
    private readonly string model;
    private readonly int maxTokens;
    private readonly string systemPrompt;

    // Der bisherige Gesprächsverlauf (nur Rolle und reiner Text).
    private readonly List<Turn> history = new();

    // Token-Verbrauch der zuletzt gesendeten Anfrage.
    public int LastInputTokens { get; private set; }
    public int LastOutputTokens { get; private set; }

    // Grund des letzten Fehlschlags (leer, wenn die letzte Anfrage geklappt hat).
    public string LastError { get; private set; } = "";

    // Wenn true: Request-JSON und Rohantwort werden in die Konsole geschrieben.
    public bool VerboseLogging { get; set; } = false;

    // Der verwendete Modellname, damit er mitprotokolliert werden kann.
    public string ModelName => model;

    public ClaudeClient(string apiKey, string model, int maxTokens, string systemPrompt)
    {
        this.apiKey = apiKey;
        this.model = model;
        this.maxTokens = maxTokens;
        this.systemPrompt = systemPrompt;
    }

    // Löscht den Gesprächsverlauf. Die KI "vergisst" damit alles Bisherige.
    public void ResetHistory() => history.Clear();

    // ------------------------------------------------------------------------
    //  SendMessageAsync
    //  Schickt eine Äußerung des Beraters an Claude und liefert die Antwort.
    //  Rückgabe: die Antwort der KI, oder null bei Fehler (Grund: LastError).
    // ------------------------------------------------------------------------
    public async Task<string> SendMessageAsync(string userMessage)
    {
        // Werte zurücksetzen, damit bei einem Fehler keine alten Zahlen
        // aus der vorherigen Runde stehen bleiben.
        LastInputTokens = 0;
        LastOutputTokens = 0;
        LastError = "";

        if (string.IsNullOrWhiteSpace(userMessage))
        {
            LastError = "Leere Nutzernachricht, nichts gesendet.";
            Debug.LogWarning($"[Claude] {LastError}");
            return null;
        }

        // Die neue Äußerung an den Verlauf anhängen.
        history.Add(new Turn { role = "user", text = userMessage.Trim() });

        RequestResult result = null;

        for (int versuch = 0; versuch <= MAX_RETRIES; versuch++)
        {
            result = await SendeAnfrage();
            if (result.Success) break;

            LastError = result.Error;

            if (!result.Retryable || versuch == MAX_RETRIES) break;

            int pauseMs = 1000 * (versuch + 1);
            Debug.LogWarning($"[Claude] Vorübergehender Fehler, neuer Versuch in {pauseMs} ms. " +
                             $"Grund: {result.Error}");
            await Task.Delay(pauseMs);
        }

        if (result == null || !result.Success)
        {
            // Die gerade angehängte Äußerung wieder entfernen, damit der
            // Verlauf konsistent bleibt (immer abwechselnd user/assistant).
            history.RemoveAt(history.Count - 1);
            Debug.LogError($"[Claude] Anfrage fehlgeschlagen: {LastError}");
            return null;
        }

        // Die Antwort in den Verlauf aufnehmen, damit die KI sich beim
        // nächsten Mal daran erinnert.
        history.Add(new Turn { role = "assistant", text = result.Text });

        Debug.Log($"[Claude] Antwort: {result.Text}");
        return result.Text;
    }

    // ------------------------------------------------------------------------
    //  Baut die Anfrage, schickt sie ab und wertet die Antwort aus.
    //  Wirft keine Exceptions, jeder Fehler kommt als RequestResult zurück.
    // ------------------------------------------------------------------------
    private async Task<RequestResult> SendeAnfrage()
    {
        string json;
        try
        {
            var body = new
            {
                model,
                max_tokens = maxTokens,
                // Ein leerer System-Prompt wird weggelassen (null wird nicht serialisiert).
                system = string.IsNullOrWhiteSpace(systemPrompt) ? null : systemPrompt,
                messages = BaueNachrichten()
            };

            json = JsonConvert.SerializeObject(body, new JsonSerializerSettings
            {
                NullValueHandling = NullValueHandling.Ignore
            });
        }
        catch (Exception e)
        {
            return Fail($"Request konnte nicht gebaut werden: {e.Message}", false);
        }

        if (VerboseLogging)
            Debug.Log($"[Claude] Sende {history.Count} Nachrichten:\n{json}");

        using var req = new UnityWebRequest(ENDPOINT, "POST");
        req.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(json));
        req.downloadHandler = new DownloadHandlerBuffer();

        req.SetRequestHeader("Content-Type", "application/json");
        req.SetRequestHeader("x-api-key", apiKey);
        req.SetRequestHeader("anthropic-version", API_VERSION);
        req.timeout = TIMEOUT_SECONDS;

        var op = req.SendWebRequest();
        while (!op.isDone) await Task.Yield();

        string raw = req.downloadHandler != null ? req.downloadHandler.text : "";

        if (VerboseLogging)
            Debug.Log($"[Claude] Rohantwort: {raw}");

        // --- Transport- oder HTTP-Fehler ---
        if (req.result != UnityWebRequest.Result.Success)
        {
            long code = req.responseCode;
            string err = req.error ?? "";
            bool timeout = err.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;

            // Vorübergehend und damit wiederholbar: Rate-Limit, Überlastung,
            // Serverfehler, Verbindungsabbruch. Ein Timeout wird bewusst nicht
            // wiederholt, sonst wartet die Versuchsperson ein weiteres Mal 30 s.
            bool retryable = !timeout &&
                             (code == 429 || code == 529 || code >= 500 ||
                              req.result == UnityWebRequest.Result.ConnectionError);

            return Fail($"HTTP {code}: {err} | {raw}", retryable);
        }

        // --- Antwort auswerten ---
        ClaudeResponse resp;
        try
        {
            resp = JsonConvert.DeserializeObject<ClaudeResponse>(raw);
        }
        catch (Exception e)
        {
            return Fail($"Antwort nicht lesbar: {e.Message} | {raw}", false);
        }

        if (resp == null || resp.content == null || resp.content.Length == 0)
        {
            return Fail($"Antwort ohne Inhalt (stop_reason={resp?.stop_reason}) | {raw}", false);
        }

        // Token-Verbrauch merken (auch wenn gleich kein Text gefunden wird,
        // denn verbraucht wurden die Tokens trotzdem).
        if (resp.usage != null)
        {
            LastInputTokens = resp.usage.input_tokens;
            LastOutputTokens = resp.usage.output_tokens;
            Debug.Log($"[Claude] Tokens - Eingabe: {LastInputTokens}, Ausgabe: {LastOutputTokens}");
        }

        // Alle Textblöcke einsammeln und andere Blocktypen (z.B. "thinking")
        // überspringen. Genau hier lag der ursprüngliche Fehler: content[0]
        // war ein Denkblock ohne Text.
        var sb = new StringBuilder();
        foreach (var block in resp.content)
        {
            if (block != null && block.type == "text" && !string.IsNullOrEmpty(block.text))
                sb.Append(block.text);
        }

        string text = sb.ToString().Trim();

        if (text.Length == 0)
        {
            string hinweis = resp.stop_reason == "max_tokens"
                ? " (maxTokens erhöhen: Denk-Tokens zählen zum Limit)"
                : "";
            return Fail($"Kein Textblock in der Antwort, stop_reason={resp.stop_reason}{hinweis} | {raw}", false);
        }

        if (resp.stop_reason == "max_tokens")
            Debug.LogWarning("[Claude] Antwort wurde wegen maxTokens abgeschnitten.");

        return Ok(text);
    }

    // ------------------------------------------------------------------------
    //  Übersetzt den Verlauf in das dokumentierte Array-Format:
    //    { "role": "user", "content": [ { "type": "text", "text": "..." } ] }
    // ------------------------------------------------------------------------
    private List<Message> BaueNachrichten()
    {
        var liste = new List<Message>(history.Count);
        foreach (var t in history)
        {
            liste.Add(new Message
            {
                role = t.role,
                content = new[] { new ContentBlockOut { type = "text", text = t.text } }
            });
        }
        return liste;
    }

    private static RequestResult Ok(string text) =>
        new RequestResult { Success = true, Text = text };

    private static RequestResult Fail(string error, bool retryable) =>
        new RequestResult { Success = false, Error = error, Retryable = retryable };

    // --- Interne Speicherform des Gesprächsverlaufs ---
    private class Turn
    {
        public string role;  // "user" oder "assistant"
        public string text;  // der reine Text
    }

    // --- Ergebnis eines einzelnen Requests ---
    private class RequestResult
    {
        public bool Success;
        public string Text;
        public string Error;
        public bool Retryable;
    }

    // --- Hilfsklassen fürs Senden ---
    private class Message
    {
        public string role;
        public ContentBlockOut[] content;
    }

    private class ContentBlockOut
    {
        public string type;  // immer "text"
        public string text;
    }

    // --- Hilfsklassen fürs Empfangen ---
    private class ClaudeResponse
    {
        public ContentBlock[] content;
        public Usage usage;
        public string stop_reason;
    }

    private class ContentBlock
    {
        public string type;   // "text", "thinking", ...
        public string text;   // nur bei type == "text" gefüllt
    }

    private class Usage
    {
        public int input_tokens;
        public int output_tokens;
    }
}