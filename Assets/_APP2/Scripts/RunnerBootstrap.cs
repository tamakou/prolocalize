// Assets/_APP/Scripts/RunnerBootstrap.cs
using System.Threading.Tasks;
using Fusion;
using Fusion.Sockets;
using UnityEngine;

[DefaultExecutionOrder(-1000)]
public class RunnerBootstrap : MonoBehaviour
{
    [SerializeField] GameMode mode = GameMode.Shared; // Host Ç≈Ç‡OK
    async void Start()
    {
        var runner = GetComponent<NetworkRunner>();
        var sceneMgr = GetComponent<NetworkSceneManagerDefault>();
        await runner.StartGame(new StartGameArgs
        {
            GameMode = mode,
            SessionName = "ML2Anchors",
            Scene = SceneRef.FromIndex(UnityEngine.SceneManagement.SceneManager.GetActiveScene().buildIndex),
            SceneManager = sceneMgr
        });
    }
}
