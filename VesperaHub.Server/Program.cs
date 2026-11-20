using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using VesperaHub.Server.Hubs;
using VesperaHub.Server.Services;

var builder = WebApplication.CreateBuilder(args);

const string jwtSigningKey = "SuperSecretJwtSigningKeyForVesperaHub2026!";
var keyBytes = Encoding.UTF8.GetBytes(jwtSigningKey);

builder.Services.AddSingleton<MessageHistoryService>();
builder.Services.AddSingleton<EncryptionService>();
builder.Services.AddSignalR();