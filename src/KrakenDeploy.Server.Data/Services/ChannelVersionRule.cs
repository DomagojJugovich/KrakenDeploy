using System.Text.RegularExpressions;
using NuGet.Versioning;

namespace KrakenDeploy.Server.Data.Services;

/// <summary>
/// A parsed, reusable channel version rule (Octopus semantics): a NuGet-style
/// version <c>range</c> that a package version must satisfy, and a <c>tag</c>
/// regular expression the version's pre-release label must match. Either may be
/// absent (empty rule). Parsed once per release creation, then applied to each
/// package version.
/// </summary>
public sealed class ChannelVersionRule
{
    // Operator-authored patterns run against operator-supplied versions — bound
    // the match to avoid a pathological regex stalling release creation (ReDoS).
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>The empty rule — matches every version.</summary>
    public static ChannelVersionRule None { get; } = new(null, null, null, null);

    private readonly VersionRange? _range;
    private readonly string? _rangeText;
    private readonly Regex? _tag;
    private readonly string? _tagText;

    private ChannelVersionRule(VersionRange? range, string? rangeText, Regex? tag, string? tagText)
    {
        _range = range;
        _rangeText = rangeText;
        _tag = tag;
        _tagText = tagText;
    }

    /// <summary>True when at least one of range / tag is set.</summary>
    public bool HasRules => _range is not null || _tag is not null;

    /// <summary>
    /// Parses the channel's rule strings. Throws <see cref="FormatException"/> when
    /// the range isn't a valid NuGet range or the tag isn't a valid regex — used to
    /// reject a malformed rule at channel-save time (and to surface a legacy bad
    /// rule clearly at release-creation time).
    /// </summary>
    public static ChannelVersionRule Parse(string? versionRange, string? versionTag)
    {
        VersionRange? range = null;
        var rangeText = versionRange?.Trim();
        if (!string.IsNullOrEmpty(rangeText))
        {
            if (!VersionRange.TryParse(rangeText, out range))
            {
                throw new FormatException(
                    $"Version range '{rangeText}' is not a valid NuGet version range " +
                    "(e.g. \"[1.0,2.0)\", \"1.2.*\", or \"[1.0.0,)\").");
            }
        }

        Regex? tag = null;
        var tagText = versionTag?.Trim();
        if (!string.IsNullOrEmpty(tagText))
        {
            try
            {
                tag = new Regex(tagText, RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException ex)
            {
                throw new FormatException(
                    $"Version tag '{tagText}' is not a valid regular expression: {ex.Message}");
            }
        }

        return new ChannelVersionRule(range, rangeText, tag, tagText);
    }

    /// <summary>
    /// Returns <see langword="null"/> when <paramref name="version"/> satisfies the
    /// rule, or a human-readable reason it does not.
    /// </summary>
    public string? Check(string version)
    {
        if (!HasRules)
        {
            return null;
        }

        if (!NuGetVersion.TryParse(version, out var parsed))
        {
            return $"'{version}' is not a valid version, so it cannot be checked against the channel's rules";
        }

        if (_range is not null && !_range.Satisfies(parsed))
        {
            return $"version {version} does not satisfy the channel version range {_rangeText}";
        }

        if (_tag is not null)
        {
            try
            {
                if (!_tag.IsMatch(parsed.Release ?? string.Empty))
                {
                    var label = string.IsNullOrEmpty(parsed.Release) ? "(none)" : parsed.Release;
                    return $"the pre-release tag of {version} ({label}) does not match the channel version-tag filter /{_tagText}/";
                }
            }
            catch (RegexMatchTimeoutException)
            {
                return $"evaluating the channel version-tag filter against {version} timed out";
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="version"/> satisfies the rule.</summary>
    public bool IsSatisfiedBy(string version) => Check(version) is null;

    /// <summary>Short human-readable description of the rule, for error messages.</summary>
    public string Describe() => (_rangeText, _tagText) switch
    {
        ({ } r, { } t) => $"range {r}, tag /{t}/",
        ({ } r, null) => $"range {r}",
        (null, { } t) => $"tag /{t}/",
        _ => "no rules",
    };
}
