namespace TruckVisit.Domain.Common;

/// <summary>
/// Base class for short identifier codes that must be stored in one canonical form:
/// upper-cased, free of whitespace, restricted to an unambiguous character set.
///
/// The acceptance criteria only demand this for unit numbers and license plates, but the same
/// guarantee is what makes terminal and location codes safe to compare — including in the
/// authorization path, where a case mismatch would silently widen or narrow access.
/// Normalising once, at construction, means no call site can forget to do it (ADR-005).
/// </summary>
public abstract record NormalizedCode
{
    protected NormalizedCode(string value)
    {
        Value = value;
    }

    /// <summary>The canonical form. Never null, never empty, never contains whitespace.</summary>
    public string Value { get; }

    public sealed override string ToString() => Value;

    /// <summary>
    /// Strips every whitespace character and upper-cases the remainder using the invariant culture.
    /// </summary>
    /// <remarks>
    /// The invariant culture is not cosmetic here. Under a Turkish locale the default
    /// <c>ToUpper()</c> maps 'i' to 'İ' (dotted capital I), so the very same plate would normalise
    /// differently depending on the server's regional settings — producing duplicate records and
    /// audit trails that cannot be matched. <c>ToUpperInvariant</c> removes that whole class of bug,
    /// and analyzer rules CA1304/CA1305/CA1310 are promoted to build errors to keep it that way.
    /// </remarks>
    protected static string Normalize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return string.Empty;
        }

        var buffer = new char[raw.Length];
        var length = 0;

        foreach (var character in raw)
        {
            if (char.IsWhiteSpace(character))
            {
                continue;
            }

            buffer[length++] = char.ToUpperInvariant(character);
        }

        return new string(buffer, 0, length);
    }

    /// <summary>
    /// Normalises <paramref name="raw"/> and enforces the shared code rules.
    /// </summary>
    /// <exception cref="DomainValidationException">The value cannot form a valid code.</exception>
    protected static string NormalizeAndValidate(string? raw, string field, int maxLength)
    {
        var normalized = Normalize(raw);

        if (normalized.Length == 0)
        {
            throw new DomainValidationException(field, $"{field} is required and cannot be blank.");
        }

        if (normalized.Length > maxLength)
        {
            throw new DomainValidationException(
                field,
                $"{field} cannot exceed {maxLength} characters once whitespace is removed (received {normalized.Length}).");
        }

        foreach (var character in normalized)
        {
            if (!IsAllowed(character))
            {
                throw new DomainValidationException(
                    field,
                    $"{field} may only contain letters A-Z, digits 0-9 and hyphens.");
            }
        }

        return normalized;
    }

    private static bool IsAllowed(char character) =>
        character is >= 'A' and <= 'Z' or >= '0' and <= '9' or '-';
}
