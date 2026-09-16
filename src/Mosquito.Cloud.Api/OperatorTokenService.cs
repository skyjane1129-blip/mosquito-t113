using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Mosquito.Cloud.Api;

public sealed class OperatorTokenService(IOptions<AuthOptions> options)
{
    private readonly AuthOptions _options = options.Value;

    public LoginResponse? Login(LoginRequest request)
    {
        if (!FixedEquals(request.Username, _options.Username) ||
            !FixedEquals(request.Password, _options.Password))
        {
            return null;
        }

        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(_options.TokenMinutes);
        var payload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new TokenPayload(
            request.Username,
            expiresAt.ToUnixTimeSeconds(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)))));
        var signature = Sign(payload, _options.SigningKey);
        return new LoginResponse($"{payload}.{signature}", expiresAt);
    }

    public bool Validate(string token)
    {
        var parts = token.Split('.', 2);
        if (parts.Length != 2 || !FixedEquals(parts[1], Sign(parts[0], _options.SigningKey)))
        {
            return false;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<TokenPayload>(Base64UrlDecode(parts[0]));
            return payload is not null &&
                   payload.ExpiresAtUnix > DateTimeOffset.UtcNow.ToUnixTimeSeconds() &&
                   FixedEquals(payload.Subject, _options.Username);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool FixedEquals(string left, string right)
    {
        var leftHash = SHA256.HashData(Encoding.UTF8.GetBytes(left));
        var rightHash = SHA256.HashData(Encoding.UTF8.GetBytes(right));
        return CryptographicOperations.FixedTimeEquals(leftHash, rightHash);
    }

    private static string Sign(string payload, string key) =>
        Base64UrlEncode(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload)));

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }

    private sealed record TokenPayload(string Subject, long ExpiresAtUnix, string Nonce);
}
