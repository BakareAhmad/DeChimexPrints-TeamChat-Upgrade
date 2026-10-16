using System.Text.Json;
using DeChimex.Domain.Entities;
using DeChimex.Domain.Enums;
using DeChimex.Domain.Repositories;
using DeChimex.Host.Services;
using DeChimex.Infrastructure.Options;
using DeChimex.Persistence.Context;
using DeChimex.Shared.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace DeChimex.IntegrationTests.Services;

/// <summary>
/// Exercises the real recipient snapshot and marketing worker with the integration
/// suite's recording email service. No provider credentials or live email service are used.
/// </summary>
public sealed class MarketingBroadcastProcessorTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public MarketingBroadcastProcessorTests(CustomWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task Worker_RendersEachSubscribedRecipientSeparately_EscapesNames_AndFallsBackWhenMissing()
    {
        var emailA = $"campaign-ada-{Guid.NewGuid():N}@test.local";
        var emailB = $"campaign-tunde-{Guid.NewGuid():N}@test.local";
        var emailBlank = $"campaign-blank-{Guid.NewGuid():N}@test.local";
        var emailOptedOut = $"campaign-optout-{Guid.NewGuid():N}@test.local";
        var firstNameA = "Nia & <b>Collective</b>";

        // These two test customers have no login account, but are registered, consented,
        // non-guest records with real email addresses exactly like the marketing audience query expects.
        var customerA = Customer.Create(firstNameA, "One", emailA);
        var customerB = Customer.Create("Tunde", "Two", emailB);
        var customerOptedOut = Customer.Create("Rae", "Later", emailOptedOut);
        var blankUserId = await TestSeed.RegisterCustomerAsync(
            _factory.Services, emailBlank, firstName: string.Empty, lastName: string.Empty);

        var contentJson = JsonSerializer.Serialize(new
        {
            blocks = new[]
            {
                new { type = "heading", text = "Welcome, {{ FirstName }}" },
                new { type = "paragraph", text = "Hi {{FirstName}}, enjoy & <great> prints." }
            }
        });
        var compiled = MarketingContentComposer.Build(contentJson);
        Assert.True(compiled.Success, compiled.Error);

        Guid campaignId;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
            db.Customers.AddRange(customerA, customerB, customerOptedOut);
            await db.SaveChangesAsync();

            var blankCustomer = await db.Customers.SingleAsync(c => c.UserId == blankUserId);
            Assert.Equal(string.Empty, blankCustomer.FirstName);
            Assert.True(blankCustomer.MarketingSubscribed);

            var ids = new HashSet<Guid> { customerA.Id, customerB.Id, blankCustomer.Id, customerOptedOut.Id };
            var eligible = await scope.ServiceProvider.GetRequiredService<IMarketingRepository>()
                .GetEligibleRecipientsAsync(MarketingAudienceSegment.AllSubscribed, DateTime.UtcNow);
            var snapshots = eligible.Where(r => ids.Contains(r.CustomerId)).ToArray();
            Assert.Equal(4, snapshots.Length);
            Assert.Equal(firstNameA, snapshots.Single(r => r.CustomerId == customerA.Id).FirstName);
            Assert.Equal("Tunde", snapshots.Single(r => r.CustomerId == customerB.Id).FirstName);
            Assert.Equal(string.Empty, snapshots.Single(r => r.CustomerId == blankCustomer.Id).FirstName);

            var campaign = MarketingBroadcast.Create(
                "Per-recipient personalization regression",
                "Print idea for {{ FirstName }}",
                "A note for {{FirstName }}",
                "DE-CHIMEX Campaign Studio",
                "campaigns@example.com",
                MarketingAudienceSegment.AllSubscribed,
                contentJson,
                compiled.Html,
                compiled.PlainText,
                "integration-test");
            campaign.Queue(snapshots, DateTime.UtcNow);
            db.MarketingBroadcasts.Add(campaign);
            await db.SaveChangesAsync();
            campaignId = campaign.Id;

            // Consent is withdrawn after the audience snapshot but before worker dispatch.
            customerOptedOut.UnsubscribeFromMarketing();
            await db.SaveChangesAsync();
        }

        var emailService = Assert.IsType<Fakes.FakeEmailService>(
            _factory.Services.GetRequiredService<DeChimex.Application.Common.Interfaces.IEmailService>());
        var processor = new MarketingBroadcastProcessor(
            _factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new MarketingOptions { AllowBroadcasts = true, BatchSize = 20 }),
            _factory.Services.GetRequiredService<ILogger<MarketingBroadcastProcessor>>());

        // Explicitly invoke the production worker processor with its sending gate armed, but
        // with IEmailService replaced by the test's in-memory recorder. No live campaign is sent.
        await processor.ProcessPendingAsync();

        var campaignMessages = emailService.Sent
            .Where(m => m.Template == "Marketing" &&
                        new[] { emailA, emailB, emailBlank, emailOptedOut }.Contains(m.ToEmail, StringComparer.OrdinalIgnoreCase))
            .ToList();
        Assert.Equal(3, campaignMessages.Count);
        Assert.DoesNotContain(campaignMessages, m => string.Equals(m.ToEmail, emailOptedOut, StringComparison.OrdinalIgnoreCase));

        var messageA = Assert.Single(campaignMessages, m => string.Equals(m.ToEmail, emailA, StringComparison.OrdinalIgnoreCase));
        Assert.Equal($"Print idea for {firstNameA}", messageA.Subject);
        Assert.Equal(firstNameA, messageA.ToName);
        Assert.Contains("Welcome, Nia &amp; &lt;b&gt;Collective&lt;/b&gt;", messageA.HtmlBody);
        Assert.Contains("Hi Nia &amp; &lt;b&gt;Collective&lt;/b&gt;, enjoy &amp; &lt;great&gt; prints.", messageA.HtmlBody);
        Assert.DoesNotContain("<b>Collective</b>", messageA.HtmlBody);
        Assert.Contains("A note for Nia &amp; &lt;b&gt;Collective&lt;/b&gt;", messageA.HtmlBody);

        var messageB = Assert.Single(campaignMessages, m => string.Equals(m.ToEmail, emailB, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Print idea for Tunde", messageB.Subject);
        Assert.Equal("Tunde", messageB.ToName);
        Assert.Contains("Welcome, Tunde", messageB.HtmlBody);
        Assert.DoesNotContain("Nia", messageB.HtmlBody);

        var blankMessage = Assert.Single(campaignMessages, m => string.Equals(m.ToEmail, emailBlank, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Print idea for there", blankMessage.Subject);
        Assert.Equal("there", blankMessage.ToName);
        Assert.Contains("Welcome, there", blankMessage.HtmlBody);
        Assert.Contains("A note for there", blankMessage.HtmlBody);

        await using var verifyScope = _factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        var completed = await verifyDb.MarketingBroadcasts
            .Include(b => b.Recipients)
            .SingleAsync(b => b.Id == campaignId);
        Assert.Equal(MarketingBroadcastStatus.Completed, completed.Status);
        Assert.Equal(3, completed.SentCount);
        Assert.Equal(1, completed.FailedCount);
        Assert.Equal(MarketingRecipientStatus.Failed,
            completed.Recipients.Single(r => r.CustomerId == customerOptedOut.Id).Status);
    }
}
