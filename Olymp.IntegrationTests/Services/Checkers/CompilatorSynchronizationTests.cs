using Xunit;

using static Olymp.IntegrationTests.Services.Checkers.CheckerTestData;

namespace Olymp.IntegrationTests.Services.Checkers;

[Trait("Category", "Integration")]
public partial class CompilatorSynchronizationTests(ServiceFactory factory) : IClassFixture<ServiceFactory>
{
    private static TestConfiguration Configuration() => new() { Name = "compiler-" + Guid.NewGuid().ToString("N") };

}
