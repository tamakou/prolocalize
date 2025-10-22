// Assets/Scripts/FusionColocationStarter.cs
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.LocalizationMaps; // ← ML2 Localization Maps
using Fusion;

public class FusionColocationStarter : MonoBehaviour
{
  [Header("Fusion Start Mode")]
  [Tooltip("ON=AutoHostOrClient, OFF=Shared")]
  public bool useAutoHostOrClient = false;

  [Header("Debug")]
  public bool logVerbose = true;
  public bool dontDestroyRunner = true;

  private MagicLeapLocalizationMapFeature _feature;
  private NetworkRunner _runner;
  private bool _started;

  void Awake()
  {
    // ML2 Localization Maps Feature を取得＆イベント有効化
    _feature = OpenXRSettings.Instance.GetFeature<MagicLeapLocalizationMapFeature>();
    if (_feature == null || !_feature.enabled)
    {
      Debug.LogError("[Colocation] MagicLeapLocalizationMapFeature not found or disabled.");
      enabled = false;
      return;
    }

    var xr = _feature.EnableLocalizationEvents(true);
    if (logVerbose) Debug.Log($"[Colocation] EnableLocalizationEvents: {xr}");

    MagicLeapLocalizationMapFeature.OnLocalizationChangedEvent += OnLocalizationChanged;
  }

  void OnDestroy()
  {
    MagicLeapLocalizationMapFeature.OnLocalizationChangedEvent -= OnLocalizationChanged;
  }

  private void OnLocalizationChanged(LocalizationEventData evt)
  {
    if (evt.State != LocalizationMapState.Localized) return;
    if (string.IsNullOrEmpty(evt.Map.MapUUID)) return;
    if (_started) return;

    _started = true;

    var id = evt.Map.MapUUID;
    var shortId = id.Length >= 8 ? id.Substring(0, 8) : id;
    var room = $"ml2-map-{shortId}";

    if (logVerbose)
    {
      var name = string.IsNullOrEmpty(evt.Map.Name) ? "名前なし" : evt.Map.Name;
      Debug.Log($"[Colocation] Localized Map: {name} ({id}) → Room={room}");
    }

    _ = StartFusionRunnerAsync(room); // fire & forget
  }

  private async Task StartFusionRunnerAsync(string room)
  {
    if (_runner && _runner.IsRunning)
    {
      if (logVerbose) Debug.Log("[Colocation] Runner already running.");
      return;
    }

    // NetworkRunner を動的生成
    var go = new GameObject("NetworkRunner");
    if (dontDestroyRunner) DontDestroyOnLoad(go);

    _runner = go.AddComponent<NetworkRunner>();
    _runner.ProvideInput = false; // 入力同期が必要なら true

    // シーン管理（現在のアクティブシーンを維持したいのでデフォルトでOK）
    var sceneMgr = go.AddComponent<NetworkSceneManagerDefault>();

    var mode = useAutoHostOrClient ? GameMode.AutoHostOrClient : GameMode.Shared;

    var args = new StartGameArgs
    {
      GameMode = mode,
      SessionName = room,
      SceneManager = sceneMgr,
      // Scene を指定しなければ今のシーン維持で起動
    };

    if (logVerbose) Debug.Log($"[Colocation] Starting Fusion2: {mode}, room={room}");

    var result = await _runner.StartGame(args);
    if (!result.Ok)
    {
      Debug.LogError($"[Colocation] Runner start failed: {result.ShutdownReason}");
    }
    else if (logVerbose)
    {
      Debug.Log($"[Colocation] Runner started. IsServer={_runner.IsServer} IsClient={_runner.IsClient}");
    }
  }
}
