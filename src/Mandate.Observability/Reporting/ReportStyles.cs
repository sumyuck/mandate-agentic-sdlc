namespace Mandate.Observability.Reporting;

/// <summary>
/// The report's stylesheet, inlined.
/// </summary>
/// <remarks>
/// No CDN, no JavaScript, no external font. A run report is evidence, and evidence that only
/// renders when the reader has network access is evidence with a dependency it should not
/// have. It also has to keep working years from now, in an archive, offline.
/// </remarks>
internal static class ReportStyles
{
    public const string Css = """
        :root {
          --ink: #1a1d21; --muted: #5b6570; --line: #dfe3e8; --panel: #f7f8fa;
          --good: #1e7e34; --warn: #b26a00; --bad: #b3261e; --info: #1a56db;
          --skip: #8a9199;
        }
        * { box-sizing: border-box; }
        body {
          margin: 0; padding: 2rem clamp(1rem, 4vw, 3.5rem);
          font: 15px/1.6 ui-sans-serif, -apple-system, "Segoe UI", Roboto, sans-serif;
          color: var(--ink); background: #fff; max-width: 1200px;
        }
        h1 { font-size: 1.5rem; margin: 0 0 .25rem; }
        h2 { font-size: 1.05rem; margin: 2.5rem 0 .75rem; padding-bottom: .35rem;
             border-bottom: 1px solid var(--line); }
        h3 { font-size: .95rem; margin: 1.5rem 0 .4rem; }
        code, pre, .mono { font-family: ui-monospace, "SF Mono", Menlo, monospace; font-size: .85em; }
        .subtitle { color: var(--muted); margin: 0 0 1.5rem; }
        .request { background: var(--panel); border-left: 3px solid var(--info);
                   padding: .75rem 1rem; margin: 0 0 1.5rem; }
        .cards { display: grid; gap: .75rem;
                 grid-template-columns: repeat(auto-fill, minmax(165px, 1fr)); }
        .card { border: 1px solid var(--line); border-radius: 6px; padding: .7rem .85rem; }
        .card .value { font-size: 1.35rem; font-weight: 600; line-height: 1.2; }
        .card .label { color: var(--muted); font-size: .78rem; text-transform: uppercase;
                       letter-spacing: .04em; }
        .card .note { color: var(--muted); font-size: .78rem; margin-top: .2rem; }
        table { border-collapse: collapse; width: 100%; margin: .5rem 0 1rem; }
        th, td { text-align: left; padding: .45rem .6rem; border-bottom: 1px solid var(--line);
                 vertical-align: top; }
        th { font-size: .78rem; text-transform: uppercase; letter-spacing: .04em;
             color: var(--muted); font-weight: 600; }
        td.num, th.num { text-align: right; font-variant-numeric: tabular-nums; }
        .pill { display: inline-block; padding: .08rem .45rem; border-radius: 999px;
                font-size: .76rem; font-weight: 600; white-space: nowrap; }
        .ok { background: #e7f4ea; color: var(--good); }
        .warn { background: #fdf2e2; color: var(--warn); }
        .bad { background: #fdecea; color: var(--bad); }
        .neutral { background: #eef1f4; color: var(--skip); }
        .info { background: #e8f0fe; color: var(--info); }
        details { margin: .5rem 0; }
        summary { cursor: pointer; color: var(--muted); font-size: .9rem; }
        .decision { border: 1px solid var(--line); border-radius: 6px; padding: .8rem 1rem;
                    margin: .6rem 0; }
        .decision .q { font-weight: 600; }
        .decision ul { margin: .4rem 0 0; padding-left: 1.1rem; color: var(--muted); }
        .muted { color: var(--muted); }
        .footnote { color: var(--muted); font-size: .82rem; margin-top: 3rem;
                    border-top: 1px solid var(--line); padding-top: 1rem; }
        svg { max-width: 100%; height: auto; }
        """;
}
