using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using VesperaHub.Server.Models;
using VesperaHub.Server.Options;

namespace VesperaHub.Server.Services;

public sealed class TokenService(IOptions<JwtOptions> options)
{
    public const string UserIdClaim = "sub";
    public const string NameClaim = "name";

    private readonly JwtOptions _jwt = options.Value;
    private readonly JsonWebTokenHandler _handler = new();

    public static SymmetricSecurityKey CreateSigningKey(string key) => new(Encoding.UTF8.GetBytes(key));

    public AuthResponse CreateToken(User user)
    {
        var expires = DateTime.UtcNow.AddHours(_jwt.LifetimeHours);
        var descriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(
            [
                new Claim(UserIdClaim, user.Id.ToString()),
                new Claim(NameClaim, user.Username),
            ]),
            Issuer = _jwt.Issuer,
            Audience = _jwt.Audience,
            Expires = expires,
            SigningCredentials = new SigningCredentials(CreateSigningKey(_jwt.Key), SecurityAlgorithms.HmacSha256),
        };

        return new AuthResponse(_handler.CreateToken(descriptor), user.Username, expires);
    }
}
