using Agent.Modules;
using Domain.Contracts;
using Domain.DTOs.WebChat;

var builder = WebApplication.CreateBuilder(args);
var cmdParams = ConfigModule.GetCommandLineParams(args);
var settings = builder.Configuration.GetSettings();

builder.Services.ConfigureAgents(settings, cmdParams, builder.Configuration);
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.SetIsOriginAllowed(_ => true)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

var app = builder.Build();
app.UseCors();

app.MapGet("/api/agents", (IAgentFactory agentFactory, string? userId) =>
    agentFactory.GetAvailableAgents(userId));

app.MapPost("/api/agents", (IAgentFactory agentFactory, string userId, CustomAgentRegistration registration) =>
    agentFactory.RegisterCustomAgent(userId, registration));

app.MapDelete("/api/agents/{agentId}", (IAgentFactory agentFactory, string userId, string agentId) =>
    agentFactory.UnregisterCustomAgent(userId, agentId));

await app.RunAsync();