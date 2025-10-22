// Assets/_APP/Scripts/grab/Ml2NetworkGrabbable.cs
using System;
using Fusion;
using UnityEngine;

[RequireComponent(typeof(NetworkObject))]
public class Ml2NetworkGrabbable : NetworkBehaviour, IStateAuthorityChanged
{
    [Header("Behaviour")]
    [SerializeField] bool expectedIsKinematicAtRest = true;   // 離した直後の既定
    [SerializeField] bool applyReleaseVelocity = true;    // 離す瞬間に手の速度を与える
    [SerializeField] float followLerp = 25f;     // 追従スムーズ

    Rigidbody _rb;

    // 掴み中のみ（ローカル）で使う追従ターゲット
    Transform _followTarget;
    Vector3 _localPosOffset;
    Quaternion _localRotOffset;

    // Release 用に推定する線形/角速度（StateAuthority 側で更新）
    Vector3 _lastPos, _vel, _angVel;
    Quaternion _lastRot;

    [Networked, OnChangedRender(nameof(OnGrabChanged))]
    public NetworkBool NetIsGrabbed { get; private set; }   // 掴み状態（全員に同期）  :contentReference[oaicite:3]{index=3}

    [Networked] public PlayerRef Holder { get; private set; } // 掴み主（情報用）

    void Awake()
    {
        _rb = GetComponent<Rigidbody>() ?? gameObject.AddComponent<Rigidbody>();
        _rb.isKinematic = expectedIsKinematicAtRest;
        _rb.useGravity = false;
        _lastPos = transform.position;
        _lastRot = transform.rotation;
    }

    // ===== 手側（コントローラ）から呼ぶAPI =====

    /// <summary>掴み要求（バンパーなどから呼ぶ）</summary>
    public void RequestGrab(Transform hand)
    {
        if (NetIsGrabbed || hand == null) return;

        // 権限を要求（NetworkObject の Allow State Authority Override を ON に）
        if (!Object.HasStateAuthority)
            Object.RequestStateAuthority();      // 権限取得は次Tickで反映。:contentReference[oaicite:4]{index=4}

        _followTarget = hand;                     // 権限が取れたTickで確定（FixedUpdateNetwork）
    }

    /// <summary>離す</summary>
    public void RequestRelease()
    {
        if (!NetIsGrabbed) return;

        if (Object.HasStateAuthority)
        {
            NetIsGrabbed = false;
            Holder = default;
            Object.ReleaseStateAuthority();      // 次の人が掴めるよう返却（用途で省略可）。:contentReference[oaicite:5]{index=5}
        }
        _followTarget = null;
    }

    // ===== Fusion =====

    public override void FixedUpdateNetwork()
    {
        // 権限が取れ、かつまだ Grab を確定していない場合に初期化
        if (Object.HasStateAuthority && _followTarget != null && !NetIsGrabbed)
        {
            _localPosOffset = _followTarget.InverseTransformPoint(transform.position);
            _localRotOffset = Quaternion.Inverse(_followTarget.rotation) * transform.rotation;
            NetIsGrabbed = true;
            Holder = Runner.LocalPlayer;
        }

        // 掴み中：StateAuthority 側だけが追従。NetworkTransform が配信して他端末へ反映
        if (Object.HasStateAuthority && NetIsGrabbed && _followTarget != null)
        {
            var targetPos = _followTarget.TransformPoint(_localPosOffset);
            var targetRot = _followTarget.rotation * _localRotOffset;

            var dt = Mathf.Max(Runner.DeltaTime, 1e-5f);
            _vel = (targetPos - _lastPos) / dt;
            _angVel = AngularVelocity(_lastRot, targetRot, dt);
            _lastPos = targetPos;
            _lastRot = targetRot;

            if (followLerp > 0f)
            {
                transform.position = Vector3.Lerp(transform.position, targetPos, 1 - Mathf.Exp(-followLerp * dt));
                transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, 1 - Mathf.Exp(-followLerp * dt));
            }
            else
            {
                transform.SetPositionAndRotation(targetPos, targetRot);
            }
        }
    }

    // Fusion 2 の権限変化フックは override ではなく IStateAuthorityChanged を実装する :contentReference[oaicite:6]{index=6}
    public void StateAuthorityChanged()
    {
        if (!Object.HasStateAuthority)
            _followTarget = null;   // 権限喪失時にフォロー解除
    }

    // ===== OnChangedRender コールバック（全クライアント） =====  :contentReference[oaicite:7]{index=7}
    // Networked プロパティ NetIsGrabbed が変化したとき呼ばれる
    void OnGrabChanged()
    {
        if (NetIsGrabbed)
        {
            if (_rb) _rb.isKinematic = true;    // 掴み中は物理停止
        }
        else
        {
            if (_rb)
            {
                _rb.isKinematic = expectedIsKinematicAtRest;

#if UNITY_6000_0_OR_NEWER
                if (applyReleaseVelocity && Object.HasStateAuthority && !expectedIsKinematicAtRest)
                {
                    _rb.linearVelocity = _vel;   // Unity 6 は linearVelocity を使用 :contentReference[oaicite:8]{index=8}
                    _rb.angularVelocity = _angVel;
                }
#else
                if (applyReleaseVelocity && Object.HasStateAuthority && !expectedIsKinematicAtRest)
                {
                    _rb.velocity        = _vel;  // 旧バージョン互換
                    _rb.angularVelocity = _angVel;
                }
#endif
            }
            _followTarget = null;
        }
    }

    static Vector3 AngularVelocity(Quaternion from, Quaternion to, float dt)
    {
        if (dt <= 0f) return Vector3.zero;
        var dq = to * Quaternion.Inverse(from);
        dq.ToAngleAxis(out float angle, out Vector3 axis);
        if (angle > 180f) angle -= 360f;
        return axis * Mathf.Deg2Rad * angle / dt;
    }
}
