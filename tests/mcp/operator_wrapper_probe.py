import json, os, sys
sys.stdin.reconfigure(encoding='utf-8')
sys.stdout.reconfigure(encoding='utf-8')
request = json.loads(sys.stdin.readline())
token = request["token"]
password = request.get("otp", "")
result = {
    "operation": request.get("operation", ""),
    "diagnostics_command_absent": request.get("operation") != "diagnostics" or "command" not in request,
    "argv_clean": bool(password and token) and token not in " ".join(sys.argv) and password not in " ".join(sys.argv),
    "env_clean": bool(password and token) and token not in os.environ.values() and password not in os.environ.values(),
    "stdin_credentials": bool(password and token),
    "port_selection": request['config']['local_port']==(24680 if request.get('command')=='echo explicit-port' else 0),
    "unicode_command": request.get("operation") != "exec" or request.get("command") == "echo \u043f\u0440\u043e\u0432\u0435\u0440\u043a\u0430" or request.get('command')=='echo explicit-port',
}
result["ok"] = all(result.values())
result["stdout"] = password + " " + token
print(json.dumps(result), flush=True)
sys.exit(0 if result["ok"] else 1)
