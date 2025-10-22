using System.Collections;
using System.Linq;
using UnityEngine;
using Fusion;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.LocalizationMaps; // MagicLeapLocalizationMapFeature, LocalizationEventData

/// <summary>
/// Space ���_�iML2���[�J���C�Y�j�ƃl�b�g���[�N�(NET_AnchorBridge)�����n���B
/// - �z�X�g: Localized ��� NET_AnchorBridge �� Space���_Pose �ɍ��킹��
/// - �S��: Cube/���H�W���̐e(contentRoot)�� NET_AnchorBridge �̎q�ɂ��ĈȌ�l�b�g���[�NTransform�œ���
/// - �Q�X�g: ���[�J���C�Y�{�^���������Ă����_�ɃX�i�b�v�����A��Ƀz�X�g�̐ڒn�ʒu�Ɉ�v
/// </summary>
[DisallowMultipleComponent]
public class ContentNetworkBinder : MonoBehaviour
{
  [Header("Targets")]
  [Tooltip("�\���R���e���c�̐e�i����ȉ��� Cube / ���H�W�� ���Ԃ牺����j")]
  public Transform contentRoot;

  [Tooltip("Space ���_ Transform�i�C�Ӂj�B���w��ł� ML2 API ����擾�\")]
  public Transform spaceOrigin;

  [Header("Network Anchor")]
  [Tooltip("�D�挟������A���J�[���iNetworkObject��GameObject���j�B�擪���ŗD��B")]
  public string[] anchorNamePriority = new[] { "NET_AnchorBridge", "AnchorNetBridge" };

  [Header("Options")]
  [Tooltip("Runner �����o���̓z�X�g�����i�J���p�j�B�{�Ԃ� false ����")]
  public bool treatNoRunnerAsHost = false;

  [Tooltip("ML2 ���[�J���C�[�[�V�����C�x���g���w�ǂ��Ď����A���C��")]
  public bool autoAlignOnLocalized = true;

  private NetworkRunner _runner;
  private NetworkObject _netAnchor;
  private MagicLeapLocalizationMapFeature _loc;
  private bool _subscribed;

  private IEnumerator Start()
  {
    // Runner �҂�
    yield return WaitRunner(5f);

    // �A���J�[���o
    yield return WaitAnchor(5f);

    // ����̐e�q�Â��i���[�J���C�Y�O�ł��q�ɂ��Ă����j
    TryBindContentToAnchor();

    // ML2 ���[�J���C�[�[�V�����C�x���g�w��
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
        Debug.LogWarning("[ContentNetworkBinder] MagicLeapLocalizationMapFeature ��������Ȃ�/�����ł��B");
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

    // Localized �� Space���_Pose �擾���A�z�X�g�Ȃ� NET_AnchorBridge �ɔ��f
    AlignAnchorToSpaceOriginIfHost();

    // �e�q�Â��ۏ؁i���[�J���C�Y���SpaceTestManager���������Ă��ŏI�I��Anchor�z���ɖ߂��j
    TryBindContentToAnchor();
  }

  /// <summary>
  /// Space���_���O���R�[�h�ōX�V���ꂽ�Ƃ��Ɏ蓮�ŌĂԂ��߂̃t�b�N�i�C�Ӂj
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
    if (!IsHost()) return; // �z�X�g�̂݃A���J�[�ʒu�����߂�

    // Space ���_ Pose �擾
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
        Debug.LogWarning("[ContentNetworkBinder] spaceOrigin���w��A���� ML2 feature�擾�s�B�A���C�����X�L�b�v���܂��B");
        return;
      }
      var origin = feature.GetMapOrigin();
      pos = origin.position;
      rot = origin.rotation;
    }

    _netAnchor.transform.SetPositionAndRotation(pos, rot); // NetworkTransform ���z�M
    Debug.Log("[ContentNetworkBinder] Anchor aligned to SpaceOrigin (Host).");
  }

  private void TryBindContentToAnchor()
  {
    if (!contentRoot || _netAnchor == null) return;

    if (contentRoot.parent != _netAnchor.transform)
    {
      contentRoot.SetParent(_netAnchor.transform, true); // ���[���h���W�ێ�
      Debug.Log("[ContentNetworkBinder] contentRoot -> NET_AnchorBridge �̎q�ɐݒ�B�Ȍ�̓l�b�g���[�N�����ɒǏ]�B");
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
