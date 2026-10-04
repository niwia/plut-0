# UI render check

Renders every view under both themes and asserts the filmstrip is actually
alive.

    dotnet build            # build the app first: this references its output
    dotnet run --project Tests/UiRenderCheck

It is a standalone project on purpose. It has its own `Main` and needs
`Avalonia.Headless` and `Avalonia.Skia`, which the app itself does not carry, so
it references `bin/Debug/net10.0/Pluto.dll` rather than recompiling the sources.
Re-compiling would mean re-creating the Avalonia XAML source generator wiring
that produces `InitializeComponent`. `Pluto.csproj` excludes `Tests/**` so the
default glob does not pull this project into the app build.

## Why it exists

The filmstrip shipped in a state where every ordinary signal was green:

- the build succeeded
- the XAML compiled
- the app started and logged `Library updated: 189 total`
- the filmstrip reported 189 items and moved its focus index correctly

and it drew absolutely nothing. Two independent causes:

1. **No template.** Neither theme provides a `ControlTheme` for a bare
   `ItemsControl`; Fluent only themes `ListBox` and friends. The subclass had
   `Template == null`, so there was no items presenter and no containers.
2. **No height.** A `VirtualizingStackPanel` reports no desired size along its
   stacking axis, so the enclosing `Auto` row collapsed and the strip measured
   `1560 x 0`.

Neither is visible to a compiler, and neither throws at runtime. Pixel checks
plus an explicit container count are what surfaced them.

## What it asserts

- each of home, detail and settings renders under both the modern and classic
  themes (distinct-colour count and lit-pixel fraction)
- the filmstrip has a template, non-zero height, realized containers, and cards
  at a non-zero size
- focus movement advances, reverses, and wraps at both ends

The pixel thresholds are deliberately loose. These views are minimal by design,
so the check is that something was drawn, not that the screen is dense. The
classic theme legitimately produces far fewer distinct colours because its
bitmap font and flat fills do not antialias.