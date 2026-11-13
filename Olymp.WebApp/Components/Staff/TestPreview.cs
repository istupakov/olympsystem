using System.Text;

namespace Olymp.WebApp.Components.Staff;

public sealed record TestPreview(string? Text, bool Truncated)
{
    public const int ByteLimit = 16 * 1024;
    public const int LineLimit = 200;

    public static TestPreview Create(byte[] prefix, int totalLength)
    {
        bool truncated = prefix.Length < totalLength;
        int length = Math.Min(prefix.Length, ByteLimit);
        string text;
        try
        {
            var decoder = new UTF8Encoding(false, true).GetDecoder();
            var chars = new char[length];
            decoder.Convert(prefix.AsSpan(0, length), chars, flush: !truncated, out _, out int used, out _);
            text = new string(chars, 0, used);
        }
        catch (DecoderFallbackException)
        {
            return new(null, truncated);
        }
        if (text.Any(c => char.IsControl(c) && c is not ('\r' or '\n' or '\t')))
            return new(null, truncated);
        int lines = 1;
        for (int index = 0; index < text.Length; index++)
        {
            if ((text[index] == '\n' || (text[index] == '\r' && (index + 1 == text.Length || text[index + 1] != '\n')))
                && ++lines > LineLimit)
                return new(text[..index], true);
        }
        return new(text, truncated || prefix.Length > ByteLimit);
    }
}
