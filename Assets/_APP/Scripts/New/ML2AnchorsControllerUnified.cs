// Assets/_APP/Scripts/ML2AnchorsControllerUnified.cs
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

public class ML2AnchorsControllerUnified : MonoBehaviour
{
    [Header("Scene refs")]
    [SerializeField] ARAnchorManager anchorManager;
    [SerializeField] Camera mainCamera;
    [SerializeField] Transform rightController;               // 任意（未設定ならカメラ基準）
    [SerializeField] GameObject anchorVisualPrefab;           // 小Cube等（見た目）

    [Header("UI refs")]
    [SerializeField] Dropdown mapsDropdown;
    [SerializeField] Button localizeButton, spawnButton, publishButton, queryButton, deleteNearestButton, deleteAllButton;
    [SerializeField] Text statusLabel, localizationLabel;

    [Header("Options")]
    [SerializeField, Tooltip("生成位置の前方オフセット(m)")] float forwardMeters = 0.30f;
    [SerializeField, Tooltip("復元クエリ半径(m)")] float restoreRadiusMeters = 10f;
    [SerializeField, Tooltip("ネット受信時に自動でPublishする")] bool autoPublishOnNetworkReceive = true;
    [SerializeField, Tooltip("重複生成の判定距離(m)")] float duplicateEpsilonMeters = 0.05f;

    // ML OpenXR
    MagicLeapLocalizationMapFeature mapFeature;
    MagicLeapSpatialAnchorsStorageFeature storageFeature;
    MLXrAnchorSubsystem mlAnchorSubsystem;

    // 状態
    LocalizationMap[] maps = Array.Empty<LocalizationMap>();
    readonly List<ARAnchor> localAnchors = new();       // 未保存
    readonly HashSet<TrackableId> storedIds = new();    // 保存済み
    readonly HashSet<string> savedMapPositionIds = new(); // PlayerPrefs と同期

    const string SavedIdListKey = "ML2_SavedAnchorMapPositionIds";
    [Serializable] class IdList { public List<string> ids = new(); }
    public static ML2AnchorsControllerUnified Instance { get; private set; }

    void Awake()
    {
        Instance = this;                   // ← 追加
        if (!anchorManager) anchorManager = FindFirstObjectByType<ARAnchorManager>();
        if (!mainCamera) mainCamera = Camera.main;
    }

    async void Start()
    {
        await WaitSubsystems();

        mapFeature = OpenXRSettings.Instance.GetFeature<MagicLeapLocalizationMapFeature>();
        storageFeature = OpenXRSettings.Instance.GetFeature<MagicLeapSpatialAnchorsStorageFeature>();

        if (mapFeature == null || storageFeature == null)
        {
            Log("OpenXR Features (LocalizationMap / SpatialAnchorsStorage) が無効です。");
            enabled = false; return;
        }

        var loader = XRGeneralSettings.Instance.Manager.activeLoader;
        mlAnchorSubsystem = loader?.GetLoadedSubsystem<XRAnchorSubsystem>() as MLXrAnchorSubsystem;

        // ARF6: trackablesChanged
        anchorManager.trackablesChanged.AddListener(OnTrackablesChanged);

        // Storage callbacks
        storageFeature.OnPublishComplete += OnPublishComplete;
        storageFeature.OnQueryComplete += OnQueryComplete;
        storageFeature.OnCreationCompleteFromStorage += OnCreationFromStorage;
        storageFeature.OnDeletedComplete += OnDeleteComplete;

        // Localization 状態イベントを有効化
        mapFeature.EnableLocalizationEvents(true);

        // UI 配線
        WireUi();

        // マップ一覧
        InitMaps();

        // 保存ID復元
        LoadSavedIds();

        UpdateLocalizationUi();
        if (publishButton) publishButton.interactable = false;
    }

    void OnDestroy()
    {
        if (Instance == this) Instance = null;
        if (anchorManager) anchorManager.trackablesChanged.RemoveListener(OnTrackablesChanged);
        if (storageFeature != null)
        {
            storageFeature.OnPublishComplete -= OnPublishComplete;
            storageFeature.OnQueryComplete -= OnQueryComplete;
            storageFeature.OnCreationCompleteFromStorage -= OnCreationFromStorage;
            storageFeature.OnDeletedComplete -= OnDeleteComplete;
        }
        if (mapFeature != null) mapFeature.EnableLocalizationEvents(false);
    }

    // ===== ここから新規: Grab Release 時の差し替え =====
    public async Task ReanchorThisHandleAsync(AnchorHandle handle)
    {
        if (handle == null || anchorManager == null) return;

        // 旧アンカーと保存IDを把握
        var old = handle.CurrentAnchor;
        string oldMapPosId = (mlAnchorSubsystem != null && old != null)
            ? mlAnchorSubsystem.GetAnchorMapPositionId(old) : null;

        // 新しいアンカーを現在位置で生成
        var pose = new Pose(handle.transform.position, handle.transform.rotation);
        var newAnchor = await SpawnLocalAnchorAtPoseAsync(pose);
        if (newAnchor == null) return;

        // 表示/ハンドルを新アンカーの子に付け替え（ワールド座標維持）
        handle.transform.SetParent(newAnchor.transform, true);
        handle.Bind(newAnchor);

        // 旧アンカーを破棄（保存済みならストレージからも削除）
        if (!string.IsNullOrEmpty(oldMapPosId))
            storageFeature?.DeleteStoredSpatialAnchors(new List<string> { oldMapPosId });

        if (old) Destroy(old.gameObject);

        // 以後、Publish ボタンで newAnchor が保存される（autoPublishOnNetworkReceive を使うならここで Publish してもよい）
    }

    async Task WaitSubsystems()
    {
        while (XRGeneralSettings.Instance == null ||
               XRGeneralSettings.Instance.Manager == null ||
               XRGeneralSettings.Instance.Manager.activeLoader == null)
        {
            await Task.Yield();
        }
    }

    void WireUi()
    {
        if (localizeButton) { localizeButton.onClick.RemoveAllListeners(); localizeButton.onClick.AddListener(LocalizeSelectedMap); }
        if (spawnButton) { spawnButton.onClick.RemoveAllListeners(); spawnButton.onClick.AddListener(async () => await SpawnLocalAnchorAsync()); }
        if (publishButton) { publishButton.onClick.RemoveAllListeners(); publishButton.onClick.AddListener(PublishTrackingLocals); }
        if (queryButton) { queryButton.onClick.RemoveAllListeners(); queryButton.onClick.AddListener(QueryNearby); }
        if (deleteNearestButton) { deleteNearestButton.onClick.RemoveAllListeners(); deleteNearestButton.onClick.AddListener(DeleteNearestStored); }
        if (deleteAllButton) { deleteAllButton.onClick.RemoveAllListeners(); deleteAllButton.onClick.AddListener(DeleteAllStored); }
    }

    void InitMaps()
    {
        if (mapFeature.GetLocalizationMapsList(out maps) == XrResult.Success && mapsDropdown)
        {
            mapsDropdown.ClearOptions();
            mapsDropdown.AddOptions(maps.Select(m => m.Name).ToList());
        }
    }

    // ---------- Buttons ----------
    void LocalizeSelectedMap()
    {
        if (maps.Length == 0) { Log("ローカライズ対象の Map がありません"); return; }
        int idx = Mathf.Clamp(mapsDropdown ? mapsDropdown.value : 0, 0, maps.Length - 1);
        string uuid = maps[idx].MapUUID;

        var res = mapFeature.RequestMapLocalization(uuid);
        if (res != XrResult.Success) { Log("Localize 失敗: " + res); return; }

        // 既存を掃除
        foreach (var a in FindObjectsByType<ARAnchor>(FindObjectsSortMode.None)) Destroy(a.gameObject);
        localAnchors.Clear(); storedIds.Clear();

        Log("ローカライズ要求: " + maps[idx].Name);
        UpdateLocalizationUi();
    }

    public async Task SpawnLocalAnchorAsync()
    {
        var src = rightController ? rightController : (mainCamera ? mainCamera.transform : null);
        if (!src) { Log("生成元 Transform 未設定"); return; }

        var pose = new Pose(
            src.position + src.forward * forwardMeters,
            Quaternion.LookRotation(Vector3.ProjectOnPlane(src.forward, Vector3.up), Vector3.up)
        );

        await SpawnLocalAnchorAtPoseAsync(pose);
        Log("ローカルアンカー生成（未保存）");
    }

    async Task<ARAnchor> SpawnLocalAnchorAtPoseAsync(Pose pose)
    {
        var result = await anchorManager.TryAddAnchorAsync(pose);
        if (!result.status.IsSuccess())
        {
            Log("Anchor 生成失敗: " + (XrResult)result.status.nativeStatusCode);
            return null;
        }

        var anchor = result.value;
        if (anchor != null)
        {
            localAnchors.Add(anchor);
            if (anchorVisualPrefab && anchor.GetComponentInChildren<MeshRenderer>() == null)
            {
                var vis = Instantiate(anchorVisualPrefab, anchor.transform);
                TintAll(vis, Color.grey);
            }
        }
        return anchor;
    }

    void PublishTrackingLocals()
    {
        mapFeature.GetLatestLocalizationMapData(out LocalizationEventData d);
        if (d.State != LocalizationMapState.Localized) { Log("Publish には Localized が必要"); return; }

        var targets = localAnchors.Where(a => a && a.trackingState == TrackingState.Tracking && !a.pending).ToList();
        if (targets.Count == 0) { Log("Publish 対象なし"); return; }

        // 0 = 無期限。完了は OnPublishComplete。
        storageFeature.PublishSpatialAnchorsToStorage(targets, 0);
        Log($"Publish 要求: {targets.Count} 件");
    }

    void QueryNearby()
    {
        var origin = rightController ? rightController.position : (mainCamera ? mainCamera.transform.position : Vector3.zero);
        if (!storageFeature.QueryStoredSpatialAnchors(origin, Mathf.Max(restoreRadiusMeters, 0.1f)))
            Log("Query 失敗");
        else
            Log($"Query 要求（{restoreRadiusMeters}m以内）");
    }

    void DeleteNearestStored()
    {
        var a = FindClosestStoredAnchorNear(GetRefPosition(), 100f);
        if (!a) { Log("近傍の保存済みアンカーなし"); return; }
        string id = mlAnchorSubsystem != null ? mlAnchorSubsystem.GetAnchorMapPositionId(a) : "";
        if (string.IsNullOrEmpty(id)) { Log("MapPositionId 取得失敗"); return; }
        storageFeature.DeleteStoredSpatialAnchors(new List<string> { id });
        Log("削除要求: " + id);
    }

    void DeleteAllStored()
    {
        var ids = new List<string>();
        foreach (var a in anchorManager.trackables)
        {
            var id = mlAnchorSubsystem != null ? mlAnchorSubsystem.GetAnchorMapPositionId(a) : "";
            if (!string.IsNullOrEmpty(id)) ids.Add(id);
        }
        if (ids.Count == 0) { Log("削除対象なし"); return; }
        storageFeature.DeleteStoredSpatialAnchors(ids);
        Log($"一括削除要求: {ids.Count} 件");
    }

    // ---------- Storage callbacks ----------
    void OnPublishComplete(ulong anchorId, string mapPosId)
    {
        // 保存済みへ昇格（見た目を白）
        var anchor = FindAnchorByMapPositionId(mapPosId);
        if (!anchor && mlAnchorSubsystem != null)
        {
            var pose = mlAnchorSubsystem.GetAnchorPoseFromId(anchorId); // ← GetAnchorPoseFromId（非推奨APIは不使用）
            anchor = FindClosestLocalAnchor(pose.position, duplicateEpsilonMeters);
        }
        if (anchor)
        {
            storedIds.Add(anchor.trackableId);
            localAnchors.Remove(anchor);
            savedMapPositionIds.Add(mapPosId);
            SaveSavedIds();
            TintAll(anchor.gameObject, Color.white);

            // ★ Fusion 2 へ Pose をブロードキャスト（AR Cloud なしの共有）
            mapFeature.GetLatestLocalizationMapData(out var d);
            string uuid = (d.State == LocalizationMapState.Localized && d.Map.Name != null) ? d.Map.MapUUID : "";
            var pose = anchor.transform.GetPose(); // 拡張メソッドが無ければ自前で position/rotation を使う
            AnchorNetBridge.Instance?.BroadcastAnchorPose(pose.position, pose.rotation, uuid);

            Log($"Publish 完了: {mapPosId}");
        }
        else
        {
            Log($"Publish 完了（Anchor未特定）: {mapPosId}");
        }
    }

    void OnQueryComplete(List<string> ids)
    {
        if (ids == null || ids.Count == 0) { Log("Query: 見つからず"); return; }

        // 既にシーンにあるIDを除外
        var existing = new HashSet<string>();
        foreach (var a in anchorManager.trackables)
        {
            var id = mlAnchorSubsystem != null ? mlAnchorSubsystem.GetAnchorMapPositionId(a) : "";
            if (!string.IsNullOrEmpty(id)) existing.Add(id);
        }
        var toCreate = ids.Where(id => !existing.Contains(id)).ToList();
        if (toCreate.Count == 0) { Log("復元対象なし"); return; }

        if (!storageFeature.CreateSpatialAnchorsFromStorage(toCreate))
            Log("CreateSpatialAnchorsFromStorage 失敗");
        else
            Log($"復元要求: {toCreate.Count} 件");
    }

    void OnCreationFromStorage(Pose pose, ulong anchorId, string mapPosId, XrResult result)
    {
        if (result != XrResult.Success) { Log($"復元失敗: {result}"); return; }
        savedMapPositionIds.Add(mapPosId);
        SaveSavedIds();
        Log($"復元通知: {mapPosId}");
        // 実体は trackablesChanged(added) で来る
    }

    void OnDeleteComplete(List<string> deletedIds)
    {
        foreach (var id in deletedIds)
        {
            var a = FindAnchorByMapPositionId(id);
            if (a) Destroy(a.gameObject);
            savedMapPositionIds.Remove(id);
        }
        SaveSavedIds();
        Log($"削除完了: {deletedIds.Count} 件");
    }

    // ---------- ARF6: trackablesChanged ----------
    void OnTrackablesChanged(ARTrackablesChangedEventArgs<ARAnchor> changes)
    {
        foreach (var a in changes.added)
        {
            var id = mlAnchorSubsystem != null ? mlAnchorSubsystem.GetAnchorMapPositionId(a) : "";
            bool stored = !string.IsNullOrEmpty(id);
            EnsureVisual(a, stored);
            if (stored) { storedIds.Add(a.trackableId); TintAll(a.gameObject, Color.white); }
            else { if (!localAnchors.Contains(a)) localAnchors.Add(a); TintAll(a.gameObject, Color.grey); }
        }
        foreach (var a in changes.updated)
        {
            var id = mlAnchorSubsystem != null ? mlAnchorSubsystem.GetAnchorMapPositionId(a) : "";
            if (!string.IsNullOrEmpty(id) && !storedIds.Contains(a.trackableId))
            {
                storedIds.Add(a.trackableId);
                localAnchors.Remove(a);
                EnsureVisual(a, true);
                TintAll(a.gameObject, Color.white);
            }
        }
        foreach (var kv in changes.removed) // KeyValuePair<TrackableId, ARAnchor>
        {
            localAnchors.Remove(kv.Value);
            storedIds.Remove(kv.Key);
        }
        UpdateLocalizationUi();
    }

    // ---------- ネット受信（Fusion 2 経由） ----------
    public async void OnNetworkAnchorPublished(Vector3 pos, Quaternion rot, string mapUUID)
    {
        // 同じ Space（MapUUID）にローカライズしているか確認
        mapFeature.GetLatestLocalizationMapData(out var d);
        if (d.State != LocalizationMapState.Localized || d.Map.MapUUID != mapUUID)
        {
            Log($"別 Space のアンカー通知を受信（無視）: {mapUUID}");
            return;
        }

        // 重複チェック
        var existStored = FindClosestStoredAnchorNear(pos, duplicateEpsilonMeters);
        var existLocal = FindClosestLocalAnchor(pos, duplicateEpsilonMeters);
        if (existStored || existLocal) return;

        var a = await SpawnLocalAnchorAtPoseAsync(new Pose(pos, rot));
        if (a != null && autoPublishOnNetworkReceive)
        {
            PublishTrackingLocals(); // Tracking 中のローカルを Publish
        }
    }

    // ---------- helpers ----------
    void EnsureVisual(ARAnchor a, bool stored)
    {
        if (!a || !anchorVisualPrefab) return;
        if (!a.GetComponentInChildren<MeshRenderer>())
        {
            var vis = Instantiate(anchorVisualPrefab, a.transform);
            TintAll(vis, stored ? Color.white : Color.grey);
        }
    }
    void TintAll(GameObject go, Color c)
    {
        foreach (var r in go.GetComponentsInChildren<MeshRenderer>()) r.material.color = c;
    }
    ARAnchor FindAnchorByMapPositionId(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        foreach (var a in anchorManager.trackables)
            if (mlAnchorSubsystem != null && mlAnchorSubsystem.GetAnchorMapPositionId(a) == id) return a;
        return null;
    }
    ARAnchor FindClosestLocalAnchor(Vector3 pos, float maxDist)
    {
        ARAnchor best = null; float bestD = maxDist;
        foreach (var a in localAnchors)
        {
            if (!a) continue;
            float d = Vector3.Distance(a.transform.position, pos);
            if (d <= bestD) { bestD = d; best = a; }
        }
        return best;
    }
    ARAnchor FindClosestStoredAnchorNear(Vector3 pos, float maxDist)
    {
        ARAnchor best = null; float bestD = maxDist;
        foreach (var a in anchorManager.trackables)
        {
            var id = mlAnchorSubsystem != null ? mlAnchorSubsystem.GetAnchorMapPositionId(a) : "";
            if (string.IsNullOrEmpty(id)) continue;
            float d = Vector3.Distance(a.transform.position, pos);
            if (d <= bestD) { bestD = d; best = a; }
        }
        return best;
    }
    Vector3 GetRefPosition() => rightController ? rightController.position : (mainCamera ? mainCamera.transform.position : Vector3.zero);

    void UpdateLocalizationUi()
    {
        if (mapFeature == null) return;
        mapFeature.GetLatestLocalizationMapData(out LocalizationEventData d);
        if (localizationLabel)
        {
            string info = $"State:{d.State} Conf:{d.Confidence}";
            if (d.State == LocalizationMapState.Localized && d.Map.Name != null) info += $"  Name:{d.Map.Name}";
            localizationLabel.text = info;
        }
        if (publishButton) publishButton.interactable = (d.State == LocalizationMapState.Localized);
    }

    void LoadSavedIds()
    {
        savedMapPositionIds.Clear();
        var json = PlayerPrefs.GetString(SavedIdListKey, "");
        if (string.IsNullOrEmpty(json)) return;
        try { var l = JsonUtility.FromJson<IdList>(json); foreach (var id in l.ids) savedMapPositionIds.Add(id); } catch { }
    }
    void SaveSavedIds()
    {
        var l = new IdList { ids = savedMapPositionIds.ToList() };
        PlayerPrefs.SetString(SavedIdListKey, JsonUtility.ToJson(l));
        PlayerPrefs.Save();
    }
    void Log(string msg) { if (statusLabel) statusLabel.text = msg; Debug.Log("[ML2AnchorsControllerUnified] " + msg); }
}

// Transform 拡張（Pose 取得用：無ければ使用しなくてもOK）
static class TransformExt
{
    public static Pose GetPose(this Transform t) => new Pose(t.position, t.rotation);
}