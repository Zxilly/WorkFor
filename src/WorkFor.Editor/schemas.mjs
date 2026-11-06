const shift = {
  type: 'object', required: ['start', 'end'], additionalProperties: false,
  properties: {
    start: { type: 'string', pattern: '^([01][0-9]|2[0-3]):[0-5][0-9]$', description: '开始时间，例如 09:00' },
    end: { type: 'string', pattern: '^([01][0-9]|2[0-3]):[0-5][0-9]$', description: '结束时间，例如 18:00' },
    endNextDay: { type: 'boolean', description: '结束时间是否在次日' },
  },
};
export const shifts = { type: 'array', maxItems: 16, items: shift };
export const dates = {
  type: 'object', maxProperties: 366, additionalProperties: false,
  patternProperties: { '^[0-9]{4}-[0-9]{2}-[0-9]{2}$': shifts },
};
export const manifest = {
  type: 'object', additionalProperties: false,
  required: ['manifestVersion', 'apiVersion', 'id', 'name', 'version', 'entry', 'parametersSchema'],
  properties: {
    manifestVersion: { const: 1 }, apiVersion: { const: 1 }, entry: { const: 'index.mjs' },
    id: { type: 'string', pattern: '^[a-z0-9][a-z0-9._-]{0,79}$' }, name: { type: 'string', minLength: 1, maxLength: 40 },
    version: { type: 'string' }, description: { type: 'string', maxLength: 200 },
    parametersSchema: { type: ['object', 'boolean'], description: '参数的 JSON Schema；最终校验由计算宿主完成。' },
  },
};
export function schemaFor(kind, parameters) {
  return kind === 'shifts' ? shifts : kind === 'dates' ? dates : kind === 'manifest' ? manifest : parameters;
}
