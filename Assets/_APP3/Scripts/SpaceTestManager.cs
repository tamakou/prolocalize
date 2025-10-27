using UnityEngine;
using UnityEngine.UI;
using System.Threading.Tasks;
using UnityEngine.Android;
using MagicLeap.OpenXR.Features.LocalizationMaps;
using System.IO;
using System.Linq;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.NativeTypes; // XrResult
using System;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// Magic Leap 2 スペースのエクスポート/インポート/ローカライゼーション + WebDAV連携
/// - WebDAVSpaceManager と連携して Export/Import をクラウド共有
/// - ローカライズ成功時に GetMapOrigin() で Space 原点 Pose を取得し、シーンへ反映
/// - API 失敗時は XrResult を含む詳細メッセージを UI に表示
/// </summary>
public class SpaceTestManager : MonoBehaviour
{
  // ==== 操作重複防止 ====
  private bool isExportInProgress = false;
  private bool isImportInProgress = false;

  // ==== UI ====
  [Header("UI Elements")]
  [SerializeField] private Button exportButton;
  [SerializeField] private Button importButton;
  [SerializeField] private Button localizeButton;
  [SerializeField] private Text statusText;

  // ==== WebDAV ====
  [Header("WebDAV Integration")]
  [SerializeField] private WebDAVSpaceManager webdavManager;

  // ==== 権限（実機で必要なもの） ====
  // ※ Manifest 側に必須：INTERNET / ACCESS_NETWORK_STATE / SPACE_MANAGER / SPACE_IMPORT_EXPORT / SPATIAL_ANCHOR（用途に応じ）
  private static readonly string[] SPACE_MANAGER_PERMISSIONS = {
    "com.magicleap.permission.SPACE_MANAGER"
  };
  private static readonly string[] SPACE_IMPORT_EXPORT_PERMISSIONS = {
    "com.magicleap.permission.SPACE_IMPORT_EXPORT",
    // 端末内ストレージに保存/読込するフォールバック用（必要な場合のみ）
    "android.permission.WRITE_EXTERNAL_STORAGE",
    "android.permission.READ_EXTERNAL_STORAGE"
  };
  private static readonly string[] SPATIAL_ANCHOR_PERMISSIONS = {
    "com.magicleap.permission.SPATIAL_ANCHOR"
  };
  private static readonly string[] NETWORK_PERMISSIONS = {
    "android.permission.INTERNET",
    "android.permission.ACCESS_NETWORK_STATE"
  };

  private string workingSpaceManagerPermission = null;
  private string workingSpaceImportExportPermission = null;
  private string workingSpatialAnchorPermission = null;
  private string workingNetworkPermission = null;
  private bool permissionsGranted = false;

  // ==== 保存ファイルパス ====
  private string SPACE_FILE_PATH
  {
    get
    {
      string[] possiblePaths = {
        Path.Combine(Application.persistentDataPath, "unity_exported_space.zip"),
        Path.Combine(Application.temporaryCachePath, "unity_exported_space.zip"),
        Path.Combine("/sdcard/Documents", "unity_exported_space.zip")
      };
      foreach (var path in possiblePaths)
      {
        try
        {
          var dir = Path.GetDirectoryName(path);
          if (Directory.Exists(dir) || CanCreateDirectory(dir))
          {
            Debug.Log($"[PATH] Using: {path}");
            return path;
          }
        }
        catch (Exception ex)
        {
          Debug.LogWarning($"[PATH] Not accessible: {path} -> {ex.Message}");
        }
      }
      Debug.LogWarning("[PATH] Fallback to persistentDataPath");
      return Path.Combine(Application.persistentDataPath, "unity_exported_space.zip");
    }
  }

  // ==== ML2 Localization Feature ====
  private MagicLeapLocalizationMapFeature localizationMapFeature;

  // ==== Localization 状態 ====
  private string lastImportedSpaceId = null;
  private bool isLocalizationInProgress = false;
  private TaskCompletionSource<(bool success, string errorMessage)> localizationTaskCompletionSource = null;
  private string currentLocalizationTargetId = null;

  // ==== シーン反映 ====
  [Header("Scene Anchoring")]
  [SerializeField] private Transform spaceOrigin;   // Space 原点の Transform（未設定なら自動生成）
  [SerializeField] private Transform contentRoot;   // 全コンテンツの親（任意）

  // =======================================================================
  // ライフサイクル
  // =======================================================================

  private async void Start()
  {
    Debug.Log("[STM] Start()");

    if (webdavManager == null)
    {
      webdavManager = GetComponent<WebDAVSpaceManager>();
      if (webdavManager != null) Debug.Log("[STM] WebDAVSpaceManager found.");
      else Debug.LogWarning("[STM] WebDAVSpaceManager not found. WebDAV disabled.");
    }

    InitializeLocalizationMapFeature();
    SetupUI();

    await Task.Delay(500);
    await CheckAndRequestPermissionsAsync();
  }

  private void OnDestroy()
  {
    try
    {
      MagicLeapLocalizationMapFeature.OnLocalizationChangedEvent -= OnLocalizationChanged;
      if (localizationMapFeature != null)
      {
        var r = localizationMapFeature.EnableLocalizationEvents(false);
        Debug.Log($"[STM] Disable LocalizationEvents: {r}");
      }
    }
    catch (Exception ex)
    {
      Debug.LogError($"[STM] Cleanup error: {ex.Message}");
    }
  }

  // =======================================================================
  // 初期化 / UI
  // =======================================================================

  private void InitializeLocalizationMapFeature()
  {
    try
    {
      localizationMapFeature = OpenXRSettings.Instance.GetFeature<MagicLeapLocalizationMapFeature>();
      if (localizationMapFeature == null)
      {
        Debug.LogError("[STM] MagicLeapLocalizationMapFeature not found/enabled.");
        HandleError("ローカライゼーションマップ機能の初期化", "OpenXR設定で Magic Leap 2 Localization Maps を有効にしてください。", OperationResult.FeatureNotAvailable);
        return;
      }

      var enableResult = localizationMapFeature.EnableLocalizationEvents(true);
      Debug.Log($"[STM] EnableLocalizationEvents: {enableResult}");
      if (enableResult != XrResult.Success)
        Debug.LogError($"[STM] EnableLocalizationEvents failed: {enableResult}");

      MagicLeapLocalizationMapFeature.OnLocalizationChangedEvent += OnLocalizationChanged;
      Debug.Log("[STM] Subscribed OnLocalizationChangedEvent.");
    }
    catch (Exception ex)
    {
      HandleError("ローカライゼーションマップ機能の初期化", ex, OperationResult.InitializationError);
    }
  }

  private void SetupUI()
  {
    if (exportButton)
    {
      exportButton.onClick.AddListener(OnExportButtonClicked);
      var xri = exportButton.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
      if (xri != null) xri.selectEntered.AddListener(_ => OnExportButtonClicked());
    }

    if (importButton)
    {
      importButton.onClick.AddListener(OnImportButtonClicked);
      var xri = importButton.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
      if (xri != null) xri.selectEntered.AddListener(_ => OnImportButtonClicked());
    }

    if (localizeButton)
    {
      localizeButton.onClick.AddListener(OnLocalizeButtonClicked);
      var xri = localizeButton.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
      if (xri != null) xri.selectEntered.AddListener(_ => OnLocalizeButtonClicked());
    }

    DisableSpaceFunctions();
    UpdateProgressStatus(StatusMessages.INITIALIZING);
  }

  // =======================================================================
  // Export
  // =======================================================================

  public void OnExportButtonClicked()
  {
    if (isExportInProgress) return;
    StartCoroutine(OnExportButtonClickedCoroutine());
  }

  private IEnumerator OnExportButtonClickedCoroutine()
  {
    isExportInProgress = true;
    try
    {
      var t = OnExportButtonClickedAsync();
      while (!t.IsCompleted) yield return null;
    }
    finally
    {
      isExportInProgress = false;
    }
  }

  private async Task OnExportButtonClickedAsync()
  {
    if (!permissionsGranted)
    {
      HandleError("Spaceエクスポート", "必要な権限が付与されていません", OperationResult.PermissionDenied);
      await CheckAndRequestPermissionsAsync();
      return;
    }
    if (localizationMapFeature == null)
    {
      HandleError("Spaceエクスポート", "ローカライゼーション機能が初期化されていません", OperationResult.FeatureNotAvailable);
      return;
    }

    try
    {
      ShowOperationProgress("Spaceエクスポート", "最新のSpaceを検索中", 1, 4);

      var latestSpace = await GetLatestSpaceAsync();
      if (latestSpace == null)
      {
        HandleError("Spaceエクスポート", "エクスポート可能なSpaceが見つかりません", OperationResult.NoSpaceFound);
        return;
      }

      string mapId = latestSpace.Value.MapUUID;
      string mapName = await GetMapNameByIdAsync(mapId);
      if (string.IsNullOrEmpty(mapName)) mapName = $"Map-{mapId.Substring(0, Math.Min(8, mapId.Length))}";
      ShowOperationProgress("Spaceエクスポート", $"Map: {mapName}\nID: {mapId}", 2, 4);

      var exportResult = await ExportSpaceAsync(mapId);
      if (!exportResult.success)
      {
        HandleError("Spaceエクスポート", exportResult.errorMessage, OperationResult.APIError);
        return;
      }

      ShowOperationProgress("Spaceエクスポート", "ファイルに保存中", 3, 4);

      var saveResult = await SaveSpaceToFileAsync(exportResult.data);
      if (!saveResult.success)
      {
        HandleError("Spaceエクスポート", saveResult.errorMessage, OperationResult.FileAccessError);
        return;
      }

      if (webdavManager != null)
      {
        ShowOperationProgress("Spaceエクスポート", "WebDAVにアップロード中", 4, 4);
        await webdavManager.UploadSpaceDataAsync(exportResult.data, mapId);
        ShowOperationSuccess("Spaceエクスポート",
          $"エクスポート完了\nMap: {mapName}\nローカル: {SPACE_FILE_PATH}\nWebDAV: unity_exported_space.zip");
      }
      else
      {
        ShowOperationSuccess("Spaceエクスポート",
          $"エクスポート完了\nMap: {mapName}\nファイル: {SPACE_FILE_PATH}");
      }
    }
    catch (Exception ex)
    {
      HandleError("Spaceエクスポート", ex, OperationResult.UnknownError);
    }
  }

  // =======================================================================
  // Import
  // =======================================================================

  public void OnImportButtonClicked()
  {
    if (isImportInProgress) return;
    StartCoroutine(OnImportButtonClickedCoroutine());
  }

  private IEnumerator OnImportButtonClickedCoroutine()
  {
    isImportInProgress = true;
    try
    {
      var t = OnImportButtonClickedAsync();
      while (!t.IsCompleted) yield return null;
    }
    finally
    {
      isImportInProgress = false;
    }
  }

  private async Task OnImportButtonClickedAsync()
  {
    if (!permissionsGranted)
    {
      HandleError("Spaceインポート", "必要な権限が付与されていません", OperationResult.PermissionDenied);
      await CheckAndRequestPermissionsAsync();
      return;
    }
    if (localizationMapFeature == null)
    {
      HandleError("Spaceインポート", "ローカライゼーション機能が初期化されていません", OperationResult.FeatureNotAvailable);
      return;
    }

    try
    {
      byte[] spaceDataToImport = null;

      if (webdavManager != null)
      {
        ShowOperationProgress("Spaceインポート", "WebDAVからダウンロード中", 1, 3);
        var downloaded = await webdavManager.DownloadSpaceDataAsync();
        if (downloaded != null && downloaded.Length > 0)
        {
          spaceDataToImport = downloaded;
          Debug.Log($"[STM] Downloaded {downloaded.Length} bytes from WebDAV");
        }
        else
        {
          Debug.LogWarning("[STM] WebDAV download failed or empty. Fallback to local file.");
        }
      }

      if (spaceDataToImport == null)
      {
        ShowOperationProgress("Spaceインポート", "ローカルファイルから読み込み", 2, 3);
        var read = await ReadSpaceFromFileAsync();
        if (!read.success)
        {
          HandleError("Spaceインポート", read.errorMessage, OperationResult.FileNotFound);
          return;
        }
        spaceDataToImport = read.data;
      }

      ShowOperationProgress("Spaceインポート", $"データをインポート中 ({spaceDataToImport.Length:N0} bytes)", 3, 3);
      var import = await ImportSpaceAsync(spaceDataToImport);
      if (!import.success)
      {
        HandleError("Spaceインポート", import.errorMessage, OperationResult.APIError);
        return;
      }

      lastImportedSpaceId = import.spaceId;

      var displayName = await GetMapNameByIdAsync(import.spaceId);
      if (string.IsNullOrEmpty(displayName))
        displayName = $"Map-{import.spaceId.Substring(0, Math.Min(8, import.spaceId.Length))}";

      ShowOperationSuccess("Spaceインポート", $"完了\nMap: {displayName}\nID: {import.spaceId}");
    }
    catch (Exception ex)
    {
      HandleError("Spaceインポート", ex, OperationResult.UnknownError);
    }
  }

  // =======================================================================
  // Localize
  // =======================================================================

  public async void OnLocalizeButtonClicked()
  {
    Debug.Log("[STM] Localize clicked.");

    if (!permissionsGranted)
    {
      HandleError("ローカライゼーション", "必要な権限が付与されていません", OperationResult.PermissionDenied);
      await CheckAndRequestPermissionsAsync();
      return;
    }
    if (localizationMapFeature == null)
    {
      InitializeLocalizationMapFeature();
      if (localizationMapFeature == null)
      {
        HandleError("ローカライゼーション", "ローカライゼーション機能が初期化できません", OperationResult.FeatureNotAvailable);
        return;
      }
    }
    if (isLocalizationInProgress)
    {
      HandleError("ローカライゼーション", "ローカライゼーション処理が既に実行中です", OperationResult.OperationInProgress);
      return;
    }

    try
    {
      ShowOperationProgress("ローカライゼーション", "対象Spaceを検索中", 1, 3);

      string targetSpaceId = await GetTargetSpaceForLocalizationAsync();
      if (string.IsNullOrEmpty(targetSpaceId))
      {
        HandleError("ローカライゼーション", "ローカライゼーション対象のSpaceが見つかりません", OperationResult.NoSpaceFound);
        return;
      }

      string mapName = await GetMapNameByIdAsync(targetSpaceId);
      if (string.IsNullOrEmpty(mapName))
        mapName = $"Map-{targetSpaceId.Substring(0, Math.Min(8, targetSpaceId.Length))}";

      ShowOperationProgress("ローカライゼーション", $"Map: {mapName}\nID: {targetSpaceId}", 2, 3);

      isLocalizationInProgress = true;
      currentLocalizationTargetId = targetSpaceId;

      var req = await RequestLocalizationAsync(targetSpaceId);
      if (!req.success)
      {
        isLocalizationInProgress = false;
        HandleError("ローカライゼーション", req.errorMessage, OperationResult.APIError);
        return;
      }

      ShowOperationProgress("ローカライゼーション", "結果を待機中", 3, 3);
      UpdateLocalizationButtonState(true);

      var wait = await WaitForLocalizationResultAsync(targetSpaceId, 30000);
      isLocalizationInProgress = false;
      UpdateLocalizationButtonState(false);

      if (!wait.success)
      {
        HandleError("ローカライゼーション", wait.errorMessage, OperationResult.TimeoutError);
        return;
      }

      Debug.Log("[STM] Localization completed.");
    }
    catch (Exception ex)
    {
      isLocalizationInProgress = false;
      UpdateLocalizationButtonState(false);
      HandleError("ローカライゼーション", ex, OperationResult.UnknownError);
    }
  }

  // 対象 Space ID を決める（直近 Import 優先 → なければ端末内の1件）
  private async Task<string> GetTargetSpaceForLocalizationAsync()
  {
    try
    {
      if (!string.IsNullOrEmpty(lastImportedSpaceId))
      {
        Debug.Log($"[STM] Using lastImportedSpaceId: {lastImportedSpaceId}");
        return lastImportedSpaceId;
      }
      var latest = await GetLatestSpaceAsync();
      if (latest.HasValue) return latest.Value.MapUUID;
      return null;
    }
    catch (Exception ex)
    {
      Debug.LogError($"[STM] GetTargetSpaceForLocalizationAsync error: {ex.Message}");
      throw;
    }
  }

  // === Localization 要求（堅牢版）===
  private Task<(bool success, string errorMessage)> RequestLocalizationAsync(string targetMapId)
  {
    try
    {
      if (string.IsNullOrEmpty(targetMapId))
        return Task.FromResult((false, "ローカライズ対象のMap IDが空です"));

      // 端末に対象IDが存在するかチェック
      var listResult = localizationMapFeature.GetLocalizationMapsList(out LocalizationMap[] maps);
      if (listResult != XrResult.Success || maps == null || maps.Length == 0)
        return Task.FromResult((false, $"端末にローカライズ可能なMapがありません（GetLocalizationMapsList: {listResult}）"));

      bool exists = maps.Any(m => !string.IsNullOrEmpty(m.MapUUID) &&
                                  m.MapUUID.Equals(targetMapId, StringComparison.OrdinalIgnoreCase));
      if (!exists)
        return Task.FromResult((false, $"指定Mapが端末に見つかりません（ID: {targetMapId}）"));

      // 念のためイベント有効化（冪等）
      var enable = localizationMapFeature.EnableLocalizationEvents(true);
      Debug.Log($"[STM] EnableLocalizationEvents (pre-request): {enable}");

      // 要求
      var xr = localizationMapFeature.RequestMapLocalization(targetMapId);
      Debug.Log($"[STM] RequestMapLocalization({targetMapId}) -> {xr}");

      if (xr == XrResult.Success) return Task.FromResult((true, (string)null));
      return Task.FromResult((false, $"RequestMapLocalization 失敗: {xr}"));
    }
    catch (Exception ex)
    {
      return Task.FromResult((false, $"RequestLocalizationAsync 例外: {ex.Message}"));
    }
  }

  // === ローカライズ結果待ち（イベント + ポーリング + タイムアウト）===
  private async Task<(bool success, string errorMessage)> WaitForLocalizationResultAsync(string targetSpaceId, int timeoutMs)
  {
    // 既存の待受があれば破棄
    if (localizationTaskCompletionSource != null)
    {
      localizationTaskCompletionSource = null;
    }

    localizationTaskCompletionSource = new TaskCompletionSource<(bool, string)>();
    var timeoutTask = Task.Delay(timeoutMs);
    var pollingTask = CreatePollingTask(targetSpaceId, timeoutMs);

    var completed = await Task.WhenAny(localizationTaskCompletionSource.Task, pollingTask, timeoutTask);

    if (completed == localizationTaskCompletionSource.Task)
    {
      var r = await localizationTaskCompletionSource.Task;
      localizationTaskCompletionSource = null;
      return r;
    }
    if (completed == pollingTask)
    {
      var pr = await pollingTask;
      localizationTaskCompletionSource = null;
      return pr;
    }

    localizationTaskCompletionSource = null;
    return (false, $"ローカライゼーションがタイムアウトしました ({timeoutMs / 1000}秒)");
  }

  private async Task<(bool success, string errorMessage)> CreatePollingTask(string targetSpaceId, int timeoutMs)
  {
    var start = DateTime.Now;
    var interval = 1000;

    while ((DateTime.Now - start).TotalMilliseconds < timeoutMs)
    {
      try
      {
        var r = localizationMapFeature.GetLocalizationMapsList(out LocalizationMap[] maps);
        if (r == XrResult.Success && maps != null)
        {
          // 必要であればここで追加の状態確認を実装
        }
      }
      catch (Exception ex)
      {
        Debug.LogError($"[STM] Polling error: {ex.Message}");
      }
      await Task.Delay(interval);
    }
    return (false, "Polling task timed out - state change not detected");
  }

  // === Localization イベント（ここでシーンへ反映）===
  private void OnLocalizationChanged(LocalizationEventData eventData)
  {
    try
    {
      Debug.Log($"[STM] EVENT State: {eventData.State}, Map: {eventData.Map.MapUUID}");

      bool isRelevant = !string.IsNullOrEmpty(currentLocalizationTargetId) &&
                        eventData.Map.MapUUID.Equals(currentLocalizationTargetId, StringComparison.OrdinalIgnoreCase);

      switch (eventData.State)
      {
        case LocalizationMapState.Localized:
          if (isRelevant)
          {
            isLocalizationInProgress = false;

            // 既存成功処理（UI等）
            HandleLocalizationSuccess(eventData);

            // ===== Space 原点 Pose を取得してシーンへ反映 =====
            try
            {
              if (localizationMapFeature == null)
                localizationMapFeature = OpenXRSettings.Instance.GetFeature<MagicLeapLocalizationMapFeature>();

              var origin = localizationMapFeature.GetMapOrigin(); // Pose

              if (spaceOrigin == null)
              {
                var go = new GameObject("SpaceOrigin");
                spaceOrigin = go.transform;
              }

              spaceOrigin.SetPositionAndRotation(origin.position, origin.rotation);

              InvokeSnappersAfterOriginUpdate(); // ★ SpaceOrigin反映“直後”にスナップ明示トリガー

              if (contentRoot != null && contentRoot.parent != spaceOrigin)
              {
                contentRoot.SetParent(spaceOrigin, true); // ワールド位置維持
              }

              // 表示用（相対位置）
              var cam = Camera.main ? Camera.main.transform : null;
              string relativeLine = "空欄";
              if (cam != null)
              {
                Vector3 relPos = Quaternion.Inverse(origin.rotation) * (cam.position - origin.position);
                Vector3 relEuler = (Quaternion.Inverse(origin.rotation) * cam.rotation).eulerAngles;
                relativeLine = $"pos {relPos.x:F3},{relPos.y:F3},{relPos.z:F3}  rotEuler {relEuler.x:F1},{relEuler.y:F1},{relEuler.z:F1}";
              }

              string mapId = eventData.Map.MapUUID ?? "unknown";
              string displayName = $"Map-{mapId.Substring(0, Math.Min(8, mapId.Length))}";

              UpdateStatus($"ローカライズ完了\nMap：{displayName}\nID：{mapId}\nSpace原点からの相対位置：{relativeLine}");
            }
            catch (Exception ex)
            {
              Debug.LogWarning($"[STM] GetMapOrigin/scene apply failed: {ex.Message}");
            }

            // 待受に成功通知
            if (localizationTaskCompletionSource != null && !localizationTaskCompletionSource.Task.IsCompleted)
            {
              localizationTaskCompletionSource.TrySetResult((true, null));
            }
          }
          break;


        case LocalizationMapState.LocalizationPending:
          if (isRelevant)
          {
            UpdateProgressStatus(StatusMessages.LOCALIZATION_PROCESSING);
            isLocalizationInProgress = true;
          }
          break;

        case LocalizationMapState.NotLocalized:
          if (isRelevant && isLocalizationInProgress)
          {
            isLocalizationInProgress = false;
            string msg = "ローカライゼーションに失敗しました";
            HandleLocalizationFailure(msg);

            if (localizationTaskCompletionSource != null && !localizationTaskCompletionSource.Task.IsCompleted)
            {
              localizationTaskCompletionSource.TrySetResult((false, msg));
            }
          }
          break;

        default:
          if (isRelevant)
          {
            isLocalizationInProgress = false;
            string msg = $"不明なローカライゼーション状態: {eventData.State}";
            HandleLocalizationFailure(msg);

            if (localizationTaskCompletionSource != null && !localizationTaskCompletionSource.Task.IsCompleted)
            {
              localizationTaskCompletionSource.TrySetResult((false, msg));
            }
          }
          break;
      }
    }
    catch (Exception ex)
    {
      isLocalizationInProgress = false;
      HandleError("ローカライゼーションイベント処理", ex, OperationResult.UnknownError);

      if (localizationTaskCompletionSource != null && !localizationTaskCompletionSource.Task.IsCompleted)
      {
        localizationTaskCompletionSource.TrySetResult((false, $"イベント処理例外: {ex.Message}"));
      }
    }
  }

  // SpaceTestManager クラス内に追加（他メソッドの外）
  private void InvokeSnappersAfterOriginUpdate()
  {
    try
    {
#if UNITY_2022_2_OR_NEWER
      var snappers = FindObjectsByType<SnapToFloorOnLocalized>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
#else
    var snappers = FindObjectsOfType<SnapToFloorOnLocalized>(false);
#endif
      foreach (var s in snappers)
      {
        if (!s) continue;
        s.TriggerSnapAfterSpaceOriginUpdate(); // ★推奨経路：SpaceOrigin更新直後に実行
      }
    }
    catch (System.Exception ex)
    {
      Debug.LogWarning($"[STM] InvokeSnappersAfterOriginUpdate failed: {ex.Message}");
    }
  }


  private void HandleLocalizationSuccess(LocalizationEventData eventData)
  {
    try
    {
      string mapId = eventData.Map.MapUUID ?? "unknown";
      string mapName = $"Map-{mapId.Substring(0, Math.Min(8, mapId.Length))}";

      var pose = GetMapOriginPose();
      if (pose.success)
      {
        string poseInfo =
          $"デバイス位置: ({pose.position.x:F2},{pose.position.y:F2},{pose.position.z:F2})\n" +
          $"デバイス回転: ({pose.rotation.eulerAngles.x:F1}°, {pose.rotation.eulerAngles.y:F1}°, {pose.rotation.eulerAngles.z:F1}°)";
        ShowOperationSuccess("ローカライゼーション", $"成功\nMap: {mapName}\n{poseInfo}");
      }
      else
      {
        ShowOperationSuccess("ローカライゼーション", $"成功\nMap: {mapName}\n(原点ポーズ未取得)");
      }
    }
    catch (Exception ex)
    {
      HandleError("ローカライゼーション成功後処理", ex, OperationResult.UnknownError);
    }
  }

  private void HandleLocalizationFailure(string reason)
  {
    HandleError("ローカライゼーション", reason, OperationResult.APIError);
  }

  private (bool success, Vector3 position, Quaternion rotation) GetMapOriginPose()
  {
    try
    {
      if (Camera.main)
      {
        return (true, Camera.main.transform.position, Camera.main.transform.rotation);
      }
      return (false, Vector3.zero, Quaternion.identity);
    }
    catch (Exception ex)
    {
      Debug.LogError($"[STM] GetMapOriginPose exception: {ex.Message}");
      return (false, Vector3.zero, Quaternion.identity);
    }
  }

  // =======================================================================
  // Space 取得/Export/Import
  // =======================================================================

  private Task<LocalizationMap?> GetLatestSpaceAsync()
  {
    try
    {
      var r = localizationMapFeature.GetLocalizationMapsList(out LocalizationMap[] maps);
      if (r != XrResult.Success || maps == null || maps.Length == 0)
      {
        Debug.LogWarning("[STM] No localization maps found.");
        return Task.FromResult<LocalizationMap?>(null);
      }
      // 「最新」の定義がAPIで取れないため先頭を返す（必要なら独自基準で絞る）
      var latest = maps[0];
      return Task.FromResult<LocalizationMap?>(latest);
    }
    catch (Exception ex)
    {
      Debug.LogError($"[STM] GetLatestSpaceAsync error: {ex.Message}");
      throw;
    }
  }

  private Task<(bool success, byte[] data, string errorMessage)> ExportSpaceAsync(string mapId)
  {
    try
    {
      var r = localizationMapFeature.ExportLocalizationMap(mapId, out byte[] data);
      if (r == XrResult.Success)
      {
        if (data != null && data.Length > 0)
          return Task.FromResult((true, data, (string)null));
        return Task.FromResult((false, (byte[])null, "エクスポートされたデータが空です"));
      }
      return Task.FromResult((false, (byte[])null, $"エクスポートAPIが失敗しました: {r}"));
    }
    catch (Exception ex)
    {
      return Task.FromResult((false, (byte[])null, ex.Message));
    }
  }

  private async Task<(bool success, string errorMessage)> SaveSpaceToFileAsync(byte[] spaceData)
  {
    try
    {
      var filePath = SPACE_FILE_PATH;
      var dir = Path.GetDirectoryName(filePath);
      if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
      await Task.Run(() => File.WriteAllBytes(filePath, spaceData));
      if (File.Exists(filePath)) return (true, null);
      return (false, "ファイルの作成に失敗しました");
    }
    catch (UnauthorizedAccessException ex) { return (false, $"書き込み権限がありません: {ex.Message}"); }
    catch (DirectoryNotFoundException ex) { return (false, $"ディレクトリが見つかりません: {ex.Message}"); }
    catch (IOException ex) { return (false, $"ファイルI/Oエラー: {ex.Message}"); }
    catch (Exception ex) { return (false, $"予期しないエラー: {ex.Message}"); }
  }

  private async Task<(bool success, byte[] data, string errorMessage)> ReadSpaceFromFileAsync()
  {
    try
    {
      var filePath = SPACE_FILE_PATH;
      if (!File.Exists(filePath)) return (false, null, $"インポート用ファイルが見つかりません: {filePath}");
      var data = await Task.Run(() => File.ReadAllBytes(filePath));
      if (data == null || data.Length == 0) return (false, null, "ファイルが空または読み込み不可");
      return (true, data, null);
    }
    catch (UnauthorizedAccessException ex) { return (false, null, $"読み込み権限がありません: {ex.Message}"); }
    catch (DirectoryNotFoundException ex) { return (false, null, $"ディレクトリが見つかりません: {ex.Message}"); }
    catch (FileNotFoundException ex) { return (false, null, $"インポート用ファイルが見つかりません: {ex.Message}"); }
    catch (IOException ex) { return (false, null, $"ファイルI/Oエラー: {ex.Message}"); }
    catch (Exception ex) { return (false, null, $"予期しないエラー: {ex.Message}"); }
  }

  private Task<(bool success, string spaceId, string errorMessage)> ImportSpaceAsync(byte[] spaceData)
  {
    try
    {
      var r = localizationMapFeature.ImportLocalizationMap(spaceData, out string importedId);
      if (r == XrResult.Success)
      {
        if (!string.IsNullOrEmpty(importedId)) return Task.FromResult((true, importedId, (string)null));
        return Task.FromResult((false, (string)null, "インポートされたSpace IDが空です"));
      }
      return Task.FromResult((false, (string)null, $"インポートAPIが失敗しました: {r}"));
    }
    catch (Exception ex)
    {
      return Task.FromResult((false, (string)null, ex.Message));
    }
  }

  // ID → 表示名（Name が取れない場合は ID の先頭8桁で代替）
  private Task<string> GetMapNameByIdAsync(string spaceId)
  {
    try
    {
      var r = localizationMapFeature.GetLocalizationMapsList(out LocalizationMap[] maps);
      if (r != XrResult.Success || maps == null || maps.Length == 0) return Task.FromResult<string>(null);

      var m = maps.FirstOrDefault(x => x.MapUUID != null &&
                                       x.MapUUID.Equals(spaceId, StringComparison.OrdinalIgnoreCase));
      if (m.MapUUID != null)
      {
        return Task.FromResult($"Map-{spaceId.Substring(0, Math.Min(8, spaceId.Length))}");
      }
      return Task.FromResult<string>(null);
    }
    catch (Exception ex)
    {
      Debug.LogError($"[STM] GetMapNameByIdAsync error: {ex.Message}");
      return Task.FromResult<string>(null);
    }
  }

  // =======================================================================
  // 権限
  // =======================================================================

  private async Task<bool> CheckAndRequestPermissionsAsync()
  {
    UpdateProgressStatus(StatusMessages.CHECKING_PERMISSIONS);

    try
    {
      await FindWorkingPermissionStrings();

      bool spaceManagerGranted = CheckPermissionGranted(workingSpaceManagerPermission);
      bool spaceImportExportGranted = CheckPermissionGranted(workingSpaceImportExportPermission);
      bool spatialAnchorGranted = CheckPermissionGranted(workingSpatialAnchorPermission);
      bool networkGranted = CheckPermissionGranted(workingNetworkPermission);

      if (spaceManagerGranted && spaceImportExportGranted && networkGranted)
      {
        permissionsGranted = true;
        UpdateStatus(StatusMessages.PERMISSIONS_GRANTED);
        EnableSpaceFunctions();
        return true;
      }

      UpdateProgressStatus(StatusMessages.REQUESTING_PERMISSIONS);

      if (!spaceImportExportGranted && !string.IsNullOrEmpty(workingSpaceImportExportPermission))
      {
        ShowOperationProgress("権限要求", "SPACE_IMPORT_EXPORT 権限を要求中", 1, 4);
        Permission.RequestUserPermission(workingSpaceImportExportPermission);
        await WaitForPermissionResponse(workingSpaceImportExportPermission);
      }

      // 外部ストレージ系（必要な場合のみ効く環境）
      ShowOperationProgress("権限要求", "外部ストレージ権限を要求中", 2, 4);
      if (!Permission.HasUserAuthorizedPermission("android.permission.WRITE_EXTERNAL_STORAGE"))
      {
        Permission.RequestUserPermission("android.permission.WRITE_EXTERNAL_STORAGE");
        await WaitForPermissionResponse("android.permission.WRITE_EXTERNAL_STORAGE");
      }
      if (!Permission.HasUserAuthorizedPermission("android.permission.READ_EXTERNAL_STORAGE"))
      {
        Permission.RequestUserPermission("android.permission.READ_EXTERNAL_STORAGE");
        await WaitForPermissionResponse("android.permission.READ_EXTERNAL_STORAGE");
      }

      if (!spaceManagerGranted && !string.IsNullOrEmpty(workingSpaceManagerPermission))
      {
        ShowOperationProgress("権限要求", "SPACE_MANAGER 権限を要求中", 3, 4);
        foreach (var p in SPACE_MANAGER_PERMISSIONS)
        {
          Permission.RequestUserPermission(p);
          await WaitForPermissionResponse(p);
          if (CheckPermissionGranted(p)) { workingSpaceManagerPermission = p; break; }
        }
      }

      if (!spatialAnchorGranted && !string.IsNullOrEmpty(workingSpatialAnchorPermission))
      {
        ShowOperationProgress("権限要求", "SPATIAL_ANCHOR 権限を要求中", 4, 4);
        foreach (var p in SPATIAL_ANCHOR_PERMISSIONS)
        {
          Permission.RequestUserPermission(p);
          await WaitForPermissionResponse(p);
          if (CheckPermissionGranted(p)) { workingSpatialAnchorPermission = p; break; }
        }
      }

      bool allGranted =
        CheckPermissionGranted(workingSpaceManagerPermission) &&
        CheckPermissionGranted(workingSpaceImportExportPermission);

      if (allGranted)
      {
        permissionsGranted = true;
        UpdateStatus(StatusMessages.PERMISSIONS_GRANTED);
        EnableSpaceFunctions();
        return true;
      }

      HandlePermissionDenied();
      return false;
    }
    catch (Exception ex)
    {
      HandleError("権限チェック", ex, OperationResult.PermissionDenied);
      return false;
    }
  }

  private async Task FindWorkingPermissionStrings()
  {
    if (string.IsNullOrEmpty(workingSpaceManagerPermission))
      workingSpaceManagerPermission = SPACE_MANAGER_PERMISSIONS[0];

    if (string.IsNullOrEmpty(workingSpaceImportExportPermission))
      workingSpaceImportExportPermission = SPACE_IMPORT_EXPORT_PERMISSIONS[0];

    if (string.IsNullOrEmpty(workingSpatialAnchorPermission))
      workingSpatialAnchorPermission = SPATIAL_ANCHOR_PERMISSIONS[0];

    if (string.IsNullOrEmpty(workingNetworkPermission))
      workingNetworkPermission = NETWORK_PERMISSIONS[0];

    await Task.Delay(50);
  }

  private bool CheckPermissionGranted(string permission)
  {
    if (string.IsNullOrEmpty(permission)) return false;
    return Permission.HasUserAuthorizedPermission(permission);
  }

  private async Task WaitForPermissionResponse(string permission)
  {
    int max = 30;
    int i = 0;
    while (i < max)
    {
      await Task.Delay(1000);
      if (Permission.HasUserAuthorizedPermission(permission)) break;
      i++;
    }
  }

  private void HandlePermissionDenied()
  {
    permissionsGranted = false;

    bool sm = CheckPermissionGranted(workingSpaceManagerPermission);
    bool ie = CheckPermissionGranted(workingSpaceImportExportPermission);
    bool sa = CheckPermissionGranted(workingSpatialAnchorPermission);

    string denied = "";
    if (!sm) denied += "SPACE_MANAGER ";
    if (!ie) denied += "SPACE_IMPORT_EXPORT ";
    if (!sa) denied += "SPATIAL_ANCHOR ";

    HandleError("権限要求", $"{ErrorMessages.PERMISSION_DENIED}\n拒否: {denied}", OperationResult.PermissionDenied);
    DisableSpaceFunctions();
  }

  private void EnableSpaceFunctions()
  {
    if (this != null && gameObject != null) Invoke(nameof(EnableSpaceFunctionsInternal), 0.05f);
    else EnableSpaceFunctionsInternal();
  }

  private void EnableSpaceFunctionsInternal()
  {
    if (exportButton) { exportButton.interactable = true; var xri = exportButton.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>(); if (xri) xri.enabled = true; }
    if (importButton) { importButton.interactable = true; var xri = importButton.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>(); if (xri) xri.enabled = true; }
    if (localizeButton) { localizeButton.interactable = true; var xri = localizeButton.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>(); if (xri) xri.enabled = true; }
  }

  private void DisableSpaceFunctions()
  {
    if (exportButton) { exportButton.interactable = false; var xri = exportButton.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>(); if (xri) xri.enabled = false; }
    if (importButton) { importButton.interactable = false; var xri = importButton.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>(); if (xri) xri.enabled = false; }
    if (localizeButton) { localizeButton.interactable = false; var xri = localizeButton.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>(); if (xri) xri.enabled = false; }
  }

  // =======================================================================
  // UI / エラー
  // =======================================================================

  private static class ErrorMessages
  {
    public const string PERMISSION_DENIED = "権限が拒否されました。設定で権限を有効にしてください。";
    public const string NO_SPACE_FOUND = "エクスポート可能なSpaceが見つかりません。";
    public const string FILE_NOT_FOUND = "インポート用ファイルが見つかりません。";
    public const string EXPORT_FAILED = "Spaceのエクスポートに失敗しました。";
    public const string IMPORT_FAILED = "Spaceのインポートに失敗しました。";
    public const string LOCALIZATION_FAILED = "ローカライゼーションに失敗しました。";
    public const string FEATURE_NOT_AVAILABLE = "ローカライゼーション機能が利用できません。";
    public const string OPERATION_IN_PROGRESS = "処理が既に実行中です。";
    public const string FILE_ACCESS_ERROR = "ファイルへのアクセスに失敗しました。";
    public const string NETWORK_ERROR = "ネットワークエラーが発生しました。";
    public const string UNKNOWN_ERROR = "予期しないエラーが発生しました。";
    public const string INITIALIZATION_ERROR = "初期化に失敗しました。";
    public const string API_ERROR = "APIの呼び出しに失敗しました。";
  }

  public enum OperationResult
  {
    Success,
    PermissionDenied,
    NoSpaceFound,
    FileNotFound,
    APIError,
    NetworkError,
    FileAccessError,
    OperationInProgress,
    FeatureNotAvailable,
    InitializationError,
    TimeoutError,
    UnknownError
  }

  private void HandleError(string operation, Exception ex, OperationResult type = OperationResult.UnknownError)
  {
    string user = GetUserFriendlyErrorMessage(type, ex.Message);
    UpdateStatus(user);
    Debug.LogError($"[STM] Error {operation} ({type}) -> {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
  }

  private void HandleError(string operation, string message, OperationResult type = OperationResult.UnknownError)
  {
    string user = GetUserFriendlyErrorMessage(type, message);
    UpdateStatus(user);
    Debug.LogError($"[STM] Error {operation} ({type}) -> {message}");
  }

  private string GetUserFriendlyErrorMessage(OperationResult type, string details = "")
  {
    string baseMsg = type switch
    {
      OperationResult.PermissionDenied => ErrorMessages.PERMISSION_DENIED,
      OperationResult.NoSpaceFound => ErrorMessages.NO_SPACE_FOUND,
      OperationResult.FileNotFound => ErrorMessages.FILE_NOT_FOUND,
      OperationResult.APIError => ErrorMessages.API_ERROR,
      OperationResult.NetworkError => ErrorMessages.NETWORK_ERROR,
      OperationResult.FileAccessError => ErrorMessages.FILE_ACCESS_ERROR,
      OperationResult.OperationInProgress => ErrorMessages.OPERATION_IN_PROGRESS,
      OperationResult.FeatureNotAvailable => ErrorMessages.FEATURE_NOT_AVAILABLE,
      OperationResult.InitializationError => ErrorMessages.INITIALIZATION_ERROR,
      _ => ErrorMessages.UNKNOWN_ERROR
    };
    if (!string.IsNullOrEmpty(details) && ShouldShowDetailsToUser(type))
      return $"{baseMsg}\n詳細: {details}";
    return baseMsg;
  }

  private bool ShouldShowDetailsToUser(OperationResult type)
  {
    return type switch
    {
      OperationResult.FileNotFound => true,
      OperationResult.FileAccessError => true,
      OperationResult.NoSpaceFound => true,
      _ => false
    };
  }

  private void UpdateStatus(string message, bool isProgress = false, bool isError = false)
  {
    if (statusText)
    {
      string msg = (isError || !isProgress) ? $"[{DateTime.Now:HH:mm:ss}] {message}" : message;
      statusText.text = msg;
      statusText.color = isError ? Color.red : (isProgress ? Color.yellow : Color.white);
    }
    if (isError) Debug.LogError($"[STM] STATUS ERR: {message}");
    else if (isProgress) Debug.Log($"[STM] STATUS PROG: {message}");
    else Debug.Log($"[STM] STATUS: {message}");
  }

  private void UpdateStatus(string message) => UpdateStatus(message, false, false);
  private void UpdateProgressStatus(string message) => UpdateStatus(message, true, false);
  private void UpdateErrorStatus(string message) => UpdateStatus(message, false, true);

  private void ShowOperationSuccess(string operation, string details = "")
  {
    string msg = string.IsNullOrEmpty(details) ? $"{operation}が完了しました" : $"{operation}が完了しました\n{details}";
    UpdateStatus(msg);
    Debug.Log($"[STM] SUCCESS {operation}: {details}");
  }

  private void ShowOperationProgress(string operation, string step, int currentStep = 0, int totalSteps = 0)
  {
    string msg = totalSteps > 0 ? $"{operation} ({currentStep}/{totalSteps}): {step}" : $"{operation}: {step}";
    UpdateProgressStatus(msg);
  }

  private void UpdateLocalizationButtonState(bool isLocalizing)
  {
    if (!localizeButton) return;
    var t = localizeButton.GetComponentInChildren<Text>();
    if (t) t.text = isLocalizing ? "キャンセル" : "ローカライゼーション";

    localizeButton.onClick.RemoveAllListeners();
    if (isLocalizing) localizeButton.onClick.AddListener(CancelLocalization);
    else localizeButton.onClick.AddListener(OnLocalizeButtonClicked);

    var xri = localizeButton.GetComponent<UnityEngine.XR.Interaction.Toolkit.Interactables.XRSimpleInteractable>();
    if (xri != null)
    {
      xri.selectEntered.RemoveAllListeners();
      if (isLocalizing) xri.selectEntered.AddListener(_ => CancelLocalization());
      else xri.selectEntered.AddListener(_ => OnLocalizeButtonClicked());
    }
  }

  public void CancelLocalization()
  {
    if (!isLocalizationInProgress) return;
    isLocalizationInProgress = false;

    if (localizationTaskCompletionSource != null && !localizationTaskCompletionSource.Task.IsCompleted)
    {
      localizationTaskCompletionSource.TrySetResult((false, "ユーザーによりローカライゼーションがキャンセルされました"));
      localizationTaskCompletionSource = null;
    }
    UpdateStatus("ローカライゼーションがキャンセルされました");
    UpdateLocalizationButtonState(false);
  }

  // =======================================================================
  // 補助
  // =======================================================================

  private bool CanCreateDirectory(string directoryPath)
  {
    try
    {
      if (Directory.Exists(directoryPath)) return true;
      Directory.CreateDirectory(directoryPath);
      return Directory.Exists(directoryPath);
    }
    catch (Exception ex)
    {
      Debug.LogWarning($"[STM] Cannot create directory {directoryPath}: {ex.Message}");
      return false;
    }
  }

  // ==== ステータスメッセージ ====
  private static class StatusMessages
  {
    public const string INITIALIZING = "Space Test Manager初期化中...";
    public const string CHECKING_PERMISSIONS = "権限をチェック中...";
    public const string REQUESTING_PERMISSIONS = "必要な権限を要求中...";
    public const string PERMISSIONS_GRANTED = "権限が付与されました";
    public const string EXPORTING_SPACE = "Spaceをエクスポート中...";
    public const string EXPORT_SUCCESS = "Spaceのエクスポートが完了しました";
    public const string IMPORTING_SPACE = "Spaceをインポート中...";
    public const string IMPORT_SUCCESS = "Spaceのインポートが完了しました";
    public const string LOCALIZING = "ローカライゼーションを開始中...";
    public const string LOCALIZATION_REQUESTING = "ローカライゼーション要求を送信しました。結果を待機中...";
    public const string LOCALIZATION_PROCESSING = "ローカライゼーション処理中...";
    public const string LOCALIZATION_SUCCESS = "ローカライゼーション成功!";
    public const string READY = "準備完了。操作を選択してください。";
  }
}