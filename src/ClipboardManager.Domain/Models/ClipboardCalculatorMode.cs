using System.Text.Json.Serialization;

namespace ClipboardManager.Domain.Models;

/// <summary>What to do when a copied text is an arithmetic expression.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ClipboardCalculatorMode>))]
public enum ClipboardCalculatorMode
{
    /// <summary>Do nothing.</summary>
    Off,
    /// <summary>Show the result next to the history row; never touch the clipboard.</summary>
    Hint,
    /// <summary>Replace the clipboard text with <c>expression=result</c> (the pre-0.8.7 "auto calculate").</summary>
    Rewrite,
}
