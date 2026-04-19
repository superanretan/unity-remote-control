# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2026-04-19

### Added
- Core command system with `RemoteCommand` JSON serialization
- Handler-based command dispatch via `ICommandHandler` / `CommandHandlerBase`
- Runtime registries: `CommandTargetRegistry`, `CommandHandlerRegistry`
- `CommandProcessor` for event-driven command routing
- ScriptableObject event channels: `CommandEventChannel`, `StringEventChannel`, `VoidEventChannel`
- `TransportHost` — Unity Transport server (listens for incoming commands)
- `TransportClient` — Unity Transport client (sends commands to host)
- `NetworkConfig` ScriptableObject for port / connection settings
- `NetworkUtility` for local IP discovery
- `CommandTarget` MonoBehaviour for registering scene objects
- `DebugLogUI` for on-screen TMP debug logging
- Editor menu to auto-create all required ScriptableObject assets
- Basic Demo sample with Host scene (cube) and Controller scene (color buttons)
