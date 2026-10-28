// Preparation only. Runtime consumes embedded PNGs; sharp is not a runtime dependency.
// Original SVG artwork, drawn for OmniCore. Run: node tools/prepare-companion-assets.cjs <absolute sharp module>
const fs = require('node:fs'), path = require('node:path');
const sharp = require(process.argv[2]);
const output = path.resolve('src/OmniCore.Cli/Assets/Companions');
fs.mkdirSync(output, { recursive: true });
const palettes = { ox: ['#ad7650','#e3b47e'], fox: ['#cf622c','#ffb65b'], owl: ['#80658c','#c0a5c7'],
  beaver: ['#80573f','#bb8a5f'], cat: ['#627f9a','#b5cad4'], capybara: ['#947854','#d5b17b'], egg: ['#dccfa9','#fff1ce'],
  'claude-crab': ['#bc624b','#e99975'], 'codex-robot': ['#8196a4','#e8efeb'] };
const ellipse = (x,y,rx,ry,fill,extra='') => `<ellipse cx="${x}" cy="${y}" rx="${rx}" ry="${ry}" fill="${fill}" ${extra}/>`;
function svg(species, frame) {
 const [dark,light] = palettes[species], pose = frame>=13 ? frame===17 ? 6 : 0 : Math.floor(frame/2), phase = frame>=13 ? 0 : frame%2;
 const sway=frame===13 ? -1 : frame===14 ? 1 : 0;
 const gaze=frame===15 ? -1.8 : frame===16 ? 1.8 : 0;
 const closed = frame===1 || pose===6 || (pose===4&&phase===1);
 const lift = pose===3&&phase ? -5 : pose===1&&phase ? -1 : 0;
 const eye = (x,y) => {x+=gaze;return closed ? `<path d="M${x-4} ${y}q4 4 8 0" fill="none" stroke="#292330" stroke-width="2.5" stroke-linecap="round"/>`
  : ellipse(x,y,3.4,4.8,'#292330')+ellipse(x-1,y-1.6,1.1,1.5,'#fff8e9');};
 let body = ellipse(64,92,27,22,'url(#fur)')+ellipse(64,94,17,17,light)
  +ellipse(47,109,10,6,dark)+ellipse(81,109,10,6,dark);
 let ears = ellipse(38,48,12,8,dark,'transform="rotate(30 38 48)"')+ellipse(90,48,12,8,dark,'transform="rotate(-30 90 48)"');
 let head = ellipse(64,61,29,27,'url(#fur)');
 let muzzle = ellipse(64,73,18,11,light)+eye(52,59)+eye(76,59)
  +`<path d="M60 73q4 4 8 0m-4 2v4" fill="none" stroke="#60453f" stroke-width="1.8" stroke-linecap="round"/>`;
 let details = '';
 if(species==='ox') {
  ears += `<path d="M42 44C22 45 21 26 28 21c-1 13 10 12 18 11M86 44c20 1 21-18 14-23 1 13-10 12-18 11" fill="#f7dfaf" stroke="#806044" stroke-width="2"/>`;
  muzzle = ellipse(64,75,22,13,'#edc5a0')+eye(51,57)+eye(77,57)+ellipse(54,75,3,2,'#79534a')+ellipse(74,75,3,2,'#79534a')
   +`<path d="M57 82q7 5 14 0M52 39q9-13 14-3q9-9 13 1" fill="none" stroke="#734c38" stroke-width="2" stroke-linecap="round"/>`;
  details += `<path d="M40 109h15m18 0h15" stroke="#553f3b" stroke-width="5" stroke-linecap="round"/>`;
 } else if(species==='fox'||species==='cat') {
  ears = `<path d="M37 49L35 18 56 36M72 36l22-18-3 32" fill="${dark}" stroke="${light}" stroke-width="2"/><path d="M40 39l-1-13 10 12M80 38l10-12-2 14" fill="#eac1b5"/>`;
  if(species==='fox') body = `<g transform="rotate(${sway*6} 86 98)"><path d="M85 99q33 0 23-32-5 16-22 14" fill="${dark}"/><path d="M108 67q8 16 0 24l-7-7z" fill="#fff0d5"/></g>`+body;
  else details+=`<path d="M39 87Q${8+sway*4} ${102+sway*3} ${24+sway*4} ${74+sway*3}m${24-sway*4} ${-30-sway*3} 5 5m11-9v6m13-2-4 5M39 70l-13-3m13 8-13 2m63-7 13-3m-13 8 13 2" fill="none" stroke="${dark}" stroke-width="3" stroke-linecap="round"/>`;
  muzzle = `<path d="M39 63q9 25 25 23 16 2 25-23L64 73z" fill="#fff0d5"/>`+eye(51,57)+eye(77,57)
   +`<path d="M59 70l5 5 5-5z" fill="#493d43"/><path d="M64 75v4m-6 0q6 5 12 0" fill="none" stroke="#795959" stroke-width="1.5"/>`;
 } else if(species==='owl') {
  ears=`<path d="M38 48L35 25l21 11m16 0 21-11-3 25" fill="${dark}"/>`;
  muzzle=ellipse(51,59,15,18,light)+ellipse(77,59,15,18,light)+eye(51,59)+eye(77,59)+`<path d="M59 72l5 9 5-9z" fill="#f2c36d"/>`;
  details=`<path d="M39 85q-8 18 8 19M89 85q8 18-8 19m-25-13 4 4m8-4 4 4m-12 5 4 4" fill="none" stroke="${dark}" stroke-width="3" stroke-linecap="round"/>`;
 } else if(species==='beaver') {
  body=`<g transform="rotate(${sway*5} 90 99)">`+ellipse(90,99,14,21,'#624a41','transform="rotate(-35 90 99)"')+`<path d="M87 85l14 16m-19-8 14 16m-16-9 12 14" stroke="#8f6c53" stroke-width="1.5"/></g>`+body;
  ears=ellipse(41,36,10,12,dark)+ellipse(87,36,10,12,dark)+ellipse(41,36,5,7,'#d5a488')+ellipse(87,36,5,7,'#d5a488');
  muzzle+=`<rect x="58" y="79" width="12" height="10" rx="2" fill="#fff5d9"/><path d="M64 79v10" stroke="#ad9480"/>`;
 } else if(species==='capybara') {
  ears=ellipse(41,33,9,9,dark)+ellipse(87,33,9,9,dark)+ellipse(41,33,4,4,light)+ellipse(87,33,4,4,light);
  head=`<rect x="33" y="38" width="62" height="48" rx="22" fill="url(#fur)"/>`;
  muzzle=ellipse(64,73,25,14,light)+eye(46,55)+eye(82,55)+ellipse(55,71,2.5,2,'#6c5544')+ellipse(73,71,2.5,2,'#6c5544')+`<path d="M56 80h16" stroke="#6c5544" stroke-width="1.5" stroke-linecap="round"/>`;
 }
 if(species==='claude-crab') {
  const wave=sway ? sway*5 : phase ? -5 : 0;
  body=`<g fill="none" stroke="${dark}" stroke-width="6" stroke-linecap="square"><path d="M42 88l-14 8-7 12m26-12-8 10v7m47-25 14 8 7 12m-26-12 8 10v7"/></g>`;
  ears=`<path d="M39 66L24 ${59+wave} 21 ${45+wave}m68 21 15 ${-7+wave} 3 -14" fill="none" stroke="${dark}" stroke-width="7"/>
   <path d="M22 ${52+wave}L11 ${42+wave}v-13l9 9 7-13 6 14-4 13zM106 ${52+wave}l11-10V29l-9 9-7-13-6 14 4 13z" fill="url(#fur)" stroke="${dark}" stroke-width="2"/>`;
  head=`<path d="M39 49h10v-6h30v6h10v10h7v25h-7v10H39V84h-7V59h7z" fill="url(#fur)"/>`;
  muzzle=(closed ? `<path d="M47 65h9m16 0h9" stroke="#292330" stroke-width="3"/>`
    : `<path d="M48 58h8v13h-8zm24 0h8v13h-8z" fill="#292330"/><path d="M49 59h2v3h-2zm24 0h2v3h-2z" fill="#fff1df"/>`)
   +`<path d="M58 79v3h12v-3" fill="none" stroke="#773f38" stroke-width="2"/>`;
  details=`<path d="M39 54h8m-9 28v5h7m38-33h5" fill="none" stroke="#f8bd97" stroke-width="2"/>`;
  if(gaze)muzzle=`<g transform="translate(${gaze} 0)">${muzzle}</g>`;
 }
 if(species==='codex-robot') {
  const hand=sway ? sway*4 : phase ? -4 : 0;
  body=`<rect x="43" y="84" width="42" height="25" rx="8" fill="url(#fur)" stroke="#627887" stroke-width="2"/>
   <path d="M41 89l-8 ${10+hand}m54-10 8 ${10-hand}" stroke="#a9bdc4" stroke-width="8" stroke-linecap="round"/>
   <rect x="46" y="108" width="13" height="8" rx="3" fill="#94a9b2"/><rect x="69" y="108" width="13" height="8" rx="3" fill="#94a9b2"/>
   <path d="M60 94l-5 4 5 4m8-8 5 4-5 4" fill="none" stroke="#447e79" stroke-width="2" stroke-linecap="round"/>`;
  ears=`<path d="M64 29V19" stroke="#93aaa9" stroke-width="3"/>${ellipse(64,17,4,4,sway<0?'#e0fff0':'#9de7c9')}
   <rect x="27" y="46" width="9" height="22" rx="4" fill="#93a9b6"/><rect x="92" y="46" width="9" height="22" rx="4" fill="#93a9b6"/>`;
  head=`<rect x="32" y="29" width="64" height="55" rx="16" fill="url(#fur)" stroke="#7f98a5" stroke-width="2"/>
   <rect x="39" y="41" width="50" height="29" rx="9" fill="#172b37" stroke="#708d97" stroke-width="1.5"/>`;
  muzzle=closed ? `<path d="M47 57q5 4 10 0m14 0q5 4 10 0" fill="none" stroke="#a6f3d7" stroke-width="2.5" stroke-linecap="round"/>`
   : `<rect x="48" y="49" width="8" height="14" rx="3" fill="#a6f3d7"/><rect x="72" y="49" width="8" height="14" rx="3" fill="#a6f3d7"/>`;
  details=`<path d="M58 76h12" stroke="#6d9194" stroke-width="2" stroke-linecap="round"/><path d="M41 35h17" stroke="#f6fbf7" stroke-width="2" stroke-linecap="round"/>`;
  if(gaze)muzzle=`<g transform="translate(${gaze} 0)">${muzzle}</g>`;
 }
 if(species==='egg') {
  ears=''; body=''; head=`<path d="M64 26C43 26 28 66 31 85c4 29 62 29 66 0 3-19-12-59-33-59Z" fill="url(#fur)" stroke="#d4c7a2" stroke-width="2"/>`;
  muzzle=eye(52,72)+eye(76,72)+`<path d="M59 85q5 4 10 0" stroke="#866e5a" fill="none" stroke-width="2"/>`;
  details=ellipse(48,50,4,6,'#fff8e3')+ellipse(81,93,5,3,'#d5c697');
 }
 let props='';
 if(pose===1) props=`<path d="M39 91h50l-5 24H44z" fill="#405e70" stroke="#9ab7c2" stroke-width="1.5"/><rect x="38" y="112" width="52" height="5" rx="2" fill="#9ab7c2"/><path d="M59 101l-4 3 4 3m10-6 4 3-4 3" stroke="#a8ead9" fill="none" stroke-width="1.6"/>`;
 if(pose===2) props=ellipse(99,87,12,12,'#f4dfae')+`<path d="M99 79v8l${phase?6:3} 4" stroke="#806547" stroke-width="2" fill="none" stroke-linecap="round"/>`;
 if(pose===3) props=`<path d="M24 33l2 6 6 2-6 2-2 6-2-6-6-2 6-2zM102 22l2 6 6 2-6 2-2 6-2-6-6-2 6-2z" fill="#ffdb78"/><path d="M18 62l6-3m78 1 7 4" stroke="#a4dfc9" stroke-width="3" stroke-linecap="round"/>`;
 if(pose===4) props=`<path d="M91 22q-9-10-15 0-11-2-10 9h39q4-12-8-12z" fill="#9aabc1"/><path d="M76 36l-3 6m13-6-3 6m13-6-3 6" stroke="#7bbcdf" stroke-width="2.5" stroke-linecap="round"/>`;
 if(pose===5) props=`<path d="M98 ${phase?24:29}c-13-14-25 6 0 20 25-14 13-34 0-20z" fill="#f093a7" stroke="#ffd1cc" stroke-width="1.5"/>`;
 if(pose===6) props=`<path d="M93 31h9l-9 10h9m1-23h6l-6 7h6" stroke="#c0cfdf" fill="none" stroke-width="2" stroke-linecap="round"/>`;
 const motion=frame===13 ? 'translate(64 112) scale(1.015 1.025) translate(-64 -112)'
  : frame===14 ? 'rotate(-3 64 105)' : frame===17 ? 'translate(64 112) scale(1.01 1.025) translate(-64 -112)' : '';
 return `<svg xmlns="http://www.w3.org/2000/svg" width="128" height="128" viewBox="0 0 128 128"><defs><linearGradient id="fur" x1="0" x2=".8" y2="1"><stop stop-color="${light}"/><stop offset="1" stop-color="${dark}"/></linearGradient></defs><rect width="128" height="128" fill="#202c3b"/>${ellipse(64,118,38,5,'#14202e')}<g transform="translate(0 ${lift}) ${motion}">${body}${ears}${head}${muzzle}${details}</g>${props}</svg>`;
}
(async()=>{
 const selected=process.argv.slice(3);
 for(const species of Object.keys(palettes).filter(id=>!selected.length||selected.includes(id))) {
  const frames=await Promise.all(Array.from({length:18},(_,frame)=>sharp(Buffer.from(svg(species,frame))).png().toBuffer()));
  await sharp({create:{width:128*18,height:128,channels:4,background:'#202c3b'}})
   .composite(frames.map((input,i)=>({input,left:i*128,top:0}))).png({palette:true,colours:64}).toFile(path.join(output,species+'.png'));
 }
 console.log('Original 128px sprite atlases prepared.');
})().catch(error=>{console.error(error);process.exitCode=1;});
