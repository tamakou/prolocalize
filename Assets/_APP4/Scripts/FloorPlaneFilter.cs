using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

/// <summary>
/// AR Foundation 6 対応の「床だけ表示」フィルタ
/// - ARPlaneManager.trackablesChanged で追加/更新を監視
/// - ARPlane.classifications のフラグに Floor が含まれるものだけ表示
/// </summary>
[RequireComponent(typeof(ARPlaneManager))]
public class FloorPlaneFilter : MonoBehaviour
{
  [SerializeField] private Material floorMaterial; // 床色（任意）
  [SerializeField] private bool hideNonFloor = true;

  private ARPlaneManager pm;

  private void Awake() => pm = GetComponent<ARPlaneManager>();

  private void OnEnable()
  {
    // AR Foundation 6: trackablesChanged (UnityEvent)
    pm.trackablesChanged.AddListener(OnPlanesChanged);
  }

  private void OnDisable()
  {
    pm.trackablesChanged.RemoveListener(OnPlanesChanged);
  }

  public void OnPlanesChanged(ARTrackablesChangedEventArgs<ARPlane> args)
  {
    foreach (var p in args.added) Apply(p);
    foreach (var p in args.updated) Apply(p);
    // removed は不要
  }

  private void Apply(ARPlane plane)
  {
    // AR Foundation 6: PlaneClassifications は [Flags]
    bool isFloor = (plane.classifications & PlaneClassifications.Floor) != 0;

    if (hideNonFloor) plane.gameObject.SetActive(isFloor);

    if (isFloor && floorMaterial)
    {
      var r = plane.GetComponent<MeshRenderer>();
      if (r) r.material = floorMaterial;
    }
  }
}
