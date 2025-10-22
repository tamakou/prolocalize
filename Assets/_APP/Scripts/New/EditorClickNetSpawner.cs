#if UNITY_EDITOR
using UnityEngine;
using UnityEngine.EventSystems;          // クリックがUI上かの判定に使用（任意）
using Fusion;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;           // 新Input System
#endif

/// <summary>
/// Editor限定：左クリックで NetworkObject を Runner.Spawn() する簡易デバッガ
/// </summary>
public class EditorClickNetSpawner : MonoBehaviour
{
    [Header("Refs")]
    [SerializeField] private Camera cam;
    [SerializeField] private NetworkObject spawnPrefab;      // NetworkObject付きのPrefab（Prefab Table登録必須）
    [SerializeField] private LayerMask rayMask = ~0;         // クリック先にColliderが無い場合のために調整可
    [SerializeField] private float fallbackDistance = 1.5f;  // 当たり無し時、カメラ前方に出す距離

    private NetworkRunner _runner;

    private void Awake()
    {
        if (!cam) cam = Camera.main;
    }

    private void Update()
    {
        // UI上のクリックは無視（任意）
        if (EventSystem.current && EventSystem.current.IsPointerOverGameObject())
            return;

        // 左クリック検出
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current == null || !Mouse.current.leftButton.wasPressedThisFrame)
            return;
#else
        if (!Input.GetMouseButtonDown(0))
            return;
#endif
        TrySpawnAtMouse();
    }

    private void TrySpawnAtMouse()
    {
        // Runner を動的取得（見つからない/未起動なら中断）
        if (_runner == null) _runner = FindAnyObjectByType<NetworkRunner>();
        if (_runner == null || !_runner.IsRunning) { Debug.LogWarning("[EditorClickNetSpawner] Runner not running."); return; }
        if (spawnPrefab == null) { Debug.LogError("[EditorClickNetSpawner] Prefab not assigned."); return; }

        // マウス位置 → 3Dレイ
        Vector3 screenPos;
#if ENABLE_INPUT_SYSTEM
        screenPos = Mouse.current != null ? (Vector3)Mouse.current.position.ReadValue() : Input.mousePosition;
#else
        screenPos = Input.mousePosition;
#endif
        Ray ray = cam ? cam.ScreenPointToRay(screenPos) : new Ray(Vector3.zero, Vector3.forward);

        Vector3 pos; Quaternion rot;
        if (Physics.Raycast(ray, out var hit, 100f, rayMask))
        {
            pos = hit.point;
            var fwd = cam ? Vector3.ProjectOnPlane(cam.transform.forward, Vector3.up).normalized : Vector3.forward;
            rot = Quaternion.LookRotation(fwd, Vector3.up);
        }
        else
        {
            pos = (cam ? cam.transform.position : Vector3.zero) + (cam ? cam.transform.forward : Vector3.forward) * fallbackDistance;
            rot = Quaternion.LookRotation(Vector3.ProjectOnPlane(cam ? cam.transform.forward : Vector3.forward, Vector3.up), Vector3.up);
        }

        // ★ SharedモードならクライアントからSpawn可 / Host・ServerモードはサーバがSpawnする
        _runner.Spawn(spawnPrefab, pos, rot, _runner.LocalPlayer);
    }
}
#endif
