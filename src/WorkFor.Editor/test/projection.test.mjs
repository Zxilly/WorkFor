import test from 'node:test';
import assert from 'node:assert/strict';
import { project, mapPositions } from '../projection.mjs';

for (const source of [
  'export default function calculate(ctx) { return ctx.currency; }',
  'export default ctx => ctx.periods.day;',
  'export default (ctx) => ctx.parameters;',
  '// export default function fake(x) {}\nconst calculate = context => context.now;\nexport default calculate;',
  'const message = "export default function fake(x) {}";\nexport default function ({currency}) { return currency; }',
]) {
  test(`projection retains positions: ${source.slice(0, 55)}`, () => {
    const result = project(source, true);
    assert.ok(result.text.includes('@workfor/sdk'));
    const lines = source.split('\n');
    for (let line = 0; line < lines.length; line++) for (let character = 0; character <= lines[line].length; character++) {
      const point = { line, character };
      assert.deepEqual(result.toEditor(result.toServer(point)), point);
    }
  });
}
test('JSON and unrelated source documents have no synthetic offsets', () => {
  const result = project('{"start":"09:00"}', false);
  assert.equal(result.text, '{"start":"09:00"}');
  const location = { uri: 'file:///other.d.ts', range: { start: { line: 1, character: 10 }, end: { line: 1, character: 11 } } };
  assert.deepEqual(mapPositions(location, () => ({ line: 9, character: 9 }), 'file:///document.js'), location);
});
