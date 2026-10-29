# Third-party notices

## DynamicNotchKit (design reference)

[DynamicNotchKit](https://github.com/MrKai77/DynamicNotchKit) (MIT License, Copyright (c) 2025 Kai Azim), read from `main`
(`Sources/DynamicNotchKit/DynamicNotch/DynamicNotchStyle.swift`, `DynamicNotch.swift`, `Views/NotchView.swift`).
No code was copied; only numbers and the order of events were taken, to follow the macOS Dynamic Island:

| Where | What follows DynamicNotchKit |
| --- | --- |
| `IslandMotion.cs` `Bouncy`, `Smooth`, `Snappy` | Opening is a bouncy spring of 0.4 s, closing a smooth one of 0.4 s, morphing a snappy one of 0.4 s (SwiftUI's presets, converted to stiffness and damping) |
| `UsageIsland.cs` `FadeOutDelay`, `Motion.Vanish` | On closing the content leaves first; the window fades 0.25 s later, over 0.15 s |
| `UsageIsland.cs` `DrawBlurFaded` | Content enters blurred, squeezed to 0.6 of its height from the top, fading in |

## Agent HUD (design reference)

[Agent HUD](https://github.com/jazzenchen/agent-hud-open) (Apache-2.0), commit `8fb6eb8567d4638b858bda71e4290b406d29c2bc`.
No code or assets were copied (its bots, provider artwork and Clawd sprites are not used). Taken as design only:

| Where | What follows Agent HUD | Agent HUD source |
| --- | --- | --- |
| `IslandStyle.cs` `Level`, `IslandSurfaces.cs` | Quota levels (green under 70 %, amber from 70 %, red from 90 %), ring around the logo (Simple mode) | `docs/parity/c07-glow-alerts-theme.md`, README |
| `QuotaWatcher.cs`, `Program.cs` | Alert kinds (Completed, Question, Warning, Reset), reset reported from a later reading, not a timer | agenthud.app, `docs/parity/c07-glow-alerts-theme.md` |
| `UsageIsland.cs` | Coloured glow with a 1.8 s pulse on arrival (double for warnings) | `docs/parity/c07-glow-alerts-theme.md` (#28) |
| `UsageIsland.cs` (live icons) | A working session is a look of its own, redrawn from an absolute clock so a stalled frame never makes the motion jump; animation stops under reduced motion or when hidden | `src/packages/hud/src/BotMark.tsx`, `bots/bot-motion.ts` (read, not copied) |
| `SessionJump.cs`, `UsageIsland.cs` | Clicking a notice returns to its session's window (behaviour only; the Windows lookup is new) | README ("Jump back"), `src/crates/platform/src/navigation.rs` |
| `HookSupport.cs` | Opt-in client hook installed by the app, preserving other entries (idea only; observation-only Claude Code command hooks are the only kind written) | `docs/client-hooks.md` |
| `Program.cs` `ReadClaudeEngineAsync` | Asking Claude Code's own engine for `get_usage` over stream-json | `src/crates/agents/src/claude/account.rs` |

## Oyenza Studio (design reference)

TrayUsage's island borrows its design from [Oyenza Studio](https://github.com/lucacadalora/oyenza-studio)
(commit `6382a85be2fd149374558a5bcecd1426ede6c53b`). No source code was copied; the C# implementation is new.
What was taken is the design: spring tunings, morph and hover behavior, shadow curve, notice and tab proportions.

| Where | What follows Oyenza | Oyenza source |
| --- | --- | --- |
| `TrayUsage/IslandMotion.cs` | Per-axis spring tunings, shrink floor, content fade tuning, shadow ramp | `crates/poni/src/pill/motion.rs`, `pill/shape.rs` |
| `TrayUsage/UsageIsland.cs` | Floating-pill geometry, hover lift, content drop and ghost scale, fixed layered window | `pill/motion.rs`, `island.rs` |
| `TrayUsage/IslandSurfaces.cs` | Notice capsule and tab pill proportions, tab-based pages | `pill/notice.rs`, `ui.rs` |
| `TrayUsage/HoverTrigger.cs` | Dwell with slop, collapse delay, hold grace | `app.rs` |
| `TrayUsage/IslandStyle.cs`, `UsageIsland.cs` | Classic look (island hanging from the top edge with 9 DIP concave shoulders), Pill look (floating), theme choice | `island.rs`, `pill/shape.rs`, README |

The Oyenza name, logo and Oyen character are not covered by the MIT license and are not used here.

```
MIT License

Copyright (c) 2026 Jatevo Labs

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Claude Island / notchi / djui-notch (design references for the detail card)

The notice detail card (task, highlighted message with choices, tool chips, figure tiles) follows the general idea of agent-notch apps that show a session summary and what is waiting on the user: [Claude Island](https://github.com/farouqaldori/claude-island) (Apache-2.0) and [djui/notch](https://github.com/djui/notch) (MIT). Only the idea was read from their READMEs; no code, artwork or layout numbers were copied. notchi (GPL-3.0-only) was looked at only through its README and nothing was taken from it.

## Noto Sans JP (font)

The island draws its text in Noto Sans JP (SIL Open Font License 1.1, https://fonts.google.com/noto/specimen/Noto+Sans+JP) when it is installed on the PC. The font is not bundled or redistributed; without it the island falls back to Segoe UI.
