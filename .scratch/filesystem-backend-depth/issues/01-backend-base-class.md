# 01 — Backend base class with unsupported defaults and the shared implementation

**What to build:** A developer writing a filesystem backend gets the twelve operations already answered. All of them return an unsupported envelope until overridden, and the pieces every existing backend had copied by hand are provided once.

The base sits in the domain project and implements the backend contract. It provides:

- The four envelope shapes the backends were each rebuilding: unsupported, not found, invalid argument, read only. The unsupported envelope names the operation the model called, in the vocabulary the model uses, never a C# method name.
- The glob prologue: applying the base path, and the trailing-slash convention that asks for directories only. Both are advertised to the model by the glob tool today and both must be implemented here rather than per backend.
- A search regex compiled case-insensitively with a match timeout, and guarded so a pattern that cannot compile becomes an error envelope rather than an escaping exception.
- The search template the backends each reimplemented, built on the guarded regex.
- One description hook per operation, virtual, with a generic default. A backend overrides a hook when its wording needs to name real files.

Nothing consumes this yet. The five existing backends keep working unchanged.

**Blocked by:** None — can start immediately.

**Status:** done

- [x] A base class implements the backend contract with all twelve operations virtual.
- [x] Every operation returns an unsupported envelope by default, and the envelope names the operation rather than the method.
- [x] The four envelope helpers are available to subclasses.
- [x] The glob prologue applies a base path, and a trailing slash yields directories only.
- [x] A search regex is compiled with a match timeout; an uncompilable pattern returns an invalid-argument envelope rather than throwing.
- [x] The search template is available to subclasses and uses the guarded regex.
- [x] Each operation has a description hook with a generic default.
- [x] The five existing backends are untouched and their tests still pass.
