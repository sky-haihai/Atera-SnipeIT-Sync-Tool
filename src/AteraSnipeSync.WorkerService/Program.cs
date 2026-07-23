using AteraSnipeSync.Core.Configuration;
using AteraSnipeSync.Core.Notifications;
using AteraSnipeSync.Core.Runtime.Windows;
using AteraSnipeSync.Core.Scheduling;
using AteraSnipeSync.Core.Status;
using AteraSnipeSync.WorkerService;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options =>
    options.ServiceName = WorkerServiceIdentity.ServiceName);
builder.Services.AddHttpClient("Atera");
builder.Services.AddHttpClient("SnipeIt");
builder.Services.AddHttpClient("Notifications").RemoveAllLoggers();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new LocalAppSettingsStore(LocalAppSettingsStore.GetDefaultFilePath()));
builder.Services.AddSingleton<ILocalAppSettingsReader>(services =>
    services.GetRequiredService<LocalAppSettingsStore>());
builder.Services.AddSingleton<ScheduleCalculator>();
builder.Services.AddSingleton<IScheduleRuntimeStateStore>(services =>
    new JsonFileScheduleRuntimeStateStore(
        JsonFileScheduleRuntimeStateStore.GetDefaultFilePath(),
        services.GetRequiredService<ILogger<JsonFileScheduleRuntimeStateStore>>()));
builder.Services.AddSingleton<WorkerScheduleManager>();
builder.Services.AddSingleton<ISyncRunCoordinator, SyncRunCoordinator>();
builder.Services.AddSingleton<WorkerRuntimeFactory>();
builder.Services.AddSingleton<IWorkerRuntimeFactory>(services =>
    services.GetRequiredService<WorkerRuntimeFactory>());
builder.Services.AddSingleton<ISyncStatusStore>(services =>
    new JsonFileSyncStatusStore(
        new SyncStatusStoreOptions(),
        services.GetRequiredService<ILogger<JsonFileSyncStatusStore>>()));
builder.Services.AddSingleton<ISmtpNotificationTransport, SystemNetSmtpNotificationTransport>();
builder.Services.AddSingleton<EmailNotificationSender>();
builder.Services.AddSingleton<WebhookNotificationSender>(services => new WebhookNotificationSender(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("Notifications"),
    services.GetRequiredService<TimeProvider>()));
builder.Services.AddSingleton<INotificationChannelSender>(services =>
    services.GetRequiredService<EmailNotificationSender>());
builder.Services.AddSingleton<INotificationChannelSender>(services =>
    services.GetRequiredService<WebhookNotificationSender>());
builder.Services.AddSingleton<CompositeNotificationPublisher>();
builder.Services.AddSingleton<INotificationPublisher>(services =>
    services.GetRequiredService<CompositeNotificationPublisher>());
builder.Services.AddSingleton<INotificationTester>(services =>
    services.GetRequiredService<CompositeNotificationPublisher>());
builder.Services.AddSingleton<NotificationEventFilter>();
builder.Services.AddSingleton<WorkerScheduler>();
builder.Services.AddSingleton<WorkerConnectionTester>();
builder.Services.AddSingleton<IWorkerCommandHandler, WorkerCommandHandler>();
builder.Services.AddSingleton<WindowsWorkerPipeFactory>();
builder.Services.AddSingleton(new WorkerIpcServerOptions());
builder.Services.AddHostedService<Worker>();
builder.Services.AddHostedService<WorkerIpcServer>();

var host = builder.Build();
host.Run();
