# 01 — A result can be mapped without unwrapping

**What to build:** nothing the model can see. This is a prefactor that makes the next
ticket small.

The filesystem result union is a closed two-case type: a typed success payload or a typed
error. Every tool that wants to adjust the success payload before answering has to
pattern-match the success case, rebuild the wrapper around the adjusted value, and hand
back the original when it is an error. Four tools do exactly that today, in four
near-identical private helpers, and the next ticket adds six more sites of the same shape.

Give the union a combinator that applies a function to the success payload and passes an
error straight through, so those sites become one expression each.

**Blocked by:** None — can start immediately.

- [ ] The result union exposes a way to transform its success payload that leaves the
      error case untouched.
- [ ] The four existing path-normalizing helpers in the filesystem tools are rewritten
      through it and their pattern-matching disappears.
- [ ] No response the model receives changes in any way; the existing filesystem tool
      tests pass unmodified.
- [ ] A test covers both cases of the union: a success is transformed, an error passes
      through unchanged and the transformation never runs.

**Status:** ready-for-agent

## Comments

From the spec at `.scratch/virtual-path-coordinates/spec.md`. Sequenced first because
"make the change easy, then make the easy change" — ticket 02 touches ten sites of this
shape.
