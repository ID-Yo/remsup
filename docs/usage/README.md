---
{"document":"usage-index","status":"active","owner":"ID-Yo","last_reviewed":"2026-10-09"}
---

# Remsup — инструкции за употреба

Тази папка е оперативният вход за v1.25 candidate: стартиране, видими контроли, връзка на агент и диагностика. Прочетете четирите номерирани ръководства; историческите handoff-и и старият SSH прототип не описват текущия продукт.

| Файл | Съдържание |
|---|---|
| [AGENTS.md](AGENTS.md) | Задължително четене и граници за агент |
| [03-agent-guide.md](03-agent-guide.md) | Изпълним файл, първо стартиране и транспортна схема |
| [04-troubleshooting.md](04-troubleshooting.md) | Контроли, настройки, екранни сесии и спиране |
| [03-agent-guide.md](03-agent-guide.md) | Защитен MCP/SSH път, запазен профил и разрешена работа |
| [04-troubleshooting.md](04-troubleshooting.md) | Състояния, TLS, pairing и ограничения |

## Codex skill

Skill-ът [remsup-support](../../skills/remsup-support/SKILL.md) насочва към тези инструкции и включва [read-only helper](../../skills/remsup-support/scripts/saved-profile.ps1) за избор на точно запазен профил. Skill-ът не съдържа credentials и не настройва MCP. Преди връзка са нужни разрешена задача, потвърден target и защитен достъп от собственика.

Пример за задача след предоставяне на тези входове:

```text
$remsup-support Провери идентичността на посоченото устройство.
Изпълни само ver и отчети резултата без промени.
```

За нов агент подайте checkout пътя към `docs/usage` и кажете да прочете `README.md`, `AGENTS.md` и четирите ръководства. Попълвайте Device ID, endpoint и protected credential source само за конкретната сесия; не записвайте реални стойности в документация, prompt или logs.

## Версия и текуща граница

v1.25 source/build candidate е за един самостоятелен `RemsupDesktop_v1.25.exe` за Windows x64. Наличието на source/configuration не доказва публикуван release или завършен двукомпютърен cutover. Вижте TLS документацията и документацията за сигурност преди да правите твърдение за live среда.

Windows relay пътят използва pinned TLS на порт 443; update endpoint-ът е pinned на 8444. На VPS stunnel приема публичния TLS relay и препраща към loopback:8445. Същият C relay процес предоставя отделен loopback:8443 за MCP. MCP остава bearer-защитен на `127.0.0.1:8765` и се достига по разрешен SSH тунел; порт 8765 не се публикува. TLS криптира мрежовия път до relay, но relay администраторът може да прочете credentials и съдържанието. LAN е изрично отделен plaintext маршрут. End-to-end secrecy, relay tokens, общ relay audit/revocation и enrollment са оставаща Phase 2 работа.

Това не е одобрение за production cutover. QA отбелязва, че двукомпютърната TLS миграция още не е завършена и старият публичен plaintext listener е останал отворен по време на прехода. Не заобикаляйте TLS грешка с plaintext порт.
