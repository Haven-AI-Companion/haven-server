using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using AshServer.Data;
using AshServer.Models;
using AshServer.AI;

namespace AshServer.Controllers;

[ApiController]
[Route("api/registry")]
[Authorize]
public class RegistryController : ControllerBase
{
    private readonly Database _db;
    private readonly CompanionRegistrySyncService _syncService;

    public RegistryController(Database db, CompanionRegistrySyncService syncService)
    {
        _db = db;
        _syncService = syncService;
    }

    [HttpGet("repos")]
    public async Task<IActionResult> GetRepositories()
    {
        try
        {
            var repos = await _db.GetRepositories();
            return Ok(repos);
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to retrieve repositories: {ex.Message}" });
        }
    }

    [HttpPost("repos")]
    public async Task<IActionResult> AddRepository([FromBody] AddRepoRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Url))
            return BadRequest(new { error = "Repository URL is required." });

        var name = string.IsNullOrWhiteSpace(req.Name) ? "Custom Repo" : req.Name.Trim();
        var url = req.Url.Trim();

        try
        {
            await _db.AddRepository(url, name);
            
            // Trigger sync in background so response isn't blocked by downloads
            _ = Task.Run(async () =>
            {
                try { await _syncService.SyncAllNow(); } catch { }
            });

            return Ok(new { ok = true, message = $"Repository '{name}' added. Synchronization started in background." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to add repository: {ex.Message}" });
        }
    }

    [HttpDelete("repos/{id:int}")]
    public async Task<IActionResult> DeleteRepository(int id)
    {
        try
        {
            await _db.DeleteRepository(id);
            return Ok(new { ok = true, message = "Repository removed successfully." });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = $"Failed to delete repository: {ex.Message}" });
        }
    }

    [HttpPost("sync")]
    public IActionResult TriggerSync()
    {
        // Trigger sync in separate thread
        _ = Task.Run(async () =>
        {
            try { await _syncService.SyncAllNow(); } catch { }
        });

        return Ok(new { ok = true, message = "Registry synchronization triggered." });
    }
}

public class AddRepoRequest
{
    public string Url { get; set; } = string.Empty;
    public string? Name { get; set; }
}
