# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Unity project for Magic Leap 2 (ML2) with Photon Fusion multiplayer networking. The application focuses on spatial anchors, co-location, and shared AR experiences using ML2's localization maps.

**Key Technologies:**
- Unity 6000.1.4f1 (Unity 6)
- Universal Render Pipeline (URP) 17.1.0
- Magic Leap SDK 2.6.0
- Photon Fusion 2.0.6 Stable (networked multiplayer)
- OpenXR 1.13.0 with Magic Leap extensions
- AR Foundation 6.1.0
- Input System 1.14.2

## Development Commands

This is a Unity project - open in Unity Editor (version 6000.1.4f1 or Unity 6 compatible with Magic Leap SDK 2.6.0).

**Building for Magic Leap 2:**
1. File > Build Settings > Switch to Android platform
2. Set build target to Magic Leap 2 in XR settings
3. Build and deploy via Magic Leap Hub or `adb install`

## Architecture

### Application Structure

The project is organized into multiple "APP" folders representing different development iterations/experiments:

- **`Assets/_APP/`**: Core Fusion integration, anchor persistence, grabbing mechanics
- **`Assets/_APP2/`**: Network bridge for anchor synchronization
- **`Assets/_APP3/`**: Main production implementation with co-location starter, space management, auto-spawning
- **`Assets/_APP4/`**: Meshing and floor detection features

### Key System Components

**Co-location & Networking:**
- `FusionColocationStarter.cs` (\_APP3): Automatically starts Fusion session when ML2 localizes to a map. Maps to room names via `ml2-map-{MapUUID}` pattern.
- `AnchorNetBridge.cs` (\_APP2): RPC relay for broadcasting anchor poses across networked clients.
- `ML2AnchorsControllerUnified.cs` (\_APP): Comprehensive anchor management - creates, publishes, queries, and deletes spatial anchors. Handles both local ARAnchors and ML2's persistent storage.

**Space Sharing:**
- `WebDAVSpaceManager.cs` (\_APP3): Uploads/downloads ML2 space data (as ZIP) to/from WebDAV server for cross-device space sharing. Contains hardcoded credentials for `soya.infini-cloud.net/dav/`.

**Bootstrap Sequence:**
- `ML2PermissionBootstrap.cs` (\_APP3): Requests necessary ML2 permissions at startup
- `FusionColocationStarter.cs` (\_APP3): Waits for localization, then starts Fusion runner
- `FusionReadyAutoSpawner.cs` (\_APP3): Auto-spawns network objects when Fusion is ready

### Fusion Network Setup

The project uses **Photon Fusion** for real-time multiplayer. Room names are automatically generated from ML2 map UUIDs to ensure users in the same physical space join the same session.

**Start Modes:**
- `useAutoHostOrClient = true`: AutoHostOrClient mode (first client becomes host)
- `useAutoHostOrClient = false`: Shared mode (all clients are equal)

### Anchor Synchronization Flow

1. ML2 localizes to a map → `FusionColocationStarter` starts Fusion
2. User creates anchor → `ML2AnchorsControllerUnified` creates local ARAnchor
3. User publishes anchor → ML2 saves to persistent storage
4. Network broadcast → `AnchorNetBridge.RPC_SendToAuthority` → `RPC_RelayToAll`
5. Other clients receive → `ML2AnchorsControllerUnified.OnNetworkAnchorPublished` → query and restore anchor

## Important Notes

**WebDAV Credentials:**
The `WebDAVSpaceManager.cs` file contains hardcoded credentials. When deploying or sharing code, replace or remove these:
- `webdavBaseUrl`: `https://soya.infini-cloud.net/dav/`
- `webdavUser`: `teragroove`
- `webdavAppPassword`: `bR6RxjGW4cukpmDy`

**Scene Files:**
Main scenes are in `Assets/_APP*/Scenes/`. The test scenes include:
- `Assets/_APP/Scenes/SampleScene.unity`
- `Assets/_APP2/TestMasterScene.unity`

**Character Encoding:**
Some source files contain Japanese comments in Shift-JIS or other encodings that may display incorrectly as mojibake (e.g., "���" instead of proper characters). This doesn't affect functionality.

## Common Development Patterns

**Creating Networked Objects:**
Use `EditorClickNetSpawner.cs` or `Ml2BumperSpawn_InputActionUnified.cs` as reference for spawning Fusion NetworkObjects.

**Anchor Lifecycle:**
1. Create local anchor with `ML2AnchorsControllerUnified.OnSpawnClicked()`
2. Publish to storage with `OnPublishClicked()`
3. Query stored anchors with `OnQueryClicked()`
4. Delete with `OnDeleteNearestClicked()` or `OnDeleteAllClicked()`

**Input Actions:**
The project uses Unity's new Input System. Input action assets:
- `Assets/InputSystem_Actions.inputactions`
- `Assets/MagicLeapInput.inputactions`
