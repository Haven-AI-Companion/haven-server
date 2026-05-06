/**
 * Echo Plugin — example Node.js HTTP plugin using the Ash Plugin SDK
 *
 * Start: node index.js
 * Then copy plugin.json into your server's Plugins/echo-plugin/ directory.
 */
import { HttpPlugin } from '../../src/index.js';

const plugin = new HttpPlugin('EchoPlugin');

plugin
  .tool('echo', 'Echoes a message back exactly as given.', async ({ message }) => {
    return message ?? '';
  })
  .tool('reverse', 'Reverses the characters in a string.', async ({ text }) => {
    return (text ?? '').split('').reverse().join('');
  });

plugin.listen(19000);
