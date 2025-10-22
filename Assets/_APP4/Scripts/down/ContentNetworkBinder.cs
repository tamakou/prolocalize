using System.Collections;
using System.Linq;
using UnityEngine;
using Fusion;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.LocalizationMaps; // MagicLeapLocalizationMapFeature, LocalizationEventData

/// <summary>
/// Space 原点（ML2ローカライズ）とネットワーク基準(NET_AnchorBridge)を橋渡し。
/// - ホスト: Localized 後に NET_AnchorBridge を Space原点Pose に合わせる
/// - 全員: Cube/道路標識の親(contentRoot)を NET_AnchorBridge の子にして以後ネットワークTransformで同期
/// - ゲスト: ローカライズボタンを押しても原点にスナップせず、常にホストの接地位置に一致
/// </summary>
[DisallowMultipleComponent]
public class ContentNetworkBinder : MonoBehaviour
{
  [Header("Targets")]
  [Tooltip("表示コンテンツの親（これ以下に Cube / 道路標識 をぶら下げる）")]
  public Transform contentRoot;

  [Tooltip("Space 原点 Transform（任意）。未指定でも ML2 API から取得可能")]
  public Transform spaceOrigin;

  [Header("Network Anchor")]
  [Tooltip("優先検索するアンカー名（NetworkObjectのGameObject名）。先頭が最優先。")]
  public string[] anchorNamePriority = new[] { "NET_AnchorBridge", "AnchorNetBridge" };

  [Header("Options")]
  [Tooltip("Runner 未検出時はホスト扱い（開発用）。本番は false 推奨")]
  public bool treatNoRunnerAsHost = false;

  [Tooltip("ML2 ローカライゼーションイベントを購読して自動アライン")]
  public bool autoAlignOnLocalized = true;

  private NetworkRunner _runner;
  private NetworkObject _netAnchor;
  private MagicLeapLocalizationMapFeature _loc;
  private bool _subscribed;

  private IEnumerator Start()
  {
    // Runner 待ち
    yield return WaitRunner(5f);

    // アンカー検出
    yield return WaitAnchor(5f);

    // 初回の親子づけ（ローカライズ前でも子にしておく）
    TryBindContentToAnchor();

    // ML2 ローカライゼーションイベント購読
    if (autoAlignOnLocalized)
    {
      _loc = OpenXRSettings.Instance?.GetFeature<MagicLeapLocalizationMapFeature>();
      if (_loc != null)
      {
        MagicLeapLocalizationMapFeature.OnLocalizationChangedEvent += OnLocalizationChanged;
        _subscribed = true;
      }
      else
      {
        Debug.LogWarning("[ContentNetworkBinder] MagicLeapLocalizationMapFeature が見つからない/無効です。");
      }
    }
  }

  private void OnDestroy()
  {
    if (_subscribed)
    {
      MagicLeapLocalizationMapFeature.OnLocalizationChangedEvent -= OnLocalizationChanged;
      _subscribed = false;
    }
  }

  private void OnLocalizationChanged(LocalizationEventData ev)
  {
    if (ev.State != LocalizationMapState.Localized) return;

    // Localized → Space原点Pose 取得し、ホストなら NET_AnchorBridge に反映
    AlignAnchorToSpaceOriginIfHost();

    // 親子づけ保証（ローカライズ後にSpaceTestManagerが何かしても最終的にAnchor配下に戻す）
    TryBindContentToAnchor();
  }

  /// <summary>
  /// Space原点が外部コードで更新されたときに手動で呼ぶためのフック（任意）
  /// </summary>
  public void OnSpaceOriginUpdated()
  {
    AlignAnchorToSpaceOriginIfHost();
    TryBindContentToAnchor();
  }

  private void AlignAnchorToSpaceOriginIfHost()
  {
    if (_netAnchor == null) FindAnchorNow();
    if (_netAnchor == null) return;
    if (!IsHost()) return; // ホストのみアンカー位置を決める

    // Space 原点 Pose 取得
    Vector3 pos;
    Quaternion rot;

    if (spaceOrigin != null)
    {
      pos = spaceOrigin.position;
      rot = spaceOrigin.rotation;
    }
    else
    {
      var feature = _loc ?? OpenXRSettings.Instance?.GetFeature<MagicLeapLocalizationMapFeature>();
      if (feature == null)
      {
        Debug.LogWarning("[ContentNetworkBinder] spaceOrigin未指定、かつ ML2 feature取得不可。アラインをスキップします。");
        return;
      }
      var origin = feature.GetMapOrigin();
      pos = origin.position;
      rot = origin.rotation;
    }

    _netAnchor.transform.SetPositionAndRotation(pos, rot); // NetworkTransform が配信
    Debug.Log("[ContentNetworkBinder] Anchor aligned to SpaceOrigin (Host).");
  }

  private void TryBindContentToAnchor()
  {
    if (!contentRoot || _netAnchor == null) return;

    if (contentRoot.parent != _netAnchor.transform)
    {
      contentRoot.SetParent(_netAnchor.transform, true); // ワールド座標維持
      Debug.Log("[ContentNetworkBinder] contentRoot -> NET_AnchorBridge の子に設定。以後はネットワーク同期に追従。");
    }
  }

  private IEnumerator WaitRunner(float timeout)
  {
    float t = 0f;
    while (t < timeout)
    {
      _runner = FindFirstObjectByType<NetworkRunner>();
      if (_runner && _runner.IsRunning) yield break;
      t += Time.deltaTime;
      yield return null;
    }
    _runner = null;
  }

  private IEnumerator WaitAnchor(float timeout)
  {
    float t = 0f;
    while (t < timeout)
    {
      if (FindAnchorNow()) yield break;
      t += Time.deltaTime;
      yield return null;
    }
  }

  private bool FindAnchorNow()
  {
    var all = FindObjectsByType<NetworkObject>(FindObjectsInactive.Include, FindObjectsSortMode.None);

    foreach (var key in anchorNamePriority)
    {
      _netAnchor = all.FirstOrDefault(no =>
        no != null && no.name.IndexOf(key, System.StringComparison.OrdinalIgnoreCase) >= 0);
      if (_netAnchor) break;
    }

    if (_netAnchor)
    {
      Debug.Log($"[ContentNetworkBinder] Anchor found: {_netAnchor.name}");
      return true;
    }

    Debug.Log("[ContentNetworkBinder] Anchor not found yet.");
    return false;
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
}
