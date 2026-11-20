namespace ChatSample.Server.Data;

/// <summary>The chat sample's validation rules, kept in one place so the API and the rooms agree.</summary>
public static class Rules
{
    /// <summary>How many messages one history page returns.</summary>
    public const int PageSize = 50;

    /// <summary>The most usernames a search returns.</summary>
    public const int SearchLimit = 20;

    /// <summary>The most members a group can have.</summary>
    public const int MaxGroupMembers = 100;

    private const int MinUsernameLength = 3;
    private const int MaxUsernameLength = 24;
    private const int MinPasswordLength = 8;
    private const int MaxTextLength = 2000;
    private const int MaxTitleLength = 60;

    /// <summary>
    /// Trims and lowercases a username. Returns null unless it is 3 to 24 characters of
    /// a-z, 0-9, underscore, dot or hyphen.
    /// </summary>
    public static string? NormalizeUsername(string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        string name = raw.Trim();
        if (name.Length is < MinUsernameLength or > MaxUsernameLength)
        {
            return null;
        }

        foreach (char c in name)
        {
            bool allowed = c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '_' or '.' or '-';
            if (!allowed)
            {
                return null;
            }
        }

        return name.ToLowerInvariant();
    }

    /// <summary>Trims message text. Returns null when it is empty or longer than 2,000 characters.</summary>
    public static string? CleanText(string? text) => CleanLimited(text, MaxTextLength);

    /// <summary>Trims a group title. Returns null when it is empty or longer than 60 characters.</summary>
    public static string? CleanTitle(string? title) => CleanLimited(title, MaxTitleLength);

    /// <summary>True when the password has at least 8 characters.</summary>
    public static bool IsValidPassword(string? password) => password is not null && password.Length >= MinPasswordLength;

    /// <summary>The key of the private chat between two users: the names sorted and joined with a colon.</summary>
    public static string DirectKey(string a, string b) =>
        string.CompareOrdinal(a, b) <= 0 ? $"{a}:{b}" : $"{b}:{a}";

    private static string? CleanLimited(string? text, int max)
    {
        string? trimmed = text?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > max ? null : trimmed;
    }
}
