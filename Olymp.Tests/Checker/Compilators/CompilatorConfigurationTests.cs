using System.Globalization;

using Olymp.Checker;
using Olymp.Checker.Compilators;

using Xunit;

namespace Olymp.Tests.Checker.Compilators;

public class CompilatorConfigurationTests
{
    public static TheoryData<Type, string, string, string> VersionExamples => new()
    {
        { typeof(GccCConfiguration), "16.2.0\n", "C (GCC 16.2)", "GCC 16.2.0" },
        { typeof(GccCpp23Configuration), "16.2.0\n", "C++23 (GCC 16.2)", "GCC 16.2.0" },
        { typeof(MsvcCConfiguration), "Microsoft (R) C/C++ Optimizing Compiler Version 19.51.36260 for x64\n",
            "C (MSVC 2026)", "MSVC 19.51.36260; Visual Studio 2026" },
        { typeof(MsvcCpp20Configuration), "Microsoft (R) C/C++ Optimizing Compiler Version 19.51.36260 for x64\n",
            "C++20 (MSVC 2026)", "MSVC 19.51.36260; Visual Studio 2026" },
        { typeof(CSharpFrameworkConfiguration), "5.11.0-1.26424.5 (5a2162bea6845eabc1fc721624a43568c65db3b1)\n    Version    REG_SZ    4.8.03761\n",
            "C# (.NET Framework 4.8)", "Compiler 5.11.0-1.26424.5; .NET Framework 4.8.03761" },
        { typeof(CSharpDotnetConfiguration), "10.0.403\nMicrosoft.AspNetCore.App 10.0.14 [C:\\dotnet]\nMicrosoft.NETCore.App 9.0.1 [C:\\dotnet]\nMicrosoft.NETCore.App 10.0.14 [C:\\dotnet]\nSupported language versions:\n14.0\n15.0 (default)\n",
            "C# 15 (.NET 10)", "C# 15.0; .NET 10.0.14; SDK 10.0.403" },
        { typeof(JavaConfiguration), "javac 27.0.1\nopenjdk version \"27.0.1\" 2026-10-25\nOpenJDK Runtime Environment (build 27.0.1+1)\n",
            "Java 27 (OpenJDK 27)", "javac 27.0.1; OpenJDK 27.0.1" },
        { typeof(KotlinConfiguration), "info: kotlinc-jvm 2.4.21 (JRE 27.0.1+1)\nopenjdk version \"27.0.1\" 2026-10-25\n",
            "Kotlin 2.4 (OpenJDK 27)", "Kotlin 2.4.21; OpenJDK 27.0.1" },
        { typeof(FreePascalConfiguration), "3.2.2\n", "Pascal (Free Pascal 3.2)", "Free Pascal 3.2.2" },
        { typeof(CPythonConfiguration), "Python 3.15.1\n", "Python 3.15 (CPython)", "CPython 3.15.1" },
        { typeof(PyPyConfiguration), "Python 3.12.15 (build info)\n[PyPy 8.0.1 with MSC v.1944 64 bit (AMD64)]\n",
            "Python 3.12 (PyPy 8)", "Python 3.12.15; PyPy 8.0.1" }
    };

    public static TheoryData<Type> ConfigurationTypes
    {
        get
        {
            var data = new TheoryData<Type>();
            foreach (var type in typeof(ICompilatorConfiguration).Assembly.GetTypes()
                .Where(type => type.IsClass && !type.IsAbstract && typeof(ICompilatorConfiguration).IsAssignableFrom(type)))
                data.Add(type);
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(VersionExamples))]
    public void VersionOutputProducesShortDisplayNameAndFullDescription(Type type, string output,
        string displayName, string description)
    {
        var configuration = Create(type);

        var result = configuration.ParseInfo(output);

        Assert.Same(configuration, result.Configuration);
        Assert.Equal(displayName, result.DisplayName);
        Assert.Equal(description, result.Description);
        Assert.Null(result.Error);
        var windowsResult = configuration.ParseInfo(output.Replace("\n", "\r\n"));
        Assert.Equal(result, windowsResult);
    }

    [Theory]
    [MemberData(nameof(ConfigurationTypes))]
    public void UnrecognizedVersionOutputIsRejected(Type type)
    {
        var configuration = Create(type);

        Assert.Throws<FormatException>(() => configuration.ParseInfo(""));
        Assert.Throws<FormatException>(() => configuration.ParseInfo("command not found: 123"));
    }

    [Theory]
    [InlineData(typeof(JavaConfiguration), "javac 27.0.1")]
    [InlineData(typeof(KotlinConfiguration), "info: kotlinc-jvm 2.4.21 (JRE 27.0.1)")]
    [InlineData(typeof(PyPyConfiguration), "Python 3.12.15")]
    [InlineData(typeof(CSharpFrameworkConfiguration), "5.11.0")]
    [InlineData(typeof(CSharpDotnetConfiguration), "10.0.403\n15.0 (default)")]
    [InlineData(typeof(CSharpDotnetConfiguration), "10.0.403\nMicrosoft.NETCore.App 10.0.14 [C:\\dotnet]")]
    public void MissingRequiredRuntimeOrCompilerInformationIsRejected(Type type, string output)
    {
        Assert.Throws<FormatException>(() => Create(type).ParseInfo(output));
    }

    [Theory]
    [InlineData(typeof(GccCConfiguration), "16.2.0", "16.2.1")]
    [InlineData(typeof(CPythonConfiguration), "Python 3.15.1", "Python 3.15.2")]
    [InlineData(typeof(FreePascalConfiguration), "3.2.2", "3.2.4")]
    [InlineData(typeof(PyPyConfiguration), "Python 3.12.15\n[PyPy 8.0.1]", "Python 3.12.16\n[PyPy 8.0.2]")]
    public void PatchUpdatesPreserveDisplayNameButUpdateDiagnostics(Type type, string before, string after)
    {
        var configuration = Create(type);
        var oldVersion = configuration.ParseInfo(before);
        var newVersion = configuration.ParseInfo(after);

        Assert.Equal(oldVersion.DisplayName, newVersion.DisplayName);
        Assert.NotEqual(oldVersion.Description, newVersion.Description);
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("ru-RU")]
    public void VersionFormattingDoesNotDependOnCulture(string culture)
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            foreach (var example in VersionExamples)
                Assert.Equal(example.Data.Item3, Create(example.Data.Item1).ParseInfo(example.Data.Item2).DisplayName);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Theory]
    [MemberData(nameof(ConfigurationTypes))]
    public void SourceAndExecutableCommandsAgreeAndHaveNoUnexpandedPlaceholders(Type type)
    {
        var configuration = Create(type);
        var filename = configuration.GetSourceFileName("answer");
        var compile = configuration.GetCompileCommand("answer");
        var run = configuration.RunCommand("answer", 128);
        string artifact = configuration switch
        {
            JavaConfiguration => "Main",
            KotlinConfiguration => "answer.jar",
            CPythonConfiguration or PyPyConfiguration => "answer.pyc",
            CSharpDotnetConfiguration => "answer.dll",
            _ => "answer.exe"
        };

        Assert.EndsWith(configuration.SourceExtension, filename);
        Assert.Contains(filename, compile);
        Assert.DoesNotContain("!", compile);
        Assert.Contains(artifact, run);
        if (configuration is JavaConfiguration)
            Assert.Equal("Main.java", filename);
    }

    [Theory]
    [InlineData(typeof(JavaConfiguration))]
    [InlineData(typeof(KotlinConfiguration))]
    public void JvmHeapLimitUsesProblemLimitWithoutProcessOverhead(Type type)
    {
        var configuration = Create(type);

        Assert.Contains("-Xmx128M", configuration.RunCommand("answer", 128));
        Assert.Contains("-Xmx256M", configuration.RunCommand("answer", 256));
        Assert.True(configuration.MemoryOverheadMb > 0);
    }

    [Fact]
    public void CppStandardsAndSourceEncodingMatchAdvertisedProfiles()
    {
        Assert.Contains("-std=c++23", new GccCpp23Configuration().GetCompileCommand("answer"));
        Assert.Contains("/std:c++20", new MsvcCpp20Configuration().GetCompileCommand("answer"));
        Assert.Contains("/utf-8", new MsvcCConfiguration().GetCompileCommand("answer"));
        Assert.Contains("/utf-8", new MsvcCpp20Configuration().GetCompileCommand("answer"));
        Assert.Contains("-FcUTF8", new FreePascalConfiguration().GetCompileCommand("answer"));
    }

    [Fact]
    public void EveryCompilerHasUniqueIdentityVersionExampleAndMatchingSelfTests()
    {
        var configurations = ConfigurationTypes.Select(row => Create(row.Data)).ToArray();
        var tests = typeof(ICheckerTest).Assembly.GetTypes()
            .Where(type => type.IsClass && !type.IsAbstract && typeof(ICheckerTest).IsAssignableFrom(type))
            .Select(type => (ICheckerTest)Activator.CreateInstance(type)!).ToArray();

        Assert.NotEmpty(configurations);
        Assert.Equal(configurations.Length, configurations.Select(configuration => configuration.Name).Distinct().Count());
        Assert.Equal(configurations.Select(configuration => configuration.GetType()).OrderBy(type => type.Name),
            VersionExamples.Select(row => row.Data.Item1).OrderBy(type => type.Name));
        foreach (var configuration in configurations)
            Assert.Contains(tests, test => test.Language == configuration.Language
                && (test.CompilatorNames.Count == 0 || test.CompilatorNames.Contains(configuration.Name)));
        foreach (var test in tests)
            Assert.Contains(configurations, configuration => test.Language == configuration.Language
                && (test.CompilatorNames.Count == 0 || test.CompilatorNames.Contains(configuration.Name)));
    }

    private static ICompilatorConfiguration Create(Type type) => (ICompilatorConfiguration)Activator.CreateInstance(type)!;
}
