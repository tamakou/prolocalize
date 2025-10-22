// Assets/_APP/Scripts/New/Ml2BumperSpawn_InputActionUnified.cs
using UnityEngine;

#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class Ml2BumperSpawn_InputActionUnified : MonoBehaviour
{
    [SerializeField] private ML2AnchorsControllerUnified controller;
    [SerializeField] private InputAction bumperAction;     // Inspector 未設定でも自動生成
    [SerializeField] private int cooldownMs = 250;
    [SerializeField] private bool preferRightHand = true;

#if ENABLE_INPUT_SYSTEM
    private double _last;

    private void OnEnable()
    {
        if (controller == null)
#if UNITY_6000_0_OR_NEWER
            controller = Object.FindFirstObjectByType<ML2AnchorsControllerUnified>();
#else
      controller = FindObjectOfType<ML2AnchorsControllerUnified>();
#endif

        if (bumperAction == null)
        {
            // ML2 の Bumper は gripPressed
            string hand = preferRightHand ? "{RightHand}" : "{LeftHand}";
            string path = $"<MagicLeapController>{hand}/gripPressed";
            bumperAction = new InputAction("ML2_Bumper", InputActionType.Button, path);
        }

        bumperAction.started += OnBumper;   // 押下瞬間
        bumperAction.performed += OnBumper;   // PressInteraction 等でも拾えるよう保険
        bumperAction.Enable();
    }

    private void OnDisable()
    {
        if (bumperAction != null)
        {
            bumperAction.started -= OnBumper;
            bumperAction.performed -= OnBumper;
            bumperAction.Disable();
        }
    }

    private void OnBumper(InputAction.CallbackContext ctx)
    {
        if (controller == null) return;
        if (ctx.time - _last < (cooldownMs / 1000.0)) return;
        _last = ctx.time;

        // ★ 修正点：存在しない SpawnFromControllerInput() ではなく、実体の Async メソッドを呼ぶ
        _ = controller.SpawnLocalAnchorAsync();
    }
#else
  private void OnEnable() {
    Debug.LogWarning("New Input System が無効です。Project Settings > Player > Active Input Handling を確認してください。");
  }
#endif
}
