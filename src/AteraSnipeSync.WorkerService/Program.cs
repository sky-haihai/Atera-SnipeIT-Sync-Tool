using AteraSnipeSync.WorkerService;
using AteraSnipeSync.Core.Configuration;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "Atera Snipe-IT Sync");
builder.Services.AddHttpClient("Atera");
builder.Services.AddHttpClient("SnipeIt");
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(new LocalAppSettingsStore(LocalAppSettingsStore.GetDefaultFilePath()));
builder.Services.AddSingleton<ILocalAppSettingsReader>(services => services.GetRequiredService<LocalAppSettingsStore>());
builder.Services.AddSingleton<WorkerRuntimeFactory>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
