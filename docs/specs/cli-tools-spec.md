# CLI Tools Specification

> MCP tools for executing commands and managing processes alongside text editing workflows

## Problem Statement

Text editing tools can modify files, but agents often need to:
1. **Validate changes** - Run linters, compilers, tests after edits
2. **Generate content** - Execute scripts that produce text output
3. **Explore context** - Navigate file systems, inspect environments
4. **Transform files** - Use CLI tools for batch operations (sed, awk, jq)
5. **Chain operations** - Combine text edits with command execution

Current `McpServerCommandRunner` has basic command execution but lacks:
- Session management for interactive tools
- Working directory control
- Environment variable management
- Output streaming for long-running commands
- Process lifecycle management

## Solution Overview

Enhanced CLI tools that complement text editing:

| Tool | Purpose |
|------|---------|
| **CliGetPlatform** | Detect shell environment (bash, pwsh, cmd, sh) |
| **CliRun** | Execute a command and return output |
| **CliSession** | Manage interactive shell sessions |
| **CliRead** | Read output from running session |
| **CliWrite** | Send input to interactive session |
| **CliStop** | Terminate a running session |
| **CliEnv** | Get/set environment variables |

---

## Tool 1: CliGetPlatform

### Purpose
Returns the available shell and platform information. Must be called first to understand command syntax.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| (none) | | | |

### Returns

```json
{
  "platform": "linux",
  "shell": "bash",
  "shellPath": "/bin/bash",
  "pathSeparator": "/",
  "commandSeparator": "&&",
  "environmentPrefix": "$",
  "features": {
    "pipelines": true,
    "redirects": true,
    "backgroundJobs": true,
    "heredoc": true
  }
}
```

Windows example:
```json
{
  "platform": "windows",
  "shell": "pwsh",
  "shellPath": "C:\\Program Files\\PowerShell\\7\\pwsh.exe",
  "pathSeparator": "\\",
  "commandSeparator": ";",
  "environmentPrefix": "$env:",
  "features": {
    "pipelines": true,
    "redirects": true,
    "backgroundJobs": true,
    "heredoc": false
  }
}
```

### Description for LLM

```
Returns shell environment information. Call this first to understand:
- Which shell is available (bash, pwsh, cmd, sh)
- Platform-specific path separators
- How to chain commands
- How to reference environment variables

Use this before CliRun to write compatible commands.
```

---

## Tool 2: CliRun

### Purpose
Executes a single command and returns the complete output. Best for short-running commands.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `command` | string | Yes | The command to execute |
| `workingDirectory` | string | No | Directory to run command in (default: current) |
| `timeout` | int | No | Max seconds to wait (default: 30) |
| `captureStderr` | boolean | No | Include stderr in output (default: true) |

### Returns

Success:
```json
{
  "status": "success",
  "exitCode": 0,
  "stdout": "file1.txt\nfile2.md\nREADME.md",
  "stderr": "",
  "duration": 0.15,
  "workingDirectory": "/home/user/project"
}
```

Timeout:
```json
{
  "status": "timeout",
  "exitCode": null,
  "stdout": "Processing file 1...\nProcessing file 2...",
  "stderr": "",
  "duration": 30.0,
  "suggestion": "Command did not complete. Use CliSession for long-running commands."
}
```

Error:
```json
{
  "status": "error",
  "exitCode": 1,
  "stdout": "",
  "stderr": "grep: invalid option -- 'z'\nUsage: grep [OPTION]... PATTERN [FILE]...",
  "duration": 0.02
}
```

### Behavior

1. **Synchronous execution**: Waits for command to complete or timeout
2. **Output capture**: Captures both stdout and stderr
3. **Exit code**: Returns actual exit code for success/failure detection
4. **Working directory**: Executes in specified directory, returns to original after
5. **Shell wrapping**: Command is executed via the detected shell

### Description for LLM

```
Executes a command and returns the output. Best for quick operations.

Parameters:
- command: The shell command to run
- workingDirectory: Optional directory to run in
- timeout: Max seconds to wait (default: 30)
- captureStderr: Include error output (default: true)

Use cases:
- List files: command="ls -la"
- Run tests: command="npm test"
- Check git status: command="git status"
- Compile code: command="dotnet build"

For commands that take longer than 30 seconds, use CliSession instead.

After text edits, use this to:
1. Validate changes (linting, type checking)
2. Run tests to confirm fix
3. Format files (prettier, gofmt)
```

---

## Tool 3: CliSession

### Purpose
Starts an interactive shell session for long-running or multi-step operations.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `command` | string | No | Initial command to run (default: starts shell) |
| `workingDirectory` | string | No | Starting directory |
| `sessionId` | string | No | Custom session ID (auto-generated if not provided) |
| `env` | object | No | Environment variables to set |

### Returns

```json
{
  "status": "started",
  "sessionId": "session-a1b2c3",
  "pid": 12345,
  "shell": "bash",
  "workingDirectory": "/home/user/project",
  "initialOutput": "$ ",
  "hint": "Use CliWrite to send commands, CliRead to get output, CliStop to terminate"
}
```

### Behavior

1. **Persistent session**: Shell stays running until explicitly stopped
2. **TTY emulation**: Supports interactive programs (less, vim, etc.)
3. **State preservation**: Environment and working directory persist across commands
4. **Multiple sessions**: Can run multiple sessions in parallel

### Description for LLM

```
Starts an interactive shell session for complex or long-running operations.

Use CliSession when:
- Running commands that take > 30 seconds
- Need to run multiple related commands
- Working with interactive tools (repl, debugger)
- Need to preserve working directory across commands

Workflow:
1. CliSession to start (get sessionId)
2. CliWrite to send commands
3. CliRead to get output
4. CliStop when done

Example - Run tests and debug:
1. CliSession workingDirectory="/project"
2. CliWrite input="npm test" → get sessionId
3. CliRead to see results
4. CliWrite input="npm run test:debug failed-test"
5. CliRead to see debug output
6. CliStop sessionId="..."
```

---

## Tool 4: CliRead

### Purpose
Reads available output from a running session.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sessionId` | string | Yes | Session to read from |
| `timeout` | int | No | Seconds to wait for output (default: 5) |
| `maxBytes` | int | No | Maximum bytes to return (default: 64KB) |

### Returns

```json
{
  "sessionId": "session-a1b2c3",
  "output": "Running tests...\n\n  ✓ test1 (5ms)\n  ✓ test2 (12ms)\n  ✗ test3 (8ms)\n\nTests: 2 passed, 1 failed",
  "hasMore": false,
  "isRunning": true,
  "waitingForInput": false
}
```

Waiting for input:
```json
{
  "sessionId": "session-a1b2c3",
  "output": "Do you want to continue? [y/N] ",
  "hasMore": false,
  "isRunning": true,
  "waitingForInput": true
}
```

### Description for LLM

```
Reads output from a running session started with CliSession.

Parameters:
- sessionId: The session to read from (from CliSession response)
- timeout: Seconds to wait for output (default: 5)
- maxBytes: Max output size (default: 64KB)

Response fields:
- output: Text produced since last read
- hasMore: More output available (call again)
- isRunning: Process still running
- waitingForInput: Process waiting for user input (use CliWrite)

Best practices:
- Poll with increasing delays for long operations
- Check waitingForInput to know when to send response
- Use hasMore to read complete output
```

---

## Tool 5: CliWrite

### Purpose
Sends input to an interactive session.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sessionId` | string | Yes | Session to write to |
| `input` | string | Yes | Text or special keys to send |
| `timeout` | int | No | Seconds to wait for response (default: 5) |

### Input Format

- Regular text: `"ls -la"`
- With enter: `"ls -la\n"` or use `{enter}`
- Special keys: `{enter}`, `{up}`, `{down}`, `{left}`, `{right}`, `{tab}`, `{backspace}`, `{escape}`, `{ctrl+c}`, `{ctrl+d}`
- Combined: `"y{enter}"` (type y and press enter)

### Returns

```json
{
  "sessionId": "session-a1b2c3",
  "status": "sent",
  "responseOutput": "total 24\ndrwxr-xr-x  4 user user 4096 Dec 26 10:00 .\ndrwxr-xr-x 10 user user 4096 Dec 25 15:30 ..",
  "isRunning": true,
  "waitingForInput": false
}
```

### Description for LLM

```
Sends input to an interactive session.

Parameters:
- sessionId: The session to write to
- input: Text and/or special keys to send
- timeout: Seconds to wait for response after sending

Special key sequences:
- {enter} - Press Enter
- {up}/{down}/{left}/{right} - Arrow keys
- {tab} - Tab key
- {backspace} - Backspace
- {escape} - Escape key
- {ctrl+c} - Interrupt (SIGINT)
- {ctrl+d} - End of input (EOF)

Examples:
- Run command: input="npm test{enter}"
- Answer prompt: input="y{enter}"
- Navigate menu: input="{down}{down}{enter}"
- Cancel operation: input="{ctrl+c}"
```

---

## Tool 6: CliStop

### Purpose
Terminates a running session.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `sessionId` | string | Yes | Session to stop |
| `force` | boolean | No | Force kill (SIGKILL) if graceful fails (default: false) |

### Returns

```json
{
  "sessionId": "session-a1b2c3",
  "status": "stopped",
  "exitCode": 0,
  "finalOutput": "Goodbye!\n"
}
```

### Description for LLM

```
Terminates a running session started with CliSession.

Parameters:
- sessionId: The session to stop
- force: Use SIGKILL if SIGTERM doesn't work (default: false)

Always stop sessions when done to free resources.
```

---

## Tool 7: CliEnv

### Purpose
Gets or sets environment variables for command execution.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `action` | string | Yes | One of: `get`, `set`, `list`, `unset` |
| `name` | string | No | Variable name (required for get/set/unset) |
| `value` | string | No | Variable value (required for set) |
| `sessionId` | string | No | Apply to specific session (default: global) |

### Returns

Get:
```json
{
  "name": "PATH",
  "value": "/usr/local/bin:/usr/bin:/bin",
  "source": "global"
}
```

List:
```json
{
  "variables": {
    "PATH": "/usr/local/bin:/usr/bin:/bin",
    "HOME": "/home/user",
    "NODE_ENV": "development"
  },
  "count": 3
}
```

Set:
```json
{
  "status": "set",
  "name": "MY_VAR",
  "value": "hello",
  "scope": "global"
}
```

### Description for LLM

```
Manages environment variables for CLI commands.

Actions:
- get: Retrieve a variable's value
- set: Set a variable (persists for session/global)
- list: List all variables
- unset: Remove a variable

Examples:
- Get PATH: action="get", name="PATH"
- Set NODE_ENV: action="set", name="NODE_ENV", value="test"
- List all: action="list"
- Remove var: action="unset", name="DEBUG"

Use sessionId to scope to a specific session, otherwise affects all commands.
```

---

## Workflow Examples

### Example 1: Validate Text Edits with Linting

```
1. TextPatch to fix a TypeScript file
2. CliRun command="npx tsc --noEmit src/file.ts"
   → Returns type errors if any
3. TextInspect to find error locations
4. TextPatch to fix remaining issues
5. CliRun command="npx tsc --noEmit src/file.ts"
   → Returns exitCode: 0 (success)
```

### Example 2: Run Tests After Code Changes

```
1. TextPatch to modify implementation
2. CliRun command="npm test -- --grep 'specific test'"
   → Returns test results
3. If failed, TextInspect to find relevant code
4. TextPatch to fix
5. CliRun to re-run tests
```

### Example 3: Interactive Debugging Session

```
1. CliSession command="node --inspect-brk app.js"
   → Returns sessionId
2. CliRead to see initial output
3. CliWrite input="c{enter}" (continue in debugger)
4. CliRead to see execution
5. CliWrite input="n{enter}" (next line)
6. CliRead to see current state
7. CliStop when done
```

### Example 4: Build and Deploy Workflow

```
1. CliGetPlatform to detect environment
2. CliRun command="git status" to check state
3. TextPatch to update version in package.json
4. CliRun command="npm run build"
   → If timeout, returns partial output
5. CliSession for long build
6. CliRead periodically until complete
7. CliRun command="npm run deploy"
```

### Example 5: Batch File Transformation

```
1. CliRun command="find . -name '*.md' -type f"
   → Returns list of markdown files
2. For each file:
   a. TextInspect to understand structure
   b. TextPatch to make changes
3. CliRun command="git diff --stat"
   → Returns summary of changes
```

### Example 6: Environment-Specific Configuration

```
1. CliEnv action="get" name="NODE_ENV"
   → Returns current environment
2. TextInspect filePath="config/settings.json"
3. Based on environment, TextPatch appropriate config
4. CliRun command="npm run validate-config"
```

---

## Integration with Text Tools

### Synergy Patterns

| Text Tool | CLI Complement | Purpose |
|-----------|----------------|---------|
| TextPatch | CliRun (lint) | Validate syntax after edit |
| TextCreate | CliRun (chmod) | Set permissions on new files |
| TextSearch | CliRun (grep -r) | Cross-file search before edits |
| TextRead | CliRun (head/tail) | Quick file preview |
| TextInspect | CliRun (wc, stat) | File metadata |

### Error Recovery Workflow

```
1. TextPatch makes an edit
2. CliRun validates (linter, compiler, tests)
3. If validation fails:
   a. Parse error output for line numbers
   b. TextInspect to understand context
   c. TextPatch to fix
   d. CliRun to re-validate
4. Repeat until validation passes
```

### File Discovery Pattern

```
1. CliRun command="find . -name '*.ts' | head -20"
   → Returns list of TypeScript files
2. For each file of interest:
   a. TextInspect to see structure
   b. TextSearch to find specific content
   c. TextRead to examine sections
   d. TextPatch to modify
3. CliRun command="npm run typecheck"
   → Validates all changes
```

---

## Implementation Notes

### File Location

- `McpServerCommandRunner/McpTools/McpCliGetPlatformTool.cs`
- `McpServerCommandRunner/McpTools/McpCliRunTool.cs`
- `McpServerCommandRunner/McpTools/McpCliSessionTool.cs`
- `McpServerCommandRunner/McpTools/McpCliReadTool.cs`
- `McpServerCommandRunner/McpTools/McpCliWriteTool.cs`
- `McpServerCommandRunner/McpTools/McpCliStopTool.cs`
- `McpServerCommandRunner/McpTools/McpCliEnvTool.cs`

### Domain Contracts

```csharp
public interface ICliSession : IAsyncDisposable
{
    string SessionId { get; }
    int? ProcessId { get; }
    bool IsRunning { get; }
    
    Task<string> ReadAsync(TimeSpan timeout, CancellationToken ct);
    Task WriteAsync(string input, CancellationToken ct);
    Task StopAsync(bool force, CancellationToken ct);
}

public interface ICliSessionManager
{
    Task<ICliSession> StartAsync(CliSessionOptions options, CancellationToken ct);
    ICliSession? Get(string sessionId);
    IReadOnlyList<ICliSession> GetAll();
    Task StopAllAsync(CancellationToken ct);
}

public record CliSessionOptions(
    string? Command = null,
    string? WorkingDirectory = null,
    string? SessionId = null,
    IReadOnlyDictionary<string, string>? Environment = null);
```

### Security Considerations

1. **Command sanitization**: Validate commands before execution
2. **Path restrictions**: Limit working directories to allowed paths
3. **Timeout enforcement**: Prevent runaway processes
4. **Resource limits**: Cap number of concurrent sessions
5. **Output limits**: Prevent memory exhaustion from large outputs
6. **Environment isolation**: Don't leak sensitive env vars

### Platform-Specific Behavior

| Feature | bash/sh | pwsh | cmd |
|---------|---------|------|-----|
| Command chaining | `&&`, `||`, `;` | `;`, pipeline | `&&`, `&` |
| Env var syntax | `$VAR` | `$env:VAR` | `%VAR%` |
| Background | `&` | `Start-Job` | `start /b` |
| Redirect stderr | `2>&1` | `2>&1` | `2>&1` |
| Here-doc | `<<EOF` | `@""@` | N/A |

### Error Handling

1. **Command not found**: Return helpful error with similar commands
2. **Permission denied**: Indicate which path/operation failed
3. **Timeout**: Return partial output with suggestion
4. **Session not found**: List available sessions
5. **Invalid input**: Parse error with correction hint

---

## Future Enhancements

1. **Command history**: Track commands run in session
2. **Output search**: Search within command output
3. **Script execution**: Run multi-line scripts from text files
4. **Pipe integration**: Pipe output directly to TextCreate
5. **Watch mode**: Monitor file changes and re-run commands
6. **Command aliases**: Define shortcuts for common operations
7. **Credential management**: Secure handling of secrets in commands
