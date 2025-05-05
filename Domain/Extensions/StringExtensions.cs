namespace Domain.Extensions;

public static class StringExtensions
{
    public static string Left(this string str, int count)
    {
        return str.Length <= count ? str : str[..count];
    }
    
    public static string HtmlSanitize(this string str)
    {
        return str
            .Replace("&", "&amp;")
            .Replace("<", "&lt;")
            .Replace(">", "&gt;");

    }
}