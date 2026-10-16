using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Infrastructure.Options;
using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using DeChimex.Shared.Constants;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Internal (work) messaging (owner directive 2026-10-07): one-to-one conversations
/// between authorized roles only — Owner/Manager/Staff/Developer per the pair matrix,
/// staff-to-staff and staff-to-developer denied server-side, customers excluded, the
/// developer served by the portal side, unread counts raised and cleared, and the
/// recipient notified. Kept separate from customer/order conversations.
/// </summary>
public class TeamMessagesTests
{
    private const string Password = "TeamMsg!2026x";

    private static HttpClient Browser(CustomWebApplicationFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private static async Task<HttpClient> LoginAsync(CustomWebApplicationFactory factory, string email)
    {
        var client = Browser(factory);
        (await TestSeed.LoginMvcAsync(client, email, Password)).Should().BeTrue($"{email} should sign in");
        return client;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string tokenPageUrl, string postUrl, Dictionary<string, string> fields)
    {
        var page = await client.GetAsync(tokenPageUrl);
        var html = await page.Content.ReadAsStringAsync();
        fields["__RequestVerificationToken"] = TestSeed.ExtractAntiforgeryToken(html);
        return await client.PostAsync(postUrl, new FormUrlEncodedContent(fields));
    }

    private sealed record UploadPart(string Name, string ContentType, string Kind, byte[] Bytes, int? Duration = null);

    private static async Task<HttpResponseMessage> PostMultipartAsync(
        HttpClient client, string tokenPageUrl, string postUrl, string body, Guid clientMessageId,
        params UploadPart[] files)
    {
        var page = await client.GetAsync(tokenPageUrl);
        var html = await page.Content.ReadAsStringAsync();
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(TestSeed.ExtractAntiforgeryToken(html)), "__RequestVerificationToken");
        form.Add(new StringContent(body), "body");
        form.Add(new StringContent(clientMessageId.ToString()), "clientMessageId");
        foreach (var part in files)
        {
            var content = new ByteArrayContent(part.Bytes);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(part.ContentType);
            form.Add(content, "files", part.Name);
            form.Add(new StringContent(part.Kind), "kinds");
            form.Add(new StringContent(part.Duration?.ToString() ?? string.Empty), "durations");
        }
        using var request = new HttpRequestMessage(HttpMethod.Post, postUrl) { Content = form };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await client.SendAsync(request);
    }

    private static byte[] TestWave()
    {
        const int sampleRate = 8000;
        const int sampleCount = 1600;
        const int dataSize = sampleCount * sizeof(short);
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataSize);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1); // PCM
        writer.Write((short)1); // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataSize);
        for (var i = 0; i < sampleCount; i++)
            writer.Write((short)(10000 * Math.Sin(2 * Math.PI * 440 * i / sampleRate)));
        writer.Flush();
        return stream.ToArray();
    }

    private static async Task<int> UnreadCountAsync(HttpClient client, string base_)
    {
        var response = await client.GetAsync($"{base_}/unread-count");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("count").GetInt32();
    }

    private sealed record Team(
        CustomWebApplicationFactory Factory, HttpClient Owner, HttpClient Manager,
        HttpClient Staff, HttpClient Staff2, HttpClient Dev, HttpClient Customer);

    private static async Task<Team> SeedTeamAsync()
    {
        var factory = new CustomWebApplicationFactory();
        await TestSeed.CreateStaffUserAsync(factory.Services, "manager@team.test", Password, RoleNames.Manager, "Mary", "Manager");
        await TestSeed.CreateStaffUserAsync(factory.Services, "sam@team.test", Password, RoleNames.SalesStaff, "Sam", "Staff");
        await TestSeed.CreateStaffUserAsync(factory.Services, "sue@team.test", Password, RoleNames.SalesStaff, "Sue", "Staff");
        await TestSeed.CreateStaffUserAsync(factory.Services, "dev@team.test", Password, RoleNames.Developer, "Dee", "Dev");
        await TestSeed.RegisterCustomerAsync(factory.Services, "cust@team.test", Password);
        await TestSeed.VerifyCustomerAsync(factory.Services, "cust@team.test");

        var owner = await TestSeed.LoginOwnerMvcAsync(factory.Services, () => Browser(factory));
        var manager = await LoginAsync(factory, "manager@team.test");
        var staff = await LoginAsync(factory, "sam@team.test");
        var staff2 = await LoginAsync(factory, "sue@team.test");
        var dev = await LoginAsync(factory, "dev@team.test");
        var customer = await LoginAsync(factory, "cust@team.test");
        return new Team(factory, owner, manager, staff, staff2, dev, customer);
    }

    private static async Task<Guid> UserIdOf(CustomWebApplicationFactory factory, string email)
    {
        using var scope = factory.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<DeChimex.Domain.Repositories.IUserRepository>();
        var user = await users.GetByEmailAsync(email);
        user.Should().NotBeNull();
        return user!.Id;
    }

    [Fact]
    public async Task Contact_lists_follow_the_matrix_and_never_offer_disallowed_users()
    {
        var team = await SeedTeamAsync();
        using var factory = team.Factory;
        var owner = team.Owner;
        var manager = team.Manager;
        var staff = team.Staff;
        var dev = team.Dev;
        var customer = team.Customer;

        // Owner sees management, staff and the developer.
        var ownerHtml = await (await owner.GetAsync("/admin/team-messages")).Content.ReadAsStringAsync();
        ownerHtml.Should().Contain("Mary Manager").And.Contain("Sam Staff").And.Contain("Dee Dev");

        // Staff sees only management: no colleagues, no developer.
        var staffHtml = await (await staff.GetAsync("/admin/team-messages")).Content.ReadAsStringAsync();
        staffHtml.Should().Contain("Mary Manager");
        staffHtml.Should().NotContain("Sue Staff", "staff-to-staff conversations are not allowed");
        staffHtml.Should().NotContain("Dee Dev", "staff-to-developer conversations are not allowed");

        // Developer sees only management, on the portal side.
        var devHtml = await (await dev.GetAsync("/dev/messages")).Content.ReadAsStringAsync();
        devHtml.Should().Contain("Mary Manager");
        devHtml.Should().NotContain("Sam Staff").And.NotContain("Sue Staff");

        // Customer has no access at all.
        var customerResponse = await customer.GetAsync("/admin/team-messages");
        customerResponse.StatusCode.Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Staff_cannot_start_staff_or_developer_conversations_or_read_others()
    {
        var team = await SeedTeamAsync();
        using var factory = team.Factory;
        var owner = team.Owner;
        var manager = team.Manager;
        var staff = team.Staff;
        var dev = team.Dev;
        var customer = team.Customer;
        var sueId = await UserIdOf(factory, "sue@team.test");
        var devId = await UserIdOf(factory, "dev@team.test");

        // Staff -> staff denied (reads as a plain redirect back, nothing created).
        var deniedStaff = await PostFormAsync(staff, "/admin/team-messages", "/admin/team-messages/start",
            new Dictionary<string, string> { ["otherUserId"] = sueId.ToString() });
        deniedStaff.StatusCode.Should().Be(HttpStatusCode.Redirect);
        deniedStaff.Headers.Location!.ToString().Should().Be("/admin/team-messages");

        // Staff -> developer denied the same way.
        var deniedDev = await PostFormAsync(staff, "/admin/team-messages", "/admin/team-messages/start",
            new Dictionary<string, string> { ["otherUserId"] = devId.ToString() });
        deniedDev.Headers.Location!.ToString().Should().Be("/admin/team-messages");

        var staffHtml = await (await staff.GetAsync("/admin/team-messages")).Content.ReadAsStringAsync();
        staffHtml.Should().NotContain("Sue Staff").And.NotContain("Dee Dev", "no conversation may exist for a denied pair");

        // Owner<->Manager conversation is invisible to staff (no discovery).
        var start = await PostFormAsync(owner, "/admin/team-messages", "/admin/team-messages/start",
            new Dictionary<string, string> { ["otherUserId"] = (await UserIdOf(factory, "manager@team.test")).ToString() });
        var conversationUrl = start.Headers.Location!.ToString();
        var staffPeek = await staff.GetAsync(conversationUrl);
        staffPeek.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Owner_manager_flow_raises_unread_counts_notification_and_clears_on_open()
    {
        var team = await SeedTeamAsync();
        using var factory = team.Factory;
        var owner = team.Owner;
        var manager = team.Manager;
        var staff = team.Staff;
        var dev = team.Dev;
        var customer = team.Customer;
        var managerId = await UserIdOf(factory, "manager@team.test");

        var start = await PostFormAsync(owner, "/admin/team-messages", "/admin/team-messages/start",
            new Dictionary<string, string> { ["otherUserId"] = managerId.ToString() });
        start.StatusCode.Should().Be(HttpStatusCode.Redirect);
        var conversationUrl = start.Headers.Location!.ToString();
        conversationUrl.Should().StartWith("/admin/team-messages/");

        (await UnreadCountAsync(manager, "/admin/team-messages")).Should().Be(0);

        var send = await PostFormAsync(owner, conversationUrl, conversationUrl + "/send",
            new Dictionary<string, string> { ["body"] = "Mary, are the weekly reports ready?" });
        send.StatusCode.Should().Be(HttpStatusCode.Redirect);

        (await UnreadCountAsync(manager, "/admin/team-messages")).Should().Be(1, "the recipient has one unread message");

        // The in-app notification exists for the recipient and links to the conversation.
        var notifications = await (await manager.GetAsync("/admin/notifications")).Content.ReadAsStringAsync();
        notifications.Should().Contain("Internal message from").And.Contain(conversationUrl);

        // Inbox preview shows the message; opening the conversation clears the unread state.
        var inbox = await (await manager.GetAsync("/admin/team-messages")).Content.ReadAsStringAsync();
        inbox.Should().Contain("Mary, are the weekly reports ready?");
        var page = await (await manager.GetAsync(conversationUrl)).Content.ReadAsStringAsync();
        page.Should().Contain("Mary, are the weekly reports ready?").And.Contain("Owner", "the sender is clearly shown");
        (await UnreadCountAsync(manager, "/admin/team-messages")).Should().Be(0, "opening the conversation clears unread");
    }

    [Fact]
    public async Task Developer_uses_the_portal_side_and_roles_cannot_cross_sides()
    {
        var team = await SeedTeamAsync();
        using var factory = team.Factory;
        var owner = team.Owner;
        var manager = team.Manager;
        var staff = team.Staff;
        var dev = team.Dev;
        var customer = team.Customer;
        var managerId = await UserIdOf(factory, "manager@team.test");

        // Developer starts a conversation with the manager from the portal.
        var start = await PostFormAsync(dev, "/dev/messages", "/dev/messages/start",
            new Dictionary<string, string> { ["otherUserId"] = managerId.ToString() });
        var conversationUrl = start.Headers.Location!.ToString();
        conversationUrl.Should().StartWith("/dev/messages/");

        var send = await PostFormAsync(dev, conversationUrl, conversationUrl + "/send",
            new Dictionary<string, string> { ["body"] = "Deployment window confirmed for tonight." });
        send.StatusCode.Should().Be(HttpStatusCode.Redirect);

        // Manager sees it on the admin side with the developer's name, and the
        // notification link points at the ADMIN side for the manager.
        (await UnreadCountAsync(manager, "/admin/team-messages")).Should().Be(1);
        var inbox = await (await manager.GetAsync("/admin/team-messages")).Content.ReadAsStringAsync();
        inbox.Should().Contain("Dee Dev").And.Contain("Deployment window confirmed for tonight.");

        // Side separation: developer cannot use the admin area, staff cannot use the dev portal.
        (await dev.GetAsync("/admin/team-messages")).StatusCode
            .Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect, HttpStatusCode.NotFound);
        (await staff.GetAsync("/dev/messages")).StatusCode
            .Should().BeOneOf(HttpStatusCode.Forbidden, HttpStatusCode.Redirect, HttpStatusCode.NotFound);

        // Developer unread badge endpoint lives on the portal side.
        (await UnreadCountAsync(dev, "/dev/messages")).Should().Be(0, "the sender has no unread messages");
    }

    [Fact]
    public async Task Message_body_validation_rejects_empty_and_overlong_bodies()
    {
        var team = await SeedTeamAsync();
        using var factory = team.Factory;
        var owner = team.Owner;
        var manager = team.Manager;
        var staff = team.Staff;
        var dev = team.Dev;
        var customer = team.Customer;
        var managerId = await UserIdOf(factory, "manager@team.test");
        var start = await PostFormAsync(owner, "/admin/team-messages", "/admin/team-messages/start",
            new Dictionary<string, string> { ["otherUserId"] = managerId.ToString() });
        var conversationUrl = start.Headers.Location!.ToString();

        // Empty body: redirected back, nothing persisted.
        await PostFormAsync(owner, conversationUrl, conversationUrl + "/send",
            new Dictionary<string, string> { ["body"] = "   " });
        // Overlong body (2001 chars): redirected back, nothing persisted.
        await PostFormAsync(owner, conversationUrl, conversationUrl + "/send",
            new Dictionary<string, string> { ["body"] = new string('b', 2001) });

        var page = await (await owner.GetAsync(conversationUrl)).Content.ReadAsStringAsync();
        page.Should().NotContain(new string('b', 2001), "overlong bodies are rejected");
        page.Should().Contain("No messages yet", "nothing invalid was persisted");

        // Boundary body (exactly 2000 chars) is accepted.
        await PostFormAsync(owner, conversationUrl, conversationUrl + "/send",
            new Dictionary<string, string> { ["body"] = new string('c', 2000) });
        page = await (await owner.GetAsync(conversationUrl)).Content.ReadAsStringAsync();
        page.Should().Contain(new string('c', 2000), "the 2000-character boundary is accepted");
    }

    [Fact]
    public async Task Team_chat_uploads_are_normalized_idempotent_and_private_to_the_pair()
    {
        var team = await SeedTeamAsync();
        using var factory = team.Factory;
        var owner = team.Owner;
        var manager = team.Manager;
        var staff2 = team.Staff2;
        var customer = team.Customer;
        var managerId = await UserIdOf(factory, "manager@team.test");
        var start = await PostFormAsync(owner, "/admin/team-messages", "/admin/team-messages/start",
            new Dictionary<string, string> { ["otherUserId"] = managerId.ToString() });
        var conversationUrl = start.Headers.Location!.ToString();
        var clientId = Guid.NewGuid();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var wave = TestWave();
        var storedFileNames = new List<string>();

        try
        {
            // Extension is authoritative: deliberately mislabel a PNG as HTML. A valid WAV
            // exercises the same private upload route and the voice-note renderer metadata.
            var first = await PostMultipartAsync(owner, conversationUrl, conversationUrl + "/send", "", clientId,
                new UploadPart("proof.png", "text/html", "file", png),
                new UploadPart("voice.wav", "audio/wav", "voice", wave, 1));
            first.StatusCode.Should().Be(HttpStatusCode.OK);
            using var firstJson = JsonDocument.Parse(await first.Content.ReadAsStringAsync());
            var message = firstJson.RootElement.GetProperty("message");
            var messageId = message.GetProperty("id").GetGuid();
            var conversationId = message.GetProperty("conversationId").GetGuid();
            message.GetProperty("body").GetString().Should().BeEmpty();
            message.GetProperty("clientMessageId").GetGuid().Should().Be(clientId);
            var attachments = message.GetProperty("attachments");
            attachments.GetArrayLength().Should().Be(2);
            var image = attachments.EnumerateArray().Single(a => a.GetProperty("kind").GetString() == "image");
            image.GetProperty("contentType").GetString().Should().Be("image/png");
            image.TryGetProperty("storedFileName", out _).Should().BeFalse("private storage keys are not exposed to browsers");
            var fileUrl = image.GetProperty("url").GetString()!;
            fileUrl.Should().Contain("/uploads/teamchatmedia/");
            var voice = attachments.EnumerateArray().Single(a => a.GetProperty("kind").GetString() == "voice");
            voice.GetProperty("contentType").GetString().Should().Be("audio/wav");
            voice.GetProperty("durationSeconds").GetInt32().Should().Be(1);
            var voiceUrl = voice.GetProperty("url").GetString()!;

            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
                var rows = await db.InternalMessageAttachments.Where(a => a.MessageId == messageId).ToListAsync();
                rows.Should().HaveCount(2);
                storedFileNames.AddRange(rows.Select(a => a.StoredFileName));
                (await db.InternalMessages.CountAsync(m => m.ConversationId == conversationId)).Should().Be(1);
            }

            // A retry with the same client id does not create a second message, attachment,
            // or file; it returns the original durable message instead.
            var retry = await PostMultipartAsync(owner, conversationUrl, conversationUrl + "/send", "", clientId,
                new UploadPart("proof.png", "text/html", "image", png),
                new UploadPart("voice.wav", "audio/wav", "voice", wave, 1));
            retry.StatusCode.Should().Be(HttpStatusCode.OK);
            using var retryJson = JsonDocument.Parse(await retry.Content.ReadAsStringAsync());
            retryJson.RootElement.GetProperty("duplicate").GetBoolean().Should().BeTrue();
            retryJson.RootElement.GetProperty("message").GetProperty("id").GetGuid().Should().Be(messageId);
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
                (await db.InternalMessages.CountAsync(m => m.Id == messageId)).Should().Be(1);
                (await db.InternalMessageAttachments.CountAsync(a => a.MessageId == messageId)).Should().Be(2);
            }

            // Only the two conversation participants can retrieve private image and audio.
            using (var scope = factory.Services.CreateScope())
            {
                var storageOptions = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<StorageOptions>>().Value;
                foreach (var stored in storedFileNames)
                    File.Exists(Path.GetFullPath(Path.Combine(storageOptions.PrivateRootPath, stored)))
                        .Should().BeTrue("the guarded middleware must resolve the file from the same private root as storage");
            }
            var ownerImage = await owner.GetAsync(fileUrl);
            ownerImage.StatusCode.Should().Be(HttpStatusCode.OK);
            ownerImage.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
            var managerVoice = await manager.GetAsync(voiceUrl);
            managerVoice.StatusCode.Should().Be(HttpStatusCode.OK);
            managerVoice.Content.Headers.ContentType!.MediaType.Should().Be("audio/wav");
            (await staff2.GetAsync(fileUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound,
                "a different staff account cannot use orders.view to reach Team Chat media");
            (await customer.GetAsync(voiceUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await UnreadCountAsync(manager, "/admin/team-messages")).Should().Be(1,
                "attachment reads do not mark the message conversation as read");
        }
        finally
        {
            if (storedFileNames.Count > 0)
            {
                using var scope = factory.Services.CreateScope();
                var storage = scope.ServiceProvider.GetRequiredService<IFileStorageService>();
                foreach (var stored in storedFileNames) await storage.DeleteAsync(stored);
            }
        }
    }

    [Fact]
    public async Task Team_chat_upload_rejects_unapproved_extensions_and_files_over_the_configured_limit()
    {
        var team = await SeedTeamAsync();
        using var factory = team.Factory;
        var owner = team.Owner;
        var managerId = await UserIdOf(factory, "manager@team.test");
        var start = await PostFormAsync(owner, "/admin/team-messages", "/admin/team-messages/start",
            new Dictionary<string, string> { ["otherUserId"] = managerId.ToString() });
        var conversationUrl = start.Headers.Location!.ToString();

        var badType = await PostMultipartAsync(owner, conversationUrl, conversationUrl + "/send", "", Guid.NewGuid(),
            new UploadPart("payload.exe", "application/octet-stream", "file", [1, 2, 3]));
        badType.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await badType.Content.ReadAsStringAsync()).Should().Contain("not allowed");

        var overLimit = await PostMultipartAsync(owner, conversationUrl, conversationUrl + "/send", "", Guid.NewGuid(),
            new UploadPart("large.png", "image/png", "image", new byte[10 * 1024 * 1024 + 1]));
        overLimit.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await overLimit.Content.ReadAsStringAsync()).Should().Contain("size");
    }
}
