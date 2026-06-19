using System.Text.RegularExpressions;

namespace AteraSnipeSync.Core.SnipeIt;

/// <summary>
/// Chooses a single high-confidence name match when strong hardware identities do not match.
/// </summary>
internal static partial class AssetNameMatcher
{
    public static SnipeAssetMatch? ChooseHighConfidenceMatch(
        string sourceName,
        IReadOnlyList<SnipeAssetMatch> candidates,
        double threshold)
    {
        var highConfidenceMatches = ScoreCandidates(sourceName, candidates, threshold);
        return highConfidenceMatches.Count == 1 ? highConfidenceMatches[0].Match : null;
    }

    public static bool HasAmbiguousHighConfidenceMatches(
        string sourceName,
        IReadOnlyList<SnipeAssetMatch> candidates,
        double threshold)
    {
        return ScoreCandidates(sourceName, candidates, threshold).Count > 1;
    }

    private static List<ScoredMatch> ScoreCandidates(
        string sourceName,
        IReadOnlyList<SnipeAssetMatch> candidates,
        double threshold)
    {
        var normalizedSource = NormalizeName(sourceName);
        if (normalizedSource.Length == 0)
        {
            return [];
        }

        return candidates
            .Select(candidate => new ScoredMatch(candidate, CalculateSimilarity(normalizedSource, NormalizeName(candidate.Name))))
            .Where(scored => scored.Score >= threshold)
            .OrderByDescending(scored => scored.Score)
            .ToList();
    }

    private static double CalculateSimilarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1;
        }

        var distance = CalculateLevenshteinDistance(left, right);
        var maxLength = Math.Max(left.Length, right.Length);
        return 1 - distance / (double)maxLength;
    }

    private static int CalculateLevenshteinDistance(string left, string right)
    {
        var previous = new int[right.Length + 1];
        var current = new int[right.Length + 1];

        for (var index = 0; index <= right.Length; index++)
        {
            previous[index] = index;
        }

        for (var leftIndex = 1; leftIndex <= left.Length; leftIndex++)
        {
            current[0] = leftIndex;
            for (var rightIndex = 1; rightIndex <= right.Length; rightIndex++)
            {
                var substitutionCost = left[leftIndex - 1] == right[rightIndex - 1] ? 0 : 1;
                current[rightIndex] = Math.Min(
                    Math.Min(current[rightIndex - 1] + 1, previous[rightIndex] + 1),
                    previous[rightIndex - 1] + substitutionCost);
            }

            (previous, current) = (current, previous);
        }

        return previous[right.Length];
    }

    private static string NormalizeName(string value)
    {
        return WhitespaceRegex().Replace(value.Trim().ToLowerInvariant(), " ");
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    private sealed record ScoredMatch(SnipeAssetMatch Match, double Score);
}
