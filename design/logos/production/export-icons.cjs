// Export approved raster masters into Windows delivery formats.
const fs=require('node:fs/promises'),path=require('node:path');
const sharp=require(process.env.SHARP_MODULE || 'sharp');
const root=__dirname;
const sizes=[16,20,24,30,32,36,40,48,60,64,72,80,96,128,256];
const icoSizes=[16,20,24,32,40,48,64,128,256];
async function render(n){const inner=Math.round(n*.86),left=Math.floor((n-inner)/2),right=n-inner-left;return sharp(path.join(root,n<=48?'small.png':'master.png')).resize(inner,inner,{kernel:'lanczos3'}).extend({top:left,bottom:right,left,right,background:{r:0,g:0,b:0,alpha:0}}).png().toBuffer();}
async function main(){
await fs.mkdir(path.join(root,'png'),{recursive:true});await fs.mkdir(path.join(root,'msix'),{recursive:true});
const frames=new Map(),checks=[];
for(const n of sizes){const b=await render(n);frames.set(n,b);await fs.writeFile(path.join(root,'png',`icon-${n}.png`),b);for(const suffix of ['','_altform-unplated','_altform-lightunplated'])await fs.writeFile(path.join(root,'msix',`AppList.targetsize-${n}${suffix}.png`),b);}
await fs.writeFile(path.join(root,'icon-master-1024.png'),await render(1024));
for(const [base,size] of [['AppList',44],['Square150x150Logo',150],['StoreLogo',50]]){await fs.writeFile(path.join(root,'msix',base+'.png'),await render(size));for(const scale of [100,125,150,200,250,300,400])await fs.writeFile(path.join(root,'msix',`${base}.scale-${scale}.png`),await render(Math.round(size*scale/100)));}
const header=Buffer.alloc(6+16*icoSizes.length);header.writeUInt16LE(1,2);header.writeUInt16LE(icoSizes.length,4);let offset=header.length;
for(let i=0;i<icoSizes.length;i++){const n=icoSizes[i],b=frames.get(n),p=6+i*16;header[p]=n===256?0:n;header[p+1]=n===256?0:n;header.writeUInt16LE(1,p+4);header.writeUInt16LE(32,p+6);header.writeUInt32LE(b.length,p+8);header.writeUInt32LE(offset,p+12);offset+=b.length;}
await fs.writeFile(path.join(root,'HunterOPS-PDF.ico'),Buffer.concat([header,...icoSizes.map(n=>frames.get(n))]));
const overlays=[];const sample=[16,24,32,48,64,128,256],width=960,height=420;
for(let row=0;row<2;row++){const bg=row?'#171928':'#F5F6FA',fg=row?'#FFFFFF':'#171928';overlays.push({input:Buffer.from(`<svg width="960" height="210"><rect width="960" height="210" fill="${bg}"/><text x="16" y="25" font-family="Segoe UI" font-size="16" fill="${fg}">${row?'Dark':'Light'} · actual pixel sizes (256 shown at 160)</text></svg>`),left:0,top:row*210});for(let i=0;i<sample.length;i++){const n=sample[i],s=Math.min(n,160),x=24+i*128;overlays.push({input:await sharp(frames.get(n)).resize(s).toBuffer(),left:x,top:row*210+45});overlays.push({input:Buffer.from(`<svg width="100" height="24"><text x="0" y="18" font-family="Segoe UI" font-size="14" fill="${fg}">${n}px</text></svg>`),left:x,top:row*210+183});}}
await sharp({create:{width,height,channels:4,background:'#F5F6FA'}}).composite(overlays).png().toFile(path.join(root,'size-preview.png'));
for(const dir of ['png','msix'])for(const file of await fs.readdir(path.join(root,dir))){const filePath=path.join(root,dir,file),m=await sharp(filePath).metadata(),{data,info}=await sharp(filePath).ensureAlpha().raw().toBuffer({resolveWithObject:true});if(m.width!==m.height||!m.hasAlpha||data[3]!==0)throw Error('Invalid icon '+file);checks.push({file:dir+'/'+file,width:m.width,height:m.height,alpha:true,transparentCorner:true});}
const ico=await fs.readFile(path.join(root,'HunterOPS-PDF.ico'));if(ico.readUInt16LE(2)!==1||ico.readUInt16LE(4)!==icoSizes.length)throw Error('ICO header');
for(let i=0;i<icoSizes.length;i++){const p=6+i*16,length=ico.readUInt32LE(p+8),start=ico.readUInt32LE(p+12),meta=await sharp(ico.subarray(start,start+length)).metadata();if(meta.width!==icoSizes[i]||meta.height!==icoSizes[i])throw Error('ICO frame');}
await fs.writeFile(path.join(root,'verification.json'),JSON.stringify({date:'2026-10-12',icoFrames:icoSizes,pngChecks:checks,smallMasterUpTo:48,contentFraction:.86},null,2));console.log(JSON.stringify({icoFrames:icoSizes,pngFiles:checks.length,verified:true}));
}
main().catch(e=>{console.error(e);process.exitCode=1;});
