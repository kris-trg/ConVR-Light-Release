using System.Collections.Generic;
using UnityEngine;

// ============================================================================
//  NpcBlink
// ----------------------------------------------------------------------------
//  Lässt den NPC-Avatar in unregelmäßigen Abständen blinzeln. Ein Blinzeln
//  dauert etwa 0,2 Sekunden (schnell zu, etwas langsamer wieder auf),
//  gelegentlich folgt ein zweites direkt hinterher.
//
//  Zwei Varianten, genau wie beim Lip-Sync:
//
//    Variante A - Blendshapes:
//      Alle Blendshapes aus 'blinkBlendShapeNames', die am Gesichts-Mesh
//      gefunden werden, werden gemeinsam gesteuert (z.B. "eyesClosed" oder
//      das Paar "eyeBlinkLeft"/"eyeBlinkRight").
//
//    Variante B - Augenlid-Knochen (Microsoft Rocketbox):
//      Hat der Avatar keinen passenden Blendshape, werden Knochen gedreht,
//      deren Name einen der Teile aus 'lidNameParts' enthält. Bei Rocketbox
//      heißen sie z.B. "Bip01 REyeBlinkTop" (oberes Lid, rechtes Auge) und
//      vermutlich "Bip01 LEyeBlinkTop" / "...Bottom" für links und unten.
//      Erkannt wird automatisch:
//        - oben/unten: Name enthält "bottom" oder "lower" -> unteres Lid
//        - links/rechts: Name enthält "leye" oder "left" -> linkes Auge
//      Jede Gruppe hat ihre eigene Drehung (siehe unten), weil die Achsen
//      beim linken und rechten Lid oft gespiegelt sind.
//
//  Einstellen der Drehung (Variante B):
//    Im Play-Modus den Avatar auswählen, an NpcBlink 'Hold Closed For Testing'
//    anhaken und die Euler-Werte so lange ändern, bis das Auge geschlossen ist.
//    Danach die Werte als Standardwerte in diese Datei übernehmen (die Komponente
//    wird zur Laufzeit angehängt, Inspector-Änderungen gehen sonst verloren).
//
//  Einbindung:
//    Wird vom AIConversationManager automatisch am Avatar angehängt
//    (SetupAvatarComponents). Eine Init()-Methode ist nicht nötig, die Suche
//    läuft in Start().
// ============================================================================
public class NpcBlink : MonoBehaviour
{
    [Header("Variante A: Blendshapes")]
    [Tooltip("Das Gesichts-Mesh. Wenn leer, wird es unter dem Avatar gesucht.")]
    [SerializeField] private SkinnedMeshRenderer faceRenderer;

    [Tooltip("Mögliche Namen von Blinzel-Blendshapes. Alle gefundenen werden gemeinsam gesteuert.")]
    [SerializeField]
    private string[] blinkBlendShapeNames =
        { "eyesClosed", "eyeBlinkLeft", "eyeBlinkRight", "eyeBlink_L", "eyeBlink_R", "Blink", "blink" };

    [Tooltip("Gewicht des Blendshapes bei geschlossenem Auge (0-100).")]
    [Range(0f, 100f)]
    [SerializeField] private float maxWeight = 100f;

    [Header("Variante B: Augenlid-Knochen")]
    [Tooltip("Namensteile (kleingeschrieben), an denen Lid-Knochen erkannt werden. " +
             "Rocketbox: 'eyeblink' passt auf 'Bip01 REyeBlinkTop'.")]
    [SerializeField] private string[] lidNameParts = { "eyeblink", "eyelid" };

    [Tooltip("Lid-Knochen von Hand vorgeben. Leer = automatische Suche über 'lidNameParts'.")]
    [SerializeField] private Transform[] eyelidBones;

    [Tooltip("Drehung (Grad, lokale Euler-Winkel) der OBEREN Lider des RECHTEN Auges bei geschlossenem Auge.")]
    [SerializeField] private Vector3 topLidEulerRight = new Vector3(0f, 0f, 20f);

    [Tooltip("Drehung der OBEREN Lider des LINKEN Auges. Oft gespiegelt zum rechten (Vorzeichen prüfen).")]
    [SerializeField] private Vector3 topLidEulerLeft = new Vector3(0f, 0f, 20f);

    [Tooltip("Drehung der UNTEREN Lider. Null = untere Lider bleiben ruhig (für ein Blinzeln meist ausreichend).")]
    [SerializeField] private Vector3 bottomLidEuler = Vector3.zero;

    [Tooltip("Nur zum Einstellen: hält die Augen dauerhaft geschlossen.")]
    [SerializeField] private bool holdClosedForTesting = false;

    [Header("Timing")]
    [Tooltip("Pause zwischen zwei Blinzeln in Sekunden (zufällig zwischen Minimum und Maximum).")]
    [SerializeField] private Vector2 intervalRange = new Vector2(2.5f, 6f);

    [Tooltip("Dauer des Schließens in Sekunden.")]
    [SerializeField] private float closeTime = 0.06f;

    [Tooltip("Dauer des Öffnens in Sekunden.")]
    [SerializeField] private float openTime = 0.12f;

    [Tooltip("Wahrscheinlichkeit für ein zweites Blinzeln direkt nach dem ersten.")]
    [Range(0f, 1f)]
    [SerializeField] private float doubleBlinkChance = 0.15f;

    // Ein Lid-Knochen samt Ruhelage und Zuordnung.
    private class LidBone
    {
        public Transform bone;
        public Quaternion rest;
        public bool bottom;
        public bool left;
    }

    private readonly List<int> shapeIndices = new();
    private readonly List<LidBone> lids = new();
    private float nextBlinkAt;
    private float blinkTimer = -1f;   // < 0: gerade kein Blinzeln
    private bool resolved;

    private bool HasBlendShapes => shapeIndices.Count > 0 && faceRenderer != null;
    private bool HasBones => lids.Count > 0;

    private void Start()
    {
        Resolve();
        ScheduleNext(false);
    }

    private void Resolve()
    {
        if (resolved) return;
        resolved = true;

        ResolveBlendShapes();
        if (HasBlendShapes) return;

        ResolveBones();

        if (!HasBones)
            Debug.LogWarning("[Blink] Weder Blinzel-Blendshapes noch Augenlid-Knochen gefunden. " +
                             "'blinkBlendShapeNames', 'lidNameParts' bzw. 'eyelidBones' prüfen.", this);
    }

    // Sucht das erste Mesh, das mindestens einen der Blendshape-Namen enthält.
    private void ResolveBlendShapes()
    {
        shapeIndices.Clear();

        SkinnedMeshRenderer[] kandidaten = faceRenderer != null
            ? new[] { faceRenderer }
            : GetComponentsInChildren<SkinnedMeshRenderer>(true);

        foreach (var smr in kandidaten)
        {
            if (smr == null || smr.sharedMesh == null) continue;

            foreach (var name in blinkBlendShapeNames)
            {
                int index = smr.sharedMesh.GetBlendShapeIndex(name);
                if (index >= 0 && !shapeIndices.Contains(index))
                    shapeIndices.Add(index);
            }

            if (shapeIndices.Count > 0)
            {
                faceRenderer = smr;
                Debug.Log($"[Blink] {shapeIndices.Count} Blendshape(s) an '{smr.name}' gefunden.");
                return;
            }
        }
    }

    // Sucht Lid-Knochen, ordnet sie oben/unten und links/rechts zu und merkt die Ruhelage.
    private void ResolveBones()
    {
        lids.Clear();

        var kandidaten = new List<Transform>();

        if (eyelidBones != null && eyelidBones.Length > 0)
        {
            kandidaten.AddRange(eyelidBones);
        }
        else
        {
            foreach (var t in GetComponentsInChildren<Transform>(true))
            {
                string n = t.name.ToLowerInvariant();
                foreach (var teil in lidNameParts)
                {
                    if (!string.IsNullOrEmpty(teil) && n.Contains(teil.ToLowerInvariant()))
                    {
                        kandidaten.Add(t);
                        break;
                    }
                }
            }
        }

        var beschreibung = new List<string>();

        foreach (var t in kandidaten)
        {
            if (t == null) continue;

            string n = t.name.ToLowerInvariant();
            var lid = new LidBone
            {
                bone = t,
                rest = t.localRotation,
                bottom = n.Contains("bottom") || n.Contains("lower"),
                left = n.Contains("leye") || n.Contains("left")
            };
            lids.Add(lid);

            beschreibung.Add($"{t.name} ({(lid.bottom ? "unten" : "oben")}, {(lid.left ? "links" : "rechts")})");
        }

        if (lids.Count > 0)
            Debug.Log($"[Blink] Augenlid-Knochen gefunden: {string.Join("; ", beschreibung)}");
    }

    // Legt fest, wann das nächste Blinzeln beginnt.
    private void ScheduleNext(bool erlaubeDoppelt)
    {
        if (erlaubeDoppelt && Random.value < doubleBlinkChance)
            nextBlinkAt = Time.time + 0.15f;
        else
            nextBlinkAt = Time.time + Random.Range(intervalRange.x, intervalRange.y);
    }

    // LateUpdate, damit der Animator die Werte nicht wieder überschreibt.
    private void LateUpdate()
    {
        if (!HasBlendShapes && !HasBones) return;

        float amount = 0f;

        if (holdClosedForTesting)
        {
            amount = 1f;
        }
        else
        {
            if (blinkTimer < 0f && Time.time >= nextBlinkAt)
                blinkTimer = 0f;

            if (blinkTimer >= 0f)
            {
                blinkTimer += Time.deltaTime;

                if (blinkTimer < closeTime)
                {
                    amount = blinkTimer / closeTime;
                }
                else if (blinkTimer < closeTime + openTime)
                {
                    amount = 1f - (blinkTimer - closeTime) / openTime;
                }
                else
                {
                    blinkTimer = -1f;
                    amount = 0f;
                    ScheduleNext(true);
                }
            }
        }

        Apply(amount);
    }

    private void Apply(float amount)
    {
        if (HasBlendShapes)
        {
            foreach (int index in shapeIndices)
                faceRenderer.SetBlendShapeWeight(index, amount * maxWeight);
        }
        else
        {
            foreach (var lid in lids)
            {
                if (lid.bone == null) continue;

                Vector3 euler = lid.bottom
                    ? bottomLidEuler
                    : (lid.left ? topLidEulerLeft : topLidEulerRight);

                // Absolut zur Ruhelage setzen, damit sich die Drehung nicht aufsummiert.
                lid.bone.localRotation = lid.rest * Quaternion.Euler(euler * amount);
            }
        }
    }
}