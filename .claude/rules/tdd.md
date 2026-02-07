---
description: Enforces test-driven development methodology for all feature and bugfix work
---

# Test-Driven Development

## Workflow

When implementing features or fixing bugs, always follow the Red-Green-Refactor cycle:

1. **Red** - Write a failing test first that defines the expected behavior
2. **Green** - Write the minimum implementation code to make the test pass
3. **Refactor** - Clean up the code while keeping all tests green

## Rules

- **Never write implementation code without a failing test first**
- Write one test at a time, then make it pass before writing the next
- Run the test suite after each change to confirm the cycle
- Tests must actually fail before implementation (verify the "red" step)
- Keep implementation minimal - only write enough code to pass the current test

## Applying TDD

- **New features**: Start with a test describing the desired behavior
- **Bug fixes**: Start with a test that reproduces the bug, then fix it
- **Refactoring**: Ensure tests exist and pass before and after changes

## Exceptions

- Pure configuration changes (appsettings, DI registration) don't require TDD
- Trivial one-line changes where the risk is negligible
