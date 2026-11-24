using System;
using UnityEngine;

// ============================================================================
//  NpcLipSync
// ----------------------------------------------------------------------------
//  Einfaches Lip-Sync für den NPC-Avatar: Der Mund öffnet sich passend zur
//  Lautstärke der Sprachausgabe. Es werden keine einzelnen Laute (Visemes)
//  unterschieden, für Gespräche in VR wirkt das aber schon deutlich lebendiger
//  als ein starrer Mund.
//
//  So funktioniert es:
//    Jede Frame wird ein kleiner Block der aktuellen Ausgabe der AudioSource
//    gelesen, daraus die Lautstärke (RMS) berechnet und geglättet. Das Ergebnis
//    (0..1) steuert die Mundöffnung auf eine von zwei Arten:
//
//    Variante A - Blendshape:
//      Das Gesichts-Mesh hat einen Blendshape zum Öffnen des Mundes, z.B.
//      "mouthOpen" oder "jawOpen". Die Namen stehen in 'mouthBlendShapeNames'.
//
//    Variante B - Kieferknochen (z.B. bei Microsoft Rocketbox):
//      Hat der Avatar keinen passenden Blendshape, wird stattdessen der
//      Kieferknochen gedreht. Er wird automatisch gesucht (Humanoid-Jaw oder
//      ein Knochen, dessen Name "jaw" enthält). Um welche Achse und wie weit
//      gedreht wird, stellt 'jawOpenEuler' ein.
//
//    Ist ein Blendshape vorhanden, hat er Vorrang vor dem Kieferknochen.
//
//  Einbindung:
//    Wird vom AIConversationManager automatisch am Avatar angehängt und mit
//    der AudioSource verbunden (siehe SetupAvatarComponents).
// ============================================================================
public class NpcLipSync : MonoBehaviour
{
    [Header("Quelle")]
    [Tooltip("Die AudioSource, aus der die Stimme kommt. Wird vom Manager gesetzt.")]
    [SerializeField] private AudioSource source;

    [Header("Variante A: Blendshape")]
    [Tooltip("Das Gesichts-Mesh. Wenn leer, wird es unter dem Avatar gesucht.")]
    [SerializeField] private SkinnedMeshRenderer faceRenderer;

    [Tooltip("Mögliche Namen des Blendshapes zum Öffnen des Mundes, in dieser Reihenfolge.")]
    [SerializeField] private string[] mouthBlendShapeNames =
        { "mouthOpen", "jawOpen", "viseme_aa", "aa", "A" };

    [Tooltip("Maximales Gewicht des Blendshapes (0-100).")]
    [Range(0f, 100f)]
    [SerializeField] private float maxWeight = 60f;

    [Header("Variante B: Kieferknochen")]
    [Tooltip("Der Kieferknochen. Wenn leer, wird er automatisch gesucht.")]
    [SerializeField] private Transform jawBone;

    [Tooltip("Drehung des Kiefers bei voll geöffnetem Mund, in Grad (lokale Euler-Winkel). " +
             "Welche Achse den Mund öffnet, hängt vom Rig ab: im Play-Modus am Kieferknochen " +
             "die Rotation von Hand verändern und den passenden Wert hier eintragen.")]
    [SerializeField] private Vector3 jawOpenEuler = new Vector3(0f, 0f, 12f);

    [Header("Feinabstimmung")]
    [Tooltip("Verstärkung der Lautstärke. Höher = Mund reagiert stärker, auch auf leise Sprache.")]
    [SerializeField] private float sensitivity = 8f;

    [Tooltip("Unterhalb dieser Lautstärke bleibt der Mund geschlossen (Rauschen).")]
    [SerializeField] private float noiseGate = 0.01f;

    [Tooltip("Wie schnell sich der Mund öffnet (pro Sekunde, 1 = voll).")]
    [SerializeField] private float openSpeed = 20f;

    [Tooltip("Wie schnell sich der Mund schließt (pro Sekunde).")]
    [SerializeField] private float closeSpeed = 10f;

    private readonly float[] samples = new float[256];
    private int shapeIndex = -1;
    private Quaternion jawRest = Quaternion.identity;
    private float current;      // aktuelle Mundöffnung, 0..1
    private bool resolved;

    // Vom Manager aufgerufen, sobald Avatar und AudioSource bekannt sind.
    public void Init(AudioSource audioSource)
    {
        source = audioSource;
        Resolve();
    }

    // Falls das Skript fest am Avatar-Prefab hängt und Init() nie aufgerufen wird.
    private void Start()
    {
        if (!resolved) Resolve();
    }

    private void Resolve()
    {
        resolved = true;

        ResolveBlendShape();
        if (shapeIndex >= 0) return;

        ResolveJaw();

        if (shapeIndex < 0 && jawBone == null)
            Debug.LogWarning("[LipSync] Weder Mund-Blendshape noch Kieferknochen gefunden. " +
                             "'mouthBlendShapeNames' bzw. 'jawBone' prüfen.", this);
    }

    // Sucht Gesichts-Mesh und passenden Blendshape.
    private void ResolveBlendShape()
    {
        shapeIndex = -1;

        SkinnedMeshRenderer[] kandidaten = faceRenderer != null
            ? new[] { faceRenderer }
            : GetComponentsInChildren<SkinnedMeshRenderer>(true);

        foreach (var smr in kandidaten)
        {
            if (smr == null || smr.sharedMesh == null) continue;

            foreach (var name in mouthBlendShapeNames)
            {
                int index = smr.sharedMesh.GetBlendShapeIndex(name);
                if (index >= 0)
                {
                    faceRenderer = smr;
                    shapeIndex = index;
                    Debug.Log($"[LipSync] Blendshape '{name}' an '{smr.name}' gefunden.");
                    return;
                }
            }
        }
    }

    // Sucht den Kieferknochen: erst über das Humanoid-Mapping, dann über den Namen.
    private void ResolveJaw()
    {
        if (jawBone == null)
        {
            var animator = GetComponent<Animator>();
            if (animator != null && animator.isHuman)
                jawBone = animator.GetBoneTransform(HumanBodyBones.Jaw);
        }

        if (jawBone == null)
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                if (t.name.IndexOf("jaw", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    jawBone = t;
                    break;
                }
            }
        }

        if (jawBone != null)
        {
            jawRest = jawBone.localRotation;
            Debug.Log($"[LipSync] Kieferknochen '{jawBone.name}' gefunden.");
        }
    }

    // LateUpdate, damit der Animator den Wert nicht wieder überschreibt.
    private void LateUpdate()
    {
        if (shapeIndex < 0 && jawBone == null) return;

        float target = 0f;

        if (source != null && source.isPlaying)
        {
            source.GetOutputData(samples, 0);

            float summe = 0f;
            for (int i = 0; i < samples.Length; i++)
                summe += samples[i] * samples[i];

            float rms = Mathf.Sqrt(summe / samples.Length);
            if (rms > noiseGate)
                target = Mathf.Clamp01(rms * sensitivity);
        }

        float speed = target > current ? openSpeed : closeSpeed;
        current = Mathf.MoveTowards(current, target, speed * Time.deltaTime);

        if (shapeIndex >= 0)
        {
            faceRenderer.SetBlendShapeWeight(shapeIndex, current * maxWeight);
        }
        else
        {
            // Absolut zur Ruhelage setzen, damit sich die Drehung nicht aufsummiert.
            jawBone.localRotation = jawRest * Quaternion.Euler(jawOpenEuler * current);
        }
    }
}
