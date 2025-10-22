using System.Collections;
using UnityEngine;
using UnityEngine.UI; // Text
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using MagicLeap.OpenXR.Features.LocalizationMaps; // MagicLeapLocalizationMapFeature, LocalizationEventData

[DisallowMultipleComponent]
public class SnapToFloorOnLocalized : MonoBehaviour
{
  [Header("Targets")]
  [Tooltip("床に合わせて下げる対象（原点キューブ等の親Transform）")]
  public Transform objectToDrop;

  [Tooltip("任意：ここ（例: 底面マーカー）を床に接地させたい場合に指定")]
  public Transform groundingPoint;

  [Tooltip("Space 原点。未設定なら 'SpaceOrigin' を自動探索")]
  public Transform spaceOrigin;

  [Header("Reset & Restore Policy")]
  [Tooltip("ローカライズ毎に XZ を Space原点へ戻す")]
  public bool resetToSpaceOriginXZEachLocalization = true;

  [Tooltip("Space原点の Yaw（水平方向）に回転を合わせる")]
  public bool alignYawToSpaceOrigin = true;

  [Tooltip("初回接地時のローカル姿勢（Space原点基準）を保存し、以降復元してから接地")]
  public bool restoreInitialLocalPoseAfterFirstSnap = true;

  [Tooltip("初回で決めた『ローカルY（=床高さ）』をロックして再利用（プレーンゆらぎ無視）")]
  public bool lockLocalYAfterFirstSnap = true;

  [Header("Raycast")]
  [Tooltip("レイの開始高さ（対象の真上から下へ飛ばす）")]
  public float castHeight = 2.0f;

  [Tooltip("めり込み防止の隙間(m)")]
  public float padding = 0.005f;

  [Tooltip("下げるアニメ速度(m/s)")]
  public float snapSpeed = 6f;

  [Tooltip("床として判定するレイヤーマスク（未設定なら全レイヤー）")]
  public LayerMask groundMask = ~0;

  [Header("Mapping control")]
  [Tooltip("接地完了後にメッシング/平面検出を停止する（安定優先で既定はOFF）")]
  public bool stopMappingAfterSnap = false;

  [Tooltip("停止時に既存の環境メッシュを破棄する")]
  public bool destroyMeshesOnStop = false;

  [Tooltip("停止時に既存プレーンを非表示にする")]
  public bool hidePlanesOnStop = false;

  [Tooltip("（任意）明示参照。未設定なら自動検索")]
  public ML2MeshingBootstrap bootstrap;

  [Header("UI feedback")]
  [Tooltip("『床に接地しました』の表示先（任意）")]
  public Text feedbackText;

  public string groundedMessage = "床に接地しました";
  public float feedbackSeconds = 5f;

  [Header("Localization Event")]
  [Tooltip("ML のローカライゼーションイベントに自動で反応する（最適化のため既定OFF）")]
  public bool subscribeToMlLocalizationEvent = false;

  // === private ===
  private Coroutine snapRoutine;
  private bool hasInitialLocal;
  private Vector3 initialLocalPos;
  private Quaternion initialLocalRot;

  private bool hasSavedLocalY;
  private float savedLocalY; // Space原点に対する objectToDrop.localPosition.y

  void Awake()
  {
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

  // （自動購読をONにした場合のみ）Localizedで呼ばれる
  private void OnLocalizationChanged(LocalizationEventData ev)
  {
    if (ev.State != LocalizationMapState.Localized) return;
    RestartSnapRoutine(0.25f); // SpaceOrigin反映と平面立ち上がり待ち
  }

  /// <summary>SpaceTestManager が Space原点を更新した直後に呼ぶ（推奨経路）</summary>
  public void TriggerSnapAfterSpaceOriginUpdate()
  {
    if (!spaceOrigin)
    {
      var go = GameObject.Find("SpaceOrigin");
      if (go) spaceOrigin = go.transform;
    }
    RestartSnapRoutine(0.1f);
  }

  private void RestartSnapRoutine(float delay)
  {
    if (snapRoutine != null) { StopCoroutine(snapRoutine); snapRoutine = null; }
    snapRoutine = StartCoroutine(SnapAfterDelay(delay));
  }

  private IEnumerator SnapAfterDelay(float delay)
  {
    if (delay > 0f) yield return new WaitForSeconds(delay);
    if (!objectToDrop) yield break;

    // ===== 原点基準への正規化（姿勢復元 or XZ/Yaw 揃え）=====
    NormalizePoseRelativeToSpaceOrigin();

    // ===== 1) マッピング再開（ホストのみ内部ゲート）=====
    bool resumedMapping = false;
    bool shouldManage = stopMappingAfterSnap && bootstrap != null;
    if (shouldManage)
    {
      bootstrap.ResumeMapping(); // ゲストなら内部で早期return
      resumedMapping = true;
    }

    // ===== 2) 近傍に床コライダーが立ち上がるのを少し待つ（初回想定）=====
    var timeout = Time.time + 3f;
    while (Time.time < timeout)
    {
      if (HasGroundCollidersNearby(objectToDrop.position, 5f)) break;
      yield return null;
    }

    // ===== 3) 目標Yの決定 =====
    float startY = objectToDrop.position.y;
    float targetY;

    if (lockLocalYAfterFirstSnap && hasSavedLocalY && spaceOrigin)
    {
      // 保存した「ローカルY」を使用してワールドYを決定（床プレーンのゆらぎを無視）
      var worldFromSaved = spaceOrigin.TransformPoint(new Vector3(0f, savedLocalY, 0f));
      targetY = worldFromSaved.y;
    }
    else
    {
      // まだ保存がなければ Raycast で床にスナップ
      if (!TryGetRaycastTargetY(out targetY))
      {
        if (resumedMapping) Debug.LogWarning("[SnapToFloor] 床ヒットなし。メッシングを継続します。");
        yield break;
      }
    }

    float duration = Mathf.Clamp(Mathf.Abs(startY - targetY) / Mathf.Max(0.01f, snapSpeed), 0.05f, 0.6f);
    yield return AnimateY(startY, targetY, duration);

    // ===== 4) 初回だけローカル姿勢 & ローカルY を保存 =====
    if (spaceOrigin)
    {
      objectToDrop.SetParent(spaceOrigin, true); // 今後ローカルで扱う
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

    // ===== 5) マッピング停止（必要時）=====
    if (resumedMapping) bootstrap.StopMapping(destroyMeshesOnStop, hidePlanesOnStop);

    // ===== 6) UI トースト =====
    if (feedbackText && feedbackSeconds > 0f) StartCoroutine(Toast(feedbackText, "床に接地しました", feedbackSeconds));
  }

  private void NormalizePoseRelativeToSpaceOrigin()
  {
    if (!objectToDrop || !spaceOrigin) return;

    // 保存済みなら「初回と同じローカル姿勢」に復元
    if (restoreInitialLocalPoseAfterFirstSnap && hasInitialLocal)
    {
      objectToDrop.SetParent(spaceOrigin, false);
      objectToDrop.localPosition = initialLocalPos;
      objectToDrop.localRotation = initialLocalRot;
      return;
    }

    // まだ初回前：XZ を原点へ、Yaw を合わせてからスナップ
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

    // 将来のローカル保存に備えて一旦親子化（world維持）
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

  // groundingPoint があればその点を床に接地、なければRenderer群の最下端を使う
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
