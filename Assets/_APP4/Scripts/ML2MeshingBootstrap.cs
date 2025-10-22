using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using MagicLeap.Android; // Permissions
using MagicLeap.OpenXR.Features.Meshing; // MagicLeapMeshingFeature
using Fusion; // NetworkRunner ロール判定

/// <summary>
/// ML2 のメッシング／プレーン検出ブートストラップ（ホスト限定）
/// - Awake/Start でまず強制OFF
/// - NetworkRunner のロール確定後、ホストのみ起動
/// - ゲストは常に ARPlane/ARMesh を OFF のまま維持
/// - シーン上の外部メッシングRoot(任意)もゲストでは強制OFF
/// </summary>
[DefaultExecutionOrder(-500)]
[DisallowMultipleComponent]
public class ML2MeshingBootstrap : MonoBehaviour
{
  [Header("References")]
  [SerializeField] private ARMeshManager meshManager;     // 環境メッシュ
  [SerializeField] private ARPlaneManager planeManager;   // 平面（床）
  [SerializeField] private Transform meshingVolume;       // メッシュ境界(中心/回転/スケール)

  [Header("Meshing Bounds (meters)")]
  [SerializeField][Tooltip("XZの幅(m)")] private float boundsSizeXZ = 8f;
  [SerializeField][Tooltip("高さ(m)")] private float boundsHeight = 4f;
  [SerializeField][Range(0.05f, 1f)][Tooltip("メッシュ密度(0〜1)")] private float meshDensity = 0.30f;
  [SerializeField][Tooltip("ヘッド位置に境界を追従")] private bool followHead = true;

  [Header("Stop/Resume options")]
  [Tooltip("Stop時に ARPlaneManager を無効化")] public bool disablePlaneManagerOnStop = true;
  [Tooltip("Stop時に既存平面を非表示")] public bool hideAllPlanesOnStop = true;
  [Tooltip("Stop時に ARMeshManager を無効化")] public bool disableMeshManagerOnStop = true;
  [Tooltip("Stop時に既存メッシュを破棄")] public bool destroyAllMeshesOnStop = true;

  [Header("Networking Gating (Fusion)")]
  [Tooltip("ホストのみマッピング(平面/メッシュ)を起動する")] public bool hostBuildsMapping = true;
  [Tooltip("Runner未検出/未起動はホスト扱い（開発用）。本番は false 推奨")] public bool treatNoRunnerAsHost = false;

  [Header("External Meshing (optional)")]
  [Tooltip("Magic Leap サンプル等の外部メッシングRoot (例: \"Meshing\" GO)。ホスト時のみON、ゲスト常時OFF")]
  public GameObject externalMeshingRoot;

  private MagicLeapMeshingFeature _meshingFeature;
  private NetworkRunner _runner;
  private bool _mappingStarted;

  private void Reset()
  {
    meshManager = FindFirstObjectByType<ARMeshManager>();
    planeManager = FindFirstObjectByType<ARPlaneManager>();
  }

  private void Awake()
  {
    // 最速で確実にOFF（インスペクタONでも強制OFF）
    if (meshManager) meshManager.enabled = false;
    if (planeManager) planeManager.enabled = false;
    if (externalMeshingRoot) externalMeshingRoot.SetActive(false);
  }

  private IEnumerator Start()
  {
    // メッシング体積の準備
    if (!meshingVolume)
    {
      var go = new GameObject("MeshingVolume");
      go.transform.SetParent(transform, false);
      meshingVolume = go.transform;
    }

    // XR Mesh Subsystem 起動待ち
    yield return WaitForXRMeshSubsystem();

    // MagicLeap Meshing Feature 取得
    _meshingFeature = OpenXRSettings.Instance?.GetFeature<MagicLeapMeshingFeature>();
    if (_meshingFeature == null || !_meshingFeature.enabled)
    {
      Debug.LogError("[MeshingBootstrap] MagicLeapMeshingFeature が無効です。OpenXR設定を確認してください。");
      yield break;
    }

    // Runner 起動/ロール確定待ち（最大10秒）
    yield return WaitRunner(10f);

    // 最初の判定適用
    ApplyRoleGating(initial: true);

    // 直後数秒はロール変化に追従（MasterClient確定の遅延対策）
    float end = Time.time + 5f;
    while (Time.time < end)
    {
      ApplyRoleGating(initial: false);
      yield return null;
    }
  }

  private IEnumerator WaitForXRMeshSubsystem()
  {
    var list = new List<XRMeshSubsystem>();
    do
    {
      SubsystemManager.GetSubsystems(list);
      yield return null;
    }
    while (list.Count == 0 || !list[0].running);
  }

  private IEnumerator WaitRunner(float upToSeconds)
  {
    float t = 0f;
    while (t < upToSeconds)
    {
      _runner = FindFirstObjectByType<NetworkRunner>();
      if (_runner && _runner.IsRunning) yield break;
      t += Time.deltaTime;
      yield return null;
    }
    _runner = null;
  }

  private bool IsHost()
  {
    if (_runner == null || !_runner.IsRunning) return treatNoRunnerAsHost;
    if (_runner.IsServer) return true;
#if FUSION_2_OR_NEWER
    return _runner.IsSharedModeMasterClient;
#else
    return _runner.IsSharedModeMasterClient;
#endif
  }

  private void ApplyRoleGating(bool initial)
  {
    bool host = IsHost();

    if (hostBuildsMapping && host)
    {
      // ホスト → 起動（未起動なら）
      if (!_mappingStarted)
      {
        RequestPermissionAndStart();
        if (externalMeshingRoot) externalMeshingRoot.SetActive(true);
        _mappingStarted = true;
        Debug.Log("[MeshingBootstrap] Host detected -> mapping started.");
      }
    }
    else
    {
      // ゲスト or hostBuildsMapping=false → 常時OFF
      if (_mappingStarted)
      {
        StopMapping();
        _mappingStarted = false;
        Debug.Log("[MeshingBootstrap] Guest detected -> mapping stopped.");
      }
      if (planeManager) planeManager.enabled = false;
      if (meshManager) meshManager.enabled = false;
      if (externalMeshingRoot) externalMeshingRoot.SetActive(false);
    }
  }

  private void RequestPermissionAndStart()
  {
    Permissions.RequestPermission(
      Permissions.SpatialMapping,
      _ => { SetupAndStartMeshing(); SetupAndStartPlanes(); },
      p => Debug.LogError("[MeshingBootstrap] Permission denied: " + p),
      p => Debug.LogError("[MeshingBootstrap] Permission denied (don't ask again): " + p)
    );
  }

  private void SetupAndStartMeshing()
  {
    // ヘッド中心に境界配置
    var cam = Camera.main ? Camera.main.transform : null;
    if (cam) meshingVolume.position = cam.position;
    meshingVolume.rotation = Quaternion.identity;
    meshingVolume.localScale = new Vector3(boundsSizeXZ, boundsHeight, boundsSizeXZ);

    if (meshManager)
    {
      meshManager.density = meshDensity;
      meshManager.enabled = true;
    }

    // ML Meshing Feature に反映
    _meshingFeature.MeshRenderMode = MeshingMode.Triangles;
    _meshingFeature.MeshBoundsOrigin = meshingVolume.position;
    _meshingFeature.MeshBoundsRotation = meshingVolume.rotation;
    _meshingFeature.MeshBoundsScale = meshingVolume.localScale;
    _meshingFeature.MeshDensity = meshDensity;

    var q = MeshingQuerySettings.DefaultSettings();
    _meshingFeature.UpdateMeshQuerySettings(in q);
    _meshingFeature.InvalidateMeshes();
  }

  private void SetupAndStartPlanes()
  {
    if (!planeManager) return;
    planeManager.requestedDetectionMode = PlaneDetectionMode.Horizontal; // 床のみ
    planeManager.enabled = true;
  }

  private void LateUpdate()
  {
    if (!_mappingStarted || !followHead || _meshingFeature == null || !meshingVolume) return;
    var cam = Camera.main ? Camera.main.transform : null;
    if (!cam) return;
    meshingVolume.position = cam.position;
    _meshingFeature.MeshBoundsOrigin = meshingVolume.position;
  }

  /// <summary>環境メッシュ/平面の更新を停止し、生成済みを破棄/非表示にする</summary>
  public void StopMapping(bool? destroyMeshes = null, bool? hidePlanes = null)
  {
    if (planeManager)
    {
      if (hidePlanes ?? hideAllPlanesOnStop)
      {
        planeManager.SetTrackablesActive(false);
      }
      if (disablePlaneManagerOnStop) planeManager.enabled = false;
    }

    if (meshManager)
    {
      if (disableMeshManagerOnStop) meshManager.enabled = false;
      if (destroyMeshes ?? destroyAllMeshesOnStop) meshManager.DestroyAllMeshes();
    }
  }

  /// <summary>停止後に再開（※ホストのみ呼ぶ）</summary>
  public void ResumeMapping()
  {
    if (hostBuildsMapping && !IsHost()) return;
    if (meshManager) meshManager.enabled = true;
    if (planeManager) planeManager.enabled = true;
    _meshingFeature?.InvalidateMeshes();
  }
}
