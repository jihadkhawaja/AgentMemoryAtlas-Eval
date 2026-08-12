using System.Collections.Generic;

internal sealed record OpenAiToolCompletion(string? Id, string? Text, IReadOnlyList<OpenAiFunctionCall> ToolCalls);
