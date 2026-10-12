"""Supported MCP SDK consumer. Requests and credentials enter only through stdin."""
import asyncio
import base64
import contextlib
import json
import ipaddress
import logging
import pathlib
import socket
import subprocess
import sys
import time
from datetime import datetime
from urllib.parse import urlparse

class ClientError(Exception):
    def __init__(self,message,details=None):
        super().__init__(message);self.details=details or {}

SCREEN_REFUSAL_CODES=frozenset(('permission-required','mouse-disabled','typing-disabled','control-required','session-mismatch','stale-frame','geometry-changed','geometry-version-mismatch','coordinates-out-of-range','invalid-action','invalid-scroll','input-rate-limit','protected-window','desktop-unavailable','dpi-unavailable','monitor-unavailable','local-input-active','foreground-required','foreground-monitor-mismatch','modifiers-held','keyboard-context-changed','invalid-keyboard-operation','key-not-allowed','text-size-limit','invalid-text','lease-expired'))

def screen_refusal(text):
    try:
        value=json.JSONDecoder().raw_decode(text[text.index('{'):])[0]
        if ((value.get('error') in SCREEN_REFUSAL_CODES and value.get('dispatched') is False) or (value.get('error')=='input-not-applied' and value.get('dispatched') is True) or (value.get('error')=='outcome-unknown' and value.get('dispatched') is None)) and type(value.get('channel_terminated')) is bool:
            return {key:value[key] for key in ('error','dispatched','channel_terminated')}
    except (ValueError,TypeError,AttributeError):pass

def frame_age(frame,received):
    try:return round(max(0,time.time()-datetime.fromisoformat(frame['captured_utc'].replace('Z','+00:00')).timestamp()),2)
    except (ValueError,KeyError,TypeError):return round(max(0,time.monotonic()-received),2)

def sanitize(value, request):
    text=json.dumps(value,ensure_ascii=False)
    for secret in (request.get('token'),request.get('otp')):
        if secret:
            text=text.replace(json.dumps(secret,ensure_ascii=False)[1:-1],'[REDACTED]').replace(secret,'[REDACTED]')
    return text

def route_check(request, health):
    route=request['route'];profile=request['profile']
    if not health.get('ok') or (health.get('relay_host'),health.get('relay_port'))!=(route['relay_host'],route['relay_port']):
        raise ClientError('route-mismatch: MCP relay differs from provisioned backend')
    if profile['Transport']!='Relay' or profile['RelayMode']=='Lan':
        raise ClientError('route-mismatch: MCP relay cannot execute a LAN profile')
    host=profile['Host'] if profile['RelayMode']=='Custom' else route['profile_host']
    port=profile['Port'] if profile['RelayMode']=='Custom' else route['profile_port']
    if (str(host).lower(),int(port))!=(str(route['profile_host']).lower(),route['profile_port']):
        raise ClientError('route-mismatch: saved profile differs from provisioned relay')

async def execute(request, call, emit=None, next_action=None):
    health=await call('remsup_health',{})
    if request['operation']=='health':
        route=request['route']
        if not health.get('ok') or (health.get('relay_host'),health.get('relay_port'))!=(route['relay_host'],route['relay_port']):raise ClientError('route-mismatch: MCP relay differs from provisioned backend')
        return {'health':health}
    route_check(request,health)
    credential={'device_id':request['profile']['DeviceId'],'otp':request['otp']}
    if request['operation']=='exec':
        return {'result':await call('remsup_exec_command',{**credential,'command':request['command']})}
    capabilities=await call('remsup_screen_capabilities',credential)
    if request.get('capabilities_only'):return {'capabilities':capabilities}
    if not capabilities.get('available') or request['monitor_id'] not in [m['monitor_id'] for m in capabilities['monitors']]:
        raise ClientError('screen-monitor-unavailable: select a capabilities monitor')
    # Capabilities closes its transient SCREEN channel; this is a new request, never a replay.
    await asyncio.sleep(1.1)
    response=await call('remsup_screen_request',{**credential,'monitor_id':request['monitor_id'],'control':bool(request.get('control'))})
    sid=response['session_id'];actions=iter(request.get('screen_actions',[]));results=[]
    try:
        end=time.monotonic()+30
        while True:
            status=await call('remsup_screen_status',{'session_id':sid})
            if status.get('can_view'):break
            if status.get('state')!='Pending' or time.monotonic()>=end:raise ClientError('screen-not-granted')
            await asyncio.sleep(1)
        if emit:emit({'session_id':sid,'status':status})
        last=None;received=0
        while True:
            action=await next_action() if next_action else next(actions,{'action':'close'})
            kind=action.get('action')
            if kind=='close':
                if emit:emit({'operation':'close','frame_age_seconds':frame_age(last,received) if last else None})
                break
            fields={'capture':set(),'status':set(),'mouse':{'frame_id','geometry_version','action','x','y','scroll_steps'},'text':{'frame_id','geometry_version','text'},'key':{'frame_id','geometry_version','key'}}
            # Wrapper action is named operation for mouse to retain its protocol action.
            kind=action.get('operation',kind)
            if kind not in fields:raise ClientError('unsupported-screen-action')
            args={k:v for k,v in action.items() if k not in ('operation','action') or k=='action' and kind=='mouse'}
            if set(args)-fields[kind]:raise ClientError('unsupported-screen-fields')
            if kind in ('mouse','text','key'):
                if not last or args.get('frame_id')!=last.get('frame_id') or args.get('geometry_version')!=last.get('geometry_version'):raise ClientError('screen-input-requires-latest-inspected-frame')
            age=frame_age(last,received) if last else None
            if emit:emit({'operation':kind,'frame_age_seconds':age,'warning':'Take and inspect a new capture before input; frame is older than 15 seconds.' if age is not None and age>15 else None})
            result=await call('remsup_screen_'+kind,{'session_id':sid,**args})
            if kind=='capture':last=result;received=time.monotonic()
            elif result.get('error') in ('input-not-applied','outcome-unknown'):last=None
            results.append({'operation':kind,'result':result})
            if emit:emit(results[-1])
        return {'screen':results}
    finally:
        try:
            closed=await asyncio.shield(call('remsup_screen_close',{'session_id':sid}))
            if emit:emit({'cleanup':closed.get('result','closed'),'channel_terminated':True})
        except Exception:
            if emit:emit({'cleanup':'screen-close-failed; lease remains bounded; do not replay input'})

@contextlib.contextmanager
def tunnel(config):
    loopback=socket.gethostbyname('localhost')
    if not ipaddress.ip_address(loopback).is_loopback:raise ClientError('localhost-must-resolve-to-loopback')
    alias=config.get('ssh_alias');endpoint=config.get('endpoint')
    if endpoint:
        url=urlparse(endpoint)
        if url.scheme!='https' or not url.hostname or url.username or url.password:raise ClientError('protected-endpoint-requires-verified-HTTPS')
        yield endpoint;return
    if not alias or not all(c.isalnum() or c in '._-' for c in alias):raise ClientError('invalid-provisioned-SSH-alias')
    policy=subprocess.run(['ssh','-G',alias],capture_output=True,text=True,timeout=10,check=True)
    user=next((line.split(' ',1)[1] for line in policy.stdout.splitlines() if line.startswith('user ')),None)
    if not user or user=='root':raise ClientError('operator-tunnel-requires-provisioned-non-root-account')
    port=int(config.get('local_port') or 0)
    if not 0<=port<=65535:raise ClientError('invalid-local-port')
    with socket.socket() as probe:
        try:probe.bind((loopback,port))
        except OSError:raise ClientError('tunnel-port-in-use: leave existing process untouched') from None
        port=probe.getsockname()[1]
    process=subprocess.Popen(['ssh','-N','-T','-o','BatchMode=yes','-o','StrictHostKeyChecking=yes','-o','ExitOnForwardFailure=yes','-o','ConnectTimeout=8','-o','ServerAliveInterval=15','-o','ServerAliveCountMax=2','-L',f'{loopback}:{port}:{loopback}:8765',alias],stdin=subprocess.DEVNULL,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
    try:
        end=time.monotonic()+12
        while time.monotonic()<end:
            if process.poll() is not None:raise ClientError('SSH-tunnel-failed: check provisioned account/host key')
            try:
                with socket.create_connection((loopback,port),timeout=.3):break
            except OSError:time.sleep(.1)
        else:raise ClientError('SSH-tunnel-timeout')
        if process.poll() is not None:raise ClientError('SSH-tunnel-exited')
        yield f'http://localhost:{port}/mcp'
    finally:
        if process.poll() is None:
            process.terminate()
            try:process.wait(timeout=5)
            except subprocess.TimeoutExpired:process.kill();process.wait(timeout=5)

async def sdk_run(request,endpoint):
    from mcp import ClientSession
    from mcp.client.streamable_http import streamable_http_client
    import httpx
    def emit(value):print(sanitize(value,request),flush=True)
    async with httpx.AsyncClient(headers={'Authorization':'Bearer '+request['token']},timeout=httpx.Timeout(15,read=request['timeout']),follow_redirects=False) as http_client, streamable_http_client(endpoint,http_client=http_client,terminate_on_close=True) as (reader,writer,_):
        async with ClientSession(reader,writer) as session:
            await session.initialize()
            async def call(name,args):
                result=await asyncio.wait_for(session.call_tool(name,args),request['timeout'])
                texts=[item.text for item in result.content if item.type=='text']
                if result.isError:
                    text=' '.join(texts)
                    refusal=screen_refusal(text) if name.startswith('remsup_screen_') else None
                    if refusal:raise ClientError(refusal['error'],refusal)
                    category=remote_error_category(text)
                    if category=='outcome-unknown':raise ClientError(category,{'dispatched':None,'channel_terminated':True,'warning':'Do not retry automatically; open a new session, take and inspect a new capture.'})
                    raise ClientError(category+': no automatic retry; outcome may be unknown')
                value=json.loads(texts[0]) if texts else {}
                images=[item for item in result.content if item.type=='image']
                if images:
                    output=pathlib.Path(request['image_directory']).resolve();output.mkdir(parents=True,exist_ok=True)
                    path=output/(value['frame_id']+'.png');path.write_bytes(base64.b64decode(images[0].data,validate=True));value['image_path']=str(path)
                return value
            async def next_action():
                emit({'next':'screen action JSON or close; inspect image before input'})
                line=await asyncio.to_thread(sys.stdin.readline)
                if not line:return {'action':'close'}
                if len(line)>65536:raise ClientError('screen-action-too-large')
                return json.loads(line)
            return await execute(request,call,emit,next_action if request.get('interactive') else None)

def remote_error_category(text):
    text=text.lower()
    if 'screen-input-outcome-unknown' in text:return 'outcome-unknown'
    for code in ('screen-session-unavailable','invalid-screen-state','invalid-screen-permission-deadline','screen-response-mismatch','screen-request-refused','desktop-unavailable','geometry-changed','screen-connection-closed','invalid-image-metadata','unsupported-png','capture-failed'):
        if code in text:return code
    return 'busy' if 'busy' in text else 'no-agent' if 'no-agent' in text else 'auth' if 'auth' in text else 'remote-tool-refused'

def main():
    logging.disable(logging.CRITICAL)
    for stream in (sys.stdin, sys.stdout):
        if hasattr(stream, 'reconfigure'):
            stream.reconfigure(encoding='utf-8-sig' if stream is sys.stdin else 'utf-8')
    request={}
    try:
        line=sys.stdin.readline(1024*1024+1)
        if len(line)>1024*1024:raise ClientError('request-too-large')
        request=json.loads(line)
        if request.get('operation') not in ('health','exec','screen') or not 1<=request.get('timeout',0)<=90:raise ClientError('invalid-operation-or-timeout')
        with tunnel(request['config']) as endpoint:
            result=asyncio.run(asyncio.wait_for(sdk_run(request,endpoint),630 if request.get('interactive') else min(300,request['timeout']*5)))
        print(sanitize({'ok':True,**result},request),flush=True)
        return 0
    except BaseException as failure:
        def known(e):
            if isinstance(e,ClientError):return e
            for child in getattr(e,'exceptions',[]):
                message=known(child)
                if message:return message
            return None
        error=known(failure)
        print(sanitize({'ok':False,'error':str(error) if error else 'auth-or-transport-failure: details suppressed; no retry; remote outcome may be unknown',**(error.details if error else {})},request),flush=True)
        return 1

if __name__=='__main__':sys.exit(main())
