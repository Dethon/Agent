namespace Domain.Extensions;

public static class StringExtensions
{
    public static string Left(this string str, int count)
    {
        return str.Length <= count ? str : str[..count];
    }
}