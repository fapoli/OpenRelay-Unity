# OpenRelay Unity

[![Unity](https://img.shields.io/badge/Unity-000000?logo=unity&logoColor=white)](https://unity.com)
[![License: MIT](https://img.shields.io/github/license/fapoli/OpenRelay-Unity)](LICENSE)

Unity client for [openrelay](https://github.com/fapoli/openrelay) — routes NGO/UTP traffic through a UDP relay so players can connect without port forwarding.

## How it works

This module handles two things:

1. **Session management** — calls the openrelay HTTP API to create or join a session, receiving a session token and a 4-byte peer token that identifies this peer within the session
2. **Transport wiring** — plugs a custom `INetworkInterface` into NGO's Unity Transport that prepends a 9-byte relay header `[4B sessionToken][4B peerToken][1B targetPeerIndex]` to every outgoing UDP packet and strips the 4-byte sender index the relay prepends to incoming packets, mapping each sender to a synthetic endpoint `10.0.0.{peerIndex}`

## Requirements

- Unity 6+
- com.unity.netcode.gameobjects 2.0+
- com.unity.transport 2.0+

## Installation

Add via UPM git URL or drop the folder into `Assets/_Modules/`.

## Setup

This package integrates with **Unity Netcode for GameObjects (NGO)**. `NetworkRelay` must be in the same scene as your `NetworkManager`.

Add a `NetworkRelay` component to your scene and fill in the Inspector fields:
- **Network Manager** — drag your `NetworkManager` here
- **Relay URL** — base URL of your [openrelay](https://github.com/fapoli/openrelay) server
- **API Key** — your project's API key from the server's `projects.yaml` (see [openrelay docs](https://github.com/fapoli/openrelay))
- **Relay Port** — UDP port (default `7777`)

## Usage

```csharp
// Host — server generates code and password; show them to the player so they can share with the client
var session = await NetworkRelay.instance.host();
Debug.Log($"Code: {session.code}  Password: {session.password}");

// Host with a known code and password
var session = await NetworkRelay.instance.host(code: "123456", password: "hunter2");

// Join
var session = await NetworkRelay.instance.join(code, password);
```

`NetworkRelay` wires up the transport and starts NGO automatically.
