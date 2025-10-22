using System;
using System.Collections;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.XR.OpenXR;
using MagicLeap.OpenXR.Features.LocalizationMaps;

/// <summary>
/// WebDAVサーバーを使用したMagic Leap 2スペースデータの共有管理クラス
/// 
/// 【移植時の注意点】
/// - webdavBaseUrl, webdavUser, webdavAppPassword を環境に合わせて変更
/// - Magic Leap 2以外の場合は localizationMapFeature 関連のコードを削除
/// - インターネットアクセス権限が必要（AndroidManifest.xml）
/// 
/// 【主な機能】
/// - スペースデータのzip形式でのアップロード/ダウンロード
/// - Basic認証によるWebDAVアクセス
/// - 非同期処理対応（CoroutineとTask両方）
/// </summary>
public class WebDAVSpaceManager : MonoBehaviour
{
    [Header("WebDAV Configuration")]
    [SerializeField] private string webdavBaseUrl = "https://soya.infini-cloud.net/dav/";  // WebDAVサーバーのベースURL
    [SerializeField] private string webdavUser = "teragroove";  // WebDAVユーザー名
    [SerializeField] private string webdavAppPassword = "bR6RxjGW4cukpmDy";  // WebDAVアプリパスワード
    [SerializeField] private string remoteFolder = "spaces";  // リモートフォルダ名

    // Magic Leap 2のローカライゼーション機能への参照（他プラットフォームでは不要）
    private MagicLeapLocalizationMapFeature localizationMapFeature;

    void Awake()
    {
        // Magic Leap 2のローカライゼーション機能を初期化（他プラットフォームではこのブロックを削除）
        localizationMapFeature = OpenXRSettings.Instance.GetFeature<MagicLeapLocalizationMapFeature>();
        if (localizationMapFeature == null || !localizationMapFeature.enabled)
        {
            Debug.LogError("[WebDAVSpaceManager] LocalizationMaps feature is disabled or not available.");
        }
        else
        {
            Debug.Log("[WebDAVSpaceManager] Initialized successfully.");
        }
    }

    /// <summary>
    /// Generate Basic Authentication header for WebDAV requests
    /// </summary>
    private string GetAuthHeader()
    {
        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{webdavUser}:{webdavAppPassword}"));
        return $"Basic {token}";
    }

    /// <summary>
    /// Combine WebDAV base URL with relative path
    /// </summary>
    private string CombineWebDAVUrl(string baseUrl, string relativePath)
    {
        if (!baseUrl.EndsWith("/"))
            baseUrl += "/";
        return baseUrl + relativePath.TrimStart('/');
    }

    /// <summary>
    /// PUT operation to upload bytes to WebDAV server
    /// </summary>
    private IEnumerator PutBytes(string relativePath, byte[] body, string contentType = "application/octet-stream")
    {
        var url = CombineWebDAVUrl(webdavBaseUrl, relativePath);
        Debug.Log($"[WebDAVSpaceManager] PUT request to: {url}, Size: {body.Length} bytes, ContentType: {contentType}");

        using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPUT);
        req.uploadHandler = new UploadHandlerRaw(body) { contentType = contentType };
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Authorization", GetAuthHeader());
        req.timeout = 30; // 30 second timeout

        Debug.Log($"[WebDAVSpaceManager] PUT starting request...");
        var startTime = Time.time;

        yield return req.SendWebRequest();

        var endTime = Time.time;
        var duration = endTime - startTime;
        Debug.Log($"[WebDAVSpaceManager] PUT completed in {duration:F2} seconds");

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[WebDAVSpaceManager] PUT error: {req.responseCode} {req.error}");
            Debug.LogError($"[WebDAVSpaceManager] Response: {req.downloadHandler.text}");
            Debug.LogError($"[WebDAVSpaceManager] Request result: {req.result}");
        }
        else
        {
            Debug.Log($"[WebDAVSpaceManager] PUT OK: {url}");
        }
    }

    /// <summary>
    /// GET operation to download bytes from WebDAV server
    /// </summary>
    private IEnumerator GetBytes(string relativePath, Action<byte[]> onDone)
    {
        var url = CombineWebDAVUrl(webdavBaseUrl, relativePath);
        Debug.Log($"[WebDAVSpaceManager] GET request to: {url}");

        using var req = UnityWebRequest.Get(url);
        req.SetRequestHeader("Authorization", GetAuthHeader());
        req.timeout = 30; // 30 second timeout

        Debug.Log($"[WebDAVSpaceManager] GET starting request...");
        var startTime = Time.time;

        yield return req.SendWebRequest();

        var endTime = Time.time;
        var duration = endTime - startTime;
        Debug.Log($"[WebDAVSpaceManager] GET completed in {duration:F2} seconds");

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[WebDAVSpaceManager] GET error: {req.responseCode} {req.error}");
            Debug.LogError($"[WebDAVSpaceManager] Request result: {req.result}");
            onDone?.Invoke(null);
        }
        else
        {
            Debug.Log($"[WebDAVSpaceManager] GET OK: {url}, received {req.downloadHandler.data.Length} bytes");
            onDone?.Invoke(req.downloadHandler.data);
        }
    }

    /// <summary>
    /// スペースデータをWebDAVサーバーにzipファイルとしてアップロード（Coroutine版）
    /// 【使用例】StartCoroutine(UploadSpaceData(data, mapId));
    /// </summary>
    /// <param name="spaceData">アップロードするスペースデータ（zip形式のバイト配列）</param>
    /// <param name="mapId">マップID（ログ用、実際のファイル名は固定）</param>
    public IEnumerator UploadSpaceData(byte[] spaceData, string mapId)
    {
        Debug.Log($"[WebDAVSpaceManager] Starting upload of space data. Size: {spaceData.Length} bytes, Map ID: {mapId}");

        // Use a consistent filename - always overwrite the latest version
        string fileName = "unity_exported_space.zip";

        Debug.Log($"[WebDAVSpaceManager] Uploading as: {fileName}");

        // Upload zip file directly
        yield return PutBytes($"{remoteFolder}/{fileName}", spaceData, "application/zip");

        Debug.Log("[WebDAVSpaceManager] Space upload completed");
    }

    /// <summary>
    /// スペースデータをWebDAVサーバーにzipファイルとしてアップロード（async/await版）
    /// 【使用例】await webdavManager.UploadSpaceDataAsync(data, mapId);
    /// </summary>
    /// <param name="spaceData">アップロードするスペースデータ（zip形式のバイト配列）</param>
    /// <param name="mapId">マップID（ログ用）</param>
    /// <returns>アップロード完了を待機するTask</returns>
    public System.Threading.Tasks.Task UploadSpaceDataAsync(byte[] spaceData, string mapId)
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<bool>();
        StartCoroutine(UploadSpaceDataCoroutine(spaceData, mapId, tcs));
        return tcs.Task;
    }

    private System.Collections.IEnumerator UploadSpaceDataCoroutine(byte[] spaceData, string mapId, System.Threading.Tasks.TaskCompletionSource<bool> tcs)
    {
        yield return UploadSpaceData(spaceData, mapId);
        tcs.SetResult(true);
    }

    /// <summary>
    /// WebDAVサーバーから最新のスペースデータをダウンロード（Coroutine版）
    /// 【使用例】StartCoroutine(DownloadSpaceData((data) => { /* 処理 */ }));
    /// </summary>
    /// <param name="onComplete">ダウンロード完了時のコールバック（データがnullの場合は失敗）</param>
    public IEnumerator DownloadSpaceData(Action<byte[]> onComplete)
    {
        Debug.Log("[WebDAVSpaceManager] Starting download of space data");

        // Download the standard filename
        string fileName = "unity_exported_space.zip";

        yield return GetBytes($"{remoteFolder}/{fileName}", (data) =>
        {
            if (data != null)
            {
                Debug.Log($"[WebDAVSpaceManager] Space download completed. Size: {data.Length} bytes");
            }
            else
            {
                Debug.LogError("[WebDAVSpaceManager] Failed to download space data");
            }
            onComplete?.Invoke(data);
        });
    }

    /// <summary>
    /// WebDAVサーバーから最新のスペースデータをダウンロード（async/await版）
    /// 【使用例】byte[] data = await webdavManager.DownloadSpaceDataAsync();
    /// </summary>
    /// <returns>ダウンロードしたスペースデータ（失敗時はnull）</returns>
    public System.Threading.Tasks.Task<byte[]> DownloadSpaceDataAsync()
    {
        var tcs = new System.Threading.Tasks.TaskCompletionSource<byte[]>();
        StartCoroutine(DownloadSpaceDataCoroutine(tcs));
        return tcs.Task;
    }

    private System.Collections.IEnumerator DownloadSpaceDataCoroutine(System.Threading.Tasks.TaskCompletionSource<byte[]> tcs)
    {
        yield return DownloadSpaceData((data) =>
        {
            tcs.SetResult(data);
        });
    }

    /// <summary>
    /// Test WebDAV connection (for development/debugging)
    /// </summary>
    [ContextMenu("Test WebDAV Connection")]
    public void TestWebDAVConnection()
    {
        StartCoroutine(TestConnectionCoroutine());
    }

    /// <summary>
    /// Create spaces folder on WebDAV server
    /// </summary>
    [ContextMenu("Create Spaces Folder")]
    public void CreateSpacesFolder()
    {
        StartCoroutine(CreateSpacesFolderCoroutine());
    }

    /// <summary>
    /// Test WebDAV upload/download with mock data (works on PC)
    /// </summary>
    [ContextMenu("Test WebDAV with Mock Data")]
    public void TestWebDAVWithMockData()
    {
        StartCoroutine(TestWebDAVWithMockDataCoroutine());
    }

    private IEnumerator TestConnectionCoroutine()
    {
        Debug.Log("[WebDAVSpaceManager] Testing WebDAV connection...");

        // Try to upload a small test file
        string testData = "WebDAV connection test from Unity";
        byte[] testBytes = Encoding.UTF8.GetBytes(testData);

        yield return PutBytes($"{remoteFolder}/test.txt", testBytes, "text/plain");

        // Try to download the test file
        bool downloadSuccess = false;
        yield return GetBytes($"{remoteFolder}/test.txt", (data) =>
        {
            if (data != null)
            {
                string receivedData = Encoding.UTF8.GetString(data);
                downloadSuccess = receivedData == testData;
                Debug.Log($"[WebDAVSpaceManager] Test download result: {downloadSuccess}");
            }
        });

        if (downloadSuccess)
        {
            Debug.Log("[WebDAVSpaceManager] ✅ WebDAV connection test successful!");
        }
        else
        {
            Debug.LogError("[WebDAVSpaceManager] ❌ WebDAV connection test failed!");
        }
    }

    private IEnumerator TestWebDAVWithMockDataCoroutine()
    {
        Debug.Log("[WebDAVSpaceManager] ===== Starting WebDAV Mock Data Test =====");

        // 1. Create mock space data
        string mockMapId = "mock-map-" + System.Guid.NewGuid().ToString();
        byte[] mockSpaceData = new byte[1024]; // 1KB of mock data
        for (int i = 0; i < mockSpaceData.Length; i++)
        {
            mockSpaceData[i] = (byte)(i % 256);
        }
        Debug.Log($"[WebDAVSpaceManager] Created mock space data: {mockSpaceData.Length} bytes, Map ID: {mockMapId}");

        // 2. Test upload
        Debug.Log("[WebDAVSpaceManager] Testing upload...");
        yield return UploadSpaceData(mockSpaceData, mockMapId);
        Debug.Log("[WebDAVSpaceManager] ✅ Upload test completed");

        // 3. Wait a bit
        yield return new WaitForSeconds(2);

        // 4. Test download
        Debug.Log("[WebDAVSpaceManager] Testing download...");
        bool downloadSuccess = false;
        byte[] downloadedData = null;

        yield return DownloadSpaceData((data) =>
        {
            downloadedData = data;
            downloadSuccess = (data != null && data.Length > 0);
        });

        if (downloadSuccess)
        {
            Debug.Log($"[WebDAVSpaceManager] ✅ Download successful: {downloadedData.Length} bytes");

            // Verify data integrity
            bool dataMatches = downloadedData.Length == mockSpaceData.Length;
            if (dataMatches)
            {
                for (int i = 0; i < mockSpaceData.Length; i++)
                {
                    if (mockSpaceData[i] != downloadedData[i])
                    {
                        dataMatches = false;
                        break;
                    }
                }
            }

            if (dataMatches)
            {
                Debug.Log("[WebDAVSpaceManager] ✅ Data integrity verified - upload/download successful!");
            }
            else
            {
                Debug.LogWarning("[WebDAVSpaceManager] ⚠️ Data mismatch - uploaded and downloaded data differ");
            }
        }
        else
        {
            Debug.LogError("[WebDAVSpaceManager] ❌ Download failed");
        }

        // 5. Verify upload by re-downloading the zip file
        Debug.Log("[WebDAVSpaceManager] Testing re-download to verify upload...");
        yield return DownloadSpaceData((verifyData) =>
        {
            if (verifyData != null && verifyData.Length == mockSpaceData.Length)
            {
                Debug.Log($"[WebDAVSpaceManager] ✅ Re-download verification successful - Size: {verifyData.Length} bytes");
            }
            else
            {
                Debug.LogError("[WebDAVSpaceManager] ❌ Re-download verification failed");
            }
        });

        Debug.Log("[WebDAVSpaceManager] ===== WebDAV Mock Data Test Complete =====");
    }

    private IEnumerator CreateSpacesFolderCoroutine()
    {
        Debug.Log("[WebDAVSpaceManager] Creating spaces folder...");

        // Try to create folder using MKCOL method
        var url = CombineWebDAVUrl(webdavBaseUrl, remoteFolder);
        Debug.Log($"[WebDAVSpaceManager] MKCOL request to: {url}");

        using var req = new UnityWebRequest(url, "MKCOL");
        req.downloadHandler = new DownloadHandlerBuffer();
        req.SetRequestHeader("Authorization", GetAuthHeader());

        yield return req.SendWebRequest();

        if (req.result != UnityWebRequest.Result.Success)
        {
            Debug.LogError($"[WebDAVSpaceManager] MKCOL error: {req.responseCode} {req.error}");
            Debug.LogError($"[WebDAVSpaceManager] Response: {req.downloadHandler.text}");
        }
        else
        {
            Debug.Log($"[WebDAVSpaceManager] ✅ Spaces folder created successfully: {url}");
        }
    }

    /// <summary>
    /// Test WebDAV with root directory (no subfolder)
    /// </summary>
    [ContextMenu("Test Root Directory")]
    public void TestRootDirectory()
    {
        StartCoroutine(TestRootDirectoryCoroutine());
    }

    private IEnumerator TestRootDirectoryCoroutine()
    {
        Debug.Log("[WebDAVSpaceManager] Testing root directory access...");

        // Try to upload directly to root directory
        string testData = "Root directory test from Unity";
        byte[] testBytes = Encoding.UTF8.GetBytes(testData);

        yield return PutBytes("test-root.txt", testBytes, "text/plain");

        // Try to download the test file
        bool downloadSuccess = false;
        yield return GetBytes("test-root.txt", (data) =>
        {
            if (data != null)
            {
                string receivedData = Encoding.UTF8.GetString(data);
                downloadSuccess = receivedData == testData;
                Debug.Log($"[WebDAVSpaceManager] Root test download result: {downloadSuccess}");
            }
        });

        if (downloadSuccess)
        {
            Debug.Log("[WebDAVSpaceManager] ✅ Root directory access successful!");
        }
        else
        {
            Debug.LogError("[WebDAVSpaceManager] ❌ Root directory access failed!");
        }
    }
}