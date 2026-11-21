// Original articulated character for a first-run preview and reproducible rig tests.
// Run from the repository root with: node scripts/make-pilot.mjs
import { writeFileSync } from 'node:fs';
const joints = [], vertices = [], materials = [], morphs = new Map();
const add = (a,b) => a.map((v,i)=>v+b[i]);
const sub = (a,b) => a.map((v,i)=>v-b[i]);
const mul = (a,k) => a.map(v=>v*k);
const unit = a => mul(a,1/(Math.hypot(...a)||1));
const cross = (a,b) => [a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];
function joint(name,en,pos,parent=-1){joints.push({name,en,pos,parent}); return joints.length-1;}
function surface(name,color,alpha=1){materials.push({name,color:color.map(v=>v/255),alpha,indices:[]});return materials.length-1;}
const dark=surface('Graphite',[44,52,74]), pearl=surface('Ceramic',[229,222,212]), teal=surface('Lagoon',[55,185,183]), gold=surface('Brass',[243,172,93]), eye=surface('Eye light',[145,247,224]), mouth=surface('Coral',[245,137,127]);
function sphere(center,radii,bone,mat,u=20,v=14){
  const start=vertices.length;
  for(let j=0;j<=v;j++)for(let i=0;i<=u;i++){
    const a=i/u*Math.PI*2,b=j/v*Math.PI;const n=[Math.cos(a)*Math.sin(b),Math.cos(b),Math.sin(a)*Math.sin(b)];
    vertices.push({p:add(center,n.map((x,k)=>x*radii[k])),n:unit(n.map((x,k)=>x/radii[k])),uv:[i/u,j/v],bone});
    if(i<u&&j<v){const x=start+j*(u+1)+i;materials[mat].indices.push(x,x+1,x+u+1,x+1,x+u+2,x+u+1);}
  }
  return [start,vertices.length];
}
function tube(a,b,r1,r2,bone,mat,sides=12){
  const dir=unit(sub(b,a)),p=unit(cross(dir,Math.abs(dir[1])>.8?[1,0,0]:[0,1,0])),q=cross(dir,p);const start=vertices.length;
  for(let j=0;j<=1;j++)for(let i=0;i<=sides;i++){
    const t=i/sides*2*Math.PI,n=add(mul(p,Math.cos(t)),mul(q,Math.sin(t)));
    vertices.push({p:add(j?b:a,mul(n,j?r2:r1)),n,uv:[i/sides,j],bone});
    if(!j&&i<sides){const x=start+i;materials[mat].indices.push(x,x+1,x+sides+1,x+1,x+sides+2,x+sides+1);}
  }
}
function morph(name,range,fn){const list=morphs.get(name)||[];for(let i=range[0];i<range[1];i++)list.push([i,fn(vertices[i].p)]);morphs.set(name,list);}
const acrylic=surface('Translucent headphones',[68,190,208],.46);
const root=joint('センター','center',[0,7,0]);const torso=joint('上半身','spine',[0,8,0],root),neck=joint('首','neck',[0,12.4,0],torso),head=joint('頭','head',[0,13.3,0],neck);
sphere([0,9.95,.05],[1.75,2.43,1.04],torso,pearl);sphere([0,8.05,0],[1.23,.65,.75],root,dark);sphere([0,12.45,0],[.55,.72,.53],neck,dark);
sphere([0,14.35,0],[1.91,1.83,1.49],head,pearl,36,24);sphere([0,14.46,-.89],[1.60,1.20,.81],head,dark,36,24);
sphere([0,10.75,-.83],[.55,.64,.25],torso,teal);sphere([0,10.75,-1.035],[.21,.23,.10],torso,gold);
for(const sign of [1,-1]){
  const side=sign===1?'左':'右',en=sign===1?'left':'right';const at=(x,y,z=0)=>[x*sign,y,z];
  const eyePos=at(.63,14.7,-1.55);const eb=joint(side+'目',en+'eye',eyePos,head);
  const er=sphere(eyePos,[.23,.33,.13],eb,eye);for(const name of ['まばたき',sign===1?'ウィンク':'ウィンク右'])morph(name,er,p=>[0,-(p[1]-eyePos[1])*.94,0]);
  const brow=sphere(at(.65,15.28,-1.44),[.4,.065,.11],head,teal);morph('上',brow,()=>[0,.19,0]);morph('怒り',brow,p=>[0,(p[0]-eyePos[0])*.33*sign,0]);
  sphere(at(1.89,15.23,.04),[.20,.68,.28],head,acrylic);sphere(at(1.76,14.34,-.04),[.30,.72,.63],head,teal);sphere(at(1.97,14.36,-.04),[.145,.4,.36],head,gold);
  const a=at(1.57,11.35),b=at(3.62,10.83),w=at(5.24,10.43), arm=joint(side+'腕',en+'arm',a,torso), elbow=joint(side+'ひじ',en+'elbow',b,arm), wrist=joint(side+'手首',en+'wrist',w,elbow);
  sphere(a,[.58,.62,.61],arm,teal);tube(a,b,.49,.35,arm,pearl);sphere(b,[.39,.39,.39],elbow,dark);tube(b,w,.42,.24,elbow,pearl);sphere(w,[.3,.28,.31],wrist,teal);sphere(at(5.55,10.42,0),[.58,.19,.40],wrist,pearl);
  const names=['親指','人指','中指','薬指','小指'],english=['thumb','index','middle','ring','little'];
  for(let f=0;f<5;f++){
    const origin=at(f===0?5.40:5.88,10.42,-.31+f*.15);let parent=wrist, pos=origin;const len=[.23,.30,.34,.30,.24][f];
    for(let s=0;s<3;s++){
      const end=add(pos,at(len,-.045,f===0?-.18:0));const node=joint(side+names[f]+(s+(f?1:0)),en+english[f]+(s+1),pos,parent);
      sphere(pos,[.105,.105,.105],node,dark,8,6);tube(pos,end,.098,.083,node,s===2?teal:pearl,8);if(s===2)sphere(end,[.08,.08,.08],node,teal,8,6);parent=node;pos=end;
    }
  }
  const hip=at(.78,7.8),knee=at(.79,4.33),ankle=at(.80,.93);const leg=joint(side+'足',en+'leg',hip,root),kn=joint(side+'ひざ',en+'knee',knee,leg),an=joint(side+'足首',en+'ankle',ankle,kn);joint(side+'つま先',en+'toe',at(.8,.41,-.95),an);
  sphere(hip,[.68,.71,.67],leg,dark);tube(hip,knee,.61,.45,leg,pearl);sphere(knee,[.52,.50,.54],kn,teal);tube(knee,ankle,.50,.32,kn,pearl);sphere(at(.80,.53,-.27),[.52,.43,.92],an,dark);
}
const lips=sphere([0,13.94,-1.63],[.43,.067,.084],head,mouth);for(const [name,dx,dy] of [['あ',-.25,3.1],['い',.36,.28],['う',-.45,2.0],['え',.22,1.6],['お',-.50,3.2]])morph(name,lips,p=>[p[0]*dx,(p[1]-13.94)*dy,0]);morph('にやり',lips,p=>[0,Math.abs(p[0])*.43,0]);
const chunks=[];function bytes(...v){chunks.push(Buffer.from(v));}function u32(n){const b=Buffer.alloc(4);b.writeInt32LE(n);chunks.push(b);}function f32(n){const b=Buffer.alloc(4);b.writeFloatLE(n);chunks.push(b);}function vec(v){v.forEach(f32);}function text(s){const b=Buffer.from(s);u32(b.length);chunks.push(b);}function h16(n){const b=Buffer.alloc(2);b.writeUInt16LE(n);chunks.push(b);}
chunks.push(Buffer.from('PMX '));f32(2);bytes(8,1,0,4,4,4,4,4,4);text('Studio Pilot');text('Studio Pilot');text('MMD Live original demonstration character. MIT.');text('');
u32(vertices.length);for(const v of vertices){vec(v.p);vec(v.n);vec(v.uv);bytes(0);u32(v.bone);f32(1);}
const indices=materials.flatMap(m=>m.indices);u32(indices.length);indices.forEach(u32);u32(0);u32(materials.length);
for(const s of materials){text(s.name);text(s.name);vec([...s.color,s.alpha]);vec([0,0,0,1]);vec(s.color.map(v=>v*.32));bytes(1);vec([0,0,0,1]);f32(0);u32(-1);u32(-1);bytes(0,1,0);text('');u32(s.indices.length);}
u32(joints.length);for(const j of joints){text(j.name);text(j.en);vec(j.pos);u32(j.parent);u32(0);h16(26);vec([0,.2,0]);}
u32(morphs.size);for(const [name,items] of morphs){text(name);text(name);bytes(4,1);u32(items.length);for(const [i,d] of items){u32(i);vec(d);}}
u32(0);u32(0);u32(0);writeFileSync('assets/studio/StudioPilot.pmx',Buffer.concat(chunks));
