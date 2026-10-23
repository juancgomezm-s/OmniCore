// Validation only. Uses pinned xterm.js 5.5.0 + addon-image 0.8.0 installed in a scratch directory.
// Usage: node tools/verify-companion-xterm.cjs <xterm-node_modules> <playwright-module> <evidence-dir>
const fs=require('node:fs'),path=require('node:path');
const modules=path.resolve(process.argv[2]),playwright=require(process.argv[3]),out=path.resolve(process.argv[4]);
(async()=>{
 const browser=await playwright.chromium.launch({channel:'msedge',headless:true});
 try{
  const page=await browser.newPage({viewport:{width:1024,height:720},deviceScaleFactor:1});
  const errors=[];page.on('pageerror',error=>errors.push(error.message));
  await page.setContent('<html><head></head><body style="margin:0;padding:12px;background:#202c3b"><div id="terminal"></div></body></html>');
  await page.addStyleTag({path:path.join(modules,'@xterm/xterm/css/xterm.css')});
  await page.addScriptTag({path:path.join(modules,'@xterm/xterm/lib/xterm.js')});
  await page.addScriptTag({path:path.join(modules,'@xterm/addon-image/lib/addon-image.js')});
  await page.evaluate(()=>{
   window.terminal=new Terminal({cols:80,rows:25,fontSize:14,fontFamily:'Consolas, monospace',allowProposedApi:true,
    theme:{background:'#202c3b',foreground:'#e1e6e9'}});
   window.images=new ImageAddon.ImageAddon({storageLimit:4,sixelSupport:true,iipSupport:false});
   terminal.open(document.getElementById('terminal'));terminal.loadAddon(images);
   window.replies=[];terminal.onData(data=>replies.push(data));
  });
  const write=sequence=>page.evaluate(data=>new Promise(resolve=>terminal.write(data,resolve)),sequence);
  await write('\x1b[c\x1b[16t');
  const replies=await page.evaluate(()=>replies.slice());
  if(!replies.some(reply=>/\x1b\[\?[^c]*;4;/.test(reply)))throw Error('Real xterm terminal did not advertise Sixel');
  for(const [scene,label] of [['ox-idle','Reposo'],['ox-working','Trabajo'],['ox-waiting','Espera'],['ox-success','Celebracion'],['ox-failure','Fallo'],['ox-pet','Caricia']]){
   await write('\x1bc\x1b[1;1HOmniCore | Buey | '+label+'\x1b[3;3H'+fs.readFileSync(path.join(out,'companion-'+scene+'.sixel'),'utf8'));
   await page.waitForTimeout(120);
   await page.locator('#terminal').screenshot({path:path.join(out,'xterm-'+scene+'.png')});
   const used=await page.evaluate(()=>images.storageUsage);
   if(!(used>0))throw Error('Sixel was not retained by real emulator: '+scene);
  }
  await page.evaluate(()=>{terminal.options.fontSize=20;terminal.resize(60,20);});
  await write('\x1bc\x1b[1;1HOmniCore | Escala de fuente 20\x1b[3;3H'+fs.readFileSync(path.join(out,'companion-ox-idle.sixel'),'utf8'));
  await page.waitForTimeout(120);await page.locator('#terminal').screenshot({path:path.join(out,'xterm-ox-font20.png')});
  await write('\x1b[2J\x1b[H');await page.waitForTimeout(120);
  const afterClear=await page.evaluate(()=>images.storageUsage);
  if(afterClear!==0)throw Error('Terminal retained image storage after erase');
  await page.locator('#terminal').screenshot({path:path.join(out,'xterm-cleared.png')});
  if(errors.length)throw Error(errors.join('\n'));
  fs.writeFileSync(path.join(out,'xterm-validation.json'),JSON.stringify({terminal:'xterm.js',version:'5.5.0',addon:'@xterm/addon-image 0.8.0',
    browser:await browser.version(),primaryDAAndCellReplies:replies,frames:6,fontSizes:[14,20],afterClearStorageMiB:afterClear,errors},null,2));
  console.log('Actual xterm.js Sixel rendering, capability replies, font-size change and clear verified.');
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
