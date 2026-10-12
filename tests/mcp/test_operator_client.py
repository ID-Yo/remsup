import asyncio
import contextlib
import importlib.util
import pathlib
import unittest
import subprocess
import socket
import sys
import tempfile
import time
from unittest.mock import patch,MagicMock

PATH=pathlib.Path(__file__).resolve().parents[2]/'skills/remsup-support/scripts/remsup_mcp_client.py'

class ClientTests(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        asyncio.get_running_loop().set_debug(False)
        spec=importlib.util.spec_from_file_location('operator_client',PATH)
        self.module=importlib.util.module_from_spec(spec);spec.loader.exec_module(self.module)
        self.request={'operation':'exec','token':'TOKEN_SENTINEL','otp':'PASSWORD_SENTINEL','profile':{'Transport':'Relay','RelayMode':'Default','DeviceId':'123456789','Host':'','Port':0},'route':{'relay_host':'127.0.0.1','relay_port':8443,'profile_host':'relay.example.invalid','profile_port':443},'command':'echo once','timeout':5}
    async def test_route_refused_before_command(self):
        calls=[]
        async def fake(name,args):
            calls.append(name);return {'ok':True,'relay_host':'wrong','relay_port':8443}
        with self.assertRaises(self.module.ClientError):await self.module.execute(self.request,fake)
        self.assertEqual(calls,['remsup_health'])
    async def test_exec_uses_only_requested_command(self):
        calls=[]
        async def fake(name,args):
            calls.append((name,args))
            if name=='remsup_health':return {'ok':True,'relay_host':'127.0.0.1','relay_port':8443}
            self.assertEqual(args,{'device_id':'123456789','otp':'PASSWORD_SENTINEL','command':'echo once'})
            return {'device_id':'123456789','exit_code':0,'stdout':'once','truncated':False}
        result=await self.module.execute(self.request,fake)
        self.assertEqual([name for name,args in calls],['remsup_health','remsup_exec_command'])
        self.assertEqual(result['result']['stdout'],'once')
    async def test_screen_never_uses_command_channel(self):
        self.request.update(operation='screen',monitor_id='synthetic',screen_actions=[{'action':'close'}])
        calls=[]
        async def fake(name,args):
            calls.append(name)
            return {'remsup_health':{'ok':True,'relay_host':'127.0.0.1','relay_port':8443},'remsup_screen_capabilities':{'available':True,'monitors':[{'monitor_id':'synthetic'}]},'remsup_screen_request':{'session_id':'session'},'remsup_screen_status':{'state':'Granted','can_view':True},'remsup_screen_close':{}}[name]
        await self.module.execute(self.request,fake)
        self.assertNotIn('remsup_exec_command',calls)
        self.assertEqual(calls[-1],'remsup_screen_close')
    async def test_unknown_mutation_is_never_retried(self):
        commands=[]
        async def fake(name,args):
            if name=='remsup_health':return {'ok':True,'relay_host':'127.0.0.1','relay_port':8443}
            commands.append(args['command'])
            raise TimeoutError('PASSWORD_SENTINEL TOKEN_SENTINEL')
        with self.assertRaises(TimeoutError):await self.module.execute(self.request,fake)
        self.assertEqual(commands,['echo once'])
    async def test_screen_finally_closes_on_failure(self):
        self.request.update(operation='screen',monitor_id='synthetic',screen_actions=[{'action':'capture'}])
        calls=[]
        async def fake(name,args):
            calls.append(name)
            if name=='remsup_health':return {'ok':True,'relay_host':'127.0.0.1','relay_port':8443}
            if name=='remsup_screen_capabilities':return {'available':True,'monitors':[{'monitor_id':'synthetic'}]}
            if name=='remsup_screen_request':return {'session_id':'session','state':'Granted'}
            if name=='remsup_screen_status':return {'state':'Granted','can_view':True}
            if name=='remsup_screen_capture':raise TimeoutError()
            return {}
        with self.assertRaises(TimeoutError):await self.module.execute(self.request,fake)
        self.assertEqual(calls[-1],'remsup_screen_close')
    async def test_error_redaction(self):
        self.assertNotIn('PASSWORD_SENTINEL',self.module.sanitize({'a':'PASSWORD_SENTINEL TOKEN_SENTINEL'},self.request))
        self.assertNotIn('TOKEN_SENTINEL',self.module.sanitize({'a':'PASSWORD_SENTINEL TOKEN_SENTINEL'},self.request))
    async def test_dispatched_error_requires_fresh_capture_before_more_input(self):
        self.request.update(operation='screen',monitor_id='synthetic',screen_actions=[{'action':'capture'},{'action':'text','text':'one','frame_id':'frame','geometry_version':1},{'action':'text','text':'two','frame_id':'frame','geometry_version':1}])
        seen=[]
        async def fake(name,args):
            seen.append(name)
            return {'remsup_health':{'ok':True,'relay_host':'127.0.0.1','relay_port':8443},'remsup_screen_capabilities':{'available':True,'monitors':[{'monitor_id':'synthetic'}]},'remsup_screen_request':{'session_id':'session'},'remsup_screen_status':{'state':'Granted','can_view':True},'remsup_screen_capture':{'frame_id':'frame','geometry_version':1},'remsup_screen_text':{'error':'input-not-applied','dispatched':True,'channel_terminated':False},'remsup_screen_close':{}}[name]
        with self.assertRaisesRegex(self.module.ClientError,'latest-inspected-frame'):await self.module.execute(self.request,fake)
        self.assertEqual(seen.count('remsup_screen_text'),1);self.assertEqual(seen[-1],'remsup_screen_close')

    async def test_unknown_screen_outcome_is_not_refusal(self):
        self.assertEqual(self.module.remote_error_category('Error executing tool: screen-input-outcome-unknown; do not retry automatically'),'outcome-unknown')
    async def test_dispatched_failure_extracts_safe_fields(self):
        self.assertEqual(self.module.screen_refusal('{"error":"input-not-applied","dispatched":true,"channel_terminated":false,"secret":"PRIVATE"}'),{'error':'input-not-applied','dispatched':True,'channel_terminated':False})

    async def test_busy_error_is_explicit(self):
        self.assertEqual(self.module.remote_error_category('ERR busy: command channel in use'),'busy')
    async def test_screen_refusal_extracts_allowlisted_code_only(self):
        import json
        for code in ('stale-frame','geometry-changed','lease-expired','protected-window','local-input-active'):
            result=self.module.screen_refusal('Error executing tool: '+json.dumps({'error':code,'dispatched':False,'channel_terminated':True,'secret':'PASSWORD_SENTINEL'}))
            self.assertEqual(result,{'error':code,'dispatched':False,'channel_terminated':True})
        self.assertIsNone(self.module.screen_refusal('{"error":"PASSWORD_SENTINEL","dispatched":false,"channel_terminated":true}'))
        import re
        native=(PATH.parents[3]/'desktop/host/ScreenChannel.cs').read_text(encoding='utf-8')
        body=native.split('static bool RefusedBeforeDispatch',1)[1].split('internal bool ProcessOne',1)[0]
        self.assertEqual(set(re.findall(r'case "([a-z-]+)"',body)),set(self.module.SCREEN_REFUSAL_CODES)-{'lease-expired'})
    async def test_main_emits_structured_screen_refusal(self):
        import io,json
        self.request['config']={}
        async def refused(*args):raise self.module.ClientError('geometry-changed',{'error':'geometry-changed','dispatched':False,'channel_terminated':True})
        @contextlib.contextmanager
        def endpoint(_):yield 'https://fixture.invalid/mcp'
        output=io.StringIO()
        with patch.object(self.module,'sdk_run',refused),patch.object(self.module,'tunnel',endpoint),patch.object(sys,'stdin',io.StringIO(json.dumps(self.request)+'\n')),patch.object(sys,'stdout',output):
            self.assertEqual(await asyncio.to_thread(self.module.main),1)
        self.assertEqual(json.loads(output.getvalue()),{'ok':False,'error':'geometry-changed','dispatched':False,'channel_terminated':True})
    async def test_frame_age_emitted_before_input_and_warns_after_15_seconds(self):
        self.request.update(operation='screen',monitor_id='synthetic',screen_actions=[{'action':'capture'},{'action':'text','text':'owned','frame_id':'frame','geometry_version':1}])
        events=[];seen=[]
        async def fake(name,args):
            seen.append(name)
            if name=='remsup_screen_text':
                self.assertEqual(events[-1]['frame_age_seconds'],20)
                self.assertIn('capture',events[-1]['warning'])
                raise self.module.ClientError('protected-window')
            return {'remsup_health':{'ok':True,'relay_host':'127.0.0.1','relay_port':8443},'remsup_screen_capabilities':{'available':True,'monitors':[{'monitor_id':'synthetic'}]},'remsup_screen_request':{'session_id':'session'},'remsup_screen_status':{'state':'Granted','can_view':True},'remsup_screen_capture':{'frame_id':'frame','geometry_version':1,'captured_utc':'2026-10-12T00:00:00Z'},'remsup_screen_close':{'result':'already-closed','channel_terminated':True}}[name]
        with patch.object(self.module.time,'time',return_value=1791763220):
            with self.assertRaises(self.module.ClientError):await self.module.execute(self.request,fake,events.append)
        self.assertEqual(seen[-1],'remsup_screen_close')
        self.assertEqual(events[-1]['cleanup'],'already-closed')
    async def test_tunnel_is_closed_on_exception_and_secrets_not_in_argv(self):
        process=MagicMock();process.poll.return_value=None
        policy=MagicMock(stdout='user operator\n')
        config={'ssh_alias':'fixture','local_port':18765}
        with patch.object(self.module.subprocess,'run',return_value=policy),patch.object(self.module.subprocess,'Popen',return_value=process) as start,patch.object(self.module.socket,'socket'),patch.object(self.module.socket,'create_connection'):
            with self.assertRaises(RuntimeError):
                with self.module.tunnel(config):raise RuntimeError('own fixture')
        process.terminate.assert_called_once();process.wait.assert_called_once()
        self.assertNotIn('TOKEN_SENTINEL',str(start.call_args));self.assertNotIn('PASSWORD_SENTINEL',str(start.call_args))
    async def test_root_tunnel_refused(self):
        with patch.object(self.module.subprocess,'run',return_value=MagicMock(stdout='user root\n')):
            with self.assertRaises(self.module.ClientError):
                with self.module.tunnel({'ssh_alias':'fixture'}):pass
    async def test_parallel_tunnels_select_distinct_free_ports_and_cleanup(self):
        listeners=[];processes=[]
        def start(args,**kwargs):
            forwarding=args[args.index('-L')+1];port=int(forwarding.split(':')[1])
            listener=socket.socket();listener.bind(('127.0.0.1',port));listener.listen();listeners.append(listener)
            process=MagicMock();process.poll.return_value=None;process.terminate.side_effect=listener.close;processes.append(process)
            return process
        try:
            with patch.object(self.module.subprocess,'run',return_value=MagicMock(stdout='user operator\n')),patch.object(self.module.subprocess,'Popen',side_effect=start):
                with self.module.tunnel({'ssh_alias':'fixture'}) as first:
                    with self.module.tunnel({'ssh_alias':'fixture'}) as second:
                        self.assertNotEqual(first,second)
                        self.assertNotIn(':0/',first)
                        self.assertNotIn(':0/',second)
            for process in processes:process.terminate.assert_called_once();process.wait.assert_called_once()
        finally:
            for listener in listeners:listener.close()
    async def test_tunnel_cleanup_on_interrupt(self):
        process=MagicMock();process.poll.return_value=None
        with patch.object(self.module.subprocess,'run',return_value=MagicMock(stdout='user operator\n')),patch.object(self.module.subprocess,'Popen',return_value=process),patch.object(self.module.socket,'create_connection'):
            with self.assertRaises(KeyboardInterrupt):
                with self.module.tunnel({'ssh_alias':'fixture'}):raise KeyboardInterrupt()
        process.terminate.assert_called_once();process.wait.assert_called_once()
    async def test_localhost_cannot_redirect_tunnel_to_remote(self):
        with patch.object(self.module.socket,'gethostbyname',return_value='192.0.2.1'),patch.object(self.module.subprocess,'Popen') as start:
            with self.assertRaises(self.module.ClientError):
                with self.module.tunnel({'ssh_alias':'fixture'}):pass
            start.assert_not_called()
    async def test_busy_port_preserves_existing_process(self):
        probe=MagicMock();probe.__enter__.return_value.bind.side_effect=OSError('busy')
        with patch.object(self.module.subprocess,'run',return_value=MagicMock(stdout='user operator\n')),patch.object(self.module.socket,'socket',return_value=probe),patch.object(self.module.subprocess,'Popen') as start:
            with self.assertRaises(self.module.ClientError):
                with self.module.tunnel({'ssh_alias':'fixture'}):pass
            start.assert_not_called()
    async def test_explicit_busy_port_preserves_listener(self):
        with socket.socket() as listener:
            listener.bind(('127.0.0.1',0));listener.listen();port=listener.getsockname()[1]
            with patch.object(self.module.subprocess,'run',return_value=MagicMock(stdout='user operator\n')),patch.object(self.module.subprocess,'Popen') as start:
                with self.assertRaisesRegex(self.module.ClientError,'tunnel-port-in-use'):
                    with self.module.tunnel({'ssh_alias':'fixture','local_port':port}):pass
                start.assert_not_called()
                self.assertEqual(listener.getsockname()[1],port)
    async def test_screen_input_requires_last_frame(self):
        self.request.update(operation='screen',monitor_id='synthetic',screen_actions=[{'action':'text','text':'owned','frame_id':'unseen','geometry_version':1}])
        calls=[]
        async def fake(name,args):
            calls.append(name)
            responses={'remsup_health':{'ok':True,'relay_host':'127.0.0.1','relay_port':8443},'remsup_screen_capabilities':{'available':True,'monitors':[{'monitor_id':'synthetic'}]},'remsup_screen_request':{'session_id':'session'},'remsup_screen_status':{'state':'Granted','can_view':True},'remsup_screen_close':{}}
            return responses[name]
        with self.assertRaises(self.module.ClientError):await self.module.execute(self.request,fake)
        self.assertNotIn('remsup_screen_text',calls);self.assertEqual(calls[-1],'remsup_screen_close')
    async def test_lan_profile_refused(self):
        self.request['profile']['Transport']='Lan'
        with self.assertRaises(self.module.ClientError):self.module.route_check(self.request,{'ok':True,'relay_host':'127.0.0.1','relay_port':8443})
    async def test_real_fake_mcp_sdk_wire_and_redacted_output(self):
        fixture=pathlib.Path(__file__).with_name('operator_fake_server.py')
        with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
        process=subprocess.Popen([sys.executable,'-B',str(fixture),str(port)],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        try:
            end=time.monotonic()+15
            while time.monotonic()<end:
                try:
                    with socket.create_connection(('127.0.0.1',port),timeout=.1):break
                except OSError:await asyncio.sleep(.1)
            result=await self.module.sdk_run(self.request,f'http://127.0.0.1:{port}/mcp')
            output=self.module.sanitize(result,self.request)
            self.assertIn('[REDACTED]',output);self.assertNotIn('PASSWORD_SENTINEL',output);self.assertNotIn('TOKEN_SENTINEL',output)
            self.assertEqual(result['result']['exit_code'],0)
            self.request['command']='fixture-busy'
            with self.assertRaises(BaseExceptionGroup) as failure:
                await self.module.sdk_run(self.request,f'http://127.0.0.1:{port}/mcp')
            self.assertIn('busy: no automatic retry',repr(failure.exception))
            self.assertNotIn('PASSWORD_SENTINEL',repr(failure.exception))
            self.assertNotIn('TOKEN_SENTINEL',repr(failure.exception))
            self.request['operation']='health'
            health=await self.module.sdk_run(self.request,f'http://127.0.0.1:{port}/mcp')
            self.assertEqual(health['health']['command_count'],2)
            self.request.update(operation='screen',monitor_id='synthetic',screen_actions=[{'action':'capture'},{'action':'text','text':'synthetic','frame_id':'synthetic-frame','geometry_version':1}])
            with self.assertRaises(BaseExceptionGroup) as refusal:
                await self.module.sdk_run(self.request,f'http://127.0.0.1:{port}/mcp')
            def find(error):
                if isinstance(error,self.module.ClientError):return error
                for child in getattr(error,'exceptions',[]):
                    result=find(child)
                    if result:return result
            details=find(refusal.exception).details
            self.assertEqual(details,{'error':'protected-window','dispatched':False,'channel_terminated':False})
            self.request['operation']='health'
            health=await self.module.sdk_run(self.request,f'http://127.0.0.1:{port}/mcp')
            self.assertEqual(health['health']['screen_closes'],1)
            self.request.update(operation='screen');self.request['screen_actions'][1]['text']='fixture-unknown'
            with self.assertRaises(BaseExceptionGroup) as unknown:await self.module.sdk_run(self.request,f'http://127.0.0.1:{port}/mcp')
            error=find(unknown.exception);self.assertEqual(str(error),'outcome-unknown');self.assertIsNone(error.details['dispatched']);self.assertTrue(error.details['channel_terminated']);self.assertIn('new capture',error.details['warning'])

        finally:
            process.terminate();process.wait(timeout=5)
    async def test_sdk_auth_error_details_never_reach_main_output(self):
        import io,json
        self.request['config']={}
        async def refused(*args):raise Exception('401 TOKEN_SENTINEL PASSWORD_SENTINEL')
        @__import__('contextlib').contextmanager
        def endpoint(_):yield 'https://fixture.invalid/mcp'
        output=io.StringIO()
        with patch.object(self.module,'sdk_run',refused),patch.object(self.module,'tunnel',endpoint),patch.object(sys,'stdin',io.StringIO(json.dumps(self.request)+'\n')),patch.object(sys,'stdout',output):
            self.assertEqual(await asyncio.to_thread(self.module.main),1)
        self.assertNotIn('TOKEN_SENTINEL',output.getvalue());self.assertNotIn('PASSWORD_SENTINEL',output.getvalue());self.assertIn('auth-or-transport-failure',output.getvalue())

if __name__=='__main__':unittest.main()
