using UnityEngine;

// ============================================================================
//  AvatarSpawner
// ----------------------------------------------------------------------------
//  Erzeugt beim Start des Beratungszimmers die Figur, die im Einstellungsmenü
//  gewählt wurde, und verbindet ihren Animator mit dem AIConversationManager.
//
//  Warum überhaupt ein Spawner?
//    Die KI-Technik (AIConversationManager, MicrophoneRecorder, AudioSource,
//    Statusanzeige) steht fest in der Szene am Objekt AICharacter. Nur das
//    Aussehen der ratsuchenden Person wechselt. Die vier Avatar-Prefabs
//    enthalten deshalb ausschließlich Modell und Animator - keine Technik.
//
//  Woher kommt die Auswahl?
//    Aus PlayerPrefs, gesetzt vom Einstellungsmenü (AvatarSelectionUI).
//    Gespeichert wird ein Index in die Liste ApiConfig.availableAvatars.
//
//  Einrichtung im Editor:
//    1. Dieses Skript auf den AvatarSpawnPoint in der BasicScene legen.
//    2. Feld "Config"               -> das ApiConfig-Asset
//    3. Feld "Spawn Point"          -> leer lassen (nimmt dann das eigene Objekt)
//    4. Feld "Conversation Manager" -> das AICharacter-Objekt aus der Szene
// ============================================================================
public class AvatarSpawner : MonoBehaviour
{
    // Schlüssel, unter dem die Auswahl gespeichert wird. Wird auch vom
    // Einstellungsmenü verwendet - deshalb hier zentral definiert.
    public const string PREF_AVATAR = "SelectedAvatarIndex";

    [Header("Konfiguration")]
    [Tooltip("Das ApiConfig-Asset mit der Liste der verfügbaren Avatare.")]
    [SerializeField] private ApiConfig config;

    [Header("Referenzen")]
    [Tooltip("Wo die Figur erscheinen soll - normalerweise der Stuhlplatz. " +
             "Wenn leer, wird die Position dieses GameObjects verwendet.")]
    [SerializeField] private Transform spawnPoint;

    [Tooltip("Der AIConversationManager in der Szene. Wenn leer, wird er beim " +
             "Start automatisch gesucht.")]
    [SerializeField] private AIConversationManager conversationManager;

    [Header("Debug")]
    [Tooltip("Ausführliche Meldungen in der Konsole ausgeben.")]
    [SerializeField] private bool debugLogs = true;

    // Die aktuell erzeugte Figur. Wird gemerkt, damit bei einem Wechsel zur
    // Laufzeit die alte entfernt werden kann.
    private GameObject aktuellerAvatar;

    public GameObject AktuellerAvatar => aktuellerAvatar;

    // ------------------------------------------------------------------------
    //  Start(): erzeugt die gewählte Figur.
    //
    //  Bewusst Start() und nicht Awake(): Der AIConversationManager richtet
    //  sich in seinem eigenen Awake() ein. Erst danach darf ihm ein Animator
    //  gemeldet werden, sonst überschreibt seine automatische Suche den Wert
    //  wieder.
    // ------------------------------------------------------------------------
    private void Start()
    {
        if (config == null)
        {
            Debug.LogError("[AvatarSpawner] ApiConfig nicht zugewiesen! " +
                           "Bitte im Inspector das ApiConfig-Asset eintragen.");
            return;
        }

        if (config.availableAvatars == null || config.availableAvatars.Length == 0)
        {
            Debug.LogError("[AvatarSpawner] Die Liste 'availableAvatars' im " +
                           "ApiConfig ist leer. Bitte dort die vier Prefabs eintragen.");
            return;
        }

        // Falls im Inspector nichts gesetzt wurde: KI in der Szene suchen.
        if (conversationManager == null)
            conversationManager = FindObjectOfType<AIConversationManager>();

        if (conversationManager == null)
            Debug.LogWarning("[AvatarSpawner] Kein AIConversationManager in der Szene " +
                             "gefunden. Die Figur erscheint, bewegt sich beim Sprechen " +
                             "aber nicht.");

        int index = LiesGewaehltenIndex();
        SpawneAvatar(index);
    }

    // ------------------------------------------------------------------------
    //  Liest den gespeicherten Index und sorgt dafür, dass er zur aktuellen
    //  Liste passt.
    //
    //  Warum die Absicherung?
    //    Wird später ein Prefab aus der Liste entfernt, zeigt ein gespeicherter
    //    Index unter Umständen ins Leere. Ohne Prüfung stürzt die Szene beim
    //    Start ab. Dann lieber auf die erste Figur zurückfallen.
    // ------------------------------------------------------------------------
    private int LiesGewaehltenIndex()
    {
        int index = PlayerPrefs.GetInt(PREF_AVATAR, 0);

        if (index < 0 || index >= config.availableAvatars.Length)
        {
            Debug.LogWarning($"[AvatarSpawner] Gespeicherte Auswahl ({index}) passt " +
                             $"nicht mehr zur Liste ({config.availableAvatars.Length} " +
                             $"Einträge). Es wird die erste Figur verwendet.");
            index = 0;
        }

        return index;
    }

    // ------------------------------------------------------------------------
    //  Erzeugt die Figur mit dem angegebenen Index und verbindet sie mit der KI.
    //  Kann auch zur Laufzeit aufgerufen werden - eine bereits vorhandene Figur
    //  wird dabei entfernt.
    // ------------------------------------------------------------------------
    public void SpawneAvatar(int index)
    {
        if (config == null || config.availableAvatars == null) return;
        if (index < 0 || index >= config.availableAvatars.Length) return;

        AvatarOption option = config.availableAvatars[index];

        if (option == null || option.prefab == null)
        {
            Debug.LogError($"[AvatarSpawner] Eintrag {index} in 'availableAvatars' hat " +
                           $"kein Prefab. Bitte im ApiConfig nachtragen.");
            return;
        }

        // Alte Figur entfernen, falls schon eine dasteht.
        if (aktuellerAvatar != null)
            Destroy(aktuellerAvatar);

        // Wo soll sie hin? Bevorzugt der eingetragene Sitzplatz, sonst die
        // Position dieses Objekts.
        Transform ziel = spawnPoint != null ? spawnPoint : transform;

        aktuellerAvatar = Instantiate(option.prefab, ziel.position, ziel.rotation, ziel);

        // Der Name im Hierarchie-Fenster wird sonst "NPC_Mona(Clone)" - so ist
        // beim Debuggen sofort klar, welche Figur geladen wurde.
        aktuellerAvatar.name = $"Avatar_{option.anzeigeName}";

        if (debugLogs)
            Debug.Log($"[AvatarSpawner] Figur erzeugt: {option.anzeigeName} " +
                      $"(Index {index}) an Position {ziel.position}");

        VerbindeAnimator();
    }

    // ------------------------------------------------------------------------
    //  Sucht den Animator der erzeugten Figur und meldet ihn an die KI.
    //
    //  Warum GetComponentInChildren mit true?
    //    Der Animator sitzt bei euren Prefabs nicht an der Wurzel, sondern am
    //    Modell-Objekt eine Ebene tiefer (z.B. npc_csl_00_character_01f_02).
    //    Das "true" sorgt dafür, dass auch deaktivierte Objekte durchsucht
    //    werden - sonst wird ein ausgeblendeter Avatar übersehen.
    // ------------------------------------------------------------------------
    private void VerbindeAnimator()
    {
        if (aktuellerAvatar == null) return;

        Animator animator = aktuellerAvatar.GetComponentInChildren<Animator>(true);

        if (animator == null)
        {
            Debug.LogError($"[AvatarSpawner] '{aktuellerAvatar.name}' hat keinen Animator. " +
                           $"Die Figur bleibt beim Sprechen regungslos. Bitte am Modell-" +
                           $"Objekt im Prefab eine Animator-Komponente mit Controller " +
                           $"hinterlegen.");
            return;
        }

        if (animator.runtimeAnimatorController == null)
        {
            Debug.LogError($"[AvatarSpawner] '{aktuellerAvatar.name}' hat einen Animator, " +
                           $"aber keinen Controller zugewiesen. Die Sprech-Animation " +
                           $"funktioniert so nicht.");
        }
        else
        {
            // Prüfen, ob der Umschalt-Parameter überhaupt existiert. Fehlt er,
            // bleibt die Figur beim Sprechen sitzen - und man sucht den Fehler
            // erfahrungsgemäß im Code, wo keiner ist.
            PruefeTalkingParameter(animator);
        }

        if (conversationManager != null)
        {
            conversationManager.SetAnimator(animator);

            if (debugLogs)
                Debug.Log($"[AvatarSpawner] Animator '{animator.name}' mit der KI verbunden " +
                          $"(Controller: {animator.runtimeAnimatorController?.name ?? "keiner"}).");
        }
    }

    // ------------------------------------------------------------------------
    //  Kontrolliert, ob im Animator Controller ein Bool-Parameter "isTalking"
    //  existiert, und meldet sich in der Konsole, falls nicht.
    //
    //  Hinweis: Heißt der Parameter im AIConversationManager anders (Feld
    //  "Talking Parameter Name"), muss der Name hier ebenfalls angepasst werden.
    // ------------------------------------------------------------------------
    private void PruefeTalkingParameter(Animator animator)
    {
        const string parameterName = "isTalking";

        foreach (AnimatorControllerParameter p in animator.parameters)
        {
            if (p.name == parameterName && p.type == AnimatorControllerParameterType.Bool)
                return;   // alles in Ordnung
        }

        Debug.LogWarning($"[AvatarSpawner] Im Controller " +
                         $"'{animator.runtimeAnimatorController.name}' fehlt der Bool-" +
                         $"Parameter '{parameterName}'. Diese Figur wird beim Sprechen " +
                         $"nicht animiert. Im Animator-Fenster unter 'Parameters' anlegen.");
    }

    // ------------------------------------------------------------------------
    //  Wechselt die Figur zur Laufzeit und merkt sich die Auswahl.
    //  Wird gebraucht, falls die Auswahl später auch im Beratungszimmer
    //  möglich sein soll.
    // ------------------------------------------------------------------------
    public void WechsleAvatar(int index)
    {
        PlayerPrefs.SetInt(PREF_AVATAR, index);
        PlayerPrefs.Save();
        SpawneAvatar(index);
    }
}