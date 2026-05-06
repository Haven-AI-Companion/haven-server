# Ash Server Plugin SDK

Build plugins for [ash-server](https://github.com/ssfdre38/ash-server) — add custom AI tools that Ash can call during conversations.

## How plugins work

Each plugin lives in a directory inside the server's `Plugins/` folder:

```
Plugins/
  my-plugin/
    plugin.json      ← manifest (required)
    ...              ← your code (optional, for process-type plugins)
```

The server reads all `plugin.json` manifests at startup and whenever you click **Reload** in the admin panel.

## Manifest format

```jsonc
{
  "id": "my-plugin",          // unique slug, no spaces
  "name": "My Plugin",        // display name
  "version": "1.0.0",
  "description": "What this plugin does",
  "enabled": true,
  "tools": [
    {
      "name": "my_tool",      // snake_case, what the AI calls
      "description": "Describe what this tool does (the AI reads this)",
      "parameters": {         // JSON Schema for the tool arguments
        "type": "object",
        "properties": {
          "query": { "type": "string", "description": "The search query" }
        },
        "required": ["query"]
      },
      "handler": {
        "type": "http",       // "http" | "process" | "builtin"
        "url": "http://localhost:19000/my_tool"
      }
    }
  ]
}
```

## Handler types

### `http` — call a web server

The server sends a POST request to your URL:

```json
{ "tool": "my_tool", "args": { "query": "hello" } }
```

Respond with plain text or JSON string — the AI receives whatever you return.

**Best for:** plugins running as persistent services (Python Flask, Node.js Express, etc.)

### `process` — spawn a process per call

The server spawns your command, writes JSON to stdin, reads the result from stdout:

- **stdin:** `{ "query": "hello" }` (just the args object)
- **stdout:** your result as a string
- Timeout: 30 seconds

**Best for:** scripts in any language, one-shot tools.

### `builtin` — core server tool

Used internally by the `core-tools` manifest. You don't need this.

---

## SDKs

| Language | Location | Handler types |
|----------|----------|---------------|
| Python   | `python/` | HTTP (Flask) + Process (stdin/stdout) |
| Node.js  | `node/`   | HTTP (Express) + Process (stdin/stdout) |
| C#       | `csharp/` | HTTP (ASP.NET minimal) + Process |

## Quick start (Python HTTP plugin)

```bash
cd plugin-sdk/python
pip install flask
python examples/echo_plugin/main.py &
```

Copy `examples/echo_plugin/plugin.json` into `Plugins/echo-plugin/plugin.json`, then click **Reload** in the admin panel.

## JSON Schema validation

Use `schema/plugin.schema.json` with your editor for autocomplete and validation.

```jsonc
{
  "$schema": "./../../schema/plugin.schema.json",
  ...
}
```
