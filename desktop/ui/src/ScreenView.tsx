import React,{useEffect,useRef,useState} from 'react';
export type ScreenState={state:string;canView:boolean;canControl:boolean;continuous?:boolean;operatorLabel:string;preview:string|null;width:number;height:number;frameId:string;geometryVersion:number;error:string};
type Action=(kind:string,payload?:unknown)=>Promise<any>;
type Frame={url:string;frameId:string;geometryVersion:number;width:number;height:number;capturedAt:number};
type PointEvent={currentTarget:HTMLImageElement;clientX:number;clientY:number};
type PointerInput={action:string;x:number;y:number;scrollSteps:number;frameId:string;geometryVersion:number};
const keys=[['ctrl+l','Ctrl+L'],['ctrl+a','Ctrl+A'],['ctrl+s','Ctrl+S'],['ctrl+shift+s','Ctrl+Shift+S'],['enter','Enter'],['tab','Tab'],['shift+tab','Shift+Tab'],['escape','Escape'],['backspace','Backspace'],['delete','Delete'],['up','Up'],['down','Down'],['left','Left'],['right','Right'],['home','Home'],['end','End']];
const refreshDelay=1100; // Receiver rejects captures less than 1000ms apart.
export function ScreenView({screen:s,sessionId,action,allowControl=true}:{screen:ScreenState;sessionId:string;action:Action;allowControl?:boolean}){
 const [control,setControl]=useState(allowControl),[monitor,setMonitor]=useState(''),[busy,setBusy]=useState(false),[mouse,setMouse]=useState('click'),[text,setText]=useState(''),[key,setKey]=useState(''),[inputStatus,setInputStatus]=useState(''),[now,setNow]=useState(Date.now()),[paused,setPaused]=useState(false),[captureError,setCaptureError]=useState('');
 const [displayed,setDisplayed]=useState<Frame|null>(null);
 const panel=useRef<HTMLElement>(null),image=useRef<HTMLImageElement>(null),refreshToggle=useRef<HTMLButtonElement>(null),latest=useRef({s,sessionId,action,allowControl}),painted=useRef<Frame|null>(null);
 latest.current={s,sessionId,action,allowControl};
 const epoch=useRef(0),mounted=useRef(false),flight=useRef<symbol|null>(null),requested=useRef(false),stopped=useRef(false),manualPause=useRef(false),hovered=useRef(false),focused=useRef(false);
 const nextCapture=useRef(0),lastStatus=useRef(0),captureStarted=useRef(0),failures=useRef(0),consumed=useRef(''),afterInput=useRef(false),retryRequested=useRef(false);
 const held=useRef<{pointer:number;button:string;released:boolean}|null>(null),queuedDown=useRef<PointerInput|null>(null),queuedMove=useRef<PointerInput|null>(null),queuedUp=useRef<PointerInput|null>(null),nextPointer=useRef(0),flightKind=useRef(''),dragFrame=useRef(false),downAcknowledged=useRef(false);
 const pointerOwner=useRef<{sessionId:string;action:Action}|null>(null);
 const visible=()=>document.visibilityState!=='hidden'&&!!panel.current?.getClientRects().length;
 const validFrame=()=>{
  const {s:state,allowControl:allowed}=latest.current,frame=painted.current;
  return !!frame&&allowed&&state.state==='Granted'&&state.canView&&state.canControl&&frame.url===state.preview&&frame.frameId===state.frameId&&frame.geometryVersion===state.geometryVersion&&frame.width===state.width&&frame.height===state.height&&(state.continuous||frame.frameId!==consumed.current)&&Date.now()-frame.capturedAt<30000;
 };
 async function call(kind:string,payload:object={}){
  if(flight.current&&kind!=='screen.close')return null;
  const generation=epoch.current,target=latest.current,token=Symbol(kind);
  if(kind==='screen.close'){stopped.current=true;requested.current=true;afterInput.current=false;held.current=null;pointerOwner.current=null;queuedDown.current=queuedMove.current=queuedUp.current=null;painted.current=null;setDisplayed(null);}
  else{flight.current=token;flightKind.current=kind;}
  if(kind==='screen.request'){requested.current=true;stopped.current=false;failures.current=0;setCaptureError('');}
  if(kind==='screen.capture'){dragFrame.current=!!held.current&&downAcknowledged.current;captureStarted.current=Date.now();afterInput.current=false;retryRequested.current=false;failures.current++;}
  setBusy(true);
  let result:any=null;
  try{result=await target.action(kind,{sessionId:target.sessionId,...payload});return result;}
  catch{return null;}
  finally{
   if(mounted.current&&epoch.current===generation){
    if(kind==='screen.capture'){
     nextCapture.current=Date.now()+(latest.current.s.continuous?100:refreshDelay);
     if(failures.current>=3)setCaptureError('Automatic refresh paused after three unsuccessful captures. Retry images to recover.');
    }
    if(flight.current===token){flight.current=null;flightKind.current='';}
    if(!flight.current)setBusy(false);
   }
  }
 }
 function cancelPointer(){if(held.current||queuedUp.current){const owner=pointerOwner.current;held.current=null;queuedDown.current=queuedMove.current=queuedUp.current=null;pointerOwner.current=null;if(owner&&owner.sessionId!==latest.current.sessionId)void owner.action('screen.close',{sessionId:owner.sessionId}).catch(()=>{});else void call('screen.close');}}
 async function flushPointer(){
  if(flight.current||Date.now()<nextPointer.current)return false;
  let input=queuedDown.current??queuedMove.current??queuedUp.current;if(!input)return false;
  if(held.current&&downAcknowledged.current&&!queuedDown.current&&!queuedUp.current&&input.action==='move'&&Date.now()>=nextCapture.current&&!manualPause.current)return false;
  if(held.current&&downAcknowledged.current&&!queuedDown.current&&painted.current&&input.geometryVersion===painted.current.geometryVersion)input={...input,frameId:painted.current.frameId};
  if(!visible()||!validFrame()||!painted.current||input.frameId!==painted.current.frameId||input.geometryVersion!==painted.current.geometryVersion){cancelPointer();queuedMove.current=null;return true;}
  if(queuedDown.current)queuedDown.current=null;else if(queuedMove.current)queuedMove.current=null;else queuedUp.current=null;
  const generation=epoch.current;
  const result=await call('screen.mouse',input);
  if(!mounted.current||epoch.current!==generation||stopped.current)return true;
  nextPointer.current=Date.now()+50;
  if(!result){held.current=null;queuedDown.current=queuedMove.current=queuedUp.current=null;setInputStatus('Pointer outcome unclear. Screen disconnected to release held input; no replay.');void call('screen.close');}
  else if(input.action.endsWith('-down')){downAcknowledged.current=true;}
  else if(input.action.endsWith('-up')){held.current=null;pointerOwner.current=null;afterInput.current=true;}
  return true;
 }
 function pointer(e:React.PointerEvent<HTMLImageElement>,kind:'move'|'down'|'up'){
  if(!latest.current.s.continuous)return;
  const r=e.currentTarget.getBoundingClientRect(),frame=painted.current;if(!frame)return;
  const input:PointerInput={action:kind,x:Math.max(0,Math.min(frame.width-1,Math.floor((e.clientX-r.left)*frame.width/r.width))),y:Math.max(0,Math.min(frame.height-1,Math.floor((e.clientY-r.top)*frame.height/r.height))),scrollSteps:0,frameId:frame.frameId,geometryVersion:frame.geometryVersion};
  if(kind==='up'){
   if(!held.current||held.current.pointer!==e.pointerId)return;
   e.preventDefault();held.current.released=true;input.action=held.current.button+'-up';queuedUp.current=input;void flushPointer();return;
  }
  if(!visible()||!validFrame()||e.currentTarget.currentSrc!==frame.url||queuedUp.current)return;
  if(kind==='down'){
   if(held.current||(flight.current&&flightKind.current!=='screen.mouse')||e.button<0||e.button>2)return;
   const button=['left','middle','right'][e.button];downAcknowledged.current=false;dragFrame.current=false;held.current={pointer:e.pointerId,button,released:false};pointerOwner.current={sessionId:latest.current.sessionId,action:latest.current.action};queuedMove.current=null;input.action=button+'-down';
   try{e.currentTarget.setPointerCapture(e.pointerId);}catch{cancelPointer();return;}
   e.preventDefault();queuedDown.current=input;
  }else{
   if(held.current&&held.current.pointer!==e.pointerId)return;
   queuedMove.current=input;
  }
  void flushPointer();
 }
 useEffect(()=>{
  mounted.current=true;epoch.current++;flight.current=null;requested.current=false;stopped.current=false;failures.current=0;nextCapture.current=0;lastStatus.current=0;captureStarted.current=0;consumed.current='';painted.current=null;afterInput.current=false;retryRequested.current=false;hovered.current=false;focused.current=false;
  manualPause.current=false;held.current=null;queuedDown.current=queuedMove.current=queuedUp.current=null;nextPointer.current=0;flightKind.current='';setPaused(false);setBusy(false);setDisplayed(null);setText('');setKey('');setInputStatus('');setCaptureError('');
  const tick=()=>{
   setNow(Date.now());
   const state=latest.current.s;
   if(!visible()){cancelPointer();return;}
   if(state.state!=='Granted'||!state.canView||!state.canControl||!latest.current.allowControl)cancelPointer();
   if(stopped.current||flight.current)return;
   if(state.state==='Off'&&!requested.current){void call('screen.request',{monitorId:'',control:latest.current.allowControl});return;}
   if(state.state==='Pending'){
    if(Date.now()-lastStatus.current>=1000){lastStatus.current=Date.now();void call('screen.status');}return;
   }
   if(state.state!=='Granted'||!state.canView||!state.canControl||!latest.current.allowControl){cancelPointer();if(state.state!=='Granted'||!state.canView)return;}
   if(state.continuous&&(queuedDown.current||queuedUp.current)){void flushPointer();return;}
   if(state.continuous&&held.current&&downAcknowledged.current&&!manualPause.current&&failures.current<3&&Date.now()>=nextCapture.current){void call('screen.capture');return;}
   if(state.continuous&&queuedMove.current){void flushPointer();return;}
   const waitingForPaint=!!state.preview&&(!painted.current||painted.current.frameId!==state.frameId);
   const interacting=state.continuous?!!held.current: hovered.current||(focused.current&&document.activeElement!==refreshToggle.current);
   if(!held.current&&!manualPause.current&&failures.current<3&&(!waitingForPaint||retryRequested.current)&&(!interacting||afterInput.current||retryRequested.current)&&Date.now()>=nextCapture.current){void call('screen.capture');return;}
   if(Date.now()-lastStatus.current>=1000){lastStatus.current=Date.now();void call('screen.status');}
  };
  const timer=setInterval(tick,100);tick();
  const blurred=()=>cancelPointer();window.addEventListener('blur',blurred);
  return()=>{cancelPointer();mounted.current=false;epoch.current++;flight.current=null;painted.current=null;clearInterval(timer);window.removeEventListener('blur',blurred);};
 },[sessionId]);
 useEffect(()=>{
  if(s.state!=='Granted'||!s.canView||stopped.current){painted.current=null;setDisplayed(null);}
 },[s.state,s.canView,s.preview]);
 function acceptPaintedImage(element:HTMLImageElement){
  const state=latest.current.s;
  if(!mounted.current||stopped.current||latest.current.sessionId!==sessionId||element!==image.current||state.state!=='Granted'||!state.canView||element.currentSrc!==state.preview||s.frameId!==state.frameId||s.geometryVersion!==state.geometryVersion)return;
  if(element.naturalWidth!==state.width||element.naturalHeight!==state.height){painted.current=null;setDisplayed(null);failures.current=3;setCaptureError('Image dimensions do not match the authorized frame. Retry images before input.');return;}
  if(held.current&&(!downAcknowledged.current||!dragFrame.current))return;
  dragFrame.current=false;const frame={url:state.preview!,frameId:state.frameId,geometryVersion:state.geometryVersion,width:state.width,height:state.height,capturedAt:captureStarted.current||Date.now()};painted.current=frame;setDisplayed(frame);failures.current=0;setCaptureError('');
 }
 useEffect(()=>{
  const previous=painted.current,element=image.current;
  // An unchanged PNG keeps its decoded pixels and does not emit another load event.
  if((!held.current||dragFrame.current)&&previous&&element?.complete&&previous.url===s.preview&&previous.frameId!==s.frameId&&previous.geometryVersion===s.geometryVersion&&previous.width===s.width&&previous.height===s.height)acceptPaintedImage(element);
 },[sessionId,s.frameId,s.preview,s.geometryVersion,s.width,s.height,s.state,s.canView]);
 const currentFrame=validFrame()&&now-(displayed?.capturedAt??0)<30000;
 const inputReady=currentFrame&&!busy;
 // Local drafts stay editable during status requests; dispatch still requires inputReady and no flight.
 async function sendInput(kind:'screen.text'|'screen.key'|'screen.mouse',payload:object){
  if(!visible()||!validFrame()||!painted.current)return;
  if(held.current||queuedUp.current)return;
  if(flight.current){setInputStatus('An image or input request is still pending. No new input was sent.');return;}
  const frame=painted.current,generation=epoch.current;
  consumed.current=frame.frameId;setInputStatus('');
  const result=await call(kind,{...payload,frameId:frame.frameId,geometryVersion:frame.geometryVersion});
  if(!mounted.current||epoch.current!==generation||stopped.current)return;
  setInputStatus(result?'Input submitted. Refreshing the image.':'Input result is unclear. Do not resend it. Inspect a new image before continuing.');
  afterInput.current=true;nextCapture.current=Math.max(nextCapture.current,Date.now());setNow(Date.now());
 }
 function point(e:PointEvent,operation:string,scrollSteps=0){
  if(!validFrame()||!painted.current||e.currentTarget.currentSrc!==painted.current.url)return;
  const frame=painted.current,r=e.currentTarget.getBoundingClientRect();
  const x=Math.floor((e.clientX-r.left)*frame.width/r.width),y=Math.floor((e.clientY-r.top)*frame.height/r.height);
  if(x<0||y<0||x>=frame.width||y>=frame.height)return;
  void sendInput('screen.mouse',{action:operation,x,y,scrollSteps});
 }
 useEffect(()=>{
  const element=image.current;if(!element)return;
  const wheel=(event:WheelEvent)=>{if(flight.current||!validFrame()||!event.deltaY)return;event.preventDefault();point({currentTarget:element,clientX:event.clientX,clientY:event.clientY},'scroll',event.deltaY>0?-1:1);};
  element.addEventListener('wheel',wheel,{passive:false});
  return()=>element.removeEventListener('wheel',wheel);
 },[s.preview,s.frameId,s.state,s.canView,sessionId]);
 function keyboard(e:React.KeyboardEvent<HTMLImageElement>){
  if(!inputReady||e.repeat||e.altKey||e.metaKey||e.nativeEvent.isComposing)return;
  const names:Record<string,string>={ArrowUp:'up',ArrowDown:'down',ArrowLeft:'left',ArrowRight:'right',Escape:'escape',Backspace:'backspace',Delete:'delete',Enter:'enter',Tab:'tab',Home:'home',End:'end'};
  const value=(e.ctrlKey?'ctrl+':'')+(e.shiftKey?'shift+':'')+(names[e.key]??e.key.toLowerCase());
  if(keys.some(([name])=>name===value)){e.preventDefault();void sendInput('screen.key',{key:value});}
 }
 const sendText=()=>{if(text.length<1||text.length>128)return;const value=text;setText('');void sendInput('screen.text',{text:value});};
 const request=()=>{nextCapture.current=0;painted.current=null;setDisplayed(null);void call('screen.request',{monitorId:monitor,control:control&&allowControl});};
 const retry=()=>{failures.current=0;retryRequested.current=true;setCaptureError('');};
 return <section ref={panel} className="screen-panel" aria-label="Screen session" onFocusCapture={()=>{focused.current=true}} onBlurCapture={e=>{focused.current=e.currentTarget.contains(e.relatedTarget as Node|null)}}>
  <div className="row between"><h2>Screen access</h2><strong role="status">{stopped.current?'Off':s.state}</strong></div>
  <p className="warning">Controlled test only. Relay traffic uses pinned TLS. Direct LAN uses the verified receiver SPKI pin. The relay administrator can read relay traffic.</p>
  <details open={s.state!=='Granted'}><summary>Request permissions and monitor</summary><label className="check"><input type="checkbox" checked={control} onChange={e=>setControl(e.target.checked)}/>Request mouse and keyboard control as well as viewing</label><label className="field"><span>Monitor ID (blank selects the first available)</span><input value={monitor} onChange={e=>setMonitor(e.target.value)} placeholder="First available monitor"/></label><p className="small muted">Receiving Security settings and local owner approval policy still apply. Pairing alone does not grant screen rights. Operator label is claimed, not independently verified.</p></details>
  <div className="row screen-actions"><button className="btn primary" disabled={busy} onClick={request}>Request screen access</button><button ref={refreshToggle} className="btn" disabled={!s.canView||stopped.current} onClick={()=>{manualPause.current=!manualPause.current;setPaused(manualPause.current)}}>{paused?'Resume images':'Pause images'}</button><button className="btn" disabled={!s.canView||busy||stopped.current} onClick={retry}>Retry images</button><button className="btn" disabled={s.state==='Off'||stopped.current} onClick={()=>void call('screen.close')}>Disconnect screen</button></div>
  {s.error&&<p className="warning" role="alert">{s.error}</p>}{captureError&&<p className="warning" role="alert">{captureError}</p>}
  {s.state==='Pending'&&!stopped.current&&<p role="status">Waiting for pairing or screen approval on the receiving computer.</p>}
  {s.state==='Granted'&&!stopped.current&&!displayed&&<p role="status">Permission granted. Awaiting first rendered frame…</p>}
  {s.state==='Granted'&&s.canView&&!stopped.current&&s.preview&&<><div className="row">{!s.continuous&&<label className="field"><span>Mouse action</span><select value={mouse} onChange={e=>setMouse(e.target.value)} disabled={!s.canControl||!allowControl}><option value="click">Left click</option><option value="double-click">Double click</option><option value="right-click">Right click</option></select></label>}<span className="small">{s.canControl&&allowControl?(s.continuous?'Move, press and drag on the image; scroll or focus it for supported key chords.':'Click or scroll on the image; focus it for supported complete key chords.'):'View only · mouse and keyboard control are disabled.'}</span></div>
   <img ref={image} key={s.continuous?'continuous':s.frameId} draggable={false} style={{touchAction:'none'}} className={'screen-preview '+(currentFrame?'controllable':'')} src={s.preview} alt="Latest owner-authorized screen capture" tabIndex={s.canControl&&allowControl?0:-1} data-frame-id={displayed?.frameId} data-geometry-version={displayed?.geometryVersion}
    onMouseEnter={()=>{hovered.current=true}} onMouseLeave={()=>{hovered.current=false}}
    onLoad={e=>acceptPaintedImage(e.currentTarget)}
    onError={()=>{painted.current=null;setDisplayed(null);failures.current=3;setCaptureError('Image could not be rendered. Retry images; no input was sent.');}}
    onPointerDown={e=>pointer(e,'down')} onPointerMove={e=>pointer(e,'move')} onPointerUp={e=>pointer(e,'up')} onPointerCancel={cancelPointer} onLostPointerCapture={()=>{if(held.current&&!held.current.released)cancelPointer();}}
    onClick={e=>{if(!s.continuous)point(e,mouse)}} onContextMenu={e=>{if(validFrame()){e.preventDefault();if(!s.continuous)point(e,'right-click');}}} onKeyDown={keyboard}/>
   <p className="small muted">{s.width} × {s.height} pixels · geometry {s.geometryVersion}. {s.continuous?'Automatic PNG frames request a 100ms cadence with one request in flight. Moves coalesce; acknowledged drags continue receiving images between input requests. Actual frame rate depends on relay and rendering latency.':'Automatic images use one request at a time, at least 1.1 seconds after completion. Hover/focus pauses refresh to preserve the inspected frame.'} This is not a video codec stream. Raw key down/up is not supported.</p>
   {s.canControl&&allowControl&&<div className="stack" role="group" aria-label="Keyboard control"><label className="field"><span>Text to type (up to 128 UTF-16 units)</span><textarea value={text} maxLength={128} onChange={e=>setText(e.target.value)} aria-label="Keyboard text"/></label><div className="row"><button className="btn compact" disabled={!inputReady||!text||text.length>128} onClick={sendText}>Send text</button></div><label className="field"><span>Keyboard key</span><select value={key} onChange={e=>setKey(e.target.value)}><option value="">Choose a key</option>{keys.map(([value,label])=><option key={value} value={value}>{label}</option>)}</select></label><button className="btn compact" disabled={!inputReady||!key} onClick={()=>{const value=key;setKey('');void sendInput('screen.key',{key:value})}}>Send key</button><p className="small muted">Input uses only the currently painted frame and unchanged geometry, younger than 30 seconds. No clipboard/file transfer or automatic input replay.</p></div>}
  </>}{inputStatus&&<p className="small warning" role="status">{inputStatus}</p>}
 </section>;
}
