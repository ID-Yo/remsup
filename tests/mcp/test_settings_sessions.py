import io
import json
import os
import sys
import unittest
import uuid
from pathlib import Path
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / 'mcp'))
os.environ.setdefault('REMSUP_MCP_TOKEN', 'synthetic-test-token')
import remsup_mcp_server as server
from remsup_screen_protocol import read_metadata, write_metadata
from remsup_screen_sessions import ScreenSessions


SETTINGS = {
    'autoUpdates': True,
    'relayHost': 'relay.example.test',
    'relayPort': 443,
    'relayPin': 'a' * 64,
    'lanAddress': '192.168.1.2',
    'lanPort': 8444,
    'lanEnabled': False,
}


class SettingsChannel:
    def __init__(self, break_on_set=False, can_control=True, settings_error=''):
        self.session_id = str(uuid.uuid4())
        self.incoming = io.BytesIO()
        self.requests = []
        self.closed = False
        self.break_on_set = break_on_set
        self.can_control = can_control
        self.settings_error = settings_error

    def write(self, data):
        request = read_metadata(io.BytesIO(data))
        self.requests.append(request)
        response = {'version': 1, 'request_id': request['request_id'], 'session_id': self.session_id,
                    'state': 'Granted', 'can_view': True, 'can_control': self.can_control,
                    'remaining_ms': 600000, 'settings': None, 'settings_error': ''}
        if request['operation'] == 'request':
            response = {'version': 1, 'request_id': request['request_id'], 'session_id': self.session_id,
                        'state': 'Granted', 'can_view': True, 'can_control': self.can_control, 'remaining_ms': 600000}
        elif request['operation'] == 'status':
            response.pop('settings'); response.pop('settings_error')
        elif request['operation'] == 'settings.read':
            response['settings'] = dict(SETTINGS, permanentPassword='must-not-escape')
            if self.settings_error:
                response['settings'] = None
                response['settings_error'] = self.settings_error
        elif request['operation'] == 'settings.set':
            if self.break_on_set:
                self.closed = True
                raise EOFError('synthetic disconnect after dispatch')
            response['settings'] = {'result': 'applied', 'connection_may_close': request['name'] != 'autoUpdates'}
        output = io.BytesIO()
        write_metadata(output, response)
        self.incoming = io.BytesIO(output.getvalue())

    def read(self, size):
        return self.incoming.read(size)

    def close(self):
        self.closed = True


class SettingsSessionTests(unittest.TestCase):
    def open_session(self, stream):
        registry = ScreenSessions(lambda *_: stream)
        result = registry.request('owner', '123456789', 'synthetic', 'monitor', True)
        return registry, result['session_id']

    def test_off_denial_and_view_only_control_gating_do_not_send_settings(self):
        stream = SettingsChannel(settings_error='settings-disabled')
        registry, session = self.open_session(stream)
        stream.can_control = True
        result = registry.operation('owner', session, 'settings.read')
        self.assertEqual(result['settings_error'], 'settings-disabled')
        self.assertEqual([item['operation'] for item in stream.requests], ['request', 'status', 'settings.read'])

        stream.requests.clear()
        stream.can_control = False
        result = registry.operation('owner', session, 'settings.read')
        self.assertEqual(result['settings_error'], 'control-required')
        self.assertEqual([item['operation'] for item in stream.requests], ['status'])

    def test_settings_read_projects_allowlist_and_rejects_unknown_fields(self):
        stream = SettingsChannel()
        registry, session = self.open_session(stream)
        result = registry.operation('owner', session, 'settings.read')
        self.assertEqual(result['settings'], SETTINGS)
        self.assertEqual({'version', 'request_id', 'session_id', 'state', 'can_view', 'can_control', 'remaining_ms', 'settings'}, set(result))
        self.assertNotIn('must-not-escape', json.dumps(result))

    def test_set_requires_exact_name_value_and_returns_applied(self):
        stream = SettingsChannel()
        registry, session = self.open_session(stream)
        result = registry.operation('owner', session, 'settings.set', name='autoUpdates', value=False)
        self.assertEqual(result['result'], 'applied')
        self.assertEqual(stream.requests[-1]['operation'], 'settings.set')
        self.assertEqual(set(stream.requests[-1]), {'version', 'request_id', 'operation', 'session_id', 'name', 'value'})
        for name, value in (('allowRemoteSettings', True), ('permanentPassword', 'secret'), ('lanEnabled', True), ('relayPort', True), ('lanPort', 65536)):
            before = len(stream.requests)
            result = registry.operation('owner', session, 'settings.set', name=name, value=value)
            self.assertIn(result['settings_error'], ('settings-forbidden', 'settings-invalid-value'))
            self.assertEqual(len(stream.requests), before)
        self.assertEqual(registry.operation('owner', session, 'settings.set', name='autoUpdates', value=False, extra='x')['settings_error'], 'settings-forbidden')

    def test_disconnect_after_set_is_unknown_and_never_replayed(self):
        stream = SettingsChannel(break_on_set=True)
        registry, session = self.open_session(stream)
        result = registry.operation('owner', session, 'settings.set', name='relayHost', value='relay.example.test')
        self.assertEqual(result['settings_error'], 'outcome-unknown')
        self.assertEqual([item['operation'] for item in stream.requests].count('settings.set'), 1)
        self.assertTrue(stream.closed)

    def test_server_tools_are_caller_owned_and_audit_only_setting_name(self):
        stream = SettingsChannel()
        registry, session = self.open_session(stream)
        events = []
        with patch.object(server, 'SCREEN', registry), patch.object(server, 'screen_caller', return_value='owner'), patch.object(server, 'append_audit', side_effect=events.append):
            result = server.remsup_settings_set(session, 'autoUpdates', False)
        self.assertEqual(result['result'], 'applied')
        self.assertEqual(events[0]['setting_name'], 'autoUpdates')
        self.assertNotIn('False', json.dumps(events[0]))
        with patch.object(server, 'SCREEN', registry), patch.object(server, 'screen_caller', return_value='other'), patch.object(server, 'append_audit'):
            with self.assertRaises(PermissionError):
                server.remsup_settings_read(session)


if __name__ == '__main__':
    unittest.main()
