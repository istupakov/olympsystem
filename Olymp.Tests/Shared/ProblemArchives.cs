using System.IO.Compression;
using System.Text.Json;

namespace Olymp.Tests.Shared;

public static class ProblemArchives
{
    public static byte[] Zip(params (string Name, byte[] Data)[] files)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipArchive(memory, ZipArchiveMode.Create, true))
            foreach (var (name, data) in files)
            {
                using var output = zip.CreateEntry(name).Open();
                output.Write(data);
            }
        return memory.ToArray();
    }

    public static byte[] ProblemZip(int number = 1, string name = "Задача", int openTestCount = 1,
        Dictionary<int, int>? scores = null, int checkerId = 4) => Zip(
        ("A/problem.json", JsonSerializer.SerializeToUtf8Bytes(new { number, name, openTestCount, scores, checkerId })),
        ("A/input01.txt", [0, 255, 13, 10]), ("A/output01.txt", [1, 2]),
        ("A/input02.txt", [3]), ("A/output02.txt", [4]));
}
