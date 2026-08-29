using CNS.StorageCluster.Server.Data;
using CNS.StorageCluster.Server.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls(builder.Configuration["Web:Urls"] ?? "http://0.0.0.0:8080");

builder.Services.AddRazorPages();
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.Configure<TcpServerOptions>(builder.Configuration.GetSection("TcpServer"));
builder.Services.AddSingleton<ServerReportFileLogger>();
builder.Services.AddSingleton<TcpServerService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<TcpServerService>());
builder.Services.AddHostedService<NodeHealthService>();
builder.Services.AddScoped<ClusterQueryService>();

var app = builder.Build();

// La primera ejecución queda lista sin tener que crear el esquema manualmente.
// En producción se puede sustituir por migraciones EF Core si se desea versionar cambios de BD.
await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    try
    {
        await db.Database.ExecuteSqlRawAsync("IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Nodes') AND name = 'MacAddress') ALTER TABLE Nodes ADD MacAddress NVARCHAR(50) NULL;");
        await db.Database.ExecuteSqlRawAsync("IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Nodes') AND name = 'IpAddress') ALTER TABLE Nodes ADD IpAddress NVARCHAR(50) NULL;");
    }
    catch { }
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseStaticFiles();
app.UseRouting();
app.UseWebSockets();
app.Map("/ws/cluster", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return;
    }

    using var socket = await context.WebSockets.AcceptWebSocketAsync();
    var tcp = context.RequestServices.GetRequiredService<TcpServerService>();
    await tcp.HandleWebSocketAsync(socket, context.RequestAborted);
});
app.MapRazorPages();

app.Run();
