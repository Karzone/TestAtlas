# Terminal demo

The animated demo in the main README is generated automatically — no manual
screen recording.

## How it works

- [`demo.tape`](demo.tape) is a [VHS](https://github.com/charmbracelet/vhs)
  script describing the exact keystrokes and pauses of the demo.
- [`.github/workflows/demo.yml`](../../.github/workflows/demo.yml) runs on an
  Ubuntu runner whenever the tape changes (or on manual dispatch). It:
  1. installs the published `TestAtlas.Cli` tool from NuGet,
  2. plays the tape against the bundled [`samples/SampleShop`](../../samples/SampleShop) solution, and
  3. commits the rendered `testatlas-demo.gif` back to this folder.

Because it runs the real CLI against a real sample, the demo can never drift
from what the tool actually does.

## Changing the demo

Edit [`demo.tape`](demo.tape) and push it to `main` — the workflow re-renders
the GIF. To preview locally instead (needs `vhs`, `ffmpeg`, and `ttyd`
installed):

```bash
dotnet tool install -g TestAtlas.Cli
vhs docs/demo/demo.tape
```

The `Sleep` values in the tape are tuned to the commands' real durations
(`index` takes several seconds; the query commands are sub-second). If you add
or reorder steps, adjust the sleeps so output has time to appear.
