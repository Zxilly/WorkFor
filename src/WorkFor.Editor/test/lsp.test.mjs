import test from 'node:test';
import assert from 'node:assert/strict';
import { LanguageConnection } from '../lsp.mjs';

test('Windows didOpen and provider requests address the same LSP document', async () => {
  const messages = [];
  const uri = 'file:///C:/Users/Test/Workspace/document.js';
  const model = { getValue: () => 'export default ctx => ctx.currency;', uri: { toString: () => uri } };
  const connection = new LanguageConnection({ postMessage: message => messages.push(message) }, { kind: 'script' }, model);
  await connection.send({ jsonrpc: '2.0', method: 'textDocument/didOpen', params: { textDocument: { uri, text: model.getValue(), version: 1 } } });
  await connection.send({ jsonrpc: '2.0', id: 7, method: 'textDocument/completion', params: { textDocument: { uri }, position: { line: 0, character: 29 } } });
  const rpc = messages.filter(message => message.type === 'lsp' && message.message.params?.textDocument);
  assert.equal(rpc[0].message.params.textDocument.uri, rpc[1].message.params.textDocument.uri);
  assert.ok(rpc[0].message.params.textDocument.text.includes('@workfor/sdk'));
  assert.equal(model.getValue(), 'export default ctx => ctx.currency;');
});
