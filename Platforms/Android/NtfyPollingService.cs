using System.Text.Json;
using Android.App;
using Android.Appwidget;
using Android.OS;
using Android.Content;
using System.Text.Json.Serialization;
using OperationCanceledException = Android.OS.OperationCanceledException;

namespace Widget.Platforms.Android
{
    [Service(Exported = false)]
    public class NtfyPollingService : Service
    {
        // ── Change these two to match your setup ──────────────────
        private const string NtfyBaseUrl = "https://ntfy.sh"; //or your self-hosted URL

        private static readonly List<NtfyChannel> Channels = new()
        {
            new NtfyChannel("strategy_mForex_DEMO_2", "📈 mForex DEMO 2"),
            new NtfyChannel("strategy_OANDA_TMS_DEMO", "📈 OANDA_TMS DEMO"),
            new NtfyChannel("strategy_FP_Markets_DEMO ", "📈 FP_Markets DEMO"),
        };
        // ─────────────────────────────────────────────────────────

        private CancellationTokenSource? _cts;

        public override IBinder? OnBind(Intent? intent) => null;

        public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
        {
            _cts?.Cancel();  // cancel any previous loop
            _cts = new CancellationTokenSource();
            Task.Run(() => PollLoop(_cts.Token));
            return StartCommandResult.Sticky;  // restart if killed
        }

        public override void OnDestroy()
        {
            _cts?.Cancel();
            _cts?.Dispose();
            base.OnDestroy();
        }

        private async Task PollLoop(CancellationToken ct)
        {
            var http = new HttpClient();
            http.Timeout = TimeSpan.FromSeconds(30);

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await PollAllChannels(http, ct);
                }
                catch (OperationCanceledException)
                {
                    break;  // service is stopping, exit cleanly
                }
                catch (Exception ex)
                {
                    SaveLatestMessage("⚠️ Error", "Poll failed", ex.Message, "");
                    RefreshWidget();
                }


                // Poll every 30 seconds
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
        }
        // ── Fetch all channels, find the most recent message ────

        private async Task PollAllChannels(HttpClient http, CancellationToken ct)
        {
            NtfyMessage? latestMessage = null;
            NtfyChannel? latestChannel = null;
            var unreadCounts = new Dictionary<string, int>();

            foreach (var channel in Channels)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var url = $"{NtfyBaseUrl}/{channel.Topic}/json?poll=1&since=6h";
                    var response = await http.GetStringAsync(url, ct);
                    var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

                    // count unread messages per channel
                    unreadCounts[channel.Topic] = lines.Length;

                    if (lines.Length == 0) continue;

                    // parse all lines, pick the newest one overall
                    foreach (var line in lines)
                    {
                        var msg = JsonSerializer.Deserialize<NtfyMessage>(line);
                        if (msg == null) continue;

                        if (latestMessage == null || msg.time > latestMessage.time)
                        {
                            latestMessage = msg;
                            latestChannel = channel;
                        }
                    }
                }
                catch (Exception ex)
                {
                    // one channel failing shouldn't stop the others
                    unreadCounts[channel.Topic] = -1;  // mark as error
                }
            }

            // Build summary line: "🔴 2  🔵 5  🟠 0"
            var summary = string.Join("  ", Channels.Select(c =>
            {
                var count = unreadCounts.GetValueOrDefault(c.Topic, 0);
                return count < 0
                    ? $"{c.Label} ?"
                    : $"{c.Label} {count}";
            }));

            if (latestMessage == null)
            {
                SaveLatestMessage(
                    label: summary,
                    title: "No messages",
                    message: "Nothing in the last 6 hours",
                    time: "");
            }
            else
            {
                var time = DateTimeOffset.FromUnixTimeSeconds(latestMessage.time).LocalDateTime.ToString("HH:mm dd/MM");

                SignalPayload? payload = null;
                try
                {
                    if (latestMessage.message != null && latestMessage.message.TrimStart().StartsWith("{"))
                    {
                        payload = JsonSerializer.Deserialize<SignalPayload>(latestMessage.message);
                    }
                }
                catch { /* not JSON, use raw message */ }

                // Build display strings from whichever format we got
                var emoji = payload?.Emoji ?? "📊";
                var lastSignal = payload?.LastSignal ?? latestMessage.title ?? "Signal";
                var displayText = payload?.DisplayText ?? latestMessage.message ?? "";
                var channel = payload?.Channel ?? latestMessage.topic ?? "";
                var caseName = payload?.CaseNameRes?.FirstOrDefault() ?? "";

                // Shorten displayText for widget — it can be long
                var shortDisplay = displayText.Length > 80 ? displayText[..80] + "…" : displayText;

                SaveLatestMessage(
                    label: summary,
                    title: $"{emoji} {lastSignal}",
                    message: shortDisplay,
                    time: $"{channel}  {time}");
            }

            RefreshWidget();
        }

        // ── Preferences helpers ─────────────────────────────────

        private static void SaveLatestMessage(string label, string title, string message, string time)
        {
            Preferences.Set("ntfy_label", label);
            Preferences.Set("ntfy_title", title);
            Preferences.Set("ntfy_message", message);
            Preferences.Set("ntfy_time", time);
        }

        // ── Widget refresh ──────────────────────────────────────

        private void RefreshWidget()
        {
            // use 'this' — inside a Service, 'this' IS the Context
            var context = this;
            var manager = AppWidgetManager.GetInstance(context);
            var component = new ComponentName(context, Java.Lang.Class.FromType(typeof(WidgetProvider)));
            var ids = manager?.GetAppWidgetIds(component);

            if (ids == null || ids.Length == 0) return;

            var intent = new Intent(context, typeof(WidgetProvider));
            intent.SetAction(AppWidgetManager.ActionAppwidgetUpdate);
            intent.PutExtra(AppWidgetManager.ExtraAppwidgetIds, ids);
            context.SendBroadcast(intent);
        }

        // ── Data models ─────────────────────────────────────────

        private class NtfyChannel
        {
            public string Topic { get; }
            public string Label { get; }

            public NtfyChannel(string topic, string label)
            {
                Topic = topic;
                Label = label;
            }
        }

        private class NtfyMessage
        {
            [JsonPropertyName("id")] public string? id { get; set; }
            [JsonPropertyName("time")] public long time { get; set; }
            [JsonPropertyName("topic")] public string? topic { get; set; }
            [JsonPropertyName("title")] public string? title { get; set; } // last signal
            [JsonPropertyName("message")] public string? message { get; set; } // displayed text
        }

        private class SignalPayload
        {
            [JsonPropertyName("LastSignal")] public string? LastSignal { get; set; }
            [JsonPropertyName("DisplayText")] public string? DisplayText { get; set; }
            [JsonPropertyName("Emoji")] public string? Emoji { get; set; }
            [JsonPropertyName("Channel")] public string? Channel { get; set; }
            [JsonPropertyName("CaseNameRes")] public string[]? CaseNameRes { get; set; }
            [JsonPropertyName("PutCounter")] public int PutCounter { get; set; }
            [JsonPropertyName("SelectedVariant")] public string? SelectedVariant { get; set; }
            [JsonPropertyName("CaseNameVariant")] public string? CaseNameVariant { get; set; }
        }
    }
}

