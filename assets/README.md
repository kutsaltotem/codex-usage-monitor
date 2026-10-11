# Logo assets

All five JPEGs are byte-for-byte copies of the images supplied by the user.

The mockup uses these three marks at a shared size and on a shared white tile:

- `openai-mark-codex.jpg` for the Codex indicator (the supplied OpenAI knot mark)
- `gemini-mark.jpg` for Gemini
- `claude-mark.jpg` for Claude

`claude-app-icon.jpg` and `gemini-app-icon.jpg` are retained as alternate supplied variants. The mockup scales the image elements with CSS and does not alter the source files.


## Runtime vector marks

The WPF app now uses shared, frozen vector drawings in `BrandMarks.cs` instead of the JPEG tiles. The original supplied JPEGs remain unchanged for the earlier mockup.

Vector source: [LobeHub Icons](https://github.com/lobehub/lobe-icons/tree/c385b2b8d1f9e19aa86e628d4e23c91ee1111a47/packages/static-svg/icons), pinned to commit `c385b2b8d1f9e19aa86e628d4e23c91ee1111a47`. Original SVGs are retained in `vector/`; the full MIT notice is in `vector/LICENSE-LobeHub.txt` and is bundled with the application.

The 24×24 source paths are used directly. OpenAI's monochrome mark is light on dark surfaces; Claude's is warm orange. Gemini retains its layered color gradients. There are no runtime downloads or SVG libraries. All brand marks identify the corresponding provider and remain trademarks of their respective owners; the app is independent and unaffiliated.
