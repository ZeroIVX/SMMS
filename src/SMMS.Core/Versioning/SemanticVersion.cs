using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace SMMS.Core.Versioning;

/// <summary>
/// 语义化版本。支持 1–4 段数字、预发布标签与构建元数据，比较规则遵循 semver 2.0。
/// SMAPI 模组版本常见形态：<c>1.6.0</c>、<c>1.0.0-beta.5</c>、<c>2.1</c>。
/// </summary>
/// <remarks>
/// 缺省段按 0 处理，因此 <c>1.0</c> 与 <c>1.0.0</c> 视为相等。构建元数据不参与比较。
/// 预发布比较：数字标识符按数值比，且优先级低于字母标识符；标识符全等时，段数少的更小。
/// </remarks>
public sealed class SemanticVersion : IComparable<SemanticVersion>, IEquatable<SemanticVersion>
{
    private readonly int[] _numbers;
    private readonly string[] _prerelease;

    private SemanticVersion(int[] numbers, string[] prerelease, string? buildMetadata, string original)
    {
        _numbers = numbers;
        _prerelease = prerelease;
        BuildMetadata = buildMetadata;
        Original = original;
    }

    public string Original { get; }

    public string? BuildMetadata { get; }

    public bool IsPrerelease => _prerelease.Length > 0;

    public IReadOnlyList<int> Numbers => _numbers;

    public IReadOnlyList<string> PrereleaseIdentifiers => _prerelease;

    public int Major => _numbers[0];

    public int Minor => _numbers.Length > 1 ? _numbers[1] : 0;

    public int Patch => _numbers.Length > 2 ? _numbers[2] : 0;

    public int Revision => _numbers.Length > 3 ? _numbers[3] : 0;

    public static SemanticVersion Parse(string value)
        => TryParse(value, out var version)
            ? version
            : throw new FormatException($"无法解析为语义化版本：'{value}'");

    public static bool TryParse(string? value, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim();
        var original = text;

        string? build = null;
        var plus = text.IndexOf('+');
        if (plus >= 0)
        {
            build = text[(plus + 1)..];
            text = text[..plus];
            if (build.Length == 0)
            {
                return false;
            }
        }

        string[] prerelease = [];
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            var pre = text[(dash + 1)..];
            text = text[..dash];
            if (pre.Length == 0)
            {
                return false;
            }

            prerelease = pre.Split('.');
            if (prerelease.Any(static id => id.Length == 0))
            {
                return false;
            }
        }

        var segments = text.Split('.');
        if (segments.Length is < 1 or > 4)
        {
            return false;
        }

        var numbers = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out var number))
            {
                return false;
            }

            numbers[i] = number;
        }

        version = new SemanticVersion(numbers, prerelease, build, original);
        return true;
    }

    public static bool TryParseLenient(string? value, [NotNullWhen(true)] out SemanticVersion? version)
    {
        version = null;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var text = value.Trim().TrimStart('v', 'V');
        if (text.Length == 0)
        {
            return false;
        }

        if (TryParse(text, out version))
        {
            return true;
        }

        var head = text.Split('-', '+')[0];
        return head.Length > 0
            && int.TryParse(head, NumberStyles.None, CultureInfo.InvariantCulture, out var bare)
            && TryParse(bare.ToString(CultureInfo.InvariantCulture), out version);
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var length = Math.Max(_numbers.Length, other._numbers.Length);
        for (var i = 0; i < length; i++)
        {
            var left = i < _numbers.Length ? _numbers[i] : 0;
            var right = i < other._numbers.Length ? other._numbers[i] : 0;
            if (left != right)
            {
                return left.CompareTo(right);
            }
        }

        if (!IsPrerelease && !other.IsPrerelease)
        {
            return 0;
        }

        if (!IsPrerelease)
        {
            return 1;
        }

        if (!other.IsPrerelease)
        {
            return -1;
        }

        var shared = Math.Min(_prerelease.Length, other._prerelease.Length);
        for (var i = 0; i < shared; i++)
        {
            var result = CompareIdentifier(_prerelease[i], other._prerelease[i]);
            if (result != 0)
            {
                return result;
            }
        }

        return _prerelease.Length.CompareTo(other._prerelease.Length);
    }

    private static int CompareIdentifier(string left, string right)
    {
        var leftIsNumeric = int.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
        var rightIsNumeric = int.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);

        return (leftIsNumeric, rightIsNumeric) switch
        {
            (true, true) => leftNumber.CompareTo(rightNumber),
            (true, false) => -1,
            (false, true) => 1,
            _ => string.CompareOrdinal(left, right)
        };
    }

    public bool IsNewerThan(SemanticVersion? other) => CompareTo(other) > 0;

    public bool IsOlderThan(SemanticVersion? other) => CompareTo(other) < 0;

    public bool Equals(SemanticVersion? other) => other is not null && CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is SemanticVersion other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        // 按固定四段归一，避免 1.0 与 1.0.0 因数组长度不同产生不同哈希。
        hash.Add(Major);
        hash.Add(Minor);
        hash.Add(Patch);
        hash.Add(Revision);
        foreach (var identifier in _prerelease)
        {
            hash.Add(identifier, StringComparer.Ordinal);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        var text = string.Join('.', _numbers);
        if (_prerelease.Length > 0)
        {
            text += "-" + string.Join('.', _prerelease);
        }

        if (!string.IsNullOrEmpty(BuildMetadata))
        {
            text += "+" + BuildMetadata;
        }

        return text;
    }

    public static bool operator ==(SemanticVersion? left, SemanticVersion? right)
        => left is null ? right is null : left.Equals(right);

    public static bool operator !=(SemanticVersion? left, SemanticVersion? right) => !(left == right);

    public static bool operator <(SemanticVersion? left, SemanticVersion? right)
        => left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion? left, SemanticVersion? right)
        => left is null ? false : left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion? left, SemanticVersion? right)
        => left is null || left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion? left, SemanticVersion? right)
        => right is null || left?.CompareTo(right) >= 0;
}
