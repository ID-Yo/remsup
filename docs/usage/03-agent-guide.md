---
{"document":"usage-agent","status":"active","owner":"ID-Yo","last_reviewed":"2026-10-09"}
---

# 3. Агент: защитено свързване и работа

When the native Remsup MCP connector is absent, use the supported [operator client package](../../skills/remsup-support/references/mcp-client.md). Setup stores a separately provisioned per-agent bearer in a DPAPI CurrentUser file and checks health. Routine health/exec/screen use a restricted non-root SSH alias or verified HTTPS endpoint; they do not require root or read server env. The linked guide gives commands for every case and interactive capture/input/close examples. Credentials enter private stdin from the exact saved profile, the selected Device ID/password and approved route are used for the authorized task, unknown mutations are never retried, and owned screen/tunnel cleanup runs in finally.

## Задължителни входове

Преди връзката получете от собственика конкретната разрешена задача, Device ID, граници на действията, актуално OTP/permanent password и provisioned MCP/SSH конфигурация. Не сканирайте за target и не извличайте credentials от чужди файлове.

Bearer token удостоверява агента пред MCP; device OTP/password се използва за pairing с receiver. Те са отделни credentials. Подавайте ги само през одобрения защитен механизъм. Не копирайте стойности в prompt, TOML, shell history, файлове, logs, memory, Issue или отчет.

## Запазен operator profile

Когато собственикът е възложил профил за конкретното устройство, проверете точно този запис и потвърдения Device ID, преди да искате нова парола. Локалният vault е `%LOCALAPPDATA%\Remsup\Operator\devices-v2.json`; стойността `records[].protectedPassword` е защитена с DPAPI CurrentUser. Само текущият Windows потребител може да я използва. Не decrypt-вайте/печатайте, не копирайте vault между потребители и не четете v1 credentials. Непознат, повреден или недостъпен store не се reset-ва автоматично.

Използвайте [read-only saved-profile helper](../../skills/remsup-support/scripts/saved-profile.ps1) от [remsup-support](../../skills/remsup-support/SKILL.md). По подразбиране показва само метаданни и дали password е налична. `-UseCredential` подава я само на локалния защитен consumer в паметта към MCP SDK call; без plaintext временен файл, argv, decrypt-and-print. Нужен е същият Windows акаунт на операторската машина. Профилът или самото съществуване на vault не доказва remote pairing.

## MCP път през SSH

Текущата candidate топология е:

```text
Local MCP client --approved SSH tunnel--> VPS 127.0.0.1:8765/mcp (Bearer)
MCP process --> same C relay process at VPS loopback:8443
Windows relay client --> public:443 pinned TLS --> stunnel PROXY --> relay loopback:8445
```

1. Използвайте вече provisioned Remsup connector. При липса, и само с разрешен SSH/host-key достъп до VPS, отворете foreground тунела от [README.md](README.md). Настройте локалния MCP client към `http://127.0.0.1:18765/mcp` и bearer token чрез protected env/secret integration. Не публикувайте порт 8765. Cloud агент не достига локалния tunnel; нужен е отделен provisioned route.
2. Инициализирайте MCP и открийте действителните `remsup_health` и `remsup_exec_command` tool names (connector prefix може да се различава). Health показва конфигурация и limits; не свързва устройство и не доказва pairing.
3. Извикайте `remsup_health({})`. Потвърдете одобрения relay host/port и `command_timeout_seconds`/`max_output_bytes`. Не споделяйте суров health output, ако съдържа endpoint или client identity.
4. За възложената ограничена команда използвайте `remsup_exec_command` през `remsup_exec_command` със схема:

   ```json
   {
     "device_id": "<разрешеният Device ID>",
     "otp": "<защитен източник на OTP/permanent password>",
     "command": "ver"
   }
   ```

   Placeholders не са реални credentials. Подавайте OTP само по одобрения protected tool/session path; не включвайте истинска стойност в prompt или отчет.
5. Достъпът използва предоставените от собственика Device ID и парола; не изисква hostname или logged-in user. Проверете резултата на възложеното действие. При timeout/disconnect резултатът може да е неизвестен — не повтаряйте автоматично.

Tool връща `device_id`, `exit_code`, `stdout`, `stderr`, `duration_ms`, `truncated`. Няма per-call override на лимитите: MCP конфигурацията по подразбиране е 30 секунди и 262144 bytes. При truncation стеснете диагностиката. Timeout/disconnect може да остави remote outcome unknown; първо проверете дали действието е завършило, без автоматичен retry.

### Screen tools

MCP screen tools използват отделен `REMSUP_SCREEN_TEST_MODE=1` server gate, който е off по подразбиране. Не го променяйте и не приемайте, че command health включва screen достъп. Когато owner и администратор изрично са разрешили controlled test и capabilities заявяват наличност:

- `remsup_screen_request` иска `control=false` за view или `control=true` за разрешени keyboard/mouse control; receiving Security view/control policies се прилагат.
- Owner setting `Automatically approve paired screen sessions` default on прескача отделния prompt след pairing в рамките на allow settings. Използвайте unattended approval само с изрично owner разрешение. Иначе собственикът одобрява видимата native заявка.
- `remsup_screen_capture` връща PNG и frame metadata. Проверете frame/geometry преди действие. Mouse coordinates са спрямо image pixels.
- `remsup_screen_text` приема до 128 UTF-16 units. `remsup_screen_key` допуска само: `ctrl+l`, `ctrl+a`, `ctrl+s`, `ctrl+shift+s`, `enter`, `tab`, `shift+tab`, `escape`, `backspace`, `delete`, `up`, `down`, `left`, `right`, `home`, `end`. Няма clipboard shortcut или file transfer.
- Всяка input операция трябва да използва текущия frame и geometry. След действие или неясен резултат не повтаряйте input; capture и проверете екрана. Използвайте `remsup_screen_close` за собствената screen сесия и спазвайте owner-видимото Disconnect/expiry.

Screen изображения и контрол стигат до relay; TLS не скрива данните от relay администратора. Phase 2 end-to-end secrecy не е реализирана.

## Отделна CLI опция

Windows CLI операторът може да свързва директно към pinned TLS relay на `HOST:443` (custom endpoint изисква `--pin`). Този fallback не използва MCP bearer token, но `--otp` стойността се вижда в process command line. Предпочитайте защитения MCP path ако credentials не могат безопасно да се предадат като argv. Текущото `RemsupDesktop_v1.25.exe` не означава, че в този пакет има отделен `RemsupOperator_v1.25.exe`; използвайте само одобрен, проверен CLI build ако изрично е доставен.

## Разрешена работа

Всяка команда е отделен `cmd.exe /c` процес; няма persistent shell, default elevation, SSH account, upload/download или command allowlist. `cd` и process variables не се запазват между извиквания. Използвайте само границите на текущото owner разрешение.

| Работа | Условие |
|---|---|
| Hostname/OS/диск/RAM и task-specific service | Изрична диагностична задача; събирайте минимум данни |
| Конкретен config/log | Разрешение за този файл; изключете credentials и лични данни |
| Repair/install/restart | Само ако е част от възложената поддръжка; проверете резултата |
| Reboot, security/password промяна или deployment | Изисква изрично покритие; не следва от „диагностицирай“ |
| Stealth, credential theft, bypass, escalation tricks, destructive actions | Забранени |

Ограничените примери според задачата:

```text
ver
ver
powershell -NoProfile -Command "Get-PSDrive -PSProvider FileSystem | Select-Object Name,Used,Free"
```

## Завършване

Сверете реалния резултат, exit code, stderr и truncation. Отчетете само нужното и без credentials. Затворете собствения foreground SSH tunnel с Ctrl+C; не спирайте owner-managed service/client. Използвайте MCP token revocation само чрез администратора и само при разрешен процес; премахване на локален connector не отменя server token. Ако owner иска да спре receiving, насочете го към tray `Exit`. Проверете останали само вашите task-owned процеси/tunnels.


## Директен LAN TLS — кандидат v1.39

Сверете публичния SPKI SHA256 отпечатък от Settings > Network на приемащата страна и го въведете в LAN профила на оператора. Не приемайте pin от непроверена първа връзка. Липсващ или променен pin отказва COMMAND и SCREEN преди изпращане на парола; няма plaintext fallback. Ключът остава локално, защитен с DPAPI. Private мрежата, local-subnet проверката и видимият scoped-firewall/UAC процес остават задължителни.
