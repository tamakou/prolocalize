// Assets/_APP/Scripts/FusionBootstrap.cs
using Fusion;
using UnityEngine;
using System.Threading.Tasks;

public class FusionBootstrap : MonoBehaviour
{
    [SerializeField] private NetworkObject bridgePrefab; // © AnchorNetBridge.prefab ‚ğŠ„‚è“–‚Ä
    [SerializeField] private string sessionName = "ml2-fusion2-demo";

    private NetworkRunner _runner;

    private async void Awake()
    {
        _runner = gameObject.AddComponent<NetworkRunner>();
        _runner.ProvideInput = true;

        var result = await _runner.StartGame(new StartGameArgs
        {
            GameMode = GameMode.Shared,
            SessionName = sessionName,
            SceneManager = gameObject.AddComponent<NetworkSceneManagerDefault>()
        });

        if (!result.Ok)
        {
            Debug.LogError($"Runner start failed: {result.ShutdownReason}");
            return;
        }

        // š AnchorNetBridge ‚ğˆê“x‚¾‚¯Spawn
        if (FindAnyObjectByType<AnchorNetBridge>() == null)
        {
            _runner.Spawn(bridgePrefab, Vector3.zero, Quaternion.identity, _runner.LocalPlayer);
        }
    }
}
