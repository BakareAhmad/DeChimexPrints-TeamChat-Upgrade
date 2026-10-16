using System.Text.RegularExpressions;

namespace DeChimex.Shared;

/// <summary>
/// Display formatting for enum values shown to users (owner directive 2026-10-07):
/// raw PascalCase names become separate readable words — InProgress -> "In Progress",
/// PendingPayment -> "Pending Payment" — everywhere they are rendered. The underlying
/// enum values, route values and form posts are never changed; this is display-only.
///
/// Surfaces that already project statuses through dedicated presenters
/// (OrderStatusPresentation / CustomerPresentation / the marketing badge switches)
/// keep their custom wording; this helper covers every verbatim render.
/// </summary>
public static class EnumDisplay
{
    /// <summary>Accepts enums, strings holding enum names, or anything with a PascalCase name.</summary>
    public static string Words(object? value) => value switch
    {
        null => "",
        Enum e => Split(e.ToString()),
        string s => Split(s),
        _ => Split(value.ToString() ?? "")
    };

    private static string Split(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name?.Trim() ?? "";
        return Regex.Replace(name.Replace('_', ' '), "(?<!^)(?=[A-Z])", " ").Trim();
    }
}
