using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.NativeTypes;

using MagicLeap.OpenXR.Features.LocalizationMaps;
using MagicLeap.OpenXR.Features.SpatialAnchors;
using MagicLeap.OpenXR.Subsystems;

public class MLAnchorsUiController : MonoBehaviour
{
    [Header("Scene refs")]
    [SerializeField] private ARAnchorManager anchorManager;   // XR Origin(AR) に付与
    [SerializeField] private Camera mainCamera;       // XR のメインカメラ
    [SerializeField] private GameObject controllerObject; // 右手コントローラ等（任意）
    [SerializeField] private GameObject anchorVisualPrefab; // 小さなCube等（見た目）

    [Header("UI refs (screenshotと対応)")]
    [SerializeField] private Dropdown mapsDropdown;           // 「マップ選択」
    [SerializeField] private Button localizeButton;         // 「Localize to Map」
    [SerializeField] private Button spawnAnchorButton;      // 「生成」
    [SerializeField] private Button publishButton;          // 「保存」
    [SerializeField] private Button queryButton;            // 「復元」
    [SerializeField] private Button deleteNearestButton;    // 「削除」
    [SerializeField] private Button deleteAllButton;        // 「全て削除」
    [SerializeField] private Text statusLabel;            // 任意：下部のステータス
    [SerializeField] private Text localizationLabel;      // 「Localization Status:」

    [Header("Spawn options")]
    [SerializeField, Tooltip("生成時の基準から前方(メートル)")]
    private float forwardMeters = 0.30f;

    // ML OpenXR features / subsystems
    private MagicLeapLocalizationMapFeature mapFeature;
    private MagicLeapSpatialAnchorsStorageFeature storageFeature;
    private MLXrAnchorSubsystem mlAnchorSubsystem;

    // マップ一覧
    private LocalizationMap[] mapList = Array.Empty<LocalizationMap>();

    // ローカル（未Publish）保持
    private readonly List<ARAnchor> localAnchors = new();
    // 既にStorage保存済みかの目印（TrackableId）
    private readonly HashSet<TrackableId> storedIds = new();

    // ---------- Unity lifecycle ----------
    private async void Start()
    {
        if (anchorManager == null) anchorManager = FindFirstObjectByType<ARAnchorManager>();
        if (mainCamera == null) mainCamera = Camera.main;

        await WaitSubsystems();

        // Feature を取得（OpenXR 設定で有効になっている必要あり）
        mapFeature = OpenXRSettings.Instance.GetFeature<MagicLeapLocalizationMapFeature>();
        storageFeature = OpenXRSettings.Instance.GetFeature<MagicLeapSpatialAnchorsStorageFeature>();
        if (mapFeature == null || storageFeature == null)
        {
            Log("OpenXR Features (LocalizationMap / SpatialAnchorsStorage) が有効ではありません。");
            enabled = false; return;
        }

        var loader = XRGeneralSettings.Instance.Manager.activeLoader;
        mlAnchorSubsystem = loader?.GetLoadedSubsystem<XRAnchorSubsystem>() as MLXrAnchorSubsystem;

        // AR Foundation 6: trackablesChanged を購読
        if (anchorManager != null)
            anchorManager.trackablesChanged.AddListener(OnTrackablesChanged);

        // Storage 側のコールバック
        storageFeature.OnPublishComplete += OnPublishComplete;
        storageFeature.OnQueryComplete += OnQueryComplete;
        storageFeature.OnCreationCompleteFromStorage += OnCreationCompleteFromStorage;
        storageFeature.OnDeletedComplete += OnAnchorDeleteComplete;

        // UI のイベント
        if (localizeButton) { localizeButton.onClick.RemoveAllListeners(); localizeButton.onClick.AddListener(LocalizeSelectedMap); }
        if (spawnAnchorButton) { spawnAnchorButton.onClick.RemoveAllListeners(); spawnAnchorButton.onClick.AddListener(SpawnLocalAnchor); }
        if (publishButton) { publishButton.onClick.RemoveAllListeners(); publishButton.onClick.AddListener(PublishTrackingLocals); }
        if (queryButton) { queryButton.onClick.RemoveAllListeners(); queryButton.onClick.AddListener(RestoreFromStorageNearby); }
        if (deleteNearestButton) { deleteNearestButton.onClick.RemoveAllListeners(); deleteNearestButton.onClick.AddListener(DeleteNearestStored); }
        if (deleteAllButton) { deleteAllButton.onClick.RemoveAllListeners(); deleteAllButton.onClick.AddListener(DeleteAllStored); }

        // マップ一覧の初期化
        InitMaps();
        UpdateLocalizationUi();
        if (publishButton) publishButton.interactable = false; // Localized 前はOFF
    }

    private void OnDestroy()
    {
        if (anchorManager != null)
            anchorManager.trackablesChanged.RemoveListener(OnTrackablesChanged);

        if (storageFeature != null)
        {
            storageFeature.OnPublishComplete -= OnPublishComplete;
            storageFeature.OnQueryComplete -= OnQueryComplete;
            storageFeature.OnCreationCompleteFromStorage -= OnCreationCompleteFromStorage;
            storageFeature.OnDeletedComplete -= OnAnchorDeleteComplete;
        }

        if (mapFeature != null)
            mapFeature.EnableLocalizationEvents(false);
    }

    private async Task WaitSubsystems()
    {
        while (XRGeneralSettings.Instance == null ||
               XRGeneralSettings.Instance.Manager == null ||
               XRGeneralSettings.Instance.Manager.activeLoader == null)
            await Task.Yield();
    }

    private void InitMaps()
    {
        // 端末内の Space 一覧を取得してドロップダウンに反映
        if (mapFeature.GetLocalizationMapsList(out mapList) == XrResult.Success && mapsDropdown)
        {
            mapsDropdown.ClearOptions();
            mapsDropdown.AddOptions(mapList.Select(m => m.Name).ToList());
        }
        var res = mapFeature.EnableLocalizationEvents(true);
        if (res != XrResult.Success) Log("EnableLocalizationEvents 失敗: " + res);
    }

    // ---------- Buttons ----------
    public void LocalizeSelectedMap()
    {
        if (mapList.Length == 0) { Log("ローカライズ対象の Map がありません"); return; }
        string uuid = mapList[Mathf.Clamp(mapsDropdown?.value ?? 0, 0, mapList.Length - 1)].MapUUID;

        var res = mapFeature.RequestMapLocalization(uuid);
        if (res != XrResult.Success) { Log("Localize 失敗: " + res); return; }

        // シーンの既存 Anchor 表示を掃除
        foreach (var a in FindObjectsByType<ARAnchor>(FindObjectsSortMode.None))
            Destroy(a.gameObject);
        localAnchors.Clear();
        storedIds.Clear();

        Log("ローカライズ要求: " + mapList[mapsDropdown?.value ?? 0].Name);
        UpdateLocalizationUi();
    }

    public void SpawnLocalAnchor()
    {
        // 生成源（右手 or カメラ）
        Transform src = (controllerObject != null) ? controllerObject.transform :
                        (mainCamera != null ? mainCamera.transform : null);
        if (src == null) { Log("生成元の Transform が未設定です"); return; }

        Pose p = new Pose(src.position + src.forward * forwardMeters,
                          Quaternion.LookRotation(Vector3.ProjectOnPlane(src.forward, Vector3.up), Vector3.up));

        // 任意の GameObject を置き、ARAnchor を付与（ARF の推奨作法）:contentReference[oaicite:4]{index=4}
        var root = new GameObject("ML2_Anchor");
        root.transform.SetPositionAndRotation(p.position, p.rotation);
        var anchor = root.AddComponent<ARAnchor>();
        localAnchors.Add(anchor);

        // 見た目（Prefab）を子に
        if (anchorVisualPrefab != null)
        {
            var vis = Instantiate(anchorVisualPrefab, anchor.transform);
            TintAll(vis, Color.grey); // 未保存は灰
        }

        Log("ローカルアンカーを生成（未保存）");
    }

    public void PublishTrackingLocals()
    {
        // Publish は Localized 状態が前提（例：未ローカライズだと失敗）:contentReference[oaicite:5]{index=5}
        mapFeature.GetLatestLocalizationMapData(out LocalizationEventData d);
        if (d.State != LocalizationMapState.Localized)
        {
            Log("Publish には Localized が必要です"); return;
        }

        var targets = localAnchors
            .Where(a => a != null && a.trackingState == TrackingState.Tracking && !a.pending)
            .ToList();

        if (targets.Count == 0) { Log("Publish 対象なし（Tracking中のローカルが必要）"); return; }

        // 期限なし（0 秒）で永続化。完了は OnPublishComplete。:contentReference[oaicite:6]{index=6}
        storageFeature.PublishSpatialAnchorsToStorage(targets, 0);
        Log($"Publish 要求: {targets.Count} 個");
    }

    public void RestoreFromStorageNearby()
    {
        if (storageFeature == null) { Log("Storage Feature が利用できません"); return; }

        Vector3 origin = (controllerObject != null) ? controllerObject.transform.position :
                         (mainCamera != null ? mainCamera.transform.position : Vector3.zero);

        // 近傍の保存済みアンカー ID を問い合わせ（例：半径10m）→ OnQueryComplete へ。:contentReference[oaicite:7]{index=7}
        if (!storageFeature.QueryStoredSpatialAnchors(origin, 10f))
            Log("Query 失敗");
        else
            Log("Query 要求（10m以内）");
    }

    public void DeleteNearestStored()
    {
        var a = FindClosestStoredAnchorNear(GetRefPosition(), 100f);
        if (a == null) { Log("近傍の保存済みアンカーが見つかりません"); return; }

        string id = mlAnchorSubsystem != null ? mlAnchorSubsystem.GetAnchorMapPositionId(a) : "";
        if (string.IsNullOrEmpty(id)) { Log("MapPositionId 取得失敗"); return; }

        storageFeature.DeleteStoredSpatialAnchors(new List<string> { id }); // 完了は OnAnchorDeleteComplete。:contentReference[oaicite:8]{index=8}
        Log("削除要求: " + id);
    }

    public void DeleteAllStored()
    {
        var ids = new List<string>();
        foreach (var a in anchorManager.trackables)
        {
            string id = mlAnchorSubsystem != null ? mlAnchorSubsystem.GetAnchorMapPositionId(a) : "";
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }
        if (ids.Count == 0) { Log("削除対象の保存済みアンカーがありません"); return; }

        storageFeature.DeleteStoredSpatialAnchors(ids);
        Log($"一括削除要求: {ids.Count} 件");
    }

    // ---------- Storage callbacks ----------
    private void OnPublishComplete(ulong anchorId, string anchorMapPositionId)
    {
        // Publish に成功した Anchor を「保存済み扱い」へ
        ARAnchor anchor = FindAnchorByMapPositionId(anchorMapPositionId);
        if (anchor == null && mlAnchorSubsystem != null)
        {
            // 近いローカルを拾う
            Pose p = mlAnchorSubsystem.GetAnchorPoseFromId(anchorId);
            anchor = FindClosestLocalAnchor(p.position, 0.05f);
        }

        if (anchor != null)
        {
            storedIds.Add(anchor.trackableId);
            localAnchors.Remove(anchor);
            TintAll(anchor.gameObject, Color.white); // 保存済み=白
            Log($"Publish 完了: {anchorMapPositionId}");
        }
        else
        {
            Log($"Publish 完了（Anchor未特定）: {anchorMapPositionId}");
        }
    }

    private void OnQueryComplete(List<string> anchorMapPositionIds)
    {
        if (anchorMapPositionIds == null || anchorMapPositionIds.Count == 0)
        {
            Log("Query: 見つかりませんでした"); return;
        }

        // すでにシーンに存在するものを除外
        var existing = new HashSet<string>();
        foreach (var a in anchorManager.trackables)
        {
            string id = mlAnchorSubsystem != null ? mlAnchorSubsystem.GetAnchorMapPositionId(a) : "";
            if (!string.IsNullOrEmpty(id)) existing.Add(id);
        }

        var toCreate = anchorMapPositionIds.Where(id => !existing.Contains(id)).ToList();
        if (toCreate.Count > 0)
        {
            // ID から Anchor を生成（復元）。生成結果は trackablesChanged/OnCreationComplete で拾う。:contentReference[oaicite:9]{index=9}
            if (!storageFeature.CreateSpatialAnchorsFromStorage(toCreate))
                Log("CreateSpatialAnchorsFromStorage 失敗");
            else
                Log($"復元要求: {toCreate.Count} 件");
        }
    }

    private void OnCreationCompleteFromStorage(Pose pose, ulong anchorId, string anchorMapPositionId, XrResult result)
    {
        if (result != XrResult.Success) { Log($"復元失敗: {result}"); return; }

        // 実体(ARAnchor)は trackablesChanged(added/updated) で来る
        Log($"復元通知: {anchorMapPositionId}");
    }

    private void OnAnchorDeleteComplete(List<string> deletedMapPositionIds)
    {
        // シーン上の該当 Anchor を削除
        foreach (var id in deletedMapPositionIds)
        {
            var a = FindAnchorByMapPositionId(id);
            if (a != null) Destroy(a.gameObject);
        }
        Log($"削除完了: {deletedMapPositionIds.Count} 件");
    }

    // ---------- ARF6: trackablesChanged ----------
    private void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARAnchor> changes)
    {
        // 追加：保存済みなら白、未保存なら灰
        foreach (var a in changes.added)
        {
            string mapPosId = mlAnchorSubsystem != null ? mlAnchorSubsystem.GetAnchorMapPositionId(a) : "";
            bool isStored = !string.IsNullOrEmpty(mapPosId);
            EnsureVisual(a, isStored);
            if (isStored) { storedIds.Add(a.trackableId); TintAll(a.gameObject, Color.white); }
            else { TintAll(a.gameObject, Color.grey); if (!localAnchors.Contains(a)) localAnchors.Add(a); }
        }

        // 更新：保存済みへ昇格していたら表示切替
        foreach (var a in changes.updated)
        {
            string mapPosId = mlAnchorSubsystem != null ? mlAnchorSubsystem.GetAnchorMapPositionId(a) : "";
            if (!string.IsNullOrEmpty(mapPosId) && !storedIds.Contains(a.trackableId))
            {
                storedIds.Add(a.trackableId);
                localAnchors.Remove(a);
                EnsureVisual(a, true);
                TintAll(a.gameObject, Color.white);
            }
        }

        // 削除：KeyValuePair<TrackableId, ARAnchor>（ARF6 仕様）:contentReference[oaicite:10]{index=10}
        foreach (var kv in changes.removed)
        {
            localAnchors.Remove(kv.Value);
            storedIds.Remove(kv.Key);
        }

        UpdateLocalizationUi();
    }

    // ---------- helpers ----------
    private void EnsureVisual(ARAnchor a, bool stored)
    {
        if (a == null || anchorVisualPrefab == null) return;
        if (a.GetComponentInChildren<MeshRenderer>() == null)
        {
            var vis = Instantiate(anchorVisualPrefab, a.transform);
            TintAll(vis, stored ? Color.white : Color.grey);
        }
    }
    private void TintAll(GameObject go, Color c)
    {
        foreach (var r in go.GetComponentsInChildren<MeshRenderer>()) r.material.color = c;
    }

    private ARAnchor FindAnchorByMapPositionId(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var a in anchorManager.trackables)
            if (mlAnchorSubsystem != null && mlAnchorSubsystem.GetAnchorMapPositionId(a) == id)
                return a;
        return null;
    }

    private ARAnchor FindClosestLocalAnchor(Vector3 pos, float maxDist)
    {
        ARAnchor best = null; float bestD = maxDist;
        foreach (var a in localAnchors)
        {
            if (a == null) continue;
            float d = Vector3.Distance(a.transform.position, pos);
            if (d <= bestD) { bestD = d; best = a; }
        }
        return best;
    }

    private ARAnchor FindClosestStoredAnchorNear(Vector3 pos, float maxDist)
    {
        ARAnchor best = null; float bestD = maxDist;
        foreach (var a in anchorManager.trackables)
        {
            string id = mlAnchorSubsystem != null ? mlAnchorSubsystem.GetAnchorMapPositionId(a) : "";
            if (string.IsNullOrEmpty(id)) continue; // 保存済みのみ
            float d = Vector3.Distance(a.transform.position, pos);
            if (d <= bestD) { bestD = d; best = a; }
        }
        return best;
    }

    private Vector3 GetRefPosition()
    {
        return (controllerObject != null) ? controllerObject.transform.position :
               (mainCamera != null ? mainCamera.transform.position : Vector3.zero);
    }

    private void UpdateLocalizationUi()
    {
        if (mapFeature == null) return;

        mapFeature.GetLatestLocalizationMapData(out LocalizationEventData d);
        if (localizationLabel)
        {
            string info = $"State:{d.State} Conf:{d.Confidence}";
            if (d.State == LocalizationMapState.Localized && d.Map.Name != null)
                info += $"  Name:{d.Map.Name}";
            localizationLabel.text = info;
        }
        if (publishButton) publishButton.interactable = (d.State == LocalizationMapState.Localized);
    }

    private void Log(string msg)
    {
        if (statusLabel) statusLabel.text = msg;
        Debug.Log("[MLAnchorsUiController] " + msg);
    }
}
