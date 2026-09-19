using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace FastStartup.Profiling
{
    /// <summary>Chrome Trace JSON + plain-text top-N summary of a finished trace.</summary>
    internal static class TraceReport
    {
        private const int TopN = 30;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string ChromeTrace(List<TraceEvent> events)
        {
            var sb = new StringBuilder(events.Count * 160);
            sb.Append("{\"displayTimeUnit\":\"ms\",\"traceEvents\":[\n");
            sb.Append("{\"name\":\"thread_name\",\"ph\":\"M\",\"pid\":1,\"tid\":").Append(StartupTrace.LaneScenes)
                .Append(",\"args\":{\"name\":\"scene loads (async)\"}},\n");
            sb.Append("{\"name\":\"thread_name\",\"ph\":\"M\",\"pid\":1,\"tid\":").Append(StartupTrace.LaneAsyncBundles)
                .Append(",\"args\":{\"name\":\"bundle loads (async)\"}},\n");
            sb.Append("{\"name\":\"thread_name\",\"ph\":\"M\",\"pid\":1,\"tid\":").Append(StartupTrace.MainThreadId)
                .Append(",\"args\":{\"name\":\"main\"}}");
            foreach (TraceEvent e in events)
            {
                sb.Append(",\n{\"name\":");
                Str(sb, e.Name);
                sb.Append(",\"cat\":");
                Str(sb, e.Cat);
                sb.Append(",\"ph\":\"").Append(e.Ph).Append("\",\"pid\":1,\"tid\":").Append(e.Tid);
                sb.Append(",\"ts\":").Append(Us(StartupTrace.ToMs(e.Start)));
                if (e.Ph == 'X')
                {
                    sb.Append(",\"dur\":").Append(Us(StartupTrace.DurMs(e.Start, e.End)));
                }
                else
                {
                    sb.Append(",\"s\":\"g\"");
                }
                sb.Append(",\"args\":{");
                bool first = true;
                Arg(sb, ref first, "detail", e.Detail);
                Arg(sb, ref first, "detail2", e.Detail2);
                if (e.Bytes >= 0)
                {
                    Sep(sb, ref first);
                    sb.Append("\"bytes\":").Append(e.Bytes);
                }
                if (e.Args != null)
                {
                    Sep(sb, ref first);
                    sb.Append(e.Args);
                }
                sb.Append("}}");
            }
            sb.Append("\n]}\n");
            return sb.ToString();
        }

        /// <summary>Per-event self time: duration minus direct children on the same real thread.</summary>
        private static Dictionary<TraceEvent, double> SelfTimes(List<TraceEvent> events, out double topLevelMainMs)
        {
            var self = new Dictionary<TraceEvent, double>();
            topLevelMainMs = 0;
            foreach (IGrouping<int, TraceEvent> thread in events.Where(e => e.Ph == 'X' && e.Tid < StartupTrace.LaneScenes).GroupBy(e => e.Tid))
            {
                var stack = new Stack<TraceEvent>();
                foreach (TraceEvent e in thread.OrderBy(e => e.Start).ThenByDescending(e => e.End))
                {
                    while (stack.Count > 0 && stack.Peek().End <= e.Start)
                    {
                        stack.Pop();
                    }
                    double dur = StartupTrace.DurMs(e.Start, e.End);
                    self[e] = dur;
                    if (stack.Count > 0)
                    {
                        TraceEvent parent = stack.Peek();
                        long childEnd = Math.Min(e.End, parent.End);
                        self[parent] -= StartupTrace.DurMs(e.Start, childEnd);
                    }
                    else if (thread.Key == StartupTrace.MainThreadId)
                    {
                        topLevelMainMs += dur;
                    }
                    stack.Push(e);
                }
            }
            return self;
        }

        /// <summary>Grouping key for the time-sink table: Harmony by owner, everything else by name.</summary>
        private static string Key(TraceEvent e) => e.Cat == "harmony" ? (e.Detail ?? "?") : e.Name;

        public static string Summary(List<TraceEvent> events, double menuReadyMs, string header)
        {
            Dictionary<TraceEvent, double> self = SelfTimes(events, out double accountedMs);
            var sb = new StringBuilder();
            sb.AppendLine(header);
            sb.AppendLine(F("menu ready {0:F0} ms since process start{1}; main-thread spans cover {2:F0} ms ({3:F0}%), " +
                            "rest = Unity/native work, frames and waits between hooked calls",
                menuReadyMs, StartupTrace.ProcessStartKnown ? "" : " (process start unknown: since FastStartup.Initialize)",
                accountedMs, menuReadyMs > 0 ? accountedMs * 100 / menuReadyMs : 0));
            sb.AppendLine();

            sb.AppendLine("== Lifecycle marks (ms since process start; GC counts gen0/1/2; managed heap) ==");
            foreach (TraceEvent m in events.Where(e => e.Ph == 'i').OrderBy(e => e.Start))
            {
                sb.AppendLine(F("{0,9:F0}  {1}{2}   [{3}]", StartupTrace.ToMs(m.Start), m.Name,
                    m.Detail != null ? " (" + m.Detail + ")" : "", m.Args?.Replace("\"", "")));
            }
            sb.AppendLine();

            sb.AppendLine("== Self time by category (ms; nested spans subtracted, no double counting) ==");
            foreach (var g in self.GroupBy(kv => kv.Key.Cat).Select(g => new { Cat = g.Key, Ms = g.Sum(kv => kv.Value), N = g.Count() })
                         .OrderByDescending(g => g.Ms))
            {
                sb.AppendLine(F("{0,10:F1}  {1,-10} {2,6} events", g.Ms, g.Cat, g.N));
            }
            sb.AppendLine("  game = mods' patches on hooked game methods (outer minus body); game.body = original body (plus any");
            sb.AppendLine("  other mod's Priority.Last prefix / Priority.First postfix registered after ours: upper bound);");
            sb.AppendLine("  plugin = plugin cctor+Awake not covered by a nested span; lifecycle = rest of that phase.");
            sb.AppendLine();

            sb.AppendLine(F("== Top {0} time sinks by self time (category, key, calls, self ms, inclusive ms) ==", TopN));
            foreach (var g in self.GroupBy(kv => kv.Key.Cat + "|" + Key(kv.Key))
                         .Select(g => new
                         {
                             Cat = g.First().Key.Cat,
                             Key = Key(g.First().Key),
                             N = g.Count(),
                             Self = g.Sum(kv => kv.Value),
                             Incl = g.Sum(kv => StartupTrace.DurMs(kv.Key.Start, kv.Key.End)),
                         })
                         .OrderByDescending(g => g.Self).Take(TopN))
            {
                sb.AppendLine(F("{0,10:F1} {1,10:F1}  {2,-10} {3,5}x  {4}", g.Self, g.Incl, g.Cat, g.N, g.Key));
            }
            sb.AppendLine();

            List<TraceEvent> plugins = events.Where(e => e.Cat == "plugin").ToList();
            sb.AppendLine(F("== Plugins: cctor + Awake inclusive (ms), {0} plugins, total {1:F0} ms ==", plugins.Count,
                plugins.Sum(e => StartupTrace.DurMs(e.Start, e.End))));
            foreach (TraceEvent e in plugins.OrderByDescending(e => e.End - e.Start).Take(TopN))
            {
                sb.AppendLine(F("{0,10:F1}  {1}  ({2})", StartupTrace.DurMs(e.Start, e.End), e.Name, e.Detail2));
            }
            sb.AppendLine();

            List<TraceEvent> harmony = events.Where(e => e.Cat == "harmony").ToList();
            sb.AppendLine("== Harmony by owner: outermost API calls inclusive (ms), wrapper builds (count, ms) ==");
            foreach (var g in harmony.GroupBy(e => e.Detail ?? "?")
                         .Select(g => new
                         {
                             Owner = g.Key,
                             Top = g.Where(e => e.Depth == 1).Sum(e => StartupTrace.DurMs(e.Start, e.End)),
                             Builds = g.Count(e => e.Name == "UpdateWrapper"),
                             Skipped = g.Count(e => e.Name == "UpdateWrapper (skipped)"),
                             BuildMs = g.Where(e => e.Name.StartsWith("UpdateWrapper", StringComparison.Ordinal)).Sum(e => StartupTrace.DurMs(e.Start, e.End)),
                         })
                         .OrderByDescending(g => g.Top).Take(TopN))
            {
                sb.AppendLine(F("{0,10:F1}  {1,5} builds {2,8:F1} ms  {3,4} skipped  {4}", g.Top, g.Builds, g.BuildMs, g.Skipped, g.Owner));
            }
            sb.AppendLine("  Top originals by wrapper build time (ms, builds):");
            foreach (var g in harmony.Where(e => e.Name == "UpdateWrapper").GroupBy(e => e.Detail2 ?? "?")
                         .Select(g => new { Target = g.Key, N = g.Count(), Ms = g.Sum(e => StartupTrace.DurMs(e.Start, e.End)) })
                         .OrderByDescending(g => g.Ms).Take(15))
            {
                sb.AppendLine(F("{0,10:F1} {1,4}x  {2}", g.Ms, g.N, g.Target));
            }
            sb.AppendLine();

            List<TraceEvent> bundles = events.Where(e => e.Cat == "bundle").ToList();
            // A cache hit loads its copy from inside the hooked stream load: count only outermost spans.
            List<TraceEvent> outer = bundles.Where(e => !bundles.Any(p => p != e && p.Tid == e.Tid && p.Start <= e.Start &&
                                                                         p.End >= e.End && (p.Start < e.Start || p.End > e.End))).ToList();
            sb.AppendLine(F("== AssetBundle loads: {0} calls ({1} outermost), {2:F0} ms on calling threads ==", bundles.Count,
                outer.Count, outer.Sum(e => StartupTrace.DurMs(e.Start, e.End))));
            foreach (TraceEvent e in bundles.OrderByDescending(e => e.End - e.Start).Take(TopN))
            {
                sb.AppendLine(F("{0,10:F1}  {1,-20} {2,8:F1} MB  {3}", StartupTrace.DurMs(e.Start, e.End), e.Detail2,
                    e.Bytes / (1024.0 * 1024.0), e.Detail ?? e.Name));
            }
            List<TraceEvent> async = events.Where(e => e.Cat == "bundle.async").ToList();
            if (async.Count > 0)
            {
                sb.AppendLine(F("  async request -> completed: {0} loads, longest {1:F0} ms", async.Count,
                    async.Max(e => StartupTrace.DurMs(e.Start, e.End))));
            }
            sb.AppendLine();

            sb.AppendLine("== Game methods: inclusive (ms) = body + mods' patches (body is an upper bound for vanilla) ==");
            foreach (var g in events.Where(e => e.Cat == "game").GroupBy(e => e.Name).Select(g => new
            {
                Name = g.Key,
                N = g.Count(),
                Incl = g.Sum(e => StartupTrace.DurMs(e.Start, e.End)),
                Body = events.Where(b => b.Cat == "game.body" && b.Name == g.Key).Sum(b => StartupTrace.DurMs(b.Start, b.End)),
            }).OrderByDescending(g => g.Incl))
            {
                sb.AppendLine(F("{0,10:F1}  body {1,8:F1}  mods {2,8:F1}  {3,3}x  {4}", g.Incl, g.Body, g.Incl - g.Body, g.N, g.Name));
            }
            sb.AppendLine("  Mods' patches by owner (game.patch, inclusive ms, calls; transpilers count as body):");
            foreach (var g in events.Where(e => e.Cat == "game.patch").GroupBy(e => e.Detail2 + "|" + e.Name)
                         .Select(g => new { Target = g.First().Detail2, Owner = g.First().Name, N = g.Count(), Ms = g.Sum(e => StartupTrace.DurMs(e.Start, e.End)) })
                         .OrderByDescending(g => g.Ms).Take(TopN))
            {
                sb.AppendLine(F("{0,10:F1} {1,4}x  {2,-28} {3}", g.Ms, g.N, g.Target, g.Owner));
            }
            foreach (TraceEvent e in events.Where(e => e.Cat == "jotunn").OrderByDescending(e => e.End - e.Start).Take(15))
            {
                sb.AppendLine(F("{0,10:F1}  jotunn {1}", StartupTrace.DurMs(e.Start, e.End), e.Name));
            }
            foreach (TraceEvent e in events.Where(e => e.Cat == "scene"))
            {
                sb.AppendLine(F("{0,10:F1}  scene {1} ({2})", StartupTrace.DurMs(e.Start, e.End), e.Name, e.Detail));
            }
            return sb.ToString();
        }

        /// <summary>One log line: menu-ready time plus the three largest self-time sinks.</summary>
        public static string OneLine(List<TraceEvent> events, double menuReadyMs)
        {
            Dictionary<TraceEvent, double> self = SelfTimes(events, out double accountedMs);
            IEnumerable<string> top = self.GroupBy(kv => kv.Key.Cat + ":" + Key(kv.Key))
                .Select(g => new { g.Key, Ms = g.Sum(kv => kv.Value) })
                .OrderByDescending(g => g.Ms).Take(3).Select(g => F("{0} {1:F0} ms", g.Key, g.Ms));
            return F("menu ready {0:F1} s, {1:F1} s in hooked spans; top: {2}", menuReadyMs / 1000, accountedMs / 1000,
                string.Join(", ", top.ToArray()));
        }

        private static string F(string format, params object[] args) => string.Format(Inv, format, args);

        private static string Us(double ms) => (ms * 1000).ToString("F1", Inv);

        private static void Sep(StringBuilder sb, ref bool first)
        {
            if (!first)
            {
                sb.Append(',');
            }
            first = false;
        }

        private static void Arg(StringBuilder sb, ref bool first, string key, string value)
        {
            if (value == null)
            {
                return;
            }
            Sep(sb, ref first);
            sb.Append('"').Append(key).Append("\":");
            Str(sb, value);
        }

        private static void Str(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", Inv));
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
