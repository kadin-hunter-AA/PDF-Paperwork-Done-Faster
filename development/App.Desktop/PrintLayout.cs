namespace HunterPdf.Desktop;

internal sealed record PrintPageSize(double Width, double Height);

internal enum PrintScaling { FitToPage, ActualSize, Custom }

internal sealed record PrintOptions(PrintScaling Scaling = PrintScaling.FitToPage, double CustomPercent = 100, int PagesPerSheet = 1,
    bool AutoOrientation = true, bool AsImage = false, int Copies = 1);

/// <summary>Rectangle in points, origin at the top-left of the printable area.</summary>
internal readonly record struct PrintRect(double X, double Y, double Width, double Height)
{
    internal double Right => X + Width;
    internal double Bottom => Y + Height;
    internal double IntersectionArea(PrintRect other)
    {
        var w = Math.Min(Right, other.Right) - Math.Max(X, other.X);
        var h = Math.Min(Bottom, other.Bottom) - Math.Max(Y, other.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }
}

/// <summary>Where one PDF page lands on a sheet: Target is the full scaled page (it may extend past Clip); Clip is its cell.</summary>
internal sealed record PrintPlacement(int PageIndex, PrintRect Target, PrintRect Clip, double Scale);

internal sealed record SheetPlan(bool Landscape, double AreaWidth, double AreaHeight, IReadOnlyList<PrintPlacement> Placements);

internal readonly record struct PrintRasterSize(int Width, int Height, double Dpi, bool Reduced);

internal static class PrintLayout
{
    internal const double TargetDpi = 300;
    internal const long MaximumRasterPixels = 40_000_000;
    internal const int MaximumRasterDimension = 32768;
    internal const double CellGutter = 8;

    internal static (int Columns, int Rows) Grid(int pagesPerSheet, bool landscape) => pagesPerSheet switch
    {
        1 => (1, 1),
        2 => landscape ? (2, 1) : (1, 2),
        4 => (2, 2),
        _ => throw new InvalidOperationException("Choose 1, 2 or 4 pages per sheet.")
    };

    internal static void Validate(PrintOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.PagesPerSheet is not (1 or 2 or 4)) throw new InvalidOperationException("Choose 1, 2 or 4 pages per sheet.");
        if (!double.IsFinite(options.CustomPercent) || options.CustomPercent is < 1 or > 1000)
            throw new InvalidOperationException("Enter a custom scale from 1% to 1000%.");
        if (options.Copies is < 1 or > 999) throw new InvalidOperationException("Copies must be from 1 to 999.");
    }

    static double ScaleFor(PrintPageSize page, double cellWidth, double cellHeight, PrintOptions options) =>
        options.PagesPerSheet > 1 || options.Scaling == PrintScaling.FitToPage ? Math.Min(cellWidth / page.Width, cellHeight / page.Height)
        : options.Scaling == PrintScaling.ActualSize ? 1d : options.CustomPercent / 100d;

    /// <summary>Plans one sheet. N-up always fits each page to its cell. With auto orientation the sheet orientation that shows the most page area wins; ties keep the printer's orientation.</summary>
    internal static SheetPlan PlanSheet(IReadOnlyList<int> pageIndices, IReadOnlyList<PrintPageSize> sizes, PrintOptions options,
        double portraitWidth, double portraitHeight, bool ticketLandscape)
    {
        Validate(options);
        ArgumentNullException.ThrowIfNull(pageIndices);
        ArgumentNullException.ThrowIfNull(sizes);
        if (pageIndices.Count is < 1 || pageIndices.Count > options.PagesPerSheet) throw new InvalidOperationException("A sheet holds between one and the chosen number of pages.");
        foreach (var index in pageIndices)
        {
            if (index < 0 || index >= sizes.Count) throw new InvalidOperationException("Invalid page index.");
            var s = sizes[index];
            if (!double.IsFinite(s.Width) || !double.IsFinite(s.Height) || s.Width <= 0 || s.Height <= 0)
                throw new InvalidOperationException("This page has invalid dimensions and cannot be printed.");
        }
        if (!double.IsFinite(portraitWidth) || !double.IsFinite(portraitHeight) || portraitWidth < 1 || portraitHeight < 1)
            throw new InvalidOperationException("The selected printer has invalid printable page dimensions.");
        var candidates = options.AutoOrientation ? new[] { ticketLandscape, !ticketLandscape } : new[] { ticketLandscape };
        SheetPlan? best = null;
        var bestScore = -1d;
        foreach (var landscape in candidates)
        {
            var plan = PlanFor(pageIndices, sizes, options, landscape ? portraitHeight : portraitWidth, landscape ? portraitWidth : portraitHeight, landscape);
            var score = plan.Placements.Sum(p => p.Target.IntersectionArea(p.Clip));
            if (best is null || score > bestScore * (1 + 1e-9) + 1e-9) { best = plan; bestScore = score; }
        }
        return best!;
    }

    static SheetPlan PlanFor(IReadOnlyList<int> pageIndices, IReadOnlyList<PrintPageSize> sizes, PrintOptions options, double width, double height, bool landscape)
    {
        var (columns, rows) = Grid(options.PagesPerSheet, landscape);
        var gutter = options.PagesPerSheet > 1 ? Math.Min(CellGutter, Math.Min(width, height) / 8) : 0;
        var cellWidth = (width - gutter * (columns - 1)) / columns;
        var cellHeight = (height - gutter * (rows - 1)) / rows;
        var placements = new List<PrintPlacement>();
        for (var i = 0; i < pageIndices.Count; i++)
        {
            var cell = new PrintRect(i % columns * (cellWidth + gutter), i / columns * (cellHeight + gutter), cellWidth, cellHeight);
            var page = sizes[pageIndices[i]];
            var scale = ScaleFor(page, cellWidth, cellHeight, options);
            var w = page.Width * scale;
            var h = page.Height * scale;
            placements.Add(new PrintPlacement(pageIndices[i], new PrintRect(cell.X + (cellWidth - w) / 2, cell.Y + (cellHeight - h) / 2, w, h), cell, scale));
        }
        return new SheetPlan(landscape, width, height, placements);
    }

    /// <summary>Groups the selected pages into sheets of PagesPerSheet (the last sheet may be partly empty).</summary>
    internal static IEnumerable<int[]> Sheets(int[] pages, int pagesPerSheet) => pages.Chunk(pagesPerSheet);

    /// <summary>Raster size for a page placed at <paramref name="scale"/>: 300 DPI of the printed size, reduced when it would exceed the pixel budget.</summary>
    internal static PrintRasterSize GetRasterSize(double pageWidthPoints, double pageHeightPoints, double scale = 1)
    {
        if (!double.IsFinite(pageWidthPoints) || !double.IsFinite(pageHeightPoints) || pageWidthPoints <= 0 || pageHeightPoints <= 0 || !double.IsFinite(scale) || scale <= 0)
            throw new InvalidOperationException("This page has invalid dimensions and cannot be printed.");
        // Normalize by the longest side before multiplying so extreme finite sizes cannot overflow the budget math.
        var longest = Math.Max(pageWidthPoints, pageHeightPoints);
        var widthRatio = pageWidthPoints / longest;
        var heightRatio = pageHeightPoints / longest;
        var wanted = longest * scale * TargetDpi / 72d;
        var limit = Math.Min(MaximumRasterDimension, Math.Sqrt(MaximumRasterPixels / (widthRatio * heightRatio)));
        var longestPixels = Math.Max(1d, Math.Min(wanted, limit));
        var width = Math.Max(1, (int)Math.Floor(widthRatio * longestPixels));
        var height = Math.Max(1, (int)Math.Floor(heightRatio * longestPixels));
        var dpi = longestPixels / (longest * scale / 72d);
        return new PrintRasterSize(width, height, Math.Min(TargetDpi, dpi), wanted > limit);
    }
}
