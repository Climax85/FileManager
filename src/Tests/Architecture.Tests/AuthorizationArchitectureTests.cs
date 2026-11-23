using Shouldly;
using System.Xml.Linq;
using Xunit;

namespace Architecture.Tests;

public sealed class AuthorizationArchitectureTests
{
    [Fact]
    public void AuthzedAndGrpc_AreRestrictedToAuthorizationRuntime()
    {
        // Arrange
        string modulesRoot = Path.Combine(ModuleArchitectureTestsFixture.SolutionRoot, "src", "Modules");

        // Act
        var offenders = Directory.GetFiles(modulesRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("Modules.Authorization.csproj", StringComparison.OrdinalIgnoreCase))
            .Where(path => XDocument.Load(path)
                .Descendants("PackageReference")
                .Any(reference => reference.Attribute("Include")?.Value is "Authzed.Net" or "Grpc.Net.Client"))
            .ToArray();

        // Assert
        offenders.ShouldBeEmpty("provider SDK dependencies must not leak into contracts or downstream modules");
    }
}
