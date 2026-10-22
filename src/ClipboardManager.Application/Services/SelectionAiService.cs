using ClipboardManager.Domain.Contracts;
using ClipboardManager.Domain.Models;

namespace ClipboardManager.Application.Services;

public sealed class SelectionAiService(ITextAiProvider provider)
{
    public const int MaxSelectionCharacters = 32000;
    public const int MaxPromptCharacters = 100000;

    public static string Expand(PromptTemplate template, string selectedText)
    {
        if (string.IsNullOrWhiteSpace(selectedText) || selectedText.Length > MaxSelectionCharacters)
            throw new ArgumentException($"所选文字须为 1–{MaxSelectionCharacters} 个字符。");
        if (string.IsNullOrWhiteSpace(template.Body) || !template.Body.Contains("{text}", StringComparison.Ordinal))
            throw new ArgumentException("提示词必须包含 {text}。");
        int placeholders = 0, position = 0;
        while ((position = template.Body.IndexOf("{text}", position, StringComparison.Ordinal)) >= 0)
        { placeholders++; position += 6; }
        if (template.Body.Length + (long)placeholders * (selectedText.Length - 6) > MaxPromptCharacters)
            throw new ArgumentException("展开后的提示词超过 100000 字，请缩短模板或原文。");
        return template.Body.Replace("{text}", selectedText, StringComparison.Ordinal);
    }

    // A user-selected toolbar action starts the request; selection capture alone never sends.
    public async Task<AiTextResponse> SendAsync(
        SelectionAiSettings settings, string prompt, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SelectionAiSettings.Validate(settings);
        if (string.IsNullOrWhiteSpace(prompt) || prompt.Length > MaxPromptCharacters)
            throw new ArgumentException("待发送内容为空或过长。");
        var response = await provider.CompleteAsync(new AiTextRequest(settings, prompt), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var text = AiPlainTextFormatter.Format(response.Text);
        if (string.IsNullOrWhiteSpace(text)) throw new AiRequestException("模型没有返回可显示的正文，请重试。");
        return new AiTextResponse(text);
    }
}
