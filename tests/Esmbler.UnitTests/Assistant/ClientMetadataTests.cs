using System.Net;
using Esmbler.Hosting.Auth;

namespace Esmbler.UnitTests.Assistant;

/// <summary>REQ-AI-007, REQ-AISAFE-004 (ADR 0025, T-41): which external MCP clients are accepted and where Esmbler may fetch from.</summary>
public class ClientMetadataTests
{
    private static readonly string[] Allowed = ["chatgpt.com", "claude.ai"];

    [Theory]
    [InlineData("https://chatgpt.com/oauth/client.json", true)]
    [InlineData("https://auth.claude.ai/clients/esmbler", true)]
    [InlineData("http://chatgpt.com/oauth/client.json", false)]
    [InlineData("https://chatgpt.com/", false)]
    [InlineData("https://chatgpt.com:8443/client.json", false)]
    [InlineData("https://user@chatgpt.com/client.json", false)]
    [InlineData("https://chatgpt.com.evil.example/client.json", false)]
    [InlineData("https://notchatgpt.com/client.json", false)]
    [InlineData("chatgpt.com/client.json", false)]
    public void Only_https_metadata_documents_on_allowlisted_hosts_are_accepted(string clientId, bool accepted) =>
        Assert.Equal(accepted, HttpClientMetadataSource.Acceptable(clientId, Allowed, out _));

    [Fact]
    public void Metadata_documents_must_name_themselves_and_list_safe_redirects()
    {
        const string id = "https://chatgpt.com/oauth/client.json";
        var ok = HttpClientMetadataSource.Parse(id, "chatgpt.com", """{"client_id":"https://chatgpt.com/oauth/client.json","client_name":"ChatGPT‮","redirect_uris":["https://chatgpt.com/cb","http://127.0.0.1:3000/cb","http://evil.example/cb","javascript:alert(1)"]}""");
        Assert.Equal("ChatGPT", ok!.Name);
        Assert.Equal(["https://chatgpt.com/cb", "http://127.0.0.1:3000/cb"], ok.RedirectUris);
        Assert.Null(HttpClientMetadataSource.Parse(id, "chatgpt.com", """{"client_id":"https://chatgpt.com/other.json","redirect_uris":["https://chatgpt.com/cb"]}"""));
        Assert.Null(HttpClientMetadataSource.Parse(id, "chatgpt.com", """{"client_id":"https://chatgpt.com/oauth/client.json","redirect_uris":[]}"""));
        Assert.Null(HttpClientMetadataSource.Parse(id, "chatgpt.com", "not json"));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    public void Metadata_is_fetched_only_from_public_addresses(string address, bool isPublic) =>
        Assert.Equal(isPublic, HttpClientMetadataSource.IsPublic(IPAddress.Parse(address)));
}
