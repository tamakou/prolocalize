using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Android;

public class ML2PermissionBootstrap : MonoBehaviour
{
  // 実際にランタイム要求が必要な “Dangerous” 権限だけ列挙
  // ※ SPATIAL_MAPPING はメッシュ等を使う時だけ必要。不要なら配列から外す。
  static readonly string[] DangerousPermissions =
  {
        "com.magicleap.permission.SPACE_IMPORT_EXPORT",
        // "com.magicleap.permission.SPATIAL_MAPPING",
    };

  void Start()
  {
#if UNITY_ANDROID && !UNITY_EDITOR
        EnsurePermissions();
#endif
  }

  static void EnsurePermissions()
  {
    var toRequest = new List<string>();
    foreach (var p in DangerousPermissions)
    {
      if (!Permission.HasUserAuthorizedPermission(p))
      {
        // Androidが「追加説明を表示すべき」と判断しているかどうか
        var shouldExplain = Permission.ShouldShowRequestPermissionRationale(p);
        if (shouldExplain)
        {
          Debug.Log($"[Perm] Rationale for {p}. You may want to show an explainer UI here.");
        }
        toRequest.Add(p);
      }
    }

    if (toRequest.Count == 0)
    {
      Debug.Log("[Perm] All dangerous permissions already granted.");
      return;
    }

    var cb = new PermissionCallbacks();
    cb.PermissionGranted += (perm) =>
    {
      Debug.Log($"[Perm] Granted: {perm}");
    };
    cb.PermissionDenied += (perm) =>
    {
      Debug.LogWarning($"[Perm] Denied: {perm}");
      // “今後表示しない”相当かどうかは、再度 ShouldShow... を見て判断
      var canAskAgain = Permission.ShouldShowRequestPermissionRationale(perm);
      if (!canAskAgain)
      {
        Debug.LogWarning($"[Perm] {perm} set to Don't Ask Again? Opening App Settings...");
        OpenAppSettings(); // 設定画面へ誘導
      }
    };

    // ※ 非推奨：PermissionDeniedAndDontAskAgain は新しめのAndroidで不安定
    // （Unity公式も PermissionDenied のみ購読を推奨）
    Permission.RequestUserPermissions(toRequest.ToArray(), cb);
  }

  // アプリの詳細設定画面を開く（ユーザーに手動で権限をONにしてもらう）
  static void OpenAppSettings()
  {
    try
    {
      using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
      using (var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity"))
      using (var uriClass = new AndroidJavaClass("android.net.Uri"))
      using (var intent = new AndroidJavaObject("android.content.Intent", "android.settings.APPLICATION_DETAILS_SETTINGS"))
      {
        string pkg = activity.Call<string>("getPackageName");
        var uri = uriClass.CallStatic<AndroidJavaObject>("fromParts", "package", pkg, null);
        intent.Call<AndroidJavaObject>("setData", uri);
        intent.Call<AndroidJavaObject>("addFlags", 0x10000000); // FLAG_ACTIVITY_NEW_TASK
        activity.Call("startActivity", intent);
      }
    }
    catch (System.Exception e)
    {
      Debug.LogError($"[Perm] Failed to open App Settings: {e.Message}");
    }
  }
}
