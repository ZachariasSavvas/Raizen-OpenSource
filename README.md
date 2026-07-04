# Raizen

Raizen is an open-source Windows privileged action approval system. It combines
an admin portal, server API, endpoint Windows service, tray client, and Windows
installers for controlled elevation workflows such as requesting approved
run-as-admin actions from Explorer.

## Components

- `src/Raizen.Server/Raizen.Server.Api` - endpoint/admin API
- `src/Raizen.Server/Raizen.Server.Web` - Blazor admin portal
- `src/Raizen.Endpoint/Raizen.Endpoint.Service` - Windows endpoint service
- `src/Raizen.Endpoint/Raizen.Endpoint.Tray` - endpoint tray/request UI
- `src/Raizen.Server/Raizen.Server.Setup` - Windows server setup wizard
- `scripts/` - build, update, uninstall, and release helpers

## Build

```powershell
dotnet build Raizen.sln
dotnet test tests/Raizen.Tests/Raizen.Tests.csproj
```

Create a Windows server package and endpoint MSI:

```powershell
.\scripts\Build-WindowsServer.ps1
```

## Security Note

Raizen brokers privileged actions on Windows endpoints. Review the code,
configuration, authentication, TLS, endpoint API key handling, audit logs, and
update flow before using it in production. See `SECURITY.md` for vulnerability
reporting guidance.
