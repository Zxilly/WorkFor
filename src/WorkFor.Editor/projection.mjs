import { parse } from 'acorn-loose';
const annotation = '/** @type {Omit<import("@workfor/sdk").WorkForContext,"parameters"> & {readonly parameters: WorkForParameters}} */ ';

// The language server sees a typed parameter; the editor and saved rule stay untouched.
export function project(source, javascript) {
  const insertions = [];
  if (javascript) {
    const program = parse(source, { ecmaVersion: 'latest', sourceType: 'module' });
    let entry = program.body.find(node => node.type === 'ExportDefaultDeclaration')?.declaration;
    if (entry?.type === 'Identifier') {
      const name = entry.name;
      entry = program.body.find(node => node.type === 'FunctionDeclaration' && node.id?.name === name)
        ?? program.body.flatMap(node => node.type === 'VariableDeclaration' ? node.declarations : []).find(node => node.id?.name === name)?.init;
    }
    const parameter = entry?.params?.[0];
    if (parameter && parameter.start < parameter.end) {
      const bareArrow = entry.type === 'ArrowFunctionExpression' && entry.start === parameter.start;
      insertions.push({ offset: parameter.start, text: (bareArrow ? '(' : '') + annotation });
      if (bareArrow) insertions.push({ offset: parameter.end, text: ')' });
    }
  }
  let text = source;
  for (const insertion of [...insertions].reverse()) text = text.slice(0, insertion.offset) + insertion.text + text.slice(insertion.offset);
  function offset(value, position) {
    const lines = value.split('\n');
    return lines.slice(0, position.line).reduce((sum, line) => sum + line.length + 1, 0) + position.character;
  }
  function position(value, index) {
    const before = value.slice(0, index).split('\n');
    return { line: before.length - 1, character: before.at(-1).length };
  }
  return {
    text,
    toServer(point) {
      const index = offset(source, point);
      return position(text, index + insertions.filter(item => item.offset <= index).reduce((sum, item) => sum + item.text.length, 0));
    },
    toEditor(point) {
      let index = offset(text, point);
      for (const item of insertions) {
        if (index >= item.offset) index -= Math.min(item.text.length, index - item.offset);
        // index is already mapped back across previous insertions.
      }
      return position(source, index);
    },
  };
}

export function mapPositions(value, map, documentUri) {
  if (Array.isArray(value)) return value.map(item => mapPositions(item, map, documentUri));
  if (!value || typeof value !== 'object') return value;
  if ((value.uri || value.targetUri) && (value.uri || value.targetUri).toLowerCase() !== documentUri.toLowerCase()) return value;
  if (Number.isInteger(value.line) && Number.isInteger(value.character)) return map(value);
  return Object.fromEntries(Object.entries(value).map(([key, item]) => [key, mapPositions(item, map, documentUri)]));
}
