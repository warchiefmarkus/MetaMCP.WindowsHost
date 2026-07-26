# MetaMCP Windows Host

Окремий Windows-host і production packager для репозиторію MetaMCP.

## Структура

```text
C:\DEV\LLM\
├── metamcp\                 # вихідний репозиторій
└── MetaMCP.WindowsHost\     # C# host + packager
    ├── src\MetaMCP.Host\
    ├── src\MetaMCP.Packager\
    └── Release\             # готовий автономний runtime
```

`Release` не потребує глобального Node.js, pnpm, вихідного репозиторію або runtime-збірки.
Frontend, backend і production dependencies готуються лише packager-ом.

## Створення Release

```powershell
dotnet run --project .\src\MetaMCP.Packager -c Release -- `
  --repo C:\DEV\LLM\metamcp
```

Повторне пакування без `pnpm install`:

```powershell
dotnet run --project .\src\MetaMCP.Packager -c Release -- `
  --repo C:\DEV\LLM\metamcp --skip-install
```

Packager виконує production build, deploy залежностей, self-contained publish EXE,
валідацію runtime-файлів і реальний smoke-test на портах `12018/12019`.

## Готовий runtime

```text
Release\
├── MetaMCP.exe
├── metamcp\
│   ├── backend\
│   └── frontend\
├── runtime\node\
├── config\
│   ├── .env.local
│   └── host.json
├── data\
└── build-manifest.json
```

Звичайний запуск:

```text
Release\MetaMCP.exe
```

У portable-режимі tray-процес сам володіє frontend, backend і reverse SSH tunnel.
`Exit` зупиняє весь runtime. Дочірні Node-процеси входять у Windows Job Object,
тому Windows прибирає їх навіть після аварійного завершення host-а.

## Windows Service

Tray-меню містить:

```text
Install Windows Service
Uninstall Windows Service
```

Встановлення потребує UAC один раз і автоматично:

- реєструє `MetaMCP.WindowsHost` як delayed-auto Windows service;
- запускає службу;
- додає `MetaMCP.exe --tray` після входу користувача;
- зберігає розв’язані параметри OpenSSH alias у `config\host.json`.

Служба володіє frontend, backend і tunnel. Tray лише показує статус та передає
`Start`, `Stop`, `Restart` через локальний named pipe. `Exit` у service-режимі
закриває лише tray, а служба продовжує працювати.

## Reverse SSH profiles

Tunnel реалізований у C# через SSH.NET; окремий `ssh.exe` не запускається.
За замовчуванням читається alias `oracle_freevps2arm` із `%USERPROFILE%\.ssh\config`.
Для service-режиму alias розв’язується під час встановлення служби.

`config\host.json` містить іменовані mapping-профілі. У tray-меню є вкладене меню
`Tunnel: ...`, де активний профіль можна змінити без перезапуску frontend/backend:
перезапускається тільки reverse SSH tunnel, а вибір зберігається в `host.json`.

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
      "Id": "thinkpad",
      "DisplayName": "ThinkPad",
      "PublicPath": "/metamcpthp",
      "RemoteBindHost": "127.0.0.1",
      "RemotePort": 18082,
      "LocalHost": "127.0.0.1",
      "LocalPort": 12008
    }
  ]
}
```

VPS port `18081` і public path `/metamcppct` зарезервовані за Proxmox і навмисно
не входять у Windows-профілі. Для нового ПК додається новий mapping з унікальним
`Id`, public path і VPS remote port, після чого аналогічний location додається в nginx.

Старий конфіг з одиночними `RemotePort`/`LocalPort` автоматично мігрується до профілів.

## Важливо

- Не переміщуй готову `Release` вручну: pnpm production deploy містить junction-и,
  створені одразу для остаточного шляху. Для іншого місця створи Release повторно
  через `--output`.
- Перед повторним пакуванням зупини `Release\MetaMCP.exe` або Windows service.
- У готовому Release немає наших `.cmd`, `.bat`, `.ps1` чи cleanup-скриптів.
- `runtime\node\npx.cmd` і `npm.cmd` є штатною частиною portable Node runtime та
  потрібні MCP-серверам, які запускаються через `npx`.
- Файлові runtime-логи вимкнені за замовчуванням параметром `LoggingEnabled: false`.
