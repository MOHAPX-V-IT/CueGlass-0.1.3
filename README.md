![CueGlass — AI meeting assistant for Windows](assets/github-preview.png)

# CueGlass

English · [Русский](README.ru.md)

A native Windows assistant that listens to meeting audio, uses screen snapshots and your notes as context, and streams text suggestions into a floating glass window.

**Version 0.1.3 · C# / WPF · OpenAI Realtime · MIT**

## What it does

- Captures microphone input and system playback audio through WASAPI.
- Shows streaming suggestions automatically after speech pauses, or on a hotkey.
- Uses snapshots of a selected monitor and imported notes or source files.
- Provides a code mode for longer explanations, fixes and new application scaffolds.
- Offers native glass blur, adjustable background density and a custom title bar.
- Requests Windows capture exclusion, with an additional Discord compatibility option.

The app connects directly to OpenAI. It has no application server or embedded browser. The interface and default response instructions are currently in Russian.

## Preview

<p align="center">
  <img src="verification/glass-preview.png" alt="CueGlass glass interface with demonstration content" width="390">
</p>

This is the actual application over a synthetic desktop background. All content shown is demonstration data.

## Get started

**Requirements:** Windows 10 version 2004 or later / Windows 11, x64; an internet connection; an OpenAI API key with billing enabled and access to the selected Realtime model. The portable build includes the .NET runtime.

1. Extract the distribution archive and open `app/CueGlass.exe`. If using source code, [build it first](#build-from-source).
2. Open **Настройки** (Settings), enter your API key and optionally enable **Сохранить ключ в Windows** (Save key in Windows).
3. Choose the microphone and playback device used by your call. A headset helps prevent duplicate audio.
4. Choose a monitor, enable screen context if needed, and add meeting notes or source code.
5. Click **Начать** (Start). Selected audio and screen context will be sent to OpenAI, using your API balance.

This version uses an API key; it has no ChatGPT sign-in button. It starts an independent model session without your ChatGPT/Codex chat history, memory, tools or project access.

## Controls

| Action | Shortcut / control |
| --- | --- |
| Request a suggestion | `Ctrl+Shift+Space` |
| Pause / resume transmission | `Ctrl+Shift+P` |
| Hide / show the window | `Ctrl+Shift+H` |
| Ask a text question | Type in the bottom input, then `Enter` |
| Copy an answer | Right-click the answer card |
| End the session | **Завершить** or close the app |

Hiding the window leaves the session active. Pause blocks new audio and image transmissions; it cannot recall data already sent. End the session to release capture devices. Sessions stop after 55 minutes; restarting creates a new session. There is no automatic reconnection or conversation restore after a connection failure.

## Audio, screen and code context

Audio from both sources is mixed into one channel, so speaker attribution is unreliable. Responses are text only. The model receives still screen images, not continuous video. Unchanged frames are reused; optional periodic updates run every 15 seconds.

The speech pause is adjustable from 450 to 1200 ms, with a 600 ms default. Screenshots are prepared during speech and answers stream as they arrive. These optimizations preserve the selected model and do not lower reasoning effort. End-to-end latency depends on your connection and the API.

For code tasks, enable **Код: развёрнутые ответы и исправления** before connecting and import the relevant text files, up to 200 KB each. Code mode raises the answer budget from 1200 to 4096 tokens. Add the error, expected behavior and relevant context; for a new app, describe its stack and request files in stages.

Imported files are read once. After editing them, refresh the context and start a new session. CueGlass suggests code as text; it does not edit your project, apply patches or run tests.

## Screen-sharing behavior

Capture exclusion is enabled by default using `WDA_EXCLUDEFROMCAPTURE`, with an optional `WCA_EXCLUDED_FROM_DDA` compatibility flag. The status line confirms Windows accepted these settings; it does not verify the viewer's stream.

A user confirmed that the black rectangle disappeared in Discord with version 0.1.1 on Windows 10 build 19045. Version 0.1.3 passed a local GDI capture check with the new glass interface; this was not a new Discord test. Capture behavior depends on Windows and the sharing application's capture method. Check the viewer's actual feed before relying on it. CueGlass remains a normal Windows process.

## Data and API costs

| Data | Handling |
| --- | --- |
| API key | Sent directly to OpenAI for authentication. Optional local storage uses Windows DPAPI for the current user. |
| Settings and imported context | Stored as plain text in `%LOCALAPPDATA%\CueGlass\settings.json`. |
| Audio, screenshots and transcripts | Not saved to disk by the app during normal use. Enabled inputs are transmitted to OpenAI during a session. |
| Analytics | No application analytics, telemetry or intermediary server. |

The saved key is at `%LOCALAPPDATA%\CueGlass\api-key.dpapi` and can be removed in Settings. API charges and provider-side data handling follow your OpenAI account's terms. The token counter is informational; it is not a balance or a currency estimate.

## Build from source

Install the **.NET 8 SDK** on Windows and run from the project root:

```powershell
.\build.ps1
```

The script restores locked dependencies and publishes a self-contained, single-file `win-x64` build to `app/`. No separate runtime installation is needed to run that EXE. The current build is not publisher-signed.

| Path | Contents |
| --- | --- |
| `src/` | WPF UI, audio capture, Realtime client and local diagnostics |
| `assets/` | README banner and GitHub social preview |
| `verification/` | Diagnostic reports and demonstration screenshots |
| `licenses/` | Third-party licenses and notices |

## Verification

Local synthetic checks cover DPAPI, audio conversion, WebSocket event ordering and UTF-8 streaming, pause behavior, screenshot caching, window lifecycle, glass rendering and capture flags. See the [self-test report](verification/self-test.json) and [visual check](verification/visual-check.json).

These checks do not use a real OpenAI API key or record a live meeting. Live API latency and current Discord compatibility are not covered by them.

## Design and license

Native WPF visual patterns were informed by [Glass Card](https://21st.dev/@educalvolpz/components/glass-card), [Segmented Tabs](https://21st.dev/@micka_design/components/tabs-base) and the [Darkmatter theme](https://21st.dev/community/themes/darkmatter) on 21st.dev. React components are not bundled.

The banner was generated with ImageGen; its [prompt](assets/preview-prompt.txt) is included. To set a repository social preview, upload [github-social-preview.jpg](assets/github-social-preview.jpg) under **Settings → Social preview → Edit**, as described in the [GitHub guide](https://docs.github.com/en/repositories/managing-your-repositorys-settings-and-features/customizing-your-repository/customizing-your-repositorys-social-media-preview). Adding a banner to the README does not set this repository setting automatically.

[MIT license](LICENSE). Dependency notices are included in [licenses/](licenses/).
