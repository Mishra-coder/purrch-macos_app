# Changelog

## 1.0.4 — 2026-10-18

**Download:** [Purrch-1.0.4.dmg](https://github.com/Mishra-coder/purrch-macos_app/releases/download/v1.0.4/Purrch-1.0.4.dmg)

- New Translate module. Type in any language, including Hinglish such as "kal milte hain yaar", and pick the language to translate into. The source language is detected automatically, or you can choose it and swap the two. It uses the same AI providers as MacMate (Apple on-device, Claude, OpenAI or Gemini), and you can pick a different one just for Translate. Translation runs live as you type and shows the detected language; your target language stays until you change it.
- Grammarly-style writing help in the same module: **Fix grammar** corrects spelling, grammar and punctuation; **Improve** rewrites for clarity; **Tone** makes text formal, professional, friendly, confident or casual; **Shorten** makes it more concise. Changed words are highlighted, and **Accept** puts the result back in the editor. Word count, sentence count and reading time are shown below the text.

## 1.0.3 — 2026-10-18

**Download:** [Purrch-1.0.3.dmg](https://github.com/Mishra-coder/purrch-macos_app/releases/download/v1.0.3/Purrch-1.0.3.dmg)

- The video title and controls fade out after a few seconds of playback and come back when the pointer moves over the video, like YouTube. A thin red progress line stays along the bottom while they are hidden.
- Full notch grows a little taller so a 16:9 video fills its whole width, with no black bars at the sides.
- Captures shows all your screenshots and screen recordings (up to the newest 500) as small tiles, newest first. It no longer stops at 24 hours, and other images on the Desktop are left out.
- No jump or stutter when the notch opens: the hidden player keeps up with the browser's position while the notch is closed, for up to 5 minutes.

## 1.0.2 — 2026-10-17

**Download:** [Purrch-1.0.2.dmg](https://github.com/Mishra-coder/purrch-macos_app/releases/download/v1.0.2/Purrch-1.0.2.dmg)

- The notch video now appears as soon as the panel opens. The YouTube player loads in the background while a browser video plays and stays ready after the notch closes, instead of reloading on every open.
- Smoother playback in the notch. The video catches up with the browser tab by playing slightly faster or slower; it seeks only for large gaps, and rarely, so it no longer stalls in a seek and re-buffer loop.
- Fixed videos falling back to low-frame-rate mirroring when YouTube ignored the first autoplay request or took longer than 8 seconds to start.
- Full notch (theater) closes normally when the pointer leaves. Hovering the notch again returns to the same video; Exit or Esc leaves Full notch.

## 1.0.1 — 2026-10-17

**Download:** [Purrch-1.0.1.dmg](https://github.com/Mishra-coder/purrch-macos_app/releases/download/v1.0.1/Purrch-1.0.1.dmg)

- Hover a dock icon to open that module; no click needed.
- Now Playing video keeps its size while it loads instead of stretching the panel.
- Full notch video stays open until Exit or Esc. Reverted in 1.0.2, where it closes on hover-out and resumes on the next hover.

## 1.0.0 — 2026-10-17

- First release.
