// Evidence preparation only: decode the actual Sixel strings produced by ImageView,
// then compose those pixels with the real DOTNET driver's cell capture. No runtime dependency.
const fs=require('node:fs'),path=require('node:path');
const dir=path.resolve(process.argv[2]),sharp=require(process.argv[3]||'sharp');
function decode(sixel, width, height) {
 const raster=/"\d+;\d+;(\d+);(\d+)/.exec(sixel);
 if(raster){width=+raster[1];height=+raster[2];}
 const rgb=Buffer.alloc(width*height*3), palette=[];
 let x=0,y=0,color=0,i=sixel.indexOf('q')+1;
 const number=()=>{let s=i;while(/\d/.test(sixel[i]||'x'))i++;return +(sixel.slice(s,i)||0);};
 const pixel=(code,count=1)=>{for(let n=0;n<count;n++,x++)for(let bit=0;bit<6;bit++){
   if(!(code&(1<<bit))||x>=width||y+bit>=height)continue;
   const c=palette[color]||[0,0,0],at=((y+bit)*width+x)*3;rgb[at]=c[0];rgb[at+1]=c[1];rgb[at+2]=c[2];
 }};
 while(i<sixel.length&&sixel[i]!=='\x1b') {
  const ch=sixel[i++];
  if(ch==='#'){color=number();if(sixel[i]===';'){const args=[];while(sixel[i]===';'){i++;args.push(number());}
    if(args[0]!==2)throw Error('Unexpected Sixel palette format');palette[color]=args.slice(1,4).map(c=>Math.round(c*255/100));}}
  else if(ch==='!'){const count=number();pixel(sixel.charCodeAt(i++)-63,count);}
  else if(ch==='"'){number();while(sixel[i]===';'){i++;number();}}
  else if(ch==='$')x=0;
  else if(ch==='-'){x=0;y+=6;}
  else if(ch>='?'&&ch<='~')pixel(ch.charCodeAt(0)-63);
 }
 return {rgb,width,height};
}
async function save(target,bytes){const temp=target+'.render-'+process.pid+'.tmp';try{fs.writeFileSync(temp,bytes);fs.renameSync(temp,target);}finally{if(fs.existsSync(temp))fs.unlinkSync(temp);}}
(async()=>{
 for(const name of fs.readdirSync(dir).filter(n=>n.endsWith('.raster.json'))) {
  const scene=name.replace('.raster.json',''),info=JSON.parse(fs.readFileSync(path.join(dir,name),'utf8'));
  const frame=decode(fs.readFileSync(path.join(dir,scene+'.sixel'),'utf8'),info.Width,info.Height);
  await save(path.join(dir,scene+'.png'),await sharp(frame.rgb,{raw:{width:frame.width,height:frame.height,channels:3}}).png().toBuffer());
  const svg=path.join(dir,'settings-companion-raster-'+scene.replace(/^companion-/,'')+'-'+info.Columns+'x'+info.Rows+'.svg');
  if(fs.existsSync(svg)){
   const image=await sharp(frame.rgb,{raw:{width:frame.width,height:frame.height,channels:3}})
    .resize(Math.round(frame.width*12/info.CellWidth),Math.round(frame.height*24/info.CellHeight),{kernel:'nearest'}).png().toBuffer();
   await save(path.join(dir,scene+'-terminal.png'),await sharp(svg).composite([{input:image,left:info.X*12,top:info.Y*24}]).png().toBuffer());
  }
 }
 const expressions=['idle','working','waiting','success','failure','pet'];
 const panels=await Promise.all(expressions.map(async(expression,index)=>({input:await sharp(path.join(dir,'companion-ox-'+expression+'.png'))
  .resize(256,256,{kernel:'nearest'}).png().toBuffer(),left:(index%3)*256,top:Math.floor(index/3)*256})));
 await save(path.join(dir,'ox-six-states.png'),await sharp({create:{width:768,height:512,channels:3,background:'#202c3b'}}).composite(panels).png().toBuffer());
 console.log('Native Sixel frames decoded and driver captures composed.');
})().catch(error=>{console.error(error);process.exitCode=1;});
