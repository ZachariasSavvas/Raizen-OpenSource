using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Raizen.Server.Api.Services;
using Raizen.Server.Core.Services;

namespace Raizen.Server.Api.Controllers;

/// <summary>
/// Allows authenticated endpoint agents to check for a newer agent version
/// and download the latest MSI installer if one is available.
///
/// Set AgentDeployment:CurrentVersion in appsettings.Production.json to the
/// version string that matches the MSI you want endpoints to install.
/// Endpoints will self-update within one check interval (default 4 hours).
///
/// The version response is RSA-signed so agents can verify the server's identity
/// and the MSI hash before running msiexec. The MSI hash is computed automatically
/// from the file at AgentDeployment:InstallerPath - no manual configuration needed.
/// </summary>
[ApiController]
[Route("api/v1/agent")]
[Authorize(Policy = "EndpointOnly")]
public sealed class AgentUpdateController(
    IConfiguration      config,
    IPollResponseSigner pollSigner,
    MsiHashCache        msiHashCache) : ControllerBase
{
    /// <summary>
    /// Returns a signed version response containing the current version and MSI hash.
    /// 404 means auto-update is not configured — agents should skip silently.
    ///
    /// Old agents (pre-v1.1.0) only read the top-level "version" field and ignore the
    /// signed payload, maintaining full backward compatibility.
    /// </summary>
    [HttpGet("version")]
    public IActionResult GetVersion()
    {
        var version = config["AgentDeployment:CurrentVersion"];
        if (string.IsNullOrWhiteSpace(version))
            return NotFound();

        var msiSha256 = msiHashCache.GetHash();
        return Ok(pollSigner.SignVersion(version, msiSha256));
    }

    /// <summary>
    /// Streams the MSI installer so the agent can apply the update.
    /// Agents must verify the file hash against the value in the signed version
    /// response before executing msiexec.
    /// </summary>
    [HttpGet("msi")]
    public IActionResult DownloadMsi()
    {
        var path = config["AgentDeployment:InstallerPath"];
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
            return NotFound(new { error = "MSI installer not available on server." });

        return PhysicalFile(path, "application/octet-stream", "RaizenEndpoint.msi");
    }
}
