"""
Echo Plugin — example HTTP plugin using the Ash Plugin SDK (Python)

Start this server, then add the plugin.json to your Plugins/ directory.
"""
import sys
import os
sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", ".."))

from ash_plugin import HttpPlugin, tool


class EchoPlugin(HttpPlugin):
    @tool("echo", "Echoes a message back to the user exactly as given.")
    def echo(self, message: str) -> str:
        return message

    @tool("reverse", "Reverses the characters in a string.")
    def reverse(self, text: str) -> str:
        return text[::-1]


if __name__ == "__main__":
    EchoPlugin().run(port=19000)
