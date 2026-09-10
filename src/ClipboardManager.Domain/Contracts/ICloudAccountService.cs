using ClipboardManager.Domain.Models;

namespace ClipboardManager.Domain.Contracts;

public interface ICredentialVault
{
    Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default);
    Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public interface ICloudAccountService
{
    Task<CloudSession?> GetSessionAsync(CancellationToken cancellationToken = default);
    Task<CloudSession> SignInAsync(CloudSignInRequest request, CancellationToken cancellationToken = default);
    Task RequestPasswordResetAsync(string email, CancellationToken cancellationToken = default);
    Task SignOutAsync(bool allDevices, CancellationToken cancellationToken = default);
}

public interface ICloudWorkspaceService
{
    Task<IReadOnlyList<CloudWorkspace>> GetAvailableAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TeamMember>> GetMembersAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<MemberGroup>> GetGroupsAsync(Guid workspaceId, CancellationToken cancellationToken = default);
    Task<TeamInvitation> CreateInvitationAsync(Guid workspaceId, TimeSpan lifetime, int maxUses, CancellationToken cancellationToken = default);
    Task JoinAsync(string invitationCode, CancellationToken cancellationToken = default);
}

public interface ICloudModeService
{
    AppMode Mode { get; }
    CloudStatus Status { get; }
    event EventHandler<CloudStatus>? StatusChanged;
    Task SetModeAsync(AppMode mode, CancellationToken cancellationToken = default);
}
