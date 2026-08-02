# 0001 — Single-adapter interfaces in Domain/Contracts stay

Status: accepted
Date: 2026-08-02

## Context

`Domain/Contracts/` holds 42 interfaces. Only 8 have two or more production
adapters. Of the rest, 20 have never been substituted at all — not even by a
test double.

An architecture review proposed deleting the ones with no Domain consumer
(`IMemoryExtractor`, `IMemoryConsolidator`, `IPushSubscriptionStore`,
`IHubNotificationSender`, `IDomainToolRegistry`, `IAgentDefinitionProvider`,
`ICaptchaSolver`, `IConversationFactory`, `IMcpChannelConnection`), on the
grounds that a seam nothing crosses is pure indirection, and that
`.claude/rules/domain-layer.md` already says interfaces are for services Domain
needs to consume.

## Decision

They stay. Adapter count is not the criterion for whether a service gets an
interface.

Two reasons:

**Uniformity.** Every injected dependency is reached through an interface. A
folder where some services have one and some do not is harder to read and to
review than one where all do, and the rule "does this have a second adapter
yet?" is not a rule anyone can apply while writing code.

**Substitutability.** The seams exist so an adapter can be swapped without
touching consumers. That no second adapter exists today is a fact about today,
not evidence the seam is unnecessary.

## Consequences

- Counting adapters is not a valid argument for deleting an interface here.
  A future review that reaches for that signal should stop at this record.
- The domain-layer rule still holds in the direction that matters: an interface
  is required wherever Domain consumes the service, because Domain cannot
  reference Infrastructure. 11 of the 20 fall into that category regardless.
- The navigation cost is real and accepted: reaching an implementation from a
  consumer is two hops rather than one.

## Not covered by this decision

`IFileSystemBackend` is the one genuinely polymorphic interface in the
codebase, with five adapters. Deepening it is a separate matter and is not
constrained by this record.
