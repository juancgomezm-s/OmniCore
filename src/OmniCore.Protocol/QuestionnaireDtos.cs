namespace OmniCore.Protocol;

/// <summary>DTOs de formulario de InteractionKind.Question; solo datos publicados por el Host.</summary>
public sealed record QuestionnairePromptDto(string Title, string? Description, IReadOnlyList<QuestionFieldDto> Questions);
public sealed record QuestionFieldDto(string Id, string Prompt, string? HelpText, string Kind,
    IReadOnlyList<QuestionOptionDto> Options, OtherInputDto? Other, bool Required,
    int? MinSelections, int? MaxSelections, int? MaxTextLength);
public sealed record QuestionOptionDto(string Id, string Label, string? Description);
public sealed record OtherInputDto(string OptionId, string Label, string? Placeholder, bool TextRequired,
    int MaxTextLength);
public sealed record QuestionAnswerDto(string QuestionId, IReadOnlyList<string> SelectedOptionIds,
    string? Text, string? OtherText);
public sealed record QuestionnaireResponseDto(IReadOnlyList<QuestionAnswerDto> Answers, bool Cancelled);
