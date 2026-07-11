using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;
using AshServer.Data;
using AshServer.Models;

namespace AshServer.Controllers;

[ApiController]
[Route("api/sync")]
[Authorize]
public class SyncController : ControllerBase
{
    private readonly Database _db;

    public SyncController(Database db)
    {
        _db = db;
    }

    private int UserId => int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // ── Memories ──

    [HttpGet("memories")]
    public async Task<IActionResult> GetMemories([FromQuery] string companion)
    {
        if (string.IsNullOrWhiteSpace(companion))
            return BadRequest(new { error = "companion name required" });
        return Ok(await _db.GetMemories(UserId, companion));
    }

    [HttpPost("memories")]
    public async Task<IActionResult> SaveMemory([FromBody] SyncMemoryRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.CompanionName) || string.IsNullOrWhiteSpace(req.Content) || string.IsNullOrWhiteSpace(req.Category))
            return BadRequest(new { error = "companionName, content, category required" });
        await _db.SaveMemory(UserId, req.CompanionName, req.Content, req.Category);
        return Ok(new { ok = true });
    }

    [HttpDelete("memories")]
    public async Task<IActionResult> DeleteMemory([FromQuery] string companion, [FromQuery] string content)
    {
        if (string.IsNullOrWhiteSpace(companion) || string.IsNullOrWhiteSpace(content))
            return BadRequest(new { error = "companion and content required" });
        await _db.DeleteMemory(UserId, companion, content);
        return Ok(new { ok = true });
    }

    // ── Diaries ──

    [HttpGet("diaries")]
    public async Task<IActionResult> GetDiaries([FromQuery] string companion)
    {
        if (string.IsNullOrWhiteSpace(companion))
            return BadRequest(new { error = "companion name required" });
        return Ok(await _db.GetDiaries(UserId, companion));
    }

    [HttpPost("diaries")]
    public async Task<IActionResult> SaveDiary([FromBody] SyncDiaryRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.CompanionName) || string.IsNullOrWhiteSpace(req.DateString) || string.IsNullOrWhiteSpace(req.Content))
            return BadRequest(new { error = "companionName, dateString, content required" });
        await _db.SaveDiary(UserId, req.CompanionName, req.DateString, req.Content);
        return Ok(new { ok = true });
    }

    // ── Group Chats ──

    [HttpGet("groups")]
    public async Task<IActionResult> GetGroups()
    {
        return Ok(await _db.GetGroups(UserId));
    }

    [HttpPost("groups")]
    public async Task<IActionResult> SaveGroup([FromBody] SyncGroupRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Id) || string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.CharacterNames))
            return BadRequest(new { error = "id, name, characterNames required" });
        await _db.SaveGroup(UserId, req.Id, req.Name, req.CharacterNames);
        return Ok(new { ok = true });
    }

    [HttpDelete("groups/{id}")]
    public async Task<IActionResult> DeleteGroup(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new { error = "id required" });
        await _db.DeleteGroup(UserId, id);
        return Ok(new { ok = true });
    }

    [HttpGet("groups/{id}/messages")]
    public async Task<IActionResult> GetGroupMessages(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return BadRequest(new { error = "id required" });
        return Ok(await _db.GetGroupMessages(id));
    }

    [HttpPost("groups/{id}/messages")]
    public async Task<IActionResult> SaveGroupMessage(string id, [FromBody] SyncGroupMessageRequest req)
    {
        if (req == null || string.IsNullOrWhiteSpace(req.Sender) || string.IsNullOrWhiteSpace(req.Content))
            return BadRequest(new { error = "sender and content required" });
        await _db.SaveGroupMessage(id, req.Sender, req.CharacterName, req.Content);
        return Ok(new { ok = true });
    }
}

public record SyncMemoryRequest(string CompanionName, string Content, string Category);
public record SyncDiaryRequest(string CompanionName, string DateString, string Content);
public record SyncGroupRequest(string Id, string Name, string CharacterNames);
public record SyncGroupMessageRequest(string Sender, string? CharacterName, string Content);
