namespace TruckVisit.Domain.Common;

/// <summary>
/// Validation helpers for free-text domain values (names, notes) that are trimmed and
/// length-bounded but, unlike <see cref="NormalizedCode"/>, keep their original casing.
/// </summary>
internal static class DomainText
{
    /// <summary>Trims the value and rejects it when blank or too long.</summary>
    public static string Required(string? raw, string field, int maxLength)
    {
        var trimmed = raw?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            throw new DomainValidationException(field, $"{field} is required and cannot be blank.");
        }

        return trimmed.Length > maxLength
            ? throw new DomainValidationException(
                field,
                $"{field} cannot exceed {maxLength} characters (received {trimmed.Length}).")
            : trimmed;
    }

    /// <summary>Trims the value, maps blank to <c>null</c>, and rejects anything too long.</summary>
    public static string? Optional(string? raw, string field, int maxLength)
    {
        var trimmed = raw?.Trim();

        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return trimmed.Length > maxLength
            ? throw new DomainValidationException(
                field,
                $"{field} cannot exceed {maxLength} characters (received {trimmed.Length}).")
            : trimmed;
    }
}
