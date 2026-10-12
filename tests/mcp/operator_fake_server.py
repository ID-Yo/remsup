"""Loopback-only synthetic MCP protocol fixture; never receives real credentials."""
import sys
import json
from mcp.server.fastmcp import FastMCP
mcp=FastMCP('owned-operator-fixture',host='127.0.0.1',port=int(sys.argv[1]))
commands=[]
screen_closes=0
@mcp.tool()
def remsup_health()->dict:return {'ok':True,'relay_host':'127.0.0.1','relay_port':8443,'command_count':len(commands),'screen_closes':screen_closes}
@mcp.tool()
def remsup_exec_command(device_id:str,otp:str,command:str)->dict:
    if otp!='PASSWORD_SENTINEL':raise ValueError('test credential mismatch')
    commands.append(command)
    if command=='fixture-busy':raise ValueError('busy PASSWORD_SENTINEL TOKEN_SENTINEL')
    return {'device_id':device_id,'exit_code':0,'truncated':False,'stdout':'fixture\nfixture\\user' if command=='hostname & whoami' else 'PASSWORD_SENTINEL TOKEN_SENTINEL','stderr':''}
@mcp.tool()
def remsup_screen_capabilities(device_id:str,otp:str)->dict:return {'available':True,'monitors':[{'monitor_id':'synthetic'}]}
@mcp.tool()
def remsup_screen_request(device_id:str,otp:str,monitor_id:str,control:bool)->dict:return {'session_id':'synthetic-session'}
@mcp.tool()
def remsup_screen_status(session_id:str)->dict:return {'state':'Granted','can_view':True}
@mcp.tool()
def remsup_screen_capture(session_id:str)->dict:return {'frame_id':'synthetic-frame','geometry_version':1,'captured_utc':'2026-10-12T00:00:00Z'}
@mcp.tool()
def remsup_screen_text(session_id:str,frame_id:str,geometry_version:int,text:str)->dict:
    if text=='fixture-unknown':raise ValueError('screen-input-outcome-unknown; do not retry automatically')
    raise ValueError('screen-input-refused-before-dispatch: '+json.dumps({'error':'protected-window','dispatched':False,'channel_terminated':False,'private':'PASSWORD_SENTINEL TOKEN_SENTINEL'}))
@mcp.tool()
def remsup_screen_close(session_id:str)->dict:
    global screen_closes
    screen_closes+=1
    return {'result':'already-closed','channel_terminated':True}
if __name__=='__main__':mcp.run(transport='streamable-http')
