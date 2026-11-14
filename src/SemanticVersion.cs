using System;
using System.Text.RegularExpressions;

namespace AstralPartyChatPlugin;

/// <summary>SemVer 2.0 precedence for plugin releases and the running BepInEx build.</summary>
internal sealed class SemanticVersion : IComparable<SemanticVersion>
{
    private static readonly Regex Format = new(
        "^v?(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.CultureInvariant);
    private readonly string[] _core;
    private readonly string[] _pre;

    private SemanticVersion(string text, string[] core, string[] pre)
    {
        Text = text;
        _core = core;
        _pre = pre;
    }

    public string Text { get; }
    public bool IsPrerelease => _pre.Length != 0;

    public static SemanticVersion Parse(string value)
    {
        var match = Format.Match(value);
        if (!match.Success) throw new FormatException("Invalid SemVer: " + value);
        var pre = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : Array.Empty<string>();
        foreach (var part in pre)
            if (Numeric(part) && part.Length > 1 && part[0] == '0')
                throw new FormatException("Invalid numeric prerelease identifier");
        return new SemanticVersion(value.StartsWith("v", StringComparison.Ordinal) ? value.Substring(1) : value,
            new[] { match.Groups[1].Value, match.Groups[2].Value, match.Groups[3].Value }, pre);
    }

    private static bool Numeric(string value)
    {
        foreach (var c in value)
            if (c < '0' || c > '9') return false;
        return true;
    }

    // Canonical numbers have no leading zeros, so a longer one is larger.
    private static int CompareNumber(string left, string right) => left.Length != right.Length
        ? left.Length.CompareTo(right.Length) : string.CompareOrdinal(left, right);

    public int CompareTo(SemanticVersion? other)
    {
        if (other == null) return 1;
        for (var i = 0; i < _core.Length; i++)
        {
            var comparison = CompareNumber(_core[i], other._core[i]);
            if (comparison != 0) return comparison;
        }
        if (_pre.Length == 0 || other._pre.Length == 0)
            return _pre.Length == other._pre.Length ? 0 : _pre.Length == 0 ? 1 : -1;
        for (var i = 0; i < Math.Min(_pre.Length, other._pre.Length); i++)
        {
            var leftNumber = Numeric(_pre[i]);
            var rightNumber = Numeric(other._pre[i]);
            var comparison = leftNumber && rightNumber ? CompareNumber(_pre[i], other._pre[i])
                : leftNumber != rightNumber ? (leftNumber ? -1 : 1) : string.CompareOrdinal(_pre[i], other._pre[i]);
            if (comparison != 0) return comparison;
        }
        return _pre.Length.CompareTo(other._pre.Length);
    }
}
