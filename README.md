# ShareLinks

ShareLinks is a Jellyfin 10.11 plugin scaffold for issuing expiring guest-share
links to individual items. This staging tree now includes the core dashboard
and web-client wiring that later workers will build on:

- plugin metadata and DI registration
- JSON-backed record storage
- token generation and hashing
- startup and scheduled cleanup shells
- configuration defaults
- Jellyfin Web script injection and a dashboard config page shell

## Current security stance

The design goal is simple: a raw share token should exist only at the moment it
is issued, returned to the caller once, and then forgotten. Persistent storage
keeps only a keyed HMAC hash of the token plus the metadata needed to audit or
clean up the link.

That means later API work must keep a few rules:

1. never log raw tokens
2. never write raw tokens to disk
3. only return the token in the initial creation response
4. treat token validation as hash comparison only
5. keep guest-user creation and teardown behind explicit service calls

## Configuration plan

The `PluginConfiguration` defaults are deliberately opinionated:

- default expiry hours
- maximum allowed expiry hours
- optional public base URL override
- guest username prefix
- transcoding and remuxing toggles
- cleanup interval in minutes
- one-use default
- guest-mode lockdown enabled by default

Later workers should wire those settings into the issue / redeem / revoke
pipeline and into the guest-user creation logic.

## Storage layout

The staging implementation stores plugin data under Jellyfin's application data
path, in a dedicated `sharelinks` directory. The persistent JSON store keeps
`ShareLinkRecord` entries keyed by id, while the token service stores its secret
key separately in the same directory.

This keeps the plugin portable and avoids any hardcoded filesystem locations.

## Cleanup architecture

There are two cleanup entry points already wired:

- `Tasks/CleanupShareLinksScheduledTask.cs`
- `Lifecycle/StartupCleanupHostedService.cs`

Both currently call an `IShareLinkCleanupService` implementation that is a
no-op. That gives later workers a stable seam for:

- expiring old links
- removing one-use links after redemption
- tearing down guest accounts and tokens
- recording cleanup attempts and failures

## Endpoint audit requirements

When the API surface is added, it should be audited for:

- authz on every create/list/redeem/revoke endpoint
- exact token handling on create and redeem flows
- rate limiting for token guesses and redemption retries
- whether any response leaks the token hash, raw token, or guest credentials
- whether guest-mode lockdown is enforced consistently
- whether cleanup can safely run while links are being created or redeemed
- whether error messages reveal link existence or status

## What is intentionally missing

The remaining work is mostly policy polish and cleanup edge cases. The plugin
already has its controller, web injection hook, and dashboard page entry point
in place.
