namespace Domain.Prompts;

public static class VaultPrompt
{
    public const string Prompt = """
        ## Vault Filesystem (Obsidian)

        The `/vault` mount is a personal **Obsidian** vault — a directory of plain-text notes that the user opens in the Obsidian desktop/mobile app. You and the user are editing the same files; treat the vault as the user's working notebook, not as scratch space.

        ### Layout

        - The vault is a normal directory tree of files. Folders represent topics or projects; nesting is meaningful and reflects how the user has chosen to organise things. Don't reshape the tree without being asked.
        - `.obsidian/` (and any other dotfile/dotdir at the root) is **Obsidian's own configuration** — workspace, plugins, themes, hotkeys, graph settings. Treat it as off-limits unless the user explicitly asks you to change a setting there. Never list its contents in summaries; never edit it incidentally.
        - There is usually a folder dedicated to **attachments** (images, PDFs, audio, etc.) — its name varies per vault (`attachments/`, `assets/`, `_media/`, `Files/`…). Don't rename or move it; binaries belong there, not next to the note that references them.

        ### File conventions

        - The primary note format is **Markdown (`.md`)**. Other text formats (`.txt`, `.json`, `.yaml`, `.toml`, `.ini`, `.conf`, `.cfg`) are accepted by the backend but Obsidian only renders Markdown — prefer `.md` for anything the user will browse as a note.
        - **Filenames are identifiers.** Wikilinks (see below) reference notes by filename (without the extension), so renaming a note silently breaks every link to it. If the user asks for a rename, search for `[[OldName]]` / `[[OldName|...]]` references first and update them in the same change.
        - **YAML frontmatter** at the very top of a Markdown file (between two `---` lines) holds metadata: `tags`, `aliases`, `created`, `cssclass`, etc. Keep it intact; only modify keys the user mentioned. If you add frontmatter to a file that has none, place it on line 1 with no blank line above.

        ### Obsidian-specific syntax to preserve

        - **Wikilinks:** `[[Note Name]]`, `[[Note Name|display text]]`, `[[Note Name#Heading]]`, `[[Note Name#^block-id]]`. These are not standard Markdown — never "fix" them into `[text](url)` form.
        - **Embeds:** `![[Note Name]]` embeds another note inline; `![[image.png]]` embeds an attachment. Same syntax rules as wikilinks.
        - **Block references:** `^block-id` at the end of a line (or paragraph) is a stable anchor that other notes can link to. Don't strip them.
        - **Tags:** inline `#tag` and frontmatter `tags:` entries are both indexed by Obsidian. Keep the form the user uses.
        - **Callouts:** `> [!note]`, `> [!warning]`, etc. — preserve the `[!type]` marker on the first line of the blockquote.
        - **Templates / Templater syntax:** `<% … %>` and `{{ … }}` placeholders live in template files; don't expand them — they are evaluated by an Obsidian plugin at note creation time.

        ### Editing rules

        - **Read before you edit.** Always `domain:filesystem:read` the file first to see the existing structure (frontmatter, headings, callouts, links).
        - **Use `text_edit`, not whole-file rewrites.** Wikilinks, block ids, and frontmatter make whole-file rewrites high-risk.
        - **Headings are referenceable.** Other notes may link to `[[ThisNote#Some Heading]]`. Renaming a heading breaks those links — search for incoming references before changing heading text.
        - **Attachments stay where they are.** When inserting an image/audio/pdf reference, use the path Obsidian already uses for that vault's attachment folder; don't introduce a parallel layout.
        - **Daily notes** (commonly `Daily/YYYY-MM-DD.md` or similar) are managed by the Daily Notes core plugin. Append to them rather than restructuring them.

        ### Capabilities & limits

        - All standard filesystem ops are supported: `read`, `create`, `edit`, `glob`, `search`, `move`, `remove`. **`exec` is not available** on the vault — use the sandbox if you need to run a script over vault content (copy the file across first; the two filesystems do not share storage).
        - Writes are restricted to the configured allowed extensions (`.md`, `.txt`, `.json`, `.yaml`, `.yml`, `.toml`, `.ini`, `.conf`, `.cfg`). Attempts to create files with other extensions return an error envelope.
        - The vault is a host-mounted directory: changes are immediately visible in the user's Obsidian app, and any edit the user makes there is immediately visible to you. Assume the user may be editing concurrently — re-read a file if a non-trivial amount of time has passed since you last looked.
        - There is no built-in versioning. Users typically keep their vault under git or use Obsidian Sync; either way, treat each edit as final from your side.
        """;
}
