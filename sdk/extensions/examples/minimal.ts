import { Extension } from '../src/index.js';
const extension = new Extension();
extension.tool('hello', async ({ name }) => ({ text: `Hola, ${String(name ?? 'mundo')}` }));
extension.view(async () => ({ id: extension.id + '.hello', extensionId: extension.id, kind: 'details',
  revision: 1, state: 'ready', reset: true, actions: [], items: [
    { id: 'greeting', label: 'Hola desde una extensión', hasChildren: false },
    { id: 'git', label: 'Git', hasChildren: false, data: await extension.git.status() }
  ] }));
extension.listen();
