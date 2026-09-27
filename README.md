# MetaMCP Host

Кросплатформний host і production packager для локального репозиторію MetaMCP.

## Структура

```text
C:\DEV\LLM\
├── metamcp\
└── MetaMCP.WindowsHost\
    ├── src\MetaMCP.Host.Core\
    ├── src\MetaMCP.Host.Windows\
    ├── src\MetaMCP.Host.Linux\
    ├── src\MetaMCP.Packager\
    ├── scripts\
    ├── ReleaseA\win-x64\
    ├── ReleaseB\win-x64\
    ├── Release\                 # Linux packages only
    │   ├── linux-x64\
    │   ├── linux-x64.tar.gz
    │   ├── linux-arm64\
    │   └── linux-arm64.tar.gz
    └── current.json
```

- `MetaMCP.Host.Core` — конфіг, runtime controller, health checks і reverse SSH.
- `MetaMCP.Host.Windows` — tray UI, Windows Service, named pipe і Job Object.
- `MetaMCP.Host.Linux` — консольний host для systemd без GUI.
- `MetaMCP.Packager` — пакети `win-x64`, `linux-x64` та `linux-arm64`.

Windows assembly і executable збережені як `MetaMCP` / `MetaMCP.exe` для сумісності.
Linux executable має назву `metamcp-host`.
## Платформні пакети

Windows x64 збирається тільки через A/B candidate builder:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-WindowsCandidate.ps1
```

Скрипт сам визначає неактивний `ReleaseA`/`ReleaseB` slot і не перезаписує live runtime.

Доступні target-и:

```text
win-x64
linux-x64
linux-arm64
all
```

Linux x64:

```powershell
dotnet run --project .\src\MetaMCP.Packager -c Release -- `
  --repo C:\DEV\LLM\metamcp `
  --target linux-x64 `
  --output Release\linux-x64
```

Фінальний layout:

```text
ReleaseA\win-x64\   # один Windows slot
ReleaseB\win-x64\   # другий Windows slot
Release\
├── linux-x64\
├── linux-x64.tar.gz
├── linux-arm64\
└── linux-arm64.tar.gz
```

Windows package містить фізичний `node_modules` без junction/symbolic links, тому його можна переносити звичайним копіюванням або архівом. Packager окремо перевіряє наявність `pg`, `pg-types`, PostgreSQL parser dependencies і запуск database runtime modules.

`Package: All platforms` зберігає Windows у A/B схемі, а Linux artifacts — у `Release`.
## VS Code tasks

У `.vscode/tasks.json` є:

```text
Package: Select target
Package: Windows x64
Deploy: Windows A/B safe switch
Package: Linux x64
Package: Linux ARM64
Package: All platforms
Build: MetaMCP.Host.Windows (Release)
Build: MetaMCP.Host.Linux (Release)
```

`Package: Select target` пропонує `win-x64`, `linux-x64`, `linux-arm64` або `all`.

### Windows A/B release slots

Windows deploy використовує два стабільні слоти та один runtime state-файл:

```text
ReleaseA\win-x64\
ReleaseB\win-x64\
current.json
pending-update.json   # існує лише між build і cutover
```

`current.json` є єдиним джерелом істини для активного слота. `Package: Windows x64` лише збирає **неактивний** слот через `scripts/Build-WindowsCandidate.ps1` і не перериває запущений MetaMCP. `Deploy: Windows A/B safe switch` запускає `scripts/Update-WindowsAB.ps1`: після успішної збірки він створює відокремлений `cmd.exe` з `timeout`, який уже поза поточним MetaMCP tool-call виконує `Switch-WindowsSlot.ps1`.

Cutover має такий порядок:

```text
active A → build B → validate → delayed cmd → stop A → start B → health OK → atomic current.json=B
active B → build A → validate → delayed cmd → stop B → start A → health OK → atomic current.json=A
```

Якщо candidate не проходить backend/frontend health-check, switch-скрипт завершує candidate і запускає попередній A/B slot.

Під час build у candidate переносяться runtime `config` і `data`; `host.json` merge-иться поверх нових default-полів.

## Windows host

Windows host запускається як portable tray application або Windows Service.

```text
current.json → ReleaseA\win-x64\MetaMCP.exe
             або ReleaseB\win-x64\MetaMCP.exe
```

Tray дозволяє:

- запускати, зупиняти й перезапускати runtime;
- виконувати `Reset MCP connections`: закривати всі downstream MCP connections/process trees без зупинки backend, frontend і SSH tunnel; якщо backend не відповідає, Host пропонує повний restart runtime;
- встановлювати або видаляти Windows Service;
- перемикати активний reverse SSH mapping без restart frontend/backend;
- показувати компактне дерево `Connections: N | Sessions: M`: без проміжних `persistent/session/idle` і `Client sessions` меню; один сервер одразу містить PID, transport, короткий session ID, active request count та idle time, а кілька однакових серверів групуються як `dc [session] ×8`; client sessions без downstream connection показуються окремим leaf `Session … [no MCP]`;
- показувати у верхньому рядку tray-меню агреговані метрики у форматі `MCP 3 | CPU 4,2% | RAM 386 MB`; `MCP` — кількість поточних `MetaMCP → MCP connections`;
- показувати в нативному tooltip при наведенні на tray icon ті самі CPU, Working Set RAM і кількість MCP connections;
- показувати у правому верхньому куті tray icon червоний badge з кількістю поточних `MetaMCP → MCP connections`; при `0` badge не відображається, значення понад `99` показується як `99+`;
- відкривати конфіг і локальний UI.
У portable mode backend і frontend входять у Windows Job Object з `KILL_ON_JOB_CLOSE`; `Stop`, `Restart`, `Exit` і аварійне завершення Host прибирають їхні дочірні MCP process trees. STDIO transport додатково виконує `taskkill /T /F` під час штатного закриття connection. У service mode використовується той самий `RuntimeController`, а tray працює як локальний клієнт через named pipe.

Ручне завершення всіх `node.exe` не рекомендується: разом із MCP servers воно вбиває MetaMCP backend і frontend. У такому стані tray показує `MCP: backend unavailable` або зберігає останні дані як `MCP telemetry delayed`.

## Linux host

Linux пакет містить self-contained .NET executable, MetaMCP frontend/backend,
вбудований Node.js runtime, конфіг і systemd deployment files.

Ручний запуск:

```bash
./metamcp-host --base /opt/metamcp
```

Вибір mapping без зміни `host.json`:

```bash
./metamcp-host --base /opt/metamcp --mapping proxmox
```

Пріоритет вибору mapping:

```text
--mapping
METAMCP_MAPPING
ReverseSsh.ActiveMapping у config/host.json
```

Host обробляє `SIGINT`/`SIGTERM`, пише статус у stdout/journald і коректно
зупиняє backend, frontend та SSH tunnel.
## Systemd installation

Після розпакування Linux archive:

```bash
cd linux-x64
./deploy/install-systemd.sh /opt/metamcp
```

Скрипт:

- копіює пакет у `/opt/metamcp`;
- встановлює executable permissions;
- створює `/etc/systemd/system/metamcp-host.service`;
- виконує `daemon-reload`;
- вмикає та запускає service.

Перевірка:

```bash
systemctl status metamcp-host --no-pager
journalctl -u metamcp-host -f
curl http://127.0.0.1:12009/health
curl -I http://127.0.0.1:12008
```

Systemd використовує `KillMode=control-group`, тому при зупинці service
прибираються host, frontend, backend і дочірні MCP-процеси.
## Конфігурація

Основні файли пакета:

```text
config/host.json
config/.env.local
```

Інтервал оновлення hover-статистики MCP-процесів задається в секундах і застосовується після перезапуску Host:

```json
"McpMetricsRefreshSeconds": 5,
"McpTelemetryTimeoutMilliseconds": 5000
```

Інтервал нормалізується до діапазону `1–3600`, а timeout telemetry — до `1000–30000` мс. Control token для локального endpoint генерується автоматично у `HostControlToken`; його не потрібно задавати вручну. CPU обчислюється між двома послідовними замірами та нормалізується до загальної потужності всіх логічних процесорів; RAM — сума Working Set локальних downstream MCP PID.

Приклад reverse SSH mappings:

```json
"ReverseSsh": {
  "Enabled": true,
  "Host": "oracle_freevps2arm",
  "ActiveMapping": "legion",
  "Mappings": [
    {
      "Id": "legion",
      "DisplayName": "Legion PC",
      "PublicPath": "/metamcp",
      "RemoteBindHost": "127.0.0.1",
      "RemotePort": 18080,
      "LocalHost": "127.0.0.1",
      "LocalPort": 12008
    },
    {
      "Id": "proxmox",
      "DisplayName": "Proxmox",
      "PublicPath": "/metamcppct",
      "RemoteBindHost": "127.0.0.1",
      "RemotePort": 18081,
      "LocalHost": "127.0.0.1",
      "LocalPort": 12008
    }
  ]
}
```
Для кожного ПК або сервера використовується унікальний VPS `RemotePort`.
Mapping `proxmox` використовує VPS `18081` і nginx path `/metamcppct`; на Windows його не слід обирати.
Mapping `yoga` використовує VPS `18083` і nginx path `/metamcpyoga`; це Windows-профіль для Yoga.

SSH.NET читає alias з користувацького `~/.ssh/config`. Для service deployment
можна зберегти розв’язані `HostName`, `User`, `Port`, `PrivateKeyPath` і fingerprint
безпосередньо в `host.json`.

Reverse SSH додатково виконує періодичну контрольну SSH-команду. Якщо TCP-сесія
залишилася формально `Established`, але перестала відповідати, probe завершується
за таймаутом, тунель закривається і host автоматично підключається знову.

Старий конфіг з одиночними `RemotePort`/`LocalPort` автоматично мігрується
до іменованого mapping-профілю.

## Формат Linux archive

```text
linux-x64/
├── metamcp-host
├── metamcp/
│   ├── backend/
│   └── frontend/
├── runtime/node/
├── config/
├── data/
├── deploy/
│   ├── metamcp-host.service
│   └── install-systemd.sh
└── build-manifest.json
```

Packager зберігає відносні pnpm symlink-и й створює стандартний PAX `tar.gz`,
який коректно розпаковується GNU tar на Linux.
## Перевірений стан

Linux x64 пакет перевірений на Proxmox:

```text
backend health: HTTP 200
frontend: HTTP 200
PostgreSQL: online
reverse SSH: online
SSH reconnect: успішний без restart frontend/backend
systemd shutdown: усі процеси й tunnel прибрані
broken symlinks: 0
```

Linux ARM64 пакет перевіряється статично як ELF AArch64 разом із вбудованим
AArch64 Node.js runtime. Для фактичного smoke-test потрібен ARM64 Linux host.

## Важливо

- Не запускай повторне пакування в output, з якого зараз працює host.
- Усі platform packages зберігаються в єдиному каталозі `Release` у власних підкаталогах.
- Не зберігай реальні API keys, SSH private keys або паролі в Git.
- `Release*`, staging, runtime cache та build logs виключені з Git.
- `LoggingEnabled: false` вимикає файлові runtime-логи, але Linux status лишається в journald/stdout.
