# JSON Tools Specification

> MCP tools for inspecting and patching large JSON documents

## Problem Statement

LLMs need to modify JSON configuration files and data structures, but:
1. Full RFC 6902 JSON Patch is too complex for LLMs to use reliably
2. Large documents exceed context limits—LLMs can't read entire files to determine paths
3. Array index-based operations are fragile when the LLM doesn't know the index

## Solution Overview

Two complementary MCP tools:

| Tool | Purpose |
|------|---------|
| **JsonInspect** | Explore document structure without loading full content |
| **JsonPatch** | Modify documents using direct paths or match-based targeting |

---

## Tool 1: JsonInspect

### Purpose
Returns the **shape** of a JSON document or subtree, allowing the LLM to understand structure without reading full content.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | string | Yes | Absolute path to the JSON file |
| `path` | string | No | JSON Pointer to inspect (default: "" for root) |
| `depth` | int | No | How many levels deep to traverse (default: 2) |

### Returns

```json
{
  "path": "/users",
  "type": "array",
  "arrayLength": 1547,
  "elementTemplate": {
    "id": "string",
    "name": "string", 
    "email": "string",
    "settings": {
      "theme": "string",
      "notifications": "boolean"
    }
  },
  "availableKeys": ["id", "name", "email", "settings", "createdAt"]
}
```

For objects:
```json
{
  "path": "/config",
  "type": "object",
  "keys": ["database", "cache", "logging", "features"],
  "children": {
    "database": { "type": "object", "keys": ["host", "port", "name"] },
    "cache": { "type": "object", "keys": ["enabled", "ttl"] },
    "logging": { "type": "object", "keys": ["level", "format"] },
    "features": { "type": "array", "arrayLength": 12 }
  }
}
```

### Behavior

1. **Arrays**: Return length and a template derived from the first element (types only, not values)
2. **Objects**: Return keys and optionally recurse into children up to `depth`
3. **Primitives**: Return type (`string`, `number`, `boolean`, `null`)
4. **Large values**: Never return actual content, only structure

### Description for LLM

```
Inspects the structure of a JSON file without loading full content. Use this to 
understand document shape before patching.

Returns:
- For objects: list of keys and nested structure up to specified depth
- For arrays: length and a template showing the structure of elements

Always call this before JsonPatch when working with unfamiliar or large documents.

Examples:
- Inspect root: path="" (or omit), depth=2
- Inspect nested: path="/config/database", depth=1
- Check array size: path="/users", depth=1
```

---

## Tool 2: JsonPatch

### Purpose
Modifies a JSON document using simplified operations. Supports both direct JSON Pointer paths and match-based targeting for arrays.

### Parameters

| Parameter | Type | Required | Description |
|-----------|------|----------|-------------|
| `filePath` | string | Yes | Absolute path to the JSON file |
| `operation` | string | Yes | One of: `set`, `insert`, `remove`, `merge` |
| `path` | string | No* | JSON Pointer to target location |
| `match` | object | No* | Match-based targeting (see below) |
| `value` | string | No** | JSON string for the new value |

\* Either `path` or `match` must be provided, not both  
\** Required for `set`, `insert`, `merge` operations

### Match Object Schema

```json
{
  "arrayPath": "/users",
  "where": { "id": "user-123" }
}
```

| Property | Type | Description |
|----------|------|-------------|
| `arrayPath` | string | JSON Pointer to the array to search |
| `where` | object | Properties to match against array elements (shallow equality) |

### Operations

| Operation | Behavior |
|-----------|----------|
| `set` | Add or replace value at path. For arrays: `/array/-` appends to end |
| `insert` | Insert into array at index, shifting existing elements right |
| `remove` | Delete value at path |
| `merge` | Deep merge an object into existing object at path |

### Returns

```json
{
  "status": "success",
  "operation": "set",
  "targetPath": "/users/42/status",
  "previousValue": "pending",
  "newValue": "active"
}
```

On error:
```json
{
  "status": "error",
  "message": "Path '/users/9999' not found. Array length is 1547.",
  "suggestion": "Use match-based targeting or JsonInspect to find the correct path"
}
```

### Behavior

1. **Path resolution**: 
   - If `path` provided: use directly as JSON Pointer
   - If `match` provided: find first matching element, derive path automatically

2. **Set operation**:
   - Creates intermediate objects/arrays if they don't exist
   - `/array/-` means append to array
   - `/array/N` replaces element at index N

3. **Insert operation**:
   - Only valid for array paths (`/array/N`)
   - Inserts **before** index N (new element becomes index N)
   - Existing elements shift right

4. **Remove operation**:
   - Deletes the value at path
   - For arrays: shifts remaining elements left

5. **Merge operation**:
   - Target must be an object
   - Deep merges `value` into existing object
   - Does not delete keys, only adds/updates

### Description for LLM

```
Modifies a JSON file by applying a patch operation.

Operations:
- 'set': Add or replace a value at the specified path
- 'insert': Insert into array at index (shifts existing elements right)
- 'remove': Delete the value at the specified path
- 'merge': Deep merge an object into existing object at path

Targeting (use ONE of these):
1. Direct path: JSON Pointer syntax (e.g., "/config/timeout", "/users/0")
2. Match-based: Find array element by properties
   - match.arrayPath: path to the array (e.g., "/users")
   - match.where: properties to match (e.g., {"id": "user-123"})

Path examples:
- "" = root document
- "/name" = top-level 'name' property
- "/users/0" = first element of 'users' array
- "/users/-" = append to 'users' array (set only)

Match-based targeting is preferred for large arrays where the index is unknown.
Use JsonInspect first to understand document structure.

Examples:
- Set nested value: path="/settings/theme", operation="set", value="\"dark\""
- Remove property: path="/obsoleteConfig", operation="remove"
- Append to array: path="/items/-", operation="set", value="{\"name\":\"new\"}"
- Update by ID: match={"arrayPath":"/users","where":{"id":"123"}}, operation="set", value="{\"status\":\"active\"}"
- Insert at position: path="/items/2", operation="insert", value="{\"name\":\"inserted\"}"
```

---

## Workflow Examples

### Example 1: Update a User's Email (Large Array)

```
1. LLM calls JsonInspect
   filePath: "/data/users.json"
   path: "/users"
   depth: 1
   
   → Returns: { arrayLength: 50000, elementTemplate: { id, name, email, ... } }

2. LLM calls JsonPatch
   filePath: "/data/users.json"
   operation: "set"
   match: { arrayPath: "/users", where: { id: "user-abc-123" } }
   value: "{\"email\": \"newemail@example.com\"}"
   
   → Returns: { status: "success", targetPath: "/users/4271/email", ... }
```

### Example 2: Add a New Feature Flag

```
1. LLM calls JsonInspect
   filePath: "/config/features.json"
   path: ""
   depth: 2
   
   → Returns: { type: "object", keys: ["flags", "experiments"], children: {...} }

2. LLM calls JsonPatch
   filePath: "/config/features.json"
   operation: "set"
   path: "/flags/newFeature"
   value: "{\"enabled\": true, \"rolloutPercent\": 10}"
```

### Example 3: Insert Item in Middle of Array

```
1. LLM calls JsonInspect to understand structure
2. LLM calls JsonPatch
   filePath: "/data/playlist.json"
   operation: "insert"
   path: "/tracks/5"
   value: "{\"id\": \"track-new\", \"name\": \"New Song\"}"
   
   → Inserts at position 5, existing items shift right
```

---

## Implementation Notes

### File Location
- `McpServerLibrary/McpTools/McpJsonInspectTool.cs`
- `McpServerLibrary/McpTools/McpJsonPatchTool.cs`

### Dependencies
- `System.Text.Json` for JSON parsing and manipulation
- JSON Pointer parsing (RFC 6901) - implement or use library

### Validation Rules
1. `filePath` must be within allowed library paths
2. `path` must be valid JSON Pointer syntax
3. `value` must be valid JSON when provided
4. `match.where` performs shallow property equality
5. Return clear errors with suggestions when paths not found

### Error Messages Should Include
- What was attempted
- Why it failed
- Available alternatives (e.g., "Array length is 50, index 100 is out of bounds")
- Suggestion to use JsonInspect if structure is unclear
