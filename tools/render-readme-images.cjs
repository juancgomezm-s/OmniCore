// Render the editable, explicitly conceptual README illustrations. No network or provider calls.
const path = require('node:path');
const sharp = require(process.argv[2] || 'sharp');
const directory = path.resolve(__dirname, '../docs/images');
async function render() {
  for (const name of ['product-vision', 'runtime-map', 'roadmap']) {
    const destination = path.join(directory, `${name}.png`);
    await sharp(path.join(directory, `${name}.svg`)).png().toFile(destination);
    const metadata = await sharp(destination).metadata();
    console.log(`${name}: ${metadata.width}x${metadata.height}`);
  }
}
render().catch(error => { console.error(error); process.exitCode = 1; });
