using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using DeChimex.Application.Common.Interfaces;
using DeChimex.Persistence.Context;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DeChimex.IntegrationTests.Controllers;

/// <summary>
/// Smart-chat HTTP surface: quoted replies (persisted + projected, cross-order
/// rejected), JSON fetch sends, batched read beacons with ownership guards, and
/// delivery backfill on state fetch (delivered WITHOUT read — no beacon, no read).
/// </summary>
public class SmartChatTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public SmartChatTests(CustomWebApplicationFactory factory) => _factory = factory;

    private HttpClient CreateBrowser() => _factory.CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        HandleCookies = true
    });

    private async Task<(HttpClient Browser, Guid OrderId)> SeedWorkspaceAsync(string tag)
    {
        var services = _factory.Services;
        var email = $"smartchat-{tag}-{Guid.NewGuid():N}@example.com";
        await TestSeed.RegisterCustomerAsync(services, email);
        await TestSeed.VerifyCustomerAsync(services, email);
        var productId = await SalesAssignmentTests.SeedCustomizableProductAsync(services, $"Chat {tag}?");
        var browser = CreateBrowser();
        (await TestSeed.LoginMvcAsync(browser, email, TestSeed.DefaultPassword)).Should().BeTrue();
        var orderId = await SalesAssignmentTests.SubmitRequestAsync(browser, productId, "Blue");
        return (browser, orderId);
    }

    private static async Task<string> TokenForAsync(HttpClient browser, string workspaceUrl)
    {
        var html = await (await browser.GetAsync(workspaceUrl)).Content.ReadAsStringAsync();
        return TestSeed.ExtractAntiforgeryToken(html);
    }

    private static async Task<HttpResponseMessage> PostOrderChatMultipartAsync(
        HttpClient browser, string workspaceUrl, string postUrl, string body, string fileName,
        string contentType, string kind, int? duration, byte[] bytes)
    {
        var token = await TokenForAsync(browser, workspaceUrl);
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(token), "__RequestVerificationToken");
        form.Add(new StringContent(body), "body");
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "files", fileName);
        form.Add(new StringContent(kind), "kinds");
        form.Add(new StringContent(duration?.ToString() ?? string.Empty), "durations");
        using var request = new HttpRequestMessage(HttpMethod.Post, postUrl) { Content = form };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return await browser.SendAsync(request);
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
        writer.Write((short)1);
        writer.Write((short)1);
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

    private static async Task<JsonDocument> StateAsync(HttpClient browser, string url)
    {
        var response = await browser.GetAsync(url);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    private static Guid MessageIdByBody(JsonElement messages, string body)
        => Guid.Parse(messages.EnumerateArray()
            .First(m => m.GetProperty("body").GetString() == body)
            .GetProperty("id").GetString()!);

    [Fact]
    public async Task Reply_PersistsQuote_AndProjectsSnippet()
    {
        var (customer, orderId) = await SeedWorkspaceAsync("reply");
        var owner = await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);
        var portalWs = $"/portal/orders/{orderId}/workspace";
        var adminWs = $"/admin/orders/{orderId}/workspace";

        var first = await customer.PostAsync($"{portalWs}/message", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["body"] = "Which shade of red?",
                ["__RequestVerificationToken"] = await TokenForAsync(customer, portalWs)
            }));
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var state = await StateAsync(customer, $"{portalWs}/state");
        var firstId = MessageIdByBody(state.RootElement.GetProperty("workspace").GetProperty("messages"),
            "Which shade of red?");

        var reply = await owner.PostAsync($"{adminWs}/message", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["body"] = "Victory red it is.",
                ["replyToMessageId"] = firstId.ToString(),
                ["__RequestVerificationToken"] = await TokenForAsync(owner, adminWs)
            }));
        reply.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var after = await StateAsync(customer, $"{portalWs}/state");
        var messages = after.RootElement.GetProperty("workspace").GetProperty("messages");
        var replyDto = messages.EnumerateArray()
            .First(m => m.GetProperty("body").GetString() == "Victory red it is.");
        replyDto.GetProperty("replyToMessageId").GetString().Should().Be(firstId.ToString());
        replyDto.GetProperty("replyToSnippet").GetString().Should().Be("Which shade of red?");
    }

    [Fact]
    public async Task Reply_CrossOrder_IsRejected()
    {
        var (customerA, orderA) = await SeedWorkspaceAsync("replya");
        var (customerB, orderB) = await SeedWorkspaceAsync("replyb");
        var portalA = $"/portal/orders/{orderA}/workspace";
        var portalB = $"/portal/orders/{orderB}/workspace";

        var first = await customerB.PostAsync($"{portalB}/message", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["body"] = "B's message",
                ["__RequestVerificationToken"] = await TokenForAsync(customerB, portalB)
            }));
        first.StatusCode.Should().Be(HttpStatusCode.Redirect);
        using var stateB = await StateAsync(customerB, $"{portalB}/state");
        var foreignId = MessageIdByBody(stateB.RootElement.GetProperty("workspace").GetProperty("messages"),
            "B's message");

        // The fetch (JSON) branch surfaces the domain rejection as a 422 payload.
        var form = new Dictionary<string, string>
        {
            ["body"] = "Quoting across orders",
            ["replyToMessageId"] = foreignId.ToString(),
            ["__RequestVerificationToken"] = await TokenForAsync(customerA, portalA)
        };
        var request = new HttpRequestMessage(HttpMethod.Post, $"{portalA}/message")
        {
            Content = new FormUrlEncodedContent(form)
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var rejected = await customerA.SendAsync(request);
        rejected.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("quoted message");
    }

    [Fact]
    public async Task SendJson_ReturnsMessage_AndEmptySend_Is422()
    {
        var (customer, orderId) = await SeedWorkspaceAsync("jsonsend");
        var portalWs = $"/portal/orders/{orderId}/workspace";
        var token = await TokenForAsync(customer, portalWs);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{portalWs}/message")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["body"] = "Fetch-sent hello",
                ["__RequestVerificationToken"] = token
            })
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var sent = await customer.SendAsync(request);
        sent.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await sent.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("message").GetProperty("body").GetString()
            .Should().Be("Fetch-sent hello");

        var empty = new HttpRequestMessage(HttpMethod.Post, $"{portalWs}/message")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["body"] = "",
                ["__RequestVerificationToken"] = token
            })
        };
        empty.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        var rejected = await customer.SendAsync(empty);
        rejected.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task MarkRead_MarksOthers_OnlyAndStrangerIs404()
    {
        var (customer, orderId) = await SeedWorkspaceAsync("markread");
        var (stranger, strangerOrderId) = await SeedWorkspaceAsync("markreadstranger");
        var owner = await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);
        var portalWs = $"/portal/orders/{orderId}/workspace";
        var adminWs = $"/admin/orders/{orderId}/workspace";

        await customer.PostAsync($"{portalWs}/message", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["body"] = "Customer question",
                ["__RequestVerificationToken"] = await TokenForAsync(customer, portalWs)
            }));
        await owner.PostAsync($"{adminWs}/message", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["body"] = "Staff answer",
                ["__RequestVerificationToken"] = await TokenForAsync(owner, adminWs)
            }));
        using var state = await StateAsync(customer, $"{portalWs}/state");
        var messages = state.RootElement.GetProperty("workspace").GetProperty("messages");
        var mineId = MessageIdByBody(messages, "Customer question");
        var staffId = MessageIdByBody(messages, "Staff answer");

        var beacon = new HttpRequestMessage(HttpMethod.Post, $"{portalWs}/messages/mark-read")
        {
            Content = new StringContent(
                $"{{\"ids\":[\"{mineId}\",\"{staffId}\",\"{Guid.NewGuid()}\"]}}",
                Encoding.UTF8, "application/json")
        };
        beacon.Headers.Add("RequestVerificationToken", await TokenForAsync(customer, portalWs));
        var read = await customer.SendAsync(beacon);
        read.StatusCode.Should().Be(HttpStatusCode.OK);
        using var readDoc = JsonDocument.Parse(await read.Content.ReadAsStringAsync());
        readDoc.RootElement.GetProperty("read").EnumerateArray().Select(e => e.GetString())
            .Should().BeEquivalentTo([staffId.ToString()], "own + unknown ids are ignored");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
        (await db.OrderMessages.FirstAsync(m => m.Id == staffId)).ReadAtUtc.Should().NotBeNull();
        (await db.OrderMessages.FirstAsync(m => m.Id == mineId)).ReadAtUtc.Should().BeNull();

        var foreign = new HttpRequestMessage(HttpMethod.Post, $"{portalWs}/messages/mark-read")
        {
            Content = new StringContent($"{{\"ids\":[\"{staffId}\"]}}", Encoding.UTF8, "application/json")
        };
        foreign.Headers.Add("RequestVerificationToken",
            await TokenForAsync(stranger, $"/portal/orders/{strangerOrderId}/workspace"));
        (await stranger.SendAsync(foreign)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delivered_BackfillsOnFetch_WithoutRead()
    {
        var (customer, orderId) = await SeedWorkspaceAsync("delivered");
        var owner = await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);
        var portalWs = $"/portal/orders/{orderId}/workspace";

        // Nobody has the workspace open (no SignalR in these tests), so the send
        // stays at sent — delivery backfills when staff fetch the thread.
        await customer.PostAsync($"{portalWs}/message", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["body"] = "Unread question",
                ["__RequestVerificationToken"] = await TokenForAsync(customer, portalWs)
            }));
        using var before = await StateAsync(customer, $"{portalWs}/state");
        var beforeMsg = before.RootElement.GetProperty("workspace").GetProperty("messages")
            .EnumerateArray().First(m => m.GetProperty("body").GetString() == "Unread question");
        beforeMsg.GetProperty("deliveredAtUtc").ValueKind.Should().Be(JsonValueKind.Null);

        (await owner.GetAsync($"/admin/orders/{orderId}/workspace/state")).StatusCode
            .Should().Be(HttpStatusCode.OK);

        using var after = await StateAsync(customer, $"{portalWs}/state");
        var afterMsg = after.RootElement.GetProperty("workspace").GetProperty("messages")
            .EnumerateArray().First(m => m.GetProperty("body").GetString() == "Unread question");
        afterMsg.GetProperty("deliveredAtUtc").ValueKind.Should().NotBe(JsonValueKind.Null);
        afterMsg.GetProperty("readAtUtc").ValueKind.Should().Be(JsonValueKind.Null,
            "fetching is delivery, not reading — only a beacon marks read");
    }

    [Fact]
    public async Task Voice_media_stays_order_scoped_and_preserves_order_chat_access()
    {
        var (customer, orderId) = await SeedWorkspaceAsync("voice-upload");
        var (stranger, _) = await SeedWorkspaceAsync("voice-stranger");
        var owner = await TestSeed.LoginOwnerMvcAsync(_factory.Services, CreateBrowser);
        var workspaceUrl = $"/portal/orders/{orderId}/workspace";
        string? storedFileName = null;

        try
        {
            var response = await PostOrderChatMultipartAsync(customer, workspaceUrl, workspaceUrl + "/message",
                "", "voice.wav", "audio/wav", "voice", 1, TestWave());
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var message = json.RootElement.GetProperty("message");
            var messageId = Guid.Parse(message.GetProperty("id").GetString()!);
            var attachment = message.GetProperty("attachments").EnumerateArray().Single();
            attachment.GetProperty("kind").GetString().Should().Be("voice");
            attachment.GetProperty("contentType").GetString().Should().Be("audio/wav");
            attachment.GetProperty("durationSeconds").GetInt32().Should().Be(1);
            attachment.TryGetProperty("storedFileName", out _).Should().BeFalse();
            var mediaUrl = attachment.GetProperty("url").GetString()!;
            mediaUrl.Should().Contain("/uploads/chatmedia/").And.NotContain("teamchatmedia");

            using (var scope = _factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DeChimexDbContext>();
                var media = await db.OrderMessageAttachments.SingleAsync(a => a.MessageId == messageId);
                storedFileName = media.StoredFileName;
                (await db.OrderMessages.SingleAsync(m => m.Id == messageId)).OrderId.Should().Be(orderId);
            }

            var customerFile = await customer.GetAsync(mediaUrl);
            customerFile.StatusCode.Should().Be(HttpStatusCode.OK);
            customerFile.Content.Headers.ContentType!.MediaType.Should().Be("audio/wav");
            (await owner.GetAsync(mediaUrl)).StatusCode.Should().Be(HttpStatusCode.OK,
                "the existing Orders.View staff permission still grants order-chat media access");
            (await stranger.GetAsync(mediaUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound,
                "a customer cannot access chat media from another order");
        }
        finally
        {
            if (storedFileName is not null)
            {
                using var scope = _factory.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IFileStorageService>().DeleteAsync(storedFileName);
            }
        }
    }

}
