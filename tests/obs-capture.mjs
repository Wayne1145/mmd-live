// Requires the task-owned portable OBS instance and a running MMD Live output window.
import fs from 'node:fs/promises';
import path from 'node:path';
import {createHash,randomUUID} from 'node:crypto';
const config=JSON.parse(await fs.readFile(process.argv[2],'utf8'));
const out=process.argv[3];await fs.mkdir(out,{recursive:true});
const socket=new WebSocket(`ws://127.0.0.1:${config.server_port}`);
const hash=s=>createHash('sha256').update(s).digest('base64');
const pending=new Map();let ready,fail;
const connected=new Promise((resolve,reject)=>{ready=resolve;fail=reject});
socket.onerror=()=>fail(Error('Cannot reach the portable OBS instance'));
socket.onmessage=({data})=>{const {op,d}=JSON.parse(data);if(op===0){const id={rpcVersion:1};if(d.authentication)id.authentication=hash(hash(config.server_password+d.authentication.salt)+d.authentication.challenge);socket.send(JSON.stringify({op:1,d:id}));}else if(op===2)ready();else if(op===7){const r=pending.get(d.requestId);if(!r)return;pending.delete(d.requestId);if(d.requestStatus.result)r.resolve(d.responseData??{});else r.reject(Error(`${d.requestType} ${d.requestStatus.code} ${d.requestStatus.comment}`));}};
const sleep=ms=>new Promise(r=>setTimeout(r,ms));
const deadline=setTimeout(()=>{console.error('OBS validation timed out');process.exit(2)},65000);
function call(requestType,requestData={}){return new Promise((resolve,reject)=>{const requestId=randomUUID();pending.set(requestId,{resolve,reject});socket.send(JSON.stringify({op:6,d:{requestType,requestId,requestData}}));});}
try{
  await connected;const sceneName='MmdQA';
  const sources=(await call('GetInputList')).inputs.map(x=>x.inputName);
  if(!sources.includes('Backdrop'))await call('CreateInput',{sceneName,inputName:'Backdrop',inputKind:'color_source_v3',inputSettings:{width:1920,height:1080,color:0xff000000},sceneItemEnabled:true});
  if(!sources.includes('MMD Game Capture'))await call('CreateInput',{sceneName,inputName:'MMD Game Capture',inputKind:'game_capture',inputSettings:{capture_mode:'window',allow_transparency:true},sceneItemEnabled:true});
  const props=await call('GetInputPropertiesListPropertyItems',{inputName:'MMD Game Capture',propertyName:'window'});
  const target=props.propertyItems.find(w=>w.itemName.includes('MMD Live — OBS Output') && w.itemValue.toLowerCase().includes('mmdlive.exe'));
  if(!target)throw Error('MMD Live output window missing from the game capture window list');
  await call('SetInputSettings',{inputName:'MMD Game Capture',inputSettings:{window:target.itemValue,capture_mode:'window',allow_transparency:true,capture_cursor:false,priority:0},overlay:true});
  await call('SetCurrentProgramScene',{sceneName});
  await sleep(3300);
  for(let attempt=0;attempt<12;attempt++){
    const check=await call('GetSourceScreenshot',{sourceName:'MMD Game Capture',imageFormat:'png',imageWidth:480,imageHeight:270});
    if(check.imageData.length>6000)break;
    await sleep(650);
  }
  const settings=await call('GetInputSettings',{inputName:'MMD Game Capture'});
  const filters=await call('GetSourceFilterList',{sourceName:'MMD Game Capture'});
  for(const [name,value] of [['black',0xff000000],['white',0xffffffff]]){
    await call('SetInputSettings',{inputName:'Backdrop',inputSettings:{color:value},overlay:true});await sleep(550);
    const img=await call('GetSourceScreenshot',{sourceName:sceneName,imageFormat:'png',imageWidth:960,imageHeight:540});
    await fs.writeFile(path.join(out,name+'.png'),Buffer.from(img.imageData.split(',')[1],'base64'));
  }
  const frame=await call('GetSourceScreenshot',{sourceName:'MMD Game Capture',imageFormat:'png',imageWidth:960,imageHeight:540});
  await fs.writeFile(path.join(out,'captured.png'),Buffer.from(frame.imageData.split(',')[1],'base64'));
  const version=await call('GetVersion');const stats=await call('GetStats');
  await fs.writeFile(path.join(out,'obs.json'),JSON.stringify({version:version.obsVersion,source:settings.inputKind,settings:settings.inputSettings,filters:filters.filters.length,stats},null,2));
  console.log('OBS',version.obsVersion,settings.inputKind,'filters',filters.filters.length,'capture PNG bytes',Math.round(frame.imageData.length*.75));
} finally {clearTimeout(deadline);socket.close();}
