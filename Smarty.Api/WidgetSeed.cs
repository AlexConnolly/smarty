namespace Smarty.Api;

/// <summary>
/// The page a first-time visitor gets.
///
/// <para>
/// Written as ordinary panels — component plus internal feed — rather than coded into the home page, and that is
/// the point of them being here: what's on today and what's running are not privileged. They go through the same
/// renderer, read the same kind of feed, and can be resized, pinned or thrown away like anything the system builds
/// for itself later. There is exactly one rendering path, so a bug in it shows up on day one rather than only on
/// the panels a model wrote.
/// </para>
/// <para>
/// They also serve as worked examples. A builder is shown the kit in its brief; these are what using it properly
/// looks like against a feed whose shape is known.
/// </para>
/// </summary>
public static class WidgetSeed
{
    public static void Ensure(WidgetStore widgets, WidgetLibrary library)
    {
        if (widgets.All().Count > 0) return;

        Add(widgets, library, "today", "Today", "what is on today and tomorrow, from the dated items on your lists",
            WidgetSizes.Wide, 75, WidgetInternals.Agenda, "every 30 minutes", Today, "today", "tomorrow");
        Add(widgets, library, "running-work", "Running work", "the background jobs running now and any waiting on you",
            WidgetSizes.Kpi, 55, WidgetInternals.Tasks, "every 2 minutes", Running, "running");
        Add(widgets, library, "project-list", "Projects", "the projects currently on the go",
            WidgetSizes.Kpi, 25, WidgetInternals.Projects, "hourly", Projects, "projects");

        Console.WriteLine("[widget] first run — seeded 3 panels and their kinds from what we already hold");
    }

    private static void Add(WidgetStore widgets, WidgetLibrary library, string kindName, string title,
        string description, string size, int priority, string internalFeed, string refresh, string code,
        params string[] model)
    {
        // Published as library kinds like anything else — these take no parameters because they are about the
        // user's own data rather than a particular thing in the world, which is the only difference.
        var kind = library.Upsert(new WidgetKind
        {
            Name = kindName,
            Title = title,
            Description = description,
            Code = code.Trim(),
            // Declared like any other kind: the component renders these names and nothing else, and the internal
            // feed is built to produce them.
            Model = model.Select(f => new WidgetField { Name = f, Type = "list", Description = f }).ToList(),
            Loader = new WidgetLoader { Mode = LoaderModes.Internal, Internal = internalFeed },
            Refresh = refresh,
            DefaultSize = size,
        });

        var w = widgets.Reserve(title, size, priority, null, null, proposed: false);
        widgets.Attach(w.Id, kind, null);
        library.Used(kind.Name);
    }

    /// <summary>
    /// What's on today. The question the old home page couldn't answer with a meal plan sat in a project.
    /// </summary>
    private const string Today = """
        if (!data) return <Meta>Loading…</Meta>
        const today = data?.today ?? []
        const tomorrow = data?.tomorrow ?? []
        const first = today[0]
        return (
          <div className="flex h-full items-center gap-4">
            <div className="min-w-0 flex-1">
              {first ? (
                <>
                  <div className="truncate text-xl font-semibold text-ink">{first.item}</div>
                  <Meta>{first.list}{today.length > 1 ? ` · and ${today.length - 1} more today` : ''}</Meta>
                </>
              ) : (
                <div className="text-sm text-ink-mute">Nothing planned for today</div>
              )}
            </div>
            {tomorrow.length > 0 && (
              <div className="hidden min-w-0 flex-1 border-l border-line pl-3 sm:block">
                <Meta>Tomorrow</Meta>
                <div className="truncate text-sm text-ink-soft">{tomorrow[0].item}</div>
              </div>
            )}
          </div>
        )
        """;

    /// <summary>Work in flight. A job stopped on a question leads, because that is the one needing a person.</summary>
    private const string Running = """
        if (!data) return <Meta>Loading…</Meta>
        const running = data?.running ?? []
        const waiting = running.filter(r => r.waiting)
        if (running.length === 0) return <div className="flex h-full items-center text-sm text-ink-mute">Nothing running</div>
        return (
          <div className="flex h-full flex-col justify-center gap-1.5">
            <div className="flex items-center gap-2">
              <Dot tone={waiting.length > 0 ? 'warn' : 'accent'} />
              <span className="text-sm font-medium text-ink">
                {running.length} running
              </span>
              {waiting.length > 0 && <Badge tone="warn">{waiting.length} need you</Badge>}
            </div>
            {running.slice(0, 2).map((r, i) => (
              <div key={i} className="truncate text-xs text-ink-soft">{r.task}</div>
            ))}
          </div>
        )
        """;

    private const string Projects = """
        if (!data) return <Meta>Loading…</Meta>
        const projects = data?.projects ?? []
        if (projects.length === 0) return <div className="flex h-full items-center text-sm text-ink-mute">No projects</div>
        return (
          <div className="flex h-full flex-col justify-center gap-1">
            {projects.slice(0, 4).map(p => (
              <Row key={p.slug} label={p.title} />
            ))}
            {projects.length > 4 && <Meta>and {projects.length - 4} more</Meta>}
          </div>
        )
        """;
}
