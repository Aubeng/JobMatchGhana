using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using JobMatch.Api.WhatsApp;
using Xunit;

namespace JobMatch.Tests;

public class SignatureValidationTests
{
    private const string Secret = "test-app-secret";

    private static string Sign(string secret, byte[] body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();
    }

    [Fact]
    public void Valid_signature_passes()
    {
        var body = Encoding.UTF8.GetBytes("""{"object":"whatsapp_business_account"}""");
        var header = Sign(Secret, body);

        WhatsAppSignatureValidator.IsValid(Secret, body, header).Should().BeTrue();
    }

    [Fact]
    public void Uppercase_hex_signature_also_passes()
    {
        var body = Encoding.UTF8.GetBytes("hello");
        var header = Sign(Secret, body).ToUpperInvariant(); // "SHA256=..." + uppercase digest

        WhatsAppSignatureValidator.IsValid(Secret, body, header).Should().BeTrue();
    }

    [Fact]
    public void Tampered_body_fails()
    {
        var body = Encoding.UTF8.GetBytes("original");
        var header = Sign(Secret, body);
        var tampered = Encoding.UTF8.GetBytes("original!"); // one byte changed

        WhatsAppSignatureValidator.IsValid(Secret, tampered, header).Should().BeFalse();
    }

    [Fact]
    public void Wrong_secret_fails()
    {
        var body = Encoding.UTF8.GetBytes("payload");
        var header = Sign("other-secret", body);

        WhatsAppSignatureValidator.IsValid(Secret, body, header).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("md5=abcd")]
    [InlineData("sha256=zzzz")]      // invalid hex / wrong length
    [InlineData("sha256=")]          // empty digest
    public void Malformed_or_missing_header_fails(string? header)
    {
        var body = Encoding.UTF8.GetBytes("payload");
        WhatsAppSignatureValidator.IsValid(Secret, body, header).Should().BeFalse();
    }

    [Fact]
    public void Empty_secret_fails_closed()
    {
        var body = Encoding.UTF8.GetBytes("payload");
        var header = Sign("", body);
        WhatsAppSignatureValidator.IsValid("", body, header).Should().BeFalse();
    }
}
