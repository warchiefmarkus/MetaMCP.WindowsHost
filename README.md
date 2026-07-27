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
    └── Release\
        ├── win-x64\
        ├── linux-x64\
        ├── linux-x64.tar.gz
        ├── linux-arm64\
        └── linux-arm64.tar.gz
```

- `MetaMCP.Host.Core` — конфіг, runtime controller, health checks і reverse SSH.
- `MetaMCP.Host.Windows` — tray UI, Windows Service, named pipe і Job Object.
- `MetaMCP.Host.Linux` — консольний host для systemd без GUI.
- `MetaMCP.Packager` — пакети `win-x64`, `linux-x64` та `linux-arm64`.

Windows assembly і executable збережені як `MetaMCP` / `MetaMCP.exe` для сумісності.
Linux executable має назву `metamcp-host`.
## Платформні пакети

```powershell
dotnet run --project .\src\MetaMCP.Packager -c Release -- `
  --repo C:\DEV\LLM\metamcp `
  --target win-x64 `
  --output Release\win-x64
```

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
Release/
├── win-x64/
├── linux-x64/
├── linux-x64.tar.gz
├── linux-arm64/
└── linux-arm64.tar.gz
```

Окремі target-и пишуть у відповідний підкаталог. `--target all` використовує корінь `Release`.

Для повторної збірки можна додати `--skip-install`.
`--target all` виконує production build MetaMCP один раз і створює всі три пакети.
## VS Code tasks

У `.vscode/tasks.json` є:

```text
Package: Select target
Package: Windows x64
Package: Linux x64
Package: Linux ARM64
Package: All platforms
Build: MetaMCP.Host.Windows (Release)
Build: MetaMCP.Host.Linux (Release)
```

`Package: Select target` пропонує `win-x64`, `linux-x64`, `linux-arm64` або `all`.

## Windows host

Windows host запускається як portable tray application або Windows Service.

```text
Release\win-x64\MetaMCP.exe
```

Tray дозволяє:

- запускати, зупиняти й перезапускати runtime;
- встановлювати або видаляти Windows Service;
- перемикати активний reverse SSH mapping без restart frontend/backend;
- відкривати конфіг і локальний UI.
У portable mode дочірні Node-процеси входять у Windows Job Object.
У service mode runtime належить службі, а tray працює як локальний клієнт через named pipe.

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

SSH.NET читає alias з користувацького `~/.ssh/config`. Для service deployment
можна зберегти розв’язані `HostName`, `User`, `Port`, `PrivateKeyPath` і fingerprint
безпосередньо в `host.json`.

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
