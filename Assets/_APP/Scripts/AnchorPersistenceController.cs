// Assets/Scripts/AnchorPersistenceController.cs
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;
using UnityEngine.XR.Management;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.NativeTypes;

using MagicLeap.OpenXR.Subsystems;
using MagicLeap.OpenXR.Features.SpatialAnchors;
using MagicLeap.OpenXR.Features.LocalizationMaps;

public class AnchorPersistenceController : MonoBehaviour
{
    [Header("Scene refs")]
    [SerializeField] private ARAnchorManager anchorManager;        // XR Origin に付けた ARAnchorManager
    [SerializeField] private Transform headTransform;              // Main Camera（XR Origin配下）
    [SerializeField] private Transform rightControllerTransform;   // 右手コントローラ（任意）
    [SerializeField] private float restoreQueryRadiusMeters = 5f;  // 復元の探索半径

    [Header("Anchor Visual")]
    [Tooltip("ARAnchorManager.anchorPrefab にも同じものを設定しておくと、復元時に自動で見た目が出ます")]
    [SerializeField] private GameObject anchorVisualPrefab;        // 目印（小Cube等）

    // ML OpenXR features
    private MagicLeapSpatialAnchorsStorageFeature storageFeature;
    private MagicLeapLocalizationMapFeature mapFeature;
    private MLXrAnchorSubsystem anchorSubsystem;

    // --- 永続管理 ---
    private const string SavedIdListKey = "ML2_SavedAnchorMapPositionIds";
    private readonly HashSet<string> savedMapIds = new();             // このアプリが Publish した/復元した Storage 側ID（重複生成の抑止にも使用）
    private readonly Dictionary<string, TrackableId> idToTrackable = new(); // Storage ID -> TrackableId（削除時のシーン掃除に使用）

    private Coroutine initRoutine;

    private void Awake()
    {
        if (anchorManager == null)
            anchorManager = FindFirstObjectByType<ARAnchorManager>();
    }

    private void OnEnable()
    {
        initRoutine = StartCoroutine(InitRoutine());
    }

    private void OnDisable()
    {
        if (initRoutine != null) { StopCoroutine(initRoutine); initRoutine = null; }

        if (storageFeature != null)
        {
            storageFeature.OnPublishComplete -= OnAnchorPublishComplete;
            storageFeature.OnQueryComplete -= OnAnchorQueryComplete;
            storageFeature.OnCreationCompleteFromStorage -= OnAnchorCreationFromStorage;
            storageFeature.OnDeletedComplete -= OnAnchorDeleteComplete;
        }
        if (anchorManager != null)
            anchorManager.trackablesChanged.RemoveListener(OnAnchorTrackablesChanged);
    }

    private IEnumerator InitRoutine()
    {
        // 1) サブシステム待機（ARAnchorSubsystem=MLXrAnchorSubsystem）
        yield return new WaitUntil(AreSubsystemsLoaded);

        // 2) ML Features を取得＆イベント購読
        storageFeature = OpenXRSettings.Instance.GetFeature<MagicLeapSpatialAnchorsStorageFeature>();
        mapFeature = OpenXRSettings.Instance.GetFeature<MagicLeapLocalizationMapFeature>();
        if (storageFeature == null || !storageFeature.enabled)
        {
            Debug.LogError("MagicLeapSpatialAnchorsStorageFeature が無効です。OpenXR設定を確認してください。");
            enabled = false; yield break;
        }
        if (mapFeature != null && mapFeature.enabled)
            mapFeature.EnableLocalizationEvents(true); // ローカライズ状態監視

        storageFeature.OnPublishComplete += OnAnchorPublishComplete;
        storageFeature.OnQueryComplete += OnAnchorQueryComplete;
        storageFeature.OnCreationCompleteFromStorage += OnAnchorCreationFromStorage;
        storageFeature.OnDeletedComplete += OnAnchorDeleteComplete;

        // AR Foundation 6: trackablesChanged（anchorsChanged は非推奨）
        anchorManager.trackablesChanged.AddListener(OnAnchorTrackablesChanged);

        // 3) 保存済みIDの復元
        LoadSavedMapIds();
    }

    private bool AreSubsystemsLoaded()
    {
        var mgr = XRGeneralSettings.Instance?.Manager;
        if (mgr?.activeLoader == null) return false;
        anchorSubsystem = mgr.activeLoader.GetLoadedSubsystem<XRAnchorSubsystem>() as MLXrAnchorSubsystem;
        return anchorSubsystem != null;
    }

    // ========== UI Buttons ==========
    // 保存（Publish）: コントローラ or カメラの30cm先にARAnchor生成→StorageへPublish(無期限)
    public void OnClick_Save()
    {
        if (!IsLocalized())
        {
            Debug.LogWarning("まだSpaceにローカライズされていません。Spacesアプリでマップ作成/ローカライズ、または本アプリ内でローカライズしてください。");
            return;
        }

        Pose pose = GetSpawnPose30cm();

        GameObject go = anchorManager.anchorPrefab != null
            ? Instantiate(anchorManager.anchorPrefab, pose.position, pose.rotation)
            : new GameObject("Anchor");

        var arAnchor = go.GetComponent<ARAnchor>() ?? go.AddComponent<ARAnchor>();
        StartCoroutine(PublishWhenTracking(arAnchor)); // Tracking になったら Publish
    }

    // 復元（Query→CreateFromStorage）
    public void OnClick_Restore()
    {
        if (!IsLocalized())
        {
            Debug.LogWarning("ローカライズされていません。復元にはローカライズが必要です。");
            return;
        }
        var origin = headTransform != null ? headTransform.position : Camera.main.transform.position;
        storageFeature.QueryStoredSpatialAnchors(origin, Mathf.Max(restoreQueryRadiusMeters, 0.1f));
    }

    // 削除（このアプリで保存した全アンカーを削除）
    public void OnClick_DeleteAll()
    {
        if (savedMapIds.Count == 0)
        {
            Debug.Log("保存済みのAnchorはありません。");
            return;
        }
        storageFeature.DeleteStoredSpatialAnchors(savedMapIds.ToList());
        // OnDeletedComplete で PlayerPrefs / シーンを掃除
    }

    // ========== 実処理 ==========

    private IEnumerator PublishWhenTracking(ARAnchor a)
    {
        while (a != null && a.trackingState != TrackingState.Tracking)
            yield return null;

        if (a == null) yield break;

        // 無期限は 0（名前付き引数は不可）
        storageFeature.PublishSpatialAnchorsToStorage(new List<ARAnchor> { a }, 0);
        // 完了は OnAnchorPublishComplete で受け取り
    }

    private Pose GetSpawnPose30cm()
    {
        Transform t = rightControllerTransform != null ? rightControllerTransform :
                      (headTransform != null ? headTransform : Camera.main.transform);
        return new Pose(t.position + t.forward * 0.30f, t.rotation);
    }

    private bool IsLocalized()
    {
        if (mapFeature == null || !mapFeature.enabled) return true; // 明示チェックなし運用も可
        LocalizationEventData data;
        if (mapFeature.GetLatestLocalizationMapData(out data))
            return data.State == LocalizationMapState.Localized;
        return false;
    }

    // ========== Storage コールバック ==========

    // Publish 完了：Storage 側 ID を控える（重複回避用にセットへ）
    private void OnAnchorPublishComplete(ulong anchorId, string anchorMapPositionId)
    {
        if (!string.IsNullOrEmpty(anchorMapPositionId))
        {
            savedMapIds.Add(anchorMapPositionId);
            SaveSavedMapIds();

            // Storage ID -> TrackableId を解決して逆引きも控える（存在すれば）
            try
            {
                var tid = anchorSubsystem.GetTrackableIdFromMapPositionId(anchorMapPositionId);
                if (tid != TrackableId.invalidId) idToTrackable[anchorMapPositionId] = tid;
            }
            catch { /* SDK 差異で未提供ならスキップ */ }
        }

        var pose = anchorSubsystem.GetAnchorPoseFromId(anchorId);
        Debug.Log($"Publish Complete: {anchorMapPositionId} at {pose.position}");
    }

    // Query 完了：未生成のものだけ CreateFromStorage
    private void OnAnchorQueryComplete(List<string> anchorMapPositionIds)
    {
        if (anchorMapPositionIds == null || anchorMapPositionIds.Count == 0)
        {
            Debug.Log("Query: 見つかりませんでした。半径を広げてください。");
            return;
        }

        var toCreate = new List<string>();

        foreach (var id in anchorMapPositionIds)
        {
            // すでに保持している Storage ID はスキップ（重複生成回避）
            if (savedMapIds.Contains(id))
                continue;

            // 追加の重複確認（ID→TrackableId→ARAnchor で既に存在ならスキップ）
            bool exists = false;
            try
            {
                var tid = anchorSubsystem.GetTrackableIdFromMapPositionId(id);
                if (tid != TrackableId.invalidId)
                {
                    var existing = anchorManager.GetAnchor(tid);
                    exists = (existing != null);
                }
            }
            catch { /* SDK差異で未提供ならこのチェックはスキップ */ }

            if (!exists) toCreate.Add(id);
        }

        if (toCreate.Count > 0)
        {
            bool ok = storageFeature.CreateSpatialAnchorsFromStorage(toCreate);
            if (!ok) Debug.LogError("CreateSpatialAnchorsFromStorage 失敗");
        }
    }

    // Storage からの生成完了：見た目付与＋マッピング保存
    private void OnAnchorCreationFromStorage(Pose pose, ulong anchorId, string anchorMapPositionId, XrResult result)
    {
        Debug.Log($"Restore: {anchorMapPositionId} result={result}");

        if (result == XrResult.Success)
        {
            savedMapIds.Add(anchorMapPositionId);
            SaveSavedMapIds();

            // ID -> TrackableId を控えて、見た目を補う
            try
            {
                var tid = anchorSubsystem.GetTrackableIdFromMapPositionId(anchorMapPositionId);
                if (tid != TrackableId.invalidId)
                {
                    idToTrackable[anchorMapPositionId] = tid;

                    var anchor = anchorManager.GetAnchor(tid);
                    if (anchor != null && anchorVisualPrefab != null && anchor.transform.childCount == 0)
                        Instantiate(anchorVisualPrefab, anchor.transform);
                }
            }
            catch { /* SDK差異 */ }
        }
    }

    // Storage 側削除完了：PlayerPrefs とシーンの該当 Anchor を掃除
    private void OnAnchorDeleteComplete(List<string> anchorMapPositionIds)
    {
        bool changed = false;
        foreach (var id in anchorMapPositionIds)
        {
            changed |= savedMapIds.Remove(id);

            // シーン上の該当 ARAnchor を破棄（可能なら）
            try
            {
                if (idToTrackable.TryGetValue(id, out var tid))
                {
                    var a = anchorManager.GetAnchor(tid);
                    if (a != null) Destroy(a.gameObject);
                    idToTrackable.Remove(id);
                }
                else
                {
                    var tid2 = anchorSubsystem.GetTrackableIdFromMapPositionId(id);
                    if (tid2 != TrackableId.invalidId)
                    {
                        var a2 = anchorManager.GetAnchor(tid2);
                        if (a2 != null) Destroy(a2.gameObject);
                    }
                }
            }
            catch { /* SDK差異 */ }
        }

        if (changed) SaveSavedMapIds();
        Debug.Log($"Delete Complete: {anchorMapPositionIds.Count} anchor(s) removed");
    }

    // ========== AR Foundation 6: trackablesChanged ==========
    private void OnAnchorTrackablesChanged(ARTrackablesChangedEventArgs<ARAnchor> e)
    {
        // 追加分：見た目が無い場合だけ補助表示を付与
        foreach (var added in e.added)
        {
            if (anchorVisualPrefab != null && added.transform.childCount == 0)
                Instantiate(anchorVisualPrefab, added.transform);
        }

        // 更新分：ここでは特に何もしない（保存判定は Storage 側で一元管理）

        // 削除分：removed は KeyValuePair<TrackableId, ARAnchor>
        foreach (var kv in e.removed)
        {
            var tid = kv.Key;

            // TrackableId→Storage ID の逆引きは保持していないため、辞書から該当エントリを消す程度にとどめる
            foreach (var pair in idToTrackable.Where(p => p.Value == tid).ToList())
                idToTrackable.Remove(pair.Key);
        }
    }

    // ========== PlayerPrefs（保存IDの永続化） ==========

    [Serializable] private class IdList { public List<string> ids = new(); }
    private void LoadSavedMapIds()
    {
        var json = PlayerPrefs.GetString(SavedIdListKey, "");
        if (string.IsNullOrEmpty(json)) return;
        try
        {
            var l = JsonUtility.FromJson<IdList>(json);
            foreach (var id in l.ids) savedMapIds.Add(id);
        }
        catch { }
    }
    private void SaveSavedMapIds()
    {
        var l = new IdList { ids = savedMapIds.ToList() };
        PlayerPrefs.SetString(SavedIdListKey, JsonUtility.ToJson(l));
        PlayerPrefs.Save();
    }
}
