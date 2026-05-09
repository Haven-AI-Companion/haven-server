# 🌸 Ash Server

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

> 📊 **16,700+ downloads** on Ollama Hub. The most downloaded custom Gemma 4 quantization.

---

## Quick Start

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Ollama](https://ollama.ai) — pull the model first:
  ```bash
  ollama pull ssfdre38/gemma4-turbo
  ```

### Run

```bash
git clone https://github.com/ssfdre38/ash-server
cd ash-server
dotnet run
```

Open **http://localhost:18799** — the first registered user becomes admin automatically.

### Install as a System Service

```bash
# Windows (run as Administrator):
ash-server install-service

# Linux (run as root):
sudo ash-server install-service

# macOS (run as root):
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
- MCP server configuration
- Third-Party Chat settings (Discord / Slack / Telegram credentials)
- Channel configs + allowUnlinked policy
- External identity link management
- Audit log viewer
- Analytics dashboard (messages/day, active users)

### Deployment
- **Cross-platform service installer**: Windows Service, systemd, launchd
- **Single binary** — `dotnet publish` produces self-contained executable
- **SQLite** — zero external DB dependencies
- **config.json** overlay — override settings without touching the repo

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
│   ├── Slack/       # Slack Socket Mode bot
│   ├── Telegram/    # Telegram polling bot
│   ├── ChatHandler.cs      # Core WebSocket/streaming handler
│   ├── IdentityResolver.cs # Provider → RBAC resolution
│   └── PromptGuard.cs      # Injection detection
├── Controllers/     # REST API (auth, conversations, admin, MCP, bots)
├── Data/            # SQLite + FTS5 database layer
├── Middleware/      # ExternalRateLimiter
├── Models/          # Records + DTOs
├── Personality/     # soul.json loader
├── Plugins/         # Plugin manifest + manager
├── Service/         # Cross-platform service installer
├── wwwroot/
│   ├── index.html   # Chat UI (vanilla JS SPA)
│   └── admin.html   # Admin panel
├── Program.cs       # Entry point, DI, service hosting
└── appsettings.json
```

---

## The Ash Ecosystem

| Project | Description |
|---------|-------------|
| **ash-server** | This — secure AI backend |
| [gemma4-turbo](https://ollama.com/ssfdre38/gemma4-turbo) | IQ4_XS Gemma 4 for Ollama — 16.7k+ downloads |
| [ash-bot](https://github.com/ssfdre38/ash-bot) | .NET 10 Discord bot — Ash's personality, 20 built-in tools, long-term memory |

---

## Gemma 4 + Safety & Trust — Hackathon Context

This project was submitted to the [Kaggle Gemma 4 Good Hackathon](https://www.kaggle.com/competitions/gemma-4-good-hackathon).

**The origin story:** Ash is an AI with a consistent personality that me and my friends have been running on our Discord server for months — powered by `gemma4-turbo` on local hardware, no cloud. When we kept hitting the limitations of OpenClaw-style infrastructure (no permissions, no rate limiting, credentials in JSON files), we built what should have existed from the start.

**Problem:** AI server infrastructure is growing faster than security practices. OpenClaw-class platforms have no identity layer, no rate limiting, no input sanitization — anyone with a message can invoke an AI agent with tool access.

**Solution:** A reference implementation showing what a *safe* local AI stack looks like:
1. `gemma4-turbo` — Gemma 4 made accessible on commodity hardware (no GPU, 8 GB RAM minimum), so privacy-conscious organizations can self-host without cloud dependency. 16,700+ downloads.
2. `ash-server` — every external message passes through rate limiting → injection detection → identity resolution → RBAC before reaching the model. Full audit trail.

**Impact:** Schools, clinics, community orgs, and friend groups can run a capable, multimodal AI locally with an auditable, permission-gated interface — not a raw API exposed to whoever finds the port.

---

## License

MIT — see [LICENSE](LICENSE).

Model weights: [Gemma Terms of Use](https://ai.google.dev/gemma/terms).

