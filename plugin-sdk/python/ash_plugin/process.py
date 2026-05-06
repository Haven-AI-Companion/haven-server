"""
Process plugin base — reads JSON from stdin, writes result to stdout.

Usage:
    from ash_plugin import ProcessPlugin, run_process_plugin

    class MyPlugin(ProcessPlugin):
        def handle_my_tool(self, query: str) -> str:
            return f"Result: {query}"

    if __name__ == "__main__":
        run_process_plugin(MyPlugin())

    Or use the functional style:
        def handle(tool_name, args):
            if tool_name == "my_tool":
                return f"Result: {args['query']}"
            return f"Unknown tool: {tool_name}"

        run_process_plugin(handle)
"""
import json
import sys
from typing import Callable, Union


class ProcessPlugin:
    """
    Base class for process-handler plugins.

    The server spawns your script and:
    - Writes JSON args object to stdin
    - Reads your response from stdout

    Override handle_{tool_name} methods for each tool, or override handle() directly.

    Example:
        class WeatherPlugin(ProcessPlugin):
            def handle_get_weather(self, city: str) -> str:
                return f"It's sunny in {city} today."

        if __name__ == "__main__":
            run_process_plugin(WeatherPlugin())
    """

    def handle(self, tool_name: str, args: dict) -> str:
        method_name = f"handle_{tool_name}"
        fn = getattr(self, method_name, None)
        if fn is None:
            return f"Unknown tool: {tool_name}"
        try:
            return str(fn(**args))
        except TypeError as e:
            return f"Tool argument error: {e}"
        except Exception as e:
            return f"Tool error: {e}"


def run_process_plugin(handler: Union[ProcessPlugin, Callable]):
    """
    Read tool args from stdin, call the handler, write result to stdout.
    This is the entry point for process-type plugins.
    """
    try:
        raw = sys.stdin.read()
        data = json.loads(raw) if raw.strip() else {}

        # Support both class-based and functional handlers
        if isinstance(handler, ProcessPlugin):
            # For process plugins, stdin is just the args dict
            # But the tool name comes from the manifest — use a wrapper convention
            # If there's a _tool key in data, use it; otherwise call handle with first key
            tool_name = data.pop("_tool", None) or "unknown"
            result = handler.handle(tool_name, data)
        elif callable(handler):
            tool_name = data.pop("_tool", "unknown")
            result = handler(tool_name, data)
        else:
            result = "Invalid handler"

        print(str(result), end="")
        sys.exit(0)
    except json.JSONDecodeError as e:
        print(f"Invalid JSON from server: {e}", end="")
        sys.exit(1)
    except Exception as e:
        print(f"Plugin error: {e}", end="")
        sys.exit(1)
