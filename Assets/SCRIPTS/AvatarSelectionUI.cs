using UnityEngine;
using TMPro;

// ============================================================================
//  AvatarSelectionUI
// ----------------------------------------------------------------------------
//  Füllt im Einstellungsmenü ein Dropdown mit den verfügbaren Figuren und
//  merkt sich die Auswahl. Beim Betreten des Beratungszimmers liest der
//  AvatarSpawner diese Auswahl aus und erzeugt die passende Figur.
//
//  Warum ein eigenes Skript und nicht im ConfigUIManager?
//    So bleibt die Änderung getrennt von der Stimmen- und Szenario-Auswahl.
//    Falls etwas nicht funktioniert, ist klar, wo es liegt - und der bereits
//    funktionierende ConfigUIManager wird nicht angefasst.
//
//  Einrichtung im Editor (Hauptmenü-Szene):
//    1. Im Einstellungs-Panel ein TMP-Dropdown anlegen (Rechtsklick auf das
//       Panel -> UI -> Dropdown - TextMeshPro).
//    2. Dieses Skript auf das Objekt legen, auf dem auch der ConfigUIManager
//       sitzt.
//    3. Feld "Config"          -> das ApiConfig-Asset
//    4. Feld "Avatar Dropdown" -> das neue Dropdown
//
//  Das Dropdown muss NICHT von Hand befüllt werden - die Einträge kommen
//  automatisch aus ApiConfig.availableAvatars.
// ============================================================================
public class AvatarSelectionUI : MonoBehaviour
{
    [Header("Konfiguration")]
    [Tooltip("Das ApiConfig-Asset mit der Liste der verfügbaren Avatare.")]
    [SerializeField] private ApiConfig config;

    [Header("UI")]
    [Tooltip("Das Dropdown, in dem die Figur gewählt wird.")]
    [SerializeField] private TMP_Dropdown avatarDropdown;

    [Header("Debug")]
    [Tooltip("Ausführliche Meldungen in der Konsole ausgeben.")]
    [SerializeField] private bool debugLogs = true;

    private void Start()
    {
        if (config == null)
        {
            Debug.LogError("[AvatarSelectionUI] ApiConfig nicht zugewiesen!");
            return;
        }

        if (avatarDropdown == null)
        {
            Debug.LogError("[AvatarSelectionUI] Kein Dropdown zugewiesen!");
            return;
        }

        if (config.availableAvatars == null || config.availableAvatars.Length == 0)
        {
            Debug.LogError("[AvatarSelectionUI] Die Liste 'availableAvatars' im ApiConfig " +
                           "ist leer. Bitte dort die Prefabs mit Anzeigenamen eintragen.");
            return;
        }

        FuelleDropdown();

        // Auf Änderungen reagieren.
        avatarDropdown.onValueChanged.AddListener(OnAvatarGewaehlt);
    }

    private void OnDestroy()
    {
        // Zuhörer wieder abmelden, damit beim Szenenwechsel nichts hängen bleibt.
        if (avatarDropdown != null)
            avatarDropdown.onValueChanged.RemoveListener(OnAvatarGewaehlt);
    }

    // ------------------------------------------------------------------------
    //  Trägt die Anzeigenamen aus dem ApiConfig ins Dropdown ein und stellt
    //  die zuletzt gewählte Figur wieder her.
    // ------------------------------------------------------------------------
    private void FuelleDropdown()
    {
        avatarDropdown.ClearOptions();

        var namen = new System.Collections.Generic.List<string>();

        for (int i = 0; i < config.availableAvatars.Length; i++)
        {
            AvatarOption option = config.availableAvatars[i];

            // Falls kein Anzeigename eingetragen wurde, den Prefab-Namen nehmen -
            // besser als ein leerer Eintrag, bei dem man nichts erkennt.
            string name = option != null && !string.IsNullOrWhiteSpace(option.anzeigeName)
                ? option.anzeigeName
                : (option != null && option.prefab != null ? option.prefab.name : $"Figur {i + 1}");

            namen.Add(name);
        }

        avatarDropdown.AddOptions(namen);

        // Zuletzt gewählte Figur wiederherstellen. Der Index wird abgesichert,
        // falls die Liste inzwischen kürzer geworden ist.
        int gespeichert = PlayerPrefs.GetInt(AvatarSpawner.PREF_AVATAR, 0);
        if (gespeichert < 0 || gespeichert >= namen.Count)
            gespeichert = 0;

        // SetValueWithoutNotify: sonst würde das Setzen sofort OnAvatarGewaehlt
        // auslösen und einen Speichervorgang anstoßen, obwohl niemand etwas
        // ausgewählt hat.
        avatarDropdown.SetValueWithoutNotify(gespeichert);
        avatarDropdown.RefreshShownValue();

        if (debugLogs)
            Debug.Log($"[AvatarSelectionUI] {namen.Count} Figuren im Dropdown, " +
                      $"vorausgewählt: {namen[gespeichert]}");
    }

    // ------------------------------------------------------------------------
    //  Wird aufgerufen, sobald im Dropdown etwas gewählt wird.
    //  Speichert nur den Index - erzeugt wird die Figur erst im
    //  Beratungszimmer durch den AvatarSpawner.
    // ------------------------------------------------------------------------
    private void OnAvatarGewaehlt(int index)
    {
        PlayerPrefs.SetInt(AvatarSpawner.PREF_AVATAR, index);
        PlayerPrefs.Save();

        if (debugLogs && index >= 0 && index < config.availableAvatars.Length)
            Debug.Log($"[AvatarSelectionUI] Figur gewählt: " +
                      $"{config.availableAvatars[index].anzeigeName} (Index {index})");
    }
}