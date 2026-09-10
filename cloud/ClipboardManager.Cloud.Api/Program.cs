using ClipboardManager.Cloud.Api;
using Npgsql;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
var connectionString = builder.Configuration.GetConnectionString("CloudDatabase");
if (string.IsNullOrWhiteSpace(connectionString))
{
    var passwordFile = builder.Configuration["CloudDatabase:PasswordFile"];
    if (string.IsNullOrWhiteSpace(passwordFile) || !File.Exists(passwordFile))
        throw new InvalidOperationException("Cloud database connection or password file is required.");
    connectionString = new NpgsqlConnectionStringBuilder
    {
        Host = builder.Configuration["CloudDatabase:Host"] ?? "postgres",
        Database = builder.Configuration["CloudDatabase:Database"] ?? "clipboardmanager",
        Username = builder.Configuration["CloudDatabase:Username"] ?? "clipboardmanager_owner",
        Password = File.ReadAllText(passwordFile).Trim(), Pooling = true, MaxPoolSize = 20
    }.ConnectionString;
}
builder.Services.AddSingleton(NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<CloudRepository>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<DatabaseMigrator>();
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options => options.AddPolicy("auth", context =>
    RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 })));

var app = builder.Build();
await app.Services.GetRequiredService<DatabaseMigrator>().MigrateAsync(CancellationToken.None);
if (args.Contains("--provision-account", StringComparer.Ordinal))
{
    // Provisioning is available only to a server operator through stdin, never HTTP.
    var input = await Console.In.ReadToEndAsync();
    var request = System.Text.Json.JsonSerializer.Deserialize<ProvisionAccountRequest>(input,
        new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))
        ?? throw new ArgumentException("Account request required.");
    var profile = await app.Services.GetRequiredService<CloudRepository>().ProvisionAccountAsync(request, CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { profile.Id, profile.Email }));
    return;
}
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseRateLimiter();
app.Use(async (context, next) =>
{
    try { await next(); }
    catch (UnauthorizedAccessException ex)
    {
        await Results.Problem(ex.Message, statusCode: StatusCodes.Status401Unauthorized).ExecuteAsync(context);
    }
    catch (InvalidOperationException)
    {
        await Results.Problem("请求状态冲突。", statusCode: StatusCodes.Status409Conflict).ExecuteAsync(context);
    }
    catch (KeyNotFoundException ex)
    {
        await Results.Problem(ex.Message, statusCode: StatusCodes.Status404NotFound).ExecuteAsync(context);
    }
    catch (ArgumentException ex)
    {
        await Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(context);
    }
});

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/v1/auth/sign-in", async (SignInRequest request, CloudRepository repository, TokenService tokens, CancellationToken ct) =>
{
    var profile = await repository.ValidatePasswordAsync(request.Email, request.Password, ct);
    if (profile is null) return Results.Problem("邮箱或密码错误。", statusCode: StatusCodes.Status401Unauthorized);
    var issued = tokens.Issue();
    await repository.CreateSessionAsync(profile.Id, request.DeviceId, request.DeviceName, issued, ct);
    return Results.Ok(new SessionResponse(profile.Id, profile.Email, profile.DisplayName,
        issued.AccessToken, issued.RefreshToken, issued.AccessExpiresAt));
}).RequireRateLimiting("auth");

app.MapPost("/v1/auth/refresh", async (RefreshRequest request, CloudRepository repository, TokenService tokens, CancellationToken ct) =>
{
    var issued = tokens.Issue();
    var profile = await repository.RotateSessionAsync(request.RefreshToken, issued, ct);
    return profile is null
        ? Results.Problem("刷新令牌无效或已撤销。", statusCode: StatusCodes.Status401Unauthorized)
        : Results.Ok(new SessionResponse(profile.Id, profile.Email, profile.DisplayName,
            issued.AccessToken, issued.RefreshToken, issued.AccessExpiresAt));
}).RequireRateLimiting("auth");

app.MapPost("/v1/auth/sign-out", async (HttpContext context, SignOutRequest request, CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    await repository.RevokeSessionsAsync(principal.ProfileId, principal.SessionId, request.AllDevices, ct);
    return Results.NoContent();
});

app.MapPost("/v1/auth/password-reset", () => Results.Problem(
    "密码重置邮件服务尚未配置，请联系平台管理员。", statusCode: StatusCodes.Status503ServiceUnavailable));

app.MapGet("/v1/workspaces", async (HttpContext context, CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    return Results.Ok(await repository.GetWorkspacesAsync(principal.ProfileId, ct));
});

app.MapPost("/v1/workspaces", async (HttpContext context, CreateWorkspaceRequest request, CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    return Results.Ok(await repository.CreateTeamWorkspaceAsync(principal, request.Name, ct));
});

app.MapGet("/v1/workspaces/{workspaceId:guid}/members", async (HttpContext context, Guid workspaceId, CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    return Results.Ok(await repository.GetMembersAsync(principal.ProfileId, workspaceId, ct));
});

app.MapPatch("/v1/workspaces/{workspaceId:guid}/members/{memberId:guid}/role", async (HttpContext context, Guid workspaceId, Guid memberId,
    UpdateMemberRoleRequest request, CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    await repository.UpdateMemberRoleAsync(principal, workspaceId, memberId, request.Role, ct);
    return Results.NoContent();
});

app.MapDelete("/v1/workspaces/{workspaceId:guid}/members/{memberId:guid}", async (HttpContext context, Guid workspaceId, Guid memberId,
    CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    await repository.RemoveMemberAsync(principal, workspaceId, memberId, ct);
    return Results.NoContent();
});

app.MapGet("/v1/workspaces/{workspaceId:guid}/groups", async (HttpContext context, Guid workspaceId, CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    return Results.Ok(await repository.GetGroupsAsync(principal.ProfileId, workspaceId, ct));
});

app.MapPost("/v1/workspaces/{workspaceId:guid}/groups", async (HttpContext context, Guid workspaceId, CreateGroupRequest request,
    CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    return Results.Ok(await repository.CreateGroupAsync(principal, workspaceId, request.Name, ct));
});

app.MapPut("/v1/workspaces/{workspaceId:guid}/groups/{groupId:guid}/members", async (HttpContext context, Guid workspaceId, Guid groupId,
    UpdateGroupMembersRequest request, CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    await repository.SetGroupMembersAsync(principal, workspaceId, groupId, request.UserIds, ct);
    return Results.NoContent();
});

app.MapPost("/v1/workspaces/{workspaceId:guid}/invitations", async (HttpContext context, Guid workspaceId, CreateInvitationRequest request,
    CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    return Results.Ok(await repository.CreateInvitationAsync(principal, workspaceId, request.LifetimeSeconds, request.MaxUses, ct));
});

app.MapPost("/v1/invitations/join", async (HttpContext context, JoinInvitationRequest request, CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    return Results.Ok(await repository.JoinInvitationAsync(principal, request.Code, ct));
});

app.MapPost("/v1/workspaces/{workspaceId:guid}/invitations/{invitationId:guid}/revoke", async (HttpContext context, Guid workspaceId, Guid invitationId, CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    await repository.RevokeInvitationAsync(principal, workspaceId, invitationId, ct);
    return Results.NoContent();
});

app.MapGet("/v1/devices", async (HttpContext context, CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    return Results.Ok(await repository.GetDevicesAsync(principal, ct));
});

app.MapPost("/v1/devices/{deviceId:guid}/revoke", async (HttpContext context, Guid deviceId, CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    await repository.RevokeDeviceAsync(principal, deviceId, ct);
    return Results.NoContent();
});

app.MapGet("/v1/sync/pull", async (HttpContext context, Guid workspaceId, long? cursor, CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    var result = await repository.PullAsync(principal.ProfileId, workspaceId, cursor ?? 0, ct);
    return Results.Ok(result);
});

app.MapPost("/v1/sync/push", async (HttpContext context, PushRequest request, CloudRepository repository, CancellationToken ct) =>
{
    var principal = await context.RequireCloudUserAsync(repository, ct);
    return Results.Ok(await repository.PushAsync(principal, request, ct));
});

app.Run();
