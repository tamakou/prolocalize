using Fusion;
using UnityEngine;

public class FusionReadyAutoSpawner : MonoBehaviour
{
  public NetworkPrefabRef cubePrefab; // NetworkObject + NetworkTransform 付きキューブを登録

  bool spawned;

  void Update()
  {
    var runner = FindFirstObjectByType<NetworkRunner>();
    if (!spawned && runner && runner.IsRunning && runner.IsConnectedToServer)
    {
      spawned = true;
      var cam = Camera.main ? Camera.main.transform : null;
      var pos = cam ? cam.position + cam.forward * 0.8f : Vector3.zero;
      runner.Spawn(cubePrefab, pos, Quaternion.identity, runner.LocalPlayer);
      Debug.Log("[Spawner] Cube spawned");
    }
  }
}
