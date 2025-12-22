namespace Domain.Extensions;

public static class StringExtensions
{
    extension(string str)
    {
        public string Left(int count)
        {
            return str.Length <= count ? str : str[..count];
        }

        public string HtmlSanitize()
        {
            return str
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }
    }
}