// Preview only: replay ImageView's captured Sixel in the actual xterm.js emulator.
// node tools/preview-companion-designs.cjs <xterm-node_modules> <playwright> <sharp> <evidence-dir>
const fs=require('node:fs'),path=require('node:path');
const modules=path.resolve(process.argv[2]),playwright=require(process.argv[3]),sharp=require(process.argv[4]),out=path.resolve(process.argv[5]);
(async()=>{
 const browser=await playwright.chromium.launch({channel:'msedge',headless:true});
 try{
  const page=await browser.newPage({viewport:{width:560,height:280},deviceScaleFactor:1});
  const errors=[];page.on('pageerror',e=>errors.push(e.message));
  await page.setContent('<body style="margin:0;padding:12px;background:#202c3b"><div id="terminal"></div></body>');
  await page.addStyleTag({path:path.join(modules,'@xterm/xterm/css/xterm.css')});
  await page.addScriptTag({path:path.join(modules,'@xterm/xterm/lib/xterm.js')});
  await page.addScriptTag({path:path.join(modules,'@xterm/addon-image/lib/addon-image.js')});
  await page.evaluate(()=>{
   window.terminal=new Terminal({cols:54,rows:13,fontSize:14,fontFamily:'Consolas, monospace',allowProposedApi:true,
    theme:{background:'#202c3b',foreground:'#e1e6e9'}});
   window.images=new ImageAddon.ImageAddon({storageLimit:4,sixelSupport:true,iipSupport:false});
   terminal.open(document.getElementById('terminal'));terminal.loadAddon(images);
  });
  const write=data=>page.evaluate(s=>new Promise(resolve=>terminal.write(s,resolve)),data);
  for(const [id,label] of [['claude-crab','Cangrejo inspirado en Claude'],['codex-robot','Robot inspirado en Codex']]){
   const captures=[];
   for(const [state,title] of [['idle','Reposo'],['working','Trabajo'],['waiting','Espera'],['success','Celebracion'],['failure','Fallo'],['pet','Caricia']]){
    await write('\x1bc\x1b[1;1H'+label+' | '+title+'\x1b[3;3H'+fs.readFileSync(path.join(out,'companion-'+id+'-'+state+'.sixel'),'utf8'));
    await page.waitForTimeout(100);
    if(!(await page.evaluate(()=>images.storageUsage>0)))throw Error('No rendered image: '+id+'/'+state);
    const target=path.join(out,'xterm-'+id+'-'+state+'.png');
    await page.locator('#terminal').screenshot({path:target});captures.push(target);
   }
   const frames=await Promise.all(captures.map(file=>sharp(file).ensureAlpha().raw().toBuffer({resolveWithObject:true})));
   const {width,height}=frames[0].info;
   await sharp(Buffer.concat(frames.map(f=>f.data)),{raw:{width,height:height*frames.length,channels:4,pageHeight:height}})
    .gif({loop:0,delay:frames.map(()=>1000)}).toFile(path.join(out,id+'-states.gif'));
  }
  await write('\x1b[2J\x1b[H');await page.waitForTimeout(100);
  if(await page.evaluate(()=>images.storageUsage!==0))throw Error('Images retained after clear');
  if(errors.length)throw Error(errors.join('\n'));
  fs.writeFileSync(path.join(out,'design-preview-validation.json'),JSON.stringify({terminal:'xterm.js 5.5.0',addon:'@xterm/addon-image 0.8.0',
   browser:await browser.version(),designs:2,statesPerDesign:6,source:'Replay of actual Terminal.Gui ImageView Sixel output; not a full live OmniCore session',errors},null,2));
  console.log('Two designs rendered in xterm.js, six states each; clean erase verified.');
 }finally{await browser.close();}
})().catch(error=>{console.error(error);process.exitCode=1;});
