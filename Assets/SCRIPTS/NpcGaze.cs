using UnityEngine;

// ============================================================================
//  NpcGaze
// ----------------------------------------------------------------------------
//  Lässt den NPC-Avatar die Versuchsperson ansehen: Kopf und Augen drehen sich
//  per Animator-IK zur Kamera (bei VR also zum Kopf der Versuchsperson).
//
//  Voraussetzungen (sonst passiert nichts):
//    1. Der Avatar hat einen HUMANOIDEN Rig (Animator mit Humanoid-Avatar).
//    2. Im Animator Controller ist beim verwendeten Layer "IK Pass" aktiviert
//       (Animator-Fenster -> Layers -> Zahnrad am Layer -> IK Pass).
//    3. Dieses Skript hängt am selben GameObject wie der Animator. Der
//       AIConversationManager erledigt das automatisch (SetupAvatarComponents).
//
//  Blickziel:
//    'target' leer lassen -> es wird Camera.main verwendet. In VR muss die
//    Kamera des XR Origin dafür den Tag "MainCamera" tragen.
//
//  Engagement:
//    Mit SetEngagement(0..1) lässt sich der Blickkontakt dosieren, z.B. etwas
//    weniger, während die KI "nachdenkt". Der Übergang ist weich.
// ============================================================================
[RequireComponent(typeof(Animator))]
public class NpcGaze : MonoBehaviour
{
    [Tooltip("Blickziel. Leer = Camera.main.")]
    [SerializeField] private Transform target;

    [Header("Gewichte (0-1)")]
    [Tooltip("Wie stark der Oberkörper mitdreht.")]
    [Range(0f, 1f)] [SerializeField] private float bodyWeight = 0.1f;

    [Tooltip("Wie stark der Kopf zum Ziel dreht.")]
    [Range(0f, 1f)] [SerializeField] private float headWeight = 0.7f;

    [Tooltip("Wie stark die Augen zum Ziel drehen (nur sichtbar, wenn der Avatar Augenknochen hat).")]
    [Range(0f, 1f)] [SerializeField] private float eyesWeight = 1f;

    [Tooltip("Begrenzt die Drehung. Höher = Kopf darf sich weniger weit drehen.")]
    [Range(0f, 1f)] [SerializeField] private float clampWeight = 0.6f;

    [Tooltip("Wie schnell sich das Engagement ändert (pro Sekunde).")]
    [SerializeField] private float blendSpeed = 2f;

    private Animator animator;
    private float weight;               // aktuelles Gesamtgewicht
    private float targetWeight = 1f;    // Zielgewicht

    private void Awake()
    {
        animator = GetComponent<Animator>();
    }

    // 0 = kein Blickkontakt, 1 = voller Blickkontakt.
    public void SetEngagement(float value)
    {
        targetWeight = Mathf.Clamp01(value);
    }

    public void SetTarget(Transform newTarget)
    {
        target = newTarget;
    }

    // Wird vom Animator aufgerufen (nur wenn "IK Pass" am Layer aktiv ist).
    private void OnAnimatorIK(int layerIndex)
    {
        Transform ziel = target;
        if (ziel == null && Camera.main != null)
            ziel = Camera.main.transform;

        if (ziel == null) return;

        weight = Mathf.MoveTowards(weight, targetWeight, blendSpeed * Time.deltaTime);

        animator.SetLookAtWeight(weight, bodyWeight, headWeight, eyesWeight, clampWeight);
        animator.SetLookAtPosition(ziel.position);
    }
}
