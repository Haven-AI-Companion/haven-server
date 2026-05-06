/**
 * Process plugin handler for Ash Server plugins.
 *
 * The server writes JSON to stdin: { "_tool": "name", ...args }
 * You write the result to stdout.
 *
 * @param {Object.<string, Function>} tools - Map of tool name → async (args) => string
 */
export async function runProcessPlugin(tools) {
  const chunks = [];
  process.stdin.on('data', (chunk) => chunks.push(chunk));
  process.stdin.on('end', async () => {
    try {
      const raw = Buffer.concat(chunks).toString('utf8').trim();
      const data = raw ? JSON.parse(raw) : {};
      const toolName = data._tool ?? 'unknown';
      delete data._tool;

      const handler = tools[toolName];
      if (!handler) {
        process.stdout.write(`Unknown tool: ${toolName}`);
        process.exit(1);
        return;
      }

      const result = await handler(data);
      process.stdout.write(typeof result === 'string' ? result : JSON.stringify(result));
      process.exit(0);
    } catch (err) {
      process.stdout.write(`Plugin error: ${err.message}`);
      process.exit(1);
    }
  });
}
