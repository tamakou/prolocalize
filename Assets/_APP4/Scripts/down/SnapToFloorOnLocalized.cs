using System.Collections;
using UnityEngine;
using UnityEngine.UI;                      // Text
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.LocalizationMaps; // MagicLeapLocalizationMapFeature, LocalizationEventData

[DisallowMultipleComponent]
public class SnapToFloorOnLocalized : MonoBehaviour
{
  [Header("Targets")]
  public Transform objectToDrop;
  public Transform groundingPoint;
  public Transform spaceOrigin;

  [Header("Reset & Restore Policy")]
  public bool resetToSpaceOriginXZEachLocalization = true;
  public bool alignYawToSpaceOrigin = true;
  public bool restoreInitialLocalPoseAfterFirstSnap = true;
  public bool lockLocalYAfterFirstSnap = true;

  [Header("Raycast")]
  public float castHeight = 2.0f;
  public float padding = 0.005f;
  public float snapSpeed = 6f;
  public LayerMask groundMask = ~0;

  [Header("Mapping control")]
  public bool stopMappingAfterSnap = false;
  public bool destroyMeshesOnStop = false;
  public bool hidePlanesOnStop = false;
  public ML2MeshingBootstrap bootstrap;

  [Header("UI feedback")]
  public Text feedbackText;
  public string groundedMessage = "床に接地しました";
  public float feedbackSeconds = 5f;

  [Header("Localization Event")]
  public bool subscribeToMlLocalizationEvent = false;

  // === private ===
  private Coroutine snapRoutine;
  private bool hasInitialLocal;
  private Vector3 initialLocalPos;
  private Quaternion initialLocalRot;

  private bool hasSavedLocalY;
  private float savedLocalY; // Space原点に対する localPosition.y

  private MagicLeapLocalizationMapFeature _mlFeature;

  void Awake()
  {
    if (!_mlFeature) _mlFeature = OpenXRSettings.Instance?.GetFeature<MagicLeapLocalizationMapFeature>();
    if (!bootstrap) bootstrap = FindFirstObjectByType<ML2MeshingBootstrap>();
    if (!spaceOrigin)
    {
      var go = GameObject.Find("SpaceOrigin");
      if (go) spaceOrigin = go.transform;
    }
  }

  void OnEnable()
  {
    if (subscribeToMlLocalizationEvent)
      MagicLeapLocalizationMapFeature.OnLocalizationChangedEvent += OnLocalizationChanged;
  }

  void OnDisable()
  {
    if (subscribeToMlLocalizationEvent)
      MagicLeapLocalizationMapFeature.OnLocalizationChangedEvent -= OnLocalizationChanged;

    if (snapRoutine != null) { StopCoroutine(snapRoutine); snapRoutine = null; }
  }

  // 自動購読ON時のみ
  private void OnLocalizationChanged(LocalizationEventData ev)
  {
    if (ev.State != LocalizationMapState.Localized) return;
    RestartSnapRoutine(0.1f); // SpaceOrigin反映/平面の立ち上がり待ち
  }

  /// <summary>SpaceTestManager が SpaceOrigin を更新した直後に呼ぶ（推奨）</summary>
  public void TriggerSnapAfterSpaceOriginUpdate() => RestartSnapRoutine(0.0f);

  private void RestartSnapRoutine(float delay)
  {
    if (snapRoutine != null) { StopCoroutine(snapRoutine); snapRoutine = null; }
    snapRoutine = StartCoroutine(SnapAfterDelay(delay));
  }

  private IEnumerator SnapAfterDelay(float delay)
  {
    if (delay > 0f) yield return new WaitForSeconds(delay);
    if (!objectToDrop) yield break;

    // --- 1) SpaceOrigin が最新の MapOrigin へ反映済みか保証 ---
    yield return EnsureSpaceOriginIsUpToDate(1.0f);

    // --- 2) 原点基準に正規化（XZ/ Yaw 揃え or 初回ローカル姿勢復元）---
    NormalizePoseRelativeToSpaceOrigin();

    // --- 3) 必要ならマッピングを一時再開 ---
    bool resumedMapping = false;
    bool shouldManage = stopMappingAfterSnap && bootstrap != null;
    if (shouldManage)
    {
      bootstrap.ResumeMapping(); // ゲストの場合は内部で早期 return
      resumedMapping = true;
    }

    // --- 4) 近傍に床コライダーが立ち上がるのを最大3秒待つ ---
    var timeout = Time.time + 3f;
    while (Time.time < timeout)
    {
      if (HasGroundCollidersNearby(objectToDrop.position, 5f)) break;
      yield return null;
    }

    // --- 5) 目標Y ---
    float startY = objectToDrop.position.y;
    float targetY;

    if (lockLocalYAfterFirstSnap && hasSavedLocalY && spaceOrigin)
    {
      var worldFromSaved = spaceOrigin.TransformPoint(new Vector3(0f, savedLocalY, 0f));
      targetY = worldFromSaved.y;
    }
    else
    {
      if (!TryGetRaycastTargetY(out targetY))
      {
        if (resumedMapping) Debug.LogWarning("[SnapToFloor] 床ヒットなし。メッシングを継続します。");
        yield break;
      }
    }

    float duration = Mathf.Clamp(Mathf.Abs(startY - targetY) / Mathf.Max(0.01f, snapSpeed), 0.05f, 0.6f);
    yield return AnimateY(startY, targetY, duration);

    // --- 6) 初回だけローカル姿勢 / ローカルY 保存 ---
    if (spaceOrigin)
    {
      objectToDrop.SetParent(spaceOrigin, true); // 以降はSpaceOrigin基準で追従
      if (!hasInitialLocal && restoreInitialLocalPoseAfterFirstSnap)
      {
        initialLocalPos = objectToDrop.localPosition;
        initialLocalRot = objectToDrop.localRotation;
        hasInitialLocal = true;
      }
      if (!hasSavedLocalY && lockLocalYAfterFirstSnap)
      {
        savedLocalY = objectToDrop.localPosition.y;
        hasSavedLocalY = true;
      }
    }

    // --- 7) マッピング停止（必要に応じて）---
    if (resumedMapping) bootstrap.StopMapping(destroyMeshesOnStop, hidePlanesOnStop);

    // --- 8) トースト ---
    if (feedbackText && feedbackSeconds > 0f) StartCoroutine(Toast(feedbackText, groundedMessage, feedbackSeconds));
  }

  /// <summary>
  /// GetMapOrigin() を参照し、SpaceOrigin が最新ポーズに同期していなければここで同期する
  /// </summary>
  private IEnumerator EnsureSpaceOriginIsUpToDate(float maxWaitSeconds)
  {
    if (_mlFeature == null || spaceOrigin == null) yield break;

    float end = Time.time + maxWaitSeconds;
    while (Time.time < end)
    {
      var origin = _mlFeature.GetMapOrigin();
      float posDiff = Vector3.Distance(spaceOrigin.position, origin.position);
      float yawDiff = Mathf.Abs(Mathf.DeltaAngle(spaceOrigin.rotation.eulerAngles.y, origin.rotation.eulerAngles.y));

      if (posDiff < 0.01f && yawDiff < 1f)
        yield break; // ほぼ一致

      // 未反映 → ここで同期
      spaceOrigin.SetPositionAndRotation(origin.position, origin.rotation);
      yield return null; // 次フレームで再確認
    }
  }

  private void NormalizePoseRelativeToSpaceOrigin()
  {
    if (!objectToDrop || !spaceOrigin) return;

    if (restoreInitialLocalPoseAfterFirstSnap && hasInitialLocal)
    {
      objectToDrop.SetParent(spaceOrigin, false);
      objectToDrop.localPosition = initialLocalPos;
      objectToDrop.localRotation = initialLocalRot;
      return;
    }

    if (resetToSpaceOriginXZEachLocalization)
    {
      var p = objectToDrop.position;
      p.x = spaceOrigin.position.x;
      p.z = spaceOrigin.position.z;
      objectToDrop.position = p;

      if (alignYawToSpaceOrigin)
      {
        var e = objectToDrop.rotation.eulerAngles;
        e.y = spaceOrigin.rotation.eulerAngles.y;
        objectToDrop.rotation = Quaternion.Euler(e);
      }
    }

    objectToDrop.SetParent(spaceOrigin, true);
  }

  private IEnumerator Toast(Text label, string msg, float seconds)
  {
    string prev = label.text;
    label.text = msg;
    label.enabled = true;
    yield return new WaitForSeconds(seconds);
    if (label) label.text = prev;
  }

  private bool HasGroundCollidersNearby(Vector3 center, float radius)
  {
    var hits = Physics.OverlapSphere(center, radius, groundMask, QueryTriggerInteraction.Ignore);
    foreach (var h in hits)
    {
      if (!h) continue;
      if (h.GetComponentInParent<ARPlane>() != null) return true;
      if (h is MeshCollider && h.GetComponentInParent<MeshFilter>() != null) return true;
    }
    return false;
  }

  private bool TryGetRaycastTargetY(out float targetY)
  {
    float bottomOffset = GetBottomOffset(objectToDrop, groundingPoint);
    Vector3 origin = objectToDrop.position + Vector3.up * castHeight;

    var hits = Physics.RaycastAll(origin, Vector3.down, castHeight * 3f, groundMask, QueryTriggerInteraction.Ignore);
    if (hits == null || hits.Length == 0)
    {
      Debug.LogWarning("[SnapToFloor] Raycast miss. Mesh/PlaneのColliderを確認してください。");
      targetY = 0f;
      return false;
    }

    System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

    foreach (var h in hits)
    {
      if (!h.collider) continue;
      if (objectToDrop && h.collider.transform.IsChildOf(objectToDrop)) continue; // 自分の子孫は除外
      targetY = h.point.y + bottomOffset + padding;
      return true;
    }

    targetY = 0f;
    return false;
  }

  private IEnumerator AnimateY(float startY, float targetY, float duration)
  {
    float t = 0f;
    var pos = objectToDrop.position;

    while (t < 1f)
    {
      t += Time.deltaTime / Mathf.Max(0.0001f, duration);
      pos.y = Mathf.Lerp(startY, targetY, Mathf.SmoothStep(0f, 1f, t));
      objectToDrop.position = pos;
      yield return null;
    }
    pos.y = targetY;
    objectToDrop.position = pos;
  }

  private static float GetBottomOffset(Transform target, Transform groundingPoint)
  {
    if (!target) return 0f;
    if (groundingPoint) return target.position.y - groundingPoint.position.y;

    var renderers = target.GetComponentsInChildren<Renderer>();
    if (renderers.Length == 0) return 0f;

    Bounds b = renderers[0].bounds;
    for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
    return target.position.y - b.min.y;
  }
}

