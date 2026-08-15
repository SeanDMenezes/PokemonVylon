namespace PokemonVylon.UpdateIndex;

public static class VersionComparer
{
    public static string Normalize(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string sanitized = value.Trim();
        if (sanitized.StartsWith('v') || sanitized.StartsWith('V'))
        {
            sanitized = sanitized[1..];
        }

        return sanitized;
    }

    public static int Compare(string left, string right)
    {
        string[] leftParts = Normalize(left).Split('.', StringSplitOptions.RemoveEmptyEntries);
        string[] rightParts = Normalize(right).Split('.', StringSplitOptions.RemoveEmptyEntries);

        int length = Math.Max(leftParts.Length, rightParts.Length);
        for (int i = 0; i < length; i++)
        {
            int leftNumber = i < leftParts.Length && int.TryParse(leftParts[i], out int leftParsed) ? leftParsed : 0;
            int rightNumber = i < rightParts.Length && int.TryParse(rightParts[i], out int rightParsed) ? rightParsed : 0;

            if (leftNumber < rightNumber)
            {
                return -1;
            }

            if (leftNumber > rightNumber)
            {
                return 1;
            }
        }

        return 0;
    }
}
