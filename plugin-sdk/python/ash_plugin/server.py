"""
HTTP plugin base — wraps Flask to expose tool endpoints.

Usage:
    from ash_plugin import HttpPlugin, tool

    class MyPlugin(HttpPlugin):
        @tool("my_tool", "Does something useful")
        def my_tool(self, query: str) -> str:
            return f"Result for: {query}"

    if __name__ == "__main__":
        MyPlugin().run(port=19000)
"""
import functools
import json
from typing import Callable

try:
    from flask import Flask, request, jsonify
except ImportError:
    raise ImportError("Flask is required for HttpPlugin. Install it: pip install flask")


def tool(name: str, description: str):
    """Decorator that marks a method as an Ash plugin tool."""
    def decorator(fn: Callable) -> Callable:
        fn._ash_tool = True
        fn._ash_tool_name = name
        fn._ash_tool_description = description
        @functools.wraps(fn)
        def wrapper(*args, **kwargs):
            return fn(*args, **kwargs)
        wrapper._ash_tool = True
        wrapper._ash_tool_name = name
        wrapper._ash_tool_description = description
        return wrapper
    return decorator


class HttpPlugin:
    """
    Base class for HTTP-handler plugins.

    The server POSTs JSON to your endpoint:
        POST /  body: {"tool": "name", "args": {...}}

    This class routes the call to the matching @tool method and
    returns the result as plain text.

    Example:
        class EchoPlugin(HttpPlugin):
            @tool("echo", "Echoes a message back")
            def echo(self, message: str) -> str:
                return message

        EchoPlugin().run(port=19000)
    """

    def __init__(self):
        self._app = Flask(self.__class__.__name__)
        self._tools: dict[str, Callable] = {}
        self._register_tools()
        self._app.route("/", methods=["POST"])(self._dispatch)

    def _register_tools(self):
        for attr_name in dir(self):
            fn = getattr(self, attr_name)
            if callable(fn) and getattr(fn, "_ash_tool", False):
                self._tools[fn._ash_tool_name] = fn

    def _dispatch(self):
        try:
            body = request.get_json(force=True)
            tool_name = body.get("tool", "")
            args = body.get("args", {})

            if tool_name not in self._tools:
                return f"Unknown tool: {tool_name}", 400

            fn = self._tools[tool_name]
            result = fn(**args)

            if isinstance(result, (dict, list)):
                return json.dumps(result)
            return str(result)

        except Exception as exc:
            return f"Plugin error: {exc}", 500

    def run(self, host: str = "0.0.0.0", port: int = 19000, debug: bool = False):
        """Start the plugin HTTP server."""
        print(f"[ash-plugin] {self.__class__.__name__} listening on http://{host}:{port}")
        print(f"[ash-plugin] Tools: {', '.join(self._tools)}")
        self._app.run(host=host, port=port, debug=debug)
