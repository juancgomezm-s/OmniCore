// Public validation profile: standard JSON Schema + the documented x-coreView semantics.
// Ajv is a development/client validation dependency, not shipped inside plugin packages.
import Ajv2020 from 'ajv/dist/2020.js';
import { readFileSync } from 'node:fs';
const schema = JSON.parse(readFileSync(new URL('./protocol-v1.schema.json', import.meta.url), 'utf8'));
const controls = /[\u0000-\u001f\u007f-\u009f]/u;
const unique = (values, key) => new Set(values.map(key)).size === values.length;
const bounded = (value, maximum) => typeof value === 'string' && value.length <= maximum;
export function coreViewSemantics(view) {
  if (!view || typeof view !== 'object' || !Array.isArray(view.items) || !Array.isArray(view.actions)) return false;
  if (!bounded(view.id, 240) || !view.id.trim() || typeof view.extensionId !== 'string' || !view.extensionId.trim()) return false;
  for (const [collection, maximum] of [[view.items, 1024], [view.actions, 128], [view.columns ?? [], 80], [view.fields ?? [], 80], [view.indicators ?? [], 80]]) {
    if (!Array.isArray(collection) || collection.some(item => !item || !bounded(item.id, maximum) || !item.id.trim())) return false;
    if (collection !== view.actions && !unique(collection, item => item.id)) return false;
  }
  if (!unique(view.actions, item => JSON.stringify([item.id, item.itemId ?? null]))) return false;
  if (view.itemOrder && !unique(view.itemOrder, id => id)) return false;
  if ((view.itemOrder ?? []).some(id => !bounded(id, 1024) || !id.trim())) return false;
  if (view.items.some(item => !bounded(item.label, 2048)) || view.actions.some(item => !bounded(item.label, 2048))) return false;
  if ([...(view.columns ?? []), ...(view.fields ?? []), ...(view.indicators ?? [])].some(item => !bounded(item.label, 240))) return false;
  for (const field of view.fields ?? []) {
    const value = field.value;
    if ((field.options ?? []).some(option => !bounded(option, 240) || controls.test(option))) return false;
    if (field.kind === 'choice' && (!field.options?.length || value != null && !field.options.includes(value))) return false;
    if (field.kind === 'text' && value != null && (!bounded(value, 4096) || controls.test(value))) return false;
    if (field.kind === 'number' && value != null && (typeof value !== 'number' || !Number.isFinite(value) || Math.abs(value) > 79228162514264337593543950335)) return false;
    if (field.kind === 'boolean' && value != null && typeof value !== 'boolean') return false;
  }
  for (const action of view.actions) {
    if (!['workspace', 'user', 'session'].includes(action.configurationScope ?? 'workspace')) return false;
    if (action.configurationKey != null && (!bounded(action.configurationKey, 80) || !/^[A-Za-z0-9.-]+$/.test(action.configurationKey)
        || action.requiresUserAuthority !== true || view.kind !== 'form' || action.itemId != null)) return false;
  }
  return true;
}
const validator = new Ajv2020({ strict: false, allErrors: true });
validator.addKeyword({ keyword: 'x-coreView', schemaType: 'boolean', validate: (enabled, data) => !enabled || coreViewSemantics(data) });
validator.addSchema(schema);
export const validateView = validator.compile({ $ref: schema.$id + '#/$defs/view' });
