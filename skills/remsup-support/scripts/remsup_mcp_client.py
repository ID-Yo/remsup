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
import re
from datetime import datetime, timedelta
from urllib.parse import urlparse

class ClientError(Exception):
    def __init__(self,message,details=None):
        super().__init__(message);self.details=details or {}

SCREEN_REFUSAL_CODES=frozenset(('permission-required','mouse-disabled','typing-disabled','control-required','session-mismatch','stale-frame','geometry-changed','geometry-version-mismatch','coordinates-out-of-range','invalid-action','invalid-scroll','input-rate-limit','protected-window','desktop-unavailable','dpi-unavailable','monitor-unavailable','local-input-active','foreground-required','foreground-monitor-mismatch','modifiers-held','keyboard-context-changed','invalid-keyboard-operation','key-not-allowed','text-size-limit','invalid-text','lease-expired'))
DIAGNOSTIC_STATES=frozenset(('Stopped','Connecting','RelayWaiting','SessionActive','Reconnecting','Failed'))
DIAGNOSTIC_CHECK_RESULTS=frozenset(('Current','Available','Error','Disabled'))
DIAGNOSTIC_INSTALL_RESULTS=frozenset(('Installed','Deferred','RolledBack','RecoveryFailed','Failed'))
DIAGNOSTIC_ERROR_CODES=frozenset(('', 'check-failed','download-failed','readiness-failed','install-failed','recovery-failed','registration-repair','diagnostics-unavailable'))
DIAGNOSTIC_FAILURES={
    'unsupported':frozenset(('wrong-channel',)),
    'unavailable':frozenset(('unavailable','no-agent','busy','transport-unavailable')),
    'denied':frozenset(('invalid-request','relay-denied','authenticated-caller-required')),
    'error':frozenset(('response-too-large','incomplete-response','invalid-response','relay-error','diagnostics-unavailable')),
}
DIAGNOSTIC_WARNINGS=frozenset((
    "LAN IP is inactive. Refresh addresses and choose this computer's active private IPv4.",
    "LAN port must be 1-65535.",
    "The network is Public — change it to Private in Windows Settings before using LAN.",
    "LAN requires a Windows Private network. Check the network profile in Windows Settings.",
    "The scoped LAN firewall rule is missing. Select Prepare LAN firewall for this address and app.",
    "LAN identity is unavailable. Restore this Windows user's protected identity; it was not regenerated.",
    "LAN listener could not start. Check whether the address/port is already in use.",
    "LAN diagnostics unavailable.",
))
SETTING_TYPES={'autoUpdates':bool,'relayHost':str,'relayPort':int,'relayPin':str,'lanAddress':str,'lanPort':int,'lanEnabled':bool}
SETTING_STRING_LIMITS={'relayHost':253,'relayPin':100,'lanAddress':45}

def _diag_utc(value):
    if not isinstance(value,str) or len(value)>40 or not (value.endswith('Z') or value.endswith('+00:00')):raise ValueError()
    parsed=datetime.fromisoformat(value.replace('Z','+00:00'))
    if parsed.tzinfo is None or parsed.utcoffset()!=timedelta(0):raise ValueError()
    return value

def _diag_version(value,optional=False):
    if value is None and optional:return None
    if not isinstance(value,str) or len(value)>32 or not re.fullmatch(r'v1\.\d{1,6}',value):raise ValueError()
    return value

def _diag_code(value):
    return value if isinstance(value,str) and value in DIAGNOSTIC_ERROR_CODES else 'diagnostics-unavailable'

def project_diagnostics(value):
    """Keep only the bounded diagnostic contract before it reaches the caller."""
    if isinstance(value,dict) and value.get('status') in DIAGNOSTIC_FAILURES:
        status=value['status'];code=value.get('error_code')
        if isinstance(code,str) and code in DIAGNOSTIC_FAILURES[status]:return {'status':status,'error_code':code}
        return {'status':'error','error_code':'invalid-response'}
    try:
        if not isinstance(value,dict) or type(value.get('schema_version')) is not int or value.get('schema_version')!=1 or value.get('status')!='ok':raise ValueError()
        lan=value.get('lan');relay=value.get('relay');updates=value.get('updates')
        if not isinstance(lan,dict) or not isinstance(relay,dict) or not isinstance(updates,dict):raise ValueError()
        if any(type(lan.get(key)) is not bool for key in ('configured','enabled','ready')):raise ValueError()
        warning=lan.get('warning')
        if not isinstance(warning,str):raise ValueError()
        if warning not in DIAGNOSTIC_WARNINGS:warning='LAN diagnostics unavailable.' if warning else ''
        state=relay.get('state');ready=relay.get('ready')
        if not isinstance(state,str) or state not in DIAGNOSTIC_STATES or type(ready) is not bool:raise ValueError()
        if type(updates.get('enabled')) is not bool:raise ValueError()
        check=updates.get('last_check')
        if check is not None:
            if not isinstance(check,dict) or not isinstance(check.get('result'),str) or check['result'] not in DIAGNOSTIC_CHECK_RESULTS:raise ValueError()
            check={'utc':_diag_utc(check.get('utc')),'result':check['result'],'version':_diag_version(check.get('version'),True),'error_code':_diag_code(check.get('error_code'))}
        download=updates.get('last_download')
        if download is not None:
            if not isinstance(download,dict):raise ValueError()
            sha=download.get('sha256')
            if not isinstance(sha,str) or not re.fullmatch(r'[0-9a-f]{64}',sha):raise ValueError()
            download={'utc':_diag_utc(download.get('utc')),'version':_diag_version(download.get('version')),'sha256':sha}
        install=updates.get('last_install')
        if install is not None:
            if not isinstance(install,dict) or not isinstance(install.get('result'),str) or install['result'] not in DIAGNOSTIC_INSTALL_RESULTS:raise ValueError()
            install={'utc':_diag_utc(install.get('utc')),'result':install['result'],'error_code':_diag_code(install.get('error_code'))}
        errors=updates.get('latest_errors')
        if not isinstance(errors,list) or len(errors)>8:raise ValueError()
        latest=[]
        for item in errors:
            if not isinstance(item,dict):raise ValueError()
            latest.append({'utc':_diag_utc(item.get('utc')),'code':_diag_code(item.get('code'))})
        return {'schema_version':1,'status':'ok','version':_diag_version(value.get('version')),
                'lan':{'configured':lan['configured'],'enabled':lan['enabled'],'ready':lan['ready'],'warning':warning},
                'relay':{'state':state,'ready':ready},
                'updates':{'enabled':updates['enabled'],'last_check':check,'last_download':download,'last_install':install,'latest_errors':latest}}
    except (TypeError,ValueError,KeyError,OverflowError):
        return {'status':'error','error_code':'invalid-response'}

async def diagnostics_tool_preflight(session):
    listing=await session.list_tools()
    tools={tool.name:tool for tool in listing.tools}
    if not {'remsup_health','remsup_diagnostics'}.issubset(tools):
        raise ClientError('required-diagnostics-tools-unavailable')
    schema=tools['remsup_diagnostics'].inputSchema
    properties=schema.get('properties') if isinstance(schema,dict) else None
    required=schema.get('required') if isinstance(schema,dict) else None
    if not isinstance(properties,dict) or set(properties)!={'device_id','otp'} or not isinstance(required,list) or set(required)!={'device_id','otp'} or any(not isinstance(properties[key],dict) or properties[key].get('type')!='string' for key in ('device_id','otp')):
        raise ClientError('diagnostics-tool-schema-mismatch')

def validate_diagnostics_credentials(device_id,otp):
    if not isinstance(device_id,str) or len(device_id)>32 or not all(ch.isascii() and (ch.isdigit() or ch in ' -()') for ch in device_id) or sum(ch.isdigit() for ch in device_id)!=9:
        raise ClientError('invalid-request')
    if not isinstance(otp,str) or not 1<=len(otp)<=128 or not otp.isascii() or any(ord(ch)<=32 or ord(ch)>126 for ch in otp):
        raise ClientError('invalid-request')

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
    if request.get('operation')=='diagnostics':
        validate_diagnostics_credentials(request.get('profile',{}).get('DeviceId'),request.get('otp'))
    health=await call('remsup_health',{})
    if request['operation']=='health':
        route=request['route']
        if not health.get('ok') or (health.get('relay_host'),health.get('relay_port'))!=(route['relay_host'],route['relay_port']):raise ClientError('route-mismatch: MCP relay differs from provisioned backend')
        return {'health':health}
    route_check(request,health)
    credential={'device_id':request['profile']['DeviceId'],'otp':request['otp']}
    if request['operation']=='exec':
        return {'result':await call('remsup_exec_command',{**credential,'command':request['command']})}
    if request['operation']=='diagnostics':
        result=project_diagnostics(await call('remsup_diagnostics',credential))
        if result.get('status')!='ok':
            code=result.get('error_code')
            if code not in ('wrong-channel','unavailable','no-agent','busy','relay-denied','invalid-request','response-too-large','invalid-response','incomplete-response','transport-unavailable','relay-error','diagnostics-unavailable'):
                code='diagnostics-unavailable'
            raise ClientError(code)
        return {'diagnostics':result}
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
            if kind in ('settings-read','settings-set'):
                expected={'action'} if kind=='settings-read' else {'action','name','value'}
                if set(action)!=expected:
                    raise ClientError('invalid-settings-action-fields')
                if kind=='settings-set':
                    name=action['name'];value=action['value']
                    expected_type=SETTING_TYPES.get(name) if isinstance(name,str) else None
                    if expected_type is None:
                        raise ClientError('settings-forbidden')
                    if type(value) is not expected_type:
                        raise ClientError('settings-invalid-value')
                    if expected_type is str and len(value)>SETTING_STRING_LIMITS[name]:
                        raise ClientError('settings-invalid-value')
                    if expected_type is int and not 1<=value<=65535:
                        raise ClientError('settings-invalid-value')
                    if name=='lanEnabled' and value is not False:
                        raise ClientError('settings-forbidden')
                status=await call('remsup_screen_status',{'session_id':sid})
                if status.get('state')!='Granted' or status.get('can_control') is not True:
                    raise ClientError('control-required')
                age=frame_age(last,received) if last else None
                if emit:emit({'operation':kind,'frame_age_seconds':age})
                if kind=='settings-read':
                    result=await call('remsup_settings_read',{'session_id':sid})
                else:
                    result=await call('remsup_settings_set',{'session_id':sid,'name':name,'value':value})
                event={'operation':kind,'result':result};results.append(event)
                if emit:emit(event)
                # A network-setting acknowledgement can be followed by a reconnect. Never replay it.
                if result.get('settings_error')=='outcome-unknown' or result.get('connection_may_close') is True:
                    break
                continue
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
            if request['operation']=='diagnostics':
                await diagnostics_tool_preflight(session)
            async def call(name,args):
                result=await asyncio.wait_for(session.call_tool(name,args),request['timeout'])
                texts=[item.text for item in result.content if item.type=='text']
                if result.isError:
                    if name=='remsup_diagnostics':return {'status':'error','error_code':'diagnostics-unavailable'}
                    text=' '.join(texts)
                    refusal=screen_refusal(text) if name.startswith('remsup_screen_') else None
                    if refusal:raise ClientError(refusal['error'],refusal)
                    category=remote_error_category(text)
                    if category=='outcome-unknown':raise ClientError(category,{'dispatched':None,'channel_terminated':True,'warning':'Do not retry automatically; open a new session, take and inspect a new capture.'})
                    raise ClientError(category+': no automatic retry; outcome may be unknown')
                try:value=json.loads(texts[0]) if texts else {}
                except (ValueError,TypeError):
                    if name=='remsup_diagnostics':return {'status':'error','error_code':'invalid-response'}
                    raise
                if name=='remsup_diagnostics':value=project_diagnostics(value)
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
        if request.get('operation') not in ('health','exec','diagnostics','screen') or not 1<=request.get('timeout',0)<=90:raise ClientError('invalid-operation-or-timeout')
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
