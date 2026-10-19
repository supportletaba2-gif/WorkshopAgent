namespace WorkshopLink.Protocol;

public static class PasswordPolicy
{
    public static string? Problem(string? password)
    {
        password ??= "";
        if (password.Length < 12)
            return "Use a password of at least 12 characters.";
        if (password.Contains('"'))
            return "The password cannot contain a quote.";
        if (!password.Any(char.IsUpper)
            || !password.Any(char.IsLower)
            || !password.Any(ch => !char.IsLetter(ch)))
            return "Use upper-case, lower-case, and a number or symbol.";
        return null;
    }
}
