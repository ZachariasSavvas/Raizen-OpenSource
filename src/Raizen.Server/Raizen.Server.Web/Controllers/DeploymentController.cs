using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Raizen.Server.Core.Services;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Raizen.Server.Web.Controllers;

/// <summary>
/// Streams a pre-built agent deployment ZIP directly over HTTP, avoiding the
/// Blazor Server SignalR channel which cannot handle large binary payloads.
/// </summary>
[Route("deployment")]
[Authorize(Policy = "Admin")]
public class DeploymentController(IConfiguration config, IPollResponseSigner pollSigner) : Controller
{
    /// <summary>
    /// GET /deployment/download?token=&amp;serverUrl=&amp;label=&amp;expiresAt=&amp;maxUses=
    /// Builds and streams the agent deployment ZIP.
    /// Called via browser navigation (NavigationManager.NavigateTo) so the
    /// browser receives it as a normal file download — no SignalR involved.
    /// </summary>
    [HttpGet("download")]
    public IActionResult Download(
        [FromQuery] string token,
        [FromQuery] string serverUrl,
        [FromQuery] string label,
        [FromQuery] string? expiresAt,   // ISO-8601 or empty = never
        [FromQuery] int    maxUses = 100)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(serverUrl))
            return BadRequest("token and serverUrl are required.");

        var zipBytes = BuildAgentZip(token, serverUrl, label, expiresAt, maxUses);
        var filename = $"RaizenAgent-{label.Replace(' ', '-')}.zip";

        return File(zipBytes, "application/zip", filename);
    }

    // ── ZIP builder (moved from AgentDeployment.razor) ──────────────────────

    private byte[] BuildAgentZip(string plaintextToken, string serverUrl,
                                  string label, string? expiresAt, int maxUses)
    {
        var installerPath = config["AgentDeployment:InstallerPath"] ?? string.Empty;

        byte[]? msiBytes = null;
        if (!string.IsNullOrWhiteSpace(installerPath) && System.IO.File.Exists(installerPath))
            msiBytes = System.IO.File.ReadAllBytes(installerPath);

        var pollPublicKey  = pollSigner.PublicKeyPem;
        var tlsThumbprint  = config["AgentDeployment:TlsCertThumbprint"] ?? GetServerCertThumbprint();

        var configJson = JsonSerializer.Serialize(new
        {
            ServerUrl                = serverUrl,
            TlsPinThumbprint         = tlsThumbprint,
            ServerPublicKeyPem       = pollPublicKey,
            RegistrationToken        = plaintextToken,
            MachineId                = string.Empty,
            ApiKey                   = (string?)null,
            PollIntervalSeconds      = 30,
            HeartbeatIntervalSeconds = 300,
            HttpProxy                = (string?)null,
            HttpTimeoutSeconds       = 30,
        }, new JsonSerializerOptions { WriteIndented = true });

        var expiryLabel = string.IsNullOrEmpty(expiresAt) ? "never expires" : $"valid until {expiresAt} UTC";

        var installPs1 = string.Join("\r\n",
            "#Requires -RunAsAdministrator",
            "#Requires -Version 5.1",
            "<#",
            ".SYNOPSIS",
            "    Installs the Raizen Endpoint agent.",
            "    Checks for .NET 8 Desktop Runtime, downloads it if missing, then runs the MSI.",
            "#>",
            "[CmdletBinding()] param()",
            "Set-StrictMode -Version Latest",
            "$ErrorActionPreference = 'Stop'",
            "$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path",
            "",
            "Write-Host '=== Raizen Endpoint Installer ===' -ForegroundColor Cyan",
            "",
            "# ── Check .NET 8 Desktop Runtime ───────────────────────────────────────",
            "$runtimes = & dotnet --list-runtimes 2>$null | Where-Object { $_ -match 'Microsoft\\.WindowsDesktop\\.App 8\\.' }",
            "if (-not $runtimes) {",
            "    Write-Host '.NET 8 Desktop Runtime not found — downloading from Microsoft...' -ForegroundColor Yellow",
            "    $dlUrl  = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe'",
            "    $dlPath = Join-Path $env:TEMP 'dotnet8-runtime.exe'",
            "    try {",
            "        Invoke-WebRequest -Uri $dlUrl -OutFile $dlPath -UseBasicParsing",
            "        Write-Host 'Installing .NET 8 Desktop Runtime...' -ForegroundColor Gray",
            "        Start-Process -FilePath $dlPath -ArgumentList '/quiet /norestart' -Wait",
            "        Write-Host '.NET 8 Desktop Runtime installed.' -ForegroundColor Green",
            "    } finally {",
            "        if (Test-Path $dlPath) { Remove-Item $dlPath -Force }",
            "    }",
            "} else {",
            "    Write-Host '.NET 8 Desktop Runtime already installed.' -ForegroundColor Green",
            "}",
            "",
            "# ── Copy config to ProgramData before MSI starts the service ────────",
            "$configSrc  = Join-Path $ScriptDir 'raizen-config.json'",
            "$configDest = Join-Path $env:ProgramData 'Raizen'",
            "if (-not (Test-Path $configDest)) { New-Item -ItemType Directory -Path $configDest -Force | Out-Null }",
            "Copy-Item -Path $configSrc -Destination (Join-Path $configDest 'raizen-config.json') -Force",
            "Write-Host 'Config deployed.' -ForegroundColor Green",
            "",
            "# ── Run MSI ─────────────────────────────────────────────────────────",
            "$msi = Join-Path $ScriptDir 'RaizenEndpoint.msi'",
            "if (-not (Test-Path $msi)) { throw \"RaizenEndpoint.msi not found in $ScriptDir\" }",
            "Write-Host 'Installing Raizen Endpoint...' -ForegroundColor Gray",
            "Start-Process msiexec -ArgumentList \"/i `\"$msi`\" /quiet /norestart /log `\"$env:TEMP\\raizen-install.log`\"\" -Wait",
            "Write-Host 'Installation complete. The endpoint will register automatically within 1 minute.' -ForegroundColor Green");

        var hasMsi = msiBytes is not null;
        var readme = string.Join("\r\n",
            "Raizen Agent Deployment Package",
            "================================",
            "",
            $"Generated : {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC",
            $"Server    : {serverUrl}",
            $"Token     : {label} ({expiryLabel}, max {maxUses} machines)",
            "",
            "CONTENTS",
            "--------",
            "  raizen-config.json      Pre-configured agent settings (contains secret token)",
            hasMsi
                ? "  RaizenEndpoint.msi      Windows Installer package"
                : "  [MSI not included — build RaizenEndpoint.msi and set AgentDeployment:InstallerPath]",
            "  Install-Raizen.ps1      Installer script (checks .NET 8, runs MSI)",
            "  README.txt              This file",
            "",
            "INSTALLATION",
            "------------",
            "1. Extract all files to the same folder on the target Windows 10/11 x64 machine.",
            "2. Open PowerShell as Administrator and run:",
            "",
            @"       Set-ExecutionPolicy Bypass -Scope Process -Force",
            @"       .\Install-Raizen.ps1",
            "",
            "   The script will:",
            "     - Download and install .NET 8 Desktop Runtime if not present",
            "     - Deploy the config file to %ProgramData%\\Raizen\\",
            "     - Install the Raizen Endpoint Windows Service (runs as SYSTEM)",
            "     - Register the Raizen Tray App to auto-start for all users",
            "     - Register Windows Explorer right-click context menus",
            "     - Start the service — it will auto-register with the server within 1 minute",
            "",
            "SILENT / MASS DEPLOYMENT (Intune, SCCM, GPO)",
            "---------------------------------------------",
            "  1. Pre-copy raizen-config.json to %ProgramData%\\Raizen\\raizen-config.json",
            "  2. Ensure .NET 8 Desktop Runtime x64 is installed on the target",
            "  3. Run:  msiexec /i RaizenEndpoint.msi /quiet /norestart",
            "",
            "SECURITY NOTE",
            "-------------",
            $"This package contains a secret registration token ({expiryLabel}).",
            "Keep it confidential. Distribute only over a secure channel (Intune, SCCM, GPO).",
            "Revoke the token on the admin portal (Settings > Agent Deployment) once deployment",
            "is complete or if the package is compromised.");

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddTextEntry(zip, "raizen-config.json", configJson);
            AddTextEntry(zip, "Install-Raizen.ps1", installPs1);
            AddTextEntry(zip, "README.txt",          readme);
            if (msiBytes is not null)
                AddBinaryEntry(zip, "RaizenEndpoint.msi", msiBytes);
        }
        return ms.ToArray();
    }

    private string GetServerCertThumbprint()
    {
        try
        {
            var certPath = config["Kestrel:Endpoints:HttpsDefault:Certificate:Path"];
            var certPass = config["Kestrel:Endpoints:HttpsDefault:Certificate:Password"];
            if (string.IsNullOrEmpty(certPath) || !System.IO.File.Exists(certPath))
                return string.Empty;

            using var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                certPath, certPass);
            return cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "Failed to load server certificate for deployment package thumbprint");
            return string.Empty;
        }
    }

    private static void AddTextEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
        w.Write(content);
    }

    private static void AddBinaryEntry(ZipArchive zip, string name, byte[] data)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using var s = entry.Open();
        s.Write(data, 0, data.Length);
    }
}
