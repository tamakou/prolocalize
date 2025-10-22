// Assets/_APP/Scripts/AnchorNetBridge.cs
using Fusion;
using UnityEngine;

public class AnchorNetBridge : NetworkBehaviour
{
    public static AnchorNetBridge Instance { get; private set; }
    void Awake() => Instance = this;
    void OnDestroy() { if (Instance == this) Instance = null; }

    // 公開API：任意のクライアントから呼べる
    public void BroadcastAnchorPose(Vector3 pos, Quaternion rot, string mapUUID)
    {
        // Host / Server 経由で全員へ中継（Fusion推奨の二段RPC）
        RPC_SendToAuthority(pos, rot, mapUUID);
    }

    // 入力権限 → 状態権限へ送信
    [Rpc(RpcSources.All, RpcTargets.StateAuthority, HostMode = RpcHostMode.SourceIsHostPlayer)]
    void RPC_SendToAuthority(Vector3 pos, Quaternion rot, string mapUUID, RpcInfo info = default)
    {
        RPC_RelayToAll(pos, rot, mapUUID);
    }

    // 状態権限 → 全員へ配信
    [Rpc(RpcSources.StateAuthority, RpcTargets.All, HostMode = RpcHostMode.SourceIsServer)]
    void RPC_RelayToAll(Vector3 pos, Quaternion rot, string mapUUID)
    {
        // 受信側は ML2AnchorsControllerUnified.OnNetworkAnchorPublished を呼ぶ
        ML2AnchorsControllerUnified.Instance?.OnNetworkAnchorPublished(pos, rot, mapUUID);
    }
}
