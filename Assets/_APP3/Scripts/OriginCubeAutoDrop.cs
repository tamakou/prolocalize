using System.Collections;
using Fusion;
using UnityEngine;

[RequireComponent(typeof(Collider))]
public class OriginCubeAutoDrop : NetworkBehaviour
{
  [Header("Raycast Settings")]
  [SerializeField] private LayerMask floorLayers = ~0;   // ML Meshing のレイヤーを含める
  [SerializeField] private float rayStartYOffset = 2.0f; // 原点からのレイ開始高さ
  [SerializeField] private float maxRayDistance = 6.0f;  // 落下可能距離
  [SerializeField] private float dropSpeed = 0.8f;       // 目視しやすい落下アニメ速度
  [SerializeField] private float skin = 0.002f;          // めり込み防止

  [Networked] public bool HasSnapped { get; private set; }

  private Transform _spaceOrigin;

  /// <summary>ローカライズ完了後に呼ぶ</summary>
  public void Begin(Transform spaceOrigin)
  {
    _spaceOrigin = spaceOrigin;
    if (HasSnapped) return;
    if (Object != null && !Object.HasStateAuthority) return; // 決定は StateAuthority のみ
    StopAllCoroutines();
    StartCoroutine(DropRoutine());
  }

  private IEnumerator DropRoutine()
  {
    var col = GetComponent<Collider>();
    float halfY = col.bounds.extents.y;

    // 原点の少し上から下向きレイ
    Vector3 rayStart = _spaceOrigin.position + Vector3.up * rayStartYOffset;
    if (!Physics.Raycast(rayStart, Vector3.down, out var hit,
        rayStartYOffset + maxRayDistance, floorLayers, QueryTriggerInteraction.Ignore))
    {
      yield break; // 床が取れない時は何もしない
    }

    float targetY = hit.point.y + halfY + skin;

    // まずキューブを原点の少し上に置いて可視落下
    Vector3 pos = _spaceOrigin.position + Vector3.up * rayStartYOffset;
    transform.position = pos;

    // アニメーションで接地まで下げる
    while (pos.y > targetY)
    {
      pos.y = Mathf.Max(targetY, pos.y - dropSpeed * Time.deltaTime);
      transform.position = pos;
      yield return null;
    }
    transform.position = pos;

    HasSnapped = true; // 同期フラグ
  }

  /// <summary>落下後のY差分（contentRoot用）</summary>
  public float GetOffsetYFromOrigin()
  {
    return transform.position.y - (_spaceOrigin ? _spaceOrigin.position.y : 0f);
  }
}
