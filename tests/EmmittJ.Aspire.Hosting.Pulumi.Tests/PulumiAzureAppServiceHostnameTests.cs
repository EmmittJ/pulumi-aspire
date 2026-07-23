// Licensed under the MIT License.

using EmmittJ.Aspire.Hosting.Pulumi.Azure.AppService;
using Xunit;

namespace EmmittJ.Aspire.Hosting.Pulumi.Tests;

public class PulumiAzureAppServiceHostnameTests
{
    private const string Suffix = "ab12cd34ef";

    [Fact]
    public void SiteName_CombinesAppNameAndSuffix()
    {
        var siteName = PulumiAzureAppServiceWebsiteContext.BuildSiteName("frontend", Suffix);

        Assert.Equal($"frontend-{Suffix}", siteName);
    }

    [Fact]
    public void SiteName_IsLowercased()
    {
        // Site names are DNS labels; App Service requires lowercase.
        var siteName = PulumiAzureAppServiceWebsiteContext.BuildSiteName("FrontEnd", Suffix);

        Assert.Equal($"frontend-{Suffix}", siteName);
    }

    [Fact]
    public void SiteName_IsTruncatedTo60Characters()
    {
        // Mirrors Aspire's take(toLower("{name}-{uniqueString(...)}"), 60).
        var longName = new string('a', 70);

        var siteName = PulumiAzureAppServiceWebsiteContext.BuildSiteName(longName, Suffix);

        Assert.Equal(60, siteName.Length);
        Assert.StartsWith(new string('a', 49), siteName);
    }

    [Fact]
    public void Host_AppendsAzureWebsitesDomain()
    {
        var host = PulumiAzureAppServiceWebsiteContext.BuildHost("frontend", Suffix);

        Assert.Equal($"frontend-{Suffix}.azurewebsites.net", host);
    }
}
