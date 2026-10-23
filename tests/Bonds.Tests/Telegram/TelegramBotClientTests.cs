using System.Net;
using System.Text;
using System.Text.Json;
using Bonds.Core.Telegram;
using Bonds.Infrastructure.Telegram;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bonds.Tests.Telegram;

public class TelegramBotClientTests
{
    private const string Token = "123456:SECRET-TOKEN-VALUE";

    private sealed class FakeHandler(Func<HttpRequestMessage, string, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Path, string Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.RequestUri!.AbsolutePath, body));
            return respond(request, body);
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { BaseAddress = new Uri("https://api.telegram.org") };
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static TelegramBotClient NewClient(FakeHandler handler, string? token = Token)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Telegram:BotToken"] = token }).Build();
        return new TelegramBotClient(new Factory(handler), config, NullLogger<TelegramBotClient>.Instance);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("REPLACE_WITH_BOT_TOKEN", false)]
    [InlineData(Token, true)]
    public void IsConfigured_IgnoresEmptyAndPlaceholder(string? token, bool expected) =>
        NewClient(new FakeHandler((_, _) => Json("{}")), token).IsConfigured.Should().Be(expected);

    [Fact]
    public async Task SendMessage_PostsHtmlAndInlineKeyboard_ReturnsMessageId()
    {
        var handler = new FakeHandler((_, _) => Json("""{"ok":true,"result":{"message_id":77}}"""));
        var keyboard = new InlineKeyboard([[new InlineButton("✅ Подтвердить", "p:1:a:AbCdEfGh123"), new InlineButton("🔎 Детали", Url: "https://example.org/bonds/plans/1")]]);

        var id = await NewClient(handler).SendMessageAsync(555, "<b>hi</b> &amp;", keyboard);

        id.Should().Be(77);
        var (path, body) = handler.Requests.Single();
        path.Should().Be($"/bot{Token}/sendMessage");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("chat_id").GetInt64().Should().Be(555);
        doc.RootElement.GetProperty("text").GetString().Should().Be("<b>hi</b> &amp;");
        doc.RootElement.GetProperty("parse_mode").GetString().Should().Be("HTML");
        var buttons = doc.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard")[0];
        buttons[0].GetProperty("callback_data").GetString().Should().Be("p:1:a:AbCdEfGh123");
        buttons[1].GetProperty("url").GetString().Should().Be("https://example.org/bonds/plans/1");
    }

    [Fact]
    public async Task SendMessage_WithoutKeyboard_OmitsReplyMarkup()
    {
        var handler = new FakeHandler((_, _) => Json("""{"ok":true,"result":{"message_id":1}}"""));
        await NewClient(handler).SendMessageAsync(1, "x");
        handler.Requests.Single().Body.Should().NotContain("reply_markup");
    }

    [Fact]
    public async Task EditMessage_WithoutKeyboard_SendsEmptyKeyboardToRemoveButtons()
    {
        var handler = new FakeHandler((_, _) => Json("""{"ok":true,"result":true}"""));
        await NewClient(handler).EditMessageTextAsync(1, 2, "done");
        var (path, body) = handler.Requests.Single();
        path.Should().EndWith("/editMessageText");
        body.Should().Contain("\"message_id\":2").And.Contain("\"inline_keyboard\":[]");
    }

    [Fact]
    public async Task EditMessage_NotModified_IsNotAnError()
    {
        var handler = new FakeHandler((_, _) => Json("""{"ok":false,"error_code":400,"description":"Bad Request: message is not modified"}""", HttpStatusCode.BadRequest));
        await NewClient(handler).EditMessageTextAsync(1, 2, "same");
    }

    [Fact]
    public async Task AnswerCallback_SendsIdTextAndAlertFlag()
    {
        var handler = new FakeHandler((_, _) => Json("""{"ok":true,"result":true}"""));
        await NewClient(handler).AnswerCallbackQueryAsync("cb1", "Готово", true);
        var (path, body) = handler.Requests.Single();
        path.Should().EndWith("/answerCallbackQuery");
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("callback_query_id").GetString().Should().Be("cb1");
        doc.RootElement.GetProperty("text").GetString().Should().Be("Готово");
        doc.RootElement.GetProperty("show_alert").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task GetUpdates_ParsesMessagesAndCallbacks_AndSendsOffsetAndTimeout()
    {
        const string response = """
        {"ok":true,"result":[
          {"update_id":10,"message":{"message_id":5,"from":{"id":111,"first_name":"Max"},"chat":{"id":111,"type":"private"},"text":"/status"}},
          {"update_id":11,"callback_query":{"id":"cbq","from":{"id":111},"data":"p:1:a:AbCdEfGh123","message":{"message_id":9,"chat":{"id":111,"type":"private"}}}},
          {"update_id":12,"edited_message":{"message_id":6}}
        ]}
        """;
        var handler = new FakeHandler((_, _) => Json(response));

        var result = await NewClient(handler).GetUpdatesAsync(10, 25);

        result.Conflict.Should().BeFalse();
        result.Updates.Select(u => u.UpdateId).Should().Equal(10, 11, 12);
        result.Updates[0].Message.Should().Be(new BotIncomingMessage(111, "private", 111, "Max", "/status"));
        result.Updates[1].Callback.Should().Be(new BotIncomingCallback("cbq", 111, 111, "private", 9, "p:1:a:AbCdEfGh123"));
        result.Updates[2].Message.Should().BeNull();
        result.Updates[2].Callback.Should().BeNull();

        var (path, body) = handler.Requests.Single();
        path.Should().Be($"/bot{Token}/getUpdates");
        body.Should().Contain("\"offset\":10").And.Contain("\"timeout\":25");
    }

    [Fact]
    public async Task GetUpdates_409_IsReportedAsConflict_NotThrown()
    {
        var handler = new FakeHandler((_, _) => Json(
            """{"ok":false,"error_code":409,"description":"Conflict: terminated by other getUpdates request; make sure that only one bot instance is running"}""",
            HttpStatusCode.Conflict));

        var result = await NewClient(handler).GetUpdatesAsync(0, 25);

        result.Conflict.Should().BeTrue();
        result.Updates.Should().BeEmpty();
    }

    [Fact]
    public async Task ApiError_ThrowsWithDescription_ButNeverTheTokenOrUrl()
    {
        var handler = new FakeHandler((_, _) => Json("""{"ok":false,"error_code":403,"description":"Forbidden: bot was blocked by the user"}""", HttpStatusCode.Forbidden));

        var act = () => NewClient(handler).SendMessageAsync(1, "x");

        var ex = (await act.Should().ThrowAsync<TelegramApiException>()).Which;
        ex.StatusCode.Should().Be(403);
        ex.Message.Should().Contain("bot was blocked").And.NotContain("SECRET").And.NotContain("api.telegram.org");
    }

    [Fact]
    public async Task NetworkFailure_ExceptionDoesNotLeakToken()
    {
        var handler = new FakeHandler((req, _) => throw new HttpRequestException($"connection refused {req.RequestUri}"));

        var act = () => NewClient(handler).SendMessageAsync(1, "x");

        var ex = (await act.Should().ThrowAsync<HttpRequestException>()).Which;
        ex.Message.Should().NotContain("SECRET").And.NotContain("api.telegram.org");
    }

    [Fact]
    public async Task NoToken_Throws_WithoutCallingNetwork()
    {
        var handler = new FakeHandler((_, _) => Json("{}"));
        var act = () => NewClient(handler, token: null).SendMessageAsync(1, "x");
        await act.Should().ThrowAsync<InvalidOperationException>();
        handler.Requests.Should().BeEmpty();
    }
}
