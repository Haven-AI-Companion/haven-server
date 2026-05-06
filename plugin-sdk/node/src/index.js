/**
 * Ash Server Plugin SDK — Node.js
 *
 * @example HTTP plugin
 *   import { HttpPlugin } from 'ash-plugin-sdk';
 *   const plugin = new HttpPlugin();
 *   plugin.tool('echo', 'Echoes a message', async ({ message }) => message);
 *   plugin.listen(19000);
 *
 * @example Process plugin
 *   import { runProcessPlugin } from 'ash-plugin-sdk';
 *   runProcessPlugin({ echo: async ({ message }) => message });
 */

export { HttpPlugin } from './http-handler.js';
export { runProcessPlugin } from './process-handler.js';
