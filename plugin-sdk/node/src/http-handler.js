/**
 * HTTP plugin handler for Ash Server plugins.
 *
 * The server sends: POST / body: { tool: "name", args: { ... } }
 * You return a string (or anything that toString()s cleanly).
 */
import express from 'express';

export class HttpPlugin {
  #app;
  #tools = new Map();
  #name;

  constructor(name = 'AshPlugin') {
    this.#name = name;
    this.#app = express();
    this.#app.use(express.json());
    this.#app.post('/', this.#dispatch.bind(this));
  }

  /**
   * Register a tool handler.
   * @param {string} name - Tool name (must match plugin.json)
   * @param {string} description - What the tool does (for reference)
   * @param {Function} handler - async (args) => string
   */
  tool(name, description, handler) {
    this.#tools.set(name, { description, handler });
    return this;
  }

  async #dispatch(req, res) {
    try {
      const { tool: toolName, args = {} } = req.body ?? {};
      const entry = this.#tools.get(toolName);

      if (!entry) {
        return res.status(400).send(`Unknown tool: ${toolName}`);
      }

      const result = await entry.handler(args);
      res.type('text').send(typeof result === 'string' ? result : JSON.stringify(result));
    } catch (err) {
      res.status(500).send(`Plugin error: ${err.message}`);
    }
  }

  /**
   * Start listening.
   * @param {number} port
   * @param {string} host
   */
  listen(port = 19000, host = '0.0.0.0') {
    this.#app.listen(port, host, () => {
      console.log(`[ash-plugin] ${this.#name} listening on http://${host}:${port}`);
      console.log(`[ash-plugin] Tools: ${[...this.#tools.keys()].join(', ')}`);
    });
  }
}
