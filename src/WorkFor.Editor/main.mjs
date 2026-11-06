import * as monaco from 'monaco-editor';
import { LanguageConnection } from './lsp.mjs';
import './style.css';

self.MonacoEnvironment = { getWorker: () => new Worker(new URL('editor.worker.js', location.href)) };
monaco.typescript.javascriptDefaults.setModeConfiguration({});
monaco.typescript.typescriptDefaults.setModeConfiguration({});
monaco.json.jsonDefaults.setModeConfiguration({ tokens: true });
const bridge = window.chrome?.webview;
const errors = [];
window.addEventListener('unhandledrejection', event => { errors.push(String(event.reason?.stack ?? event.reason)); if (errors.length > 16) errors.shift(); });
let model, editor, connection, client;
let config = { revision: 0, kind: 'parameters', value: '' };
let applying = false;
function send(message) { bridge?.postMessage(message); }
function configure(value) {
  if (!Number.isSafeInteger(value.revision) || typeof value.value !== 'string') return;
  const replaceValue = config.revision !== value.revision;
  config = value;
  applying = true;
  try {
    if (!model) {
      model = monaco.editor.createModel(value.value, value.kind === 'script' ? 'javascript' : 'json', monaco.Uri.parse(value.documentUri));
      editor = monaco.editor.create(document.querySelector('#editor'), {
        model, automaticLayout: true, minimap: { enabled: false }, fontFamily: 'Consolas, monospace', fontSize: 14,
        lineNumbersMinChars: 3, scrollBeyondLastLine: false, padding: { top: 8, bottom: 8 },
        scrollbar: { alwaysConsumeMouseWheel: false }, tabSize: 2, insertSpaces: true, wordWrap: 'off',
        unicodeHighlight: { ambiguousCharacters: false }, fixedOverflowWidgets: true, renderLineHighlight: 'gutter',
      });
      connection = new LanguageConnection(bridge, value, model);
      client = new monaco.lsp.MonacoLspClient(connection);
      model.onDidChangeContent(() => { if (!applying) send({ type: 'changed', revision: config.revision, value: model.getValue() }); });
      editor.onDidContentSizeChange(() => send({ type: 'height', height: editor.getContentHeight() }));
    } else if (replaceValue && model.getValue() !== value.value) model.setValue(value.value);
    editor.updateOptions({ readOnly: !!value.readOnly, ariaLabel: value.label ?? '代码编辑器' });
    const theme = value.theme ?? { name: 'vs' };
    if (theme.name.startsWith('hc-')) monaco.editor.setTheme(theme.name);
    else {
      monaco.editor.defineTheme('workfor', { base: theme.name === 'vs-dark' ? 'vs-dark' : 'vs', inherit: true,
        rules: [], colors: theme.background ? { 'editor.background': theme.background, 'editor.foreground': theme.foreground } : {} });
      monaco.editor.setTheme('workfor');
    }
    if (connection.ready) connection.updateConfiguration(value);
  } finally { applying = false; }
  send({ type: 'height', height: editor.getContentHeight() });
}
async function request(method, parameters = {}) {
  if (!connection?.ready) throw new Error('代码提示尚未就绪');
  return connection.request(method, { textDocument: { uri: model.uri.toString() }, ...parameters });
}
window.workforEditor = {
  configure,
  getState: () => ({ revision: config.revision, value: model?.getValue() ?? config.value, language: model?.getLanguageId() }),
  format: async () => {
    const end = model.getPositionAt(model.getValueLength());
    const edits = await request('textDocument/rangeFormatting', {
      range: { start: { line: 0, character: 0 }, end: { line: end.lineNumber - 1, character: end.column - 1 } },
      options: { tabSize: 2, insertSpaces: true },
    });
    if (edits?.length) editor.executeEdits('format', edits.map(edit => ({
      range: new monaco.Range(edit.range.start.line + 1, edit.range.start.character + 1, edit.range.end.line + 1, edit.range.end.character + 1), text: edit.newText,
    })));
  },
  inspect: () => ({ state: window.workforEditor.getState(), lspReady: connection?.ready, serverInfo: connection?.serverInfo, errors,
    tokens: model ? monaco.editor.tokenize(model.getValue(), model.getLanguageId()) : [],
    markers: model ? monaco.editor.getModelMarkers({ resource: model.uri }) : [],
    theme: document.querySelector('.monaco-editor')?.className }),
  request,
  suggest: (lineNumber, column) => { editor.setPosition({ lineNumber, column }); editor.focus(); editor.trigger('keyboard', 'editor.action.triggerSuggest', {}); },
};
bridge?.addEventListener('message', event => {
  if (event.data.type === 'lsp') connection?.receive(event.data.message);
  else if (event.data.type === 'format') window.workforEditor.format().then(
    () => send({ type: 'formatted', id: event.data.id }),
    error => send({ type: 'formatted', id: event.data.id, error: error.message }));
  else configure(event.data);
});
send({ type: 'ready' });
