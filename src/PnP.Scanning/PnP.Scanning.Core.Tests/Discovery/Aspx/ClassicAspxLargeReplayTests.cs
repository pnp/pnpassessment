using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using PnP.Scanning.Core.Discovery.Aspx;
using Xunit;

namespace PnP.Scanning.Core.Tests.Discovery.Aspx;

public sealed class ClassicAspxLargeReplayTests
{
    /// <summary>Streams ten thousand distinct physical files in bounded batches and keeps one inventory row per identity after replay.</summary>
    [Fact]
    public async Task TenThousandPhysicalFilesRemainBoundedAndReplayIdempotent()
    {
        await using var scenario = new ClassicAspxStreamingFixture();
        await scenario.InitializeAsync();
        var scanId = Guid.NewGuid();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await new ClassicAspxWebAcquisition(ClassicAspxStreamingFixture.Context(scanId)).AcquireAsync(
                ClassicAspxStreamingFixture.Pages(scanId, 10000), scenario.PublishAsync);
        }
        using var context = scenario.CreateContext();
        (await context.ClassicAspxInventories.CountAsync()).Should().Be(10000);
        scenario.LargestPublishedBatch.Should().BeInRange(1, 128);
    }

    /// <summary>Propagates a publication failure and converges on the same unique file set after an explicit replay.</summary>
    [Fact]
    public async Task ReplayAfterFailedPublicationDoesNotDuplicatePhysicalFiles()
    {
        await using var scenario = new ClassicAspxStreamingFixture();
        await scenario.InitializeAsync();
        var scanId = Guid.NewGuid();
        int publication = 0;
        var acquire = () => new ClassicAspxWebAcquisition(ClassicAspxStreamingFixture.Context(scanId)).AcquireAsync(
            ClassicAspxStreamingFixture.Pages(scanId, 1000), async (batch, token) =>
            {
                if (++publication == 2)
                {
                    throw new IOException("publication sentinel");
                }
                await scenario.PublishAsync(batch, token);
            });
        await acquire.Should().ThrowAsync<IOException>().WithMessage("publication sentinel");
        await new ClassicAspxWebAcquisition(ClassicAspxStreamingFixture.Context(scanId)).AcquireAsync(
            ClassicAspxStreamingFixture.Pages(scanId, 1000), scenario.PublishAsync);
        using var context = scenario.CreateContext();
        (await context.ClassicAspxInventories.CountAsync()).Should().Be(1000);
        scenario.LargestPublishedBatch.Should().BeInRange(1, 128);
    }
}
