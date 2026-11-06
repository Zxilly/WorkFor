import { project, mapPositions } from './projection.mjs';
import { schemaFor } from './schemas.mjs';

export class LanguageConnection {
  state = { value: { state: 'open' }, onChange: () => ({ dispose() {} }) };
  pending = new Map();
  queue = [];
  serial = 0;
  ready = false;
  initializeId = null;
  serverInfo = null;
  constructor(bridge, configuration, model) {
    this.bridge = bridge;
    this.config = configuration;
    this.model = model;
    this.projection = project(model.getValue(), configuration.kind === 'script');
  }
  toString() { return 'WorkFor local stdio LSP'; }
  setListener(listener) { this.listener = listener; if (listener) for (const message of this.queue.splice(0)) listener(message); }
  async send(message) {
    message = structuredClone(message);
    // Monaco's LSP adapter normalizes didOpen URIs on Windows but can retain
    // display casing on later requests. Keep every document key consistent.
    if (message.params?.textDocument?.uri?.startsWith('file:')) message.params.textDocument.uri = message.params.textDocument.uri.toLowerCase();
    if (message.method === 'initialize') {
      this.initializeId = message.id;
      message.params.rootUri = this.config.workspaceUri;
      message.params.workspaceFolders = [{ uri: this.config.workspaceUri, name: 'WorkFor' }];
      message.params.initializationOptions = this.config.kind === 'script'
        ? { disableAutomaticTypingAcquisition: true, tsserver: { path: this.config.tsserverPath } }
        : { provideFormatter: true, handledSchemaProtocols: [] };
      if (message.params.capabilities.textDocument) delete message.params.capabilities.textDocument.semanticTokens;
    } else if (['textDocument/didOpen', 'textDocument/didChange'].includes(message.method)) {
      this.serverDocumentUri = message.params.textDocument.uri;
      this.projection = project(this.model.getValue(), this.config.kind === 'script');
      if (message.method.endsWith('didOpen')) message.params.textDocument.text = this.projection.text;
      else message.params.contentChanges = [{ text: this.projection.text }];
      this.ready = true;
      this.bridge.postMessage({ type: 'language-ready' });
    } else if (message.params) message.params = mapPositions(message.params, point => this.projection.toServer(point), this.model.uri.toString());
    this.bridge.postMessage({ type: 'lsp', message });
    if (message.method === 'initialized') this.updateConfiguration(this.config);
    if (message.method === 'textDocument/didOpen') this.updateConfiguration(this.config);
  }
  receive(message) {
    if (message.method === 'workspace/configuration') {
      this.bridge.postMessage({ type: 'lsp', message: { jsonrpc: '2.0', id: message.id, result: message.params.items.map(() => ({})) } });
      return;
    }
    if (message.id === this.initializeId && message.result) {
      this.serverInfo = message.result.serverInfo;
      delete message.result.capabilities.semanticTokensProvider;
    }
    if (message.result) message.result = mapPositions(message.result, point => this.projection.toEditor(point), this.model.uri.toString());
    if (message.params) message.params = mapPositions(message.params, point => this.projection.toEditor(point), this.model.uri.toString());
    if (this.pending.has(message.id)) {
      const { resolve, reject, timer } = this.pending.get(message.id);
      this.pending.delete(message.id); clearTimeout(timer);
      if (message.error) reject(new Error(message.error.message)); else resolve(message.result);
      return;
    }
    if (this.listener) this.listener(message); else this.queue.push(message);
  }
  updateConfiguration(configuration) {
    this.config = configuration;
    const schema = schemaFor(configuration.kind, configuration.parametersSchema);
    this.bridge.postMessage({ type: 'lsp', message: { jsonrpc: '2.0', method: 'workspace/didChangeConfiguration', params: {
      settings: { json: { validate: { enable: true }, schemas: schema == null ? [] : [{ uri: 'https://workfor.invalid/schema', fileMatch: [this.serverDocumentUri ?? this.model.uri.toString()], schema }] } },
    } } });
  }
  request(method, params) {
    const id = `workfor-${++this.serial}`;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error('Language server request timed out')); }, 10000);
      this.pending.set(id, { resolve, reject, timer });
      if (params.textDocument) params.textDocument.uri = this.serverDocumentUri ?? params.textDocument.uri;
      this.send({ jsonrpc: '2.0', id, method, params }).catch(reject);
    });
  }
}
