using Markdig;

namespace ClipboardManager.Application.Services;

public static class AiPlainTextFormatter
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseEmphasisExtras()
        .Build();

    public static string Format(string text) => Markdown.ToPlainText(text, Pipeline).Trim();
}
