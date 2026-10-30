using IdleForge.Core.Networking;
using IdleForge.Core.UserScripts;
using System.Net;

namespace IdleForge.Core.Tests;

public sealed class SecurityBrokerTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.1.2.3")]
    [InlineData("169.254.169.254")]
    [InlineData("::1")]
    public void RestrictedAddressesAreBlocked(string address) => Assert.True(ConnectPolicy.IsRestricted(IPAddress.Parse(address)));

    [Fact]
    public async Task ConnectStarRequiresSeparateApproval()
    {
        var denied = new ConnectPolicy(["*"]);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.ValidateAsync(new Uri("https://93.184.216.34/test")));
        await new ConnectPolicy(["*"], true).ValidateAsync(new Uri("https://93.184.216.34/test"));
    }

    [Fact]
    public async Task RedirectToPrivateAddressIsRejectedBeforeSecondRequest()
    {
        var handler = new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("http://127.0.0.1/admin") } });
        using var broker = new SecureHttpBroker(handler);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => broker.SendAsync(new("r1", "GET", new Uri("https://93.184.216.34/start")), new ConnectPolicy(["*"], true)));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task SensitiveHeadersAreRejected()
    {
        using var broker = new SecureHttpBroker(new SequenceHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => broker.SendAsync(new("r2", "GET", new Uri("https://93.184.216.34/"), new Dictionary<string,string>{{"Cookie","secret=1"}}), new ConnectPolicy(["93.184.216.34"])));
    }

    [Fact]
    public async Task AcquisitionDoesNotExecuteAndFindsUserScriptLink()
    {
        var handler = new SequenceHandler(request => request.RequestUri!.AbsolutePath == "/page"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<a href='/tool.user.js'>install</a>") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("// ==UserScript==\n// @name T\n// @version 1\n// ==/UserScript==\nglobalThis.__mustNotRun=true") });
        var result = await new ScriptAcquisitionService(handler).AcquireAsync(new Uri("https://93.184.216.34/page"));
        Assert.Equal("T", result.Metadata.Name); Assert.Equal(2, handler.Calls);
    }

    private sealed class SequenceHandler(Func<HttpRequestMessage,HttpResponseMessage> factory) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) { Calls++; return Task.FromResult(factory(request)); }
    }
}
