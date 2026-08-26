namespace ProjectFileHub.Core.Services;

public sealed class NaturalStringComparer : IComparer<string?>
{
    public static NaturalStringComparer OrdinalIgnoreCase { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right))
        {
            return 0;
        }

        if (left is null)
        {
            return -1;
        }

        if (right is null)
        {
            return 1;
        }

        var leftIndex = 0;
        var rightIndex = 0;

        while (leftIndex < left.Length && rightIndex < right.Length)
        {
            if (char.IsDigit(left[leftIndex]) && char.IsDigit(right[rightIndex]))
            {
                var leftStart = leftIndex;
                var rightStart = rightIndex;

                while (leftIndex < left.Length && char.IsDigit(left[leftIndex]))
                {
                    leftIndex++;
                }

                while (rightIndex < right.Length && char.IsDigit(right[rightIndex]))
                {
                    rightIndex++;
                }

                var leftDigits = left.AsSpan(leftStart, leftIndex - leftStart).TrimStart('0');
                var rightDigits = right.AsSpan(rightStart, rightIndex - rightStart).TrimStart('0');

                var lengthComparison = leftDigits.Length.CompareTo(rightDigits.Length);
                if (lengthComparison != 0)
                {
                    return lengthComparison;
                }

                var digitComparison = leftDigits.CompareTo(rightDigits, StringComparison.Ordinal);
                if (digitComparison != 0)
                {
                    return digitComparison;
                }

                continue;
            }

            var characterComparison = char.ToUpperInvariant(left[leftIndex])
                .CompareTo(char.ToUpperInvariant(right[rightIndex]));

            if (characterComparison != 0)
            {
                return characterComparison;
            }

            leftIndex++;
            rightIndex++;
        }

        return left.Length.CompareTo(right.Length);
    }
}
