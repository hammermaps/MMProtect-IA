using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace MmProtect.InstanceAgent.Domain.Instances;

public interface IDomainNameValidator
{
    DomainValidationResult Validate(string? domain);
}

public sealed record DomainValidationResult(bool IsValid, string? NormalizedDomain, string? ErrorCode)
{
    public static DomainValidationResult Invalid() => new(false, null, "DOMAIN_INVALID");

    public static DomainValidationResult Valid(string domain) => new(true, domain, null);
}

public sealed partial class DomainNameValidator : IDomainNameValidator
{
    private static readonly IdnMapping Idn = new() { UseStd3AsciiRules = true };

    public DomainValidationResult Validate(string? domain)
    {
        if (string.IsNullOrWhiteSpace(domain) || domain.Length > 253 || domain != domain.Trim() ||
            domain.IndexOfAny(['/', ';', '\\', '\r', '\n']) >= 0 ||
            domain.Any(char.IsWhiteSpace) || IPAddress.TryParse(domain, out _))
        {
            return DomainValidationResult.Invalid();
        }

        string normalized;
        try
        {
            normalized = Idn.GetAscii(domain).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return DomainValidationResult.Invalid();
        }

        var labels = normalized.Split('.', StringSplitOptions.None);
        if (labels.Length < 2 || labels.Any(label => label.Length is < 1 or > 63 || !LabelPattern().IsMatch(label)))
        {
            return DomainValidationResult.Invalid();
        }

        return DomainValidationResult.Valid(normalized);
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")]
    private static partial Regex LabelPattern();
}
