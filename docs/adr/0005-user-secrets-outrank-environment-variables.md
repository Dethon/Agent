# 0005 — User secrets outrank environment variables

Status: accepted
Date: 2026-08-03

## Context

Every MCP server in the repo binds its settings with the same eight lines, copied
thirteen times. The order of two of those lines is load-bearing, and it is the
opposite of what ASP.NET Core does by default:

```csharp
configBuilder
    .AddEnvironmentVariables()
    .AddUserSecrets<Program>()   // last, so it wins
    .Build();
```

`WebApplication.CreateBuilder` adds user secrets before environment variables, so
under the framework's own precedence an environment variable wins. Here the copied
block re-adds environment variables and then adds user secrets, so a secret wins.

That matters because of how secrets reach a container. `DockerCompose/.env` ships
every secret as an empty placeholder — `CAPSOLVER__APIKEY=`, `HOMEASSISTANT__TOKEN=`,
`BOTS__0__BOTTOKEN=` — and compose exports an empty value as an empty string, not as
an absent variable. The real values come from the host's user-secrets directory,
mounted read-only into each container by the platform override files. Under the
framework's precedence the empty placeholder would win and silently blank the real
secret.

The failure is silent rather than loud, because several settings treat an empty
string as "feature not configured": `McpSettings.CapSolver.ApiKey` gates the CAPTCHA
solver, `WebPushConfig.IsConfigured` gates push notifications, and
`MusicAssistantConfiguration.IsConfigured` gates the podcast-episode action. A
deployment would come up healthy with those features quietly switched off.

Nothing records this today. It survives only as the order of two lines that thirteen
files happen to have copied correctly.

## Decision

`BindSettings<TSettings>` is the single place any MCP server binds its settings, and
it adds environment variables first and user secrets last. User secrets outrank
environment variables, deliberately.

The user-secrets source is optional and keyed off the entry assembly rather than a
`Program` type, because the call now lives in a shared project where `typeof(Program)`
would resolve to the wrong assembly. Five of the thirteen servers have no
`UserSecretsId` and the source is simply absent for them, exactly as it is today.

## Considered options

**Follow the framework default and let environment variables win.** The least
surprising order for anyone who knows ASP.NET Core. Rejected because it breaks every
containerised deployment that relies on the mounted user-secrets directory, and
breaks it silently.

**Stop shipping empty placeholders in `.env`.** Removing the empty lines would make
the framework order correct. Rejected because the placeholders are the documentation
of which secrets a deployment needs; `DockerCompose/.env` is the checklist an operator
fills in, and an absent line is not a prompt to add one.

**Reject empty strings during binding, so a blank secret fails loudly.** Rejected in
grilling: `McpChannelServiceBus`'s connection string, Telegram's bot tokens and
WebSearch's Brave key all ship as `""` in `appsettings.json`, and an empty CapSolver
key is how that feature is turned off. An empty-is-invalid rule would refuse to start
three shipped servers. Validation therefore rejects null only.

## Consequences

- A developer running a server outside a container gets the same precedence, so a
  user secret overrides an environment variable on their machine too. That is the
  intent: user secrets are the developer's own values.
- The order must not be "tidied" toward the framework default. This ADR exists so
  that anyone who tries finds out why before the next release quietly loses CapSolver,
  push notifications and Music Assistant.
- Adding a `UserSecretsId` to one of the five servers that lacks one starts a config
  source it never read. That is an intentional opt-in, not a side effect of this
  change.
