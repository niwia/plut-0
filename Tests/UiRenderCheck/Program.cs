using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Pluto;
using Pluto.Controls;
using Pluto.Services;

/// <summary>
/// Renders every view under both themes and checks the filmstrip is actually alive.
///
/// This exists because the usual signals all stayed green while the filmstrip
/// drew nothing. The build succeeded, the XAML compiled, the app started, the
/// library reported 189 games, and no cards appeared: a bare ItemsControl has
/// no ControlTheme in Fluent, so the subclass had no template and no items
/// presenter, and a VirtualizingStackPanel reported no desired size so the Auto
/// row collapsed to zero height. None of that is visible to a compiler.
///
/// Run with:  dotnet run --project Tests/UiRenderCheck
/// </summary>
internal static class Program
{
    private static int _fail;

    private static void Check(string label, bool ok, string detail = "")
    {
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}: {label}{(ok || detail.Length == 0 ? "" : "  <-- " + detail)}");
        if (!ok) _fail++;
    }

    private enum View { Home, Detail, Settings }

    private static readonly View[] Views = { View.Home, View.Detail, View.Settings };

    public static int Main(string[] args)
    {
        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .WithInterFont()
            .SetupWithoutStarting();

        foreach (var style in new[] { "modern", "classic" })
        {
            Console.WriteLine();
            Console.WriteLine($"=== theme: {style} ===");

            ThemeService.ApplyStyle((App)Application.Current!,
                style == "classic" ? ThemeStyle.Classic : ThemeStyle.Modern);

            var win = new MainWindow { Width = 1600, Height = 900 };
            win.Show();
            Settle(win);

            foreach (var view in Views)
            {
                Show(win, view);
                Settle(win);

                var (colors, lit) = Capture(win);
                Console.WriteLine($"  {view,-8} {colors,6} colours  {lit,7:F2}% lit");

                // Deliberately loose: a minimal view is mostly empty by design, so
                // the check is that something was drawn, not that it was dense.
                Check($"{style}/{view} draws content", colors > 3 && lit > 0.05,
                      $"{colors} colours, {lit:F2}% lit");
            }

            win.Close();
        }

        CheckFilmstrip();

        Console.WriteLine();
        Console.WriteLine(_fail == 0 ? "ALL CHECKS PASSED" : $"{_fail} FAILED");
        return _fail == 0 ? 0 : 1;
    }

    private static void Show(MainWindow win, View view)
    {
        win.FindControl<Grid>("MainListPanel")!.IsVisible = view == View.Home;
        win.FindControl<Grid>("GameDetailPanel")!.IsVisible = view == View.Detail;
        win.FindControl<Grid>("SettingsPanel")!.IsVisible = view == View.Settings;
    }

    /// <summary>Pumps the dispatcher and forces layout so the render is settled.</summary>
    private static void Settle(MainWindow win)
    {
        for (int i = 0; i < 10; i++)
        {
            Dispatcher.UIThread.RunJobs();
            var size = new Size(win.Width, win.Height);
            win.Measure(size);
            win.Arrange(new Rect(size));
            win.UpdateLayout();
        }
    }

    /// <summary>
    /// Confirms the strip is populated and that focus movement moves.
    /// A strip can hold items and still be inert, which no pixel check catches.
    /// </summary>
    private static void CheckFilmstrip()
    {
        Console.WriteLine();
        Console.WriteLine("=== filmstrip ===");

        ThemeService.ApplyStyle((App)Application.Current!, ThemeStyle.Modern);

        var win = new MainWindow { Width = 1600, Height = 900 };
        win.Show();
        Settle(win);

        var strip = win.FindControl<Filmstrip>("Filmstrip")!;

        // The library load and artwork fetch are async; pump until it populates
        // rather than assuming one round of dispatcher jobs is enough.
        for (int i = 0; i < 200 && strip.ItemCount == 0; i++)
        {
            Dispatcher.UIThread.RunJobs();
            Settle(win);
        }

        Check("has cards", strip.ItemCount > 0, $"ItemCount={strip.ItemCount}");
        Check("has a template", strip.Template != null, "no template means nothing can draw");
        Check("has height", strip.Bounds.Height > 0, $"height={strip.Bounds.Height}");
        Check("realizes containers", strip.GetRealizedContainers().Any());

        var card = strip.GetVisualDescendants().OfType<FilmstripCard>().FirstOrDefault();
        Check("cards are sized", card != null && card.Bounds.Width > 0 && card.Bounds.Height > 0,
              card == null ? "no card realized" : $"{card.Bounds}");

        if (strip.ItemCount > 1)
        {
            strip.FocusFirst();
            var first = strip.FocusedIndex;

            strip.Move(1);
            var right = strip.FocusedIndex;
            strip.Move(-1);
            var back = strip.FocusedIndex;

            Check("focus starts valid", first >= 0, $"first={first}");
            Check("Move(1) advances", right == first + 1, $"{first} -> {right}");
            Check("Move(-1) returns", back == first, $"{right} -> {back}");

            strip.FocusedIndex = 0;
            strip.Move(-1);
            Check("movement wraps", strip.FocusedIndex == strip.ItemCount - 1,
                  $"0 -> {strip.FocusedIndex} of {strip.ItemCount}");
        }

        win.Close();
    }

    private static (int colors, double litPct) Capture(Control target)
    {
        var px = new PixelSize((int)target.Bounds.Width, (int)target.Bounds.Height);
        if (px.Width <= 0 || px.Height <= 0) return (0, 0);

        using var rtb = new RenderTargetBitmap(px, new Vector(96, 96));
        rtb.Render(target);

        // RenderTargetBitmap writes straight into unmanaged memory, so the
        // buffer is pinned rather than marshalled through a Bitmap.
        const int bytesPerPixel = 4;
        int stride = px.Width * bytesPerPixel;
        var buffer = new byte[stride * px.Height];

        var colors = new System.Collections.Generic.HashSet<uint>();
        int lit = 0;

        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer,
            System.Runtime.InteropServices.GCHandleType.Pinned);
        try
        {
            rtb.CopyPixels(new PixelRect(px), handle.AddrOfPinnedObject(), buffer.Length, stride);

            for (int i = 0; i + 3 < buffer.Length; i += 4)
            {
                uint c = (uint)(buffer[i] | (buffer[i + 1] << 8) | (buffer[i + 2] << 16) | (buffer[i + 3] << 24));
                colors.Add(c);
                if ((c & 0x00FFFFFF) != 0) lit++;
            }
        }
        finally
        {
            handle.Free();
        }

        return (colors.Count, 100.0 * lit / (px.Width * px.Height));
    }
}