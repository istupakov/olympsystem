using System.Globalization;

namespace Olymp.WebApp.Components.Staff;

public static class ByteSize
{
    public static string Format(int bytes) => bytes < 1024 ? $"{bytes} B"
        : bytes < 1024 * 1024 ? (bytes / 1024d).ToString("0.#", CultureInfo.CurrentCulture) + " KiB"
        : (bytes / (1024d * 1024)).ToString("0.#", CultureInfo.CurrentCulture) + " MiB";
}
