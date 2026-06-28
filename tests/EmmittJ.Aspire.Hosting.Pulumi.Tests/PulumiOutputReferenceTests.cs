// Licensed under the MIT License.

using EmmittJ.Aspire.Hosting.Pulumi;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

public class PulumiOutputReferenceTests
{
    [Fact]
    public void ValueExpression_UsesResourceAndOutputName()
    {
        var resource = new TestResource("cache");
        var reference = new PulumiOutputReference("connectionString", resource);

        Assert.Equal("{cache.outputs.connectionString}", reference.ValueExpression);
    }

    [Fact]
    public async Task GetValueAsync_CompletesAfterSetValue()
    {
        var reference = new PulumiOutputReference("url", new TestResource("api"));

        var pending = reference.GetValueAsync(TestContext.Current.CancellationToken);
        Assert.False(pending.IsCompleted);

        reference.SetValue("https://example.com");

        Assert.Equal("https://example.com", await pending);
        Assert.Equal("https://example.com", reference.Value);
    }

    [Fact]
    public async Task GetValueAsync_FaultsAfterSetException()
    {
        var reference = new PulumiOutputReference("url", new TestResource("api"));

        var pending = reference.GetValueAsync(TestContext.Current.CancellationToken);
        reference.SetException(new InvalidOperationException("deploy failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => await pending);
    }
}
