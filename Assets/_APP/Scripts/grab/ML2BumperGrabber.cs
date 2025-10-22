// Assets/_APP/Scripts/Ml2BumperGrabber.cs
using System.Linq;
using UnityEngine;
using UnityEngine.InputSystem;

public class Ml2BumperGrabber : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] Transform hand;                     // ML Rig ‚Ì Controller ‚È‚Ç
    [SerializeField] InputActionReference bumperAction;  // Input System ‚Ì ActioniButtonj
    [Header("Grab Settings")]
    [SerializeField] float grabRadius = 0.12f;
    [SerializeField] LayerMask grabbableMask = ~0;

    Ml2NetworkGrabbable _current;

    void OnEnable()
    {
        if (bumperAction && bumperAction.action != null)
        {
            bumperAction.action.performed += OnBumper;
            bumperAction.action.Enable();
        }
    }

    void OnDisable()
    {
        if (bumperAction && bumperAction.action != null)
        {
            bumperAction.action.performed -= OnBumper;
            bumperAction.action.Disable();
        }
    }

    void OnBumper(InputAction.CallbackContext _)
    {
        if (hand == null) hand = transform;

        if (_current != null)
        {
            _current.RequestRelease();
            _current = null;
            return;
        }

        // ‹ß–T‚Ì’Í‚ß‚é‘ÎÛ‚ð’Tõ
        var hits = Physics.OverlapSphere(hand.position, grabRadius, grabbableMask, QueryTriggerInteraction.Collide);
        var target = hits
            .Select(h => h.GetComponentInParent<Ml2NetworkGrabbable>())
            .Where(g => g != null)
            .OrderBy(g => Vector3.Distance(g.transform.position, hand.position))
            .FirstOrDefault();

        if (target != null)
        {
            _current = target;
            _current.RequestGrab(hand);
        }
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (hand == null) hand = transform;
        Gizmos.color = new Color(0, 1, 1, 0.25f);
        Gizmos.DrawSphere(hand.position, grabRadius);
    }
#endif
}
