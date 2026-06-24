using Xunit;

namespace Bonds.IntegrationTests.Infrastructure;

[CollectionDefinition("Integration")]
public class IntegrationCollection : ICollectionFixture<TestWebApplicationFactory> { }
