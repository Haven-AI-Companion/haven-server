# 🌸 Ash Server

> Modern self-hosted AI chat server — streaming, multi-user, agent mode, admin panel. Built for Ollama.

Ash Server is a lightweight, self-contained AI chat backend written in **C# / ASP.NET Core**. It connects to any Ollama instance (or OpenAI-compatible API) and provides a full-featured web chat interface with JWT authentication, conversation history, and an agentic tool-calling mode.

---

## Features

- **Streaming chat** — token-by-token responses via WebSocket, just like ChatGPT
- **Agent mode** — tool-calling loop with built-in tools: web search, URL fetch, calculator, clock
- **JWT authentication** — register/login, per-user isolated conversations, admin roles
- **Multi-backend** — connect multiple Ollama instances or OpenAI-compatible APIs
- **SQLite persistence** — conversations and messages stored locally, no external DB needed
- **Personality system** — customise Ash's persona via `soul.json` and per-user context files
- **Admin panel** — manage users, backends, view analytics, trigger backups
- **Zero-config Ollama** — auto-discovers `localhost:11434` with no setup required

---

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Ollama](https://ollama.ai) running locally (or any OpenAI-compatible API)

---

## Quick Start

```bash
git clone https://github.com/ssfdre38/ash-server
cd ash-server
dotnet run
```

Then open **http://localhost:18799** in your browser.

The first registered user is automatically made admin.

---

## Configuration

Edit `appsettings.json` (or create a `config.json` beside the exe to override without touching the repo):

```json
{
  "Port": 18799,
  "DatabasePath": "ash_server.db",
  "PersonalityDir": "personality",
  "DefaultModel": "",
  "RequireAuth": true,
  "AllowRegistration": true,
  "Jwt": {
    "Secret": "change-me-in-production"
  }
}
```

> ⚠️ **Change `Jwt:Secret`** before exposing to the internet.

---

## Personality

Drop files in the `personality/` folder:

| File | Purpose |
|------|---------|
| `soul.json` | Base system prompt used for all users |
| `{username}.txt` | Extra context injected for a specific user |

---

## Admin Panel

Navigate to **http://localhost:18799/admin.html** (or click the ⚙️ button when logged in as admin).

From there you can:
- Add/remove AI backends (Ollama or OpenAI-compatible)
- Manage users and admin roles
- View message/conversation analytics
- Trigger a database backup

---

## Agent Mode

Toggle the **🔧** button in the chat input bar. In agent mode, Ash can:

- **web_search** — search DuckDuckGo Lite
- **fetch_url** — retrieve any URL
- **calculate** — evaluate math expressions
- **get_time** — current date/time

Agent mode uses a tool-calling loop (up to 8 iterations) and requires a model that supports Ollama's tools API (e.g. `qwen2.5`, `gemma4`, `llama3.1`).

---

## Project Structure

```
ash-server/
├── AI/                  # Backend manager, Ollama + OpenAI-compat clients
├── Agent/               # Tool definitions + agent runner loop
├── Auth/                # JWT generation + BCrypt password hashing
├── Chat/                # WebSocket handler
├── Controllers/         # REST API endpoints
├── Data/                # SQLite database layer
├── Models/              # Records / DTOs
├── Personality/         # Personality file loader
├── wwwroot/             # Static frontend (index.html, admin.html)
│   ├── index.html       # Chat UI
│   └── admin.html       # Admin panel
├── personality/         # Default personality files
├── Program.cs           # App entry point + DI wiring
└── appsettings.json     # Configuration
```

---

## Part of the Ash Ecosystem

| Project | Description |
|---------|-------------|
| **ash-server** | This — the backend chat server |
| [ash-forge](https://github.com/ash-forge) | AI model training and fine-tuning tools |

---

## License

MIT
