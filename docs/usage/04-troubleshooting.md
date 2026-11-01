---
{"document":"usage-troubleshooting","status":"active","owner":"ID-Yo","last_reviewed":"2026-10-09"}
---

# 4. Проблеми и ограничения

| Симптом | Проверка |
|---|---|
| Remsup не отваря основния UI | Уверете се, че одобреният WebView2 Evergreen Runtime е инсталиран. Remsup не го инсталира автоматично; използвайте само одобрената Microsoft/организационна дистрибуция. |
| `Ready` не се показва | `Ready` изисква действителна relay регистрация. Проверете показаната грешка, Network host/port и точния custom pin с администратора. Не тълкувайте `Local only` или конфигурация като доказателство за relay. |
| TLS handshake/pin mismatch | Проверете одобрения endpoint, TLS 1.2+, системния часовник и custom SPKI pin чрез администратора. Не сменяйте pin на случаен, не приемайте произволен сертификат и не правете fallback към plaintext 8443. |
| `ERR no-agent` | Няма чакащ receiver с този Device ID/OTP. Owner проверява Device ID, текущата OTP/permanent password и видимото `Ready`; не повтаряйте догадки. |
| MCP `401` / `403` | Администраторът проверява bearer token, URL и scopes в защитената конфигурация. Не премахвайте auth и не печатайте token. |
| MCP connection refused | Проверете разрешения foreground SSH tunnel, локалния порт и loopback service с администратора. `127.0.0.1` на operator машината означава tunnel вход, не автоматично VPS. Не публикувайте 8765. |
| `screen-test-mode-disabled` | MCP screen tools са изключени, освен ако администраторът отделно не е разрешил server gate `REMSUP_SCREEN_TEST_MODE=1`. Health/command достъп не го включва. Не променяйте gate без отделно разрешение. |
| Custom relay refuses connection | Host, port и SPKI pin трябва да съвпадат с одобрените стойности. Клиентът изисква валиден pin; няма TOFU/accept-all. Нов TLS сертификат с друг ключ изисква нова изрично доверена конфигурация. |
| LAN не се свързва | Най-честата причина е, че мрежата в Windows е **Public**. LAN работи само при **Private** мрежа: собственикът я сменя от Windows Settings (вижте LAN изисква Private мрежа в Windows Settings). Проверете още активния Private IPv4, scoped firewall правилото (Prepare LAN firewall) и еднакъв LAN port. Не включвайте LAN за заобикаляне на relay/TLS проблем. |
| „LAN requires an active Private IPv4 and this app's prepared rule“, Stopped/Unavailable или Save остава „staged“ | Мрежата е Public, firewall правилото не е подготвено или запазеният IP адрес вече не е активен (рутерът го е сменил). Сменете мрежата на Private, натиснете Prepare LAN firewall, Refresh addresses и изберете активния адрес, после Save. Ако LAN не е нужен, изключете Allow support on a Private network: relay продължава да работи. До поправката на #61 неуспешен LAN може да спре и relay поддръжката. |
| Update deferred | Има активен command или screen session; кандидатът се оставя за следващ idle опит. Завършете/затворете разрешената сесия и оставете приложението работещо. Не заменяйте EXE ръчно по време на работа. |
| Update rollback/recovery failure | При неготов нов кандидат updater опитва да възстанови стария EXE и registrations. При съобщена recovery failure спрете нови действия и запазете текущото състояние за администратор; не изтривайте update backup/папката. |
| Команда timeout, disconnect или `truncated` | Изходът може да е непълен или remote резултатът неизвестен. Проверете целта и ефекта преди повторение; стеснете заявката, ако е отрязана. Не изпращайте паралелни команди към зает receiver. |
| Screen input отказан/неясен | Capture и проверете актуалния foreground desktop и frame/geometry. Не повтаряйте input автоматично. Контролът използва видимия foreground application; екранният preview не доказва, че текстът е приет. |

## Защита, потвърдена от source и оставащ риск

- Windows relay source изисква TLS 1.2+ и SPKI SHA-256 pin преди registration/credentials. Custom relay без изричен валиден pin отказва; няма insecure fallback.
- Pinned TLS криптира Windows-to-relay network traffic. Relay администраторът може да прочете device credentials, команди/results и screen трафик. Това не е end-to-end secrecy.
- MCP listener е bearer-authenticated и loopback-only на VPS; операторският път е през authorized SSH. Не излагайте `127.0.0.1:8765` през firewall/public proxy.
- Изричният direct LAN режим използва TLS с проверен SPKI pin (от v1.39) и работи само при Windows мрежа тип Private. Използвайте го само по изрично разрешение на доверена локална мрежа.
- `REMSUP_SCREEN_TEST_MODE=1` е отделен MCP screen test gate; screen сесиите имат свои права, видим local approval policy, Disconnect и expiry. Тези свойства не превръщат транспорта в production-ready.
- Phase 2 relay tokens, enrollment, общ audit/revocation, end-to-end secrecy и силна credential/session схема остават незавършени.
- TLS QA status отбелязва public443 и update endpoint проверени, но двукомпютърният cutover остава unverified и старият публичен plaintext listener е бил оставен по време на прехода. Тези инструкции не потвърждават live cutover или release публикация.

## Безопасно събиране на доказателства

Запишете версията, времето, слоя и кратко редактирано съобщение. Сурови settings, vault, runtime, env или client/service logs могат да съдържат credentials и лични данни; не ги копирайте. Наличен Git source или MCP health не доказва pairing/достъп на клиентско устройство. Не променяйте relay, SSH, firewall, token, password, pin или server config само за да „тръгне“; конкретното разрешение трябва да включва такава промяна.

След задача затворете само собствените processes/tunnels. Не прекратявайте owner-managed Remsup, relay, stunnel или MCP service.

При отказ на update проверете свободното място на системния диск: EXE се сваля в LocalAppData, а native кешът използва TEMP. При недостиг старата версия остава; освободете само разрешени данни и използвайте Check for updates. Не променяйте pin и не включвайте plaintext fallback.
