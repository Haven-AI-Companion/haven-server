using AshServer.Auth;
using AshServer.Data;
using AshServer.Models;

namespace AshServer.Chat;

/// <summary>
/// Resolves an external (Discord, Slack, etc.) user identity to ash-server
/// permissions before any chat routing happens.
///
/// Flow:
///   1. Look up (provider, externalId) in external_identities.
///   2. If linked → use that user's ash-server roles/permissions.
///   3. If unlinked → check channel config for allow_unlinked + unlinked_role.
///   4. Return ResolvedIdentity with IsAllowed, Permissions, AgentAllowed, MaxTurns.
/// </summary>
public class IdentityResolver
{
    private readonly Database            _db;
    private readonly ILogger<IdentityResolver> _logger;

    public IdentityResolver(Database db, ILogger<IdentityResolver> logger)
    {
        _db     = db;
        _logger = logger;
    }

    public async Task<ResolvedIdentity> ResolveAsync(
        string provider,
        string channelId,
        string externalId,
        string? externalUsername,
        CancellationToken ct = default)
    {
        // Load channel config (controls agent access, fallback role, etc.)
        var channel = await _db.GetChannelConfig(provider, channelId);

        // Channel must be enabled
        if (channel is { Enabled: false })
            return Deny("This channel is disabled.");

        // Try to find a linked ash-server account
        var identity = await _db.GetIdentityByExternal(provider, externalId);

        if (identity is not null)
        {
            // Linked user — load their actual permissions
            var user = await _db.GetUserById(identity.UserId);
            if (user is null)
            {
                // Edge case: user was deleted but identity row remains
                await _db.RemoveIdentity(identity.Id);
                return await HandleUnlinked(provider, externalId, externalUsername, channel);
            }

            var perms = user.IsAdmin
                ? Permissions.All.ToList()
                : (await _db.GetUserPermissions(identity.UserId)).ToList();

            var agentAllowed = channel?.AgentEnabled ?? true;
            var maxTurns     = channel?.MaxTurns ?? 10;

            // Tool allowlist filter: if channel restricts tools, record it
            var toolAllowlist = channel?.ToolAllowlist ?? [];

            _logger.LogDebug("[identity] {Provider}/{ExternalId} → user#{UserId} ({Username}) perms:{Count}",
                provider, externalId, identity.UserId, user.Username, perms.Count);

            return new ResolvedIdentity(
                UserId:       identity.UserId,
                Username:     user.Username,
                Permissions:  perms,
                IsLinked:     true,
                AgentAllowed: agentAllowed,
                MaxTurns:     maxTurns,
                DenyReason:   ""
            );
        }

        // Not linked
        return await HandleUnlinked(provider, externalId, externalUsername, channel);
    }

    private async Task<ResolvedIdentity> HandleUnlinked(
        string provider,
        string externalId,
        string? externalUsername,
        ChannelConfig? channel)
    {
        if (channel is null || !channel.AllowUnlinked)
            return Deny("Your account is not linked. Ask an admin to link your account or use the /link command.");

        // Use the unlinked role's permissions if configured
        List<string> perms = [];
        if (channel.UnlinkedRoleId.HasValue)
        {
            var role = await _db.GetRole(channel.UnlinkedRoleId.Value);
            perms = role?.Permissions ?? [];
        }

        _logger.LogDebug("[identity] {Provider}/{ExternalId} → unlinked, role:{RoleId}",
            provider, externalId, channel.UnlinkedRoleId);

        return new ResolvedIdentity(
            UserId:       null,
            Username:     externalUsername,
            Permissions:  perms,
            IsLinked:     false,
            AgentAllowed: channel.AgentEnabled && perms.Contains(Permissions.AgentMode),
            MaxTurns:     Math.Min(channel.MaxTurns, 3), // cap unlinked users lower
            DenyReason:   ""
        );
    }

    private static ResolvedIdentity Deny(string reason) => new(
        UserId: null, Username: null, Permissions: [],
        IsLinked: false, AgentAllowed: false, MaxTurns: 0,
        DenyReason: reason);
}
