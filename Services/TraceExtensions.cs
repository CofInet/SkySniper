using System.Collections.Generic;
using System.Diagnostics;
using Coflnet.Sky.Core;

namespace Coflnet.Sky.Sniper.Services;
#nullable enable
public static class TraceExtensions
{
    public static Activity? Log(this Activity? activity, string message)
    {
        return activity?.AddEvent(new ActivityEvent("log", System.DateTimeOffset.Now, new ActivityTagsCollection(new[] { new KeyValuePair<string, object?>("message", message.Truncate(3_000)) })));
    }
}

#nullable restore