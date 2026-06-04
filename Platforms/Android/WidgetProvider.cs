using Android.App;
using Android.Appwidget;
using Android.Content;
using Android.Widget;

namespace Widget.Platforms.Android
{
    [BroadcastReceiver(Label = "Ntfy Widget", Exported = true)]
    [IntentFilter(new[] { AppWidgetManager.ActionAppwidgetUpdate })]
    [MetaData(AppWidgetManager.MetaDataAppwidgetProvider, Resource = "@xml/widget_provider_info")]

    public class WidgetProvider : AppWidgetProvider
    {
        public override void OnUpdate(Context? context, AppWidgetManager? appWidgetManager, int[]? appWidgetIds)
        {
            if (context == null || appWidgetManager == null || appWidgetIds == null)
                return;

            // Read whatever the polling service last saved
            var label = Preferences.Get("ntfy_label", "Checking...");
            var title = Preferences.Get("ntfy_title", "");
            var message = Preferences.Get("ntfy_message", "");
            var time = Preferences.Get("ntfy_time", "");

            foreach (var widgetId in appWidgetIds)
            {
                var views = new RemoteViews(context.PackageName, Resource.Layout.widget);
                views.SetTextViewText(Resource.Id.widgetLabel, label);
                views.SetTextViewText(Resource.Id.widgetTitle, title);
                views.SetTextViewText(Resource.Id.widgetText, message);
                views.SetTextViewText(Resource.Id.widgetTime, time);
                appWidgetManager.UpdateAppWidget(widgetId, views);

                // Start the polling service if not already running
                var serviceIntent = new Intent(context, typeof(NtfyPollingService));
                context.StartService(serviceIntent);
            }
        }
    }
}
