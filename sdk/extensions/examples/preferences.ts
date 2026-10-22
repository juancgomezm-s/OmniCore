import { Extension, type JsonObject } from '../src/index.js';

const extension = new Extension();
extension.view(async request => {
  if (request.viewId === 'indicators') return {
    id: extension.id + '.indicators', extensionId: extension.id, kind: 'indicator', revision: 0,
    state: 'ready', reset: true, items: [], actions: [], indicators: [
      { id: 'configuration', label: 'extension.indicator.configuration', state: 'ready' },
      { id: 'quota', label: 'extension.indicator.quota', state: 'unknown', value: null },
    ],
  };
  const saved = await extension.config.get('display') as JsonObject | null;
  return {
    id: extension.id + '.preferences', extensionId: extension.id, kind: 'form', revision: 0,
    state: 'ready', reset: true, items: [],
    fields: [
      { id: 'title', label: 'extension.form.title', kind: 'text', value: saved?.title ?? 'OmniCore', required: true, readOnly: false },
      { id: 'interval', label: 'extension.form.interval', kind: 'number', value: saved?.interval ?? 2.5, required: true, readOnly: false },
      { id: 'enabled', label: 'extension.form.auto_refresh', kind: 'boolean', value: saved?.enabled ?? true, required: false, readOnly: false },
      { id: 'detail', label: 'extension.form.detail', kind: 'choice', value: saved?.detail ?? 'normal', options: ['compact', 'normal', 'wide'], required: true, readOnly: false },
      { id: 'api', label: 'extension.form.api', kind: 'text', value: '1.0', required: true, readOnly: true },
    ],
    actions: [{ id: 'save', label: 'extension.form.save', requiresUserAuthority: true, configurationKey: 'display', configurationScope: 'workspace' }],
  };
});
extension.listen();
