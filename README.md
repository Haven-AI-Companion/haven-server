# 🌸 Ash Server

<p align="center">
  <img src="docs/logo.png" alt="Ash Server — Secure AI Orchestrator" width="600">
</p>

> **Security-first, privacy-first, locally-running AI backend** — built as a hardened alternative to OpenClaw, powered by [gemma4-turbo](https://ollama.com/ssfdre38/gemma4-turbo).

[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10-purple)](https://dotnet.microsoft.com/download)
[![Model: gemma4-turbo](https://img.shields.io/badge/Model-gemma4--turbo-orange)](https://ollama.com/ssfdre38/gemma4-turbo)

---

## Why Ash Server Exists

Self-hosted AI platforms like [OpenClaw](https://github.com/badlogic/lemmy) have grown rapidly — but without security foundations. The result is a class of real vulnerabilities:

| Problem | OpenClaw | Ash Server |
|---------|----------|------------|
| Identity layer | ❌ None | ✅ JWT auth + RBAC roles |
| External chat permissions | ❌ Anyone can invoke agent | ✅ Permission-gated by linked identity |
| Prompt injection protection | ❌ None | ✅ 7-pattern injection guard |
| Rate limiting | ❌ None | ✅ Per-user sliding window |
| Credential management | ❌ Edit JSON files | ✅ Admin panel, masked storage |
| Audit trail | ❌ None | ✅ Full per-action audit log |
| Discord/Slack/Telegram security | ❌ No gates | ✅ RBAC via identity linking |
| Service deployment | ❌ Manual | ✅ Windows/Linux/macOS installer |

Ash Server is not a wrapper around OpenClaw — it is a clean reimplementation in **C# / ASP.NET Core** with safety and trust built into every layer.

---

## Powered by Gemma 4 Turbo

Ash Server ships with first-class support for [`ssfdre38/gemma4-turbo`](https://ollama.com/ssfdre38/gemma4-turbo) — a purpose-built quantization of Google's Gemma 4 family:

- **IQ4_XS** non-linear quantization applied to **original bf16 source weights** (not re-quantizing already-quantized weights)
- Full **vision + multimodal** capabilities preserved
- Runs on **commodity hardware** — no GPU required

| Tag | Size | RAM | Best For |
|-----|------|-----|----------|
| `e2b` | 4.3 GB | 8 GB+ | Laptops, Raspberry Pi 5 |
| `e4b` / `latest` | 6.1 GB | 10 GB+ | Desktops, servers *(recommended)* |
| `26b` | 15 GB | 20 GB+ | High quality |
| `31b` | 18 GB | 24 GB+ | Maximum quality |

```bash
ollama run ssfdre38/gemma4-turbo   # pull and run e4b (recommended)
```

**17,300+ downloads** on Ollama Hub as of submission date — the **#2 most downloaded Gemma 4 model in the world**, behind only Google's official release, with 6.8x more downloads than the next community model.

---

## Quick Start

### Option A — Pre-built binary (recommended)

Download from the [latest release](https://github.com/ssfdre38/ash-server/releases/latest):

| Platform | Download |
|----------|----------|
| **Windows (x64)** | `ash-server-*-windows-x64-setup.exe` |
| **Linux (x64)**   | `ash-server-*-linux-x64.zip` |
| **Linux (arm64)** | `ash-server-*-linux-arm64.zip` |
| **macOS (Apple Silicon)** | `ash-server-*-osx-arm64.zip` |
| **macOS (Intel)** | `ash-server-*-osx-x64.zip` |

**Windows** — run the `.exe` installer as Administrator. Done.

**Linux / macOS** — unzip and run the bundled install script:
```bash
unzip ash-server-*-linux-x64.zip -d ash-server
sudo bash ash-server/build/linux/install.sh ash-server/ash-server
```

---

### Option B — Install from source (git clone)

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Ollama](https://ollama.ai).

**Linux / macOS:**
```bash
git clone https://github.com/ssfdre38/ash-server
sudo bash ash-server/install.sh          # build + install as system service
# — or —
bash ash-server/install.sh --run         # build + run in foreground (no root needed)
```

**Windows** (PowerShell as Administrator):
```powershell
git clone https://github.com/ssfdre38/ash-server
cd ash-server
.\install.ps1           # build + install as Windows Service
# — or —
.\install.ps1 -Run      # build + run in foreground (no admin needed)
```

**Pull the recommended model first:**
```bash
ollama pull ssfdre38/gemma4-turbo
```

Open **http://localhost:18799** — the first registered user becomes admin automatically.

### Uninstall

```bash
# Linux / macOS:
sudo bash ash-server/install.sh --uninstall

# Windows (Administrator PowerShell):
.\install.ps1 -Uninstall
```

### Install as a System Service (from an already-built binary)

```bash
# Windows (run as Administrator):
ash-server.exe install-service

# Linux / macOS (run as root):
sudo ash-server install-service
```

Registers with the native service manager: **Windows SCM**, **systemd**, or **launchd**.

---

## Security Architecture

```
External Message (Discord / Slack / Telegram)
        │
        ▼
  ┌─────────────────────────────┐
  │  ExternalRateLimiter        │  sliding window per user
  │  (5 req / 10s default)      │
  └────────────┬────────────────┘
               │ pass
        ▼
  ┌─────────────────────────────┐
  │  PromptGuard                │  7 injection patterns
  │  (instruction override,     │  jailbreak, persona hijack,
  │   base64, delimiters…)      │  delimiter injection, etc.
  └────────────┬────────────────┘
               │ clean
        ▼
  ┌─────────────────────────────┐
  │  IdentityResolver           │  Discord/Slack/Telegram user
  │                             │  → linked ash-server account
  └────────────┬────────────────┘
               │ resolve
        ▼
  ┌─────────────────────────────┐
  │  RBAC Permission Check      │  role-based: can_chat,
  │                             │  can_use_agent, can_use_tools
  └────────────┬────────────────┘
               │ authorized
        ▼
  ┌─────────────────────────────┐
  │  ChatHandler / AgentRunner  │  streams from gemma4-turbo
  │                             │  via Ollama backend
  └────────────┬────────────────┘
               │
        ▼
  ┌─────────────────────────────┐
  │  Audit Log                  │  every action recorded
  └─────────────────────────────┘
```

---

## Features

### Chat & Agent
- **Streaming chat** — WebSocket token-by-token, just like ChatGPT
- **Agent mode** — tool-calling loop (web search, URL fetch, calculator, file ops)
- **Multimodal** — image input via gemma4-turbo vision encoder
- **Conversation export** — download as JSON, Markdown, or plain text
- **Full-text search** — search across all conversations (SQLite FTS5)
- **Personality system** — `soul.json` + per-user context files

### Security & Trust
- **JWT authentication** — RS256-compatible, configurable secret
- **RBAC roles & permissions** — DB-backed, fully managed via admin UI
- **Prompt injection guard** — 7 regex patterns, block or log mode
- **Rate limiting** — per-user sliding window (external) + fixed window (HTTP API)
- **External identity linking** — Discord/Slack/Telegram users link to accounts
- **Audit log** — every external chat action recorded with provider, channel, user
- **Masked credential storage** — tokens shown as `••••` + last 4 chars

### Integrations
- **Discord bot** — full gateway bot with `/link` command, thread support
- **Slack bot** — Socket Mode, rate-limited, permission-gated
- **Telegram bot** — polling-based, per-chat conversation state
- All integrations: PromptGuard + ExternalRateLimiter enforced on every message

### Admin Panel (`/admin.html`)
- User management + role assignment
- AI backend CRUD (Ollama, OpenAI-compatible)
- **MCP App Store** — browse, configure required variables, and install servers with a single click
- **Update Center** — view current vs latest version, inspect release notes, and hot-swap binary updates
- Third-Party Chat credentials management
- Channel configs + allowUnlinked policy
- External identity link management
- Audit log viewer
- Analytics dashboard (messages/day, active users)

### Deployment & Lifecycle
- **Cross-platform service installer**: Windows Service, systemd, launchd
- **Native Self-Updating System**: Hot-swaps running locked binary executables and web assets seamlessly on all platforms (SCM, Systemd, Launchd)
- **Single binary** — `dotnet publish` produces self-contained executable
- **SQLite** — zero external DB dependencies
- **config.json** overlay — override settings without touching the repo

---

## P2P AI Compute Grid & VPN Binding (v1.2)

Ash Server version 1.2 introduces two powerful network-level capabilities to support decentralized, private, and distributed local AI architectures:

### 1. P2P AI Compute Grid
Ash Server features a built-in hub-and-spoke peer compute sharing system. Multiple instances of Ash Server can cluster together to distribute LLM inference work across different physical machines:
*   **Master Node (Orchestrator)**: Receives client requests, maintains connection pools, and routes tasks based on the capabilities, CPU/GPU, and memory parameters of connected workers.
*   **Worker Node**: Connects to the Master via secure WebSockets to process local inference queries.
    *   *To start in Worker Mode:*
        ```bash
        ash-server --worker --master http://<master-ip>:18799 --token <pairing-token>
        ```
    *   *Auto-Pairing & Reconnection*: Dynamically authenticates with a one-time pairing token, stores a secure generated worker identity, and automatically reconnects with exponential backoff if the link drops.

### 2. Generalized VPN & Adapter Binding
Rather than binding to all public interfaces (`0.0.0.0`) or being hardcoded to a single VPN, Ash Server features generic network interface auto-discovery and binding:
*   **Zero-Trust Mesh & Tunnel Support**: Natively scans and detects secure VPNs like **NetBird**, **Tailscale**, **Proton VPN**, **WireGuard**, **OpenVPN**, and custom adapters, resolving their statuses and IPs.
*   **UI-Driven Interface Pinning**: Exposes active interfaces in the Admin Panel network tab. Administrators can select an interface (e.g. `"NetBird"`, `"Tailscale Tunnel"`, or `"ProtonVPN"`) to save `BindInterface` to config.
*   **Resilient Startup**: Dynamically binds Kestrel to the chosen network IP. If the VPN interface drops or is disabled, the server automatically binds to localhost (`127.0.0.1`) for local-only safety rather than failing to boot.

---

## One-Click MCP App Store & Update System

Ash Server introduces a state-of-the-art administrative experience with two native subsystems built into the core:

### 1. One-Click MCP App Store
Instead of manually typing NPM packages, arguments, and environment keys to configure tools, Ash Server features a fully integrated App Store:
*   **Git-based Registry**: Automatically pulls available packages from the community-driven, public repository at [ssfdre38/mcp-registry](https://github.com/ssfdre38/mcp-registry).
*   **Dynamic Variable Substitution**: Renders customized input forms for required keys (like GitHub personal tokens or folder paths), formats them into command-line arguments and environment variables, writes them to SQLite, and dynamically boots the server instantly in the background.
*   **Pre-built High-Fidelity Apps**: Includes out-of-the-box configurations for *Filesystem*, *Google Search*, *GitHub Integration*, *PostgreSQL*, *Puppeteer Browser*, and *SQLite Database Manager*.
*   **Universal Tool Calling**: Leverages our fully integrated OpenAI tool-calling adapter to enable multi-step agents and MCP tool execution for OpenAI, Groq, Mistral, Together AI, OpenRouter, and local OpenAI endpoints (LM Studio, Jan, LocalAI).

### 2. Native Self-Updating System
Keeping a self-hosted AI orchestrator updated is traditionally complex, especially under different system service contexts:
*   **Dynamic File Swapping**: Renames locked executing binary files first (which filesystems permit) and writes the new build in place, recursively replacing web assets in `wwwroot` while fully preserving configurations and databases.
*   **Deferred Service Restarts**: Automatically detects your platform service manager (**Windows SCM**, **systemd**, or **launchd**) and schedules a delayed daemon shell script to stop, restart the service, and terminate the previous process.
*   **Sleek Update Center Dashboard**: An administrative center showing current vs latest tags, raw markdown release notes, and real-time installation log polling that automatically reconnects the browser session once the server is back online.

---

## Configuration

`appsettings.json` (or create `config.json` beside the exe to override):

```json
{
  "Port": 18799,
  "DatabasePath": "ash_server.db",
  "PersonalityDir": "personality",
  "Jwt": {
    "Secret": "change-me-32-chars-minimum-in-production"
  },
  "RateLimit": {
    "External": { "MaxRequests": 5, "WindowSeconds": 10 },
    "Http":     { "PermitLimit": 60, "WindowSeconds": 60 }
  },
  "PromptGuard": {
    "BlockOnDetect": true,
    "MaxMessageLength": 8000
  },
  "ThirdPartyChat": {
    "BotLinkSecret": "your-bot-secret",
    "Discord":  { "Enabled": false, "BotToken": "", "ApplicationId": "" },
    "Slack":    { "Enabled": false, "BotToken": "", "AppToken": "" },
    "Telegram": { "Enabled": false, "BotToken": "" }
  }
}
```

> ⚠️ **Always change `Jwt:Secret`** before exposing to the network.

---

## Project Structure

```
ash-server/
├── AI/              # Backend manager, Ollama + OpenAI-compat clients
├── Agent/           # Tool definitions + agent runner loop
├── Auth/            # JWT + BCrypt + permission evaluation
├── Chat/
│   ├── Discord/     # Discord.Net gateway bot + message router
│   ├── Slack/       # Slack Events API bot (HMAC verified)
│   ├── Telegram/    # Telegram polling bot
│   ├── ChatHandler.cs      # Core WebSocket/streaming handler
│   ├── IdentityResolver.cs # Provider → RBAC resolution
│   └── PromptGuard.cs      # Injection detection
├── Controllers/     # REST API (auth, conversations, admin, MCP, bots)
├── Data/            # SQLite + FTS5 database layer
├── Middleware/      # ExternalRateLimiter
├── Models/          # Records + DTOs
├── Personality/     # soul.json loader + default personality/
├── Plugins/         # Plugin manifest + manager
├── Service/         # Cross-platform service installer
├── build/
│   ├── publish-all.ps1     # Build all 5 platform targets
│   ├── publish-all.sh
│   ├── windows/
│   │   └── installer.nsi   # NSIS Windows installer script
│   ├── linux/
│   │   └── install.sh      # Linux systemd installer
│   └── macos/
│       └── install.sh      # macOS launchd installer
├── .github/workflows/
│   └── release.yml         # CI: build all targets + GitHub Release on tag push
├── wwwroot/
│   ├── index.html   # Chat UI (vanilla JS SPA)
│   └── admin.html   # Admin panel
├── install.sh       # One-liner: git clone + build + install (Linux/macOS)
├── install.ps1      # One-liner: git clone + build + install (Windows)
├── Program.cs       # Entry point, DI, service hosting
└── appsettings.json
```

---

## The Ash Ecosystem

| Project | Description |
|---------|-------------|
| **ash-server** | This — secure AI backend |
| [gemma4-turbo](https://ollama.com/ssfdre38/gemma4-turbo) | IQ4_XS Gemma 4 for Ollama — 17.3k+ downloads |
| [ash-bot](https://github.com/ssfdre38/ash-bot) | .NET 10 Discord bot — Ash's personality, 20 built-in tools, long-term memory |
| [Discord](https://discord.gg/DCYC2fFQQ6) | Join us on the G4Turbo.com Discord server.

---

## License

MIT — see [LICENSE](LICENSE).

Model weights: [Gemma Terms of Use](https://ai.google.dev/gemma/terms).

