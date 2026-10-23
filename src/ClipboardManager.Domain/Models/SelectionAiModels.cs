using System.Security.Cryptography;
using System.Text;
using System.Net;

namespace ClipboardManager.Domain.Models;

public sealed record PromptTemplate(
    Guid Id,
    string Name,
    string Body,
    string Action = PromptActions.Prompt,
    string? Url = null);

public static class PromptActions
{
    public const string Prompt = "Prompt";
    public const string OpenUrl = "OpenUrl";
}

public sealed record SelectionAiSettings
{
    public bool HotkeyEnabled { get; init; }
    public bool FloatingToolbarEnabled { get; init; }
    public string Hotkey { get; init; } = "Ctrl+Shift+G";
    public string Provider { get; init; } = "DeepSeek";
    public string BaseUrl { get; init; } = "https://api.deepseek.com";
    public string Model { get; init; } = "deepseek-flash";
    // Process names (not paths) where selection AI stays inactive entirely.
    // Empty means global: the feature activates in every application.
    public IReadOnlyList<string> ExcludedProcesses { get; init; } = [];
    public IReadOnlyList<PromptTemplate> Templates { get; init; } =
    [
        new(new Guid("de57c176-a095-4840-9309-e149fb19e254"), "翻译", "请将以下内容翻译为简体中文，只输出译文：\n\n{text}"),
        new(new Guid("d6960081-1ad8-40de-b3d2-0bbc49808d5d"), "摘要", "请将以下内容提炼为三条准确、简洁的要点：\n\n{text}"),
        new(new Guid("35bce662-cc84-44bc-b970-c0448b396e82"), "解释", "请用通俗的语言解释以下内容：\n\n{text}")
    ];

    // Scope credentials to the endpoint as well as the provider to avoid sending a saved
    // key to a newly configured, unrelated compatible endpoint.
    [System.Text.Json.Serialization.JsonIgnore]
    public string CredentialKey => "SelectionAI/" + Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(Provider + "|" + BaseUrl.TrimEnd('/'))));

    public static void Validate(SelectionAiSettings settings)
    {
        if (settings.Provider is not ("DeepSeek" or "OpenAICompatible" or "LocalGateway"))
            throw new ArgumentException("请选择 DeepSeek、OpenAI 兼容接口或本地 AI 网关。");
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("模型 API 地址不能包含用户名、查询参数或片段。");
        if (settings.Provider == "LocalGateway")
        {
            if ((uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !IsLoopback(uri))
                throw new ArgumentException("本地 AI 网关仅允许 localhost、127.0.0.1 或 ::1 地址。");
        }
        else if (uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new ArgumentException("远端模型 API 地址必须使用 HTTPS；本地网关请切换为“本地 AI 网关”。");
        }
        if (string.IsNullOrWhiteSpace(settings.Model) || settings.Model.Length > 200)
            throw new ArgumentException("模型名称必须为 1–200 个字符。");
        if (string.IsNullOrWhiteSpace(settings.Hotkey))
            throw new ArgumentException("划词快捷键不能为空。");
        if (settings.ExcludedProcesses is null || settings.ExcludedProcesses.Count > 100 ||
            settings.ExcludedProcesses.Any(name => string.IsNullOrWhiteSpace(name) || name.Length > 100 || name.IndexOfAny(['\\', '/', ':']) >= 0))
            throw new ArgumentException("划词排除列表最多 100 项，请填写进程名，不要填写完整路径。");
        if (settings.Templates is null || settings.Templates.Count > 50 ||
            settings.Templates.Select(p => p.Id).Distinct().Count() != settings.Templates.Count)
            throw new ArgumentException("提示词最多 50 条，且标识不能重复。");
        foreach (var prompt in settings.Templates)
            ValidateTemplate(prompt);
    }

    public static void ValidateTemplate(PromptTemplate prompt)
    {
        if (prompt.Id == Guid.Empty || string.IsNullOrWhiteSpace(prompt.Name) || prompt.Name.Length > 40)
            throw new ArgumentException("操作名称为 1–40 字。");
        var action = string.IsNullOrWhiteSpace(prompt.Action) ? PromptActions.Prompt : prompt.Action;
        if (action == PromptActions.Prompt)
        {
            if (string.IsNullOrWhiteSpace(prompt.Body) || prompt.Body.Length > 16000 ||
                !prompt.Body.Contains("{text}", StringComparison.Ordinal))
                throw new ArgumentException("提示词正文最多 16000 字，并须包含 {text}。");
            return;
        }
        if (action != PromptActions.OpenUrl)
            throw new ArgumentException("操作类型只支持提示词或打开网站。");
        if (!Uri.TryCreate(prompt.Url, UriKind.Absolute, out var target) ||
            target.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(target.UserInfo))
            throw new ArgumentException("网站地址必须是不含用户名的 http 或 https 地址。");
    }

    private static bool IsLoopback(Uri uri) =>
        uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(uri.Host, out var address) && IPAddress.IsLoopback(address));
}

public sealed record AiTextRequest(SelectionAiSettings Settings, string Prompt);
public sealed record AiTextResponse(string Text);

public sealed class AiRequestException(string message) : Exception(message);
