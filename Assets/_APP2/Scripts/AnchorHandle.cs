// Assets/_APP/Scripts/AnchorHandle.cs
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

[RequireComponent(typeof(XRGrabInteractable))]
public class AnchorHandle : MonoBehaviour
{
    public ARAnchor CurrentAnchor { get; private set; }

    XRGrabInteractable _grab;

    void Awake()
    {
        _grab = GetComponent<XRGrabInteractable>();
        _grab.selectExited.AddListener(OnSelectExited);
    }

    void OnDestroy()
    {
        if (_grab) _grab.selectExited.RemoveListener(OnSelectExited);
    }

    // ML2AnchorsControllerUnified.EnsureVisual() で親に付く
    void OnEnable()
    {
        if (CurrentAnchor == null)
            CurrentAnchor = GetComponentInParent<ARAnchor>();
    }

    public void Bind(ARAnchor anchor) => CurrentAnchor = anchor;

    async void OnSelectExited(SelectExitEventArgs _)
    {
        if (ML2AnchorsControllerUnified.Instance == null) return;
        // つかんで離した場所にアンカーを差し替え
        await ML2AnchorsControllerUnified.Instance.ReanchorThisHandleAsync(this);
    }
}
