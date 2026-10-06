// Rasterize SVG snapshots exported by the real Terminal.Gui driver fixture.
// Usage: node tools/render-tui-frames.cjs <snapshot-directory> [sharp-module-path]
// No desktop capture, invented content, palette substitution, or provider calls.
const fs = require('node:fs');
const path = require('node:path');
const directory = process.argv[2];
if (!directory) throw new Error('A snapshot directory is required.');
const sharp = require(process.argv[3] || 'sharp');
async function render() {
  const files = fs.readdirSync(directory).filter(name => /^(conversation|sidebar|notice|settings|account|login|models|picker|commands|activity|table)-\d+x\d+\.svg$/.test(name));
  if (!files.length) throw new Error('No real driver snapshots found.');
  for (const name of files) {
    const destination = path.join(directory, name.replace(/\.svg$/, '.png'));
    await sharp(path.join(directory, name)).png().toFile(destination);
    console.log(destination);
  }
}
render().catch(error => { console.error(error); process.exitCode = 1; });
