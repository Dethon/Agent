# 04 — The embedding client stops naming a provider

**What to build:** Prefactor. Make the change easy before making the change.

The embedding client already speaks plain OpenAI-compatible JSON, which is exactly what
both the hosted provider and the local server serve, so the client itself needs no
rewriting to talk to either. What stops it is configuration: it is named after one provider
and reads its address out of that provider's settings section, with an authorization header
it always sends.

Move the base address and model to the memory embedding configuration where the model name
already lives, give the type a provider-neutral name, and make the authorization header
conditional on a key being configured. Default configuration keeps pointing at the hosted
provider, so this ticket changes nothing a user could observe.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] Base address and model both come from the memory embedding configuration
- [x] With no key configured, no authorization header is sent
- [x] The type name no longer names a provider
- [x] Behaviour is unchanged under default configuration
- [x] Existing embedding tests still pass, renamed where they name the provider
