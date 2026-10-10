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
    ├── MetaMCP.exe               # stable Windows bootstrapper/gateway
    ├── config\                   # stable Windows config
    ├── data\                     # stable Windows state
    ├── ReleaseA\win-x64\         # runtime payload A
    ├── ReleaseB\win-x64\         # runtime payload B
    ├── Release\                 # Linux packages only
    │   ├── linux-x64\
    │   ├── linux-x64.tar.gz
    │   ├── linux-arm64\
    │   └── linux-arm64.tar.gz
    └── current.json
```

- `MetaMCP.Host.Core` — конфіг, runtime controller, health checks і reverse SSH.
- `MetaMCP.Host.Windows` — portable tray UI, `RuntimeController` і Windows Job Object.
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
MetaMCP.exe           # stable Windows bootstrapper/gateway
ReleaseA\win-x64\   # runtime payload A
ReleaseB\win-x64\   # runtime payload B
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
Deploy: Windows runtime hot-swap
Package: Linux x64
Package: Linux ARM64
Package: All platforms
Build: MetaMCP.Host.Windows (Release)
Build: MetaMCP.Host.Linux (Release)
```

`Package: Select target` пропонує `win-x64`, `linux-x64`, `linux-arm64` або `all`.

### Windows bootstrapper + A/B runtime slots

Windows має стабільний control/data-plane gateway у root `MetaMCP.exe`. Він не є частиною `ReleaseA/B` і не перезапускається під час звичайного runtime deploy.

```text
MetaMCP.exe                 # стабільний tray + HTTP/MCP gateway
config\                     # спільний host/runtime config
data\                       # спільний runtime state
current.json
pending-update.json         # тільки між build і swap
ReleaseA\win-x64\           # runtime payload A
ReleaseB\win-x64\           # runtime payload B
```

Публічні порти завжди належать bootstrapper-у:

```text
frontend gateway  127.0.0.1:12008
backend/MCP       127.0.0.1:12009

Runtime A: frontend 12108, backend 12109
Runtime B: frontend 12208, backend 12209
```

`Package: Windows x64` / `scripts\Build-WindowsCandidate.ps1` збирає лише неактивний payload slot. `Deploy: Windows runtime hot-swap` / `scripts\Update-WindowsAB.ps1` після build викликає локальний token-protected control endpoint bootstrapper-а і переключає runtime без restart `MetaMCP.exe`.

Hot-swap:

```text
A active
→ build B
→ start B on 12208/12209
→ health B
→ gateway: new sessions → B
→ existing A mcp-session-id → A
→ A drain (sessions=0 && in-flight=0)
→ stop A
→ current.json = B
```

MCP session не переноситься між process generations посеред protocol state. Gateway запам'ятовує `mcp-session-id` і тримає стару сесію на runtime, де вона була створена. Тому tool call, який ініціював swap, може коректно завершитися через старий runtime; нові sessions уже йдуть у новий slot. Якщо клієнт тримає стару session відкритою, старий slot лишається `draining` і навмисно не перезаписується наступним build.

Локальне керування:

```powershell
.\scripts\Swap-WindowsRuntime.ps1 -Status
.\scripts\Swap-WindowsRuntime.ps1
.\scripts\Swap-WindowsRuntime.ps1 -Slot B
```

Control routes доступні тільки з loopback і вимагають `HostControlToken` із `config\host.json`:

```text
GET  /__host/runtime/status
POST /__host/runtime/swap
```

`Install-WindowsBootstrapper.ps1` потрібен лише коли оновлюється сам bootstrapper/Host. Це окрема операція, яка замінює root `MetaMCP.exe` і тому коротко перезапускає gateway. Звичайні зміни MetaMCP backend/frontend/runtime використовують live A/B swap без restart bootstrapper-а.

## Windows host

Windows Reverse SSH використовує окремий системний `ssh.exe` (Windows OpenSSH Client) замість in-process SSH.NET forwarding. Це ізолює фатальні винятки SSH-потоків від `MetaMCP.exe`. Процес прив'язаний до Windows Job Object (`KILL_ON_JOB_CLOSE`), має `ExitOnForwardFailure`, `ServerAliveInterval`, пробу HTTP через VPS і перепідключення за `ReverseSsh.ReconnectDelaySeconds`. Windows OpenSSH вимагає налаштовану SSH key authentication та перевірений host key у `known_hosts` (не вимикайте `StrictHostKeyChecking`).

Tray menu `Reconnect Reverse SSH` перезапускає **лише SSH-тунель** без примусового видалення віддалених `sshd`; `Connection diagnostics...` порівнює direct/backend gateway/frontend HTTP health та виводить причину SSH-збою, час останнього з'єднання і stderr. Діагностика доступна навіть коли backend недоступний.

Windows host запускається тільки як portable tray/bootstrapper application. Він володіє стабільними public ports, reverse SSH tunnel routing і A/B runtime lifecycle.

Tray дозволяє:

- запускати, зупиняти й перезапускати активний runtime;
- виконувати `Reset MCP connections`;
- перемикати активний reverse SSH mapping;
- показувати MCP connections без проміжних mode-груп (`dc [session]`, `JAD-X [persistent]`, `... [idle]`) з деталями у submenu, а також client sessions, CPU/RAM та MCP badge;
- показувати версію bootstrapper-а й активний runtime slot;
- відкривати конфіг і локальний UI.

Backend/frontend кожного payload запускаються в Windows Job Object з `KILL_ON_JOB_CLOSE`. Під час hot-swap старий runtime не вбивається, поки gateway бачить прив'язані до нього MCP sessions або in-flight requests; після drain його process tree прибирається автоматично.
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

Host і production packager використовують один парсер `.env.local`. Він підтримує
посилання виду `${VAR}` на значення з цього файлу (зокрема визначені нижче рядком),
а також на змінні середовища процесу. Приклад для нативного PostgreSQL:

```dotenv
POSTGRES_HOST=127.0.0.1
POSTGRES_PORT=5432
POSTGRES_USER=metamcp_user
POSTGRES_PASSWORD=replace-with-private-password
POSTGRES_DB=metamcp_db
DATABASE_URL=postgresql://${POSTGRES_USER}:${POSTGRES_PASSWORD}@${POSTGRES_HOST}:${POSTGRES_PORT}/${POSTGRES_DB}
```

Парсер прибирає кінцеві коментарі після пробілу (`FLAG=true # comment`),
але зберігає `#` усередині значень. Для невизначених і циклічних
`${VAR}` виводиться явна помилка. Реальні паролі зберігай лише у локальному
`.env.local` (він ігнорується Git), а не в репозиторії.

Регресійні перевірки: `dotnet run --project .\\tests\\MetaMCP.EnvFile.Tests -c Release`.


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
