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
